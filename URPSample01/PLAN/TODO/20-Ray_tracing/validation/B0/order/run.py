"""Windows, Python 3.12+, git, matching Unity Editor. Usage: python run.py UNITY_EXE SCRATCH_PARENT"""
import hashlib
import io
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile

HERE = Path(__file__).resolve().parent
BASE = 'd4592ac68d154bd3657c0e475c643e27ed86bb3b'
PKG = 'com.unity.render-pipelines.universal@14.0.12'
COMMON = 'RendererFeatures/ScreenSpaceCommon/'


def install(project):
    runtime = project / 'Packages' / PKG / 'Runtime'
    def patch(file, before, after):
        path = runtime / file
        text = path.read_text(encoding='utf-8')
        if text.count(before) != 1:
            raise ValueError(f'{file}: expected one baseline anchor: {before}')
        path.write_text(text.replace(before, after), encoding='utf-8')

    call = 'renderPass.Execute(context, ref renderingData);'
    for file in ('ScriptableRenderer.cs', 'NativeRenderPass.cs'):
        patch(file, call, 'B0Trace.Pass(ref renderingData, renderPass);\n                ' + call)
    def mark(file, anchor, label):
        patch(file, anchor, anchor + f'\n                B0Trace.Mark(ref renderingData, {label});')

    motion = 'Passes/MotionVectorRenderPass.cs'
    patch(motion, 'if (motionData == null)\n                return;',
          'if (motionData == null) { B0Trace.Mark(ref renderingData, "noMotionData"); return; }')
    mark(motion, 'DrawObjectMotionVectors(context, ref renderingData, camera, objectMaterial, cmd, ref passData.filteringSettings);', '"builtinCameraAndObjectCallsReturned"')
    editor = COMMON + 'ScreenSpaceEditorCameraMotionPass.cs'
    mark(editor, 's_EditorFirstFrameSkipCount++;', '"historySeed"')
    mark(editor, 's_EditorDrawCount++;', '"editorDrawRecorded"')
    mark(COMMON + 'ScreenSpaceDepthPyramidPass.cs', 's_EditorDownsampleDispatchCount += info.mipLevelCount - 1;', '"hizCopy+downsamples:" + (info.mipLevelCount - 1)')
    mark('RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingRendererFeature.cs', 'history.depthPyramidSource = depthPyramid;', '"ssl:" + cameraData.renderType + ":ssgi=" + stack.GetComponent<ScreenSpaceGlobalIllumination>().enabled.value')
    shutil.copyfile(HERE / 'B0Trace.cs', runtime / 'B0Trace.cs')
    dest = project / 'Assets/Editor'
    dest.mkdir(exist_ok=True)
    shutil.copyfile(HERE / 'B0Driver.cs', dest / 'B0Driver.cs')


def run(unity, project, api):
    log = project.parent / (api + '.log')
    args = [str(unity), '-batchmode', '-quit', '-force-' + api, '-job-worker-count', '2',
            '-projectPath', str(project), '-executeMethod', 'B0Driver.Run', '-logFile', str(log)]
    code = subprocess.run(args, cwd=project).returncode
    text = log.read_text(encoding='utf-8', errors='replace')
    rows = [s.split('|') for s in text.splitlines() if s.startswith('B0|')]
    result = dict(command=args, exitCode=code, logSha256=hashlib.sha256(log.read_bytes()).hexdigest(),
                  commandBufferDiagnostics=sorted({s for s in text.splitlines() if s.startswith('CommandBuffer:')}), samples=[])
    for row in rows:
        if row[1] == 'meta':
            result.update(zip(('utc','unity','api','gpu','driver','supportsRayTracing','pipeline','feature'), row[2:]))
        if row[1] != 'sample':
            continue
        _, _, camera, ident, additional, frame, pose, trace = row
        actual = trace.split(',')
        game = camera == 'Game'
        expected = ['ssl:Base:ssgi=True','201:CopyDepthPass','201:DBufferRenderPass','201:ScreenSpaceShadowsPass','202:MotionVectorRenderPass',
                    'builtinCameraAndObjectCallsReturned' if game else 'noMotionData',
                    '203:ScreenSpaceDepthPyramidPass','hizCopy+downsamples:7']
        if not game:
            expected += ['203:ScreenSpaceEditorCameraMotionPass','historySeed' if pose == '0' else 'editorDrawRecorded']
        result['samples'].append(dict(camera=camera,id=int(ident),additionalData=additional=='True',frame=int(frame),pose=int(pose),trace=actual,
                                      passed=actual==expected and (additional=='True')==game))
    samples = result['samples']
    result['passed'] = (code == 0 and result.get('api') == {'d3d11':'Direct3D11','d3d12':'Direct3D12'}[api]
                        and [(s['camera'],s['pose']) for s in samples] == [('Game',0),('Game',1),('SceneView',0),('SceneView',1)]
                        and all(s['passed'] for s in samples))
    return result


if __name__ == '__main__':
    unity, parent = (Path(x).resolve(strict=True) for x in sys.argv[1:])
    repo = Path(subprocess.check_output(['git','-C',str(HERE),'rev-parse','--show-toplevel'],text=True).strip()).resolve()
    if parent.is_relative_to(repo):
        raise ValueError('External scratch parent required')
    scratch = Path(tempfile.mkdtemp(prefix='b0order-', dir=parent))
    project = scratch / 'URPSample01'
    print('New isolated project:', project, flush=True)
    archive = subprocess.check_output(['git','-C',str(repo),'archive',BASE,'URPSample01/Assets','URPSample01/Packages','URPSample01/ProjectSettings'])
    with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
        tar.extractall(scratch, filter='data')
    install(project)
    evidence = dict(baseline=BASE,runs=[])
    for api in ('d3d11','d3d12'):
        evidence['runs'].append(run(unity, project, api))
        (HERE / 'execution.json').write_text(json.dumps(evidence,indent=2)+'\n',encoding='utf-8')
    sys.exit(0 if all(r['passed'] for r in evidence['runs']) else 1)
