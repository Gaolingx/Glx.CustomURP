import hashlib
import json
import math
import subprocess
from pathlib import Path


def check(contract):
    root = Path(__file__).resolve().parent
    sha = contract['baselineCommit']
    replay = contract['originalPathReplay']
    platform = contract['platform']
    pins = {
        replay['scene']: replay['sceneBlob'],
        'Packages/packages-lock.json': platform['packagesLockBlob'],
        f"Packages/com.unity.render-pipelines.universal@{platform['embeddedUrp']}": platform['urpTree'],
        **{f'Assets/Tests/ContactShadow/{k}': v for k, v in replay['assetTrees'].items()},
    }
    for path, oid in pins.items():
        actual = subprocess.check_output(['git', 'rev-parse', '--verify', f'{sha}:URPSample01/{path}'], cwd=root, text=True).strip()
        if actual != oid:
            raise ValueError(f'{path}: expected {oid}, got {actual}')

    g, r = contract['probes'], contract['reflections']
    lights, abi = contract['lightingBudget'], contract['hitAbi']
    total = contract['combinedBudgetSingleBaseCamera']
    primary = g['B'] * g['R'] + r['KperBaseCamera1080p']
    shadow = primary * lights['Smax']
    checks = {
        'camera frame': replay['captureCameraRenderOneBased'] == replay['warmupCameraRenders'] + 1,
        'probe grid': math.prod(g['gridXYZ']) == g['count'],
        'maximum revisit': math.ceil(g['count'] / g['B']) == g['maxRevisitFrames'],
        'fixed directions': g['inactiveRaysPerProbe'] == g['fixedNonRotatingPrefix'] == 32 < g['R'],
        'active batch': g['activePrimaryRaysPerBatchMax'] == g['B'] * g['R'],
        'light caps': 0 < lights['Smax'] <= lights['LmaxHit'] <= lights['Lmax'],
        'payload': abi['productionPayloadBytes'] == 6 * 4 <= abi['hardPayloadCapBytes'] == 64,
        'combined primary': primary == total['primaryRaysMax'],
        'combined shadow': shadow == total['shadowRaysMax'],
        'total rays': primary + shadow == total['primaryPlusShadowMax'],
        'feedback taps': g['B'] * g['R'] * g['feedbackSnapshotQueriesPerGiHitMax'] * g['neighborsPerQueryMax'] == total['feedbackNeighborTapsMax'],
    }
    failures = [k for k, ok in checks.items() if not ok]
    if failures:
        raise ValueError(', '.join(failures))


if __name__ == '__main__':
    data = Path(__file__).with_name('Freeze.json').read_bytes()
    check(json.loads(data))
    print('Verified pinned Git inputs and budget arithmetic (not GPU acceptance).')
    print('Freeze.json SHA-256:', hashlib.sha256(data).hexdigest())
