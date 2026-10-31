"""3000 complete episodes, 1500 paired seeds, ten learning rounds.
Same-element candidate/control face identical seeded opponent pools. Heldout
results are opened only after all models freeze. Frozen game code is untouched.
"""
import argparse,gzip,json,hashlib,time
from pathlib import Path
from copy import deepcopy
from concurrent.futures import ProcessPoolExecutor,as_completed
from .catalog import ROOT,ELEMENTS,ROUTES,CARDS,copies,export
from .element_train import room,SIZES
from .outcome_train import interval,propose,reward_rows,stages
from .free_train import describe,correlation
from .train import group_metrics
from .cohort import selected_statistics
from .analytics import Statistics
from .quick_test import save

def score(r):
 v=group_metrics([r]);return (v['championship_rate']+v['win_rate']+(v['mean_score']+3)/6)/3

def paired(rs):
 groups={}
 for r in rs:groups.setdefault(r['seed'],{})[r['pair_side']]=r
 values=[]
 for seed,g in groups.items():
  assert set(g)=={'candidate','control'},seed
  values.append(score(g['candidate'])-score(g['control']))
 return interval(values)

def worker(job):
 seed,e,mode,ri,cfg,opponents,folder,segment,side=job
 result=room(seed,e,mode,ri,cfg,opponents,folder,segment)
 if result['ok']:
  r=result['room'];r['pair_side']=side
  with gzip.open(Path(folder)/f'room-{seed}.json.gz','wt',encoding='utf-8') as f:json.dump(r,f,ensure_ascii=False)
 return result

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--out',required=True);ap.add_argument('--seed',type=int,default=326000);ap.add_argument('--workers',type=int,default=12);a=ap.parse_args()
 out=Path(a.out).resolve();out.mkdir(parents=True,exist_ok=False);export(out)
 src=ROOT/'research/v1.2-element-styles-1500-20261029'
 initial={e:json.loads((src/f'{e}-final-policy.json').read_text(encoding='utf-8')) for e in ELEMENTS}
 for e,c in initial.items():c['checkpoint']=f'paired-{e}-initial';c['size_exploration']=dict(bonus=0.,ceiling=SIZES[e]+4)
 best=deepcopy(initial);save(out/'initial-policies.json',initial)
 files=list(Path(__file__).parent.rglob('*.py'))+list((Path(__file__).parent/'rules').glob('*.json'))+list((ROOT/'releases/v1.2').glob('*.json'))
 hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
 save(out/'inputs.json',dict(cards=len(CARDS),stock=sum(copies(c) for c in CARDS.values()),source=str(src.relative_to(ROOT)),
  source_hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in src.glob('*-final-policy.json')},sha256=hashes,
  seed_range=[a.seed,a.seed+1499],episodes=3000,pairs=1500,
  protocol='each element10*(18paired-train+6paired-validation+6reserved-pairs);1800train600validation600final episodes; inner/outer50%;route intentions balanced',
  selection='paired train outcome delta + bounded size reward>=0; paired validation mean>=.002; each mode validation mean>=-.025; no heldout feedback',
  size_bonus='0..6 linear over10 rounds, bounded training objective reward<=.02; validation/test outcome-only; no hard length',default_promotion=False))
 batches={e:[] for e in ELEMENTS};directions={e:{} for e in ELEMENTS};paths=[];issues=[];reserved=[];started=time.monotonic()
 with ProcessPoolExecutor(max_workers=a.workers) as pool:
  def collect(jobs):
   rs=[]
   for f in as_completed([pool.submit(worker,j) for j in jobs]):
    v=f.result()
    if v['ok']:rs.append(v['room'])
    else:issues.append(v)
   return sorted(rs,key=lambda r:(r['seed'],r['pair_side']))
  for step in range(1,11):
   snapshot=deepcopy(best);candidates={};jobs=[];folders={}
   for ei,e in enumerate(ELEMENTS):
    c=propose(best[e],initial[e],step,directions[e],a.seed+ei*811)
    c['checkpoint']=f'paired-{e}-candidate-{step:02d}';c['size_exploration']['bonus']=round(6*(step-1)/9,4);candidates[e]=c
    folder=out/e/f'{step:02d}';folder.mkdir(parents=True);folders[e]=folder
    save(folder/'candidate.json',c);save(folder/'incumbent.json',best[e]);save(folder/'opponents.json',snapshot)
    for i in range(30):
     seed=a.seed+(step-1)*150+ei*30+i;mode='internal' if i%2==0 else 'external';ri=(i//2)%3
     if i>=24:reserved.append((seed,e,mode,ri));continue
     seg='train' if i<18 else 'validation'
     for side,cfg in (('candidate',c),('control',best[e])):
      target=folder/side;target.mkdir(exist_ok=True)
      jobs.append((seed,e,mode,ri,cfg,snapshot,str(target),seg,side))
   rooms=collect(jobs)
   for e in ELEMENTS:
    rs=[r for r in rooms if r['comparison']['element']==e];train=[r for r in rs if r['comparison']['segment']=='train'];val=[r for r in rs if r['comparison']['segment']=='validation']
    td,vd=paired(train),paired(val);modes={m:paired([r for r in val if r['comparison']['mode']==m]) for m in ('internal','external')}
    cand=[r for r in train if r['pair_side']=='candidate'];ctrl=[r for r in train if r['pair_side']=='control']
    cd,od=describe(cand),describe(ctrl)
    reward=.02*(step-1)/9*max(-1,min(1,(cd['mean_size']-od['mean_size'])/(SIZES[e]+4-8)))
    accept=len(train)==36 and len(val)==12 and td['mean']+reward>=0 and vd['mean']>=.002 and all(v['mean']>=-.025 for v in modes.values())
    if accept:best[e]=deepcopy(candidates[e]);best[e]['checkpoint']=f'paired-{e}-accepted-{step:02d}'
    # Both paired sides provide 36 focal observations; no other elements enter the gradient.
    rows=[]
    for r in train:
     for p,x,y in reward_rows([r]):
      if p['pid'] in r['comparison']['first_seats']:rows.append((p,x,y+.02*(step-1)/9*min(1,max(0,(len(p['deck'])-8)/(SIZES[e]+4-8)))))
    directions[e]={prefix+k:correlation(rows,prefix+k) for section,prefix in (('weights','w.'),('construction','c.')) for k in initial[e][section]}
    choice=dict(round=step,accepted=accept,selected=best[e]['checkpoint'],bonus=candidates[e]['size_exploration']['bonus'],train=td,validation=vd,modes=modes,size_reward=reward,candidate_decks=cd,control_decks=od)
    batches[e].append(choice);save(folders[e]/'selection.json',choice);save(folders[e]/'checkpoint.json',best[e])
   for job in jobs:
    path=Path(job[6])/f'room-{job[0]}.json.gz'
    if path.exists():paths.append(path)
   save(out/'issues.json',issues);progress=dict(round=step,episodes=len(paths),reserved_pairs=len(reserved),skipped=len(issues),accepted={e:[b['round'] for b in bs if b['accepted']] for e,bs in batches.items()},elapsed=round(time.monotonic()-started,1))
   save(out/'progress.json',progress);print(json.dumps(progress),flush=True)
  save(out/'frozen-policies.json',best);jobs=[]
  for seed,e,mode,ri in reserved:
   for side,cfg in (('candidate',best[e]),('control',initial[e])):
    target=out/'final'/side;target.mkdir(parents=True,exist_ok=True)
    jobs.append((seed,e,mode,ri,cfg,initial,str(target),'final-holdout',side))
  final=collect(jobs)
  for job in jobs:
   path=Path(job[6])/f'room-{job[0]}.json.gz'
   if path.exists():paths.append(path)
 for name,digest in hashes.items():assert hashlib.sha256((ROOT/name).read_bytes()).hexdigest()==digest,name
 stats=Statistics()
 for path in paths:
  with gzip.open(path,'rt',encoding='utf-8') as f:stats.add(json.load(f))
 save(out/'statistics.json',stats.result());evaluation={}
 for e in ELEMENTS:
  rs=[r for r in final if r['comparison']['element']==e];cs=[r for r in rs if r['pair_side']=='candidate'];os=[r for r in rs if r['pair_side']=='control']
  evaluation[e]=dict(paired=paired(rs),candidate=group_metrics(cs),control=group_metrics(os),decks=describe(cs),control_decks=describe(os),
   modes={m:dict(paired=paired([r for r in rs if r['comparison']['mode']==m]),candidate=group_metrics([r for r in cs if r['comparison']['mode']==m]),control=group_metrics([r for r in os if r['comparison']['mode']==m])) for m in ('internal','external')},stages=stages(cs))
  save(out/f'{e}-final-policy.json',best[e]);save(out/f'{e}-final-statistics.json',selected_statistics(cs))
 save(out/'training.json',dict(episodes=len(paths),pairs=len(paths)//2,battles=stats.battles,skipped=len(issues),batches=batches,evaluation=evaluation,input_hashes_verified=True,default_changed=False))
 save(out/'issues.json',issues);save(out/'progress.json',dict(finished=True,episodes=len(paths),battles=stats.battles,skipped=len(issues),elapsed=round(time.monotonic()-started,1)))
 print('PAIRED_ELEMENT_TRAINING_COMPLETE',flush=True)

if __name__=='__main__':main()
