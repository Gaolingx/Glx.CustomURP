"""Compare matched T4 captures (Pillow only); never infer the route from a filename alone."""
import hashlib
import json
from pathlib import Path
from PIL import Image, ImageChops

C3 = Path(__file__).resolve().parent
FEATURES = ('MainShadowCascade', 'ReceiveShadowsOff', 'MainShadowScreen', 'AdditionalShadowAtlas', 'LightCookie', 'LightLayers', 'ScreenSpaceAO')
STATES = ('ForwardPlusOff', 'ClusteredOff', 'ForwardPlusOn', 'ClusteredOn')
SIZE = (1280, 720)
PIXELS = SIZE[0] * SIZE[1]


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def diff(a, b):
    rgb = ImageChops.difference(a, b)
    r, g, b = rgb.split()
    peak = ImageChops.lighter(r, ImageChops.lighter(g, b))
    h = peak.histogram()
    mean = sum((i % 256) * n for i, n in enumerate(rgb.histogram())) / (PIXELS * 3)
    mx = peak.getextrema()[1]
    return {'meanRGB': mean, 'maxChannel': mx, 'changedPixels': sum(h[1:]), 'over8': sum(h[9:]), 'over24': sum(h[25:]), 'passed': mean <= 2 and mx <= 24}, peak


def locations(a, b, threshold):
    lut = [255 * (x > threshold) for x in range(256)]
    a, b = a.point(lut), b.point(lut)
    shared = ImageChops.darker(a, b).histogram()[255]
    removed, added = ImageChops.subtract(a, b), ImageChops.subtract(b, a)
    def unique(mask):
        return {'pixels': mask.histogram()[255], 'bboxXYXYExclusive': mask.getbbox()}
    return {'shared': shared, 'baselineOnly': unique(removed), 'mainOnly': unique(added), 'identical': not (added.getbbox() or removed.getbbox()), 'baselineMaskSHA256': hashlib.sha256(a.tobytes()).hexdigest(), 'mainMaskSHA256': hashlib.sha256(b.tobytes()).hexdigest()}


def compare_api(api):
    matrices, roots = {}, {}
    output = {'samePath': {}, 'crossPath': {}, 'featureMatrixPassed': {}, 'routesValidated': {}, 'featureActive': {}}
    for label, parent in [('baseline', 't4-baseline'), ('main', 't4')]:
        root = C3 / parent
        build = load(root / f'CdArenaT4BuildSummary-Direct3D{api}.json')
        assert build['result'] == 'Succeeded' and build['totalErrors'] == 0 and build['graphicsApis'] == f'Direct3D{api}'
        root = roots[label] = root / f'player-d3d{api}'
        j = load(root / 'T4FeatureMatrix.json')
        assert j['graphicsDeviceType'] == f'Direct3D{api}' and not j['error']
        device = [j[k] for k in ('unityVersion', 'graphicsDeviceName', 'graphicsDeviceVersion')]
        assert output.setdefault('environment', device) == device
        m = matrices[label] = {e['feature']: e for e in j['entries']}
        assert len(j['entries']) == len(FEATURES) and set(m) == set(FEATURES)
        assert {p.name for p in root.glob('*.png')} == {f'{f}_{s}.png' for f in FEATURES for s in STATES}
        for f, e in m.items():
            assert len(e['states']) == 4 and {s['name'] for s in e['states']} == set(STATES)
            for s in e['states']:
                assert s['file'] == f"{f}_{s['name']}.png" and s['magentaPixelFraction'] == 0
                assert s['forwardPlusKeyword'] == s['name'].startswith('ForwardPlus')
                if label == 'baseline':
                    assert s['clusteredLightingTargetLive'] == s['name'].startswith('Clustered')
        output['routesValidated'][label] = 28
        output['featureMatrixPassed'][label] = {f: e['passed'] for f, e in m.items()}
        output['featureActive'][label] = {f: e['featureActive'] for f, e in m.items()}
    for f in FEATURES:
        shots = {}
        for s in STATES:
            key = f'{f}_{s}'
            for label, root in roots.items():
                with Image.open(root / (key + '.png')) as im:
                    assert im.size == SIZE
                    shots[label, s] = im.convert('RGB')
            output['samePath'][key] = diff(shots['baseline', s], shots['main', s])[0]
        for switch in ('On', 'Off'):
            row, peaks = {}, {}
            for label, m in matrices.items():
                metrics, peak = diff(shots[label, 'ForwardPlus' + switch], shots[label, 'Clustered' + switch])
                expected, = (c for c in m[f]['comparisons'] if c['name'] == 'forward_plus_vs_clustered_' + switch.lower())
                assert (metrics['maxChannel'], metrics['over8'], metrics['passed']) == (expected['maxChannelDiff'], expected['pixelsOverThreshold'], expected['passed']) and abs(metrics['meanRGB'] - expected['meanChannelDiff']) < 1e-10
                row[label], peaks[label] = metrics, peak
            for threshold in (8, 24):
                row[f'locationsOver{threshold}'] = locations(peaks['baseline'], peaks['main'], threshold)
            output['crossPath'][f + '_' + switch] = row
    same = list(output['samePath'].values())
    cross = list(output['crossPath'].values())
    output['summary'] = {'samePathIdentical': sum(r['maxChannel'] == 0 for r in same), 'samePathPassed': sum(r['passed'] for r in same), 'samePathMax': max(r['maxChannel'] for r in same), 'samePathLargestMean': max(r['meanRGB'] for r in same), 'crossPathPassed': {v: sum(r[v]['passed'] for r in cross) for v in roots}, **{f'crossPathIdenticalLocationsOver{t}': sum(r[f'locationsOver{t}']['identical'] for r in cross) for t in (8, 24)}}
    return output


if __name__ == '__main__':
    report = {'baseline': '5ab7626', 'main': '646f75a', 'inputs': {'baseline': 't4-baseline/player-d3d{11,12}', 'main': 't4/player-d3d{11,12}'}, 'fixture': {'receiveShadowsOffMaterialSHA256': '0c17b13d43dd4deab20386a9b78b61892288f5062f8d25d14005c84002f77a39', 'receiveShadowsOffScope': 'even receiver index AND original sharedMaterial == sphereMaterial; other receivers retain original material', 'baselineClusteredProof': 'Per state, active arena camera renderer pass-owned lighting texture exists and IsCreated only on Clustered. In 5ab7626 ClusteredDeferredPass allocates this target in Execute after camera/resource checks.', 'forwardPlusProof': 'Independent pipeline and renderer; _FORWARD_PLUS true only in both ForwardPlus states.'}, 'thresholds': {'meanRGBAtMost': 2, 'maxChannelAtMost': 24, 'noiseBand': 8}, 'maskEncoding': '1280x720 row-major bytes, top-left origin; 255 if max absolute RGB difference exceeds threshold, else 0', 'apis': {f'D3D{api}': compare_api(api) for api in (11, 12)}}
    path = C3 / 'T4MatchedFixtureComparison.json'
    path.with_suffix('.tmp').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    path.with_suffix('.tmp').replace(path)
    print({k: v['summary'] for k, v in report['apis'].items()})
