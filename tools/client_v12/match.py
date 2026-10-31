"""Resumable single-player adapter around the canonical v1.2 tournament.

One isolated authority process per match. The worker retains the original
tournament stack while the JSON transport waits for human decisions. No game
state, private opponent deck or executable payload is accepted from the UI.
"""
from queue import Queue
from threading import Thread
from unittest.mock import patch
import json
import sys

from tools.sim_v12 import tournament as T, policy
from tools.sim_v12.catalog import CARDS
from tools.sim_v12.six_styles import IDS, load_policy
from tools.client_v12.bridge import PUBLIC_EVENTS, card_view, public_state


def human_play_decision(battle, seat):
    s = battle.players[seat]
    hand = [card_view(x, battle.cost(s, x)) for x in s.hand]
    affordable = {x.uid: x for x in s.hand if battle.cost(s, x) <= s.energy}
    return dict(kind='play', energy=s.energy, hand=hand), affordable


def automatic_choice(candidates, count):
    """None requests input; an empty/list result is an unambiguous choice."""
    if count <= 0 or not candidates: return []
    if len(candidates) == 1: return list(candidates)
    return None


def deletion_options(p, r, used):
    limit, fees = T.deletion_rules(r)
    if used >= limit or len(p.deck) <= T.ECONOMY['minimum_deck_size']: return None
    fee = fees[used]
    if p.points < fee: return None
    main = sum(CARDS[n].element == p.spec and CARDS[n].kind != 'specialization' for n, _ in p.deck)
    legal = [j for j, (n, _) in enumerate(p.deck) if not CARDS[n].ability and CARDS[n].rarity != 'legendary' and not (CARDS[n].element == p.spec and main <= 3)]
    return dict(fee=fee, remaining=limit-used, legal=legal) if legal else None


class MatchSession:
    def __init__(self, seed=1, ai_styles=('fire', 'water', 'poison')):
        if len(ai_styles) != 3 or any(s not in IDS for s in ai_styles):
            raise ValueError('Expected three valid v1.2 AI styles')
        self.seed = int(seed)
        self.styles = ('flexible', *ai_styles)
        self.configs = tuple(load_policy(s) for s in self.styles)
        self.inputs, self.outputs = Queue(), Queue()
        self.revision = 0
        self.auto_all = self.auto_round = False
        self.players, self.dead, self.pairs, self.events = [], [], [], []
        self.round = self.battle_cursor = 0
        self.last_battles = []
        self.history = []
        self.result = None
        self.thread = Thread(target=self._run, daemon=True)
        self.thread.start()

    def receive(self):
        return self.outputs.get(timeout=120)

    def send(self, command):
        if command.get('revision') != self.revision:
            raise ValueError('Stale match revision; reload the current decision')
        if self.result is not None:
            raise ValueError('Match has finished')
        self.inputs.put(command)
        return self.receive()

    def view(self):
        own = next((p for p in self.players + self.dead if p.pid == 0), None)
        return dict(version='1.2', round=self.round, seed=self.seed,
                    standings=T.public(list({p.pid: p for p in self.players + self.dead}.values())),
                    ai_styles=list(self.styles[1:]), pairs=self.pairs,
                    events=list(self.events), last_battles=self.last_battles,
                    previous_battles=next((row['battles'] for row in reversed(self.history) if row['round'] < self.round), []),
                    completed_rounds=[row['round'] for row in self.history],
                    own=None if own is None else dict(hp=own.hp, points=own.points,
                        bank=own.bank, income=own.income, carry=own.carry, bonus=own.bonus,
                        spec=own.spec, route=own.route, deck=[dict(index=j, **CARDS[n].__dict__) for j, (n, _) in enumerate(own.deck)]))

    def emit(self, **payload):
        self.revision += 1
        self.outputs.put(dict(self.view(), revision=self.revision, **payload))

    def record_round(self):
        self.history.append(dict(round=self.round, events=list(self.events),
                            standings=T.public(list({p.pid: p for p in self.players + self.dead}.values())),
                            battles=self.last_battles))

    def review(self, round_number):
        require(type(round_number) is int and 1 <= round_number <= len(self.history), 'Round has not finished')
        return dict(version='1.2', status='review', revision=self.revision, review=self.history[round_number-1])

    def ask(self, question, validate, **context):
        error = None
        while True:
            self.emit(status='decision', decision=question, error=error, **context)
            command = self.inputs.get()
            kind = command.get('kind')
            if kind == 'delegate_match':
                self.auto_all = True
                return dict(kind='auto')
            if kind == 'delegate_round' and question['kind'] in ('draft', 'refresh', 'delete'):
                self.auto_round = True
                return dict(kind='auto')
            try:
                validate(command)
                return command
            except (ValueError, KeyError, TypeError) as exc:
                error = str(exc)

    def _run(self):
        original_buy, original_delete = T.buy_phase, T.delete_phase
        original_refresh, original_schedule = T.refresh_wanted, T.schedule
        original_play = policy.choose_play
        owner = self

        def schedule(players, dead, last, meetings, rng):
            self.players, self.dead = players, dead
            if self.round:
                self.record_round()
                if not self.auto_all:
                    self.ask(dict(kind='round', eliminated=[p.pid for p in dead]),
                             lambda c: require(c.get('kind') == 'next_round', 'Expected next_round'))
            self.round += 1
            self.auto_round = False
            self.human_face = 0
            self.deleted = 0
            self.events, self.last_battles = [], []
            self.pairs = original_schedule(players, dead, last, meetings, rng)
            self.battle_cursor = 0
            return self.pairs

        def buy(p, players, pool, r, events):
            self.events = events
            if p.pid != 0 or self.auto_all or self.auto_round:
                return original_buy(p, players, pool, r, events)
            face = self.human_face
            self.human_face += 1
            bought = set()
            while True:
                offers = []
                legal = {}
                for slot, token in enumerate(pool.offers[p.pid]):
                    if not token:
                        continue
                    name = pool.tokens[token]
                    cost, old = T.price_and_replace(p, name, r)
                    offers.append(dict(slot=slot, **CARDS[name].__dict__, price=cost,
                                       replaces=old, affordable=cost is not None and cost <= p.points))
                    if cost is not None and cost <= p.points:
                        legal[slot] = name

                def valid(c):
                    require(c.get('kind') in ('buy', 'end_face', 'delete'), 'Expected purchase, deletion or end_face')
                    if c['kind'] == 'buy':
                        require(type(c.get('slot')) is int and c['slot'] in legal, 'Unavailable or unaffordable offer')
                    if c['kind'] == 'delete':
                        require(deletion is not None and type(c.get('index')) is int and c['index'] in deletion['legal'], 'Protected or unavailable card')

                deletion = deletion_options(p, r, self.deleted)
                answer = self.ask(dict(kind='draft', offers=offers,
                                      can_refresh=face == 0 and p.points >= T.ECONOMY['refresh_cost'] and len(bought) < 8,
                                      can_delete=deletion is not None, deletion=deletion), valid)
                if answer['kind'] == 'auto':
                    return bought | original_buy(p, players, pool, r, events)
                if answer['kind'] == 'end_face':
                    return bought
                if answer['kind'] == 'delete':
                    name, token = p.deck.pop(answer['index']); pool.remove(token); p.points -= deletion['fee']; self.deleted += 1
                    p.actions.append(dict(kind='delete', name=name, cost=deletion['fee'])); pool.audit(players)
                    continue
                name = legal[answer['slot']]
                # The canonical transaction handles payment, replacements,
                # finite ownership, free entitlement and specialization grant.
                with patch.object(T, 'purchase_value', lambda player, candidate, *args: 1 if candidate == name else -1):
                    bought |= original_buy(p, players, pool, r, events)
                pool.audit(players)

        def refresh(p, r, unbought, public):
            if p.pid != 0 or self.auto_all or self.auto_round:
                return original_refresh(p, r, unbought, public)
            answer = self.ask(dict(kind='refresh', cost=T.ECONOMY['refresh_cost']),
                              lambda c: require(c.get('kind') in ('refresh', 'keep_face'), 'Expected refresh or keep_face'))
            return original_refresh(p, r, unbought, public) if answer['kind'] == 'auto' else answer['kind'] == 'refresh'

        def delete(p, pool, r):
            if p.pid != 0 or ((self.auto_all or self.auto_round) and self.deleted == 0):
                return original_delete(p, pool, r)
            if not (self.auto_all or self.auto_round): return
            limit, fees = T.deletion_rules(r)
            for k in range(self.deleted, limit):
                fee = fees[k]
                if len(p.deck) <= T.ECONOMY['minimum_deck_size'] or p.points < fee:
                    break
                main = sum(CARDS[n].element == p.spec and CARDS[n].kind != 'specialization' for n, _ in p.deck)
                legal = [j for j, (n, _) in enumerate(p.deck)
                         if not CARDS[n].ability and CARDS[n].rarity != 'legendary'
                         and not (CARDS[n].element == p.spec and main <= 3)]
                if not legal:
                    break

                # Only delegated remaining deletions reach this path.
                candidates = [(T.deletion_value(p, p.deck[j][0], fee), j) for j in legal]
                value, index = max(candidates)
                if value < 0: break
                name, token = p.deck.pop(index)
                self.deleted += 1
                pool.remove(token); p.points -= fee
                p.actions.append(dict(kind='delete', name=name, cost=fee))
                pool.audit(self.players)

        class ClientBattle(T.StrictBattle):
            def __init__(self, *args, **kwargs):
                super().__init__(*args, **kwargs)
                a, b = owner.pairs[owner.battle_cursor]
                owner.battle_cursor += 1
                self.human_seat = 0 if a == 0 else 1 if b == 0 else -1
                self._authority = self
                self.delegated = False
                self.user_plan = []
                self.user_plan_stage = -1
                self.participants = (a, b)

            def choose(self, s, candidates, count, purpose, optional=False):
                if s.seat != self.human_seat or owner.auto_all or self._authority.delegated:
                    return super().choose(s, candidates, count, purpose, optional)
                trivial = automatic_choice(candidates, count)
                if trivial is not None: return trivial
                legal = {x.uid: x for x in candidates}
                required = min(count, len(candidates))
                def valid(c):
                    require(c.get('kind') in ('select', 'delegate_battle'), 'Expected selection')
                    if c['kind'] == 'delegate_battle': return
                    ids = c.get('uids', [])
                    require(isinstance(ids, list) and len(set(ids)) == len(ids) and all(uid in legal for uid in ids), 'Invalid selection targets')
                    require(len(ids) <= required and (optional or len(ids) == required), 'Invalid selection count')
                answer = owner.ask(dict(kind='select', purpose=purpose, count=required, optional=optional, energy=s.energy,
                                        candidates=[card_view(x) for x in candidates]), valid, **self.context())
                if answer['kind'] in ('auto', 'delegate_battle'):
                    self._authority.delegated = True
                    return super().choose(s, candidates, count, purpose, optional)
                return [legal[uid] for uid in answer.get('uids', [])]

            def context(self):
                b = self._authority
                return dict(combat=dict(time=b.time, human_seat=self.human_seat, participants=list(b.participants),
                            draw_pile=[card_view(x) for x in sorted(self.players[self.human_seat].pile, key=lambda x: (x.name, x.uid))],
                            discard_pile=[card_view(x) for x in self.players[self.human_seat].discard],
                            planned=list(b.user_plan),
                            public=public_state(b),
                            log=[e for e in b.log if e['kind'] in PUBLIC_EVENTS]))

            def run(self, *args, **kwargs):
                result = super().run(*args, **kwargs)
                owner.last_battles.append(dict(a=self.participants[0], b=self.participants[1],
                    winner=result['winner'], hp=result['hp'], time=result['time'],
                    log=[e for e in result['log'] if e['kind'] in PUBLIC_EVENTS]))
                return result

        def play(b, seat):
            if seat != b.human_seat or self.auto_all or b._authority.delegated:
                return original_play(b, seat)
            s = b.players[seat]
            if b._authority.user_plan_stage != s.stage:
                b._authority.user_plan_stage = s.stage
                b._authority.user_plan = []
            decision, affordable = human_play_decision(b, seat)
            def valid(c):
                require(c.get('kind') in ('play', 'delegate_battle'), 'Expected play or delegate_battle')
                if c['kind'] == 'play':
                    require(c.get('uid') is None or c['uid'] in affordable, 'Unavailable or unaffordable card')
            answer = self.ask(decision, valid, **b.context())
            if answer['kind'] in ('auto', 'delegate_battle'):
                b._authority.delegated = True
                return original_play(b, seat)
            chosen = affordable.get(answer.get('uid'))
            if chosen:
                b._authority.user_plan.append(chosen.name)
            return chosen

        try:
            with patch.object(T, 'buy_phase', buy), patch.object(T, 'delete_phase', delete), \
                 patch.object(T, 'refresh_wanted', refresh), patch.object(T, 'schedule', schedule), \
                 patch.object(T, 'StrictBattle', ClientBattle), patch.object(policy, 'choose_play', play):
                self.result = T.simulate(self.seed, trace=True, policy_by_seat=self.configs,
                                         refresh_threshold=lambda p: T.ECONOMY['refresh_cost'] if p.pid == 0 and not (self.auto_all or self.auto_round) else 2)
            self.record_round()
            placements = [{k: row[k] for k in ('pid', 'hp', 'round', 'spec', 'route', 'rank', 'score')}
                          for row in self.result['placements']]
            self.emit(status='finished', placements=placements, battles=self.result['battles'])
        except Exception as exc:
            self.emit(status='error', error=str(exc))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def main():
    session = None
    for line in sys.stdin:
        try:
            request = json.loads(line)
            if request.get('op') == 'start':
                require(session is None, 'A match is already running')
                session = MatchSession(request.get('seed', 1), request.get('ai_styles', ('fire', 'water', 'poison')))
                response = session.receive()
            elif request.get('op') == 'review':
                require(session is not None, 'Start a match first')
                response = session.review(request.get('round'))
            else:
                require(session is not None, 'Start a match first')
                response = session.send(request)
        except Exception as exc:
            response = dict(status='error', error=str(exc))
        print(json.dumps(response, ensure_ascii=True), flush=True)


if __name__ == '__main__':
    main()
