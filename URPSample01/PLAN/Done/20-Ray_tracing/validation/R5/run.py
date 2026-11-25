"""Build the current embedded URP in an isolated R5 project; run reflection material/budget on both APIs."""
import argparse, hashlib, json, os, shutil, subprocess, tarfile, tempfile
from pathlib import Path
HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[4]
PKG = Path('Packages/com.unity.render-pipelines.universal@14.0.12')
UNITY = os.environ.get('UNITY_EDITOR', 'D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe')
BASE = '464c769637bfe8063a44fa853ad24c87a8f50a0b'

def save(p, x): p.write_text(json.dumps(x, indent=2) + '\n', encoding='utf8')
def sha(p):
    with p.open('rb') as f: return hashlib.file_digest(f, 'sha256').hexdigest()

def prepare(root):
    project = root / PROJECT.name
    paths = ['Packages/manifest.json', 'Packages/packages-lock.json', 'ProjectSettings/ProjectVersion.txt',
             'ProjectSettings/ProjectSettings.asset', 'ProjectSettings/GraphicsSettings.asset',
             'ProjectSettings/QualitySettings.asset', 'Assets/Settings', 'Assets/Settings.meta',
             'Assets/UniversalRenderPipelineGlobalSettings.asset', 'Assets/UniversalRenderPipelineGlobalSettings.asset.meta']
    archive = root / 'fixture.tar'
    subprocess.run(['git', '-C', PROJECT.parent, 'archive', '-o', archive, BASE,
                    *[f'{PROJECT.name}/{p}' for p in paths]], check=True)
    with tarfile.open(archive) as t: t.extractall(root, filter='data')
    shutil.copytree(PROJECT / PKG, project / PKG)
    manifest = project / 'Packages/manifest.json'
    deps = json.loads(manifest.read_text())['dependencies']
    save(manifest, {'dependencies': {k: v for k, v in deps.items()
        if k.startswith('com.unity.modules.') or k in
        ['com.unity.render-pipelines.universal', 'com.unity.ugui', 'com.unity.test-framework']}})
    (project / 'Assets/Fixture').mkdir()
    save(project / 'Assets/Fixture/R4.asmdef', {'name': 'Unity.RayTracing.Tests.Editor',
        'references': ['Unity.RenderPipelines.Universal.Runtime', 'Unity.RenderPipelines.Core.Runtime']})
    return project

if __name__ == '__main__':
    a = argparse.ArgumentParser()
    a.add_argument('--scratch', type=Path)
    a.add_argument('--out', type=Path, default=HERE / 'evidence')
    a.add_argument('--prepare-only', action='store_true')
    a.add_argument('--refresh', action='store_true')
    args = a.parse_args()
    out = args.out.resolve(); out.mkdir(exist_ok=True)
    root = args.scratch or Path(tempfile.mkdtemp(prefix='R5-', dir='E:/tmp'))
    project = root / PROJECT.name if args.scratch else prepare(root)
    print(root, flush=True)
    if project.parent.resolve().parent != Path('E:/tmp').resolve() or not root.name.startswith(('R4-', 'R5-')):
        raise ValueError('Not an R4/R5 scratch')
    if args.prepare_only: quit()
    fixture = project / 'Assets/Fixture'
    shutil.copy2(HERE / 'R5Player.cs', fixture / 'R5Player.cs')
    shutil.copy2(HERE / 'R5Probe.raytrace', fixture / 'R5Probe.raytrace')
    files = sorted(p for p in (PROJECT / PKG).rglob('*') if p.is_file())
    if args.refresh:
        for p in files:
            dst = project / PKG / p.relative_to(PROJECT / PKG)
            if not dst.exists() or sha(p) != sha(dst):
                dst.parent.mkdir(exist_ok=True, parents=True); shutil.copy2(p, dst)
        extra = [p for p in (project / PKG).rglob('*') if p.is_file() and not (PROJECT / PKG / p.relative_to(project / PKG)).exists()]
        assert not extra, extra
    mismatch = [p for p in files if sha(p) != sha(project / PKG / p.relative_to(PROJECT / PKG))]
    assert not mismatch, mismatch
    save(out / 'Inputs.json', {'base': BASE, 'scratch': str(root),
        'packageHashes': {str(p.relative_to(PROJECT / PKG)): sha(p) for p in files},
        'fixtureHashes': {n: sha(HERE / n) for n in ['R5Player.cs', 'R5Probe.raytrace', 'run.py']}})
    exe = root / 'Build/R5.exe'; exe.parent.mkdir(exist_ok=True)
    subprocess.run([UNITY, '-batchmode', '-quit', '-buildTarget', 'Win64', '-force-d3d12', '-projectPath', project,
                    '-executeMethod', 'R5Player.Build', '-exe', exe, '-logFile', out / 'Build.log'],
                   env={**os.environ}, timeout=3600, check=True)
    codes = {}
    for api, name in [(11, 'D3D11'), (12, 'D3D12')]:
        dest = out / name; dest.mkdir(exist_ok=True)
        codes[api] = subprocess.run([exe, f'-force-d3d{api}', '-expectedApi', f'Direct3D{api}',
                                     '-result', dest / 'Report.json', '-screen-fullscreen', '0',
                                     '-screen-width', '320', '-screen-height', '240',
                                     '-logFile', dest / 'Player.log'], timeout=1200).returncode
    save(out / 'Run.json', {'exe': str(exe), 'sha256': sha(exe), 'exitCodes': codes})
    subprocess.run(['python', str(HERE / 'verify.py'), str(out)], check=True)
