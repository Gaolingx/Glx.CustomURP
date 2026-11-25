"""R4 acceptance: registration scope, unsupported material accounting, payload/variants, hit cost."""
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
variants = load('Variants.json')
summary = {'api': {}, 'checks': {}}

if d11:
    check(d11.get('passed') is True, 'D3D11: fixture did not pass')
    check(d11.get('denial') == 'RayTracingUnsupported', 'D3D11: gate did not deny with RayTracingUnsupported')
    check(d11.get('buildCount', -1) == 0 and d11.get('instances', -1) == 0, 'D3D11: RT resources were built')
    check(not d11.get('error'), f"D3D11 error: {d11.get('error')}")
    summary['api']['D3D11'] = {'denial': d11.get('denial'), 'buildCount': d11.get('buildCount')}

if d12:
    check(d12.get('passed') is True, 'D3D12: fixture did not pass')
    check(not d12.get('error'), f"D3D12 error: {d12.get('error')}")
    check(d12.get('payloadBytes') == 24, 'D3D12: payload is not 24 bytes')
    check(d12.get('requestBytes') == 48, 'D3D12: request ABI changed')
    check(d12.get('members') == 3, 'D3D12: registered member count changed')
    check(d12.get('instances') == 3, 'D3D12: TLAS instance count changed')
    check(d12.get('unsupportedInstances') == 3, 'D3D12: unsupported instance count changed')
    check(d12.get('disabledSubmeshes') == 1, 'D3D12: disabled submesh count changed')
    check(d12.get('skinnedSkipped') == 1, 'D3D12: skinned control missing')
    check(d12.get('probeProducerAllocated') is False, 'D3D12: a GI probe producer was allocated')
    check(d12.get('surfaceCacheAllocated') is False, 'D3D12: surface cache resources were allocated')
    check(len(d12.get('shots', [])) >= 9 and all(s.get('passed') for s in d12.get('shots', [])),
          'D3D12: a material coverage shot failed')
    support = {r['name']: r for r in d12.get('materialRows', [])}
    for name, expected in [('lit emission', True), ('lit cutout', True), ('simple lit', False),
                           ('complex lit', False), ('transparent lit', False), ('unregistered lit', True)]:
        check(name in support, f'D3D12: missing material row {name}')
        if name in support:
            check(support[name]['supported'] is expected, f'D3D12: {name} support classification changed')
            check(support[name]['dxrPasses'] == (1 if support[name]['shader'] == 'Universal Render Pipeline/Lit' else 0),
                  f'D3D12: {name} GlxDXR pass count changed')
    summary['api']['D3D12'] = {
        'members': d12.get('members'), 'instances': d12.get('instances'),
        'unsupportedInstances': d12.get('unsupportedInstances'), 'disabledSubmeshes': d12.get('disabledSubmeshes'),
        'payloadBytes': d12.get('payloadBytes'), 'requestBytes': d12.get('requestBytes'),
        'dispatchCount': d12.get('dispatchCount'), 'submitAverageMs': d12.get('submitAverageMs'),
        'gpuTimingMeasured': d12.get('gpuTimingMeasured'), 'gpuMsPerDispatch': d12.get('gpuMsPerDispatch'),
        'gpuSamples': d12.get('gpuSamples'), 'lightSelected': d12.get('lightSelected'),
        'lightShadowRays': d12.get('lightShadowRays'), 'lightOverflow': d12.get('lightOverflow'),
        'materials': [{k: r[k] for k in ('name', 'shader', 'hasDxrPass', 'supported', 'dxrPasses', 'surface', 'queue', 'keywords')}
                      for r in d12.get('materialRows', [])],
        'shots': d12.get('shots')
    }

if variants:
    check(variants.get('distinctSets') == 16, 'variants: Lit GlxDXR is not the frozen 16-set space')
    check(not any(set(s.split()) - {'_NORMALMAP', '_METALLICSPECGLOSSMAP', '_EMISSION', '_ALPHATEST_ON'}
                  for s in variants.get('keywordSets', [])), 'variants: unexpected keyword used')
    summary['checks']['variants'] = {'distinctSets': variants.get('distinctSets'), 'callbacks': variants.get('callbacks')}
else:
    check(False, 'variants: audit missing')

summary['status'] = 'passed' if not errors else 'failed'
summary['errors'] = errors
(out / 'Summary.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf8')
print(json.dumps({'status': summary['status'], 'errors': errors, 'D3D12': summary['api'].get('D3D12', {})}, indent=2))
sys.exit(0 if not errors else 1)
