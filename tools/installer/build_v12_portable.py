"""Build the native C# Windows client, with no Python runtime or Python source."""
from pathlib import Path
import argparse, hashlib, json, shutil, subprocess, zipfile
ROOT = Path(__file__).resolve().parents[2]
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def main():
    p=argparse.ArgumentParser()
    for name in ('godot','template','licenses','output'): p.add_argument('--'+name, type=Path, required=True)
    a=p.parse_args(); out=a.output.resolve()
    if out.exists(): raise RuntimeError('Choose a new output folder')
    out.mkdir(parents=True)
    presets=ROOT/'export_presets.cfg'; old=presets.read_bytes()
    lines=old.decode('utf-8-sig').splitlines()
    lines=[line for line in lines if not line.startswith(('custom_template/release=','custom_template/debug='))]
    lines.insert(lines.index('[preset.0.options]')+1,'custom_template/release='+json.dumps(a.template.resolve().as_posix(),ensure_ascii=False))
    debug=a.template.with_name('windows_debug_x86_64.exe')
    if debug.exists(): lines.insert(lines.index('[preset.0.options]')+1,'custom_template/debug='+json.dumps(debug.resolve().as_posix(),ensure_ascii=False))
    presets.write_text('\n'.join(lines)+'\n',encoding='utf-8')
    try:
        with Path(str(out)+'.export.log').open('w',encoding='utf-8') as log:
            for cmd in ([str(a.godot),'--headless','--editor','--path',str(ROOT),'--import','--quit'],
                        [str(a.godot),'--headless','--path',str(ROOT),'--export-release','Windows Portable',str(out/'Magicians.exe')]):
                subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT,check=True)
    finally: presets.write_bytes(old)
    export_log = Path(str(out)+'.export.log').read_text(encoding='utf-8')
    if 'Export .NET Project: Failed' in export_log or 'Failed to export project' in export_log:
        raise RuntimeError('Export did not rebuild the .NET client; do not distribute this output')
    data=out/'runtime-data'
    for relative in ('Data','docs/cards-v1.2','tools/sim_v12/rules','releases/v1.2','releases/ai-v1.2-six-styles-20261029'):
        src=ROOT/relative; dst=data/relative; dst.parent.mkdir(parents=True,exist_ok=True)
        shutil.copytree(src,dst,ignore=shutil.ignore_patterns('__pycache__','*.py','*.pyc','*.zip'))
    shutil.copytree(a.licenses,out/'licenses')
    info=dict(version='1.2',runtime='native-csharp',cards=183,pool=677,python_required=False,cards_sha256=sha(data/'releases/v1.2/cards.json'),models_sha256=sha(data/'releases/ai-v1.2-six-styles-20261029/manifest.json'))
    (out/'BUILD-INFO.json').write_text(json.dumps(info,indent=2),encoding='utf-8')
    (out/'README.txt').write_text('Magicians! v1.2 C# 客户端\n双击 Magicians.exe，选择开始单人赛事。游戏规则与六风格AI原生运行，不需要Python或Godot编辑器。\n183张卡、677份库存与五系定稿卡名沿用原版，小字保留但局内隐藏。\n请保留整个文件夹；需要分享时压缩整个文件夹。\n',encoding='utf-8')
    hashes={f.relative_to(out).as_posix():sha(f) for f in sorted(out.rglob('*')) if f.is_file()}
    (out/'integrity.json').write_text(json.dumps(hashes,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(dict(client=str(out/'Magicians.exe')),ensure_ascii=False))
if __name__=='__main__': main()
