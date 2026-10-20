import hashlib
import importlib.util
import io
import json
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location('modpack_registry', Path(__file__).resolve().parents[1] / 'tools/modpack_registry.py')
registry = importlib.util.module_from_spec(spec)
spec.loader.exec_module(registry)


def zipped(files):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w') as archive:
        for name, value in files.items():
            archive.writestr(name, value)
    return stream.getvalue()


class RegistryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / 'tools').mkdir()
        self.files = {'Plugin/Plugin.dll': b'plugin', 'Plugin/assets/tone.wav': b'audio', 'LICENSE.txt': b'notice'}
        self.data = zipped(self.files)
        self.mod = {'id': 'dev.test.plugin', 'origin': 'https://github.com/example/Plugin', 'layout': 'zip',
                    'stripPrefix': 'Plugin/', 'artifact': {'version': '1.2.3', 'fileName': 'Plugin.zip',
                    'downloadUrl': 'https://github.com/example/Plugin/releases/download/v1.2.3/Plugin.zip',
                    'sha256': hashlib.sha256(self.data).hexdigest(), 'gameVersion': '0.33'}}
        self.mods = [self.mod]
        self.save()

    def save(self):
        entries = []
        for mod in self.mods:
            entries.append(f'[mods."{mod["id"]}"]\norigin="{mod["origin"]}"\nlayout="{mod["layout"]}"\nstrip_prefix="{mod.get("stripPrefix", "")}"')
        (self.root / 'tools/modpack-extras.toml').write_text('\n'.join(entries), encoding='utf-8')
        self.save_lock()

    def save_lock(self):
        (self.root / 'tools/modpack-extras.lock.json').write_text(json.dumps({'schema': 1,
            'registry': {'manifestUrl': 'https://registry.test/manifest.json', 'versionUrl': 'https://registry.test/version.json', 'version': '1.0.0'},
            'mods': self.mods}), encoding='utf-8')

    def test_prepare_maps_payload_preserves_media_notices_and_exact_pin(self):
        with patch.object(registry, 'download', return_value=self.data) as fetch:
            contents, records = registry.prepare_extras(self.root)
        self.assertEqual(fetch.call_args.args, (self.mod['artifact']['downloadUrl'],))
        prefix = 'mods/dev.test.plugin/'
        self.assertEqual(set(contents), {prefix + n for n in ['Plugin.dll', 'assets/tone.wav', 'LICENSE.txt', 'meta.json']})
        meta = json.loads(contents[prefix + 'meta.json'])
        self.assertEqual(meta['artifact']['version'], '1.2.3')
        self.assertNotIn('sha256', meta['artifact'])
        self.assertEqual(records[0]['version'], '1.2.3')
        for item in records[0]['files']:
            self.assertEqual(item['sha256'], hashlib.sha256(contents[item['path']]).hexdigest())

    def test_cache_is_reverified_and_corrupt_cache_fails_closed(self):
        with patch.object(registry, 'download', return_value=self.data):
            registry.prepare_extras(self.root)
        with patch.object(registry, 'download', side_effect=AssertionError('unexpected network')):
            registry.prepare_extras(self.root)
            cache = next((self.root / 'dist/modpack-cache').rglob('Plugin.zip'))
            cache.write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError, 'SHA-256 mismatch'):
                registry.prepare_extras(self.root)

    def test_wrong_download_hash_is_not_cached(self):
        with patch.object(registry, 'download', return_value=b'wrong'):
            with self.assertRaisesRegex(ValueError, 'SHA-256 mismatch'):
                registry.prepare_extras(self.root)
        self.assertFalse((self.root / 'dist/modpack-cache').exists())

    def test_lock_origin_and_layout_must_match_configuration(self):
        for key, value in [('origin', 'https://github.com/other/Plugin'), ('layout', '7z'), ('stripPrefix', 'Other/')]:
            previous = self.mod[key]
            self.mod[key] = value
            self.save_lock()
            with self.assertRaisesRegex(ValueError, 'differs from lock'):
                registry.load_locked_mods(self.root)
            self.mod[key] = previous

    def test_artifact_must_come_from_exact_origin(self):
        self.mod['artifact']['downloadUrl'] = 'https://github.com/example/Plugin-evil/releases/Plugin.zip'
        self.save_lock()
        with self.assertRaisesRegex(ValueError, 'declared origin'):
            registry.load_locked_mods(self.root)

    def test_dependency_and_extends_require_locked_matching_versions(self):
        for kind in ['dependencies', 'extends']:
            reference = {'id': 'missing', 'version': '2.0.0'}
            self.mod['artifact'][kind] = [reference] if kind == 'dependencies' else reference
            self.save_lock()
            with self.assertRaisesRegex(ValueError, 'Unresolved locked dependency'):
                registry.load_locked_mods(self.root)
            reference['id'] = self.mod['id']
            self.save_lock()
            with self.assertRaisesRegex(ValueError, 'Unresolved locked dependency'):
                registry.load_locked_mods(self.root)
            reference['version'] = '1.2.3'
            self.save_lock()
            registry.load_locked_mods(self.root)
            del self.mod['artifact'][kind]

    def test_incompatibility_rejected_and_protected_id_rejected(self):
        self.mod['artifact']['incompatibilities'] = [{'id': self.mod['id']}]
        self.save_lock()
        with self.assertRaisesRegex(ValueError, 'Incompatible'):
            registry.load_locked_mods(self.root)
        self.mod['id'] = 'NOSMR'
        self.save()
        with self.assertRaisesRegex(ValueError, 'protected'):
            registry.load_locked_mods(self.root)

    def test_unsafe_paths_rejected_before_member_read(self):
        for name in ['../escaped.dll', '/absolute.dll', 'C:/drive.dll', 'folder\\evil.dll', 'a/../b.dll',
                     'x.dll:ads', 'NUL.txt', 'foo./bar.dll', 'a//b.dll']:
            with self.subTest(name=name):
                with self.assertRaisesRegex(ValueError, 'Unsafe archive path'):
                    # ZipInfo normalizes Windows separators while writing;
                    # replace the raw local/central names to model hostile input.
                    data = zipped({name: b'data'})
                    if '\\' in name:
                        data = data.replace(name.replace('\\', '/').encode(), name.encode())
                    registry.archive_files(data, 'zip')

    def test_symlink_and_duplicate_or_case_colliding_members_rejected(self):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w') as archive:
            member = zipfile.ZipInfo('link.dll')
            member.create_system = 3
            member.external_attr = (stat.S_IFLNK | 0o777) << 16
            archive.writestr(member, 'target')
        with self.assertRaisesRegex(ValueError, 'Unsupported member'):
            registry.archive_files(stream.getvalue(), 'zip')
        with self.assertRaisesRegex(ValueError, 'case-colliding'):
            registry.archive_files(zipped({'A.dll': b'a', 'a.dll': b'b'}), 'zip')
        with self.assertRaisesRegex(ValueError, 'path collision'):
            registry.archive_files(zipped({'a.dll': b'a', 'a.dll/asset.wav': b'b'}), 'zip')

    def test_archive_size_and_count_limits(self):
        with patch.object(registry, 'MAX_MEMBER', 2):
            with self.assertRaisesRegex(ValueError, 'member size'):
                registry.archive_files(self.data, 'zip')
        with patch.object(registry, 'MAX_MEMBERS', 1):
            with self.assertRaisesRegex(ValueError, 'member-count'):
                registry.archive_files(self.data, 'zip')
        with patch.object(registry, 'MAX_EXPANDED', 2):
            with self.assertRaisesRegex(ValueError, 'expanded size'):
                registry.archive_files(self.data, 'zip')

    def test_mapping_rejects_unknown_layout_core_game_and_executables(self):
        for name in ['Assembly-CSharp.dll', 'BepInEx.dll', '0Harmony.dll', 'UnityEngine.CoreModule.dll',
                     'config/test.cfg', 'patchers/Plugin.dll', 'run.ps1', 'setup.exe', 'meta.json']:
            with self.subTest(name=name):
                with self.assertRaises(ValueError):
                    registry.plugin_files({'Plugin.dll': b'p', name: b'bad'}, '')
        with self.assertRaisesRegex(ValueError, 'outside declared'):
            registry.plugin_files({'wrong/Plugin.dll': b'p'}, 'Plugin/')
        with self.assertRaisesRegex(ValueError, 'no DLL'):
            registry.plugin_files({'README.md': b'readme'}, '')

    def test_dll_layout_and_legacy_download_url(self):
        self.mod['layout'] = 'dll'
        self.mod['stripPrefix'] = ''
        artifact = self.mod['artifact']
        artifact['fileName'] = 'Plugin.dll'
        artifact['sha256'] = hashlib.sha256(b'dll').hexdigest()
        artifact['downloadURL'] = artifact.pop('downloadUrl')
        self.save()
        with patch.object(registry, 'download', return_value=b'dll'):
            contents, _ = registry.prepare_extras(self.root)
        self.assertEqual(contents['mods/dev.test.plugin/Plugin.dll'], b'dll')
        artifact['downloadUrl'] = 'https://elsewhere.test/Plugin.dll'
        with self.assertRaisesRegex(ValueError, 'Conflicting'):
            registry.artifact_bytes(self.root, artifact)

    def test_insecure_urls_credentials_and_redirects_rejected(self):
        for url in ['http://example.test/file', 'https://user:secret@example.test/file', 'https://example.test/file#fragment']:
            with self.assertRaises(ValueError):
                registry.https_url(url)
        with self.assertRaises(ValueError):
            registry._HttpsRedirect().redirect_request(None, None, 302, '', {}, 'http://example.test/file')

    def test_7z_stdout_backend_preserves_members(self):
        import py7zr
        stream = io.BytesIO()
        with py7zr.SevenZipFile(stream, 'w') as archive:
            archive.writestr(b'plugin', 'Plugin/Plugin.dll')
            archive.writestr(b'asset', 'Plugin/assets/tone.wav')
        actual = registry.archive_files(stream.getvalue(), '7z')
        self.assertEqual(actual, {'Plugin/Plugin.dll': b'plugin', 'Plugin/assets/tone.wav': b'asset'})

    def test_7z_backend_rejects_unexpected_member_size(self):
        executable = registry.bsdtar_executable()
        stream = io.BytesIO()
        import py7zr
        with py7zr.SevenZipFile(stream, 'w') as archive:
            archive.writestr(b'abc', 'Plugin.dll')
        path = self.root / 'archive.7z'
        path.write_bytes(stream.getvalue())
        with self.assertRaisesRegex(ValueError, 'extraction mismatch'):
            registry.bsdtar_member(executable, path, 'Plugin.dll', 1)


if __name__ == '__main__':
    unittest.main()
