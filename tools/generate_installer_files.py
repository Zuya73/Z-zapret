"""Generate the installer's exact list of owned files; never recursively remove the user's directory."""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
published = root / 'desktop/artifacts/Z-zapret'
if not (published / 'Z-zapret.exe').is_file():
    raise SystemExit('Publish the application before building its installer.')
lines = ['; Generated from the published file set.']
for file in sorted(published.rglob('*')):
    if file.is_file():
        relative = str(file.relative_to(published)).replace('/', '\\').replace('$', '$$')
        if '"' in relative:
            raise SystemExit('Unsafe filename in published files.')
        lines.append(f'Delete "$INSTDIR\\{relative}"')
for directory in sorted((p for p in published.rglob('*') if p.is_dir()), key=lambda p: len(p.parts), reverse=True):
    relative = str(directory.relative_to(published)).replace('/', '\\').replace('$', '$$')
    lines.append(f'RMDir "$INSTDIR\\{relative}"')
(root / 'desktop/artifacts/uninstall-files.nsh').write_text('\n'.join(lines) + '\n', encoding='utf-8')
print(f'Installer tracks {sum(p.is_file() for p in published.rglob("*"))} owned files.')
