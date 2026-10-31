"""Offline differential probes for complex, legal v1.2 decks and full traces."""
from pathlib import Path
import collections
import json
import os
import queue
import random
import subprocess
import sys
import threading
import time

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(__file__).resolve().parent / 'mixed-battle-parity'
sys.path.insert(0, str(ROOT))
from tools.sim_v12.catalog import CARDS, ELEMENTS, SPECIAL, ROUTES, START
from tools.sim_v12.strict import StrictBattle


def difference(expected, actual, path='$'):
    if isinstance(expected, dict) and isinstance(actual, dict):
        if expected.keys() != actual.keys():
            return dict(path=path, missing=list(expected.keys()-actual.keys()), extra=list(actual.keys()-expected.keys()))
        for key in expected:
            result = difference(expected[key], actual[key], path+'.'+str(key))
            if result:
                return result
        return None
    if isinstance(expected, list) and isinstance(actual, list):
        if len(expected) != len(actual):
            return dict(path=path, expected_length=len(expected), actual_length=len(actual))
        for index, (left, right) in enumerate(zip(expected, actual)):
            result = difference(left, right, path+f'[{index}]')
            if result:
                return result
        return None
    return None if expected == actual else dict(path=path, expected=expected, actual=actual)


def build_deck(element, rng, route=None, full=False):
    route = route or rng.choice(ROUTES[element])
    special = SPECIAL[element]
    commons = [name for name, card in CARDS.items() if card.element in (element, 'generic') and card.rarity in ('common', 'rare', 'epic') and name not in START]
    chosen = commons if full else rng.sample(commons, rng.randint(6, min(len(commons), 20)))
    legends = [name for name, card in CARDS.items() if card.element in (element, 'generic') and card.rarity == 'legendary' and card.kind != 'route_legendary']
    if full:
        stacked = next(name for name in legends if CARDS[name].element == element)
        chosen += [stacked, '时光倒流']
    else:
        chosen += rng.sample(legends, rng.randint(0, min(2, len(legends))))
    deck = list(START) + [special, route] + chosen
    assert len([name for name in deck if CARDS[name].rarity == 'legendary']) <= 3
    assert all(CARDS[name].element in (element, 'generic') for name in deck)
    return deck


def cases(count):
    rng = random.Random(2026103107)
    rows = []
    index = 0
    for element in ELEMENTS:
        for route in ROUTES[element]:
            other = ELEMENTS[(ELEMENTS.index(element)+2) % len(ELEMENTS)]
            rows.append(dict(id=f'full-{index}-{element}', seed=rng.randrange(1, 2**31-1), specializations=[element, other],
                             decks=[build_deck(element, rng, route, full=True), build_deck(other, rng, full=True)]))
            index += 1
    while len(rows) < count:
        left, right = rng.choice(ELEMENTS), rng.choice(ELEMENTS)
        rows.append(dict(id=f'mixed-{len(rows)}-{left}-{right}', seed=rng.randrange(1, 2**31-1),
                         specializations=[left, right], decks=[build_deck(left, rng), build_deck(right, rng)]))
    return rows[:count]


def main():
    count = int(sys.argv[1]) if len(sys.argv)>1 else 100
    OUT.mkdir(exist_ok=True)
    requests = cases(count)
    (OUT/'cases.json').write_text(json.dumps(requests, ensure_ascii=False, indent=2), encoding='utf-8')
    env = dict(os.environ, PYTHONHASHSEED='0', PYTHONUTF8='1', PYTHONIOENCODING='utf-8')
    native = subprocess.Popen([r'C:\Program Files\dotnet\dotnet.exe', str(ROOT/'migration/NativeRunner/bin/Debug/net8.0/NativeRunner.dll'), str(ROOT)],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=(OUT/'native.stderr.log').open('w',encoding='utf-8'),
        text=True, encoding='utf-8', env=env, cwd=ROOT)
    replies = queue.Queue()
    def read_lines():
        for line in native.stdout:
            replies.put(line)
        replies.put(None)
    threading.Thread(target=read_lines,daemon=True).start()
    results=[]
    started=time.monotonic()
    try:
        for row in requests:
            tick=time.monotonic()
            request=dict(op='battle_full', **{key:row[key] for key in ('decks','specializations','seed')})
            try:
                expected=StrictBattle(row['decks'], specializations=row['specializations'], seed=row['seed'], trace=True).run()
            except Exception as error:
                results.append(dict(id=row['id'], status='python_error', error=repr(error)))
                print('PYTHON_ERROR '+row['id']+' '+repr(error),flush=True)
                continue
            native.stdin.write(json.dumps(request,ensure_ascii=False)+'\n');native.stdin.flush()
            line=replies.get(timeout=120)
            if line is None:
                raise RuntimeError('Native runner exited unexpectedly')
            actual=json.loads(line)
            diff=difference(expected,actual)
            result=dict(id=row['id'],status='different' if diff else 'equal',seed=row['seed'],time=expected['time'],
                        events=len(expected['log']),actions=expected['actions'],duration=round(time.monotonic()-tick,3))
            if diff:
                result['difference']=diff
                (OUT/(row['id']+'.expected.json')).write_text(json.dumps(expected,ensure_ascii=False,indent=2),encoding='utf-8')
                (OUT/(row['id']+'.actual.json')).write_text(json.dumps(actual,ensure_ascii=False,indent=2),encoding='utf-8')
                print('DIFFERENT '+json.dumps(result,ensure_ascii=False),flush=True)
            elif len(results)%10 == 0:
                print('EQUAL '+row['id']+' events='+str(len(expected['log'])),flush=True)
            results.append(result)
            report=dict(total=len(results),planned=len(requests),equal=sum(x['status']=='equal' for x in results),
                        different=sum(x['status']=='different' for x in results),python_errors=sum(x['status']=='python_error' for x in results),
                        seconds=round(time.monotonic()-started,3),results=results)
            (OUT/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    finally:
        if native.poll() is None:
            native.stdin.close()
            try:native.wait(timeout=5)
            except subprocess.TimeoutExpired:native.kill();native.wait()
    print(json.dumps({key:value for key,value in report.items() if key!='results'},ensure_ascii=False),flush=True)
    return 0 if report['different']==0 and report['python_errors']==0 else 1

if __name__=='__main__':
    raise SystemExit(main())
