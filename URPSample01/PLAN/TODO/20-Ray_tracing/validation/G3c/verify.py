"""Gate the G3c toggle/thin-wall evidence against the frozen analytic room."""
import json, math, sys
from pathlib import Path

OUT = Path(sys.argv[1])
errors = []
def require(x, msg):
    if not x: errors.append(msg)

def load(p):
    return json.loads(p.read_text())

INPUTS = load(OUT / 'Inputs.json')
reports = {}
for api, name in [(11, 'D3D11'), (12, 'D3D12')]:
    rp = OUT / name / 'Report.json'
    if not rp.exists():
        errors.append(f'missing {rp}')
        continue
    reports[api] = load(rp)
guids = {r['guid'] for r in reports.values()}
require(len(guids) == 1 and len(next(iter(guids), '')) == 32, 'one build GUID across APIs')

PROBE_Y = .96 * 2.0                     # oneMinusReflectivity * emission
FEEDBACK_Y = PROBE_Y / (1 - .25)        # emissive room fixed point with a 0.25 albedo
ERROR_MAX = .10
FLOOR = .02
OVERLAY = .05

def stage_of(rep, name, renderer):
    return next((s for s in rep['stages'] if s['stage'] == name and s['renderer'] == renderer), None)

def near(value, expected, tol):
    return abs(value - expected) <= tol * abs(expected)

def gate_d3d11(rep):
    require(not rep['supportsRayTracing'], 'D3D11 DXR support')
    for s in rep['stages']:
        require(s['gate'] == 'Backend', f"D3D11/{s['stage']}/{s['renderer']} gate {s['gate']}")
        require(s['updates'] == 0, f"D3D11/{s['stage']}/{s['renderer']} probe dispatch")
        require(s['producerAllocated'] is False, f"D3D11/{s['stage']}/{s['renderer']} producer allocation")
        require(s['instances'] == 0 and s['builds'] == 0, f"D3D11/{s['stage']}/{s['renderer']} RT scene")
    for r in range(3):
        off, cls, rel = stage_of(rep, 'off', r), stage_of(rep, 'classification', r), stage_of(rep, 'relocation', r)
        if not (off and cls and rel):
            errors.append(f'D3D11 missing stages for renderer {r}')
            continue
        require(off['frameHash'] == cls['frameHash'], f'D3D11/classification/{r} changed the image')
        require(off['frameHash'] == rel['frameHash'], f'D3D11/relocation/{r} changed the image')

def gate_d3d12(rep):
    require(rep['supportsRayTracing'], 'D3D12 DXR support')
    require(rep['sceneBuilds'] > 0, 'D3D12 scene was never built')
    require(rep['subscribers'] == 1, 'GI-only scene subscription')
    require(rep['sceneRequests'] == 'GI', 'scene request scope')
    for r in range(3):
        stages = {s['stage']: s for s in rep['stages'] if s['renderer'] == r}
        for name in ('off', 'classification', 'relocation', 'darkStep', 'both', 'cut', 'dual',
                     'feedback', 'feedbackDecay', 'thinWallFront', 'thinWallRear'):
            if name not in stages:
                errors.append(f'D3D12 missing {name}/{r}')
                return

        off, cls, rel = stages['off'], stages['classification'], stages['relocation']
        require(off['active'] == 256 and off['relocated'] == 0, f'off/{r} toggles were not off')
        require(off['roiMeanY'] <= .8 * PROBE_Y, f"off/{r} did not consume the black enclosures ({off['roiMeanY']})")
        require(off['nonBlack'] > 1000, f'off/{r} blank frame')
        require(cls['inactive'] > 0, f'classification/{r} has no inactive probes')
        require(near(cls['roiMeanY'], PROBE_Y, ERROR_MAX),
                f"classification/{r} not analytic ({cls['roiMeanY']} vs {PROBE_Y})")
        require(cls['roiMeanY'] >= 1.2 * off['roiMeanY'], f'classification/{r} did not lift the enclosure cells')
        require(rel['relocated'] > 0 and .2 < rel['offsetMax'] <= .45 + 1e-3,
                f"relocation/{r} offsets {rel['relocated']} max {rel['offsetMax']}")
        require(abs(rel['roiMeanY'] - cls['roiMeanY']) <= .02 * PROBE_Y,
                f"relocation/{r} changed the receiver ({rel['roiMeanY']} vs {cls['roiMeanY']})")

        dark = stages['darkStep']
        require(dark['inactiveStoredIrr'] > .5 * 2 * math.pi,
                f"darkStep/{r} inactive tile was rewritten ({dark['inactiveStoredIrr']})")
        require(dark['activeStoredIrr'] <= .1 * 2 * math.pi,
                f"darkStep/{r} active tile kept the old energy ({dark['activeStoredIrr']})")
        require(dark['roiMeanY'] <= OVERLAY * PROBE_Y, f"darkStep/{r} consumed frozen tiles ({dark['roiMeanY']})")

        both = stages['both']
        require(near(both['roiMeanY'], PROBE_Y, ERROR_MAX),
                f"both/{r} SSGI blend moved the ROI ({both['roiMeanY']})")
        require(both['pixelDiffMax'] <= ERROR_MAX * PROBE_Y, f"both/{r} per-pixel seam {both['pixelDiffMax']}")
        require(both['ssgiProbeFlag'] is True, f'both/{r} SSGI fallback not forced to None')

        cut = stages['cut']
        require(near(cut['roiMeanY'], PROBE_Y, ERROR_MAX), f"cut/{r} settled ROI {cut['roiMeanY']}")
        require(abs(cut['firstFrameY'] - cut['roiMeanY']) <= .1 * cut['roiMeanY'],
                f"cut/{r} first frame after the cut {cut['firstFrameY']} vs {cut['roiMeanY']}")
        require(near(cut['firstFrameY'], PROBE_Y, ERROR_MAX * 2), f"cut/{r} first frame {cut['firstFrameY']}")

        dual = stages['dual']
        require(dual['historyCount'] == 2, f"dual/{r} kept {dual['historyCount']} camera histories")
        require(dual['historyOk'] is True, f'dual/{r} missing a camera history entry')
        require(near(dual['roiMeanY'], PROBE_Y, ERROR_MAX), f"dual/{r} ROI {dual['roiMeanY']}")

        fb, decay = stages['feedback'], stages['feedbackDecay']
        require(near(fb['roiMeanY'], FEEDBACK_Y, ERROR_MAX),
                f"feedback/{r} fixed point {fb['roiMeanY']} vs {FEEDBACK_Y}")
        require(fb['activeStoredIrr'] > cls['activeStoredIrr'],
                f"feedback/{r} atlas did not gain ({fb['activeStoredIrr']})")
        require(decay['roiMeanY'] <= OVERLAY * FEEDBACK_Y,
                f"feedbackDecay/{r} left {decay['roiMeanY']} of the old signal")

        front, rear = stages['thinWallFront'], stages['thinWallRear']
        require(front['roiMeanY'] > .02, f"thinWall/{r} lit face is blank ({front['roiMeanY']})")
        require(rear['ratio'] <= OVERLAY, f"thinWall/{r} rear leaked {rear['ratio']}")

for api, name in [(11, 'D3D11'), (12, 'D3D12')]:
    rep = reports.get(api)
    if not rep: continue
    require(rep['api'] == ('Direct3D11' if name == 'D3D11' else 'Direct3D12'), f'{name} actual API {rep["api"]}')
    require(rep['passed'], f'{name} fixture gates failed: {rep.get("error")}')
    gate_d3d11(rep) if api == 11 else gate_d3d12(rep)

summary = {
    'passed': not errors, 'errors': errors, 'guids': sorted(guids), 'base': INPUTS.get('base'),
    'stages': {r['api']: [{'stage': s['stage'], 'renderer': s['renderer'], 'gate': s['gate'],
                           'feedback': s['feedback'], 'relocation': s['relocation'],
                           'classification': s['classification'], 'intensity': s['intensity'],
                           'roiMeanY': s['roiMeanY'], 'expectedY': s['expectedY'],
                           'relativeError': s['relativeError'], 'pixelDiffMax': s['pixelDiffMax'],
                           'ratio': s['ratio'], 'firstFrameY': s['firstFrameY'],
                           'active': s['active'], 'inactive': s['inactive'],
                           'relocated': s['relocated'], 'offsetMax': s['offsetMax'],
                           'activeStoredIrr': s['activeStoredIrr'],
                           'inactiveStoredIrr': s['inactiveStoredIrr'],
                           'historyCount': s['historyCount'], 'historyOk': s['historyOk'],
                           'ssgiProbeFlag': s['ssgiProbeFlag'], 'ssgiProduced': s['ssgiProduced'],
                           'updates': s['updates'], 'generation': s['generation'],
                           'probeBytes': s['producerBytes'], 'instances': s['instances'],
                           'builds': s['builds'], 'subscribers': s['subscribers'],
                           'frameHash': s['frameHash'], 'stateHash': s['stateHash']}
                          for s in r['stages']] for r in reports.values()},
}
(OUT / 'Summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(errors)
sys.exit(bool(errors))
