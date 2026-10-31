"""Freeze the reviewed v1.2 card definitions and combat implementation once."""
import sys,json,hashlib,zipfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT))

def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    release=ROOT/'releases/v1.2';manifest=release/'freeze-manifest.json'
    if manifest.exists():
        from tools.verify_v12 import main as verify
        verify();return
    package=ROOT/'releases/Magicians-cards-combat-v1.2.zip'
    if package.exists():raise RuntimeError('Existing package without manifest: inspect before retrying')
    review=ROOT/'research/v1.2-freeze-confirmation-20261029'
    audit=json.loads((review/'audit.json').read_text(encoding='utf-8'))
    replay=json.loads((review/'replay.json').read_text(encoding='utf-8'))
    regression=(review/'regression.log').read_text(encoding='utf-8')
    assert audit['cards']==183 and not audit['numeric_definitions_changed'] and not audit['combat_core_changed'] and not audit['ai_feature_changes'] and audit['contracts_match']
    assert replay['rooms']==10 and replay['battles']==286 and replay['all_exact']
    assert 'Ran 144 tests' in regression and regression.rstrip().endswith('OK')
    for name,digest in json.loads((review/'combat-baseline.json').read_text(encoding='utf-8')).items():assert sha(ROOT/name)==digest,name
    from tools.sim_v12.catalog import CARDS,START,SHIELD_CARDS,RETIRED,export,copies
    from tools.sim_v12.effects import verify_registry
    assert len(CARDS)==verify_registry()==183 and sum(copies(c) for c in CARDS.values())==677
    release.mkdir(parents=True,exist_ok=True);export(release)
    validation=dict(audit=audit,replay=replay,regressions=144,regression_log_sha256=sha(review/'regression.log'),notes='Card copy clarified; combat core and AI static features match the tested 1000-room baseline.')
    (release/'verification.json').write_text(json.dumps(validation,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    (release/'implementation-review.json').write_bytes((review/'implementation-review.json').read_bytes())
    info=dict(version='1.2',status='frozen',date='2026-10-29',scope='independent Python simulator; v1.1 client unchanged',cards=183,pool_copies=677,initial_deck=list(START),baseline='research/v1.2-additive-temporary-1000-20261029',baseline_seeds=[324000,324999],temporary_positive_values='additive',direct_damage='additive',shield_stripping='sum_percentages_on_one_prelaunch_shield_capped_at_100',max_penetration=1,min_interval=1,negative_effects='per card: fear multiplies; etch heal-block takes max; total heal-block capped at 60 percent',shield_cards=sorted(SHIELD_CARDS),training_may_change='AI policies, weights and decisions; never card definitions or protected combat code')
    (release/'combat-rules.json').write_text(json.dumps(info,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    (release/'retired-cards.json').write_text(json.dumps(dict(version='1.2',retired=sorted(RETIRED)),ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    runtime=[f'tools/sim_v12/{n}' for n in ('catalog.py','model.py','engine.py','effects.py','locked.py','strict.py','panel_budget.py','rules/combat.json','rules/panel-budgets.json')]
    protected=[ROOT/n for n in runtime]+list(release.glob('*.json'))+list(release.glob('*.md'))
    protected+=list((ROOT/'docs/cards-v1.2').glob('*.md'))+list((ROOT/'docs/cards-v1.2').glob('*.json'))
    protected+=[ROOT/f'tools/{n}' for n in ('freeze_v12.py','verify_v12.py','build_v12_catalogue.py','audit_v12_cards.py','audit_v12_layers.py')]
    protected=sorted(set(protected));hashes={p.relative_to(ROOT).as_posix():sha(p) for p in protected}
    # AI code/config are reference copies in the package, not live frozen files.
    files=sorted(set(protected+list((ROOT/'tools/sim_v12').rglob('*.py'))+list((ROOT/'tools/sim_v12/rules').glob('*.json'))+list((ROOT/'tools/round_robin').glob('*.py'))))
    packaged={p.relative_to(ROOT).as_posix():sha(p) for p in files}
    with zipfile.ZipFile(package,'x',zipfile.ZIP_DEFLATED) as z:
        for name in packaged:z.write(ROOT/name,name)
    with zipfile.ZipFile(package) as z:
        assert z.testzip() is None
        for name,digest in packaged.items():assert hashlib.sha256(z.read(name)).hexdigest()==digest,name
    result={**info,'sha256':hashes,'runtime_protected':runtime,'package':package.relative_to(ROOT).as_posix(),'package_sha256':sha(package),'package_files_sha256':packaged}
    manifest.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('V12_FROZEN cards=183 stock=677 package_sha256='+result['package_sha256'])

if __name__=='__main__':main()
