#!/usr/bin/env python3
"""P1-4 GTAO material coverage and Deferred dual-path metrics.

Only reads the committed PNGs, so every number in Report.md can be regenerated with
python analyze.py [PlayerDir].
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
TAN_HALF = math.tan(math.radians(60.0) * 0.5)

CAMERA = (0.0, 2.0, -9.0)

# Coverage rig (ScreenSpaceLightingValidationPlayerVerification.BuildCoverageRig): five fins with
# their front face at z=3.8, a TerrainLit patch spanning x 5.5..13.5 / z 0..8 with a contact box at
# (9.5, 4), and a transparent panel against the sky.
FINS = {
    "lit": (-10.0, 0.4, 3.8),
    "complexLit": (-6.5, 0.4, 3.8),
    "simpleLit": (-3.0, 0.4, 3.8),
    "bakedLit": (0.5, 0.4, 3.8),
    "unlit": (4.0, 0.4, 3.8),
}
FLOOR_CONTACT = (-3.0, 0.0, 3.3)        # floor 0.5 m in front of the SimpleLit fin
TERRAIN_CONTACT = (9.5, 0.01, 3.0)      # terrain 0.4 m in front of the contact box (box front z=3.4)
TERRAIN_OPEN = (7.0, 0.01, 1.0)         # terrain away from the box
TRANSPARENT = (0.0, 5.2, 12.0)          # transparent panel centre, sky behind

AO_RECEIVING = ("lit", "complexLit", "simpleLit", "bakedLit")
CHECK_POINTS = ("lit", "floorContact", "terrainContact")


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
    dx = point[0] - CAMERA[0]
    dy = point[1] - CAMERA[1]
    dz = point[2] - CAMERA[2]
    x = WIDTH * 0.5 + (dx / dz) / (TAN_HALF * ASPECT) * (WIDTH * 0.5)
    y = HEIGHT * 0.5 - (dy / dz) / TAN_HALF * (HEIGHT * 0.5)
    return x, y


def disc_coords(cx, cy, radius):
    points = []
    x0, x1 = max(0, int(cx) - radius), min(WIDTH, int(cx) + radius + 1)
    y0, y1 = max(0, int(cy) - radius), min(HEIGHT, int(cy) + radius + 1)
    for y in range(y0, y1):
        for x in range(x0, x1):
            if (x - cx) ** 2 + (y - cy) ** 2 <= radius * radius:
                points.append((x, y))
    return points


def channel(rows, channels, x, y, index):
    return rows[y][x * channels + min(index, channels - 1)]


def luminance(rows, channels, x, y):
    return (0.2126 * srgb_to_linear(channel(rows, channels, x, y, 0)) +
            0.7152 * srgb_to_linear(channel(rows, channels, x, y, 1)) +
            0.0722 * srgb_to_linear(channel(rows, channels, x, y, 2)))


def mean_luminance(rows, channels, cx, cy, radius):
    points = disc_coords(cx, cy, radius)
    return sum(luminance(rows, channels, x, y) for x, y in points) / len(points)


def region(rows_on, ch_on, rows_off, ch_off, cx, cy, radius):
    points = disc_coords(cx, cy, radius)
    on_values = [luminance(rows_on, ch_on, x, y) for x, y in points]
    off_values = [luminance(rows_off, ch_off, x, y) for x, y in points]
    max_diff = 0
    changed = 0
    for x, y in points:
        d = 0
        for index in range(3):
            d = max(d, abs(channel(rows_on, ch_on, x, y, index) - channel(rows_off, ch_off, x, y, index)))
        if d > 0:
            changed += 1
        max_diff = max(max_diff, d)
    count = len(points)
    mean_on = sum(on_values) / count
    mean_off = sum(off_values) / count
    return {
        "pixel": [round(cx, 2), round(cy, 2)],
        "pixels": count,
        "meanOn": round(mean_on, 5),
        "meanOff": round(mean_off, 5),
        "ratio": round(mean_on / mean_off, 5) if mean_off > 1e-6 else 0.0,
        "delta": round(mean_on - mean_off, 6),
        "maxByteDiff": max_diff,
        "changedFraction": round(changed / float(count), 4),
    }


def load(name):
    return read_png(os.path.join(PLAYER, name))


def byte_diff(rows_a, ca, rows_b, cb):
    maximum = 0
    changed = 0
    for y in range(HEIGHT):
        for x in range(WIDTH):
            d = max(abs(rows_a[y][x * ca + c] - rows_b[y][x * cb + c]) for c in range(min(ca, cb)))
            maximum = max(maximum, d)
            if d > 0:
                changed += 1
    return {"maxAbsDiff": maximum, "changedPixels": changed}


def single_application(pair, point, radius):
    """Compare the with-light pair with the ambient-only pair at the same pixel.

    In linear space the shaded colour is A*ao + D, where A is the ambient part (measured with the
    directional light off) and D the directional part. The AO pass must therefore move the pixel by
    A*(ao-1) in both Deferred lighting paths; a second multiply would move it by A*(ao^2-1).
    """
    _, _, ch_on, on_dir = pair["On"]
    _, _, ch_off, off_dir = pair["Off"]
    _, _, cn_on, on_ambient = pair["NoLightOn"]
    _, _, cn_off, off_ambient = pair["NoLightOff"]

    cx, cy = project(point)
    ambient = mean_luminance(off_ambient, cn_off, cx, cy, radius)
    lit = mean_luminance(off_dir, ch_off, cx, cy, radius)
    ambient_on = mean_luminance(on_ambient, cn_on, cx, cy, radius)
    lit_on = mean_luminance(on_dir, ch_on, cx, cy, radius)
    ao = ambient_on / ambient if ambient > 1e-6 else 0.0
    predicted = lit + ambient * (ao - 1.0)
    double_predicted = lit + ambient * (ao * ao - 1.0)
    return {
        "pixel": [round(cx, 2), round(cy, 2)],
        "ambientOff": round(ambient, 5),
        "litOff": round(lit, 5),
        "ao": round(ao, 5),
        "litOnMeasured": round(lit_on, 5),
        "litOnSingleApplication": round(predicted, 5),
        "litOnDoubleApplication": round(double_predicted, 5),
        "errorSingle": round(abs(lit_on - predicted) / max(lit, 1e-5), 5),
        "errorDouble": round(abs(lit_on - double_predicted) / max(lit, 1e-5), 5),
    }


def main():
    result = {}
    pairs = {
        "forward": ("forward_coverage_on.png", "forward_coverage_off.png"),
        "forwardPlus": ("forwardplus_coverage_on.png", "forwardplus_coverage_off.png"),
        "deferred": ("deferred_coverage_on.png", "deferred_coverage_off.png"),
        "forwardNoLight": ("forward_nolight_on.png", "forward_nolight_off.png"),
        "deferredNoLight": ("deferred_nolight_on.png", "deferred_nolight_off.png"),
    }

    images = {}
    for name, (on_name, off_name) in pairs.items():
        images[name] = (load(on_name), load(off_name))

    for name, ((_, _, con, on_rows), (_, _, coff, off_rows)) in images.items():
        entry = {}
        for material, point in FINS.items():
            cx, cy = project(point)
            entry[material] = region(on_rows, con, off_rows, coff, cx, cy, 6)
        cx, cy = project(FLOOR_CONTACT)
        entry["floorContact"] = region(on_rows, con, off_rows, coff, cx, cy, 3)
        cx, cy = project(TERRAIN_CONTACT)
        entry["terrainContact"] = region(on_rows, con, off_rows, coff, cx, cy, 3)
        cx, cy = project(TERRAIN_OPEN)
        entry["terrainOpen"] = region(on_rows, con, off_rows, coff, cx, cy, 8)
        cx, cy = project(TRANSPARENT)
        entry["transparent"] = region(on_rows, con, off_rows, coff, cx, cy, 20)
        result[name] = entry

    # AO value the material sampled, from the material facing global debug view.
    _, _, cfa, forward_ao = load("forward_nolight_ao.png")
    _, _, cda, deferred_ao = load("deferred_nolight_ao.png")
    ao_values = {}
    for point_name, point, radius in (("floorContact", FLOOR_CONTACT, 2),
                                      ("terrainContact", TERRAIN_CONTACT, 2),
                                      ("lit", FINS["lit"], 3)):
        cx, cy = project(point)
        ao_values[point_name] = {
            "forward": round(mean_luminance(forward_ao, cfa, cx, cy, radius), 4),
            "deferred": round(mean_luminance(deferred_ao, cda, cx, cy, radius), 4),
        }
    result["aoValues"] = ao_values

    # The ambient-only ratio must equal the AO the material sampled: one multiply, no more.
    ratio_matches = {}
    for prefix, key in (("forwardNoLight", "forward"), ("deferredNoLight", "deferred")):
        for point_name in ("floorContact", "terrainContact", "lit"):
            ratio = result[prefix][point_name]["ratio"]
            ao = ao_values[point_name][key]
            ratio_matches[prefix + ":" + point_name] = {
                "ratio": ratio,
                "ao": ao,
                "ratioError": round(abs(ratio - ao), 4),
            }
    result["ratioMatchesAO"] = ratio_matches
    result["ratioMatchesAOPassed"] = all(item["ratioError"] <= 0.02 for item in ratio_matches.values())

    # Deferred dual path: the directional light path and the AmbientOcclusionOnly path must move
    # the pixel by the same A*(ao-1); the forward path is measured with the same formula as a check.
    deferred_pair = {
        "On": images["deferred"][0], "Off": images["deferred"][1],
        "NoLightOn": images["deferredNoLight"][0], "NoLightOff": images["deferredNoLight"][1],
    }
    forward_pair = {
        "On": images["forward"][0], "Off": images["forward"][1],
        "NoLightOn": images["forwardNoLight"][0], "NoLightOff": images["forwardNoLight"][1],
    }
    application = {}
    for name, pair in (("deferred", deferred_pair), ("forward", forward_pair)):
        rows = {}
        for point_name in CHECK_POINTS:
            point = FINS[point_name] if point_name in FINS else (
                FLOOR_CONTACT if point_name == "floorContact" else TERRAIN_CONTACT)
            rows[point_name] = single_application(pair, point, 3)
        application[name] = rows
    result["singleApplication"] = application
    # The acceptance gate covers the two Deferred paths. The forward rows are reported for
    # comparison; at small AO deltas their 8-bit quantisation is comparable to the difference
    # between the single and double hypotheses, so they are not used as a gate.
    result["singleApplicationPassed"] = all(
        row["errorSingle"] <= 0.02 and row["errorSingle"] < row["errorDouble"]
        for row in application["deferred"].values())

    # Material coverage. Forward/Forward+/Deferred must darken the AO receiving families and leave
    # Unlit, the transparent panel and the unoccluded terrain untouched.
    coverage = {}
    for name in ("forward", "forwardPlus", "deferred"):
        entry = result[name]
        receiving = {material: entry[material]["ratio"] <= 0.97 and entry[material]["changedFraction"] >= 0.5
                     for material in AO_RECEIVING}
        contact_gap = round(entry["terrainOpen"]["ratio"] - entry["terrainContact"]["ratio"], 4)
        # With a directional light AO only scales the ambient term, so a dark albedo plus a strong
        # direct term leaves a small pixel change. The ambient-only pair isolates the AO factor for
        # the terrain coverage gate; the lit pair still has to darken the contact more than the open
        # terrain. Forward+ has no ambient-only pair in this manifest (its TerrainLit renders
        # ambient-only regardless of the light, so the terrain gate is not evaluated there).
        ambient_pair = {"forward": "forwardNoLight", "deferred": "deferredNoLight"}.get(name)
        ambient = None
        if ambient_pair is not None:
            ambient_entry = result[ambient_pair]
            ambient_gap = round(ambient_entry["terrainOpen"]["ratio"] -
                                ambient_entry["terrainContact"]["ratio"], 4)
            ambient = {
                "contactRatio": ambient_entry["terrainContact"]["ratio"],
                "openRatio": ambient_entry["terrainOpen"]["ratio"],
                "gap": ambient_gap,
                "passed": ambient_entry["terrainContact"]["ratio"] <= 0.95 and ambient_gap >= 0.03,
            }
        coverage[name] = {
            "receiving": receiving,
            "terrainAmbient": ambient,
            "terrainContactVsOpenGap": contact_gap,
            "terrainContactDarkerThanOpen": contact_gap >= 0.005,
            "unlitUnchanged": entry["unlit"]["maxByteDiff"] == 0,
            "transparentUnchanged": entry["transparent"]["maxByteDiff"] == 0,
            "passed": (all(receiving.values()) and contact_gap >= 0.005 and
                       (ambient is None or ambient["passed"]) and
                       entry["unlit"]["maxByteDiff"] == 0 and
                       entry["transparent"]["maxByteDiff"] == 0),
        }
    result["coverage"] = coverage

    # Closed baselines: intensity=0 and a disabled feature must be byte identical for both Deferred
    # AO paths (directional light and AmbientOcclusionOnly).
    baselines = {}
    for zero_name, disabled_name in (("deferred_off_zero.png", "deferred_off_disabled.png"),
                                     ("deferred_nolight_off_zero.png", "deferred_nolight_off_disabled.png")):
        _, _, cz, zero_rows = load(zero_name)
        _, _, cd, disabled_rows = load(disabled_name)
        baselines[zero_name] = byte_diff(zero_rows, cz, disabled_rows, cd)
    result["offBaseline"] = baselines
    result["offBaselinePassed"] = all(item["maxAbsDiff"] == 0 for item in baselines.values())

    result["passed"] = (result["coverage"]["forward"]["passed"] and
                        result["coverage"]["forwardPlus"]["passed"] and
                        result["coverage"]["deferred"]["passed"] and
                        result["ratioMatchesAOPassed"] and
                        result["singleApplicationPassed"] and
                        result["offBaselinePassed"])

    out = os.path.join(HERE, "Metrics.json")
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
