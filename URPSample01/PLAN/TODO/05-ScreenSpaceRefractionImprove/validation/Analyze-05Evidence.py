#!/usr/bin/env python3
"""Rebuilds the 05 refraction evidence layout and reports from T11Verification.json."""
import csv
import datetime
import json
import os
import shutil

ROOT = os.path.dirname(os.path.abspath(__file__))
T11_JSON = os.path.join(ROOT, "T11Verification.json")
T11_LOG = os.path.join(ROOT, "Logs", "UnityT11Verification.log")
EDITMODE_LOG = os.path.join(ROOT, "Logs", "UnityEditModeTests.log")

TASK_IMAGES = {
    "P0-1": ["Reference", "RefractionOn", "NoProxyFallback", "InfiniteProxy", "Tinted",
             "CurrentColorPyramidPanelOn", "CurrentColorPyramidPanelOff"],
    "P0-2": ["Opacity0", "Opacity0p3", "Opacity1", "Opacity1Reference", "ModelsNone",
             "Intensity0", "OffAgain"],
    "P0-3": ["NoProxyFallback", "InfiniteProxy", "RefractionOn"],
    "P1-1": ["RefractionOn"],
    "P1-2": ["Tinted"],
}


def luminance(c):
    return 0.2126 * c["r"] + 0.7152 * c["g"] + 0.0722 * c["b"]


def linear_fit(xs, ys):
    n = len(xs)
    sx = sum(xs)
    sy = sum(ys)
    sxx = sum(x * x for x in xs)
    sxy = sum(x * y for x, y in zip(xs, ys))
    slope = (n * sxy - sx * sy) / (n * sxx - sx * sx)
    intercept = (sy - slope * sx) / n
    fitted = [intercept + slope * x for x in xs]
    ss_res = sum((y - f) ** 2 for y, f in zip(ys, fitted))
    mean = sum(ys) / n
    ss_tot = sum((y - mean) ** 2 for y in ys)
    r2 = 1.0 - ss_res / ss_tot if ss_tot else 1.0
    return slope, intercept, r2


def parse_test_counts(path):
    if not os.path.exists(path):
        return None
    text = open(path, encoding="utf-8", errors="replace").read()
    for line in text.splitlines():
        if "tests finished:" in line:
            counts = {}
            for part in line.split():
                if "=" in part:
                    k, v = part.split("=", 1)
                    if k in ("passed", "failed", "skipped"):
                        counts[k] = int(v)
            return counts
    return None


def main():
    data = json.load(open(T11_JSON, encoding="utf-8"))
    modes = {m["name"]: m for m in data["modes"]}

    for task, names in TASK_IMAGES.items():
        os.makedirs(os.path.join(ROOT, task), exist_ok=True)
        for mode in modes:
            for name in names:
                src = os.path.join(ROOT, f"T11_{mode}_{name}.png")
                dst = os.path.join(ROOT, task, os.path.basename(src))
                if os.path.exists(src) and not os.path.exists(dst):
                    shutil.copy2(src, dst)
            csv_name = f"T11_{mode}_OpacitySweep.csv"
            src = os.path.join(ROOT, csv_name)
            if task == "P0-2" and os.path.exists(src):
                dst = os.path.join(ROOT, task, csv_name)
                if not os.path.exists(dst):
                    shutil.copy2(src, dst)

    metrics = {}
    for name, m in modes.items():
        xs = [0.0, 0.3, 1.0]
        ys = [luminance(m["opacityZeroMean"]), luminance(m["opacityMidMean"]), luminance(m["opacityOneMean"])]
        slope, intercept, r2 = linear_fit(xs, ys)
        span = ys[0] - ys[2]
        normalized_mid = (ys[1] - ys[2]) / span if abs(span) > 1e-9 else 0.0
        mid_error = abs(normalized_mid - 0.7) / 0.7
        ref = next((s for s in m["states"] if s["state"] == "Reference"), {})
        metrics[name] = {
            "opacityLuminance": dict((str(a), round(b, 6)) for a, b in zip(xs, ys)),
            "opacityFitSlope": round(slope, 6),
            "opacityFitIntercept": round(intercept, 6),
            "opacityFitR2": round(r2, 6),
            "opacityNormalizedMid": round(normalized_mid, 6),
            "opacityMidError": round(mid_error, 6),
            "opacityOneChangedPixels": m.get("opacityOneChangedPixels"),
            "opacityOneMaxDelta": m.get("opacityOneMaxDelta"),
            "opacityMonotonic": m.get("opacityMonotonic"),
            "boxChangedPixels": m.get("boxChangedPixels"),
            "complexChangedPixels": m.get("complexChangedPixels"),
            "shaderGraphChangedPixels": m.get("shaderGraphChangedPixels"),
            "legacyChangedPixels": m.get("legacyChangedPixels"),
            "currentFramePyramidFollowsScene": m.get("currentFramePyramidFollowsScene"),
            "noProxyBoundCount": m.get("noProxyBoundCount"),
            "noProxyInfiniteCount": m.get("noProxyInfiniteCount"),
            "noProxyChangedPixels": m.get("noProxyChangedPixels"),
            "infiniteProxyBoundCount": m.get("infiniteProxyBoundCount"),
            "infiniteProxyCount": m.get("infiniteProxyCount"),
            "infiniteProxyChangedPixels": m.get("infiniteProxyChangedPixels"),
            "referenceSha256": ref.get("sha256"),
        }

    baseline = {
        "source": "T11Verification.json",
        "generatedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "modeReferenceSha256": dict((k, v["referenceSha256"]) for k, v in metrics.items()),
        "note": "Reference captured with the volume disabled and the refractive test materials at opacity 0.",
    }
    json.dump(baseline, open(os.path.join(ROOT, "BaselineHashes.json"), "w", encoding="utf-8"), indent=2)

    def write_csv(path, header, rows):
        with open(path, "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(header)
            for row in rows:
                w.writerow(row)

    write_csv(os.path.join(ROOT, "P0-1", "metrics.csv"),
              ["mode", "boxChangedPixels", "complexChangedPixels", "shaderGraphChangedPixels",
               "legacyChangedPixels", "currentFramePyramidFollowsScene", "referenceSha256"],
              [[k, v["boxChangedPixels"], v["complexChangedPixels"], v["shaderGraphChangedPixels"],
                v["legacyChangedPixels"], v["currentFramePyramidFollowsScene"], v["referenceSha256"]]
               for k, v in metrics.items()])
    write_csv(os.path.join(ROOT, "P0-2", "fitting.csv"),
              ["mode", "lum_opacity0", "lum_opacity0p3", "lum_opacity1", "fitSlope", "fitIntercept",
               "r2", "normalizedMid", "midError", "opacityOneChangedPixels", "opacityOneMaxDelta", "monotonic"],
              [[k, v["opacityLuminance"]["0.0"], v["opacityLuminance"]["0.3"], v["opacityLuminance"]["1.0"],
                v["opacityFitSlope"], v["opacityFitIntercept"], v["opacityFitR2"], v["opacityNormalizedMid"],
                v["opacityMidError"], v["opacityOneChangedPixels"], v["opacityOneMaxDelta"], v["opacityMonotonic"]]
               for k, v in metrics.items()])
    write_csv(os.path.join(ROOT, "P0-3", "metrics.csv"),
              ["mode", "noProxyBoundCount", "noProxyInfiniteCount", "noProxyChangedPixels",
               "infiniteProxyBoundCount", "infiniteProxyCount", "infiniteProxyChangedPixels"],
              [[k, v["noProxyBoundCount"], v["noProxyInfiniteCount"], v["noProxyChangedPixels"],
                v["infiniteProxyBoundCount"], v["infiniteProxyCount"], v["infiniteProxyChangedPixels"]]
               for k, v in metrics.items()])

    editmode = parse_test_counts(EDITMODE_LOG)
    mode_names = list(modes.keys())
    p02_pass = all(metrics[m]["opacityMonotonic"] and metrics[m]["opacityOneChangedPixels"] == 0
                   and metrics[m]["opacityMidError"] <= 0.05 and metrics[m]["opacityFitR2"] >= 0.99
                   for m in mode_names)
    p03_pass = all(metrics[m]["noProxyBoundCount"] > 0 and metrics[m]["noProxyInfiniteCount"] == 0
                   and metrics[m]["infiniteProxyBoundCount"] > 0 and metrics[m]["infiniteProxyCount"] > 0
                   for m in mode_names)
    p01_pass = all(metrics[m]["boxChangedPixels"] > 0 and metrics[m]["legacyChangedPixels"] == 0
                   and metrics[m]["currentFramePyramidFollowsScene"] for m in mode_names)

    cases = [
        {
            "taskId": "P0-1",
            "case": "screen_path_and_legacy_unchanged",
            "passed": bool(p01_pass),
            "images": [f"P0-1/T11_{m}_RefractionOn.png" for m in mode_names] +
                      [f"P0-1/T11_{m}_Reference.png" for m in mode_names],
            "metrics": {m: {k: metrics[m][k] for k in
                            ("boxChangedPixels", "complexChangedPixels", "shaderGraphChangedPixels",
                             "legacyChangedPixels", "currentFramePyramidFollowsScene")}
                        for m in mode_names},
            "baselineHash": baseline["modeReferenceSha256"],
            "notes": "Refraction changes the refractive glasses, the legacy transparent material is byte identical, "
                     "and the transmitted color follows the current-frame pyramid. A probe-only screen-miss image and "
                     "the edge-band continuity metric are not isolated yet (edge semantics is P2-1).",
        },
        {
            "taskId": "P0-2",
            "case": "opacity_mask_0p0_0p3_1p0",
            "passed": bool(p02_pass),
            "images": [f"P0-2/T11_{m}_Opacity0.png" for m in mode_names] +
                      [f"P0-2/T11_{m}_Opacity0p3.png" for m in mode_names] +
                      [f"P0-2/T11_{m}_Opacity1.png" for m in mode_names] +
                      [f"P0-2/T11_{m}_Opacity1Reference.png" for m in mode_names],
            "metrics": {m: {k: metrics[m][k] for k in
                            ("opacityLuminance", "opacityFitR2", "opacityMidError",
                             "opacityOneChangedPixels", "opacityOneMaxDelta", "opacityMonotonic")}
                        for m in mode_names},
            "baselineHash": baseline["modeReferenceSha256"],
            "notes": "Opacity 0/0.3/1 sweep in the box-glass interior; normalized mid error and least-squares fit are "
                     "in P0-2/fitting.csv. Opacity 1 is compared against the same surface with the volume disabled.",
        },
        {
            "taskId": "P0-3",
            "case": "any_probe_and_infinite_proxy",
            "passed": bool(p03_pass),
            "images": [f"P0-3/T11_{m}_NoProxyFallback.png" for m in mode_names] +
                      [f"P0-3/T11_{m}_InfiniteProxy.png" for m in mode_names],
            "metrics": {m: {k: metrics[m][k] for k in
                            ("noProxyBoundCount", "noProxyInfiniteCount", "noProxyChangedPixels",
                             "infiniteProxyBoundCount", "infiniteProxyCount", "infiniteProxyChangedPixels")}
                        for m in mode_names},
            "baselineHash": baseline["modeReferenceSha256"],
            "notes": "Non-box-projected probe still binds a box proxy (infinite count 0); disabling every probe "
                     "binds the infinite/sky proxy (infinite count > 0) and refraction stays visible.",
        },
        {
            "taskId": "P1-1",
            "case": "ior_fresnel0_analytic",
            "passed": None,
            "images": [f"P1-1/T11_{m}_RefractionOn.png" for m in mode_names],
            "metrics": {"analyticFresnel0": {"ior1.0": 0.0, "ior1.5": 0.04, "ior2.5": 0.1837}},
            "baselineHash": None,
            "notes": "Implemented and covered by the EditMode analytic contract test; no HDRP IOR gradient capture yet.",
        },
        {
            "taskId": "P1-2",
            "case": "thin_absorption_distance",
            "passed": None,
            "images": [f"P1-2/T11_{m}_Tinted.png" for m in mode_names],
            "metrics": {"thinAbsorptionDistance": 0.005},
            "baselineHash": None,
            "notes": "Implemented and covered by the static contract test; a coloured Thin Beer-Lambert capture is pending.",
        },
    ]

    report = {
        "round": "05-ScreenSpaceRefractionImprove",
        "generatedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "unityVersion": data.get("unityVersion"),
        "graphicsDeviceName": data.get("graphicsDeviceName"),
        "graphicsDeviceType": data.get("graphicsDeviceType"),
        "source": {
            "t11Verification": "T11Verification.json",
            "t11Log": "Logs/UnityT11Verification.log",
            "editModeLog": "Logs/UnityEditModeTests.log",
        },
        "t11Passed": data.get("passed"),
        "editMode": editmode,
        "cases": cases,
        "exitCriteriaMet": False,
        "exitCriteriaBlockers": [
            "P1-1 and P1-2 only have static/analytic evidence, no HDRP visual comparison.",
            "P0-1 probe-only screen-miss and edge-band continuity evidence depend on P2-1.",
            "P1-3 per-object probe table and all P2 tasks are not started.",
        ],
    }
    json.dump(report, open(os.path.join(ROOT, "Report.json"), "w", encoding="utf-8"), indent=2)
    print(json.dumps({"t11Passed": data.get("passed"), "editMode": editmode,
                      "p01": p01_pass, "p02": p02_pass, "p03": p03_pass}, indent=2))


if __name__ == "__main__":
    main()
