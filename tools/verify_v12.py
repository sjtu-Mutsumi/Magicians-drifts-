"""Read-only verification of the reviewed v1.2 frozen release."""
from pathlib import Path
import sys,json,hashlib,zipfile
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT))

def main():
    release=ROOT/'releases/v1.2'
    m=json.loads((release/'freeze-manifest.json').read_text(encoding='utf-8'))
    assert m['status']=='frozen'
    for name,digest in m['sha256'].items():
        if hashlib.sha256((ROOT/name).read_bytes()).hexdigest()!=digest:raise RuntimeError('Frozen file changed: '+name)
    package=ROOT/m['package']
    assert hashlib.sha256(package.read_bytes()).hexdigest()==m['package_sha256']
    with zipfile.ZipFile(package) as z:
        assert z.testzip() is None
        for name,digest in m['package_files_sha256'].items():assert hashlib.sha256(z.read(name)).hexdigest()==digest,name
    from tools.sim_v12.catalog import CARDS,copies,load_sources,validate_frozen_cards
    from tools.sim_v12.effects import verify_registry
    from tools.sim_v12.panel_budget import value,bounds
    validate_frozen_cards(load_sources())
    assert len(CARDS)==verify_registry()==183 and sum(copies(c) for c in CARDS.values())==677
    contracts=json.loads((ROOT/'docs/cards-v1.2/snapshot-contract.json').read_text(encoding='utf-8'))['records']
    assert set(contracts)==set(CARDS)
    for name,card in CARDS.items():
        assert bounds(name,card.rarity)[0]<=value(card.cost,card.attack,card.defense)<=bounds(name,card.rarity)[1],name
        assert contracts[name]['card_text']==card.text,name
    print('V12_RELEASE_VERIFIED cards=183 stock=677 frozen_hashes=true package=true runtime_guard=true')

if __name__=='__main__':main()
