"""Five rounds x five elements x 60 unique rooms = 1500.
Each element: 180 train / 60 validation / 60 frozen test rooms. One focal seat
per room, balanced assigned route intentions and internal/external matches.
"""
import argparse,gzip,hashlib,json,random,time,traceback
from copy import deepcopy
from pathlib import Path
from concurrent.futures import ProcessPoolExecutor,as_completed
from .catalog import ROOT,CARDS,ELEMENTS,ROUTES,copies,export
from .outcome_train import objective,stages,propose,reward_rows
from .free_train import describe,correlation
from .train import group_metrics
from .cohort import selected_statistics
from .analytics import Statistics
from .quick_test import save,reports

SIZES=dict(fire=10,water=12,earth=14,wind=10,poison=14)
PROFILES=dict(fire=dict(recycle=1.25,draw=1.1,passive=1.1),water=dict(layer=1.2,protection=1.2,future=1.1),
 earth=dict(protection=1.35,passive=1.15,panel=1.1),wind=dict(draw=1.15,fee=1.15,tempo=1.2,lookahead=1.15),
 poison=dict(layer=1.3,protection=1.2,passive=1.15))

def initialize(source,e):
 c=deepcopy(source);c.pop('breadth',None);c.pop('role_preferences',None)
 c['element_intent']=e;c['checkpoint']=f'{e}-style-initial'
 for k,v in PROFILES[e].items():c['weights'][k]*=v
 c['construction']['first_layer']*=1.2 if e in ('water','poison') else 1.05
 if e in ('water','earth','poison'):c['construction']['dilution']*=.9
 c['size_exploration']=dict(bonus=0.,ceiling=SIZES[e]+4)
 return c

def room(seed,e,mode,route_index,first,others,folder,segment):
 try:
  from .tournament import simulate
  rng=random.Random(seed^84521);seat=rng.randrange(4);configs=[];opponents=[]
  other_elements=[x for x in ELEMENTS if x!=e]
  rng.shuffle(other_elements)
  for i in range(4):
   oe=e if mode=='internal' or i==seat else other_elements.pop()
   cfg=deepcopy(first if i==seat else others[oe]);cfg['element_intent']=oe
   # Route preferences never grant cards or bypass unique route ownership.
   intent=ROUTES[oe][(route_index+(0 if i==seat else i+1))%3]
   cfg['route_intent']=intent;configs.append(cfg)
   if i!=seat:opponents.append(cfg['checkpoint'])
  r=simulate(seed,policy_by_seat=configs)
  r['comparison']=dict(first=first['checkpoint'],second='opponent-pool',first_seats=[seat],segment=segment,
   element=e,mode=mode,route_intent=ROUTES[e][route_index],opponents=opponents)
  with gzip.open(Path(folder)/f'room-{seed}.json.gz','wt',encoding='utf-8') as h:json.dump(r,h,ensure_ascii=False)
  return dict(ok=True,room=r)
 except Exception as exc:
  issue=dict(ok=False,seed=seed,element=e,mode=mode,error=repr(exc),traceback=traceback.format_exc())
  save(Path(folder)/f'error-{seed}.json',issue);return issue

def metrics(rs):
 return dict(games=len(rs),selected=group_metrics(rs),control=group_metrics(rs,False),objective=objective(rs),
  stages=stages(rs),decks=describe(rs))

def assess(rs):
 return {mode:metrics([r for r in rs if r['comparison']['mode']==mode]) for mode in ('internal','external')}

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--out',required=True);ap.add_argument('--seed',type=int,default=298000);ap.add_argument('--workers',type=int,default=12);a=ap.parse_args()
 out=Path(a.out).resolve();out.mkdir(parents=True,exist_ok=False);export(out)
 source=ROOT/'research/v1.2-outcome-1000-20261029/final-policy.json';base=json.loads(source.read_text(encoding='utf-8'))
 initial={e:initialize(base,e) for e in ELEMENTS};best=deepcopy(initial)
 save(out/'initial-policies.json',initial)
 files=list(Path(__file__).parent.rglob('*.py'))+list((Path(__file__).parent/'rules').glob('*.json'))+list((ROOT/'docs/cards-v1.2').glob('*.md'))+list((ROOT/'docs/cards-v1.2').glob('*.json'))
 hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
 save(out/'inputs.json',dict(cards=len(CARDS),stock=sum(copies(c) for c in CARDS.values()),seeds=[a.seed,a.seed+1499],
  source=str(source.relative_to(ROOT)),source_sha256=hashlib.sha256(source.read_bytes()).hexdigest(),sha256=hashes,
  budget='each element5*(36train12validation12reserved);180/60/60;1500unique;1focal+3controls',
  protocol='route intentions balanced, no forced cards; internal all same element, external three different elements; each mode50%; final after all step5 frozen',
  objective='equal champion / win / normalized score; train-only size reward up to .02, bonus levels0,1.5,3,4.5,6; validation outcome only',
  gate='train augmented composite>=0; validation composite>=.005; each mode composite>=-.03; no validation component below-.08; retain otherwise',
  final='fixed initial style controls current rules, not historical win rates; no tuning after final; no default promotion'))
 directions={e:{} for e in ELEMENTS};batches={e:[] for e in ELEMENTS};paths=[];reserved=[];issues=[];started=time.monotonic()
 with ProcessPoolExecutor(max_workers=a.workers) as pool:
  def collect(jobs):
   rs=[]
   for f in as_completed([pool.submit(room,*job) for job in jobs]):
    v=f.result()
    if v['ok']:rs.append(v['room'])
    else:issues.append(v)
   return sorted(rs,key=lambda r:r['seed'])
  for step in range(1,6):
   candidates={};jobs=[];snapshot=deepcopy(best);folders={}
   for ei,e in enumerate(ELEMENTS):
    c=propose(best[e],initial[e],step,directions[e],a.seed+ei*700)
    c['checkpoint']=f'{e}-candidate-{step:02d}';c['size_exploration']['bonus']=1.5*(step-1);candidates[e]=c
    folder=out/e/'batches'/f'{step:02d}';folder.mkdir(parents=True);folders[e]=folder
    save(folder/'candidate.json',c);save(folder/'incumbent.json',best[e]);save(folder/'opponent-snapshot.json',snapshot)
    start=a.seed+(step-1)*300+ei*60
    for i in range(60):
     mode='internal' if i%2==0 else 'external';route_index=(i//2)%3
     if i<48:jobs.append((start+i,e,mode,route_index,c,snapshot,str(folder),'train' if i<36 else 'validation'))
     else:reserved.append((start+i,e,mode,route_index))
   rooms=collect(jobs)
   for e in ELEMENTS:
    rs=[r for r in rooms if r['comparison']['element']==e];train=[r for r in rs if r['comparison']['segment']=='train'];val=[r for r in rs if r['comparison']['segment']=='validation']
    td,vd=objective(train),objective(val);mode=assess(val)
    # Larger decks are a bounded training exploration incentive, never a test reward.
    size_reward=.02*(step-1)/4*min(1,max(0,(describe(train)['mean_size']-8)/(SIZES[e]+4-8)))
    accept=(len(train)==36 and len(val)==12 and td['composite']['mean']+size_reward>=0 and vd['composite']['mean']>=.005
     and all(v['objective']['composite']['mean']>=-.03 for v in mode.values())
     and all(vd[k]['mean']>=-.08 for k in ('championship','win','normalized_score')))
    if accept:best[e]=deepcopy(candidates[e]);best[e]['checkpoint']=f'{e}-accepted-{step:02d}'
    focal_rows=[]
    for r in train:
     seats=set(r['comparison']['first_seats'])
     for p,x,y in reward_rows([r]):
      if p['pid'] in seats:focal_rows.append((p,x,y+.02*(step-1)/4*min(1,max(0,(len(p['deck'])-8)/(SIZES[e]+4-8)))))
    directions[e]={prefix+k:correlation(focal_rows,prefix+k) for section,prefix in (('weights','w.'),('construction','c.')) for k in initial[e][section]}
    choice=dict(round=step,accepted=accept,selected=best[e]['checkpoint'],size_bonus=candidates[e]['size_exploration']['bonus'],size_reward=size_reward,
     candidate_decks=describe(train),train=td,validation=vd,validation_modes=mode,gradient=directions[e])
    save(folders[e]/'selection.json',choice);save(folders[e]/'checkpoint.json',best[e]);batches[e].append(choice)
    paths.extend(folders[e]/f"room-{r['seed']}.json.gz" for r in rs)
   save(out/'issues.json',issues);progress=dict(round=step,completed=len(paths),reserved=len(reserved),skipped=len(issues),checkpoints={e:c['checkpoint'] for e,c in best.items()},elapsed=round(time.monotonic()-started,1))
   save(out/'progress.json',progress);print(json.dumps(progress),flush=True)
  save(out/'frozen-policies.json',best);folder=out/'final';folder.mkdir()
  final=collect([(seed,e,mode,ri,best[e],initial,str(folder),'final-holdout') for seed,e,mode,ri in sorted(reserved)])
  paths.extend(folder/f"room-{r['seed']}.json.gz" for r in final)
 for path,digest in hashes.items():assert hashlib.sha256((ROOT/path).read_bytes()).hexdigest()==digest,path
 stats=Statistics()
 for path in paths:
  with gzip.open(path,'rt',encoding='utf-8') as h:stats.add(json.load(h))
 result=stats.result();save(out/'statistics.json',result);reports(out,result)
 ev={}
 for e in ELEMENTS:
  rs=[r for r in final if r['comparison']['element']==e]
  ev[e]=dict(overall=metrics(rs),modes=assess(rs));save(out/f'{e}-final-policy.json',best[e]);save(out/f'{e}-final-statistics.json',selected_statistics(rs))
 save(out/'training.json',dict(completed=len(paths),battles=stats.battles,skipped=len(issues),batches=batches,evaluation=ev,
  accepted={e:[b['round'] for b in bs if b['accepted']] for e,bs in batches.items()},input_hashes_verified=True,default_changed=False))
 save(out/'issues.json',issues);save(out/'progress.json',dict(finished=True,completed=len(paths),battles=stats.battles,skipped=len(issues),elapsed=round(time.monotonic()-started,1)))
 print('ELEMENT_TRAINING_COMPLETE '+str(out/'training.json'),flush=True)

if __name__=='__main__':main()
