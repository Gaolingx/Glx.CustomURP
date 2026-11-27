"""B6.1 attribution matrix roll-up.

For every API/case: determinism (two fresh processes), and the luma difference against the
base case over the whole frame and the seam ROI. P1 is reported separately because its frozen
hashes are a regression gate, not a quality measurement.
"""
import array
import json
import struct
import sys
from pathlib import Path

W, H = 1920, 1080
ROI = (0.15, 0.45, 0.85, 0.95)
FROZEN_P1 = {"d3d12": "b1e0a889", "d3d11": "7fb3e01b"}
CASES = ["base", "thick", "k-low", "fullres", "nodenoise", "p1"]


def luma_topdown(path):
    data = Path(path).read_bytes()
    if len(data) != W * H * 8:
        raise SystemExit(f"unexpected frame.raw size {len(data)} at {path}")
    out = array.array("f", bytes(W * H * 4))
    row = W * 4
    # frame.raw is bottom-up (ReadPixels); the metric works top-down.
    for y in range(H):
        vals = struct.unpack_from(f"<{row}e", data, (H - 1 - y) * row * 2)
        base = y * W
        for x in range(W):
            i = x * 4
            out[base + x] = 0.2126 * vals[i] + 0.7152 * vals[i + 1] + 0.0722 * vals[i + 2]
    return out


def diff(a, b):
    x0, x1 = int(ROI[0] * W), int(ROI[2] * W)
    y0, y1 = int(ROI[1] * H), int(ROI[3] * H)
    total = roi_total = peak = 0.0
    over = 0
    for y in range(H):
        base = y * W
        in_roi_y = y0 <= y <= y1
        for x in range(W):
            d = abs(a[base + x] - b[base + x])
            total += d
            if d > peak:
                peak = d
            if d > 0.02:
                over += 1
            if in_roi_y and x0 <= x <= x1:
                roi_total += d
    roi_px = (x1 - x0 + 1) * (y1 - y0 + 1)
    return {
        "meanAbs": total / (W * H),
        "roiMeanAbs": roi_total / roi_px,
        "peak": peak,
        "over002": over,
    }


def main(root):
    root = Path(root)
    report = {"cases": {}, "determinism": {}, "p1": {}, "diffs": {}}
    frames = {}
    for api in ("d3d12", "d3d11"):
        report["cases"][api] = {}
        for case in CASES:
            d1 = root / api / case / "run-01"
            d2 = root / api / case / "run-02"
            if not (d1 / "Player.json").exists():
                report["cases"][api][case] = {"status": "MISSING"}
                continue
            j1 = json.loads((d1 / "Player.json").read_text())
            j2 = json.loads((d2 / "Player.json").read_text()) if (d2 / "Player.json").exists() else None
            report["cases"][api][case] = {
                "passed": j1.get("passed"),
                "sha": j1.get("frameSHA256", "")[:16],
                "p3": j1.get("p3SsrAfterGbufferRequested"),
                "thickness": j1.get("p3ThicknessRequested"),
                "fullres": j1.get("p3FullResolutionRequested"),
                "budget": j1.get("p3BudgetRequested"),
                "originalsBinBytes": j1.get("originalsBinBytes"),
                "hitsBinBytes": j1.get("hitsBinBytes"),
                "counters": j1.get("r1Counters") or j1.get("evidenceCounters"),
            }
            report["determinism"][f"{api}/{case}"] = bool(
                j1 and j2 and j1.get("frameSHA256") == j2.get("frameSHA256"))
            if j1.get("originalsBinBytes"):
                pass
            frames[(api, case)] = d1 / "frame.raw"
        base = frames.get((api, "base"))
        if base is None:
            continue
        base_luma = luma_topdown(base)
        report["diffs"][api] = {}
        for case in ("thick", "k-low", "fullres", "nodenoise", "p1"):
            path = frames.get((api, case))
            if path is None:
                continue
            report["diffs"][api][case] = diff(base_luma, luma_topdown(path))
        p1 = report["cases"][api].get("p1", {})
        sha = (p1.get("sha") or "")
        report["p1"][api] = {
            "sha16": sha,
            "frozen16": FROZEN_P1[api],
            "matches": sha.startswith(FROZEN_P1[api]),
        }
    out = root / "Comparison.json"
    out.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else
         "PLAN/TODO/23-Ray_Tracing_Inprove_2/validation/P3/B61")
