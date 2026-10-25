"""Copy resolved dependency licences and package source metadata into the Windows package."""
import json
from pathlib import Path
import shutil
import sys
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
out = Path(sys.argv[1]) / 'licenses' / 'hardware'
out.mkdir(parents=True, exist_ok=True)
for p in (root / 'vendor/hardware/licenses').glob('*.txt'):
    shutil.copyfile(p, out / p.name)
assets = json.loads((root / 'src/Zapret.Desktop/obj/project.assets.json').read_text(encoding='utf-8-sig'))
folders = [Path(p) for p in assets['packageFolders']]
records = []
for key, item in assets['libraries'].items():
    if item['type'] != 'package':
        continue
    package = next((folder / item['path'] for folder in folders if (folder / item['path']).is_dir()), None)
    if package is None:
        raise ValueError('Resolved package is missing: ' + key)
    spec = next(package.glob('*.nuspec'))
    doc = ET.parse(spec)
    node = lambda name: next((n for n in doc.iter() if n.tag.split('}')[-1] == name), None)
    license_node, source = node('license'), node('repository')
    record = {'package': key, 'license': license_node.text if license_node is not None else None,
              'source': source.attrib if source is not None else None}
    url = node('licenseUrl')
    if url is not None:
        record['licenseUrl'] = url.text
    records.append(record)
    names = [p for p in package.iterdir() if p.is_file() and any(x in p.name.lower() for x in ('license', 'notice', 'copying'))]
    if license_node is not None and license_node.attrib.get('type') == 'file':
        names.append(package / license_node.text)
    for p in set(names):
        if not p.is_file():
            raise ValueError('Missing package licence: ' + str(p))
        shutil.copyfile(p, out / (key.replace('/', '-') + '-' + p.name))
(out / 'resolved-packages.json').write_text(json.dumps(records, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
print('Packaged dependency licence metadata:', len(records))
