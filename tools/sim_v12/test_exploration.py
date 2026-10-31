from copy import deepcopy
import unittest
from .exploration import INITIAL,ELEMENT_INITIAL,purchase,deletion,refresh
from .learning_policy import INITIAL_WEIGHTS,effective_weights
from .tournament import Player

class ExplorationTests(unittest.TestCase):
 def player(self):
  p=Player(0,'water');p.spec='water';p.points=3
  p.deck=[(n,None) for n in ('潮汐之心','生命之泉','深渊','奔流','活水','泉眼','灵感涌现','护盾')]
  p.policy=dict(kind='explorer',weights=deepcopy(INITIAL_WEIGHTS),construction=deepcopy(INITIAL),element_weights=deepcopy(ELEMENT_INITIAL))
  return p

 def test_no_target_length_or_size_cap_parameter(self):
  p=self.player();self.assertFalse({'target','min_size','max_size'}&INITIAL.keys())
  before=(purchase(p,'涌泉',2,None,[]),deletion(p,'泉眼',0),refresh(p,8,[],[]))
  for artificial_target in (8,12,15,18,1000):
   p.policy['construction']['target']=artificial_target
   self.assertEqual(before,(purchase(p,'涌泉',2,None,[]),deletion(p,'泉眼',0),refresh(p,8,[],[])))

 def test_elemental_weights_are_small_and_do_not_mutate_config(self):
  p=self.player();original=deepcopy(p.policy)
  w=effective_weights(p.policy,'water');self.assertGreater(w['layer'],p.policy['weights']['layer'])
  w['layer']=1000;self.assertEqual(p.policy,original)

 def test_price_opportunity_and_delete_fees_are_counted(self):
  p=self.player()
  self.assertGreater(purchase(p,'涌泉',0,None,[]),purchase(p,'涌泉',2,None,[]))
  self.assertGreater(deletion(p,'泉眼',0),deletion(p,'泉眼',1))

 def test_medium_restart_encourages_useful_purchase_and_saves_delete_points(self):
  from .medium_train import restart
  p=self.player();before_purchase=purchase(p,'涌泉',2,None,[]);before_delete=deletion(p,'泉眼',1)
  p.policy=restart(p.policy)
  self.assertGreater(purchase(p,'涌泉',2,None,[]),before_purchase)
  self.assertLess(deletion(p,'泉眼',1),before_delete)
  self.assertEqual(p.policy['breadth']['target'],15)

 def test_medium_restart_keeps_battle_weights_frozen_and_has_no_size_cap(self):
  from .medium_train import restart
  p=self.player();original=deepcopy(p.policy);p.policy=restart(original)
  self.assertEqual(p.policy['weights'],original['weights'])
  self.assertEqual(p.policy['element_weights'],original['element_weights'])
  self.assertFalse({'min_size','max_size'}&p.policy['breadth'].keys())
  # Even above 15, a sufficiently strong new specialization/route remains legal;
  # legality and the unchanged minimum are enforced separately by tournament.
  self.assertIsInstance(purchase(p,'回响之潮',3,None,[]),float)

 def test_role_preferences_change_construction_without_mutating_battle_weights(self):
  from .exploration import quality
  p=self.player();weights=deepcopy(p.policy['weights'])
  normal=quality(p,'泉眼');flow=quality(p,'活水')
  p.policy['role_preferences']=dict(numeric=1.15,strong=.85,weak=1.)
  self.assertGreater(quality(p,'泉眼'),normal);self.assertLess(quality(p,'活水'),flow)
  self.assertEqual(p.policy['weights'],weights)

 def test_dual_initializations_retain_distinct_soft_preferences(self):
  from .dual_train import initialize
  models=initialize();m,s=models['medium'],models['small']
  self.assertEqual(m['breadth']['target'],15)
  self.assertNotIn('breadth',s)
  self.assertGreater(m['role_preferences']['numeric'],m['role_preferences']['strong'])
  self.assertGreater(s['role_preferences']['strong'],s['role_preferences']['numeric'])

if __name__=='__main__':unittest.main()
