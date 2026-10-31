"""1000 episodes:480train192validation328test; six independent styles."""
import argparse,gzip,hashlib,json,random,time,traceback
from copy import deepcopy
from pathlib import Path
from concurrent.futures import ProcessPoolExecutor,as_completed
from .catalog import ROOT,CARDS,ELEMENTS,ROUTES,copies,export
from .outcome_train import propose,reward_rows,interval
from .element_paired_train import paired,score
from .free_train import correlation,describe
from .train import group_metrics
from .cohort import selected_statistics
from .analytics import Statistics
from .quick_test import save

STYLES=(*ELEMENTS,'flexible')
LABELS=dict(fire='火系',water='水系',earth='土系',wind='风系',poison='毒系',flexible='灵活多变')

def initial_models():
 from .six_styles import IDS,load_policy
 models={e:deepcopy(load_policy(e)) for e in IDS}
 for e,c in models.items():c['checkpoint']=f'six-{e}-initial'
 return models

def worker(job):
 seed,e,mode,ri,first,bank,folder,seg,side=job
 try:
  from .tournament import simulate
  rng=random.Random(seed^578471);seat=rng.randrange(4);configs=[];other_styles=list(STYLES);rng.shuffle(other_styles)
  for i in range(4):
   oe=e if mode=='internal' else other_styles.pop()
   cfg=deepcopy(first if i==seat else bank[oe]);style=e if i==seat else oe
   if style in ELEMENTS:cfg['element_intent']=style;cfg['route_intent']=ROUTES[style][(ri+(0 if i==seat else i+1))%3]
   else:
    cfg.pop('element_intent',None);cfg.pop('route_intent',None)
   configs.append(cfg)
  r=simulate(seed,policy_by_seat=configs)
  r['comparison']=dict(first=first['checkpoint'],second='six-style-pool',first_seats=[seat],segment=seg,element=e,mode=mode,
   route_intent=ROUTES[e][ri] if e in ELEMENTS else '',opponents=[c['checkpoint'] for i,c in enumerate(configs) if i!=seat])
  r['pair_side']=side
  with gzip.open(Path(folder)/f'room-{seed}.json.gz','wt',encoding='utf-8') as f:json.dump(r,f,ensure_ascii=False)
  return dict(ok=True,room=r)
 except Exception as exc:return dict(ok=False,seed=seed,style=e,side=side,error=repr(exc),traceback=traceback.format_exc())

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--out',required=True);ap.add_argument('--seed',type=int,default=330000);ap.add_argument('--workers',type=int,default=12);a=ap.parse_args()
 out=Path(a.out).resolve();out.mkdir(parents=True,exist_ok=False);export(out)
 initial=initial_models();best=deepcopy(initial);save(out/'initial-policies.json',initial)
 files=list(Path(__file__).parent.rglob('*.py'))+list((Path(__file__).parent/'rules').glob('*.json'))+list((ROOT/'releases/v1.2').glob('*.json'))
 hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
 save(out/'inputs.json',dict(episodes=1000,pairs=500,seeds=[a.seed,a.seed+499],cards=len(CARDS),stock=sum(copies(c) for c in CARDS.values()),sha256=hashes,
  protocol='4rounds each6styles*(10train4val pairs)=672episodes; final164pairs328episodes; earth/flexible28pairs, other27; no heldout reselection',
  objective='equal championship/battlewin/(mean_score+3)/6, no size reward; paired train>=0,val>=.002,no mode<-.04',
  source_hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in [*(ROOT/'research/v1.2-population-1500-20261029').glob('*-selected-policy.json'),ROOT/'research/v1.2-outcome-1000-20261029/final-policy.json']}))
 batches={e:[] for e in STYLES};rows={e:[] for e in STYLES};direction={e:{} for e in STYLES};paths=[];issues=[];started=time.monotonic()
 with ProcessPoolExecutor(max_workers=a.workers) as pool:
  def collect(jobs):
   rs=[]
   for fu in as_completed([pool.submit(worker,j) for j in jobs]):
    v=fu.result()
    if v['ok']:rs.append(v['room'])
    else:issues.append(v)
   for j in jobs:
    p=Path(j[6])/f'room-{j[0]}.json.gz'
    if p.exists():paths.append(p)
   return rs
  for step in range(1,5):
   snapshot=deepcopy(best);jobs=[];candidates={};folders={}
   for ei,e in enumerate(STYLES):
    c=propose(best[e],initial[e],step,direction[e],a.seed+ei*771);c['checkpoint']=f'six-{e}-candidate-{step:02d}';candidates[e]=c
    folder=out/e/f'{step:02d}';folder.mkdir(parents=True);folders[e]=folder
    save(folder/'candidate.json',c);save(folder/'incumbent.json',best[e])
    for i in range(14):
     seed=a.seed+(step-1)*84+ei*14+i;mode='internal' if (i+step)%2==0 else 'external';ri=i%3;seg='train' if i<10 else 'validation'
     for side,cfg in (('candidate',c),('control',best[e])):
      dest=folder/side;dest.mkdir(exist_ok=True);jobs.append((seed,e,mode,ri,cfg,snapshot,str(dest),seg,side))
   rooms=collect(jobs)
   for e in STYLES:
    own=[r for r in rooms if r['comparison']['element']==e];tr=[r for r in own if r['comparison']['segment']=='train'];va=[r for r in own if r['comparison']['segment']=='validation']
    td,vd=paired(tr),paired(va);modes={m:paired([r for r in va if r['comparison']['mode']==m]) for m in ('internal','external')}
    accept=len(tr)==20 and len(va)==8 and td['mean']>=0 and vd['mean']>=.002 and all(v['mean']>=-.04 for v in modes.values())
    if accept:best[e]=deepcopy(candidates[e]);best[e]['checkpoint']=f'six-{e}-accepted-{step:02d}'
    for r in tr:rows[e].extend((p,x,y) for p,x,y in reward_rows([r]) if p['pid'] in r['comparison']['first_seats'])
    direction[e]={prefix+k:correlation(rows[e],prefix+k) for section,prefix in (('weights','w.'),('construction','c.')) for k in initial[e][section]}
    choice=dict(round=step,accepted=accept,selected=best[e]['checkpoint'],train=td,validation=vd,modes=modes)
    batches[e].append(choice);save(folders[e]/'selection.json',choice);save(folders[e]/'checkpoint.json',best[e])
   save(out/'issues.json',issues);progress=dict(round=step,episodes=len(paths),skipped=len(issues),accepted={e:[b['round'] for b in bs if b['accepted']] for e,bs in batches.items()},elapsed=round(time.monotonic()-started,1))
   save(out/'progress.json',progress);print(json.dumps(progress),flush=True)
  save(out/'frozen-policies.json',best);jobs=[];seed=a.seed+336
  for e in STYLES:
   for i in range(28 if e in ('earth','flexible') else 27):
    mode='internal' if i%2==0 else 'external';ri=i%3
    for side,cfg in (('candidate',best[e]),('control',initial[e])):
     dest=out/'final'/side;dest.mkdir(parents=True,exist_ok=True);jobs.append((seed,e,mode,ri,cfg,initial,str(dest),'final-holdout',side))
    seed+=1
  assert seed==a.seed+500
  final=collect(jobs)
 for name,h in hashes.items():assert hashlib.sha256((ROOT/name).read_bytes()).hexdigest()==h,name
 stats=Statistics()
 for path in paths:
  with gzip.open(path,'rt',encoding='utf-8') as f:stats.add(json.load(f))
 save(out/'statistics.json',stats.result());evaluation={}
 for e in STYLES:
  own=[r for r in final if r['comparison']['element']==e];cs=[r for r in own if r['pair_side']=='candidate'];os=[r for r in own if r['pair_side']=='control']
  evaluation[e]=dict(paired=paired(own),candidate=group_metrics(cs),control=group_metrics(os),decks=describe(cs),modes={m:paired([r for r in own if r['comparison']['mode']==m]) for m in ('internal','external')})
  save(out/f'{e}-final-policy.json',best[e]);save(out/f'{e}-statistics.json',selected_statistics(cs))
 save(out/'training.json',dict(episodes=len(paths),battles=stats.battles,skipped=len(issues),batches=batches,evaluation=evaluation,input_hashes_verified=True))
 save(out/'progress.json',dict(finished=True,episodes=len(paths),battles=stats.battles,skipped=len(issues),elapsed=round(time.monotonic()-started,1)));save(out/'issues.json',issues)
 print('SIX_STYLE_TRAINING_COMPLETE',flush=True)

if __name__=='__main__':main()
