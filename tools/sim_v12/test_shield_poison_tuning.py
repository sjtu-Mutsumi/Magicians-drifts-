import unittest
from fractions import Fraction as Q
from .test_current import setup,one

class ShieldPoisonTuning(unittest.TestCase):
 def test_rock_armor_reads_effective_pre_card_defense_and_rounds(self):
  for defense,shield in ((0,10),(20,50),(40,90),(80,170),(Q(401,10),91)):
   b=setup('岩甲');b.players[0].defense=defense
   b.resolve_sequences(one(b));self.assertEqual(b.players[0].shield,shield)
  b=setup('岩甲');s,e=b.players;s.defense=80;s.poison=3;e.layers['poison']=10;e.active.add('万毒之源');s.shield=100
  b.resolve_sequences(one(b));self.assertEqual(b.players[0].shield,205)
 def test_mountain_body_consumes_real_total_shields_up_to_150(self):
  for hp,ordinary,temporary,final_hp,left in ((700,100,50,925,0),(700,20,30,775,0),(774,300,0,999,150),(999,300,0,1000,299),(1000,300,0,1000,300)):
   b=setup('山岳之躯');s=b.players[0];s.hp=hp;s.shield=ordinary
   if temporary:s.temporary=[[temporary,s.stage+1,0]]
   b.resolve_sequences(one(b));s=b.players[0]
   self.assertEqual((s.hp,s.shield_total),(final_hp,left))
 def test_topdeck_shield_is_40_with_or_without_legal_target_and_stone_is_100(self):
  b=setup('护盾强化',('攻击强化',));s=b.players[0];target=s.pile.pop(0);s.hand.append(target)
  b.resolve_sequences(one(b));s=b.players[0]
  self.assertEqual((s.shield,s.pile[0].uid),(40,target.uid))
  b=setup('护盾强化');b.resolve_sequences(one(b));self.assertEqual(b.players[0].shield,40)
  b=setup('磐石');b.resolve_sequences(one(b));self.assertEqual(b.players[0].shield,100)
