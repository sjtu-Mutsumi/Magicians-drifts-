"""Independent four-player construction and elimination, with finite inventory."""
from collections import Counter
from copy import deepcopy
from dataclasses import dataclass,field
from pathlib import Path
import json
import math
import random
from .catalog import CARDS, ELEMENTS, SPECIAL, START, START_ONLY, RANK, ROOT
from .pool import Pool, CFG
from .engine import Battle
from .locked import LockedBattle
from .strict import StrictBattle
from .policy import draft_value, DRAW, SHUFFLE, STYLES
from .construction import is_thin,purchase_value,deletion_value,refresh_wanted

RULES=Path(__file__).parent/'rules'
ECONOMY=json.loads((RULES/'economy.json').read_text(encoding='utf-8'))
DAMAGE=json.loads((RULES/'damage.json').read_text(encoding='utf-8'))['table']

def arena_loss(round_num,winner_remaining_hp,won=False,draw=False):
    if round_num<1:raise ValueError(round_num)
    m,n=DAMAGE[str(min(round_num,max(map(int,DAMAGE))))]
    return 0 if won else m if draw else m+math.floor(math.log(max(1,winner_remaining_hp))*n)

@dataclass
class Player:
    pid:int
    preference:str
    deck:list=field(default_factory=lambda:[(n,None) for n in START])
    spec:str=''
    hp:int=100
    streak:int=0
    bank:int=0
    points:int=0
    free_used:bool=False
    income:int=0
    carry:int=0
    bonus:int=0
    style:str='balanced'
    policy:dict|None=None
    bought_elements:set=field(default_factory=set)
    actions:list=field(default_factory=list)
    @property
    def route(self):
        return next((n for n,t in self.deck if CARDS[n].kind=='route_legendary'),'')

def public(players):
    return [dict(pid=p.pid,hp=p.hp,spec=p.spec,route=p.route) for p in players]

def base_income(r):
    if r < 1:raise ValueError(r)
    matches=[row['points'] for row in ECONOMY['income']
             if row['rounds'][0]<=r and (row['rounds'][1] is None or r<=row['rounds'][1])]
    if len(matches)!=1:raise ValueError(('Ambiguous or missing income rule',r))
    return matches[0]

def free_price(p,c,r):
    free=not p.free_used and ((r<=2 and c.rarity=='common') or (3<=r<=4 and c.rarity in ('common','rare')))
    return 0 if free else RANK[c.rarity]+1

def price_and_replace(p,name,r):
    c=CARDS[name];names=[n for n,t in p.deck]
    if name in names or name in START_ONLY:return None,None
    if p.spec and c.element not in ('generic',p.spec):return None,None
    if c.kind=='route_legendary':
        if not p.spec:return None,None
        if p.route:return 2,p.route
    legends=[n for n in names if CARDS[n].rarity=='legendary']
    if c.rarity=='legendary' and len(legends)>=3:
        legal=[n for n in legends if CARDS[n].kind!='route_legendary']
        if not legal:return None,None
        old=min(legal,key=lambda n:draft_value(p,n,[]))
        if CARDS[old].element==p.spec and c.element!='generic' and c.element!=p.spec:return None,None
        main_count=sum(CARDS[n].element==p.spec and CARDS[n].kind!='specialization' for n in names)
        if main_count<=3 and CARDS[old].element==p.spec and c.element!=p.spec:return None,None
        return 4,old
    return free_price(p,c,r),None

def buy_phase(p,players,pool,r,events):
    bought=set()
    while True:
        options=[]
        for slot,token in enumerate(pool.offers[p.pid]):
            if not token:continue
            n=pool.tokens[token];cost,old=price_and_replace(p,n,r)
            if cost is None or cost>p.points:continue
            value=purchase_value(p,n,cost,old,public(players))
            options.append((value,slot,n,cost,old))
        if not options or max(options)[0]<=0:break
        value,slot,name,cost,old=max(options)
        if old:
            item=next(x for x in p.deck if x[0]==old);p.deck.remove(item);pool.remove(item[1])
        p.points-=cost
        if cost==0:p.free_used=True
        token=pool.buy(p.pid,slot);p.deck.append((name,token));bought.add(slot)
        p.bought_elements.add(CARDS[name].element)
        p.actions.append(dict(kind='buy',name=name,cost=cost,slot=slot,replaced=old))
        if CARDS[name].kind=='route_legendary':events.append(dict(kind='route',pid=p.pid,name=name))
        if not p.spec:
            counts=Counter(CARDS[n].element for n,t in p.deck if CARDS[n].enchantment)
            eligible=[e for e in ELEMENTS if counts[e]>=3]
            if eligible:
                p.spec=max(eligible,key=lambda e:(counts[e],e==p.preference));p.deck.append((SPECIAL[p.spec],None))
                events.append(dict(kind='specialization',pid=p.pid,element=p.spec))
    return bought

def deletion_rules(r):
    if r<1:raise ValueError(r)
    key='rounds_1_to_3' if r<=3 else 'rounds_4_to_6' if r<=6 else 'rounds_7_plus'
    limit,fees=ECONOMY['deletions'][key]
    if limit!=len(fees):raise ValueError(('Invalid deletion schedule',key))
    return limit,tuple(fees)

def delete_phase(p,pool,r):
    limit,fees=deletion_rules(r)
    for k in range(limit):
        fee=fees[k]
        if len(p.deck)<=8 or p.points<fee:break
        counts=Counter(n for n,t in p.deck)
        main=sum(CARDS[n].element==p.spec and CARDS[n].kind!='specialization' for n,t in p.deck)
        options=[]
        for j,(name,token) in enumerate(p.deck):
            c=CARDS[name]
            if c.ability or c.rarity=='legendary':continue
            if c.element==p.spec and main<=3:continue
            if is_thin(p):
                options.append((deletion_value(p,name,fee),j,name,token))
                continue
            value=draft_value(p,name,[])
            undesirable=(name in START_ONLY)*6+(counts[name]>1)*2+(c.element not in ('generic',p.spec) and bool(p.spec))*8
            if name in DRAW or name in SHUFFLE:undesirable-=4
            options.append((undesirable-value/3,j,name,token))
        if not options or max(options)[0]<(0 if is_thin(p) else 2+fee*2):break
        _,index,name,token=max(options);p.deck.pop(index);pool.remove(token);p.points-=fee
        p.actions.append(dict(kind='delete',name=name,cost=fee))

def templates(players,r,rng):
    key=str(r) if r<8 else '8_plus'
    weights=CFG['rarities'][key]
    result=[]
    for face in range(2):
        byplayer={}
        for p in players:
            values=rng.choices(range(4),weights[face],k=8)
            if face==0 and str(r) in CFG['first_face_legendary_guarantees']:
                values[:3]=[3]*3
            byplayer[p.pid]=values
        result.append(byplayer)
    return result

def schedule(players,dead,last,meetings,rng):
    ids=[p.pid for p in players]
    if len(ids)==4:
        a,b,c,d=ids;choices=[[(a,b),(c,d)],[(a,c),(b,d)],[(a,d),(b,c)]]
    elif len(ids)==3:
        choices=[]
        for g in ids:
            others=[i for i in ids if i!=g]
            for ghost in dead:choices.append([(others[0],others[1]),(g,'g'+str(ghost.pid))])
    else:choices=[[(ids[0],ids[1])]]
    def value(pairs):
        return sum(last.get(a)==b or last.get(b)==a for a,b in pairs),sum(meetings[tuple(sorted((str(a),str(b))))] for a,b in pairs),rng.random()
    pairs=min(choices,key=value)
    for a,b in pairs:last[a]=b;last[b]=a;meetings[tuple(sorted((str(a),str(b))))]+=1
    return pairs

def simulate(seed=1,trace=False,max_rounds=80,random_styles=True,sequence_mode='locked',policy_by_seat=None,refresh_threshold=None):
    rng=random.Random(seed);pool=Pool()
    players=[Player(i,ELEMENTS[(seed+i)%5]) for i in range(4)]
    if policy_by_seat:
        for p in players:
            p.policy=policy_by_seat[p.pid]
            if p.policy and p.policy.get('element_intent') in ELEMENTS:
                p.preference=p.policy['element_intent']
    if random_styles:
        for p in players:p.style=rng.choice(tuple(STYLES))
    initial_styles={p.pid:p.style for p in players}
    dead=[];last={};meetings=Counter();rounds=[];placements=[];battle_counter=0
    for r in range(1,max_rounds+1):
        pool.begin();pairs=schedule(players,dead,last,meetings,rng)
        events=[dict(kind='pairing',a=a,b=b) for a,b in pairs]
        for p in players:
            bonus=int(p.streak!=0 and abs(p.streak)%3==0)
            p.carry=p.bank;p.bonus=bonus;p.income=base_income(r)+p.bank+bonus
            p.points=p.income;p.free_used=False;p.bought_elements.clear();p.actions=[]
        faces=templates(players,r,rng)
        pool.generate(players,faces[0],r,0,rng)
        exposure={p.pid:[dict(face=0,slot=j,name=pool.tokens[t]) for j,t in enumerate(pool.offers[p.pid]) if t] for p in players}
        purchase_slots={}
        for p in players:purchase_slots[p.pid]=buy_phase(p,players,pool,r,events)
        pool.audit(players)
        for p in players:
            # Refresh choice uses only own shown candidates and bank; never inspects pool inventory.
            if p.points >= (refresh_threshold(p) if refresh_threshold else 2) and len(purchase_slots[p.pid])<8:
                old_public=public(players)
                unbought=[pool.tokens[t] for t in pool.offers[p.pid] if t]
                refresh=refresh_wanted(p,r,unbought,old_public)
                if refresh is True or refresh is None and (all(draft_value(p,n,old_public)<STYLES[p.style]['refresh'] for n in unbought) or len(p.deck)<15):
                    p.points-=1;p.actions.append(dict(kind='refresh',cost=1));pool.release(p.pid)
                    pool.generate([p],faces[1],r,1,rng,bought=purchase_slots)
                    exposure[p.pid].extend(dict(face=1,slot=j,name=pool.tokens[t]) for j,t in enumerate(pool.offers[p.pid]) if t)
                    buy_phase(p,players,pool,r,events)
            delete_phase(p,pool,r);p.bank=min(2,p.points);pool.release(p.pid)
        pool.audit(players)
        ledger=[dict(pid=p.pid,style=p.style,income=p.income,base_income=base_income(r),carry=p.carry,bonus=p.bonus,remaining=p.points,bank=p.bank,actions=deepcopy(p.actions),exposure=exposure[p.pid],deck=[n for n,t in p.deck]) for p in players]
        for item in ledger:
            assert item['income']-sum(a['cost'] for a in item['actions'])==item['remaining']
        snapshots={p.pid:p for p in players};archives={p.pid:p for p in dead};results=[]
        for a,b in pairs:
            one=snapshots[a];ghost=isinstance(b,str);two=archives[int(b[1:])] if ghost else snapshots[b]
            battle_seed=int.from_bytes(__import__('hashlib').sha256(f'{seed}:{r}:{a}:{b}'.encode()).digest()[:8],'big')
            battle_class=StrictBattle if sequence_mode=='locked' else Battle
            battle=battle_class(([v for v,t in one.deck],[v for v,t in two.deck]),(one.spec,two.spec),battle_seed,trace,styles=(one.style,two.style),sequence_mode=sequence_mode)
            battle.policy_configs=(one.policy,two.policy)
            result=battle.run();battle_counter+=1
            for index,p in [(0,one)]+([] if ghost else [(1,two)]):
                won=result['winner']==index;draw=result['winner'] is None
                loss=arena_loss(r,result['hp'][1-index],won=won,draw=draw)
                p.hp-=loss;p.streak=0 if draw else max(0,p.streak)+1 if won else min(0,p.streak)-1
            participants=[dict(pid=one.pid,spec=one.spec,route=one.route,style=one.style,deck=[v for v,t in one.deck]),
                          dict(pid=two.pid,spec=two.spec,route=two.route,style=two.style,deck=[v for v,t in two.deck])]
            results.append(dict(a=a,b=b,ghost=ghost,participants=participants,**result))
        eliminated=[p for p in players if p.hp<=0]
        for p in eliminated:
            placements.append(dict(pid=p.pid,hp=p.hp,round=r,spec=p.spec,route=p.route,style=p.style,deck=[n for n,t in p.deck]))
            dead.append(deepcopy(p))
            for name,token in p.deck:pool.remove(token)
        players=[p for p in players if p.hp>0]
        pool.audit(players)
        rounds.append(dict(round=r,events=events,ledger=ledger,battles=results,eliminated=[p.pid for p in eliminated]))
        if len(players)<=1:
            for p in players:placements.append(dict(pid=p.pid,hp=p.hp,round=r+1,spec=p.spec,route=p.route,style=p.style,deck=[n for n,t in p.deck]))
            placements.sort(key=lambda p:(p['round'],p['hp']),reverse=True)
            scores=(3,1,-1,-3);pos=0
            while pos<4:
                end=pos+1
                while end<4 and (placements[end]['round'],placements[end]['hp'])==(placements[pos]['round'],placements[pos]['hp']):end+=1
                for p in placements[pos:end]:p.update(rank=pos+1,score=sum(scores[pos:end])/(end-pos))
                pos=end
            return dict(version='1.2',sequence_rule=sequence_mode,seed=seed,styles=initial_styles,rounds=rounds,placements=placements,battles=battle_counter,
                        pool_total=len(pool.tokens),pool_stats=dict(pool.stats))
    raise RuntimeError('Tournament did not terminate; no fabricated champion')
