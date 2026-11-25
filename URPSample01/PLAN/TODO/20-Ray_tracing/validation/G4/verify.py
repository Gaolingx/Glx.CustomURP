"""G4 acceptance: default-on relocation/classification counters, offsets and reactivation."""
import json, sys
from pathlib import Path

out = Path(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).resolve().parent / 'evidence')
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
    check(d11.get('allocated') is False, 'D3D11: producer allocated resources')
    check(d11.get('buildCount', -1) == 0, 'D3D11: RTAS was built')
    check(d11.get('batches', -1) == 0, 'D3D11: probe batch dispatched')
    check(not d11.get('error'), f"D3D11 error: {d11.get('error')}")
    summary['api']['D3D11'] = {'denial': d11.get('denial'), 'allocated': d11.get('allocated'),
                               'buildCount': d11.get('buildCount'), 'batches': d11.get('batches')}

if d12:
    check(d12.get('passed') is True, 'D3D12: fixture did not pass')
    check(not d12.get('error'), f"D3D12 error: {d12.get('error')}")
    check(d12.get('supportsRayTracing') is True, 'D3D12: runtime reports no RT support')
    check(d12.get('allocated') is True, 'D3D12: producer was not allocated')
    check(d12.get('buildCount', 0) >= 1, 'D3D12: no RTAS build')
    check(d12.get('lightsRegistered') == 2 and d12.get('lightsUploaded') == 2, 'D3D12: unexpected light tallies')
    frames = d12.get('frames', [])
    check(len(frames) == 96, f'D3D12: expected 96 batches, got {len(frames)}')
    check(all(f.get('passed') for f in frames), 'D3D12: a batch violated the frozen budget')
    check(all(f.get('active', 0) + f.get('inactive', 0) == 16 for f in frames), 'D3D12: active+inactive is not B')
    check(all(f.get('fixedRays') == 512 and f.get('rotated') == 96 * f.get('active', 0) for f in frames),
          'D3D12: maintenance/rotated ray counts are wrong')
    check(max((f.get('relocated', 0) for f in frames[:17]), default=0) >= 1,
          'D3D12: no relocated probe during the enclosed phase')

    views = {v.get('name'): v for v in d12.get('views', [])}
    def view(name):
        if name not in views:
            errors.append(f'D3D12: missing probe view {name}')
            return {}
        return views[name]

    enclosed = view('enclosed probe')
    check(enclosed.get('published') == 1 and enclosed.get('finite') is True, 'D3D12: enclosed probe is invalid')
    check(0.2 < enclosed.get('length', 0) <= 0.451, f"D3D12: relocation offset outside (0.2, 0.45] ({enclosed.get('length')})")
    stable = view('enclosed probe stable')
    check(abs(stable.get('length', 0) - enclosed.get('length', 0)) <= 1e-3, 'D3D12: relocation offset oscillates')
    for neighbour in (141, 143, 134, 150, 110, 174):
        n = view(f'relocated neighbour {neighbour}')
        check(n.get('length', 1) <= 1e-4, f'D3D12: relocation leaked into probe {neighbour}')
    centre = view('empty centre')
    check(centre.get('published') == 1 and centre.get('state') == 0, 'D3D12: empty centre is not inactive')
    near = view('near wall')
    check(near.get('state') == 1, 'D3D12: near-wall probe is not active')
    preserved = view('empty centre preserved')
    check(preserved.get('state') == 0 and abs(preserved.get('irradianceRed', 1) - centre.get('irradianceRed', 0)) <= 1e-6,
          'D3D12: an inactive probe tile was rewritten')
    removed = view('relocation removed')
    check(removed.get('length', 1) <= 1e-3, 'D3D12: relocation offset did not clear')
    reactivated = view('reactivated centre')
    check(reactivated.get('state') == 1, 'D3D12: probe did not reactivate on new geometry')
    measured = view('reactivated centre measured')
    check(measured.get('state') == 1 and measured.get('irradianceRed', 0) > 0,
          'D3D12: reactivated probe did not measure the new geometry')
    deactivated = view('deactivated centre')
    check(deactivated.get('state') == 0, 'D3D12: probe did not deactivate after removal')
    count = view('active count')
    check(0 < count.get('irradianceRed', 0) < 256, 'D3D12: classification did not split active/inactive')
    for name in ['enclosed relocation and classification', 'reactivation and deactivation']:
        check(name in d12.get('checks', []), f'D3D12: missing check {name}')

    summary['checks'] = {
        'batches': len(frames),
        'relocated': max((f.get('relocated', 0) for f in frames), default=0),
        'enclosedOffset': enclosed.get('length'),
        'stableDrift': abs(stable.get('length', 0) - enclosed.get('length', 0)),
        'emptyCentreState': centre.get('state'),
        'nearWallState': near.get('state'),
        'reactivatedState': reactivated.get('state'),
        'deactivatedState': deactivated.get('state'),
        'activeCount': count.get('irradianceRed'),
    }
    summary['api']['D3D12'] = {'buildCount': d12.get('buildCount'), 'batches': len(frames),
                               'lights': [d12.get('lightsRegistered'), d12.get('lightsUploaded')],
                               'checks': d12.get('checks')}

summary['status'] = 'passed' if not errors else 'failed'
summary['errors'] = errors
(out / 'Summary.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf8')
print(json.dumps({'status': summary['status'], 'errors': errors, 'D3D12': summary['api'].get('D3D12', {})}, indent=2))
sys.exit(0 if not errors else 1)
