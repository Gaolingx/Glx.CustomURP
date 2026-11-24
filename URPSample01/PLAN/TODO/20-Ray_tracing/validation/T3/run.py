"""Build one isolated Win64 T3 development Player and validate it on D3D11 and D3D12."""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tarfile
import tempfile
from pathlib import Path
from uuid import UUID


HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[4]
BASE = '676f003edfca1d2541891139e610e76f2584f03c'
PACKAGE = Path('Packages/com.unity.render-pipelines.universal@14.0.12')
OVERLAYS = (PACKAGE/'Shaders/RayTracing', PACKAGE/'Runtime/RendererFeatures/RayTracing')
UNITY = Path(os.environ.get('UNITY_EDITOR', 'D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe'))
APIS = (11, 12)


def write_json(path, data):
    path.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def prepare(scratch, out):
    proj = scratch/PROJECT.name
    archive = scratch/'base.tar'
    # Include Graphics/Quality settings AND their referenced pipeline/renderer/global assets.
    # T2's empty Assets project has no URP feature set for the production shader stripper.
    paths = ['Packages', 'ProjectSettings/ProjectVersion.txt', 'ProjectSettings/ProjectSettings.asset',
             'ProjectSettings/GraphicsSettings.asset', 'ProjectSettings/QualitySettings.asset',
             'Assets/Settings', 'Assets/Settings.meta', 'Assets/UniversalRenderPipelineGlobalSettings.asset',
             'Assets/UniversalRenderPipelineGlobalSettings.asset.meta']
    subprocess.run(['git', '-C', PROJECT.parent, 'archive', '-o', archive, BASE,
                    *(f'{PROJECT.name}/{p}' for p in paths)], check=True)
    archive_hash = sha256(archive)
    with tarfile.open(archive) as tf:
        tf.extractall(scratch, filter='data')
    archive.unlink()

    manifest = proj/'Packages/manifest.json'
    deps = json.loads(manifest.read_text(encoding='utf-8'))['dependencies']
    keep = {'com.unity.render-pipelines.universal', 'com.unity.ugui', 'com.unity.test-framework'}
    write_json(manifest, {'dependencies': {k: v for k, v in deps.items() if k in keep or k.startswith('com.unity.modules.')}})
    inputs = [manifest]
    for rel in OVERLAYS:
        dest = proj/rel
        if dest.exists():
            shutil.rmtree(dest)  # Match the working source tree, including any deletions.
        shutil.copytree(PROJECT/rel, dest)
        folder_meta = dest.with_suffix('.meta')
        shutil.copy2((PROJECT/rel).with_suffix('.meta'), folder_meta)
        inputs.append(folder_meta)
        for asset in sorted(dest.rglob('*')):
            if asset.suffix != '.meta' and not Path(str(asset) + '.meta').is_file():
                raise ValueError(f'Missing Unity metadata: {asset}.meta')
            if asset.is_file():
                inputs.append(asset)

    fixture = proj/'Assets/Fixture'
    fixture.mkdir()
    asmdef = fixture/'T3.asmdef'
    write_json(asmdef, {'name': 'Unity.RayTracing.Tests.Editor', 'references': [
        'Unity.RenderPipelines.Universal.Runtime', 'Unity.RenderPipelines.Core.Runtime']})
    driver = fixture/'T3Player.cs'
    shutil.copy2(HERE/driver.name, driver)
    inputs.extend([asmdef, driver])
    write_json(out/'Inputs.json', {
        'base': BASE, 'archivePaths': paths, 'archiveSha256': archive_hash,
        'runnerSha256': sha256(Path(__file__)),
        'overlaidAndGeneratedSha256': {p.relative_to(proj).as_posix(): sha256(p) for p in inputs},
        'shader': 'Hidden/Universal Render Pipeline/GlxBasicHit', 'pass': 'GlxDXR',
        'shaderStripperBypass': False,
        'retention': 'Baseline URP GraphicsSettings, QualitySettings, renderers and global stripping settings; no fixture callback restores variants.'
    })
    return proj


def run_player(exe, scratch, api):
    result = scratch/f'D3D{api}.json'
    expected = f'Direct3D{api}'
    code = None
    report = {}
    errors = []
    try:
        code = subprocess.run([exe, '-profiler-enable', '-screen-fullscreen', '0', '-screen-width', '320',
                               '-screen-height', '240', f'-force-d3d{api}', '-expectedApi', expected,
                               '-result', result, '-logFile', scratch/f'D3D{api}.log'],
                              cwd=scratch, timeout=180).returncode
        data = json.loads(result.read_text(encoding='utf-8-sig'))
        if not isinstance(data, dict):
            raise ValueError('Player result must be a JSON object')
        report = data
        if code != 0 or report.get('passed') is not True:
            errors.append('Player suite did not pass')
        if api == 12 and (not report.get('gpuTimingMeasured') or report.get('markerGpuBlocks', 0) != 3):
            errors.append('Expected three measured Glx Basic Hit Trace GPU marker blocks')
        if report.get('development') is not True or report.get('api') != expected:
            errors.append('Wrong graphics API or not a development build')
        if not isinstance(report.get('guid'), str) or not UUID(report['guid']).int:
            errors.append('Missing or zero build GUID')
    except (OSError, subprocess.TimeoutExpired, ValueError) as exc:
        errors.append(str(exc))
    report.update(exitCode=code, base=BASE, expectedApi=expected, validationErrors=errors)
    return report


def run(out, parent, keep):
    out.mkdir(parents=True, exist_ok=True)
    names = ['Inputs.json', *(f'Player-D3D{api}.json' for api in APIS)]
    if any((out/name).exists() for name in names):
        raise FileExistsError(f'Use a fresh output directory; evidence already exists in {out}')
    parent.mkdir(parents=True, exist_ok=True)
    scratch = Path(tempfile.mkdtemp(prefix='T3-', dir=parent))
    print('Scratch project, build and logs:', scratch, flush=True)
    proj = prepare(scratch, out)
    exe = scratch/'Build/T3.exe'
    exe.parent.mkdir()
    subprocess.run([UNITY, '-batchmode', '-quit', '-buildTarget', 'Win64', '-force-d3d12',
                    '-projectPath', proj, '-executeMethod', 'T3Player.Build', '-exe', exe,
                    '-logFile', scratch/'Build.log'], check=True, cwd=scratch, timeout=3600)
    reports = {api: run_player(exe, scratch, api) for api in APIS}
    inputs_hash = sha256(out/'Inputs.json')
    guid = reports[11].get('guid')
    same_build = bool(guid) and guid == reports[12].get('guid')
    for api, report in reports.items():
        if not same_build:
            report['validationErrors'].append('Same build GUID not established across both APIs')
        report['inputsSha256'] = inputs_hash
        write_json(out/f'Player-D3D{api}.json', report)
    if any(r['validationErrors'] for r in reports.values()):
        raise RuntimeError(f'T3 validation failed; see reports in {out} and logs/build in {scratch}')
    if not keep:
        shutil.rmtree(scratch)  # Only after both validated reports are on disk.
    print('T3 passed on both APIs; reports:', out)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--scratch-parent', type=Path, default=Path('E:/tmp'), help='parent for unique T3-* directory (default: E:/tmp)')
    parser.add_argument('--out', type=Path, required=True, help='destination for Inputs.json and Player-D3D{11,12}.json')
    parser.add_argument('--keep', action='store_true', help='retain scratch project, logs and executable even on success')
    args = parser.parse_args()
    run(args.out.resolve(), args.scratch_parent.resolve(), args.keep)
