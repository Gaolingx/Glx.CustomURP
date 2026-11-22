"""Offline C4 vs C3 player comparison (Python 3 + Pillow).

Pass a candidate evidence root laid out like validation/c3. JSON goes to stdout
or a NEW --output file. Exit 1 means incomplete evidence or image gate failure;
recorded timings, variants and cross-route comparisons are informational.
"""
import argparse
import json
import math
import re
import sys
from pathlib import Path

from PIL import Image, ImageChops

C3 = Path(__file__).resolve().parent.parent / 'validation/c3'
COUNTS = (0, 8, 48, 256)
MODES = ('ClusteredDeferred', 'ForwardPlus')
FEATURES = ('MainShadowCascade', 'ReceiveShadowsOff', 'MainShadowScreen',
            'AdditionalShadowAtlas', 'LightCookie', 'LightLayers', 'ScreenSpaceAO')
STATES = ('ForwardPlusOn', 'ForwardPlusOff', 'ClusteredOn', 'ClusteredOff')


def compare_images(a, b):
    with Image.open(a) as left, Image.open(b) as right:
        if left.size != right.size or left.size != (1280, 720):
            raise ValueError(f'image sizes: {left.size}, {right.size}; expected 1280x720')
        diff = ImageChops.difference(left.convert('RGB'), right.convert('RGB'))
    h = diff.histogram()
    peak = max(i % 256 for i, n in enumerate(h) if n)
    mean = sum(i % 256 * n for i, n in enumerate(h)) / sum(h)
    return dict(maxRGB=peak, meanRGB=mean, passed=peak <= 24 and mean <= 2)


def pair(a, b):
    return dict(c3=a, candidate=b, delta=b-a if a is not None and b is not None else None)


def timing(s, prefix):
    n = s.get(prefix + 'Samples')
    result = {'samples': n}
    for stat in ('MeanMs', 'MedianMs', 'P95Ms'):
        v = s.get(prefix + stat)
        result[stat] = v if isinstance(n, int) and n > 0 and isinstance(v, (int, float)) and math.isfinite(v) and v >= 0 else None
    return result


def manifest(path, api, group):
    data = json.loads(path.read_text(encoding='utf-8-sig'))
    if data['graphicsDeviceType'] != f'Direct3D{api}' or data['error']:
        raise ValueError('wrong API or player reported an error')
    samples, cross = {}, {}
    if group:
        if not data['completed'] or data['transparentFixture'] != (group == 'cluster-on') or not data['forwardClusterLights']:
            raise ValueError('incomplete or mismatched T5 fixture')
        for mode in data['modes']:
            for s in mode['samples']:
                key = f"{mode['name']}_L{s['lightCount']}.png"
                if s['screenshotFile'] != key or key in samples:
                    raise ValueError(f'invalid sample {key}')
                samples[key] = s
        for d in data.get('forwardPlusDiffs', []):
            cross[str(d['lightCount'])] = d
    else:
        for entry in data['entries']:
            for s in entry['states']:
                key = f"{entry['feature']}_{s['name']}.png"
                if s['file'] != key or key in samples or s['forwardPlusKeyword'] != s['name'].startswith('ForwardPlus'):
                    raise ValueError(f'invalid state {key}')
                samples[key] = s
            for d in entry.get('comparisons', []):
                if d['name'].startswith('forward_plus_vs_clustered_'):
                    cross[entry['feature'] + '/' + d['name']] = d
    return samples, cross


def variant(log, api):
    header = 'Compiling shader "Universal Render Pipeline/Lit" pass "ForwardLit" (fp)'
    result = {'log': str(log), 'count': None}
    try:
        lines = log.read_text(encoding='utf-8-sig', errors='replace').splitlines()
        if f'ClusteredDeferredArenaBuild.BuildD3D{api}AndExit' not in lines:
            raise ValueError('expected API build method not found')
        hits = []
        for i, line in enumerate(lines):
            if line == header:
                for j in range(i+1, len(lines)):
                    if not lines[j].startswith((' ', '\t')):
                        break
                    m = re.fullmatch(r'\s*After scriptable stripping:\s*(\d+)\s*', lines[j])
                    if m:
                        hits.append((j+1, int(m[1])))
                        break
        if len(hits) != 1:
            raise ValueError(f'expected one matching stripping count, found {len(hits)}')
        result['line'], result['count'] = hits[0]
    except (OSError, ValueError) as exc:
        result['unavailable'] = str(exc)
    return result


def compute_record(root):
    path = root / 'C3VariantAndScope.json'
    try:
        return json.loads(path.read_text(encoding='utf-8-sig'))['clusteredDeferredCompute']['combinations']
    except (OSError, ValueError, KeyError, TypeError):
        return None


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('candidate', type=Path)
    p.add_argument('--output', type=Path, help='create exclusively; never replace')
    for api in (11, 12):
        p.add_argument(f'--log{api}', type=Path, help='candidate T5 build log override')
    p.add_argument('--compute-source', type=Path, help='exact candidate ClusteredDeferred.compute used in build')
    args = p.parse_args()
    roots = [C3, args.candidate.resolve()]
    r = dict(c3=str(C3), candidate=str(roots[1]), thresholds={'maxRGB': 24, 'meanRGB': 2},
             samePath={}, crossRoute={}, timings={}, variants={}, errors=[])
    totals = {}
    for api in (11, 12):
        logs = [root / f'build-t5-d3d{api}.log' for root in roots]
        logs[1] = getattr(args, f'log{api}') or logs[1]
        a, b = (variant(log, api) for log in logs)
        r['variants'][f'Lit/ForwardLit/fp/D3D{api}'] = dict(c3=a, candidate=b, delta=pair(a['count'], b['count'])['delta'])
        for group in ('control', 'cluster-on', None):
            rel = f't5-d3d{api}/{group}' if group else f't4/player-d3d{api}'
            names = ([f'{m}_L{n}.png' for m in MODES for n in COUNTS] if group else
                     [f'{f}_{s}.png' for f in FEATURES for s in STATES])
            records = []
            for root in roots:
                path = root / rel / ('CdPerf.json' if group else 'T4FeatureMatrix.json')
                try:
                    samples, cross = manifest(path, api, group)
                    if set(samples) != set(names):
                        raise ValueError('sample set differs from expected matrix')
                except (OSError, ValueError, KeyError, TypeError) as exc:
                    r['errors'].append(f'{path}: {exc}')
                    samples, cross = {}, {}
                records.append((samples, cross))
            (sa, ca), (sb, cb) = records
            for k in sorted(ca.keys() | cb.keys()):
                a, b = ca.get(k, {}), cb.get(k, {})
                pa, pb = a.get('passed'), b.get('passed')
                status = ('unavailable' if None in (pa, pb) else 'pre-existing failure' if not pa and not pb
                          else 'new failure' if not pb else 'resolved' if not pa else 'passed')
                r['crossRoute'][rel + '/' + k] = dict(status=status, c3=a, candidate=b)
            for name in names:
                key = rel + '/' + name
                try:
                    row = compare_images(*(root / key for root in roots))
                except (OSError, ValueError) as exc:
                    row = dict(passed=False, error=str(exc))
                r['samePath'][key] = row
                suite = 'T5' if group else 'T4'
                totals[suite] = totals.get(suite, 0) + int(row['passed'])
                if group:
                    metrics = r['timings'][key] = {}
                    for prefix in ('cpu', 'gpu'):
                        a, b = (timing(s.get(name, {}), prefix) for s in (sa, sb))
                        metrics[prefix] = {k: pair(a[k], b[k]) for k in a}
    count = compute_record(roots[1])
    if args.compute_source:
        text = args.compute_source.read_text(encoding='utf-8-sig')
        axes = re.findall(r'^\s*#pragma multi_compile[ \t]+([^\r\n]+)', text, re.M)
        if not axes or not re.search(r'^#pragma kernel ClusteredDeferred\s*$', text, re.M):
            p.error('expected ClusteredDeferred kernel and keyword axes')
        count = math.prod(len(axis.split('//')[0].split()) for axis in axes)
    r['variants']['ClusteredDeferredDeclaredPreStrip'] = dict(**pair(compute_record(C3), count), source=str(args.compute_source or roots[1] / 'C3VariantAndScope.json'))
    r['samePathPassedCounts'] = totals
    ok = totals == {'T5': 32, 'T4': 56} and not r['errors']
    r['samePathGatePassed'] = ok
    data = json.dumps(r, indent=2) + '\n'
    if args.output:
        with args.output.open('x', encoding='utf-8') as out:
            out.write(data)
    else:
        sys.stdout.write(data)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
