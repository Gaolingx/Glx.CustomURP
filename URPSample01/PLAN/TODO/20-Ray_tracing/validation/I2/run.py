"""Build the current embedded URP in an isolated I2 project; run the 1080p quality/performance suite
on D3D12 and D3D11, then build and run a D3D11-only strip control with a full ray tracing variant
audit (Lit GlxDXR hit pass and every _SCREEN_SPACE_GI_PROBE variant)."""
import argparse, hashlib, json, os, shutil, subprocess, tarfile, tempfile
from pathlib import Path
HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[4]
PKG = Path('Packages/com.unity.render-pipelines.universal@14.0.12')
UNITY = os.environ.get('UNITY_EDITOR', 'D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe')
BASE = 'aceb6ca976c3f8900afecf70f4b32c5390a1e6b6'
FIXTURES = ['I2Player.cs', 'I2VariantAudit.cs']

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
    save(project / 'Assets/Fixture/I2.asmdef', {'name': 'Unity.RayTracing.Tests.Editor',
        'references': ['Unity.RenderPipelines.Universal.Runtime', 'Unity.RenderPipelines.Core.Runtime']})
    return project

def sets_from(path):
    if not path.exists(): return []
    return sorted({line[4:] for line in path.read_text().splitlines() if line.startswith('set ')})

def rt_audit(path):
    audit = json.loads(path.read_text())
    for name in ('glxDxr', 'probeKeyword'):
        if audit.get(name + 'Sets', 0) != sum(entry['count'] for entry in audit.get(name, [])):
            raise RuntimeError(f'{path.name}: {name} set count does not match the recorded passes')
    return audit

if __name__ == '__main__':
    a = argparse.ArgumentParser()
    a.add_argument('--scratch', type=Path)
    a.add_argument('--out', type=Path, default=HERE / 'evidence')
    a.add_argument('--prepare-only', action='store_true')
    a.add_argument('--refresh', action='store_true')
    a.add_argument('--strip-only', action='store_true', help='build + audit only; skip the 1080p player suite')
    args = a.parse_args()
    out = args.out.resolve(); out.mkdir(parents=True, exist_ok=True)
    root = args.scratch or Path(tempfile.mkdtemp(prefix='I2-', dir='E:/tmp'))
    project = root / PROJECT.name if args.scratch else prepare(root)
    print(root, flush=True)
    if project.parent.resolve().parent != Path('E:/tmp').resolve() or not root.name.startswith('I2-'):
        raise ValueError('Not an I2 scratch')
    if args.prepare_only: quit()
    fixture = project / 'Assets/Fixture'
    for name in FIXTURES:
        shutil.copy2(HERE / name, fixture / name)
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
        'fixtureHashes': {n: sha(HERE / n) for n in FIXTURES}})

    audit = out / 'variant-audit.txt'
    audit.unlink(missing_ok=True)
    rt12_path = out / 'RtVariants.json'
    exe = root / 'Build/I2.exe'; exe.parent.mkdir(exist_ok=True)
    subprocess.run([UNITY, '-batchmode', '-quit', '-buildTarget', 'Win64', '-force-d3d12', '-projectPath', project,
                    '-executeMethod', 'I2Player.Build', '-exe', exe, '-logFile', out / 'Build.log'],
                   env={**os.environ, 'I2_VARIANTS': str(audit), 'I2_RT_AUDIT': str(rt12_path)},
                   timeout=3600, check=True)
    sets = sets_from(audit)
    save(out / 'Variants.json', {'pass': 'Universal Render Pipeline/Lit:GlxDXR', 'keywordSets': sets,
        'distinctSets': len(sets),
        'callbacks': [l for l in audit.read_text().splitlines() if l.startswith('pass ')] if audit.exists() else []})
    if not 0 < len(sets) <= 16:
        raise RuntimeError(f'Lit DXR keyword sets outside 0..16: {len(sets)}')
    rt12 = rt_audit(rt12_path)
    if rt12['glxDxrSets'] <= 0 or rt12['probeKeywordSets'] <= 0:
        raise RuntimeError(f"Win64 build lost the ray tracing variants: {rt12['glxDxrSets']} GlxDXR, "
                           f"{rt12['probeKeywordSets']} probe")

    codes = {}
    if not args.strip_only:
        for api, name in [(11, 'D3D11'), (12, 'D3D12')]:
            dest = out / name; dest.mkdir(exist_ok=True)
            codes[api] = subprocess.run([exe, '-profiler-enable', f'-force-d3d{api}', '-out', dest, '-result', dest / 'Report.json',
                                         '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720',
                                         '-logFile', dest / 'Player.log'], timeout=5400).returncode

    scene = project / 'Assets/I2.unity'
    strip_exe = root / 'Build/I2Strip.exe'
    audit11 = out / 'variant-audit-d3d11.txt'
    audit11.unlink(missing_ok=True)
    rt11_path = out / 'D3D11OnlyRtVariants.json'
    subprocess.run([UNITY, '-batchmode', '-quit', '-force-d3d11', '-buildTarget', 'Win64', '-projectPath', project,
                    '-executeMethod', 'I2VariantAudit.BuildD3D11Only', '-logFile', out / 'D3D11OnlyBuild.log'],
                   env={**os.environ, 'I2_SCENE': scene.relative_to(project).as_posix(),
                        'I2_STRIP_EXE': str(strip_exe), 'I2_VARIANTS': str(audit11),
                        'I2_RT_AUDIT': str(rt11_path)},
                   timeout=3600, check=True)
    sets11 = sets_from(audit11)
    rt11 = rt_audit(rt11_path)
    save(out / 'D3D11OnlyVariants.json', {'pass': 'Universal Render Pipeline/Lit:GlxDXR',
        'keywordSets': sets11, 'distinctSets': len(sets11),
        'callbacks': [l for l in audit11.read_text().splitlines() if l.startswith('pass ')] if audit11.exists() else []})
    if sets11:
        raise RuntimeError(f'D3D11-only build retained {len(sets11)} Lit DXR keyword sets')
    if rt11['glxDxrSets'] or rt11['probeKeywordSets']:
        raise RuntimeError(f"D3D11-only build retained {rt11['glxDxrSets']} GlxDXR and "
                           f"{rt11['probeKeywordSets']} probe keyword sets")

    strip_out = out / 'D3D11Strip'; strip_out.mkdir(exist_ok=True)
    codes['strip'] = subprocess.run([strip_exe, '-profiler-enable', '-mode', 'strip', '-force-d3d11',
                                     '-out', strip_out, '-result', strip_out / 'Report.json',
                                     '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720',
                                     '-logFile', strip_out / 'Player.log'], timeout=1200).returncode

    save(out / 'Run.json', {'exe': str(exe), 'sha256': sha(exe), 'stripExe': str(strip_exe),
        'stripSha256': sha(strip_exe), 'exitCodes': codes, 'scope': 'strip-only' if args.strip_only else 'full'})
    subprocess.run(['python', str(HERE / 'verify.py'), str(out)] + (['strip-only'] if args.strip_only else []),
                   check=True)
