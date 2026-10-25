import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch as mock
import import_flowseal_strategies as importer
import sync_flowseal as sync

class FlowsealSyncTests(unittest.TestCase):
    def test_upstream_code_and_workflows_cannot_replace_the_app(self):
        self.assertIsNone(sync.destination('.github/workflows/release.yml'))
        self.assertIsNone(sync.destination('src/MainWindow.cs'))
        self.assertEqual(sync.destination('bin/winws.exe'), 'vendor/zapret/bin/winws.exe')
        self.assertEqual(sync.destination('lists/list-general.txt'), 'resources/lists/list-general.txt')
        self.assertIsNone(sync.destination('lists/list-general-user.txt'))
        for path in ['../src/a', '/absolute', 'bin/../escape', 'bin/nested/a']:
            with self.assertRaises(ValueError): sync.destination(path)

    def test_compatibility_overrides_are_idempotent_and_fail_on_unexpected_source(self):
        rules = [dict(source='a.ps1', replacements=[dict(before='old', after='new')])]
        self.assertEqual(sync.patch('a.ps1', b'old', rules), b'new')
        self.assertEqual(sync.patch('a.ps1', b'new', rules), b'new')
        with self.assertRaises(ValueError): sync.patch('a.ps1', b'unknown', rules)

    def test_legacy_seed_fingerprints_match_the_original_shipped_lists(self):
        root = Path(__file__).resolve().parents[1]
        hashes = json.loads((root / 'resources/catalogs/legacy-seed-hashes.json').read_text())
        for name, expected in hashes.items():
            data = (root / 'resources/lists' / name).read_text(encoding='utf-8-sig').replace('\r\n', '\n')
            self.assertEqual(hashlib.sha256(data.encode()).hexdigest(), expected)

    def profiles(self, names, variable='%GameFilterTCP%'):
        directory = tempfile.TemporaryDirectory()
        root = Path(directory.name)
        folder = root / 'legacy/flowseal'
        folder.mkdir(parents=True)
        for name in names:
            (folder / name).write_text('@echo off\n' + '"%BIN%winws.exe" --wf-tcp=80,' + variable +
                ' --hostlist="%LISTS%list-general.txt" --dpi-desync-fake-tls=!\n', encoding='utf-8')
        return directory, root

    def test_new_profile_counts_are_imported_without_executing_batch_files(self):
        directory, root = self.profiles(['general.bat'] + [f'general (ALT{i}).bat' for i in range(1, 26)])
        with directory, mock.object(importer, 'ROOT', root):
            profiles = importer.generate()
            self.assertEqual(len(profiles), 26)
            self.assertEqual(profiles[0]['id'], 'general')
            self.assertIn('--dpi-desync-fake-tls=!', profiles[0]['arguments'])
            self.assertIn('--wf-tcp=80,{GAME_TCP}', profiles[0]['arguments'])

    def test_repeated_options_and_literal_exclamation_are_retained(self):
        directory, root = self.profiles(['general.bat'])
        path = root / 'legacy/flowseal/general.bat'
        path.write_text(path.read_text().replace('--dpi-desync-fake-tls=!', '--dpi-desync-fake-tls=! --dpi-desync-fake-tls={BIN}a.bin --dpi-desync-fake-tls={BIN}b.bin'))
        with directory, mock.object(importer, 'ROOT', root):
            args = importer.generate()[0]['arguments']
            self.assertEqual(sum(arg.startswith('--dpi-desync-fake-tls=') for arg in args), 3)

    def test_unknown_upstream_variables_require_review(self):
        directory, root = self.profiles(['general.bat'], '%NEW_VARIABLE%')
        with directory, mock.object(importer, 'ROOT', root):
            with self.assertRaises(ValueError): importer.generate()

    def test_repeated_normalized_ids_are_rejected(self):
        directory, root = self.profiles(['general.bat', 'general (A+B).bat', 'general (A B).bat'])
        with directory, mock.object(importer, 'ROOT', root):
            with self.assertRaises(ValueError): importer.generate()

    def test_retagged_release_cannot_silently_replace_a_pinned_version(self):
        with tempfile.TemporaryDirectory() as directory:
            lock = Path(directory) / 'lock.json'
            lock.write_text(json.dumps(dict(version='1.10.4', commit='a' * 40, files=[])))
            with mock.object(sync, 'LOCK', lock), mock.object(sync, 'snapshot', return_value=(dict(version='1.10.4', commit='b' * 40), {})), \
                 mock('sys.argv', ['sync_flowseal.py', '--tag', '1.10.4']):
                with self.assertRaisesRegex(ValueError, 'retagged'): sync.main()

if __name__ == '__main__':
    unittest.main()
