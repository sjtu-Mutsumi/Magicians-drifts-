"""Ten 100-room budgets: 600 train, 200 validation, 200 frozen final.

Equal champion / battle-win / normalized rank-score reward. No size target,
role preference, tempo reward, or held-out feedback. Historical priors are the
starting point, not a constraint on the learned mechanism weights.
"""
import argparse,gzip,hashlib,json,math,random,time
from copy import deepcopy
from pathlib import Path
from collections import defaultdict,Counter
from concurrent.futures import ProcessPoolExecutor,as_completed
from .catalog import ROOT,CARDS,copies,export
from .free_train import training_rows,correlation,describe,win_interval
from .thin_train import safe_room,paired_interval,deck_metrics
from .train import group_metrics
from .cohort import selected_statistics
from .analytics import Statistics
from .quick_test import save,reports

def interval(xs):
 mean=sum(xs)/max(1,len(xs));se=math.sqrt(sum((v-mean)**2 for v in xs)/max(1,len(xs)-1)/max(1,len(xs)))
 return dict(games=len(xs),mean=mean,low=mean-1.96*se,high=mean+1.96*se)

def initialize(source):
 cfg=deepcopy(source)
 cfg.pop('breadth',None);cfg.pop('role_preferences',None)
 cfg['checkpoint']='outcome-initial-neutral'
 return cfg

def objective(rooms):
 cells=[];champ=[];wins=[];scores=[]
 for r in rooms:
  a,b=group_metrics([r]),group_metrics([r],False)
  ch=a['championship_rate']-b['championship_rate'];w=a['win_rate']-b['win_rate'];s=(a['mean_score']-b['mean_score'])/6
  champ.append(ch);wins.append(w);scores.append(s);cells.append((ch+w+s)/3)
 return dict(composite=interval(cells),championship=interval(champ),win=interval(wins),normalized_score=interval(scores))

def stages(rooms):
 result={}
 for name,low,high in (('1-4',1,4),('5-8',5,8),('9-12',9,12),('13+',13,100)):
  filtered=[dict(r,rounds=[rd for rd in r['rounds'] if low<=rd['round']<=high]) for r in rooms]
  filtered=[r for r in filtered if r['rounds']]
  result[name]=dict(selected=group_metrics(filtered),control=group_metrics(filtered,False),paired_win=win_interval(filtered))
 return result

def reward_rows(rooms):
 # Same public mechanism feature vectors, but all three outcomes drive updates.
 return [(p,x,((p['rank']==1)+win+(p['score']+3)/6)/3) for p,x,win in training_rows(rooms)]

def propose(best,initial,step,direction,seed):
 cfg=deepcopy(best);cfg['checkpoint']=f'outcome-candidate-{step:02d}'
 rng=random.Random(seed+9871+step)
 for section,prefix,rate in (('weights','w.',.07),('construction','c.',.10)):
  for k,v in cfg[section].items():
   anchor=initial[section][k];signal=direction.get(prefix+k,0)
   change=rate*(.6*signal+.4*rng.choice((-1,1)))
   cfg[section][k]=round(max(.65*anchor,min(1.5*anchor,v*(1+change))),6)
 # Element-specific priors may move slightly; no size/type quotas are introduced.
 for e,weights in cfg['element_weights'].items():
  for k,v in weights.items():
   anchor=initial['element_weights'][e][k];signal=direction.get(e+'.'+k,0)
   cfg['element_weights'][e][k]=round(max(.9*anchor,min(1.1*anchor,v*(1+.025*signal))),6)
 return cfg

def evaluation(rooms):
 return dict(games=len(rooms),selected=group_metrics(rooms),control=group_metrics(rooms,False),
  objective=objective(rooms),paired_win=win_interval(rooms),paired_score=paired_interval(rooms),
  stages=stages(rooms),selected_decks=describe(rooms),control_decks=describe(rooms,False),
  selected_actions=deck_metrics(rooms),control_actions=deck_metrics(rooms,False))

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--out',required=True);ap.add_argument('--workers',type=int,default=12)
 ap.add_argument('--seed',type=int,default=296000);ap.add_argument('--source',default='research/v1.2-dual-growthv7-1000-20261029');args=ap.parse_args()
 out=Path(args.out).resolve();out.mkdir(parents=True,exist_ok=False);export(out)
 source=Path(args.source).resolve();small=json.loads((source/'small-final-policy.json').read_text(encoding='utf-8'))
 medium=json.loads((source/'medium-final-policy.json').read_text(encoding='utf-8'));legacy=dict(kind='legacy',checkpoint='fixed-legacy')
 initial=initialize(small);best=deepcopy(initial);anchors=dict(original_small=small,original_medium=medium,legacy=legacy)
 save(out/'initial-policy.json',initial);save(out/'anchors.json',anchors)
 files=list(Path(__file__).parent.rglob('*.py'))+list((Path(__file__).parent/'rules').glob('*.json'))+list((ROOT/'docs/cards-v1.2').glob('*.md'))+list((ROOT/'docs/cards-v1.2').glob('*.json'))
 hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
 save(out/'inputs.json',dict(seeds=[args.seed,args.seed+999],cards=len(CARDS),stock=sum(copies(c) for c in CARDS.values()),
  source=str(source.relative_to(ROOT)),source_hashes={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in source.glob('*-final-policy.json')},
  protocol='10*(60train+20validation+20reserved); final200 only after step10 freeze; equal champion/win/normalized-score; no size/type reward',
  selection='train composite >=0; validation composite>=.005; no validation component<-.04; no phase win delta<-.08 (>=12 rooms)',
  final_gate='both fixed anchors: room composite 95% lower>0 and no phase paired-win mean<-.03; no automatic default replacement',sha256=hashes))
 paths=[];reserved=[];issues=[];batches=[];direction={};started=time.monotonic()
 with ProcessPoolExecutor(max_workers=args.workers) as pool:
  def collect(jobs):
   rooms=[]
   for future in as_completed([pool.submit(safe_room,*job) for job in jobs]):
    result=future.result()
    if result['ok']:rooms.append(result['room'])
    else:issues.append(result)
   return sorted(rooms,key=lambda r:r['seed'])
  for step in range(1,11):
   folder=out/'batches'/f'{step:02d}';folder.mkdir(parents=True);start=args.seed+(step-1)*100
   candidate=propose(best,initial,step,direction,args.seed)
   save(folder/'candidate.json',candidate);save(folder/'incumbent.json',best)
   opponents=[best,small,medium,legacy]
   rooms=collect([(start+i,candidate,opponents[i%4],str(folder),'train' if i<60 else 'validation') for i in range(80)])
   train=[r for r in rooms if r['comparison']['segment']=='train'];val=[r for r in rooms if r['comparison']['segment']=='validation']
   td,vd=objective(train),objective(val);phase=stages(val)
   accept=(len(train)==60 and len(val)==20 and td['composite']['mean']>=0 and vd['composite']['mean']>=.005
    and all(vd[k]['mean']>=-.04 for k in ('championship','win','normalized_score'))
    and all(v['paired_win']['games']<12 or v['paired_win']['mean']>=-.08 for v in phase.values()))
   if accept:best=deepcopy(candidate);best['checkpoint']=f'outcome-accepted-{step:02d}'
   rows=reward_rows(train)
   direction={prefix+k:correlation(rows,prefix+k) for section,prefix in (('weights','w.'),('construction','c.')) for k in initial[section]}
   direction.update({e+'.'+k:correlation(rows,'w.'+k,e) for e in initial['element_weights'] for k in initial['element_weights'][e]})
   choice=dict(round=step,accepted=accept,selected=best['checkpoint'],train=td,validation=vd,validation_stages=phase,gradient=direction)
   save(folder/'selection.json',choice);save(folder/'checkpoint.json',best);batches.append(choice)
   paths.extend(folder/f"room-{r['seed']}.json.gz" for r in rooms);reserved.extend(range(start+80,start+100))
   save(out/'issues.json',issues);save(out/'progress.json',dict(round=step,completed=len(paths),reserved=len(reserved),skipped=len(issues),checkpoint=best['checkpoint'],accepted=accept,elapsed=round(time.monotonic()-started,1)))
   print(json.dumps(dict(round=step,completed=len(paths),accepted=accept,checkpoint=best['checkpoint'],elapsed=round(time.monotonic()-started,1))),flush=True)
  save(out/'frozen-policy.json',best);folder=out/'final';folder.mkdir()
  final=collect([(seed,best,small if i%2==0 else medium,str(folder),'final-holdout') for i,seed in enumerate(reserved)])
  paths.extend(folder/f"room-{r['seed']}.json.gz" for r in final)
 for name,digest in hashes.items():assert hashlib.sha256((ROOT/name).read_bytes()).hexdigest()==digest,name
 stats=Statistics()
 for path in paths:
  with gzip.open(path,'rt',encoding='utf-8') as h:stats.add(json.load(h))
 result=stats.result();result['input_hashes_verified']=True;save(out/'statistics.json',result);reports(out,result)
 ev={key:evaluation([r for r in final if r['comparison']['second']==anchors[key]['checkpoint']]) for key in ('original_small','original_medium')}
 gate=not issues and all(v['games']==100 and v['objective']['composite']['low']>0 and all(s['paired_win']['mean']>=-.03 for s in v['stages'].values()) for v in ev.values())
 save(out/'training.json',dict(completed=len(paths),attempted=1000,battles=stats.battles,skipped=len(issues),batches=batches,
  accepted_rounds=[b['round'] for b in batches if b['accepted']],evaluation=ev,final_test_games=len(final),
  overall_final=evaluation(final),robust_improvement_gate=gate,input_hashes_verified=True,default_changed=False))
 save(out/'final-policy.json',best);save(out/'final-selected-statistics.json',selected_statistics(final));save(out/'issues.json',issues)
 for key in ev:save(out/f'{key}-final-selected-statistics.json',selected_statistics([r for r in final if r['comparison']['second']==anchors[key]['checkpoint']]))
 save(out/'progress.json',dict(completed=len(paths),battles=stats.battles,skipped=len(issues),finished=True,elapsed=round(time.monotonic()-started,1)))
 print('OUTCOME_TRAINING_COMPLETE '+str(out/'training.json'),flush=True)

if __name__=='__main__':main()
