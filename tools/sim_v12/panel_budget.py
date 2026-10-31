"""Uniform v1.2 panel budgets by rarity and functional role, never element."""
from fractions import Fraction as Q
import json
from pathlib import Path

PATH=Path(__file__).parent/'rules/panel-budgets.json'
RULES=json.loads(PATH.read_text(encoding='utf-8'))

def role(name):return RULES['card_roles'][name]

def mechanism_band(name):
    matches=[(key,data) for key,data in RULES.get('mechanism_bands',{}).items() if name in data['cards']]
    if len(matches)>1:raise ValueError(('Overlapping mechanism budgets',name))
    return matches[0] if matches else None

def budget_group(name):
    band=mechanism_band(name)
    return band[0] if band else role(name)

def reference_rarity(name,rarity):
    band=mechanism_band(name)
    return band[1]['reference_rarity'] if band else rarity

def multiplier(name):
    band=mechanism_band(name)
    return Q(str(band[1]['multiplier'] if band else RULES['multipliers'][role(name)]))

def value(cost,attack,defense):return Q(2*attack+3*defense,cost+1)

def bounds(name,rarity):
 factor=multiplier(name)
 return tuple(Q(str(n))*factor for n in RULES['base_bands'][reference_rarity(name,rarity)])
