"""20 x 100 attempted rooms; independent final 400-room blind comparison.

Rounds 1–16: 60 train + 20 validation + 20 unopened rolling controls.
Rounds 17–20: frozen model, no updates, 50 old-AI + 50 fixed-battle ablation.
Only marginal drafting/deletion parameters learn; battle weights stay fixed.
"""
import argparse,gzip,hashlib,json,math,random,time,traceback
from collections import Counter,defaultdict
from concurrent.futures import ProcessPoolExecutor,as_completed
from copy import deepcopy
from pathlib import Path
from .catalog import ROOT,CARDS,export,START_ONLY,copies
from .construction import THIN_INITIAL,REPEAT
from .train import worker as run_room,group_metrics
from .quick_test import save,reports
from .analytics import Statistics
from .cohort import selected_statistics

def safe_room(seed,first,second,folder,segment):
 try:return dict(ok=True,room=run_room(seed,first,second,folder,segment))
 except Exception as exc:
  row=dict(ok=False,seed=seed,segment=segment,first=first['checkpoint'],second=second['checkpoint'],
   error=repr(exc),traceback=traceback.format_exc())
  save(Path(folder)/f'error-{seed}.json',row)
  return row

def deck_metrics(rooms,first=True):
 rows=[];purchases=deletions=paid=0
 for r in rooms:
  seats=set(r['comparison']['first_seats'])
  if not first:seats=set(range(4))-seats
  for p in r['placements']:
   if p['pid'] not in seats:continue
   names=p['deck'];n=max(1,len(names))
   rows.append((n,sum(not CARDS[x].ability for x in names),sum(CARDS[x].element=='generic' for x in names)/n))
  for rd in r['rounds']:
   for row in rd['ledger']:
    if row['pid'] not in seats:continue
    for a in row['actions']:
     purchases+=a['kind']=='buy';deletions+=a['kind']=='delete';paid+=a['kind']=='delete' and a['cost']>0
 n=max(1,len(rows))
 return dict(players=len(rows),deck_size=sum(r[0] for r in rows)/n,repeatable_size=sum(r[1] for r in rows)/n,
  generic_fraction=sum(r[2] for r in rows)/n,purchases_per_player=purchases/n,deletions_per_player=deletions/n,paid_deletions_per_player=paid/n)

def paired_interval(rooms):
 values=[]
 for r in rooms:
  seats=r['comparison']['first_seats']
  values.append(sum(p['score']*(1 if p['pid'] in seats else -1) for p in r['placements'])/2)
 if not values:return dict(mean=None,low=None,high=None,games=0)
 mean=sum(values)/len(values);se=math.sqrt(sum((v-mean)**2 for v in values)/max(1,len(values)-1)/len(values))
 return dict(mean=mean,low=mean-1.96*se,high=mean+1.96*se,games=len(values))

def update_direction(rooms):
 rows=[];by_element=defaultdict(list);by_style=defaultdict(list)
 for r in rooms:
  deleted=Counter();bought=Counter();basic=Counter();stages=Counter();casts=defaultdict(Counter);flow=defaultdict(Counter)
  for rd in r['rounds']:
   for row in rd['ledger']:
    pid=row['pid'];stages[pid]+=1
    for a in row['actions']:
     bought[pid]+=a['kind']=='buy';deleted[pid]+=a['kind']=='delete'
     basic[pid]+=a['kind']=='delete' and a['name'] in START_ONLY
   for b in rd['battles']:
    for i,p in enumerate(b['participants']):
     if i==1 and b['ghost']:continue
     casts[p['pid']].update(b['executed_by_seat'][i]);flow[p['pid']].update(b['flow_by_seat'][i])
  for p in r['placements']:
   pid=p['pid'];names=p['deck'];n=max(1,len(names));turns=max(1,stages[pid]);f=flow[pid]
   x=dict(target=n,dilution=-n,buy_margin=-bought[pid]/turns,point_cost=-bought[pid]/turns,
    delete_margin=-deleted[pid]/turns,delete_pressure=deleted[pid]/turns,starter_bias=basic[pid]/turns,
    core_weight=sum(v for name,v in casts[pid].items() if name in REPEAT)/max(1,sum(casts[pid].values())),
    first_layer=sum(CARDS[name].layers for name in names)/n,
    generic_tax=-sum(CARDS[name].element=='generic' for name in names)/n,
    flow_scale=f['played']/max(1,5*f['stages']+f['instant_drawn']))
   y=p['score'];rows.append((p,x,y));by_element[p['spec']].append(y);by_style[p['style']].append(y)
 if not rows:return {k:0 for k in THIN_INITIAL}
 em={k:sum(v)/len(v) for k,v in by_element.items()};sm={k:sum(v)/len(v) for k,v in by_style.items()}
 ys=[y-.5*em[p['spec']]-.3*sm[p['style']] for p,x,y in rows];ym=sum(ys)/len(ys)
 sy=math.sqrt(sum((y-ym)**2 for y in ys)/len(ys));result={}
 for k in THIN_INITIAL:
  xs=[x[k] for p,x,y in rows];xm=sum(xs)/len(xs);sx=math.sqrt(sum((x-xm)**2 for x in xs)/len(xs))
  result[k]=sum((x-xm)*(y-ym) for x,y in zip(xs,ys))/len(xs)/max(1e-9,sx*sy)
 return result

def main():
 p=argparse.ArgumentParser();p.add_argument('--out',required=True);p.add_argument('--workers',type=int,default=12);args=p.parse_args()
 out=Path(args.out).resolve();out.mkdir(exist_ok=True);assert not (out/'batches').exists()
 previous=json.loads((ROOT/'research/v1.2-training-1000-final-20261028/final-policy.json').read_text(encoding='utf-8'))
 heavy=dict(kind='mechanism',weights=previous['weights'],checkpoint='fixed-battle-heavy-draft')
 legacy=dict(kind='legacy',checkpoint='fixed-legacy')
 incumbent=deepcopy(heavy);best=deepcopy(THIN_INITIAL)
 save(out/'baselines.json',dict(heavy=heavy,legacy=legacy));export(out)
 files=list(Path(__file__).parent.rglob('*.py'))+list((Path(__file__).parent/'rules').glob('*.json'))+list((ROOT/'docs/cards-v1.2').glob('*.md'))+list((ROOT/'docs/cards-v1.2').glob('*.json'))
 hashes={str(f.relative_to(ROOT)):hashlib.sha256(f.read_bytes()).hexdigest() for f in files}
 save(out/'inputs.json',dict(cards=len(CARDS),stock=sum(copies(c) for c in CARDS.values()),attempts=2000,rounds=20,seeds=[290000,291999],
  protocol='rounds1-16:60train20validation20unopened; rounds17-20:100frozen-final; final50/50legacy/heavy; no holdout updates',sha256=hashes))
 issues=[];batches=[];paths=[];start=time.monotonic()
 direction={k:(-1 if k in ('target','delete_margin') else 1) for k in THIN_INITIAL}
 with ProcessPoolExecutor(max_workers=args.workers) as pool:
  def collect(jobs):
   results=[]
   futures=[pool.submit(safe_room,*job) for job in jobs]
   for f in as_completed(futures):
    row=f.result()
    if row['ok']:results.append(row['room'])
    else:issues.append(row)
   return sorted(results,key=lambda r:r['seed'])
  for step in range(1,21):
   folder=out/'batches'/f'{step:02d}';folder.mkdir(parents=True);seed0=290000+(step-1)*100
   if step<=16:
    candidate=dict(kind='thin',weights=deepcopy(heavy['weights']),construction=deepcopy(best),checkpoint=f'thin-candidate-{step:02d}')
    norm=max(abs(v) for v in direction.values()) or 1
    for k in best:
     value=best[k]*(1+.03*direction[k]/norm)
     lo,hi=(14,22) if k=='target' else (.6*THIN_INITIAL[k],1.8*THIN_INITIAL[k])
     candidate['construction'][k]=round(max(lo,min(hi,value)),6)
    save(folder/'candidate.json',candidate);save(folder/'incumbent.json',incumbent)
    rooms=collect([(seed0+i,candidate,incumbent,str(folder),'train' if i<60 else 'validation') for i in range(80)])
    train=[r for r in rooms if r['comparison']['segment']=='train'];val=[r for r in rooms if r['comparison']['segment']=='validation']
    td=group_metrics(train)['mean_score']-group_metrics(train,False)['mean_score']
    vd=group_metrics(val)['mean_score']-group_metrics(val,False)['mean_score']
    accepted=len(train)>=50 and len(val)>=18 and td>=0 and vd>=.2
    if accepted:incumbent=deepcopy(candidate);incumbent['checkpoint']=f'thin-accepted-{step:02d}';best=deepcopy(candidate['construction'])
    direction=update_direction(train)
    selection=dict(round=step,accepted=accepted,train_games=len(train),validation_games=len(val),train_delta=td,validation_delta=vd,
     selected=incumbent['checkpoint'],train_deck=deck_metrics(train),validation_deck=deck_metrics(val),gradient=direction)
    save(folder/'selection.json',selection)
    # No outcomes from these rooms are inspected before training is over.
    held=collect([(seed0+i,incumbent,legacy if i<90 else heavy,str(folder),'rolling-holdout') for i in range(80,100)])
    rooms+=held
   else:
    accepted=None
    selection=dict(round=step,accepted=None,selected=incumbent['checkpoint'],frozen_final_test=True)
    rooms=collect([(seed0+i,incumbent,legacy if i<50 else heavy,str(folder),'final-holdout') for i in range(100)])
    save(folder/'selection.json',selection)
   paths.extend(folder/f"room-{r['seed']}.json.gz" for r in rooms);batches.append(selection)
   save(folder/'checkpoint.json',incumbent);save(out/'issues.json',issues)
   progress=dict(round=step,attempted=step*100,completed=len(paths),skipped=len(issues),checkpoint=incumbent['checkpoint'],accepted=accepted,elapsed=round(time.monotonic()-start,1))
   save(out/'progress.json',progress);print(json.dumps(progress),flush=True)
 for name,digest in hashes.items():assert hashlib.sha256((ROOT/name).read_bytes()).hexdigest()==digest,name
 stats=Statistics();final=[];rolling=[]
 for path in sorted(paths):
  with gzip.open(path,'rt',encoding='utf-8') as h:r=json.load(h)
  stats.add(r)
  if r['comparison']['segment']=='final-holdout':final.append(r)
  elif r['comparison']['segment']=='rolling-holdout':rolling.append(r)
 result=stats.result();result['input_hashes_verified']=True;save(out/'statistics.json',result);reports(out,result)
 save(out/'final-policy.json',incumbent);save(out/'final-selected-statistics.json',selected_statistics(final))
 controls={key:[r for r in final if r['comparison']['second']==model['checkpoint']] for key,model in [('legacy',legacy),('heavy',heavy)]}
 evaluation={key:dict(games=len(rs),selected=group_metrics(rs),control=group_metrics(rs,False),selected_decks=deck_metrics(rs),control_decks=deck_metrics(rs,False),paired=paired_interval(rs)) for key,rs in controls.items()}
 enough=all(v['games']>=180 for v in evaluation.values())
 promote=incumbent['kind']=='thin' and enough and len(issues)<=20 and all(v['paired']['low'] is not None and v['paired']['low']>0 for v in evaluation.values())
 deployed=incumbent if promote else legacy
 summary=dict(attempted=2000,completed=len(paths),skipped=len(issues),training_rounds=16,blind_rounds=4,
  batches=batches,accepted_steps=sum(b['accepted'] is True for b in batches),candidate=incumbent,evaluation=evaluation,
  final_test_games=len(final),rolling_test_games=len(rolling),promoted=promote,deployed=deployed['checkpoint'],hashes_verified=True,
  method='Construction-only bounded3% search; fixed battle policy, fresh validation seeds, unopened400 final controls; confidence gate against both baselines')
 save(out/'training.json',summary);save(out/'deployment.json',dict(promoted=promote,deployed=deployed['checkpoint']))
 save(Path(__file__).parent/'rules/ai-policy.json',deployed)
 print('THIN_TRAINING_COMPLETE '+str(out/'training.json'),flush=True)

if __name__=='__main__':main()
