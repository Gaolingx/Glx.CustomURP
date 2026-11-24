"""Windows: python repro.py [--unity path/to/Unity.exe]. No Unity invocation targets the checkout."""
import argparse, hashlib, io, json, os, re, shutil, subprocess, tempfile, zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

out = Path(__file__).resolve().parent
project = out.parents[4]
repo = project.parent
base = 'f916303c876553a0d51764a54dc95d79382f996e'
pkg = Path('Packages/com.unity.render-pipelines.universal@14.0.12')
tests = Path('Assets/Tests/RayTracing')
fixture = 'UnityEditor.Rendering.Universal.Tests.SsrRequestTests'
p = argparse.ArgumentParser()
p.add_argument('--unity', default=r'D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe')
p.add_argument('--output', type=Path, help='New result directory; defaults to a fresh temp directory')
a = p.parse_args()
results = Path(tempfile.mkdtemp(prefix='R1-results-')) if a.output is None else a.output.resolve()
if a.output is not None: results.mkdir(parents=True, exist_ok=False)
print('Review results:', results, flush=True)
scratch = Path(tempfile.mkdtemp(prefix='Glx-R1-')).resolve()
assert not scratch.is_relative_to(repo)
print('Private scratch and logs:', scratch, flush=True)
inputs = [pkg, Path('Packages/manifest.json'), Path('Packages/packages-lock.json'), Path('ProjectSettings/ProjectVersion.txt'), tests/'Unity.RayTracing.Tests.Editor.asmdef']
archive = subprocess.check_output(['git','-C',str(repo),'archive','--format=zip',base,*['URPSample01/'+s.as_posix() for s in inputs]])
zipfile.ZipFile(io.BytesIO(archive)).extractall(scratch)
work = scratch/'URPSample01'
runtime = pkg/'Runtime/RendererFeatures/ScreenSpaceLighting/Passes'
shaders = pkg/'Shaders/Utils/ScreenSpaceLighting'
files = [runtime/'SsrPass.cs', shaders/'ScreenSpaceReflections.compute', tests/'SSR.meta']
for parent, pattern in [(runtime,'SsrRequestQueue*'),(shaders,'SsrRequestQueue*'),(tests/'SSR','*')]:
    files.extend(f.relative_to(project) for f in (project/parent).glob(pattern) if f.is_file())
hashes = {}
for f in files:
    (work/f).parent.mkdir(exist_ok=True, parents=True)
    shutil.copyfile(project/f,work/f)
    hashes[f.as_posix()] = hashlib.sha256((work/f).read_bytes()).hexdigest()
# Resolve registry packages from the frozen lock using the locally installed package cache.
lock = json.loads((work/'Packages/packages-lock.json').read_bytes())['dependencies']
manifest = {'dependencies': {}}
needed = set()
def include(name):
    if name in needed: return
    needed.add(name)
    for child in lock[name]['dependencies']: include(child)
for root in ('com.unity.render-pipelines.universal','com.unity.test-framework','com.unity.ugui'):
    include(root)
# Keep built-in modules available as in B0, omit unrelated editor extensions/assets.
for name,dep in lock.items():
    if dep['source']=='builtin' or name in needed: manifest['dependencies'][name]=dep['version']
    if name in needed and dep['source'] == 'registry':
        source = project/'Library/PackageCache'/(name+'@'+dep['version'])
        if source.is_dir():
            dest = scratch/'registry'/source.name
            shutil.copytree(source,dest)
            manifest['dependencies'][name]='file:'+dest.as_posix()
(work/'Packages/manifest.json').write_text(json.dumps(manifest))
for api in (11,12):
    raw = scratch/f'{api}.xml'; log = scratch/f'{api}.log'
    run = subprocess.run([a.unity,'-batchmode','-projectPath',str(work),f'-force-d3d{api}','-runTests','-testPlatform','EditMode','-testFilter',fixture,'-testResults',str(raw),'-logFile',str(log)],timeout=1200)
    text = log.read_text(encoding='utf-8',errors='replace')
    device = re.search(r'^\s*Version:\s*(Direct3D [^\r\n]+)',text,re.M)
    gpu = re.search(r'^\s*Renderer:\s*([^\r\n]+)',text,re.M)
    root = ET.parse(raw).getroot(); cases = root.findall('.//test-case')
    assert run.returncode == 0 and root.get('result') == 'Passed' and len(cases)>=2 and all(c.get('result')=='Passed' and c.get('classname')==fixture for c in cases) and device and re.match(rf'Direct3D {api}\b',device[1]), (api, scratch)
    dest = results/f'D3D{api}'; dest.mkdir()
    safe = ET.Element('test-run',{k:root.attrib[k] for k in ('result','total','passed','failed','skipped')})
    for c in cases: ET.SubElement(safe,'test-case',{k:c.attrib[k] for k in ('fullname','result','duration')})
    ET.indent(safe); ET.ElementTree(safe).write(dest/'Tests.xml', encoding='utf-8',xml_declaration=True)
    (dest/'Summary.json').write_text(json.dumps(dict(base=base,device=device[1],gpu=gpu[1],scope='Isolated EditMode compute; injected original hits, not a camera replay or DXR dispatch.',inputs=hashes,observations=re.findall(r'R1 counts: [^\r\n<]+',raw.read_text(encoding='utf-8-sig'))),indent=2)+'\n')
    print('Passed',api,len(cases),flush=True)
