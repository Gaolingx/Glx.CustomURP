"""Python 3, stdlib only. Run parse.py to print verdicts; --check to verify verdicts.json.
inputs.zip contains full original logs (including C0 stock T5), the frozen budget,
BuildReport summaries and the two build trees' ClusterBuilder sources. builderXX.bin
are byte slices, not rebuilt shaders: manifest.json gives original path, parent hash
and [start,end) to reproduce them from each player's globalgamemanagers.assets.
"""
import hashlib
import json
import re
import struct
import sys
from collections import defaultdict
from pathlib import Path
from zipfile import ZipFile

HERE = Path(__file__).resolve().parent
FROZEN = {'11': 'c7d8f77e1817075dea3ce147ec9c2b333b94dcd8f475c60012628247dabb7ba2',
          '12': 'a692f08b7ef95ae2f44a6fc2fd3acf02609a59e27ffb7a13a72281e160834cfd'}
LIT = ('Universal Render Pipeline/Lit', 'ForwardLit')
NEW = 'ClusterTileExpandForwardPlus'
sha = lambda data: hashlib.sha256(data).hexdigest()


def parse_log(data, api):
    lines = data.decode('utf-8-sig', errors='replace').splitlines()
    stages = defaultdict(list)
    compute = {}
    g = c = None
    headers = printed = 0
    success = []
    for n, line in enumerate(lines, 1):
        if line.startswith(f'Clustered deferred Direct3D{api} player: Succeeded -> '):
            success.append(n)
        if line.startswith('Compiling shader '):
            g = re.fullmatch(r'Compiling shader "(.*)" pass "(.*)" \((\w+)\)', line).groups()
            headers += 1
            c = None
        elif line.startswith('Compiling compute shader '):
            name, = re.fullmatch(r'Compiling compute shader "(.*)"', line).groups()
            assert name not in compute
            c = compute[name] = {'headerLine': n, 'retained': None, 'declared': None,
                                 'localHits': None, 'remoteHits': None, 'compiledNow': None}
            g = None
        elif 'After scriptable stripping:' in line:
            assert g is not None
            stages[g].append(int(line.split(':')[-1]))
            printed += 1
            g = None
        elif c is not None:
            strip = re.fullmatch(r'\s*finished in [\d.]+ seconds\. (\d+) of (\d+) variants left', line)
            cache = re.fullmatch(r'\s*finished in [\d.]+ seconds\. Local cache hits (\d+), remote cache hits (\d+), compiled (\d+) variants', line)
            if strip:
                c['retained'], c['declared'] = map(int, strip.groups())
            if cache:
                c['localHits'], c['remoteHits'], c['compiledNow'] = map(int, cache.groups())
                assert sum(c[k] for k in ('localHits', 'remoteHits', 'compiledNow')) == c['retained']
                c = None
    assert len(success) == 1 and printed == headers and printed > 0
    assert all(c['retained'] is not None for c in compute.values())
    stages = {k: sorted(v) for k, v in stages.items()}
    counts = [x for v in stages.values() for x in v]
    result = dict(build='Succeeded', successLine=success[0], logSHA256=sha(data),
                  shaderWarnings=sum(s.startswith('Shader warning in') for s in lines),
                  shaderErrors=sum(s.startswith('Shader error in') for s in lines),
                  graphicsHeaders=headers, afterScriptableRows=printed, reportedZeroRows=counts.count(0),
                  graphicsSum=sum(counts), litVP=stages.get((*LIT, 'vp')), litFP=stages.get((*LIT, 'fp')),
                  computeAssetSum=sum(x['retained'] for x in compute.values()), compute=compute)
    return result, stages


def builder(data, source, api):
    # For these Unity 2022.3.62f1 player assets, validate the serialized name,
    # platform/kernel count, ordered length-prefixed kernel names, and the actual
    # executable DXBC compute programs between adjacent kernel records.
    u32 = lambda pos: struct.unpack_from('<I', data, pos)[0]
    string = lambda s: struct.pack('<I', len(s)) + s.encode('ascii')
    assert data.startswith(string('ClusterBuilder'))
    start = (len(string('ClusterBuilder')) + 3) & ~3
    platforms, platform, _, count = struct.unpack_from('<4I', data, start)
    assert platforms == 1 and platform == {'11': 2, '12': 18}[api]
    names = re.findall(rb'^#pragma kernel (\w+)', source, re.M)
    assert len(names) == count and len(set(names)) == count
    names = [s.decode() for s in names]
    markers = [string(s) for s in names]
    assert all(data.count(m) == 1 for m in markers)
    offsets = [data.index(m) for m in markers] + [len(data)]
    assert offsets == sorted(offsets) and offsets[0] == start + 16
    programs = {}
    for i, name in enumerate(names):
        record = data[offsets[i]:offsets[i+1]]
        hashes = []
        for hit in re.finditer(b'DXBC', record):
            pos = hit.start()
            length, chunks = struct.unpack_from('<II', record, pos+24)
            blob = record[pos:pos+length]
            assert len(blob) == length and 0 < chunks < 64
            chunk_offsets = struct.unpack_from(f'<{chunks}I', blob, 32)
            assert all(off + 8 + struct.unpack_from('<I', blob, off+4)[0] <= length for off in chunk_offsets)
            assert any(blob[off:off+4] in (b'SHEX', b'SHDR') and
                       struct.unpack_from('<I', blob, off+8)[0] >> 16 == 5 for off in chunk_offsets)
            hashes.append(sha(blob))
        assert hashes, name
        programs[name] = hashes
    return programs


def changes(before, after):
    return [dict(shader=k[0], shaderPass=k[1], stage=k[2], before=before.get(k), after=after.get(k))
            for k in sorted(before.keys() | after.keys()) if before.get(k) != after.get(k)]


def audit(z):
    manifest = json.loads(z.read('manifest.json'))
    for name, meta in manifest.items():
        b = z.read(name)
        assert len(b) == meta['bytes'] and sha(b) == meta['sha256'], name
    # The frozen file omits one opening quote in unrelated c3Removes prose.
    # Preserve its original bytes; normalize just that token in memory.
    raw = z.read('C0/budget.json')
    typo = b', stencil Lit/SimpleLit variants"]'
    assert raw.count(typo) == 1
    budget = json.loads(raw.replace(typo, b', "stencil Lit/SimpleLit variants"]'))
    lit = budget['litForwardLit']['t5Player']
    cap = budget['clusteredDeferredCompute']['combinations']
    result = {'reference': 'Frozen stock T5 player; not T4 or a material probe.',
              'frozenCommit': budget['frozenCommit'],
              'interpretation': 'Graphics sum counts printed stage blocks with multiplicity, not unique GPU binaries. Null means not observed; [0] is a printed zero. Compute retained counts include cache hits. Kernel bytecode counts are from finished player slices, not dispatch evidence. ClusterBuilder 7 is a measured C0 reference, not an explicit cap in budget.json.',
              'byPlayerAPI': {}}
    for api in ('11', '12'):
        runs = result['byPlayerAPI'][api] = {}
        rows = {}
        for arm in ('C0', 'base', 'candidate'):
            r, rows[arm] = parse_log(z.read(f'{arm}/build{api}.log'), api)
            runs[arm] = r
            if arm == 'C0':
                assert r['logSHA256'] == FROZEN[api]
                assert r['litVP'] == [lit['vertexVariants']] and r['litFP'] == [lit['fragmentVariants']]
                assert r['compute']['ClusteredDeferred']['retained'] == cap
                continue
            summary = json.loads(z.read(f'{arm}/summary{api}.json'))
            assert summary['result'] == r['build'] and summary['graphicsApis'] == 'Direct3D'+api
            r['buildErrors'] = summary['totalErrors']
            codes = builder(z.read(f'{arm}/builder{api}.bin'), z.read(f'{arm}/ClusterBuilder.compute'), api)
            r['builderProgramSHA256ByKernel'] = codes
            total = sum(map(len, codes.values()))
            assert total == r['compute']['ClusterBuilder']['retained']
            r['newKernelProgramsInPlayer'] = len(codes.get(NEW, []))  # complete enumerated table
            ref = runs['C0']
            delta = {k: (r['compute'][k]['retained'] - ref['compute'][k]['retained']
                         if k in r['compute'] and k in ref['compute'] else None)
                     for k in sorted(ref['compute'].keys() | r['compute'].keys())}
            r['computeDeltasVsC0'] = {k: v for k, v in delta.items() if v != 0}
            checks = {'graphicsSumNotAboveC0': r['graphicsSum'] <= ref['graphicsSum'],
                      'litVPNotAboveC0': len(r['litVP'] or []) == 1 and r['litVP'][0] <= lit['vertexVariants'],
                      'litFPNotAboveC0': len(r['litFP'] or []) == 1 and r['litFP'][0] <= lit['fragmentVariants'],
                      'clusteredDeferredNotAboveC0': r['compute']['ClusteredDeferred']['retained'] <= cap,
                      'noObservedComputeAssetGrowthVsC0': all(v is not None and v <= 0 for v in delta.values())}
            r['verdicts'] = {k: ('PASS' if ok else 'FAIL') for k, ok in checks.items()}
            r['graphicsDeltaVsC0'] = r['graphicsSum'] - ref['graphicsSum']
        runs['graphicsRowChangesC0ToBase'] = changes(rows['C0'], rows['base'])
        runs['graphicsRowChangesBaseToCandidate'] = changes(rows['base'], rows['candidate'])
    return result


if __name__ == '__main__':
    with ZipFile(HERE / 'inputs.zip') as archive:
        text = json.dumps(audit(archive), indent=2) + '\n'
    if sys.argv[1:] == ['--check']:
        if text != (HERE / 'verdicts.json').read_text(encoding='utf-8'):
            sys.exit('Verdicts differ')
        print('Verified archive inputs and reproduced verdicts.json')
    elif not sys.argv[1:]:
        sys.stdout.write(text)
    else:
        sys.exit(__doc__)
