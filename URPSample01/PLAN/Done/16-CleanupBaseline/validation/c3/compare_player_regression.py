"""Offline C3/C0 player evidence; requires Pillow. Does not run Unity."""
import json
from pathlib import Path
from PIL import Image, ImageChops

C3 = Path(__file__).resolve().parent
C0 = C3.parent / 'baseline'
counts = [0, 8, 48, 256]


def load(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))


def compare(rel):
    with Image.open(C0 / rel) as a, Image.open(C3 / rel) as b:
        assert a.size == b.size == (1280, 720)
        diff = ImageChops.difference(a.convert('RGB'), b.convert('RGB'))
    r, g, b = diff.split()
    h = ImageChops.lighter(ImageChops.lighter(r, g), b).histogram()
    mean = sum(i % 256 * n for i, n in enumerate(diff.histogram())) / (3 * sum(h))
    peak = max(i for i, n in enumerate(h) if n)
    return dict(file=rel.as_posix(), mean=mean, max=peak, changed=sum(h[1:]), over8=sum(h[9:]), passed=mean <= 2 and peak <= 24)


report = {'thresholds': {'meanRGB': 2, 'maxChannel': 24}, 't5': {}, 't4D3D11': {}}
for api in (11, 12):
    for group in ('control', 'cluster-on'):
        rel = Path(f't5-d3d{api}') / group
        j = load(C3 / rel / 'CdPerf.json')
        base = load(C0 / rel / 'CdPerf.json')
        assert j['graphicsDeviceType'] == base['graphicsDeviceType'] == f'Direct3D{api}'
        assert j['completed'] and not j['error'] and j['forwardClusterLights']
        assert j['lightCounts'] == j['screenshotLightCounts'] == counts
        assert j['transparentFixture'] == (group == 'cluster-on')
        assert [m['name'] for m in j['modes']] == ['ClusteredDeferred', 'ForwardPlus']
        rows = []
        for m in j['modes']:
            assert [s['lightCount'] for s in m['samples']] == counts
            for s in m['samples']:
                filename = f"{m['name']}_L{s['lightCount']}.png"
                assert s['screenshotFile'] == filename and s['magentaPixelFraction'] == 0
                rows.append(compare(rel / filename))
        report['t5'][rel.as_posix()] = dict(api=j['graphicsDeviceType'], transparent=j['transparentFixture'], samePath=rows,
            crossRoute=[{k:d[k] for k in ('lightCount','meanChannelDiff','maxChannelDiff','pixelsOverThreshold','passed')} for d in j['forwardPlusDiffs']])

for api in (11, 12):
    rel = Path(f't4/player-d3d{api}')
    j = load(C3 / rel / 'T4FeatureMatrix.json')
    base = load(C0 / rel / 'T4FeatureMatrix.json')
    assert j['graphicsDeviceType'] == base['graphicsDeviceType'] == f'Direct3D{api}' and not j['error']
    assert {e['feature'] for e in j['entries']} == {e['feature'] for e in base['entries']} and len(j['entries']) == 7
    matrix = report[f't4D3D{api}'] = {}
    for e in j['entries']:
        states = e['states']
        assert {s['name'] for s in states} == {'ForwardPlusOn','ForwardPlusOff','ClusteredOn','ClusteredOff'}
        matrix[e['feature']] = dict(routeOK=all(s['forwardPlusKeyword'] == s['name'].startswith('ForwardPlus') for s in states),
            noMagenta=all(s['magentaPixelFraction'] == 0 for s in states), featureActive=e['featureActive'], matrixPassed=e['passed'],
            comparisons=e['comparisons'], samePath=[compare(rel / s['file']) for s in states if s['name'].startswith('Clustered')])

(C3 / 'C3PlayerRegression.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
for name, data in {**report['t5'], **report['t4D3D11']}.items():
    print(name, [(round(d['mean'], 5), d['max'], d['over8']) for d in data['samePath']], data.get('routeOK', ''), data.get('noMagenta', ''))
