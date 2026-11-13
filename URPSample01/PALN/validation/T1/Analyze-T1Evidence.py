import argparse
import csv
import hashlib
import json
import shutil
from datetime import datetime, timezone
from pathlib import Path

MODES = {
    "SslForwardPipeline": "Forward",
    "SslForwardPlusPipeline": "ForwardPlus",
    "SslDeferredPipeline": "Deferred",
}


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def validate_protocol(report, budget):
    issues = []
    target = budget["primaryTarget"]
    expected_fields = {
        "unityVersion": budget["unityVersion"],
        "graphicsDeviceType": "Direct3D11",
        "randomSeed": target["randomSeed"],
        "warmupFrames": target["warmupFrames"],
        "sampleFrames": target["sampleFrames"],
        "targetFrameRate": target["targetFrameRate"],
        "width": int(target["resolution"].split("x")[0]),
        "height": int(target["resolution"].split("x")[1]),
    }
    for field, expected in expected_fields.items():
        if report.get(field) != expected:
            issues.append(f"{field}: expected {expected!r}, got {report.get(field)!r}")
    if not report.get("completed") or report.get("error"):
        issues.append("baseline report is incomplete or contains an error")

    modes = {mode["name"]: mode for mode in report.get("modes", [])}
    if set(modes) != set(MODES) or len(report.get("modes", [])) != len(MODES):
        issues.append("baseline report does not contain exactly the three required modes")
        return issues

    for name, mode in modes.items():
        if mode["cpuSampleCount"] < target["sampleFrames"] / 2:
            issues.append(f"{name}: insufficient CPU timing samples")
        if not mode["gpuTimingAvailable"] or mode["gpuSampleCount"] < target["sampleFrames"] / 2:
            issues.append(f"{name}: insufficient GPU timing samples")
        if not mode["screenshotCaptured"] or mode["magentaPixelFraction"] > 0.01:
            issues.append(f"{name}: invalid screenshot")
        if mode["graphicsDriverBytes"] <= 0:
            issues.append(f"{name}: missing graphics-driver memory counter")
    return issues


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runs", nargs=2, default=["Run1", "Run2"])
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    project_root = root.parents[2]
    budget = load_json(root / "PerformanceBudget.json")
    tolerance = budget["measurement"]["timingVariationTolerancePercent"]

    run_data = []
    for run_name in args.runs:
        run_root = root / run_name
        report = load_json(run_root / "BaselineReport.json")
        with (run_root / "FrameTimes.csv").open(newline="", encoding="utf-8-sig") as stream:
            rows = {row["Mode"]: row for row in csv.DictReader(stream)}
        if set(rows) != set(MODES):
            raise ValueError(f"{run_name} does not contain exactly the three required modes")
        run_data.append((run_name, run_root, report, rows))

    protocol = {}
    for name, _, report, _ in run_data:
        issues = validate_protocol(report, budget)
        protocol[name] = {
            "sampleFrames": report.get("sampleFrames"),
            "valid": not issues,
            "issues": issues,
        }
    protocol_valid = all(item["valid"] for item in protocol.values())
    environment_fields = (
        "unityVersion",
        "graphicsDeviceName",
        "graphicsDeviceType",
        "graphicsDeviceVersion",
    )
    environment_matches = all(
        run_data[0][2].get(field) == run_data[1][2].get(field)
        for field in environment_fields
    )

    comparisons = []
    for mode, image_name in MODES.items():
        first_name, first_root, first_report, first_rows = run_data[0]
        second_name, second_root, second_report, second_rows = run_data[1]
        first = first_rows[mode]
        second = second_rows[mode]
        first_image = first_root / f"{image_name}.png"
        second_image = second_root / f"{image_name}.png"
        first_hash = sha256(first_image)
        second_hash = sha256(second_image)
        timings = {}
        for metric in ("CpuP95Ms", "GpuP95Ms"):
            first_value = float(first[metric])
            second_value = float(second[metric])
            mean = (first_value + second_value) / 2
            variation = abs(first_value - second_value) / mean * 100 if mean else 0
            timings[metric] = {
                first_name: first_value,
                second_name: second_value,
                "symmetricVariationPercent": variation,
                "withinTolerance": variation <= tolerance,
            }
        first_mode = next(item for item in first_report["modes"] if item["name"] == mode)
        second_mode = next(item for item in second_report["modes"] if item["name"] == mode)
        comparisons.append({
            "mode": mode,
            "screenshots": {
                "file": f"{image_name}.png",
                "sha256": {first_name: first_hash, second_name: second_hash},
                "identical": first_hash == second_hash,
                "captured": first_mode["screenshotCaptured"] and second_mode["screenshotCaptured"],
                "magentaPixelFraction": {
                    first_name: first_mode["magentaPixelFraction"],
                    second_name: second_mode["magentaPixelFraction"],
                },
            },
            "graphicsDriverBytes": {
                first_name: first_mode["graphicsDriverBytes"],
                second_name: second_mode["graphicsDriverBytes"],
                "identical": first_mode["graphicsDriverBytes"] == second_mode["graphicsDriverBytes"],
            },
            "timing": timings,
        })

    images_reproducible = all(
        item["screenshots"]["identical"]
        and item["screenshots"]["captured"]
        and max(item["screenshots"]["magentaPixelFraction"].values()) <= 0.01
        for item in comparisons
    )
    memory_stable = all(item["graphicsDriverBytes"]["identical"] for item in comparisons)
    timing_within_tolerance = all(
        metric["withinTolerance"]
        for item in comparisons
        for metric in item["timing"].values()
    )
    reports_complete = all(item[2]["completed"] for item in run_data)
    reproducibility = {
        "analysisUtc": datetime.now(timezone.utc).isoformat(),
        "selectedRuns": args.runs,
        "availableRuns": sorted(path.name for path in root.glob("Run*") if path.is_dir()),
        "variationFormula": "abs(run2 - run1) / ((run1 + run2) / 2) * 100",
        "timingVariationTolerancePercent": tolerance,
        "reportsComplete": reports_complete,
        "protocol": protocol,
        "protocolValid": protocol_valid,
        "environmentMatches": environment_matches,
        "imagesReproducible": images_reproducible,
        "graphicsDriverMemoryStable": memory_stable,
        "timingWithinTolerance": timing_within_tolerance,
        "passed": reports_complete and protocol_valid and environment_matches and images_reproducible and memory_stable and timing_within_tolerance,
        "modes": comparisons,
    }
    write_json(root / "Reproducibility.json", reproducibility)

    golden_images_valid = protocol_valid and environment_matches and images_reproducible
    golden_root = project_root / "Assets" / "Tests" / "ScreenSpaceLighting" / "GoldenImages"
    golden_root.mkdir(parents=True, exist_ok=True)
    if golden_images_valid:
        for image_name in MODES.values():
            shutil.copy2(run_data[1][1] / f"{image_name}.png", golden_root / f"{image_name}.png")

    frame_debugger_path = root / "FrameDebugger" / "FrameDebuggerReport.json"
    frame_debugger = load_json(frame_debugger_path) if frame_debugger_path.exists() else None
    settings_path = root / "FrameDebuggerSettingsRestoration.json"
    settings = load_json(settings_path) if settings_path.exists() else None
    frame_debugger_passed = bool(
        frame_debugger
        and frame_debugger.get("completed")
        and len(frame_debugger.get("modes", [])) == 3
        and all(mode["eventCount"] > 0 and mode["eventNames"] for mode in frame_debugger["modes"])
    )
    settings_restored = bool(
        settings
        and settings.get("allSettingsRestored")
        and settings.get("unityExitCode") == 0
    )
    build = load_json(root / "BuildSummary.json")
    build_passed = build.get("result") == "Succeeded" and build.get("totalErrors") == 0
    build_settings_path = root / "BuildSettingsRestoration.json"
    build_settings = load_json(build_settings_path) if build_settings_path.exists() else None
    build_settings_restored = bool(
        build_settings
        and build_settings.get("allSettingsRestored")
        and build_settings.get("unityExitCode") == 0
    )
    summary = {
        "analysisUtc": datetime.now(timezone.utc).isoformat(),
        "build": build,
        "buildPassed": build_passed,
        "buildSettingsRestoration": {
            "file": "BuildSettingsRestoration.json" if build_settings else None,
            "passed": build_settings_restored,
        },
        "environment": load_json(root / "Environment.json"),
        "baselineRuns": {
            name: {
                "report": f"{name}/BaselineReport.json",
                "frameTimes": f"{name}/FrameTimes.csv",
                "rawFrameTimes": f"{name}/RawFrameTimes.csv" if (run_root / "RawFrameTimes.csv").exists() else None,
                "completed": report["completed"],
            }
            for name, run_root, report, _ in run_data
        },
        "reproducibility": {
            "file": "Reproducibility.json",
            "passed": reproducibility["passed"],
            "protocolValid": protocol_valid,
            "environmentMatches": environment_matches,
            "imagesReproducible": images_reproducible,
            "graphicsDriverMemoryStable": memory_stable,
            "timingWithinTolerance": timing_within_tolerance,
        },
        "frameDebugger": {
            "file": "FrameDebugger/FrameDebuggerReport.json" if frame_debugger else None,
            "passed": frame_debugger_passed,
            "eventCounts": {
                mode["name"]: mode["eventCount"] for mode in frame_debugger["modes"]
            } if frame_debugger else {},
        },
        "settingsRestoration": {
            "file": "FrameDebuggerSettingsRestoration.json" if settings else None,
            "passed": settings_restored,
        },
        "goldenImages": [f"{name}.png" for name in MODES.values()] if golden_images_valid else [],
        "t1ExitCriteriaMet": reproducibility["passed"] and build_passed and build_settings_restored and frame_debugger_passed and settings_restored,
    }
    write_json(root / "T1Report.json", summary)
    print(json.dumps(summary["reproducibility"], indent=2))
    print(f"Build passed: {build_passed}")
    print(f"Build settings restored: {build_settings_restored}")
    print(f"Frame Debugger passed: {frame_debugger_passed}")
    print(f"Frame Debugger settings restored: {settings_restored}")
    print(f"T1 exit criteria met: {summary['t1ExitCriteriaMet']}")


if __name__ == "__main__":
    main()
