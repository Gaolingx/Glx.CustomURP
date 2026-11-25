"""Gate the I2 1080p quality/performance, D3D11 off and stripping evidence against B0.
Usage: verify.py <evidence> [strip-only]"""
import json, sys
from pathlib import Path

OUT = Path(sys.argv[1])
STRIP_ONLY = len(sys.argv) > 2 and sys.argv[2] == 'strip-only'
errors = []
def require(x, msg):
    if not x: errors.append(msg)

def load(p):
    return json.loads(p.read_text())

INPUTS = load(OUT / 'Inputs.json')
variants = load(OUT / 'Variants.json')
variants11 = load(OUT / 'D3D11OnlyVariants.json')
rt = load(OUT / 'RtVariants.json')
rt11 = load(OUT / 'D3D11OnlyRtVariants.json')
reports = {}
for name in ('D3D11', 'D3D12'):
    rp = OUT / name / 'Report.json'
    if not rp.exists():
        if not STRIP_ONLY: errors.append(f'missing {rp}')
        continue
    reports[name] = load(rp)
strip = load(OUT / 'D3D11Strip' / 'Report.json')

require(0 < variants['distinctSets'] <= 16, f"Win64 Lit DXR sets {variants['distinctSets']}")
require(variants11['distinctSets'] == 0, f"D3D11-only Lit DXR sets {variants11['distinctSets']}")
require('Direct3D12' in rt['graphicsAPIs'], f"Win64 build APIs {rt['graphicsAPIs']}")
require(rt['glxDxrSets'] > 0, f"Win64 build compiled {rt['glxDxrSets']} GlxDXR variants")
require(rt['probeKeywordSets'] > 0, f"Win64 build compiled {rt['probeKeywordSets']} probe keyword variants")
require(rt11['graphicsAPIs'] == ['Direct3D11'], f"D3D11-only build APIs {rt11['graphicsAPIs']}")
require(rt11['glxDxrSets'] == 0, f"D3D11-only build retained {rt11['glxDxrSets']} GlxDXR variants")
require(rt11['probeKeywordSets'] == 0, f"D3D11-only build retained {rt11['probeKeywordSets']} probe keyword variants")
build11 = (OUT / 'D3D11OnlyBuild.log')
if build11.exists():
    log11 = build11.read_text(errors='ignore')
    compiled = log11.count('with _SCREEN_SPACE_GI_PROBE')
    require(compiled == 0, f"D3D11-only log compiled {compiled} probe keyword variants")
    probe_warnings = log11.count('GlxProbeQuery.hlsl')
    require(probe_warnings == 0, f"D3D11-only log compiled the probe query library {probe_warnings} times")
guids = {r['guid'] for r in reports.values()}
if not STRIP_ONLY:
    require(len(guids) == 1 and len(next(iter(guids), '')) == 32, 'one build GUID across D3D12/D3D11')
    require(strip['guid'] not in guids, 'strip build reused the Win64 GUID')

GPU_FRAME, CPU_TLAS, GPU_TLAS = 4.0, .5, 1.0
PROBE, MAINTAIN, SSR_TRACE, SSR_RECON, MEMORY = 1.25, .25, 1.75, .5, 256.0

def stages(rep, renderer, *names):
    return [s for s in rep['stages'] if s['renderer'] == renderer and s['stage'] in names]

def worst_p95(rows, key):
    return max((s[key] for s in rows), default=0.0)

def gate_d3d11(rep):
    require(rep['api'] == 'Direct3D11' and not rep['supportsRayTracing'], 'D3D11 API/support')
    require(rep['passed'], f"D3D11 player gates: {rep.get('error')}")
    for r in range(3):
        backend = stages(rep, r, 'backend')
        off = stages(rep, r, 'off')
        require(len(backend) == 1, f'D3D11/{r} backend stage missing')
        if backend:
            b = backend[0]
            require(not b['probeAllocated'] and not b['ssrQueue'] and b['updates'] == 0 and
                    b['instances'] == 0 and b['subscribers'] == 0 and b['requests'] == 'None',
                    f'D3D11/{r} allocated RT resources')
        require(len(off) == 3, f'D3D11/{r} off repeats {len(off)}')
        for s in off:
            require(s['cpuFrameSamples'] > 0 and s['cpuFrameP95'] > 0, f'D3D11/{r} off CPU timing')

def gate_d3d12(rep):
    require(rep['api'] == 'Direct3D12' and rep['supportsRayTracing'], 'D3D12 API/support')
    require(rep['passed'], f"D3D12 player gates: {rep.get('error')}")
    require(rep['gpuTimingMeasured'], 'D3D12 GPU timing was not measured')
    require(rep['profilerEnabled'] and rep['gpuAreaEnabled'], 'D3D12 profiler/GPU area')
    for r in range(3):
        quality = stages(rep, r, 'quality')
        require(len(quality) == 1, f'D3D12/{r} quality stage')
        if quality:
            q = quality[0]
            require(q['nonFinite'] == 0, f'quality/{r} non-finite {q["nonFinite"]}')
            require(q['nonBlack'] > 100000, f'quality/{r} blank')
            require(q['onHash'] != q['offHash'], f'quality/{r} RT-on equals RT-off')
            require(q['diffPixels'] > 1000, f'quality/{r} diff pixels {q["diffPixels"]}')
            require(q['mirrorOn'] > q['mirrorOff'] * 1.2, f'quality/{r} mirror')
            require(q['instances'] > 0 and q['probeAllocated'] and q['ssrQueue'] and q['subscribers'] == 2,
                    f'quality/{r} RT not active')

        off = stages(rep, r, 'off')
        gi = stages(rep, r, 'gi')
        ssr = stages(rep, r, 'ssr')
        both = stages(rep, r, 'both')
        static = stages(rep, r, 'bothStatic')
        require(len(off) == 3 and len(gi) == 3 and len(ssr) == 3 and len(both) == 3 and len(static) == 3,
                f'D3D12/{r} repeat coverage')
        if not (off and gi and ssr and both and static):
            continue

        delta = worst_p95(both, 'gpuRtSumP95')
        require(0 < delta <= GPU_FRAME, f'perf/{r} RT GPU delta {delta:.3f}')
        cpu_tlas = worst_p95(both, 'tlasCpuP95')
        require(0 < cpu_tlas <= CPU_TLAS, f'perf/{r} CPU TLAS submit {cpu_tlas:.3f}')
        gpu_tlas = worst_p95(both, 'tlasBuildGpuP95')
        require(0 < gpu_tlas <= GPU_TLAS, f'perf/{r} GPU TLAS build {gpu_tlas:.3f}')
        probe = worst_p95(gi + both, 'probeTraceP95')
        require(0 < probe <= PROBE, f'perf/{r} probe trace/update {probe:.3f}')
        maintain = worst_p95(gi + both, 'probeMaintP95')
        require(0 < maintain <= MAINTAIN, f'perf/{r} relocate/classify {maintain:.3f}')
        ssr_trace = worst_p95(ssr + both, 'ssrTraceP95')
        require(0 < ssr_trace <= SSR_TRACE, f'perf/{r} SSR trace {ssr_trace:.3f}')
        ssr_recon = worst_p95(ssr + both, 'ssrReconP95')
        require(0 < ssr_recon <= SSR_RECON, f'perf/{r} SSR reconstruction {ssr_recon:.3f}')

        for s in both:
            require(s['gpuRtSumSamples'] > 0 and s['probeTraceSamples'] > 0 and s['tlasBuildSamples'] > 0,
                    f'both/{r}/{s["repeat"]} missing GPU samples')
            require(s['builds'] >= 60, f'both/{r}/{s["repeat"]} dynamic builds {s["builds"]}')
        for s in ssr:
            require(s['ssrTraceSamples'] > 0 and s['ssrReconSamples'] > 0,
                    f'ssr/{r}/{s["repeat"]} missing SSR samples')
        for s in static:
            require(s['tlasBuildSamples'] <= 3, f'static/{r}/{s["repeat"]} rebuilt the TLAS')
        for s in off:
            require(s['instances'] == 0 and s['subscribers'] == 0 and s['updates'] == 0,
                    f'off/{r}/{s["repeat"]} allocated RT resources')
        for s in gi:
            require(s['probeAllocated'] and not s['ssrQueue'] and s['updates'] > 0,
                    f'gi/{r}/{s["repeat"]} isolation')
        for s in ssr:
            require(not s['probeAllocated'] and s['ssrQueue'] and s['updates'] == 0,
                    f'ssr/{r}/{s["repeat"]} isolation')

        off_peak = max((s['driverPeakMiB'] for s in off), default=0)
        both_peak = max((s['driverPeakMiB'] for s in both), default=0)
        if off_peak > 0 and both_peak >= off_peak:
            require(both_peak - off_peak <= MEMORY, f'perf/{r} extra GPU memory {both_peak - off_peak:.1f}')
        else:
            require(any('blocked' in n for n in rep.get('knownDifferences', [])),
                    f'perf/{r} memory counter missing without a blocked note')

gate_d3d11(reports['D3D11']) if 'D3D11' in reports else None
gate_d3d12(reports['D3D12']) if 'D3D12' in reports else None

require(strip['api'] == 'Direct3D11' and not strip['supportsRayTracing'], 'strip API')
require(strip['passed'], f"strip player gates: {strip.get('error')}")
strip_stage, = [s for s in strip['stages'] if s['stage'] == 'strip']
require(not strip_stage['probeAllocated'] and not strip_stage['ssrQueue'] and strip_stage['updates'] == 0 and
        strip_stage['instances'] == 0 and strip_stage['subscribers'] == 0 and strip_stage['requests'] == 'None',
        'strip allocated RT resources')

keys = ['stage', 'renderer', 'repeat', 'frames', 'warmup', 'dynamic', 'requests', 'instances', 'builds',
        'subscribers', 'updates', 'probeAllocated', 'ssrQueue', 'producerBytes', 'queueBytes', 'trackedBytes',
        'cpuFrameMedian', 'cpuFrameP95', 'gpuFrameMedian', 'gpuFrameP95', 'tlasBuildSamples',
        'tlasBuildGpuP95', 'tlasCpuSamples', 'tlasCpuP95', 'probeTraceSamples', 'probeTraceP95',
        'probeMaintSamples', 'probeMaintP95', 'ssrTraceSamples', 'ssrTraceP95', 'ssrReconSamples',
        'ssrReconP95', 'ssrPassP95', 'gpuRtSumSamples', 'gpuRtSumP95', 'driverPeakMiB', 'onHash', 'offHash', 'mirrorOn', 'mirrorOff',
        'frameOn', 'frameOff', 'diffPixels', 'nonFinite', 'nonBlack']
summary = {
    'scope': 'strip-only' if STRIP_ONLY else 'full',
    'passed': not errors, 'errors': errors, 'guids': sorted(guids), 'stripGuid': strip['guid'],
    'base': INPUTS.get('base'), 'variantSets': variants['distinctSets'], 'stripVariantSets': variants11['distinctSets'],
    'rayTracingVariants': {
        'win64': {'graphicsAPIs': rt['graphicsAPIs'], 'glxDxrSets': rt['glxDxrSets'],
                  'probeKeywordSets': rt['probeKeywordSets'], 'passes': rt['glxDxr'] + rt['probeKeyword'],
                  'rtAssetEntries': rt['rtAssetEntries']},
        'd3d11Only': {'graphicsAPIs': rt11['graphicsAPIs'], 'glxDxrSets': rt11['glxDxrSets'],
                      'probeKeywordSets': rt11['probeKeywordSets'],
                      'rtAssetEntries': rt11['rtAssetEntries']},
    },
    'reports': {name: {'api': rep['api'], 'passed': rep['passed'], 'gpuTimingMeasured': rep['gpuTimingMeasured'],
                       'gates': rep.get('gates', []), 'knownDifferences': rep.get('knownDifferences', []),
                       'stages': [{k: s.get(k) for k in keys} for s in rep['stages']]}
                for name, rep in reports.items()},
    'strip': {k: strip.get(k) for k in ('api', 'passed', 'supportsRayTracing', 'mode')},
}
(OUT / 'Summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(errors)
sys.exit(bool(errors))
