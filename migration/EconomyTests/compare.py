from pathlib import Path
import json,subprocess,sys,math
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT))
from tools.sim_v12 import catalog as C
from tools.sim_v12.pool import Pool
from tools.sim_v12 import tournament as T,policy
from tools.sim_v12.six_styles import IDS,load_policy
import random

actual=json.loads(subprocess.check_output([r'C:\Program Files\dotnet\dotnet.exe',str(Path(__file__).parent/'bin/Debug/net8.0/EconomyTests.dll'),str(ROOT)],cwd=ROOT))
expected=[]
for seed in (-17,0,1,17,334020):
    rng=random.Random(seed);pool=Pool()
    players=[T.Player(i,C.ELEMENTS[(seed+i)%5]) for i in range(4)]
    for p in players:p.policy=load_policy('flexible' if p.pid==0 else IDS[p.pid-1])
    pool.begin();templates=T.templates(players,1,rng);offers=pool.generate(players,templates[0],1,0,rng)
    events=[]
    for p in players:p.points=T.base_income(1)
    draft=[dict(pid=p.pid,values={n:policy.draft_value(p,n,T.public(players)) for n in C.CARDS}) for p in players]
    for p in players:T.buy_phase(p,players,pool,1,events)
    pool.audit(players)
    expected.append(dict(seed=seed,templates=templates,offers={pid:[pool.tokens[t] if t else None for t in xs] for pid,xs in offers.items()},draft=draft,
        players=[dict(pid=p.pid,spec=p.spec,points=p.points,deck=p.deck,actions=p.actions) for p in players],stats=dict(pool.stats)))
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
(Path(__file__).parent/'comparison.json').write_text(json.dumps(dict(status='passed',seeds=5,cards=183,players=4,comparisons='rarity templates, all offers, 3660 draft valuations, exact purchases, pool statistics'),indent=2),encoding='utf-8')
print('ECONOMY_PARITY_OK seeds=5 draft_values=3660 templates=OK offers=OK purchases=OK pool=OK')
