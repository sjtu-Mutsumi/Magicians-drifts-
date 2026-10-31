import re,unittest
from fractions import Fraction as Q
from unittest.mock import patch
from .test_current import setup,one
from .catalog import CARDS

class WindRebalance(unittest.TestCase):
 def test_submissile_card_text_and_runtime_match_for_each_card(self):
  expected={'风暴':(90,30),'风痕回廊':(100,30),'裂风':(70,20),'风切':(40,50),'风痕刻印':(60,30)}
  for name,pair in expected.items():
   m=re.search(r'固定(\d+)＋攻击力(\d+)%',CARDS[name].text)
   self.assertEqual(tuple(map(int,m.groups())),pair,name)
   b=setup(name);b.resolve_sequences(one(b));s=b.players[0]
   for fixed,ratio,marks,leech in s.permanent_subs+s.subs:
    self.assertEqual((fixed,ratio),(pair[0],Q(pair[1],100)),name)
 def test_combo_gap_boundary_uses_post_card_delay_and_fifty_percent_attack(self):
  for delay,fixed in ((-23,70),(-24,160)):
   b=setup('疾风连斩');s=b.players[0];s.fx['permanent_delay']=delay
   b.resolve_sequences(one(b));s=b.players[0]
   self.assertEqual(s.fx['permanent_delay'],delay-1)
   self.assertEqual(s.subs,[(fixed,Q(1,2),1,False)]*2)
 def test_real_corridor_hits_before_true_sub_and_full_mark_detonation(self):
  b=setup('风痕回廊');s,e=b.players;s.active.update(('天岚','风痕'));s.fx['shots']=2;e.wind_marks=4;e.hp=e.max_hp=10000;e.defense=0
  b.resolve_sequences(one(b));s,e=b.players;s.attack=100;s.permanent_subs=[(90,Q(3,10),1,False)]
  packets=[];wave=b.wave
  def capture(ps,**kw):packets.extend(ps);return wave(ps,**kw)
  with patch.object(b,'wave',side_effect=capture):b.fire([0])
  self.assertEqual([p['raw'] for p in packets],[100,120,130,130,40,210])
  self.assertEqual([p['kind'] for p in packets],['missile']*4+['true']*2)
  self.assertEqual(e.wind_marks,0)
 def test_structure_boosts_new_shields_additively_without_boosting_old(self):
  b=setup('层岩结构');b.resolve_sequences(one(b));s=b.players[0];s.shield=100
  b.shield_gain(s,100);b.flush_shields(s);self.assertEqual(s.shield,183)
  b.shield_gain(s,100,temporary=True);b.flush_shields(s)
  self.assertEqual((s.shield,s.temporary[-1][0]),(183,108))
  s.fx['shield_structure']=2;b.shield_gain(s,100,no_decay=True);b.flush_shields(s)
  self.assertEqual(s.shield,299)
