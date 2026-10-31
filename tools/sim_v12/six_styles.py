"""Versioned policy-only handoff for future v1.2 single-player integration.

Use load_policy(style) as a policy_by_seat entry. Strategic style IDs are not
the six tactical STYLES keys used by the combat heuristic.
"""
import hashlib,json
from pathlib import Path
from .catalog import ROOT

IDS=('fire','water','earth','wind','poison','flexible')
DEFAULT_BUNDLE=ROOT/'releases/ai-v1.2-six-styles-20261029'

def available_styles(bundle=DEFAULT_BUNDLE):
 manifest=json.loads((Path(bundle)/'manifest.json').read_text(encoding='utf-8'))
 if manifest['game_version']!='1.2' or set(manifest['models'])!=set(IDS):raise ValueError('Invalid six-style registry')
 if hashlib.sha256((ROOT/'releases/v1.2/cards.json').read_bytes()).hexdigest()!=manifest['cards_sha256']:raise ValueError('Card snapshot mismatch')
 return manifest

def load_policy(style,bundle=DEFAULT_BUNDLE):
 if style not in IDS:raise ValueError('Unknown strategic style: '+str(style))
 root=Path(bundle).resolve();manifest=available_styles(root);item=manifest['models'][style]
 path=(root/item['file']).resolve()
 if not path.is_relative_to(root):raise ValueError('Policy path leaves bundle')
 raw=path.read_bytes()
 if hashlib.sha256(raw).hexdigest()!=item['sha256']:raise ValueError('Policy hash mismatch')
 cfg=json.loads(raw)
 if cfg.get('kind')!='explorer' or not {'weights','construction','element_weights'}<=cfg.keys():raise ValueError('Unsupported policy schema')
 if style=='flexible':
  if cfg.get('element_intent') or cfg.get('route_intent'):raise ValueError('Flexible policy must not force element/route')
 elif cfg.get('element_intent')!=style:raise ValueError('Element policy intent mismatch')
 return cfg
