"""R3 shade-source acceptance: ambient vs published probe vs invalid snapshot, one term only."""
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
summary = {'api': {}}
if d11:
    check(d11.get('passed') is True, 'D3D11: fixture did not pass')
    check(d11.get('denial') == 'Backend', 'D3D11: gate did not deny with Backend')
    check(d11.get('buildCount', -1) == 0, 'D3D11: RTAS was built')
    check(not d11.get('error'), f"D3D11 error: {d11.get('error')}")
    summary['api']['D3D11'] = {'denial': d11.get('denial'), 'buildCount': d11.get('buildCount')}
if d12:
    check(d12.get('passed') is True, 'D3D12: fixture did not pass')
    check(not d12.get('error'), f"D3D12 error: {d12.get('error')}")
    check(d12.get('payloadBytes') == 24 and d12.get('requestBytes') == 48, 'D3D12: ABI changed')
    check(d12.get('instances') == 1, 'D3D12: receiver instance missing')
    check(d12.get('giProducerAllocated') is False, 'D3D12: GI producer was allocated by the SSR-only fixture')
    check(d12.get('ssrPassInstantiated') is False, 'D3D12: SSR pass was instantiated')
    shots = {s['name']: s for s in d12.get('shots', [])}
    expected = {
        'ambient': 'ambient', 'probe published': 'probe', 'version mismatch': 'ambient',
        'unpublished neighbours': 'ambient', 'inactive neighbours': 'ambient', 'probe restored': 'probe'
    }
    ambient = .96 * 2 / 3.141592653589793
    probe = .96 * 6 / 3.141592653589793
    for name, source in expected.items():
        check(name in shots, f'D3D12: missing shot {name}')
        if name not in shots: continue
        shot = shots[name]
        check(shot['expected'] == source, f'D3D12: {name} source label changed')
        reference = probe if source == 'probe' else ambient
        check(abs(shot['actual'] - reference) <= shot['tolerance'],
              f'D3D12: {name} radiance {shot["actual"]} != {reference}')
        check(shot['hit'] and shot['finite'] and shot['passed'], f'D3D12: {name} shot failed')
    if 'probe restored' in shots and 'ambient' in shots:
        check(abs(shots['probe restored']['actual'] - (ambient + probe)) > 1e-3,
              'D3D12: ambient and probe were both added')
    summary['api']['D3D12'] = {
        'instances': d12.get('instances'), 'dispatches': d12.get('dispatches'),
        'ambientExpected': ambient, 'probeExpected': probe,
        'shots': [{k: s[k] for k in ('name', 'expected', 'actual', 'reference', 'hit', 'passed')}
                  for s in d12.get('shots', [])]
    }
summary['status'] = 'passed' if not errors else 'failed'
summary['errors'] = errors
(out / 'Summary.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf8')
print(json.dumps({'status': summary['status'], 'errors': errors, 'D3D12': summary['api'].get('D3D12', {})}, indent=2))
sys.exit(0 if not errors else 1)
