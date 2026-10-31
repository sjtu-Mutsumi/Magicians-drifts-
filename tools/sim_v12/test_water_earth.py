import unittest
from .test_current import setup,one

class WaterEarth(unittest.TestCase):
 def test_flat_tide_coefficients_and_conditional_branches(self):
  for name,hp,initial,expected in (('洪潮',1000,0,80),('洪潮',1000,100,160),('洪流',1000,0,100),('潮汐回廊',1000,0,80),('血怒',1000,0,80),('血怒',500,0,120)):
   b=setup(name);s=b.players[0];s.hp=hp;s.tide=initial;s.layers['water']=10;s.active.add('潮汐之心')
   b.resolve_sequences(one(b));self.assertEqual(b.players[0].tide,expected,name)
  b=setup('静水');s=b.players[0];s.hp=500;s.layers['water']=10;s.active.add('潮汐之心');b.resolve_sequences(one(b));s=b.players[0]
  b.heal(s,1);self.assertEqual(s.tide,67)
 def test_earth_base_and_enhanced_burst_coefficients(self):
  b=setup('不周山');s,e=b.players;s.layers['earth']=10;e.defense=0
  self.assertEqual(b.burst(0,100)['raw'],25)
  s.active.add('反震岩核');self.assertEqual(b.burst(0,100)['raw'],35)
 def test_spring_gap_and_water_lifesteal_override(self):
  b=setup('生命之泉');s=b.players[0];s.layers['water']=10;s.active.add('生命之泉')
  self.assertEqual(b.gap(s),35);self.assertEqual(float(b.missile_parameters(0)['pen']),.2)
  for blood,expected in ((False,512),(True,512)):
   b=setup('潮汐之心');s,e=b.players;s.hp=500;s.layers['water']=2;s.specialization='water';s.active.add('潮汐之心');e.defense=0
   if blood:s.active.add('血潮')
   b.wave([b.packet(0,100,'missile')]);self.assertEqual(s.hp,expected)
