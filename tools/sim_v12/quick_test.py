"""Reproducible, durable, parallel 500-room four-AI quick test."""
import argparse
from concurrent.futures import ProcessPoolExecutor, as_completed
import gzip
import hashlib
import json
import os
from pathlib import Path
import time
from .catalog import ROOT, CARDS, export
from .policy import TARGETS
from .analytics import Statistics

def save(path,value):
    path.write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')

def worker(seed,folder):
    from .tournament import simulate
    room=simulate(seed,random_styles=True,sequence_mode='locked')
    path=Path(folder)/f'room-{seed}.json.gz'
    with gzip.open(path,'wt',encoding='utf-8') as handle:json.dump(room,handle,ensure_ascii=False)
    return seed,room

def pct(v):return '—' if v is None else f'{v:.1%}'
def number(v):return '—' if v is None else f'{v:.3f}'

def reports(out,result):
    cn={'fire':'火','water':'水','earth':'土','wind':'风','poison':'毒'}
    lines=['# v1.2 快速卡牌测试','',
        f"{result['games']} 局完整四 AI 赛事，{result['players']} 名人次，{result['battles']} 场战斗。每名 AI 每局独立随机风格，使用 locked 锁定运转/同位次正式结算。",
        '',f"平均 {sum(result['round_counts'])/result['games']:.2f} 赛事轮，范围 {min(result['round_counts'])}–{max(result['round_counts'])}；规划 {result['planned_actions']} 次出牌，正式执行 {result['executed_actions']} 次；差额包含死亡截断。失效跳过 {sum(result['sequence_skips'].values())} 次。",'',
        '第一至第四名计 +3/+1/−1/−3；并列占用名次槽位均分。战斗胜率=(胜+0.5×平)/实际玩家参战次数，幽灵不计为玩家。夺冠率按最终选择该元素/路线的玩家计，战斗按当时实际携带的元素/路线计。',
        '', '|元素|最终人数|战斗胜率|夺冠率|总分|人均分|','|---|---:|---:|---:|---:|---:|']
    for e,m in sorted(result['elements'].items(),key=lambda p:p[1]['mean_score'] if p[1]['mean_score'] is not None else -100,reverse=True):
        lines.append(f"|{cn[e]}|{m['players']}|{pct(m['win_rate'])}|{pct(m['championship_rate'])}|{m['score']:g}|{number(m['mean_score'])}|")
    lines+=['','|路线|最终人数|战斗胜率|夺冠率|总分|人均分|','|---|---:|---:|---:|---:|---:|']
    for n,m in sorted(result['routes'].items(),key=lambda p:p[1]['mean_score'] if p[1]['mean_score'] is not None else -100,reverse=True):
        lines.append(f"|{n}|{m['players']}|{pct(m['win_rate'])}|{pct(m['championship_rate'])}|{m['score']:g}|{number(m['mean_score'])}|")
    lines+=['','## 低携带复审候选','',
        '同元素、同品质内部取携带率最低四分之一，并要求至少30名同系玩家看见过该牌。初始牌、专精和传奇不进入此筛选；路线依赖一并列出。携带不足30人的成绩样本标为小样本。此轮保留全部卡牌供审阅。','',
        '|卡牌|同系携带率|全体携带率|携带人数|曝光人数|曝光购买率|携带人均分|携带夺冠率|路线关联|',
        '|---|---:|---:|---:|---:|---:|---:|---:|---|']
    for c in result['review_candidates']:
        lines.append(f"|{c['name']}|{pct(c['same_element_carry_rate'])}|{pct(c['carry_rate'])}|{c['carriers']}{'' if c['performance_sample_sufficient'] else '（小）'}|{c['exposed_players']}|{pct(c['conversion'])}|{number(c['mean_score'])}|{pct(c['championship_rate'])}|{'、'.join(c['route_support']) or '—'}|")
    lines+=['','逐卡完整统计见 [cards.md](cards.md)，机器数据见 [statistics.json](statistics.json)。逐局压缩账本在 rooms/，输入哈希在 inputs.json。','',
        '这些是当前启发式 AI 的相关表现，受构筑曝光、成本、路线和风格影响，不能单凭低携带率认定卡弱；本轮没有训练 AI 或改动平衡数值。']
    (out/'report.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    cards=[f'# 全{len(CARDS)}张卡牌携带与表现','',
        '携带率按最终仍持有该牌的玩家计；同名重复不重复计人。得分率=累计名次分/(3×携带人数)，范围−100%至100%。初始牌/专精免费发放且不入池，曝光购买率不适用。完整曝光、曾携带、删除、留存、施放和风格分项见 statistics.json。','',
        '|卡牌|最终携带人数|携带率|同系携带率|得分率|人均分|夺冠率|战斗胜率|曾携带率|曝光人数|购买率|',
        '|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|']
    for c in sorted(result['cards'],key=lambda c:c['carry_rate']):
        cards.append(f"|{c['name']}|{c['players']}|{pct(c['carry_rate'])}|{pct(c['same_element_carry_rate'])}|{pct(c['score_rate'])}|{number(c['mean_score'])}|{pct(c['championship_rate'])}|{pct(c['win_rate'])}|{pct(c['ever_carry_rate'])}|{c['exposed_players']}|{pct(c['exposure_conversion'])}|")
    (out/'cards.md').write_text('\n'.join(cards)+'\n',encoding='utf-8')

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--games',type=int,default=500)
    parser.add_argument('--seed',type=int,default=281200)
    parser.add_argument('--workers',type=int,default=min(12,os.cpu_count() or 1))
    parser.add_argument('--out',required=True)
    args=parser.parse_args()
    assert args.games>0 and args.workers>0
    out=Path(args.out).resolve();out.mkdir(parents=True,exist_ok=False)
    rooms=out/'rooms';rooms.mkdir();export(out)
    files=list(Path(__file__).parent.rglob('*.py'))+list((Path(__file__).parent/'rules').glob('*.json'))
    files+=list((ROOT/'docs/cards-v1.2').glob('*.md'))+list((ROOT/'docs/cards-v1.2').glob('*.json'))
    manifest={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
    save(out/'inputs.json',dict(games=args.games,seed=args.seed,workers=args.workers,sequence_rule='locked',sha256=manifest))
    save(out/'target-decks.json',TARGETS)
    stats=Statistics();started=time.monotonic()
    try:
        with ProcessPoolExecutor(max_workers=args.workers) as pool:
            futures={pool.submit(worker,args.seed+i,str(rooms)):args.seed+i for i in range(args.games)}
            for future in as_completed(futures):
                seed,room=future.result();stats.add(room)
                if stats.rooms%5==0 or stats.rooms==args.games:
                    progress=dict(completed=stats.rooms,total=args.games,battles=stats.battles,elapsed_seconds=round(time.monotonic()-started,1),last_seed=seed)
                    save(out/'progress.json',progress);print(json.dumps(progress),flush=True)
        for path,digest in manifest.items():assert hashlib.sha256((ROOT/path).read_bytes()).hexdigest()==digest,path
        result=stats.result();result['input_hashes_verified']=True
        save(out/'statistics.json',result);reports(out,result)
        print(str(out/'report.md'),flush=True)
    except BaseException as exc:
        save(out/'failure.json',dict(completed=stats.rooms,error=repr(exc)))
        raise

if __name__=='__main__':main()
