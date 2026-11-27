"""P0 comparison: determinism, SSR contribution and denoise delta on the linear frames.

Windows: python compare.py [--root path/to/validation/P0/Win64-D3D12]

Reads frame.raw (RGBA16F linear, bottom-up) for the three variants' run-01 and writes
Comparison.json next to them. Run-02 hashes come from Player.json; a mismatch is reported, never
averaged away. The floor ROI is fixed here and echoed into the output so later rounds reuse it.
"""
import argparse
import json
import math
import struct
from pathlib import Path

W, H = 1920, 1080
VARIANTS = ("base", "nodenoise", "ssroff")
# Top-down normalized rectangle covering the reflective floor and the object reflections.
ROI = (0.15, 0.45, 0.85, 0.95)
SAMPLE_STRIDE = 2


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
    x0, y0, x1, y1 = ROI if roi else (0.0, 0.0, 1.0, 1.0)
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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent / "Win64-D3D12")
    args = parser.parse_args()
    root = args.root.resolve()

    report = {
        "frames": {},
        "determinism": {},
        "comparisons": {},
        "roi": {"x0": ROI[0], "y0": ROI[1], "x1": ROI[2], "y1": ROI[3], "space": "normalized top-down"},
        "sampleStride": SAMPLE_STRIDE,
    }
    raws = {}
    for variant in VARIANTS:
        run1 = root / variant / "run-01"
        run2 = root / variant / "run-02"
        p1 = read_json(run1 / "Player.json")
        p2 = read_json(run2 / "Player.json")
        report["frames"][variant] = {
            "run01": p1["frameSHA256"],
            "run02": p2["frameSHA256"],
            "lumaMean": p1["lumaMean"],
            "lumaMax": p1["lumaMax"],
            "nonFinite": p1["nonFiniteSamples"],
            "ssrEnabled": p1["ssrEnabled"],
            "ssrDenoise": p1["ssrDenoise"],
            "traceDispatches": p1["ssrDispatch"]["traceDispatches"],
            "hwMergeDispatches": p1["ssrDispatch"]["hwMergeDispatches"],
            "temporalDispatches": p1["ssrDispatch"]["temporalDispatches"],
            "historyValid": p1["ssrDispatch"]["historyValid"],
        }
        report["determinism"][variant] = {
            "bitIdentical": p1["frameSHA256"] == p2["frameSHA256"],
            "firstUnityFrame": [p1["firstUnityFrame"], p2["firstUnityFrame"]],
            "unityFrameAtCapture": [p1["unityFrameAtCapture"], p2["unityFrameAtCapture"]],
        }
        raws[variant] = load_raw(run1 / "frame.raw")

    for name, left, right in (
        ("base-vs-ssroff", "base", "ssroff"),
        ("base-vs-nodenoise", "base", "nodenoise"),
    ):
        report["comparisons"][name] = {
            "full": stats(raws[left], raws[right]),
            "floorRoi": stats(raws[left], raws[right], ROI),
        }

    out = root.parent / "Comparison.json"
    out.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("determinism", "comparisons")}, indent=2))
    print("written", out)


if __name__ == "__main__":
    main()
