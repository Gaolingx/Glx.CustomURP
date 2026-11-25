"""Gate the G3 probe-fallback evidence against the analytic constant-radiance room."""
import json, math, sys
from pathlib import Path

OUT = Path(sys.argv[1])
errors = []
def require(x, msg):
    if not x: errors.append(msg)

def load(p):
    return json.loads(p.read_text())

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

EXPECTED = .96 * 2.0          # oneMinusReflectivity * emission, un-premultiplied linear
ERROR_MAX = .10
FLOOR = .02
GRID = dict(x0=-3.5, y0=.5, z0=-3.5, spacing=1.0, nx=8, ny=4, nz=8)

def project(rep, world):
    w, h = rep['width'], rep['height']
    tanv = math.tan(math.radians(rep['fov']) / 2)
    tanh = tanv * (w / h)
    depth = world[2] - rep['camZ']
    nx = world[0] / (tanh * depth)
    ny = (world[1] - rep['camY']) / (tanv * depth)
    return (nx + 1) * .5 * w, (ny + 1) * .5 * h

def roi_rect(rep):
    half = 1.0
    xs, ys = [], []
    for corner in range(4):
        px, py = project(rep, ((-half if (corner & 1) == 0 else half),
                               rep['camY'] + (-half if (corner & 2) == 0 else half), rep['receiverZ']))
        xs.append(px); ys.append(py)
    inset = 6
    x0 = min(max(math.ceil(min(xs)) + inset, 0), rep['width'] - 2)
    y0 = min(max(math.ceil(min(ys)) + inset, 0), rep['height'] - 2)
    x1 = min(max(math.floor(max(xs)) - inset, 1), rep['width'] - 1)
    y1 = min(max(math.floor(max(ys)) - inset, 1), rep['height'] - 1)
    return x0, y0, max(1, x1 - x0), max(1, y1 - y0)

def stage_of(rep, name, renderer):
    return next(s for s in rep['stages'] if s['stage'] == name and s['renderer'] == renderer)

def gate(rep, key):
    require(not rep.get('error'), f'{key} error {rep.get("error")}')
    require(rep['passed'], f'{key} not passed')
    require(rep['development'], f'{key} not a development build')
    require((rep['width'], rep['height']) == (960, 540), f'{key} resolution')
    require(abs(rep['expectedY'] - EXPECTED) < 1e-6, f'{key} expectedY contract')
    rect = roi_rect(rep)
    for stage in rep['stages']:
        require((stage['roiX'], stage['roiY'], stage['roiW'], stage['roiH']) == rect,
                f"{key}/{stage['stage']}/{stage['renderer']} ROI mismatch")
        if stage['expectedY'] > 0:
            expected = EXPECTED * stage['coverage'] / 100
            require(abs(stage['expectedY'] - expected) < 1e-6,
                    f"{key}/{stage['stage']}/{stage['renderer']} expected share")
            recomputed = abs(stage['roiMeanY'] - expected) / max(abs(expected), .01)
            require(abs(recomputed - stage['relativeError']) < 1e-4,
                    f"{key}/{stage['stage']}/{stage['renderer']} relative error bookkeeping")

    if key == 'D3D11':
        require(not rep['supportsRayTracing'], 'D3D11 DXR support')
        for s in rep['stages']:
            require(s['gate'] == 'Backend', f"{key}/{s['stage']}/{s['renderer']} gate {s['gate']}")
            require(s['updates'] == 0, f"{key}/{s['stage']}/{s['renderer']} probe dispatch")
            require(s['producerAllocated'] is False, f"{key}/{s['stage']}/{s['renderer']} producer allocation")
            require(s['instances'] == 0 and s['builds'] == 0, f"{key}/{s['stage']}/{s['renderer']} RT scene")
        for r in range(3):
            off, again, probe = stage_of(rep, 'off', r), stage_of(rep, 'offAgain', r), stage_of(rep, 'probe', r)
            require(off['frameHash'] and off['frameHash'] == again['frameHash'], f'{key}/off/{r} replay differs')
            require(off['frameHash'] == probe['frameHash'], f'{key}/probe/{r} changed the D3D11 image')
        return

    require(rep['supportsRayTracing'], 'D3D12 DXR support')
    require(rep['sceneBuilds'] > 0 and rep['sceneInstances'] == 6, 'D3D12 scene registration')
    require(rep['subscribers'] == 1, 'GI-only scene subscription')
    require(rep['sceneRequests'] == 'GI', 'scene request scope')

    for r in range(3):
        off, again, probe = stage_of(rep, 'off', r), stage_of(rep, 'offAgain', r), stage_of(rep, 'probe', r)
        cold, half, both = stage_of(rep, 'cold', r), stage_of(rep, 'probeHalf', r), stage_of(rep, 'probeBoth', r)
        outside = stage_of(rep, 'probeOutside', r)
        require(off['frameHash'] and off['frameHash'] == again['frameHash'], f'D3D12/off/{r} replay differs')
        require(probe['updates'] > 0 and probe['generation'] > 0, f'D3D12/probe/{r} producer idle')
        require(probe['producerAllocated'], f'D3D12/probe/{r} no atlas')
        require(probe['producerBytes'] > 0, f'D3D12/probe/{r} zero bytes')
        require(probe['ssgiProbeFlag'], f'D3D12/probe/{r} SSGI fallback not forced to None')
        require(probe['subscribers'] == 1 and probe['requests'] == 'GI', f'D3D12/probe/{r} subscription')
        require(cold['notes'] in ('baked', 'probeReady'), f'D3D12/cold/{r} partial generation {cold["notes"]}')
        require(abs(cold['roiMeanY']) <= FLOOR or abs(cold['roiMeanY'] - EXPECTED) <= ERROR_MAX * EXPECTED,
                f'D3D12/cold/{r} value {cold["roiMeanY"]}')
        require(off['nonBlack'] > 1000, f'D3D12/off/{r} blank frame')
        require(probe['relativeError'] <= ERROR_MAX, f'D3D12/probe/{r} error {probe["relativeError"]}')
        require(half['halfScaleMax'] <= FLOOR, f'D3D12/probeHalf/{r} not linear {half["halfScaleMax"]}')
        require(both['relativeError'] <= ERROR_MAX, f'D3D12/probeBoth/{r} error {both["relativeError"]}')
        require(abs(outside['roiMeanY']) <= FLOOR, f'D3D12/probeOutside/{r} leaked {outside["roiMeanY"]}')
        require(off['frameHash'] != probe['frameHash'], f'D3D12/probe/{r} never changed the image')

for api, name in [(11, 'D3D11'), (12, 'D3D12')]:
    rep = reports.get(api)
    if not rep: continue
    require(rep['api'] == ('Direct3D11' if name == 'D3D11' else 'Direct3D12'), f'{name} actual API {rep["api"]}')
    gate(rep, name)

summary = {
    'passed': not errors, 'errors': errors, 'guids': sorted(guids), 'base': inputs.get('base'),
    'knownDifferences': {r['api']: r.get('knownDifferences', []) for r in reports.values()},
    'stages': {r['api']: [{'stage': s['stage'], 'renderer': s['renderer'], 'gate': s['gate'],
                           'coverage': s['coverage'], 'ssgi': s['ssgiIntensity'],
                           'roiMeanY': s['roiMeanY'], 'expectedY': s['expectedY'],
                           'relativeError': s['relativeError'], 'halfScaleMax': s['halfScaleMax'],
                           'notes': s['notes'], 'updates': s['updates'], 'generation': s['generation'],
                           'probeBytes': s['producerBytes'], 'instances': s['instances'],
                           'builds': s['builds'], 'subscribers': s['subscribers'],
                           'frameHash': s['frameHash']} for s in r['stages']] for r in reports.values()},
}
(OUT / 'Summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(errors)
sys.exit(bool(errors))
