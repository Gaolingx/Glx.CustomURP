"""Recompute the analytic planar-mirror reference for the B0 source-switch scene and gate the R3 image evidence."""
import json, math, struct, sys
from pathlib import Path

OUT = Path(sys.argv[1])
errors = []
def require(x, msg):
    if not x: errors.append(msg)

def load(p):
    return json.loads(p.read_text())

MIRROR_Z, MIRROR_Y, MIRROR_HALF = 3.98, 2.0, 1.0
FOV, EMISSION, ROI_PAD = 60.0, 2.0, 6.0
EMISSION_Y = .2126 * EMISSION
CAM = (0.0, 1.6, -2.5)

inputs = load(OUT / 'Inputs.json')
reports = {}
for api, name in [(11, 'D3D11'), (12, 'D3D12')]:
    rp = OUT / name / 'Report.json'
    if not rp.exists():
        errors.append(f'missing {rp}')
        continue
    reports[api] = load(rp)
guids = {r['guid'] for r in reports.values()}
require(len(guids) == 1 and len(next(iter(guids), '')) == 32, 'one build GUID across APIs')

def project(rep, world):
    w, h = rep['width'], rep['height']
    tanv = math.tan(math.radians(rep['fov']) / 2)
    tanh = tanv * (w / h)
    depth = world[2] - rep['camZ']
    nx = (world[0] - rep['camX']) / (tanh * depth)
    ny = (world[1] - rep['camY']) / (tanv * depth)
    return (nx + 1) * .5 * w, (ny + 1) * .5 * h

def screen_box(rep, row, half, virtual):
    z = 2 * MIRROR_Z - row['z'] if virtual else row['z']
    xs, ys = [], []
    for corner in range(8):
        x = row['x'] + (-half if (corner & 1) == 0 else half)
        y = row['y'] + (-half if (corner & 2) == 0 else half)
        zz = z + (-half if (corner & 4) == 0 else half)
        px, py = project(rep, (x, y, zz))
        xs.append(px); ys.append(py)
    return min(xs), min(ys), max(xs), max(ys)

def roi_rect(rep, stage):
    w, h = rep['width'], rep['height']
    half = stage['half']
    minx = miny = float('inf'); maxx = maxy = float('-inf')
    for row in stage['rows']:
        x0, y0, x1, y1 = screen_box(rep, row, half, True)
        minx, maxx = min(minx, x0), max(maxx, x1)
        miny, maxy = min(miny, y0), max(maxy, y1)
    x0 = min(max(math.floor(minx - ROI_PAD), 0), w - 2)
    y0 = min(max(math.floor(miny - ROI_PAD), 0), h - 2)
    x1 = min(max(math.ceil(maxx + ROI_PAD), 1), w - 1)
    y1 = min(max(math.ceil(maxy + ROI_PAD), 1), h - 1)
    return x0, y0, max(1, x1 - x0), max(1, y1 - y0)

def rays(rep, rect):
    w, h = rep['width'], rep['height']
    tanv = math.tan(math.radians(rep['fov']) / 2)
    tanh = tanv * (w / h)
    x0, y0, rw, rh = rect
    table = []
    for y in range(rh):
        for x in range(rw):
            u = (x0 + x + .5) / w * 2 - 1
            v = (y0 + y + .5) / h * 2 - 1
            d = (u * tanh, v * tanv, 1.0)
            n = math.sqrt(sum(c * c for c in d))
            table.append(tuple(c / n for c in d))
    return table

def ray_box(o, d, c, half):
    near, far = 0.0, 1e9
    for axis in range(3):
        lo, hi = c[axis] - half, c[axis] + half
        if abs(d[axis]) < 1e-8:
            if o[axis] < lo or o[axis] > hi: return False
            continue
        a, b = (lo - o[axis]) / d[axis], (hi - o[axis]) / d[axis]
        if a > b: a, b = b, a
        near, far = max(near, a), min(far, b)
        if near > far: return False
    return far >= max(near, 0.0)

def offscreen(rep, row, half):
    x0, y0, x1, y1 = screen_box(rep, row, half, False)
    return x1 < -8 or x0 > rep['width'] + 8 or y1 < -8 or y0 > rep['height'] + 8

def reference(rep, table, pose, half):
    plane = MIRROR_Z - rep['camZ']
    mask = bytearray(len(table))
    ref_count = field = 0
    for i, d in enumerate(table):
        if d[2] <= 0: continue
        if ray_box(CAM, d, pose, half): continue
        s = plane / d[2]
        o = (rep['camX'] + d[0] * s, rep['camY'] + d[1] * s, MIRROR_Z)
        if abs(o[0]) > MIRROR_HALF or abs(o[1] - MIRROR_Y) > MIRROR_HALF: continue
        field += 1
        if ray_box(o, (d[0], d[1], -d[2]), pose, half):
            mask[i] = 1
            ref_count += 1
    return mask, ref_count, field

def check_stage(rep, stage, table, key, folder):
    x0, y0, rw, rh = roi_rect(rep, stage)
    require((stage['roiX'], stage['roiY'], stage['roiW'], stage['roiH']) == (x0, y0, rw, rh),
            f'{key} recorded ROI mismatch')
    errs = []
    if not stage['rows']:
        return [f'{key} rows']
    union = bytearray(rw * rh)
    off_union = bytearray(rw * rh)
    for row in stage['rows']:
        pose = (row['x'], row['y'], row['z'])
        mask, count, field = reference(rep, table, pose, stage['half'])
        if abs(count - row['refPixels']) > 4:
            errs.append(f"{key} frame {row['frame']} refPixels {row['refPixels']} != {count}")
        expected = EMISSION_Y * count / max(field, 1)
        if abs(expected - row['refMeanY']) > 1.5e-3:
            errs.append(f"{key} frame {row['frame']} refMeanY {row['refMeanY']} != {expected}")
        off = offscreen(rep, row, stage['half'])
        if off != row['offscreen']:
            errs.append(f"{key} frame {row['frame']} offscreen flag")
        for i, v in enumerate(mask):
            union[i] |= v
            if off: off_union[i] |= v
    base = OUT / folder / f"{stage['stage']}-{stage['renderer']}"
    saved_ref = saved_off = None
    for suffix, expected in (('.refmask', union), ('.offmask', off_union), ('.hitmask', None)):
        p = Path(str(base) + suffix)
        if not p.exists():
            errs.append(f'{key} missing {suffix}')
            continue
        saved = p.read_bytes()
        if len(saved) != rw * rh:
            errs.append(f'{key} {suffix} size')
            continue
        if suffix == '.refmask': saved_ref = saved
        if suffix == '.offmask': saved_off = saved
        if expected is not None:
            diff = sum(1 for a, b in zip(saved, expected) if a != bool(b))
            limit = max(8, int(0.005 * max(1, sum(expected))))
            if diff > limit: errs.append(f'{key} {suffix} differs in {diff} pixels')
    hitpath = Path(str(base) + '.hitmask')
    offhitpath = Path(str(base) + '.offhit')
    if hitpath.exists() and offhitpath.exists() and saved_ref is not None and saved_off is not None and len(hitpath.read_bytes()) == rw * rh and len(offhitpath.read_bytes()) == rw * rh:
        hit = hitpath.read_bytes()
        offhit = offhitpath.read_bytes()
        ref_n = sum(saved_ref)
        hit_n = sum(hit)
        common = sum(1 for a, b in zip(saved_ref, hit) if a and b)
        off_ref_n = sum(saved_off)
        off_hit = sum(1 for a, b in zip(saved_off, offhit) if a and b)
        m = stage['metrics']
        if abs(ref_n - m['refUnionPixels']) > max(8, int(0.005 * max(1, ref_n))): errs.append(f'{key} ref union mismatch')
        if hit_n != m['detectedUnionPixels']: errs.append(f'{key} hit union mismatch')
        if abs(common / max(ref_n, 1) - m['recallUnion']) > 1e-3: errs.append(f'{key} recall mismatch')
        if abs((hit_n - common) / max(ref_n, 1) - m['falsePositiveFraction']) > 1e-3: errs.append(f'{key} fp mismatch')
        if abs(off_ref_n - m['offRefPixels']) > max(8, int(0.005 * max(1, off_ref_n))): errs.append(f'{key} off union mismatch')
        if abs(off_hit / max(off_ref_n, 1) - m['recallOffscreen']) > 1e-3: errs.append(f'{key} off recall mismatch')
    else:
        errs.append(f'{key} missing hit masks')
    return errs

def stage_of(rep, name, renderer):
    return next(s for s in rep['stages'] if s['stage'] == name and s['renderer'] == renderer)

def gate(rep, key, errs):
    require(not rep.get('error'), f'{key} error {rep.get("error")}')
    require(rep['passed'], f'{key} not passed')
    require(rep['giProducerAllocated'] is False and rep['giAtlasAllocated'] is False, f'{key} GI producer')
    require(rep['offNonBlack'] > 1000, f'{key} RT-off blank')
    if key == 'D3D11':
        require(rep['denial'] == 'Backend', 'D3D11 gate decision')
        require(rep['sceneBuilds'] == 0 and rep['sceneInstances'] == 0 and rep['subscribers'] == 0,
                'D3D11 allocated RT resources')
    else:
        require(rep['supportsRayTracing'], 'D3D12 RT support')
        require(rep['sceneRequests'] == 'Reflections', 'SSR-only request scope')
        require(rep['sceneInstances'] > 0 and rep['sceneBuilds'] > 0, 'D3D12 never built')
    for stage in rep['stages']:
        if stage['stage'] in ('off', 'offAgain', 'transparent', 'transparentOff'):
            continue
        table = rays(rep, roi_rect(rep, stage))
        errs += check_stage(rep, stage, table, f"{key}/{stage['stage']}/{stage['renderer']}", key)
    for r in range(3):
        sw, woff = stage_of(rep, 'switch', r), stage_of(rep, 'switchOff', r)
        exit_stage, coat = stage_of(rep, 'exit', r), stage_of(rep, 'clearCoat', r)
        trans, transoff = stage_of(rep, 'transparent', r), stage_of(rep, 'transparentOff', r)
        for stage in (sw, woff, exit_stage, coat):
            require(stage['metrics']['refUnionPixels'] > 0, f'{key}/{stage["stage"]}/{r} empty reference')
        if key == 'D3D12' and r < 2:
            require(sw['metrics']['detectedUnionPixels'] > 0, f'{key}/switch/{r} no detected pixels')
        require(trans['metrics']['transparentMeanBlue'] > .01, f'{key}/transparent/{r} blank control')
        require(transoff['metrics']['transparentMutualMaxDifference'] == 0,
                f'{key}/transparent/{r} consumed the opaque trace')
        if key == 'D3D12' and r < 2:
            require(sw['metrics']['recallUnion'] >= .90, f'{key}/switch/{r} recall')
            require(sw['metrics']['falsePositiveFraction'] <= .05, f'{key}/switch/{r} false positive')
            require(sw['metrics']['recallOffscreen'] >= .90, f'{key}/switch/{r} off-screen recall')
            require(sw['metrics']['seamSpan'] <= .10, f'{key}/switch/{r} seam')
            require(sw['metrics']['stepVsReferenceMax'] <= .10, f'{key}/switch/{r} step')
            require(sw['metrics']['servedFrames'] >= 10, f'{key}/switch/{r} core frames')
            require(woff['metrics']['recallOffscreen'] <= .05, f'{key}/switchOff/{r} recovered')
            require(exit_stage['metrics']['trailFrames'] <= 2, f'{key}/exit/{r} trail')
            require(.8 <= coat['metrics']['clearCoatRatio'] <= 1.25, f'{key}/clearCoat/{r} ratio')
        elif key == 'D3D12':
            # Deferred composes the SSL blend while writing the GBuffer, so it must reach the
            # reflection like the forward renderers; the untouched case is only a recorded difference.
            require(sw['metrics']['falsePositiveFraction'] <= .05, f'{key}/Deferred/{r} false positive')
            require(coat['metrics']['recallUnion'] > .5,
                    f'{key}/Deferred/{r} forward-drawn clear coat did not resolve the hybrid')
            if sw['metrics']['recallUnion'] >= .02:
                require(sw['metrics']['recallUnion'] >= .90, f'{key}/Deferred/{r} recall')
                require(sw['metrics']['recallOffscreen'] >= .90, f'{key}/Deferred/{r} off-screen recall')
                require(sw['metrics']['seamSpan'] <= .10, f'{key}/Deferred/{r} seam')
                require(sw['metrics']['stepVsReferenceMax'] <= .10, f'{key}/Deferred/{r} step')
                require(sw['metrics']['servedFrames'] >= 10, f'{key}/Deferred/{r} core frames')
                require(woff['metrics']['recallOffscreen'] <= .05, f'{key}/Deferred/{r} recovered')
                require(exit_stage['metrics']['trailFrames'] <= 2, f'{key}/Deferred/{r} trail')
                require(.8 <= coat['metrics']['clearCoatRatio'] <= 1.25, f'{key}/Deferred/{r} ratio')
            else:
                require(abs(sw['metrics']['recallUnion'] - woff['metrics']['recallUnion']) <= .02,
                        f'{key}/Deferred/{r} changed without reaching the reflection')
        else:
            require(abs(sw['metrics']['recallUnion'] - woff['metrics']['recallUnion']) <= .02,
                    f'{key}/switch/{r} diverged from RT-off')
            require(sw['metrics']['recallOffscreen'] <= .05 and woff['metrics']['recallOffscreen'] <= .05,
                    f'{key}/off-path/{r} produced a silhouette')

for api, name in [(11, 'D3D11'), (12, 'D3D12')]:
    rep = reports.get(api)
    if not rep: continue
    require(rep['api'] == ('Direct3D11' if name == 'D3D11' else 'Direct3D12'), f'{name} actual API {rep["api"]}')
    require(rep['development'], f'{name} not a development build')
    require(rep['payloadBytes'] == 24 and rep['requestBytes'] == 48, f'{name} ABI bytes')
    require((rep['width'], rep['height']) == (1920, 1080), f'{name} resolution')
    gate(rep, name, errors)
    for r in range(3):
        off, again = stage_of(rep, 'off', r), stage_of(rep, 'offAgain', r)
        require(off['frameHash'] and off['frameHash'] == again['frameHash'],
                f'{name}/off/{r} RT-off replay differs')
        if r < 2:
            require(off['nonBlack'] > 1000 and again['nonBlack'] > 1000, f'{name}/off/{r} blank replay')
        crop = OUT / name / f"off-{r}-{rep['api']}.crop"
        require(crop.exists(), f'{name}/off/{r} missing crop')
        if crop.exists():
            vals = [x[0] for x in struct.iter_unpack('<4e', crop.read_bytes())]
            require(all(math.isfinite(v) for v in vals), f'{name}/off/{r} non-finite crop')
        a = OUT / name / f'switch-{r}-100.roi'
        b = OUT / name / f'switchOff-{r}-100.roi'
        require(a.exists() and b.exists(), f'{name}/switch/{r} missing on/off key pair')
        if a.exists() and b.exists():
            same = a.read_bytes() == b.read_bytes()
            if name == 'D3D11':
                require(same, f'{name}/switch/{r} hybrid-off key image changed')
            else:
                require(not same, f'{name}/switch/{r} hybrid produced no image change')
        for stage in ('switch', 'exit', 'clearCoat'):
            keyframe = 5 if stage == 'exit' else 100
            p = OUT / name / f'{stage}-{r}-{keyframe}.roi'
            require(p.exists(), f'{name}/{stage}/{r} missing key image')
            if not p.exists(): continue
            vals = [x[0] for x in struct.iter_unpack('<4e', p.read_bytes())]
            require(all(math.isfinite(v) for v in vals), f'{name}/{stage}/{r} non-finite ROI')
            if name == 'D3D12' and r < 2:
                require(sum(1 for v in vals[1:] if v > .1) > 16,
                        f'{name}/{stage}/{r} ROI did not recover the donor')

summary = {'passed': not errors, 'errors': errors, 'guids': sorted(guids), 'base': inputs.get('base'),
           'knownDifferences': {r['api']: r.get('knownDifferences', []) for r in reports.values()},
           'stages': {r['api']: [{'stage': s['stage'], 'renderer': s['renderer'],
                                  'roi': [s['roiX'], s['roiY'], s['roiW'], s['roiH']],
                                  'recallUnion': s['metrics']['recallUnion'],
                                  'recallOffscreen': s['metrics']['recallOffscreen'],
                                  'falsePositiveFraction': s['metrics']['falsePositiveFraction'],
                                  'seamSpan': s['metrics']['seamSpan'],
                                  'stepVsReferenceMax': s['metrics']['stepVsReferenceMax'],
                                  'servedFrames': s['metrics']['servedFrames'],
                                  'trailFrames': s['metrics']['trailFrames'],
                                  'clearCoatRatio': s['metrics']['clearCoatRatio'],
                                  'transparentMutualMaxDifference': s['metrics']['transparentMutualMaxDifference'],
                                  'instances': s['instances'], 'builds': s['builds'], 'gate': s['gate']}
                      for s in r['stages']] for r in reports.values()}}
(OUT / 'Summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(errors)
sys.exit(bool(errors))
