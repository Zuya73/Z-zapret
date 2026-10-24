"""Generate exact installer ownership lists; preserve unrelated user files."""
from pathlib import Path
import json
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
version = ET.parse(root / 'Directory.Build.props').findtext('./PropertyGroup/Version')
if not version or not re.fullmatch(r'\d+\.\d+\.\d+', version):
    raise SystemExit('Desktop version must use major.minor.patch.')
published = root / 'artifacts/x2zapret'
if not (published / 'x2zapret.exe').is_file():
    raise SystemExit('Publish the application before building its installer.')

def safe_relative(path):
    relative = path.as_posix().replace('/', '\\').replace('$', '$$')
    if '"' in relative:
        raise SystemExit('Unsafe filename in published files.')
    return relative

lines = ['; Generated from the published file set.']
for file in sorted(published.rglob('*')):
    if file.is_file():
        lines.append(f'Delete "$INSTDIR\\{safe_relative(file.relative_to(published))}"')
for directory in sorted((p for p in published.rglob('*') if p.is_dir()), key=lambda p: len(p.parts), reverse=True):
    lines.append(f'RMDir "$INSTDIR\\{safe_relative(directory.relative_to(published))}"')
(root / 'artifacts/uninstall-files.nsh').write_text('\n'.join(lines) + '\n', encoding='utf-8')
(root / 'artifacts/version.nsh').write_text(f'!define APP_VERSION "{version}"\n', encoding='utf-8')

# Exact files shipped in 0.1/0.2. Never use wildcard or recursive deletion.
obsolete = json.loads((root / 'build/obsolete-files.json').read_text(encoding='utf-8'))
for name in obsolete:
    if Path(name).is_absolute() or '..' in Path(name).parts or any(c in name for c in ':*?"\r\n\\'):
        raise SystemExit(f'Unsafe obsolete filename: {name!r}')
obsolete = [Path(name) for name in obsolete]
cleanup = ['; Remove known obsolete launchers only; user data stays intact.']
cleanup += [f'Delete "$INSTDIR\\{safe_relative(path)}"' for path in sorted(obsolete)]
cleanup += ['RMDir "$INSTDIR\\utils"', 'RMDir "$INSTDIR\\.service"']
(root / 'artifacts/upgrade-cleanup.nsh').write_text('\n'.join(cleanup) + '\n', encoding='utf-8')
print(f'Installer tracks {sum(p.is_file() for p in published.rglob("*"))} owned files and {len(obsolete)} obsolete files.')
