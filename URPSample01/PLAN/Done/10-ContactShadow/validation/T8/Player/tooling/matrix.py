"""Run with Python 3 on Windows. All Unity launches use a new temporary project."""
import argparse, gzip, hashlib, json, re, shutil, subprocess, tempfile, traceback
from pathlib import Path

here = Path(__file__).resolve().parent
project = next(p for p in here.parents if (p/'ProjectSettings/ProjectVersion.txt').is_file())
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--unity', default=r'D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe')
parser.add_argument('--output', type=Path, default=here.parent/'results')
args = parser.parse_args()
out = args.output.resolve(); out.mkdir(parents=True, exist_ok=False)
scratch = Path(tempfile.mkdtemp(prefix='T8Contact-')).resolve()  # Expand Windows 8.3 aliases: Unity TypeDB keys use long assembly paths.
print('Scratch:', scratch, flush=True)
rows = []
def copy(src, dest):
    dest.parent.mkdir(parents=True, exist_ok=True)
    if src.is_dir(): shutil.copytree(src, dest)
    else: shutil.copy2(src, dest)

def run(cmd, log, timeout):
    try: return subprocess.run([*cmd,'-logFile',str(log)],cwd=scratch,timeout=timeout).returncode
    finally:
        if log.exists():
            with log.open('rb') as src, gzip.open(str(log)+'.gz','wb') as dst: shutil.copyfileobj(src,dst)
            log.unlink()

def save():
    (out/'matrix.json').write_text(json.dumps(dict(scratch=str(scratch), sourceHEAD=subprocess.check_output(['git','rev-parse','HEAD'],cwd=project,text=True).strip(), rows=rows),indent=2))
try:
    for name in ['Packages']+['ProjectSettings/'+x for x in ('ProjectVersion.txt','ProjectSettings.asset','GraphicsSettings.asset','QualitySettings.asset','EditorSettings.asset','TagManager.asset')]+['Assets/UniversalRenderPipelineGlobalSettings.asset'+x for x in ('','.meta')]+['Assets/Tests/ContactShadow/'+x+ext for x in ('Materials','Pipelines','Profiles','Scenes') for ext in ('','.meta')]: copy(project/name,scratch/name)
    copy(here/'T8PlayerProbe.cs',scratch/'Assets/T8PlayerProbe.cs'); copy(here/'link.xml',scratch/'Assets/link.xml')
    for config in ('Release','Development'):
        for v in (11,12):
            api = f'Direct3D{v}'; d = out/f'{config}-D3D{v}'; d.mkdir(); row = dict(config=config, api=api, passed=False); rows.append(row); save()
            exe = scratch/d.name/'Contact.exe'; exe.parent.mkdir(); common=['-probeConfig',config,'-probeApi',api]; print(d.name,flush=True)
            try:
                b = d/'build.json'; p = d/'player.json'
                row['editorExit']=run([args.unity,'-batchmode','-quit','-force-d3d11','-buildTarget','Win64','-projectPath',str(scratch),'-executeMethod','T8PlayerProbe.Build','-probeExe',str(exe),'-probeJson',str(b),*common],d/'build.log',3600)
                if row['editorExit']: raise RuntimeError('Unity build failed; see build.log.gz')
                build=json.loads(b.read_text()); row['buildWarnings']=build['warnings']; row['buildErrors']=build['errors']; row['exeSHA256']=hashlib.sha256(exe.read_bytes()).hexdigest()
                row['playerExit']=run([str(exe),f'-force-d3d{v}','-batchmode','-screen-fullscreen','0','-screen-width','320','-screen-height','240','-probeJson',str(p),*common],d/'player.log',240)
                player=json.loads(p.read_text()); row['passed']=row['buildErrors']==0 and row['playerExit']==0 and player['passed'] and player['buildGuid'].replace('-','').lower()==build['guid'].replace('-','').lower() and [x['actual'] for x in player['paths']]==['Forward','ForwardPlus','Deferred'] and all(x['passed'] for x in player['paths'])
            except Exception: row['failure']=traceback.format_exc()
            save()
finally:
    save()
    from diagnostics import extract
    extract(out)
if len(rows)!=4 or not all(x['passed'] for x in rows): raise SystemExit('Failure; scratch retained: '+str(scratch))
shutil.rmtree(scratch)
