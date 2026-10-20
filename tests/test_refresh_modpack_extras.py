"""Offline tests for explicit registry refresh and download boundaries."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.request

SCRIPT = Path(__file__).resolve().parents[1] / 'tools/refresh-modpack-extras.py'
SPEC = importlib.util.spec_from_file_location('refresh_modpack_extras', SCRIPT)
refresh = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(refresh)


class RefreshTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.config = Path(self.directory.name) / 'extras.toml'
        self.config.write_text('[mods.example]\norigin="https://github.com/owner/repo"\nlayout="dll"\n')
        self.payload = b'reviewed plugin bytes'
        self.artifact = {'version': '1.2.0', 'category': 'release', 'type': 'plugin',
                         'fileName': 'Plugin.dll',
                         'downloadUrl': 'https://github.com/owner/repo/releases/download/v1.2.0/Plugin.dll',
                         'hash': 'sha256:' + hashlib.sha256(self.payload).hexdigest()}
        self.entry = {'id': 'example', 'githubOwner': 'owner', 'githubRepoName': 'repo',
                      'artifacts': [self.artifact]}
        self.manifest = [self.entry]
        self.fetch_patch = patch.object(refresh, 'fetch', side_effect=self.fetch)
        self.fetch_patch.start()
        self.addCleanup(self.fetch_patch.stop)

    def fetch(self, url):
        if url == refresh.VERSION_URL:
            return b'"1.0"'
        if url == refresh.MANIFEST_URL:
            return json.dumps(self.manifest).encode()
        return self.payload

    def test_success_records_verified_digest_and_latest_release(self):
        self.entry['artifacts'].append(dict(self.artifact, version='9.0', category='preRelease'))
        lock = refresh.refresh(self.config)
        self.assertEqual(lock['schema'], 1)
        self.assertEqual(lock['mods'][0]['artifact']['version'], '1.2.0')
        self.assertEqual(lock['mods'][0]['artifact']['sha256'], hashlib.sha256(self.payload).hexdigest())

    def test_alternate_url_normalized(self):
        self.artifact['downloadURL'] = self.artifact.pop('downloadUrl')
        artifact = refresh.refresh(self.config)['mods'][0]['artifact']
        self.assertIn('downloadUrl', artifact)
        self.assertNotIn('downloadURL', artifact)

    def test_conflicting_urls_rejected(self):
        self.artifact['downloadURL'] = 'https://github.com/other/repo/releases/download/v1/a.dll'
        with self.assertRaisesRegex(ValueError, 'conflicting'):
            refresh.refresh(self.config)

    def test_wrong_hash_rejected(self):
        self.artifact['hash'] = 'sha256:' + '0' * 64
        with self.assertRaisesRegex(ValueError, 'SHA-256 mismatch'):
            refresh.refresh(self.config)

    def test_missing_hash_rejected(self):
        self.artifact['hash'] = None
        with self.assertRaisesRegex(ValueError, 'SHA-256 missing'):
            refresh.refresh(self.config)

    def test_source_changed_rejected(self):
        self.entry['githubOwner'] = 'other'
        with self.assertRaisesRegex(ValueError, 'origin changed'):
            refresh.refresh(self.config)

    def test_artifact_other_repository_rejected(self):
        self.artifact['downloadUrl'] = 'https://github.com/owner/another/releases/download/v1/Plugin.dll'
        with self.assertRaisesRegex(ValueError, 'expected origin release path'):
            refresh.refresh(self.config)

    def test_dependencies_and_extends_must_be_configured(self):
        for key, value in [('dependencies', [{'id': 'missing', 'version': '1.0'}]),
                           ('extends', {'id': 'missing', 'version': '1.0'})]:
            with self.subTest(key=key):
                self.artifact[key] = value
                with self.assertRaisesRegex(ValueError, 'not configured'):
                    refresh.refresh(self.config)
                del self.artifact[key]

    def test_dependencies_and_extends_exact_version(self):
        dependency = dict(self.artifact, version='1.0')
        self.manifest.append(dict(self.entry, id='dependency', artifacts=[dependency]))
        with self.config.open('a') as stream:
            stream.write('[mods.dependency]\norigin="https://github.com/owner/repo"\nlayout="dll"\n')
        for key in ('dependencies', 'extends'):
            with self.subTest(key=key):
                requirement = {'id': 'dependency', 'version': '1.1'}
                self.artifact[key] = [requirement] if key == 'dependencies' else requirement
                with self.assertRaisesRegex(ValueError, 'version does not match'):
                    refresh.refresh(self.config)
                requirement['version'] = '1.0'
                self.assertEqual(len(refresh.refresh(self.config)['mods']), 2)
                dependency['version'] = '1.2'
                with self.assertRaisesRegex(ValueError, 'version does not match'):
                    refresh.refresh(self.config)
                dependency['version'] = '1.0'
                requirement.pop('version')
                self.assertEqual(len(refresh.refresh(self.config)['mods']), 2)
                del self.artifact[key]

    def test_failure_keeps_existing_lock(self):
        output = Path(self.directory.name) / 'lock.json'
        output.write_bytes(b'original lock')
        self.artifact['hash'] = None
        with patch('sys.argv', [str(SCRIPT), '--config', str(self.config), '--output', str(output)]):
            with patch('sys.stderr', io.StringIO()), self.assertRaises(SystemExit):
                refresh.main()
        self.assertEqual(output.read_bytes(), b'original lock')


class DownloadTests(unittest.TestCase):
    def test_https_redirect_rejected_before_following(self):
        handler = refresh.HTTPSRedirectHandler()
        request = urllib.request.Request('https://github.com/owner/repo')
        with self.assertRaisesRegex(ValueError, 'outside HTTPS'):
            handler.redirect_request(request, None, 302, 'Found', {}, 'http://example.invalid/data')

    def test_download_bounds_with_and_without_content_length(self):
        for url, limit in [(refresh.MANIFEST_URL, refresh.MANIFEST_LIMIT),
                           ('https://github.com/owner/repo/releases/download/v1/a.dll', refresh.DOWNLOAD_LIMIT)]:
            for declared in (False, True):
                with self.subTest(url=url, declared=declared):
                    response = unittest.mock.MagicMock()
                    response.__enter__.return_value = response
                    response.url = url
                    response.headers = {'Content-Length': str(limit + 1)} if declared else {}
                    response.read.return_value = b'x' * (limit + 1)
                    opener = unittest.mock.Mock()
                    opener.open.return_value = response
                    with patch.object(refresh.urllib.request, 'build_opener', return_value=opener):
                        with self.assertRaisesRegex(ValueError, 'exceeds'):
                            refresh.fetch(url)
                    if not declared:
                        response.read.assert_called_once_with(limit + 1)


if __name__ == '__main__':
    unittest.main()
