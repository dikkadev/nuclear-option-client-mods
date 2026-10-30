"""Local release preparation and snapshot-only GitHub publication.

Private plans and receipts stay outside the exported source tree. Nothing in this
module changes the development repository's branches, index or remotes.
"""
from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tempfile
import tomllib
from datetime import datetime, timezone
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from urllib.parse import quote, unquote, urlsplit
import zipfile
from functools import lru_cache

from process_utils import background_kwargs

ROOT = Path(__file__).resolve().parents[1]
REGISTRY = 'https://kopterbuzz.github.io/NOMNOM/manifest/manifest.json'
PACKS = {
    'our': ('Modpack-mine-v', 'nuclear-option-modpack-mine.nommpack'),
    'full': ('Modpack-full-v', 'nuclear-option-modpack-full.nommpack'),
}
LEGACY_PACKS = {
    'our': ('ClientModpack-v', 'nuclear-option-client-mods.nommpack'),
    'full': ('FullModpack-v', 'nuclear-option-full-modpack.nommpack'),
}


def pack_label(name):
    return 'mine' if name == 'our' else name


def pack_variants(name):
    return (PACKS[name], LEGACY_PACKS[name])


def pack_versions(releases, name):
    return [version for release in releases if not release.get('draft')
            for prefix, _ in pack_variants(name)
            if (version := tag_version(release.get('tag_name'), prefix))]


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode('utf-8')


def digest(data):
    return hashlib.sha256(data).hexdigest()


def seal(value, key):
    result = dict(value)
    result[key] = digest(canonical(value))
    return result


def check_seal(value, key):
    body = {k: v for k, v in value.items() if k != key}
    if value.get(key) != digest(canonical(body)):
        raise ValueError(f'{key} mismatch: the saved document changed')


def write_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + '.tmp')
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    os.replace(temporary, path)


def run(args, *, cwd=ROOT, env=None, data=None):
    result = subprocess.run([str(a) for a in args], cwd=cwd, env=env, input=data,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            **background_kwargs())
    if result.returncode:
        # Never print environment variables, tokens or the complete command input.
        raise ValueError(f'{Path(str(args[0])).name} {args[1] if len(args)>1 else ""} failed: '
                         + result.stderr.decode('utf-8', 'replace').strip())
    return result.stdout


def git(*args, cwd=ROOT, env=None, data=None):
    from release_source import find_git
    child_env = dict(os.environ if env is None else env, GIT_TERMINAL_PROMPT='0', GH_HOST='github.com')
    for key in ('GIT_DIR', 'GIT_WORK_TREE', 'GIT_COMMON_DIR', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY',
                'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_NAMESPACE', 'GIT_CONFIG_PARAMETERS', 'GIT_CONFIG_COUNT'):
        child_env.pop(key, None)
    return run([find_git(ROOT), *args], cwd=cwd, env=child_env, data=data)


def load_builder(root=ROOT):
    spec = importlib.util.spec_from_file_location('release_modpack_builder', root / 'tools/build-modpack.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def config(root=ROOT):
    return tomllib.loads((root / 'tools/release.toml').read_text('utf-8'))


def repository(value):
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+', value):
        raise ValueError('Repository must be OWNER/NAME, without a URL')
    if value.split('/')[0].lower() != 'dikkadev':
        raise ValueError('This publisher targets a repository under dikkadev')
    if value.split('/')[1] in {'.', '..'}:
        raise ValueError('Invalid repository name')
    return value


def version_key(value):
    if not isinstance(value, str) or not re.fullmatch(r'(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){1,3}', value):
        raise ValueError(f'Invalid numeric version: {value!r}; use X.Y, X.Y.Z or X.Y.Z.W')
    parts = tuple(map(int, value.split('.')))
    if any(p > 2147483647 for p in parts):
        raise ValueError('Version components must fit a .NET System.Version')
    # System.Version distinguishes missing build/revision components from zero.
    return parts + (-1,) * (4 - len(parts))


def next_version(current, change):
    version_key(current)
    if change not in {'patch', 'minor', 'major'}:
        version_key(change)
        return change
    parts = list(map(int, current.split('.')))
    parts += [0] * (3 - len(parts))
    index = {'major': 0, 'minor': 1, 'patch': 2}[change]
    parts[index] += 1
    for i in range(index + 1, len(parts)):
        parts[i] = 0
    return '.'.join(map(str, parts))


def tag_version(tag, prefix):
    if not isinstance(tag, str) or not tag.startswith(prefix):
        return None
    value = tag[len(prefix):]
    try:
        version_key(value)
        return value
    except ValueError:
        return None


@lru_cache(maxsize=1)
def has_gh_auth():
    if not shutil.which('gh'):
        return False
    try:
        gh('auth', 'status')
        return True
    except ValueError:
        return False


def fetch_json(url, *, missing=False, empty=False):
    if url.startswith('https://api.github.com/') and has_gh_auth():
        try:
            return gh_json('api', url[len('https://api.github.com/'):])
        except ValueError as error:
            if (missing and 'HTTP 404' in str(error)) or (empty and 'HTTP 409' in str(error)):
                return None
            raise
    request = Request(url, headers={'User-Agent': 'nuclear-option-local-release', 'Accept': 'application/vnd.github+json'})
    try:
        with urlopen(request, timeout=45) as response:
            return json.load(response)
    except HTTPError as error:
        if (missing and error.code == 404) or (empty and error.code == 409):
            return None
        raise ValueError(f'Metadata request failed ({error.code}): {url}') from error
    except OSError as error:
        raise ValueError(f'Metadata request failed: {url}: {error}') from error


def release_list(repo, *, missing=False):
    result = []
    for page in range(1, 1001):
        items = fetch_json(f'https://api.github.com/repos/{repo}/releases?per_page=100&page={page}', missing=missing)
        if items is None:
            return result
        if not isinstance(items, list):
            raise ValueError('Unexpected GitHub releases response')
        result.extend(items)
        if len(items) < 100:
            return result
    raise ValueError('Release pagination exceeded 100,000 items')


def discovery(mods, repo, *, offline=False):
    result = {'offline': offline, 'baseCommit': None, 'exists': False, 'versions': {}, 'releases': [],
              'defaultBranch': None, 'registryEntries': {}, 'legacyReleases': {}}
    if offline:
        return result
    remote = fetch_json(f'https://api.github.com/repos/{repo}', missing=True)
    if remote is not None:
        result['exists'] = True
        result['defaultBranch'] = remote['default_branch']
        result['releases'] = release_list(repo)
        branch = fetch_json(f'https://api.github.com/repos/{repo}/commits/{remote["default_branch"]}', empty=True)
        result['baseCommit'] = branch['sha'] if branch else None
    registry = fetch_json(REGISTRY)
    if not isinstance(registry, list):
        raise ValueError('NOMNOM did not return a mod array')
    by_id = {m['id']: m for m in registry}
    result['registryEntries'] = {m['guid']: by_id[m['guid']] for m in mods if m['guid'] in by_id}
    for mod in mods:
        known = []
        entry = by_id.get(mod['guid'], {})
        for artifact in entry.get('artifacts', []):
            try:
                version_key(artifact['version'])
                known.append(artifact['version'])
            except (ValueError, KeyError):
                continue
        prefix = mod['name'] + '-v'
        known += [v for release in result['releases'] if not release.get('draft')
                  and (v := tag_version(release.get('tag_name'), prefix))]
        # Old repositories matter during migration, including releases not yet in NOMNOM.
        owner = entry.get('githubOwner')
        legacy = entry.get('githubRepoName')
        if owner and legacy and f'{owner}/{legacy}' != repo:
            if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9-]*', owner) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]*', legacy) or legacy in {'.', '..'}:
                raise ValueError('Invalid legacy repository coordinates in registry')
            legacy_releases = release_list(f'{owner}/{legacy}', missing=True)
            result['legacyReleases'][mod['guid']] = legacy_releases
            for release in legacy_releases:
                if not release.get('draft') and (v := tag_version(release.get('tag_name'), 'v')):
                    known.append(v)
        result['versions'][mod['name']] = max(known, key=version_key) if known else None
    return result


def status(root=ROOT, *, repo=None, offline=False):
    builder = load_builder(root)
    mods = builder.load_mods(root)
    settings = config(root)
    repo = repository(repo or settings['publication']['repository'])
    remote = discovery(mods, repo, offline=offline)
    members = published_pack_members(mods, remote['releases']) if not offline else {}
    rows = []
    for mod in mods:
        individual = remote['versions'].get(mod['name'])
        packed = [r['version'] for r in members.get(mod['name'], [])]
        pack_version = max(packed, key=version_key) if packed else None
        known = [v for v in (individual, pack_version) if v]
        latest = max(known, key=version_key) if known else None
        suggested = mod['version'] if not latest or version_key(mod['version']) > version_key(latest) else next_version(latest, 'patch')
        rows.append({'name': mod['name'], 'local': mod['version'], 'published': latest,
                     'individualPublished': individual, 'packPublished': pack_version, 'suggested': suggested})
    for name in PACKS:
        versions = pack_versions(remote['releases'], name)
        rows.append({'name': f'pack:{pack_label(name)}', 'local': settings['packs'][name + '_version'],
                     'published': max(versions, key=version_key) if versions else None})
    return {'repository': repo, 'offline': offline, 'items': rows}


def bump(root, names, change, *, pack=None):
    pack = 'our' if pack == 'mine' else pack
    builder = load_builder(root)
    mods = builder.load_mods(root)
    selected = select_mods(mods, names)
    paths = []
    if pack:
        if names:
            raise ValueError('Use --pack or --mod for bump, not both')
        path = root / 'tools/release.toml'
        old = config(root)['packs'][pack + '_version']
        new = next_version(old, change)
        source = path.read_bytes()
        replacement, count = re.subn(rb'(?m)^(' + pack.encode() + rb'_version\s*=\s*)"[^"]+"',
                                     lambda m: m[1] + b'"' + new.encode() + b'"', source)
        paths.append((path, replacement, count, old, new))
    else:
        if not selected:
            raise ValueError('Select at least one --mod, or --pack')
        for mod in selected:
            path = mod['project']
            old, new = mod['version'], next_version(mod['version'], change)
            replacement, count = re.subn(rb'(<Version>)[^<]+(</Version>)',
                                         lambda m: m[1] + new.encode() + m[2], path.read_bytes())
            paths.append((path, replacement, count, old, new))
    if any(count != 1 or version_key(new) <= version_key(old) for _, _, count, old, new in paths):
        raise ValueError('Version edit is ambiguous or does not increase the current version')
    result = []
    for path, content, _, old, new in paths:
        path.write_bytes(content)
        result.append({'path': path.relative_to(root).as_posix(), 'old': old, 'new': new})
    return result


def select_mods(mods, names):
    names = set(names or [])
    unknown = names - {m['name'] for m in mods}
    if unknown:
        raise ValueError('Unknown or retired mods: ' + ', '.join(sorted(unknown)))
    return [m for m in mods if m['name'] in names]


def sanitized_config(mods):
    lines = ['# Public build/package inputs. Project files supply plugin versions.', 'configuration = "Release"', '']
    for mod in mods:
        lines.append(f'[mods.{mod["name"]}]')
        for key in ('id', 'display_name', 'description', 'tags', 'include'):
            if key in mod:
                lines.append(f'{key} = {json.dumps(mod[key], ensure_ascii=False)}')
        if mod.get('data'):
            # Pins only: the data itself ships in release ZIPs, never in the source snapshot.
            lines.append('data = { ' + ', '.join(f'{json.dumps(path)} = {json.dumps(sha)}'
                                                 for path, sha in sorted(mod['data'].items())) + ' }')
        lines.append('project = ' + json.dumps(mod['name'] + '/' + mod['project'].name))
        lines.append('')
    return '\n'.join(lines).encode()


def mod_input_hashes(files, mods):
    """Stable build/package inputs per plugin, independent of other releases.

    Root publication metadata, pack versions and tests do not change a plugin's
    build ID. Shared-source consumers conservatively include all production
    Shared sources, so a helper change cannot slip past an unchanged version.
    """
    from release_source import COVER, source_hash, strip_cover
    result = {}
    for mod in mods:
        prefix = mod['name'] + '/'
        # README covers are presentation only and stay out of the fingerprint.
        inputs = {name: data for name, data in files.items()
                  if name.startswith(prefix) and 'tests' not in Path(name).parts and name != prefix + COVER}
        if prefix + 'README.md' in inputs:
            inputs[prefix + 'README.md'] = strip_cover(inputs[prefix + 'README.md'], mod['name'])
        project = files[mod['name'] + '/' + mod['project'].name].replace(b'\\', b'/')
        if b'Shared/' in project:
            inputs.update({name: data for name, data in files.items()
                           if name.startswith('Shared/') and 'tests' not in Path(name).parts
                           and Path(name).suffix in {'.cs', '.props', '.targets', '.txt'}})
        inputs.update({name: files[name] for name in ('global.json', 'NuGet.Config', 'LICENSE', 'LICENSE-NOTICE.md') if name in files})
        inputs['_package-inputs.json'] = canonical({k: mod[k] for k in ('guid', 'version', 'assembly', 'include', 'data') if k in mod})
        result[mod['name']] = source_hash(inputs)
    return result


def download_reference(url):
    parsed = urlsplit(url)
    if parsed.scheme != 'https' or parsed.hostname != 'github.com' or parsed.username or parsed.password or parsed.port not in (None, 443):
        raise ValueError('Published plugin references must use HTTPS GitHub release URLs')
    parts = parsed.path.strip('/').split('/')
    if len(parts) == 6 and parts[2:4] == ['releases', 'download'] and has_gh_auth():
        repo = '/'.join(parts[:2])
        release = gh_json('api', f'repos/{repo}/releases/tags/{quote(unquote(parts[4]), safe="")}')
        asset = next((a for a in release.get('assets', []) if a['name'] == unquote(parts[5])), None)
        if asset is None:
            raise ValueError('Published reference asset is missing: ' + url)
        if asset['size'] > 64 * 1024 * 1024:
            raise ValueError('Published plugin reference exceeds the 64 MiB download limit')
        data = gh('api', f'repos/{repo}/releases/assets/{asset["id"]}', '-H', 'Accept: application/octet-stream')
        if len(data) > 64 * 1024 * 1024:
            raise ValueError('Published plugin reference exceeds the 64 MiB download limit')
        return data
    try:
        with urlopen(Request(url, headers={'User-Agent': 'nuclear-option-local-release'}), timeout=45) as response:
            data = response.read(64 * 1024 * 1024 + 1)
    except OSError as error:
        raise ValueError('Could not verify the published plugin package: ' + url) from error
    if len(data) > 64 * 1024 * 1024:
        raise ValueError('Published plugin reference exceeds the 64 MiB download limit')
    return data


def reference_identity(mod, artifact):
    data = download_reference(artifact['url'])
    checksum = digest(data)
    if artifact.get('checksum') and artifact['checksum'] != 'sha256:' + checksum:
        raise ValueError(f'Published package checksum mismatch: {mod["name"]}')
    metadata = {}
    if artifact['filename'].lower().endswith('.dll'):
        dll = data
    else:
        try:
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                dlls = [i for i in archive.infolist() if Path(i.filename).name == mod['assembly'] + '.dll']
                if len(dlls) != 1 or dlls[0].file_size > 32 * 1024 * 1024:
                    raise ValueError(f'Ambiguous or oversized published DLL: {mod["name"]}')
                dll = archive.read(dlls[0])
                notes = [i for i in archive.infolist() if Path(i.filename).name == 'release-info.json']
                if len(notes) == 1 and notes[0].file_size < 65536:
                    metadata = json.loads(archive.read(notes[0]))
        except zipfile.BadZipFile as error:
            raise ValueError(f'Invalid published plugin ZIP: {mod["name"]}') from error
    return {'name': mod['name'], 'version': mod['version'], 'dllSha256': digest(dll),
            'inputHash': metadata.get('inputHash'), 'artifactSha256': checksum, 'url': artifact['url']}


def published_pack_members(mods, releases):
    """Read actual DLL identities from the latest published own/full packs.

    A mod may first ship in a pack, without an individual release tag. Those
    versions must still be protected on later pack AND individual publications.
    """
    result = {m['name']: [] for m in mods}
    for name in PACKS:
        candidates = [(version_key(v), r, filename) for r in releases if not r.get('draft')
                      for prefix, filename in pack_variants(name)
                      if (v := tag_version(r.get('tag_name'), prefix))]
        if not candidates:
            continue
        _, release, filename = max(candidates, key=lambda pair: pair[0])
        asset = next((a for a in release.get('assets', []) if a['name'] == filename), None)
        if asset is None:
            raise ValueError(f'Published pack is missing its named asset: {release["tag_name"]}')
        data = download_reference(asset['browser_download_url'])
        checksum = digest(data)
        if asset.get('digest') and asset['digest'] != 'sha256:' + checksum:
            raise ValueError('Published modpack checksum mismatch')
        try:
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                info = archive.getinfo('build-info.json')
                if info.file_size > 1024 * 1024:
                    raise ValueError('Published modpack metadata exceeds 1 MiB')
                entries = {m['id']: m for m in json.loads(archive.read(info))['mods']}
                for mod in mods:
                    entry = entries.get(mod['guid'])
                    if entry is None:
                        continue
                    version_key(entry['version'])
                    dll = archive.getinfo(f'mods/{mod["guid"]}/{mod["assembly"]}.dll')
                    if dll.file_size > 32 * 1024 * 1024:
                        raise ValueError('Oversized DLL in a published modpack')
                    result[mod['name']].append({'name': mod['name'], 'version': entry['version'],
                        'dllSha256': digest(archive.read(dll)), 'inputHash': entry.get('inputHash'),
                        'artifactSha256': checksum, 'url': asset['browser_download_url']})
        except (zipfile.BadZipFile, KeyError, json.JSONDecodeError) as error:
            raise ValueError('Cannot read published modpack member identities') from error
    return result


def pack_references(mods, selected_names, input_hashes, remote, *, include_unselected=True):
    """Protect previously shipped versions, including pack-only publications."""
    pack_members = published_pack_members(mods, remote['releases'])
    references = {}
    for mod in mods:
        if not include_unselected and mod['name'] not in selected_names:
            continue
        latest = remote['versions'].get(mod['name'])
        known = [r['version'] for r in pack_members[mod['name']]]
        if latest:
            known.append(latest)
        latest = max(known, key=version_key) if known else None
        if latest is None or version_key(mod['version']) > version_key(latest):
            continue  # A new, unreleased version can be introduced in a pack.
        if version_key(mod['version']) < version_key(latest):
            raise ValueError(f'Pack would downgrade {mod["name"]} to {mod["version"]}; published version is {latest}')
        prior = [r for r in pack_members[mod['name']] if r['version'] == mod['version']]
        # Selected individual releases are separately validated by their immutable
        # tag/asset receipt. Only prior packs constrain a selected new release.
        if mod['name'] in selected_names:
            if not prior:
                continue
            reference = prior[0]
            if any(r['dllSha256'] != reference['dllSha256'] for r in prior):
                raise ValueError(f'Published packs disagree on {mod["name"]} {mod["version"]}; use a new version')
            if any(r['inputHash'] and r['inputHash'] != input_hashes[mod['name']] for r in prior):
                raise ValueError(f'{mod["name"]} source/package inputs changed without a version bump. Bump it and make a new plan.')
            references[mod['name']] = reference
            continue
        tag = mod['name'] + '-v' + mod['version']
        release = next((r for r in remote['releases'] if r['tag_name'] == tag and not r.get('draft')), None)
        artifact = None
        if release:
            asset = next((a for a in release.get('assets', []) if a['name'] == mod['name'] + '.zip'), None)
            if asset:
                artifact = {'url': asset['browser_download_url'], 'filename': asset['name'], 'checksum': asset.get('digest')}
        if artifact is None:
            entry = remote.get('registryEntries', {}).get(mod['guid'], {})
            old = next((a for a in entry.get('artifacts', []) if a.get('version') == mod['version']), None)
            if old:
                artifact = {'url': old['downloadUrl'], 'filename': old['fileName'], 'checksum': old.get('hash')}
        if artifact is None:
            old_release = next((r for r in remote.get('legacyReleases', {}).get(mod['guid'], [])
                                if tag_version(r.get('tag_name'), 'v') == mod['version'] and not r.get('draft')), None)
            if old_release and len(old_release.get('assets', [])) == 1:
                asset = old_release['assets'][0]
                artifact = {'url': asset['browser_download_url'], 'filename': asset['name'], 'checksum': asset.get('digest')}
        if artifact is None and prior:
            reference = prior[0]
        elif artifact is None:
            raise ValueError(f'Cannot verify published {mod["name"]} {mod["version"]} for the pack; bump and select it for release')
        else:
            reference = reference_identity(mod, artifact)
        if any(r['dllSha256'] != reference['dllSha256'] for r in prior):
            raise ValueError(f'Published files disagree on {mod["name"]} {mod["version"]}; use a new version')
        if any(r['inputHash'] and r['inputHash'] != input_hashes[mod['name']] for r in [reference, *prior]):
            raise ValueError(f'{mod["name"]} source/package inputs changed without a version bump. Bump it and make a new plan.')
        references[mod['name']] = reference
    return references


def check_pack_dlls(mods, contents, references):
    for mod in mods:
        reference = references.get(mod['name'])
        if reference:
            dll = contents[f'mods/{mod["guid"]}/{mod["assembly"]}.dll']
            if digest(dll) != reference['dllSha256']:
                raise ValueError(f'Pack DLL for {mod["name"]} {mod["version"]} differs from the published same-version DLL. Bump and select this mod, or rebuild with the original inputs/toolchain.')


def plan(root, names, packs, *, game_version, output, repo=None, offline=False,
         prerelease=False, notes='', include_untracked=False, license_file=None, exclude_source_mods=None):
    packs = ['our' if name == 'mine' else name for name in packs]
    from release_source import (collect_source, source_hash, file_manifest, write_snapshot,
                                public_readme, scan_for_secrets, list_source_files,
                                is_exportable, mod_directories)
    builder = load_builder(root)
    mods = builder.load_mods(root)
    excluded = select_mods(mods, exclude_source_mods)
    excluded_names = {m['name'] for m in excluded}
    if excluded_names.intersection(names):
        raise ValueError('A selected release cannot also be excluded from source')
    if excluded and packs:
        raise ValueError('Source exclusions cannot be used with packs; packs must contain all active mods')
    mods = [m for m in mods if m['name'] not in excluded_names]
    selected = select_mods(mods, names)
    if not selected and not packs:
        raise ValueError('Select --mod, --all, or --pack')
    if not re.fullmatch(r'[0-9]+(?:\.[0-9]+){1,3}', game_version or ''):
        raise ValueError('--game-version requires the client game version, such as 0.34.2')
    settings = config(root)
    repo = repository(repo or settings['publication']['repository'])
    remote = discovery(mods, repo, offline=offline)
    if remote.get('defaultBranch') and remote['defaultBranch'] != settings['publication']['branch']:
        raise ValueError('Configured publication branch does not match the public default branch')
    items = []
    for mod in selected:
        items.append({'name': mod['name'], 'kind': 'mod', 'id': mod['guid'], 'version': mod['version'],
                      'tag': mod['name'] + '-v' + mod['version'], 'asset': mod['name'] + '.zip'})
    for name in packs:
        prefix, asset = PACKS[name]
        value = settings['packs'][name + '_version']
        version_key(value)
        items.append({'name': name, 'kind': 'pack', 'version': value, 'tag': prefix + value, 'asset': asset})
    for item in items:
        latest = remote['versions'].get(item['name'])
        if item['kind'] == 'pack':
            known = pack_versions(remote['releases'], item['name'])
            latest = max(known, key=version_key) if known else None
        existing = next((r for r in remote['releases'] if r['tag_name'] == item['tag']), None)
        if existing is not None:
            raise ValueError(f'Tag {item["tag"]} already exists. Resume its original saved plan, or bump for a new update.')
        if latest and version_key(item['version']) <= version_key(latest):
            raise ValueError(f'{item["name"]} {item["version"]} must exceed published {latest}; use bump first')
        item['prerelease'] = prerelease
        item['notes'] = notes.strip() or f'{release_title(item)} for Nuclear Option {game_version}.'
    files = collect_source(root, mods, include_untracked=include_untracked)
    if excluded and 'install-all-mods.sh' in files:
        script = files['install-all-mods.sh'].decode()
        for name in excluded_names:
            script = re.sub(r'^\s*"' + re.escape(name) + r'"\s*\n', '', script, flags=re.MULTILINE)
        files['install-all-mods.sh'] = script.encode()
    directories = mod_directories(root, mods)
    new_source = [p for p, mode in list_source_files(root, True).items()
                  if mode == 'untracked' and is_exportable(p, directories)]
    modified_source = [p.decode('utf-8') for p in git('diff', '--name-only', '-z', 'HEAD', cwd=root).split(b'\0')
                       if p and is_exportable(p.decode('utf-8'), directories)]
    if license_file:
        files['LICENSE'] = Path(license_file).read_bytes().replace(b'\r\n', b'\n')
    files['README.md'] = public_readme(mods, repo, files)
    files['tools/nomnom-mirrors.toml'] = sanitized_config(mods)
    # Public tools remain usable without copying private release configuration.
    files['tools/release.toml'] = (
        '[publication]\nrepository = ' + json.dumps(repo) + '\nbranch = ' + json.dumps(settings['publication']['branch'])
        + '\ncommit_name = "dikkadev"\ncommit_email = "dikkadev@users.noreply.github.com"\n\n[packs]\n'
        + '\n'.join(f'{name}_version = {json.dumps(settings["packs"][name + "_version"])}' for name in PACKS)
        + '\n\n[build]\nsdk_version = ' + json.dumps(settings.get('build', {}).get('sdk_version', '10.0.401')) + '\n').encode()
    files['global.json'] = canonical({'sdk': {'version': settings.get('build', {}).get('sdk_version', '10.0.401'),
                                              'rollForward': 'latestPatch', 'allowPrerelease': False}}) + b'\n'
    files['.gitignore'] = b'.env\n.env.*\n!.env.example\n**/bin/\n**/obj/\n**/dist/\n**/__pycache__/\n.git/\n.jj/\n'
    input_hashes = mod_input_hashes(files, mods)
    references = pack_references(mods, {m['name'] for m in selected}, input_hashes, remote,
                                 include_unselected=bool(packs)) if not offline else {}
    files['publication.json'] = json.dumps({'schema': 1, 'gameVersion': game_version,
        'mods': [{'name': m['name'], 'id': m['guid'], 'version': m['version'], 'inputHash': input_hashes[m['name']]} for m in mods],
        'releases': [{k: i[k] for k in ('kind', 'name', 'version', 'tag', 'asset', 'prerelease')} for i in items]}, indent=2).encode() + b'\n'
    for name, data in files.items():
        scan_for_secrets(name, data)
    for item in items:
        scan_for_secrets('release notes', item['notes'].encode())
    bundled = {mod['name']: read_bundled_data(root, mod) for mod in selected if mod.get('data')}
    created = datetime.now(timezone.utc).isoformat(timespec='seconds')
    value = seal({'schema': 1, 'repository': repo, 'branch': settings['publication']['branch'],
        'commitName': settings['publication']['commit_name'], 'commitEmail': settings['publication']['commit_email'],
        'createdAt': created, 'privateSourceRevision': git('rev-parse', 'HEAD', cwd=root).decode().strip(),
        'workingTreeChanged': bool(git('status', '--porcelain', cwd=root).strip()),
        'modifiedSource': sorted(modified_source), 'untrackedSource': sorted(new_source),
        'omittedUntrackedSource': [] if include_untracked else sorted(new_source),
        'offline': offline, 'baseCommit': remote['baseCommit'], 'repositoryExisted': remote['exists'],
        'registryEntries': {m['guid']: remote.get('registryEntries', {})[m['guid']] for m in selected
                            if m['guid'] in remote.get('registryEntries', {})},
        'modInputHashes': input_hashes, 'packReferences': references,
        'excludedSourceMods': sorted(excluded_names),
        'sourceHash': source_hash(files), 'sourceFiles': file_manifest(files), 'gameVersion': game_version,
        'bundledData': {name: {path: {'sha256': digest(data), 'size': len(data)} for path, data in entries.items()}
                        for name, entries in bundled.items()},
        'items': items}, 'planHash')
    output = Path(output).resolve()
    if output.exists() and any(output.iterdir()):
        raise ValueError('Output directory is not empty; choose a new --output')
    output.mkdir(parents=True, exist_ok=True)
    write_snapshot(files, output / 'source')
    for name, entries in bundled.items():
        for path, data in entries.items():
            target = output / 'data' / name / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
    write_json(output / 'plan.json', value)
    return value


def read_bundled_data(root, mod):
    """Read a mod's pinned, private data files from its data_source folder."""
    source = Path(root).resolve() / mod.get('data_source', '')
    if not mod.get('data_source') or not source.resolve().is_relative_to(Path(root).resolve()):
        raise ValueError(f'{mod["name"]}: data entries need a data_source inside the repository')
    result = {}
    for path, sha256 in sorted(mod['data'].items()):
        file = source / PurePosixPath(path).name
        if not file.is_file():
            raise ValueError(f'{mod["name"]}: missing data file {file}')
        data = file.read_bytes()
        if digest(data) != sha256:
            raise ValueError(f'{mod["name"]}: {file.name} does not match its pinned SHA-256; update the pin and bump the version')
        result[path] = data
    return result


def plan_data(plan_path, value, mod):
    """Bundled data copied by plan, checked against the plan and the frozen pins."""
    folder = Path(plan_path).resolve().parent / 'data' / mod['name']
    expected = value.get('bundledData', {}).get(mod['name'], {})
    if set(expected) != set(mod.get('data', {})):
        raise ValueError(f'{mod["name"]}: planned data differs from the frozen data pins')
    result = {}
    for path, entry in expected.items():
        file = folder / path
        if file.is_symlink() or not file.resolve().is_relative_to(folder.resolve()) or not file.is_file():
            raise ValueError(f'{mod["name"]}: prepared data file is missing or unsafe: {path}')
        data = file.read_bytes()
        if digest(data) != entry['sha256'] or len(data) != entry['size'] or entry['sha256'] != mod['data'][path]:
            raise ValueError(f'{mod["name"]}: prepared data changed: {path}')
        result[path] = data
    return result


def read_plan(path):
    path = Path(path).resolve()
    value = json.loads(path.read_text('utf-8'))
    check_seal(value, 'planHash')
    if value.get('schema') != 1:
        raise ValueError('Unsupported plan schema')
    repository(value['repository'])
    if not value['items']:
        raise ValueError('The plan contains no releases')
    branch = value['branch']
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._/-]*', branch) or any(
            token in branch for token in ('..', '//', '@{')) or any(
                part.endswith(('.', '.lock')) or part.startswith('.') for part in branch.split('/')) or branch.endswith('/'):
        raise ValueError('Unsafe publication branch')
    if value['baseCommit'] is not None and not re.fullmatch(r'[0-9a-f]{40}', value['baseCommit']):
        raise ValueError('Invalid public baseline commit')
    files = read_source(path, value)
    from release_source import is_exportable, mod_directories, scan_for_secrets
    snapshot = path.parent / 'source'
    # Read metadata using the trusted local runner, not Python code in a handoff.
    mods = load_builder().load_mods(snapshot)
    by_name = {m['name']: m for m in mods}
    directories = mod_directories(snapshot, mods)
    generated = {'README.md', 'publication.json', 'global.json', 'tools/nomnom-mirrors.toml', 'tools/release.toml'}
    for name, content in files.items():
        if name not in generated and not is_exportable(name, directories):
            raise ValueError(f'Prepared source is outside the public allowlist: {name}')
        scan_for_secrets(name, content)
    input_hashes = mod_input_hashes(files, mods)
    if value.get('modInputHashes', input_hashes) != input_hashes:
        raise ValueError('Per-mod input fingerprints differ from the frozen source')
    for name, reference in value.get('packReferences', {}).items():
        if name not in by_name or reference['version'] != by_name[name]['version'] or not re.fullmatch(r'[0-9a-f]{64}', reference['dllSha256']):
            raise ValueError('Invalid published pack-member reference')
    seen_tags, seen_assets = set(), set()
    for item in value['items']:
        version_key(item['version'])
        if not isinstance(item['prerelease'], bool) or not isinstance(item['notes'], str):
            raise ValueError('Invalid release category or notes')
        scan_for_secrets('release notes', item['notes'].encode())
        if item['kind'] == 'mod':
            mod = by_name.get(item['name'])
            if mod is None or not re.fullmatch(r'[A-Za-z][A-Za-z0-9_]*', item['name']) or mod['version'] != item['version'] or mod['guid'] != item['id']:
                raise ValueError('Planned plugin identity/version differs from the frozen project')
            tag, asset = item['name'] + '-v' + item['version'], item['name'] + '.zip'
        elif item['kind'] == 'pack' and item['name'] in PACKS:
            prefix, asset = PACKS[item['name']]
            tag = prefix + item['version']
            for old_prefix, old_asset in pack_variants(item['name']):
                if item['tag'] == old_prefix + item['version'] and item['asset'] == old_asset:
                    tag, asset = item['tag'], item['asset']
                    break
            if config(snapshot)['packs'][item['name'] + '_version'] != item['version']:
                raise ValueError('Planned pack version differs from the frozen configuration')
        else:
            raise ValueError('Unknown release item')
        if item['tag'] != tag or item['asset'] != asset or tag in seen_tags or asset in seen_assets:
            raise ValueError('Release tag/asset does not match its identity/version, or is duplicated')
        seen_tags.add(tag)
        seen_assets.add(asset)
    return value


def read_source(path, value):
    from release_source import file_manifest, source_hash
    directory = Path(path).resolve().parent / 'source'
    if directory.resolve() != directory:
        raise ValueError('Prepared source directory must not be a link')
    files = {}
    for file in directory.rglob('*'):
        if file.is_symlink() or not file.resolve().is_relative_to(directory.resolve()):
            raise ValueError('Symlink or path escape in prepared source')
        if file.is_file():
            files[file.relative_to(directory).as_posix()] = file.read_bytes()
    if source_hash(files) != value['sourceHash'] or file_manifest(files) != value['sourceFiles']:
        raise ValueError('Prepared source changed; make a new plan')
    return files


def write_archive(path, files):
    with zipfile.ZipFile(path, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, content in sorted(files.items()):
            if name.startswith('/') or '..' in Path(name).parts or '\\' in name:
                raise ValueError('Unsafe archive member')
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, content)


def prepare(plan_path, *, root=ROOT):
    value = read_plan(plan_path)
    folder = Path(plan_path).resolve().parent
    if (folder / 'receipt.json').exists():
        return verify_prepared(plan_path)
    files = read_source(plan_path, value)
    from release_source import write_snapshot
    builder = load_builder(root)
    env = builder.environment(root)
    for key in ('GH_TOKEN', 'GITHUB_TOKEN', 'GH_ENTERPRISE_TOKEN', 'GITHUB_ENTERPRISE_TOKEN'):
        env.pop(key, None)
    # All compilation takes place in a disposable copy of the exact public source.
    with tempfile.TemporaryDirectory(prefix='no-release-build-') as temporary:
        build = Path(temporary) / 'source'
        write_snapshot(files, build)
        mods = builder.load_mods(build)
        env.update({'SourceRevisionId': value['sourceHash'], 'ContinuousIntegrationBuild': 'true',
                    'PathMap': str(build) + '=/_/src', 'MSBUILDDISABLENODEREUSE': '1',
                    'UseSharedCompilation': 'false'})
        by_name = {mod['name']: mod for mod in mods}
        input_hashes = mod_input_hashes(files, mods)
        for item in value['items']:
            if item['kind'] == 'mod' and (by_name[item['name']]['version'] != item['version'] or by_name[item['name']]['guid'] != item['id']):
                raise ValueError('Release version/ID differs from the source project')
        builder.verify_build(build, mods, env, source_revisions=input_hashes)
        contents = builder.payload(build, mods)
        check_pack_dlls(mods, contents, value.get('packReferences', {}))
        info = {'schema': 1, 'verification': 'build+logic+metadata', 'sourceHash': value['sourceHash'],
                'mods': [{'id': m['guid'], 'version': m['version'], 'inputHash': input_hashes[m['name']],
                          'dllSha256': digest(contents[f'mods/{m["guid"]}/{m["assembly"]}.dll'])} for m in mods]}
        contents['build-info.json'] = canonical(info)
        prepared = folder / 'artifacts'
        prepared.mkdir(exist_ok=True)
        receipts = []
        for item in value['items']:
            if item['kind'] == 'mod':
                mod = next(m for m in mods if m['name'] == item['name'])
                prefix = f'mods/{mod["guid"]}/'
                payload = {mod['name'] + '/' + name[len(prefix):]: data for name, data in contents.items()
                           if name.startswith(prefix) and name != prefix + 'meta.json'}
                payload[mod['name'] + '/release-info.json'] = canonical({
                    'id': item['id'], 'version': item['version'], 'sourceHash': value['sourceHash'],
                    'inputHash': input_hashes[mod['name']]})
                if 'LICENSE' in files:
                    payload[mod['name'] + '/LICENSE'] = files['LICENSE']
                    if 'LICENSE-NOTICE.md' in files:
                        payload[mod['name'] + '/LICENSE-NOTICE.md'] = files['LICENSE-NOTICE.md']
                if mod.get('data'):
                    if mod['name'] + '/DATA-NOTICE.md' not in payload:
                        raise ValueError(f'{mod["name"]}: bundled data requires DATA-NOTICE.md in its include list')
                    for path, data in plan_data(plan_path, value, mod).items():
                        payload[mod['name'] + '/' + path] = data
            else:
                payload = contents
                if item['name'] == 'full':
                    payload = builder.full_payload(build, contents)
                payload = dict(payload)
                payload['release-info.json'] = canonical({'version': item['version'], 'sourceHash': value['sourceHash']})
                if 'LICENSE' in files:
                    if item['name'] == 'full':
                        payload['licenses/our-mods-LICENSE.txt'] = files['LICENSE']
                        if 'LICENSE-NOTICE.md' in files:
                            payload['licenses/our-mods-LICENSE-NOTICE.md'] = files['LICENSE-NOTICE.md']
                        payload['LICENSE-SCOPE.txt'] = b'The original-code license in licenses/our-mods-LICENSE.txt applies to the bundled dev.dikka plugins. External plugins are registry references, with their own upstream licenses; their files are not bundled here.\n'
                    else:
                        payload['LICENSE'] = files['LICENSE']
                        if 'LICENSE-NOTICE.md' in files:
                            payload['LICENSE-NOTICE.md'] = files['LICENSE-NOTICE.md']
            target = prepared / item['asset']
            write_archive(target, payload)
            receipts.append({'tag': item['tag'], 'asset': item['asset'], 'size': target.stat().st_size,
                             'sha256': digest(target.read_bytes())})
        metadata = folder / 'manifests'
        metadata.mkdir(exist_ok=True)
        for item in value['items']:
            if item['kind'] != 'mod':
                continue
            mod = next(m for m in mods if m['name'] == item['name'])
            asset = next(a for a in receipts if a['tag'] == item['tag'])
            write_json(metadata / (mod['guid'] + '.json'), manifest(mod, item, asset, value))
    reference = Path(env['NO_MANAGED_DIR']) / 'Assembly-CSharp.dll'
    receipt = seal({'schema': 1, 'planHash': value['planHash'], 'sourceHash': value['sourceHash'],
                    'verification': 'local-build+logic+client-metadata; game-runtime-not-tested',
                    'clientReferenceSha256': digest(reference.read_bytes()), 'assets': receipts}, 'receiptHash')
    write_json(folder / 'receipt.json', receipt)
    return receipt


def manifest(mod, item, asset, value):
    source = f'https://github.com/{value["repository"]}/tree/{item["tag"]}'
    artifact = {'type': 'plugin', 'version': item['version'], 'category': 'preRelease' if item['prerelease'] else 'release',
                'gameVersion': value['gameVersion'], 'fileName': item['asset'],
                'downloadUrl': f'https://github.com/{value["repository"]}/releases/download/{item["tag"]}/{item["asset"]}',
                'hash': 'sha256:' + asset['sha256']}
    # Registry relationships require registered IDs and known version bounds.
    # Do not invent registry versions for retired, unregistered native conflicts.
    old = value.get('registryEntries', {}).get(mod['guid'], {})
    historical = old.get('artifacts', [])
    if historical:
        for relationship in ('dependencies', 'incompatibilities', 'extends'):
            if relationship in historical[0]:
                artifact[relationship] = historical[0][relationship]
    artifacts = {entry['version']: entry for entry in historical if 'version' in entry}
    artifacts[item['version']] = artifact
    return dict(old, **{'id': mod['guid'], 'displayName': mod.get('display_name', mod['name']),
            'description': mod['description'], 'tags': mod.get('tags', []), 'authors': ['dikkadev'],
            'isClientOrServer': 'Client', 'urls': [{'name': 'info', 'url': source}, {'name': 'source', 'url': source}],
            'githubOwner': value['repository'].split('/')[0], 'githubRepoName': value['repository'].split('/')[1],
            'autoUpdateArtifacts': 'True', 'releaseTagPrefix': mod['name'] + '-v', 'releaseAssetName': item['asset'],
            'artifacts': [artifacts[v] for v in sorted(artifacts, key=version_key, reverse=True)]})


def verify_prepared(plan_path):
    value = read_plan(plan_path)
    folder = Path(plan_path).resolve().parent
    receipt = json.loads((folder / 'receipt.json').read_text('utf-8'))
    check_seal(receipt, 'receiptHash')
    if receipt['planHash'] != value['planHash'] or receipt['sourceHash'] != value['sourceHash']:
        raise ValueError('Receipt belongs to a different plan or source snapshot')
    if len(receipt['assets']) != len(value['items']):
        raise ValueError('Receipt is missing a release asset')
    for item in value['items']:
        matching = [a for a in receipt['assets'] if a['tag'] == item['tag'] and a['asset'] == item['asset']]
        if len(matching) != 1:
            raise ValueError('Receipt release/asset set does not match the plan')
        expected = matching[0]
        file = folder / 'artifacts' / item['asset']
        if file.is_symlink() or not file.resolve().is_relative_to(folder) or file.stat().st_size != expected['size'] or digest(file.read_bytes()) != expected['sha256']:
            raise ValueError(f'Prepared asset changed: {item["asset"]}')
    return receipt


def gh(*args, data=None):
    return run(['gh', *args], data=data, env=dict(os.environ, GH_HOST='github.com', GH_PROMPT_DISABLED='1'))


def gh_json(*args, data=None):
    return json.loads(gh(*args, data=data))


def authenticated_releases(repo):
    pages = gh_json('api', '--paginate', '--slurp', f'repos/{repo}/releases?per_page=100')
    return [item for page in pages for item in page]


def release_body(item, commit, value, asset):
    return (item['notes'] + '\n\n' + f'Source: https://github.com/{value["repository"]}/tree/{commit}\n\n'
            + f'SHA-256 ({asset["asset"]}): `{asset["sha256"]}`\n')


def release_title(item):
    name = item['name']
    if item['kind'] == 'pack':
        if item['tag'].startswith(LEGACY_PACKS[name][0]):
            name = {'our': 'Nuclear Option Client Modpack', 'full': 'Nuclear Option Full Modpack'}[name]
        else:
            name = 'Nuclear Option Modpack -' + pack_label(name)
    return f'{name} {item["version"]}'


def verify_remote_asset(repo, tag, actual, expected):
    if actual.get('state', 'uploaded') != 'uploaded':
        raise ValueError(f'Remote asset upload is incomplete: {tag}')
    if actual['name'] != expected['asset'] or actual['size'] != expected['size']:
        raise ValueError(f'Remote asset name/size mismatch: {tag}')
    checksum = actual.get('digest')
    if checksum:
        if checksum != 'sha256:' + expected['sha256']:
            raise ValueError(f'Remote asset SHA-256 mismatch: {tag}')
    else:
        with tempfile.TemporaryDirectory(prefix='release-download-') as temporary:
            gh('release', 'download', tag, '--repo', repo, '--pattern', expected['asset'], '--dir', temporary)
            if digest((Path(temporary) / expected['asset']).read_bytes()) != expected['sha256']:
                raise ValueError(f'Downloaded remote asset SHA-256 mismatch: {tag}')


def check_remote_release(repo, release, item, asset, body, *, allow_incomplete):
    normalize = lambda text: (text or '').replace('\r\n', '\n').strip()
    if release.get('prerelease') != item['prerelease'] or release.get('name') != release_title(item) or normalize(release.get('body')) != normalize(body):
        raise ValueError(f'Release metadata differs from the prepared update: {item["tag"]}')
    assets = release.get('assets', [])
    if allow_incomplete and release['draft'] and len(assets) == 1 and assets[0].get('state') in {'starter', 'open'} and assets[0]['name'] == asset['asset']:
        return
    if len(assets) == 1:
        verify_remote_asset(repo, item['tag'], assets[0], asset)
    elif not (allow_incomplete and release['draft'] and not assets):
        raise ValueError(f'Remote release asset set mismatch: {item["tag"]}')


def snapshot_commit(value, files, cwd):
    """Create a public-only commit. No private objects or development refs enter here."""
    git('init', '--template=', '--object-format=sha1', '--initial-branch', value['branch'], cwd=cwd)
    git('config', 'core.autocrlf', 'false', cwd=cwd)
    git('config', 'core.hooksPath', '.git/no-hooks', cwd=cwd)
    remote = f'https://github.com/{value["repository"]}.git'
    git('remote', 'add', 'origin', remote, cwd=cwd)
    auth = ['-c', 'credential.helper=', '-c', 'credential.helper=!gh auth git-credential']
    if value['baseCommit']:
        git(*auth, 'fetch', '--depth=1', 'origin', value['baseCommit'], cwd=cwd)
        git('update-ref', f'refs/heads/{value["branch"]}', value['baseCommit'], cwd=cwd)
    # The temporary worktree is empty except .git, even when its parent commit exists.
    for name, data in files.items():
        path = Path(cwd) / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    # Bypass .gitignore, global excludes and all clean filters. The public tree
    # must contain exactly the bytes that were built, with portable script modes.
    names = sorted(files)
    blobs = git('hash-object', '-w', '--no-filters', '--stdin-paths', cwd=cwd,
                data=('\n'.join(names) + '\n').encode()).decode().splitlines()
    expected = {name: ('100755' if name.endswith('.sh') else '100644', sha)
                for name, sha in zip(names, blobs)}
    if len(blobs) != len(names):
        raise ValueError('Git did not hash the complete public source tree')
    git('read-tree', '--empty', cwd=cwd)
    records = b''.join(f'{expected[name][0]} {expected[name][1]}\t{name}\0'.encode() for name in names)
    git('update-index', '-z', '--index-info', cwd=cwd, data=records)
    env = dict(os.environ, GIT_AUTHOR_NAME=value['commitName'], GIT_AUTHOR_EMAIL=value['commitEmail'],
               GIT_COMMITTER_NAME=value['commitName'], GIT_COMMITTER_EMAIL=value['commitEmail'],
               GIT_AUTHOR_DATE=value['createdAt'], GIT_COMMITTER_DATE=value['createdAt'])
    title = 'Publish ' + ', '.join(f'{i["name"]} {i["version"]}' for i in value['items'])
    git('-c', 'core.autocrlf=false', '-c', 'commit.gpgsign=false', 'commit', '--allow-empty', '-m', title, cwd=cwd, env=env)
    actual = {}
    for record in git('ls-tree', '-r', '-z', '--full-tree', 'HEAD', cwd=cwd).split(b'\0'):
        if record:
            header, name = record.split(b'\t', 1)
            mode, kind, sha = header.decode().split(' ')
            actual[name.decode()] = (mode, sha)
    if actual != expected:
        raise ValueError('Public Git tree differs from the prepared source bytes or modes')
    return git('rev-parse', 'HEAD', cwd=cwd).decode().strip(), auth


def publish(plan_path, *, create_repo=False, dry_run=False, expect_hash=None):
    value = read_plan(plan_path)
    receipt = verify_prepared(plan_path)
    if expect_hash and expect_hash != value['planHash']:
        raise ValueError('--expect-hash does not match the prepared plan')
    if value['offline']:
        raise ValueError('Offline plans are local previews. Make a new online plan before publication.')
    files = read_source(plan_path, value)
    if 'LICENSE' not in files or not files['LICENSE'].strip():
        raise ValueError('Choose a source license before publication: add LICENSE or plan with --license-file FILE')
    repo = value['repository']
    login = gh_json('api', 'user')['login']
    owner = repo.split('/')[0]
    # Recheck version evidence even for offline plans, before the first external write.
    builder = load_builder()
    mods = builder.load_mods(Path(plan_path).resolve().parent / 'source')
    remote = discovery(mods, repo)
    packs = [i for i in value['items'] if i['kind'] == 'pack']
    references = pack_references(mods, {i['name'] for i in value['items'] if i['kind'] == 'mod'},
                                 mod_input_hashes(files, mods), remote, include_unselected=bool(packs))
    if references:
        contents = {}
        if packs:
            archive_path = Path(plan_path).resolve().parent / 'artifacts' / packs[0]['asset']
            with zipfile.ZipFile(archive_path) as archive:
                for mod in mods:
                    name = f'mods/{mod["guid"]}/{mod["assembly"]}.dll'
                    if archive.getinfo(name).file_size > 32 * 1024 * 1024:
                        raise ValueError('Oversized prepared plugin DLL')
                    contents[name] = archive.read(name)
        else:
            for mod in mods:
                if mod['name'] in references:
                    with zipfile.ZipFile(Path(plan_path).resolve().parent / 'artifacts' / (mod['name'] + '.zip')) as archive:
                        name = mod['name'] + '/' + mod['assembly'] + '.dll'
                        if archive.getinfo(name).file_size > 32 * 1024 * 1024:
                            raise ValueError('Oversized prepared plugin DLL')
                        contents[f'mods/{mod["guid"]}/{mod["assembly"]}.dll'] = archive.read(name)
        check_pack_dlls(mods, contents, references)
    if remote['exists']:
        details = gh_json('api', f'repos/{repo}')
        if details['default_branch'] != value['branch']:
            raise ValueError('Repository default branch differs from the plan')
        if not details.get('permissions', {}).get('push'):
            raise ValueError(f'GitHub account {login} does not have write permission for {repo}; accept its invitation and check collaborator access')
        existing = {r['tag_name']: r for r in authenticated_releases(repo)}
    else:
        existing = {}
        if not create_repo:
            raise ValueError('Repository does not exist; use --create-repo for the first publication')
        if login.lower() != owner.lower():
            raise ValueError(f'The repository must first be created by {owner}; clankagent can publish to an existing repository as a collaborator')
    for item in value['items']:
        latest = remote['versions'].get(item['name'])
        if item['kind'] == 'pack':
            versions = pack_versions(remote['releases'], item['name'])
            latest = max(versions, key=version_key) if versions else None
        if latest and version_key(item['version']) <= version_key(latest) and item['tag'] not in existing:
            raise ValueError(f'Newer/equal version now exists for {item["name"]}; bump and prepare again')
    assets_by_tag = {a['tag']: a for a in receipt['assets']}
    with tempfile.TemporaryDirectory(prefix='public-release-') as temporary:
        commit, auth = snapshot_commit(value, files, Path(temporary))
        refs = git(*auth, 'ls-remote', 'origin', f'refs/heads/{value["branch"]}', 'refs/tags/*', cwd=temporary).decode().splitlines() if remote['exists'] else []
        remote_refs = {line.split('\t')[1]: line.split('\t')[0] for line in refs}
        head = remote_refs.get(f'refs/heads/{value["branch"]}')
        if head not in (value['baseCommit'], commit):
            raise ValueError('Public branch advanced to another update; make a new plan (no force push is used)')
        for item in value['items']:
            asset = assets_by_tag[item['tag']]
            tag_sha = remote_refs.get('refs/tags/' + item['tag'])
            if tag_sha and tag_sha != commit:
                raise ValueError(f'Tag already points at another source snapshot: {item["tag"]}. Resume the original saved plan, or bump and make a new update.')
            if item['tag'] in existing:
                check_remote_release(repo, existing[item['tag']], item, asset, release_body(item, commit, value, asset), allow_incomplete=True)
            git('update-ref', 'refs/tags/' + item['tag'], commit, cwd=temporary)
        if dry_run:
            return {'dryRun': True, 'repository': repo, 'planHash': value['planHash'], 'sourceCommit': commit,
                    'actions': ['push one complete source snapshot and its release tags', 'stage, verify and publish releases'],
                    'items': value['items']}
        if not remote['exists']:
            gh('repo', 'create', repo, '--public', '--description', 'Nuclear Option client mods: published source snapshots and locally built releases')
        git(*auth, 'push', '--atomic', 'origin', f'HEAD:refs/heads/{value["branch"]}',
            *['refs/tags/' + i['tag'] for i in value['items']], cwd=temporary)
    # Stage and verify every asset before making any new release visible to NOMNOM.
    staged = []
    folder = Path(plan_path).resolve().parent
    for item in value['items']:
        asset = assets_by_tag[item['tag']]
        body = release_body(item, commit, value, asset)
        release = existing.get(item['tag'])
        if release is None:
            release = gh_json('api', f'repos/{repo}/releases', '--method', 'POST', '--input', '-',
                data=canonical({'tag_name': item['tag'], 'target_commitish': commit,
                                'name': release_title(item), 'body': body,
                                'draft': True, 'prerelease': item['prerelease'], 'make_latest': 'false'}))
        if release['draft'] and len(release.get('assets', [])) == 1 and release['assets'][0].get('state') in {'starter', 'open'}:
            # GitHub may retain an unusable starter asset after a failed upload.
            # Only that incomplete draft asset is removed; published data is immutable.
            gh('api', f'repos/{repo}/releases/assets/{release["assets"][0]["id"]}', '--method', 'DELETE')
            release['assets'] = []
        if not release.get('assets'):
            gh('release', 'upload', item['tag'], str(folder / 'artifacts' / item['asset']), '--repo', repo)
        release = gh_json('api', f'repos/{repo}/releases/{release["id"]}')
        check_remote_release(repo, release, item, asset, body, allow_incomplete=False)
        staged.append((item, release))
    for item, release in staged:
        if release['draft']:
            gh('api', f'repos/{repo}/releases/{release["id"]}', '--method', 'PATCH', '--input', '-',
               data=canonical({'draft': False, 'make_latest': 'false'}))
    result = {'repository': repo, 'sourceCommit': commit, 'planHash': value['planHash'],
              'releases': [f'https://github.com/{repo}/releases/tag/{i["tag"]}' for i in value['items']]}
    write_json(folder / 'published.json', result)
    return result


def verify_published(plan_path):
    value = read_plan(plan_path)
    receipt = verify_prepared(plan_path)
    folder = Path(plan_path).resolve().parent
    published = json.loads((folder / 'published.json').read_text('utf-8'))
    if published.get('planHash') != value['planHash'] or not re.fullmatch(r'[0-9a-f]{40}', published['sourceCommit']):
        raise ValueError('Published receipt belongs to a different plan')
    remote = {r['tag_name']: r for r in authenticated_releases(value['repository'])}
    assets_by_tag = {a['tag']: a for a in receipt['assets']}
    for item in value['items']:
        asset = assets_by_tag[item['tag']]
        release = remote.get(item['tag'])
        if release is None or release['draft']:
            raise ValueError(f'Published release missing: {item["tag"]}')
        check_remote_release(value['repository'], release, item, asset,
            release_body(item, published['sourceCommit'], value, asset), allow_incomplete=False)
        ref = gh_json('api', f'repos/{value["repository"]}/git/ref/tags/{item["tag"]}')
        if ref['object']['sha'] != published['sourceCommit']:
            raise ValueError(f'Published tag moved: {item["tag"]}')
    return published
