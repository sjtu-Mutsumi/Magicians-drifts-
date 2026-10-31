"""Mechanism features; own composition and revealed choices, no hidden order."""
from collections import Counter
from functools import lru_cache
import re
from .catalog import CARDS
from .panel_budget import role as panel_role
DRAW_N = {'速读': 1, '掠影': 1, '活水': 1, '余火': 1, '余烬引擎': 2, '燃页': 1, '火种库': 2, '回流': 1, '毒涌': 1.5, '循毒': 1, '枯潮': 0.5, '狂风': 3, '疾行': 1, '穿云': 1, '追云': 2, '追风': 1, '风刃': 1, '风驰': 2, '风痕回廊': 2, '风之躯': 1, '灵感涌现': 2, '通晓': 2, '翻阅': 2, '禁咒目录': 2, '开岩': 0.7, '狂战': 1, '换页': 1}
FEE_N = {'灵感涌现': 1, '狂风': 1, '风刃': 0.7, '风驰': 1.5, '顺风': 0.7, '枯潮': 0.6, '战术': 0.4}
SEARCH_N = {'检索附魔': 1, '寻风': 1, '寻岩': 1, '寻毒': 1, '水之低语': 1, '火种翻找': 1, '大检索': 2, '命运检索': 2, '深流': 1, '潮汐回廊': 1, '岩核共鸣': 1}
DISCARD_N = {'通晓': 1, '坚壁': 1, '包扎': 1, '狂战': 1, '换页': 1, '护盾强化': 1, '蓄能': 1}
SHUFFLES = {'重洗', '循环', '盾之共鸣', '余烬引擎', '灰烬重生'}
FUTURE = {'循环': (1, 1), '禁忌知识': (2, 1), '战神徽记': (0.8, 0), '守护者壁垒': (0, 0.25), '恐惧': (1, 0), '预备': (-1, 1), '整理': (2, -1), '蓄能': (1, 0), '翻阅': (-1, 0), '潮声': (0, 0), '疾流': (0, 1.2), '余烬心脏': (0.7, 0), '余烬刻度': (0.5, 0), '灰烬账本': (1.5, 0)}
PASSIVES = {'时光倒流': 5, '永恒壁垒': 2, '战神徽记': 3, '守护者壁垒': 1.3, '禁忌知识': 2, '风暴': 4, '余烬之核': 3, '灰烬循环': 4, '潮汐回响': 4, '生命之泉': 5, '血潮': 3, '反震岩核': 4, '壁垒之心': 4, '大地之握': 4, '疾风之刃': 4, '穿云箭': 4, '风痕': 4, '毒爆': 4, '蚀命': 4, '蚀骨': 4, '气旋护壁': 2, '层岩结构': 1.0, '山岳壁垒': 1.0, '涌泉': 1.2, '深渊': 1.5, '烈风': 1.5, '缓释毒素': 0.96}

@lru_cache(None)
def static(name):
    c = CARDS[name]
    t = c.text
    role = panel_role(name)
    shield = sum(map(int, re.findall('获得(\\d+)(?:普通|临时)?护盾', t)))
    heal = sum(map(int, re.findall('回复(\\d+)生命', t)))
    delay = sum(map(float, re.findall('(?:间隔减少|间隔永久减少)(\\d+(?:\\.\\d+)?)', t)))
    if name == '疾风之刃':delay = 0
    if name in ('短促', '烈焰冲刺', '风步', '疾行', '烈风'):
        delay = {'短促': 5, '烈焰冲刺': 10, '风步': 7.5, '疾行': 10, '烈风': 2.5}[name]
    return dict(draw=DRAW_N.get(name, 0), fee=FEE_N.get(name, 0), search=SEARCH_N.get(name, 0), discard=DISCARD_N.get(name, 0), shuffle=float(name in SHUFFLES), recover=float(name in ('不灭烈焰', '循毒')), future_draw=FUTURE.get(name, (0, 0))[0], future_fee=FUTURE.get(name, (0, 0))[1], passive=PASSIVES.get(name, 0), shield=shield / 50, heal=heal / 40, delay=delay / 5,panel_role=role)

@lru_cache(maxsize=20000)
def composition(names):
    counts = Counter(names)
    total = max(1, len(names))
    draw = sum((static(n)['draw'] * k for n, k in counts.items())) / total
    fee = sum((static(n)['fee'] * k for n, k in counts.items())) / total
    cheap = sum((k for n, k in counts.items() if CARDS[n].cost <= 1)) / total
    return (draw, fee, cheap)

def dynamic(b, s, name):
    f = dict(static(name))
    e = b.players[1 - s.seat]
    if name == '疾风之刃':
        f['delay'] = (4+s.layers['wind']/2)/5
    if name == '活水':
        f['draw'] = int(s.hp < s.max_hp)
    elif name == '毒涌':
        f['draw'] = 2 if e.poison else 1
    elif name == '枯潮':
        f['draw'] = int(e.poison + 2 >= 6)
        f['fee'] = int(e.poison + 2 >= 3)
    elif name == '开岩':
        f['draw'] = int(s.shield_total >= 30)
    elif name == '风驰':
        f['fee'] = 2 if b.gap(s) <= 30 else 1
    elif name == '风刃':
        f['fee'] = int(any((x.name == name and x.uid not in s.seen for x in s.hand)))
    elif name == '顺风':
        f['fee'] = int(s.counts['wind'] >= 2 and (not s.fx['tailwind_used']))
    elif name == '风痕回廊':
        f['draw'] = 0 if s.has('风痕') else 2
    elif name == '战术':
        f['fee'] = int(s.energy == 0 and (not s.fx['tactic_used']))
    elif name == '疾流':
        f['future_fee'] = 2 if s.tide >= 30 else 0
    elif name == '循毒':
        if e.poison >= 3 and any((CARDS[x.name].element == 'poison' and CARDS[x.name].enchantment for x in s.discard)):
            f['draw'] = 0
            f['recover'] = 1
        else:
            f['draw'] = 1
            f['recover'] = 0
    if name in ('不灭烈焰', '循毒') and (not s.discard):
        f['recover'] = 0
    return f
