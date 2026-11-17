"""Compare GTAO full-resolution captures before and after the F-01 guard fix.

The full resolution path used to read the half resolution depth mip with full resolution
coordinates. The CPU-side regression test pins the guard name; this script quantifies the visual
effect on the same scene and camera: full resolution AO should move towards the half resolution
reference instead of sampling unrelated depth.

Usage:
    python compare.py --before-dir .../P0/Smoke --after-dir .../P1-1/Player --out Metrics.json
"""

import argparse
import json
from pathlib import Path

from PIL import Image

SCALE = 2  # Downsample before the pure-Python luminance loop.


def luminance(path):
    image = Image.open(path).convert("RGB")
    width, height = image.size
    image = image.resize((max(1, width // SCALE), max(1, height // SCALE)), Image.BILINEAR)
    values = []
    data = image.tobytes()
    for index in range(0, len(data), 3):
        values.append(0.2126 * data[index] + 0.7152 * data[index + 1] + 0.0722 * data[index + 2])
    return values


def correlation(left, right):
    count = min(len(left), len(right))
    if count == 0:
        return 0.0
    mean_left = sum(left[:count]) / count
    mean_right = sum(right[:count]) / count
    covariance = 0.0
    variance_left = 0.0
    variance_right = 0.0
    for index in range(count):
        delta_left = left[index] - mean_left
        delta_right = right[index] - mean_right
        covariance += delta_left * delta_right
        variance_left += delta_left * delta_left
        variance_right += delta_right * delta_right
    denominator = (variance_left * variance_right) ** 0.5
    return covariance / denominator if denominator > 0 else 0.0


def mean_absolute_difference(left, right):
    count = min(len(left), len(right))
    if count == 0:
        return 0.0
    return sum(abs(left[index] - right[index]) for index in range(count)) / count


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--before-dir", type=Path, required=True)
    parser.add_argument("--after-dir", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    before_full = luminance(args.before_dir / "forward_gtao_full.png")
    before_half = luminance(args.before_dir / "forward_gtao_half.png")
    after_full = luminance(args.after_dir / "forward_gtao_full_after.png")
    after_half = luminance(args.after_dir / "forward_gtao_half_after.png")

    metrics = {
        "before": {
            "fullHalfCorrelation": correlation(before_full, before_half),
            "fullHalfMeanAbsoluteDifference": mean_absolute_difference(before_full, before_half),
        },
        "after": {
            "fullHalfCorrelation": correlation(after_full, after_half),
            "fullHalfMeanAbsoluteDifference": mean_absolute_difference(after_full, after_half),
        },
        "fullResolutionChange": {
            "meanAbsoluteDifference": mean_absolute_difference(before_full, after_full),
            "correlation": correlation(before_full, after_full),
        },
        "halfResolutionChange": {
            "meanAbsoluteDifference": mean_absolute_difference(before_half, after_half),
            "correlation": correlation(before_half, after_half),
        },
    }

    args.out.write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    print(json.dumps(metrics, indent=2))


if __name__ == "__main__":
    main()