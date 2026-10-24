"""Import every original BAT strategy as literal argv; never execute batch files.

Run from the repository root. --check verifies the committed catalog is current.
The original scripts remain the source of truth; repeated options and ! are significant.
"""
import argparse
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
DESTINATION = ROOT / "desktop/Zapret.Desktop/Assets/strategies.json"


def generate():
    profiles = []
    paths = sorted(ROOT.glob("general*.bat"), key=lambda p: (
        p.name != "general.bat",
        [int(x) if x.isdigit() else x for x in re.split(r"(\d+)", p.name)]))
    for path in paths:
        text = path.read_text(encoding="utf-8")
        marker = '"%BIN%winws.exe"'
        if text.count(marker) != 1:
            raise ValueError(f"Expected a single engine invocation: {path.name}")
        command = text.split(marker, 1)[1].replace("^\n", " ").replace("^!", "!").strip()
        token_pattern = r'(?:[^\s"]+|"[^"\n]*")+'
        tokens = re.findall(token_pattern, command)
        if re.sub(token_pattern, '', command).strip():
            raise ValueError(f"Unbalanced quotes in {path.name}")
        arguments = [x.replace('"', '').replace('%BIN%', '{BIN}').replace('%LISTS%', '{LISTS}')
                     .replace('%GameFilterTCP%', '{GAME_TCP}').replace('%GameFilterUDP%', '{GAME_UDP}')
                     for x in tokens]
        if not all(x.startswith('--') for x in arguments):
            raise ValueError(f"Unsupported command syntax in {path.name}")
        identifier = 'general' if path.stem == 'general' else re.sub('[^a-z0-9]+', '-', path.stem.lower()).strip('-')
        profiles.append(dict(id=identifier, name=path.stem.replace('general', 'Основная', 1),
                             source=path.name,
                             description='Экспериментальная стратегия' if '(EXP)' in path.name else
                             'Расширенный охват IPv4; используйте при необходимости' if '(ALT5)' in path.name else
                             'Оригинальный профиль Flowseal', arguments=arguments))
    return profiles


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true')
    options = parser.parse_args()
    catalog = generate()
    if options.check:
        if json.loads(DESTINATION.read_text(encoding='utf-8')) != catalog:
            raise SystemExit('The strategy catalog differs from original BAT profiles; regenerate it.')
    else:
        DESTINATION.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f'{len(catalog)} strategies verified')
