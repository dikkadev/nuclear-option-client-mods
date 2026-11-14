#!/usr/bin/env python3
# /// script
# requires-python = ">=3.11"
# dependencies = ["py7zr==1.1.3"]
# ///
"""Build NOMM -mine and -full packs; -full references pinned external mods."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import tomllib
import xml.etree.ElementTree as ET
import zipfile
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
from process_utils import run_logged


def environment(root: Path) -> dict[str, str]:
    """Read literal KEY=value assignments; never execute or expand shell expressions."""
    result = {}
    dotenv = root / '.env'
    if dotenv.exists():
        for number, line in enumerate(dotenv.read_text(encoding='utf-8-sig').splitlines(), 1):
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            match = re.fullmatch(r'([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)', line)
            if not match:
                raise ValueError(f'.env line {number}: expected KEY=value')
            key, value = match.groups()
            single_quoted = value.startswith("'")
            if value.startswith(('"', "'")):
                if len(value) < 2 or value[-1] != value[0]:
                    raise ValueError(f'.env line {number}: unmatched quote')
                value = value[1:-1]
            if not single_quoted:
                def expand(match):
                    reference = match.group(1) or match.group(2)
                    if reference in os.environ:
                        return os.environ[reference]
                    if reference in result:
                        return result[reference]
                    raise ValueError(f'.env line {number}: unresolved variable {reference}')
                value = re.sub(r'\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)', expand, value)
            result[key] = value
    result.update(os.environ)
    return result


def inside(root: Path, path: Path) -> Path:
    resolved = path.resolve()
    if not resolved.is_relative_to(root.resolve()):
        raise ValueError(f'Path escapes repository: {path}')
    return resolved


def load_mods(root: Path) -> list[dict]:
    config = tomllib.loads((root / 'tools/nomnom-mirrors.toml').read_text(encoding='utf-8'))
    mods = []
    ids = set()
    for name, entry in config['mods'].items():
        project = inside(root, root / entry['project'])
        xml = ET.parse(project).getroot()
        def prop(key):
            values = [item.text.strip() for item in xml.findall(f'.//{key}') if item.text]
            if len(values) != 1:
                raise ValueError(f'{name}: expected one literal {key}')
            return values[0]
        guid, version, assembly = prop('BepInExPluginGuid'), prop('Version'), prop('AssemblyName')
        if guid != entry['id'] or not re.fullmatch(r'[A-Za-z0-9_.-]+', guid) or guid in ids:
            raise ValueError(f'{name}: invalid, duplicate or mismatched plugin ID')
        if not re.fullmatch(r'\d+(?:\.\d+){1,3}', version):
            raise ValueError(f'{name}: NOMM requires a numeric version')
        if not re.fullmatch(r'[A-Za-z0-9_.-]+', assembly):
            raise ValueError(f'{name}: unsafe assembly name')
        for path, sha256 in entry.get('data', {}).items():
            if not re.fullmatch(r'maps/[A-Za-z0-9_.-]+\.nosat', path) or not re.fullmatch(r'[0-9a-f]{64}', sha256):
                raise ValueError(f'{name}: invalid data entry {path}')
        ids.add(guid)
        mods.append(dict(entry, name=name, project=project, guid=guid, version=version, assembly=assembly))
    if not mods:
        raise ValueError('No active mods configured')
    return mods


def dotnet_executable(env: dict[str, str]) -> str:
    executable = shutil.which('dotnet', path=env.get('PATH', ''))
    if executable:
        return executable
    if env.get('DOTNET_ROOT'):
        candidate = Path(env['DOTNET_ROOT']) / ('dotnet.exe' if os.name == 'nt' else 'dotnet')
        if candidate.is_file():
            return str(candidate)
    raise ValueError('A .NET 10 SDK is required: put dotnet on PATH or set DOTNET_ROOT to its installation directory')


def verify_build(root: Path, mods: list[dict], env: dict[str, str], *, source_revisions: dict[str, str] | None = None) -> None:
    managed = env.get('NO_MANAGED_DIR', '')
    core = env.get('NO_BEPINEX_CORE_DIR', '')
    if not Path(managed).is_dir() or not Path(core).is_dir() or not managed or not core:
        raise ValueError('Set NO_MANAGED_DIR and NO_BEPINEX_CORE_DIR to real client reference directories')
    dotnet = dotnet_executable(env)
    props = [f'-p:NuclearOptionManagedDir={managed}', f'-p:BepInExCoreDir={core}']
    def run(args):
        print('Running: ' + ' '.join(args), flush=True)
        run_logged(args, cwd=root, env=env)
    for mod in mods:
        revision = [f'-p:SourceRevisionId={source_revisions[mod["name"]]}'] if source_revisions else []
        run([dotnet, 'build', str(mod['project']), '-c', 'Release', *props, *revision])
    for directory, assembly, arguments in [
        ('tests/ClientLogic.Tests', 'ClientLogic.Tests', []),
        ('Shared/NuclearMfd/tests', 'LayoutChecks', []),
        ('Shared/NuclearModMigration/tests', 'MigrationChecks', []),
        ('NuclearMinimap/tests', 'MinimapMigrationChecks', []),
        ('Shared/NuclearBindings/tests', 'BindingChecks', []),
        ('tests/ClientApiCheck', 'ClientApiCheck', [str(root), managed, str(root / 'tests/api-contract.json'), ','.join(m['name'] for m in mods)]),
    ]:
        run([dotnet, 'build', str(root / directory / (assembly + '.csproj')), '-c', 'Release', *props])
        # Launch the DLL directly: dotnet run otherwise launches another console apphost.
        run([dotnet, str(root / directory / 'bin/Release/net10.0' / (assembly + '.dll')), *arguments])


def payload(root: Path, mods: list[dict]) -> dict[str, bytes]:
    result = {'modlist.nomm.json': json.dumps([{'id': m['guid'], 'version': m['version']} for m in mods], indent=2).encode()}
    for mod in mods:
        directory = mod['project'].parent
        prefix = f"mods/{mod['guid']}/"
        dll = inside(root, directory / 'bin/Release' / (mod['assembly'] + '.dll'))
        result[prefix + dll.name] = dll.read_bytes()
        result[prefix + 'meta.json'] = json.dumps({'id': mod['guid'], 'artifact': {'version': mod['version']}}, indent=2).encode()
        for include in mod.get('include', []):
            # Deliberately narrow allowlist: no generic globs or build output copying.
            if include in {'README.md', 'THIRD-PARTY-NOTICES.txt', 'DATA-NOTICE.md'}:
                source = inside(root, directory / include)
                result[prefix + include] = source.read_bytes()
            elif include == 'assets/**':
                assets = inside(root, directory / 'assets')
                files = sorted(p for p in assets.rglob('*') if p.is_file())
                if not files:
                    raise ValueError(f"{mod['name']}: declared assets are missing")
                for source in files:
                    source = inside(assets, source)
                    relative = source.relative_to(assets)
                    if source.suffix.lower() not in {'.mp3', '.wav', '.ogg', '.png', '.jpg', '.jpeg'} or any(p.startswith('.') for p in relative.parts):
                        raise ValueError(f"{mod['name']}: asset is outside media allowlist: {relative}")
                    result[prefix + 'assets/' + relative.as_posix()] = source.read_bytes()
            else:
                raise ValueError(f'Unsupported include: {include}')
    return result


def prepared_payload(root: Path, selected: list[str] | None = None, skip_build: bool = False) -> dict[str, bytes]:
    mods = load_mods(root)
    known = {m['name'] for m in mods}
    if selected and (set(selected) - known):
        raise ValueError('Unknown or retired mod selection: ' + ', '.join(sorted(set(selected) - known)))
    build_env = None
    if skip_build:
        print('Skipping build and compatibility checks; packaging existing output only.', flush=True)
    else:
        # The metadata preflight covers all active mods, even when packaging a subset.
        build_env = environment(root)
        verify_build(root, mods, build_env)
    packaged_mods = [m for m in mods if not selected or m['name'] in selected]
    contents = payload(root, packaged_mods)
    provenance = {
        'schema': 1,
        'verification': 'existing-unverified' if skip_build else 'build+logic+metadata',
        'mods': [],
    }
    for mod in packaged_mods:
        prefix = f"mods/{mod['guid']}/"
        dll_path = prefix + mod['assembly'] + '.dll'
        provenance['mods'].append({
            'id': mod['guid'],
            'version': mod['version'],
            'dll': {'path': dll_path, 'sha256': hashlib.sha256(contents[dll_path]).hexdigest()},
            'assets': [
                {'path': name, 'sha256': hashlib.sha256(data).hexdigest()}
                for name, data in sorted(contents.items()) if name.startswith(prefix + 'assets/')
            ],
        })
    if build_env is not None:
        reference = Path(build_env.get('NO_MANAGED_DIR', '')) / 'Assembly-CSharp.dll'
        if reference.is_file():
            provenance['clientReference'] = {
                'assembly': 'Assembly-CSharp.dll',
                'sha256': hashlib.sha256(reference.read_bytes()).hexdigest(),
            }
    contents['build-info.json'] = json.dumps(provenance, indent=2).encode()
    return contents


def reference_extras(root: Path) -> list[dict]:
    # Read the reviewed pins without downloading or unpacking third-party files.
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from modpack_registry import load_locked_mods
    return [{'id': mod['id'], 'version': mod['artifact']['version'],
             'origin': mod['origin'], 'artifact': mod['artifact'], 'mode': 'reference'}
            for mod in load_locked_mods(root)]


def full_payload(root: Path, own: dict[str, bytes]) -> dict[str, bytes]:
    origins = reference_extras(root)
    contents = dict(own)
    modlist = json.loads(own['modlist.nomm.json'])
    if {m['id'] for m in modlist} & {m['id'] for m in origins}:
        raise ValueError('Third-party IDs collide with our mods')
    modlist.extend({'id': m['id'], 'version': m['version']} for m in origins)
    contents['modlist.nomm.json'] = json.dumps(modlist, indent=2).encode()
    contents['origins.json'] = json.dumps({'schema': 1, 'mods': origins}, indent=2).encode()
    contents['PACK-README.txt'] = (
        'Nuclear Option Modpack -full\n\n'
        'Import this whole .nommpack in NOMM 5.4.0. Import enables included mods and disables\n'
        'other non-protected mods. Existing game/BepInEx/user configuration is preserved.\n'
        'Your dev.dikka mods are bundled. External plugins are registry references only:\n'
        'NOMM downloads missing external IDs from its online registry. No external DLLs,\n'
        'assets or configurations are vendored in this archive. Pins and sources are in origins.json.\n'
        'NOMM retains an already-installed external ID even if its version differs from the pin.\n'
        'Use NOMM to update/remove that external mod when the pinned version is required.\n'
        'Configuration Manager: F1 (default).\n'
        'For sound testing: F1 > NuclearSounds > Audio Test > Play Audio Test,\n'
        'tick once and close F1. A tone plays after two seconds, followed by a lower-gear voice.\n'
        'Autopilot downloads its Audio and Profiles from its upstream registry artifact.\n'
        'If two fuel estimates appear, use F1 > NO Autopilot > Visuals > Show Fuel Overlay\n'
        'to turn off its fuel overlay while retaining NuclearSounds fuel text.\n'
        'For Loadout Presets isolation, disable it in NOMM rather than its internal Enabled\n'
        'setting: that setting can still suppress vanilla loadout defaults.\n'
        'Build/API checks cover our active mods. External files are downloaded by NOMM;\n'
        'full-pack gameplay still requires testing in the client.\n'
    ).encode()
    info = json.loads(own['build-info.json'])
    info['thirdPartyMode'] = 'registry-references'
    info['thirdPartyVerification'] = 'locked-reference-metadata; game-runtime-unverified'
    info['thirdParty'] = origins
    contents['build-info.json'] = json.dumps(info, indent=2).encode()
    return contents


def write_packs(packs: dict[Path, dict[str, bytes]]) -> None:
    staged = []
    try:
        # Finish every archive before replacing any previous output.
        for output, contents in packs.items():
            if output.suffix != '.nommpack':
                raise ValueError('Output must have the .nommpack extension')
            output.parent.mkdir(parents=True, exist_ok=True)
            with tempfile.NamedTemporaryFile(dir=output.parent, suffix='.tmp', delete=False) as stream:
                temporary = Path(stream.name)
            staged.append((temporary, output))
            with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
                for name, data in sorted(contents.items()):
                    archive.writestr(name, data)
        for temporary, output in staged:
            os.replace(temporary, output)
            print(f'Modpack: {output}', flush=True)
    finally:
        for temporary, _ in staged:
            temporary.unlink(missing_ok=True)


def package(root: Path, output: Path, selected: list[str] | None = None, skip_build: bool = False) -> Path:
    output = output.resolve()
    write_packs({output: prepared_payload(root, selected, skip_build)})
    return output


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--mod', action='append', help='Package selected active mod (repeatable); default all active mods')
    parser.add_argument('--pack', choices=['mine', 'full', 'both', 'our'], help='Default both; mine bundles your mods, full adds registry references; our is a mine alias')
    parser.add_argument('--output', type=Path, help='Custom filename for one pack (incompatible with --pack both)')
    parser.add_argument('--skip-build', action='store_true', help='Package existing DLLs without build or compatibility verification')
    args = parser.parse_args()
    try:
        choice = args.pack or ('our' if args.mod or args.output else 'both')
        if choice == 'mine':
            choice = 'our'
        if args.mod and choice != 'our':
            raise ValueError('--mod requires --pack our')
        if args.output and choice == 'both':
            raise ValueError('--output requires --pack our or --pack full')
        own_output = ROOT / 'dist/modpacks/nuclear-option-modpack-mine.nommpack'
        full_output = ROOT / 'dist/modpacks/nuclear-option-modpack-full.nommpack'
        own = prepared_payload(ROOT, args.mod, args.skip_build)
        packs = {}
        if choice in ('our', 'both'):
            packs[(args.output or own_output).resolve()] = own
        if choice in ('full', 'both'):
            packs[(args.output or full_output).resolve()] = full_payload(ROOT, own)
        write_packs(packs)
    except (ValueError, OSError, ET.ParseError, subprocess.CalledProcessError) as error:
        print(f'ERROR: {error}', file=__import__('sys').stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
