import json
import unittest
from unittest.mock import patch
from tools.client_v12 import bridge
from tools.sim_v12.catalog import CARDS, START, ELEMENTS, SPECIAL
from tools.sim_v12.strict import StrictBattle


class BridgeTests(unittest.TestCase):
    def test_fifteen_routes_match_native_results_and_events(self):
        from tools.sim_v12 import policy
        targets = list(policy.TARGETS.values())
        original_play, original_cards = policy.choose_play, policy.choose_cards
        for index, target in enumerate(targets):
            decks = [target['entries'], targets[(index + 7) % 15]['entries']]
            specs = [next((el for el, n in SPECIAL.items() if n in d), '') for d in decks]
            commands = []
            def play(b, seat):
                x = original_play(b, seat)
                if seat == 0 and any(b.cost(b.players[seat], c) <= b.players[seat].energy for c in b.players[seat].hand):
                    commands.append(dict(kind='play', uid=x.uid if x else None))
                return x
            def select(b, s, candidates, count, purpose):
                chosen = original_cards(b, s, candidates, count, purpose)
                if s.seat == 0: commands.append(dict(kind='select', uids=[x.uid for x in chosen]))
                return chosen
            with patch.object(policy, 'choose_play', play), patch.object(policy, 'choose_cards', select):
                native = StrictBattle(decks, specializations=specs, seed=12000+index, trace=True).run()
            with self.subTest(route=target['route']):
                client = bridge.respond(dict(decks=decks, specializations=specs, commands=commands, seed=12000+index))
                self.assertEqual(client['status'], 'finished')
                self.assertEqual(client['winner'], native['winner'])
                self.assertEqual([p['hp'] for p in client['public']], native['hp'])
                self.assertEqual(client['log'], [e for e in native['log'] if e['kind'] in bridge.PUBLIC_EVENTS])
                self.assertEqual(client['executed'], native['executed'])

    def test_catalog_exact(self):
        result = bridge.respond({'op': 'catalog'})
        self.assertEqual({c['name'] for c in result['cards']}, set(CARDS))
        self.assertEqual(len(result['routes']), 15)

    def test_replay_private_and_invalid_input(self):
        request = dict(decks=[START, START], seed=17)
        a = bridge.respond(request)
        self.assertEqual(a, bridge.respond(request))
        self.assertFalse(any(e['kind'] in ('draw', 'sequences_locked') for e in a['log']))
        self.assertNotIn('hand', a['public'][1])
        with self.assertRaises(ValueError):
            bridge.respond(dict(request, commands=[dict(kind='play', uid='1:0')]))
        with self.assertRaises(ValueError):
            bridge.respond(dict(decks=[['retired'], START]))

    def test_every_card_through_client_decisions(self):
        coverage = []
        for name, card in CARDS.items():
            for rich in (False, True):
                class FixtureBattle(StrictBattle):
                    def begin_stage(self):
                        super().begin_stage()
                        s, e = self.players
                        if s.stage != 1:
                            return
                        s.pile = list(s.instances)
                        s.hand = s.pile[:3]; s.pile = s.pile[3:]
                        s.discard = []; s.drawn = set(x.uid for x in s.hand)
                        s.energy = 100
                        if rich:
                            s.hp = 400; s.shield = 250; e.shield = 150; e.poison = 6
                            element = card.element if card.element in ELEMENTS else 'fire'
                            s.specialization = element; s.active.add(SPECIAL[element])
                            for el in ELEMENTS: s.layers[el] = 5
                            s.ember = 3 if element == 'fire' else 0
                            s.tide = 75 if element == 'water' else 0

                request = dict(decks=[[name, '火花', '掠影', '护盾', '短促'], list(START)], commands=[], seed=19)
                with self.subTest(name=name, rich=rich), patch.object(bridge, 'StrictBattle', FixtureBattle):
                    for step in range(30):
                        result = bridge.respond(request)
                        if any(e['kind'] == 'play' and e['seat'] == 0 and e['name'] == name for e in result['log']):
                            coverage.append(dict(name=name, rich=rich, passed=True))
                            break
                        self.assertEqual(result['status'], 'decision')
                        decision = result['decision']
                        if decision['kind'] == 'play':
                            answer = dict(kind='play', uid='0:0' if not request['commands'] else None)
                        else:
                            answer = dict(kind='select', uids=[c['uid'] for c in decision['candidates'][:decision['count']]])
                        request['commands'].append(answer)
                    else:
                        self.fail('No committed play for ' + name)
        self.assertEqual(len(coverage), 366)
        from pathlib import Path
        output = Path(__file__).resolve().parents[2] / 'runtime/tests/client-v12'
        output.mkdir(parents=True, exist_ok=True)
        (output / 'card-coverage.json').write_text(json.dumps(coverage, ensure_ascii=False, indent=2), encoding='utf-8')


if __name__ == '__main__':
    unittest.main()
