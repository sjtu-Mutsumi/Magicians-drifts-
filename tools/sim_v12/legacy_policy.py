"""v1.2 public-information heuristic policy; no v1.1 weights or card lists."""
from collections import Counter
from .catalog import CARDS, ELEMENTS, ROUTES, SPECIAL, START, RANK
DRAW = frozenset(('速读', '掠影', '活水', '余火', '余烬引擎', '燃页', '火种库', '回流', '毒涌', '循毒', '枯潮', '狂风', '疾行', '穿云', '追云', '追风', '风刃', '风驰', '风痕回廊', '风之躯', '灵感涌现', '通晓', '翻阅', '禁咒目录', '开岩'))
SHUFFLE = frozenset(('重洗', '循环', '盾之共鸣', '余烬引擎', '灰烬重生'))
SEARCH = frozenset(('检索附魔', '寻风', '寻岩', '寻毒', '水之低语', '火种翻找', '大检索', '命运检索', '深流', '潮汐回廊', '岩核共鸣'))
SUPPORT = {'燃烧血统': ('压缩爆燃', '高温炉心', '狂暴', '爆燃', '熔盾烈焰'), '灰烬循环': ('余烬引擎', '灰烬重生', '回火', '余火', '灰烬账本', '火种库'), '余烬之核': ('焚心', '余烬心脏', '余烬护心', '高温炉心'), '生命之泉': ('奔流', '水幕', '穿潮', '活水', '潮声', '回响之潮'), '潮汐回响': ('回响之潮', '洪潮', '深渊', '静水', '泉心'), '血潮': ('血刃', '细流', '血怒', '血潮汹涌'), '反震岩核': ('万钧碎盾', '不坏壁垒', '回震岩甲', '回岩', '碎盾术', '山岳壁垒'), '壁垒之心': ('层岩结构', '山岳壁垒', '磐岩壁', '山岳'), '大地之握': ('裂岩护壁', '握盾', '盾脉', '层岩结构', '岩核重压'), '疾风之刃': ('烈风', '疾风连斩', '疾行', '追风', '风步'), '穿云箭': ('穿云破甲', '裂空穿云', '风暴', '裂风'), '风痕': ('风痕回廊', '风痕刻印', '风暴', '裂风', '疾风连斩', '狂风'), '毒爆': ('万毒爆', '毒潮', '固毒', '毒涌', '毒龙', '毒针'), '蚀命': ('万蚀', '腐息', '缓释毒素', '毒瓶'), '蚀骨': ('万毒蚀骨', '蚀骨之触', '疫病', '腐溃', '蚀创')}

def targets():
    result = {}
    for element in ELEMENTS:
        for route in ROUTES[element]:
            support = list(SUPPORT[route])
            extra = sorted((n for n, c in CARDS.items() if c.element == element and c.enchantment and (n not in support)), key=lambda n: (n in DRAW, n in SEARCH, CARDS[n].cost <= 1, CARDS[n].layers), reverse=True)
            learned = [SPECIAL[element], route] + support + extra[:4] + ['灵感涌现', '通晓', '盾之共鸣']
            result[route] = dict(element=element, route=route, entries=list(START) + list(dict.fromkeys(learned)))
    assert len(result) == 15
    assert all((n in CARDS for d in result.values() for n in d['entries']))
    return result
TARGETS = targets()
STYLES = {'balanced': dict(attack=1.0, defense=1.5, draw=20, search=8, shuffle=5, layer=12, curve=0.4, support=8, refresh=8), 'aggressive': dict(attack=1.6, defense=0.8, draw=14, search=6, shuffle=3, layer=10, curve=0.25, support=6, refresh=7), 'defensive': dict(attack=0.7, defense=2.4, draw=16, search=10, shuffle=6, layer=12, curve=0.4, support=8, refresh=9), 'tempo': dict(attack=1.0, defense=1.1, draw=26, search=8, shuffle=4, layer=13, curve=0.8, support=6, refresh=9), 'engine': dict(attack=0.8, defense=1.2, draw=36, search=16, shuffle=14, layer=10, curve=0.5, support=7, refresh=10), 'synergy': dict(attack=1.0, defense=1.5, draw=18, search=14, shuffle=7, layer=16, curve=0.35, support=16, refresh=8)}
STYLE_LABELS = {'balanced': '均衡', 'aggressive': '进攻', 'defensive': '防守', 'tempo': '节奏', 'engine': '运转', 'synergy': '协同'}

def choose_play(battle, i):
    s, enemy = (battle.players[i], battle.players[1 - i])
    style = STYLES[battle.styles[i]]
    affordable = [x for x in s.hand if battle.cost(s, x) <= s.energy]
    if not affordable:
        return None

    def score(x):
        c = CARDS[x.name]
        value = 10 + c.attack * style['attack'] + c.defense * style['defense']
        if x.uid not in s.seen:
            value += c.layers * style['layer']
        if c.kind == 'specialization':
            value += 150
        if c.kind == 'route_legendary':
            value += 80
        if x.name in DRAW:
            value += style['draw']
        if x.name in SEARCH:
            value += style['search']
        if x.name in SHUFFLE:
            value += style['shuffle']
        if x.name == '气旋护壁':
            value += 60 if s.hp < 300 else -8
        if x.name == '顺风' and s.counts['wind'] < 2:
            value -= 25
        if x.name == '战术':
            value -= 80
        if x.name in ('包扎', '坚壁', '狂战') and len(s.hand) <= 1:
            value -= 10
        if x.name == '泉眼' and s.hp * 2 <= s.max_hp:
            value += 10
        if x.name in ('万毒爆', '毒潮') and enemy.poison == 0:
            value -= 15
        return (value / max(1, battle.cost(s, x)) ** style['curve'], x.uid)
    return max(affordable, key=score)

def draft_value(player, name, public):
    c = CARDS[name]
    counts = Counter((CARDS[n].element for n, t in player.deck))
    style = STYLES[getattr(player, 'style', 'balanced')]
    focus = player.spec or max(ELEMENTS, key=lambda e: (counts[e], e == player.preference))
    route = player.route
    value = (c.attack * style['attack'] + c.defense * style['defense']) / ((c.cost + 1) / 2) / 3 + (7 if c.element == focus else 0 if c.element == 'generic' else -3)
    if not player.spec and c.enchantment and (c.element == focus):
        value += 3 + counts[focus] * 3
    if c.name in DRAW:
        value += style['draw'] / 5
    if c.name in SEARCH:
        value += style['search'] / 8
    if c.name in SHUFFLE and (not any((n in SHUFFLE for n, t in player.deck))):
        value += style['shuffle'] / 2
    if route and name in SUPPORT[route]:
        value += style['support']
    if c.kind == 'route_legendary':
        occupied = {p['route'] for p in public if p['pid'] != player.pid}
        if name in occupied:
            return -100
        value += 12 if not route else 8 if name in SUPPORT.get(route, ()) else -12
        value += sum((n in SUPPORT[name] for n, t in player.deck)) * 2
    value -= max(0, len(player.deck) - 18) * 0.6
    return value
