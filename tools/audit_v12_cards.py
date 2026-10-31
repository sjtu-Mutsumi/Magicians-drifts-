"""Static design audit; this does not execute v1.2 card effects."""
from pathlib import Path
from collections import Counter, defaultdict
import json
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
from tools.sim_v12.panel_budget import RULES, bounds, multiplier, budget_group, reference_rarity, value as panel_value
OUT = ROOT / 'research/v1.2-freeze-confirmation-20261029'

def main():
    cards = {}
    groups = {}
    values = defaultdict(list)
    problems = []
    rarity_names={'普通':'common','稀有':'rare','史诗':'epic','传奇':'legendary','专精':'specialization'}
    normalized = defaultdict(list)
    for element in ('fire', 'water', 'earth', 'wind', 'poison', 'generic'):
        rarity = None
        counts = Counter()
        for line in (ROOT / f'docs/cards-v1.2/{element}.md').read_text(encoding='utf-8').splitlines():
            match = re.match(r'## (专精|传奇|史诗|稀有|普通)', line)
            if match:
                rarity = match[1]
            if not re.match(r'^\|[^|]+\|\d+\|', line):
                continue
            cells = line.split('|')[1:-1]
            assert len(cells) == 6, line
            name = cells[0]
            assert name not in cards, name
            cost, attack, defense, layers = map(int, cells[1:5])
            value = panel_value(cost,attack,defense)
            lo, hi = bounds(name,rarity_names[rarity])
            if not lo <= value <= hi:
                problems.append(dict(name=name, value=float(value), expected=[float(lo),float(hi)]))
            values[rarity+'/'+budget_group(name)].append(float(value))
            normalized[reference_rarity(name,rarity_names[rarity])].append(float(value/multiplier(name)))
            counts[rarity] += 1
            cards[name] = dict(element=element, rarity=rarity, cost=cost,
                               attack=attack, defense=defense, layers=layers,
                               text=cells[5],panel_role=RULES['card_roles'][name],panel_budget_group=budget_group(name))
        groups[element] = dict(counts)
    baseline = json.loads((ROOT / 'releases/v1.1/cards.json').read_text(encoding='utf-8'))['cards']
    ratios = {key: max(v) / min(v) for key, v in values.items()}
    normalized_ratios={key:max(v)/min(v) for key,v in normalized.items()}
    assert len(cards) == 183, len(cards)
    assert cards.keys()==RULES['card_roles'].keys()
    assert not problems, problems
    assert all(r <= 1.2 + 1e-9 for r in ratios.values()), ratios
    assert all(r <= 1.2 + 1e-9 for r in normalized_ratios.values()),normalized_ratios
    result = dict(scope='static design only; runtime effects NOT verified',
                  count=len(cards), groups=groups, budget_problems=problems,
                  value_ratios=ratios, added=sorted(cards.keys() - baseline.keys()),
                  normalized_value_ratios=normalized_ratios,panel_role_counts=dict(Counter(RULES['card_roles'].values())),
                  retired=sorted(baseline.keys() - cards.keys()),
                  missing_starters=[n for n in ('攻击强化', '防御强化', '护盾', '检索附魔', '短促', '重洗') if n not in cards])
    OUT.mkdir(exist_ok=True, parents=True)
    (OUT / 'static-audit.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, ensure_ascii=True))

if __name__ == '__main__':
    main()
