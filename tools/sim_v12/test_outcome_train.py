import unittest
from .outcome_train import initialize,objective,reward_rows,propose
from .dual_train import initialize as dual_initialize

class OutcomeTests(unittest.TestCase):
 def test_neutral_start_retains_mechanisms_and_removes_preferences(self):
  source=dual_initialize()['small'];cfg=initialize(source)
  self.assertNotIn('role_preferences',cfg);self.assertNotIn('breadth',cfg)
  for k in ('weights','construction','element_weights'):self.assertEqual(cfg[k],source[k])
  self.assertIn('role_preferences',source)

 def test_updates_are_bounded_without_reintroducing_size_or_roles(self):
  initial=initialize(dual_initialize()['small']);best=initial
  for step in range(1,11):best=propose(best,initial,step,{},296000)
  self.assertNotIn('breadth',best);self.assertNotIn('role_preferences',best)
  for section in ('weights','construction'):
   for k,v in best[section].items():self.assertGreaterEqual(v,.65*initial[section][k]-.000001);self.assertLessEqual(v,1.5*initial[section][k]+.000001)

 def test_reward_components_have_equal_scale_and_swap_sign(self):
  r=dict(comparison=dict(first_seats=[0,1]),placements=[dict(pid=i,rank=i+1,score=s) for i,s in enumerate((3,1,-1,-3))],rounds=[])
  x=objective([r]);self.assertAlmostEqual(x['composite']['mean'],(.5+0+4/6)/3)
  r['comparison']['first_seats']=[2,3]
  self.assertAlmostEqual(objective([r])['composite']['mean'],-x['composite']['mean'])

if __name__=='__main__':unittest.main()
