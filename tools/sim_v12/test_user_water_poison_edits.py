import unittest
from fractions import Fraction as Q
from unittest.mock import patch
from .test_current import setup,one
from .catalog import CARDS
from .mechanics import dynamic

class UserWaterPoisonEdits(unittest.TestCase):
 def test_active_water_and_poison_values(self):
  b=setup('血刃');s=b.players[0];s.hp=1000;s.layers['water']=10;s.active.add('潮汐之心')
  b.resolve_sequences(one(b));s=b.players[0]
  self.assertEqual((s.hp,s.tide),(950,30))
  b=setup('潮涌');b.players[0].hp=500;b.players[0].active.add('潮汐之心');b.resolve_sequences(one(b))
  self.assertEqual((b.players[0].hp,b.players[0].fx['tide_shield']),(540,50))
  b=setup('腐溃');b.players[1].poison=5;b.resolve_sequences(one(b))
  self.assertEqual(b.players[0].fx['main_attack'],28)
 def test_abyss_discrete_real_consumption_growth(self):
  b=setup('深渊');b.resolve_sequences(one(b));s=b.players[0];base=s.attack
  for spent,bonus in ((0,0),(49,0),(50,3),(99,3),(100,6)):
   s.spent_tide=spent;self.assertEqual(b.stats(0)[0],base+bonus)
  s.fx['abyss']=2;self.assertEqual(b.stats(0)[0],base+12)
  s.active.add('潮汐之心');s.tide=20;b.consume_tide(s,100)
  self.assertEqual(s.spent_tide,120);self.assertEqual(b.stats(0)[0],base+12)
 def test_pierce_uses_actual_shot_tide_boundary(self):
  for tide,expected in ((49,Q(2,5)),(50,Q(7,10))):
   b=setup('穿潮');s=b.players[0];s.active.add('潮汐之心');s.tide=tide
   b.resolve_sequences(one(b))
   with patch.object(b,'packet',wraps=b.packet) as packet:
    b.fire([0])
   mains=[c for c in packet.call_args_list if c.kwargs.get('main')]
   self.assertEqual(len(mains),1);self.assertEqual(mains[0].kwargs['def_pen'],expected)
   self.assertEqual(b.players[0].fx['main_def_pen'],0)
 def test_kuchao_applies_two_then_locks_branches_once(self):
  for initial in (0,1,3,4):
   b=setup('枯潮',('攻击强化','护盾'));s,e=b.players;e.poison=initial;s.active.add('毒爆')
   f=dynamic(b,s,'枯潮');self.assertEqual((f['fee'],f['draw']),(int(initial+2>=3),int(initial+2>=6)))
   seq=one(b);self.assertEqual(seq[0][0].flags['poison_fee'],initial+2>=3)
   b.resolve_sequences(seq);s,e=b.players
   self.assertEqual(e.poison,initial+2)
   self.assertEqual(s.attack,100+6+CARDS['枯潮'].attack)
   self.assertEqual(s.energy,100-CARDS['枯潮'].cost+int(initial+2>=3))
   self.assertEqual(len(s.hand),int(initial+2>=6))
  b=setup('枯潮',('攻击强化','护盾'),opponent='风之躯');b.players[1].poison=4
  seq=one(b,both=True);b.resolve_sequences(seq);s,e=b.players
  self.assertEqual((e.poison,len(s.hand)),(2,1))
  self.assertEqual(s.energy,101-CARDS['枯潮'].cost)
