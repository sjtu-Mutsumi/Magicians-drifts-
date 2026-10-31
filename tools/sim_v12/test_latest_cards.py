import unittest
from .test_current import setup,one
from .catalog import CARDS

class LatestCards(unittest.TestCase):
 def test_bone_no_longer_doubles_decay_but_retains_last_layer(self):
  from .engine import INF
  b=setup('蚀骨');s=b.players[0]
  self.assertEqual(b.poison_interval(s,2),50)
  s.active.add('蚀骨');self.assertEqual(b.poison_interval(s,2),50)
  self.assertEqual(b.poison_interval(s,1),INF)
  s.fx['slow_poison']=1;self.assertEqual(b.poison_interval(s,2),60)
 def test_chain_tide_heals_without_followup(self):
  b=setup('连环潮');s=b.players[0];s.hp=500
  b.resolve_sequences(one(b));self.assertEqual(b.players[0].hp,520)
  self.assertEqual(b.players[0].fx['water_discount_heal'],0)
 def test_wind_cleanse_counts_types_and_poison_without_double_debuff(self):
  b=setup('风蚀');s,e=b.players;s.hp=500;s.poison=4;s.wind_marks=6
  e.active.add('万毒之源');e.layers['poison']=10
  b.temp(0,'fear',1);b.temp(0,'fear',1)
  b.resolve_sequences(one(b));s=b.players[0]
  self.assertEqual((s.hp,s.poison,s.wind_marks),(600,1,0))
 def test_dual_penetration_is_next_main_only(self):
  b=setup('破甲');b.resolve_sequences(one(b))
  p=b.missile_parameters(0,True);q=b.missile_parameters(0,False)
  self.assertEqual((float(p['def_pen']),float(p['pen'])),(.4,.4))
  self.assertEqual((q['def_pen'],q['pen']),(0,0))
  b.fire([0]);self.assertEqual(b.missile_parameters(0,True)['pen'],0)
 def test_core_heal_and_blood_lifesteal_rates(self):
  b=setup('余烬之核');s=b.players[0];s.hp=600;s.ember=1;s.active.update(('余烬之核','炽焰王冠'))
  b.consume_ember(s,1);self.assertEqual(s.hp,640)
  b=setup('血潮');s,e=b.players;s.hp=500;s.layers['water']=2;s.active.update(('血潮','潮汐之心'));e.defense=0
  b.wave([b.packet(0,100,'missile')]);self.assertEqual(s.hp,506)
 def test_retired_cards_absent_and_discard_rewards(self):
  self.assertNotIn('拆盾弹',CARDS);self.assertNotIn('翻找',CARDS)
  b=setup('坚壁',('攻击强化',));s=b.players[0];s.hand.append(s.pile.pop());b.resolve_sequences(one(b));self.assertEqual(b.players[0].shield,50)
  b=setup('包扎',('攻击强化',));s=b.players[0];s.hp=500;s.hand.append(s.pile.pop());b.resolve_sequences(one(b));self.assertEqual(b.players[0].hp,520)
