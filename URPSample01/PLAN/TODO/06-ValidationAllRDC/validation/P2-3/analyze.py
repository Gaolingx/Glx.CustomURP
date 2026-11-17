#!/usr/bin/env python3
"""P2-3 SSGI denoiser projection metrics (F-09).

The denoiser reconstructs a world position per pixel, offsets it by a disk sample and projects the
tap back to the screen. The projection has to undo the render-target Y flip (`UNITY_UV_STARTS_AT_TOP`)
exactly like Unity's ComputeNormalizedDeviceCoordinatesWithZ; otherwise every tap lands on the
mirrored row, fails the depth test and the filter degrades to a pass-through.

Authoritative check: the validation player captures one frame with denoise on and the
`SslSsgiProjection` (filter input) and `SslSsgiDenoisedHF` (filter output) textures are exported from
that same capture. Alpha carries the SSGI validity (1 on a ray hit, 0 on a miss with rayMiss=None),
so a working bilateral filter turns the binary mask into a smooth field; a pass-through leaves it
binary.

Usage: python analyze.py
"""

import json
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
PLAYER = os.path.join(HERE, "Player")
EXPORT = os.path.join(PLAYER, "fix", "renderdoc-mcp-export")

X0, X1, Y0, Y1 = 160, 1120, 100, 650
MAX_HF_RATIO = 0.75
MIN_MID_FRACTION = 0.01
MIN_CHANGED_FRACTION = 0.05


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


def alpha(rows, channels):
    return [[rows[y][x * channels + 3] / 255.0 for x in range(X1 + 1)] for y in range(Y1 + 1)]


def alpha_stats(a):
    total = mid = high = low = 0
    hf = 0.0
    count = 0
    for y in range(Y0, Y1):
        for x in range(X0, X1):
            v = a[y][x]
            total += 1
            if v < 0.02:
                low += 1
            elif v > 0.98:
                high += 1
            else:
                mid += 1
    for y in range(Y0 + 1, Y1 - 1):
        for x in range(X0 + 1, X1 - 1):
            hf += abs(4.0 * a[y][x] - a[y - 1][x] - a[y + 1][x] - a[y][x - 1] - a[y][x + 1])
            count += 1
    return {
        "pixels": total,
        "midFraction": round(mid / float(total), 5),
        "highFraction": round(high / float(total), 5),
        "hf": round(hf / count, 6),
    }


def compare_exports(raw_name, filtered_name):
    try:
        _, _, cr, raw_rows = read_png(os.path.join(EXPORT, raw_name))
        _, _, cf, filtered_rows = read_png(os.path.join(EXPORT, filtered_name))
    except FileNotFoundError as error:
        return {"error": str(error)}

    raw_alpha = alpha(raw_rows, cr)
    filtered_alpha = alpha(filtered_rows, cf)
    raw_stats = alpha_stats(raw_alpha)
    filtered_stats = alpha_stats(filtered_alpha)

    changed = 0
    total = 0
    for y in range(Y0, Y1):
        for x in range(X0, X1):
            total += 1
            if abs(raw_alpha[y][x] - filtered_alpha[y][x]) > 0.01:
                changed += 1

    hf_ratio = filtered_stats["hf"] / raw_stats["hf"] if raw_stats["hf"] > 0.0 else 0.0
    changed_fraction = changed / float(total)
    passed = (hf_ratio <= MAX_HF_RATIO and
              filtered_stats["midFraction"] >= MIN_MID_FRACTION and
              changed_fraction >= MIN_CHANGED_FRACTION)

    return {
        "inputMidFraction": raw_stats["midFraction"],
        "outputMidFraction": filtered_stats["midFraction"],
        "inputHf": raw_stats["hf"],
        "outputHf": filtered_stats["hf"],
        "highFrequencyRatio": round(hf_ratio, 4),
        "changedFraction": round(changed_fraction, 4),
        "passed": passed,
    }


def luminance(rows, channels):
    values = []
    for y in range(Y1 + 1):
        row = rows[y]
        out = []
        for x in range(X1 + 1):
            i = x * channels
            if channels >= 3:
                out.append((0.2126 * row[i] + 0.7152 * row[i + 1] + 0.0722 * row[i + 2]) / 255.0)
            else:
                out.append(row[i] / 255.0)
        values.append(out)
    return values


def screenshot_pairs():
    """Cross-frame visual record only: the stochastic hit mask differs per frame, so the numeric
    acceptance uses the same-frame texture exports above. The screenshots show the alpha debug view,
    which the camera output stores as luminance with an opaque alpha."""
    result = {}
    for name in ("fullResolution", "halfResolution"):
        stem = "full" if name == "fullResolution" else "half"
        raw = os.path.join(PLAYER, "forward_ssgi_%s_denoise_raw.png" % stem)
        filtered = os.path.join(PLAYER, "forward_ssgi_%s_denoise_filtered.png" % stem)
        try:
            _, _, cr, raw_rows = read_png(raw)
            _, _, cf, filtered_rows = read_png(filtered)
        except FileNotFoundError as error:
            result[name] = {"error": str(error)}
            continue
        raw_hf = alpha_stats(luminance(raw_rows, cr))["hf"]
        filtered_hf = alpha_stats(luminance(filtered_rows, cf))["hf"]
        result[name] = {
            "inputHf": raw_hf,
            "outputHf": filtered_hf,
            "highFrequencyRatio": round(filtered_hf / raw_hf, 4) if raw_hf > 0.0 else 0.0,
        }
    return result


def main():
    same_frame = compare_exports("tex_ResourceId__9830_mip0.png", "tex_ResourceId__9840_mip0.png")

    report = {}
    report_path = os.path.join(PLAYER, "Report.json")
    if os.path.exists(report_path):
        with open(report_path, encoding="utf-8") as handle:
            report = json.load(handle)

    result = {
        "reportPassed": report.get("passed", False),
        "completed": report.get("completed", False),
        "sameFrame": same_frame,
        "screenshots": screenshot_pairs(),
        "thresholds": {
            "maxHighFrequencyRatio": MAX_HF_RATIO,
            "minOutputMidFraction": MIN_MID_FRACTION,
            "minChangedFraction": MIN_CHANGED_FRACTION,
        },
        "passed": report.get("passed", False) and same_frame.get("passed", False),
    }

    out = os.path.join(HERE, "Metrics.json")
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
