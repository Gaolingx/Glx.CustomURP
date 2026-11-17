#!/usr/bin/env python3
"""Builds the T12 exit report from the combination matrix, Frame Debugger and EditMode logs."""

import hashlib
import json
import os
import re
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(ROOT, "..", "..", "..", "..", ".."))
URP = "Packages/com.unity.render-pipelines.universal@14.0.12"

REQUIRED_ASSETS = [
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT12Verification.cs",
    "Assets/Tests/ScreenSpaceLighting/Renderers/SslT12ForwardRenderer.asset",
    "Assets/Tests/ScreenSpaceLighting/Renderers/SslT12ForwardPlusRenderer.asset",
    "Assets/Tests/ScreenSpaceLighting/Renderers/SslT12DeferredRenderer.asset",
    "Assets/Tests/ScreenSpaceLighting/Renderers/SslT12ForwardPipeline.asset",
    "Assets/Tests/ScreenSpaceLighting/Renderers/SslT12ForwardPlusPipeline.asset",
    "Assets/Tests/ScreenSpaceLighting/Renderers/SslT12DeferredPipeline.asset",
    URP + "/Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingT12SchedulingTests.cs",
]

MODE_NAMES = ["Forward", "ForwardPlus", "Deferred"]

REQUIRED_CASES = [
    "Reference",
    "AllOn",
    "AllOffAgain",
    "SsgiOnly",
    "SsrOnly",
    "RefractionOnly",
    "GtaoOnly",
    "SsgiSsr",
    "GtaoAndSsl",
    "RuntimeToggleOff",
    "GtaoFirst",
    "NoVolumeComponents",
    "OverrideStateOff",
    "TwoVolumes",
    "CameraVolumeMask",
    "PostProcessingOn",
    "RenderScale75",
    "TargetResize",
    "SameFrameDoubleRender",
    "CameraCut",
    "AlgorithmSwitch",
    "DepthPrimingForced",
    "OpaqueTexture",
    "TemporalAA",
    "MotionBlur",
    "DecalDBuffer",
    "DecalScreenSpace",
    "MSAA4x",
    "UnsupportedMaterial",
    "CameraStack",
    "PreviewCamera",
    "ReflectionProbeCamera",
    "GlobalKeywordCleanup",
]

REQUIRED_PNGS = [
    "Reference",
    "AllOn",
    "AllOffAgain",
    "GtaoFirst",
    "DepthPrimingForced",
    "OpaqueTexture",
    "TemporalAA",
    "MotionBlur",
    "DecalDBuffer",
    "DecalScreenSpace",
    "MSAA4x",
]


def load_json(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def case_map(mode):
    return {case["name"]: case for case in mode.get("cases", [])}


def parse_editmode(log_path):
    if not os.path.exists(log_path):
        return {"log": log_path, "present": False, "passed": False, "summary": None}
    with open(log_path, encoding="utf-8", errors="replace") as handle:
        text = handle.read()
    match = re.search(
        r"EditMode tests finished: status=(\w+) passed=(\d+) failed=(\d+) skipped=(\d+)",
        text,
    )
    summary = None
    if match:
        summary = {
            "status": match.group(1),
            "passed": int(match.group(2)),
            "failed": int(match.group(3)),
            "skipped": int(match.group(4)),
        }
    return {
        "log": log_path,
        "present": True,
        "passed": summary is not None and summary["status"] == "Passed" and summary["failed"] == 0 and summary["passed"] > 0,
        "summary": summary,
    }


def analyze_mode(name, mode):
    problems = []
    cases = case_map(mode)

    for required in REQUIRED_CASES:
        if required not in cases:
            problems.append(f"missing case {required}")

    if name == "Deferred" and "DeferredFallbackForward" not in cases:
        problems.append("missing case DeferredFallbackForward")
    if name == "Deferred" and not cases.get("DeferredFallbackForward", {}).get("passed", False):
        problems.append("the deferred forward fallback case failed")

    for case_name, case in cases.items():
        if not case.get("passed", False):
            problems.append(f"{case_name} failed")
        if case.get("nonFinitePixels", 0):
            problems.append(f"{case_name} has non-finite pixels")
        if case.get("magentaPixels", 0):
            problems.append(f"{case_name} has error-shader pixels")

    if mode.get("renderScaleResetCount", 0) <= 0:
        problems.append("render scale change did not reset the SSL history")
    if mode.get("targetResizeResetCount", 0) <= 0:
        problems.append("target resize did not reset the SSL history")
    if mode.get("cameraCutResetCount", 0) <= 0:
        problems.append("camera cut did not reset the SSL history")
    if mode.get("algorithmSwitchResetCount", 0) <= 0:
        problems.append("SSR algorithm switch did not reset the accumulation history")
    if not mode.get("depthPrimingChanged", False):
        problems.append("depth priming did not render the effects")
    if mode.get("unsupportedChangedPixels", 1) != 0:
        problems.append("an unsupported material changed with screen-space lighting")
    if not mode.get("forwardOnlySupported", False):
        problems.append("the forward-only Complex Lit object did not receive screen-space effects")
    if not mode.get("cameraStackPassed", False):
        problems.append("camera stack case failed")
    if not mode.get("previewCameraPassed", False):
        problems.append("preview camera case failed")
    if not mode.get("reflectionCameraPassed", False):
        problems.append("reflection probe camera case failed")
    if not mode.get("globalKeywordsClean", False):
        problems.append("global screen-space keywords leaked after camera cleanup")
    if mode.get("sslPrivateBytes", 0) <= 0 or mode.get("gtaoPrivateBytes", 0) <= 0:
        problems.append("private resource cost was not measured")
    if mode.get("sslDepthPyramidBytes", 0) <= 0 or mode.get("depthPyramidDispatches", 0) <= 0:
        problems.append("Hi-Z cost was not measured")
    if mode.get("transparentPrepassExecutions", 0) <= 0:
        problems.append("the transparent prepass did not run in the all-on case")
    return problems


def analyze_frame_debugger(path):
    problems = []
    if not os.path.exists(path):
        return ["Frame Debugger report is missing", None]
    report = load_json(path)
    if not report.get("completed"):
        problems.append("Frame Debugger capture did not complete")
    modes = {mode.get("name"): mode for mode in report.get("modes", [])}
    summary = {}
    for name in MODE_NAMES:
        mode = modes.get(name)
        if mode is None:
            problems.append(f"Frame Debugger mode {name} is missing")
            continue
        events = mode.get("eventNames", [])
        first_motion = next((i for i, event in enumerate(events) if "MotionVectors" in event), -1)
        first_ssl = next((i for i, event in enumerate(events) if "SSL Depth Pyramid" in event), -1)
        first_gtao = next((i for i, event in enumerate(events) if "GTAO Depth Pyramid" in event), -1)
        first_gbuffer = next((i for i, event in enumerate(events) if "GBuffer" in event or "DrawOpaqueObjects" in event), -1)
        summary[name] = {
            "eventCount": len(events),
            "firstMotion": first_motion,
            "firstSslDepthPyramid": first_ssl,
            "firstGtaoDepthPyramid": first_gtao,
            "firstMainRendering": first_gbuffer,
        }
        if first_ssl < 0 or first_gtao < 0 or first_gbuffer < 0:
            problems.append(f"{name} is missing SSL/GTAO depth pyramid or main rendering events")
            continue
        if first_motion < 0 or first_motion > first_ssl:
            problems.append(f"{name} does not schedule motion vectors before the SSL depth pyramid")
        if not (first_ssl < first_gbuffer and first_gtao < first_gbuffer):
            problems.append(f"{name} runs a depth pyramid after the G-buffer/opaque pass")
    return problems, summary


def main():
    verification_path = os.path.join(ROOT, "T12Verification.json")
    report_path = os.path.join(ROOT, "T12Report.json")
    editmode = parse_editmode(os.path.join(ROOT, "UnityTests.log"))
    verification = load_json(verification_path)
    modes = {mode.get("name"): mode for mode in verification.get("modes", [])}

    mode_problems = {name: analyze_mode(name, modes.get(name, {})) for name in MODE_NAMES}
    frame_problems, frame_summary = analyze_frame_debugger(os.path.join(ROOT, "FrameDebugger", "FrameDebuggerReport.json"))

    missing_meta = []
    assets = {}
    for relative in REQUIRED_ASSETS:
        absolute = os.path.join(PROJECT, relative)
        if not os.path.exists(absolute):
            missing_meta.append(f"missing asset: {relative}")
            continue
        assets[relative] = sha256(absolute)
        if not os.path.exists(absolute + ".meta"):
            missing_meta.append(f"missing meta: {relative}.meta")

    pngs = {}
    for mode in MODE_NAMES:
        missing = []
        for view in REQUIRED_PNGS:
            filename = f"T12_{mode}_{view}.png"
            if not os.path.exists(os.path.join(ROOT, filename)):
                missing.append(filename)
            else:
                pngs[filename] = sha256(os.path.join(ROOT, filename))
        if missing:
            mode_problems[mode].append("missing captures: " + ", ".join(missing))

    criteria = {
        "verificationCompleted": bool(verification.get("completed")),
        "threePathsPassed": all(not mode_problems[name] for name in MODE_NAMES),
        "rendererAndModuleMatrixComplete": all(
            not any(p.startswith("missing case") for p in mode_problems[name]) for name in MODE_NAMES
        ),
        "featureOrderStable": all(
            case_map(modes.get(name, {})).get("GtaoFirst", {}).get("identicalToReference", False)
            for name in MODE_NAMES
        ),
        "volumeCasesPassed": all(
            not any("NoVolumeComponents" in p or "OverrideStateOff" in p or "TwoVolumes" in p or "CameraVolumeMask" in p for p in mode_problems[name])
            for name in MODE_NAMES
        ),
        "resolutionAndHistoryLifecycle": all(
            modes.get(name, {}).get("renderScaleResetCount", 0) > 0
            and modes.get(name, {}).get("targetResizeResetCount", 0) > 0
            and modes.get(name, {}).get("cameraCutResetCount", 0) > 0
            and modes.get(name, {}).get("algorithmSwitchResetCount", 0) > 0
            for name in MODE_NAMES
        ),
        "urpCombinationsPassed": all(
            not any(
                p.startswith("DepthPrimingForced") or p.startswith("OpaqueTexture") or p.startswith("TemporalAA")
                or p.startswith("MotionBlur") or p.startswith("Decal") or p.startswith("MSAA4x")
                for p in mode_problems[name]
            )
            for name in MODE_NAMES
        ),
        "cameraTypesPassed": all(
            modes.get(name, {}).get("cameraStackPassed", False)
            and modes.get(name, {}).get("previewCameraPassed", False)
            and modes.get(name, {}).get("reflectionCameraPassed", False)
            for name in MODE_NAMES
        ),
        "globalStateClean": all(modes.get(name, {}).get("globalKeywordsClean", False) for name in MODE_NAMES),
        "costQuantified": all(
            modes.get(name, {}).get("sslPrivateBytes", 0) > 0
            and modes.get(name, {}).get("gtaoPrivateBytes", 0) > 0
            and modes.get(name, {}).get("sslDepthPyramidBytes", 0) > 0
            and modes.get(name, {}).get("transparentPrepassExecutions", 0) > 0
            for name in MODE_NAMES
        ),
        "preGbufferSchedule": not frame_problems,
        "deferredFallbackForwardCovered": "DeferredFallbackForward" in case_map(modes.get("Deferred", {})),
        "requiredAssetsHaveMeta": not missing_meta,
        "editModeTestsPassed": bool(editmode.get("passed")),
    }

    report = {
        "utcTimestamp": datetime.now(timezone.utc).isoformat(),
        "unityVersion": verification.get("unityVersion"),
        "graphicsDeviceName": verification.get("graphicsDeviceName"),
        "graphicsDeviceType": verification.get("graphicsDeviceType"),
        "verification": {
            "completed": verification.get("completed"),
            "passed": verification.get("passed"),
            "error": verification.get("error"),
            "modes": {
                name: {
                    "passed": not mode_problems[name],
                    "issues": mode_problems[name],
                    "cases": case_map(modes.get(name, {})),
                    "renderScaleResetCount": modes.get(name, {}).get("renderScaleResetCount"),
                    "targetResizeResetCount": modes.get(name, {}).get("targetResizeResetCount"),
                    "cameraCutResetCount": modes.get(name, {}).get("cameraCutResetCount"),
                    "algorithmSwitchResetCount": modes.get(name, {}).get("algorithmSwitchResetCount"),
                    "depthPrimingChanged": modes.get(name, {}).get("depthPrimingChanged"),
                    "msaaChangedFromAllOn": modes.get(name, {}).get("msaaChangedFromAllOn"),
                    "unsupportedChangedPixels": modes.get(name, {}).get("unsupportedChangedPixels"),
                    "forwardOnlyChangedPixels": modes.get(name, {}).get("forwardOnlyChangedPixels"),
                    "cameraStackPassed": modes.get(name, {}).get("cameraStackPassed"),
                    "cameraStackChangedPixels": modes.get(name, {}).get("cameraStackChangedPixels"),
                    "previewCameraPassed": modes.get(name, {}).get("previewCameraPassed"),
                    "reflectionCameraPassed": modes.get(name, {}).get("reflectionCameraPassed"),
                    "globalKeywordsClean": modes.get(name, {}).get("globalKeywordsClean"),
                    "sslPrivateBytes": modes.get(name, {}).get("sslPrivateBytes"),
                    "gtaoPrivateBytes": modes.get(name, {}).get("gtaoPrivateBytes"),
                    "sslDepthPyramidBytes": modes.get(name, {}).get("sslDepthPyramidBytes"),
                    "transparentPrepassExecutions": modes.get(name, {}).get("transparentPrepassExecutions"),
                    "depthPyramidDispatches": modes.get(name, {}).get("depthPyramidDispatches"),
                    "sslColorPyramidMips": modes.get(name, {}).get("sslColorPyramidMips"),
                    "sslDepthPyramidMips": modes.get(name, {}).get("sslDepthPyramidMips"),
                }
                for name in MODE_NAMES
            },
        },
        "frameDebugger": {
            "problems": frame_problems,
            "modes": frame_summary,
        },
        "criteria": criteria,
        "t12ExitCriteriaMet": all(criteria.values()),
        "assets": assets,
        "missingMeta": missing_meta,
        "editMode": editmode,
        "captures": pngs,
    }

    with open(report_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)

    print(json.dumps({
        "t12ExitCriteriaMet": report["t12ExitCriteriaMet"],
        "criteria": criteria,
        "modeProblems": mode_problems,
        "frameProblems": frame_problems,
    }, indent=2))
    if not report["t12ExitCriteriaMet"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()