"""Read-only archive gate: python audit.py (before or after moving the plan)."""
import hashlib
import json
import re
import subprocess
from pathlib import Path
from xml.etree import ElementTree as ET

here = Path(__file__).resolve().parent
v = here.parents[1]
plan = v.parent
project = next(p for p in plan.parents if (p/'ProjectSettings/ProjectVersion.txt').is_file())
repo = Path(subprocess.check_output(['git', '-C', str(project), 'rev-parse', '--show-toplevel'], text=True).strip())

def git(*args):
    return subprocess.check_output(['git', '-C', str(repo), *args]).decode('utf-8')

def names(*args):
    return [s for s in git(*args).split('\0') if s]

def eligible(folder):
    return [repo/p for p in names('ls-files', '-co', '--exclude-standard', '-z', '--', folder.relative_to(repo).as_posix()) if (repo/p).is_file()]

def load(p):
    return json.loads(p.read_bytes())

errors = []
base = load(v/'P0/Baseline.json')['implementationHead']
files = eligible(v)
rows = load(here/'PngArchive.json')['images']
index = {}
historical_root = f'{project.name}/PLAN/TODO/10-ContactShadow/validation/'
tracked_images = {s[len(historical_root):] for s in names('ls-tree','-rz','--name-only','305d6f4',historical_root) if s.endswith('.png')}
for r in rows:
    src = v/r['canonical']
    if not src.is_file() or hashlib.sha256(src.read_bytes()).hexdigest() != r['sha256'] or src.stat().st_size != r['bytes']:
        errors.append('Canonical bytes: '+r['canonical'])
    for name in [r['canonical'], *r['aliases']]:
        # Compare the original capture, not merely two fields in this manifest.
        if name in tracked_images:
            original = subprocess.check_output(['git','-C',str(repo),'show',f'305d6f4:{historical_root}{name}'])
            if hashlib.sha256(original).hexdigest() != r['sha256']: errors.append('Historical bytes: '+name)
        if name in index or (name != r['canonical'] and (v/name).exists()):
            errors.append('Duplicate path: '+name)
        index[name] = r
if tracked_images - index.keys(): errors.append('Missing frozen images: '+str(sorted(tracked_images-index.keys())))
for mode, control in load(v/'P5/PreIntegration.json')['controls'].items():
    if index[control['archivedOff']]['sha256'] != control['offSha256']:
        errors.append('Frozen baseline: '+mode)
# Historical XML stdout is immutable; resolve its original path via the manifest.
for xml in v.glob('T6/*/Lighting.xml'):
    for node in ET.parse(xml).iter('output'):
        for line in (node.text or '').splitlines():
            if line.strip().endswith('.png') and line.strip().replace('\\','/').split('/validation/')[-1] not in index:
                errors.append('Unmapped output: '+line)
png = list(v.rglob('*.png'))
if len({hashlib.sha256(p.read_bytes()).digest() for p in png}) != len(png):
    errors.append('Duplicate PNG bytes')
png_bytes = sum(p.stat().st_size for p in png)
size = sum(p.stat().st_size for p in files)
if len(png)>80 or png_bytes>45000000 or size>50000000: errors.append('Archive budget exceeded')
if any(p.suffix.lower() in {'.log','.exe','.dll','.rdc','.gz'} for p in files): errors.append('Unexpected archive artifact')
rdc = names('ls-files','-z','*.rdc')
ignored = names('ls-files','-ic','--exclude-standard','-z','*.rdc')
rdc_bytes = sum((repo/p).stat().st_size for p in rdc)
if ignored or git('diff',base,'--name-only','--','*.rdc') or names('ls-files','-o','--exclude-standard','-z','*.rdc'): errors.append('RDC delta or ignored-but-tracked capture')
package = project/'Packages/com.unity.render-pipelines.universal@14.0.12'
code = {'.cs','.shader','.compute','.hlsl','.raytrace'}
allowed = {'ShaderLibrary/Shadows.hlsl','Shaders/Utils/ClusteredDeferred.compute'}
scanned = []; modified = []
for p in sorted(eligible(package)):
    if p.suffix not in code: continue
    rel = p.relative_to(repo).as_posix(); local = p.relative_to(package).as_posix()
    added = local.startswith(('Runtime/RendererFeatures/ContactShadows/','Editor/RendererFeatures/ContactShadows/','Tests/Editor/RendererFeatures/ContactShadows/','Shaders/Utils/ContactShadows/')) or local=='ShaderLibrary/ContactShadows.hlsl'
    if not added and local not in allowed: continue
    scanned.append(local)
    text = p.read_text('utf-8-sig') if added else '\n'.join(x[1:] for x in git('diff','-U0',base,'--',rel).splitlines() if x.startswith('+') and not x.startswith('+++'))
    if re.search(r'(?i)raytracing|raygeneration|\b(?:DXR|TLAS|TraceRay|RayQuery)\b',text): errors.append('AC13: '+local)
for record in git('diff','--no-renames','--name-status',base,'--',package.relative_to(repo).as_posix()).splitlines():
    status, name = record.split('\t',1); local = (repo/name).relative_to(package).as_posix()
    if Path(name).suffix in code:
        if status!='A': modified.append(local)
        if local not in scanned or (status!='A' and local not in allowed): errors.append('AC12: '+record)
for name in names('ls-files','-o','--exclude-standard','-z','--',package.relative_to(repo).as_posix()):
    if Path(name).suffix in code and (repo/name).relative_to(package).as_posix() not in scanned: errors.append('AC12 untracked: '+name)
# Preserve the distinction between pass counters and a real debugger/XR capture.
xml = ET.parse(v/'T2/EditMode.xml')
outputs = '\n'.join(n.text or '' for n in xml.iter('output'))
for config in ['ContactOnly','ContactSslGtao']:
    if f'{config}, Direct3D11: pass 203 execute/copy = 1/1' not in outputs: errors.append('Missing T2 counter: '+config)
for api in ['D3D11','D3D12']:
    suite = ET.parse(v/f'P3/{api}/EditMode.xml').find('.//test-suite[@name="ContactShadowMipConfirmationTests"]')
    if suite is None or suite.get('result')!='Passed' or suite.get('passed')!='5': errors.append('Missing mip-confirmation proof: '+api)
links = 0
for p in plan.rglob('*.md'):
    for url in re.findall(r'\]\(([^\s)]+)\)',p.read_text('utf-8-sig')):
        if url.startswith(('#','https:','http:','mailto:')): continue
        links += 1
        if not (p.parent/url.split('#')[0]).exists(): errors.append(f'Broken link {p.name}: {url}')
old = 'TODO/10-ContactShadow'
for p in files+eligible(project/'Assets/Tests/ContactShadow')+[package/s for s in scanned]:
    if p.suffix in {'.ps1','.cs','.py'} and p.resolve()!=Path(__file__).resolve() and old in p.read_text('utf-8-sig').replace('\\','/'):
        errors.append('Hardcoded TODO in '+p.name)
print(json.dumps(dict(originalPngPaths=len(index),pngCount=len(png),pngBytes=png_bytes,validationFiles=len(files),validationBytes=size,trackedRdc=len(rdc),rdcBytes=rdc_bytes,ignoredTrackedRdc=len(ignored),implementationBase=base,ac12Modified=modified,ac13Files=len(scanned),localMarkdownLinks=links,errors=errors),indent=2))
raise SystemExit(bool(errors))
