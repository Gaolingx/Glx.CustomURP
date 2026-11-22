from pathlib import Path
import json,sys,struct,colorsys,itertools,runpy
from PIL import Image, ImageChops
D=Path(__file__).resolve().parent; C3=D.parent/'c3'; C4=D.parents[1]/'c4'; rows=[];cross=[]
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def diff(a,b,label):
    with Image.open(a) as x, Image.open(b) as y:
        assert x.size==y.size==(1280,720); rgb=ImageChops.difference(x.convert('RGB'),y.convert('RGB'))
    r,g,bl=rgb.split(); mx=ImageChops.lighter(ImageChops.lighter(r,g),bl); h=rgb.histogram(); peak=mx.getextrema()[1];mean=sum((i%256)*n for i,n in enumerate(h))/sum(h);over=sum(mx.histogram()[25:]);rows.append(dict(group=label,a=str(a.relative_to(D.parents[1])),b=str(b.relative_to(D.parents[1])),peak=peak,mean=mean,over24=over,bbox=mx.point(lambda v:255 if v>24 else 0).getbbox(),rawPass=peak<=24 and mean<=2));return rows[-1]
def checks(d,t):
    j=read(d/('T4FeatureMatrix.json' if t else 'CdPerf.json'));assert not j['error'];return ({e['feature']+'/'+c['name']:c for e in j['entries'] for c in e['comparisons'] if c['name'].startswith('forward_plus_vs_clustered_')} if t else {str(c['lightCount']):c for c in j['forwardPlusDiffs']})
for d in sorted(D.glob('matrix-*'))+sorted(D.glob('repeat-*')):
    rep,api,g=d.name.split('-',2);t=g=='t4'; rel=f't4/player-{api}' if t else f't5-{api}/{g}'; new=checks(d,t);assert len(new)==(14 if t else 4)
    for root in (C3,C4/'final-player'):
        ref=root/rel;old=checks(ref,t);assert new.keys()==old.keys()
        for k,v in new.items():cross.append(dict(run=d.name,reference=str(ref.relative_to(D.parents[1])),key=k,old=old[k],new=v))
        for p in ref.glob('*.png'):diff(ref/p.name,d/p.name,rep+'/'+root.name)
    if rep=='repeat':
        for p in d.glob('*.png'):diff(d.with_name(d.name.replace('repeat','matrix'))/p.name,p,'repeat/matrix')
for a,b in itertools.combinations([C3/'t5-d3d11-cap-a',C3/'t5-d3d11-cap-b',C4/'cap-control',*sorted(D.glob('*-d3d11-cap'))],2):
    for p in a.glob('*.png'):diff(p,b/p.name,'cap')
ids={};f32=lambda x:struct.unpack('<f',struct.pack('<f',x))[0]; linear=lambda x:((x+.055)/1.055)**2.4 if x>.04045 else x/12.92
for d in sorted(D.glob('id-*')):
    vs=list(struct.iter_unpack('<4f',(d/'light-buffer.bin').read_bytes()));ordered=[];errors=[]
    for i,(x,y,z,w) in enumerate(vs[:256]):
        if w==0:assert vs[i]==(0,0,0,0) and vs[256+i]==(0,0,0,0);continue
        candidates=[]
        for j in range(256):
            pos=(-14+j%8*4,1.6+((j//8)%2)*3,-10+((j//16)%4)*6.6); hue=f32(j*f32(.61803398875))%1; col=[linear(c)*(12 if j%3==2 else 8) for c in colorsys.hsv_to_rgb(hue,.55,1)];err=max(abs(a-b) for a,b in zip(col,vs[256+i][:3]));
            if max(abs(a-b) for a,b in zip(pos,(x,y,z)))<.0001 and err<.001:candidates.append((j,err))
        assert len(candidates)==1,(i,candidates); j,err=candidates[0];ordered.append(j);errors.append(err)
    assert len(ordered)==len(set(ordered))==255;ids[d.name]=dict(orderedFieldLightIndices=ordered,missing=sorted(set(range(256))-set(ordered)),maxColorError=max(errors))
    for base in [C3/'t5-d3d11/control',C4/'final-player/t5-d3d11/control',D/'matrix-d3d11-control',D/'repeat-d3d11-control']:
        diff(base/'ForwardPlus_L256.png',d/'rt_1147_0.png','capture/player')
for a,b in itertools.combinations(sorted(D.glob('id-*')),2):diff(a/'rt_1147_0.png',b/'rt_1147_0.png','capture/pair')
for name,obj in [('images',rows),('cross-route',cross),('gpu-ids',ids)]:
    with (D/(name+'.json')).open('x') as f:json.dump(obj,f,indent=2)
