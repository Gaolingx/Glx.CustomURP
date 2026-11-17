#!/usr/bin/env python3
"""P1-3 GTAO correctness metrics from the validation player screenshots.

All measurements use only the committed PNGs, so every number in Report.md can be regenerated
without RenderDoc: python analyze.py [PlayerDir]
"""

import json
import math
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
PLAYER = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "Player")

WIDTH, HEIGHT = 1280, 720
ASPECT = WIDTH / float(HEIGHT)
FOV_Y = math.radians(60.0)
TAN_HALF = math.tan(FOV_Y * 0.5)

# Default layout camera and the analytic world points used by the perspective metrics.
CAMERA = (0.0, 2.0, -7.0)
CONTACT = (-2.0, 0.0, 4.0)   # bottom of the sphere whose centre is (-2, 1, 4)
# Open floor in front of the sphere array (no occluder inside the 2 m world radius) and the
# gradient panel far behind it, which is unoccluded by construction.
OPEN_FLOOR = (0.0, 0.0, 2.0)
OPEN_PANEL = (0.0, 2.0, 13.0)

# Orthographic layout: camera (0, 3.5, -2) pitched 10 degrees down, orthographic size 4. The two
# sphere pairs sit at a 2 m and a 20 m contact depth with the same world size.
ORTHO_CAMERA = (0.0, 3.5, -2.0)
ORTHO_PITCH = math.radians(10.0)
ORTHO_SIZE = 4.0
ORTHO_CONTACTS = {
    "nearLeft": (-3.0, 0.0, -0.586),
    "nearRight": (3.0, 0.0, -0.586),
    "farLeft": (-3.0, 0.0, 17.710),
    "farRight": (3.0, 0.0, 17.710),
}
NEAR_DEPTH = 3.5 * math.sin(ORTHO_PITCH) + (1.413564 * math.cos(ORTHO_PITCH))
FAR_DEPTH = 3.5 * math.sin(ORTHO_PITCH) + (19.710300 * math.cos(ORTHO_PITCH))


def read_png(path):
    data = open(path, "rb").read()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", path
    pos = 8
    idat = b""
    while pos < len(data):
        length, = struct.unpack(">I", data[pos:pos + 4])
        ctype = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if ctype == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", chunk)
            assert depth == 8 and interlace == 0, (depth, interlace)
        elif ctype == b"IDAT":
            idat += chunk
        elif ctype == b"IEND":
            break
    channels = {0: 1, 2: 3, 4: 2, 6: 4}[color]
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


def project(point):
    dz = point[2] - CAMERA[2]
    dx = point[0] - CAMERA[0]
    dy = point[1] - CAMERA[1]
    x = WIDTH * 0.5 + (dx / dz) / (TAN_HALF * ASPECT) * (WIDTH * 0.5)
    y = HEIGHT * 0.5 - (dy / dz) / TAN_HALF * (HEIGHT * 0.5)
    return x, y


def ortho_project(point):
    forward = (0.0, -math.sin(ORTHO_PITCH), math.cos(ORTHO_PITCH))
    up = (0.0, math.cos(ORTHO_PITCH), math.sin(ORTHO_PITCH))
    delta = (point[0] - ORTHO_CAMERA[0], point[1] - ORTHO_CAMERA[1], point[2] - ORTHO_CAMERA[2])
    half_h = ORTHO_SIZE
    half_w = ORTHO_SIZE * ASPECT
    ndc_x = delta[0] / half_w
    ndc_y = (delta[1] * up[1] + delta[2] * up[2]) / half_h
    x = (ndc_x * 0.5 + 0.5) * WIDTH
    y = (0.5 - ndc_y * 0.5) * HEIGHT
    return x, y, forward


def linear_at(rows, channels, x, y):
    return srgb_to_linear(rows[y][x * channels])


def disc(rows, channels, cx, cy, radius):
    values = []
    x0, x1 = max(0, int(cx) - radius), min(WIDTH, int(cx) + radius + 1)
    y0, y1 = max(0, int(cy) - radius), min(HEIGHT, int(cy) + radius + 1)
    for y in range(y0, y1):
        for x in range(x0, x1):
            if (x - cx) ** 2 + (y - cy) ** 2 <= radius * radius:
                values.append(linear_at(rows, channels, x, y))
    values.sort()
    count = len(values)
    return {
        "pixels": count,
        "mean": round(sum(values) / count, 4),
        "p50": round(values[count // 2], 4),
        "p10": round(values[max(0, count // 10)], 4),
        "below090": sum(1 for v in values if v < 0.90),
        "below070": sum(1 for v in values if v < 0.70),
        "below097": sum(1 for v in values if v < 0.97),
        "fractionBelow090": round(sum(1 for v in values if v < 0.90) / count, 4),
    }


def luminance_downsampled(rows, channels, scale=2):
    values = []
    for y in range(0, HEIGHT - scale + 1, scale):
        for x in range(0, WIDTH - scale + 1, scale):
            total = 0.0
            for dy in range(scale):
                row = rows[y + dy]
                for dx in range(scale):
                    i = (x + dx) * channels
                    total += 0.2126 * row[i] + 0.7152 * row[i + 1] + 0.0722 * row[i + 2]
            values.append(total / (scale * scale))
    return values


def correlation(left, right):
    count = min(len(left), len(right))
    mean_left = sum(left[:count]) / count
    mean_right = sum(right[:count]) / count
    covariance = variance_left = variance_right = 0.0
    for i in range(count):
        a = left[i] - mean_left
        b = right[i] - mean_right
        covariance += a * b
        variance_left += a * a
        variance_right += b * b
    denominator = (variance_left * variance_right) ** 0.5
    return covariance / denominator if denominator > 0 else 0.0


def mean_absolute_difference(left, right):
    count = min(len(left), len(right))
    return sum(abs(left[i] - right[i]) for i in range(count)) / count


def load(name):
    return read_png(os.path.join(PLAYER, name))


def load_gray(name):
    _, _, channels, rows = load(name)
    return channels, rows


def main():
    result = {}

    # G3 contact/open visibility from the ambient-occlusion debug view.
    channels, contact_rows = load_gray("forward_gtao_ao_contact.png")
    cx, cy = project(CONTACT)
    ox, oy = project(OPEN_FLOOR)
    px, py = project(OPEN_PANEL)
    contact = disc(contact_rows, channels, cx, cy, 12)
    contact_broad = disc(contact_rows, channels, cx, cy, 24)
    open_floor = disc(contact_rows, channels, ox, oy, 20)
    open_panel = disc(contact_rows, channels, px, py, 20)
    result["contact"] = {"pixel": [round(cx, 2), round(cy, 2)], "disc12": contact, "disc24": contact_broad}
    result["openFloor"] = {"pixel": [round(ox, 2), round(oy, 2)], "disc20": open_floor}
    result["openPanel"] = {"pixel": [round(px, 2), round(py, 2)], "disc20": open_panel}
    result["contactPassed"] = contact["mean"] <= 0.70
    result["openPassed"] = open_floor["mean"] >= 0.98 and open_panel["mean"] >= 0.99

    # G3 intensity monotonicity.
    intensity = {}
    for label, name in (("05", "forward_gtao_ao_intensity_05.png"),
                        ("1", "forward_gtao_ao_contact.png"),
                        ("4", "forward_gtao_ao_intensity_4.png")):
        channels, rows = load_gray(name)
        intensity[label] = disc(rows, channels, cx, cy, 12)
    result["intensity"] = intensity
    means = [intensity["05"]["mean"], intensity["1"]["mean"], intensity["4"]["mean"]]
    result["intensityMonotonic"] = means[0] > means[1] > means[2]

    # G3 radius monotonicity: the strongly occluded footprint around the contact grows with the
    # world radius. A fixed near-white threshold is dominated by the flat floor level (and by the
    # sub-pixel sampling noise of the 0.25 m radius), so the strong footprint (<0.7) is measured.
    radius = {}
    for label in ("025", "1", "3"):
        channels, rows = load_gray("forward_gtao_ao_radius_%s.png" % label)
        radius[label] = disc(rows, channels, cx, cy, 120)["below070"]
    result["radiusStrongFootprint"] = radius
    result["radiusMonotonic"] = radius["025"] < radius["1"] < radius["3"]

    # G5 orthographic: identical spheres at 2 m and 20 m contact depth, left and right of the view.
    channels, ortho_rows = load_gray("forward_gtao_ortho.png")
    ortho = {}
    for name, point in ORTHO_CONTACTS.items():
        px, py, _ = ortho_project(point)
        ortho[name] = {"pixel": [round(px, 2), round(py, 2)], "disc40": disc(ortho_rows, channels, px, py, 40)}
    near_coverage = (ortho["nearLeft"]["disc40"]["fractionBelow090"] + ortho["nearRight"]["disc40"]["fractionBelow090"]) / 2.0
    far_coverage = (ortho["farLeft"]["disc40"]["fractionBelow090"] + ortho["farRight"]["disc40"]["fractionBelow090"]) / 2.0
    near_pair = abs(ortho["nearLeft"]["disc40"]["fractionBelow090"] - ortho["nearRight"]["disc40"]["fractionBelow090"])
    far_pair = abs(ortho["farLeft"]["disc40"]["fractionBelow090"] - ortho["farRight"]["disc40"]["fractionBelow090"])
    depth_relative = abs(near_coverage - far_coverage) / max(1e-6, (near_coverage + far_coverage) * 0.5)
    result["ortho"] = ortho
    result["orthoDepths"] = {"near": round(NEAR_DEPTH, 3), "far": round(FAR_DEPTH, 3)}
    result["orthoNearCoverage"] = round(near_coverage, 4)
    result["orthoFarCoverage"] = round(far_coverage, 4)
    result["orthoDepthRelativeDifference"] = round(depth_relative, 4)
    result["orthoScreenRelativeDifference"] = round(max(near_pair, far_pair), 4)
    result["orthoDepthPassed"] = depth_relative <= 0.05
    result["orthoScreenPassed"] = max(near_pair, far_pair) <= 0.05

    # G4 full resolution against the half resolution reference.
    cf, full_rows = load_gray("forward_gtao_full_temporal.png")
    ch, half_rows = load_gray("forward_gtao_half_temporal.png")
    full_luma = luminance_downsampled(full_rows, cf)
    half_luma = luminance_downsampled(half_rows, ch)
    result["fullHalfCorrelation"] = round(correlation(full_luma, half_luma), 5)
    result["fullHalfMeanAbsoluteDifference"] = round(mean_absolute_difference(full_luma, half_luma), 4)
    result["fullHalfPassed"] = result["fullHalfCorrelation"] >= 0.95

    # G2/G6 intensity=0 against a disabled feature: both must be byte identical.
    cz, zero_rows = load_gray("forward_gtao_off_zero.png")
    cd, disabled_rows = load_gray("forward_gtao_off_disabled.png")
    maximum = 0
    above = 0
    for y in range(HEIGHT):
        for x in range(WIDTH):
            d = abs(zero_rows[y][x * cz] - disabled_rows[y][x * cd])
            if d > maximum:
                maximum = d
            if d > 0:
                above += 1
    result["offBaseline"] = {"maxAbsDiff": maximum, "changedPixels": above}
    result["offBaselinePassed"] = maximum == 0

    statuses = [
        result["contactPassed"], result["openPassed"], result["intensityMonotonic"],
        result["radiusMonotonic"], result["orthoDepthPassed"], result["orthoScreenPassed"],
        result["fullHalfPassed"], result["offBaselinePassed"],
    ]
    result["passed"] = all(statuses)

    out = os.path.join(HERE, "Metrics.json")
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
