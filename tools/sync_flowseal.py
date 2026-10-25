"""Import a pinned stable Flowseal release without running upstream code."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import urllib.request
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = 'Flowseal/zapret-discord-youtube'
LOCK = ROOT / 'vendor/zapret/upstream.lock.json'

def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT)

def blob(data):
    return hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()

def destination(source):
    parts = PurePosixPath(source).parts
    if not parts or '..' in parts or source.startswith('/') or '\\' in source:
        raise ValueError('Invalid upstream path: ' + source)
    if source.startswith('bin/'):
        if len(parts) != 2: raise ValueError('Nested engine layout requires review: ' + source)
        return 'vendor/zapret/' + source
    if source.startswith('lists/'):
        if len(parts) != 2: raise ValueError('Nested lists require review: ' + source)
        if source.endswith('-user.txt'): return None
        return 'resources/' + source
    if source == 'LICENSE.txt': return 'vendor/zapret/licenses/Flowseal-LICENSE.txt'
    if re.fullmatch(r'general.*\.bat', source) or source in {'README.md', 'service.bat'} or source.startswith(('utils/', '.service/')):
        return 'legacy/flowseal/' + source
    return None

def patch(source, data, overrides):
    for override in overrides:
        if override['source'] != source: continue
        text = data.decode('utf-8').replace('\r\n', '\n')
        for item in override['replacements']:
            before, after = item['before'], item['after']
            if text.count(before) == 1:
                text = text.replace(before, after, 1)
            elif text.count(after) != 1:
                raise ValueError('Local compatibility patch needs review: ' + source)
        data = text.encode('utf-8')
    return data

def latest_tag():
    request = urllib.request.Request('https://api.github.com/repos/' + REPOSITORY + '/releases/latest',
        headers={'User-Agent': 'x2zapret-upstream-sync', 'Accept': 'application/vnd.github+json'})
    token = os.environ.get('GH_TOKEN')
    if token: request.add_header('Authorization', 'Bearer ' + token)
    with urllib.request.urlopen(request, timeout=30) as response:
        release = json.load(response)
    if release['draft'] or release['prerelease']: raise ValueError('Only stable releases may be imported.')
    return release['tag_name']

def snapshot(tag):
    if not re.fullmatch(r'\d+\.\d+\.\d+', tag): raise ValueError('Unsupported release tag: ' + tag)
    subprocess.run(['git', 'fetch', '--no-tags', '--depth=1', 'https://github.com/' + REPOSITORY + '.git',
                    'refs/tags/' + tag], cwd=ROOT, check=True)
    commit = git('rev-parse', 'FETCH_HEAD^{commit}').decode().strip()
    records = git('ls-tree', '-r', '-z', commit).split(b'\0')
    overrides = json.loads((ROOT / 'tools/flowseal-overrides.json').read_text(encoding='utf-8'))
    files, content, total = [], {}, 0
    for record in records:
        if not record: continue
        meta, rawpath = record.split(b'\t', 1)
        mode, kind, sha = meta.decode().split()
        source = rawpath.decode('utf-8')
        target = destination(source)
        if not target: continue
        if mode not in {'100644', '100755'} or kind != 'blob':
            raise ValueError('Links/submodules may not be imported: ' + source)
        data = git('show', commit + ':' + source)
        total += len(data)
        if len(data) > 20_000_000 or total > 50_000_000 or len(files) >= 1000:
            raise ValueError('Unexpected upstream size; review required.')
        data = patch(source, data, overrides)
        if target.casefold() in {p.casefold() for p in content}:
            raise ValueError('Windows path collision: ' + target)
        files.append(dict(source=source, path=target, sourceBlob=sha, blob=blob(data)))
        content[target] = data
    required = ['vendor/zapret/bin/winws.exe', 'vendor/zapret/bin/WinDivert.dll', 'vendor/zapret/bin/WinDivert64.sys',
                'legacy/flowseal/general.bat', 'legacy/flowseal/.service/version.txt', 'resources/lists/list-general.txt',
                'vendor/zapret/licenses/Flowseal-LICENSE.txt']
    if any(path not in content for path in required): raise ValueError('Required upstream files are missing.')
    if content['legacy/flowseal/.service/version.txt'].decode().strip() != tag:
        raise ValueError('Release tag and upstream version file differ.')
    count = sum(bool(re.fullmatch(r'general.*\.bat', item['source'])) for item in files)
    if not 1 <= count <= 256: raise ValueError('Unexpected profile count.')
    lock = dict(schemaVersion=1, repository=REPOSITORY, version=tag, commit=commit, profileCount=count, files=files)
    return lock, content

def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

def bump_version(tag):
    props = ROOT / 'Directory.Build.props'
    old = ET.parse(props).findtext('.//Version')
    match = re.fullmatch(r'(\d+)\.(\d+)\.(\d+)', old or '')
    if not match: raise ValueError('Invalid desktop version.')
    major, minor, patch = map(int, match.groups())
    new = f'{major}.{minor}.{patch + 1}'
    for name in ['Directory.Build.props', 'src/Zapret.Desktop/app.manifest', 'README.md', 'docs/USER-GUIDE.md']:
        path = ROOT / name
        path.write_text(path.read_text(encoding='utf-8').replace(old, new), encoding='utf-8')
    (ROOT / f'docs/releases/{new}.md').write_text(
        f'x2zapret {new}\n\n- Обновлены компоненты Flowseal до {tag}: движок, профили и стандартные списки.\n'
        '- Настройки и пользовательские списки сохраняются.\n\n'
        f'Изменения автора: https://github.com/{REPOSITORY}/releases/tag/{tag}\n', encoding='utf-8')

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tag')
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--bump-version', action='store_true')
    args = parser.parse_args()
    if not args.check and git('status', '--porcelain', '--untracked-files=no').strip():
        raise ValueError('Commit or stash tracked changes before importing upstream files.')
    previous = json.loads(LOCK.read_text(encoding='utf-8'))
    tag = args.tag or (previous['version'] if args.check else latest_tag())
    lock, content = snapshot(tag)
    if previous['version'] == tag and previous['commit'] != lock['commit']:
        raise ValueError('Upstream retagged an existing release. Manual review is required.')
    changed = lock != previous
    if args.check:
        if changed: raise ValueError('The pinned snapshot differs from its lock file.')
        for item in lock['files']:
            if blob((ROOT / item['path']).read_bytes()) != item['blob']:
                raise ValueError('Vendored file differs: ' + item['path'])
        print('Pinned upstream snapshot and compatibility overrides verified.')
        return
    if changed:
        # Delete only paths recorded by our previous lock, never the application or user files.
        for item in previous['files']:
            path = item['path']
            if path not in content:
                safe = destination(item['source'])
                if safe != path: raise ValueError('Invalid previous lock path.')
                (ROOT / path).unlink(missing_ok=True)
        for path, data in content.items():
            target = ROOT / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        write_json(LOCK, lock)
        write_json(ROOT / 'resources/catalogs/upstream-info.json',
                   {key: lock[key] for key in ['version', 'profileCount', 'repository', 'commit']})
        obsolete_path = ROOT / 'build/obsolete-files.json'
        obsolete = set(json.loads(obsolete_path.read_text(encoding='utf-8')))
        obsolete.update(item['source'] for item in lock['files'] if item['path'].startswith('legacy/flowseal/') and item['source'] != 'README.md')
        write_json(obsolete_path, sorted(obsolete))
        for script in ['tools/import_flowseal_strategies.py', 'tools/generate_engine_manifest.py']:
            subprocess.run([sys.executable, str(ROOT / script)], cwd=ROOT, check=True)
        vendor_readme = ROOT / 'vendor/zapret/README.md'
        vendor_readme.write_text(vendor_readme.read_text(encoding='utf-8').replace(previous['version'], tag), encoding='utf-8')
        if args.bump_version: bump_version(tag)
    summary = ROOT / 'artifacts/upstream-summary.md'
    summary.parent.mkdir(parents=True, exist_ok=True)
    summary.write_text(f'Flowseal {tag}\n\nPinned commit: {lock["commit"]}\n\n'
        f'Imported profiles: {lock["profileCount"]}. Files: {len(lock["files"])}.\n\n'
        'Updates only managed upstream components and generated catalogs. Existing compatibility overrides are retained. '
        'Application code and user lists are not imported from upstream.\n\n'
        f'Changelog: https://github.com/{REPOSITORY}/releases/tag/{tag}\n', encoding='utf-8')
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            output.write('changed=' + str(changed).lower() + '\n')
    print(('Prepared update to ' if changed else 'Already using ') + tag)

if __name__ == '__main__':
    main()
