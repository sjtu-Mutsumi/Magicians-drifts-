import unittest
from tools.sim_v12.catalog import CARDS
from tools.sim_v12.test_current import setup

class FireClassificationTests(unittest.TestCase):
    def test_all_fire_cards_are_enchantments(self):
        for card in CARDS.values():
            if card.element == 'fire': self.assertTrue(card.enchantment, card.name)
            elif card.ability: self.assertFalse(card.enchantment, card.name)

    def test_ember_draw_discount_for_every_fire_card(self):
        for card in CARDS.values():
            if card.element != 'fire': continue
            with self.subTest(card=card.name):
                b=setup('余火', [card.name]); s=b.players[0]
                target=s.pile[0]
                b.play_local(0,s.hand[0].uid)
                self.assertIn(target,s.hand)
                self.assertEqual(b.cost(s,target),max(0,card.cost-1))

    def test_fire_ability_consumes_element_discount_and_counts(self):
        for name in ('炽焰王冠','灰烬循环','燃烧血统','余烬之核'):
            b=setup(name);s=b.players[0];x=s.hand[0];s.fx['discount_fire']=1
            self.assertEqual(b.cost(s,x),max(0,CARDS[name].cost-1))
            b.play_local(0,x.uid)
            self.assertEqual(s.fx['discount_fire'],0)
            self.assertEqual(s.counts['fire'],1)
            self.assertIn(x,s.removed)

if __name__=='__main__': unittest.main()
