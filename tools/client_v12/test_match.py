import unittest
from tools.client_v12.match import MatchSession
from tools.sim_v12 import tournament as T
from tools.sim_v12.six_styles import load_policy


class MatchTests(unittest.TestCase):
    def test_automatic_zero_or_single_selection(self):
        from tools.client_v12.match import automatic_choice
        self.assertEqual(automatic_choice([], 1), [])
        self.assertEqual(automatic_choice(['only'], 1), ['only'])
        self.assertEqual(automatic_choice(['only'], 0), [])
        self.assertIsNone(automatic_choice(['first', 'second'], 1))

    def test_one_point_refresh_is_legal_and_charged(self):
        session = MatchSession(334003); response = session.receive()
        try:
            free = next(c for c in response['decision']['offers'] if c['price'] == 0)
            response = session.send(dict(kind='buy', slot=free['slot'], revision=response['revision']))
            paid = next(c for c in response['decision']['offers'] if c['price'] == 1)
            response = session.send(dict(kind='buy', slot=paid['slot'], revision=response['revision']))
            self.assertEqual(response['own']['points'], 1)
            self.assertTrue(response['decision']['can_refresh'])
            response = session.send(dict(kind='end_face', revision=response['revision']))
            self.assertEqual(response['decision']['kind'], 'refresh')
            response = session.send(dict(kind='refresh', revision=response['revision']))
            self.assertEqual(response['own']['points'], 0)
            self.assertEqual(response['decision']['kind'], 'draft')
            self.assertFalse(response['decision']['can_refresh'])
        finally:
            result = self.finish(session, response); self.assertEqual(result['status'], 'finished', result.get('error')); session.thread.join(2)
    def test_zero_energy_keeps_free_card_legal(self):
        from tools.client_v12.match import human_play_decision
        from tools.sim_v12.strict import StrictBattle
        b = StrictBattle((['预备', '攻击强化'], ['防御强化']))
        s = b.players[0]; s.hand = list(s.instances); s.pile = []; s.energy = 0
        decision, legal = human_play_decision(b, 0)
        self.assertEqual((decision['kind'], decision['energy']), ('play', 0))
        self.assertIn(s.instances[0].uid, legal)
        self.assertNotIn(s.instances[1].uid, legal)
        b.play_local(0, s.instances[0].uid)
        self.assertEqual(s.energy, 0)
        # Even with only unaffordable cards remaining, the human explicitly
        # confirms the end rather than disappearing into the next window.
        decision, legal = human_play_decision(b, 0)
        self.assertEqual(decision['kind'], 'play')
        self.assertFalse(legal)
    def finish(self, session, response):
        if response['status'] == 'decision':
            return session.send(dict(kind='delegate_match', revision=response['revision']))
        return response

    def test_delegated_full_tournament_equals_native(self):
        for seed, styles in [(334001, ('fire', 'water', 'earth')), (334002, ('wind', 'poison', 'flexible'))]:
            session = MatchSession(seed, styles)
            response = session.receive()
            self.assertEqual(response['decision']['kind'], 'draft')
            result = self.finish(session, response)
            self.assertEqual(result['status'], 'finished', result.get('error'))
            session.thread.join(2)
            native = T.simulate(seed, trace=True, policy_by_seat=tuple(load_policy(s) for s in ('flexible', *styles)))
            self.assertEqual(session.result, native)
            self.assertEqual(len(result['placements']), 4)
            self.assertEqual(result['completed_rounds'], list(range(1, len(native['rounds'])+1)))
            for row in native['rounds']:
                review = session.review(row['round'])['review']
                self.assertEqual(review['round'], row['round'])
                for battle in review['battles']:
                    self.assertTrue(all(e['kind'] not in ('draw', 'sequences_locked', 'random_insert') for e in battle['log']))
                    self.assertNotIn('participants', battle)
            with self.assertRaises(ValueError): session.review(len(native['rounds'])+1)

    def test_manual_buy_and_invalid_command_are_transactional(self):
        session = MatchSession(334003)
        response = session.receive()
        try:
            before = response['own']
            response = session.send(dict(kind='buy', slot=100, revision=response['revision']))
            self.assertEqual(response['own'], before)
            self.assertIsNotNone(response['error'])
            legal = next(c for c in response['decision']['offers'] if c['affordable'])
            response = session.send(dict(kind='buy', slot=legal['slot'], revision=response['revision']))
            self.assertEqual(response['own']['points'], before['points']-legal['price'])
            self.assertIn(legal['name'], [c['name'] for c in response['own']['deck']])
            for player in response['standings']:
                self.assertNotIn('deck', player)
                self.assertNotIn('policy', player)
            with self.assertRaises(ValueError):
                session.send(dict(kind='end_face', revision=response['revision']-1))
        finally:
            result = self.finish(session, response)
            self.assertEqual(result['status'], 'finished', result.get('error'))
            session.thread.join(2)

    def test_manual_round_battle_and_spectator_to_champion(self):
        session = MatchSession(334004)
        response = session.receive()
        kinds = set()
        try:
            for _ in range(1000):
                if response['status'] != 'decision':
                    break
                decision = response['decision']; kind = decision['kind']; kinds.add(kind)
                if kind == 'draft':
                    legal = [c for c in decision['offers'] if c['affordable']]
                    command = dict(kind='buy', slot=legal[0]['slot']) if legal and response['round'] != 1 else dict(kind='end_face')
                elif kind == 'refresh': command = dict(kind='refresh')
                elif kind == 'delete': command = dict(kind='delete', index=decision['legal'][0])
                elif kind == 'play':
                    cards = [c for c in decision['hand'] if c['payable_cost'] <= decision['energy']]
                    command = dict(kind='play', uid=cards[0]['uid'] if cards else None)
                elif kind == 'select':
                    command = dict(kind='select', uids=[c['uid'] for c in decision['candidates'][:decision['count']]])
                elif kind == 'round':
                    if response['round'] >= 8: command = dict(kind='delegate_match')
                    else: command = dict(kind='next_round')
                else: self.fail(kind)
                response = session.send(dict(command, revision=response['revision']))
                self.assertFalse(response.get('error'), response.get('error'))
            self.assertEqual(response['status'], 'finished')
            self.assertTrue({'draft', 'refresh', 'play', 'select', 'round'} <= kinds, kinds)
            self.assertEqual(len(response['placements']), 4)
            for row in session.result['rounds']:
                for ledger in row['ledger']:
                    self.assertEqual(ledger['income']-sum(a['cost'] for a in ledger['actions']), ledger['remaining'])
            session.thread.join(2)
        finally:
            self.finish(session, response)


if __name__ == '__main__': unittest.main()
