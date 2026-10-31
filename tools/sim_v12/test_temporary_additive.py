"""The final rule: temporary numeric values add, never overwrite/max."""
from fractions import Fraction as Q
from unittest import TestCase
from unittest.mock import patch
from .effects import EFFECTS
from .test_current import setup


class TemporaryAdditive(TestCase):
    def play(self, b, name):
        EFFECTS[name](b, 0, type('CardRef', (), {'name': name})(), {'fire': 0, 'first': False})

    def test_bonus_order_independent_and_conditional_components_add(self):
        for names in [('猛火弹','引火','爆燃'),('爆燃','引火','猛火弹')]:
            b=setup('护盾');s=b.players[0]
            for n in names:self.play(b,n)
            self.assertEqual(s.fx['bonus'],50)
            s.attack=135;s.ember=2
            self.play(b,'压缩爆燃');self.play(b,'导火索')
            self.assertEqual(s.fx['bonus'],145)
            self.play(b,'压缩爆燃');self.assertEqual(s.fx['bonus'],225)

    def test_interval_temporary_permanent_and_element_add_with_floor(self):
        b=setup('护盾');s=b.players[0]
        s.layers['wind']=4;s.fx['permanent_delay']=-3
        for n in ('烈焰冲刺','短促','风步'):self.play(b,n)
        self.assertEqual((s.fx['delay'],b.gap(s)),(Q(-45,2),22))
        b.players[1].poison=3
        self.play(b,'爆裂');self.play(b,'爆裂')
        self.assertEqual((s.fx['delay'],b.gap(s)),(Q(-125,2),1))

    def test_penetration_adds_then_caps_and_resets(self):
        b=setup('护盾');s=b.players[0];s.active.update(('天岚','生命之泉','潮汐之心'));s.layers['wind']=5
        for n in ('水幕','水幕','破甲','穿潮','穿潮'):self.play(b,n)
        s.tide=50
        self.assertEqual(s.fx['main_shield_pen'],180)
        with patch.object(b,'packet',wraps=b.packet) as packets:b.fire([0])
        p=next(c for c in packets.call_args_list if c.kwargs.get('main'))
        self.assertEqual((p.kwargs['def_pen'],p.kwargs['pen']),(Q(1),Q(1)))
        self.assertEqual((s.fx['main_def_pen'],s.fx['main_shield_pen'],s.fx['pierce_tide']),(0,0,0))
        self.assertEqual(b.missile_parameters(0,True)['pen'],Q(35,100))

    def test_attack_adds_and_main_only_buff_does_not_leak_to_sub(self):
        b=setup('护盾');s,e=b.players;e.poison=4;e.defense=0
        for n in ('狂暴','狂暴','腐溃','腐溃'):self.play(b,n)
        self.assertEqual((s.fx['temp_attack'],s.fx['main_attack']),(60,48))
        s.subs.append((0,Q(1),1,False))
        with patch.object(b,'packet',wraps=b.packet) as packets:b.fire([0])
        self.assertEqual([c.args[1] for c in packets.call_args_list if c.args[2]=='missile'],[208,160])

    def test_direct_sources_and_duplicate_cards_add(self):
        b=setup('护盾');s,e=b.players;e.defense=0;s.shield=200
        for n in ('岩核重压','岩核重压','高温炉心','高温炉心'):self.play(b,n)
        self.assertEqual((s.fx['direct'],s.fx['hot_core']),(80,10))
        s.active.update(('血潮','潮汐之心','炽焰王冠'));s.tide=50;s.ember=2;s.layers['fire']=0
        with patch.object(b,'packet',wraps=b.packet) as packets:b.fire([0])
        main=next(c for c in packets.call_args_list if c.kwargs.get('main'))
        self.assertEqual(main.args[1],350)

    def test_strip_percentages_share_one_basis_and_cap(self):
        b=setup('护盾');s,e=b.players;e.shield=1000;e.defense=1000
        for n in ('裂伤','裂空穿云'):self.play(b,n)
        with patch.object(b,'wave'):b.fire([0])
        self.assertEqual((e.shield,s.fx['permanent_delay']),(400,-2))
        e.shield=1000
        for n in ('裂空穿云','裂空穿云','裂空穿云'):self.play(b,n)
        with patch.object(b,'wave'):b.fire([0])
        self.assertEqual((e.shield,s.fx['permanent_delay']),(0,-8))

    def test_armor_break_repeats_per_hit_without_changing_panel(self):
        b=setup('护盾');s,e=b.players;e.defense=0
        self.play(b,'穿云破甲');self.play(b,'穿云破甲')
        s.subs.append((10,Q(0),1,False));b.fire([0])
        self.assertEqual((e.defense,e.fx['permanent_def_down']),(0,24))

    def test_locked_sequence_preserves_additive_commit(self):
        b=setup('猛火弹',('引火',));s=b.players[0]
        s.hand.extend(s.pile);s.pile=[]
        def choose(shadow,i):return shadow.players[i].hand[0] if i==0 and shadow.players[i].hand else None
        with patch('tools.sim_v12.policy.choose_play',side_effect=choose):plans=b.plan_stage()
        b.resolve_sequences(plans)
        self.assertEqual(b.players[0].fx['bonus'],25)

    def test_next_card_discounts_add_consistently(self):
        b=setup('护盾');s=b.players[0]
        for n in ('导火索','导火索','连环潮','连环潮','岩拳','岩拳'):self.play(b,n)
        self.assertEqual([s.fx['discount_'+e] for e in ('fire','water','earth')],[2,2,2])

    def test_remaining_numeric_next_event_buffs_add(self):
        b=setup('护盾');s=b.players[0];s.active.update(('潮汐之心','大地之握'))
        for n in ('回响之潮','潮涌','握盾','磐岩壁'):
            self.play(b,n);self.play(b,n)
        self.assertEqual([s.fx[k] for k in ('tide_bonus','tide_record','tide_shield','grip_bonus','shield_leech')],[100,100,100,200,60])
