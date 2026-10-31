import hashlib,json,tempfile,unittest
from pathlib import Path
from .six_styles import IDS,ROOT,load_policy,available_styles
from .six_train import initial_models

class SixStyleTests(unittest.TestCase):
 def bundle(self,path):
  models={}
  for style,cfg in initial_models().items():
   raw=json.dumps(cfg,ensure_ascii=False).encode();(path/f'{style}.json').write_bytes(raw)
   models[style]=dict(file=f'{style}.json',sha256=hashlib.sha256(raw).hexdigest())
  manifest=dict(game_version='1.2',cards_sha256=hashlib.sha256((ROOT/'releases/v1.2/cards.json').read_bytes()).hexdigest(),models=models)
  (path/'manifest.json').write_text(json.dumps(manifest),encoding='utf-8')
 def test_all_six_and_flexible_intent_boundary(self):
  with tempfile.TemporaryDirectory() as d:
   p=Path(d);self.bundle(p)
   self.assertEqual(set(available_styles(p)['models']),set(IDS))
   for e in IDS:
    c=load_policy(e,p);self.assertEqual(c.get('element_intent'),None if e=='flexible' else e)
 def test_corrupt_policy_is_rejected(self):
  with tempfile.TemporaryDirectory() as d:
   p=Path(d);self.bundle(p);(p/'fire.json').write_text('{}')
   with self.assertRaises(ValueError):load_policy('fire',p)
 def test_unknown_style_rejected(self):
  with self.assertRaises(ValueError):load_policy('unknown')
