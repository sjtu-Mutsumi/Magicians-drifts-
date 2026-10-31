from pathlib import Path
import json,unittest
from .catalog import ROOT
from .engine import COMBAT_RULES,FATIGUE_START_AV
from .test_current import setup

class FatigueTests(unittest.TestCase):
 def battle(self,av):
  b=setup('护盾');b.time=av
  for s in b.players:
   s.pile.extend(s.hand);s.hand=[];s.hp=1000
  return b

 def test_fatigue_begins_at_1200_not_before(self):
  for av,expected in ((1100,1000),(1199,1000),(1200,830),(1300,820),(1500,800)):
   with self.subTest(action_value=av):
    b=self.battle(av);b.end_stage()
    self.assertEqual([s.hp for s in b.players],[expected,expected])

 def test_fatigue_uses_current_health_and_rounds_up(self):
  b=self.battle(1200);b.end_stage();b.time=1300;b.end_stage()
  self.assertEqual([s.hp for s in b.players],[680,680])

 def test_fatigue_retains_existing_guard_and_shield_reactions(self):
  b=self.battle(1200);s=b.players[0];s.hp=1;s.fx['lifeguard']=1
  b.end_stage();self.assertEqual((s.hp,s.guard_used),(1,True))
  b=self.battle(1200);s=b.players[0];s.fx['eternal']=1
  b.end_stage();self.assertEqual((s.hp,s.shield),(830,170))

 def test_published_combat_config_matches_runtime(self):
  self.assertEqual(FATIGUE_START_AV,1200)
  data=json.loads((ROOT/'docs/cards-v1.2/combat-rules.json').read_text(encoding='utf-8'))
  self.assertEqual(data,COMBAT_RULES)

if __name__=='__main__':unittest.main()
