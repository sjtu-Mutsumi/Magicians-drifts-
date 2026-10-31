import unittest
from fractions import Fraction as Q
from unittest.mock import patch
from .test_current import setup,one

class SpringRefinement(unittest.TestCase):
 def test_gap_activation_layers_and_minimum(self):
  for layers,expected in ((0,44),(1,43),(10,34),(100,1)):
   b=setup('生命之泉');s=b.players[0];s.layers['water']=layers
   self.assertEqual(b.gap(s),50)
   b.resolve_sequences(one(b));self.assertEqual(b.players[0].layers['water'],layers+1)
   self.assertEqual(b.gap(b.players[0]),expected)
 def test_penetration_applies_to_main_and_sub_without_multiplying_tide(self):
  b=setup('生命之泉');s,e=b.players;s.active.update(('生命之泉','潮汐之心'));s.tide=100;e.defense=0;e.shield=1000
  self.assertEqual((b.missile_parameters(0)['pen'],b.missile_parameters(0,True)['pen']),(Q(1,5),Q(1,5)))
  with patch.object(b,'packet',wraps=b.packet) as packet:b.fire([0])
  main=next(c for c in packet.call_args_list if c.kwargs.get('main'))
  self.assertEqual(main.args[1],200)
  self.assertEqual((e.hp,e.shield,s.spent_tide,s.tide),(960,840,100,0))
