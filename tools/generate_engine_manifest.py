"""Keep the repository-layout engine manifest synchronized with the vendored binaries."""
import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--check', action='store_true')
args = parser.parse_args()
folder = ROOT / 'vendor/zapret'
hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest().upper() for p in sorted((folder / 'bin').iterdir()) if p.is_file()}
manifest = folder / 'engine-manifest.json'
if args.check:
    if json.loads(manifest.read_text(encoding='utf-8')) != hashes:
        raise SystemExit('Vendored engine manifest is out of date. Run tools/generate_engine_manifest.py.')
else:
    manifest.write_text(json.dumps(hashes, indent=2) + '\n', encoding='utf-8')
print(f'Verified {len(hashes)} vendored engine files.')
