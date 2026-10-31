"""Five 300-episode batches, two independently trained starts per element.
900 train / 300 validation / 300 final episodes; final compares frozen large
and small, with branch choice made on validation before opening that test.
"""
import argparse,gzip,hashlib,json,time
from pathlib import Path
from copy import deepcopy
from concurrent.futures import ProcessPoolExecutor,as_completed
from .catalog import ROOT,CARDS,ELEMENTS,export,copies
from .element_paired_train import worker,paired,score
from .outcome_train import propose,reward_rows,interval
from .free_train import describe,correlation
from .train import group_metrics
from .cohort import selected_statistics
from .analytics import Statistics
from .quick_test import save

def initialize(e,f):
 src=ROOT/'research/v1.2-element-paired-3000-20261029'
 p=src/f'{e}-final-policy.json' if f=='large' else src/'initial-policies.json'
 c=json.loads(p.read_text(encoding='utf-8'));c=deepcopy(c if f=='large' else c[e])
 c['checkpoint']=f'population-{e}-{f}-initial';c['size_exploration']['bonus']=3. if f=='large' else 0.
 if f=='large':
  c['construction']['dilution']*=.92;c['construction']['delete_margin']*=1.15
 else:c['construction']['dilution']*=1.03
 return c

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--out',required=True);ap.add_argument('--seed',type=int,default=328000);ap.add_argument('--workers',type=int,default=12);a=ap.parse_args()
 out=Path(a.out).resolve();out.mkdir(parents=True,exist_ok=False);export(out)
 families=('large','small');initial={f:{e:initialize(e,f) for e in ELEMENTS} for f in families};best=deepcopy(initial)
 save(out/'initial-policies.json',initial)
 files=list(Path(__file__).parent.rglob('*.py'))+list((Path(__file__).parent/'rules').glob('*.json'))+list((ROOT/'releases/v1.2').glob('*.json'))
 hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
 save(out/'inputs.json',dict(episodes=1500,pairs=750,seeds=[a.seed,a.seed+749],cards=len(CARDS),stock=sum(copies(c) for c in CARDS.values()),sha256=hashes,
  protocol='5batches; each element per batch large9train3val3reserved pairs + small same; 900train300val300test episodes; final30large-small pairs per element',
  objective='equal championship,battle win,(mean score+3)/6; no size reward; independently update construction and combat; initial sizes soft only',
  selection='train paired mean>=0; validation mean>=.002; no mode mean<-.04; branch chosen by latest3 retained-model validation focal scores before test',
  source='research/v1.2-element-paired-3000-20261029',source_hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in (ROOT/'research/v1.2-element-paired-3000-20261029').glob('*.json') if p.name.endswith('policy.json') or p.name=='initial-policies.json'}))
 batches={f:{e:[] for e in ELEMENTS} for f in families};directions={f:{e:{} for e in ELEMENTS} for f in families};reserved=[];paths=[];issues=[];started=time.monotonic()
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
  for step in range(1,6):
   snapshot=deepcopy(best);jobs=[];candidates={};folders={}
   for ei,e in enumerate(ELEMENTS):
    for fi,f in enumerate(families):
     c=propose(best[f][e],initial[f][e],step,directions[f][e],a.seed+ei*811+fi*99)
     c['checkpoint']=f'population-{e}-{f}-candidate-{step:02d}';candidates[(f,e)]=c
     folder=out/f/e/f'{step:02d}';folder.mkdir(parents=True);folders[(f,e)]=folder
     save(folder/'candidate.json',c);save(folder/'incumbent.json',best[f][e])
     for i in range(15):
      seed=a.seed+(step-1)*150+ei*30+fi*15+i;mode='internal' if (i+step+fi)%2==0 else 'external';ri=i%3
      if i>=12:reserved.append((seed,e,mode,ri));continue
      opponents=snapshot[families[(seed//2)%2]];seg='train' if i<9 else 'validation'
      for side,cfg in (('candidate',c),('control',best[f][e])):
       dest=folder/side;dest.mkdir(exist_ok=True);jobs.append((seed,e,mode,ri,cfg,opponents,str(dest),seg,side))
   rs=collect(jobs)
   for f in families:
    for e in ELEMENTS:
     c=candidates[(f,e)];own=[r for r in rs if r['comparison']['element']==e and (r['comparison']['first']==c['checkpoint'] or r['comparison']['first']==best[f][e]['checkpoint'])]
     train=[r for r in own if r['comparison']['segment']=='train'];val=[r for r in own if r['comparison']['segment']=='validation']
     td,vd=paired(train),paired(val);modes={m:paired([r for r in val if r['comparison']['mode']==m]) for m in ('internal','external')}
     accept=len(train)==18 and len(val)==6 and td['mean']>=0 and vd['mean']>=.002 and all(v['mean']>=-.04 for v in modes.values())
     if accept:best[f][e]=deepcopy(c);best[f][e]['checkpoint']=f'population-{e}-{f}-accepted-{step:02d}'
     rows=[]
     for r in train:
      rows.extend((p,x,y) for p,x,y in reward_rows([r]) if p['pid'] in r['comparison']['first_seats'])
     # 18 observations are deliberately shrunk by correlation's minimum30 gate;
     # accumulate training observations across rounds, never validation/test.
     save(folders[(f,e)]/'training-rows.json',rows)
     accumulated=[]
     for k in range(1,step+1):accumulated+=json.loads((out/f/e/f'{k:02d}'/'training-rows.json').read_text(encoding='utf-8'))
     directions[f][e]={prefix+k:correlation(accumulated,prefix+k) for section,prefix in (('weights','w.'),('construction','c.')) for k in initial[f][e][section]}
     retained_side='candidate' if accept else 'control';retained=[r for r in val if r['pair_side']==retained_side]
     choice=dict(round=step,accepted=accept,selected=best[f][e]['checkpoint'],train=td,validation=vd,modes=modes,retained_validation_score=sum(score(r) for r in retained)/max(1,len(retained)),
      decks=describe([r for r in train if r['pair_side']=='candidate']))
     batches[f][e].append(choice);save(folders[(f,e)]/'selection.json',choice);save(folders[(f,e)]/'checkpoint.json',best[f][e])
   progress=dict(round=step,episodes=len(paths),skipped=len(issues),accepted={f:{e:[b['round'] for b in bs if b['accepted']] for e,bs in es.items()} for f,es in batches.items()},elapsed=round(time.monotonic()-started,1))
   save(out/'progress.json',progress);save(out/'issues.json',issues);print(json.dumps(progress),flush=True)
  choices={e:max(families,key=lambda f:sum(b['retained_validation_score'] for b in batches[f][e][-3:])/3) for e in ELEMENTS}
  save(out/'pretest-selection.json',choices);save(out/'frozen-policies.json',best);jobs=[]
  for seed,e,mode,ri in reserved:
   opponents=initial[families[(seed//2)%2]]
   for side,f in (('candidate','large'),('control','small')):
    dest=out/'final'/side;dest.mkdir(parents=True,exist_ok=True);jobs.append((seed,e,mode,ri,best[f][e],opponents,str(dest),'final-holdout',side))
  final=collect(jobs)
 for name,h in hashes.items():assert hashlib.sha256((ROOT/name).read_bytes()).hexdigest()==h,name
 stats=Statistics()
 for path in paths:
  with gzip.open(path,'rt',encoding='utf-8') as f:stats.add(json.load(f))
 save(out/'statistics.json',stats.result());evaluation={}
 for e in ELEMENTS:
  rs=[r for r in final if r['comparison']['element']==e]
  evaluation[e]=dict(paired_large_minus_small=paired(rs),modes={m:paired([r for r in rs if r['comparison']['mode']==m]) for m in ('internal','external')},families={})
  for f,side in (('large','candidate'),('small','control')):
   own=[r for r in rs if r['pair_side']==side]
   evaluation[e]['families'][f]=dict(metrics=group_metrics(own),decks=describe(own));save(out/f'{e}-{f}-final-policy.json',best[f][e]);save(out/f'{e}-{f}-statistics.json',selected_statistics(own))
  save(out/f'{e}-selected-policy.json',best[choices[e]][e])
 save(out/'training.json',dict(episodes=len(paths),pairs=len(paths)//2,battles=stats.battles,skipped=len(issues),batches=batches,evaluation=evaluation,choices=choices,input_hashes_verified=True,default_changed=False))
 save(out/'progress.json',dict(finished=True,episodes=len(paths),battles=stats.battles,skipped=len(issues),elapsed=round(time.monotonic()-started,1)));save(out/'issues.json',issues)
 print('POPULATION_TRAINING_COMPLETE',flush=True)

if __name__=='__main__':main()
