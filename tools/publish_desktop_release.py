"""Publish tested desktop assets as a complete stable release discoverable by old clients."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET


def gh(*args):
    return subprocess.check_output(['gh', *args], text=True)


def publish():
    version = ET.parse('Directory.Build.props').findtext('.//Version')
    if not re.fullmatch(r'\d+\.\d+\.\d+', version or ''):
        raise ValueError('Invalid desktop version.')
    tag = f'desktop-v{version}'
    repo, commit = os.environ['GITHUB_REPOSITORY'], os.environ['GITHUB_SHA']
    directory = Path('release')
    installer = directory / f'x2zapret-Setup-{version}-x64.exe'
    portable = directory / f'x2zapret-{version}-win-x64.zip'
    checksums = directory / 'SHA256SUMS.txt'
    sums = dict((name, digest.lower()) for digest, name in
                (line.split(None, 1) for line in checksums.read_text().splitlines() if line.strip()))
    for path in [installer, portable]:
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != sums.get(path.name):
            raise ValueError(f'Missing or corrupt tested asset: {path.name}')
    releases = json.loads(gh('api', f'repos/{repo}/releases?per_page=100'))
    existing = next((release for release in releases if release['tag_name'] == tag), None)
    if existing and not existing['draft']:
        if existing['prerelease']:
            raise ValueError('Version already exists as a prerelease; choose a new stable version.')
        print(f'{tag} is already published; released assets remain unchanged.')
    else:
        if existing and existing['target_commitish'] != commit:
            raise ValueError('Draft belongs to a different commit; do not overwrite its assets.')
        if not existing:
            notes = Path('docs/releases') / f'{version}.md'
            if not notes.is_file():
                notes = directory / 'release-notes.md'
                notes.write_text(f'x2zapret {version} для Windows 10/11 x64.\n\n'
                                 'Установщик и portable ZIP проверены автоматической сборкой. '
                                 'Настройки и пользовательские списки сохраняются при обновлении.\n')
            gh('release', 'create', tag, '--draft', '--target', commit,
               '--title', f'x2zapret {version}', '--notes-file', str(notes))
        files = sorted(path for path in directory.iterdir() if path.suffix in {'.exe', '.zip', '.png'}) + [checksums]
        gh('release', 'upload', tag, *map(str, files), '--clobber')
        draft = json.loads(gh('api', f'repos/{repo}/releases/tags/{tag}'))
        asset = next(asset for asset in draft['assets'] if asset['name'] == installer.name)
        if asset.get('digest') != 'sha256:' + sums[installer.name] or asset['size'] != installer.stat().st_size:
            raise ValueError('GitHub installer digest/size do not match the tested installer; keep release hidden.')
        newer = any(re.fullmatch(r'desktop-v\d+\.\d+\.\d+', release['tag_name']) and
                    tuple(map(int, release['tag_name'][9:].split('.'))) > tuple(map(int, version.split('.')))
                    for release in releases if not release['draft'] and not release['prerelease'])
        gh('release', 'edit', tag, '--draft=false', '--latest=' + str(not newer).lower())
        print(f'Published {tag} with verified installer SHA256.')
    # Store exactly the public list consumed by UpdateManifest.Parse, without credentials.
    public = json.loads(gh('api', f'repos/{repo}/releases?per_page=30'))
    (directory / 'published-releases.json').write_text(json.dumps([r for r in public if not r['draft']]))


if __name__ == '__main__':
    publish()
