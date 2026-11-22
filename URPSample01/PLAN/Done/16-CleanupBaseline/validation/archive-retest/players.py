from pathlib import Path
import subprocess, json, hashlib, sys
from datetime import datetime, timezone
out=Path(sys.argv[1]).resolve(); out.mkdir(exist_ok=True)
project=Path(__file__).resolve().parents[5]; builds=project/'Builds'
for api,group,rep in [(a,g,'matrix') for a in (11,12) for g in ('control','cluster-on','t4')]+[(11,g,'repeat') for g in ('control','cluster-on')]+[(11,'cap',r) for r in ('a','b','c')]:
    d=out/f'{rep}-d3d{api}-{group}'; d.mkdir()
    t4=group=='t4'; exe=builds/('ClusteredDeferredArenaC4T4' if t4 else 'ClusteredDeferredArenaC4')/f'Direct3D{api}'/('CdArenaT4.exe' if t4 else 'CdArena.exe'); counts='255,256' if group=='cap' else '0,8,48,256'
    cmd=[str(exe),f'-force-d3d{api}','--cd-output',str(d),'-logFile',str(d/'Player.log')]+(['--cd-t4'] if t4 else ['--cd-total-lights','256','--cd-light-counts',counts,'--cd-screenshot-lights',counts]+(['--cd-transparent'] if group=='cluster-on' else []))
    p=subprocess.Popen(cmd); row=dict(command=cmd,pid=p.pid,utc=datetime.now(timezone.utc).isoformat(),sha256={})
    for f in (exe,exe.parent/(exe.stem+'_Data')/'globalgamemanagers',*(exe.parent/(exe.stem+'_Data')/'Managed').glob('*Universal.Runtime.dll'),exe.parent/(exe.stem+'_Data')/'Managed/Assembly-CSharp.dll'):
        row['sha256'][str(f)]=hashlib.sha256(f.read_bytes()).hexdigest()
    try: row['exit']=p.wait(timeout=240)
    except subprocess.TimeoutExpired: p.kill();row['timeout']=True;row['exit']=p.wait()
    (d/'process.json').write_text(json.dumps(row,indent=2)); print(d.name,row['exit'],flush=True)
