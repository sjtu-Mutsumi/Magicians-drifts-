import unittest
from copy import deepcopy
from .dual_train import initialize as dual
from .element_train import initialize,SIZES
from .exploration import purchase,deletion,quality
from .tournament import Player

class ElementTrainingTests(unittest.TestCase):
 def test_distinct_profiles_keep_independent_weights(self):
  base=dual()['small'];original=deepcopy(base)
  fire,earth=initialize(base,'fire'),initialize(base,'earth')
  self.assertGreater(fire['weights']['recycle'],base['weights']['recycle'])
  self.assertGreater(earth['weights']['protection'],base['weights']['protection'])
  fire['weights']['panel']=999
  self.assertNotEqual(earth['weights']['panel'],999);self.assertEqual(base,original)

 def test_element_and_route_intentions_do_not_make_other_cards_valuable(self):
  p=Player(0,'water');p.policy=initialize(dual()['small'],'water')
  self.assertEqual(quality(p,'战意'),-100)
  before=quality(p,'血潮');p.policy['route_intent']='血潮'
  self.assertEqual(quality(p,'血潮'),before+8)

 def test_larger_deck_bonus_affects_both_buy_and_delete_softly(self):
  p=Player(0,'water');p.spec='water';p.policy=initialize(dual()['small'],'water')
  before=purchase(p,'涌泉',2,None,[]);delete=deletion(p,'攻击强化',1)
  p.policy['size_exploration']['bonus']=6
  self.assertGreater(purchase(p,'涌泉',2,None,[]),before)
  self.assertLess(deletion(p,'攻击强化',1),delete)
  self.assertNotIn('max_size',p.policy['size_exploration'])

if __name__=='__main__':unittest.main()
