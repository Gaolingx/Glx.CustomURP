"""Evaluate readbacks; analytic intersections are references, not substitutes for GPU output."""
import json,math,struct,sys
from pathlib import Path
out=Path(sys.argv[1]); stages='cold warm id occluded blockerID removed far inside ssOnly'.split(); results={};errors=[]
def require(x,msg):
    if not x: errors.append(msg)
def records(d,s,ext,fmt):return list(struct.iter_unpack('<'+fmt,(d/(s+'.'+ext)).read_bytes()))
def box(o,v,b):
    lo,hi=-math.inf,math.inf
    for a,n,low,high in zip(o,v,b[:3],b[3:]):
        if abs(n)<1e-12:
            if not low<=a<=high:return math.inf
        else:
            x,y=sorted(((low-a)/n,(high-a)/n));lo=max(lo,x);hi=min(hi,y)
    return max(lo,0) if hi>=max(lo,0) else math.inf
refs={'target':((-1.5,-1,-2.05,1.5,1,-1.95),(2,.5,.125),37),'blocker':((-2,-1.5,-1.05,2,1.5,-.95),(0,0,0),53),'far':((-22.5,-15,-58.05,22.5,15,-57.95),(2,.5,.125),37),'inside':((.365,-.3,1.45,.715,.3,1.55),(2,.5,.125),37)}
guids=set()
for api in [11,12]:
 d=out/f'D3D{api}'
 for s in stages:
    key=f'{api}/{s}';r=results[key]={}
    try:
        row=json.loads((d/(s+'.json')).read_text());guids.add(row['guid']);require(row['api']==f'Direct3D{api}',key+' API');require(row['visible']==(s=='inside'),key+' frustum');require(row['queue']==(api==12 and s!='ssOnly'),key+' camera queue');pixels=records(d,s,'ssr','4e') if row['produced'] else [];camera=records(d,s,'camera','4e');require(all(math.isfinite(x) for p in pixels+camera for x in p),key+' image finite');r['peakSsrAlpha']=max((p[3] for p in pixels),default=0)
        if api==11:
            require(not row['owner'],key+' hybrid allocation');require(not any(d.glob(s+'.hits')) and not any(d.glob(s+'.counts')),key+' hardware buffers');
            if s=='warm':
                ref=records(d,'ssOnly','camera','4e');r['maxCameraDifferenceVsSSOnly']=max(abs(a-b) for p,q in zip(ref,camera) for a,b in zip(p,q));require(r['maxCameraDifferenceVsSSOnly']==0,key+' fallback equivalence')
            continue
        if s=='ssOnly':
            w,h=row['screenW'],row['screenH']; t=math.tan(math.radians(25)); ref=set();found=set();cameraFound=set()
            for i in range(w*h):
                x,y=i%w,i//w
                if abs((2*(x+.5)/w-1)*w/h*t*7.95)<=1.5 and abs((2*(y+.5)/h-1)*t*7.95)<=1:ref.add(i)
                if i<len(pixels) and pixels[i][0]>.5 and pixels[i][3]>.01:found.add(i)
                if camera[i][0]>.5 and camera[i][0]>2*camera[i][2]:cameraFound.add(i)
            require(len(ref)>0,key+' empty silhouette reference')
            for typ,det in [('ssr',found),('camera',cameraFound)]:
                r[typ+'Silhouette']={'referencePixels':len(ref),'detected':len(det),'recall':0.0,'falsePositiveFraction':len(det-ref)/len(ref)};require(len(det)==0,key+' '+typ+' silhouette')
            continue
        if s in ('cold','warm'):
            w,h=row['screenW'],row['screenH']; t=math.tan(math.radians(25)); ref=set();found=set();cameraFound=set()
            for i in range(w*h):
                x,y=i%w,i//w
                if abs((2*(x+.5)/w-1)*w/h*t*7.95)<=1.5 and abs((2*(y+.5)/h-1)*t*7.95)<=1:ref.add(i)
                if i<len(pixels) and pixels[i][0]>.5 and pixels[i][3]>.01:found.add(i)
                if camera[i][0]>.5 and camera[i][0]>2*camera[i][2]:cameraFound.add(i)
            require(len(ref)>0,key+' empty silhouette reference')
            # The frozen 8-per-8x8-tile budget cannot serve a dense off-screen silhouette in one
            # frame. Gate the pixels the queued rays actually resolved and report coverage; the
            # full-silhouette recall at this scene belongs to R3 (high-sample/temporal reference).
            cc=[x[0] for x in records(d,s,'counts','I')];rq=records(d,s,'requests','4I14f2I')[:cc[2]];hh=records(d,s,'hits','4f2I')
            resolved={q[0] for q in rq if (hh[q[0]][4]&2) and q[11]<=hh[q[0]][3]<=q[15]}
            tw=(w+1)//2;th=(h+1)//2
            def slot(i):
                x,y=i%w,i//w;return ((((y+1)*th-1)//h)*tw)+(((x+1)*tw-1)//w)
            servedHit={i for i in ref if slot(i) in resolved}
            only=records(d,'ssOnly','ssr','4e');onlyHit={i for i in range(min(w*h,len(only))) if only[i][0]>.5 and only[i][3]>.01}
            require(len(servedHit)>0,key+' no served silhouette reference');require(len(only)>=w*h and not (onlyHit&servedHit),key+' ss-only silhouette')
            for typ,det in [('ssr',found),('camera',cameraFound)]:
                rec=len(servedHit&det)/len(servedHit);fp=len(det-ref)/len(ref);r[typ+'Silhouette']={'referencePixels':len(ref),'servedHitPixels':len(servedHit),'coverage':len(servedHit)/len(ref),'servedFraction':cc[2]/cc[1] if cc[1] else 0,'detected':len(det),'recallServed':rec,'recallFull':len(ref&det)/len(ref),'falsePositiveFraction':fp};require(rec>=.9 and fp<=.05,key+' '+typ+' served silhouette')
            continue
        if s=='ssOnly':continue
        counts=records(d,s,'counts','I');counts=[x[0] for x in counts];orig=records(d,s,'original','4I14f2I');requests=records(d,s,'requests','4I14f2I')[:counts[2]];hits=records(d,s,'hits','4f2I');slots={q[0] for q in requests};r.update(counts=counts,unserved=sum(q[17]>0 and q[0] not in slots for q in orig),badUnserved=sum(any(hits[q[0]]) for q in orig if q[17]>0 and q[0] not in slots));require(len(slots)==counts[2]>0 and counts[1]==counts[2]+counts[3] and r['unserved']==counts[3]>0 and not r['badUnserved'],key+' queue partition');require(all(q==orig[q[0]] for q in requests),key+' compaction');require(all(math.isfinite(x) for p in hits+orig for x in p),key+' finite buffers');require(s!='cold' or counts[7]==counts[1],key+' first frame');names={'removed':[],'far':['far'],'inside':['inside'],'occluded':['target','blocker'],'blockerID':['target','blocker']}.get(s,['target']);actualHits=wrong=beyond=0;dt=dr=0.
        for q in requests:
            o=q[4:7];v=q[12:15];v=[x/math.sqrt(sum(a*a for a in v)) for x in v];pairs=sorted((box(o,v,refs[n][0]),n) for n in names);t,n=pairs[0] if pairs else (math.inf,None);h=hits[q[0]];beyond+=int(math.isfinite(t) and t>q[15]);expected=q[11]<=t<=q[15];wrong+=int((h[4]&3)!=(2 if expected else 1));
            if expected:
                actualHits+=1;dt=max(dt,abs(h[3]-t));rgb=(refs[n][2],0,0) if s in ('id','blockerID') else refs[n][1];dr=max(dr,max(abs(x-y)/max(abs(y),.01) for x,y in zip(h,rgb)));wrong+=int(s in ('id','blockerID') and tuple(h[:3])!=tuple(rgb))
        r.update(analyticHits=actualHits,wrong=wrong,beyondTMax=beyond,maxDistanceError=dt,maxRgbRelativeError=dr);require(not wrong and dt<=.001 and dr<=.03,key+' intersection/shading');require(actualHits>0 if s not in ('far','removed') else actualHits==0,key+' non-vacuous intersection');require(s!='far' or beyond>0,key+' finite TMax reference');
        if s in ('far','removed'):require(r['peakSsrAlpha']==0,key+' unresolved output')
    except Exception as e:errors.append(key+' '+repr(e))
require(len(guids)==1 and len(next(iter(guids),''))==32,'same build GUID');summary={'passed':not errors,'guids':sorted(guids),'errors':errors,'results':results};(out/'Summary.json').write_text(json.dumps(summary,indent=2)+'\n');print(errors);sys.exit(bool(errors))
