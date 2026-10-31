"""500-room broad restart toward ~15 cards, versus the last small-deck model.

Battle policy is identical and frozen on both sides, isolating construction.
Three learning batches then two frozen 100-room test batches. No default change.
"""
import argparse,gzip,hashlib,json,random,time
from copy import deepcopy
from pathlib import Path
from concurrent.futures import ProcessPoolExecutor,as_completed
from .catalog import ROOT,CARDS,copies,export
from .free_train import win_interval,describe,training_rows,correlation
from .thin_train import safe_room,paired_interval
from .train import group_metrics
from .analytics import Statistics
from .cohort import selected_statistics
from .quick_test import save,reports

def restart(small):
 model=deepcopy(small);model['checkpoint']='medium-restart'
 model['construction'].update(dilution=.84,buy_margin=.2,delete_margin=2.4,starter_bias=.6,first_layer=3.1,core_weight=2.4)
 model['breadth']=dict(target=15.,fill_bonus=4.5,retention_bonus=4.,paid_delete_penalty=6.,quality_floor=.7)
 return model

def propose(best,initial,step,direction):
 c=deepcopy(best);c['checkpoint']=f'medium-candidate-{step:02d}';rng=random.Random(14000+step)
 for key,value in c['construction'].items():
  signal=direction.get('c.'+key,rng.choice((-1,1)))
  delta=.15*(.6*signal+.4*rng.choice((-1,1)))
  anchor=initial['construction'][key]
  c['construction'][key]=round(max(.65*anchor,min(1.5*anchor,value*(1+delta))),6)
 for key in ('fill_bonus','retention_bonus','paid_delete_penalty','quality_floor'):
  value=c['breadth'][key];anchor=initial['breadth'][key]
  c['breadth'][key]=round(max(.75*anchor,min(1.25*anchor,value*(1+.12*rng.choice((-1,1))))),6)
 return c

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--out',required=True);ap.add_argument('--workers',type=int,default=12);args=ap.parse_args()
 out=Path(args.out).resolve();out.mkdir(parents=True,exist_ok=False);export(out)
 small=json.loads((ROOT/'research/v1.2-free-growth-1000-20261029/final-policy.json').read_text(encoding='utf-8'))
 small['checkpoint']='fixed-small-explore-07'
 initial=restart(small);best=deepcopy(initial)
 save(out/'baselines.json',dict(small=small,initial=initial))
 files=list(Path(__file__).parent.rglob('*.py'))+list((Path(__file__).parent/'rules').glob('*.json'))+list((ROOT/'docs/cards-v1.2').glob('*.md'))+list((ROOT/'docs/cards-v1.2').glob('*.json'))
 hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
 save(out/'inputs.json',dict(cards=len(CARDS),stock=sum(copies(c) for c in CARDS.values()),attempted=500,seeds=[293000,293499],
  protocol='3*(60train20validation20sealed)+2*100frozen;200final vs fixed-small;construction only;win-only;15 soft target',sha256=hashes))
 paths=[];issues=[];batches=[];direction={};started=time.monotonic()
 with ProcessPoolExecutor(max_workers=args.workers) as pool:
  def collect(jobs):
   results=[]
   for f in as_completed([pool.submit(safe_room,*job) for job in jobs]):
    result=f.result()
    if result['ok']:results.append(result['room'])
    else:issues.append(result)
   return sorted(results,key=lambda r:r['seed'])
  for step in range(1,6):
   folder=out/'batches'/f'{step:02d}';folder.mkdir(parents=True);seed0=293000+(step-1)*100
   if step<=3:
    candidate=propose(best,initial,step,direction);save(folder/'candidate.json',candidate);save(folder/'incumbent.json',best)
    rooms=collect([(seed0+i,candidate,best,str(folder),'train' if i<60 else 'validation') for i in range(80)])
    train=[r for r in rooms if r['comparison']['segment']=='train'];val=[r for r in rooms if r['comparison']['segment']=='validation']
    td,vd=win_interval(train),win_interval(val)
    accepted=len(train)==60 and len(val)==20 and td['mean']>=0 and vd['mean']>=.005
    if accepted:best=deepcopy(candidate);best['checkpoint']=f'medium-accepted-{step:02d}'
    rows=training_rows(train);direction={'c.'+k:correlation(rows,'c.'+k) for k in initial['construction']}
    selection=dict(round=step,accepted=accepted,selected=best['checkpoint'],train=td,validation=vd,train_decks=describe(train),gradient=direction)
    save(folder/'selection.json',selection)
    rooms+=collect([(seed0+i,best,small,str(folder),'rolling-holdout') for i in range(80,100)])
   else:
    selection=dict(round=step,accepted=None,selected=best['checkpoint'],frozen_final_test=True)
    rooms=collect([(seed0+i,best,small,str(folder),'final-holdout') for i in range(100)])
    save(folder/'selection.json',selection)
   assert best['weights']==small['weights'] and best['element_weights']==small['element_weights']
   save(folder/'checkpoint.json',best);batches.append(selection)
   paths.extend(folder/f"room-{r['seed']}.json.gz" for r in rooms);save(out/'issues.json',issues)
   progress=dict(round=step,attempted=100*step,completed=len(paths),skipped=len(issues),checkpoint=best['checkpoint'],accepted=selection['accepted'],elapsed=round(time.monotonic()-started,1))
   save(out/'progress.json',progress);print(json.dumps(progress),flush=True)
 for name,digest in hashes.items():assert hashlib.sha256((ROOT/name).read_bytes()).hexdigest()==digest,name
 stats=Statistics();final=[]
 for p in paths:
  with gzip.open(p,'rt',encoding='utf-8') as h:r=json.load(h)
  stats.add(r)
  if r['comparison']['segment']=='final-holdout':final.append(r)
 result=stats.result();result['input_hashes_verified']=True;save(out/'statistics.json',result);reports(out,result)
 evaluation=dict(games=len(final),selected=group_metrics(final),control=group_metrics(final,False),paired_win=win_interval(final),paired_score=paired_interval(final),
  selected_decks=describe(final),control_decks=describe(final,False))
 summary=dict(attempted=500,completed=len(paths),battles=stats.battles,skipped=len(issues),accepted_steps=sum(b['accepted'] is True for b in batches),
  candidate=best,batches=batches,evaluation=evaluation,final_test_games=len(final),input_hashes_verified=True,
  battle_weights_identical=True,train_games=180,validation_games=60,rolling_holdout_games=60,final_holdout_games=200,
  default_changed=False,significantly_better=len(final)==200 and not issues and evaluation['paired_win']['low']>0)
 save(out/'training.json',summary);save(out/'final-policy.json',best);save(out/'final-selected-statistics.json',selected_statistics(final))
 print('MEDIUM_TRAINING_COMPLETE '+str(out/'training.json'),flush=True)

if __name__=='__main__':main()
