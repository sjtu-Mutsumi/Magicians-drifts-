"""Marginal deck quality, with legal purchases/deletions left to tournament.

Repeated play gains fixed panels and explicitly repeatable effects. First-play
element layers are never counted again just because a card cycles.
"""
from functools import lru_cache
from collections import Counter
from .catalog import CARDS,SPECIAL,START_ONLY,ELEMENTS
from .mechanics import static

THIN_INITIAL=dict(target=18.,dilution=.82,buy_margin=.65,point_cost=.5,delete_margin=1.,
 delete_pressure=.25,starter_bias=1.,core_weight=2.8,first_layer=1.5,generic_tax=1.,flow_scale=.6)
REPEAT={'风暴':4,'层岩结构':2,'奔流':1.25,'烈风':2,'深渊':3,
 '山岳壁垒':1.5,'蚀创':3,'战意':1.3,'熔盾烈焰':1,'回震岩甲':1.6}
BURST={'引火':.6,'猛火弹':2.5,'利刃':1.2,'爆燃':3.5,'压缩爆燃':5.,'燃血':2.,
 '疾风连斩':5.,'风痕回廊':4.,'风切':2.5,'裂风':3.,'风痕刻印':3.,'穿潮':2.,
 '岩核重压':3.,'裂岩护壁':3.,'万钧碎盾':4.,'碎盾术':2.8,'万毒爆':4.,'毒潮':2.5,
 '万蚀':3.,'万毒蚀骨':3.,'腐溃':2.,'腐息':2.5,'血怒':2.,'洪流':2.,'潮汐风暴':3.}

def policy(p):
 from .learning_policy import config
 return config(p)

def is_thin(p):return policy(p).get('kind') in ('thin','explorer')

@lru_cache(maxsize=30000)
def context(names,spec,route,style,params):
 from .policy import SUPPORT,STYLES
 v=dict(zip(THIN_INITIAL,params));weights=STYLES[style]
 eff=sum(.35 if CARDS[n].ability else 1 for n in names)
 mainlayers=sum(CARDS[n].layers for n in names if CARDS[n].element==spec)
 draw=sum(static(n)['draw'] for n in names)/max(1,len(names))
 fee=sum(static(n)['fee'] for n in names)/max(1,len(names))
 def quality(n):
  c=CARDS[n];f=static(n)
  panel=.36*(c.attack*weights['attack']+c.defense*weights['defense'])
  flow=(3.6*f['draw']+5*f['fee']+1.7*f['search']+2.2*f['shuffle']+2.5*f['recover'])*v['flow_scale']
  if n=='风痕回廊' and route=='风痕':flow-=7.2*v['flow_scale']
  value=panel+flow+f['delay']*1.8+BURST.get(n,0)+v['core_weight']*REPEAT.get(n,0)
  value+=.6*f['shield']+.5*f['heal']+1.2*(f['future_draw']+1.3*f['future_fee'])
  if c.ability:value+=f['passive']*1.5
  if c.element==spec:
   value+=v['first_layer']*c.layers/(1+mainlayers/12)
  elif c.element=='generic':value-=v['generic_tax']
  elif spec:value-=4
  if n in SUPPORT.get(route,()):value+=2.2
  if n in ('毒龙','毒瓶','毒针') and spec=='poison':value+=2.5
  if n=='气旋护壁':value+=4
  if n=='回流' and spec=='water':value+=3
  if f['draw'] and f['fee']==0 and draw>fee+.4:value-=min(2,draw-fee)*c.cost
  return value/(1+.35*c.cost)
 values={n:quality(n) for n in set(names)}
 repeatable=sorted((q for n,q in values.items() if not CARDS[n].ability),reverse=True)
 reference=sum(repeatable[:8])/max(1,len(repeatable[:8]))
 return values,reference,eff,quality

def quality_context(p):
 params=policy(p).get('construction',THIN_INITIAL)
 return context(tuple(n for n,t in p.deck),p.spec,p.route,p.style,tuple(params[k] for k in THIN_INITIAL))

def draft_quality(p,name,public=()):
 if policy(p).get('kind')=='explorer':
  from .exploration import quality
  return quality(p,name,public)
 values,reference,eff,quality=quality_context(p);q=quality(name)
 c=CARDS[name]
 if not p.spec and c.enchantment:
  counts=Counter(CARDS[n].element for n,t in p.deck)
  focus=max(ELEMENTS,key=lambda e:(counts[e],e==p.preference))
  q+=6+3*counts[focus] if c.element==focus else -3
 if c.kind=='route_legendary':
  if name in {r['route'] for r in public if r['pid']!=p.pid}:return -100
  q+=12 if not p.route else -12
 return q

def purchase_value(p,name,cost,replaced,public):
 if policy(p).get('kind')=='explorer':
  from .exploration import purchase
  return purchase(p,name,cost,replaced,public)
 from .policy import draft_value
 if not is_thin(p):
  value=draft_value(p,name,public)-2*cost
  return value-(draft_value(p,replaced,public) if replaced else 0)
 par=policy(p)['construction'];_,reference,eff,_=quality_context(p)
 q=draft_quality(p,name,public)
 pressure=max(0,eff-par['target'])*.3
 value=q-par['dilution']*reference-par['buy_margin']-par['point_cost']*cost-pressure
 if replaced:value=q-draft_quality(p,replaced,public)-par['point_cost']*cost-par['buy_margin']
 return value

def deletion_value(p,name,fee):
 if policy(p).get('kind')=='explorer':
  from .exploration import deletion
  return deletion(p,name,fee)
 par=policy(p)['construction'];_,reference,eff,_=quality_context(p)
 q=draft_quality(p,name)
 pressure=max(0,eff-par['target'])*par['delete_pressure']
 gain=par['dilution']*reference-q+pressure+par['starter_bias']*(name in START_ONLY)
 return gain-par['delete_margin']-fee*par['point_cost']

def refresh_wanted(p,round_number,unbought,public):
 if policy(p).get('kind')=='explorer':
  from .exploration import refresh
  return refresh(p,round_number,unbought,public)
 if not is_thin(p):return None
 _,ref,eff,_=quality_context(p)
 # Do not spend a refresh point just to enlarge an already sufficient deck.
 return p.points>=2 and (eff<policy(p)['construction']['target']-1 or (round_number>=8 and not p.route))
