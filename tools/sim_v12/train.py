"""Ten 100-room steps: 60 training, 20 checkpoint validation, 20 held out.

All models use current cards/rules. Each comparison seats two agents per policy
with independently random styles. Holdout results are excluded from updates.
"""
import argparse
from collections import Counter,defaultdict
from concurrent.futures import ProcessPoolExecutor,as_completed
from copy import deepcopy
import gzip,hashlib,importlib.util,json,math,random,sys,time
from pathlib import Path
from .catalog import ROOT,CARDS,export
from .mechanics import static
from .learning_policy import INITIAL_WEIGHTS
from .analytics import Statistics
from .quick_test import reports,save,pct,number

KEYS=tuple(INITIAL_WEIGHTS)

def worker(seed,first,second,folder,segment):
 from .tournament import simulate
 rng=random.Random(seed^978149);seats=list(range(4));rng.shuffle(seats)
 first_seats=set(seats[:2]);configs=[first if i in first_seats else second for i in range(4)]
 room=simulate(seed,policy_by_seat=configs)
 room['comparison']=dict(first=first['checkpoint'],second=second['checkpoint'],first_seats=sorted(first_seats),segment=segment)
 with gzip.open(Path(folder)/f'room-{seed}.json.gz','wt',encoding='utf-8') as handle:json.dump(room,handle,ensure_ascii=False)
 return room

def group_metrics(rooms,first=True):
 score=0;players=0;champions=0;battles=wins=0;times=[];flow=Counter()
 for room in rooms:
  seats=set(room['comparison']['first_seats'])
  if not first:seats=set(range(4))-seats
  for p in room['placements']:
   if p['pid'] in seats:players+=1;score+=p['score'];champions+=p['rank']==1
  for rnd in room['rounds']:
   for battle in rnd['battles']:
    for i,p in enumerate(battle['participants']):
     if i==1 and battle['ghost'] or p['pid'] not in seats:continue
     battles+=1;wins+=.5 if battle['winner'] is None else battle['winner']==i
     flow.update(battle['flow_by_seat'][i])
     if battle['winner']==i:times.append(battle['time'])
 return dict(players=players,mean_score=score/max(1,players),championship_rate=champions/max(1,players),
  battle_count=battles,win_rate=wins/max(1,battles),winner_time=sum(times)/max(1,len(times)),
  plays_per_battle=flow['played']/max(1,battles),instant_draw_per_battle=flow['instant_drawn']/max(1,battles),
  immediate_fee_per_battle=flow['instant_fee']/max(1,battles),discard_per_battle=flow['discarded']/max(1,battles),
  shuffle_per_battle=flow['shuffles']/max(1,battles),unused_fee_per_stage=flow['unused_energy']/max(1,flow['stages']))

def feature_vector(p,casts,battles):
 names=p['deck'];n=max(1,len(names));v=Counter()
 for name in names:
  c=CARDS[name];f=static(name)
  blend=.5/n+.5*casts[name]/max(1,battles*20)
  for k in KEYS:
   value=(c.attack+1.5*c.defense) if k=='panel' else c.layers if k=='layer' else f.get(k,0)
   if k=='recycle':value=f['shuffle']+f['recover']
   elif k=='future':value=f['future_draw']+1.3*f['future_fee']
   elif k=='protection':value=f['shield']+f['heal']
   elif k=='thinning':value=f['discard']/max(1,c.cost+1)
   elif k=='lookahead':value=f['draw']*max(0,f['fee']+1-c.cost/2)+f['search']*.5
   elif k=='tempo':value=f['delay']
   v[k]+=value*blend
 return v

def gradient(rooms):
 rows=[];element=defaultdict(list);styles=defaultdict(list)
 for room in rooms:
  casts=defaultdict(Counter);battles=Counter();wins=Counter();times=defaultdict(list)
  for rnd in room['rounds']:
   for b in rnd['battles']:
    for i,p in enumerate(b['participants']):
     if i==1 and b['ghost']:continue
     pid=p['pid'];battles[pid]+=1;casts[pid].update(b['executed_by_seat'][i])
     if b['winner']==i:wins[pid]+=1;times[pid].append(b['time'])
  for p in room['placements']:
   pid=p['pid'];tempo=sum(max(0,1-min(t,2000)/2000) for t in times[pid])/max(1,len(times[pid]))
   reward=p['score']+.15*wins[pid]/max(1,battles[pid])+.1*tempo
   rows.append((p,feature_vector(p,casts[pid],battles[pid]),reward))
   element[p['spec']].append(reward);styles[p['style']].append(reward)
 means=lambda d:{k:sum(v)/len(v) for k,v in d.items()}
 em,sm=means(element),means(styles)
 y=[reward-.5*em[p['spec']]-.3*sm[p['style']] for p,x,reward in rows]
 ym=sum(y)/len(y);ys=math.sqrt(sum((v-ym)**2 for v in y)/len(y))
 result={}
 for k in KEYS:
  x=[v[k] for p,v,r in rows];xm=sum(x)/len(x);xs=math.sqrt(sum((z-xm)**2 for z in x)/len(x))
  result[k]=sum((a-xm)*(b-ym) for a,b in zip(x,y))/len(x)/max(1e-9,xs*ys)
 return result

def historical(out):
 source=ROOT/'research/v1.2-quick-500-final-20261028';old=json.loads((source/'statistics.json').read_text(encoding='utf-8'))
 baseline=out/'baseline-code'
 spec=importlib.util.spec_from_file_location('historical_v12',baseline/'__init__.py',submodule_search_locations=[str(baseline)])
 package=importlib.util.module_from_spec(spec);sys.modules[spec.name]=package;spec.loader.exec_module(package)
 from importlib import import_module
 Battle=import_module('historical_v12.locked').LockedBattle
 replay=[];played=Counter();choice=None
 files=sorted((source/'rooms').glob('*.gz'))
 for index in range(0,500,21):
  room=json.loads(gzip.decompress(files[index].read_bytes()))
  # Sample differing element matchups over the entire historical seed range.
  rnd=room['rounds'][min(8,len(room['rounds'])-1)];b=rnd['battles'][0]
  seed=int.from_bytes(hashlib.sha256(f"{room['seed']}:{rnd['round']}:{b['a']}:{b['b']}".encode()).digest()[:8],'big')
  ps=b['participants'];engine=Battle(tuple(p['deck'] for p in ps),tuple(p['spec'] for p in ps),seed,True,styles=tuple(p['style'] for p in ps))
  result=engine.run();assert result['hp']==b['hp'] and result['winner']==b['winner']
  plays=[e for e in result['log'] if e['kind']=='play'];played.update(e['name'] for e in plays)
  record=dict(room_seed=room['seed'],round=rnd['round'],a=b['a'],b=b['b'],battle_seed=seed,hp_verified=b['hp'],events=len(result['log']),play_order=plays)
  replay.append(record)
 save(out/'historical-replays.json',replay)
 summary=dict(historical_games=old['games'],historical_battles=old['battles'],replayed_battles=len(replay),all_replay_results_match=True,
  previous_zero_adoption=[c['name'] for c in old['cards'] if not c['casts']],
  replay_casts=dict(played),previous_elements=old['elements'],
  limitations='旧500局未保存逐事件日志；基于完整账本、冻结239张快照和种子重放24场，全部生命/胜者一致；旧胜率不与删卡及新规则后的胜率作因果对比')
 save(out/'historical-analysis.json',summary)
 print('Historical replay verified: '+str(len(replay)),flush=True)

def main():
 parser=argparse.ArgumentParser();parser.add_argument('--out',required=True);parser.add_argument('--workers',type=int,default=12)
 args=parser.parse_args();out=Path(args.out).resolve();out.mkdir(exist_ok=True)
 assert not (out/'training.json').exists() and not (out/'batches').exists()
 historical(out);export(out)
 files=list(Path(__file__).parent.rglob('*.py'))+list((Path(__file__).parent/'rules').glob('*.json'))+list((ROOT/'docs/cards-v1.2').glob('*.md'))+list((ROOT/'docs/cards-v1.2').glob('*.json'))
 hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
 save(out/'inputs.json',dict(cards=len(CARDS),games=1000,batches=10,batch_size=100,seed_start=284000,source_sha256=hashes,
  protocol='60 training + 20 candidate/incumbent validation + 20 selected/immutable-legacy holdout per batch; 2v2 random seats/styles'))
 legacy=dict(kind='legacy',checkpoint='immutable-legacy');incumbent=dict(kind='mechanism',weights=deepcopy(INITIAL_WEIGHTS),checkpoint='initial-mechanism')
 save(out/'initial-policy.json',incumbent);save(out/'baseline-policy.json',legacy)
 direction={k:(1 if k in ('draw','fee','lookahead','recycle','thinning') else -.25 if k=='future' else .15) for k in KEYS}
 batches=[];all_rooms=[];holdout_paths=[];started=time.monotonic()
 with ProcessPoolExecutor(max_workers=args.workers) as pool:
  for step in range(1,11):
   folder=out/'batches'/f'{step:02d}';folder.mkdir(parents=True)
   candidate=deepcopy(incumbent);candidate['checkpoint']=f'candidate-{step:02d}'
   scale=max(abs(v) for v in direction.values()) or 1
   for k in KEYS:
    change=.05*direction[k]/scale
    candidate['weights'][k]=round(max(INITIAL_WEIGHTS[k]*.65,min(INITIAL_WEIGHTS[k]*1.5,incumbent['weights'][k]*(1+change))),6)
   save(folder/'candidate.json',candidate);save(folder/'incumbent.json',incumbent)
   rooms=[];seed0=284000+(step-1)*100
   futures={pool.submit(worker,seed0+i,candidate,incumbent,str(folder),'train' if i<60 else 'validation'):i for i in range(80)}
   for future in as_completed(futures):
    rooms.append(future.result())
    if len(rooms)%20==0:print(json.dumps(dict(batch=step,completed=(step-1)*100+len(rooms),total=1000,elapsed=round(time.monotonic()-started,1))),flush=True)
   rooms.sort(key=lambda r:r['seed']);train=rooms[:60];validation=rooms[60:]
   tr1,tr0=group_metrics(train),group_metrics(train,False);v1,v0=group_metrics(validation),group_metrics(validation,False)
   train_delta=tr1['mean_score']-tr0['mean_score'];val_delta=v1['mean_score']-v0['mean_score']
   accepted=train_delta>=0 and val_delta>=.2
   previous=incumbent['checkpoint']
   if accepted:incumbent=deepcopy(candidate);incumbent['checkpoint']=f'accepted-{step:02d}'
   direction=gradient(train)  # Never uses validation/holdout rows for updates.
   save(folder/'checkpoint.json',incumbent)
   entry=dict(batch=step,training_games=60,validation_games=20,holdout_games=20,accepted=accepted,previous=previous,
    selected=incumbent['checkpoint'],candidate=candidate,train_candidate=tr1,train_incumbent=tr0,validation_candidate=v1,validation_incumbent=v0,
    train_score_delta=train_delta,validation_score_delta=val_delta,training_gradient=direction)
   save(folder/'selection.json',entry);batches.append(entry);all_rooms.extend(rooms)
   # Select before dispatching holdout; its outcomes do not enter gradient or gate.
   futures=[pool.submit(worker,seed0+i,incumbent,legacy,str(folder),'holdout') for i in range(80,100)]
   for future in as_completed(futures):
    r=future.result();holdout_paths.append(folder/f"room-{r['seed']}.json.gz")
   save(out/'progress.json',dict(completed=step*100,total=1000,batch=step,accepted=accepted,checkpoint=incumbent['checkpoint']))
   print(json.dumps(dict(batch=step,completed=step*100,total=1000,accepted=accepted,validation_delta=val_delta,checkpoint=incumbent['checkpoint'],elapsed=round(time.monotonic()-started,1))),flush=True)
 for path,digest in hashes.items():assert hashlib.sha256((ROOT/path).read_bytes()).hexdigest()==digest,path
 holdouts=[json.loads(gzip.decompress(p.read_bytes())) for p in sorted(holdout_paths)]
 all_rooms+=holdouts;all_rooms.sort(key=lambda r:r['seed'])
 assert len(all_rooms)==1000 and {r['seed'] for r in all_rooms}==set(range(284000,285000))
 stats=Statistics();held=Statistics()
 for r in all_rooms:stats.add(r)
 for r in holdouts:held.add(r)
 result=stats.result();result['input_hashes_verified']=True;save(out/'statistics.json',result);reports(out,result)
 save(out/'holdout-statistics.json',held.result())
 training=dict(games=1000,batches=batches,accepted_steps=sum(b['accepted'] for b in batches),final_policy=incumbent,
  heldout_games=200,heldout_selected=group_metrics(holdouts),heldout_legacy=group_metrics(holdouts,False),
  final_holdout_selected=group_metrics([r for r in holdouts if r['comparison']['first']==incumbent['checkpoint']]),
  final_holdout_legacy=group_metrics([r for r in holdouts if r['comparison']['first']==incumbent['checkpoint']],False),
  final_holdout_games=sum(r['comparison']['first']==incumbent['checkpoint'] for r in holdouts),
  hashes_verified=True,elapsed_seconds=round(time.monotonic()-started,1))
 # Checkpoint selection never uses holdout. Deployment has a separate final
 # gate: do not replace the baseline with a measured regression.
 selected=training['final_holdout_selected'];baseline=training['final_holdout_legacy']
 promote=selected['mean_score']>baseline['mean_score'] and selected['win_rate']>=baseline['win_rate']
 deployed=incumbent if promote else dict(kind='legacy',checkpoint='deployment-fallback-legacy')
 training['deployment']=dict(promoted=promote,candidate=incumbent['checkpoint'],applied=deployed['checkpoint'],
  reason='Final heldout score and battle-win gate; checkpoint/parameters remain untouched')
 save(out/'training.json',training);save(out/'final-policy.json',incumbent)
 save(out/'deployment.json',training['deployment']);save(Path(__file__).parent/'rules/ai-policy.json',deployed)
 print('Training complete: '+str(out/'training.json'),flush=True)

if __name__=='__main__':main()
