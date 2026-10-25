"""Verify the pinned unmodified Microsoft DiskSpd x64 engine used only with owned files."""
import hashlib
import json
from pathlib import Path
import struct

root = Path(__file__).resolve().parents[1]
info = json.loads((root / 'vendor/diskspd/manifest.json').read_text())
data = (root / 'vendor/diskspd/bin/diskspd.exe').read_bytes()
assert len(data) == info['size'] and hashlib.sha256(data).hexdigest() == info['sha256'], 'DiskSpd integrity mismatch'
assert data[:2] == b'MZ' and struct.unpack_from('<H', data, struct.unpack_from('<I', data, 60)[0] + 4)[0] == 0x8664, 'DiskSpd is not an x64 PE'
assert (root / 'vendor/diskspd/LICENSE.txt').is_file(), 'DiskSpd MIT licence missing'
print('Verified Microsoft DiskSpd', info['version'], 'x64:', len(data), 'bytes')
