"""P1 acceptance: SS/RT boundary luma step.

Two masks come from the captured frame of the fixed player:

* _R1Original[slot] (64 B R1Sample, trace resolution 960x540) classifies every trace
  slot: reason 2 = the screen-space march resolved it (ssShare ~1), reason 4/6 = hybrid
  but left to the hardware path (requestShare > 0), reason 0/1 = outside the hybrid
  domain. The SS/RT seam is the boundary between reason 2 and reason 4/6.
* _R2Hits carries the debug colour (2,0,2) in every slot the deferred kernel wrote, i.e.
  the slots served by a hardware ray this frame.

A trace slot owns the full-resolution 2x2 block at (2tx, 2ty) (SsrGetFullResCoord in the
half-resolution path). For each adjacent pair across the boundary inside the floor ROI
the step is |mean luma(block) - mean luma(neighbour block)| in linear light; the "outer"
step from the RT block to the next block outward is the local-gradient control.

Usage:
  python seam_metric.py --originals _R1Original.bin --hits _R2Hits.bin \
      --frame p0=seam/p0/frame.raw --frame p1=seam/p1/frame.raw --out seam/seam_metric.json
"""
import argparse
import array
import json
import struct
from pathlib import Path

W, H = 1920, 1080
TW, TH = 960, 540
STRIDE = 64
ROI = (0.15, 0.45, 0.85, 0.95)
MAGENTA = (2.0, 0.0, 2.0)

SS_REASON = 2
RT_REASONS = (4, 6)


def load_hits_mask(path):
    data = Path(path).read_bytes()
    mask = bytearray(TW * TH)
    for i in range(TW * TH):
        r, g, b, hit_t = struct.unpack_from("<ffff", data, i * 24)
        flags, _mode = struct.unpack_from("<II", data, i * 24 + 16)
        if (r, g, b) == MAGENTA and hit_t == 1.0 and flags == 2:
            mask[i] = 1
    return mask


def load_classification(path):
    data = Path(path).read_bytes()
    if len(data) != TW * TH * STRIDE:
        raise SystemExit(f"unexpected _R1Original size: {len(data)}")
    reasons = bytearray(TW * TH)
    ss_share = array.array("f", bytes(TW * TH * 4))
    for i in range(TW * TH):
        # B6.2 (64 B record): slot, reason, position, roughness, normal, tMin, direction, tMax,
        # ssShare, requestShare. ssShare sits at byte 56; the coverage export carries it at 0.
        reason = struct.unpack_from("<I", data, i * STRIDE + 4)[0]
        ss_share[i] = struct.unpack_from("<f", data, i * STRIDE + 56)[0]
        reasons[i] = reason & 0xFF
    return reasons, ss_share


def load_luma(path):
    data = Path(path).read_bytes()
    if len(data) != W * H * 8:
        raise SystemExit(f"unexpected frame.raw size at {path}: {len(data)}")
    out = array.array("f", bytes(W * H * 4))
    unpack = struct.unpack_from
    for y in range(H):
        src = (H - 1 - y) * W * 8
        dst = y * W
        for x in range(W):
            r, g, b, _a = unpack("<eeee", data, src + x * 8)
            out[dst + x] = 0.2126 * r + 0.7152 * g + 0.0722 * b
    return out


def block_mean(luma, tx, ty):
    x = tx * 2
    y = ty * 2
    return 0.25 * (luma[y * W + x] + luma[y * W + x + 1] +
                   luma[(y + 1) * W + x] + luma[(y + 1) * W + x + 1])


def in_roi(tx, ty):
    x = (tx * 2 + 1) / W
    y = (ty * 2 + 1) / H
    return ROI[0] <= x <= ROI[2] and ROI[1] <= y <= ROI[3]


def pct(values, q):
    if not values:
        return 0.0
    return values[min(len(values) - 1, int(len(values) * q))]


def summarize(values):
    values.sort()
    n = len(values)
    return {
        "samples": n,
        "mean": sum(values) / n if n else 0.0,
        "median": pct(values, 0.5),
        "p90": pct(values, 0.9),
        "p95": pct(values, 0.95),
        "p99": pct(values, 0.99),
        "max": values[-1] if n else 0.0,
    }


def boundary_pairs(mask_a, mask_b):
    """Adjacent trace slots where one side is mask_a and the other mask_b, plus the step
    from the mask_b slot to the next slot outward as a local-gradient control."""
    pairs, outer = [], []
    for ty in range(TH):
        for tx in range(TW):
            if not mask_a[ty * TW + tx] or not in_roi(tx, ty):
                continue
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = tx + dx, ty + dy
                if nx < 0 or ny < 0 or nx >= TW or ny >= TH or not mask_b[ny * TW + nx]:
                    continue
                if mask_a[ny * TW + nx]:
                    continue
                pairs.append((tx, ty, nx, ny))
                ox, oy = nx + dx, ny + dy
                if 0 <= ox < TW and 0 <= oy < TH and not mask_a[oy * TW + ox] and not mask_b[oy * TW + ox]:
                    outer.append((nx, ny, ox, oy))
    return pairs, outer


def measure(luma, pairs, outer):
    steps = [abs(block_mean(luma, tx, ty) - block_mean(luma, nx, ny))
             for tx, ty, nx, ny in pairs]
    outer_steps = [abs(block_mean(luma, ax, ay) - block_mean(luma, bx, by))
                   for ax, ay, bx, by in outer]
    return summarize(steps), summarize(outer_steps)


def normalize(boundary, gradient):
    """B6.1: the raw step is content dominated (a high-contrast reflected object next to
    sky beats any shading difference), so the gate reads the boundary step relative to the
    local gradient just outside it. 1.0 means the SS/RT edge is no sharper than the scene's
    own gradients; values well above 1 are the seam."""
    median_gradient = gradient.get("median", 0.0)
    p90_gradient = gradient.get("p90", 0.0)
    eps = 1e-9
    return {
        "boundaryMedianOverGradientMedian": boundary.get("median", 0.0) / max(median_gradient, eps),
        "boundaryP90OverGradientP90": boundary.get("p90", 0.0) / max(p90_gradient, eps),
        "boundaryMeanOverGradientMean": boundary.get("mean", 0.0) / max(gradient.get("mean", 0.0), eps),
    }


def region_means(luma, reasons, ss_share):
    ss, rt, other = [], [], []
    for ty in range(TH):
        for tx in range(TW):
            if not in_roi(tx, ty):
                continue
            slot = ty * TW + tx
            mean = block_mean(luma, tx, ty)
            if reasons[slot] == SS_REASON and ss_share[slot] > 0.5:
                ss.append(mean)
            elif reasons[slot] in RT_REASONS:
                rt.append(mean)
            else:
                other.append(mean)
    return {
        "ssBlocks": len(ss), "ssMeanLuma": sum(ss) / len(ss) if ss else 0.0,
        "rtBlocks": len(rt), "rtMeanLuma": sum(rt) / len(rt) if rt else 0.0,
        "otherBlocks": len(other), "otherMeanLuma": sum(other) / len(other) if other else 0.0,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--originals", type=Path, required=True, help="_R1Original 64 B export")
    parser.add_argument("--hits", type=Path, help="debug=1 _R2Hits export (served mask)")
    parser.add_argument("--frame", action="append", default=[], metavar="NAME=PATH")
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    reasons, ss_share = load_classification(args.originals)
    ss_mask = bytearray(TW * TH)
    rt_mask = bytearray(TW * TH)
    for i in range(TW * TH):
        if reasons[i] == SS_REASON and ss_share[i] > 0.5:
            ss_mask[i] = 1
        elif reasons[i] in RT_REASONS:
            rt_mask[i] = 1

    report = {
        "frame": {"width": W, "height": H, "traceWidth": TW, "traceHeight": TH},
        "roi": {"x0": ROI[0], "y0": ROI[1], "x1": ROI[2], "y1": ROI[3], "space": "normalized top-down"},
        "classification": {
            "ssSlots": sum(ss_mask),
            "rtSlots": sum(rt_mask),
            "reasonHistogram": {str(r): reasons.count(r) for r in set(reasons)},
        },
        "frames": {},
    }

    ss_pairs, ss_outer = boundary_pairs(ss_mask, rt_mask)
    report["boundary"] = {"ssRtPairs": len(ss_pairs), "ssRtOuterPairs": len(ss_outer)}

    served_pairs = None
    if args.hits:
        served = load_hits_mask(args.hits)
        report["classification"]["servedSlots"] = sum(served)
        served_pairs, served_outer = boundary_pairs(served, bytearray(1 - b for b in served))
        report["boundary"]["servedPairs"] = len(served_pairs)

    for item in args.frame:
        name, _, path = item.partition("=")
        luma = load_luma(path)
        ss_rt, ss_rt_outer = measure(luma, ss_pairs, ss_outer)
        entry = {
            "path": Path(path).name,
            "region": region_means(luma, reasons, ss_share),
            "ssRtBoundary": ss_rt,
            "ssRtOuter": ss_rt_outer,
            "ssRtBoundaryNormalized": normalize(ss_rt, ss_rt_outer),
        }
        if served_pairs is not None:
            served_steps, _ = measure(luma, served_pairs, [])
            entry["servedBoundary"] = served_steps
        report["frames"][name] = entry

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)
        handle.write("\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
