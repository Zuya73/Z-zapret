"""Check a staged runtime or optional legacy distribution without starting the driver."""
import argparse
import hashlib
import json
from pathlib import Path
import re

USER_LISTS = {'list-general-user.txt', 'list-exclude-user.txt', 'ipset-exclude-user.txt', 'ipset-include-user.txt'}

def require(value, message):
    if not value:
        raise ValueError(message)

def verify(directory, legacy=False):
    directory = Path(directory)
    needed = ['bin/winws.exe', 'bin/WinDivert.dll', 'bin/WinDivert64.sys', 'bin/cygwin1.dll',
              'lists/list-general.txt', 'lists/list-exclude.txt', 'lists/ipset-all.txt.backup',
              'README.md', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt', 'licenses/LGPL-3.0.txt']
    if legacy:
        needed += ['service.bat', 'general.bat', 'utils/test zapret.ps1', 'utils/targets.txt',
                   'utils/check_updates.enabled', '.service/version.txt', '.service/hosts', '.service/ipset-service.txt']
        expected = len(list((Path(__file__).resolve().parents[1] / 'legacy/flowseal').glob('general*.bat')))
        require(len(list(directory.glob('general*.bat'))) == expected, 'Legacy bundle must contain every imported profile.')
    else:
        needed += ['x2zapret.exe', 'x2zapret.dll', 'x2zapret.deps.json', 'x2zapret.runtimeconfig.json',
                   'Zapret.Core.dll', 'strategies.json', 'targets.json', 'engine-manifest.json', 'upstream-info.json', 'legacy-seed-hashes.json']
        scripts = [p for p in directory.rglob('*') if p.suffix.lower() in {'.bat', '.ps1'}]
        require(not scripts, f'Runtime must not contain legacy scripts: {scripts}')
        require(not (directory / 'utils').exists() and not (directory / '.service').exists(), 'Runtime contains legacy folders.')
        catalog = json.loads((directory / 'strategies.json').read_text(encoding='utf-8-sig'))
        info = json.loads((directory / 'upstream-info.json').read_text(encoding='utf-8-sig'))
        require(len(catalog) == info['profileCount'] and len(catalog) > 0, 'Runtime profile count differs from its component version.')
        for strategy in catalog:
            for argument in strategy['arguments']:
                for folder, name in re.findall(r'\{(BIN|LISTS)\}([^\s"{}]+)', argument):
                    if folder == 'LISTS' and name in USER_LISTS:
                        continue
                    require(Path(name).name == name, f'Unexpected resource path: {name}')
                    require((directory / folder.lower() / name).is_file(), f'Missing strategy resource: {name}')
        hashes = json.loads((directory / 'engine-manifest.json').read_text(encoding='utf-8-sig'))
        require(set(hashes) == {p.name for p in (directory / 'bin').iterdir() if p.is_file()}, 'Engine manifest does not cover the binary directory.')
        for name, expected in hashes.items():
            require(hashlib.sha256((directory / 'bin' / name).read_bytes()).hexdigest().lower() == expected.lower(), f'Invalid engine hash: {name}')
    for name in needed:
        require((directory / name).is_file(), f'Missing package file: {name}')
    print(f'Validated {"legacy" if legacy else "runtime"} package: {directory}')

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--legacy', action='store_true')
    args = parser.parse_args()
    try:
        verify(args.directory, args.legacy)
    except (OSError, ValueError, KeyError) as error:
        raise SystemExit(str(error)) from error
