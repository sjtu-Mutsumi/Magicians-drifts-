"""Explicit v1.2 effect registry; unknown names are fatal, never old fallbacks."""
from fractions import Fraction as Q
import math
from .catalog import CARDS, RANK, SHIELD_CARDS
from .model import ceil
EFFECTS = {}

def additive(s, key, value):
    """Temporary numerical effects add, including repeated copies at one launch."""
    s.fx[key] += value

def register(*names):

    def decorate(fn):
        for n in names:
            assert n in CARDS and n not in EFFECTS, n
            EFFECTS[n] = fn
        return fn
    return decorate

def simple(table, operation):
    for name, value in table.items():

        def effect(b, i, x, before, value=value):
            operation(b, b.players[i], value)
        register(name)(effect)
simple({'护盾': 60, '坚岩': 70}, lambda b, s, n: b.shield_gain(s, n))
simple({'引火': 5, '猛火弹': 20, '利刃': 10}, lambda b, s, n: additive(s, 'bonus', n))
simple({'短促': 5, '烈焰冲刺': 10, '风步': Q(15, 2)}, lambda b, s, n: additive(s, 'delay', -n))
simple({'速读': 1, '掠影': 1}, lambda b, s, n: b.draw(s, n))
simple({'破甲': 40}, lambda b, s, n: (additive(s, 'main_def_pen', n), additive(s, 'main_shield_pen', n)))
simple({'燎原': 0, '深渊潮': 0, '山崩': 0, '飓风': 0, '瘟潮': 0, '攻击强化': 0, '防御强化': 0}, lambda b, s, n: None)

@register('炽焰王冠', '潮汐之心', '不周山', '天岚', '万毒之源', '余烬之核', '灰烬循环', '燃烧血统', '潮汐回响', '生命之泉', '血潮', '反震岩核', '壁垒之心', '大地之握', '疾风之刃', '穿云箭', '风痕', '毒爆', '蚀命', '蚀骨', '守护者壁垒', '战神徽记')
def activate(b, i, x, pre):
    s = b.players[i]
    n = x.name
    s.active.add(n)
    if n == '炽焰王冠':
        s.fx['fire_activation_stage'] = s.stage
    elif n == '天岚':
        s.fx['shots'] = 0
    elif n in ('蚀骨',):
        b.operations.append(('decay_reschedule', 1 - i, i))

@register('不灭烈焰', '循毒')
def recover(b, i, x, pre):
    s, e = (b.players[i], b.players[1 - i])
    if x.name == '不灭烈焰':
        b.recover(s, 1, 'fire')
    elif b.plan_read(i, 'recover', e.poison >= 3 and any((CARDS[a.name].enchantment and CARDS[a.name].element == 'poison' for a in s.discard))):
        b.recover(s, 1, 'poison')
    else:
        b.draw(s, 1)

@register('余烬引擎', '灰烬重生', '盾之共鸣', '循环', '重洗')
def shuffle(b, i, x, pre):
    s = b.players[i]
    b.shuffle(s)
    if x.name == '余烬引擎':
        b.draw(s, 2)
    elif x.name == '灰烬重生':
        b.add_ember(s, 1)
    elif x.name == '盾之共鸣':
        b.shield_gain(s, 60)
    elif x.name == '循环':
        s.pending.update(draw=1, energy=1)

@register('余烬心脏', '余烬刻度', '余烬护心', '回火', '焚心', '灰烬账本')
def embers(b, i, x, pre):
    s = b.players[i]
    n = x.name
    if n == '余烬心脏':
        if b.plan_read(i, 'low_hp', s.hp * 10 <= s.max_hp * 3):
            b.add_ember(s, 2)
            b.heal(s, Q(s.max_hp - s.hp, 5))
        else:
            b.add_ember(s, 1)
            s.pending['draw'] += 1
    elif n == '余烬刻度':
        if b.plan_read(i, 'fire_gate', s.layers['fire'] >= 8):
            b.add_ember(s, 1)
        else:
            s.pending['draw'] += 1
    elif n == '余烬护心':
        if s.ember:
            b.consume_ember(s, 1)
            b.heal(s, Q((s.max_hp - s.hp) * 15, 100))
        else:
            b.shield_gain(s, 60)
    elif n == '回火':
        if s.fx['shuffled']:
            b.add_ember(s, 1)
    elif n == '焚心':
        if s.ember:
            for _ in range(min(2, s.ember)):
                b.consume_ember(s, 1)
                b.heal(s, Q(s.max_hp - s.hp, 5))
        else:
            b.add_ember(s, 1)
    elif n == '灰烬账本':
        if b.plan_read(i, 'ticket', s.spent_ember // 3 > s.fx['book_claims']):
            if s.spent_ember // 3 > s.fx['book_claims']:
                s.fx['book_claims'] += 1
                b.add_ember(s, 1)
        else:
            s.pending['draw'] += 2

@register('压缩爆燃', '爆燃', '导火索', '战意', '烈焰附身', '高温炉心', '熔盾烈焰')
def fire_modifiers(b, i, x, pre):
    s = b.players[i]
    n = x.name
    if n == '压缩爆燃':
        additive(s, 'bonus', 30 + 20 * (b.stats(i)[0] >= 135) + 30 * (s.ember >= 2))
    elif n == '爆燃':
        additive(s, 'bonus', 35 if pre['fire'] >= 2 else 25)
    elif n == '导火索':
        s.fx['discount_fire'] += 1
        additive(s, 'bonus', 15)
    elif n == '战意':
        if s.layers['fire'] >= 4:
            s.attack += 4
    elif n == '烈焰附身':
        b.temp(i, 'heal_block', 25, 1 - i)
    elif n == '高温炉心':
        s.fx['hot_core'] += 5
    elif n == '熔盾烈焰':
        s.fx['melt_shield'] += 1

@register('狂暴', '燃血', '燃页', '毒瓶', '血刃', '禁忌知识')
def payment(b, i, x, pre):
    s = b.players[i]
    n = x.name
    rate = Q(3 if n == '燃页' else 5, 100)
    paid = b.pay_life(s, rate)
    if n == '狂暴':
        additive(s, 'temp_attack', 30)
    elif n == '燃血':
        b.add_ember(s, 1)
        additive(s, 'bonus', 15)
    elif n == '燃页':
        b.draw(s, 1)
        additive(s, 'bonus', 10)
    elif n == '毒瓶':
        b.target_poison(i, 2)
    elif n == '血刃':
        s.attack += 6
        b.add_tide(s, Q(paid * s.layers['water'] * 6, 100))
    elif n == '禁忌知识':
        s.pending.update(draw=2, energy=1)

@register('火花', '轻羽', '毒引', '余火', '追云', '追风', '活水')
def first_and_draw(b, i, x, pre):
    s = b.players[i]
    n = x.name
    if n in ('火花', '轻羽'):
        if pre['first']:
            s.layers[CARDS[n].element] += 1
    elif n == '毒引':
        if pre['first'] and b.players[1 - i].poison:
            s.layers['poison'] += 1
    elif n in ('余火', '追云'):
        element = 'fire' if n == '余火' else 'wind'
        for inst in b.draw(s, 1 if n == '余火' else 2):
            if CARDS[inst.name].enchantment and CARDS[inst.name].element == element and (inst.uid not in s.seen):
                s.discounts[inst.uid] = s.discounts.get(inst.uid, 0) + 1
    elif n == '追风':
        drawn = b.draw(s, 1)
        if drawn and CARDS[drawn[0].name].enchantment and (CARDS[drawn[0].name].element == 'wind'):
            s.fx['permanent_delay'] -= 1
    elif n == '活水':
        healed = b.heal(s, 20)
        if b.plan_read(i, 'healed_draw', healed > 0):
            b.draw(s, 1)

@register('火种库', '火种翻找', '水之低语', '深流', '寻风', '寻毒', '寻岩', '穿云', '岩核共鸣', '命运检索', '大检索', '检索附魔', '禁咒目录')
def searches(b, i, x, pre):
    s = b.players[i]
    n = x.name
    if n == '火种库':
        b.search(s, lambda c: c.enchantment and c.element == 'fire', 4, 2, True, unseen_only=True, optional=True)
    elif n == '火种翻找':
        b.search(s, lambda c: c.enchantment and c.element == 'fire', 3, prioritize_unseen=True)
    elif n == '水之低语':
        b.search(s, lambda c: c.element == 'water', 2)
    elif n == '深流':
        if not b.search(s, lambda c: c.element == 'water' and c.rarity == 'legendary', 3):
            b.heal(s, 30)
    elif n in ('寻风', '寻毒'):
        b.search(s, lambda c: c.element == ('wind' if n == '寻风' else 'poison'), 2, optional=n == '寻风')
    elif n == '寻岩':
        b.search(s, lambda c: c.name in SHIELD_CARDS, 2, optional=True)
    elif n == '穿云':
        b.search(s, lambda c: c.element == 'wind', 3, instant=True, optional=True)
    elif n == '岩核共鸣':
        b.shield_gain(s, 100)
        b.search(s, lambda c: c.element == 'earth' and c.enchantment and (RANK[c.rarity] >= 1), 5, optional=True)
        s.pending['earth_discount'] += 1
    elif n in ('命运检索', '大检索'):
        b.search(s, lambda c: True, 5 if n == '命运检索' else 4, 2)
    elif n == '检索附魔':
        b.search(s, lambda c: c.enchantment, 2)
    elif n == '禁咒目录':
        b.search(s, lambda c: RANK[c.rarity] >= 2, 5, 2, True)

@register('回响之潮', '奔流', '水幕', '泉心', '洪潮', '深渊', '潮汐风暴', '血潮汹涌', '回流', '洪流', '涌泉', '潮声', '潮汐回廊', '疾流', '穿潮', '血怒', '连环潮', '静水', '泉眼', '潮涌', '细流')
def water(b, i, x, pre):
    s = b.players[i]
    n = x.name
    layers = s.layers['water']
    if n == '回响之潮':
        b.heal(s, 100)
        if s.has('潮汐之心'):
            additive(s, 'tide_bonus', 50)
            additive(s, 'tide_record', 50)
    elif n == '奔流':
        s.fx['flow'] += 1
    elif n == '水幕':
        b.heal(s, 50)
        additive(s, 'main_shield_pen', 70)
    elif n == '泉心':
        b.heal(s, Q(s.max_hp - s.hp, 5), bonus=100)
    elif n == '洪潮':
        b.add_tide(s, Q(s.tide * 3, 5) if s.tide else layers * 8)
    elif n == '深渊':
        s.fx['abyss'] += 1
    elif n == '潮汐风暴':
        amount = b.consume_tide(s, s.tide)
        if amount:
            b.operations.append(('packet', 1 - i, b.packet(i, Q(amount * 3, 2), 'water')))
    elif n == '血潮汹涌':
        b.add_tide(s, Q((s.max_hp - s.hp) * layers, 50) * (Q(3, 2) if s.hp * 100 <= s.max_hp * 40 else 1))
    elif n == '回流':
        if not b.search(s, lambda c: c.name == '潮汐之心', 1, instant=True):
            b.heal(s, 30)
    elif n == '洪流':
        if s.has('潮汐之心'):
            b.add_tide(s, layers * 10)
        else:
            b.heal(s, 60)
    elif n == '涌泉':
        b.heal(s, 50)
        s.fx['springs'] += 1
    elif n == '潮声':
        b.heal(s, 25)
        s.pending.setdefault('heal_batches', []).append(25)
    elif n == '潮汐回廊':
        if not b.search(s, lambda c: c.name == '潮汐之心', 1):
            b.search(s, lambda c: c.element == 'water' and c.rarity == 'legendary', len(s.pile))
        b.add_tide(s, layers * 8)
    elif n == '疾流':
        if b.plan_read(i, 'tide_gate', s.tide >= 30):
            b.consume_tide(s, 30)
            s.pending['energy'] += 2
        else:
            b.heal(s, 30)
    elif n == '穿潮':
        additive(s, 'pierce_tide', 1)
    elif n == '血怒':
        b.add_tide(s, layers * (12 if pre['hp'] * 2 <= s.max_hp else 8))
    elif n == '连环潮':
        s.fx['discount_water'] += 1
        b.heal(s, 20)
    elif n == '静水':
        if s.has('潮汐之心'):
            s.fx['still_water'] += 1
    elif n == '泉眼':
        b.heal(s, 50 if s.hp * 2 <= s.max_hp else 20)
    elif n == '潮涌':
        b.heal(s, 40)
        if s.has('潮汐之心'):
            additive(s, 'tide_shield', 50)
    elif n == '细流':
        b.heal(s, 30)
        s.fx['trickle'] += 1

@register('万钧碎盾', '碎盾术', '不坏壁垒', '回震岩甲', '山岳之躯', '山岳壁垒', '岩核重压', '岩潮壁垒', '层岩结构', '临时壁垒', '回岩', '守岩', '山岳', '岩拳', '握盾', '盾脉', '碎岩印记', '磐岩壁', '裂岩护壁', '岩甲', '磐石', '开岩')
def earth(b, i, x, pre):
    s = b.players[i]
    n = x.name
    if n in ('万钧碎盾', '碎盾术'):
        b.shield_gain(s, 120 if n == '万钧碎盾' else 80)
        b.flush_shields(s)
        if s.has('不周山'):
            consumed = b.take_shield(s, s.shield_total)
            packet = b.burst(i, consumed, Q(3, 2) if n == '万钧碎盾' else 1)
            if n == '万钧碎盾':
                packet['active_return'] = Q(consumed * 3, 10)
            b.operations.append(('packet', 1 - i, packet))
    elif n == '不坏壁垒':
        b.shield_gain(s, 220, no_decay=True)
    elif n == '回震岩甲':
        b.shield_gain(s, 100)
        s.fx['return_flat'] += 30
    elif n == '山岳之躯':
        used = b.take_shield(s, min(150, ceil(Q((s.max_hp - s.hp)*2, 3))))
        b.heal(s, used * Q(3,2))
    elif n == '山岳壁垒':
        s.fx['earth_extra'] += 30
    elif n == '岩核重压':
        s.fx['direct'] += Q(s.shield, 5)
    elif n == '岩潮壁垒':
        b.shield_gain(s, 140, temporary=True, conversion=Q(1, 2))
    elif n == '层岩结构':
        s.fx['shield_structure'] += 1
    elif n == '临时壁垒':
        b.shield_gain(s, 120, temporary=True)
    elif n == '回岩':
        b.shield_gain(s, 150)
        s.fx['return_once'] += 80
    elif n == '守岩':
        b.shield_gain(s, 80)
        s.fx['next_flat'] += 40
    elif n == '山岳':
        b.shield_gain(s, 200 if not pre['shield'] else 120)
    elif n == '岩拳':
        b.shield_gain(s, 60)
        s.fx['discount_earth'] += 1
    elif n == '握盾':
        b.shield_gain(s, 80)
        if s.has('大地之握'):
            additive(s, 'grip_bonus', 100)
    elif n == '盾脉':
        b.shield_gain(s, 80)
        b.flush_shields(s)
        s.fx['shield_pulse'] += 2
    elif n == '碎岩印记':
        b.shield_gain(s, 60)
        if (getattr(b, '_slot', None) or b.players)[i].fx['broken']:
            s.defense += 5
            b.shield_gain(s, 40, temporary=True)
    elif n == '磐岩壁':
        b.shield_gain(s, 60)
        additive(s, 'shield_leech', 30)
    elif n == '裂岩护壁':
        b.shield_gain(s, 40)
        s.fx['direct'] += b.flush_shields(s) * 2
    elif n == '岩甲':
        b.shield_gain(s, 10 + 2*pre['defense'])
    elif n == '磐石':
        b.shield_gain(s, 100)
        b.temp(i, 'atk_down', 20, 1 - i)
    elif n == '开岩':
        if b.plan_read(i, 'shield_gate', pre['total'] >= 30):
            b.take_shield(s, 30)
            b.draw(s, 1)
        else:
            b.shield_gain(s, 30)

@register('气旋护壁', '烈风', '疾风连斩', '穿云破甲', '裂空穿云', '风暴', '风痕回廊', '狂风', '疾行', '裂风', '顺风', '风刃', '风切', '风痕刻印', '风驰', '风眼')
def wind(b, i, x, pre):
    s = b.players[i]
    n = x.name

    def sub(fixed, ratio, count=1, marks=1, leech=False):
        s.subs.extend([(fixed, Q(ratio, 100), marks, leech)] * count)
    if n == '气旋护壁':
        s.fx['lifeguard'] = 1
    elif n == '烈风':
        s.fx['permanent_delay'] -= Q(5, 2)
    elif n == '疾风连斩':
        from .engine import Battle
        s.fx['permanent_delay'] -= 1
        # This card explicitly reduces its own interval before its threshold.
        # The general strict gap() reads Ck and would miss this local -1.
        sub(160 if Battle.gap(b, s) <= 25 else 70, 50, 2)
    elif n == '穿云破甲':
        additive(s, 'armor_break', 6)
    elif n == '裂空穿云':
        s.fx.setdefault('strip', []).append((Q(2, 5), 2))
    elif n == '风暴':
        s.permanent_subs.append((90, Q(3, 10), 1, False))
    elif n == '风痕回廊':
        sub(100, 30, 2, 2 if s.has('风痕') else 1)
        if b.plan_read(i, 'corridor_draw', not s.has('风痕')):
            b.draw(s, 2)
    elif n == '狂风':
        b.draw(s, 3)
        s.energy += 1
    elif n == '疾行':
        additive(s, 'delay', -10)
        b.draw(s, 1)
    elif n == '裂风':
        sub(70, 20, 2)
    elif n == '顺风':
        if pre['wind'] >= 2 and (not s.fx['tailwind_used']):
            s.energy += 1
            s.fx['tailwind_used'] = 1
    elif n == '风刃':
        b.draw(s, 1)
        if pre['first']:
            s.energy += 1
    elif n == '风切':
        sub(40, 50, leech=True)
    elif n == '风痕刻印':
        sub(60, 30, marks=2 if s.has('风痕') else 1)
        if not s.has('风痕'):
            additive(s, 'delay', -10)
    elif n == '风驰':
        reward = b.plan_read(i, 'wind_reward', 2 if b.gap(s) <= 30 else 1)
        b.draw(s, 2)
        s.energy += reward
    elif n == '风眼':
        b.temp(i, 'negative_guard', 1)

@register('万毒爆', '毒潮', '万蚀', '毒龙', '疫病', '蚀骨之触', '缓释毒素', '固毒', '枯潮', '毒囊', '毒涌', '爆裂', '腐息', '腐溃', '万毒蚀骨', '腐潮', '蚀创', '蚀刻', '毒针')
def poison(b, i, x, pre):
    s, e = (b.players[i], b.players[1 - i])
    n = x.name
    layers = e.poison
    if n in ('万毒爆', '毒潮'):
        consumed = layers if n == '万毒爆' else min(3, layers)
        if consumed:
            raw = b.stats(i)[0] * s.layers['poison'] * consumed * Q(4 if s.specialization == 'poison' else 2, 100) * (4 if n == '万毒爆' else Q(5, 2))
            packet = b.packet(i, raw, 'explosion', defense=b.stats(1 - i)[1])
            b.target_poison(i, -consumed)
            b.operations.append(('packet', 1 - i, packet))
    elif n == '万蚀':
        for packet in b.poison_packets(i):
            b.operations.append(('packet', 1 - i, packet))
    elif n == '毒龙':
        b.target_poison(i, 1)
    elif n == '疫病':
        b.temp(i, 'atk_down', math.floor(3 + Q(layers * 3, 2)), 1 - i)
    elif n == '蚀骨之触':
        b.operations.append(('def_down_permanent', 1 - i, math.floor(2 + Q(layers, 2))))
    elif n == '缓释毒素':
        s.fx['slow_poison'] += 1
        b.operations.append(('decay_reschedule', 1 - i, i))
    elif n == '固毒':
        s.fx['poison_guards'] += 1
    elif n == '枯潮':
        b.target_poison(i, 2)
        if b.plan_read(i, 'poison_fee', layers + 2 >= 3):
            s.energy += 1
        if b.plan_read(i, 'poison_draw', layers + 2 >= 6):
            b.draw(s, 1)
    elif n == '毒囊':
        b.shield_gain(s, 40 + 5 * layers)
    elif n == '毒涌':
        if b.plan_read(i, 'poison_draw', bool(layers)):
            b.target_poison(i, -1)
            b.draw(s, 2)
        else:
            b.draw(s, 1)
    elif n == '爆裂':
        additive(s, 'delay', -20 if layers >= 3 else -10)
    elif n == '腐息':
        s.fx['poison_repeat'] += 1
    elif n == '腐溃':
        additive(s, 'main_attack', 8 + 4 * layers)
    elif n == '万毒蚀骨':
        reductions = max(0, e.attack - b.stats(1 - i)[0]) + max(0, e.defense - b.stats(1 - i)[1])
        additive(s, 'main_attack', 4 * layers + Q(3, 2) * reductions)
    elif n == '腐潮':
        b.heal(s, (s.max_hp - s.hp) * min(Q(3, 10), Q(15 + layers, 100)))
    elif n == '蚀创':
        s.attack += layers
        s.defense += layers
    elif n == '蚀刻':
        b.temp(i, 'heal_block_max', 50 if layers >= 3 else 30, 1 - i)
    elif n == '毒针':
        s.pending['poison'] += 1

@register('风之躯', '风蚀')
def cleanse(b, i, x, pre):
    s = b.players[i]
    basis = (getattr(b, '_slot', None) or b.players)[i]
    kinds = {('heal_block' if m.kind == 'heal_block_max' else m.kind) for m in basis.mods if m.kind in {'atk_down','def_down','fear','heal_block','heal_block_max'}}
    if basis.wind_marks: kinds.add('wind_marks')
    cleared, _ = b.cleanse(s, poison=s.poison if x.name == '风之躯' else 3)
    if x.name == '风之躯':
        b.draw(s, 1)
    else:
        b.heal(s, (cleared + len(kinds)) * 20)

@register('时光倒流', '恐惧', '永恒壁垒', '灵感涌现', '通晓', '坚壁', '战术', '护盾强化', '断流封印', '狂战', '翻阅', '裂伤', '包扎', '预备', '换页', '整理', '蓄能')
def generic(b, i, x, pre):
    s = b.players[i]
    n = x.name
    if n == '时光倒流':
        s.rewind = (b.time, *b.stats(i), s.hp, s.shield_total)
    elif n == '恐惧':
        b.temp(i, 'fear', 1, 1 - i)
        s.pending['draw'] += 1
    elif n == '永恒壁垒':
        s.fx['eternal'] += 1
    elif n == '灵感涌现':
        b.draw(s, 2)
        s.energy += 1
    elif n == '通晓':
        b.draw(s, 2)
        b.move_hand(s, 1, 'discard')
    elif n == '坚壁':
        if b.move_hand(s, 1, 'discard'):
            b.shield_gain(s, 50)
    elif n == '战术':
        amount = s.energy
        s.energy = 0
        s.spent += amount
        if amount:
            s.pending['energy'] += amount + 1
        elif not s.fx['tactic_used']:
            s.energy = 1
            s.fx['tactic_used'] = 1
    elif n == '护盾强化':
        b.shield_gain(s, 40)
        b.move_hand(s, 1, 'topdeck')
    elif n == '断流封印':
        b.temp(i, 'heal_block', 25, 1 - i)
    elif n == '狂战':
        if b.move_hand(s, 1, 'discard'):
            b.draw(s, 1)
            additive(s, 'bonus', 10)
    elif n == '翻阅':
        b.draw(s, 2)
        s.pending['draw'] -= 1
    elif n == '裂伤':
        s.fx.setdefault('strip', []).append((Q(1, 5), 0))
    elif n == '包扎':
        if b.move_hand(s, 1, 'discard'):
            b.heal(s, 20)
    elif n == '预备':
        s.pending.update(draw=-1, energy=1)
    elif n == '换页':
        b.draw(s, 1)
        b.move_hand(s, 1, 'insert')
    elif n == '整理':
        s.pending.update(draw=2, energy=-1)
    elif n == '蓄能':
        if b.move_hand(s, 1, 'topdeck'):
            s.pending['draw'] += 1

def verify_registry():
    missing = CARDS.keys() - EFFECTS.keys()
    extra = EFFECTS.keys() - CARDS.keys()
    if missing or extra:
        raise RuntimeError(dict(missing=sorted(missing), extra=sorted(extra)))
    return len(EFFECTS)
verify_registry()
