"""Build a fresh Win64 T4 development Player, then run the same binary on D3D11 and D3D12.

Python 3.12+, Git, Unity 2022.3.62f1 with Windows build support and a DXR adapter.
Set UNITY_EDITOR to the full Unity.exe path if not installed at the default location.
Use --out with a new directory; build, scratch project and logs are retained on failure.
"""
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
BASE = '464c769637bfe8063a44fa853ad24c87a8f50a0b'
PKG = Path('Packages/com.unity.render-pipelines.universal@14.0.12')
PATHS = ['Packages/manifest.json', 'Packages/packages-lock.json',
         'ProjectSettings/ProjectVersion.txt', 'ProjectSettings/ProjectSettings.asset',
         'ProjectSettings/GraphicsSettings.asset', 'ProjectSettings/QualitySettings.asset',
         'Assets/Settings', 'Assets/Settings.meta', 'Assets/UniversalRenderPipelineGlobalSettings.asset',
         'Assets/UniversalRenderPipelineGlobalSettings.asset.meta']
UNITY = Path(os.environ.get('UNITY_EDITOR', 'D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe'))


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def save(path, data):
    path.write_text(json.dumps(data, indent=2, allow_nan=False) + '\n', encoding='utf-8')


def prepare(work, out):
    proj = work/PROJECT.name
    archive = work/'baseline.tar'
    subprocess.run(['git', '-C', PROJECT.parent, 'archive', '-o', archive, BASE,
                    *(f'{PROJECT.name}/{s}' for s in PATHS)], check=True)
    archive_hash = sha(archive)
    with tarfile.open(archive) as tf:
        tf.extractall(work, filter='data')
    archive.unlink()
    # Start with an EMPTY package destination. Copy the complete working embedded package,
    # including new subdirectories, shader library, SSR consumer, stripper, resources and metas.
    # Do not merge a few hand-picked files over a stale archived Runtime/Shaders pair.
    pkg = proj/PKG
    shutil.copytree(PROJECT/PKG, pkg)
    if 'BindLights(' not in (pkg/'Runtime/RendererFeatures/RayTracing/RayTracingSceneResources.cs').read_text(encoding='utf-8-sig'):
        raise RuntimeError('The working package does not yet contain the T4 scene.BindLights API')
    manifest = proj/'Packages/manifest.json'
    deps = json.loads(manifest.read_text(encoding='utf-8'))['dependencies']
    keep = {'com.unity.render-pipelines.universal', 'com.unity.ugui', 'com.unity.test-framework'}
    save(manifest, {'dependencies': {k:v for k,v in deps.items() if k in keep or k.startswith('com.unity.modules.')}})
    fixture = proj/'Assets/Fixture'
    fixture.mkdir()
    asmdef = fixture/'T4.asmdef'
    save(asmdef, {'name':'Unity.RayTracing.Tests.Editor', 'references':[
        'Unity.RenderPipelines.Universal.Runtime', 'Unity.RenderPipelines.Core.Runtime']})
    for name in ('T4Player.cs', 'T4Probe.raytrace'):
        shutil.copy2(HERE/name, fixture/name)
    save(out/'Inputs.json', {'base':BASE, 'archivePaths':PATHS, 'archiveSha256':archive_hash,
         'runnerSha256':sha(Path(__file__)), 'workingPackage':PKG.as_posix(),
         'inputSha256':{p.relative_to(proj).as_posix():sha(p) for p in
                        [manifest, *sorted(fixture.iterdir()), *sorted(pkg.rglob('*'))] if p.is_file()}})
    return proj


def player(exe, api, out, work):
    expected = f'Direct3D{api}'
    path = out/f'Player-D3D{api}.json'
    errors, data, code = [], {}, None
    try:
        code = subprocess.run([exe, f'-force-d3d{api}', '-expectedApi', expected, '-result', path,
            '-screen-fullscreen','0','-screen-width','320','-screen-height','240',
            '-logFile',out/f'D3D{api}.log'], cwd=work, timeout=240).returncode
        data = json.loads(path.read_text(encoding='utf-8-sig'))
        if not isinstance(data, dict):
            data = {}
            raise ValueError('Invalid Player report')
        if code != 0 or data.get('passed') is not True or data.get('development') is not True or data.get('api') != expected:
            errors.append('Wrong API, build type or unsuccessful Player suite')
        if not UUID(data.get('guid', '')).int or data.get('released') is not True:
            errors.append('Invalid GUID or resources not released')
        if api == 12:
            rows = data.get('batches') or []
            if not (data.get('supportsRayTracing') and data.get('buildCount') == 1 and
                    data.get('dispatches') == len(rows) == 17 and
                    all(r['passed'] is True and r['finite'] is True and r['actual'] == r['expected'] for r in rows)):
                errors.append('Incomplete DXR vectors, invalid counters or unexpected geometry rebuild')
        if api == 11 and not (data.get('denial') == 'RayTracingUnsupported' and data.get('buildCount') == 0 and
                              data.get('dispatches') == 0 and not data.get('batches')):
            errors.append('Missing negative D3D11 gate evidence')
    except (OSError, subprocess.TimeoutExpired, ValueError, TypeError, KeyError) as ex:
        errors.append(str(ex))
    data.update(exitCode=code, expectedApi=expected, validationErrors=errors, inputsSha256=sha(out/'Inputs.json'))
    save(path, data)
    return data


def run(out, parent, keep):
    out.mkdir(parents=True, exist_ok=False)
    parent.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix='T4-', dir=parent))
    print('Scratch:', work, 'Evidence:', out, flush=True)
    proj = prepare(work, out)
    exe = work/'Build/T4.exe'
    exe.parent.mkdir()
    subprocess.run([UNITY, '-batchmode', '-quit', '-buildTarget', 'Win64', '-force-d3d12', '-projectPath', proj,
                    '-executeMethod', 'T4Player.Build', '-exe', exe, '-logFile', out/'Build.log'],
                   cwd=work, check=True, timeout=3600)
    reports = [player(exe, api, out, work) for api in (11, 12)]
    same = bool(reports[0].get('guid')) and reports[0]['guid'] == reports[1].get('guid')
    ok = same and not any(r['validationErrors'] for r in reports)
    save(out/'Summary.json', {'passed':ok, 'sameBuildGuid':same, 'exeSha256':sha(exe), 'inputsSha256':sha(out/'Inputs.json')})
    if not ok:
        raise RuntimeError(f'T4 validation failed: {out}; scratch retained at {work}')
    if not keep:
        shutil.rmtree(work)
    print('T4 passed on both APIs:', out)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True, help='new evidence directory')
    parser.add_argument('--scratch-parent', type=Path, default=Path('E:/tmp'))
    parser.add_argument('--keep', action='store_true', help='retain build and scratch project even on success')
    args = parser.parse_args()
    run(args.out.resolve(), args.scratch_parent.resolve(), args.keep)
