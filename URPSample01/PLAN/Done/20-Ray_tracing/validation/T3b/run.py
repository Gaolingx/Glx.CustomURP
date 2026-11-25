"""Build the T3b Player; retain the scratch project and logs for inspection."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
from uuid import UUID

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[4]
PKG = Path('Packages/com.unity.render-pipelines.universal@14.0.12')
BASE = '9a950bda81082c2339fe70f3333dc398a8153823'
UNITY = Path(os.environ.get('UNITY_EDITOR', 'D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe'))
KEYS = {'_NORMALMAP', '_METALLICSPECGLOSSMAP', '_EMISSION', '_ALPHATEST_ON'}


def save(path, data):
    path.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prepare(scratch):
    inputs = ['Packages', 'ProjectSettings/ProjectVersion.txt', 'ProjectSettings/ProjectSettings.asset',
              'ProjectSettings/GraphicsSettings.asset', 'ProjectSettings/QualitySettings.asset',
              'Assets/Settings', 'Assets/Settings.meta', 'Assets/UniversalRenderPipelineGlobalSettings.asset',
              'Assets/UniversalRenderPipelineGlobalSettings.asset.meta']
    archive = scratch/'base.tar'
    subprocess.run(['git', '-C', PROJECT.parent, 'archive', '-o', archive, BASE,
                    *(f'{PROJECT.name}/{s}' for s in inputs)], check=True)
    with tarfile.open(archive) as tf:
        tf.extractall(scratch, filter='data')
    archive.unlink()
    proj = scratch/PROJECT.name
    manifest = proj/'Packages/manifest.json'
    deps = json.loads(manifest.read_text())['dependencies']
    keep = {'com.unity.render-pipelines.universal', 'com.unity.ugui', 'com.unity.test-framework'}
    save(manifest, {'dependencies': {k:v for k,v in deps.items() if k in keep or k.startswith('com.unity.modules.')}})
    assets = proj/'Assets'
    (assets/'Fixture').mkdir()
    (assets/'Editor').mkdir()
    save(assets/'Fixture/T3b.asmdef', {'name':'Unity.RayTracing.Tests.Editor', 'references':[
        'Unity.RenderPipelines.Universal.Runtime','Unity.RenderPipelines.Core.Runtime']})
    (scratch/'Build').mkdir()
    return proj


def build_run(scratch, out):
    proj = scratch/PROJECT.name
    assert (proj/'Assets/Fixture/T3b.asmdef').is_file()
    copies = {}
    for rel in ['Shaders/Lit.shader', 'Editor/ShaderScriptableStripper.cs',
                'Runtime/RendererFeatures/RayTracing/RayTracingSceneInstances.cs']:
        copies[PROJECT/PKG/rel] = proj/PKG/rel
    for f in (PROJECT/PKG/'Shaders/RayTracing').iterdir():
        copies[f] = proj/f.relative_to(PROJECT)
    for name in ('T3bPlayer.cs','T3bRaster.cs','T3bRaster.shader','T3bProbe.raytrace','T3bVariantAudit.cs'):
        sub = 'Editor' if name.endswith('Audit.cs') else 'Fixture' if name.endswith('.cs') else ''
        copies[HERE/name] = proj/'Assets'/sub/name
    for src, dst in copies.items():
        shutil.copy2(src, dst)
    save(out/'Inputs.json', {'base':BASE,'runnerSha256':sha(Path(__file__)), 'inputSha256':{
        dst.relative_to(proj).as_posix():sha(dst) for dst in copies.values()}})
    exe = scratch/'Build/T3b.exe'
    log = out/'Build.log'
    audit = out/'variant-audit.txt'
    subprocess.run([UNITY,'-batchmode','-quit','-force-d3d12','-buildTarget','Win64','-projectPath',proj,
                    '-executeMethod','T3bPlayer.Build','-exe',exe,'-logFile',log],
                   env={**os.environ,'T3B_VARIANTS':str(audit)}, check=True, timeout=3600)
    # Preserve the read-only post-strip audit; it must never add a variant to the build.
    lines = audit.read_text().splitlines() if audit.exists() else []
    sets = sorted(set(' '.join(sorted(line[4:].split())) for line in lines if line.startswith('set ')))
    if any(set(s.split()) - KEYS for s in sets):
        raise RuntimeError(f'Unexpected Lit DXR keyword set: {sets}')
    save(out/'Variants.json', {'pass':'Universal Render Pipeline/Lit:GlxDXR','keywordSets':sets,
        'distinctSets':len(sets),'callbacks':[s for s in lines if s.startswith('pass ')]})
    passed = True
    guids = []
    for api in (11,12):
        result = out/f'Player-D3D{api}.json'
        code = subprocess.run([exe,'-profiler-enable','-screen-fullscreen','0','-screen-width','320','-screen-height','240',
            f'-force-d3d{api}','-expectedApi',f'Direct3D{api}','-result',result,'-logFile',out/f'D3D{api}.log'],timeout=240).returncode
        d = json.loads(result.read_text(encoding='utf-8-sig'))
        d.update(exitCode=code, inputsSha256=sha(out/'Inputs.json'))
        save(result,d)
        passed &= code == 0 and d['passed'] and d['development'] and d['api'] == f'Direct3D{api}'
        if api == 12:
            passed &= d['gpuTimingMeasured'] and d['markerGpuBlocks'] == 5 and len(d['vectors']) == 63
        guids.append(UUID(d['guid']))
    if not (passed and guids[0].int and guids[0]==guids[1] and len(sets)==16):
        raise RuntimeError(f'T3b validation failed: {out}')
    scene, = (proj/'Assets').glob('*.unity')
    control = scratch/'D3D11Only/T3b.exe'
    control.parent.mkdir(exist_ok=True)
    control_log = out/'D3D11OnlyBuild.log'
    control_audit = out/'variant-audit-d3d11.txt'
    subprocess.run([UNITY,'-batchmode','-quit','-force-d3d11','-buildTarget','Win64','-projectPath',proj,
        '-executeMethod','T3bVariantAudit.BuildD3D11Only','-logFile',control_log],
        env={**os.environ,'T3B_VARIANTS':str(control_audit),'T3B_SCENE':scene.relative_to(proj).as_posix(),
             'T3B_EXE':str(control)},check=True,timeout=3600)
    result = out/'D3D11Only.json'
    code = subprocess.run([control,'-force-d3d11','-expectedApi','Direct3D11','-result',result,
        '-screen-fullscreen','0','-screen-width','320','-screen-height','240','-logFile',out/'D3D11Only.log'],timeout=240).returncode
    d = json.loads(result.read_text(encoding='utf-8-sig'))
    sets11 = [s for s in control_audit.read_text().splitlines() if s.startswith('set ')] if control_audit.exists() else []
    passes = [m['pass'] for m in d['materials']]
    d.update(exitCode=code, retainedKeywordSets=sets11, inputsSha256=sha(out/'Inputs.json'))
    save(result,d)
    if code != 0 or not d['passed'] or sets11 or not passes or any(i >= 0 for i in passes) or d['api'] != 'Direct3D11' or UUID(d['guid']) == guids[0]:
        raise RuntimeError('D3D11-only control failed')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--scratch-parent',type=Path,default=Path('E:/tmp'))
    parser.add_argument('--reuse',type=Path,help='reuse only an isolated T3b scratch project created by this runner')
    parser.add_argument('--out',type=Path,required=True)
    args = parser.parse_args()
    out = args.out.resolve()
    out.mkdir(parents=True,exist_ok=False)
    scratch = args.reuse.resolve() if args.reuse else Path(tempfile.mkdtemp(prefix='T3b-',dir=args.scratch_parent))
    print('Scratch:',scratch,'Reports:',out,flush=True)
    if not args.reuse:
        prepare(scratch)
    build_run(scratch,out)
