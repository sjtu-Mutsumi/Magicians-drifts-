import unittest
from copy import deepcopy
from .element_paired_train import paired

class PairedTrainingTests(unittest.TestCase):
 def pair(self):
  a=dict(seed=1,pair_side='candidate',comparison=dict(first_seats=[0]),rounds=[],placements=[dict(pid=i,rank=i+1,score=s) for i,s in enumerate((3,1,-1,-3))])
  b=deepcopy(a);b['pair_side']='control'
  return a,b
 def test_identical_focal_outcomes_have_zero_delta(self):
  a,b=self.pair();self.assertEqual(paired([a,b])['mean'],0)
 def test_comparison_uses_focal_outcomes_and_reverses_sign(self):
  a,b=self.pair();b['placements'][0].update(rank=4,score=-3)
  self.assertAlmostEqual(paired([a,b])['mean'],2/3)
  a['pair_side'],b['pair_side']='control','candidate'
  self.assertAlmostEqual(paired([a,b])['mean'],-2/3)
 def test_missing_pair_is_rejected(self):
  a,b=self.pair()
  with self.assertRaises(AssertionError):paired([a])
