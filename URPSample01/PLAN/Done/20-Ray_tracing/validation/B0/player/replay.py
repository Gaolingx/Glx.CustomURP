"""Win64: python replay.py [--output FRESH_ROOT] [--scratch-parent DIR].
Recheck retained bytes & reports without Unity: python replay.py --verify [--output ROOT].
"""
import argparse
import gzip
import hashlib
import json
import shutil
import struct
import subprocess
import tarfile
import tempfile
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[5]
REV = 'd4592ac68d154bd3657c0e475c643e27ed86bb3b'
FIX = 'Assets/Tests/ContactShadow/'
SCENE = FIX + 'Scenes/ContactShadowBaseline.unity'
W, H = 1920, 1080

def require(ok, why):
    if not ok:
        raise RuntimeError(why)

def sha(b):
    return hashlib.sha256(b).hexdigest()

def save(path, obj):
    path.write_text(json.dumps(obj, indent=2, allow_nan=False) + '\n', encoding='utf-8')

def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def git(*args):
    return subprocess.check_output(['git', '-C', PROJECT.parent, *args])

def player_dirs(root, api):
    return [root / f'Win64-D3D{api}-Forward' / f'run-0{i}' for i in (1, 2)]

def checked_frame(data):
    require(len(data) == W*H*8, 'Unexpected RGBAHalf byte count')
    # Compare actual floating values for validity/variation, not variation in alpha,
    # exponent bits, signed zero or a difference between constant R and constant G.
    mins, maxs = [], []
    for c in range(4):
        words = bytearray(len(data)//4)
        words[0::2], words[1::2] = data[2*c::8], data[2*c+1::8]
        # Every fp16 NaN or infinity has exponent 31.
        require(not any((hi & 0x7c) == 0x7c for hi in words[1::2]), f'Nonfinite channel {c}')
        lo = min(struct.iter_unpack('<e', words))[0]; hi = max(struct.iter_unpack('<e', words))[0]
        mins.append(lo); maxs.append(hi)
    require(any(hi > lo for lo, hi in zip(mins[:3], maxs[:3])), 'Spatially constant RGB')
    return {'bytes': len(data), 'sha256': sha(data), 'minRGBA': mins, 'maxRGBA': maxs, 'nonfiniteChannels': 0}

def verify(root, write, reference=None):
    identities, pids = set(), set()
    for api in (11, 12):
        dirs = player_dirs(root, api)
        frames = []
        for d in dirs:
            p = load(d/'Player.json'); m = load(d/'BuildIdentity.json')
            identities.add((m['buildGuid'], m['exeSHA256'], m['source'], m['sceneSHA256']))
            pids.add(p['pid'])
            require(m['source'] == REV and m['sceneSHA256'] == sha(git('show', f'{REV}:URPSample01/{SCENE}')), 'Source mismatch')
            require(uuid.UUID(p['buildGuid']) == uuid.UUID(m['buildGuid']) and p['passed'] and p['development'] and p['bits'] == 64, 'Wrong build')
            require(p['api'] == f'Direct3D{api}' and p['actualRoute'] == p['pipeline'] == 'Forward' and p['scene'] == SCENE and p['camera'] == 'ContactCamera', 'Wrong route/fixture')
            require((p['width'],p['height'],p['warmup'],p['render'],p['seed'],p['captureHz']) == (W,H,32,33,20261125,60) and p['unityFrame']-p['firstUnityFrame'] == 32, 'Replay mismatch')
            require(p['colorSpace'] == 'Linear' and p['targetFormat'] == p['readFormat'] == 'R16G16B16A16_SFloat', 'Format mismatch')
            require(p['hdr'] and p['samples'] == p['renderScale'] == 1 and p['aa'] == 'None' and not any(p[k] for k in ('post','dynamicResolution','sRGB','contactFeature','contactVolume','cameraShadows')), 'Render settings mismatch')
            require(p['lightStates'] == ['Main:Directional:enabled=True:shadows=None:contact=False', 'Point:Point:enabled=False:shadows=None:contact=False', 'Spot:Spot:enabled=False:shadows=None:contact=False'], 'Light states mismatch')
            require((d/'Player.log.txt').stat().st_size > 0, 'Missing Player log')
            with gzip.open(d/'frame.rgba16f.gz', 'rb') as f:
                data = f.read(W*H*8+1)
            stats = checked_frame(data)
            if write: save(d/'Frame.json', stats)
            else: require(stats == load(d/'Frame.json'), 'Retained frame does not match report')
            frames.append(data)
        a, b = frames
        equal = a == b  # Full byte comparison, including alpha and fp16 sign bits.
        changed = 0 if equal else sum(x != y for x,y in zip(struct.iter_unpack('<H',a), struct.iter_unpack('<H',b)))
        pair = {'api': f'Direct3D{api}', 'comparison': 'bitwise little-endian RGBA16F, run-01 vs run-02', 'rawSHA256': [sha(x) for x in frames], 'channelsCompared': W*H*4, 'differentChannels': changed, 'passed': equal}
        if write: save(dirs[1]/'Pair.json', pair)
        else: require(pair == load(dirs[1]/'Pair.json'), 'Pair report mismatch')
        print(json.dumps(pair), flush=True)
        require(equal, 'Pair differs')
        if reference is not None:
            ref_dir = player_dirs(reference, api)[0]
            with gzip.open(ref_dir/'frame.rgba16f.gz', 'rb') as f:
                ref = f.read(W*H*8+1)
            require(sha(ref) == load(ref_dir/'Frame.json')['sha256'], 'Invalid reference')
            reference_result = {'api': pair['api'], 'referenceSHA256': sha(ref), 'rerunSHA256': sha(a), 'passed': ref == a}
            if write: save(dirs[1]/'Reference.json', reference_result)
            require(ref == a, f'D3D{api} rerun differs from retained reference')
    require(len(identities) == 1 and len(pids) == 4, 'Expected one build and four independent process records')

def run(opts, log, seconds):
    print(f'Start {Path(opts[0]).name}: {log}', flush=True)
    subprocess.run([*opts,'-logFile',log], check=True, timeout=seconds)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=HERE.parent)
    parser.add_argument('--scratch-parent', type=Path)
    parser.add_argument('--unity', type=Path, default=Path(r'D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe'))
    parser.add_argument('--verify', action='store_true')
    args = parser.parse_args(); root = args.output.resolve()
    reference = HERE.parent if root != HERE.parent and (player_dirs(HERE.parent,12)[1]/'Pair.json').is_file() else None
    if args.verify: return verify(root, False, reference)
    for path in [PROJECT/'Assets',PROJECT/'Packages',PROJECT/'ProjectSettings']:
        require(not root.is_relative_to(path), 'Unsafe evidence path')
    require(all(not d.exists() for api in (11,12) for d in player_dirs(root,api)), 'Use fresh run directories (--output)')
    require(args.unity.is_file(), 'Missing Unity editor')
    base = (args.scratch_parent or Path(tempfile.gettempdir())).resolve()
    require(not base.is_relative_to(PROJECT), 'Scratch must be outside project')
    scratch = Path(tempfile.mkdtemp(prefix='B0-', dir=base)); print(f'Scratch: {scratch}', flush=True)
    proj = scratch/'URPSample01'
    fixture = ['Packages','Assets/UniversalRenderPipelineGlobalSettings.asset','Assets/UniversalRenderPipelineGlobalSettings.asset.meta']
    fixture += ['ProjectSettings/'+s for s in ['ProjectVersion.txt','ProjectSettings.asset','GraphicsSettings.asset','QualitySettings.asset','EditorSettings.asset','TagManager.asset']]
    fixture += [FIX+d+s for d in ('Materials','Pipelines','Profiles','Scenes') for s in ('','.meta')]
    archive = scratch/'frozen.tar'
    git('archive','--format=tar','-o',str(archive), REV, *[':(top)URPSample01/'+p for p in fixture]);
    with tarfile.open(archive) as t: t.extractall(scratch, filter='data')
    archive.unlink()
    shutil.copyfile(HERE/'B0Replay.cs', proj/'Assets/B0Replay.cs')
    (proj/'Assets/link.xml').write_bytes(git('show', f'{REV}:URPSample01/PLAN/Done/10-ContactShadow/validation/P5/tooling/link.xml'))
    exe = scratch/'Player/B0Replay.exe'; exe.parent.mkdir(); guid = scratch/'BuildGuid.txt'
    run([args.unity,'-batchmode','-quit','-buildTarget','Win64','-force-d3d11','-projectPath',proj,'-executeMethod','B0Replay.Build','-probeExe',exe,'-probeGuid',guid],scratch/'Build.log',3600)
    manifest = {'source':REV, 'sceneSHA256':sha(git('show', f'{REV}:URPSample01/{SCENE}')), 'exeSHA256':sha(exe.read_bytes()), 'buildGuid':guid.read_text().strip()}
    for api in (11,12):
        for d in player_dirs(root,api):
            d.mkdir(parents=True, exist_ok=False); save(d/'BuildIdentity.json', manifest)
            run([exe,f'-force-d3d{api}','-batchmode','-screen-fullscreen','0','-screen-width','320','-screen-height','240','-probeApi',f'Direct3D{api}','-probeDir',d], d/'Player.log.txt',180)
            raw = d/'frame.raw'; packed = d/'frame.rgba16f.gz'
            with raw.open('rb') as f, gzip.open(packed,'xb') as out: shutil.copyfileobj(f,out)
            with gzip.open(packed,'rb') as f: require(f.read() == raw.read_bytes(), 'Invalid compressed copy')
            raw.unlink()
    verify(root, True, reference)
    shutil.rmtree(scratch)

if __name__ == '__main__': main()
