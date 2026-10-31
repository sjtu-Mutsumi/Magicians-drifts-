from fractions import Fraction as Q
from unittest.mock import patch
import unittest
from .test_current import setup,one

class RouteBalanceTests(unittest.TestCase):
 def test_fire_layers_apply_new_final_damage_rates_to_all_source_packets(self):
  for specialization,damage in (('',115),('fire',130)):
   for kind in ('missile','true','poison','burst'):
    b=setup('攻击强化');s,e=b.players;s.layers['fire']=10;s.specialization=specialization;e.defense=0
    b.wave([b.packet(0,100,kind)])
    self.assertEqual(e.hp,1000-damage)

 def test_wind_true_sub_is_last_and_detonates_marks_from_every_earlier_sub(self):
  b=setup('风痕');s,e=b.players;s.active.update(('天岚','风痕'));s.fx['shots']=2;e.wind_marks=4;e.defense=0;e.hp=e.max_hp=10000
  s.permanent_subs=[(10,Q(0),2,False)];s.subs=[(20,Q(0),3,False)]
  packets=[];original=b.wave
  def capture(ps,**kw):
   packets.extend(ps);return original(ps,**kw)
  with patch.object(b,'wave',side_effect=capture):b.fire([0])
  self.assertEqual([p['kind'] for p in packets],['missile','missile','missile','true','true'])
  self.assertEqual([p['raw'] for p in packets],[100,10,20,40,210])
  self.assertEqual(e.wind_marks,0)
  b=setup('天岚');s=b.players[0];s.active.add('天岚');s.fx['shots']=2;packets=[]
  with patch.object(b,'wave',side_effect=lambda ps:packets.extend(ps)):b.fire([0])
  self.assertEqual([p['raw'] for p in packets],[100,20])

 def test_blood_tide_surge_forty_percent_boundary(self):
  for hp,expected in ((400,180),(401,120)):
   b=setup('血潮汹涌');s=b.players[0];s.hp=hp;s.layers['water']=10;s.active.add('潮汐之心')
   b.resolve_sequences(one(b));self.assertEqual(b.players[0].tide,expected)

 def test_earth_grip_uses_recorded_decay_loss(self):
  b=setup('大地之握');s=b.players[0];s.active.add('大地之握');s.fx['grip_loss']=25;s.layers['earth']=10
  packets=[]
  with patch.object(b,'wave',side_effect=lambda ps:packets.extend(ps)):b.fire([0])
  self.assertEqual(packets[0]['raw'],110)

 def test_bastion_reflects_two_percent_per_layer(self):
  b=setup('攻击强化');s,e=b.players;s.defense=0;e.defense=0
  e.shield=1000;e.layers['earth']=10;e.active.update(('不周山','壁垒之心'))
  b.wave([b.packet(0,100,'missile')])
  self.assertEqual(s.hp,988)
  self.assertEqual(e.shield,915)

 def test_wind_layer_gap_and_activation_are_separate(self):
  b=setup('天岚');s=b.players[0];s.layers['wind']=10
  self.assertEqual(b.gap(s),45);self.assertEqual(b.missile_parameters(0)['pen'],0)
  s.specialization='wind';self.assertEqual(b.gap(s),40)
  s.active.add('天岚');self.assertEqual(b.gap(s),40)
  self.assertEqual(b.missile_parameters(0)['pen'],Q(25,100))
  s.active.add('疾风之刃');self.assertEqual(b.gap(s),31)
  s.active.add('穿云箭');p=b.missile_parameters(0)
  self.assertEqual((p['def_pen'],p['ignore'],p['shield_factor']),(Q(25,100),Q(15,100),Q(120,100)))
  s.layers['wind']=100;self.assertEqual(b.missile_parameters(0)['def_pen'],1)

 def test_poison_damage_and_dynamic_debuff(self):
  b=setup('万蚀');s,e=b.players;s.layers['poison']=10;e.poison=3
  self.assertEqual(b.poison_packets(0)[0]['raw'],60)
  s.specialization='poison';self.assertEqual(b.poison_packets(0)[0]['raw'],120)
  s.active.add('蚀命');self.assertEqual(b.poison_packets(0)[0]['raw'],420)
  s.active.add('万毒之源');self.assertEqual(b.stats(1),(77,20))
  s.active.add('蚀骨');self.assertEqual(b.stats(1),(59,8))

 def test_poison_growth_counts_actual_changes_both_directions(self):
  b=setup('毒龙');s,e=b.players;s.active.add('毒爆')
  b.change_poison(s,e,3);self.assertEqual(s.attack,109)
  b.change_poison(s,e,-10);self.assertEqual(s.attack,118)
  b.change_poison(s,e,-1);self.assertEqual(s.attack,118)

 def test_fire_bloodline_and_blood_tide_packets(self):
  b=setup('燃烧血统');s,e=b.players;s.active.update(('燃烧血统','炽焰王冠'));s.specialization='fire';s.layers['fire']=10;s.ember=2
  packets=[]
  with patch.object(b,'wave',side_effect=lambda ps:packets.extend(ps)):b.fire([0])
  self.assertEqual(packets[0]['raw'],180)
  self.assertEqual(packets[0]['final'],Q(13,10)*Q(16,10))
  b=setup('血潮');s,e=b.players;s.active.add('血潮');packets=[]
  with patch.object(b,'wave',side_effect=lambda ps:packets.extend(ps)):b.fire([0])
  self.assertEqual((s.hp,packets[0]['raw']),(900,200))

 def test_wind_marks_sum_and_true_sub_are_separate(self):
  b=setup('风痕');s,e=b.players;s.active.update(('天岚','风痕'));s.fx['shots']=2;e.wind_marks=6
  packets=[]
  with patch.object(b,'wave',side_effect=lambda ps:packets.extend(ps)):b.fire([0])
  self.assertEqual([p['raw'] for p in packets],[100,40,102])
  self.assertEqual(e.wind_marks,0)

 def test_echo_refunds_fifteen_percent_of_actual_spent_tide(self):
  b=setup('潮汐回响');s=b.players[0];s.active.update(('潮汐回响','潮汐之心'));s.tide=100
  with patch.object(b,'wave'):b.fire([0])
  self.assertEqual((s.spent_tide,s.tide),(100,25))

 def test_echo_healing_records_seven_percent_per_water_layer(self):
  b=setup('潮汐回响');s=b.players[0];s.active.update(('潮汐回响','潮汐之心'));s.hp=500;s.layers['water']=10
  b.heal(s,100);self.assertEqual(s.tide,100)

 def test_grip_scales_with_current_earth_layers(self):
  for layers,raw in ((0,100),(5,105),(20,120)):
   b=setup('大地之握');s=b.players[0];s.active.add('大地之握');s.fx['grip_loss']=25;s.layers['earth']=layers
   packets=[]
   with patch.object(b,'wave',side_effect=lambda ps:packets.extend(ps)):b.fire([0])
   self.assertEqual(packets[0]['raw'],raw)

 def test_ash_rewards_cap_across_multiple_ember_consumptions(self):
  b=setup('灰烬循环');s=b.players[0];s.active.update(('灰烬循环','炽焰王冠'));s.ember=5
  b.consume_ember(s,5);self.assertEqual((s.pending['draw'],s.pending['energy']),(3,3))
  s.ember=5;b.consume_ember(s,5);self.assertEqual((s.pending['draw'],s.pending['energy']),(3,3))

 def test_blood_tide_protection_is_dynamic_and_only_against_main_missiles(self):
  for hp,kind,main,expected_loss in ((1000,'missile',True,70),(500,'missile',True,85),(100,'missile',True,97),(1000,'missile',False,100),(1000,'true',True,100)):
   b=setup('血潮');s,e=b.players;e.active.add('血潮');e.defense=0;e.hp=hp
   b.wave([b.packet(0,100,kind,main=main)])
   self.assertEqual(e.hp,hp-expected_loss)

 def test_spring_penetrates_shields_not_defense_and_stacks_with_wind(self):
  b=setup('生命之泉');s=b.players[0];s.active.add('生命之泉')
  self.assertEqual((b.missile_parameters(0)['def_pen'],b.missile_parameters(0)['pen']),(0,Q(1,5)))
  s.layers['wind']=10;s.active.add('天岚')
  self.assertEqual(b.missile_parameters(0)['pen'],Q(45,100))

 def test_wind_blade_extra_gap_scales_with_layers(self):
  for layers,expected in ((0,46),(4,40),(10,31),(20,16)):
   b=setup('疾风之刃');s=b.players[0];s.specialization='wind';s.layers['wind']=layers;s.active.add('疾风之刃')
   self.assertEqual(b.gap(s),expected)

 def test_bastion_no_longer_tops_shields_or_grants_future_energy(self):
  b=setup('壁垒之心',('坚岩','岩甲','握盾','回岩'))
  b.resolve_sequences(one(b));s=b.players[0]
  self.assertEqual(len(s.known_top),0)
  self.assertEqual(s.pending['energy'],0)

 def test_poison_tide_consumption_cap_and_multiplier_exclude_erosion(self):
  from .effects import EFFECTS
  for layers,consumed in ((0,0),(1,1),(3,3),(5,3)):
   b=setup('毒潮');s,e=b.players;s.specialization='poison';s.layers['poison']=10;s.active.add('蚀命');e.poison=layers
   EFFECTS['毒潮'](b,0,s.hand[0],{})
   packets=[op[2] for op in b.operations if op[0]=='packet']
   self.assertEqual(len(packets),int(consumed>0))
   if consumed:
    self.assertEqual(packets[0]['raw'],100*10*consumed*Q(4,100)*Q(5,2))
    self.assertIn(('poison',1,(0,-consumed)),b.operations)

 def test_erosion_current_hp_bonus_scales_with_both_layer_counts(self):
  b=setup('蚀命');s,e=b.players;s.active.add('蚀命');s.specialization='poison';e.max_hp=2000
  for own,target,hp in ((1,3,2000),(10,3,2000),(10,6,500)):
   s.layers['poison']=own;e.poison=target;e.hp=hp
   self.assertEqual(b.poison_packets(0)[0]['raw'],100*own*target*Q(4,100)+hp*own*target*Q(1,100))
  e.poison=0;self.assertEqual(b.poison_packets(0),[])

if __name__=='__main__':unittest.main()
