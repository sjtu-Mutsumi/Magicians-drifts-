"""Player-filtered descriptive card metrics for sealed policy comparisons."""
from collections import Counter,defaultdict
from .catalog import CARDS,ELEMENTS,ROUTES
from .analytics import Statistics

def selected_statistics(rooms):
 elements={e:Counter() for e in ELEMENTS};routes={r:Counter() for rs in ROUTES.values() for r in rs}
 cards={n:Counter() for n in CARDS};people=0
 for room in rooms:
  selected=set(room['comparison']['first_seats']);people+=len(selected)
  final={p['pid']:p for p in room['placements'] if p['pid'] in selected}
  champions=sum(p['rank']==1 for p in room['placements'])
  seen=defaultdict(set);bought=defaultdict(set);ever=defaultdict(set)
  for rnd in room['rounds']:
   for row in rnd['ledger']:
    pid=row['pid']
    if pid not in selected:continue
    ever[pid].update(row['deck'])
    for event in row['exposure']:seen[pid].add(event['name']);cards[event['name']]['offer_slots']+=1
    for action in row['actions']:
     if action['kind']=='buy':bought[pid].add(action['name'])
   for b in rnd['battles']:
    for i,p in enumerate(b['participants']):
     if i==1 and b['ghost'] or p['pid'] not in selected:continue
     books=([elements[p['spec']]] if p['spec'] else [])+([routes[p['route']]] if p['route'] else [])
     for book in books:Statistics.combat(book,b['winner'],i)
     for name in set(p['deck']):
      Statistics.combat(cards[name],b['winner'],i)
      casts=b['executed_by_seat'][i].get(name,0)
      cards[name]['casts']+=casts;cards[name]['cast_battles']+=casts>0
  for pid,p in final.items():
   share=1/champions if p['rank']==1 else 0
   for book in ([elements[p['spec']]] if p['spec'] else [])+([routes[p['route']]] if p['route'] else []):Statistics.ending(book,p,share)
   for name in set(p['deck']):Statistics.ending(cards[name],p,share)
   for name in seen[pid]:cards[name]['exposed']+=1
   for name in bought[pid]:cards[name]['buyers']+=1
   for name in ever[pid]:cards[name]['ever']+=1
 result=[]
 for name,b in cards.items():
  c=CARDS[name];m=Statistics.metrics(b)
  m.update(name=name,element=c.element,rarity=c.rarity,carry_rate=b['players']/max(1,people),
   exposed_players=b['exposed'],buying_players=b['buyers'],exposure_conversion=b['buyers']/b['exposed'] if b['exposed'] else None,
   ever_carry_rate=b['ever']/max(1,people),casts=b['casts'],cast_battles=b['cast_battles'],
   usage_rate=b['cast_battles']/b['battles'] if b['battles'] else None,
   casts_per_carried_battle=b['casts']/b['battles'] if b['battles'] else None)
  result.append(m)
 return dict(games=len(rooms),players=people,elements={k:Statistics.metrics(v) for k,v in elements.items()},
  routes={k:Statistics.metrics(v) for k,v in routes.items()},cards=result)
