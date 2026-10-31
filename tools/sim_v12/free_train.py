"""1000 fresh rooms: eight learning batches plus two frozen blind batches.

Learns mechanism weights and target-free marginal construction; only win rate
is the selection reward. Room-level confidence intervals preserve dependence.
"""
import argparse,gzip,hashlib,json,math,random,time
from collections import Counter,defaultdict
from concurrent.futures import ProcessPoolExecutor,as_completed
from copy import deepcopy
from pathlib import Path
from .catalog import ROOT,CARDS,ELEMENTS,copies,export
from .exploration import INITIAL,ELEMENT_INITIAL
from .learning_policy import INITIAL_WEIGHTS
from .mechanics import static
from .train import worker,group_metrics,feature_vector
from .thin_train import safe_room,paired_interval,deck_metrics
from .analytics import Statistics
from .cohort import selected_statistics
from .quick_test import save,reports

def win_interval(rooms):
 values=[]
 for r in rooms:
  sides=[Counter(),Counter()];first=set(r['comparison']['first_seats'])
  for rd in r['rounds']:
   for b in rd['battles']:
    for i,p in enumerate(b['participants']):
     if i==1 and b['ghost']:continue
     side=sides[int(p['pid'] not in first)];side['n']+=1
     side['w']+=.5 if b['winner'] is None else b['winner']==i
  values.append(sides[0]['w']/max(1,sides[0]['n'])-sides[1]['w']/max(1,sides[1]['n']))
 mean=sum(values)/max(1,len(values))
 se=math.sqrt(sum((x-mean)**2 for x in values)/max(1,len(values)-1)/max(1,len(values)))
 return dict(games=len(values),mean=mean,low=mean-1.96*se,high=mean+1.96*se)

def describe(rooms,first=True):
 sizes=[];groups=defaultdict(list);actions=Counter();rows=[];by_round=defaultdict(list)
 for r in rooms:
  selected=set(r['comparison']['first_seats'])
  if not first:selected=set(range(4))-selected
  for rd in r['rounds']:
   for row in rd['ledger']:
    if row['pid'] not in selected:continue
    for a in row['actions']:actions[a['kind']]+=1
    by_round[rd['round']].append(len(row['deck']))
  for p in r['placements']:
   if p['pid'] not in selected:continue
   names=p['deck'];n=len(names);sizes.append(n)
   flow=sum(any(static(x)[k] for k in ('draw','fee','search','discard','shuffle','recover')) for x in names)/max(1,n)
   row=dict(size=n,flow=flow,layers=sum(CARDS[x].layers for x in names if CARDS[x].element==p['spec']))
   groups[p['spec']].append(row);rows.append(row)
 def metrics(rs):
  ns=sorted(r['size'] for r in rs);count=len(ns)
  return dict(players=count,mean_size=sum(ns)/max(1,count),median=ns[count//2] if count else None,
   minimum=min(ns,default=None),maximum=max(ns,default=None),histogram=dict(sorted(Counter(ns).items())),
   flow_fraction=sum(r['flow'] for r in rs)/max(1,count),main_first_layers=sum(r['layers'] for r in rs)/max(1,count))
 result=metrics(rows);result.update(elements={e:metrics(groups[e]) for e in ELEMENTS},
  purchases_per_player=actions['buy']/max(1,len(sizes)),deletions_per_player=actions['delete']/max(1,len(sizes)),
  round_mean_size={r:sum(ns)/len(ns) for r,ns in by_round.items()})
 return result

def training_rows(rooms):
 rows=[]
 for r in rooms:
  casts=defaultdict(Counter);played=Counter();wins=Counter();deleted=Counter();bought=Counter()
  for rd in r['rounds']:
   for row in rd['ledger']:
    for a in row['actions']:
     deleted[row['pid']]+=a['kind']=='delete';bought[row['pid']]+=a['kind']=='buy'
   for b in rd['battles']:
    for i,p in enumerate(b['participants']):
     if i==1 and b['ghost']:continue
     pid=p['pid'];played[pid]+=1;wins[pid]+=.5 if b['winner'] is None else b['winner']==i
     casts[pid].update(b['executed_by_seat'][i])
  for p in r['placements']:
   pid=p['pid'];names=p['deck'];n=max(1,len(names));f=feature_vector(p,casts[pid],played[pid])
   extra=dict(dilution=-n,buy_margin=-bought[pid],point_cost=-bought[pid],delete_margin=-deleted[pid],
    starter_bias=deleted[pid],core_weight=f['passive'],first_layer=sum(CARDS[x].layers for x in names)/n,
    generic_tax=-sum(CARDS[x].element=='generic' for x in names)/n,flow_scale=f['draw']+f['fee']+f['recycle'])
   rows.append((p,dict(**{'w.'+k:v for k,v in f.items()},**{'c.'+k:v for k,v in extra.items()}),wins[pid]/max(1,played[pid])))
 return rows

def correlation(rows,key,element=None):
 if element:rows=[r for r in rows if r[0]['spec']==element]
 if len(rows)<30:return 0.
 # Shrink elemental/style outcome differences, keeping the reward win-only.
 es=defaultdict(list);ss=defaultdict(list)
 for p,x,y in rows:es[p['spec']].append(y);ss[p['style']].append(y)
 em={k:sum(v)/len(v) for k,v in es.items()};sm={k:sum(v)/len(v) for k,v in ss.items()}
 xs=[x.get(key,0) for p,x,y in rows];ys=[y-.6*em[p['spec']]-.25*sm[p['style']] for p,x,y in rows]
 xm=sum(xs)/len(xs);ym=sum(ys)/len(ys)
 cov=sum((x-xm)*(y-ym) for x,y in zip(xs,ys));sx=sum((x-xm)**2 for x in xs);sy=sum((y-ym)**2 for y in ys)
 return cov/max(1e-9,math.sqrt(sx*sy))

def propose(best,initial,step,direction):
 c=deepcopy(best);c['checkpoint']=f'explore-candidate-{step:02d}';rng=random.Random(9100+step)
 for section,prefix in (('weights','w.'),('construction','c.')):
  for k,value in c[section].items():
   signal=direction.get(prefix+k,rng.choice((-1,1)))
   delta=.04*(.7*signal+.3*rng.choice((-1,1)))
   anchor=initial[section][k]
   c[section][k]=round(max(anchor*.55,min(anchor*1.8,value*(1+delta))),6)
 for e in ELEMENTS:
  for k,value in c['element_weights'][e].items():
   signal=direction.get(e+'.'+k,0)
   anchor=initial['element_weights'][e][k]
   c['element_weights'][e][k]=round(max(anchor*.85,min(anchor*1.15,value*(1+.015*signal))),6)
 return c

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--out',required=True);ap.add_argument('--workers',type=int,default=12);args=ap.parse_args()
 out=Path(args.out).resolve();out.mkdir(parents=True,exist_ok=False);export(out)
 old=json.loads((ROOT/'research/v1.2-thin-deck-2000-20261029/final-policy.json').read_text(encoding='utf-8'))
 legacy=dict(kind='legacy',checkpoint='fixed-legacy')
 thin=deepcopy(old);thin['checkpoint']='fixed-prior-thin-current-rules'
 initial=dict(kind='explorer',checkpoint='explore-initial',weights=deepcopy(old['weights']),construction=deepcopy(INITIAL),element_weights=deepcopy(ELEMENT_INITIAL))
 best=deepcopy(initial);save(out/'baselines.json',dict(initial=initial,legacy=legacy,thin=thin))
 files=list(Path(__file__).parent.rglob('*.py'))+list((Path(__file__).parent/'rules').glob('*.json'))+list((ROOT/'docs/cards-v1.2').glob('*.md'))+list((ROOT/'docs/cards-v1.2').glob('*.json'))
 hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
 save(out/'inputs.json',dict(cards=len(CARDS),stock=sum(copies(c) for c in CARDS.values()),games=1000,seed_range=[292000,292999],
  protocol='8*(60train+20validation+20sealed)+2*100frozen;100final vs each anchor; reward=win rate only; no target-size feature',sha256=hashes))
 paths=[];issues=[];batches=[];direction={};started=time.monotonic()
 with ProcessPoolExecutor(max_workers=args.workers) as pool:
  def collect(jobs):
   result=[]
   for future in as_completed([pool.submit(safe_room,*job) for job in jobs]):
    row=future.result()
    if row['ok']:result.append(row['room'])
    else:issues.append(row)
   return sorted(result,key=lambda r:r['seed'])
  for step in range(1,11):
   folder=out/'batches'/f'{step:02d}';folder.mkdir(parents=True);seed0=292000+(step-1)*100
   if step<=8:
    candidate=propose(best,initial,step,direction);save(folder/'candidate.json',candidate);save(folder/'incumbent.json',best)
    rooms=collect([(seed0+i,candidate,best,str(folder),'train' if i<60 else 'validation') for i in range(80)])
    train=[r for r in rooms if r['comparison']['segment']=='train'];val=[r for r in rooms if r['comparison']['segment']=='validation']
    td=win_interval(train);vd=win_interval(val)
    accept=len(train)==60 and len(val)==20 and td['mean']>=0 and vd['mean']>=.005
    if accept:best=deepcopy(candidate);best['checkpoint']=f'explore-accepted-{step:02d}'
    rows=training_rows(train)
    direction={prefix+k:correlation(rows,prefix+k) for section,prefix in (('weights','w.'),('construction','c.')) for k in initial[section]}
    direction.update({e+'.'+k:correlation(rows,'w.'+k,e) for e in ELEMENTS for k in initial['element_weights'][e]})
    selection=dict(round=step,accepted=accept,selected=best['checkpoint'],train=td,validation=vd,train_decks=describe(train),gradient=direction)
    save(folder/'selection.json',selection)
    rooms+=collect([(seed0+i,best,legacy if i<90 else thin,str(folder),'rolling-holdout') for i in range(80,100)])
   else:
    selection=dict(round=step,accepted=None,selected=best['checkpoint'],frozen_final_test=True)
    rooms=collect([(seed0+i,best,legacy if i<50 else thin,str(folder),'final-holdout') for i in range(100)])
    save(folder/'selection.json',selection)
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
 evaluation={}
 for key,anchor in (('legacy',legacy),('prior_thin',thin)):
  rs=[r for r in final if r['comparison']['second']==anchor['checkpoint']]
  evaluation[key]=dict(games=len(rs),selected=group_metrics(rs),control=group_metrics(rs,False),paired_win=win_interval(rs),paired_score=paired_interval(rs),selected_decks=describe(rs),control_decks=describe(rs,False))
 promote=not issues and all(v['games']==100 and v['paired_win']['low']>0 for v in evaluation.values())
 summary=dict(completed=len(paths),attempted=1000,battles=stats.battles,skipped=len(issues),accepted_steps=sum(b['accepted'] is True for b in batches),
  batches=batches,candidate=best,evaluation=evaluation,final_test_games=len(final),final_decks=describe(final),promoted=promote,
  input_hashes_verified=True,train_games=480,validation_games=160,rolling_holdout_games=160,final_holdout_games=200)
 save(out/'training.json',summary);save(out/'final-policy.json',best);save(out/'final-selected-statistics.json',selected_statistics(final))
 current=json.loads((Path(__file__).parent/'rules/ai-policy.json').read_text(encoding='utf-8'))
 if promote:save(Path(__file__).parent/'rules/ai-policy.json',best)
 save(out/'deployment.json',dict(promoted=promote,deployed=(best if promote else current)['checkpoint'],criterion='both100room paired win95%lower>0, no skipped games'))
 print('FREE_TRAINING_COMPLETE '+str(out/'training.json'),flush=True)

if __name__=='__main__':main()
