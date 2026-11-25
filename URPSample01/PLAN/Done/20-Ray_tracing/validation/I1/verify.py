"""Gate the I1 switch-matrix evidence against the frozen analytic room."""
import json, math, sys
from pathlib import Path

OUT = Path(sys.argv[1])
errors = []
def require(x, msg):
    if not x: errors.append(msg)

def load(p):
    return json.loads(p.read_text())

PROBE_Y = .96 * 2.0
BUDGET = 32768
ERR = .10
MIRROR_ON = .5
MIRROR_OFF = .1

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

def stage_of(rep, name, renderer):
    return next((s for s in rep['stages'] if s['stage'] == name and s['renderer'] == renderer), None)

def near(value, expected, tol):
    return abs(value - expected) <= tol * abs(expected)

def gate_d3d11(rep):
    require(not rep['supportsRayTracing'], 'D3D11 DXR support')
    for s in rep['stages']:
        key = f"D3D11/{s['stage']}/{s['renderer']}"
        require(s['gateGi'] == 'RayTracingUnsupported' and s['gateSsr'] == 'RayTracingUnsupported', f'{key} gate')
        require(not s['probeAllocated'], f'{key} producer allocation')
        require(not s['ssrQueue'], f'{key} SSR queue')
        require(s['updates'] == 0, f'{key} probe dispatch')
        require(s['instances'] == 0 and s['builds'] == 0 and s['subscribers'] == 0 and s['liveResources'] == 0,
                f'{key} RT scene')
        require(s['lightRegistered'] == 0 and s['lightUploaded'] == 0, f'{key} light table')
    for r in range(3):
        off, gi, ssr, both = (stage_of(rep, n, r) for n in ('off', 'gi', 'ssr', 'both'))
        runtime_off = stage_of(rep, 'runtimeOff', r)
        if not (off and gi and ssr and both and runtime_off):
            errors.append(f'D3D11 missing matrix stages for renderer {r}')
            continue
        require(off['frameHash'] == gi['frameHash'], f'D3D11/gi/{r} changed the image')
        require(ssr['frameHash'] == both['frameHash'], f'D3D11/both/{r} changed the image')
        require(runtime_off['frameHash'] == off['frameHash'], f'D3D11/runtimeOff/{r} left a residue')

def gate_d3d12(rep):
    require(rep['supportsRayTracing'], 'D3D12 DXR support')
    require(rep['sceneInstances'] > 0 and rep['sceneBuilds'] > 0, 'D3D12 never built the RT scene')
    for s in rep['stages']:
        key = f"D3D12/{s['stage']}/{s['renderer']}"
        require(s['gateGi'] == 'Accepted' and s['gateSsr'] == 'Accepted', f'{key} gate {s["gateGi"]}/{s["gateSsr"]}')
    for r in range(3):
        off, gi, ssr, both = (stage_of(rep, n, r) for n in ('off', 'gi', 'ssr', 'both'))
        runtime_off = stage_of(rep, 'runtimeOff', r)
        if not (off and gi and ssr and both and runtime_off):
            errors.append(f'D3D12 missing matrix stages for renderer {r}')
            continue

        require(off['instances'] == 0 and off['builds'] == 0 and off['subscribers'] == 0 and
                off['liveResources'] == 0, f'off/{r} allocated the RT scene')
        require(off['updates'] == 0 and not off['probeAllocated'] and not off['ssrQueue'], f'off/{r} kept a consumer')
        require(off['requests'] == 'None', f'off/{r} requests {off["requests"]}')
        require(off['nonBlack'] > 1000, f'off/{r} blank frame')

        require(gi['updates'] > 0 and gi['probeAllocated'] and gi['producerBytes'] > 900000,
                f'gi/{r} no probe producer')
        require(not gi['ssrQueue'] and gi['subscribers'] == 1 and gi['requests'] == 'GI',
                f'gi/{r} subscribed beyond GI')
        require(gi['instances'] > 0 and gi['builds'] >= 1 and gi['liveResources'] == 1, f'gi/{r} RT scene')
        require(near(gi['giMeanY'], PROBE_Y, ERR), f'gi/{r} GI {gi["giMeanY"]} vs {PROBE_Y}')
        require(gi['mirrorSignal'] <= MIRROR_OFF, f'gi/{r} mirror not black')

        require(ssr['updates'] == 0 and not ssr['probeAllocated'] and ssr['producerBytes'] == 0,
                f'ssr/{r} ran the probe producer')
        require(ssr['ssrQueue'] and ssr['ssrBudgetActual'] == BUDGET, f'ssr/{r} no SSR queue')
        require(ssr['subscribers'] == 1 and ssr['requests'] == 'Reflections', f'ssr/{r} extra subscription')
        require(ssr['instances'] == gi['instances'], f'ssr/{r} different TLAS membership')
        require(ssr['giMeanY'] <= .1, f'ssr/{r} lit the receiver')
        require(ssr['mirrorSignal'] >= MIRROR_ON, f'ssr/{r} no reflected donor')

        require(both['instances'] == gi['instances'] and both['liveResources'] == 1, f'both/{r} scene not shared')
        require(both['builds'] == 1, f'both/{r} built the shared TLAS {both["builds"]} times')
        require(both['updates'] > 0 and both['ssrQueue'], f'both/{r} lost a consumer')
        require(both['subscribers'] == 2 and both['requests'] == 'GI, Reflections', f'both/{r} subscriptions')
        require(both['lightRegistered'] == 1 and both['lightUploaded'] == 1 and both['lightUploads'] <= 2,
                f'both/{r} light table')
        require(near(both['giMeanY'], PROBE_Y, ERR), f'both/{r} GI')
        require(both['mirrorSignal'] >= MIRROR_ON, f'both/{r} mirror')
        require(len({off['frameHash'], gi['frameHash'], ssr['frameHash'], both['frameHash']}) == 4,
                f'both/{r} configs are not distinguishable')

        on, budget_off, on2 = (stage_of(rep, n, r) for n in ('t3bOn', 't3bOff', 't3bOn2'))
        if not (on and budget_off and on2):
            errors.append(f't3b missing for renderer {r}')
        else:
            require(on['ssrQueue'] and on['mirrorSignal'] >= MIRROR_ON and on['requests'] == 'GI, Reflections', f't3bOn/{r}')
            require(not budget_off['ssrQueue'] and budget_off['requests'] == 'GI' and budget_off['updates'] > 0,
                    f't3bOff/{r} kept Reflections')
            require(budget_off['mirrorSignal'] <= MIRROR_OFF, f't3bOff/{r} kept the reflection')
            require(on2['ssrQueue'] and on2['mirrorSignal'] >= MIRROR_ON and on2['requests'] == 'GI, Reflections', f't3bOn2/{r}')

        base, shadow, loff, lon = (stage_of(rep, n, r) for n in ('t4Base', 't4ShadowOn', 't4LightOff', 't4LightOn'))
        if not (base and shadow and loff and lon):
            errors.append(f't4 missing for renderer {r}')
        else:
            require(base['lightRegistered'] == 1 and base['lightUploaded'] == 1, f't4Base/{r} light')
            require(shadow['lightUploads'] >= 1 and shadow['builds'] == 0 and shadow['requests'] == 'GI, Reflections' and
                    shadow['lightRegistered'] == 1 and shadow['lightUploaded'] == 1, f't4ShadowOn/{r}')
            require(loff['lightUploaded'] == 0 and loff['lightUploads'] >= 1 and loff['builds'] == 0 and
                    loff['subscribers'] == 2, f't4LightOff/{r}')
            require(lon['lightUploaded'] == 1 and lon['lightUploads'] >= 1 and lon['builds'] == 0 and
                    lon['requests'] == 'GI, Reflections', f't4LightOn/{r}')

        dark_on = dark_off = None
        for combo in range(8):
            s = stage_of(rep, 'sweep' + str(combo), r)
            key = f'sweep{combo}/{r}'
            if not s:
                errors.append(f'{key} missing'); continue
            require(s['configMatched'], f'{key} ignored a toggle')
            require(abs(s['generation'] - s['frames']) <= 2, f'{key} generation {s["generation"]}')
            require(not s['ssrQueue'] and s['requests'] == 'GI' and s['subscribers'] == 1, f'{key} left SSR subscribed')
            if not s['classification']:
                require(s['inactive'] == 0, f'{key} inactive {s["inactive"]}')
            elif not s['relocation']:
                require(s['inactive'] > 0, f'{key} inactive {s["inactive"]}')
            require(s['relocated'] > 0 if s['relocation'] else s['relocated'] == 0, f'{key} relocated {s["relocated"]}')
            if s['relocation'] and s['relocated'] > 0:
                require(s['offsetMax'] <= .45 + 1e-3, f'{key} offset {s["offsetMax"]}')
            if s['classification'] and not s['relocation'] and not s['feedback']: dark_on = s
            if not s['classification'] and not s['relocation'] and not s['feedback']: dark_off = s
        if dark_on and dark_off:
            require(dark_on['giMeanY'] >= 1.2 * dark_off['giMeanY'], f'sweep/{r} classification had no effect')

        require(runtime_off['liveResources'] == 0 and runtime_off['subscribers'] == 0 and
                runtime_off['requests'] == 'None', f'runtimeOff/{r} kept the scene')
        require(runtime_off['instances'] == 0 and runtime_off['generation'] == 0 and runtime_off['producerBytes'] == 0 and
                not runtime_off['probeAllocated'] and not runtime_off['ssrQueue'], f'runtimeOff/{r} kept a consumer')
        require(runtime_off['frameHash'] == off['frameHash'], f'runtimeOff/{r} did not return to off')

        cut = stage_of(rep, 'cut', r)
        small = stage_of(rep, 'resSmall', r)
        back = stage_of(rep, 'resBack', r)
        stack = stage_of(rep, 'stack', r)
        dyn_before, dyn_moved, dyn_restored = (stage_of(rep, n, r) for n in
                                               ('dynamicBefore', 'dynamicMoved', 'dynamicRestored'))
        if not (cut and small and back and stack and dyn_before and dyn_moved and dyn_restored):
            errors.append(f'isolation stages missing for renderer {r}')
        else:
            require(cut['builds'] == 0, f'cut/{r} rebuilt the TLAS')
            require(cut['updates'] <= cut['frames'] + 4, f'cut/{r} duplicated the probe batch')
            require(abs(cut['firstGiMeanY'] - cut['giMeanY']) <= ERR * cut['giMeanY'], f'cut/{r} first frame GI')
            require(cut['firstMirrorSignal'] >= .5 * cut['mirrorSignal'], f'cut/{r} first frame mirror')
            require(near(cut['giMeanY'], PROBE_Y, ERR), f'cut/{r} settled GI')
            require(small['builds'] == 0 and small['instances'] == cut['instances'], f'resSmall/{r} rebuilt the TLAS')
            require(small['liveResources'] == 1 and small['historyCount'] == 1, f'resSmall/{r} history')
            require(small['targetWidth'] == 640 and small['targetHeight'] == 360, f'resSmall/{r} target size')
            require(near(small['giMeanY'], PROBE_Y, ERR), f'resSmall/{r} GI')
            require(near(back['giMeanY'], PROBE_Y, ERR), f'resBack/{r} GI')
            require(stack['updates'] <= stack['frames'] + 4, f'stack/{r} duplicated the probe batch')
            require(stack['subscribers'] == 2 and stack['requests'] == 'GI, Reflections', f'stack/{r} subscriptions')
            require(stack['historyOk'] and 1 <= stack['historyCount'] <= 2, f'stack/{r} history')
            require(near(stack['giMeanY'], PROBE_Y, ERR), f'stack/{r} GI')
            require(dyn_before['mirrorSignal'] >= MIRROR_ON, f'dynamic/{r} donor never reflected')
            require(dyn_moved['instances'] == dyn_before['instances'], f'dynamic/{r} instance count')
            require(dyn_moved['liveResources'] == 1 and dyn_moved['subscribers'] == 2, f'dynamic/{r} lost the scene')
            require(dyn_moved['mirrorSignal'] <= MIRROR_OFF, f'dynamic/{r} ghost reflection {dyn_moved["mirrorSignal"]}')
            require(dyn_restored['mirrorSignal'] >= MIRROR_ON, f'dynamic/{r} did not recover')

for api, name in [(11, 'D3D11'), (12, 'D3D12')]:
    rep = reports.get(api)
    if not rep: continue
    require(rep['api'] == ('Direct3D11' if name == 'D3D11' else 'Direct3D12'), f'{name} actual API {rep["api"]}')
    require(rep['passed'], f'{name} fixture gates failed: {rep.get("error")}')
    gate_d3d11(rep) if api == 11 else gate_d3d12(rep)

keys = ['stage', 'renderer', 'gateGi', 'gateSsr', 'gi', 'ssr', 'ssrBudget', 'ssrBudgetActual',
        'feedback', 'relocation', 'classification', 'configMatched', 'subscribers', 'requests',
        'instances', 'builds', 'liveResources', 'lightUploads', 'lightRegistered', 'lightUploaded',
        'updates', 'generation', 'probeAllocated', 'ssrQueue', 'ssrProduced', 'producerBytes',
        'active', 'inactive', 'relocated', 'offsetMax', 'giMeanY', 'giError', 'mirrorSignal',
        'mirrorSignalPixels', 'firstGiMeanY', 'firstMirrorSignal', 'historyCount', 'historyOk',
        'targetWidth', 'targetHeight', 'nonBlack', 'frameHash', 'stateHash']
summary = {
    'passed': not errors, 'errors': errors, 'guids': sorted(guids), 'base': INPUTS.get('base'),
    'stages': {r['api']: [{k: s.get(k) for k in keys} for s in r['stages']] for r in reports.values()},
}
(OUT / 'Summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(errors)
sys.exit(bool(errors))
