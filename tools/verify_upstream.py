"""Verify the exact managed upstream snapshot and the profile catalog."""
import json
from pathlib import Path
from sync_flowseal import blob, destination

ROOT = Path(__file__).resolve().parents[1]

def verify():
    lock = json.loads((ROOT / 'vendor/zapret/upstream.lock.json').read_text(encoding='utf-8'))
    if lock['schemaVersion'] != 1 or lock['repository'] != 'Flowseal/zapret-discord-youtube':
        raise ValueError('Invalid upstream lock.')
    seen = set()
    for item in lock['files']:
        if item['path'] != destination(item['source']) or item['path'].casefold() in seen:
            raise ValueError('Invalid or repeated managed path.')
        seen.add(item['path'].casefold())
        if blob((ROOT / item['path']).read_bytes()) != item['blob']:
            raise ValueError('Modified upstream file: ' + item['path'])
    profiles = list((ROOT / 'legacy/flowseal').glob('general*.bat'))
    if len(profiles) != lock['profileCount']:
        raise ValueError('Profile count differs from upstream lock.')
    info = json.loads((ROOT / 'resources/catalogs/upstream-info.json').read_text(encoding='utf-8'))
    if any(info[key] != lock[key] for key in ['version', 'commit', 'repository', 'profileCount']):
        raise ValueError('Runtime component metadata differs from the pinned snapshot.')
    print(f'Verified Flowseal {lock["version"]}: {len(lock["files"])} files, {len(profiles)} profiles.')

if __name__ == '__main__':
    verify()
