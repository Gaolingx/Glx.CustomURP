#!/usr/bin/env python3
"""Builds the T8 exit report from the Unity verification and EditMode log."""

import hashlib
import json
import os
import re
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(ROOT, "..", "..", ".."))
URP = "Packages/com.unity.render-pipelines.universal@14.0.12"

SSL_RUNTIME = URP + "/Runtime/RendererFeatures/ScreenSpaceLighting"
SSL_SHADERS = URP + "/Shaders/Utils/ScreenSpaceLighting"
COMMON_SHADERS = URP + "/Shaders/Utils/ScreenSpaceCommon"
CORE_SHADERS = URP + "/ShaderLibrary/ScreenSpaceLighting"

REQUIRED_ASSETS = [
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT8Verification.cs",
    SSL_RUNTIME + "/Passes/SsrPass.cs",
    SSL_RUNTIME + "/Passes/ScreenSpaceLightingSetupPass.cs",
    SSL_RUNTIME + "/Passes/ScreenSpaceLightingDebugPass.cs",
    SSL_RUNTIME + "/ScreenSpaceLightingShaderVariables.cs",
    SSL_RUNTIME + "/ScreenSpaceLightingShaderVariables.cs.hlsl",
    SSL_RUNTIME + "/ScreenSpaceLightingShaderIDs.cs",
    SSL_RUNTIME + "/ScreenSpaceLightingResources.cs",
    SSL_RUNTIME + "/ScreenSpaceLightingHistory.cs",
    SSL_RUNTIME + "/ScreenSpaceLightingDebugMode.cs",
    SSL_RUNTIME + "/ScreenSpaceReflection.cs",
    SSL_SHADERS + "/ScreenSpaceLightingCommon.hlsl",
    SSL_SHADERS + "/RayMarching.hlsl",
    SSL_SHADERS + "/ScreenSpaceReflections.compute",
    COMMON_SHADERS + "/DepthPyramid.compute",
    CORE_SHADERS + "/ScreenSpaceLightingInput.hlsl",
    CORE_SHADERS + "/ScreenSpaceLightingBlend.hlsl",
    URP + "/ShaderLibrary/GlobalIllumination.hlsl",
    URP + "/Shaders/Lit.shader",
    URP + "/Shaders/ComplexLit.shader",
    URP + "/Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingSsrTests.cs",
    URP + "/Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingSsrPbrTests.cs",
]

MODE_NAMES = ["Forward", "ForwardPlus", "Deferred"]
MODE_PNGS = [
    "Reference",
    "Approximation",
    "PbrDefault",
    "MovingTargetOnly",
    "MovingRejectAll",
    "DebugSpeedRejectAll",
    "DebugSpeedNoReject",
]

EXPECTED_COMBINATIONS = {
    "NoWorld_Both": "ScreenSpaceReflectionsAccumulateNoWorldSpeedRejectionBoth",
    "NoWorld_SurfaceOnly": "ScreenSpaceReflectionsAccumulateNoWorldSpeedRejectionSourceOnly",
    "NoWorld_TargetOnly": "ScreenSpaceReflectionsAccumulateNoWorldSpeedRejectionTargetOnly",
    "Hard_Both": "ScreenSpaceReflectionsAccumulateHardThresholdSpeedRejectionBoth",
    "Hard_SurfaceOnly": "ScreenSpaceReflectionsAccumulateHardThresholdSpeedRejectionSourceOnly",
    "Hard_TargetOnly": "ScreenSpaceReflectionsAccumulateHardThresholdSpeedRejectionTargetOnly",
    "Smooth_Both": "ScreenSpaceReflectionsAccumulateSmoothSpeedRejectionBoth",
    "Smooth_SurfaceOnly": "ScreenSpaceReflectionsAccumulateSmoothSpeedRejectionSourceOnly",
    "Smooth_TargetOnly": "ScreenSpaceReflectionsAccumulateSmoothSpeedRejectionTargetOnly",
}


def load_json(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def state_map(mode):
    return {state["state"]: state for state in mode.get("states", [])}


def parse_editmode(log_path):
    if not os.path.exists(log_path):
        return {"log": log_path, "present": False, "passed": False, "summary": None}
    with open(log_path, encoding="utf-8", errors="replace") as handle:
        text = handle.read()
    match = re.search(r"EditMode tests finished: status=(\w+) passed=(\d+) failed=(\d+) skipped=(\d+)", text)
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


def combo_is_exclusive(combo, expected_kernel):
    counts = {entry["kernel"]: entry["count"] for entry in combo.get("kernelCounts", [])}
    if not counts:
        return False
    if any(value <= 0 for value in counts.values()):
        return False
    return list(counts.keys()) == [expected_kernel] and counts[expected_kernel] >= 3


def analyze_mode(mode):
    problems = []
    states = state_map(mode)

    def require(name):
        state = states.get(name)
        if state is None:
            problems.append(f"{name} capture is missing")
        return state

    if mode.get("approxExecutions", 0) <= 0:
        problems.append("the Approximation pass never executed")
    if mode.get("approxAccumulations", 0) != 0:
        problems.append("Approximation dispatched an accumulation kernel")
    if mode.get("approxUsingPbr", True):
        problems.append("Approximation reported the PBR output path")

    for key in ("pbrTraceDispatches", "pbrReprojectDispatches", "pbrAccumulationDispatches"):
        if mode.get(key, 0) <= 0:
            problems.append(f"{key} is zero")
    if not mode.get("pbrUsingPbr", False):
        problems.append("PBR reported the Approximation output path")

    default_counts = mode.get("pbrDefaultKernelCounts", [])
    default_kernel = "ScreenSpaceReflectionsAccumulateNoWorldSpeedRejectionBoth"
    if default_counts != [{"kernel": default_kernel, "count": 14}]:
        problems.append(f"the default PBR kernel counts are {default_counts}")

    combos = {combo["name"]: combo for combo in mode.get("combinations", [])}
    for name, kernel in EXPECTED_COMBINATIONS.items():
        combo = combos.get(name)
        if combo is None:
            problems.append(f"{name} combination is missing")
            continue
        if combo.get("lastKernel") != kernel:
            problems.append(f"{name} selected {combo.get('lastKernel')}, expected {kernel}")
        if not combo_is_exclusive(combo, kernel):
            problems.append(f"{name} did not dispatch only its own kernel: {combo.get('kernelCounts')}")
        weight = combo.get("weight", {})
        if weight.get("nonFinitePixels", 0) > 0 or weight.get("negativePixels", 0) > 0:
            problems.append(f"{name} produced invalid accumulation output")

    if mode.get("surfaceVsTargetPixels", 0) < 1280 * 720 / 1000:
        problems.append("SurfaceOnly and TargetOnly do not change the image on a moving scene")
    if mode.get("rejectionPixels", 0) < 1280 * 720 / 1000:
        problems.append("the speed rejection parameter does not change the image")

    if mode.get("debugRejectColoredPixels", 0) < 100 or mode.get("debugRejectRedFraction", 0.0) < 0.5:
        problems.append("the hard speed rejection debug view is not predominantly red")
    if mode.get("debugAcceptColoredPixels", 0) < 100 or mode.get("debugAcceptGreenFraction", 0.0) < 0.5:
        problems.append("the accepted speed debug view is not predominantly green")

    if mode.get("algorithmSwitchResets", 0) < 2:
        problems.append("the algorithm switches did not reset the accumulation history twice")

    if mode.get("missPixelsCompared", 0) <= 0:
        problems.append("no zero weight pixel was compared with the probe image")
    if mode.get("missPixelsDifferent", 0) != 0:
        problems.append(f"{mode.get('missPixelsDifferent')} zero weight pixels differ from the probe image")

    for name in ("Approximation", "PbrDefault"):
        state = require(name)
        if state is not None:
            if name == "PbrDefault" and not state.get("differsFromReference", False):
                problems.append("PbrDefault does not change the image")
            if state.get("magentaPixels", 0) or state.get("nonFinitePixels", 0):
                problems.append(f"{name} contains invalid pixels")

    for name in ("PbrFirstFrameAfterReset", "PbrZeroIntensityRestoresBaseline"):
        state = require(name)
        if state is not None and not state.get("identicalToReference", False):
            problems.append(f"{name} differs from the probe image by {state.get('differingPixels')} pixels")

    cut = require("CameraCut")
    if cut is not None and (cut.get("magentaPixels", 0) or cut.get("nonFinitePixels", 0)):
        problems.append("CameraCut contains invalid pixels")
    if mode.get("cameraCutResets", 0) <= 0:
        problems.append("the camera cut did not invalidate the history")

    warm = {snapshot["label"]: snapshot for snapshot in mode.get("historyChecks", [])}.get("AfterPbrWarmup")
    if warm is None:
        problems.append("AfterPbrWarmup history snapshot is missing")
    else:
        if not warm.get("ssrPbrResourcesAllocated") or not warm.get("ssrPbrHasValidHistory"):
            problems.append("the PBR resources or history never became valid")
        if warm.get("ssrPbrWidth") != 1280 or warm.get("ssrPbrHeight") != 720:
            problems.append("the PBR history size is wrong")
        if warm.get("ssrPbrReadSlot") == warm.get("ssrPbrWriteSlot") and warm.get("ssrPbrHasValidHistory"):
            problems.append("PBR reads and writes the same history slot")

    diag = mode.get("diagPbrParams", {})
    if not (0.0 < diag.get("x", 0.0) < 1.0 and abs(diag.get("y", 0.0) - 0.5) < 1e-3 and
            abs(diag.get("z", 0.0) - 0.5) < 1e-3 and diag.get("w", 0.0) == 1.0):
        problems.append(f"the PBR constant buffer readback is unexpected: {diag}")

    return problems


def main():
    verification_path = os.path.join(ROOT, "T8Verification.json")
    report_path = os.path.join(ROOT, "T8Report.json")
    if not os.path.exists(verification_path):
        raise SystemExit(f"missing {verification_path}")

    verification = load_json(verification_path)
    modes = {mode["name"]: mode for mode in verification.get("modes", [])}
    mode_problems = {}
    for name in MODE_NAMES:
        mode = modes.get(name)
        if mode is None:
            mode_problems[name] = ["mode capture is missing"]
            continue
        mode_problems[name] = analyze_mode(mode)

    assets = []
    missing_meta = []
    for path in REQUIRED_ASSETS:
        meta = os.path.join(PROJECT, path + ".meta")
        exists = os.path.exists(os.path.join(PROJECT, path))
        has_meta = os.path.exists(meta)
        assets.append({"path": path, "exists": exists, "meta": has_meta})
        if not exists or not has_meta:
            missing_meta.append(path)

    editmode = parse_editmode(os.path.join(ROOT, "UnityTests.log"))

    pngs = {}
    for mode in MODE_NAMES:
        missing = []
        for view in MODE_PNGS:
            name = f"T8_{mode}_{view}.png"
            if not os.path.exists(os.path.join(ROOT, name)):
                missing.append(name)
            else:
                pngs[name] = sha256(os.path.join(ROOT, name))
        if missing:
            mode_problems[mode].append("missing captures: " + ", ".join(missing))

    criteria = {
        "verificationCompleted": bool(verification.get("completed")),
        "threePathsPassed": all(not mode_problems[name] for name in MODE_NAMES),
        "approximationDoesNotAccumulate": all(
            modes.get(name, {}).get("approxAccumulations", 1) == 0
            and not modes.get(name, {}).get("approxUsingPbr", True)
            for name in MODE_NAMES
        ),
        "pbrDispatchesOccur": all(
            modes.get(name, {}).get("pbrTraceDispatches", 0) > 0
            and modes.get(name, {}).get("pbrReprojectDispatches", 0) > 0
            and modes.get(name, {}).get("pbrAccumulationDispatches", 0) > 0
            for name in MODE_NAMES
        ),
        "nineCombinationsSelectTheirKernel": all(
            all(combo_is_exclusive(combo, kernel) for name, kernel in EXPECTED_COMBINATIONS.items()
                for combo in [next((c for c in modes.get(mode, {}).get("combinations", []) if c["name"] == name), None)]
                if combo is not None)
            and all(
                len(modes.get(mode, {}).get("combinations", [])) == 9
                for mode in MODE_NAMES
            )
            for mode in MODE_NAMES
        ),
        "motionSourceSettingHasEffect": all(
            modes.get(name, {}).get("surfaceVsTargetPixels", 0) >= 1280 * 720 / 1000 for name in MODE_NAMES
        ),
        "speedRejectionHasEffect": all(
            modes.get(name, {}).get("rejectionPixels", 0) >= 1280 * 720 / 1000 for name in MODE_NAMES
        ),
        "speedDebugView": all(
            modes.get(name, {}).get("debugRejectRedFraction", 0.0) >= 0.5
            and modes.get(name, {}).get("debugAcceptGreenFraction", 0.0) >= 0.5
            for name in MODE_NAMES
        ),
        "algorithmSwitchResetsHistory": all(
            modes.get(name, {}).get("algorithmSwitchResets", 0) >= 2 for name in MODE_NAMES
        ),
        "missKeepsReflectionProbe": all(
            modes.get(name, {}).get("missPixelsDifferent", 1) == 0 for name in MODE_NAMES
        ),
        "firstFrameFallback": all(
            state_map(modes.get(name, {})).get("PbrFirstFrameAfterReset", {}).get("identicalToReference", False)
            for name in MODE_NAMES
        ),
        "zeroIntensityRestoresBaseline": all(
            state_map(modes.get(name, {})).get("PbrZeroIntensityRestoresBaseline", {}).get("identicalToReference", False)
            for name in MODE_NAMES
        ),
        "noInvalidPixels": all(
            modes.get(name, {}).get("weight", {}).get("nonFinitePixels", 1) == 0
            and modes.get(name, {}).get("weight", {}).get("negativePixels", 1) == 0
            for name in MODE_NAMES
        ),
        "constantBufferRoundTrip": all(
            modes.get(name, {}).get("diagPbrParams", {}).get("w", 0.0) == 1.0
            and 0.0 < modes.get(name, {}).get("diagPbrParams", {}).get("x", 0.0) < 1.0
            for name in MODE_NAMES
        ),
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
                    "state": state_map(modes.get(name, {})),
                    "dispatches": {
                        key: modes.get(name, {}).get(key)
                        for key in (
                            "approxExecutions",
                            "approxAccumulations",
                            "approxUsingPbr",
                            "pbrExecutions",
                            "pbrTraceDispatches",
                            "pbrReprojectDispatches",
                            "pbrAccumulationDispatches",
                            "pbrClearDispatches",
                            "pbrUsingPbr",
                            "algorithmSwitchResets",
                            "cameraCutResets",
                            "missPixelsCompared",
                            "missPixelsDifferent",
                            "surfaceVsTargetPixels",
                            "surfaceVsTargetMaxDelta",
                            "rejectionPixels",
                            "rejectionMaxDelta",
                        )
                    },
                    "kernelSelection": modes.get(name, {}).get("combinations"),
                    "debugSpeed": {
                        "rejectRed": modes.get(name, {}).get("debugRejectRedFraction"),
                        "rejectGreen": modes.get(name, {}).get("debugRejectGreenFraction"),
                        "rejectColored": modes.get(name, {}).get("debugRejectColoredPixels"),
                        "acceptRed": modes.get(name, {}).get("debugAcceptRedFraction"),
                        "acceptGreen": modes.get(name, {}).get("debugAcceptGreenFraction"),
                        "acceptColored": modes.get(name, {}).get("debugAcceptColoredPixels"),
                    },
                    "constantBufferDiagnostic": modes.get(name, {}).get("diagPbrParams"),
                    "weight": modes.get(name, {}).get("weight"),
                    "historyChecks": modes.get(name, {}).get("historyChecks"),
                }
                for name in MODE_NAMES
            },
        },
        "designNotes": {
            "accumulationContract": "PBR accumulates un-premultiplied radiance and weight with the "
            "same coefficient; the material blend is identical to T7 (lerp before EnvironmentBRDF). "
            "HDRP accumulates premultiplied radiance.",
            "algorithmSelection": "usedAlgorithm selects Approximation or PBRAccumulation; nine "
            "accumulate kernels cover world/screen speed and surface/hit/both motion sources. "
            "Disabling both motion sources falls back to Both instead of the HDRP #error path.",
            "algorithmSwitch": "The last algorithm is stored per camera history; switching either "
            "way resets the PBR accumulation and bumps ssrPbrResetCount.",
            "worldSpeedRejection": "The world-speed reconstruction is ported verbatim from HDRP. Its "
            "baseline saturates the normalized speed on a static camera, so the behavioral "
            "accept/reject test uses the screen-space kernels; the world kernels are verified by "
            "kernel selection and the hard rejection debug view.",
            "debugView": "SsrAccumulationSpeed colorizes the rejection decision (hard red/green or "
            "smooth hue). SsrPbrParams is a diagnostic view of the constant buffer.",
            "deferredSmoothness": "SSR runs before the GBuffer overwrites GBuffer2, so all paths "
            "read the 0.5 + 0.5 * smoothness encoding from the depth-normals prepass.",
        },
        "editModeTests": editmode,
        "requiredAssets": assets,
        "missingMeta": missing_meta,
        "captures": pngs,
        "criteria": criteria,
    }

    report["t8ExitCriteriaMet"] = all(criteria.values())
    with open(report_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)

    print(json.dumps({key: report[key] for key in ("criteria", "t8ExitCriteriaMet")}, indent=2))
    raise SystemExit(0 if report["t8ExitCriteriaMet"] else 1)


if __name__ == "__main__":
    main()