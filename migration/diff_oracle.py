"""Replay Python authority fixtures through the compiled native C# authority."""
from pathlib import Path
import argparse,json,subprocess,sys,time

ROOT=Path(__file__).resolve().parents[1]
DLL=ROOT/'migration/NativeRunner/bin/Debug/net8.0/NativeRunner.dll'


def difference(expected,actual,path='$'):
    if isinstance(expected,dict) and isinstance(actual,dict):
        if expected.keys()!=actual.keys():return dict(path=path,missing=sorted(expected.keys()-actual.keys()),extra=sorted(actual.keys()-expected.keys()))
        for key in expected:
            result=difference(expected[key],actual[key],path+'.'+str(key))
            if result:return result
    elif isinstance(expected,list) and isinstance(actual,list):
        if len(expected)!=len(actual):return dict(path=path,expected_length=len(expected),actual_length=len(actual))
        for i,(left,right) in enumerate(zip(expected,actual)):
            result=difference(left,right,path+f'[{i}]')
            if result:return result
    elif expected!=actual:return dict(path=path,expected=expected,actual=actual)
    return None


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--kind',choices=['all','card','battle','session'],default='all')
    parser.add_argument('--limit',type=int,default=0)
    args=parser.parse_args()
    error_path=ROOT/f'migration/oracle/native-{args.kind}.stderr.log'
    errors=error_path.open('w',encoding='utf-8')
    process=subprocess.Popen([r'C:\Program Files\dotnet\dotnet.exe',str(DLL),str(ROOT)],stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,stderr=errors,text=True,encoding='utf-8',cwd=ROOT)
    def request(data):
        process.stdin.write(json.dumps(data,ensure_ascii=False)+'\n');process.stdin.flush()
        line=process.stdout.readline()
        if not line:raise RuntimeError('Native worker closed: '+error_path.read_text(encoding='utf-8')[-2000:])
        return json.loads(line)
    counts={};failures=[];started=time.monotonic()
    try:
        for line in (ROOT/'migration/oracle/baseline.jsonl').open(encoding='utf-8'):
            row=json.loads(line);kind=row['kind']
            if args.kind!='all' and kind!=args.kind:continue
            if args.limit and sum(counts.values())>=args.limit:break
            case=dict(kind=kind,name=row.get('name'),rich=row.get('rich'),seed=row.get('seed'))
            if kind=='card':
                actual=request(dict(op='card_fixture',name=row['name'],rich=row['rich']))
                mismatch=difference(row,actual)
            elif kind=='battle':
                actual=request(dict(op='battle_full',decks=row['decks'],specializations=row['specializations'],seed=row['seed']))
                mismatch=difference(row['result'],actual)
            else:
                mismatch=None
                for i,exchange in enumerate(row['exchanges']):
                    actual=request(exchange['request'])
                    mismatch=difference(exchange['response'],actual)
                    if mismatch:
                        case['exchange']=i;case['request']=exchange['request'];break
                if not mismatch:
                    for review in row['reviews']:
                        actual=request(dict(op='review',round=review['review']['round']))
                        mismatch=difference(review,actual)
                        if mismatch:case['review_round']=review['review']['round'];break
            counts[kind]=counts.get(kind,0)+1
            if mismatch:
                case['difference']=mismatch
                if actual.get('status')=='error':case['native_error']=actual.get('error')
                failures.append(case)
                if len(failures)<=8:print('DIFF '+json.dumps(case,ensure_ascii=False),flush=True)
            if sum(counts.values())%50==0:print('PROGRESS '+json.dumps(dict(counts=counts,failures=len(failures))),flush=True)
    finally:
        process.stdin.close()
        try:process.wait(5)
        except subprocess.TimeoutExpired:process.kill()
        errors.close()
    report=dict(counts=counts,failures=failures,passed=not failures,seconds=round(time.monotonic()-started,3))
    (ROOT/f'migration/oracle/diff-{args.kind}.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print('NATIVE_DIFF_RESULT '+json.dumps(dict(counts=counts,failures=len(failures),passed=not failures,seconds=report['seconds'])),flush=True)
    return bool(failures)


if __name__=='__main__':sys.exit(main())
