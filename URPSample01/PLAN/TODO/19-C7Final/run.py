"""C7 serial acquisition; requires fresh scratch directory, copied C0 players and C5 isolation projects."""
import subprocess as sp, sys, json, datetime, threading
from pathlib import Path
root=Path('E:/tmp/C7Final'); project=Path('E:/tmp/C5-tests'); unity=Path('D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe')
trace=root/'runs.jsonl'
def now():return datetime.datetime.now().astimezone().isoformat()
def run(name,cmd,perf=False):
    d=root/name; d.mkdir(exist_ok=False); args=list(map(str,cmd))+['-logFile',str(d/'unity.log')]; event={'name':name,'start':now(),'args':args,'compilerObservations':[]}; stop=threading.Event()
    def watch():
        while not stop.is_set():
            names=sp.check_output(['tasklist','/FO','CSV','/NH']).decode(errors='replace').splitlines(); procs=[x for x in names if x.lower().startswith(('"unity.exe','"unityshadercompiler','"bee_backend','"csc.exe'))]; event['compilerObservations'].append([now(),procs]);stop.wait(2)
    if perf:t=threading.Thread(target=watch);t.start()
    try:event['exit']=sp.run(args,cwd=d,timeout=900).returncode
    finally:
        stop.set();
        if perf:t.join()
        event['end']=now();trace.open('a').write(json.dumps(event)+'\n')
if __name__=='__main__' and '--paired' in sys.argv:
 # Never use the legacy path Builds/ClusteredDeferredArena as proof of C0 identity.
 frozen=Path('D:/C7-warm0/URPSample01'); assert all((frozen/f'Builds/ClusteredDeferredArena/Direct3D{i}/CdArena.exe').is_file() for i in [11,12])
 for api in [11,12]:
  for rep in range(3):
   for v in (['frozen','final'] if rep!=1 else ['final','frozen']):
    for trans in [False,True]:
     name=f'{v}-{api}-{rep}-{int(trans)}'; player=(frozen/'Builds/ClusteredDeferredArena' if v=='frozen' else Path('E:/tmp/C5-verify/Builds/ClusteredDeferredArenaC4'))/f'Direct3D{api}/CdArena.exe'; run(name,[player,f'-force-d3d{api}','--cd-total-lights','256','--cd-light-counts','0,8,48,256','--cd-screenshot-lights','0,8,48,256','--cd-samples','150','--cd-output',root/name]+(['--cd-transparent'] if trans else []),True)
 for api in [11,12]:
  name=f'T12-frozen-{api}';run(name,[unity,'-batchmode',f'-force-d3d{api}','-projectPath',frozen,'-executeMethod','ScreenSpaceLightingT12Verification.RunT12Verification','-ssl-t12-output',root/name])
elif __name__=='__main__' and '--initial' in sys.argv:
 # Acquisition history: C0-* in the FIRST sweep was later disproven (no StencilDeferred mode).
 for api in [11,12]:
    for rep in range(3):
     for v in (['C0','C5'] if rep!=1 else ['C5','C0']):
      for trans in [False,True]:
       name=f'{v}-{api}-{rep}-{int(trans)}'; player=(root/'C0' if v=='C0' else Path('E:/tmp/C5-verify/Builds/ClusteredDeferredArenaC4'))/f'Direct3D{api}/CdArena.exe'; run(name,[player,f'-force-d3d{api}','--cd-total-lights','256','--cd-light-counts','0,8,48,256','--cd-screenshot-lights','0,8,48,256','--cd-samples','150','--cd-output',root/name]+(['--cd-transparent'] if trans else []),True)
 for api in [11,12]:
    name=f'T12-{api}';run(name,[unity,'-batchmode',f'-force-d3d{api}','-projectPath',project,'-executeMethod','ScreenSpaceLightingT12Verification.RunT12Verification','-ssl-t12-output',root/name]);name=f'T4build-{api}';run(name,[unity,'-batchmode',f'-force-d3d{api}','-projectPath',project,'-executeMethod',f'ClusteredDeferredArenaBuild.BuildT4D3D{api}AndExit']);player=project/f'Builds/ClusteredDeferredArenaC4T4/Direct3D{api}/CdArenaT4.exe';name=f'T4-{api}';
    if player.exists():run(name,[player,f'-force-d3d{api}','--cd-output',root/name,'--cd-t4'])
