"""Private operation receipts and resumable, simultaneous combat transactions.

Synchronous card handlers are resumed by deterministic replay between primitive
boundaries. Cached calls restore their committed own state and never re-execute
randomness, resource expenditure or reactions. Opponent reads use the slot's
common public snapshot. No planning combat state is copied into real combat.
"""
from collections import Counter, deque
from copy import copy, deepcopy
from dataclasses import dataclass, field
from fractions import Fraction as Q
import math
import random
from .engine import Battle, NEGATIVE
from .catalog import CARDS
from .model import Fighter

PRIVATE = ('pile','hand','discard','removed','resolving','discounts','drawn','known_top')
PRIVATE_CALLS = {'draw','shuffle','search','move_hand','recover','choose','random_insert'}
PRIMITIVES = PRIVATE_CALLS | {'heal','shield_gain','flush_shields','take_shield',
    'lose_life','add_ember','add_tide','consume_tide','temp',
    'cleanse','target_poison','poison_packets'}

@dataclass
class Entry:
    uid: str
    name: str
    cost: int
    receipts: list = field(default_factory=list)
    flags: dict = field(default_factory=dict)
    after: dict = field(default_factory=dict)
    energy: int = 0
    spent: int = 0

class Frontier(Exception):
    def __init__(self, name, args, kwargs):
        self.name,self.args_,self.kwargs=name,args,kwargs

def private_state(s):
    return {**{k:deepcopy(getattr(s,k)) for k in PRIVATE},'rng':s.rng.getstate()}

def restore_private(s, state):
    for k in PRIVATE:setattr(s,k,deepcopy(state[k]))
    s.rng.setstate(state['rng'])

def restore_fighter(s,state):
    # Preserve containers already loaded by Python augmented assignment, e.g.
    # s.fx['direct'] += take_shield(...). Replacing fx would detach its target.
    for key,value in deepcopy(state).__dict__.items():
        previous=s.__dict__.get(key)
        if isinstance(value,dict) and type(previous) is type(value):
            previous.clear();previous.update(value)
        elif isinstance(value,list) and isinstance(previous,list):previous[:]=value
        elif isinstance(value,set) and isinstance(previous,set):
            previous.clear();previous.update(value)
        else:setattr(s,key,value)

class LockedBattle(Battle):
    def __init__(self,*args,sequence_mode='locked',**kwargs):
        super().__init__(*args,sequence_mode=sequence_mode,**kwargs)
        self._phase='actual';self._depth=0;self._atomic=0
        self._events=deque();self._draining=False
        self._cut=set();self._entries={};self._cursors={}
        self._slot=None;self._old_poison=None;self._old_left=None
        self._decay_credit={}
        self._wave_depth=0

    def __getattribute__(self,name):
        fn=super().__getattribute__(name)
        if name not in PRIMITIVES:return fn
        def call(*args,**kwargs):
            phase=self._phase
            if self._depth:return fn(*args,**kwargs)
            if phase=='probe':
                k=self._probe_cursor;self._probe_cursor+=1
                if k<len(self._cache):
                    cached_name,result,state,cursors=self._cache[k]
                    assert cached_name==name,(cached_name,name)
                    s=self.players[self._actor]
                    restore_fighter(s,state)
                    self.operations=[];self._cursors=dict(cursors)
                    return deepcopy(result)
                raise Frontier(name,args,kwargs)
            if name in PRIVATE_CALLS and phase=='plan':
                s=args[0];self._depth+=1
                try:result=fn(*args,**kwargs)
                finally:self._depth-=1
                self._planning_entry.receipts.append((name,deepcopy(result),private_state(s),s.fx['shuffled']))
                return result
            if name in PRIVATE_CALLS and phase=='actual' and self._entries:
                s=args[0];i=s.seat;entry=self._entries[i];k=self._cursors.get(i,0)
                assert k<len(entry.receipts),(entry.name,name,k)
                expected,result,state,shuffles=entry.receipts[k]
                assert name==expected,(entry.name,expected,name)
                self._cursors[i]=k+1
                # The private RNG and exact instances come from P, rewards from C.
                before_hand={x.uid for x in s.hand};before_discard={x.uid for x in s.discard}
                previous=s.fx['shuffled'];restore_private(s,state)
                delta=max(0,shuffles-previous);s.fx['shuffled']+=delta
                if delta and s.has('灰烬循环'):self.add_ember(s,2*delta)
                flow=self.flow_by_seat[i]
                gained=len({x.uid for x in s.hand}-before_hand)
                if name=='recover':flow['recovered']+=gained
                elif name in ('draw','search'):flow['instant_drawn']+=gained
                flow['discarded']+=len({x.uid for x in s.discard}-before_discard)
                flow['shuffles']+=delta
                if name in ('search','move_hand'):flow['topdecked']+=max(0,len(s.known_top))
                return deepcopy(result)
            self._depth+=1
            try:return fn(*args,**kwargs)
            finally:self._depth-=1
        return call

    def plan_read(self,i,key,value):
        if self._phase=='plan':
            self._planning_entry.flags[key]=value
            return value
        if self._phase in ('actual','probe') and i in self._entries:
            return self._entries[i].flags[key]
        return value

    def cost(self,s,inst):
        if self._phase in ('actual','probe') and s.seat in self._entries:
            entry=self._entries[s.seat]
            if entry.uid==inst.uid:return entry.cost
        return super().cost(s,inst)

    def packet(self,i,raw,kind='missile',**kwargs):
        if self._phase=='probe':kwargs.setdefault('defense',self.stats(1-i)[1])
        return super().packet(i,raw,kind,**kwargs)

    def plan_stage(self,max_actions=10000):
        from .policy import choose_play
        sequences=[]
        for i in range(2):
            shadow=copy(self);shadow.players=deepcopy(self.players)
            e=shadow.players[1-i]
            e.deck=();e.instances=()
            for k in ('pile','hand','discard','removed','resolving'):setattr(e,k,[])
            e.discounts={};e.drawn=set();e.seen=set();e.rng=random.Random(0)
            shadow._phase='plan';shadow._depth=0;shadow._entries={}
            shadow.trace=False;shadow.log=[];shadow.operations=[]
            shadow.executed=Counter();shadow.executed_by_seat=[Counter(),Counter()]
            shadow._decay_credit={};shadow._events=deque();shadow._cut=set()
            shadow._economic_visits={}
            sequence=[]
            while True:
                inst=choose_play(shadow,i)
                if inst is None:break
                if len(sequence)>=max_actions:raise RuntimeError('Private plan did not terminate')
                entry=Entry(inst.uid,inst.name,shadow.cost(shadow.players[i],inst))
                shadow._planning_entry=entry
                shadow.operations=[]
                shadow.play_local(i,inst.uid)
                # Predictable public poison/debuff budgets, never preview damage,
                # reflection, death, revive, automatic execute or hit rewards.
                for kind,target,value in shadow.operations:
                    if kind=='poison':
                        source,delta=value;shadow.change_poison(shadow.players[source],shadow.players[target],delta)
                    elif kind=='modifier':shadow.players[target].mods.append(value)
                    elif kind=='def_down_permanent':shadow.players[target].fx['permanent_def_down']+=value
                s=shadow.players[i]
                entry.after=private_state(s);entry.energy=s.energy;entry.spent=s.spent
                sequence.append(entry)
            sequences.append(tuple(sequence))
        return tuple(sequences)

    def begin_atomic(self):
        if self._phase=='actual':self._atomic+=1

    def end_atomic(self):
        if self._phase=='actual':
            self._atomic-=1
            assert self._atomic>=0
            if not self._atomic:self._drain()

    def state_changed(self,kind,seat):
        if self._phase!='actual':return
        self._events.append((kind,seat))
        if not self._atomic:self._drain()

    def _death(self):
        for s in self.players:
            if s.hp>0:continue
            self._cut.add(s.seat)
            if s.rewind and not s.revived and self.time<=s.rewind[0]+200:
                _,a,d,hp,shield=s.rewind
                s.revived=True;s.attack=a;s.defense=d;s.hp=hp;s.shield=shield
                s.fx['temp_attack']=s.fx['abyss']=s.fx['permanent_def_down']=0
                s.temporary=[]
                # Revival clears actual current poison, including new slot
                # layers; it is not a competing Ck-only card cleanse request.
                budget=self._old_left;self._old_left=None
                try:super().cleanse(s,s.poison)
                finally:self._old_left=budget
                self._events.extend((('hp',s.seat),('shield',s.seat)))
                self.event('revive',s.seat)

    def _drain(self):
        if self._draining:return
        self._draining=True
        try:
            self._death()
            guard=0
            while self._events:
                guard+=1
                if guard>10000:raise RuntimeError('Nonterminating execute reaction')
                owners=set()
                while self._events:
                    kind,seat=self._events.popleft()
                    owners.add(1-seat if kind=='hp' else seat)
                packets=[];committed=[]
                # All checks caused by one atomic batch share the same state.
                # Neither consumption/defense-zeroing nor first-seat death may
                # cancel the other side's already qualified automatic burst.
                for i in sorted(owners):
                    s,e=self.players[i],self.players[1-i]
                    if s.hp<=0 or e.hp<=0 or not self.execution_ready(i) or not s.shield_total:continue
                    packet=self.burst(i,s.shield_total)
                    amount=max(0,math.floor(packet['raw']*packet['final']))
                    shield,hp=self.execution_target(1-i)
                    if max(0,amount-shield)<hp:continue
                    self.event('auto_execute',i,shield=s.shield_total,target_hp=e.hp)
                    packets.append(packet);committed.append(i)
                for i in committed:
                    s=self.players[i]
                    self.take_shield(s,s.shield_total);s.defense=Q(0)
                    s.fx['defense_zero_position']=1
                if packets:self.wave(packets,reaction=True)
                self._death()
        finally:self._draining=False

    def execution_ready(self,i):
        return self.players[i].has('不周山')

    def execution_target(self,i):
        return self.players[i].shield_total,self.players[i].hp

    def slot_begin(self,initial):
        pass

    def slot_end(self):
        pass

    def shield_gain(self,s,*args,**kwargs):
        super().shield_gain(s,*args,**kwargs)
        if self._phase=='actual' and self._entries and not self._wave_depth:
            loss=super().flush_shields(s)
            self._decay_credit[s.seat]=self._decay_credit.get(s.seat,0)+loss

    def wave(self,packets,reaction=False):
        self._wave_depth+=1
        try:return super().wave(packets,reaction)
        finally:self._wave_depth-=1

    def flush_shields(self,s):
        loss=super().flush_shields(s)
        return loss+self._decay_credit.pop(s.seat,0)

    def heal(self,s,*args,**kwargs):
        if self._phase=='actual' and s.hp<=0:return 0
        return super().heal(s,*args,**kwargs)

    def change_poison(self,source,target,delta):
        if self._phase=='actual' and self._old_left is not None and delta<0:
            use=min(-delta,self._old_left[target.seat])
            self._old_left[target.seat]-=use;delta=-use
        return super().change_poison(source,target,delta)

    def cleanse(self,s,poison=0):
        if self._phase!='actual' or self._slot is None:return super().cleanse(s,poison)
        old=self._slot[s.seat]
        kinds=NEGATIVE
        removed={m.kind for m in old.mods if m.kind in kinds}
        s.mods=[m for m in s.mods if not (m in old.mods and m.kind in kinds)]
        s.wind_marks=max(0,s.wind_marks-old.wind_marks)
        if self._entries[s.seat].name=='风之躯':poison=old.poison
        confirmed=min(old.poison,poison)
        self.change_poison(self.players[1-s.seat],s,-confirmed)
        return confirmed,len(removed)

    def _commit_ops(self,operations):
        packets=[]
        for kind,target,value in operations:
            s=self.players[target]
            if kind=='modifier':s.mods.append(value)
            elif kind=='poison':
                source,delta=value;self.change_poison(self.players[source],s,delta)
            elif kind=='def_down_permanent':s.fx['permanent_def_down']+=value
            elif kind=='decay_reschedule':
                source=self.players[value]
                s.poison_due=max(self.time+1,s.poison_start+self.poison_interval(source,s.poison))
            elif kind=='packet':packets.append(value)
            else:raise ValueError(('Unknown locked operation',kind))
        return packets

    def _slot_step(self,entries):
        self._entries=entries;self._cursors={i:0 for i in entries}
        initial=deepcopy(self.players);self._slot=initial
        self.slot_begin(initial)
        self._old_left=[s.poison for s in initial]
        caches={i:[] for i in entries};done=set();packets=[]
        while len(done)<len(entries):
            probes={}
            for i,entry in entries.items():
                if i in done:continue
                probe=copy(self);probe.players=deepcopy(initial)
                probe._phase='probe';probe._actor=i;probe._cache=caches[i];probe._probe_cursor=0
                probe.operations=[];probe.log=[];probe.trace=False;probe.executed=Counter()
                probe._cursors={i:0};probe._depth=0
                try:
                    probe.play_local(i,entry.uid)
                    frontier=None
                except Frontier as stop:frontier=stop
                probes[i]=(probe,frontier)
            self.begin_atomic()
            # Both direct segments become visible before either reaction.
            for i,(probe,frontier) in probes.items():
                newly_active=probe.players[i].has('不周山') and not self.players[i].has('不周山')
                self.players[i]=probe.players[i]
                if newly_active:self.state_changed('shield',i)
            operations=[op for probe,_ in probes.values() for op in probe.operations]
            packets.extend(self._commit_ops(operations))
            pending=[]
            for i,(probe,frontier) in probes.items():
                self._cursors[i]=probe._cursors.get(i,0)
                if frontier is None:
                    done.add(i);entry=entries[i];s=self.players[i]
                    restore_private(s,entry.after);s.energy=entry.energy;s.spent=entry.spent
                    assert self._cursors[i]==len(entry.receipts),(entry.name,'unconsumed receipt')
                    self.executed[entry.name]+=1;self.executed_by_seat[i][entry.name]+=1
                    self.flow_by_seat[i]['instant_fee']+=max(0,entry.energy-(initial[i].energy-entry.cost))
                    self.flow_by_seat[i]['played']+=1
                    self.event('play',i,name=entry.name,uid=entry.uid,cost=entry.cost,energy=s.energy)
                else:
                    args=tuple(self.players[a.seat] if isinstance(a,Fighter) else a for a in frontier.args_)
                    result=getattr(self,frontier.name)(*args,**frontier.kwargs)
                    pending.append((i,frontier.name,deepcopy(result)))
            operations,self.operations=self.operations,[]
            packets.extend(self._commit_ops(operations))
            self.end_atomic()
            for i,name,result in pending:
                caches[i].append((name,result,deepcopy(self.players[i]),dict(self._cursors)))
        self._entries={};self._cursors={}
        if packets:self.wave(packets)
        self.slot_end()
        self._slot=None;self._old_left=None
        self.finish_transaction()

    def resolve_sequences(self,sequences):
        self._cut=set();played=0
        self.event('sequences_locked',0,sequences=[[p.name for p in seq] for seq in sequences])
        for position in range(max(map(len,sequences),default=0)):
            entries={i:seq[position] for i,seq in enumerate(sequences)
                     if position<len(seq) and i not in self._cut and self.players[i].hp>0}
            if not entries:break
            self._slot_step(entries);played+=len(entries)
            if any(s.hp<=0 for s in self.players):break
        return played

    def finish_transaction(self):
        if self._phase!='actual':return
        self._drain()
        for s in self.players:
            self.flush_shields(s);s.assert_instances()
