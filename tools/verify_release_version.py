"""Reject changed application inputs if the already-built product version was reused."""
import json
from pathlib import Path
import xml.etree.ElementTree as ET
from root_executable import inputs_hash

root = Path(__file__).resolve().parents[1]
version = ET.parse(root / 'Directory.Build.props').findtext('.//Version')
receipt = root / 'build/root-executable.json'
if receipt.exists():
    previous = json.loads(receipt.read_text(encoding='utf-8'))
    if previous['version'] == version and previous['inputs_sha256'] != inputs_hash():
        raise SystemExit('Application components changed without a new product version. Bump Directory.Build.props and app.manifest before release.')
print('Application version is compatible with the tracked build receipt:', version)
