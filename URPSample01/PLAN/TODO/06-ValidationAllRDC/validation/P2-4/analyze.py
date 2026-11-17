#!/usr/bin/env python3
"""P2-4 SSGI coverage / probes / temporal / half-resolution / material metrics.

All numbers are recomputed from the screenshots the validation player writes, so the evidence can be
reviewed without RenderDoc. The debug views are written to an sRGB target, so every sampled value is
linearised before ratios or thresholds are applied.

Acceptance maps to the round plan:
  S2  coverage fraction >= 0.15, emitter G gain >= 1.5x, uncovered pixels == baked GI
  S3  static convergence, camera-cut validity reset, ghost residual after 30 frames
  S4  probe atlas on/off per renderer, four-colour weight falloff, zero probes without NaN
  S5  half-resolution upsample correlation >= 0.98 with full resolution and no 1 px seam
  S6  only Lit / ComplexLit / ShaderGraph Lit receive SSGI

Usage: python analyze.py
"""

import json
import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
PLAYER = os.path.join(HERE, "Player")

# S2 room layout (camera 0,2,-7): the whole interior, no sky.
S2_REGION = (60, 1220, 60, 690)
# Floor in front of the emissive back-wall panel.
S2_GAIN_REGION = (540, 740, 450, 500)
# Default rig scrub region used by the temporal cut residual (camera 0,2,-7).
DEFAULT_REGION = (60, 1220, 90, 690)
COVERAGE_WEIGHT = 0.5
MIN_COVERAGE = 0.15
MIN_EMITTER_GAIN = 1.5
MAX_UNCOVERED_DIFF = 1.0

# S3: mean absolute difference in 8-bit units.
MAX_CONVERGENCE_MEAN = 1.0
MAX_CUT_RESIDUAL_MEAN = 1.0

# S4: probe zones on the probe rig (camera 0,3.2,-8, 14 degrees down), one centre every 6 m.
PROBE_CENTERS = {-9: 239, -3: 507, 3: 774, 9: 1041}
PROBE_ROW = (335, 362)
PROBE_CORE_HALF_WIDTH = 55
PROBE_RAMP_HALF_WIDTH = 110
PROBE_ZONE_COLOURS = {-9: 0, -3: 1, 3: 2, 9: "rg"}  # red, green, blue, yellow (r+g)
MIN_PROBE_RATIO = 1.5
MAX_DEFAULT_RATIO = 1.2

# S5: smooth floor band shared by the full and half-resolution frames.
S5_REGION = (60, 1220, 360, 690)
MIN_HALF_CORRELATION = 0.98
MAX_SEAM_RATIO = 1.5

# S6: fin centres at z=4 for camera (0,2,-9); fin band y 350..395, half width 28 px.
FIN_CENTERS = {
    "lit": (160, "target"),
    "complexLit": (328, "target"),
    "simpleLit": (496, "control"),
    "bakedLit": (664, "control"),
    "unlit": (832, "control"),
    "shaderGraphLit": (1000, "target"),
    "terrain": (1200, "control"),
}
FIN_HALF_WIDTH = 24
FIN_Y = (400, 445)
TERRAIN_REGION = (1150, 1250, 392, 404)
MAX_CONTROL_DIFF = 1.0
MIN_TARGET_MEAN = 0.5
MIN_TARGET_MAX = 2.0


def read_png(path):
    data = open(path, "rb").read()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", path
    pos = 8
    idat = b""
    width = height = channels = 0
    while pos < len(data):
        length, = struct.unpack(">I", data[pos:pos + 4])
        ctype = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if ctype == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", chunk)
            assert depth == 8 and interlace == 0, (depth, interlace)
            channels = {0: 1, 2: 3, 4: 2, 6: 4}[color]
        elif ctype == b"IDAT":
            idat += chunk
        elif ctype == b"IEND":
            break
    stride = width * channels
    raw = zlib.decompress(idat)
    rows = []
    prev = bytearray(stride)
    p = 0
    for _ in range(height):
        f = raw[p]
        p += 1
        line = bytearray(raw[p:p + stride])
        p += stride
        if f == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 255
        elif f == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif f == 3:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 255
        elif f == 4:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = prev[i]
                c = prev[i - channels] if i >= channels else 0
                q = a + b - c
                pa, pb, pc = abs(q - a), abs(q - b), abs(q - c)
                pred = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pred) & 255
        rows.append(line)
        prev = line
    return width, height, channels, rows


def srgb_to_linear(value):
    v = value / 255.0
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4


def load_linear(path):
    _, _, channels, rows = read_png(path)
    image = []
    for row in rows:
        out_row = []
        for x in range(len(row) // channels):
            i = x * channels
            if channels >= 3:
                out_row.append((srgb_to_linear(row[i]), srgb_to_linear(row[i + 1]), srgb_to_linear(row[i + 2])))
            else:
                grey = srgb_to_linear(row[i])
                out_row.append((grey, grey, grey))
        image.append(out_row)
    return image


def load_raw_channel(path):
    _, _, channels, rows = read_png(path)
    # Debug mode 3 writes the weight into every channel and alpha stays opaque.
    return [[row[x * channels] / 255.0 for x in range(len(row) // channels)] for row in rows]


def region_mean(image, region):
    x0, x1, y0, y1 = region
    total = [0.0, 0.0, 0.0]
    count = 0
    for y in range(y0, y1):
        row = image[y]
        for x in range(x0, x1):
            c = row[x]
            total[0] += c[0]
            total[1] += c[1]
            total[2] += c[2]
            count += 1
    return [t / count for t in total]


def diff_stats(a, b, region, mask=None):
    x0, x1, y0, y1 = region
    biggest = 0.0
    total = 0.0
    count = 0
    above = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            if mask is not None and not mask[y][x]:
                continue
            d = max(abs(a[y][x][0] - b[y][x][0]), abs(a[y][x][1] - b[y][x][1]), abs(a[y][x][2] - b[y][x][2]))
            d8 = d * 255.0
            biggest = max(biggest, d8)
            total += d8
            count += 1
            if d8 > 1.0:
                above += 1
    return {
        "pixels": count,
        "mean": round(total / count, 4) if count else 0.0,
        "max": round(biggest, 4),
        "aboveOne": above,
    }


def pearson(a, b, region, mask=None):
    x0, x1, y0, y1 = region
    xs = []
    ys = []
    for y in range(y0, y1):
        for x in range(x0, x1):
            if mask is not None and not mask[y][x]:
                continue
            xs.append(0.2126 * a[y][x][0] + 0.7152 * a[y][x][1] + 0.0722 * a[y][x][2])
            ys.append(0.2126 * b[y][x][0] + 0.7152 * b[y][x][1] + 0.0722 * b[y][x][2])
    if len(xs) < 16:
        return 0.0
    mx = sum(xs) / len(xs)
    my = sum(ys) / len(ys)
    cov = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    vx = sum((x - mx) ** 2 for x in xs)
    vy = sum((y - my) ** 2 for y in ys)
    if vx <= 0.0 or vy <= 0.0:
        return 0.0
    return cov / math.sqrt(vx * vy)


def seam_ratio(image, region):
    """Ratio of horizontal neighbour differences crossing even/odd columns. A half-resolution
    upsample seam would make one parity systematically larger."""
    x0, x1, y0, y1 = region
    even = odd = 0.0
    even_n = odd_n = 0
    for y in range(y0, y1):
        row = image[y]
        for x in range(x0, x1 - 1):
            d = abs(row[x][1] - row[x + 1][1])
            if x % 2 == 0:
                even += d
                even_n += 1
            else:
                odd += d
                odd_n += 1
    even_mean = even / even_n if even_n else 0.0
    odd_mean = odd / odd_n if odd_n else 0.0
    ratio = max(even_mean, odd_mean) / min(even_mean, odd_mean) if min(even_mean, odd_mean) > 1e-9 else 0.0
    return round(ratio, 4)


def zone_ratio(image, centre, row, half_width, channels):
    x0, x1 = centre - half_width, centre + half_width
    y0, y1 = row
    total = [0.0, 0.0, 0.0]
    count = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            c = image[y][x]
            total[0] += c[0]
            total[1] += c[1]
            total[2] += c[2]
            count += 1
    mean = [t / count for t in total]
    if channels == "rg":
        other = (mean[2] + 1e-6)
        return round((mean[0] + mean[1]) / 2.0 / other, 4)
    other = sum(mean[i] for i in range(3) if i != channels) / 2.0
    return round(mean[channels] / (other + 1e-6), 4)


def yellow_ratio(image, centre, row, half_width):
    x0, x1 = centre - half_width, centre + half_width
    y0, y1 = row
    total = [0.0, 0.0, 0.0]
    count = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            c = image[y][x]
            total[0] += c[0]
            total[1] += c[1]
            total[2] += c[2]
            count += 1
    mean = [t / count for t in total]
    return round((mean[0] + mean[1]) * 0.5 / (mean[2] + 1e-6), 4)


def channel_chroma(image, centre, row, half_width, channel):
    x0, x1 = centre - half_width, centre + half_width
    y0, y1 = row
    total = 0.0
    count = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            c = image[y][x]
            others = [c[i] for i in range(3) if i != channel]
            total += max(0.0, c[channel] - sum(others) * 0.5)
            count += 1
    return total / count if count else 0.0


def probe_case(name):
    return os.path.join(PLAYER, name + ".png")


def main():
    metrics = {}
    report_path = os.path.join(PLAYER, "Report.json")
    report = {}
    if os.path.exists(report_path):
        with open(report_path, encoding="utf-8") as handle:
            report = json.load(handle)
    cases = {c["id"]: c for c in report.get("cases", [])}
    metrics["reportPassed"] = report.get("passed", False)
    metrics["caseCount"] = len(cases)

    # ---- S2 coverage / emitter gain / uncovered equality ----
    weights = load_raw_channel(os.path.join(PLAYER, "ssgi_coverage_weight.png"))
    x0, x1, y0, y1 = S2_REGION
    covered = 0
    nonzero = 0
    total = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            total += 1
            if weights[y][x] >= COVERAGE_WEIGHT:
                covered += 1
            if weights[y][x] > 0.02:
                nonzero += 1
    coverage = covered / float(total)
    any_coverage = nonzero / float(total)

    gain_on = load_linear(os.path.join(PLAYER, "ssgi_gain_on.png"))
    gain_off = load_linear(os.path.join(PLAYER, "ssgi_gain_off.png"))
    on_mean = region_mean(gain_on, S2_GAIN_REGION)
    off_mean = region_mean(gain_off, S2_GAIN_REGION)
    gain = on_mean[1] / off_mean[1] if off_mean[1] > 1e-6 else 0.0

    # Uncovered pixels: no traced ray and no fallback (rayMiss=None). Erode by one pixel so the
    # denoiser footprint around the boundary cannot bias the comparison.
    uncovered = [[False] * (x1 + 1) for _ in range(y1 + 1)]
    for y in range(y0 + 1, y1 - 1):
        for x in range(x0 + 1, x1 - 1):
            if all(weights[yy][xx] < 0.02 for yy in (y - 1, y, y + 1) for xx in (x - 1, x, x + 1)):
                uncovered[y][x] = True
    uncovered_diff = diff_stats(gain_on, gain_off, S2_REGION, uncovered)
    covered_pixels = sum(1 for y in range(y0, y1) for x in range(x0, x1) if uncovered[y][x])

    metrics["s2"] = {
        "coverageFraction": round(coverage, 4),
        "coverageAnyFraction": round(any_coverage, 4),
        "coverageThreshold": COVERAGE_WEIGHT,
        "emitterGain": round(gain, 4),
        "emitterRegionOnG": round(on_mean[1], 5),
        "emitterRegionOffG": round(off_mean[1], 5),
        "uncoveredPixels": covered_pixels,
        "uncoveredDiff": uncovered_diff,
        "passed": (coverage >= MIN_COVERAGE and gain >= MIN_EMITTER_GAIN and
                   covered_pixels > 0 and uncovered_diff["max"] <= MAX_UNCOVERED_DIFF),
    }

    # ---- S3 temporal ----
    early = load_linear(os.path.join(PLAYER, "ssgi_temporal_static.png"))
    late = load_linear(os.path.join(PLAYER, "ssgi_temporal_static_later.png"))
    convergence = diff_stats(early, late, S5_REGION)
    cut = cases.get("ssgi_temporal_cut", {}).get("ssgiSwitch")
    cut_frame = load_linear(os.path.join(PLAYER, "ssgi_temporal_cut.png"))
    cut_later = load_linear(os.path.join(PLAYER, "ssgi_temporal_cut_later.png"))
    cut_reference = load_linear(os.path.join(PLAYER, "ssgi_cut_reference.png"))
    residual = diff_stats(cut_later, cut_reference, DEFAULT_REGION)
    metrics["s3"] = {
        "convergence": convergence,
        "cameraCut": {
            "present": cut is not None,
            "beforeHistoryValid": cut.get("beforeHistoryValid") if cut else None,
            "afterHistoryValid": cut.get("afterHistoryValid") if cut else None,
            "beforeResetCount": cut.get("beforeResetCount") if cut else None,
            "afterResetCount": cut.get("afterResetCount") if cut else None,
            "passed": bool(cut and cut.get("passed")),
        },
        "resetFrameMean": round(region_mean(cut_frame, DEFAULT_REGION)[1], 5),
        "cutResidual": residual,
        "passed": (bool(cut and cut.get("passed")) and
                   convergence["mean"] <= MAX_CONVERGENCE_MEAN and
                   residual["mean"] <= MAX_CUT_RESIDUAL_MEAN),
    }

    # ---- S4 probes ----
    probe_cases = {}
    for name in ("ssgi_probes_forward_on", "ssgi_probes_forward_off", "ssgi_probes_forward_zero",
                 "ssgi_probes_forwardplus", "ssgi_probes_forwardplus_atlas",
                 "ssgi_probes_deferred_on", "ssgi_probes_deferred_off"):
        path = probe_case(name)
        if not os.path.exists(path):
            probe_cases[name] = {"error": "missing screenshot"}
            continue
        image = load_linear(path)
        green = zone_ratio(image, PROBE_CENTERS[-3], PROBE_ROW, PROBE_CORE_HALF_WIDTH, 1)
        red = zone_ratio(image, PROBE_CENTERS[-9], PROBE_ROW, PROBE_CORE_HALF_WIDTH, 0)
        blue = zone_ratio(image, PROBE_CENTERS[3], PROBE_ROW, PROBE_CORE_HALF_WIDTH, 2)
        yellow = yellow_ratio(image, PROBE_CENTERS[9], PROBE_ROW, PROBE_CORE_HALF_WIDTH)
        bands = []
        for half in (40, 75, 110):
            bands.append(round(channel_chroma(image, PROBE_CENTERS[-3], PROBE_ROW, half, 1), 6))
        probe_cases[name] = {
            "greenOverRed": green,
            "redOverOthers": red,
            "blueOverOthers": blue,
            "yellowOverBlue": yellow,
            "greenChromaBands": bands,
        }

    def ratio_of(name):
        return probe_cases.get(name, {}).get("greenOverRed", 0.0)

    forward_on = ratio_of("ssgi_probes_forward_on") >= MIN_PROBE_RATIO
    forward_off = ratio_of("ssgi_probes_forward_off") < MAX_DEFAULT_RATIO
    deferred_on = ratio_of("ssgi_probes_deferred_on") >= MIN_PROBE_RATIO
    deferred_off = ratio_of("ssgi_probes_deferred_off") < MAX_DEFAULT_RATIO
    forwardplus = (ratio_of("ssgi_probes_forwardplus") >= MIN_PROBE_RATIO and
                   ratio_of("ssgi_probes_forwardplus_atlas") >= MIN_PROBE_RATIO)
    zero_case = cases.get("ssgi_probes_forward_zero", {})
    zero_probes = {
        "nonFinitePixels": zero_case.get("nonFinitePixels"),
        "greenOverRed": ratio_of("ssgi_probes_forward_zero"),
        "passed": zero_case.get("nonFinitePixels") == 0 and ratio_of("ssgi_probes_forward_zero") < MAX_DEFAULT_RATIO,
    }
    ramp_cases = [bands for bands in (probe_cases.get(n, {}).get("greenChromaBands")
                                      for n in ("ssgi_probes_forward_on", "ssgi_probes_forwardplus",
                                                "ssgi_probes_forwardplus_atlas", "ssgi_probes_deferred_on"))
                  if bands]
    monotonic = len(ramp_cases) == 4 and all(
        bands[0] > bands[1] > bands[2] and bands[0] > 0.0 for bands in ramp_cases)
    metrics["s4"] = {
        "cases": probe_cases,
        "togglePassed": forward_on and forward_off and deferred_on and deferred_off,
        "forwardPlusPassed": forwardplus,
        "zeroProbes": zero_probes,
        "monotonicFalloff": bool(monotonic),
        "passed": (forward_on and forward_off and deferred_on and deferred_off and
                   forwardplus and zero_probes["passed"] and monotonic),
    }

    # ---- S5 half resolution ----
    full = load_linear(os.path.join(PLAYER, "ssgi_full_reference.png"))
    half = load_linear(os.path.join(PLAYER, "ssgi_half_reference.png"))
    correlation = pearson(full, half, S5_REGION)
    full_seam = seam_ratio(full, S5_REGION)
    half_seam = seam_ratio(half, S5_REGION)
    metrics["s5"] = {
        "correlation": round(correlation, 5),
        "fullSeamRatio": full_seam,
        "halfSeamRatio": half_seam,
        "seamRatio": round(half_seam / full_seam, 4) if full_seam > 0 else 0.0,
        "passed": correlation >= MIN_HALF_CORRELATION and
                  (half_seam <= max(MAX_SEAM_RATIO, full_seam * MAX_SEAM_RATIO)),
    }

    # ---- S6 materials ----
    material_on = load_linear(os.path.join(PLAYER, "ssgi_material_on.png"))
    material_off = load_linear(os.path.join(PLAYER, "ssgi_material_off.png"))
    materials = {}
    for name, (centre, role) in FIN_CENTERS.items():
        region = (TERRAIN_REGION if name == "terrain"
                  else (centre - FIN_HALF_WIDTH, centre + FIN_HALF_WIDTH, FIN_Y[0], FIN_Y[1]))
        stats = diff_stats(material_on, material_off, region)
        stats["role"] = role
        if role == "target":
            stats["passed"] = stats["mean"] >= MIN_TARGET_MEAN and stats["max"] >= MIN_TARGET_MAX
        else:
            stats["passed"] = stats["max"] <= MAX_CONTROL_DIFF
        materials[name] = stats
    metrics["s6"] = {
        "materials": materials,
        "passed": all(m["passed"] for m in materials.values()),
    }


    # ---- GPU exports (RenderDoc) ----
    export_dir = os.path.join(PLAYER, "GPU", "renderdoc-mcp-export")
    gpu = {}
    atlas_path = os.path.join(export_dir, "probe_atlas.png")
    if os.path.exists(atlas_path):
        aw, ah, _, _ = read_png(atlas_path)
        gpu["probeAtlasExport"] = {"width": aw, "height": ah}
    gpu_probe = {}
    for name, stem in (("on", "probe_on_denoised"), ("off", "probe_off_denoised")):
        path = os.path.join(export_dir, stem + ".png")
        if not os.path.exists(path):
            continue
        image = load_linear(path)
        green = zone_ratio(image, PROBE_CENTERS[-3], PROBE_ROW, PROBE_CORE_HALF_WIDTH, 1)
        gpu_probe[name] = {"greenZoneGreenOverOthers": green}
    if gpu_probe:
        gpu["probeZones"] = gpu_probe
    full_path = os.path.join(export_dir, "full_final.png")
    half_path = os.path.join(export_dir, "half_final.png")
    if os.path.exists(full_path) and os.path.exists(half_path):
        gpu["halfVsFullCorrelation"] = round(pearson(load_linear(full_path), load_linear(half_path), S5_REGION), 5)
    metrics["gpu"] = gpu

    metrics["passed"] = (metrics["reportPassed"] and metrics["s2"]["passed"] and metrics["s3"]["passed"] and
                         metrics["s4"]["passed"] and metrics["s5"]["passed"] and metrics["s6"]["passed"])

    with open(os.path.join(HERE, "Metrics.json"), "w", encoding="utf-8") as handle:
        json.dump(metrics, handle, indent=2)
    print(json.dumps(metrics, indent=2))


if __name__ == "__main__":
    main()