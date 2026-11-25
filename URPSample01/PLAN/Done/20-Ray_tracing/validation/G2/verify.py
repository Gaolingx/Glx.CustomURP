"""G2 acceptance: producer counters, publication, maintenance and settle from the Player report."""
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
    check(d11.get('denial') == 'RayTracingUnsupported', 'D3D11: gate did not deny with RayTracingUnsupported')
    check(d11.get('allocated') is False, 'D3D11: producer allocated resources')
    check(d11.get('buildCount', -1) == 0, 'D3D11: RTAS was built')
    check(d11.get('updateCount', -1) == 0, 'D3D11: probe batch dispatched')
    check(not d11.get('error'), f"D3D11 error: {d11.get('error')}")
    summary['api']['D3D11'] = {'denial': d11.get('denial'), 'allocated': d11.get('allocated'),
                               'buildCount': d11.get('buildCount'), 'updateCount': d11.get('updateCount')}

if d12:
    check(d12.get('passed') is True, 'D3D12: fixture did not pass')
    check(not d12.get('error'), f"D3D12 error: {d12.get('error')}")
    check(d12.get('supportsRayTracing') is True, 'D3D12: runtime reports no RT support')
    check(d12.get('buildCount', 0) >= 1, 'D3D12: no RTAS build')
    check(d12.get('batches', 0) >= 500, 'D3D12: too few probe batches')
    check(d12.get('lightsRegistered') == 3 and d12.get('lightsUploaded') == 3,
          'D3D12: unexpected light tallies')
    check(d12.get('publishedAfterFirst') == d12.get('budget'),
          'D3D12: first batch did not publish exactly the scheduled probes')
    check(d12.get('publishedAfterRevisit') == d12.get('probes'),
          'D3D12: a probe remained unpublished after one revisit')
    check(d12.get('revisitBatches') == 16, 'D3D12: static revisit is not ceil(N/B)')
    check(0 < d12.get('settleBatch', 0) <= 256, 'D3D12: light step did not settle within 256 batches')
    check(d12.get('settleResidualAt256', 1) <= 0.05, 'D3D12: old signal exceeds 5% at 256 batches')
    check(d12.get('settlePreRed', 0) > 0.01, 'D3D12: settle fixture has no light step')
    frames = d12.get('frames', [])
    check(len(frames) >= 500, 'D3D12: missing batch records')
    check(all(f.get('passed') for f in frames), 'D3D12: a batch violated the frozen budget')
    views = {v.get('name'): v for v in d12.get('views', [])}
    expected = ['unscheduled cold probe', 'room centre', 'near wall', 'relocated probe',
                'relocated probe stable', 'relocation removed', 'reactivated centre']
    for name in expected:
        check(name in views, f'D3D12: missing probe view {name}')
    if 'room centre' in views:
        check(views['room centre']['state'] == 0 and views['room centre']['published'] == 1,
              'D3D12: empty-region probe is not classified inactive')
    if 'near wall' in views:
        check(views['near wall']['state'] == 1, 'D3D12: near-wall probe is not active')
    if 'relocated probe' in views:
        length = views['relocated probe']['length']
        check(0.2 < length <= 0.451, 'D3D12: relocation offset outside (0.2, 0.45]')
    if 'relocated probe stable' in views and 'relocated probe' in views:
        drift = abs(views['relocated probe stable']['length'] - views['relocated probe']['length'])
        check(drift <= 1e-3, 'D3D12: relocation offset oscillates')
    if 'relocation removed' in views:
        check(views['relocation removed']['length'] <= 1e-3, 'D3D12: relocation offset did not clear')
    if 'reactivated centre' in views:
        check(views['reactivated centre']['state'] == 1, 'D3D12: probe did not reactivate')
    if 'unscheduled cold probe' in views:
        cold = views['unscheduled cold probe']
        check(cold['published'] == 0 and cold['irradianceRed'] == 0 and cold['finite'],
              'D3D12: cold tile is not an invalid zero tile')
    check('two cameras one update' in d12.get('checks', []), 'D3D12: shared-frame camera check missing')
    summary['api']['D3D12'] = {
        'buildCount': d12.get('buildCount'), 'updateCount': d12.get('updateCount'),
        'batches': len(frames), 'submitAverageMs': d12.get('submitAverageMs'),
        'submitMaxMs': d12.get('submitMaxMs'), 'publishedAfterFirst': d12.get('publishedAfterFirst'),
        'publishedAfterRevisit': d12.get('publishedAfterRevisit'), 'revisitBatches': d12.get('revisitBatches'),
        'settleBatch': d12.get('settleBatch'), 'settleResidualAt256': d12.get('settleResidualAt256'),
        'settlePreRed': d12.get('settlePreRed'), 'settleFinalRed': d12.get('settleFinalRed'),
        'lightsRegistered': d12.get('lightsRegistered'), 'lightsUploaded': d12.get('lightsUploaded'),
        'checks': d12.get('checks'), 'views': {k: v for k, v in views.items() if k != 'active count'}
    }

summary['status'] = 'passed' if not errors else 'failed'
summary['errors'] = errors
(out / 'Summary.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf8')
print(json.dumps({'status': summary['status'], 'errors': errors, 'D3D12': summary['api'].get('D3D12', {})}, indent=2))
sys.exit(0 if not errors else 1)
