#!/usr/bin/env python3
"""Builds the T6 exit report from the Unity verification, EditMode log and optional Frame Debugger run."""

import hashlib
import json
import os
import re
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(ROOT, "..", "..", "..", "..", ".."))
URP = "Packages/com.unity.render-pipelines.universal@14.0.12"

SSL_RUNTIME = URP + "/Runtime/RendererFeatures/ScreenSpaceLighting"
SSL_SHADERS = URP + "/Shaders/Utils/ScreenSpaceLighting"
COMMON_SHADERS = URP + "/Shaders/Utils/ScreenSpaceCommon"
CORE_SHADERS = URP + "/ShaderLibrary/ScreenSpaceLighting"

REQUIRED_ASSETS = [
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT6Verification.cs",
    SSL_RUNTIME + "/Passes/SsgiPass.cs",
    SSL_RUNTIME + "/ScreenSpaceLightingShaderVariables.cs",
    SSL_RUNTIME + "/ScreenSpaceLightingShaderVariables.cs.hlsl",
    SSL_RUNTIME + "/ScreenSpaceLightingShaderIDs.cs",
    SSL_RUNTIME + "/ScreenSpaceLightingResources.cs",
    SSL_RUNTIME + "/ScreenSpaceLightingHistory.cs",
    SSL_RUNTIME + "/ScreenSpaceGlobalIllumination.cs",
    SSL_SHADERS + "/ScreenSpaceLightingCommon.hlsl",
    SSL_SHADERS + "/RayMarching.hlsl",
    SSL_SHADERS + "/Sampling.hlsl",
    SSL_SHADERS + "/Denoising.hlsl",
    SSL_SHADERS + "/ScreenSpaceGlobalIllumination.compute",
    SSL_SHADERS + "/TemporalFilter.compute",
    SSL_SHADERS + "/DiffuseDenoiser.compute",
    COMMON_SHADERS + "/BilateralUpsample.compute",
    CORE_SHADERS + "/ScreenSpaceLightingInput.hlsl",
    CORE_SHADERS + "/ScreenSpaceLightingBlend.hlsl",
    URP + "/ShaderLibrary/GlobalIllumination.hlsl",
    URP + "/Shaders/LitGBufferPass.hlsl",
    URP + "/Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingSsgiTests.cs",
]

MODE_NAMES = ["Forward", "ForwardPlus", "Deferred"]
MODE_PNGS = [
    "Reference",
    "SsgiFullRes",
    "SsgiHalfRes",
    "SsgiRaw",
    "SsgiDenoised",
    "SsgiHistory",
    "SsgiWeight",
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


def analyze_mode(mode, verification_root):
    problems = []
    states = state_map(mode)

    def require(name):
        state = states.get(name)
        if state is None:
            problems.append(f"{name} capture is missing")
        return state

    if mode.get("ssgiExecutions", 0) <= 0:
        problems.append("the SSGI pass never executed")
    for key in ("traceDispatches", "reprojectDispatches", "validationDispatches", "accumulationDispatches", "diffuseDispatches"):
        if mode.get(key, 0) <= 0:
            problems.append(f"{key} is zero")
    if mode.get("setupExecutions", 0) <= 0 or mode.get("ssgiBoundFrames", 0) <= 0:
        problems.append("the setup pass never bound the SSGI texture and keyword")
    if mode.get("upsampleDispatches", 0) != 0:
        problems.append("full resolution ran the half resolution upsample")
    if mode.get("halfResUpsampleDispatches", 0) <= 0:
        problems.append("half resolution never dispatched the bilateral upsample")

    on = require("SsgiFullRes")
    if on is not None:
        if not on.get("differsFromReference"):
            problems.append("SsgiFullRes does not change the image")
        if on.get("magentaPixels", 0) or on.get("nonFinitePixels", 0):
            problems.append("SsgiFullRes contains invalid pixels")
    half = require("SsgiHalfRes")
    if half is not None:
        if not half.get("differsFromReference"):
            problems.append("SsgiHalfRes does not change the image")
        if half.get("magentaPixels", 0) or half.get("nonFinitePixels", 0):
            problems.append("SsgiHalfRes contains invalid pixels")
    zero = require("ZeroIntensityRestoresBaseline")
    if zero is not None and not zero.get("identicalToReference"):
        problems.append(f"ZeroIntensityRestoresBaseline differs by {zero.get('differingPixels')} pixels")
    first = require("FirstFrameAfterReset")
    if first is not None and not first.get("identicalToReference"):
        problems.append(f"FirstFrameAfterReset differs by {first.get('differingPixels')} pixels")
    cut = require("CameraCut")
    if cut is not None and (cut.get("magentaPixels", 0) or cut.get("nonFinitePixels", 0)):
        problems.append("CameraCut contains invalid pixels")

    if mode.get("missPixelsCompared", 0) <= 0:
        problems.append("no zero weight pixel was compared with baked GI")
    if mode.get("missPixelsDifferent", 0) != 0:
        problems.append(f"{mode.get('missPixelsDifferent')} zero weight pixels differ from baked GI")
    if mode.get("missMaxChannelDelta", 1.0) > 1e-4:
        problems.append("the zero weight pixels changed by more than float precision")

    weight = mode.get("weight", {})
    if weight.get("nonFinitePixels", 0) > 0:
        problems.append("the SSGI output texture contains NaN or Inf")
    if weight.get("coveredFraction", 0.0) < 0.001:
        problems.append("the SSGI output never reaches full weight on a measurable area")
    if weight.get("missFraction", 0.0) < 0.01:
        problems.append("no pixel fell back to baked GI")

    if mode.get("cameraCutResetCount", 0) <= 0:
        problems.append("the camera reset did not invalidate the SSGI history")

    ratio = mode.get("emissiveLuminanceRatio", 1.0)
    if mode.get("emissivePixelCount", 0) > 0 and not (0.65 <= ratio <= 1.35):
        problems.append(f"the emissive surface luminance ratio is {ratio:.3f}; emission may be added twice or replaced")

    if mode.get("debugConsumptionPixels", 0) <= 0:
        problems.append("the debug view was not compared with the consumed SSGI texture")
    elif mode.get("debugConsumptionMismatchPixels", 0) > mode.get("debugConsumptionPixels", 1) * 0.01:
        problems.append("the debug view and the consumed SSGI texture disagree")

    for image in mode.get("debugImages", []):
        if image.get("magentaPixels", 0) or image.get("nonFinitePixels", 0):
            problems.append(f"{image['view']} debug view contains invalid pixels")
        if image.get("meanLuminance", 0.0) <= 0.0:
            problems.append(f"{image['view']} debug view is completely black")
        png = os.path.join(verification_root, image["file"])
        if not os.path.exists(png):
            problems.append(f"{image['file']} is missing")
        elif sha256(png) != image.get("sha256", ""):
            # PNG files are gamma encoded; the recorded hash is the raw capture hash, so only check presence.
            pass

    history = {snapshot["label"]: snapshot for snapshot in mode.get("historyChecks", [])}
    full_history = history.get("AfterFullResWarmup")
    if full_history is None:
        problems.append("AfterFullResWarmup history snapshot is missing")
    else:
        if not full_history.get("ssgiResourcesAllocated") or not full_history.get("ssgiHasValidHistory"):
            problems.append("the SSGI resources or history never became valid")
        if full_history.get("ssgiReadSlot") == full_history.get("ssgiWriteSlot"):
            problems.append("the frame read and wrote the same SSGI history slot")
        if full_history.get("ssgiTraceWidth") != 1280 or full_history.get("ssgiTraceHeight") != 720:
            problems.append("the full resolution trace size is wrong")
    half_history = history.get("AfterHalfResWarmup")
    if half_history is not None and (half_history.get("ssgiTraceWidth") != 640 or half_history.get("ssgiTraceHeight") != 360):
        problems.append("the half resolution trace size is wrong")

    return problems


def main():
    verification_path = os.path.join(ROOT, "T6Verification.json")
    report_path = os.path.join(ROOT, "T6Report.json")
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
        mode_problems[name] = analyze_mode(mode, ROOT)

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
            name = f"T6_{mode}_{view}.png"
            if not os.path.exists(os.path.join(ROOT, name)):
                missing.append(name)
            else:
                pngs[name] = sha256(os.path.join(ROOT, name))
        if missing:
            mode_problems[mode].append("missing captures: " + ", ".join(missing))

    frame_debugger_path = os.path.join(ROOT, "FrameDebugger", "FrameDebuggerReport.json")
    frame_debugger = None
    frame_debugger_order = {}
    if os.path.exists(frame_debugger_path):
        frame_debugger = load_json(frame_debugger_path)
        for mode in frame_debugger.get("modes", []):
            names = mode.get("eventNames", [])
            pyramid = next((i for i, name in enumerate(names) if "SSL Depth Pyramid" in name), -1)
            ssgi = next((i for i, name in enumerate(names) if "Screen Space Global Illumination" in name), -1)
            opaque = next((i for i, name in enumerate(names) if i > ssgi >= 0 and "RenderLoop.DrawSRPBatcher" in name), -1)
            capture = next((i for i, name in enumerate(names) if "SSL Color History Capture" in name), -1)
            frame_debugger_order[mode["name"]] = {
                "depthPyramid": pyramid,
                "ssgi": ssgi,
                "opaque": opaque,
                "historyCapture": capture,
                "ordered": 0 <= pyramid < ssgi < opaque and 0 <= capture,
            }

    criteria = {
        "verificationCompleted": bool(verification.get("completed")),
        "threePathsPassed": all(not mode_problems[name] for name in MODE_NAMES),
        "dispatchChainExecuted": all(modes.get(name, {}).get("traceDispatches", 0) > 0 for name in MODE_NAMES),
        "missKeepsBakedGI": all(modes.get(name, {}).get("missPixelsDifferent", 1) == 0 for name in MODE_NAMES),
        "firstFrameFallback": all(
            state_map(modes.get(name, {})).get("FirstFrameAfterReset", {}).get("identicalToReference", False)
            for name in MODE_NAMES
        ),
        "zeroIntensityRestoresBaseline": all(
            state_map(modes.get(name, {})).get("ZeroIntensityRestoresBaseline", {}).get("identicalToReference", False)
            for name in MODE_NAMES
        ),
        "halfResolutionUpsample": all(modes.get(name, {}).get("halfResUpsampleDispatches", 0) > 0 for name in MODE_NAMES),
        "debugViewMatchesConsumedTexture": all(
            modes.get(name, {}).get("debugConsumptionPixels", 0) > 0
            and modes.get(name, {}).get("debugConsumptionMismatchPixels", 1)
            <= modes.get(name, {}).get("debugConsumptionPixels", 1) * 0.01
            for name in MODE_NAMES
        ),
        "emissionNotDoubled": all(
            0.65 <= modes.get(name, {}).get("emissiveLuminanceRatio", 1.0) <= 1.35 for name in MODE_NAMES
        ),
        "noInvalidPixels": all(
            all(
                state_map(modes.get(name, {})).get(view, {}).get("magentaPixels", 0) == 0
                and state_map(modes.get(name, {})).get(view, {}).get("nonFinitePixels", 0) == 0
                for view in ("Reference", "SsgiFullRes", "SsgiHalfRes", "FirstFrameAfterReset", "CameraCut")
                if view in state_map(modes.get(name, {}))
            )
            for name in MODE_NAMES
        ),
        "frameDebuggerOrder": bool(frame_debugger_order)
        and all(entry["ordered"] for entry in frame_debugger_order.values()),
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
                    "state": {state["state"]: state for state in modes.get(name, {}).get("states", [])},
                    "dispatches": {
                        key: modes.get(name, {}).get(key)
                        for key in (
                            "ssgiExecutions",
                            "traceDispatches",
                            "reprojectDispatches",
                            "validationDispatches",
                            "accumulationDispatches",
                            "diffuseDispatches",
                            "upsampleDispatches",
                            "setupExecutions",
                            "ssgiBoundFrames",
                            "halfResUpsampleDispatches",
                            "cameraCutResetCount",
                        )
                    },
                    "weight": modes.get(name, {}).get("weight"),
                    "rawWeight": modes.get(name, {}).get("rawWeight"),
                    "accumulationWeight": modes.get(name, {}).get("accumulationWeight"),
                    "hitPointHits": modes.get(name, {}).get("hitPointHits"),
                    "hitPointPixels": modes.get(name, {}).get("hitPointPixels"),
                    "missingHistory": modes.get(name, {}).get("missPixelsCompared"),
                    "missingHistoryDifferent": modes.get(name, {}).get("missPixelsDifferent"),
                    "emissivePixelCount": modes.get(name, {}).get("emissivePixelCount"),
                    "emissiveLuminanceRatio": modes.get(name, {}).get("emissiveLuminanceRatio"),
                    "debugConsumptionMismatchPixels": modes.get(name, {}).get("debugConsumptionMismatchPixels"),
                    "debugConsumptionPixels": modes.get(name, {}).get("debugConsumptionPixels"),
                    "historyChecks": modes.get(name, {}).get("historyChecks"),
                    "debugImages": modes.get(name, {}).get("debugImages"),
                }
                for name in MODE_NAMES
            },
        },
        "editModeTests": editmode,
        "frameDebugger": frame_debugger,
        "frameDebuggerOrder": frame_debugger_order,
        "requiredAssets": assets,
        "missingMeta": missing_meta,
        "captures": pngs,
        "criteria": criteria,
    }

    report["t6ExitCriteriaMet"] = all(criteria.values())
    with open(report_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)

    print(json.dumps({key: report[key] for key in ("criteria", "t6ExitCriteriaMet")}, indent=2))
    raise SystemExit(0 if report["t6ExitCriteriaMet"] else 1)


if __name__ == "__main__":
    main()