"""Pinned, bounded registry downloads and plugin payloads for local NOMM packs.

No installation or registry refresh happens here. The reviewed config and lock
are the authority; cached bytes are verified again before every use.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import tempfile
import threading
import tomllib
import urllib.parse
import urllib.request
import zipfile
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from process_utils import background_kwargs

MAX_DOWNLOAD = 128 * 1024 * 1024
MAX_MEMBER = 64 * 1024 * 1024
MAX_EXPANDED = 256 * 1024 * 1024
MAX_MEMBERS = 2048
PROTECTED_IDS = {'NOSMR', 'NOMM-Integration'}


def safe_path(value: str) -> str:
    """Accept portable relative paths, including Windows filename constraints."""
    if not isinstance(value, str) or not value or '\\' in value:
        raise ValueError(f'Unsafe archive path: {value!r}')
    parts = value.rstrip('/').split('/')
    reserved = {'con', 'prn', 'aux', 'nul', *(f'com{i}' for i in range(1, 10)), *(f'lpt{i}' for i in range(1, 10))}
    for part in parts:
        if (part in {'', '.', '..'} or part.endswith((' ', '.'))
                or any(ord(c) < 32 or c in ':<>"|?*' for c in part)
                or part.split('.')[0].lower() in reserved):
            raise ValueError(f'Unsafe archive path: {value!r}')
    return '/'.join(parts)


def https_url(value: str) -> str:
    if not isinstance(value, str):
        raise ValueError('Download URL must be HTTPS')
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password or parsed.fragment:
        raise ValueError('Download URL must be HTTPS without credentials or fragment')
    return value


class _HttpsRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        https_url(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def download(url: str) -> bytes:
    opener = urllib.request.build_opener(_HttpsRedirect())
    request = urllib.request.Request(https_url(url), headers={'User-Agent': 'NuclearOptionClientMods-modpack/1'})
    with opener.open(request, timeout=30) as response:
        https_url(response.geturl())
        size = response.headers.get('Content-Length')
        if size is not None and int(size) > MAX_DOWNLOAD:
            raise ValueError('Registry download exceeds size limit')
        data = response.read(MAX_DOWNLOAD + 1)
    if len(data) > MAX_DOWNLOAD:
        raise ValueError('Registry download exceeds size limit')
    return data


def artifact_bytes(root: Path, artifact: dict) -> bytes:
    digest = artifact.get('sha256', '')
    if not isinstance(digest, str) or not re.fullmatch('[a-f0-9]{64}', digest):
        raise ValueError('Locked artifact requires a lowercase SHA-256')
    filename = artifact.get('fileName')
    if not isinstance(filename, str) or safe_path(filename) != filename or '/' in filename:
        raise ValueError('Locked artifact requires a safe fileName')
    primary, legacy = artifact.get('downloadUrl'), artifact.get('downloadURL')
    if primary and legacy and primary != legacy:
        raise ValueError('Conflicting artifact download URLs')
    url = https_url(primary or legacy)
    cache_root = root / 'dist/modpack-cache'
    cache = cache_root / digest / filename
    if not cache.resolve().is_relative_to(cache_root.resolve()):
        raise ValueError('Cache path escapes cache directory')
    if cache.exists():
        if cache.stat().st_size > MAX_DOWNLOAD:
            raise ValueError('Cached artifact exceeds size limit')
        data = cache.read_bytes()
    else:
        data = download(url)
    if hashlib.sha256(data).hexdigest() != digest:
        raise ValueError(f'Artifact SHA-256 mismatch: {filename}')
    if not cache.exists():
        cache.parent.mkdir(parents=True, exist_ok=True)
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(dir=cache.parent, delete=False) as stream:
                temporary = Path(stream.name)
                stream.write(data)
            os.replace(temporary, cache)
        finally:
            if temporary:
                temporary.unlink(missing_ok=True)
    return data


def _validate_members(members: list[tuple[str, int, bool, bool]]) -> None:
    if len(members) > MAX_MEMBERS or sum(size for _, size, _, _ in members) > MAX_EXPANDED:
        raise ValueError('Archive exceeds expanded size or member-count limit')
    seen = set()
    files = set()
    for name, size, directory, regular in members:
        normalized = safe_path(name).casefold()
        if normalized in seen:
            raise ValueError(f'Duplicate/case-colliding archive member: {name}')
        seen.add(normalized)
        if size < 0 or size > MAX_MEMBER or not regular:
            raise ValueError(f'Unsupported member or member size: {name}')
        if not directory:
            files.add(normalized)
    for name in seen:
        parts = name.split('/')
        if any('/'.join(parts[:i]) in files for i in range(1, len(parts))):
            raise ValueError(f'File/directory path collision: {name}')


def archive_files(data: bytes, layout: str) -> dict[str, bytes]:
    if layout == 'zip':
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            members = archive.infolist()
            descriptions = []
            for member in members:
                kind = stat.S_IFMT(member.external_attr >> 16)
                regular = kind in {0, stat.S_IFREG, stat.S_IFDIR}
                if member.flag_bits & 1:
                    raise ValueError('Encrypted archives are unsupported')
                # ZipInfo normalizes backslashes and truncates NULs on Windows;
                # validate the original archive name before that transformation.
                descriptions.append((member.orig_filename, member.file_size, member.is_dir(), regular))
            _validate_members(descriptions)
            return {safe_path(m.filename): archive.read(m) for m in members if not m.is_dir()}
    if layout != '7z':
        raise ValueError(f'Unsupported archive layout: {layout}')
    import py7zr

    with py7zr.SevenZipFile(io.BytesIO(data), mode='r') as archive:
        if archive.needs_password():
            raise ValueError('Encrypted archives are unsupported')
        members = archive.list()
        _validate_members([(m.filename, m.uncompressed, m.is_directory,
                            m.is_directory or m.is_file) for m in members])
    # py7zr cannot decode the upstream BCJ2 archive. libarchive's bsdtar
    # supports it; only stdout extraction is used, after validating all entries.
    executable = bsdtar_executable()
    result = {}
    with tempfile.TemporaryDirectory(prefix='modpack-7z-') as temporary:
        source = Path(temporary) / 'payload.7z'
        source.write_bytes(data)
        for member in members:
            if member.is_directory:
                continue
            result[safe_path(member.filename)] = bsdtar_member(executable, source, member.filename, member.uncompressed)
    return result


def bsdtar_executable() -> str:
    override = os.environ.get('MODPACK_BSDTAR')
    candidates = [override] if override else [shutil.which('bsdtar'), shutil.which('tar')]
    for executable in candidates:
        if not executable:
            continue
        try:
            checked = subprocess.run([executable, '--version'], capture_output=True, timeout=10, check=True, **background_kwargs())
        except (OSError, subprocess.SubprocessError):
            continue
        if b'bsdtar' in checked.stdout.lower() and b'libarchive' in checked.stdout.lower():
            return executable
    raise ValueError('7z decoding requires bsdtar/libarchive; set MODPACK_BSDTAR to its executable')


def bsdtar_member(executable: str, source: Path, member: str, size: int) -> bytes:
    with tempfile.TemporaryFile() as errors:
        process = subprocess.Popen([executable, '-xOf', str(source), '--', member],
                                   stdout=subprocess.PIPE, stderr=errors, stdin=subprocess.DEVNULL, **background_kwargs())
        timed_out = threading.Event()
        def stop():
            timed_out.set()
            process.kill()
        timer = threading.Timer(30, stop)
        timer.start()
        try:
            data = process.stdout.read(size + 1)
            if len(data) > size:
                process.kill()
            code = process.wait()
            if timed_out.is_set():
                raise ValueError(f'7z member extraction timed out: {member}')
            if code != 0 or len(data) != size:
                errors.seek(0)
                detail = errors.read(1024).decode(errors='replace')
                raise ValueError(f'7z member extraction mismatch: {member}: {detail}')
            return data
        finally:
            timer.cancel()
            if process.poll() is None:
                process.kill()
                process.wait()
            process.stdout.close()


def _notice(name: str) -> bool:
    stem = Path(name).stem.casefold()
    return '/' not in name and (stem.startswith(('readme', 'license', 'licence', 'copying', 'notice', 'changelog')))


def plugin_files(files: dict[str, bytes], strip_prefix: str) -> dict[str, bytes]:
    prefix = safe_path(strip_prefix).rstrip('/') + '/' if strip_prefix else ''
    result = {}
    seen = set()
    for name, data in files.items():
        if prefix and not name.startswith(prefix):
            if _notice(name):
                target = name
            else:
                raise ValueError(f'File outside declared plugin prefix: {name}')
        else:
            target = name[len(prefix):]
        target = safe_path(target)
        folded = target.casefold()
        if folded in seen or folded == 'meta.json':
            raise ValueError(f'Payload collision/reserved metadata: {target}')
        seen.add(folded)
        parts = folded.split('/')
        basename = parts[-1]
        extension = Path(basename).suffix
        if (any(p in {'bepinex', 'core', 'patchers', 'config', 'plugins', 'disabledplugins', 'nuclearoption_data'} for p in parts[:-1])
                or basename in {'assembly-csharp.dll', 'assembly-csharp-firstpass.dll', 'bepinex.dll', '0harmony.dll', 'mirage.dll', 'rewired_core.dll', 'doorstop_config.ini'}
                or basename.startswith(('unityengine.', 'unity.', 'bepinex.', 'system.', 'microsoft.'))
                or extension in {'.exe', '.com', '.bat', '.cmd', '.ps1', '.sh', '.py', '.msi', '.nobp', '.zip', '.7z', '.rar'}):
            raise ValueError(f'Unsupported game/core/executable payload: {target}')
        result[target] = data
    if not any(name.lower().endswith('.dll') for name in result):
        raise ValueError('Plugin payload contains no DLL')
    return result


def load_locked_mods(root: Path) -> list[dict]:
    config = tomllib.loads((root / 'tools/modpack-extras.toml').read_text(encoding='utf-8'))
    lock = json.loads((root / 'tools/modpack-extras.lock.json').read_text(encoding='utf-8'))
    if lock.get('schema') != 1 or not isinstance(lock.get('mods'), list):
        raise ValueError('Unsupported extras lock schema')
    registry = lock.get('registry', {})
    https_url(registry.get('manifestUrl'))
    https_url(registry.get('versionUrl'))
    if not registry.get('version'):
        raise ValueError('Extras lock lacks registry version')
    configured = config.get('mods', {})
    entries = {}
    for mod in lock['mods']:
        guid = mod.get('id', '')
        if (not re.fullmatch('[A-Za-z0-9_.-]+', guid) or guid in entries or guid in PROTECTED_IDS):
            raise ValueError('Invalid, duplicate or protected extras ID')
        if guid not in configured:
            raise ValueError(f'Locked ID absent from extras config: {guid}')
        declared = configured[guid]
        if (mod.get('origin') != declared.get('origin') or mod.get('layout') != declared.get('layout')
                or mod.get('stripPrefix', '') != declared.get('strip_prefix', '')):
            raise ValueError(f'Extras config differs from lock: {guid}')
        if mod.get('layout') not in {'zip', '7z', 'dll'}:
            raise ValueError(f'Unsupported extras layout: {guid}')
        origin = https_url(mod.get('origin')).rstrip('/')
        artifact = mod.get('artifact', {})
        version = artifact.get('version', '')
        if not isinstance(version, str) or not re.fullmatch(r'\d+(?:\.\d+){1,3}', version):
            raise ValueError(f'Invalid locked version: {guid}')
        url = https_url(artifact.get('downloadUrl') or artifact.get('downloadURL'))
        if not url.startswith(origin + '/'):
            raise ValueError(f'Artifact does not match declared origin: {guid}')
        entries[guid] = mod
    if set(configured) != set(entries):
        raise ValueError('Extras lock is missing configured IDs')
    for guid, mod in entries.items():
        artifact = mod['artifact']
        references = list(artifact.get('dependencies', []))
        if artifact.get('extends'):
            references.append(artifact['extends'])
        for reference in references:
            target = entries.get(reference.get('id'))
            if target is None or (reference.get('version') is not None and reference['version'] != target['artifact']['version']):
                raise ValueError(f'Unresolved locked dependency for {guid}: {reference}')
        for conflict in artifact.get('incompatibilities', []):
            target = entries.get(conflict.get('id'))
            if target and (conflict.get('version') is None or conflict['version'] == target['artifact']['version']):
                raise ValueError(f'Incompatible locked extras: {guid} and {target["id"]}')
    return list(entries.values())


def prepare_extras(root: Path) -> tuple[dict[str, bytes], list[dict]]:
    contents, records = {}, []
    for mod in load_locked_mods(root):
        artifact = mod['artifact']
        data = artifact_bytes(root, artifact)
        if mod['layout'] == 'dll':
            if mod.get('stripPrefix') or not artifact['fileName'].lower().endswith('.dll'):
                raise ValueError('DLL layout requires a .dll filename and no prefix')
            files = {artifact['fileName']: data}
        else:
            files = archive_files(data, mod['layout'])
        files = plugin_files(files, mod.get('stripPrefix', ''))
        prefix = f'mods/{mod["id"]}/'
        contents.update({prefix + name: value for name, value in files.items()})
        metadata_artifact = {key: value for key, value in artifact.items() if key != 'sha256'}
        contents[prefix + 'meta.json'] = json.dumps({'id': mod['id'], 'artifact': metadata_artifact}, indent=2).encode()
        records.append({'id': mod['id'], 'version': artifact['version'], 'origin': mod['origin'], 'artifact': artifact,
                        'files': [{'path': prefix + name, 'sha256': hashlib.sha256(value).hexdigest()}
                                  for name, value in sorted(files.items())]})
    return contents, records
