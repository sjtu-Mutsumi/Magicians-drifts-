from pathlib import Path
import json,subprocess,sys,math,random
ROOT=Path(__file__).resolve().parents[2];sys.path.insert(0,str(ROOT))
from tools.sim_v12 import catalog as C,tournament as T,policy
from tools.sim_v12.pool import Pool
from tools.sim_v12.construction import refresh_wanted
from tools.sim_v12.six_styles import IDS,load_policy
actual=json.loads(subprocess.check_output([r'C:\Program Files\dotnet\dotnet.exe',str(Path(__file__).parent/'bin/Debug/net8.0/EconomyTests.dll'),str(ROOT),'long'],cwd=ROOT))
expected=[]
for seed in (1,334020):
    rng=random.Random(seed);pool=Pool();players=[T.Player(i,C.ELEMENTS[(seed+i)%5]) for i in range(4)]
    for p in players:
        p.policy=load_policy('flexible' if p.pid==0 else IDS[(seed+p.pid)%5]);p.style=rng.choice(tuple(policy.STYLES))
    last={};from collections import Counter
    meetings=Counter();rounds=[]
    for r in range(1,13):
        pool.begin();pairs=T.schedule(players,[],last,meetings,rng)
        for p in players:p.carry=p.bank;p.bonus=0;p.income=T.base_income(r)+p.bank;p.points=p.income;p.free_used=False;p.bought_elements.clear();p.actions=[]
        templates=T.templates(players,r,rng);pool.generate(players,templates[0],r,0,rng)
        first={pid:[pool.tokens[t] if t else None for t in xs] for pid,xs in pool.offers.items()}
        events=[];bought={p.pid:T.buy_phase(p,players,pool,r,events) for p in players};refresh={}
        for p in players:
            if p.points>=2 and len(bought[p.pid])<8:
                pub=T.public(players);unbought=[pool.tokens[t] for t in pool.offers[p.pid] if t];wanted=refresh_wanted(p,r,unbought,pub)
                if wanted is True or wanted is None and (all(policy.draft_value(p,n,pub)<policy.STYLES[p.style]['refresh'] for n in unbought) or len(p.deck)<15):
                    p.points-=1;p.actions.append(dict(kind='refresh',cost=1));pool.release(p.pid);pool.generate([p],templates[1],r,1,rng,bought)
                    refresh[p.pid]=[pool.tokens[t] if t else None for t in pool.offers[p.pid]];T.buy_phase(p,players,pool,r,events)
            T.delete_phase(p,pool,r);p.bank=min(2,p.points);pool.release(p.pid)
        pool.audit(players)
        rounds.append(dict(r=r,pairs=pairs,templates=templates,first=first,refresh=refresh,events=events,players=[dict(pid=p.pid,style=p.style,spec=p.spec,points=p.points,bank=p.bank,deck=p.deck[:],actions=p.actions[:]) for p in players],stats=dict(pool.stats)))
    expected.append(dict(seed=seed,rounds=rounds,next=[rng.random() for _ in range(8)]))
expected=json.loads(json.dumps(expected,ensure_ascii=False))
def compare(a,b,path=''):
    if isinstance(a,(int,float)) and isinstance(b,(int,float)):
        if not math.isclose(a,b,rel_tol=1e-12,abs_tol=1e-12):raise AssertionError((path,a,b))
    elif type(a)!=type(b):raise AssertionError((path,type(a),type(b)))
    elif isinstance(a,dict):
        if set(a)!=set(b):raise AssertionError((path,'keys',set(a)^set(b)))
        for k in a:compare(a[k],b[k],path+'/'+str(k))
    elif isinstance(a,list):
        if len(a)!=len(b):raise AssertionError((path,'length',len(a),len(b)))
        for i,(aa,bb) in enumerate(zip(a,b)):compare(aa,bb,path+'/'+str(i))
    elif a!=b:raise AssertionError((path,a,b))
compare(expected,actual)
(Path(__file__).parent/'long-comparison.json').write_text(json.dumps(dict(status='passed',seeds=2,rounds=24,players=4,comparisons='schedules, all rarity templates/offers/refreshes, exact purchases/deletions, specialization events, inventories, next RNG outputs'),indent=2),encoding='utf-8')
print('ECONOMY_LONG_PARITY_OK seeds=2 rounds=24 templates=OK offers=OK refresh=OK purchases=OK deletion=OK specialization=OK rng=OK')
