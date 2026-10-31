import unittest
from types import SimpleNamespace
from unittest.mock import patch
from .catalog import CARDS,START
from .panel_budget import role,bounds,value
from .tournament import Player,deletion_rules,delete_phase

class EarlyDeletionTests(unittest.TestCase):
 def test_current_phase_boundaries(self):
  for r in range(1,101):
   expected=(0,()) if r<=3 else (1,(1,)) if r<=6 else (2,(0,1))
   self.assertEqual(deletion_rules(r),expected)
  with self.assertRaises(ValueError):deletion_rules(0)

 def test_runtime_deletion_actually_charges_and_obeys_limits(self):
  for r,points,expected_count,expected_cost in ((3,2,0,0),(4,0,0,0),(4,1,1,1),(6,2,1,1),(7,0,1,0),(7,1,2,1),(8,2,2,1)):
   with self.subTest(round=r,points=points):
    p=Player(0,'fire');p.points=points;p.deck=[(n,None) for n in (*START,'猛火弹','坚岩')]
    pool=SimpleNamespace(remove=lambda token:None)
    with patch('tools.sim_v12.tournament.is_thin',return_value=True),patch('tools.sim_v12.tournament.deletion_value',return_value=100):delete_phase(p,pool,r)
    self.assertEqual(len(p.actions),expected_count)
    self.assertEqual(sum(a['cost'] for a in p.actions),expected_cost)
    self.assertEqual(p.points,points-expected_cost)
    self.assertEqual(len(p.deck),10-expected_count)

 def test_instant_combat_examples_use_high_numeric_budget(self):
  for name in ('猛火弹','坚岩','攻击强化','泉眼','万蚀'):
   c=CARDS[name];lo,hi=bounds(name,c.rarity)
   self.assertEqual(role(name),'numeric')
   self.assertGreaterEqual(value(c.cost,c.attack,c.defense),lo+(hi-lo)*3/4)
   self.assertLessEqual(value(c.cost,c.attack,c.defense),hi)
  self.assertEqual((CARDS['猛火弹'].attack,CARDS['猛火弹'].defense),(6,0))

 def test_war_intent_and_unselected_persistent_growth_stay_unchanged(self):
  expected={'战意':(8,0),'深渊':(8,9),'回震岩甲':(13,1),'山岳壁垒':(1,9),'蚀创':(6,5),'血刃':(2,4),'烈风':(8,1),'岩核重压':(8,1),'盾脉':(1,7)}
  for name,panel in expected.items():self.assertEqual((CARDS[name].attack,CARDS[name].defense),panel)

if __name__=='__main__':unittest.main()
