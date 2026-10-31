"""Generate presentation copy from approved names without changing simulator data."""
from pathlib import Path
import argparse
import json
import re

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'Data/Presentation/card-display.json'
ELEMENTS = ('fire', 'water', 'poison', 'wind', 'earth')


def build():
    cards = json.loads((ROOT / 'releases/v1.2/cards.json').read_text(encoding='utf-8'))['cards']
    names = {key: key for key in cards}
    for element in ELEMENTS:
        mapping = json.loads((ROOT / f'docs/cards-v1.2/{element}-name-map.json').read_text(encoding='utf-8'))['cards']
        expected = {key for key, card in cards.items() if card['element'] == element}
        if set(mapping) != expected:
            raise ValueError(f'{element}: approved name IDs do not match the current card set')
        for key, row in mapping.items():
            if row['original_name'] != key or not row['display_name'].strip():
                raise ValueError('Invalid display name: ' + key)
            names[key] = row['display_name']
    if len(set(names.values())) != len(names):
        raise ValueError('Display names must be unique')

    # Longest original IDs first, exactly one pass: e.g. 潮涌→回流 must not
    # subsequently become 观潮; 风痕刻印 must not match its 风痕 prefix.
    tokens = re.compile('|'.join(re.escape(key) for key in sorted(cards, key=lambda key: (-len(key), key))))

    def rules(text):
        def replace(match):
            key = match.group()
            # 风痕 is also a debuff. Only activation references name the card.
            if key == '风痕' and not text[:match.start()].endswith('激活'):
                return key
            return names[key]
        return tokens.sub(replace, text)

    return {
        'schema_version': 1,
        'version': '1.2',
        'note': '仅用于显示；键为原始运行ID。小字独立保存在docs/cards-v1.2，暂不进入局内。',
        'cards': {key: {'name': names[key], 'rules': rules(card['text'])} for key, card in cards.items()},
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Fail if the checked-in display data is stale')
    args = parser.parse_args()
    data = build()
    content = json.dumps(data, ensure_ascii=False, indent=2) + '\n'
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text(encoding='utf-8') != content:
            raise SystemExit('Card display data is stale; run python tools/build_v12_presentation.py')
    else:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        OUTPUT.write_text(content, encoding='utf-8')
    renamed = sum(key != row['name'] for key, row in data['cards'].items())
    print(f'V12_PRESENTATION_OK cards={len(data["cards"])} renamed={renamed} flavor_in_match=false')


if __name__ == '__main__':
    main()
