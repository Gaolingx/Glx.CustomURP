"""Recreate compact C7 image/performance evidence from acquisition scratch and archived C0 refs."""
from pathlib import Path
import csv,zipfile,hashlib,json,statistics,sys,collections
sys.dont_write_bytecode=True
from compare import compare,sha
o=Path(__file__).resolve().parent;r=o.parents[1];tmp=Path('E:/tmp/C7Final');base=next(r.glob('Done/16*/validation/baseline')); frozen=Path('D:/C7-warm0/URPSample01');project=Path('D:/C7-warm5/URPSample01')
rows=[];manifest={};seen=set();perf=[];stats={};runs=[json.loads(l) for l in (tmp/'runs.jsonl').read_text().splitlines()];paired=[x for x in runs if x['name'].startswith(('frozen-','final-'))];assert len(paired)==24 and all(x['compilerObservations'] and not any(y for t,y in x['compilerObservations']) for x in paired)
def table(path,header,rows):
 with path.open('w',newline='',encoding='utf8') as f:w=csv.writer(f);w.writerow(header);w.writerows(rows)
with zipfile.ZipFile(o/'measurements.zip','w',zipfile.ZIP_DEFLATED) as z:
 def img(key,p):
  h=sha(p);manifest[key]=h
  if h not in seen:z.write(p,'png/'+h+'.png');seen.add(h)
 def pair(group,api,state,a,b):
  assert a.name==b.name;rows.append([group,api,state,b.name,*compare(a,b)]);img(f'{group}/reference/{api}/{state}/{a.name}',a);img(f'{group}/candidate/{api}/{state}/{b.name}',b)
 for api in (11,12):
  d=tmp/f'T4-{api}';j=json.loads((d/'T4FeatureMatrix.json').read_text());assert len(j['entries'])==7 and j['graphicsDeviceType']==f'Direct3D{api}';z.write(d/'T4FeatureMatrix.json',f'T4/{api}/T4FeatureMatrix.json');
  for b in d.glob('*.png'):
   a=base/f't4/player-d3d{api}'/b.name
   if a.exists():pair('T4-C0',api,'',a,b)
   pair('T4-matched-5ab7626',api,'',base.parent/f'c3/t4-baseline/player-d3d{api}'/b.name,b)
  for v in ['baseline','final']:
   d=tmp/(f'T12-frozen-{api}' if v=='baseline' else f'T12-{api}');j=json.loads((d/'T12Verification.json').read_text());assert j['completed'] and j['graphicsDeviceType']==f'Direct3D{api}';z.write(d/'T12Verification.json',f'T12/{v}/{api}/T12Verification.json');stats[f'T12-{v}-{api}']=[(m['name'],sum(x['passed'] for x in m['cases']),len(m['cases'])) for m in j['modes']]
  for b in sorted((tmp/f'T12-{api}').glob('*.png')):pair('T12-C0',api,'',tmp/f'T12-frozen-{api}'/b.name,b)
  for t in [0,1]:
   for b in sorted((tmp/f'final-{api}-0-{t}').glob('*.png')):pair('T5-C0',api,t,base/f't5-d3d{api}'/('cluster-on' if t else 'control')/b.name,b)
   for mode in ['ClusteredDeferred','ForwardPlus']:
    for l in [0,8,48,256]:
     for metric in ['CpuMedianMs','GpuMedianMs']:
      vals=[]
      for v in ['frozen','final']:
       series=[]
       for rep in range(3):
        name=f'{v}-{api}-{rep}-{t}';d=tmp/name;j=json.loads((d/'CdPerf.json').read_text());assert j['completed'] and j['graphicsDeviceType']==f'Direct3D{api}' and j['width']==1280 and j['height']==720 and j['transparentFixture']==bool(t) and j['warmupFrames']==40 and j['sampleFrames']==150;assert ('StencilDeferred' in [m['name'] for m in j['modes']])==(v=='frozen');rec=next(x for x in csv.DictReader((d/'CdPerf.csv').open()) if x['Mode']==mode and int(x['LightCount'])==l);assert int(rec['GpuSamples'])>0;series.append(float(rec[metric]))
       vals.append(series)
      delta=statistics.median(vals[1])-statistics.median(vals[0]);perf.append([api,t,mode,l,metric,*vals[0],*vals[1],round(delta,6),delta<=0]);
  for p in [*frozen.glob(f'PLAN/**/CdArenaBuildSummary-Direct3D{api}.json'),*project.glob(f'PLAN/**/CdArenaT4BuildSummary-Direct3D{api}.json')]:z.write(p,'build/'+p.name)
 for x in paired:
  d=tmp/x['name'];
  for p in [d/'CdPerf.csv',d/'CdPerf.json']:z.write(p,'perf/'+x['name']+'/'+p.name)
 z.writestr('image-manifest.json',json.dumps(manifest,indent=1));z.write(tmp/'runs.jsonl','runs.jsonl')
table(o/'matrix.csv',['comparison','api','transparent','file','refSHA','finalSHA','pixels','meanRGB8','peakRGB8','pxGT8','fractionGT8','meanPeakPass'],rows);table(o/'performance.csv',['api','transparent','route','lights','metric','C0-1','C0-2','C0-3','C5-1','C5-2','C5-3','deltaMs','nonSlower'],perf);stats['matrices']={str(k):v for k,v in collections.Counter((x[0],x[1],x[-1]) for x in rows).items()};stats['perf']={str(k):v for k,v in collections.Counter((x[4],x[-1]) for x in perf).items()};print(json.dumps(stats,indent=2))
