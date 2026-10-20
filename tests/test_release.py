"""Offline regression checks for source/release parity and publication recovery."""
import contextlib
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import release_engine as engine
import release_source as source
import release as cli


class Versions(unittest.TestCase):
    def test_exact_case_sensitive_prefix_and_numeric_suffix(self):
        self.assertEqual(engine.tag_version('NuclearUnits-v1.2.3', 'NuclearUnits-v'), '1.2.3')
        for bad in ('nuclearunits-v1.2.3', 'NuclearUnits-v1.2.3-rc1', 'NuclearUnits-vlatest', 'NuclearSounds-v9.0'):
            self.assertIsNone(engine.tag_version(bad, 'NuclearUnits-v'))
        for bad in ('1', '1.02.3', '1.2.-1', '2147483648.0', '1.2.3.4.5'):
            with self.assertRaises(ValueError):
                engine.version_key(bad)
        self.assertLess(engine.version_key('1.2'), engine.version_key('1.2.0'))

    def test_bumps_reset_lower_components_and_keep_independent_numbers(self):
        self.assertEqual(engine.next_version('1.2.9', 'patch'), '1.2.10')
        self.assertEqual(engine.next_version('1.2.9', 'minor'), '1.3.0')
        self.assertEqual(engine.next_version('1.2.9.4', 'major'), '2.0.0.0')

    def test_destination_is_users_account(self):
        self.assertEqual(engine.repository('dikkadev/test'), 'dikkadev/test')
        for bad in ('clankagent/test', 'https://github.com/dikkadev/test', 'dikkadev/../test'):
            with self.assertRaises(ValueError):
                engine.repository(bad)

    def test_discovery_uses_registry_and_legacy_but_not_titles_or_other_mod_tags(self):
        mod = {'name': 'Alpha', 'guid': 'dev.test.alpha'}
        def fetch(url, **kwargs):
            if url == engine.REGISTRY:
                return [{'id': 'dev.test.alpha', 'githubOwner': 'dikkadev', 'githubRepoName': 'old',
                         'artifacts': [{'version': '1.3.0'}]}]
            if url.endswith('/commits/main'):
                return {'sha': 'a' * 40}
            return {'private': False, 'size': 1, 'default_branch': 'main'}
        def releases(repo, **kwargs):
            if repo.endswith('/old'):
                return [{'tag_name': 'v1.4.0', 'name': '999.0.0'}]
            return [{'tag_name': 'Beta-v99.0.0'}, {'tag_name': 'Alpha-v1.2.0', 'name': '500.0.0'},
                    {'tag_name': 'Alpha-v2.0.0-rc1'}, {'tag_name': 'Alpha-v10.0.0', 'draft': True}]
        with patch.object(engine, 'fetch_json', side_effect=fetch), patch.object(engine, 'release_list', side_effect=releases):
            found = engine.discovery([mod], 'dikkadev/test')
        self.assertEqual(found['versions'], {'Alpha': '1.4.0'})

    def test_base_commit_is_checked_even_when_repository_size_is_stale(self):
        def fetch(url, **kwargs):
            if url == engine.REGISTRY:
                return []
            if '/commits/' in url:
                return {'sha': 'a' * 40}
            return {'private': False, 'default_branch': 'main', 'size': 0}
        with patch.object(engine, 'fetch_json', side_effect=fetch), patch.object(engine, 'release_list', return_value=[]):
            self.assertEqual(engine.discovery([], 'dikkadev/test')['baseCommit'], 'a' * 40)

    def test_private_repository_is_discovered_normally(self):
        def fetch(url, **kwargs):
            if url == engine.REGISTRY:
                return []
            if '/commits/' in url:
                return {'sha': 'a' * 40}
            return {'private': True, 'default_branch': 'main'}
        with patch.object(engine, 'fetch_json', side_effect=fetch), patch.object(engine, 'release_list', return_value=[]):
            result = engine.discovery([], 'dikkadev/test')
        self.assertTrue(result['exists'])
        self.assertEqual(result['baseCommit'], 'a' * 40)

    def test_private_asset_download_uses_authenticated_github_api(self):
        url = 'https://github.com/dikkadev/test/releases/download/Alpha-v1.2.3/Alpha.zip'
        metadata = {'assets': [{'id': 123, 'name': 'Alpha.zip', 'size': 9}]}
        with patch.object(engine, 'has_gh_auth', return_value=True), patch.object(engine, 'gh_json', return_value=metadata), patch.object(engine, 'gh', return_value=b'zip bytes') as gh:
            self.assertEqual(engine.download_reference(url), b'zip bytes')
        gh.assert_called_once_with('api', 'repos/dikkadev/test/releases/assets/123', '-H', 'Accept: application/octet-stream')


class Fixture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='release-tests-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'private'
        self.root.mkdir()
        self.git = source.find_git(ROOT)
        self.command('init', '--initial-branch', 'trunk')
        self.command('config', 'user.name', 'Private Developer')
        self.command('config', 'user.email', 'private@example.test')
        self.write('.gitignore', '.env\ndist/\n**/bin/\n**/obj/\n')
        self.write('.env.example', 'NO_MANAGED_DIR="/your/game/Managed"\nNO_BEPINEX_CORE_DIR="/your/BepInEx/core"\n')
        self.write('tools/release.toml', '[publication]\nrepository="dikkadev/test"\nbranch="main"\ncommit_name="dikkadev"\ncommit_email="dikkadev@users.noreply.github.com"\n[packs]\nour_version="1.0.0"\nfull_version="2.0.0"\n')
        entries = []
        for name, version in [('Alpha', '1.2.3'), ('Beta', '0.2.0')]:
            self.write(f'{name}/{name}.csproj', f'<Project><PropertyGroup><Version>{version}</Version><AssemblyName>{name}</AssemblyName><BepInExPluginGuid>dev.test.{name.lower()}</BepInExPluginGuid></PropertyGroup></Project>\n')
            self.write(f'{name}/Plugin.cs', '// own source\r\n')
            self.write(f'{name}/README.md', 'Install instructions\n')
            self.write(f'{name}/scripts/install-mod.sh', '#!/usr/bin/env bash\r\nexit 0\r\n')
            entries.append(f'[mods.{name}]\nid="dev.test.{name.lower()}"\nproject="{name}/{name}.csproj"\ndescription="{name}"\ninclude=["README.md"]\n')
        self.write('LICENSE', 'Fixture license, not a grant for the real project.\n')
        self.write('.dev/private.md', 'DO NOT EXPORT\n')
        self.write('tools/nomnom-mirrors.toml', 'forgejo_source_url="https://private.invalid/repo"\n' + '\n'.join(entries))
        for name in ('build-modpack.py', 'process_utils.py'):
            self.write('tools/' + name, (ROOT / 'tools' / name).read_bytes())
        self.command('add', '--all')
        self.command('commit', '-m', 'Private development that must never go public')
        self.private_head = self.command('rev-parse', 'HEAD').decode().strip()
        self.out = Path(self.temp.name) / 'update'

    def write(self, path, data):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data.encode() if isinstance(data, str) else data)

    def command(self, *args, cwd=None):
        return subprocess.check_output([self.git, *args], cwd=cwd or self.root,
                                       stderr=subprocess.PIPE, **engine.background_kwargs())

    def make_plan(self, names=None, packs=None, **kwargs):
        return engine.plan(self.root, names or ['Alpha'], packs or [], game_version='0.34.2',
                           output=self.out, offline=kwargs.pop('offline', True), **kwargs)

    def prepare_fake(self):
        original = engine.load_builder(self.root)
        def build(root, mods, env, *, source_revisions=None):
            for mod in mods:
                target = mod['project'].parent / 'bin/Release' / (mod['assembly'] + '.dll')
                target.parent.mkdir(parents=True)
                target.write_bytes((mod['guid'] + ':' + mod['version']).encode())
        ref = self.root / 'references/Assembly-CSharp.dll'
        ref.parent.mkdir(exist_ok=True)
        ref.write_bytes(b'reference fingerprint')
        fake = types.SimpleNamespace(load_mods=original.load_mods, payload=original.payload,
            verify_build=build, environment=lambda root: {'NO_MANAGED_DIR': str(ref.parent)})
        with patch.object(engine, 'load_builder', return_value=fake):
            return engine.prepare(self.out / 'plan.json', root=self.root)


class LocalPreparation(Fixture):
    def test_plan_freezes_whole_public_source_and_preserves_worktree(self):
        before = self.command('status', '--porcelain')
        value = self.make_plan()
        files = engine.read_source(self.out / 'plan.json', value)
        self.assertIn('Beta/Plugin.cs', files)
        self.assertNotIn('.dev/private.md', files)
        self.assertNotIn('https://private.invalid/repo', files['tools/nomnom-mirrors.toml'].decode())
        self.assertNotIn(self.private_head.encode(), b''.join(files.values()))
        self.assertEqual(self.command('status', '--porcelain'), before)
        self.assertEqual([i['name'] for i in value['items']], ['Alpha'])
        self.assertEqual(files['Alpha/Plugin.cs'], b'// own source\n')

    def test_plan_does_not_silently_bump_or_overwrite(self):
        self.make_plan()
        with self.assertRaisesRegex(ValueError, 'not empty'):
            self.make_plan()
        self.assertIn(b'<Version>1.2.3</Version>', (self.root / 'Alpha/Alpha.csproj').read_bytes())

    def test_new_files_need_explicit_opt_in(self):
        self.write('Beta/New.cs', '// new\n')
        value = self.make_plan()
        self.assertNotIn('Beta/New.cs', value['sourceFiles'])
        self.out = Path(self.temp.name) / 'with-new'
        value = self.make_plan(include_untracked=True)
        self.assertIn('Beta/New.cs', value['sourceFiles'])

    def test_explicit_bump_preserves_other_versions_and_line_endings(self):
        old_beta = (self.root / 'Beta/Beta.csproj').read_bytes()
        result = engine.bump(self.root, ['Alpha'], 'minor')
        self.assertEqual(result[0]['new'], '1.3.0')
        self.assertEqual((self.root / 'Beta/Beta.csproj').read_bytes(), old_beta)
        with self.assertRaises(ValueError):
            engine.bump(self.root, ['Alpha'], '1.0.0')

    def test_pack_versions_are_independent(self):
        engine.bump(self.root, [], 'minor', pack='full')
        settings = engine.config(self.root)
        self.assertEqual(settings['packs']['full_version'], '2.1.0')
        self.assertEqual(settings['packs']['our_version'], '1.0.0')

    def test_package_and_pack_use_identical_dlls(self):
        self.make_plan(packs=['our'])
        self.prepare_fake()
        with zipfile.ZipFile(self.out / 'artifacts/Alpha.zip') as own, zipfile.ZipFile(self.out / 'artifacts/nuclear-option-client-mods.nommpack') as pack:
            self.assertEqual(own.read('Alpha/Alpha.dll'), pack.read('mods/dev.test.alpha/Alpha.dll'))
            self.assertEqual(json.loads(own.read('Alpha/release-info.json'))['version'], '1.2.3')
            self.assertEqual(json.loads(pack.read('modlist.nomm.json')),
                             [{'id': 'dev.test.alpha', 'version': '1.2.3'}, {'id': 'dev.test.beta', 'version': '0.2.0'}])

    def test_tampered_source_and_artifact_are_refused(self):
        self.make_plan()
        self.prepare_fake()
        target = self.out / 'artifacts/Alpha.zip'
        original = target.read_bytes()
        target.write_bytes(original + b'changed')
        with self.assertRaisesRegex(ValueError, 'asset changed'):
            engine.verify_prepared(self.out / 'plan.json')
        target.write_bytes(original)
        (self.out / 'source/Alpha/Plugin.cs').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'source changed'):
            engine.read_plan(self.out / 'plan.json')

    def test_newer_registry_version_prevents_reusing_an_older_version(self):
        remote = {'versions': {'Alpha': '1.2.4'}, 'releases': [], 'baseCommit': None, 'exists': False}
        with patch.object(engine, 'discovery', return_value=remote):
            with self.assertRaisesRegex(ValueError, 'must exceed'):
                self.make_plan(offline=False)

    def test_registry_draft_preserves_existing_history_and_relationships(self):
        value = self.make_plan()
        old = {'version': '1.0.0', 'fileName': 'old.zip', 'dependencies': [{'id': 'known.helper', 'version': '1.0.0'}]}
        value['registryEntries'] = {'dev.test.alpha': {'id': 'dev.test.alpha', 'downloadCount': 123, 'artifacts': [old]}}
        mod = engine.load_builder(self.root).load_mods(self.root)[0]
        item = value['items'][0]
        result = engine.manifest(mod, item, {'sha256': 'a' * 64}, value)
        self.assertEqual(result['downloadCount'], 123)
        self.assertEqual([a['version'] for a in result['artifacts']], ['1.2.3', '1.0.0'])
        self.assertEqual(result['artifacts'][0]['dependencies'], old['dependencies'])
        self.assertEqual(result['githubRepoName'], 'test')
        self.assertEqual(result['releaseTagPrefix'], 'Alpha-v')

    def test_deterministic_archives(self):
        one, two = Path(self.temp.name) / 'a.zip', Path(self.temp.name) / 'b.zip'
        engine.write_archive(one, {'a.txt': b'a', 'b.txt': b'b'})
        engine.write_archive(two, {'b.txt': b'b', 'a.txt': b'a'})
        self.assertEqual(one.read_bytes(), two.read_bytes())

    def test_public_history_starts_with_public_snapshot_and_preserves_bytes(self):
        value = self.make_plan()
        files = engine.read_source(self.out / 'plan.json', value)
        target = Path(self.temp.name) / 'public'
        target.mkdir()
        sha, _ = engine.snapshot_commit(value, files, target)
        self.assertEqual(self.command('rev-list', '--count', 'HEAD', cwd=target).strip(), b'1')
        self.assertEqual(self.command('show', 'HEAD:Alpha/Plugin.cs', cwd=target), files['Alpha/Plugin.cs'])
        self.assertTrue(self.command('ls-tree', 'HEAD', 'Alpha/scripts/install-mod.sh', cwd=target).startswith(b'100755'))
        self.assertEqual(self.command('rev-parse', 'HEAD', cwd=target).decode().strip(), sha)
        with self.assertRaises(subprocess.CalledProcessError):
            self.command('cat-file', '-e', self.private_head, cwd=target)
        other = Path(self.temp.name) / 'public-again'
        other.mkdir()
        private_index = (self.root / '.git/index').read_bytes()
        with patch.dict(os.environ, GIT_DIR=str(self.root / '.git'), GIT_INDEX_FILE=str(self.root / '.git/index')):
            again, _ = engine.snapshot_commit(value, files, other)
        self.assertEqual(sha, again)
        self.assertEqual((self.root / '.git/index').read_bytes(), private_index)

    def test_plan_field_validation_even_if_a_bad_document_is_resealed(self):
        value = self.make_plan()
        for key, bad in [('branch', '--help'), ('baseCommit', '--upload-pack=bad')]:
            changed = {k: v for k, v in value.items() if k != 'planHash'}
            changed[key] = bad
            engine.write_json(self.out / 'plan.json', engine.seal(changed, 'planHash'))
            with self.assertRaises(ValueError):
                engine.read_plan(self.out / 'plan.json')
        for key, bad in [('id', 'wrong.id'), ('version', '9.9.9'), ('tag', '--help'), ('asset', '-bad.zip')]:
            changed = copy.deepcopy({k: v for k, v in value.items() if k != 'planHash'})
            changed['items'][0][key] = bad
            engine.write_json(self.out / 'plan.json', engine.seal(changed, 'planHash'))
            with self.assertRaises(ValueError):
                engine.read_plan(self.out / 'plan.json')

    def test_incremental_publication_has_only_public_parents_and_removes_old_files(self):
        value = self.make_plan()
        files = engine.read_source(self.out / 'plan.json', value)
        remote = Path(self.temp.name) / 'public-remote.git'
        self.command('init', '--bare', str(remote))
        first = Path(self.temp.name) / 'first-public'
        first.mkdir()
        real_git = engine.git
        def local_remote(*args, **kwargs):
            if args[:3] == ('remote', 'add', 'origin'):
                args = (*args[:3], str(remote))
            return real_git(*args, **kwargs)
        with patch.object(engine, 'git', side_effect=local_remote):
            sha, _ = engine.snapshot_commit(value, files, first)
        self.command('push', 'origin', 'main', cwd=first)
        second = Path(self.temp.name) / 'second-public'
        second.mkdir()
        updated = dict(files)
        updated.pop('Beta/README.md')
        updated['Alpha/New.cs'] = b'// released source\n'
        new_plan = dict(value, baseCommit=sha)
        with patch.object(engine, 'git', side_effect=local_remote):
            engine.snapshot_commit(new_plan, updated, second)
        self.assertEqual(self.command('rev-list', '--count', 'HEAD', cwd=second).strip(), b'2')
        listing = self.command('ls-tree', '-r', '--name-only', 'HEAD', cwd=second).decode()
        self.assertNotIn('Beta/README.md', listing)
        self.assertIn('Alpha/New.cs', listing)
        with self.assertRaises(subprocess.CalledProcessError):
            self.command('cat-file', '-e', self.private_head, cwd=second)


class PackVersionConsistency(Fixture):
    def reference(self, dll, input_hash):
        content = io.BytesIO()
        with zipfile.ZipFile(content, 'w') as archive:
            archive.writestr('Beta/Beta.dll', dll)
            archive.writestr('Beta/release-info.json', json.dumps({'inputHash': input_hash}))
        return content.getvalue()

    def baseline(self):
        value = self.make_plan(packs=['our'])
        self.out = Path(self.temp.name) / 'next-update'
        return value['modInputHashes']['Beta']

    def remote(self, data):
        return {'versions': {'Beta': '0.2.0'}, 'releases': [], 'baseCommit': None, 'exists': True,
                'registryEntries': {'dev.test.beta': {'artifacts': [{'version': '0.2.0',
                    'fileName': 'Beta.zip', 'downloadUrl': 'https://github.com/dikkadev/test/releases/download/Beta-v0.2.0/Beta.zip',
                    'hash': 'sha256:' + engine.digest(data)}]}}}

    def test_input_hashes_ignore_other_mods_pack_versions_and_test_changes(self):
        value = self.make_plan()
        files = engine.read_source(self.out / 'plan.json', value)
        mods = engine.load_builder(self.root).load_mods(self.root)
        hashes = engine.mod_input_hashes(files, mods)
        files['publication.json'] = b'new publication'
        files['tools/release.toml'] += b'# pack version changed\n'
        files['tests/new-test.cs'] = b'// new test\n'
        self.assertEqual(engine.mod_input_hashes(files, mods), hashes)
        files['Alpha/Plugin.cs'] += b'// alpha code changed\n'
        changed = engine.mod_input_hashes(files, mods)
        self.assertEqual(changed['Beta'], hashes['Beta'])
        self.assertNotEqual(changed['Alpha'], hashes['Alpha'])

    def test_shared_production_changes_affect_consumers(self):
        value = self.make_plan()
        files = engine.read_source(self.out / 'plan.json', value)
        mods = engine.load_builder(self.root).load_mods(self.root)
        files['Alpha/Alpha.csproj'] += b'<!-- Import ../Shared/Helper/Helper.props -->\n'
        files['Shared/Helper/Helper.cs'] = b'// shared production code\n'
        before = engine.mod_input_hashes(files, mods)
        files['Shared/Helper/Helper.cs'] += b'// changed\n'
        after = engine.mod_input_hashes(files, mods)
        self.assertNotEqual(before['Alpha'], after['Alpha'])
        self.assertEqual(before['Beta'], after['Beta'])

    def test_changed_unselected_same_version_source_is_refused_at_plan(self):
        old_hash = self.baseline()
        data = self.reference(b'dev.test.beta:0.2.0', old_hash)
        self.write('Beta/Plugin.cs', '// beta changed without a bump\n')
        with patch.object(engine, 'discovery', return_value=self.remote(data)), patch.object(engine, 'download_reference', return_value=data):
            with self.assertRaisesRegex(ValueError, 'without a version bump'):
                self.make_plan(packs=['our'], offline=False)

    def test_unchanged_unselected_member_can_keep_its_published_version(self):
        old_hash = self.baseline()
        data = self.reference(b'dev.test.beta:0.2.0', old_hash)
        with patch.object(engine, 'discovery', return_value=self.remote(data)), patch.object(engine, 'download_reference', return_value=data):
            value = self.make_plan(packs=['our'], offline=False)
        self.assertEqual(value['packReferences']['Beta']['dllSha256'], engine.digest(b'dev.test.beta:0.2.0'))
        self.prepare_fake()
        with zipfile.ZipFile(self.out / 'artifacts/nuclear-option-client-mods.nommpack') as archive:
            self.assertEqual(archive.read('mods/dev.test.beta/Beta.dll'), b'dev.test.beta:0.2.0')

    def test_same_source_but_different_compiled_same_version_dll_is_refused(self):
        old_hash = self.baseline()
        data = self.reference(b'other compiled bytes', old_hash)
        with patch.object(engine, 'discovery', return_value=self.remote(data)), patch.object(engine, 'download_reference', return_value=data):
            self.make_plan(packs=['our'], offline=False)
        with self.assertRaisesRegex(ValueError, 'differs from the published same-version DLL'):
            self.prepare_fake()
        self.assertFalse((self.out / 'receipt.json').exists())

    def pack_only_remote(self, old_hash):
        content = io.BytesIO()
        with zipfile.ZipFile(content, 'w') as archive:
            archive.writestr('build-info.json', json.dumps({'mods': [{'id': 'dev.test.beta', 'version': '0.2.0', 'inputHash': old_hash}]}))
            archive.writestr('mods/dev.test.beta/Beta.dll', b'dev.test.beta:0.2.0')
        data = content.getvalue()
        return {'versions': {}, 'baseCommit': None, 'exists': True, 'releases': [{'tag_name': 'ClientModpack-v1.0.0',
            'assets': [{'name': 'nuclear-option-client-mods.nommpack', 'browser_download_url':
                'https://github.com/dikkadev/test/releases/download/ClientModpack-v1.0.0/nuclear-option-client-mods.nommpack',
                'digest': 'sha256:' + engine.digest(data)}]}]}, data

    def test_a_previous_pack_only_version_cannot_hide_changed_source_in_next_pack(self):
        old_hash = self.baseline()
        remote, data = self.pack_only_remote(old_hash)
        engine.bump(self.root, [], 'patch', pack='our')
        self.write('Beta/Plugin.cs', '// changed since the first pack-only release\n')
        with patch.object(engine, 'discovery', return_value=remote), patch.object(engine, 'download_reference', return_value=data):
            with self.assertRaisesRegex(ValueError, 'without a version bump'):
                self.make_plan(packs=['our'], offline=False)

    def test_individual_release_after_pack_only_publication_keeps_same_dll(self):
        old_hash = self.baseline()
        remote, data = self.pack_only_remote(old_hash)
        with patch.object(engine, 'discovery', return_value=remote), patch.object(engine, 'download_reference', return_value=data):
            value = self.make_plan(names=['Beta'], offline=False)
        self.assertIn('Beta', value['packReferences'])
        self.prepare_fake()
        with zipfile.ZipFile(self.out / 'artifacts/Beta.zip') as archive:
            self.assertEqual(archive.read('Beta/Beta.dll'), b'dev.test.beta:0.2.0')

    def test_changed_pack_only_version_needs_bump_before_individual_release_too(self):
        old_hash = self.baseline()
        remote, data = self.pack_only_remote(old_hash)
        self.write('Beta/Plugin.cs', '// changed after pack publication\n')
        with patch.object(engine, 'discovery', return_value=remote), patch.object(engine, 'download_reference', return_value=data):
            with self.assertRaisesRegex(ValueError, 'without a version bump'):
                self.make_plan(names=['Beta'], offline=False)

    def test_status_includes_versions_published_only_in_packs(self):
        old_hash = self.baseline()
        remote, data = self.pack_only_remote(old_hash)
        with patch.object(engine, 'discovery', return_value=remote), patch.object(engine, 'download_reference', return_value=data):
            result = engine.status(self.root)
        beta = next(row for row in result['items'] if row['name'] == 'Beta')
        self.assertEqual(beta['published'], '0.2.0')
        self.assertEqual(beta['packPublished'], '0.2.0')
        self.assertIsNone(beta['individualPublished'])


class Publication(Fixture):
    def setUp(self):
        super().setUp()
        remote = {'versions': {}, 'releases': [], 'baseCommit': None, 'exists': True}
        with patch.object(engine, 'discovery', return_value=remote):
            self.make_plan(names=['Alpha', 'Beta'], offline=False)
        self.prepare_fake()
        self.value = engine.read_plan(self.out / 'plan.json')
        self.receipt = engine.verify_prepared(self.out / 'plan.json')
        self.remote = remote
        self.releases = {}
        self.events = []
        self.head = None
        self.fail_upload = False
        self.commit = 'b' * 40
        self.login = 'dikkadev'
        self.can_push = True
        self.repo_private = False

    def fake_git(self, *args, **kwargs):
        if 'ls-remote' in args:
            return (f'{self.head}\trefs/heads/main\n'.encode() if self.head else b'')
        if 'push' in args:
            self.events.append(('push', args))
            self.head = self.commit
        return b''

    def fake_gh_json(self, *args, data=None):
        if args == ('api', 'user'):
            return {'login': self.login}
        path = args[1]
        if path == 'repos/dikkadev/test' and len(args) == 2:
            return {'private': self.repo_private, 'default_branch': 'main', 'permissions': {'push': self.can_push}}
        if '--slurp' in args:
            return [list(copy.deepcopy(self.releases).values())]
        if path == 'repos/dikkadev/test/releases' and data:
            entry = json.loads(data)
            entry['id'] = len(self.releases) + 1
            entry['assets'] = []
            self.releases[entry['tag_name']] = entry
            self.events.append(('draft', entry['tag_name']))
            return copy.deepcopy(entry)
        if path.startswith('repos/dikkadev/test/releases/'):
            identifier = int(path.rsplit('/', 1)[1])
            return copy.deepcopy(next(r for r in self.releases.values() if r['id'] == identifier))
        raise AssertionError(args)

    def fake_gh(self, *args, data=None):
        if args[:2] == ('release', 'upload'):
            tag = args[2]
            self.events.append(('upload', tag))
            if self.fail_upload:
                self.fail_upload = False
                raise ValueError('Simulated connection loss')
            path = Path(args[3])
            self.releases[tag]['assets'] = [{'name': path.name, 'size': path.stat().st_size,
                                            'state': 'uploaded', 'id': 100 + self.releases[tag]['id'],
                                            'digest': 'sha256:' + engine.digest(path.read_bytes())}]
            return b''
        if args[0] == 'api' and args[-1] == 'DELETE':
            identifier = int(args[1].rsplit('/', 1)[1])
            entry = next(r for r in self.releases.values() if r['assets'][0]['id'] == identifier)
            self.events.append(('remove-starter', entry['tag_name']))
            entry['assets'] = []
            return b''
        if args[0] == 'api' and data:
            identifier = int(args[1].rsplit('/', 1)[1])
            release = next(r for r in self.releases.values() if r['id'] == identifier)
            release.update(json.loads(data))
            self.events.append(('publish', release['tag_name']))
            return b'{}'
        raise AssertionError(args)

    def publishing(self):
        stack = contextlib.ExitStack()
        stack.enter_context(patch.object(engine, 'discovery', return_value=self.remote))
        stack.enter_context(patch.object(engine, 'gh_json', side_effect=self.fake_gh_json))
        stack.enter_context(patch.object(engine, 'gh', side_effect=self.fake_gh))
        stack.enter_context(patch.object(engine, 'git', side_effect=self.fake_git))
        stack.enter_context(patch.object(engine, 'snapshot_commit', return_value=(self.commit, [])))
        return stack

    def test_retry_resumes_empty_draft_and_does_not_skip_it(self):
        self.fail_upload = True
        with self.publishing():
            with self.assertRaisesRegex(ValueError, 'connection loss'):
                engine.publish(self.out / 'plan.json')
            self.assertTrue(self.releases['Alpha-v1.2.3']['draft'])
            self.assertEqual(self.releases['Alpha-v1.2.3']['assets'], [])
            engine.publish(self.out / 'plan.json')
            self.assertTrue(all(not r['draft'] and r['assets'] for r in self.releases.values()))
            # A third call verifies the existing releases without uploading again.
            uploads = sum(e[0] == 'upload' for e in self.events)
            engine.publish(self.out / 'plan.json')
            self.assertEqual(sum(e[0] == 'upload' for e in self.events), uploads)
        first_publish = next(i for i, e in enumerate(self.events) if e[0] == 'publish')
        uploads_before = [e for e in self.events[:first_publish] if e[0] == 'upload']
        self.assertGreaterEqual(len(uploads_before), 2)
        for event, args in self.events:
            if event == 'push':
                self.assertIn('--atomic', args)
                self.assertNotIn('--force', args)

    def test_mismatched_existing_asset_is_refused_before_push(self):
        with self.publishing():
            engine.publish(self.out / 'plan.json')
            self.releases['Alpha-v1.2.3']['assets'][0]['digest'] = 'sha256:' + '0' * 64
            count = sum(e[0] == 'push' for e in self.events)
            with self.assertRaisesRegex(ValueError, 'SHA-256 mismatch'):
                engine.publish(self.out / 'plan.json')
            self.assertEqual(sum(e[0] == 'push' for e in self.events), count)

    def test_starter_asset_on_our_draft_is_removed_and_retried(self):
        with self.publishing():
            self.fail_upload = True
            with self.assertRaises(ValueError):
                engine.publish(self.out / 'plan.json')
            draft = self.releases['Alpha-v1.2.3']
            draft['assets'] = [{'name': 'Alpha.zip', 'id': 123, 'state': 'starter', 'size': 0}]
            engine.publish(self.out / 'plan.json')
        self.assertIn(('remove-starter', 'Alpha-v1.2.3'), self.events)
        self.assertFalse(draft['draft'])

    def test_open_asset_on_our_draft_is_removed_and_retried(self):
        with self.publishing():
            self.fail_upload = True
            with self.assertRaises(ValueError):
                engine.publish(self.out / 'plan.json')
            draft = self.releases['Alpha-v1.2.3']
            draft['assets'] = [{'name': 'Alpha.zip', 'id': 123, 'state': 'open', 'size': 0}]
            engine.publish(self.out / 'plan.json')
        self.assertIn(('remove-starter', 'Alpha-v1.2.3'), self.events)
        self.assertFalse(draft['draft'])

    def test_new_same_version_publication_is_rechecked_before_pack_publish(self):
        self.out = Path(self.temp.name) / 'pack-race'
        with patch.object(engine, 'discovery', return_value=self.remote):
            engine.plan(self.root, [], ['our'], game_version='0.34.2', output=self.out)
        self.prepare_fake()
        content = io.BytesIO()
        with zipfile.ZipFile(content, 'w') as archive:
            archive.writestr('Beta/Beta.dll', b'different published dll')
        self.remote['versions'] = {'Beta': '0.2.0'}
        self.remote['registryEntries'] = {'dev.test.beta': {'artifacts': [{'version': '0.2.0', 'fileName': 'Beta.zip',
            'downloadUrl': 'https://github.com/dikkadev/test/releases/download/Beta-v0.2.0/Beta.zip'}]}}
        with self.publishing(), patch.object(engine, 'download_reference', return_value=content.getvalue()):
            with self.assertRaisesRegex(ValueError, 'differs from the published same-version DLL'):
                engine.publish(self.out / 'plan.json')
        self.assertEqual(self.events, [])

    def test_reordered_receipt_assets_are_matched_by_tag(self):
        receipt = dict(self.receipt)
        receipt.pop('receiptHash')
        receipt['assets'] = list(reversed(receipt['assets']))
        engine.write_json(self.out / 'receipt.json', engine.seal(receipt, 'receiptHash'))
        with self.publishing():
            engine.publish(self.out / 'plan.json')
        self.assertEqual({a['assets'][0]['name'] for a in self.releases.values()}, {'Alpha.zip', 'Beta.zip'})

    def test_branch_drift_cannot_publish(self):
        with self.publishing():
            self.head = 'c' * 40
            with self.assertRaisesRegex(ValueError, 'branch advanced'):
                engine.publish(self.out / 'plan.json')
        self.assertFalse(any(e[0] == 'push' for e in self.events))

    def test_private_repository_and_writable_collaborator_can_publish(self):
        self.login = 'release-collaborator'
        self.repo_private = True
        with self.publishing():
            result = engine.publish(self.out / 'plan.json')
        self.assertEqual(result['repository'], 'dikkadev/test')
        self.assertTrue(all(not r['draft'] and r['assets'] for r in self.releases.values()))

    def test_invited_agent_can_validate_a_repository_owned_by_the_user(self):
        self.login = 'clankagent'
        with self.publishing():
            result = engine.publish(self.out / 'plan.json', dry_run=True)
        self.assertEqual(result['repository'], 'dikkadev/test')
        self.assertTrue(result['dryRun'])
        self.assertEqual(self.events, [])

    def test_invited_agent_without_write_permission_cannot_publish(self):
        self.login = 'clankagent'
        self.can_push = False
        with self.publishing():
            with self.assertRaisesRegex(ValueError, 'does not have write permission'):
                engine.publish(self.out / 'plan.json')
        self.assertEqual(self.events, [])

    def test_invited_agent_cannot_create_a_repository_under_the_users_account(self):
        self.login = 'clankagent'
        self.remote['exists'] = False
        with self.publishing():
            with self.assertRaisesRegex(ValueError, 'must first be created by dikkadev'):
                engine.publish(self.out / 'plan.json', create_repo=True)
        self.assertEqual(self.events, [])

    def test_wrong_approval_hash_or_changed_receipt_stops_before_external_calls(self):
        with self.publishing():
            with self.assertRaisesRegex(ValueError, 'expect-hash'):
                engine.publish(self.out / 'plan.json', expect_hash='0' * 64)
        self.assertEqual(self.events, [])

    def test_dry_run_does_not_push_or_create_drafts(self):
        with self.publishing():
            result = engine.publish(self.out / 'plan.json', dry_run=True)
        self.assertTrue(result['dryRun'])
        self.assertEqual(self.events, [])
        with self.publishing():
            self.head = 'c' * 40
            with self.assertRaisesRegex(ValueError, 'branch advanced'):
                engine.publish(self.out / 'plan.json', dry_run=True)


class CommandLine(unittest.TestCase):
    def test_json_output_preserves_unicode_on_a_windows_legacy_stream(self):
        raw = io.BytesIO()
        output = io.TextIOWrapper(raw, encoding='cp1252')
        value = {'notes': '\ufeff\u65e5\u672c\u8a9e'}
        with patch.object(engine, 'status', return_value=value), contextlib.redirect_stdout(output):
            self.assertEqual(cli.main(['status', '--offline', '--json']), 0)
        output.flush()
        self.assertEqual(json.loads(raw.getvalue().decode('ascii')), value)

    def test_notes_file_accepts_windows_utf8_bom(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'notes.txt'
            path.write_text('Stable release notes.', encoding='utf-8-sig')
            with patch.object(engine, 'plan', return_value={'ok': True}) as plan, contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(cli.main(['plan', '--all', '--game-version', '0.34.1', '--notes-file', str(path),
                                           '--output', str(Path(directory) / 'release'), '--json']), 0)
            self.assertEqual(plan.call_args.kwargs['notes'], 'Stable release notes.')

    def test_no_terminal_no_args_does_not_open_wizard(self):
        with patch.object(sys.stdin, 'isatty', return_value=False), contextlib.redirect_stdout(io.StringIO()), patch.object(cli, 'wizard') as wizard:
            self.assertEqual(cli.main([]), 2)
        wizard.assert_not_called()

    def test_argument_mode_never_enters_wizard(self):
        with patch.object(engine, 'status', return_value={'items': []}), patch.object(cli, 'wizard') as wizard, contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(cli.main(['status', '--offline', '--json']), 0)
            self.assertEqual(json.loads(output.getvalue()), {'items': []})
        wizard.assert_not_called()

    def test_help_explains_history_versions_auth_and_recovery(self):
        help_text = cli.parser().format_help()
        for phrase in ('private Git/jj history', 'same built DLLs', 'dikkadev', 'Retry the SAME', '--expect-hash', 'EXIT CODES'):
            self.assertIn(phrase, help_text)

    def test_wizard_operation_errors_are_reported_without_tracebacks(self):
        class Terminal(io.StringIO):
            def isatty(self):
                return True
        with patch.object(sys.stdin, 'isatty', return_value=True), contextlib.redirect_stdout(Terminal()), \
             contextlib.redirect_stderr(io.StringIO()) as errors, patch.object(cli, 'wizard', side_effect=ValueError('Tag already exists')):
            self.assertEqual(cli.main([]), 1)
        self.assertIn('ERROR: Tag already exists', errors.getvalue())
        self.assertNotIn('Traceback', errors.getvalue())


class Wizard(Fixture):
    def test_manual_selection_prepares_without_publishing_by_default(self):
        value = self.make_plan()
        answers = iter([['Alpha'], 'None', '1.2.3', '0.34.2', True, '', False, str(self.out), True, False])
        question = lambda *args, **kwargs: types.SimpleNamespace(ask=lambda: next(answers))
        questionary = types.SimpleNamespace(Choice=lambda label, value: value,
            checkbox=question, select=question, text=question, confirm=question, path=question)
        state = {'repository': 'dikkadev/test', 'items': [{'name': 'Alpha', 'local': '1.2.3',
            'published': None, 'suggested': '1.2.3'}]}
        with patch.dict(sys.modules, questionary=questionary), patch.object(engine, 'ROOT', self.root), \
             patch.object(engine, 'status', return_value=state), patch.object(engine, 'plan', return_value=value) as planned, \
             patch.object(engine, 'prepare') as prepared, patch.object(engine, 'publish') as published, \
             contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(cli.wizard(), 0)
        self.assertEqual(planned.call_args.args[1], ['Alpha'])
        self.assertTrue(planned.call_args.kwargs['prerelease'])
        prepared.assert_called_once()
        published.assert_not_called()


if __name__ == '__main__':
    unittest.main()
