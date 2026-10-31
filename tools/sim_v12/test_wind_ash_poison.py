import unittest
from fractions import Fraction as Q
from unittest.mock import patch
from .test_current import setup,one

class WindAshPoison(unittest.TestCase):
 def test_blood_uses_common_lifesteal_and_never_heals_shield_damage(self):
  for spec in ('','water'):
   for shield in (0,1000):
    b=setup('血潮');s,e=b.players;s.hp=500;s.layers['water']=10;s.specialization=spec;s.active.update(('血潮','潮汐之心'));e.shield=shield;e.defense=0
    b.wave([b.packet(0,100,'missile')])
    self.assertEqual(s.hp,500+(0 if shield else 60 if spec else 30))
 def test_submissile_values_and_conditional_marks(self):
  for name,fixed,ratio,count in (('风暴',90,Q(3,10),1),('风痕回廊',100,Q(3,10),2),('裂风',70,Q(1,5),2),('风切',40,Q(1,2),1),('风痕刻印',60,Q(3,10),1)):
   for marked in (False,True):
    b=setup(name,('攻击强化','护盾'));s=b.players[0]
    if marked:s.active.add('风痕')
    b.resolve_sequences(one(b));s=b.players[0];subs=s.permanent_subs if name=='风暴' else s.subs
    marks=2 if marked and name in ('风痕回廊','风痕刻印') else 1
    self.assertEqual(subs,[(fixed,ratio,marks,name=='风切')]*count,name)
 def test_arrow_ignore_multiplies_penetration_and_strip_requires_actual_shield(self):
  b=setup('穿云箭');s,e=b.players;s.active.update(('天岚','穿云箭'));s.layers['wind']=10;e.defense=100;e.shield=1000
  params=b.missile_parameters(0);self.assertEqual((params['def_pen'],params['ignore'],params['shield_factor']),(Q(25,100),Q(15,100),Q(120,100)))
  # Remaining defense=100*.75*.85=63.75; effective hit=floor(100-63.75)=36.
  b.wave([b.packet(0,100,'missile',**params)])
  self.assertEqual(e.hp,991);self.assertEqual(e.shield,968)
  for shield,expected,delay in ((100,60,-2),(0,0,0)):
   b=setup('裂空穿云');s,e=b.players;e.shield=shield
   b.resolve_sequences(one(b));s,e=b.players
   with patch.object(b,'wave'):b.fire([0])
   self.assertEqual((e.shield,s.fx['permanent_delay']),(expected,delay))
 def test_ash_three_reward_cap_resets_without_carrying_overflow(self):
  b=setup('灰烬循环');s=b.players[0];s.active.update(('灰烬循环','炽焰王冠'));s.ember=10
  b.consume_ember(s,7);self.assertEqual((s.pending['draw'],s.pending['energy'],s.fx['ash_reward']),(3,3,3))
  b.consume_ember(s,3);self.assertEqual((s.pending['draw'],s.pending['energy']),(3,3))
  b.begin_stage();self.assertEqual((s.energy,s.fx['ash_reward']),(6,0))
  self.assertEqual((s.pending['draw'],s.pending['energy']),(0,0))
  s.ember=1;b.consume_ember(s,1);self.assertEqual((s.pending['draw'],s.pending['energy']),(1,1))
