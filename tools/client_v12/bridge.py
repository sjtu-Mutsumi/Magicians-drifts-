"""JSON stdin/stdout, deterministic command replay; no serialized Python objects.

The UI submits its own choices only. The opponent's private plan never crosses
this boundary. A request runs until the next human decision or battle result.
"""
import json
import sys
from dataclasses import asdict
from unittest.mock import patch
from tools.sim_v12.catalog import CARDS, SPECIAL
from tools.sim_v12.strict import StrictBattle
from tools.sim_v12 import policy

PUBLIC_EVENTS = {'play', 'damage', 'heal', 'shield', 'poison', 'revive', 'auto_execute', 'shuffle'}


class Pending(Exception):
    def __init__(self, decision):
        self.decision = decision


def card_view(x, cost=None):
    return dict(uid=x.uid, **asdict(CARDS[x.name]), payable_cost=cost)


def public_state(b):
    return [dict(seat=s.seat, hp=s.hp, shield=s.shield_total,
                 attack=str(b.stats(s.seat)[0]), defense=str(b.stats(s.seat)[1]),
                 energy=s.energy, layers=s.layers, ember=s.ember, tide=s.tide,
                 poison=s.poison, active=sorted(s.active), next_fire=s.next_fire,
                 modifiers=[dict(kind=m.kind, value=str(m.value)) for m in s.mods],
                 missile_bonuses={k: str(s.fx[k]) for k in ('bonus', 'delay', 'temp_attack', 'main_def_pen', 'main_shield_pen', 'main_attack') if s.fx[k]})
            for s in b.players]


def respond(request):
    if request.get('op') == 'catalog':
        return dict(version='1.2', cards=[asdict(c) for c in CARDS.values()],
                    routes=policy.TARGETS)
    decks = request['decks']
    if len(decks) != 2 or any(not d or len(d) > 256 for d in decks):
        raise ValueError('Expected two nonempty decks of at most 256 cards')
    commands = request.get('commands', [])
    if len(commands) > 10000:
        raise ValueError('Too many commands')
    cursor = 0
    class ClientBattle(StrictBattle):
        def choose(self, s, candidates, count, purpose, optional=False):
            if s.seat != 0:
                return super().choose(s, candidates, count, purpose, optional)
            return choose_cards(self, s, candidates, count, purpose, optional)

    specs = request.get('specializations', [next((el for el, n in SPECIAL.items() if n in d), '') for d in decks])
    b = ClientBattle(decks, specializations=specs,
                     seed=int(request.get('seed', 1)), trace=True)
    if request.get('ai_style'):
        from tools.sim_v12.six_styles import load_policy
        b.policy_configs = (load_policy('flexible'), load_policy(request['ai_style']))
    original_play = policy.choose_play

    def consume(decision):
        nonlocal cursor
        if cursor == len(commands):
            raise Pending(decision)
        answer = commands[cursor]
        cursor += 1
        if answer.get('kind') != decision['kind']:
            raise ValueError('Command does not match current decision')
        return answer

    def choose_play(shadow, seat):
        if seat != 0:
            return original_play(shadow, seat)
        s = shadow.players[seat]
        affordable = {x.uid: x for x in s.hand if shadow.cost(s, x) <= s.energy}
        if not affordable:
            return None
        decision = dict(kind='play', time=b.time, energy=s.energy,
                        hand=[card_view(x, shadow.cost(s, x)) for x in s.hand])
        answer = consume(decision)
        uid = answer.get('uid')
        if uid is None:
            return None
        if uid not in affordable:
            raise ValueError('Card is not in hand or cannot be paid for')
        return affordable[uid]

    def choose_cards(shadow, s, candidates, count, purpose, optional=False):
        answer = consume(dict(kind='select', purpose=purpose, count=count,
                              optional=optional, candidates=[card_view(x) for x in candidates]))
        ids = answer.get('uids', [])
        legal = {x.uid: x for x in candidates}
        required = min(count, len(candidates))
        if (len(set(ids)) != len(ids) or any(uid not in legal for uid in ids)
                or len(ids) > required or (not optional and len(ids) != required)):
            raise ValueError('Invalid selection')
        return [legal[uid] for uid in ids]

    with patch.object(policy, 'choose_play', choose_play):
        try:
            result = b.run()
            if cursor != len(commands):
                raise ValueError('Unused commands after battle ended')
            return dict(version='1.2', status='finished', time=b.time,
                        public=public_state(b), winner=result['winner'],
                        log=[e for e in b.log if e['kind'] in PUBLIC_EVENTS],
                        executed=result['executed'])
        except Pending as pending:
            return dict(version='1.2', status='decision', time=b.time,
                        public=public_state(b), decision=pending.decision,
                        log=[e for e in b.log if e['kind'] in PUBLIC_EVENTS])


def main():
    try:
        result = respond(json.load(sys.stdin))
    except Exception as error:
        result = dict(status='error', error=str(error))
    print(json.dumps(result, ensure_ascii=True))


if __name__ == '__main__':
    main()
