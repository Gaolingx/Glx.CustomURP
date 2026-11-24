"""Win64 T2 Editor regression, optionally --player for an isolated development build."""
import argparse, hashlib, json, os, subprocess, tempfile, xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[4]
UNITY = Path(os.environ.get('UNITY_EDITOR', 'D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe'))
LOCAL = Path(tempfile.mkdtemp(prefix='T2-'))
PKG = 'com.unity.render-pipelines.universal@14.0.12'
TEST = PROJECT/'Assets/Tests/RayTracing/TLAS'
NS = 'UnityEditor.Rendering.Universal.Tests.'

def hashes(folder):
    return {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in folder.iterdir() if p.is_file()}

def unity(project, api, *args):
    log = LOCAL/(project.name+str(api)+'.log')
    print('Unity diagnostic (local only):', log, flush=True)
    return subprocess.run([UNITY,'-batchmode','-projectPath',project,'-logFile',log,f'-force-d3d{api}',*args]).returncode

def editor(out, api):
    xml = LOCAL/f'Tests{api}.xml'
    classes = [NS+'RayTracingSceneGateTests', NS+'RayTracingT2Tests']
    settings = PROJECT/'ProjectSettings'
    before = hashes(settings)
    # Unity can serialize these during startup. Do not touch another job's inputs.
    names = ['GraphicsSettings.asset','QualitySettings.asset','ProjectSettings.asset','EditorSettings.asset']
    backup = {n:(settings/n).read_bytes() for n in names}
    try:
        code = unity(PROJECT, api,'-runTests','-testPlatform','EditMode','-testFilter',';'.join(classes),'-testResults',xml)
    finally:
        after = hashes(settings)
        for n,b in backup.items():
            if after.get(n)!=before[n]: (settings/n).write_bytes(b)
        restored = hashes(settings)==before
    root = ET.parse(xml).getroot(); cases = root.findall('.//test-case')
    (out/f'Restoration-D3D{api}.json').write_text(json.dumps({'before':before,'changedDuringRun':sorted(n for n in after if after[n]!=before.get(n)), 'restored':restored},indent=2)+'\n')
    # Export only names, outcomes and fixture output. Omit command line/environment/Editor log.
    review = ET.Element('tests',api=str(api),exitCode=str(code),settingsRestored=str(restored))
    for case in cases:
        item=ET.SubElement(review,'case',name=case.get('fullname'),result=case.get('result'))
        for name in ('output','failure/message'):
            if case.findtext(name): ET.SubElement(item,name.split('/')[-1]).text=case.findtext(name)
    ET.indent(review); ET.ElementTree(review).write(out/f'Editor-D3D{api}.xml',encoding='utf-8')
    assert code==0 and restored and root.get('result')=='Passed' and {c.get('classname') for c in cases}==set(classes) and all(c.get('result')=='Passed' for c in cases)
    assert ('api=Direct3D'+str(api)) in ''.join(review.itertext())

if __name__=='__main__':
    p=argparse.ArgumentParser(); p.add_argument('--out',type=Path,required=True); p.add_argument('--api',type=int,nargs='+',default=[11,12]); p.add_argument('--player',action='store_true'); a=p.parse_args(); a.out.mkdir(parents=True,exist_ok=True)
    if a.player:
        from player import run
        run(a.out.resolve())
    else:
        for api in a.api: editor(a.out.resolve(),api)
