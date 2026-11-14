#!/usr/bin/env python3
"""Aggregate the T3 empty-framework evidence into a single machine-checkable report.

Inputs (all relative to this directory):
  T3Verification.json              rendering / lifecycle verification per pipeline path
  FrameDebuggerNoPlugin/*.json     no-plugin event data per path
  UI/T3UiEvidence.json             real editor UI path capture report
  UnityTests.log                   EditMode test summary
  ../../validation/T1/T1Report.json        T1 baseline reference (Run17/Run18)

Output: T3Report.json
"""

import datetime
import hashlib
import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
PROJECT = HERE.parent.parent.parent
MODES = ("Forward", "ForwardPlus", "Deferred")


def load(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def ui_evidence():
    path = HERE / "UI" / "T3UiEvidence.json"
    report = load(path)
    captures = []
    for entry in report.get("captures", []):
        file = pathlib.Path(entry["file"])
        captures.append({
            "step": entry["step"],
            "file": file.name,
            "width": entry["width"],
            "height": entry["height"],
            "sha256": sha256(file) if file.is_file() else None,
        })
    features = report.get("rendererFeatures", [])
    components = report.get("volumeComponents", [])
    problems = list(report.get("errors", []))
    if report.get("status") != "completed":
        problems.append("UI evidence status is not completed")
    if len(captures) != 13:
        problems.append("expected 13 UI screenshots, found %d" % len(captures))
    if any(capture["sha256"] is None for capture in captures):
        problems.append("one or more UI screenshots are missing")
    if sorted(features) != sorted(["ScreenSpaceLightingRendererFeature",
                                   "GroundTruthAmbientOcclusionRendererFeature"]):
        problems.append("renderer feature list is not exactly SSL + GTAO: %s" % features)
    enabled = [component["type"] for component in components if component.get("enabled")]
    if sorted(enabled) != sorted(["ScreenSpaceGlobalIllumination",
                                  "ScreenSpaceReflection", "ScreenSpaceRefraction"]):
        problems.append("enabled volume overrides are not exactly SSGI/SSR/Refraction: %s" % enabled)
    if not report.get("cameraVolumeMask"):
        problems.append("camera volume mask is empty")
    if report.get("cameraVolumeMask") != 1:
        problems.append("camera volume mask does not target the Default layer")
    return {
        "passed": not problems,
        "problems": problems,
        "status": report.get("status"),
        "captures": captures,
        "rendererFeatures": features,
        "volumeComponents": components,
        "cameraName": report.get("cameraName"),
        "cameraVolumeMask": report.get("cameraVolumeMask"),
        "cameraVolumeMaskLayers": report.get("cameraVolumeMaskLayers"),
        "globalVolumeProfile": report.get("globalVolumeProfile"),
    }


def verification():
    report = load(HERE / "T3Verification.json")
    problems = []
    if not report.get("completed"):
        problems.append("verification did not complete")
    states = {}
    for mode in report.get("modes", []):
        states[mode["name"]] = mode
        if not mode.get("passed"):
            problems.append("%s: %s" % (mode["name"], mode.get("issues")))
        if mode.get("featureCount") != 2:
            problems.append("%s: expected two renderer features" % mode["name"])
        state_map = {state["state"]: state for state in mode.get("states", [])}
        required = ("T1Baseline", "FeaturesOn_VolumeOff", "FeaturesOff_VolumeOn",
                    "FeaturesOn_VolumeOn", "GtaoOnly", "SslOnly")
        for name in required:
            state = state_map.get(name)
            if state is None:
                problems.append("%s: missing state %s" % (mode["name"], name))
                continue
            if name != "T1Baseline":
                if not state.get("identicalToReference") or state.get("differingPixels", 0) != 0:
                    problems.append("%s/%s: neutral output differs from the T1 baseline" %
                                    (mode["name"], name))
                if state.get("magentaPixels", 0) != 0:
                    problems.append("%s/%s: error-shader magenta present" % (mode["name"], name))
        off = state_map.get("FeaturesOff_VolumeOn", {})
        on = state_map.get("FeaturesOn_VolumeOn", {})
        if off.get("gtaoExecutions") or off.get("sslExecutions"):
            problems.append("%s: a pass executed while both features were disabled" % mode["name"])
        if on.get("gtaoExecutions", 0) <= 0 or on.get("sslExecutions", 0) <= 0:
            problems.append("%s: volume-enabled state did not enqueue both passes" % mode["name"])
        gtao_only = state_map.get("GtaoOnly", {})
        if gtao_only.get("sslExecutions"):
            problems.append("%s: SSL executed while its feature was disabled" % mode["name"])
        ssl_only = state_map.get("SslOnly", {})
        if ssl_only.get("gtaoExecutions"):
            problems.append("%s: GTAO executed while its feature was disabled" % mode["name"])
        if (mode.get("gtaoHistoryEntries"), mode.get("sslHistoryEntries")) != (1, 1):
            problems.append("%s: main camera history counts are not (1, 1)" % mode["name"])
        if (mode.get("gtaoHistoryEntriesWithTempCamera"),
                mode.get("sslHistoryEntriesWithTempCamera")) != (2, 2):
            problems.append("%s: temporary camera did not add history entries" % mode["name"])
        if (mode.get("gtaoHistoryEntriesAfterCameraDestroy"),
                mode.get("sslHistoryEntriesAfterCameraDestroy")) != (1, 1):
            problems.append("%s: destroyed camera history was not pruned" % mode["name"])
    for mode in MODES:
        if mode not in states:
            problems.append("missing pipeline path %s" % mode)
    return {"passed": not problems, "problems": problems, "modes": states}


def t1_regression():
    reference = load(PROJECT / "PALN" / "validation" / "T1" / "T1Report.json")
    baseline = {mode["name"]: mode for mode in
                load(PROJECT / "PALN" / "validation" / "T1" / "FrameDebugger" /
                     "FrameDebuggerReport.json")["modes"]}
    problems = []
    if not reference.get("t1ExitCriteriaMet"):
        problems.append("T1 exit criteria are not met")
    runs = reference.get("baselineRuns", {})
    if sorted(runs) != ["Run17", "Run18"] or not all(run.get("completed") for run in runs.values()):
        problems.append("T1 report does not point at completed Run17/Run18")
    hashes = {}
    for mode in MODES:
        path = HERE / "FrameDebuggerNoPlugin" / ("%s.json" % mode)
        report = load(path)
        hashes[mode] = {
            "eventCount": report.get("eventCount"),
            "eventDataHash": report.get("eventDataHash"),
            "baselineEventDataHash": baseline.get(mode, {}).get("eventDataHash"),
        }
        if report.get("eventDataHash") != baseline.get(mode, {}).get("eventDataHash"):
            problems.append("%s: no-plugin events differ from the T1 baseline" % mode)
    return {"passed": not problems, "problems": problems, "eventHashes": hashes}


def edit_mode_tests():
    log = (HERE / "UnityTests.log").read_text(encoding="utf-8", errors="replace")
    summary = None
    for line in log.splitlines():
        if "EditMode tests finished:" in line:
            summary = line.strip()
    passed = summary is not None and "status=Passed" in summary and "failed=0" in summary
    return {"passed": passed, "summary": summary}


def meta_completeness():
    root = PROJECT / "Assets" / "Tests" / "ScreenSpaceLighting"
    extensions = (".cs", ".asset", ".unity", ".mat")
    missing = []
    for path in root.rglob("*"):
        if path.is_file() and path.suffix.lower() in extensions:
            if not path.with_name(path.name + ".meta").is_file():
                missing.append(str(path.relative_to(PROJECT)).replace("\\", "/"))
    return {"passed": not missing, "missing": missing}


def main():
    sections = {
        "uiPath": ui_evidence(),
        "verification": verification(),
        "t1Regression": t1_regression(),
        "editModeTests": edit_mode_tests(),
        "metaCompleteness": meta_completeness(),
    }
    sections["t3ExitCriteriaMet"] = all(section["passed"] for section in sections.values())
    sections["utcTimestamp"] = datetime.datetime.utcnow().isoformat() + "Z"
    sections["unityVersion"] = "2022.3.62f1"

    output = HERE / "T3Report.json"
    with open(output, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(sections, handle, indent=4)
        handle.write("\n")

    for name in ("uiPath", "verification", "t1Regression", "editModeTests", "metaCompleteness"):
        state = "pass" if sections[name]["passed"] else "FAIL"
        print("%-18s %s" % (name, state))
    print("t3ExitCriteriaMet  %s" % sections["t3ExitCriteriaMet"])
    return 0 if sections["t3ExitCriteriaMet"] else 1


if __name__ == "__main__":
    sys.exit(main())
