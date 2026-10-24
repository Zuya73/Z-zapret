"""Validate a self-contained root EXE and record reproducible source inputs for publication."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]

def inputs_hash():
    paths = ['src', 'resources', 'vendor/zapret', 'Directory.Build.props', 'global.json',
             'build/build-root.ps1', 'tools/root_executable.py']
    listing = subprocess.check_output(['git', 'ls-tree', '-r', 'HEAD', '--', *paths], cwd=ROOT)
    return hashlib.sha256(listing).hexdigest()

def validate(executable):
    data = Path(executable).read_bytes()
    if not data.startswith(b'MZ') or not 1_000_000 < len(data) < 100 * 1024 * 1024:
        raise ValueError('Root EXE must be a self-contained Windows executable smaller than 100 MiB.')
    icon = (ROOT / 'src/Zapret.Desktop/Assets/app.ico').read_bytes()
    _, kind, count = struct.unpack_from('<HHH', icon)
    if kind != 1 or count == 0:
        raise ValueError('Invalid source icon.')
    for index in range(count):
        size, offset = struct.unpack_from('<II', icon, 6 + index * 16 + 8)
        if icon[offset:offset + size] not in data:
            raise ValueError('Root executable is missing an X2 icon image.')
    return hashlib.sha256(data).hexdigest()

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('metadata', type=Path)
    args = parser.parse_args()
    metadata = {'version': ET.parse(ROOT / 'Directory.Build.props').findtext('./PropertyGroup/Version'),
                'inputs_sha256': inputs_hash(), 'exe_sha256': validate(args.executable),
                'built_from': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()}
    args.metadata.write_text(json.dumps(metadata, indent=2) + '\n', encoding='utf-8')
    print(f'Validated self-contained root EXE with embedded X2 icon: {args.executable.stat().st_size} bytes.')
