import unittest
from .test_current import setup

class WaterLifeOnly(unittest.TestCase):
 def test_water_heals_life_only_with_overkill_cap_and_heal_block(self):
  for shield,target_hp,source_hp,blocked,blood,expected_hp,expected_tide in (
   (1000,1000,500,False,False,500,0),
   (60,1000,500,False,False,524,15),
   (0,5,500,False,False,503,2),
   (60,1000,999,False,False,1000,1),
   (60,1000,500,True,False,512,8),
   (60,1000,500,False,True,524,15)):
   b=setup('潮汐之心');s,e=b.players;s.hp=source_hp;s.layers['water']=10;s.specialization='water';s.active.add('潮汐之心');e.defense=0;e.shield=shield;e.hp=target_hp
   if blocked:b.temp(0,'heal_block',50)
   if blood:s.active.add('血潮')
   b.wave([b.packet(0,100,'missile')])
   self.assertEqual((s.hp,s.tide),(expected_hp,expected_tide))
  b=setup('攻击强化');s,e=b.players;s.hp=500;s.layers['water']=10;e.defense=0;e.shield=60
  b.wave([b.packet(0,100,'missile')]);self.assertEqual(s.hp,512)
 def test_actual_healing_records_six_or_ten_percent_per_layer(self):
  for echo,hp,expected in ((False,500,60),(True,500,100),(False,990,6),(True,990,10)):
   b=setup('潮汐之心');s=b.players[0];s.hp=hp;s.layers['water']=10;s.active.add('潮汐之心')
   if echo:s.active.add('潮汐回响')
   b.heal(s,100);self.assertEqual(s.tide,expected)
 def test_core_main_threshold_140_includes_shield_but_not_sub(self):
  for damage,main,expected in ((139,True,0),(140,True,1),(140,False,0)):
   b=setup('余烬之核');s,e=b.players;s.active.update(('余烬之核','炽焰王冠'));e.shield=1000;e.defense=0
   b.wave([b.packet(0,damage,'missile',main=main)])
   self.assertEqual(s.ember,expected)
