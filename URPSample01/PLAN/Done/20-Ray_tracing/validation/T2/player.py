import hashlib, json, os, shutil, subprocess, tarfile, tempfile
from pathlib import Path
from run import HERE, PROJECT, PKG, TEST, UNITY


def run(out):
    base='f916303'; scratch=Path(tempfile.mkdtemp(prefix='T2Player-', dir=os.environ.get('T2_SCRATCH_PARENT')))
    print('Isolated project/build logs (local only):', scratch, flush=True)
    proj=scratch/'URPSample01'; archive=scratch/'base.tar'; assets=proj/'Assets'
    subprocess.run(['git','-C',PROJECT.parent,'archive',base,'-o',archive,'URPSample01/Packages','URPSample01/ProjectSettings/ProjectVersion.txt'],check=True)
    with tarfile.open(archive) as tf: tf.extractall(scratch, filter='data')
    archive.unlink(); assets.mkdir()
    # Limit import to the embedded URP dependency graph plus the few engine modules used here.
    manifest=proj/'Packages/manifest.json'; deps=json.loads(manifest.read_text())['dependencies']
    keep={k:v for k,v in deps.items() if k.startswith('com.unity.modules.') or k in ('com.unity.render-pipelines.universal','com.unity.ugui','com.unity.test-framework')}
    manifest.write_text(json.dumps({'dependencies':keep}))
    folder=Path('Packages')/PKG/'Runtime/RendererFeatures/RayTracing'
    shutil.copytree(PROJECT/folder,proj/folder,dirs_exist_ok=True)
    # Put test vectors in a scratch-only friend assembly using W1's existing internal visibility.
    fixture=assets/'Fixture'; fixture.mkdir(); (fixture/'T2.asmdef').write_text(json.dumps({'name':'Unity.RayTracing.Tests.Editor','references':['Unity.RenderPipelines.Universal.Runtime','Unity.RenderPipelines.Core.Runtime']}))
    editor=assets/'Editor'; editor.mkdir(); shutil.copy2(HERE/'T2KeepFixture.cs',editor)
    inputs={'T2KeepFixture.cs':hashlib.sha256((editor/'T2KeepFixture.cs').read_bytes()).hexdigest()}
    for src,dest in [(TEST/'T2VectorSuite.cs',fixture),(HERE/'T2Player.cs',fixture),(TEST/'T2Fixture.shader',assets),(TEST/'T2Fixture.raytrace',assets)]: shutil.copy2(src,dest)
    for src in [*(proj/folder).glob('*.cs'),*fixture.glob('*.cs'),*assets.glob('T2Fixture.*')]: inputs[src.name]=hashlib.sha256(src.read_bytes()).hexdigest()
    exe=scratch/'Build/T2.exe'; exe.parent.mkdir()
    subprocess.run([UNITY,'-batchmode','-quit','-buildTarget','Win64','-force-d3d12','-projectPath',proj,'-executeMethod','T2Player.Build','-exe',exe,'-logFile',scratch/'Build.log'],check=True,timeout=3600)
    guid=None
    for api in (11,12):
        result=scratch/f'D3D{api}.json'
        code=subprocess.run([exe,'-batchmode','-screen-fullscreen','0','-screen-width','320','-screen-height','240',f'-force-d3d{api}','-expectedApi',f'Direct3D{api}','-result',result,'-logFile',scratch/f'D3D{api}.log'],timeout=180).returncode
        report=json.loads(result.read_text()); report.update(exitCode=code,base=base,inputs=inputs)
        (out/f'Player-D3D{api}.json').write_text(json.dumps(report,indent=2)+'\n')
        assert code==0 and report['development'] and report['passed'] and report['api']==f'Direct3D{api}' and report['guid'] and (guid is None or report['guid']==guid)
        guid=report['guid']
    shutil.rmtree(scratch)
