import json,hashlib
import tempfile
from pathlib import Path
from dataclasses import asdict,replace
from fractions import Fraction as Q
from unittest import TestCase
from unittest.mock import patch
from .catalog import CARDS,validate_frozen_cards
from .effects import EFFECTS
from .test_current import setup

class FreezeContract(TestCase):
    def play(self,b,name):
        EFFECTS[name](b,0,type('Ref',(),{'name':name})(),{'first':False,'fire':0})

    def test_guard_allows_exact_snapshot_and_rejects_drift(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as temp:
            folder=Path(temp)
            self.assertTrue(folder.resolve().is_relative_to(Path(__file__).parent.resolve()))
            (folder/'cards.json').write_text(json.dumps({'cards':{n:asdict(c) for n,c in CARDS.items()}},ensure_ascii=False),encoding='utf-8')
            digest=hashlib.sha256((folder/'cards.json').read_bytes()).hexdigest()
            (folder/'freeze-manifest.json').write_text(json.dumps({'sha256':{'releases/v1.2/cards.json':digest}}),encoding='utf-8')
            validate_frozen_cards(CARDS,folder)
            for key,value in [('text','changed'),('attack',999),('cost',999),('layers',999)]:
                changed=dict(CARDS);changed['引火']=replace(CARDS['引火'],**{key:value})
                with self.subTest(key=key),self.assertRaisesRegex(RuntimeError,'引火'):validate_frozen_cards(changed,folder)
            changed=dict(CARDS);changed.pop('引火')
            with self.assertRaisesRegex(RuntimeError,'引火'):validate_frozen_cards(changed,folder)
            with self.assertRaisesRegex(RuntimeError,'order'):validate_frozen_cards(dict(reversed(list(CARDS.items()))),folder)
            (folder/'cards.json').unlink()
            with self.assertRaisesRegex(RuntimeError,'missing'):validate_frozen_cards(CARDS,folder)
            (folder/'cards.json').write_text('{}',encoding='utf-8');(folder/'freeze-manifest.json').unlink()
            with self.assertRaisesRegex(RuntimeError,'incomplete'):validate_frozen_cards(CARDS,folder)

    def test_guard_rejects_corrupt_release_snapshot(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as temp:
            folder=Path(temp)
            self.assertTrue(folder.resolve().is_relative_to(Path(__file__).parent.resolve()))
            (folder/'cards.json').write_text('{}',encoding='utf-8')
            (folder/'freeze-manifest.json').write_text(json.dumps({'sha256':{'releases/v1.2/cards.json':'bad'}}),encoding='utf-8')
            with self.assertRaisesRegex(RuntimeError,'checksum'):validate_frozen_cards(CARDS,folder)

    def test_pending_draw_and_energy_use_counter_addition(self):
        b=setup('护盾');s=b.players[0]
        for n in ('循环','循环','禁忌知识','预备','整理'):self.play(b,n)
        self.assertEqual((s.pending['draw'],s.pending['energy']),(5,3))

    def test_grip_uses_launch_layers_for_all_recorded_losses(self):
        b=setup('护盾');s=b.players[0];s.active.add('大地之握');s.layers['earth']=2;s.shield=100
        b.shield_gain(s,20);b.flush_shields(s)
        self.assertEqual((s.shield,s.fx['grip_loss']),(95,25))
        s.layers['earth']=10
        with patch.object(b,'packet',wraps=b.packet) as packets:b.fire([0])
        main=next(c for c in packets.call_args_list if c.kwargs.get('main'))
        self.assertEqual(main.args[1],100+Q(48*4*10,100))
        self.assertEqual(s.fx['grip_loss'],0)

    def test_still_water_repeated_rewards_consume_on_one_effective_heal(self):
        b=setup('护盾');s=b.players[0];s.active.add('潮汐之心');s.layers['water']=2
        self.play(b,'静水');self.play(b,'静水');b.heal(s,1)
        self.assertEqual((s.tide,s.fx['still_water']),(0,2))
        s.hp=900;b.heal(s,10)
        self.assertEqual((s.tide,s.fx['still_water']),(26,0)) # ceil(10*.06*2) + 2*6*2
        b.heal(s,10);self.assertEqual(s.tide,28)

    def test_trickle_repeated_rewards_share_one_actual_card_payment(self):
        b=setup('护盾');s=b.players[0]
        self.play(b,'细流');self.play(b,'细流');s.hp=800
        self.assertEqual(b.pay_life(s,Q(1,10),False),80)
        self.assertEqual(s.fx['trickle'],2)
        self.assertEqual(b.pay_life(s,Q(1,10)),72)
        self.assertEqual((s.hp,s.fx['trickle']),(720,0))
