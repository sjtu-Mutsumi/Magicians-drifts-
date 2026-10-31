"""python -m tools.sim_v12 --games 5 --seed 1200 --out research/<new-folder>"""
from collections import Counter
from pathlib import Path
import argparse
import hashlib
import json
import sys
from .catalog import ROOT, CARDS, START, START_ONLY, copies, export
from .effects import verify_registry
from .policy import TARGETS

def main():
    parser=argparse.ArgumentParser(description='Independent v1.2 four-AI simulation')
    parser.add_argument('--games',type=int,default=5)
    parser.add_argument('--seed',type=int,default=1200)
    parser.add_argument('--out',required=True)
    parser.add_argument('--trace',action='store_true')
    args=parser.parse_args()
    if args.games<1:parser.error('games must be positive')
    if hasattr(sys.stdout,'reconfigure'):sys.stdout.reconfigure(encoding='utf-8')
    out=Path(args.out).resolve()
    out.mkdir(parents=True,exist_ok=False)
    export(out)
    files=list(Path(__file__).parent.rglob('*.py'))+list((Path(__file__).parent/'rules').glob('*.json'))
    files+=list((ROOT/'docs/cards-v1.2').glob('*.md'))
    manifest={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
    manifest.update({'_version':'1.2','_games':args.games,'_seed':args.seed})
    (out/'inputs.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    (out/'target-decks.json').write_text(json.dumps(TARGETS,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    assert verify_registry()==len(CARDS)
    assert sum(copies(c) for c in CARDS.values())==sum(copies(c) for c in CARDS.values())
    assert all(copies(CARDS[n])==0 for n in START_ONLY)
    from .tournament import simulate
    elements=Counter({e:0 for e in ('fire','water','earth','wind','poison')})
    routes=Counter({r:0 for r in TARGETS});element_counts=Counter();route_counts=Counter()
    executed=Counter();battles=0;round_counts=[]
    with (out/'episodes.jsonl').open('w',encoding='utf-8') as handle:
        for index in range(args.games):
            result=simulate(args.seed+index,trace=args.trace)
            handle.write(json.dumps(result,ensure_ascii=False)+'\n');handle.flush()
            assert len(result['placements'])==4
            assert sum(p['score'] for p in result['placements'])==0
            for p in result['placements']:
                if p['spec']:elements[p['spec']]+=p['score'];element_counts[p['spec']]+=1
                if p['route']:routes[p['route']]+=p['score'];route_counts[p['route']]+=1
            for r in result['rounds']:
                for b in r['battles']:executed.update(b['executed'])
            battles+=result['battles'];round_counts.append(len(result['rounds']))
            print(f'Completed {index+1}/{args.games}: rounds={round_counts[-1]}, battles={result["battles"]}',flush=True)
    for name,digest in manifest.items():
        if not name.startswith('_'):assert hashlib.sha256((ROOT/name).read_bytes()).hexdigest()==digest,name
    summary=dict(version='1.2',games=args.games,battles=battles,round_counts=round_counts,
                 card_count=len(CARDS),registered_effects=len(CARDS),pool_copies=sum(copies(c) for c in CARDS.values()),starter=list(START),
                 starter_only=sorted(START_ONLY),executed_cards=len(executed),executed=dict(executed),
                 element_scores=dict(elements),route_scores=dict(routes),
                 element_selections=dict(element_counts),route_selections=dict(route_counts),
                 interpretation='Smoke tests do not establish balance or optimal AI play' if args.games<1000 else 'Heuristic AI sample; not proof of optimal play')
    (out/'summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(str(out/'summary.json'))

if __name__=='__main__':main()
