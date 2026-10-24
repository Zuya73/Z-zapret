"""Validate repository paths and the relationship between solution, projects and resources."""
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]

def existing(path):
    if not path.is_file():
        raise ValueError(f'Missing referenced file: {path.relative_to(ROOT)}')

def verify():
    if list(ROOT.glob('*.bat')):
        raise ValueError('Legacy BAT files must not be stored at the repository root.')
    solution = ET.parse(ROOT / 'x2zapret.slnx')
    projects = [ROOT / node.attrib['Path'] for node in solution.iter('Project')]
    if len(projects) != 3 or len(set(projects)) != 3:
        raise ValueError('Solution must include the application, core and tests once each.')
    for project in projects:
        existing(project)
        document = ET.parse(project)
        for kind in ['ProjectReference', 'Content', 'Resource']:
            for node in document.iter(kind):
                existing(project.parent / node.attrib['Include'].replace('\\', '/'))
    for name in json.loads((ROOT / 'build/obsolete-files.json').read_text()):
        if name != 'README-DESKTOP.md':
            existing(ROOT / 'legacy/flowseal' / name)
    for name in ['Directory.Build.props', 'global.json', 'resources/catalogs/strategies.json',
                 'resources/catalogs/targets.json', 'build/package.ps1', 'build/installer.nsi',
                 'build/package-legacy.ps1', 'docs/USER-GUIDE.md', 'docs/BUILD.md']:
        existing(ROOT / name)
    print('Repository paths, three solution projects and package resources verified.')

if __name__ == '__main__':
    try:
        verify()
    except (OSError, ValueError, KeyError, ET.ParseError) as error:
        raise SystemExit(str(error)) from error
