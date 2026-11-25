"""R5 acceptance: reflection full-material hit shading, light/shadow budgets, no probe feedback."""
import json, sys
from pathlib import Path

out = Path(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).resolve().parent / 'evidence')
errors = []

def check(ok, message):
    if not ok: errors.append(message)

def load(name):
    path = out / name
    if not path.exists():
        errors.append(f'missing {name}')
        return None
    return json.loads(path.read_text())

d11, d12 = load('D3D11/Report.json'), load('D3D12/Report.json')
summary = {'api': {}, 'checks': {}}

if d11:
    check(d11.get('passed') is True, 'D3D11: fixture did not pass')
    check(d11.get('denial') == 'RayTracingUnsupported', 'D3D11: gate did not deny with RayTracingUnsupported')
    check(d11.get('buildCount', -1) == 0 and d11.get('instances', -1) == 0, 'D3D11: RT resources were built')
    check(d11.get('dispatchCount', -1) == 0, 'D3D11: rays were dispatched')
    check(not d11.get('error'), f"D3D11 error: {d11.get('error')}")
    summary['api']['D3D11'] = {'denial': d11.get('denial'), 'buildCount': d11.get('buildCount'),
                               'dispatchCount': d11.get('dispatchCount')}

if d12:
    check(d12.get('passed') is True, 'D3D12: fixture did not pass')
    check(not d12.get('error'), f"D3D12 error: {d12.get('error')}")
    for key, value in [('payloadBytes', 24), ('requestBytes', 48), ('members', 9), ('instances', 9),
                       ('disabledSubmeshes', 0), ('registeredLights', 66), ('uploadedLights', 64), ('tableOverflow', 2)]:
        check(d12.get(key) == value, f'D3D12: {key} changed ({d12.get(key)})')
    check(d12.get('probeProducerAllocated') is False, 'D3D12: a GI probe producer was allocated')
    check(d12.get('surfaceCacheAllocated') is False, 'D3D12: surface cache resources were allocated')
    check(d12.get('noRebuild') is True, 'D3D12: light/shadow changes rebuilt the TLAS')
    check(d12.get('released') is True, 'D3D12: resources were not released')
    check(d12.get('feedbackIdentical') is True, 'D3D12: reflection radiance changed under probe feedback')
    check(d12.get('dispatchCount', 0) > 0, 'D3D12: no dispatch recorded')
    shots = d12.get('shots', [])
    check(len(shots) >= 20 and all(s.get('passed') for s in shots), 'D3D12: a reflection shot failed')
    contour = d12.get('contour', {})
    check(contour.get('passed') is True and contour.get('hits') == 10 and contour.get('misses') == 10
          and contour.get('mismatches') == 0, 'D3D12: cutout contour changed')
    seam = d12.get('seam', {})
    check(seam.get('passed') is True, 'D3D12: adjacent-material seam changed')
    gpu = d12.get('gpuTimingMeasured') is True
    check(gpu == (d12.get('gpuSamples', 0) > 0 and d12.get('gpuMs', 0) > 0), 'D3D12: inconsistent GPU timing report')
    support = {r['name']: r for r in d12.get('materialRows', [])}
    for name in ['dielectric', 'metal gloss', 'normal map', 'cutout', 'blocker', 'budget blocker']:
        check(name in support and support[name]['dxrPass'] >= 0, f'D3D12: missing GlxDXR pass for {name}')
    check('_METALLICSPECGLOSSMAP' in support.get('metal gloss', {}).get('keywords', ''), 'D3D12: metal gloss keyword missing')
    check('_NORMALMAP' in support.get('normal map', {}).get('keywords', ''), 'D3D12: normal map keyword missing')
    check('_ALPHATEST_ON' in support.get('cutout', {}).get('keywords', ''), 'D3D12: cutout keyword missing')
    summary['api']['D3D12'] = {
        'members': d12.get('members'), 'instances': d12.get('instances'),
        'registeredLights': d12.get('registeredLights'), 'uploadedLights': d12.get('uploadedLights'),
        'tableOverflow': d12.get('tableOverflow'), 'dispatchCount': d12.get('dispatchCount'),
        'submitAverageMs': d12.get('submitAverageMs'), 'gpuSamples': d12.get('gpuSamples'),
        'gpuTimingMeasured': d12.get('gpuTimingMeasured'), 'gpuMsPerDispatch': d12.get('gpuMsPerDispatch'),
        'feedbackDelta': d12.get('feedbackDelta'), 'feedbackIdentical': d12.get('feedbackIdentical'),
        'contour': {k: contour.get(k) for k in ('hits', 'expectedHits', 'misses', 'expectedMisses', 'mismatches', 'passed')},
        'seam': {k: seam.get(k) for k in ('name', 'ownError', 'crossError', 'passed')},
        'materials': [{k: r[k] for k in ('name', 'shader', 'keywords', 'dxrPass', 'passEnabled', 'queue', 'surface', 'supported')}
                      for r in d12.get('materialRows', [])],
        'shots': [{k: s.get(k) for k in ('name', 'expectedSource', 'expectedRgb', 'actualRgb', 'maxRelativeError',
                                          'expectedFlags', 'actualFlags', 'expectedCounts', 'actualCounts', 'passed')}
                  for s in shots]
    }

summary['status'] = 'passed' if not errors else 'failed'
summary['errors'] = errors
(out / 'Summary.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf8')
print(json.dumps({'status': summary['status'], 'errors': errors, 'D3D12': summary['api'].get('D3D12', {})}, indent=2))
sys.exit(0 if not errors else 1)
