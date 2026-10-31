"""Trainable mechanism utility for drafting, ordering and revealed-card choices."""
from collections import Counter
from pathlib import Path
import json
from .catalog import CARDS, ELEMENTS
from .mechanics import static, dynamic, composition, SHUFFLES
INITIAL_WEIGHTS = dict(panel=0.36, layer=2.1, draw=3.6, fee=5.0, search=1.9, recycle=2.4, discard=0.8, future=1.6, passive=2.0, tempo=1.6, protection=0.8, thinning=1.0, lookahead=1.0)
DEFAULT = dict(kind='mechanism', weights=INITIAL_WEIGHTS, checkpoint='mechanism-initial')
path = Path(__file__).parent / 'rules/ai-policy.json'
if path.exists():
    DEFAULT = json.loads(path.read_text(encoding='utf-8'))

def config(obj, i=None):
    if i is None:
        return getattr(obj, 'policy', None) or DEFAULT
    return getattr(obj, 'policy_configs', (None, None))[i] or DEFAULT

def utility(c, f, w, style):
    return w['panel'] * (c.attack * style['attack'] + c.defense * style['defense']) + w['draw'] * f['draw'] * style['draw'] / 20 + w['fee'] * f['fee'] + w['search'] * f['search'] * style['search'] / 10 + w['recycle'] * (f['shuffle'] + f['recover']) + w['discard'] * f['discard'] + w['future'] * (f['future_draw'] + 1.3 * f['future_fee']) + w['passive'] * f['passive'] + w['tempo'] * f['delay'] + w['protection'] * (f['shield'] * 0.7 + f['heal'] * 0.5)

def effective_weights(cfg,element):
    w=cfg['weights']
    if cfg.get('kind')!='explorer':return w
    factors=cfg.get('element_weights',{}).get(element,{})
    return {k:v*factors.get(k,1.) for k,v in w.items()}

def cycle_options(b, s, affordable):
    """An AI may change action/end its turn rather than repeat a mana-positive
 economic cycle forever. No game effect or already planned card is truncated.
 Uses multisets, not the unrevealed order or RNG. Shared by both competitors.
 """
    if getattr(b, '_phase', '') != 'plan':
        return (affordable, None)
    key = (tuple(sorted((x.uid for x in s.hand))), tuple(sorted(Counter((x.name for x in s.pile)).items())), tuple(sorted((x.uid for x in s.discard))), tuple(sorted((x.uid for x in s.removed))), tuple(sorted(s.seen)), tuple(s.known_top), tuple(sorted(s.discounts.items())), min(4, s.spent), min(4, s.played), min(2, s.counts['wind']), min(2, s.counts['fire']), min(6, b.players[1 - s.seat].poison), min(50, s.tide), s.ember, s.fx['tailwind_used'], s.fx['tactic_used'], tuple((s.fx['discount_' + e] for e in ELEMENTS)))
    history = getattr(b, '_economic_visits', None)
    if history is None:
        history = {}
        b._economic_visits = history
    record = history.get(key)
    if record is None:
        record = dict(energy=s.energy, chosen=None, blocked=set())
        history[key] = record
    elif s.energy >= record['energy'] and record['chosen'] is not None:
        record['blocked'].add(record['chosen'])
        b.cycle_decisions[s.seat]['alternative_or_end'] += 1
    options = [x for x in affordable if x.uid not in record['blocked']]
    if affordable and (not options):
        b.cycle_decisions[s.seat]['voluntary_end'] += 1
    return (options, record)

def choose_play(b, i):
    from .policy import STYLES
    cfg = config(b, i)
    s = b.players[i]
    affordable = [x for x in s.hand if b.cost(s, x) <= s.energy]
    affordable, record = cycle_options(b, s, affordable)
    if not affordable:
        return None
    if cfg['kind'] == 'legacy':
        from .legacy_policy import choose_play as old
        hand = s.hand
        s.hand = list(affordable)
        try:
            chosen = old(b, i)
        finally:
            s.hand = hand
        if record is not None:
            record.update(energy=s.energy, chosen=chosen.uid if chosen else None)
        return chosen
    w = effective_weights(cfg,s.specialization)
    style = STYLES[b.styles[i]]
    bag = [x.name for x in s.pile] + [x.name for x in s.discard]
    known = {x.uid: x.name for x in s.instances}
    top = [known[uid] for uid in s.known_top if uid in known]
    hand_names = [x.name for x in s.hand]

    def score(x):
        c = CARDS[x.name]
        f = dynamic(b, s, x.name)
        cost = b.cost(s, x)
        after = s.energy - cost + f['fee']
        available = top[:max(1, int(f['draw']))] or bag
        playable = sum((CARDS[n].cost <= after for n in available)) / max(1, len(available))
        value = utility(c, f, w, style)
        value += w['layer'] * c.layers * (x.uid not in s.seen)
        if c.kind == 'specialization':
            value += 120 if not s.has(c.name) else 0
        elif c.kind == 'route_legendary':
            value += 65 if not s.has(c.name) else 0
        elif c.ability:
            value += 12
        value += w['lookahead'] * (f['draw'] * (playable * 5 - 2) + f['fee'] * min(3, len(s.hand)) * 1.2)
        if top and f['draw']:
            value += w['lookahead'] * sum((5 if CARDS[n].kind == 'specialization' else 2 + static(n)['fee'] for n in top[:max(1, int(f['draw']))]))
        if f['search']:
            follows = any((n != x.name and static(n)['draw'] and (CARDS[n].cost <= s.energy - cost) for n in hand_names))
            value += w['lookahead'] * (5 if follows else -2)
        if f['shuffle']:
            recycled = sum((static(a.name)['draw'] + static(a.name)['fee'] + (a.uid in s.seen) for a in s.discard))
            value += w['recycle'] * min(3, recycled / max(1, len(s.pile)))
            if not s.discard:
                value -= w['recycle'] * 1.5
            if top:
                value -= w['lookahead'] * 10
            if s.has('灰烬循环') and s.has('炽焰王冠'):
                value += 3
        if f['discard']:
            others = [a for a in s.hand if a.uid != x.uid]
            waste = sum((b.cost(s, a) > max(0, after) or a.name in ('攻击强化', '防御强化') for a in others))
            value += w['thinning'] * min(f['discard'], waste) * 3
            if not others:
                value -= 3
        if x.name == '顺风' and (not f['fee']):
            value -= 12
        if x.name == '战术' and s.energy:
            value -= 30 + 8 * sum((a.uid != x.uid and b.cost(s, a) <= s.energy for a in s.hand))
        if x.name in ('万毒爆', '毒潮') and (not b.players[1 - i].poison):
            value -= 10
        if x.name == '气旋护壁' and (not s.fx['lifeguard']):
            value += 10 if s.hp < 300 else 1
        return (value / max(1, cost) ** style['curve'], x.uid)
    chosen = max(affordable, key=score)
    if record is not None:
        record.update(energy=s.energy, chosen=chosen.uid)
    return chosen

def draft_value(p, name, public):
    from .policy import STYLES, SUPPORT
    cfg = config(p)
    if cfg['kind'] in ('thin','explorer'):
        from .construction import draft_quality
        return draft_quality(p,name,public)
    if cfg['kind'] == 'legacy':
        from .legacy_policy import draft_value as old
        return old(p, name, public)
    c = CARDS[name]
    w = effective_weights(cfg,p.spec or p.preference)
    style = STYLES[p.style]
    names = tuple((n for n, t in p.deck))
    counts = Counter((CARDS[n].element for n in names))
    focus = p.spec or max(ELEMENTS, key=lambda e: (counts[e], e == p.preference))
    f = static(name)
    value = utility(c, f, w, style) / ((c.cost + 1) / 2) ** 0.35
    value += 5 if c.element == focus else 0 if c.element == 'generic' else -4
    if not p.spec and c.enchantment and (c.element == focus):
        value += 3 + counts[focus] * 3
    value += w['layer'] * c.layers
    draw, fee, cheap = composition(names)
    if f['draw']:
        value += w['lookahead'] * (cheap + fee - draw * 0.8) * f['draw']
    if f['fee']:
        value += w['lookahead'] * min(2, draw) * f['fee'] * 2
    if f['search']:
        value += w['lookahead'] * (2 if draw > 0.35 else -0.5) * f['search']
    if f['discard']:
        value += w['thinning'] * (1 - cheap) * 2
    if f['shuffle'] and any((n in SHUFFLES for n in names)):
        value -= 2
    if p.route and name in SUPPORT[p.route]:
        value += style['support']
    if c.kind == 'route_legendary':
        occupied = {v['route'] for v in public if v['pid'] != p.pid}
        if name in occupied:
            return -100
        value += 12 if not p.route else -12
        value += sum((n in SUPPORT[name] for n in names)) * 2
    value -= max(0, len(names) - 18) * 0.7
    return value

def choose_cards(b, s, candidates, count, purpose):
    from .policy import STYLES
    cfg = config(b, s.seat)
    if cfg['kind'] == 'legacy':
        return sorted(candidates, key=lambda x: (b.rank_card(s, x), x.uid), reverse=purpose != 'discard')[:count]
    w = effective_weights(cfg,s.specialization)
    style = STYLES[b.styles[s.seat]]
    instant = purpose in ('draw', 'recover')
    budget = s.energy if instant else max(s.energy, 3 + s.pending['energy'])

    def score(x):
        c = CARDS[x.name]
        f = dynamic(b, s, x.name)
        value = utility(c, f, w, style)
        value += w['layer'] * c.layers * (x.uid not in s.seen)
        if c.kind == 'specialization' and (not s.has(c.name)):
            value += 70
        if c.kind == 'route_legendary' and (not s.has(c.name)):
            value += 40
        if c.cost > budget:
            value -= 14 + 3 * (c.cost - budget)
        if c.name in ('攻击强化', '防御强化') and x.uid in s.seen:
            value -= 4
        if instant and c.cost <= budget:
            value += f['fee'] * 3 + f['draw'] * 2
        return (value / max(1, c.cost) ** 0.4, x.uid)
    if purpose == 'discard':
        return sorted(candidates, key=score)[:count]
    if purpose == 'topdeck' and candidates is s.hand:
        return sorted(candidates, key=score)[:count]
    return sorted(candidates, key=score, reverse=True)[:count]
