"""Strict pre-position combat reads; new protection cannot save this position.

Own explicit gain/consume order is retained. Shield provenance separates the
position's old protection from new shields. Same-position healing is deferred
across incoming damage/death checks. Operation receipts remain inherited.
"""
from copy import deepcopy
from fractions import Fraction as Q
from .engine import Battle
from .locked import LockedBattle
from .model import ceil

class StrictBattle(LockedBattle):
    def __init__(self,*args,**kwargs):
        super().__init__(*args,**kwargs)
        self._old_shields=None;self._slot_heals=None;self._strict_damage=0
        self.snapshot_rule='strict_pre_position_v2'

    def card_panel(self,s,c,first):
        # Even the acting card's fixed panels/layers are absent from Ck reads.
        pass

    def finish_card_panel(self,s,c,first):
        Battle.card_panel(self,s,c,first)
        if s.fx['defense_zero_position']:s.defense-=c.defense

    def slot_begin(self,initial):
        self._old_shields=[dict(ordinary=s.shield,temporary=[b[0] for b in s.temporary]) for s in initial]
        self._slot_heals=[0,0]
        for s in self.players:s.fx['defense_zero_position']=0

    def slot_end(self):
        self._old_shields=None;self._slot_heals=None

    def stats(self,i):
        if self._slot is None or self._phase=='plan':return super().stats(i)
        actual=self.players;self.players=self._slot
        try:return Battle.stats(self,i)
        finally:self.players=actual

    def fire_factor(self,s):
        return super().fire_factor(self._slot[s.seat] if self._slot is not None else s)

    def gap(self,s):
        return super().gap(self._slot[s.seat] if self._slot is not None and self._phase!='plan' else s)

    def execution_ready(self,i):
        return (self._slot or self.players)[i].has('不周山')

    def execution_target(self,i):
        if self._old_shields is None:return super().execution_target(i)
        old=self._old_shields[i]
        return old['ordinary']+sum(old['temporary']),max(0,self.players[i].hp-self._slot_heals[i])

    def burst(self,i,shield,multiplier=1):
        actual=self.players
        if self._slot is not None:self.players=self._slot
        try:return Battle.burst(self,i,shield,multiplier)
        finally:self.players=actual

    def damage_snapshot(self):
        if self._slot is None:return super().damage_snapshot()
        snapshot=deepcopy(self._slot)
        for i,s in enumerate(snapshot):
            old=self._old_shields[i]
            s.shield=old['ordinary']
            s.temporary=[[n,0,0] for n in old['temporary']]
            s.hp=self.players[i].hp
        return snapshot

    def flush_shields(self,s):
        if self._phase=='actual' and self._old_shields is not None:
            if any(not temporary and not no_decay for _,temporary,no_decay,_ in s.shield_events):
                old=self._old_shields[s.seat]
                old['ordinary']=ceil(Q(old['ordinary']*3,4))
        return super().flush_shields(s)

    def shield_gain(self,s,*args,**kwargs):
        # Use the virtual flush method so old-shield provenance sees decay.
        Battle.shield_gain(self,s,*args,**kwargs)
        if self._phase=='actual' and self._entries and not self._wave_depth:
            loss=self.flush_shields(s)
            self._decay_credit[s.seat]=self._decay_credit.get(s.seat,0)+loss

    def take_shield(self,s,n,ordinary_only=False):
        before=[b[0] for b in s.temporary];ordinary=s.shield
        result=super().take_shield(s,n,ordinary_only)
        if self._phase=='actual' and self._old_shields is not None:
            old=self._old_shields[s.seat]
            for k,amount in enumerate(old['temporary']):
                old['temporary'][k]-=min(amount,max(0,before[k]-s.temporary[k][0]))
            old['ordinary']-=min(old['ordinary'],max(0,ordinary-s.shield))
        return result

    def take_damage_shield(self,s,amount):
        if self._old_shields is None:return super().take_damage_shield(s,amount)
        old=self._old_shields[s.seat];left=amount
        for k,available in enumerate(old['temporary']):
            if k>=len(s.temporary):old['temporary'][k]=0;continue
            used=min(left,available);left-=used
            old['temporary'][k]-=used;s.temporary[k][0]-=used
        used=min(left,old['ordinary']);left-=used
        old['ordinary']-=used;s.shield-=used
        actual=amount-left
        if actual:self.state_changed('shield',s.seat)
        return actual


    def heal(self,s,*args,**kwargs):
        if self._phase!='actual' or self._slot is None:return super().heal(s,*args,**kwargs)
        old=self._slot[s.seat]
        layers,mods,active=s.layers,s.mods,s.active
        s.layers=old.layers;s.mods=old.mods;s.active=old.active
        try:actual=super().heal(s,*args,**kwargs)
        finally:s.layers=layers;s.mods=mods;s.active=active
        self._slot_heals[s.seat]+=actual
        return actual

    def lose_life(self,s,amount,payment=False):
        if payment or self._slot is None or not self._strict_damage:
            return super().lose_life(s,amount,payment)
        active=s.fx['lifeguard'];s.fx['lifeguard']=self._slot[s.seat].fx['lifeguard']
        try:return super().lose_life(s,amount,payment)
        finally:s.fx['lifeguard']=active

    def _death(self):
        if self._slot is None:return super()._death()
        rewinds=[s.rewind for s in self.players]
        for i,s in enumerate(self.players):s.rewind=self._slot[i].rewind
        try:return super()._death()
        finally:
            for i,(s,rewind) in enumerate(zip(self.players,rewinds)):
                s.rewind=self._slot[i].rewind if s.hp<=0 and s.seat in self._cut else rewind

    def wave(self,packets,reaction=False):
        if self._slot is None:return super().wave(packets,reaction)
        # New healing cannot retroactively rescue a lethal Ck damage event.
        deferred=list(self._slot_heals);revived=[s.revived for s in self.players]
        self._slot_heals=[0,0]
        for i,s in enumerate(self.players):s.hp-=deferred[i]
        self._strict_damage+=1
        try:return super().wave(packets,reaction)
        finally:
            self._strict_damage-=1
            for i,s in enumerate(self.players):
                if s.hp>0 and s.revived==revived[i]:
                    amount=min(deferred[i],max(0,s.max_hp-s.hp))
                    s.hp+=amount;self._slot_heals[i]+=amount

