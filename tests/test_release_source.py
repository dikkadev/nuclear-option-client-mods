import importlib.util
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

REPO_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO_ROOT / 'tools'))
import release_source as rs  # noqa: E402

# The workspace ships its own git; make it visible the same way collect_source finds it.
if not shutil.which('git'):
    try:
        os.environ['PATH'] = str(Path(rs.find_git(REPO_ROOT)).parent) + os.pathsep + os.environ['PATH']
    except rs.SourceError:
        pass
HAVE_GIT = shutil.which('git') is not None


def secret(kind):
    # Built at run time so this file never contains a complete token pattern.
    return {
        'github': 'ghp' + '_' + 'a1B2c3D4' * 5,
        'pat': 'github' + '_pat_' + 'A1b2C3d4E5' * 4,
        'key': '-----BEGIN ' + 'OPENSSH PRIVATE KEY' + '-----\nabc\n',
    }[kind]


def git(root, *args, input=None):
    completed = subprocess.run(['git', *args], cwd=root, input=input, check=True,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               **rs.background_kwargs())
    return completed.stdout.decode().strip()


def csproj(name):
    return (f'<Project><PropertyGroup><BepInExPluginGuid>dev.test.{name.lower()}</BepInExPluginGuid>'
            f'<Version>1.2.3</Version><AssemblyName>{name}</AssemblyName></PropertyGroup></Project>')


def mods_for(root, *names):
    return [dict(name=n, project=(root / n / f'{n}.csproj').resolve(), guid=f'dev.test.{n.lower()}',
                 version='1.2.3', assembly=n, include=['README.md'], description=f'{n} mod')
            for n in names]


@unittest.skipUnless(HAVE_GIT, 'git is required for fixture repositories')
class CollectSourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve() / 'repo'
        self.root.mkdir()
        git(self.root, 'init', '-q')
        self.write('.gitignore', 'bin/\nobj/\ndist/\n.env\n')
        for name in ('Alpha', 'Beta'):
            self.write(f'{name}/{name}.csproj', csproj(name))
            self.write(f'{name}/Plugin.cs', 'class P {}\r\nclass Q {}\r\n')
            self.write(f'{name}/README.md', f'{name} readme\n')
            self.write(f'{name}/scripts/install-mod.sh', '#!/usr/bin/env bash\necho install\n')
        self.write('Alpha/THIRD-PARTY-NOTICES.txt', 'Third party notice\r\nMIT\r\n')
        self.write('Alpha/assets/sound.mp3', b'\x00\x01audio\xff')
        self.write('Shared/Helper/Helper.cs', 'class H {}\n')
        self.write('Shared/Helper/Helper.props', '<Project/>\n')
        self.write('tests/ClientApiCheck/Program.cs', 'class T {}\n')
        self.write('tests/api-contract.json', '{"a":1}\n')
        self.write('tests/test_example.py', 'pass\n')
        self.write('tests/SMOKE-TESTS.md', 'Use Tailscale to reach the test host.\n')
        self.write('tools/build-modpack.py', 'print("build")\n')
        self.write('tools/process_utils.py', 'pass\n')
        self.write('tools/release_cli.py', 'pass\n')
        self.write('tools/nomnom-mirrors.toml', 'forgejo_source_url = "https://private.example/x"\n')
        self.write('tools/publish-nomnom-mirrors.py', 'pass\n')
        self.write('tools/random-script.py', 'pass\n')
        self.write('NuGet.Config', '<configuration/>\n')
        self.write('.env.example', 'NO_MANAGED_DIR="/path"\n')
        self.write('README.md', 'private root readme\n')
        self.write('AGENTS.md', 'private\n')
        self.write('global.json', '{"sdk":{"version":"10.0.100"}}\n')
        self.write('.dev/modding-reference.md', 'private\n')
        self.write('Alpha/.dev/wip/notes.md', 'private\n')
        self.write('archive/Old/Old.cs', 'class Old {}\n')
        self.write('NuclearReplay/Replay.cs', 'class R {}\n')
        self.write('references/managed/Game.dll', b'dll')
        self.write('Alpha/bin/Release/Generated.cs', 'generated\n')
        self.write('Alpha/obj/Generated.cs', 'generated\n')
        self.write('Alpha/screenshot.png', b'png')
        self.write('Alpha/settings.cfg', 'runtime\n')
        self.write('Alpha/Native.dll', b'dll')
        self.write('Unlisted/Other.cs', 'class O {}\n')
        self.write('.env', 'SECRET=x\n')
        git(self.root, 'add', '-A')
        git(self.root, 'add', '-f', '--', 'Alpha/bin/Release/Generated.cs', '.env')
        self.mods = mods_for(self.root, 'Alpha', 'Beta')

    def write(self, relative, content):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content if isinstance(content, bytes) else content.encode())

    def test_allowlist_exports_sources_and_excludes_private_material(self):
        files = rs.collect_source(self.root, self.mods)
        self.assertEqual(sorted(files), [
            '.env.example', '.gitignore', 'Alpha/Alpha.csproj', 'Alpha/Plugin.cs', 'Alpha/README.md',
            'Alpha/THIRD-PARTY-NOTICES.txt', 'Alpha/assets/sound.mp3', 'Alpha/scripts/install-mod.sh',
            'Beta/Beta.csproj', 'Beta/Plugin.cs', 'Beta/README.md', 'Beta/scripts/install-mod.sh',
            'NuGet.Config', 'Shared/Helper/Helper.cs', 'Shared/Helper/Helper.props',
            'tests/ClientApiCheck/Program.cs', 'tests/SMOKE-TESTS.md', 'tests/api-contract.json',
            'tests/test_example.py', 'tools/build-modpack.py', 'tools/process_utils.py',
            'tools/release_cli.py',
        ])

    def test_license_exported_when_present(self):
        self.write('LICENSE', 'Some licence text\n')
        git(self.root, 'add', 'LICENSE')
        self.assertEqual(rs.collect_source(self.root, self.mods)['LICENSE'], b'Some licence text\n')

    def test_text_is_normalized_to_lf_and_notice_content_preserved(self):
        files = rs.collect_source(self.root, self.mods)
        self.assertEqual(files['Alpha/Plugin.cs'], b'class P {}\nclass Q {}\n')
        self.assertEqual(files['Alpha/THIRD-PARTY-NOTICES.txt'], b'Third party notice\nMIT\n')
        self.assertEqual(files['Alpha/assets/sound.mp3'], b'\x00\x01audio\xff')
        self.assertIn(b'Tailscale', files['tests/SMOKE-TESTS.md'])

    def test_untracked_files_require_opt_in(self):
        self.write('Beta/New.cs', 'class New {}\n')
        self.write('Beta/bin/Ignored.cs', 'ignored\n')
        self.assertNotIn('Beta/New.cs', rs.collect_source(self.root, self.mods))
        files = rs.collect_source(self.root, self.mods, include_untracked=True)
        self.assertIn('Beta/New.cs', files)
        self.assertNotIn('Beta/bin/Ignored.cs', files)

    def test_secrets_are_refused_without_echoing_values(self):
        for kind in ('github', 'pat', 'key'):
            with self.subTest(kind=kind):
                self.write('Beta/Plugin.cs', f'// {secret(kind)}\n')
                with self.assertRaises(rs.SourceError) as caught:
                    rs.collect_source(self.root, self.mods)
                message = str(caught.exception)
                self.assertIn('Beta/Plugin.cs', message)
                self.assertNotIn(secret(kind).strip().splitlines()[0][-12:], message)
                self.assertNotIn('a1B2c3D4', message)

    def test_secret_in_excluded_file_does_not_block_export(self):
        self.write('.env', f'TOKEN={secret("github")}\n')
        self.write('Alpha/.dev/wip/notes.md', secret('key'))
        self.assertIn('Alpha/Plugin.cs', rs.collect_source(self.root, self.mods))

    def test_tracked_symlink_and_gitlink_entries_fail_closed(self):
        blob = git(self.root, 'hash-object', '-w', '--stdin', input=b'../outside')
        git(self.root, 'update-index', '--add', '--cacheinfo', f'120000,{blob},Beta/Link.cs')
        with self.assertRaisesRegex(rs.SourceError, 'Beta/Link.cs.*symbolic'):
            rs.collect_source(self.root, self.mods)
        git(self.root, 'update-index', '--force-remove', 'Beta/Link.cs')
        git(self.root, 'update-index', '--add', '--cacheinfo', f'160000,{"1" * 40},Beta/Nested.cs')
        with self.assertRaisesRegex(rs.SourceError, 'Beta/Nested.cs.*gitlink'):
            rs.collect_source(self.root, self.mods)

    def test_gitlink_in_excluded_area_is_ignored(self):
        git(self.root, 'update-index', '--add', '--cacheinfo', f'160000,{"1" * 40},archive/Sub')
        self.assertIn('Beta/Beta.csproj', rs.collect_source(self.root, self.mods))

    def test_tracked_file_missing_from_worktree_fails(self):
        (self.root / 'Beta/Plugin.cs').unlink()
        with self.assertRaisesRegex(rs.SourceError, 'missing from the working tree'):
            rs.collect_source(self.root, self.mods)

    def outside_directory(self):
        outside = Path(self.temp.name) / 'outside'
        outside.mkdir()
        (outside / 'Evil.cs').write_text('class Evil {}\n')
        return outside

    def test_real_file_symlink_escape_is_refused(self):
        outside = self.outside_directory()
        try:
            os.symlink(outside / 'Evil.cs', self.root / 'Beta/Linked.cs')
        except (OSError, NotImplementedError):
            self.skipTest('file symbolic links are not available')
        with self.assertRaisesRegex(rs.SourceError, 'Beta/Linked.cs'):
            rs.collect_source(self.root, self.mods, include_untracked=True)

    def test_directory_link_escape_is_refused(self):
        outside = self.outside_directory()
        link = self.root / 'Beta/linkdir'
        try:
            os.symlink(outside, link, target_is_directory=True)
        except (OSError, NotImplementedError):
            if os.name != 'nt':
                self.skipTest('symbolic links are not available')
            subprocess.run(['cmd', '/c', 'mklink', '/J', str(link), str(outside)], check=True,
                           stdout=subprocess.DEVNULL, **rs.background_kwargs())
        # Git lists a directory link as one entry that has no exportable name, or as its contents.
        for listing in ({'Beta/linkdir/Evil.cs': '100644'}, {'Beta/linkdir/Evil.cs': 'untracked'}):
            with patch.object(rs, 'list_source_files', return_value=listing):
                with self.assertRaisesRegex(rs.SourceError, 'link in path'):
                    rs.collect_source(self.root, self.mods)
        # Real git: junctions are listed as contents (refused above); POSIX links as one extensionless entry.
        try:
            files = rs.collect_source(self.root, self.mods, include_untracked=True)
        except rs.SourceError as error:
            self.assertIn('link in path', str(error))
        else:
            self.assertNotIn('Beta/linkdir/Evil.cs', files)
            self.assertNotIn(b'Evil', b''.join(files.values()))

    def test_traversal_and_absolute_paths_fail_closed(self):
        for bad in ('../escape.cs', 'Beta/../../escape.cs', '/etc/passwd.cs', 'C:/x/Plugin.cs',
                    'Beta\\Plugin.cs', 'Beta//Plugin.cs', 'Beta/./Plugin.cs', 'Beta/trailing./Plugin.cs'):
            with self.subTest(path=bad), patch.object(rs, 'list_source_files', return_value={bad: '100644'}):
                with self.assertRaises(rs.SourceError):
                    rs.collect_source(self.root, self.mods)

    def test_mod_validation(self):
        outside = mods_for(Path(self.temp.name), 'Alpha')
        with self.assertRaisesRegex(rs.SourceError, 'outside'):
            rs.collect_source(self.root, outside)
        with self.assertRaisesRegex(rs.SourceError, 'No active mods'):
            rs.collect_source(self.root, [])
        self.write('archive/Old/Old.csproj', csproj('Old'))
        git(self.root, 'add', '-A')
        with self.assertRaisesRegex(rs.SourceError, 'excluded directory'):
            rs.collect_source(self.root, mods_for(self.root, 'Alpha') + [dict(
                name='Old', project=(self.root / 'archive/Old/Old.csproj').resolve())])
        missing = mods_for(self.root, 'Alpha') + [dict(name='Gone', project=(self.root / 'Gone/Gone.csproj').resolve())]
        with self.assertRaisesRegex(rs.SourceError, 'not in the source list'):
            rs.collect_source(self.root, missing)

    def test_repeated_collection_is_deterministic(self):
        first = rs.collect_source(self.root, self.mods)
        second = rs.collect_source(self.root, list(reversed(self.mods)))
        self.assertEqual(list(first), sorted(first))
        self.assertEqual(rs.source_hash(first), rs.source_hash(second))


class HashAndSnapshotTests(unittest.TestCase):
    FILES = {'b/two.cs': b'two\r\n', 'a/one.cs': b'one\n', 'z.md': b''}

    def test_manifest_shape_and_hash_independent_of_order(self):
        manifest = rs.file_manifest(self.FILES)
        self.assertEqual(list(manifest), ['a/one.cs', 'b/two.cs', 'z.md'])
        self.assertEqual(manifest['a/one.cs'], {'sha256': hashlib.sha256(b'one\n').hexdigest(), 'size': 4})
        reordered = {key: self.FILES[key] for key in reversed(list(self.FILES))}
        self.assertEqual(rs.source_hash(self.FILES), rs.source_hash(reordered))
        canonical = json.dumps(manifest, sort_keys=True, separators=(',', ':'))
        self.assertEqual(rs.source_hash(self.FILES), hashlib.sha256(canonical.encode()).hexdigest())

    def test_hash_changes_with_bytes_line_endings_and_paths(self):
        base = rs.source_hash(self.FILES)
        self.assertNotEqual(base, rs.source_hash(dict(self.FILES, **{'a/one.cs': b'one\r\n'})))
        renamed = {('a/uno.cs' if key == 'a/one.cs' else key): value for key, value in self.FILES.items()}
        self.assertNotEqual(base, rs.source_hash(renamed))
        self.assertNotEqual(base, rs.source_hash({k: v for k, v in self.FILES.items() if k != 'z.md'}))

    def test_unsafe_paths_and_non_bytes_rejected(self):
        for bad in ({'../x.cs': b''}, {'/x.cs': b''}, {'a\\b.cs': b''}, {'a/x.cs': 'text'}):
            with self.subTest(files=bad), self.assertRaises(rs.SourceError):
                rs.source_hash(bad)

    def test_write_snapshot_preserves_bytes_and_refuses_nonempty_destination(self):
        with tempfile.TemporaryDirectory() as temp:
            destination = Path(temp) / 'out'
            rs.write_snapshot(self.FILES, destination)
            for path, data in self.FILES.items():
                self.assertEqual((destination / path).read_bytes(), data)
            with self.assertRaisesRegex(rs.SourceError, 'not an empty directory'):
                rs.write_snapshot(self.FILES, destination)
            self.assertEqual((destination / 'z.md').read_bytes(), b'')
            empty = Path(temp) / 'empty'
            empty.mkdir()
            rs.write_snapshot({'x.cs': b'x'}, empty)
            self.assertEqual((empty / 'x.cs').read_bytes(), b'x')
            occupied_file = Path(temp) / 'file'
            occupied_file.write_text('x')
            with self.assertRaises(rs.SourceError):
                rs.write_snapshot({'x.cs': b'x'}, occupied_file)

    def test_write_snapshot_validates_before_writing(self):
        with tempfile.TemporaryDirectory() as temp:
            destination = Path(temp) / 'out'
            with self.assertRaises(rs.SourceError):
                rs.write_snapshot({'ok.cs': b'ok', '../escape.cs': b'x'}, destination)
            self.assertFalse(destination.exists())
            with self.assertRaisesRegex(rs.SourceError, 'differ only by case'):
                rs.write_snapshot({'A.cs': b'1', 'a.cs': b'2'}, destination)
            self.assertFalse(destination.exists())


class PublicReadmeTests(unittest.TestCase):
    MODS = [
        dict(name='Zed', project=Path('/private/place/Zed/Zed.csproj'), guid='dev.test.zed', version='2.0.0',
             assembly='Zed', include=[], description='Zed | pipes\nand lines'),
        dict(name='Alpha', project=Path('/private/place/Alpha/Alpha.csproj'), guid='dev.test.alpha', version='1.2.3',
             assembly='Alpha', include=[]),
    ]

    def test_lists_every_mod_and_reference_prerequisite(self):
        text = rs.public_readme(self.MODS, 'dikkadev/nuclear-option-mods').decode()
        for token in ('`Alpha`', '1.2.3', '`dev.test.alpha`', '`Zed`', '2.0.0', '`dev.test.zed`',
                      'Zed \\| pipes and lines', 'NO_MANAGED_DIR', 'NO_BEPINEX_CORE_DIR', '.NET 10',
                      'https://github.com/dikkadev/nuclear-option-mods.git', 'cd nuclear-option-mods',
                      'proprietary', 'THIRD-PARTY-NOTICES.txt', 'Alpha/Alpha.csproj'):
            self.assertIn(token, text)
        self.assertLess(text.index('`Alpha`'), text.index('`Zed`'))

    def test_no_private_details_and_deterministic(self):
        data = rs.public_readme(self.MODS, 'dikkadev/nuclear-option-mods')
        self.assertEqual(data, rs.public_readme(list(reversed(self.MODS)), 'dikkadev/nuclear-option-mods'))
        self.assertNotIn(b'\r', data)
        text = data.decode().lower()
        for forbidden in ('/private/place', 'c:\\', 'users', 'agent', 'forgejo', '.dev', 'claude', 'tailscale', 'jj '):
            self.assertNotIn(forbidden, text)

    def test_repository_name_is_validated(self):
        self.assertIn(b'https://example.test/o/r.git', rs.public_readme(self.MODS, 'https://example.test/o/r'))
        for bad in ('', 'no-slash', 'a/b/c', 'a b/c', 'http://insecure/x/y', 'x/y\n# injected'):
            with self.subTest(repo=bad), self.assertRaises(rs.SourceError):
                rs.public_readme(self.MODS, bad)


@unittest.skipUnless(HAVE_GIT and (REPO_ROOT / '.git').exists() and (REPO_ROOT / 'tools/nomnom-mirrors.toml').exists(),
                     'needs the real git workspace')
class RealRepositoryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('build_modpack_for_release', REPO_ROOT / 'tools/build-modpack.py')
        builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(builder)
        cls.mods = builder.load_mods(REPO_ROOT)
        cls.files = rs.collect_source(REPO_ROOT, cls.mods)

    def test_every_active_mod_and_shared_source_is_exported(self):
        for mod in self.mods:
            relative = Path(mod['project']).resolve().relative_to(REPO_ROOT.resolve()).as_posix()
            self.assertIn(relative, self.files)
        self.assertTrue(any(path.startswith('Shared/') for path in self.files))
        self.assertIn('tests/api-contract.json', self.files)
        self.assertIn('tests/ClientApiCheck/Program.cs', self.files)
        self.assertIn('NuGet.Config', self.files)

    def test_private_and_generated_paths_are_absent(self):
        for path in self.files:
            lowered = path.lower().split('/')
            self.assertNotIn('.dev', lowered, path)
            self.assertNotIn('archive', lowered, path)
            self.assertNotIn('nuclearreplay', lowered, path)
            self.assertNotEqual(lowered[-1], 'agents.md', path)
            self.assertNotEqual(path, 'tools/nomnom-mirrors.toml')
            self.assertNotEqual(path, 'README.md')
            self.assertFalse(path.endswith(('.dll', '.pdb', '.cfg', '.env')), path)

    def test_hash_is_stable(self):
        self.assertEqual(rs.source_hash(self.files), rs.source_hash(rs.collect_source(REPO_ROOT, self.mods)))


if __name__ == '__main__':
    unittest.main()
