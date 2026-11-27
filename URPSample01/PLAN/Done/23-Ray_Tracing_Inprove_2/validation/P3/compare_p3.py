"""P3 (B6) comparison: determinism, rollback parity and P1<->P3 deltas.

Windows: python compare_p3.py [--root path/to/validation/P3]

Layout read: <root>/Win64-<API>/<p1|p3>/<variant>/<run>/Player.json (+ frame.raw for run-01).
Writes <root>/Comparison.json.

* determinism: run-01 vs run-02 frame SHA per API/path/variant.
* rollback parity: the p1 (ssrAfterGbuffer=false) path must reproduce the frozen frame SHAs.
* p1-vs-p3: full-frame and floor-ROI absolute channel deltas per API/variant.
* D5 #4 one-frame jump: full-frame meanAbs and the worst 32x32 block-mean step on base.

frame.raw is ignored by git; only hashes and this JSON are committed.
"""
import argparse
import json
import struct
from pathlib import Path

W, H = 1920, 1080
VARIANTS = ("base", "nodenoise", "ssroff")
APIS = ("Win64-D3D12", "Win64-D3D11")
PATHS = ("p1", "p3")
ROI = (0.15, 0.45, 0.85, 0.95)
SAMPLE_STRIDE = 2
BLOCK = 32

# Frozen references: D3D11 is API-stable since P0; the D3D12 base moved once in P1 B6 when the
# hit-shading kernel started executing (HANDOFF 23 P1). nodenoise/ssroff on D3D12 are recorded
# informationally because P1 changed the RT hit path only for the runs that shade hits.
FROZEN = {
    "Win64-D3D11": {
        "base": "7fb3e01b95bdd935cb5e0ea681e0e9b73438230a0de4664f74521e4a9224c42c",
        "nodenoise": "1bfd98e99589c6f9e0f375785ad023fe32b92d610895e4ce91cc02a32a0754a2",
        "ssroff": "8ab8e5699250712bdf5d77a373f028664817fdd36b18342477303cf1451c0362",
    },
    "Win64-D3D12": {
        "base": "b1e0a88906fc808bca3fd11687fb281abe610aaece3edcdc5b42de3596b9b5f6",
    },
}


def read_json(path):
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def load_raw(path):
    data = Path(path).read_bytes()
    if len(data) != W * H * 8:
        raise SystemExit(f"unexpected frame.raw size at {path}: {len(data)}")
    return data


def pixel(data, x, y_top_down):
    index = ((H - 1 - y_top_down) * W + x) * 8
    return struct.unpack_from("<eeee", data, index)


def luma(rgb):
    return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2]


def stats(a, b, roi=None):
    x0, y0, x1, y1 = roi if roi else (0.0, 0.0, 1.0, 1.0)
    xs = range(int(x0 * W), int(x1 * W), SAMPLE_STRIDE)
    ys = range(int(y0 * H), int(y1 * H), SAMPLE_STRIDE)
    diffs = []
    luma_a = []
    luma_b = []
    for y in ys:
        for x in xs:
            pa = pixel(a, x, y)
            pb = pixel(b, x, y)
            for ca, cb in zip(pa, pb):
                diffs.append(abs(ca - cb))
            luma_a.append(luma(pa))
            luma_b.append(luma(pb))
    diffs.sort()
    n = len(diffs)
    return {
        "samples": n,
        "meanAbs": sum(diffs) / n,
        "p99Abs": diffs[min(n - 1, int(n * 0.99))],
        "maxAbs": diffs[-1],
        "fractionOver1e2": sum(1 for d in diffs if d > 0.01) / n,
        "lumaMeanA": sum(luma_a) / n,
        "lumaMeanB": sum(luma_b) / n,
    }


def block_step(a, b):
    """Worst and 99th percentile |block luma mean| step on a BLOCK grid, whole frame."""
    steps = []
    for by in range(0, H, BLOCK):
        for bx in range(0, W, BLOCK):
            sum_a = 0.0
            sum_b = 0.0
            count = 0
            for y in range(by, min(by + BLOCK, H), SAMPLE_STRIDE):
                for x in range(bx, min(bx + BLOCK, W), SAMPLE_STRIDE):
                    sum_a += luma(pixel(a, x, y))
                    sum_b += luma(pixel(b, x, y))
                    count += 1
            steps.append(abs(sum_a - sum_b) / count)
    steps.sort()
    return {
        "blocks": len(steps),
        "maxBlockMeanAbs": steps[-1],
        "p99BlockMeanAbs": steps[int(len(steps) * 0.99)],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent)
    args = parser.parse_args()
    root = args.root.resolve()

    report = {
        "frames": {},
        "determinism": {},
        "frozenParity": {},
        "comparisons": {},
        "roi": {"x0": ROI[0], "y0": ROI[1], "x1": ROI[2], "y1": ROI[3], "space": "normalized top-down"},
        "sampleStride": SAMPLE_STRIDE,
        "blockSize": BLOCK,
    }

    raws = {}
    for api in APIS:
        for path in PATHS:
            for variant in VARIANTS:
                base = root / api / path / variant
                run1 = base / "run-01"
                run2 = base / "run-02"
                if not (run1 / "Player.json").exists():
                    report["frames"][f"{api}/{path}/{variant}"] = {"missing": True}
                    continue
                p1 = read_json(run1 / "Player.json")
                p2 = read_json(run2 / "Player.json") if (run2 / "Player.json").exists() else None
                key = f"{api}/{path}/{variant}"
                report["frames"][key] = {
                    "run01": p1["frameSHA256"],
                    "run02": p2["frameSHA256"] if p2 else None,
                    "graphicsDeviceType": p1["graphicsDeviceType"],
                    "supportsRayTracing": p1["supportsRayTracing"],
                    "lumaMean": p1["lumaMean"],
                    "nonFinite": p1["nonFiniteSamples"],
                    "p3SsrAfterGbuffer": p1["p3SsrAfterGbufferRequested"],
                    "p3SsrRollback": p1["p3SsrRollbackRequested"],
                    "featureSsrAfterGbuffer": p1["featureSsrAfterGbuffer"],
                    "featureSsrIndirectSource": p1["featureSsrIndirectSource"],
                    "featureReadback": p1["featureReadback"],
                    "tlasInstances": p1["tlasInstances"],
                    "r1Requested": p1["r1Requested"],
                    "r1Served": p1["r1Served"],
                    "r1Overflow": p1["r1Overflow"],
                    "hitDeferredDispatches": p1["hitDeferredDispatches"],
                    "ssrDispatch": p1["ssrDispatch"],
                }
                if p2 is not None:
                    report["determinism"][key] = {
                        "bitIdentical": p1["frameSHA256"] == p2["frameSHA256"],
                        "firstUnityFrame": [p1["firstUnityFrame"], p2["firstUnityFrame"]],
                        "unityFrameAtCapture": [p1["unityFrameAtCapture"], p2["unityFrameAtCapture"]],
                    }
                if (run1 / "frame.raw").exists():
                    raws[key] = load_raw(run1 / "frame.raw")

    for api, variants in FROZEN.items():
        for variant, expected in variants.items():
            key = f"{api}/p1/{variant}"
            actual = report["frames"].get(key, {}).get("run01")
            report["frozenParity"][key] = {
                "expected": expected,
                "actual": actual,
                "match": actual == expected,
            }

    for api in APIS:
        for variant in VARIANTS:
            left = f"{api}/p1/{variant}"
            right = f"{api}/p3/{variant}"
            if left not in raws or right not in raws:
                continue
            entry = {
                "full": stats(raws[left], raws[right]),
                "floorRoi": stats(raws[left], raws[right], ROI),
            }
            if variant == "base":
                entry["d5OneFrameJump"] = block_step(raws[left], raws[right])
            report["comparisons"][f"{api}/p1-vs-p3/{variant}"] = entry

    out = root / "Comparison.json"
    out.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({
        "determinism": report["determinism"],
        "frozenParity": report["frozenParity"],
        "comparisons": report["comparisons"],
    }, indent=2))
    print("written", out)


if __name__ == "__main__":
    main()
