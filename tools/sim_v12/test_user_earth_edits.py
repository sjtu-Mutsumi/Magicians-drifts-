import unittest
from pathlib import Path
from unittest.mock import patch
from .test_current import setup,one
from .catalog import CARDS,load

class UserEarthEdits(unittest.TestCase):
 def test_spaced_markdown_tables_load_without_changing_identity(self):
  original=Path.read_text
  def spaced(p,*a,**kw):
   t=original(p,*a,**kw)
   if p.name!='earth.md':return t
   return '\n'.join('|'+'|'.join('  '+x.strip()+'  ' for x in l.split('|')[1:-1])+'|' if l.startswith('|') else l for l in t.splitlines())
  with patch.object(Path,'read_text',spaced):self.assertEqual(load(),CARDS)
 def test_new_shield_amounts_and_pulse(self):
  for name,ordinary,temporary in (('不坏壁垒',220,0),('回震岩甲',100,0),('岩潮壁垒',0,140),('临时壁垒',0,120),('守岩',80,0),('磐岩壁',60,0),('裂岩护壁',40,0),('坚岩',70,0)):
   b=setup(name);b.resolve_sequences(one(b));s=b.players[0]
   self.assertEqual((s.shield,sum(x[0] for x in s.temporary)),(ordinary,temporary),name)
  b=setup('回岩');b.resolve_sequences(one(b));self.assertEqual(b.players[0].fx['return_once'],80)
  b=setup('盾脉');b.resolve_sequences(one(b));s=b.players[0];self.assertEqual(s.shield,80)
  b.shield_gain(s,100,no_decay=True);b.flush_shields(s);self.assertEqual(s.shield,220)
  b.shield_gain(s,100,no_decay=True);b.flush_shields(s);self.assertEqual((s.shield,s.fx['shield_pulse']),(360,0))
 def test_stone_mark_mixes_ordinary_decay_and_conditional_temporary_shield(self):
  for broken in (0,1):
   b=setup('碎岩印记');s=b.players[0];s.shield=100;s.fx['broken']=broken
   b.resolve_sequences(one(b));s=b.players[0]
   self.assertEqual((s.shield,s.defense),(135,40+5*broken))
   self.assertEqual(sum(x[0] for x in s.temporary),40*broken)
   if broken:self.assertEqual(s.temporary[0][1],s.stage+1)
 def test_mountain_body_consumes_by_one_point_five_and_obeys_caps(self):
  for hp,expected_hp,expected_shield in ((700,925,150),(699,924,150),(990,1000,293),(1000,1000,300)):
   b=setup('山岳之躯');s=b.players[0];s.hp=hp;s.shield=300
   b.resolve_sequences(one(b));s=b.players[0]
   self.assertEqual((s.hp,s.shield),(expected_hp,expected_shield))
