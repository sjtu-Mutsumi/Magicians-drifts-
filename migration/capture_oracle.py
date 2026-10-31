"""Capture the unchanged Python authority for cross-language differential tests."""
from pathlib import Path
import sys
import json
import dataclasses
from fractions import Fraction
import hashlib

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
from tools.sim_v12.catalog import CARDS, START, ELEMENTS, SPECIAL
from tools.sim_v12.strict import StrictBattle
from tools.sim_v12 import policy, tournament
from tools.sim_v12.six_styles import load_policy, IDS
from tools.client_v12.match import MatchSession


def exact(value):
    if isinstance(value,Fraction):
        return value.numerator if value.denominator==1 else str(value)
    if isinstance(value,dict):return {str(k):exact(v) for k,v in value.items()}
    if isinstance(value,(tuple,list)):return [exact(x) for x in value]
    if isinstance(value,set):return sorted(exact(x) for x in value)
    if dataclasses.is_dataclass(value):return exact(dataclasses.asdict(value))
    if hasattr(value,'getstate'):return exact(value.getstate())
    if value is None or isinstance(value,(str,int,float,bool)):return value
    raise TypeError(type(value).__name__)


def snapshot(b):
    return exact(dict(players=[vars(s) for s in b.players],time=b.time,
        log=b.log,operations=b.operations,executed=b.executed,
        executed_by_seat=b.executed_by_seat,flow_by_seat=b.flow_by_seat,
        cycle_decisions=b.cycle_decisions,sequence_skips=b.sequence_skips,
        planned_count=b.planned_count))


def card_case(name,rich):
    b=StrictBattle(([name,'火花','掠影','护盾','短促'],list(START)),seed=19,trace=True)
    b.begin_stage()
    s,e=b.players
    s.pile=list(s.instances);s.hand=s.pile[:3];s.pile=s.pile[3:]
    s.discard=[];s.drawn={x.uid for x in s.hand};s.energy=100
    if rich:
        s.hp=400;s.shield=250;e.shield=150;e.poison=6
        element=CARDS[name].element if CARDS[name].element in ELEMENTS else 'fire'
        s.specialization=element;s.active.add(SPECIAL[element])
        for el in ELEMENTS:s.layers[el]=5
        s.ember=3 if element=='fire' else 0;s.tide=75 if element=='water' else 0
    before=snapshot(b)
    b.play_local(0,'0:0')
    return dict(kind='card',name=name,rich=rich,before=before,after=snapshot(b))


def session_case(seed,styles,manual):
    session=MatchSession(seed,styles)
    response=exact(session.receive())
    rows=[dict(request=dict(op='start',seed=seed,ai_styles=styles),response=response)]
    for tick in range(2000):
        if response['status']!='decision':break
        d=response['decision'];kind=d['kind']
        if not manual or response['round']>=3:command=dict(kind='delegate_match')
        elif kind=='draft':
            legal=[c for c in d['offers'] if c['affordable']]
            command=dict(kind='buy',slot=legal[0]['slot']) if legal else dict(kind='end_face')
        elif kind=='refresh':command=dict(kind='refresh')
        elif kind=='play':
            cards=[c for c in d['hand'] if c['payable_cost']<=d['energy']]
            command=dict(kind='play',uid=cards[0]['uid'] if cards else None)
        elif kind=='select':command=dict(kind='select',uids=[c['uid'] for c in d['candidates'][:d['count']]])
        elif kind=='round':command=dict(kind='next_round')
        else:raise RuntimeError(kind)
        command['revision']=response['revision']
        response=exact(session.send(command))
        rows.append(dict(request=command,response=response))
    assert response['status']=='finished',response.get('error')
    session.thread.join(2)
    return dict(kind='session',seed=seed,styles=styles,manual=manual,exchanges=rows,
        reviews=[session.review(r['round']) for r in session.history])


def main():
    folder=ROOT/'migration/oracle';folder.mkdir(parents=True,exist_ok=True)
    target=folder/'baseline.jsonl'
    counts={}
    with target.open('w',encoding='utf-8',newline='\n') as stream:
        def add(row):
            counts[row['kind']]=counts.get(row['kind'],0)+1
            stream.write(json.dumps(exact(row),ensure_ascii=False,separators=(',',':'))+'\n')
        for name in CARDS:
            for rich in (False,True):add(card_case(name,rich))
        print('ORACLE cards=366',flush=True)
        targets=list(policy.TARGETS.values())
        for i,route in enumerate(targets):
            decks=[route['entries'],targets[(i+7)%len(targets)]['entries']]
            specs=[next((el for el,n in SPECIAL.items() if n in d),'') for d in decks]
            result=StrictBattle(decks,specs,12000+i,trace=True).run()
            add(dict(kind='battle',decks=decks,specializations=specs,seed=12000+i,result=result))
        print('ORACLE routes=15',flush=True)
        for i,style in enumerate(IDS):
            styles=(style,IDS[(i+1)%len(IDS)],IDS[(i+3)%len(IDS)])
            add(session_case(334020+i,styles,False))
        add(session_case(334004,('fire','water','poison'),True))
        print('ORACLE sessions=7',flush=True)
    report=dict(baseline_commit='0c0150dbeda044f06c0e31d02a157aa56c7106c8',
        python=sys.version,python_hash_seed=__import__('os').environ.get('PYTHONHASHSEED'),
        cases=counts,bytes=target.stat().st_size,sha256=hashlib.sha256(target.read_bytes()).hexdigest())
    (folder/'manifest.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(report,ensure_ascii=False))


if __name__=='__main__':main()
