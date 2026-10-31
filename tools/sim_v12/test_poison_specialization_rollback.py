import unittest
from .test_current import setup

class PoisonSpecializationRollback(unittest.TestCase):
 def test_dynamic_point_five_defense_reduction_floors_clamps_and_clears(self):
  b=setup('万毒之源');s,e=b.players;s.active.add('万毒之源')
  for own,target,expected in ((0,0,(100,40)),(0,2,(95,35)),(3,1,(94,34)),(10,3,(77,20)),(100,5,(0,0)),(10,0,(100,40))):
   s.layers['poison']=own;e.poison=target
   self.assertEqual(b.stats(1),expected)
   self.assertEqual((e.attack,e.defense),(100,40))
 def test_bone_override_stays_one_point_two_and_point_nine(self):
  b=setup('万毒之源');s,e=b.players;s.active.update(('万毒之源','蚀骨'));s.layers['poison']=10;e.poison=3
  self.assertEqual(b.stats(1),(59,8))
  s.active.remove('蚀骨');self.assertEqual(b.stats(1),(77,20))
  e.poison=0;self.assertEqual(b.stats(1),(100,40))
