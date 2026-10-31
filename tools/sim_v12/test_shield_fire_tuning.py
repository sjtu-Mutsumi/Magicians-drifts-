import unittest
from fractions import Fraction as Q
from unittest.mock import patch
from .test_current import setup,one
from .catalog import CARDS
from .mechanics import static

class ShieldFireTuning(unittest.TestCase):
 def test_two_cost_rare_direct_shields_and_mountain_branches(self):
  for name,initial,expected in (('回岩',0,150),('山岳',0,200),('山岳',100,195),('岩核共鸣',0,100),('握盾',0,80),('盾脉',0,80)):
   b=setup(name);b.players[0].shield=initial;b.resolve_sequences(one(b))
   self.assertEqual(b.players[0].shield,expected,name)
  self.assertEqual(static('壁垒之心')['search'],0)
 def test_rebound_armor_grants_100_and_returns_30_on_enemy_break(self):
  b=setup('回震岩甲');b.resolve_sequences(one(b));s=b.players[0]
  self.assertEqual((s.shield,s.fx['return_flat']),(100,30))
  s.defense=0;b.wave([b.packet(1,500,'missile')]);self.assertEqual(s.shield,30)
 def test_fire_gates_read_pre_position_layers_and_fuse_bonus(self):
  for layers,ember,draw in ((7,0,1),(8,1,0)):
   b=setup('余烬刻度');s=b.players[0];s.layers['fire']=layers;s.active.add('炽焰王冠');b.resolve_sequences(one(b));s=b.players[0]
   self.assertEqual((s.ember,s.pending['draw']),(ember,draw))
  for layers,bonus in ((3,0),(4,4)):
   b=setup('战意');b.players[0].layers['fire']=layers;b.resolve_sequences(one(b))
   self.assertEqual(b.players[0].attack,100+CARDS['战意'].attack+bonus)
  b=setup('导火索');b.resolve_sequences(one(b));expected=b.stats(0)[0]*Q(115,100);packets=[]
  with patch.object(b,'wave',side_effect=lambda ps:packets.extend(ps)):b.fire([0])
  self.assertEqual(packets[0]['raw'],expected)
 def test_cloud_armor_break_applies_six_to_main_sub_and_true_sub(self):
  b=setup('穿云破甲');b.resolve_sequences(one(b));s,e=b.players;e.defense=1000
  s.active.add('天岚');s.fx['shots']=2;s.permanent_subs=[(10,Q(0),1,False)]
  b.fire([0]);self.assertEqual(e.fx['permanent_def_down'],18)
  b.wave([b.packet(0,10,'missile')]);self.assertEqual(e.fx['permanent_def_down'],18)
