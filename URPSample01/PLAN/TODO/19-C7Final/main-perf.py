"""Acquire native T5 frames from three prebuilt player directories, each containing Direct3D{11,12}."""
import argparse
import csv
import datetime
import hashlib
import json
import statistics
import subprocess
import threading
import zipfile
from pathlib import Path


def now():
    return datetime.datetime.now().astimezone().isoformat()


def compilers():
    output = subprocess.check_output(['tasklist', '/fo', 'csv', '/nh']).decode(errors='replace')
    names = {'unity.exe', 'unityshadercompiler.exe', 'bee_backend.exe', 'csc.exe'}
    return [row[:2] for row in csv.reader(output.splitlines()) if row and row[0].lower() in names]


def measure(root, player, version, api, transparent, rep):
    name = f'{api}-{transparent}-{rep}-{version}'
    dest = root / name
    dest.mkdir()
    args = [str(player), f'-force-d3d{api}', '--cd-total-lights', '256', '--cd-light-counts', '0,8,48,256',
            '--cd-screenshot-lights', '0,8,48,256', '--cd-warmup', '40', '--cd-samples', '150',
            '--cd-output', str(dest), '-logFile', str(dest / 'player.log')]
    if transparent:
        args.append('--cd-transparent')
    observations = [[now(), compilers()]]
    assert not observations[0][1], observations
    stop = threading.Event()

    def observe():
        while not stop.wait(2):
            observations.append([now(), compilers()])

    thread = threading.Thread(target=observe)
    thread.start()
    event = dict(start=now(), args=args, observations=observations)
    try:
        event['exit'] = subprocess.run(args, timeout=180).returncode
    finally:
        stop.set()
        thread.join()
        event['end'] = now()
        (dest / 'launch.json').write_text(json.dumps(event, indent=1))
    assert event['exit'] == 0 and not any(o[1] for o in observations), name
    report = json.loads((dest / 'CdPerf.json').read_text())
    assert report['completed'] and not report['error'] and report['graphicsDeviceType'] == f'Direct3D{api}'
    assert (report['width'], report['height'], report['warmupFrames'], report['sampleFrames']) == (1280, 720, 40, 150)
    assert report['transparentFixture'] == bool(transparent) and not report['clusterListAsync']
    assert report['lightCounts'] == [0, 8, 48, 256]
    rows = list(csv.DictReader((dest / 'CdPerf.csv').open()))
    modes = ['ClusteredDeferred', 'ForwardPlus'] + (['StencilDeferred'] if version == 'C0' else [])
    assert sorted((x['Mode'], int(x['LightCount'])) for x in rows) == sorted((m, n) for m in modes for n in report['lightCounts'])
    assert all(int(x['CpuSamples']) == 150 and int(x['GpuSamples']) > 0 and (dest / x['Screenshot']).is_file() for x in rows)
    print(name, event['exit'], flush=True)
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ('C0', 'parent', 'candidate', 'out'):
        parser.add_argument(key, type=Path)
    p = parser.parse_args()
    root = p.out.resolve()
    root.mkdir()
    runs = {}
    for api in (11, 12):
        for transparent in (0, 1):
            for rep, order in enumerate([('C0', 'parent', 'candidate'), ('candidate', 'parent', 'C0'), ('parent', 'candidate', 'C0')], 1):
                for version in order:
                    player = getattr(p, version).resolve() / f'Direct3D{api}/CdArena.exe'
                    rows = measure(root, player, version, api, transparent, rep)
                    for x in rows:
                        for metric in ('CpuMedianMs', 'GpuMedianMs'):
                            runs.setdefault((api, transparent, x['Mode'], x['LightCount'], metric, version), []).append(float(x[metric]))
    with (root / 'performance.csv').open('w', newline='') as out:
        writer = csv.writer(out)
        writer.writerow(['api', 'transparent', 'mode', 'lights', 'metric', 'C0-1', 'C0-2', 'C0-3', 'parent-1', 'parent-2', 'parent-3', 'candidate-1', 'candidate-2', 'candidate-3', 'candidateMinusParentMs', 'candidateMinusC0Ms'])
        for k in sorted({k[:-1] for k in runs if k[-1] == 'candidate'}):
            c0, parent, cand = [runs[k + (v,)] for v in ('C0', 'parent', 'candidate')]
            writer.writerow([*k, *c0, *parent, *cand, round(statistics.median(cand)-statistics.median(parent), 6), round(statistics.median(cand)-statistics.median(c0), 6)])
    manifest = {}
    with zipfile.ZipFile(root / 'native.zip', 'w', zipfile.ZIP_DEFLATED) as raw, zipfile.ZipFile(root / 'images.zip','w') as images:
        seen = set()
        for path in sorted(root.glob('*/*')):
            name = path.relative_to(root).as_posix()
            if path.suffix == '.png':
                digest = hashlib.sha256(path.read_bytes()).hexdigest()
                manifest[name] = digest
                if digest not in seen:
                    images.write(path, digest + '.png')
                    seen.add(digest)
            else:
                raw.write(path, name)
        raw.writestr('images.json', json.dumps(manifest, indent=1))


if __name__ == '__main__':
    main()
