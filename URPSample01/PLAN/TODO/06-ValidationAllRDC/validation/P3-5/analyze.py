#!/usr/bin/env python3
"""P3-5 SSR correctness metrics from the validation player screenshots.

The player renders a static scene at 1280x720 with a 60 degree camera. All measurements use only
the committed PNGs, so the numbers in Report.md can be regenerated without RenderDoc.

Usage: python analyze.py [PlayerDir]
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
FOV_Y = math.radians(60.0)
ASPECT = WIDTH / float(HEIGHT)
TAN_HALF = math.tan(FOV_Y * 0.5)

# Calibration rig: camera (0, 2, -6) looking down +Z; emissive sphere at (-1.6, 1.2, 2); the mirror
# floor is the y = 0 plane.
CAMERA = (0.0, 2.0, -6.0)
EMISSIVE = (-1.6, 1.2, 2.0)
MIRROR_TARGET = (EMISSIVE[0], -EMISSIVE[1], EMISSIVE[2])
# Transparent SSR panel footprint, from the tilted rig (3.2 x 2.2 at z = 1.2, rotated -45 degrees)
# projected at runtime. The top edge is closer to the camera, so the box is wider than the untilted
# panel and slightly asymmetric.
PANEL = (480, 800, 325, 478)  # x0, x1, y0, y1 (exclusive)


def project(point):
    dz = point[2] - CAMERA[2]
    dx = point[0] - CAMERA[0]
    dy = point[1] - CAMERA[1]
    x = WIDTH * 0.5 + (dx / dz) / (TAN_HALF * ASPECT) * (WIDTH * 0.5)
    y = HEIGHT * 0.5 - (dy / dz) / TAN_HALF * (HEIGHT * 0.5)
    return x, y


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


def rgb(rows, channels, x, y):
    i = x * channels
    row = rows[y]
    if channels >= 3:
        return row[i], row[i + 1], row[i + 2]
    return row[i], row[i], row[i]


def luminance(px):
    return 0.2126 * px[0] + 0.7152 * px[1] + 0.0722 * px[2]


def diff(a, b):
    return max(abs(a[0] - b[0]), abs(a[1] - b[1]), abs(a[2] - b[2]))


def region_diff(rows_a, ca, rows_b, cb, x0, x1, y0, y1):
    pixels = total = 0
    above1 = 0
    maximum = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            d = diff(rgb(rows_a, ca, x, y), rgb(rows_b, cb, x, y))
            total += 1
            pixels += d
            if d > 1:
                above1 += 1
            if d > maximum:
                maximum = d
    return {
        "pixels": total,
        "meanAbsDiff": round(pixels / float(total), 3),
        "maxAbsDiff": maximum,
        "pixelsAbove1": above1,
    }


def load(name):
    return read_png(os.path.join(PLAYER, name))


def weight_coverage(weight, cw, threshold, x0=100, x1=1180, y0=600, y1=700):
    total = nonzero = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            total += 1
            if weight[y][x * cw] > threshold:
                nonzero += 1
    return {"pixels": total, "nonzero": nonzero, "fraction": round(nonzero / float(total), 4)}


def main():
    _, _, cw, weight = load("ssr_weight_map.png")
    _, _, caw, approx_weight = load("ssr_approx_weight_map.png")
    _, _, ca, final_on = load("ssr_final_on.png")
    _, _, cb, final_off = load("ssr_final_off.png")
    _, _, cz, intensity_zero = load("ssr_intensity_zero.png")
    _, _, ct, transparent_on = load("ssr_transparent_on.png")
    _, _, cto, transparent_off = load("ssr_transparent_off.png")
    _, _, cs, sky_on = load("ssr_reflectsky_on.png")
    _, _, cso, sky_off = load("ssr_reflectsky_off.png")
    _, _, cc, calibration_raw = load("ssr_mirror_calibration_raw.png")

    result = {}

    # R3 smoothness weight monotonicity. Sphere centres sit at the analytic projections. A fixed
    # disk also contains miss pixels (the reflected ray leaves the screen and the weight is zero),
    # so the p90 of the sphere's reflecting pixels is the stable material weight.
    samples = []
    for smoothness, cx in ((0.95, -6.0), (0.80, -2.0), (0.65, 2.0), (0.40, 6.0)):
        px, py = project((cx, 1.0, 4.0))
        values = []
        for y in range(int(py) - 50, int(py) + 51):
            for x in range(int(px) - 50, int(px) + 51):
                dx, dy = x - px, y - py
                if dx * dx + dy * dy <= 48 * 48:
                    values.append(weight[y][x * cw])
        values.sort()
        p90 = values[int((len(values) - 1) * 0.9)]
        samples.append({"smoothness": smoothness, "p90Weight": p90})
    monotonic = all(samples[i]["p90Weight"] > samples[i + 1]["p90Weight"] for i in range(len(samples) - 1))
    result["weightMonotonicity"] = {"samples": samples, "decreasing": monotonic, "passed": monotonic}

    # R3 hole filling coverage. Approximation has the explicit neighbour fill; PBR relies on the
    # 3x3 gather. The clean floor band has no object silhouettes.
    result["coverage"] = {
        "region": {"x": [100, 1180], "y": [600, 700]},
        "pbr": weight_coverage(weight, cw, 5),
        "approximation": weight_coverage(approx_weight, caw, 5),
    }
    result["coverage"]["approximation"]["passed"] = result["coverage"]["approximation"]["fraction"] >= 0.95
    result["coverage"]["pbr"]["passed"] = result["coverage"]["pbr"]["fraction"] >= 0.95

    # R3 mirror reflection centre. The calibration raw map holds only SSR radiance; the emissive
    # reflection is the only yellow blob and its centroid must match the mirrored centre.
    expected_x, expected_y = project(MIRROR_TARGET)
    sx = sy = sw = 0.0
    for y in range(540, 690):
        for x in range(440, 600):
            r, g, b = rgb(calibration_raw, cc, x, y)
            if r - b > 20 and g > 0.45 * r and r > 30:
                sx += x * r
                sy += y * r
                sw += r
    measured_x, measured_y = sx / sw, sy / sw
    error = math.hypot(measured_x - expected_x, measured_y - expected_y)
    result["mirrorCenter"] = {
        "expected": [round(expected_x, 2), round(expected_y, 2)],
        "measured": [round(measured_x, 2), round(measured_y, 2)],
        "errorPx": round(error, 2),
        "passed": error <= 2.0,
    }

    # Gradient orientation. The reflected quad must show blue, green, red, white from top to bottom;
    # a vertically flipped reflection would start with white.
    def classify(px):
        r, g, b = px
        if max(r, g, b) < 25:
            return "black"
        if r > 150 and g > 150 and b > 150:
            return "white"
        if b > r and b > g and b > 60:
            return "blue"
        if g > r and g > b and g > 60:
            return "green"
        if r > g and r > b and r > 60:
            return "red"
        return "other"

    runs = []
    previous = None
    for y in range(540, 700):
        kind = classify(rgb(calibration_raw, cc, 765, y))
        if kind != previous:
            runs.append([kind, y, y])
            previous = kind
        else:
            runs[-1][2] = y
    bands = [run[0] for run in runs if run[0] not in ("other", "black") and run[2] - run[1] >= 6]
    expected_bands = ["blue", "green", "red", "white"]
    result["gradientOrder"] = {
        "column": 765,
        "measured": bands[:4],
        "expected": expected_bands,
        "passed": bands[:4] == expected_bands,
    }

    # R4 transparent opt-in: only the panel that opts in changes; the opaque control region only
    # differs by the colour-history feedback of the new transparent result.
    panel_diff = region_diff(transparent_on, ct, transparent_off, cto, *PANEL)
    control_diff = region_diff(transparent_on, ct, transparent_off, cto, 60, 460, 520, 690)
    result["transparentOptIn"] = {
        "panelDiff": panel_diff,
        "controlDiff": control_diff,
        "passed": (panel_diff["maxAbsDiff"] >= 3 * control_diff["maxAbsDiff"]
                   and panel_diff["meanAbsDiff"] >= 1.0
                   and panel_diff["pixelsAbove1"] >= 3 * control_diff["pixelsAbove1"]),
    }

    # R4 transparent weights: the debug view re-emits the transparent SSR alpha, so the panel must
    # carry a non-zero reflection weight. Before the panel was tilted every transparent ray left the
    # screen and the whole map was black.
    _, _, ctw, transparent_weight = load("ssr_transparent_weight.png")
    weighted = total = 0
    for y in range(PANEL[2], PANEL[3]):
        for x in range(PANEL[0], PANEL[1]):
            total += 1
            if transparent_weight[y][x * ctw] > 5:
                weighted += 1
    result["transparentWeight"] = {
        "region": {"x": list(PANEL[:2]), "y": list(PANEL[2:])},
        "pixels": total,
        "weightedPixels": weighted,
        "fraction": round(weighted / float(total), 4),
        "passed": weighted >= total * 0.1,
    }

    # R4 reflectSky=0: the sky is untouched, the floor reflection changes.
    result["reflectSky"] = {
        "floorDiff": region_diff(sky_on, cs, sky_off, cso, 60, 1220, 430, 690),
        "skyDiff": region_diff(sky_on, cs, sky_off, cso, 60, 1220, 40, 300),
    }
    result["reflectSky"]["passed"] = (
        result["reflectSky"]["skyDiff"]["maxAbsDiff"] == 0
        and result["reflectSky"]["floorDiff"]["meanAbsDiff"] > 1.0)

    # R5 disabled baseline: intensity = 0 must be identical to SSR off.
    baseline = region_diff(final_off, cb, intensity_zero, cz, 0, WIDTH, 0, 690)
    result["baselineIntensityZero"] = baseline
    result["baselineIntensityZero"]["passed"] = baseline["maxAbsDiff"] == 0

    # Probe fallback: where no SSR was produced (weight zero, eroded by the material's bilinear tap
    # and excluding the transparent panel whose background is SSR shaded), the final colour must
    # keep the reflection probe.
    def isolated_zero(x, y, radius=3):
        for yy in range(max(0, y - radius), min(HEIGHT, y + radius + 1)):
            for xx in range(max(0, x - radius), min(WIDTH, x + radius + 1)):
                if weight[yy][xx * cw] > 5:
                    return False
        return True

    total = 0
    maximum = 0
    above1 = 0
    for y in range(0, 690):
        for x in range(0, WIDTH):
            if PANEL[0] <= x < PANEL[1] and PANEL[2] <= y < PANEL[3]:
                continue
            if weight[y][x * cw] > 5 or not isolated_zero(x, y):
                continue
            total += 1
            d = diff(rgb(final_on, ca, x, y), rgb(final_off, cb, x, y))
            if d > maximum:
                maximum = d
            if d > 1:
                above1 += 1
    result["probeFallback"] = {
        "pixels": total,
        "maxAbsDiff": maximum,
        "pixelsAbove1": above1,
        "passed": total > 0 and maximum <= 8 and above1 <= total * 0.0001,
    }

    with open(os.path.join(HERE, "Metrics.json"), "w") as handle:
        json.dump(result, handle, indent=2)

    for key, value in result.items():
        print(key, value)
    print("allPassed", all(entry.get("passed", True) for entry in result.values()))


if __name__ == "__main__":
    main()
