"""Collect a public, reproducible source snapshot of the active mods.

The snapshot is an allowlisted subset of the Git file list, held in memory as
``{posix_relative_path: bytes}``. Text line endings are normalized to LF before
hashing/building; binary assets stay unchanged. Anything not explicitly allowed,
or that looks like a
credential, is excluded or rejected; error messages name paths and pattern kinds
but never echo secret values.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
from pathlib import Path, PurePosixPath

sys.path.insert(0, str(Path(__file__).resolve().parent))
from process_utils import background_kwargs

# Extensions that may be exported inside the controlled roots.
SOURCE_EXTENSIONS = frozenset({
    '.cs', '.csproj', '.props', '.targets', '.py', '.json', '.md', '.toml', '.txt', '.sh',
})
# Media is exported only below an `assets` directory.
MEDIA_EXTENSIONS = frozenset({'.mp3', '.ogg', '.wav', '.png'})
# Extensionless or dot-prefixed files that are safe by name.
SAFE_NAMES = frozenset({'.gitignore', '.env.example'})

# Directory components that are never exported, at any depth (compared lowercase).
DENIED_DIRECTORIES = frozenset({
    '.git', '.jj', '.dev', '.vs', '.vscode', '.idea', '.agent-feedback', '.cache',
    'archive', 'nuclearreplay', 'references', 'game', 'nuclear option', 'bepinex',
    'screenshots', 'screenshot', 'bin', 'obj', 'dist',
    'node_modules', '__pycache__', 'downloads', 'logs', 'tmp', 'temp',
})
# File names that are never exported (compared lowercase).
DENIED_NAMES = frozenset({
    'agents.md', 'claude.md', 'gemini.md', 'global.json', 'nomnom-mirrors.toml',
    'publication.json', '.env', 'thumbs.db', 'desktop.ini', '.ds_store',
})

# Root-level files that may be exported when tracked.
ROOT_FILES = frozenset({
    'NuGet.Config', '.env.example', '.gitignore', 'install-all-mods.sh',
    'LICENSE', 'LICENSE.md', 'LICENSE.txt', 'NOTICE', 'NOTICE.md', 'NOTICE.txt',
    'LICENSE-NOTICE.md',
    'THIRD-PARTY-NOTICES.txt',
})
# Known-safe build tooling.  The mirror registry is excluded: the publisher
# writes a sanitized replacement instead of exporting the private original.
TOOL_FILES = frozenset({
    'tools/build-modpack.py', 'tools/modpack_registry.py', 'tools/process_utils.py',
    'tools/modpack-extras.toml', 'tools/modpack-extras.lock.json',
    'tools/refresh-modpack-extras.py', 'tools/Build-Modpack.ps1',
    'tools/Invoke-QuietProcess.ps1',
    'tools/Release-Mods.ps1',
})
RELEASE_TOOL = re.compile(r'tools/release(?:_[a-z0-9_]+)?\.py')
SHARED_ROOT = 'Shared'
TESTS_ROOT = 'tests'

# README cover image inside each mod directory (tools/covers/generate-covers.py).
COVER = 'docs/assets/cover.png'
# Covers of unpublished mods, shown in the public README without their source.
PREVIEW_COVERS = {'NuclearReplay': 'assets/covers/NuclearReplay.png'}

SECRET_PATTERNS = (
    ('private key', re.compile(rb'-----BEGIN [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----')),
    ('GitHub token', re.compile(rb'\bgh[pousr]_[A-Za-z0-9]{30,}')),
    ('GitHub fine-grained token', re.compile(rb'\bgithub_pat_[A-Za-z0-9_]{20,}')),
    ('npm token', re.compile(rb'\bnpm_[A-Za-z0-9]{30,}')),
    ('AWS access key', re.compile(rb'\bAKIA[0-9A-Z]{16}\b')),
    ('Slack token', re.compile(rb'\bxox[abprs]-[A-Za-z0-9-]{10,}')),
    ('NuGet feed credentials', re.compile(rb'<package' + rb'SourceCredentials', re.IGNORECASE)),
)


class SourceError(ValueError):
    """The source tree cannot be exported safely."""


# --------------------------------------------------------------------------
# Paths and policy
# --------------------------------------------------------------------------

def check_relative_path(path: str) -> str:
    """Return ``path`` if it is a safe relative POSIX path, else raise."""
    if not isinstance(path, str) or not path or '\\' in path or any(ord(c) < 32 for c in path):
        raise SourceError(f'Unsafe path: {path!r}')
    if path.startswith('/') or re.match(r'^[A-Za-z]:', path):
        raise SourceError(f'Absolute path rejected: {path!r}')
    parts = path.split('/')
    for part in parts:
        if part in ('', '.', '..') or part != part.rstrip(' .') or ':' in part:
            raise SourceError(f'Unsafe path component in {path!r}')
    return path


def mod_directories(root: Path, mods: list[dict]) -> list[str]:
    """Relative POSIX directory of each mod project, validated against policy."""
    if not mods:
        raise SourceError('No active mods supplied')
    root = root.resolve()
    directories = []
    for mod in mods:
        project = Path(mod['project']).resolve()
        try:
            relative = project.relative_to(root).as_posix()
        except ValueError:
            raise SourceError(f"{mod.get('name')}: project is outside the source root") from None
        directory = PurePosixPath(relative).parent.as_posix()
        if directory in ('', '.') or not project.name.endswith('.csproj'):
            raise SourceError(f"{mod.get('name')}: project must be a .csproj in its own directory")
        check_relative_path(directory)
        if directory in directories:
            raise SourceError(f"{mod.get('name')}: duplicate project directory")
        if any(part.lower() in DENIED_DIRECTORIES for part in directory.split('/')):
            raise SourceError(f"{mod.get('name')}: project is in an excluded directory")
        if directory in (SHARED_ROOT, TESTS_ROOT, 'tools'):
            raise SourceError(f"{mod.get('name')}: project directory collides with a shared root")
        directories.append(directory)
    return directories


def is_exportable(path: str, mod_dirs: list[str]) -> bool:
    """Whether a safe relative path belongs in the public snapshot."""
    if path in PREVIEW_COVERS.values():
        return True
    parts = path.split('/')
    name = parts[-1]
    lowered = [part.lower() for part in parts]
    if any(part in DENIED_DIRECTORIES for part in lowered[:-1]):
        return False
    if lowered[-1] in DENIED_NAMES:
        return False
    if len(parts) == 1:
        return name in ROOT_FILES
    if parts[0] == 'tools':
        return path in TOOL_FILES or bool(RELEASE_TOOL.fullmatch(path))
    if parts[0] not in (SHARED_ROOT, TESTS_ROOT) and not any(path.startswith(d + '/') for d in mod_dirs):
        return False
    suffix = PurePosixPath(name).suffix.lower()
    if name in SAFE_NAMES or suffix in SOURCE_EXTENSIONS:
        return True
    return suffix in MEDIA_EXTENSIONS and 'assets' in lowered[:-1]


# --------------------------------------------------------------------------
# Git
# --------------------------------------------------------------------------

def find_git(root: Path | None = None) -> str:
    """Locate git on PATH, or in a workspace-local ``tools/git/cmd`` above root."""
    found = shutil.which('git')
    if found:
        return found
    if root is not None:
        for parent in [root, *root.resolve().parents][:4]:
            candidate = parent / 'tools' / 'git' / 'cmd' / ('git.exe' if os.name == 'nt' else 'git')
            if candidate.is_file():
                return str(candidate)
    raise SourceError('git is required to list source files; put it on PATH')


def _git_list(git: str, root: Path, *arguments: str) -> bytes:
    command = [git, 'ls-files', '-z', *arguments]
    env = dict(os.environ)
    for key in ('GIT_DIR', 'GIT_WORK_TREE', 'GIT_COMMON_DIR', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY',
                'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_NAMESPACE', 'GIT_CONFIG_PARAMETERS', 'GIT_CONFIG_COUNT'):
        env.pop(key, None)
    completed = subprocess.run(command, cwd=root, check=False, stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               env=env,
                               **background_kwargs())
    if completed.returncode != 0:
        detail = completed.stderr.decode('utf-8', 'replace').strip().splitlines()
        raise SourceError('git ls-files failed' + (f': {detail[-1]}' if detail else ''))
    return completed.stdout


def list_source_files(root: Path, include_untracked: bool = False) -> dict[str, str]:
    """Map candidate paths to git mode (``100644`` etc.; untracked use ``untracked``)."""
    git = find_git(root)
    entries: dict[str, str] = {}
    for record in _git_list(git, root, '--cached', '--stage').split(b'\x00'):
        if not record:
            continue
        meta, separator, raw_path = record.partition(b'\t')
        if not separator:
            raise SourceError('Unexpected git ls-files output')
        entries[raw_path.decode('utf-8', 'surrogateescape')] = meta.split(b' ')[0].decode('ascii', 'replace')
    if include_untracked:
        for raw_path in _git_list(git, root, '--others', '--exclude-standard').split(b'\x00'):
            if raw_path:
                entries.setdefault(raw_path.decode('utf-8', 'surrogateescape'), 'untracked')
    return entries


# --------------------------------------------------------------------------
# Collection
# --------------------------------------------------------------------------

def scan_for_secrets(path: str, data: bytes) -> None:
    for kind, pattern in SECRET_PATTERNS:
        if pattern.search(data):
            raise SourceError(f'{path}: contains what looks like a {kind}; secret value not shown')


def read_regular_file(root: Path, path: str) -> bytes:
    """Read bytes of a regular, non-link file whose real path stays inside root."""
    target = root / path
    try:
        info = os.lstat(target)
    except FileNotFoundError:
        raise SourceError(f'{path}: listed by git but missing from the working tree') from None
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
        raise SourceError(f'{path}: symbolic links and special files are not exported')
    if os.path.realpath(target) != os.path.join(os.path.realpath(root), *path.split('/')):
        raise SourceError(f'{path}: resolves outside its expected location (link in path)')
    return target.read_bytes()


def collect_source(root: Path, mods: list[dict], *, include_untracked: bool = False) -> dict[str, bytes]:
    """Return the public source snapshot for ``root`` as ``{path: bytes}``."""
    root = Path(root).resolve()
    mod_dirs = mod_directories(root, mods)
    files: dict[str, bytes] = {}
    candidates = list_source_files(root, include_untracked)
    for name, public in PREVIEW_COVERS.items():
        if candidates.get(f'{name}/{COVER}') in ('100644', 'untracked'):
            files[public] = read_regular_file(root, f'{name}/{COVER}')
    for path, mode in sorted(candidates.items()):
        try:
            check_relative_path(path)
        except SourceError:
            if path.split('/')[0].lower() in DENIED_DIRECTORIES:
                continue
            raise
        if not is_exportable(path, mod_dirs):
            continue
        if mode == '120000':
            raise SourceError(f'{path}: symbolic links are not exported')
        if mode == '160000':
            raise SourceError(f'{path}: nested repositories (gitlinks) are not exported')
        if mode not in ('100644', '100755', 'untracked'):
            raise SourceError(f'{path}: unsupported git file mode {mode}')
        data = read_regular_file(root, path)
        scan_for_secrets(path, data)
        if PurePosixPath(path).suffix.lower() not in MEDIA_EXTENSIONS:
            data = data.replace(b'\r\n', b'\n')
        files[path] = data
    for mod, directory in zip(mods, mod_dirs):
        project = Path(mod['project']).resolve().relative_to(root).as_posix()
        if project not in files:
            raise SourceError(f"{mod.get('name', directory)}: project file {project} is not in the source list")
    return files


# --------------------------------------------------------------------------
# Hashing, manifest, writing
# --------------------------------------------------------------------------

def file_manifest(files: dict[str, bytes]) -> dict[str, dict]:
    manifest = {}
    for path in sorted(files):
        check_relative_path(path)
        data = files[path]
        if not isinstance(data, (bytes, bytearray)):
            raise SourceError(f'{path}: content must be bytes')
        manifest[path] = {'sha256': hashlib.sha256(data).hexdigest(), 'size': len(data)}
    return manifest


def source_hash(files: dict[str, bytes]) -> str:
    """SHA-256 of the canonical manifest: depends only on paths and bytes."""
    canonical = json.dumps(file_manifest(files), sort_keys=True, separators=(',', ':'), ensure_ascii=True)
    return hashlib.sha256(canonical.encode('ascii')).hexdigest()


def write_snapshot(files: dict[str, bytes], destination: Path) -> None:
    """Write ``files`` byte-for-byte into an absent or empty directory."""
    destination = Path(destination)
    if destination.is_symlink():
        raise SourceError('Snapshot destination must not be a symbolic link')
    if destination.exists():
        if not destination.is_dir() or any(destination.iterdir()):
            raise SourceError('Snapshot destination exists and is not an empty directory')
    manifest = file_manifest(files)  # validates every path before touching disk
    folded = {}
    for path in manifest:
        key = path.lower()
        if key in folded:
            raise SourceError(f'Paths differ only by case: {folded[key]} and {path}')
        folded[key] = path
    destination.mkdir(parents=True, exist_ok=True)
    base = destination.resolve()
    for path in manifest:
        target = base.joinpath(*path.split('/'))
        target.parent.mkdir(parents=True, exist_ok=True)
        if base not in target.resolve().parents:
            raise SourceError(f'{path}: would be written outside the destination')
        target.write_bytes(files[path])
        if target.suffix == '.sh':
            target.chmod(0o755)


# --------------------------------------------------------------------------
# Public README
# --------------------------------------------------------------------------

def _cell(value: object) -> str:
    return re.sub(r'\s+', ' ', str(value)).replace('|', '\\|').strip()


def cover_line(name: str) -> str:
    return f'![{name}]({COVER})'


def strip_cover(readme: bytes, name: str) -> bytes:
    """A mod README without its leading cover line, so adding a cover is not a source change."""
    prefix = cover_line(name).encode() + b'\n\n'
    return readme[len(prefix):] if readme.startswith(prefix) else readme


def _gallery(mods: list[dict], files: dict[str, bytes]) -> list[str]:
    lines = []
    for mod in sorted(mods, key=lambda item: item['name']):
        directory = Path(mod['project']).parent.name
        if f'{directory}/{COVER}' in files:
            lines += [f'[![{mod["name"]}]({directory}/{COVER})]({directory}/)', '']
    previews = [(name, path) for name, path in sorted(PREVIEW_COVERS.items()) if path in files]
    if previews:
        lines += ['**Experimental**, not yet part of these releases:', '']
        lines += [line for name, path in previews for line in (f'![{name}]({path})', '')]
    return lines


def public_readme(mods: list[dict], repo: str, files: dict[str, bytes] | None = None) -> bytes:
    """Consumer-facing build document: all supplied mods, no private details."""
    if re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo):
        clone = f'https://github.com/{repo}.git'
    elif re.fullmatch(r'https://[A-Za-z0-9_.-]+(?::\d+)?/[A-Za-z0-9_./-]+', repo):
        clone = repo if repo.endswith('.git') else repo + '.git'
    else:
        raise SourceError('repo must be owner/name or an https URL')
    folder = clone.rsplit('/', 1)[-1].removesuffix('.git')
    rows = []
    for mod in sorted(mods, key=lambda item: item['name']):
        description = _cell(mod['description']) if mod.get('description') else ''
        rows.append(f"| `{_cell(mod['name'])}` | {_cell(mod['version'])} | `{_cell(mod['guid'])}` | {description} |")
    first = Path(sorted(mods, key=lambda item: item['name'])[0]['project'])
    lines = [
        '# Nuclear Option client mods',
        '',
        'Source for regular (non-VR) BepInEx 5 client mods for Nuclear Option.',
        'This repository holds published source snapshots and releases only, not the',
        'private development history.',
        '',
        *_gallery(mods, files or {}),
        '## Mods',
        '',
        '| Mod | Version | Plugin ID | Description |',
        '| --- | --- | --- | --- |',
        *rows,
        '',
        'Each publication contains all of these mods at the versions shown.',
        '',
        '## Requirements',
        '',
        '- The .NET 10 SDK.',
        '- A Nuclear Option client with BepInEx 5 installed.',
        '- The game and BepInEx reference assemblies from your own installation. They are',
        '  proprietary, are not part of this repository, and must not be committed or',
        '  redistributed. Point the build at them with the two settings below.',
        '',
        '## Build',
        '',
        '```bash',
        f'git clone {clone}',
        f'cd {folder}',
        'cp .env.example .env   # then edit the paths for this machine',
        'set -a; source .env; set +a',
        '```',
        '',
        'Set `NO_MANAGED_DIR` to the game `NuclearOption_Data/Managed` folder and',
        '`NO_BEPINEX_CORE_DIR` to the `BepInEx/core` folder, as environment variables or',
        'as the MSBuild properties `NuclearOptionManagedDir` and `BepInExCoreDir`:',
        '',
        '```bash',
        f'dotnet build {first.parent.name}/{first.name} -c Release \\',
        '  -p:NuclearOptionManagedDir="/path/to/NuclearOption_Data/Managed" \\',
        '  -p:BepInExCoreDir="/path/to/BepInEx/core"',
        '```',
        '',
        'Build every project listed above the same way. Building does not install',
        'anything: copy a built plugin into `BepInEx/plugins/<ModName>/` yourself.',
        '',
        '## Checks',
        '',
        'The `tests/` folder holds dependency-free logic checks, a metadata check against',
        'your own client assemblies, and runtime acceptance notes in `tests/SMOKE-TESTS.md`.',
        'Passing offline checks does not prove in-game behavior; see the notes for what',
        'still needs a real client.',
        '',
        '## Licensing',
        '',
        'This README does not grant a license. Rights in this source are governed by a',
        '`LICENSE` file where one is supplied, and third-party material by the',
        '`THIRD-PARTY-NOTICES.txt` files beside the affected code.',
        '',
    ]
    return '\n'.join(lines).encode('utf-8')
