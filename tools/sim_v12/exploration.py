"""Size-free marginal construction. No target length or length penalty.

Only rule-level minimum size is enforced by tournament. First-use layers compete
against the opportunity cost of drawing weaker cards and paying study points.
"""
from functools import lru_cache
from collections import Counter
from .catalog import CARDS,ELEMENTS,START_ONLY
from .mechanics import static
from .construction import REPEAT,BURST,policy

INITIAL=dict(dilution=.92,buy_margin=.45,point_cost=.4,delete_margin=.6,
 starter_bias=.9,core_weight=2.,first_layer=2.5,generic_tax=.8,flow_scale=.8)
ELEMENT_INITIAL={e:dict(layer=1.12 if e in ('fire','water','earth') else .94,
 recycle=1.08 if e in ('wind','poison') else .98,
 protection=1.05 if e in ('fire','water','earth') else .98) for e in ELEMENTS}

@lru_cache(maxsize=24000)
def context(names,spec,preference,route,style,params,weight_items,element_items,role_items=()):
 from .policy import STYLES,SUPPORT
 v=dict(zip(INITIAL,params));w=dict(weight_items);factors=dict(element_items);role_factors=dict(role_items)
 style=STYLES[style];counts=Counter(CARDS[n].element for n in names if CARDS[n].enchantment)
 focus=spec or max(ELEMENTS,key=lambda e:(counts[e],e==preference))
 mainlayers=sum(CARDS[n].layers for n in names if CARDS[n].element==focus)
 draw=sum(static(n)['draw'] for n in names)/max(1,len(names))
 fee=sum(static(n)['fee'] for n in names)/max(1,len(names))
 def value(n):
  c=CARDS[n];f=static(n)
  # Public composition only; no top-deck order or future opponents/offers.
  flow=(w['draw']*f['draw']+w['fee']*f['fee']+w['search']*f['search']+w['recycle']*factors.get('recycle',1)*(f['shuffle']+f['recover']))*v['flow_scale']
  if n=='风痕回廊' and route=='风痕':flow-=2*w['draw']*v['flow_scale']
  q=w['panel']*(c.attack*style['attack']+c.defense*style['defense'])+flow
  q+=v['core_weight']*REPEAT.get(n,0)+BURST.get(n,0)+w['tempo']*f['delay']
  q+=w['protection']*factors.get('protection',1)*(.7*f['shield']+.5*f['heal'])
  q+=w['future']*(f['future_draw']+1.3*f['future_fee'])
  q+=w['discard']*f['discard']+w['passive']*f['passive']
  if c.element==focus:
   q+=v['first_layer']*factors.get('layer',1)*c.layers/(1+mainlayers/20)
  elif c.element=='generic':q-=v['generic_tax']
  elif spec:q-=4
  if n in SUPPORT.get(route,()):q+=2.2
  if f['draw'] and not f['fee'] and draw>fee+.4:q-=min(2,draw-fee)*c.cost
  return q*role_factors.get(f['panel_role'],1.)/(1+.35*c.cost)
 values={n:value(n) for n in set(names)}
 repeat=[values[n] for n in names if not CARDS[n].ability]
 reference=sum(repeat)/max(1,len(repeat))
 return values,reference,value,focus

def view(p):
 cfg=policy(p);v=cfg['construction'];e=p.spec or p.preference
 return context(tuple(n for n,t in p.deck),p.spec,p.preference,p.route,p.style,
  tuple(v[k] for k in INITIAL),tuple(cfg['weights'].items()),tuple(cfg['element_weights'][e].items()),tuple(cfg.get('role_preferences',{}).items()))

def quality(p,name,public=()):
 _,ref,score,focus=view(p);q=score(name);c=CARDS[name]
 cfg=policy(p)
 if cfg.get('element_intent') and c.element not in ('generic',cfg['element_intent']):return -100
 intent=cfg.get('route_intent')
 if intent and c.kind=='route_legendary':
  q+=8 if name==intent else -4
 if not p.spec and c.enchantment:
  count=sum(CARDS[n].element==focus for n,t in p.deck)
  q+=6+3*count if c.element==focus else -3
 if c.kind=='route_legendary':
  if name in {x['route'] for x in public if x['pid']!=p.pid}:return -100
  q+=12 if not p.route else -12
 return q

def purchase(p,name,cost,replaced,public):
 cfg=policy(p);v=cfg['construction'];_,ref,_,_=view(p);q=quality(p,name,public)
 value=q-(quality(p,replaced,public) if replaced else v['dilution']*ref)-v['buy_margin']-v['point_cost']*cost
 breadth=cfg.get('breadth')
 if cfg.get('size_exploration') and not replaced:
  b=cfg['size_exploration'];n=len(p.deck)
  if q>=.65*ref:value+=b['bonus']*max(0,(b['ceiling']-n)/max(1,b['ceiling']-8))
 if breadth and not replaced:
  # A soft incentive to add useful cards, never a forced purchase or size cap.
  c=CARDS[name];focus=p.spec or p.preference;n=len(p.deck)
  eligible=c.element in ('generic',focus) and (not c.name in START_ONLY)
  if eligible and q>=breadth['quality_floor']*ref:
   value+=breadth['fill_bonus']*max(0,(breadth['target']-n)/max(1,breadth['target']-8))
  value-=max(0,n-breadth['target'])*.45
 return value

def deletion(p,name,fee):
 cfg=policy(p);v=cfg['construction'];_,ref,_,_=view(p)
 value=v['dilution']*ref-quality(p,name)+v['starter_bias']*(name in START_ONLY)-v['delete_margin']-fee*v['point_cost']
 breadth=cfg.get('breadth')
 if cfg.get('size_exploration'):
  b=cfg['size_exploration']
  value-=b['bonus']*max(0,(b['ceiling']-len(p.deck))/max(1,b['ceiling']-8))
 if breadth:
  value-=breadth['retention_bonus']*min(1,max(0,breadth['target']+1-len(p.deck)))
  value-=breadth['paid_delete_penalty']*fee
 return value

def refresh(p,r,unbought,public):
 if p.points<2:return False
 values=[purchase(p,n,0,None,public) for n in unbought]
 return not values or max(values)<=0 or (r>=8 and not p.route)
