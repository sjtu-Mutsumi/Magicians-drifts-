"""Read-only independent audit of the exported native C# deliverable."""
from pathlib import Path
import hashlib
import json
import re
import argparse

ROOT=Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--package',required=True,type=Path)
parser.add_argument('--original',type=Path,help='Optional original playable package for retention check')
args=parser.parse_args()
PACKAGE=args.package.resolve()
ORIGINAL=args.original.resolve() if args.original else None
DATA=PACKAGE/'runtime-data'

def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()

integrity=json.loads((PACKAGE/'integrity.json').read_text(encoding='utf-8-sig'))
files={p.relative_to(PACKAGE).as_posix():p for p in PACKAGE.rglob('*') if p.is_file()}
assert set(integrity)==set(files)-{'integrity.json'},('unlisted or missing files',set(files)-{'integrity.json'}-set(integrity),set(integrity)-set(files))
assert all(sha(files[name])==digest for name,digest in integrity.items()),'Integrity checksum mismatch'
forbidden=[name for name in files if Path(name).suffix.lower() in {'.py','.pyc','.pyd'} or
           re.match(r'^python.*\.(?:exe|dll)$',Path(name).name,flags=re.I)]
assert not forbidden,forbidden
pck=(PACKAGE/'Magicians.pck').read_bytes()
assert not re.search(rb'(?i)res://[^\x00\r\n]{0,240}\.py(?:[cd])?(?:\x00|\r|\n)',pck),'Python resource in PCK'
info=json.loads((PACKAGE/'BUILD-INFO.json').read_text(encoding='utf-8-sig'))
assert info['runtime']=='native-csharp' and info['python_required'] is False
cards=json.loads((DATA/'releases/v1.2/cards.json').read_text(encoding='utf-8'))['cards']
assert len(cards)==183 and sum(card['pool_copies'] for card in cards.values())==677
assert (DATA/'releases/v1.2/cards.json').read_bytes()==(ROOT/'releases/v1.2/cards.json').read_bytes()
assert info['cards_sha256']==sha(DATA/'releases/v1.2/cards.json')
display=json.loads((DATA/'Data/Presentation/card-display.json').read_text(encoding='utf-8'))['cards']
assert set(display)==set(cards) and sum(key!=row['name'] for key,row in display.items())==103
assert (DATA/'Data/Presentation/card-display.json').read_bytes()==(ROOT/'Data/Presentation/card-display.json').read_bytes()
assert all(set(row)=={'name','rules'} for row in display.values())
bundle=DATA/'releases/ai-v1.2-six-styles-20261029'
manifest=json.loads((bundle/'manifest.json').read_text(encoding='utf-8'))
assert set(manifest['models'])=={'fire','water','earth','wind','poison','flexible'}
assert manifest['cards_sha256']==sha(DATA/'releases/v1.2/cards.json')
assert info['models_sha256']==sha(bundle/'manifest.json')
for style,row in manifest['models'].items():
    path=bundle/row['file']
    assert sha(path)==row['sha256']
    assert path.read_bytes()==(ROOT/'releases/ai-v1.2-six-styles-20261029'/row['file']).read_bytes()
flavor_count=0
for element in ('water','poison','earth','wind'):
    rel=f'docs/cards-v1.2/{element}-flavor.json'
    assert (DATA/rel).read_bytes()==(ROOT/rel).read_bytes()
    flavor=json.loads((DATA/rel).read_text(encoding='utf-8'))
    assert flavor['display_in_match'] is False and len(flavor['cards'])==5
    flavor_count+=len(flavor['cards'])

native_sources=list((ROOT/'Scripts/Native').rglob('*.cs'))+list((ROOT/'Scripts/Application').rglob('*.cs'))
bad_calls=[]
pattern=re.compile(r'Process\s*\.\s*Start\s*\(|new\s+ProcessStartInfo|PythonEngine|IronPython|Python\.Runtime|python\.exe|MAGICIANS_PYTHON|NotImplementedException|TODO')
for path in native_sources:
    for number,line in enumerate(path.read_text(encoding='utf-8-sig').splitlines(),1):
        if pattern.search(line):bad_calls.append({'file':path.relative_to(ROOT).as_posix(),'line':number,'text':line.strip()})
assert not bad_calls,bad_calls
if ORIGINAL is not None:
    assert (ORIGINAL/'Magicians.exe').exists(),'Original playable version missing'
baseline=json.loads((ROOT/'migration/baseline.json').read_text(encoding='utf-8'))
ui_changed=[]
for row in baseline['files']:
    if row['path'].startswith('Scripts/Presentation/') or row['path'].startswith('Scenes/UI/'):
        data=(ROOT/row['path']).read_bytes()
        blob=hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
        if blob!=row['sha']:ui_changed.append(row['path'])
report=dict(status='passed',package=str(PACKAGE),integrity_entries=len(integrity),files=len(files),
    python_files=forbidden,python_call_or_stub_scan=bad_calls,runtime=info['runtime'],python_required=info['python_required'],
    cards=len(cards),stock=sum(card['pool_copies'] for card in cards.values()),models=len(manifest['models']),
    renamed_cards=sum(key!=row['name'] for key,row in display.items()),preserved_hidden_flavor_cards=flavor_count,
    source_presentation_or_card_ui_changes=ui_changed,original_playable_version_exists=(ORIGINAL/'Magicians.exe').exists() if ORIGINAL else None,
    rule_and_display_data_equal_to_source=True)
(Path(__file__).parent/'package-audit.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False))
