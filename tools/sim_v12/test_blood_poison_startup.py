import unittest
from unittest.mock import patch
from .test_current import setup,one
from .catalog import CARDS

def limited_plan(b,limits):
 used=[0,0]
 def choose(shadow,i):
  if used[i]>=limits[i] or not shadow.players[i].hand:return None
  used[i]+=1;return shadow.players[i].hand[0]
 with patch('tools.sim_v12.policy.choose_play',side_effect=choose):return b.plan_stage()

class BloodPoisonStartup(unittest.TestCase):
 def test_rollback_activation_never_grants_poison_across_replay_and_second_instance(self):
  b=setup('毒爆',('毒爆',));base=100;c=CARDS['毒爆']
  for cast in range(3):
   if cast:
    s=b.players[0]
    # Force an ability replay for guard coverage; normal play removes it.
    s.hand=[s.removed.pop()] if cast==1 else [s.pile.pop(0)]
    s.energy=100
   seq=one(b);self.assertEqual(b.players[1].poison,0)
   b.resolve_sequences(seq);s,e=b.players
   self.assertEqual((e.poison,s.attack),(0,base+(cast+1)*c.attack))
  self.assertEqual(s.layers['poison'],2*c.layers)
 def test_rollback_planning_cannot_invent_initial_poison_or_enable_extra_draw(self):
  b=setup('毒爆',('毒涌','攻击强化','护盾'));s=b.players[0];s.hand.append(s.pile.pop(0))
  seq=limited_plan(b,(2,0));self.assertEqual(seq[0][1].flags['poison_draw'],False)
  self.assertEqual(b.players[1].poison,0)
  b.resolve_sequences(seq);s,e=b.players
  self.assertEqual((e.poison,len(s.hand)),(0,1))
  self.assertEqual(s.attack,100+CARDS['毒爆'].attack+CARDS['毒涌'].attack)
 def test_same_position_cleanse_preserves_new_poison_and_unreached_tail_has_none(self):
  b=setup('毒爆',opponent='风之躯');b.players[1].poison=4
  b.resolve_sequences(one(b,both=True));self.assertEqual(b.players[1].poison,0)
  b=setup('短促',('毒爆',),opponent='潮汐风暴');s,e=b.players;s.hand.append(s.pile.pop(0));s.hp=1;e.active.add('潮汐之心');e.tide=1000
  seq=limited_plan(b,(2,1));self.assertEqual(len(seq[0]),2)
  b.resolve_sequences(seq);s,e=b.players
  self.assertNotIn('毒爆',s.active);self.assertEqual((e.poison,b.executed['毒爆']),(0,0))
 def test_blood_protection_reads_current_life_after_payment(self):
  b=setup('血潮');s,e=b.players;s.active.add('血潮');s.defense=e.defense=0
  b.fire([0]);self.assertEqual(s.hp,900)
  b.wave([b.packet(1,100,'missile',main=True)]);self.assertEqual(s.hp,827)
  # Independent attacks must use the newly reduced current life each time.
  s.hp=500;b.wave([b.packet(1,100,'missile',main=True)]);self.assertEqual(s.hp,415)
  b.wave([b.packet(1,100,'missile',main=True)]);self.assertEqual(s.hp,328)
