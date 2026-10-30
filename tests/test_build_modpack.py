import importlib.util
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import contextlib
import io
import sys

spec = importlib.util.spec_from_file_location('build_modpack', Path(__file__).resolve().parents[1] / 'tools/build-modpack.py')
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)
import process_utils


class ModpackTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'tools').mkdir()
        entries = []
        for name in ('Active', 'Tactical'):
            directory = self.root / name
            (directory / 'bin/Release').mkdir(parents=True)
            (directory / f'{name}.csproj').write_text(f'<Project><PropertyGroup><BepInExPluginGuid>dev.test.{name}</BepInExPluginGuid><Version>1.2.3</Version><AssemblyName>{name}</AssemblyName></PropertyGroup></Project>')
            (directory / f'bin/Release/{name}.dll').write_bytes(name.encode())
            (directory / 'bin/Release/Assembly-CSharp.dll').write_bytes(b'private')
            (directory / 'README.md').write_text('Install guidance')
            entries.append(f'[mods.{name}]\nid="dev.test.{name}"\nproject="{name}/{name}.csproj"\ninclude=' + ('["README.md", "assets/**"]' if name == 'Tactical' else '["README.md"]'))
        (self.root / 'Tactical/assets/audio').mkdir(parents=True)
        (self.root / 'Tactical/assets/audio/warning.mp3').write_bytes(b'audio')
        (self.root / 'Retired').mkdir()
        (self.root / 'Retired/native.dll').write_bytes(b'retired')
        (self.root / '.env').write_text('SECRET=neverpack\nNO_MANAGED_DIR="literal path"\n')
        (self.root / 'tools/nomnom-mirrors.toml').write_text('\n'.join(entries))
        self.output = self.root / 'dist/modpacks/test.nommpack'

    def test_layout_metadata_and_allowlist(self):
        builder.package(self.root, self.output, skip_build=True)
        with zipfile.ZipFile(self.output) as archive:
            expected = {'modlist.nomm.json', 'build-info.json'}
            for name in ('Active', 'Tactical'):
                prefix = f'mods/dev.test.{name}/'
                expected.update({prefix + f'{name}.dll', prefix + 'meta.json', prefix + 'README.md'})
                self.assertEqual(json.loads(archive.read(prefix + 'meta.json')), {'id': f'dev.test.{name}', 'artifact': {'version': '1.2.3'}})
            expected.add('mods/dev.test.Tactical/assets/audio/warning.mp3')
            self.assertEqual(set(archive.namelist()), expected)
            self.assertEqual(json.loads(archive.read('modlist.nomm.json')), [{'id': 'dev.test.Active', 'version': '1.2.3'}, {'id': 'dev.test.Tactical', 'version': '1.2.3'}])

    def test_provenance_hashes_and_unverified_existing_output(self):
        builder.package(self.root, self.output, skip_build=True)
        with zipfile.ZipFile(self.output) as archive:
            info = json.loads(archive.read('build-info.json'))
            self.assertEqual(info['schema'], 1)
            self.assertEqual(info['verification'], 'existing-unverified')
            self.assertNotIn('clientReference', info)
            self.assertEqual([(m['id'], m['version']) for m in info['mods']],
                             [('dev.test.Active', '1.2.3'), ('dev.test.Tactical', '1.2.3')])
            assets = []
            for mod in info['mods']:
                for item in [mod['dll'], *mod['assets']]:
                    self.assertEqual(item['sha256'], hashlib.sha256(archive.read(item['path'])).hexdigest())
                assets.extend(mod['assets'])
            self.assertEqual([a['path'] for a in assets], ['mods/dev.test.Tactical/assets/audio/warning.mp3'])
            self.assertNotIn(str(self.root), archive.read('build-info.json').decode())

    def test_verified_provenance_reference_is_optional_and_hashed(self):
        env = {'NO_MANAGED_DIR': str(self.root)}
        with patch.object(builder, 'verify_build') as verify, patch.object(builder, 'environment', return_value=env):
            builder.package(self.root, self.output)
            verify.assert_called_once()
            with zipfile.ZipFile(self.output) as archive:
                info = json.loads(archive.read('build-info.json'))
                self.assertEqual(info['verification'], 'build+logic+metadata')
                self.assertNotIn('clientReference', info)
            (self.root / 'Assembly-CSharp.dll').write_bytes(b'client-reference')
            builder.package(self.root, self.output)
            with zipfile.ZipFile(self.output) as archive:
                info = json.loads(archive.read('build-info.json'))
                self.assertEqual(info['clientReference'], {
                    'assembly': 'Assembly-CSharp.dll',
                    'sha256': hashlib.sha256(b'client-reference').hexdigest(),
                })

    def test_selection_and_retired_rejection(self):
        builder.package(self.root, self.output, ['Active'], True)
        with zipfile.ZipFile(self.output) as archive:
            self.assertFalse(any('Tactical' in item for item in archive.namelist()))
        with self.assertRaises(ValueError):
            builder.package(self.root, self.output, ['Retired'], True)

    def test_required_third_party_notice_is_preserved_and_missing_notice_blocks_pack(self):
        config = self.root / 'tools/nomnom-mirrors.toml'
        config.write_text(config.read_text().replace('["README.md"]', '["README.md", "THIRD-PARTY-NOTICES.txt"]'))
        notice = self.root / 'Active/THIRD-PARTY-NOTICES.txt'
        notice.write_bytes(b'MIT copyright and permission notice\n')
        builder.package(self.root, self.output, skip_build=True)
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(archive.read('mods/dev.test.Active/THIRD-PARTY-NOTICES.txt'), notice.read_bytes())
            self.assertFalse(any('Assembly-CSharp' in name for name in archive.namelist()))
        prior = self.output.read_bytes()
        notice.unlink()
        with self.assertRaises(FileNotFoundError):
            builder.package(self.root, self.output, skip_build=True)
        self.assertEqual(self.output.read_bytes(), prior)

    def test_missing_dll_or_assets_preserves_prior_archive(self):
        self.output.parent.mkdir(parents=True)
        self.output.write_bytes(b'prior')
        dll = self.root / 'Active/bin/Release/Active.dll'
        dll.unlink()
        with self.assertRaises(FileNotFoundError):
            builder.package(self.root, self.output, skip_build=True)
        self.assertEqual(self.output.read_bytes(), b'prior')
        dll.write_bytes(b'active')
        (self.root / 'Tactical/assets/audio/warning.mp3').unlink()
        with self.assertRaises(ValueError):
            builder.package(self.root, self.output, skip_build=True)
        self.assertEqual(self.output.read_bytes(), b'prior')

    def test_build_failure_preserves_archive(self):
        self.output.parent.mkdir(parents=True)
        self.output.write_bytes(b'prior')
        with patch.object(builder, 'verify_build', side_effect=subprocess.CalledProcessError(1, 'dotnet')):
            with self.assertRaises(subprocess.CalledProcessError):
                builder.package(self.root, self.output)
        self.assertEqual(self.output.read_bytes(), b'prior')

    def test_guid_and_version_errors(self):
        project = self.root / 'Active/Active.csproj'
        original = project.read_text()
        for changed in (original.replace('dev.test.Active', 'other.id'), original.replace('1.2.3', '1.2.3-beta')):
            project.write_text(changed)
            with self.assertRaises(ValueError):
                builder.load_mods(self.root)

    def test_build_commands_reference_properties_and_checks(self):
        with patch.object(builder.subprocess, 'run') as run, patch.object(builder, 'dotnet_executable', return_value='dotnet'):
            builder.verify_build(self.root, builder.load_mods(self.root), {'NO_MANAGED_DIR': str(self.root), 'NO_BEPINEX_CORE_DIR': str(self.root)})
        commands = [call.args[0] for call in run.call_args_list]
        # Two fixture mods plus build/execute for each of six check programs.
        self.assertEqual(len(commands), 2 + 2 * 6)
        self.assertIn(f'-p:NuclearOptionManagedDir={self.root}', commands[0])
        self.assertIn(f'-p:BepInExCoreDir={self.root}', commands[0])
        self.assertEqual(Path(commands[2][2]).name, 'ClientLogic.Tests.csproj')
        self.assertEqual(Path(commands[4][2]).name, 'LayoutChecks.csproj')
        self.assertEqual(Path(commands[6][2]).name, 'MigrationChecks.csproj')
        self.assertEqual(Path(commands[8][2]).name, 'MinimapMigrationChecks.csproj')
        self.assertEqual(Path(commands[10][2]).name, 'BindingChecks.csproj')
        self.assertEqual(Path(commands[12][2]).name, 'ClientApiCheck.csproj')
        self.assertEqual(commands[13][-2:], [str(self.root), str(self.root)])
        self.assertTrue(all('run' not in cmd for cmd in commands))
        for call in run.call_args_list:
            self.assertEqual(call.kwargs['stdout'], subprocess.PIPE)
            self.assertEqual(call.kwargs['stderr'], subprocess.STDOUT)
            if sys.platform == 'win32':
                self.assertTrue(call.kwargs['creationflags'] & subprocess.CREATE_NO_WINDOW)

    def test_background_flags_are_windows_only(self):
        with patch.object(process_utils.sys, 'platform', 'win32'):
            self.assertEqual(process_utils.background_kwargs(), {'creationflags': 0x08000000})
        with patch.object(process_utils.sys, 'platform', 'linux'):
            self.assertEqual(process_utils.background_kwargs(), {})

    def test_satellite_active_entry_and_data_exclusion(self):
        mods = builder.load_mods(builder.ROOT)
        self.assertEqual(len(mods), 16)
        satellite = next(mod for mod in mods if mod['name'] == 'NuclearSatelliteMap')
        self.assertEqual(satellite['guid'], 'dev.dikka.nuclearsatellitemap')
        self.assertEqual(satellite['include'], ['README.md', 'DATA-NOTICE.md'])
        self.assertEqual(set(satellite['data']), {'maps/heartland-46396.nosat', 'maps/ignus-archipelago-46395.nosat'})
        self.assertEqual(satellite['tags'], ['Map', 'Visual'])
        # Use synthetic bytes, never a real game-derived data file. Exercise the
        # real satellite includes against local data in several plausible paths.
        # Modpacks never carry the data; only individual release ZIPs do.
        directory = self.root / 'Active'
        (directory / 'DATA-NOTICE.md').write_text('Data notice')
        for relative in ('fixture.nosat', 'maps/fixture.nosat', 'assets/fixture.nosat'):
            path = directory / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b'synthetic local data')
        fixture = dict(satellite, project=directory / 'Active.csproj', assembly='Active')
        payload = builder.payload(self.root, [fixture])
        prefix = 'mods/dev.dikka.nuclearsatellitemap/'
        self.assertEqual(set(payload), {'modlist.nomm.json', prefix + 'meta.json',
                                       prefix + 'Active.dll', prefix + 'README.md', prefix + 'DATA-NOTICE.md'})

    def test_hidden_child_keeps_diagnostics_and_failure_status(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output), self.assertRaises(subprocess.CalledProcessError) as error:
            process_utils.run_logged([sys.executable, '-c',
                "import sys; print('build output', flush=True); print('build error', file=sys.stderr, flush=True); sys.exit(7)"])
        self.assertEqual(error.exception.returncode, 7)
        self.assertIn('build output', output.getvalue())
        self.assertIn('build error', output.getvalue())

    def test_environment_is_literal_and_process_wins(self):
        with patch.dict(builder.os.environ, {'NO_MANAGED_DIR': 'process path'}, clear=True):
            env = builder.environment(self.root)
        self.assertEqual(env['NO_MANAGED_DIR'], 'process path')
        self.assertEqual(env['SECRET'], 'neverpack')

    def test_environment_chains_and_single_quoted_literals(self):
        (self.root / '.env').write_text('STEAMAPPS_DIR="/steam"\nGAME_DIR="$STEAMAPPS_DIR/common/Nuclear Option"\nNO_MANAGED_DIR="${GAME_DIR}/Managed"\nLITERAL=\'$MISSING\'\nCOMMAND="$(never-run)"\n')
        with patch.dict(builder.os.environ, {'STEAMAPPS_DIR': '/override'}, clear=True), patch.object(builder.subprocess, 'run') as run:
            env = builder.environment(self.root)
        self.assertEqual(env['NO_MANAGED_DIR'], '/override/common/Nuclear Option/Managed')
        self.assertEqual(env['LITERAL'], '$MISSING')
        self.assertEqual(env['COMMAND'], '$(never-run)')
        run.assert_not_called()

    def test_unresolved_environment_variable_fails(self):
        (self.root / '.env').write_text('NO_MANAGED_DIR="$ABSENT/Managed"\n')
        with patch.dict(builder.os.environ, {}, clear=True):
            with self.assertRaisesRegex(ValueError, 'unresolved variable ABSENT'):
                builder.environment(self.root)

    def test_dotnet_resolution_and_missing_sdk(self):
        with patch.object(builder.shutil, 'which', return_value=None):
            with self.assertRaisesRegex(ValueError, '.NET 10 SDK'):
                builder.dotnet_executable({})
            install = self.root / 'sdk'
            install.mkdir()
            executable = install / ('dotnet.exe' if builder.os.name == 'nt' else 'dotnet')
            executable.write_bytes(b'fixture')
            self.assertEqual(builder.dotnet_executable({'DOTNET_ROOT': str(install)}), str(executable))
        with patch.object(builder.shutil, 'which', return_value='/path/dotnet'):
            self.assertEqual(builder.dotnet_executable({}), '/path/dotnet')

    def test_unsafe_asset_rejected(self):
        (self.root / 'Tactical/assets/BepInEx.dll').write_bytes(b'private')
        with self.assertRaises(ValueError):
            builder.package(self.root, self.output, skip_build=True)
        self.assertFalse(self.output.exists())

    def test_full_pack_keeps_own_payload_and_references_extras_without_vendoring(self):
        own = builder.prepared_payload(self.root, skip_build=True)
        origins = [{'id': 'extra.plugin', 'version': '4.5.6', 'origin': 'https://example.test/repo'}]
        with patch.object(builder, 'reference_extras', return_value=origins):
            full = builder.full_payload(self.root, own)
        self.assertEqual(full['mods/dev.test.Active/Active.dll'], own['mods/dev.test.Active/Active.dll'])
        self.assertFalse(any(name.startswith('mods/extra.plugin/') for name in full))
        self.assertEqual({name for name in full if name.startswith('mods/')},
                         {name for name in own if name.startswith('mods/')})
        self.assertEqual(json.loads(full['modlist.nomm.json'])[-1], {'id': 'extra.plugin', 'version': '4.5.6'})
        self.assertNotIn('origins.json', own)
        self.assertEqual(json.loads(full['origins.json'])['mods'], origins)
        self.assertIn('game-runtime-unverified', json.loads(full['build-info.json'])['thirdPartyVerification'])
        self.assertEqual(json.loads(full['build-info.json'])['thirdPartyMode'], 'registry-references')
        self.assertIn('already-installed external ID', full['PACK-README.txt'].decode())

    def test_full_pack_rejects_referenced_id_collision(self):
        own = builder.prepared_payload(self.root, skip_build=True)
        with patch.object(builder, 'reference_extras', return_value=[{'id': 'dev.test.Active', 'version': '1.2.3'}]):
            with self.assertRaisesRegex(ValueError, 'IDs collide'):
                builder.full_payload(self.root, own)

    def test_archive_staging_failure_preserves_both_outputs(self):
        second = self.output.with_name('full.nommpack')
        self.output.parent.mkdir(parents=True)
        self.output.write_bytes(b'prior-own')
        second.write_bytes(b'prior-full')
        original = builder.zipfile.ZipFile.writestr
        calls = 0
        def failing(archive, name, data, *args, **kwargs):
            nonlocal calls
            calls += 1
            if calls == 2:
                raise OSError('second archive failed')
            return original(archive, name, data, *args, **kwargs)
        with patch.object(builder.zipfile.ZipFile, 'writestr', failing):
            with self.assertRaises(OSError):
                builder.write_packs({self.output: {'one': b'new'}, second: {'two': b'new'}})
        self.assertEqual(self.output.read_bytes(), b'prior-own')
        self.assertEqual(second.read_bytes(), b'prior-full')
        self.assertFalse(list(self.output.parent.glob('*.tmp')))


if __name__ == '__main__':
    unittest.main()
