"""Compare C4b T4 and T5 with the last C4a player, same API, path and fixture.
Run after all four players finish. Writes JSON to stdout; does not modify baselines.
"""
import json
from pathlib import Path

from PIL import Image, ImageChops

root = Path(__file__).resolve().parent
baseline = root.parents[1] / 'Done/16-CleanupBaseline/c4/final-player/t4'
features = ('MainShadowCascade', 'ReceiveShadowsOff', 'MainShadowScreen',
            'AdditionalShadowAtlas', 'LightCookie', 'LightLayers', 'ScreenSpaceAO')
states = ('ForwardPlusOn', 'ForwardPlusOff', 'ClusteredOn', 'ClusteredOff')
rows = []
for api in (11, 12):
    old = baseline / f'player-d3d{api}'
    new = root / f't4-d3d{api}'
    manifests = [json.loads((folder / 'T4FeatureMatrix.json').read_text(encoding='utf-8-sig')) for folder in (old, new)]
    for manifest in manifests:
        assert manifest['graphicsDeviceType'] == f'Direct3D{api}' and not manifest['error']
        assert {(e['feature'], s['name'], s['file'], s['forwardPlusKeyword']) for e in manifest['entries'] for s in e['states']} == {
            (f, s, f'{f}_{s}.png', s.startswith('ForwardPlus')) for f in features for s in states}
    for feature in features:
        for state in states:
            file = f'{feature}_{state}.png'
            with Image.open(old / file) as a, Image.open(new / file) as b:
                assert a.size == b.size == (1280, 720)
                hist = ImageChops.difference(a.convert('RGB'), b.convert('RGB')).histogram()
            peak = max(i % 256 for i, count in enumerate(hist) if count)
            mean = sum((i % 256) * count for i, count in enumerate(hist)) / sum(hist)
            rows.append(dict(api=api, file=file, maxRGB=peak, meanRGB=mean, passed=peak <= 24 and mean <= 2))
t4 = rows
rows = []
for api in (11, 12):
    for fixture in ('control', 'cluster-on'):
        rel = Path(f't5-d3d{api}', fixture)
        old, new = baseline.parent / rel, root / rel
        for folder in (old, new):
            m = json.loads((folder / 'CdPerf.json').read_text(encoding='utf-8-sig'))
            assert m['graphicsDeviceType'] == f'Direct3D{api}' and m['completed'] and not m['error']
            assert m['lightCounts'] == [0, 8, 48, 256] and m['transparentFixture'] == (fixture == 'cluster-on')
            assert m['forwardClusterLights'] and not any(m[k] for k in ('clusterListAsync', 'clusterListPrune', 'clusterListCompact'))
        for route in ('ClusteredDeferred', 'ForwardPlus'):
            for count in (0, 8, 48, 256):
                file = f'{route}_L{count}.png'
                with Image.open(old / file) as a, Image.open(new / file) as b:
                    assert a.size == b.size == (1280, 720)
                    diff = ImageChops.difference(a.convert('RGB'), b.convert('RGB'))
                hist = diff.histogram()
                peak = max(i % 256 for i, n in enumerate(hist) if n)
                mean = sum((i % 256) * n for i, n in enumerate(hist)) / sum(hist)
                r, g, b = diff.split()
                over24 = sum(ImageChops.lighter(ImageChops.lighter(r, g), b).histogram()[25:])
                rows.append(dict(api=api, fixture=fixture, file=file, maxRGB=peak, meanRGB=mean, over24=over24,
                                 passed=peak <= 24 and mean <= 2))

def report(rows):
    return dict(passed=sum(row['passed'] for row in rows), total=len(rows), rows=rows)

print(json.dumps(dict(baseline=str(baseline.parent), T4=report(t4), T5=report(rows)), indent=2))
raise SystemExit(not all(row['passed'] for row in t4 + rows))
