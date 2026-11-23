"""Validate all receipts before comparing native RGB. Use independent gate catalog and tolerances."""
import sys, json, struct, math
from pathlib import Path
import zipfile
out=Path(__file__).resolve().parent
archive = out.parent / 'historical-frame-audit.zip'
sys.path.insert(0, str(archive))
import gate
with zipfile.ZipFile(archive) as raw:
    gate_hash = gate.sha(raw.read('gate.py'))
rows={}; pairs={}; cache={}
for p in sorted(out.glob('*.json')):
    j=json.loads(p.read_text());
    if 'native' not in j: continue
    assert j['colors']==j['positions']==j['lights']==256
    data=[]
    for key in ('ids','native'):
        a=j[key]; assert (a['run'],a['frame'])==(j['run'],j['frame']); blob=(out/a['path']).read_bytes(); assert gate.sha(blob)==a['sha256']; data.append(blob)
    b,png=data; assert len(b)==8192; v=list(struct.iter_unpack('<4f',b)); ids=[]
    for pos,col in zip(v[:256],v[256:]):
        assert all(map(math.isfinite,pos+col)) and any(pos)==any(col)
        if not any(pos): ids.append(None); continue
        hits=[i for i,(q,k) in enumerate(gate.catalog) if pos[3]==1 and max(abs(pos[j]-q[j]) for j in range(3))<1e-4 and max(abs(col[j]-k[j]) for j in range(3))<1e-3]; assert len(hits)==1,(p,len(hits)); ids.append(hits[0])
    assert len(set(ids))==256 and ids.count(None) in (0,1); rows[p.stem]=dict(nonzero=256-ids.count(None),**gate.idrow(ids)); cache[p.stem]=(ids,png)
def compare(a,b):
    x,u=cache[a]; y,v=cache[b]; X=set(x)-{None}; Y=set(y)-{None}; diff=dict(leftOnly=sorted(X-Y),rightOnly=sorted(Y-X),changedSlots=sum(i!=j for i,j in zip(x,y))); pairs[a+' vs '+b]=dict(ids=diff,rgb=gate.metrics(u,v)); print(a,b,diff,pairs[a+' vs '+b]['rgb'])
for api in (11,12):
    for t in (0,1):
        for m in ('ClusteredDeferred','ForwardPlus'): compare(f'C0-{api}-{t}-{m}',f'candidate-{api}-{t}-{m}')
        for s in ('C0','candidate'): compare(f'{s}-{api}-{t}-ClusteredDeferred',f'{s}-{api}-{t}-ForwardPlus')
assert len(rows)==20 and len(pairs)==16
(out/'results.json').write_text(json.dumps(dict(gateSHA256=gate_hash,acquireSHA256=gate.sha((out/'acquire.py').read_bytes()),analyzeSHA256=gate.sha(Path(__file__).read_bytes()),rows=rows,pairs=pairs),indent=1))
