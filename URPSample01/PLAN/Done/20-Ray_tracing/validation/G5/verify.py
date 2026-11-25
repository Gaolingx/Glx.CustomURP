"""G5 acceptance: analytic feedback term, reflection non-interference, producer step/decay."""
import json, sys
from pathlib import Path

out = Path(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).resolve().parent / 'evidence')
Cap = 0.9
Irr = 6.0
InvPi = 0.31830988618
errors = []

def check(ok, message):
    if not ok: errors.append(message)

def load(api):
    path = out / api / 'Report.json'
    if not path.exists():
        errors.append(f'{api}: missing Report.json')
        return None
    return json.loads(path.read_text())

d11, d12 = load('D3D11'), load('D3D12')
summary = {'api': {}, 'checks': {}}

if d11:
    check(d11.get('passed') is True, 'D3D11: fixture did not pass')
    check(d11.get('denial') == 'Backend', 'D3D11: gate did not deny with Backend')
    check(d11.get('buildCount', -1) == 0, 'D3D11: RTAS was built')
    check(d11.get('dispatches', -1) == 0, 'D3D11: a ray was dispatched')
    check(not d11.get('error'), f"D3D11 error: {d11.get('error')}")
    summary['api']['D3D11'] = {'denial': d11.get('denial'), 'buildCount': d11.get('buildCount'),
                               'dispatches': d11.get('dispatches')}

if d12:
    check(d12.get('passed') is True, 'D3D12: fixture did not pass')
    check(not d12.get('error'), f"D3D12 error: {d12.get('error')}")
    check(d12.get('supportsRayTracing') is True, 'D3D12: runtime reports no RT support')
    check(d12.get('payloadBytes') == 24 and d12.get('requestBytes') == 48, 'D3D12: unexpected ABI')
    check(d12.get('giProducerAllocated') is True, 'D3D12: producers were not allocated')
    check(d12.get('ssrPassInstantiated') is False, 'D3D12: SSR consumer was instantiated')
    check(d12.get('gpuTimingMeasured') is False, 'D3D12: GPU timing unexpectedly reported')
    check(d12.get('feedbackCapBits') == 0x3F666666, 'D3D12: feedback cap is not 0.9f')

    shots = {s.get('name'): s for s in d12.get('shots', [])}
    expected = {
        'white albedo capped': (Cap * Irr * InvPi, 'probe feedback'),
        # _BaseColor is a [Gamma] property: sRGB 0.5 is uploaded as linear 0.214041.
        'gray albedo below cap': (0.21404114048223255 * Irr * InvPi, 'probe feedback'),
        'feedback disabled': (0.0, 'no feedback'),
        'reflection ambient, feedback on': (0.96 * 2.0 * InvPi, 'ambient'),
        'reflection ambient, feedback off': (0.96 * 2.0 * InvPi, 'ambient'),
        'invalid snapshot': (0.0, 'no feedback'),
        'inactive neighbours': (0.0, 'no feedback'),
        'unpublished neighbours': (0.0, 'no feedback'),
    }
    for name, (value, source) in expected.items():
        shot = shots.get(name)
        if shot is None:
            errors.append(f'D3D12: missing shot {name}')
            continue
        check(shot.get('passed') is True, f'D3D12: shot {name} did not pass')
        check(shot.get('hit') is True and shot.get('finite') is True, f'D3D12: shot {name} is not a finite hit')
        check(shot.get('expectedSource') == source, f'D3D12: shot {name} has the wrong source label')
        check(abs(shot.get('actual', 1e9) - value) <= 1e-3, f'D3D12: shot {name} = {shot.get("actual")} (wanted {value})')

    steps = {s.get('name'): s for s in d12.get('steps', [])}
    for name in ['steady feedback off', 'steady feedback on', 'decay feedback off', 'decay feedback on']:
        step = steps.get(name)
        if step is None:
            errors.append(f'D3D12: missing step {name}')
            continue
        check(step.get('active') == 16 and step.get('traced') == 2048 and step.get('fixedRays') == 512 and
              step.get('rotated') == 1536 and 0 <= step.get('shadowRays', 1e9) <= 4096,
              f'D3D12: {name} counters violate the frozen budget ({step})')
        check(step.get('passed') is True, f'D3D12: {name} did not pass')
    preOff = steps.get('steady feedback off', {}).get('pre', 0.0)
    preOn = steps.get('steady feedback on', {}).get('pre', 0.0)
    check(preOff > 0.01, 'D3D12: feedback-off steady irradiance is not measurable')
    check(preOn > preOff * 1.01, f'D3D12: feedback did not raise steady irradiance ({preOn} vs {preOff})')
    for name in ['decay feedback off', 'decay feedback on']:
        step = steps.get(name, {})
        check(step.get('batches') == 256, f'D3D12: {name} did not run 256 batches')
        check(step.get('residual', 1.0) <= 0.05, f'D3D12: {name} residual exceeds 5% ({step.get("residual")})')

    checks = d12.get('checks', [])
    for name in ['analytic feedback term', 'feedback raises steady irradiance', 'feedback decays within 256 batches']:
        check(name in checks, f'D3D12: missing check {name}')

    summary['checks'] = {
        'shots': {k: {'actual': v.get('actual'), 'expected': v.get('expected')} for k, v in shots.items()},
        'steady': {'feedbackOff': preOff, 'feedbackOn': preOn, 'ratio': preOn / preOff if preOff else None},
        'decay': {k: steps.get(k, {}).get('residual') for k in ['decay feedback off', 'decay feedback on']},
        'buildCount': d12.get('buildCount'), 'dispatches': d12.get('dispatches'),
    }
    summary['api']['D3D12'] = {'buildCount': d12.get('buildCount'), 'dispatches': d12.get('dispatches'),
                               'checks': checks, 'shots': len(shots), 'steps': len(steps)}

summary['status'] = 'passed' if not errors else 'failed'
summary['errors'] = errors
(out / 'Summary.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf8')
print(json.dumps({'status': summary['status'], 'errors': errors, 'D3D12': summary['api'].get('D3D12', {})}, indent=2))
sys.exit(0 if not errors else 1)
