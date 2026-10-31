"""Five 100-room budgets for each family; 1000 unique rooms in total.

Each budget reserves 60 train, 20 validation, 20 final-test rooms. All 200 final
rooms run only after both fifth-round checkpoints freeze. No default promotion.
"""
import argparse,gzip,hashlib,json,random,time
from copy import deepcopy
from collections import Counter
from pathlib import Path
from concurrent.futures import ProcessPoolExecutor,as_completed
from .catalog import ROOT,CARDS,copies,export
from .free_train import win_interval,describe,training_rows,correlation
from .thin_train import safe_room,paired_interval,deck_metrics
from .train import group_metrics
from .cohort import selected_statistics
from .analytics import Statistics
from .quick_test import save,reports

def initialize(resume_from=None):
 if resume_from:
  source=Path(resume_from)
  models={f:json.loads((source/f'{f}-final-policy.json').read_text(encoding='utf-8')) for f in ('medium','small')}
  for family,cfg in models.items():cfg['checkpoint']=f'dual-{family}-continued-initial'
  return models
 small=json.loads((Path(__file__).parent/'fixtures/small-policy.json').read_text(encoding='utf-8'))
 medium=json.loads((Path(__file__).parent/'fixtures/medium-policy.json').read_text(encoding='utf-8'))
 small['checkpoint']='dual-small-initial';medium['checkpoint']='dual-medium-initial'
 # Explicit but soft functional preferences: medium is not merely a larger
 # draw-heavy deck. No forced element, purchase, or flow-fraction quota.
 medium['role_preferences']=dict(numeric=1.15,strong=.85,weak=1.)
 small['role_preferences']=dict(numeric=.98,strong=1.08,weak=1.02)
 return dict(medium=medium,small=small)

def owned_room(seed,first,second,folder,segment,owner):
 result=safe_room(seed,first,second,folder,segment)
 if result['ok']:
  r=result['room'];r['comparison']['budget_owner']=owner
  with gzip.open(Path(folder)/f'room-{seed}.json.gz','wt',encoding='utf-8') as h:json.dump(r,h,ensure_ascii=False)
 return result

def propose(best,initial,step,direction,family,run_seed=0):
 cfg=deepcopy(best);cfg['checkpoint']=f'dual-{family}-candidate-{step:02d}'
 rng=random.Random(22000+step+(0 if family=='medium' else 100)+run_seed)
 for section,prefix,rate in (('weights','w.',.06),('construction','c.',.10)):
  for key,value in cfg[section].items():
   anchor=initial[section][key];signal=direction.get(prefix+key,rng.choice((-1,1)))
   change=rate*(.6*signal+.4*rng.choice((-1,1)))
   cfg[section][key]=round(max(.6*anchor,min(1.6*anchor,value*(1+change))),6)
 if 'breadth' in cfg:
  for key in ('fill_bonus','retention_bonus','paid_delete_penalty','quality_floor'):
   anchor=initial['breadth'][key]
   cfg['breadth'][key]=round(max(.75*anchor,min(1.25*anchor,cfg['breadth'][key]*(1+.08*rng.choice((-1,1))))),6)
 for key,value in cfg['role_preferences'].items():
  anchor=initial['role_preferences'][key]
  cfg['role_preferences'][key]=round(max(.94*anchor,min(1.06*anchor,value*(1+.03*rng.choice((-1,1))))),6)
 return cfg

def mirror(rooms):
 return [dict(r,comparison=dict(r['comparison'],first_seats=sorted(set(range(4))-set(r['comparison']['first_seats'])))) for r in rooms]

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--out',required=True);ap.add_argument('--workers',type=int,default=12)
 ap.add_argument('--seed',type=int,default=294000);ap.add_argument('--resume-from');args=ap.parse_args()
 out=Path(args.out).resolve();out.mkdir(parents=True,exist_ok=False);export(out)
 initial=initialize(args.resume_from);best=deepcopy(initial);save(out/'initial-policies.json',initial)
 files=list(Path(__file__).parent.rglob('*.py'))+list((Path(__file__).parent/'rules').glob('*.json'))+list((ROOT/'docs/cards-v1.2').glob('*.md'))+list((ROOT/'docs/cards-v1.2').glob('*.json'))
 hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
 source_hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in Path(args.resume_from).resolve().glob('*-final-policy.json')} if args.resume_from else {}
 save(out/'inputs.json',dict(attempted=1000,per_family_budget=500,seeds=[args.seed,args.seed+999],cards=len(CARDS),stock=sum(copies(c) for c in CARDS.values()),
  protocol='each family5*(60train20validation20reserved);200joint frozen final after both step5;win-only;independent updates;no default change',
  resumed_from=args.resume_from,initial_model_sha256=source_hashes,sha256=hashes))
 directions={f:{} for f in best};batches={f:[] for f in best};paths=[];reserved=[];issues=[];started=time.monotonic()
 with ProcessPoolExecutor(max_workers=args.workers) as pool:
  def collect(jobs):
   rs=[]
   for future in as_completed([pool.submit(owned_room,*j) for j in jobs]):
    result=future.result()
    if result['ok']:rs.append(result['room'])
    else:issues.append(result)
   return sorted(rs,key=lambda r:r['seed'])
  for step in range(1,6):
   candidates={f:propose(best[f],initial[f],step,directions[f],f,args.seed) for f in best}
   jobs=[]
   for index,family in enumerate(('medium','small')):
    folder=out/family/'batches'/f'{step:02d}';folder.mkdir(parents=True)
    save(folder/'candidate.json',candidates[family]);save(folder/'incumbent.json',best[family])
    seed0=args.seed+(step-1)*200+index*100
    jobs.extend((seed0+i,candidates[family],best[family],str(folder),'train' if i<60 else 'validation',family) for i in range(80))
    reserved.extend((seed0+i,family) for i in range(80,100))
   rooms=collect(jobs)
   for family in ('medium','small'):
    rs=[r for r in rooms if r['comparison']['budget_owner']==family]
    train=[r for r in rs if r['comparison']['segment']=='train'];val=[r for r in rs if r['comparison']['segment']=='validation']
    td,vd=win_interval(train),win_interval(val)
    accept=len(train)==60 and len(val)==20 and td['mean']>=0 and vd['mean']>=.005
    if accept:best[family]=deepcopy(candidates[family]);best[family]['checkpoint']=f'dual-{family}-accepted-{step:02d}'
    rows=training_rows(train)
    directions[family]={prefix+k:correlation(rows,prefix+k) for section,prefix in (('weights','w.'),('construction','c.')) for k in initial[family][section]}
    choice=dict(round=step,accepted=accept,selected=best[family]['checkpoint'],train=td,validation=vd,train_decks=describe(train),gradient=directions[family])
    folder=out/family/'batches'/f'{step:02d}'
    save(folder/'selection.json',choice);save(folder/'checkpoint.json',best[family]);batches[family].append(choice)
    paths.extend(folder/f"room-{r['seed']}.json.gz" for r in rs)
   save(out/'issues.json',issues)
   progress=dict(learning_round=step,completed=len(paths),reserved=len(reserved),skipped=len(issues),
    checkpoints={f:best[f]['checkpoint'] for f in best},elapsed=round(time.monotonic()-started,1))
   save(out/'progress.json',progress);print(json.dumps(progress),flush=True)
  save(out/'frozen-policies.json',best)
  folder=out/'final';folder.mkdir()
  jobs=[(seed,best['medium'],best['small'],str(folder),'final-holdout',owner) for seed,owner in sorted(reserved)]
  final=collect(jobs);paths.extend(folder/f"room-{r['seed']}.json.gz" for r in final)
 for name,digest in hashes.items():assert hashlib.sha256((ROOT/name).read_bytes()).hexdigest()==digest,name
 stats=Statistics()
 for path in sorted(paths):
  with gzip.open(path,'rt',encoding='utf-8') as h:r=json.load(h)
  stats.add(r)
 result=stats.result();result['input_hashes_verified']=True;save(out/'statistics.json',result);reports(out,result)
 evaluation=dict(games=len(final),medium=group_metrics(final),small=group_metrics(final,False),paired_win=win_interval(final),paired_score=paired_interval(final),
  medium_decks=describe(final),small_decks=describe(final,False),medium_actions=deck_metrics(final),small_actions=deck_metrics(final,False))
 summary=dict(completed=len(paths),attempted=1000,battles=stats.battles,skipped=len(issues),batches=batches,candidates=best,
  accepted_steps={f:sum(row['accepted'] for row in bs) for f,bs in batches.items()},evaluation=evaluation,
  budgets={f:dict(train=300,validation=100,final_reserved=100,total=500) for f in best},final_test_games=len(final),
  input_hashes_verified=True,default_changed=False)
 save(out/'training.json',summary);save(out/'issues.json',issues)
 save(out/'medium-final-statistics.json',selected_statistics(final));save(out/'small-final-statistics.json',selected_statistics(mirror(final)))
 for f in best:save(out/f'{f}-final-policy.json',best[f])
 save(out/'progress.json',dict(completed=len(paths),attempted=1000,battles=stats.battles,skipped=len(issues),finished=True,elapsed=round(time.monotonic()-started,1)))
 print('DUAL_TRAINING_COMPLETE '+str(out/'training.json'),flush=True)

if __name__=='__main__':main()
