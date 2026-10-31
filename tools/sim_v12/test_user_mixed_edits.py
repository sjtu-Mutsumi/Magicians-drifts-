import unittest
from .test_current import setup,one
from .catalog import CARDS
from .panel_budget import value,bounds

class UserMixedEdits(unittest.TestCase):
 def test_echo_cost_two_is_affordable_and_panel_is_within_budget(self):
  b=setup('回响之潮');s=b.players[0];s.energy=2;s.hp=500;s.active.add('潮汐之心')
  b.resolve_sequences(one(b));s=b.players[0]
  self.assertEqual((s.energy,s.hp,s.fx['tide_bonus'],s.fx['tide_record']),(0,600,50,50))
  c=CARDS['回响之潮'];self.assertEqual((c.cost,c.attack,c.defense),(2,7,6))
  lo,hi=bounds(c.name,c.rarity);self.assertTrue(lo<=value(c.cost,c.attack,c.defense)<=hi)
 def test_new_healing_uses_actual_loss_and_caps_recorded_tide(self):
  for name,hp,healed,tide in (('泉心',500,100,120),('泉心',990,2,3),('水幕',500,50,30),('水幕',990,10,6)):
   b=setup(name);s=b.players[0];s.hp=hp;s.layers['water']=10;s.active.add('潮汐之心')
   b.resolve_sequences(one(b));s=b.players[0]
   self.assertEqual((s.hp,s.tide),(hp+healed,tide),name)
 def test_heavy_shatter_120_and_actual_consumption_return_thirty_percent(self):
  b=setup('万钧碎盾');b.resolve_sequences(one(b));self.assertEqual(b.players[0].shield,120)
  b=setup('万钧碎盾');s,e=b.players;s.shield=100;s.layers['earth']=10;s.active.add('不周山');e.hp=e.max_hp=10000;e.defense=0
  b.resolve_sequences(one(b));s,e=b.players
  self.assertEqual((s.shield,e.hp),(59,9927))
 def test_earth_direct_records_twenty_percent_and_grip_uses_eighty_shield(self):
  b=setup('岩核重压');s=b.players[0];s.shield=100
  b.resolve_sequences(one(b));s=b.players[0];self.assertEqual(s.fx['direct'],20)
  s.shield=500;self.assertEqual(s.fx['direct'],20)
  b=setup('握盾');s=b.players[0];s.active.add('大地之握');s.shield=100
  b.resolve_sequences(one(b));s=b.players[0]
  self.assertEqual((s.shield,s.fx['grip_bonus'],s.fx['grip_loss']),(155,100,25))
