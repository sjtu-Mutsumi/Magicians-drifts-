from fractions import Fraction as Q
from pathlib import Path
from collections import Counter
import json,math,unittest
from .catalog import CARDS,ROOT
from .panel_budget import RULES,bounds,value,role,reference_rarity,budget_group,multiplier
from .mechanics import static
from .tournament import arena_loss,DAMAGE
from .test_current import setup,one

class PanelAndDamageTests(unittest.TestCase):
 def test_selected_permanent_budget_uses_rare_reference_and_preserves_abyss(self):
  selected={'熔盾烈焰','奔流','层岩结构','风暴','缓释毒素'}
  self.assertEqual(set(RULES['mechanism_bands']['three_cost_permanent']['cards']),selected)
  for n,c in CARDS.items():
   if n in selected:
    self.assertEqual((c.cost,c.rarity),(3,'epic'))
    self.assertEqual(reference_rarity(n,c.rarity),'rare')
    self.assertEqual(multiplier(n),Q(4,5))
    self.assertEqual(bounds(n,c.rarity),(Q(6),Q(36,5)))
    self.assertEqual(value(c.cost,c.attack,c.defense),7)
   else:self.assertNotEqual(budget_group(n),'three_cost_permanent')
  self.assertEqual(bounds('深渊','epic'),(Q(19,2),Q(11)))
  self.assertEqual(value(CARDS['深渊'].cost,CARDS['深渊'].attack,CARDS['深渊'].defense),Q(43,4))

 def test_every_card_uses_uniform_rarity_and_role_budget(self):
  self.assertEqual(set(RULES['card_roles']),set(CARDS))
  self.assertEqual(Counter(RULES['card_roles'].values()),dict(numeric=119,strong=32,weak=32))
  for name,c in CARDS.items():
   with self.subTest(name=name):
    lo,hi=bounds(name,c.rarity);v=value(c.cost,c.attack,c.defense)
    self.assertLessEqual(lo,v);self.assertLessEqual(v,hi)
    self.assertEqual(static(name)['panel_role'],role(name))
  self.assertEqual(bounds('狂风','rare'),(Q(21,4),Q(63,10)))
  self.assertEqual(bounds('检索附魔','common'),(Q(9,2),Q(27,5)))

 def test_strong_precedence_covers_conditional_draw_and_discount(self):
  for name in ('枯潮','循毒','狂战','换页','禁咒目录','风痕回廊','导火索','连环潮','岩拳'):self.assertEqual(role(name),'strong')
  for name in ('不灭烈焰','重洗','盾之共鸣','灰烬循环','疾流','坚壁'):self.assertEqual(role(name),'weak')
  self.assertEqual(static('狂战')['draw'],1);self.assertEqual(static('换页')['draw'],1)

 def test_recalibrated_panels_are_applied_by_runtime(self):
  for name in ('掠影','狂风','速读','检索附魔'):
   b=setup(name,('攻击强化','防御强化','护盾'))
   b.resolve_sequences(one(b));s=b.players[0];c=CARDS[name]
   self.assertEqual((s.attack,s.defense),(100+c.attack,40+c.defense))

 def test_published_damage_matches_runtime_and_late_rounds_unchanged(self):
  published=json.loads((ROOT/'releases/deckbuilding-v1.2/damage.json').read_text(encoding='utf-8'))
  self.assertEqual(published['table'],DAMAGE)
  for r in range(1,81):
   expected=[3,1.] if r<=4 else [5,2.] if r<=8 else [7,2.] if r<=12 else [9,3.2] if r<=14 else [10,4.5] if r<=16 else [11,6.]
   self.assertEqual(DAMAGE[str(r)],expected)

 def test_actual_loss_boundaries_and_draws_and_wins(self):
  for r,losses in ((1,(7,9,9)),(4,(7,9,9)),(5,(14,17,18)),(8,(14,17,18)),(9,(16,19,20))):
   for hp,expected in zip((100,500,1000),losses):self.assertEqual(arena_loss(r,hp),expected)
   self.assertEqual(arena_loss(r,1000,won=True),0)
   self.assertEqual(arena_loss(r,-100,draw=True),DAMAGE[str(r)][0])
  self.assertEqual(arena_loss(81,500),arena_loss(80,500))
  with self.assertRaises(ValueError):arena_loss(0,100)

if __name__=='__main__':unittest.main()
