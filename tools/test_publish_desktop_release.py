import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from publish_desktop_release import publish


class ReleasePublicationTests(unittest.TestCase):
    def run_publication(self, existing=False, corrupt=False):
        calls = []
        with tempfile.TemporaryDirectory() as directory:
            old = Path.cwd()
            try:
                os.chdir(directory)
                Path('Directory.Build.props').write_text('<Project><Version>0.2.3</Version></Project>')
                Path('release').mkdir()
                paths = [Path('release/x2zapret-Setup-0.2.3-x64.exe'), Path('release/x2zapret-0.2.3-win-x64.zip')]
                for path in paths:
                    path.write_bytes(b'MZ' + b'x' * 4096)
                digest = hashlib.sha256(paths[0].read_bytes()).hexdigest()
                Path('release/SHA256SUMS.txt').write_text('\n'.join(f'{digest}  {path.name}' for path in paths))
                release = dict(tag_name='desktop-v0.2.3', draft=False, prerelease=False,
                               target_commitish='abc', assets=[dict(name=paths[0].name, size=4098,
                               digest='sha256:' + ('0' * 64 if corrupt else digest))])

                created = existing

                def gh(*args):
                    nonlocal created
                    calls.append(args)
                    if args[:2] == ('release', 'create'):
                        created = True
                    if args[0] == 'api':
                        self.assertNotIn('/tags/', args[1], 'Draft tags are not available via the public by-tag endpoint.')
                        return json.dumps([release] if created else [])
                    return ''

                with patch.dict(os.environ, GITHUB_REPOSITORY='owner/repo', GITHUB_SHA='abc'), patch('publish_desktop_release.gh', gh):
                    if corrupt:
                        with self.assertRaisesRegex(ValueError, 'digest/size'):
                            publish()
                    else:
                        publish()
            finally:
                os.chdir(old)
        return calls

    def test_assets_are_verified_while_hidden_before_publishing(self):
        calls = self.run_publication()
        create = next(i for i, args in enumerate(calls) if args[:2] == ('release', 'create'))
        upload = next(i for i, args in enumerate(calls) if args[:2] == ('release', 'upload'))
        verify = next(i for i, args in enumerate(calls) if i > upload and args[0] == 'api')
        edit = next(i for i, args in enumerate(calls) if args[:2] == ('release', 'edit'))
        self.assertIn('--draft', calls[create])
        self.assertLess(create, upload)
        self.assertLess(upload, verify)
        self.assertLess(verify, edit)
        self.assertIn('--draft=false', calls[edit])

    def test_a_published_version_is_never_overwritten(self):
        self.assertFalse(any(args[0] == 'release' for args in self.run_publication(existing=True)))

    def test_bad_installer_digest_keeps_release_hidden(self):
        self.assertFalse(any(args[:2] == ('release', 'edit') for args in self.run_publication(corrupt=True)))


if __name__ == '__main__':
    unittest.main()
