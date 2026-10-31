"""Bounded adversarial-command parity against the reviewed JSON authority."""
from pathlib import Path
import copy,json,os,queue,subprocess,threading,shutil,sys

ROOT=Path(__file__).resolve().parents[2]
HERE=Path(__file__).parent
PYTHON=Path(os.environ.get('MAGICIANS_REFERENCE_PYTHON',sys.executable))
NATIVE=[os.environ.get('DOTNET_EXE',shutil.which('dotnet') or 'dotnet'),str(ROOT/'migration/NativeRunner/bin/Debug/net8.0/NativeRunner.dll'),str(ROOT)]
ENV=dict(os.environ,PYTHONUTF8='1',PYTHONIOENCODING='utf-8',PYTHONHASHSEED='0')

class Worker:
    def __init__(self,command,name):
        self.errors=(HERE/(name+'-protocol-stderr.log')).open('w',encoding='utf-8')
        self.p=subprocess.Popen(command,cwd=ROOT,env=ENV,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=self.errors,text=True,encoding='utf-8',bufsize=1)
        self.lines=queue.Queue()
        def read():
            for line in self.p.stdout:self.lines.put(line)
            self.lines.put(None)
        self.thread=threading.Thread(target=read,daemon=True);self.thread.start()
    def send(self,command):
        self.p.stdin.write(json.dumps(command,ensure_ascii=False)+'\n');self.p.stdin.flush()
        line=self.lines.get(timeout=120)
        if line is None:raise RuntimeError('Worker exited: '+str(self.p.poll()))
        return json.loads(line)
    def close(self):
        self.p.stdin.close()
        try:self.p.wait(timeout=5)
        except subprocess.TimeoutExpired:self.p.kill();self.p.wait()
        self.errors.close()

rows=[]
def difference(a,b,path=''):
    if type(a)!=type(b):
        if isinstance(a,(int,float)) and isinstance(b,(int,float)) and a==b:return None
        return(path,a,b)
    if isinstance(a,dict):
        if set(a)!=set(b):return(path,'keys',set(a)^set(b))
        for k in a:
            error=difference(a[k],b[k],path+'/'+str(k))
            if error:return error
    elif isinstance(a,list):
        if len(a)!=len(b):return(path,'length',len(a),len(b))
        for i,(aa,bb) in enumerate(zip(a,b)):
            error=difference(aa,bb,path+'/'+str(i))
            if error:return error
    elif a!=b:return(path,a,b)
    return None

python=Worker([str(PYTHON),'-u','-m','tools.client_v12.match'],'python')
native=Worker(NATIVE,'native')
current=None
def request(command,label,outer_error=False):
    global current
    expected=python.send(command);actual=native.send(command)
    if outer_error:
        assert expected.get('status')==actual.get('status')=='error',(label,expected,actual)
        assert expected['error'] in actual['error'],(label,expected['error'],actual['error'])
    else:
        error=difference(expected,actual)
        if error:
            (HERE/'invalid-protocol-difference.json').write_text(json.dumps(dict(label=label,command=command,expected=expected,actual=actual),ensure_ascii=False,indent=2),encoding='utf-8')
            raise AssertionError((label,error))
        current=expected
    rows.append(dict(label=label,command=command,status=expected.get('status'),decision=expected.get('decision',{}).get('kind'),error=expected.get('error'),revision=expected.get('revision')))
    return expected
def send(command,label,**kwargs):return request(dict(command,revision=current['revision']),label,**kwargs)
try:
    request(dict(op='start',seed=334003),'start')
    request(dict(op='review',round=1),'unfinished-review',outer_error=True)
    before=copy.deepcopy(current)
    send(dict(kind='buy',slot=100),'invalid-slot-100')
    assert current['own']==before['own'] and current['decision']==before['decision']
    stale_revision=current['revision']-1
    request(dict(kind='end_face',revision=stale_revision),'stale-revision',outer_error=True)
    free=next(c for c in current['decision']['offers'] if c['price']==0)
    send(dict(kind='buy',slot=free['slot']),'free-purchase-after-stale')
    before=copy.deepcopy(current)
    send(dict(kind='buy',slot=free['slot']),'repeat-purchased-slot')
    assert current['own']==before['own'] and current['decision']==before['decision']
    paid=next(c for c in current['decision']['offers'] if c['price']==1)
    send(dict(kind='buy',slot=paid['slot']),'paid-purchase')
    assert current['own']['points']==1 and current['decision']['can_refresh']
    before=copy.deepcopy(current)
    send(dict(kind='invented_command'),'unknown-kind-in-draft')
    assert current['own']==before['own'] and current['decision']==before['decision']
    send(dict(kind='end_face'),'end-first-face')
    assert current['decision']['kind']=='refresh'
    send(dict(kind='refresh'),'one-point-refresh')
    assert current['decision']['kind']=='draft' and current['own']['points']==0 and not current['decision']['can_refresh']
    before=copy.deepcopy(current)
    send(dict(kind='buy',slot=100),'invalid-buy-after-refresh')
    assert current['own']==before['own'] and current['decision']==before['decision']
    send(dict(kind='end_face'),'end-second-face')
    assert current['decision']['kind']=='play'
    before=copy.deepcopy(current)
    foreign=f'{1-current["combat"]["human_seat"]}:0'  # a real opposing instance, never an own candidate
    send(dict(kind='play',uid=foreign),'foreign-uid-play')
    assert current['own']==before['own'] and current['decision']==before['decision'] and current['combat']==before['combat']
    request(dict(kind='play',uid=None,revision=current['revision']-1),'stale-play-revision',outer_error=True)
    selection_checked=False
    for step in range(22):
        decision=current['decision'];kind=decision['kind']
        if kind=='select':
            before=copy.deepcopy(current)
            send(dict(kind='select',uids=[foreign]),'foreign-uid-selection')
            assert current['own']==before['own'] and current['decision']==before['decision'] and current['combat']==before['combat']
            required=current['decision']['count']
            targets=[c['uid'] for c in current['decision']['candidates'][:required]]
            send(dict(kind='select',uids=targets),'valid-selection-after-rejection')
            selection_checked=True;break
        if kind=='play':
            legal=[c for c in decision['hand'] if c['payable_cost']<=decision['energy']]
            # Prefer the starter search card to exercise a real choice window.
            legal.sort(key=lambda c:(c['name']!='检索附魔',c['name'] not in ('通晓','包扎','护盾强化')))
            send(dict(kind='play',uid=legal[0]['uid'] if legal else None),'manual-play-'+str(step))
        elif kind=='round':send(dict(kind='next_round'),'next-round-for-selection')
        elif kind=='draft':send(dict(kind='end_face'),'end-draft-for-selection')
        elif kind=='refresh':send(dict(kind='keep_face'),'keep-face-for-selection')
        else:raise AssertionError(('unexpected decision',kind))
    assert selection_checked,'Did not reach a selection window within bounded 22 legal actions'
    finished=send(dict(kind='delegate_match'),'finish-after-invalid-inputs')
    assert finished['status']=='finished' and len(finished['placements'])==4
    request(dict(op='review',round=1),'review-after-finish')
    request(dict(kind='play',uid=None,revision=current['revision']),'command-after-finish',outer_error=True)
    report=dict(status='passed',requests=len(rows),seed=334003,checks=rows)
    (HERE/'invalid-protocol-result.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print('INVALID_PROTOCOL_PARITY_OK requests='+str(len(rows))+' slot=OK stale-revision=OK refresh=OK foreign-UID=OK recovery=OK finished=OK')
finally:
    python.close();native.close()
