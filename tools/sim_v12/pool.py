"""Finite-copy shared offers; joint min-cost matching, no rarity rerolls."""
import json
import math
import random
from pathlib import Path
from collections import Counter, defaultdict, deque
from . import catalog as C
from .construction_templates import apply_refresh_legendary_guarantee

CFG = json.loads((Path(__file__).parent/'rules/probabilities.json').read_text(encoding='utf-8'))
RANK = {'common':0,'rare':1,'epic':2,'legendary':3}
ELEMENTS = ['fire','water','earth','wind','poison']
SPECIAL = {c.element:n for n,c in C.CARDS.items() if c.kind=='specialization'}
ENGINE_NAMES = set(CFG['generic_engine_legendaries'])


def category(name):
    c=C.CARDS[name]
    if c.kind=='route_legendary':return 'route'
    if c.element!='generic':return 'stacking'
    return 'generic_engine' if name in ENGINE_NAMES else 'generic_other'


def stochastic_round(x,rng):
    return math.floor(x)+(rng.random()<x-math.floor(x))


def copies(c,config=None):
    cfg=config or CFG
    if c.name in C.START_ONLY or c.rarity=='specialization':return 0
    if c.rarity!='legendary':return cfg.get('card_copy_overrides',{}).get(c.name,cfg['nonlegendary_copy_counts'][c.rarity]['generic' if c.element=='generic' else 'enchantment'])
    return cfg['legendary_copy_counts']['generic' if c.element=='generic' else ('route' if c.kind=='route_legendary' else 'stacking')]


def assert_uniform_counts(cfg):
    """Hard design boundary: six rarity/category buckets, never card-name bias."""
    groups=defaultdict(set)
    for c in C.CARDS.values():
        if c.rarity in ('common','rare','epic') and c.name not in C.START_ONLY:
            q=copies(c,cfg)
            assert isinstance(q,int) and q>0
            groups[(c.rarity,'generic' if c.element=='generic' else 'enchantment')].add(q)
    assert all(len(v)==1 for v in groups.values()),'Nonlegendary copies must be uniform within rarity/category'
    return {r:{g:next(iter(groups[(r,g)])) for g in ('enchantment','generic')} for r in ('common','rare','epic')}


class Pool:
    def __init__(self, unlimited_generic=False, noncompetitive=False,config=None):
        self.cfg=config or CFG
        assert_uniform_counts(self.cfg)
        self.tokens={}
        self.free=defaultdict(list)
        for name,c in C.CARDS.items():
            count=4 if unlimited_generic and c.rarity=='legendary' and c.element=='generic' else copies(c,self.cfg)
            if noncompetitive:count=max(4,count*4) if count else 0
            for k in range(count):
                token=f'{name}#{k}'
                self.tokens[token]=name
                self.free[name].append(token)
        self.owner={}
        self.pending=[]
        self.seen=set()
        self.player_seen=[set() for _ in range(4)]
        self.offers={}
        self.stats=Counter()

    def begin(self):
        assert not self.offers
        for token in self.pending:self.free[self.tokens[token]].append(token)
        self.pending=[];self.seen=set();self.player_seen=[set() for _ in range(4)]

    def release(self,pid):
        for token in self.offers.pop(pid,[None]*8):
            if token:self.free[self.tokens[token]].append(token)

    def remove(self,token):
        if token:
            assert token in self.owner
            del self.owner[token];self.pending.append(token)

    def buy(self,pid,slot):
        token=self.offers[pid][slot]
        assert token and token not in self.owner
        self.offers[pid][slot]=None;self.owner[token]=pid
        return token

    def audit(self,players):
        parts=[t for ts in self.free.values() for t in ts]+list(self.owner)+self.pending+[t for xs in self.offers.values() for t in xs if t]
        assert len(parts)==len(self.tokens) and len(set(parts))==len(parts)
        for p in players:
            names=[n for n,t in p.deck]
            counts=Counter(names)
            assert all(v==1 or n in ('攻击强化','防御强化') and v<=2 for n,v in counts.items())
            for n,t in p.deck:
                if t:assert self.owner[t]==p.pid and self.tokens[t]==n
            assert sum(C.CARDS[n].kind=='route_legendary' for n in names)<=1
            assert sum(C.CARDS[n].rarity=='legendary' for n in names)<=3
            if p.spec:assert sum(C.CARDS[n].element==p.spec and C.CARDS[n].kind!='specialization' for n in names)>=3

    def generate(self,players,templates,round_num,face,rng,bought=None):
        """Plan slots jointly, weighted random costs, explicit shortage downgrades.

        Minimum-cost matching is a constrained weighted sampler, not independent
        per-card draws. All category/rarity deviations are counted.
        """
        slots=[];wanted={};desired_type={};active_by_player={}
        for p in players:
            active=[i for i in range(8) if not bought or i not in bought[p.pid]]
            active_by_player[p.pid]=active
            if not active:continue
            ranks=list(templates[p.pid])
            if face==1 and str(round_num) in CFG['second_face_legendary_guarantees']:
                ranks,_=apply_refresh_legendary_guarantee(ranks,set(range(8))-set(active),rng)
            if p.spec:target=stochastic_round(self.cfg['post_specialization_enchantment_share']*len(active),rng)
            else:
                distribution=self.cfg['pre_specialization_enchantment_count']
                target=stochastic_round(rng.choices([int(k) for k in distribution],list(distribution.values()))[0]*len(active)/8,rng)
            used=set();assigned={}
            allowed=ELEMENTS[:] if not p.spec else [p.spec]
            if face==1 and not p.spec:
                allowed=[e for e in allowed if e not in p.bought_elements or rng.random()<self.cfg['refresh_bought_element_admission']]
            counts=Counter(C.CARDS[n].element for n,t in p.deck if C.CARDS[n].kind!='specialization')
            def choose_element(options):
                bias=self.cfg.get('pre_progress_weight_per_card',0) if not p.spec else 0
                return rng.choices(options,[1+bias*min(2,counts[e]) for e in options])[0]
            leg=[i for i in active if ranks[i]==3]
            for i in leg:
                forced=face==0 and round_num in (8,10,12) and i<2
                types=CFG['legendary_type_order'];weights=CFG['legendary_types']['6_to_8' if round_num<=8 else '9_plus']
                typ='route' if forced else rng.choices(types,weights)[0]
                desired_type[p.pid,i]=typ
                if typ in ('route','stacking'):
                    options=[e for e in allowed if p.spec or e not in used]
                    if options:
                        assigned[i]=choose_element(options);used.add(assigned[i])
                    else:assigned[i]='generic'
                else:assigned[i]='generic'
            rest=[i for i in active if i not in assigned];rng.shuffle(rest)
            need=max(0,target-sum(e!='generic' for e in assigned.values()))
            for i in rest:
                options=[e for e in allowed if p.spec or e not in used]
                if need>0 and options:
                    assigned[i]=choose_element(options);used.add(assigned[i]);need-=1
                else:assigned[i]='generic'
            wanted[p.pid]=target
            for i in active:slots.append((p,i,ranks[i],assigned[i]))
        # Graph: source -> slot -> (player,name) -> card name -> sink.
        # player-name capacity one prevents duplicate same-face names.
        graph=defaultdict(list)
        def edge(a,b,cap,cost):
            graph[a].append([b,len(graph[b]),cap,cost,cap])
            graph[b].append([a,len(graph[a])-1,0,-cost,0])
        candidates={}
        for name,tokens in self.free.items():
            avail=[t for t in tokens if C.CARDS[name].rarity!='legendary' or t not in self.seen]
            if avail:
                candidates[name]=avail
                edge(('name',name),'sink',len(avail),0)
        pn=set()
        for p,i,rank,element in slots:
            slot=('slot',p.pid,i);edge('source',slot,1,0)
            owned={n for n,t in p.deck}
            for name,avail in candidates.items():
                c=C.CARDS[name];cr=RANK[c.rarity]
                if cr>rank or name in owned:continue
                if p.spec and c.element not in ('generic',p.spec):continue
                if not p.spec and c.element not in ('generic',element):continue
                if c.rarity=='legendary' and name in self.player_seen[p.pid]:continue
                if c.kind=='route_legendary' and round_num in (10,12) and any(C.CARDS[n].kind=='route_legendary' for n,t in p.deck):
                    shown=sum(C.CARDS[n].kind=='route_legendary' for n in self.player_seen[p.pid])
                    route_slot=next((slot for pp,slot,rr,el in slots if pp.pid==p.pid and rr==3),None)
                    if shown or i!=route_slot:continue
                key=('pn',p.pid,name)
                if key not in pn:
                    pn.add(key);edge(key,('name',name),1,0)
                downgrade=rank-cr
                guaranteed=rank==3 and (face==0 and round_num in (8,10,12) and i<3 or face==1 and round_num in (8,10,12))
                priority=(4-i) if guaranteed and face==0 and i<3 else 1
                cost=downgrade*(10000000*priority if guaranteed else 1000000)
                cost+=10000*(c.element!=element)
                if cr==3 and (p.pid,i) in desired_type:
                    route_priority=2 if face==0 and round_num in (8,10,12) and i==0 else 1
                    cost+=50000*route_priority*(category(name)!=desired_type[p.pid,i])
                # Weighted exponential race; same-bucket free-copy weighting.
                cost+=int(100*(-math.log(max(1e-12,rng.random()))/len(avail)))
                edge(slot,key,1,cost)
            edge(slot,'sink',1,1000000000)  # explicit empty when all legal cards exhausted
        for _ in slots:
            distance={'source':0};parent={};queue=deque(['source']);in_queue={'source'}
            while queue:
                a=queue.popleft();in_queue.remove(a)
                for j,e in enumerate(graph[a]):
                    b,rev,cap,cost,orig=e
                    if cap and distance.get(b,10**30)>distance[a]+cost:
                        distance[b]=distance[a]+cost;parent[b]=(a,j)
                        if b not in in_queue:queue.append(b);in_queue.add(b)
            assert 'sink' in parent
            b='sink'
            while b!='source':
                a,j=parent[b];e=graph[a][j];e[2]-=1;graph[b][e[1]][2]+=1;b=a
        result={p.pid:[None]*8 for p in players}
        for p,i,rank,element in slots:
            chosen=[e[0][2] for e in graph[('slot',p.pid,i)] if isinstance(e[0],tuple) and e[0][0]=='pn' and e[4] and not e[2]]
            if not chosen:self.stats['empty']+=1;continue
            name=chosen[0]
            token=rng.choice(candidates[name]);candidates[name].remove(token);self.free[name].remove(token)
            result[p.pid][i]=token
            c=C.CARDS[name]
            self.stats['generated']+=1
            self.stats['downgrade']+=RANK[c.rarity]<rank
            self.stats['category_deviation']+=c.element!=element
            if c.rarity=='legendary':
                assert token not in self.seen and name not in self.player_seen[p.pid]
                self.seen.add(token);self.player_seen[p.pid].add(name)
                self.stats['legendary']+=1
                self.stats['type_deviation']+=category(name)!=desired_type.get((p.pid,i),category(name))
        for p in players:
            board=result[p.pid]
            assert len([self.tokens[t] for t in board if t])==len(set(self.tokens[t] for t in board if t))
            if not p.spec:
                elems=[C.CARDS[self.tokens[t]].element for t in board if t and C.CARDS[self.tokens[t]].element!='generic']
                assert len(elems)==len(set(elems))
            self.offers[p.pid]=board
        return result
