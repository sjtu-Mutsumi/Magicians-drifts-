"""v1.2 instance-based, exact-arithmetic transaction simulator.

No runtime import, effect dispatch, or monkey patch from a previous version.
Both card actions project from one snapshot; opponent mutations commit after
both local projections. Missile waves likewise commit both directions together.
"""
from collections import Counter
from copy import deepcopy, copy
from fractions import Fraction as Q
import hashlib
import math
import json
from pathlib import Path
from .catalog import CARDS, ELEMENTS
from .model import Fighter, Modifier, PlannedCard, ceil

INF = 10**12
COMBAT_RULES=json.loads((Path(__file__).parent/'rules/combat.json').read_text(encoding='utf-8'))
FATIGUE_START_AV=COMBAT_RULES['fatigue_start_av']
NEGATIVE = {'atk_down', 'def_down', 'fear', 'heal_block', 'heal_block_max'}

class Battle:
    def __init__(self, decks, specializations=('', ''), seed=1, trace=False, chooser=None, styles=('balanced','balanced'), sequence_mode='live'):
        self.players = []
        for i, (deck, spec) in enumerate(zip(decks, specializations)):
            if any(n not in CARDS for n in deck):
                raise ValueError('Unknown v1.2 card')
            salt = int.from_bytes(hashlib.sha256(repr((seed, tuple(deck))).encode()).digest()[:8], 'big')
            self.players.append(Fighter(i, tuple(deck), spec, salt))
        assert len(self.players) == 2
        self.time = 0
        self.trace = trace
        self.log = []
        self.chooser = chooser
        self.operations = []
        self.executed = Counter()
        self.executed_by_seat = [Counter(),Counter()]
        self.flow_by_seat = [Counter(),Counter()]
        self.cycle_decisions = [Counter(),Counter()]
        self.projecting = False
        self.styles = tuple(styles)
        self.choice_records = None
        self.replay_decisions = None
        self.decision_cursors = {}
        self.sequence_skips = Counter()
        self.planned_count = 0
        if sequence_mode not in ('live','private_positions_experimental','locked'):
            raise ValueError('Unknown sequence rule')
        self.sequence_mode = sequence_mode

    def plan_read(self, i, key, value):
        return value

    def card_panel(self,s,c,first):
        s.attack += c.attack
        s.defense += c.defense
        if first:
            if c.element != 'generic':s.layers[c.element] += c.layers

    def finish_card_panel(self,s,c,first):
        pass

    def damage_snapshot(self):
        return deepcopy(self.players)

    def take_damage_shield(self,s,amount):
        return self.take_shield(s,amount)

    def state_changed(self, kind, seat):
        pass

    def begin_atomic(self):
        pass

    def end_atomic(self):
        pass

    def event(self, kind, seat, **data):
        if self.trace:
            def exact(value):
                if isinstance(value, Q):
                    return value.numerator if value.denominator == 1 else str(value)
                if isinstance(value, (tuple, list)):
                    return [exact(v) for v in value]
                if isinstance(value, dict):
                    return {k:exact(v) for k,v in value.items()}
                return value
            self.log.append(exact(dict(time=self.time, kind=kind, seat=seat, **data)))

    def stats(self, i):
        s, enemy = self.players[i], self.players[1-i]
        a = s.attack + s.fx['temp_attack'] + 3*s.fx['abyss']*(s.spent_tide//50)
        d = s.defense
        if not s.guarded:
            a -= s.value('atk_down')
            d -= s.value('def_down')+s.fx['permanent_def_down']
            if s.poison and enemy.has('万毒之源'):
                ac, dc = (Q(6, 5), Q(9, 10)) if enemy.has('蚀骨') else (Q(3, 5), Q(1, 2))
                a -= math.floor(5+ac*enemy.layers['poison']*s.poison)
                d -= math.floor(5+dc*enemy.layers['poison']*s.poison)
            factor = Q(4, 5)**int(s.value('fear'))
            a *= factor
            d *= factor
        return max(Q(0), a), max(Q(0), d)

    def fire_factor(self, s):
        return 1 + Q(s.layers['fire']*(30 if s.specialization == 'fire' else 15), 1000)

    def gap(self, s):
        fixed = 50+s.fx['permanent_delay']+s.fx['delay']
        if s.has('生命之泉'):
            fixed -= 5+s.layers['water']
        fixed -= s.layers['wind']*(1 if s.specialization == 'wind' else Q(1,2))
        if s.has('疾风之刃'):
            fixed -= 4+Q(s.layers['wind'], 2)
        interval_factor = max(Q(0), 1 + int(s.has('燃烧血统')) - Q(s.fx['flow'],20))
        return max(1, math.floor(fixed * interval_factor))

    def temp(self, i, kind, value, target=None):
        s = self.players[i]
        mod = Modifier(kind, Q(value), i, s.stage+1, (i,self.time,s.stage,s.played,kind))
        if target is None or target == i:
            s.mods.append(mod)
        else:
            self.operations.append(('modifier', target, mod))

    def add_ember(self, s, n):
        if s.has('炽焰王冠'):
            s.ember = min(5, s.ember+n)

    def consume_ember(self, s, n):
        n = min(n, s.ember) if s.has('炽焰王冠') else 0
        s.ember -= n
        for _ in range(n):
            s.spent_ember += 1
            if s.has('余烬之核'):
                self.heal(s, Q(s.max_hp-s.hp, 10))
            if s.has('灰烬循环') and s.fx['ash_reward'] < 3:
                s.pending['draw'] += 1
                s.pending['energy'] += 1
                s.fx['ash_reward'] += 1
        return n

    def add_tide(self, s, n):
        if s.has('潮汐之心'):
            s.tide += max(0, ceil(n))

    def consume_tide(self, s, n):
        amount = min(s.tide, n) if s.has('潮汐之心') else 0
        s.tide -= amount
        s.spent_tide += amount
        return amount

    def heal(self, s, amount, record=True, bonus=0):
        block = 0 if s.guarded else min(60, s.value('heal_block') + max((m.value for m in s.mods if m.kind == 'heal_block_max'), default=0))
        actual = min(max(0, ceil(amount*(1-block/100))), max(0, s.max_hp-s.hp))
        s.hp += actual
        if actual:
            if record and s.has('潮汐之心'):
                rate = 10 if s.has('潮汐回响') else 6
                self.add_tide(s, Q(actual*rate*s.layers['water'], 100)*(1+Q(bonus, 100)))
            if s.has('潮汐之心') and s.fx['still_water']:
                self.add_tide(s, s.fx['still_water']*s.layers['water']*6)
                s.fx['still_water'] = 0
        self.event('heal', s.seat, amount=actual)
        if actual:self.state_changed('hp', s.seat)
        return actual

    def shield_gain(self, s, amount, temporary=False, no_decay=False, conversion=0):
        if amount > 0:
            s.shield_events.append((Q(amount), temporary, no_decay, Q(conversion)))

    def flush_shields(self, s):
        events, s.shield_events = s.shield_events, []
        if not events:
            return 0
        ordinary = [x for x in events if not x[1]]
        decay = any(not x[2] for x in ordinary)
        loss = s.shield-ceil(Q(s.shield*3, 4)) if decay else 0
        if loss:
            s.shield -= loss
            if s.has('大地之握'):
                s.fx['grip_loss'] += loss
        amount = sum(x[0] for x in ordinary)
        if ordinary and s.fx['shield_pulse']:
            amount += 40
            s.fx['shield_pulse'] -= 1
        multiplier = 1 + Q(s.fx['shield_structure']*8,100)
        if ordinary:
            s.shield += ceil(amount*multiplier)
        for amount, temporary, _, conversion in events:
            if temporary:
                s.temporary.append([ceil(amount*multiplier), s.stage+1, conversion])
        self.event('shield', s.seat, ordinary=s.shield, temporary=sum(x[0] for x in s.temporary), decay=loss)
        self.state_changed('shield', s.seat)
        return loss

    def take_shield(self, s, n, ordinary_only=False):
        n = min(n, s.shield if ordinary_only else s.shield_total)
        left = n
        if not ordinary_only:
            for batch in s.temporary:
                use = min(left, batch[0])
                batch[0] -= use
                left -= use
        s.shield -= left
        if n:self.state_changed('shield', s.seat)
        return n

    def lose_life(self, s, amount, payment=False):
        amount = max(0, amount)
        if not payment:
            if s.guard_time == self.time:
                amount = min(amount, max(0, s.hp-1))
            elif amount >= s.hp and s.hp > 0 and s.fx['lifeguard'] and not s.guard_used:
                s.guard_used = True
                s.guard_time = self.time
                amount = max(0, s.hp-1)
        actual = min(max(0, s.hp), amount)
        s.hp -= amount
        if actual and s.fx['eternal']:
            self.shield_gain(s, actual*s.fx['eternal'])
        if amount:self.state_changed('hp', s.seat)
        return actual

    def pay_life(self, s, rate, from_card=True):
        paid = max(0, min(max(0, s.hp-1), ceil(s.hp*rate)))
        self.lose_life(s, paid, True)
        if paid and from_card and s.fx['trickle']:
            count, s.fx['trickle'] = s.fx['trickle'], 0
            self.heal(s, Q(paid*count, 2))
        return paid

    def poison_interval(self, source, layers):
        if layers <= 0 or layers == 1 and source.has('蚀骨'):
            return INF
        return max(1, ceil(max(1, 100//layers)*(1 + Q(source.fx['slow_poison'],5))))

    def change_poison(self, source, target, delta):
        old = target.poison
        target.poison = max(0, old+delta)
        if source.has('毒爆'):
            source.attack += 3*abs(target.poison-old)
        if target.poison != old:
            target.poison_start = self.time
            target.poison_due = self.time+self.poison_interval(source, target.poison)
        self.event('poison', target.seat, before=old, after=target.poison)

    def target_poison(self, i, delta):
        self.operations.append(('poison', 1-i, (i, delta)))

    def cleanse(self, s, poison=0):
        kinds = NEGATIVE
        removed = {m.kind for m in s.mods if m.kind in kinds}
        s.mods = [m for m in s.mods if m.kind not in kinds]
        s.wind_marks = 0
        actual = min(s.poison, poison)
        if actual:
            # Opponent's growth must commit after its own local card projection.
            self.change_poison(self.players[1-s.seat], s, -actual)
            if self.projecting and self.players[1-s.seat].has('毒爆'):
                self.operations.append(('poison_growth', 1-s.seat, actual))
        return actual, len(removed)

    def rank_card(self, s, inst):
        from .policy import STYLES
        c = CARDS[inst.name]
        style=STYLES[self.styles[s.seat]]
        return (100 if c.kind == 'specialization' and not s.has(c.name) else 65 if c.kind == 'route_legendary' else 0) + c.layers*8 + c.attack*style['attack'] + c.defense*style['defense'] - c.cost*3

    def choose(self, s, candidates, count, purpose, optional=False):
        # The callback receives only the legally revealed candidates and public
        # own resources, never an enemy deck, unseen cards or random state.
        if self.replay_decisions is not None:
            script=self.replay_decisions.get(s.seat,())
            cursor=self.decision_cursors.get(s.seat,0)
            while cursor<len(script) and script[cursor][0]!=purpose:
                cursor+=1
            ids=script[cursor][1] if cursor<len(script) else ()
            self.decision_cursors[s.seat]=cursor+1
            legal={x.uid:x for x in candidates}
            chosen=[legal[uid] for uid in ids if uid in legal][:count]
            if len(chosen)!=len(ids):
                self.sequence_skips['choice_target_unavailable']+=len(ids)-len(chosen)
            return chosen
        if self.chooser:
            visible = tuple({'uid': x.uid, 'name': x.name, 'cost': CARDS[x.name].cost} for x in candidates)
            ids = self.chooser(dict(hp=s.hp, energy=s.energy, specialization=s.specialization, optional=optional), visible, count, purpose)
            chosen = [next(x for x in candidates if x.uid == uid) for uid in ids]
            if len({x.uid for x in chosen}) != len(chosen) or len(chosen) > min(count, len(candidates)) or not optional and len(chosen) != min(count, len(candidates)):
                raise ValueError('Invalid revealed-card choice')
        else:
            from .policy import choose_cards
            chosen=choose_cards(self,s,candidates,count,purpose)
        if self.choice_records is not None:
            self.choice_records.setdefault(s.seat,[]).append((purpose,tuple(x.uid for x in chosen)))
        return chosen

    def leave_hand(self, s, x):
        s.hand.remove(x)
        s.discounts.pop(x.uid, None)

    def random_insert(self, s, instances):
        if instances:s.known_top.clear()
        for x in instances:
            s.discounts.pop(x.uid, None)
            s.pile.insert(s.rng.randrange(len(s.pile)+1), x)
            self.event('random_insert', s.seat, uid=x.uid)

    def shuffle(self, s):
        s.known_top.clear()
        s.pile.extend(s.discard)
        s.discard.clear()
        s.rng.shuffle(s.pile)
        s.fx['shuffled'] += 1
        if s.has('灰烬循环'):
            self.add_ember(s, 2)
        self.event('shuffle', s.seat)

    def draw(self, s, count):
        drawn = []
        for _ in range(max(0, count)):
            if not s.pile and s.discard:
                self.shuffle(s)
            if not s.pile:
                break
            x = s.pile.pop(0)
            if s.known_top and s.known_top[0]==x.uid:s.known_top.pop(0)
            else:s.known_top.clear()
            s.hand.append(x)
            s.drawn.add(x.uid)
            drawn.append(x)
            self.event('draw', s.seat, uid=x.uid, name=x.name)
        return drawn

    def search(self, s, predicate, sample, count=1, instant=False, prioritize_unseen=False, unseen_only=False, optional=False):
        legal = [x for x in s.pile if predicate(CARDS[x.name]) and (not unseen_only or x.uid not in s.seen)]
        if prioritize_unseen:
            fresh = [x for x in legal if x.uid not in s.seen]
            rest = [x for x in legal if x.uid in s.seen]
            candidates = s.rng.sample(fresh, min(sample, len(fresh)))
            candidates += s.rng.sample(rest, min(sample-len(candidates), len(rest)))
        else:
            candidates = s.rng.sample(legal, min(sample, len(legal)))
        chosen = self.choose(s, candidates, count, 'draw' if instant else 'topdeck', optional)
        for x in chosen:
            s.pile.remove(x)
            if x.uid in s.known_top:s.known_top.remove(x.uid)
        if instant:
            s.hand.extend(chosen)
            s.drawn.update(x.uid for x in chosen)
            for x in chosen:
                self.event('draw', s.seat, uid=x.uid, name=x.name, directed=True)
        else:
            s.pile[0:0] = chosen
            s.known_top[0:0]=[x.uid for x in chosen]
        return chosen

    def move_hand(self, s, count, mode):
        chosen = self.choose(s, s.hand, count, 'discard' if mode == 'discard' else 'topdeck')
        for x in chosen:
            self.leave_hand(s, x)
        if mode == 'discard':
            s.discard.extend(chosen)
        elif mode == 'topdeck':
            s.pile[0:0] = chosen
            s.known_top[0:0]=[x.uid for x in chosen]
        elif mode == 'insert':
            self.random_insert(s, chosen)
        return len(chosen)

    def recover(self, s, count, element=None):
        legal = [x for x in s.discard if CARDS[x.name].enchantment and (element is None or CARDS[x.name].element == element)]
        chosen = self.choose(s, legal, count, 'recover')
        for x in chosen:
            s.discard.remove(x)
            s.hand.append(x)
        return chosen

    def cost(self, s, inst):
        c = CARDS[inst.name]
        discount = s.discounts.get(inst.uid, 0)
        if c.enchantment:
            discount += s.fx['discount_'+c.element]
        if c.element == 'earth' and c.ability:
            discount += s.fx['earth_ability_discount']
        return max(0, c.cost-discount)

    def play_local(self, i, uid):
        from .effects import EFFECTS
        s = self.players[i]
        x = next(x for x in s.hand if x.uid == uid)
        c = CARDS[x.name]
        cost = self.cost(s, x)
        if cost > s.energy:
            raise ValueError('Unaffordable card')
        before = dict(hp=s.hp, shield=s.shield, total=s.shield_total, defense=self.stats(i)[1],
                      spent=s.spent, played=s.played, fire=s.counts['fire'], wind=s.counts['wind'],
                      first=x.uid not in s.seen)
        s.energy -= cost
        s.spent += cost
        self.leave_hand(s, x)
        s.resolving.append(x)
        if c.enchantment:
            s.fx['discount_'+c.element] = 0
        if c.element == 'earth' and c.ability:
            s.fx['earth_ability_discount'] = 0
        self.card_panel(s,c,before['first'])
        if before['first']:
            s.seen.add(x.uid)
        previous_springs = s.fx['springs']
        EFFECTS[c.name](self, i, x, before)
        if c.name in ('炽焰王冠', '灰烬循环'):
            counterpart = '灰烬循环' if c.name == '炽焰王冠' else '炽焰王冠'
            targets = [a for a in s.pile if a.name == counterpart and a.uid not in s.drawn]
            if targets:
                s.pile.remove(targets[0])
                s.pile.insert(0, targets[0])
                if targets[0].uid in s.known_top:s.known_top.remove(targets[0].uid)
                s.known_top.insert(0,targets[0].uid)
        if c.enchantment and c.element == 'water':
            for _ in range(previous_springs):
                self.heal(s, 20)
        self.finish_card_panel(s,c,before['first'])
        s.played += 1
        if c.enchantment:
            s.counts[c.element] += 1
        s.resolving.remove(x)
        (s.removed if c.ability else s.discard).append(x)
        self.flush_shields(s)
        self.executed[c.name] += 1
        self.event('play', i, name=c.name, uid=x.uid, cost=cost, energy=s.energy)

    def card_step(self, actions):
        """actions maps actor to a currently affordable stable instance UID."""
        snapshot = deepcopy(self.players)
        projections, by_actor, logs = {}, {}, {}
        prefix, executed = self.log, self.executed
        def project(i, uid):
            self.players = deepcopy(snapshot)
            self.operations = []
            self.log = []
            self.executed = Counter()
            self.projecting = True
            self.decision_cursors[i]=0
            self.play_local(i, uid)
            projections[i] = self.players[i]
            by_actor[i] = self.operations
            logs[i] = self.log
        for i, uid in sorted(actions.items()):
            project(i, uid)
        operations = [op for i in sorted(actions) for op in by_actor[i]]
        self.log = prefix+[entry for i in sorted(actions) for entry in logs[i]]
        self.executed = executed
        for i,uid in actions.items():
            name=next(x.name for x in snapshot[i].hand if x.uid==uid)
            self.executed[name] += 1
            self.executed_by_seat[i][name] += 1
        self.players = snapshot
        self.projecting = False
        for i, projected in projections.items():
            self.players[i] = projected
        self.operations = []
        packets = []
        for kind, target, value in operations:
            s = self.players[target]
            if kind == 'modifier':
                s.mods.append(value)
            elif kind == 'poison':
                source, delta = value
                self.change_poison(self.players[source], s, delta)
            elif kind == 'poison_growth':
                if s.has('毒爆'):
                    s.attack += 3*value
            elif kind == 'decay_reschedule':
                source = self.players[value]
                s.poison_due = max(self.time+1, s.poison_start+self.poison_interval(source, s.poison))
            elif kind == 'def_down_permanent':
                s.fx['permanent_def_down'] += value
            elif kind == 'packet':
                packets.append(value)
            else:
                raise ValueError(('Unknown operation', kind))
        if packets:
            self.wave(packets)
        self.finish_transaction()

    def packet(self, i, raw, kind='missile', **kw):
        s = self.players[i]
        if self.projecting:
            kw.setdefault('defense', self.stats(1-i)[1])
        if kind in ('poison', 'explosion'):
            kw.setdefault('poison_suppressed', self.players[1-i].guarded)
        return dict(source=i, raw=Q(raw), kind=kind, final=self.fire_factor(s), **kw)

    def poison_packets(self, i):
        s, e = self.players[i], self.players[1-i]
        if not e.poison or not s.layers['poison'] or e.guarded:
            return []
        raw = self.stats(i)[0]*s.layers['poison']*e.poison*Q(4 if s.specialization == 'poison' else 2, 100)
        if s.has('蚀命'):
            target_hp = max(0, (getattr(self, '_slot', None) or self.players)[1-i].hp)
            raw += Q(target_hp*s.layers['poison']*e.poison, 100)
        repeat, s.fx['poison_repeat'] = s.fx['poison_repeat'], 0
        return [self.packet(i, raw, 'poison') for _ in range(1+repeat)]

    def burst(self, i, shield, multiplier=1):
        s = self.players[i]
        raw = max(Q(0), shield*s.layers['earth']*(Q(35, 1000) if s.has('反震岩核') else Q(25, 1000))-self.stats(1-i)[1]/2)
        return self.packet(i, raw*multiplier, 'burst', defense_done=True)

    def wave(self, packets, reaction=False):
        """Lock both incoming amounts before applying life/shield changes."""
        snap = self.damage_snapshot()
        results = []
        for p in packets:
            i = p['source'];s, e = snap[i], snap[1-i]
            defense = p.get('defense', self.stats(1-i)[1])
            raw = p['raw']
            kind = p['kind']
            ordinary = kind == 'missile'
            if ordinary:
                defense *= max(Q(0), 1-p.get('def_pen', Q(0)))
                defense *= 1-p.get('ignore', Q(0))
                raw -= defense
            elif kind in ('poison', 'explosion'):
                raw -= defense/2
            elif kind == 'water':
                raw -= defense
            reduction = 15 if ordinary and e.has('壁垒之心') and e.shield_total else 0
            if ordinary and p.get('main') and e.has('血潮'):
                reduction += Q(30*max(0,e.hp),e.max_hp)
            flat = e.fx['next_flat'] if ordinary else 0
            amount = math.floor(max(Q(0), raw-flat)*p['final']*max(Q(0), 1-Q(reduction, 100)))
            amount = max(0 if kind == 'burst' else 1, amount)
            if kind in ('poison', 'explosion') and p.get('poison_suppressed', e.guarded):
                amount = 0
            if kind in ('true', 'poison', 'explosion', 'fatigue'):
                pen = Q(1)
            else:
                pen = p.get('pen', Q(0))
            direct = amount*pen
            covered = amount-direct
            shield_factor = p.get('shield_factor', Q(1))
            absorbed = min(e.shield_total, math.floor(covered*shield_factor))
            # Shield-only amplification cannot become amplified overflow life.
            overflow = max(Q(0), covered-Q(absorbed, 1)/shield_factor)
            life = math.floor(direct+overflow)
            actual = min(max(0, e.hp), life)
            results.append((p, amount, absorbed, life, actual, e.shield_total))
        reflected, heals = [], []
        self.begin_atomic()
        for p, amount, absorbed, life, _, old_shield in results:
            i = p['source'];s, e = self.players[i], self.players[1-i]
            self.take_damage_shield(e, absorbed)
            actual = self.lose_life(e, life)
            if p.get('active_return') and e.hp > 0:
                self.shield_gain(s, p['active_return'])
            ordinary = p['kind'] == 'missile'
            hit = absorbed+actual > 0
            if ordinary:
                e.fx['next_flat'] = 0
                if e.fx['shield_leech']:
                    heals.append((e.seat, absorbed*e.fx['shield_leech']/100, 0))
                    e.fx['shield_leech'] = 0
            if ordinary and p.get('lifesteal', True):
                basis, rate = actual, 6 if s.specialization == 'water' else 3
                if s.layers['water']:
                    heals.append((i, Q(basis*rate*s.layers['water'], 100), p.get('record_bonus', 0)))
                if p.get('leech'):
                    heals.append((i, Q(actual, 2), 0))
            if hit and p.get('armor_break'):
                self.operations.append(('def_down_permanent', 1-i, p['armor_break']))
            if hit and ordinary and s.has('天岚') and s.has('风痕'):
                e.wind_marks += p.get('marks', 1)
            if p.get('main'):
                s.last_main = absorbed+actual
                if actual and s.layers['poison']:
                    self.change_poison(s, e, 1)
                if s.has('余烬之核') and amount >= 140:
                    self.add_ember(s, 1)
            broken = old_shield > 0 and absorbed >= old_shield
            if not reaction and absorbed and e.has('不周山'):
                if ordinary and e.has('壁垒之心'):
                    raw = max(Q(0), Q(absorbed*e.layers['earth']*3, 200)-self.stats(i)[1]/2)
                    reflected.append(self.packet(1-i, raw, 'burst'))
                elif broken:
                    reflected.append(self.burst(1-i, old_shield))
            if broken:
                e.fx['broken'] = 1
                gain = e.fx['return_flat']+e.fx['return_once']
                e.fx['return_once'] = 0
                if e.has('不周山') and e.has('反震岩核'):
                    gain += Q(old_shield*2, 5)
                self.shield_gain(e, gain)
            self.event('damage', i, target=1-i, damage_kind=p['kind'], amount=amount, shield=absorbed, life=actual)
        for kind, target, value in self.operations:
            assert kind == 'def_down_permanent'
            self.players[target].fx['permanent_def_down'] += value
        self.operations = []
        self.end_atomic()
        if reflected:
            self.wave(reflected, reaction=True)
        for seat, amount, bonus in heals:
            self.heal(self.players[seat], amount, bonus=bonus)
        for s in self.players:
            self.flush_shields(s)

    def finish_transaction(self):
        self.auto_burst()
        for s in self.players:
            if s.hp <= 0 and s.rewind and not s.revived and self.time <= s.rewind[0]+200:
                _, a, d, hp, shield = s.rewind
                s.revived = True
                # Restore effective values without reapplying pre-snapshot deltas.
                s.attack, s.defense, s.hp, s.shield = a, d, hp, shield
                s.fx['temp_attack'] = 0
                s.fx['abyss'] = 0
                s.fx['permanent_def_down'] = 0
                s.temporary = []
                self.cleanse(s, s.poison)
                self.event('revive', s.seat)
        for s in self.players:
            self.flush_shields(s)
            s.assert_instances()

    def auto_burst(self):
        packets = []
        committed = []
        for i, s in enumerate(self.players):
            e = self.players[1-i]
            if s.hp <= 0 or e.hp <= 0 or not s.has('不周山') or not s.shield_total:
                continue
            packet = self.burst(i, s.shield_total)
            amount = max(0, math.floor(packet['raw']*packet['final']))
            if max(0, amount-e.shield_total) >= e.hp:
                packets.append(packet)
                committed.append(i)
        for i in committed:
            s = self.players[i]
            self.take_shield(s, s.shield_total)
            s.defense = 0
        if packets:
            self.wave(packets, reaction=True)

    def begin_stage(self):
        # Poison injection -> regular poison -> natural decay -> expiry -> draw.
        for i, s in enumerate(self.players):
            if s.pending['poison']:
                self.change_poison(s, self.players[1-i], s.pending['poison'])
                s.pending['poison'] = 0
        packets = []
        if self.time:
            for i in range(2):
                packets.extend(self.poison_packets(i))
        if packets:
            self.wave(packets)
            self.finish_transaction()
            if any(s.hp <= 0 for s in self.players):
                return
        self.decay()
        self.finish_transaction()
        if any(s.hp <= 0 for s in self.players):
            return
        for i, s in enumerate(self.players):
            s.stage += 1
            for target in self.players:
                target.mods = [m for m in target.mods if not (m.owner == i and m.expires <= s.stage)]
            old_shield = s.shield_total
            converted = sum(batch[0]*batch[2] for batch in s.temporary if batch[1] <= s.stage)
            s.temporary = [batch for batch in s.temporary if batch[1] > s.stage]
            if s.shield_total != old_shield:self.state_changed('shield', i)
            self.shield_gain(s, converted)
            for key in ('lifeguard', 'eternal', 'earth_ability_discount'):
                s.fx[key] = 0
            for key in ('shuffled', 'ash_reward', 'trickle', 'springs', 'tactic_used', 'tailwind_used'):
                s.fx[key] = 0
            s.drawn.clear();s.discounts.clear();s.counts.clear()
            s.spent = s.played = 0
            for e in ELEMENTS:
                s.fx['discount_'+e] = 0
            for amount in s.pending.get('heal_batches', []):
                self.heal(s, amount)
            s.fx['earth_ability_discount'] = s.pending['earth_discount']
            extra = s.pending['energy']
            s.energy = max(0, 3+extra)
            count = max(0, 5+s.pending['draw'])
            s.pending.clear()
            self.flush_shields(s)
            self.draw(s, count)

    def decay(self):
        changes = []
        for i, target in enumerate(self.players):
            source = self.players[1-i]
            if target.poison_due > self.time or not target.poison:
                continue
            if target.poison == 1 and source.has('蚀骨'):
                target.poison_due = INF
                continue
            changes.append((i, bool(source.fx['poison_guards'])))
        for i, guarded in changes:
            target, source = self.players[i], self.players[1-i]
            if guarded:
                source.fx['poison_guards'] -= 1
                self.change_poison(source, target, 1)
            else:
                self.change_poison(source, target, -1)

    def end_stage(self):
        for s in self.players:
            self.flow_by_seat[s.seat]['unused_energy']+=s.energy
            self.flow_by_seat[s.seat]['stages']+=1
            unused, s.hand = list(s.hand), []
            self.random_insert(s, unused)
            s.discounts.clear()
            s.energy = 0
            if s.has('守护者壁垒') and s.played <= 2:
                s.pending['energy'] += 1
            if s.has('战神徽记') and s.played >= 3:
                s.pending['draw'] += 1
            if s.has('炽焰王冠') and s.stage > s.fx['fire_activation_stage']:
                self.add_ember(s, 1)
            for key in ('trickle', 'springs'):
                s.fx[key] = 0
            for e in ELEMENTS:
                s.fx['discount_'+e] = 0
        if self.time >= FATIGUE_START_AV:
            amounts = [ceil(s.hp*(Q(5, 100)+Q(self.time, 10000))) for s in self.players]
            for s, amount in zip(self.players, amounts):
                self.lose_life(s, amount)
            for s in self.players:
                self.flush_shields(s)
        self.finish_transaction()

    def missile_parameters(self, i, main=False):
        s = self.players[i]
        defense_pen = 0
        if s.has('穿云箭'):
            defense_pen += 5+2*s.layers['wind']
        shield_pen = Q(5+2*s.layers['wind'], 100) if s.has('天岚') else Q(0)
        if s.has('生命之泉'):
            shield_pen += Q(1, 5)
        if main:
            defense_pen += s.fx['main_def_pen']
            shield_pen += Q(s.fx['main_shield_pen'], 100)
        return dict(def_pen=min(Q(1), Q(defense_pen, 100)), pen=min(Q(1), shield_pen),
                    ignore=Q(15,100) if s.has('穿云箭') and s.has('天岚') else Q(0),
                    shield_factor=1 + Q(s.fx['melt_shield'],10) + (Q(20,100) if s.has('穿云箭') else 0))

    def fire(self, due):
        groups = [[], []]
        for i in due:
            s = self.players[i]
            # Expiring at launch is distinct from expiring after missile damage.
            a, d = self.stats(i)
            if s.layers['earth']:
                rate = 4 if s.specialization == 'earth' else 2
                self.shield_gain(s, ceil((a+d)*s.layers['earth']*Q(rate, 100))+s.fx['earth_extra'])
            self.flush_shields(s)
        for i in due:
            s = self.players[i]
            a = self.stats(i)[0]
            extra = s.fx['direct']
            if s.has('血潮'):
                extra += self.pay_life(s, Q(10, 100), False)
            ember = self.consume_ember(s, s.ember)
            tide = self.consume_tide(s, s.tide)
            extra += tide*(1+Q(s.fx['tide_bonus'], 100))
            extra += s.fx['grip_loss']*Q(s.layers['earth']*4,100)*(1+Q(s.fx['grip_bonus'], 100))
            extra += a*ember*Q(s.fx['hot_core'], 100)
            if s.has('疾风之刃') and s.last_fire >= 0:
                extra += s.last_main*max(Q(0), 1-Q(self.time-s.last_fire, 50))*Q(1, 5)
            if tide and s.fx['tide_shield']:
                self.shield_gain(s, tide*s.fx['tide_shield']/100)
            if s.has('潮汐回响'):
                self.add_tide(s, Q(tide, 4))
            if s.fx['pierce_tide']:
                s.fx['main_def_pen'] += s.fx['pierce_tide']*(70 if tide >= 50 else 40)
            self.flush_shields(s)
            if s.fx['strip']:
                e = self.players[1-i]
                strips = s.fx.pop('strip')
                # Add percentages against the same pre-launch shield, capped at 100%.
                for ratio, speed in [(min(Q(1), sum(r for r, _ in strips)), sum(v for _, v in strips))]:
                    removed = self.take_shield(e, math.floor(e.shield_total*ratio))
                    if removed:
                        s.fx['permanent_delay'] -= speed
            params = self.missile_parameters(i, True)
            ratio = Q(9,5) if s.has('燃烧血统') else 1
            main = self.packet(i, ((a+s.fx['main_attack'])*ratio+extra)*(1+Q(s.fx['bonus'], 100)), 'missile',
                               main=True, armor_break=s.fx['armor_break'],
                               record_bonus=s.fx['tide_record'], **params)
            main['final'] *= 1+s.layers['fire']*ember*(Q(3, 100) if s.has('燃烧血统') else Q(25, 1000))
            groups[i].append(main)
            for fixed, ratio, marks, leech in s.permanent_subs+s.subs:
                groups[i].append(self.packet(i, fixed+a*ratio, 'missile', marks=marks,
                                             armor_break=s.fx['armor_break'], leech=leech, **self.missile_parameters(i)))
            if s.has('天岚'):
                s.fx['shots'] += 1
                if s.fx['shots'] % 3 == 0:
                    # Appended after every ordinary sub: wind marks from all of
                    # this launch's earlier missiles exist before detonation.
                    groups[i].append(self.packet(i, a*(Q(2,5) if s.has('风痕') else Q(1,5)), 'true', armor_break=s.fx['armor_break'], detonate=True, locked_attack=a))
            s.last_fire = self.time
        for k in range(max(map(len, groups), default=0)):
            packets = [g[k] for g in groups if k < len(g)]
            self.wave(packets)
            detonations = []
            for p in packets:
                i = p['source'];s, e = self.players[i], self.players[1-i]
                if p.get('detonate') and s.has('风痕') and not e.guarded:
                    n, e.wind_marks = e.wind_marks, 0
                    if n:
                        detonations.append(self.packet(i, p['locked_attack']*(Q(12*n, 100)+Q(n*(n-1), 100)), 'true'))
            if detonations:
                self.wave(detonations)
        for i in due:
            s = self.players[i]
            s.next_fire = self.time+self.gap(s)
            s.subs.clear()
            for key in ('direct', 'bonus', 'delay', 'main_def_pen', 'main_shield_pen', 'temp_attack', 'main_attack',
                        'grip_loss', 'grip_bonus', 'hot_core', 'tide_bonus', 'tide_record',
                        'pierce_tide', 'tide_shield', 'armor_break'):
                s.fx[key] = 0
        self.finish_transaction()

    def run(self, max_time=10000, max_actions=10000):
        from .policy import choose_play
        actions_count = 0
        while True:
            if self.time % 100 == 0:
                self.begin_stage()
                if any(s.hp <= 0 for s in self.players):
                    break
                if self.sequence_mode in ('private_positions_experimental','locked'):
                    sequences=self.plan_stage(max_actions=max_actions)
                    self.planned_count+=sum(map(len,sequences))
                    if self.planned_count>max_actions:
                        raise RuntimeError('Nonterminating planned card sequence; no fabricated result')
                    actions_count+=self.resolve_sequences(sequences)
                else:
                    while True:
                        choices={i:choose_play(self,i) for i in range(2)}
                        actions={i:x.uid for i,x in choices.items() if x is not None}
                        if not actions:break
                        actions_count+=len(actions)
                        if actions_count>max_actions:
                            raise RuntimeError('Nonterminating card sequence; no fabricated result')
                        self.card_step(actions)
                        if any(s.hp<=0 for s in self.players):break
                self.end_stage()
            else:
                self.decay()
                self.finish_transaction()
            if any(s.hp <= 0 for s in self.players):
                break
            due = [i for i, s in enumerate(self.players) if s.next_fire == self.time]
            if due:
                self.fire(due)
            if any(s.hp <= 0 for s in self.players):
                break
            next_time = min((self.time//100+1)*100, *(s.next_fire for s in self.players), *(s.poison_due for s in self.players))
            if next_time <= self.time or next_time > max_time:
                raise RuntimeError(('Nonterminating scheduler; no fabricated winner', self.time, next_time))
            self.time = next_time
        dead = [s.hp <= 0 for s in self.players]
        return dict(version='1.2', winner=None if all(dead) else 1 if dead[0] else 0,
                    combat_rule=getattr(self,'snapshot_rule','legacy_position_projection'),
                    sequence_rule=self.sequence_mode,
                    time=self.time, hp=[s.hp for s in self.players], actions=actions_count,
                    planned_actions=self.planned_count,sequence_skips=dict(self.sequence_skips),
                    executed_by_seat=[dict(c) for c in self.executed_by_seat],
                    flow_by_seat=[dict(c) for c in self.flow_by_seat],
                    cycle_decisions=[dict(c) for c in self.cycle_decisions],
                    executed=dict(self.executed), log=self.log if self.trace else [])

    def plan_stage(self, max_actions=10000):
        """Both plans are complete before any real card result is committed.

        Each shadow contains own hidden zones and only the opponent's starting
        public combat state. Its opponent never plays cards. Preview results,
        random state and private choices cannot mutate the real battle.
        """
        from .policy import choose_play
        sequences=[]
        for i in range(2):
            shadow=copy(self)
            shadow.players=deepcopy(self.players)
            enemy=shadow.players[1-i]
            enemy.deck=();enemy.instances=()
            enemy.pile=[];enemy.hand=[];enemy.discard=[];enemy.removed=[];enemy.resolving=[]
            enemy.discounts={};enemy.drawn=set();enemy.seen=set()
            enemy.rng=__import__('random').Random(0)
            shadow.log=[];shadow.trace=False;shadow.executed=Counter()
            shadow.executed_by_seat=[Counter(),Counter()]
            shadow.operations=[];shadow.replay_decisions=None
            shadow.sequence_skips=Counter();shadow.decision_cursors={}
            sequence=[]
            while True:
                inst=choose_play(shadow,i)
                if inst is None:
                    break
                if len(sequence)>=max_actions:
                    raise RuntimeError('Private planning did not terminate')
                cost=shadow.cost(shadow.players[i],inst)
                shadow.choice_records={i:[]}
                shadow.card_step({i:inst.uid})
                sequence.append(PlannedCard(inst.uid,inst.name,cost,tuple(shadow.choice_records[i])))
                if any(p.hp<=0 for p in shadow.players):
                    break
            sequences.append(tuple(sequence))
        return tuple(sequences)

    def resolve_sequences(self,sequences):
        """Immutable positions, no repicking or queue compression on failure."""
        self.event('sequences_locked',0,sequences=[[p.name for p in seq] for seq in sequences])
        played=0
        for position in range(max(map(len,sequences),default=0)):
            actions={};decisions={}
            for i,sequence in enumerate(sequences):
                if position>=len(sequence):continue
                entry=sequence[position];s=self.players[i]
                inst=next((x for x in s.hand if x.uid==entry.uid),None)
                reason='missing_instance' if inst is None else 'insufficient_energy' if self.cost(s,inst)>s.energy else None
                if reason:
                    self.sequence_skips[reason]+=1
                    self.event('sequence_skip',i,position=position,name=entry.name,reason=reason)
                    continue
                actions[i]=entry.uid;decisions[i]=entry.choices
            if actions:
                self.replay_decisions=decisions
                try:
                    self.card_step(actions)
                finally:
                    self.replay_decisions=None
                played+=len(actions)
            if any(s.hp<=0 for s in self.players):break
        return played
