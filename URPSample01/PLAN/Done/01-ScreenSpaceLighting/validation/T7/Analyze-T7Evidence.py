#!/usr/bin/env python3
"""Builds the T7 exit report from the Unity verification, EditMode log and Frame Debugger run."""

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
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT7Verification.cs",
    SSL_RUNTIME + "/Passes/SsrPass.cs",
    SSL_RUNTIME + "/Passes/ScreenSpaceLightingSetupPass.cs",
    SSL_RUNTIME + "/ScreenSpaceLightingShaderVariables.cs",
    SSL_RUNTIME + "/ScreenSpaceLightingShaderVariables.cs.hlsl",
    SSL_RUNTIME + "/ScreenSpaceLightingShaderIDs.cs",
    SSL_RUNTIME + "/ScreenSpaceLightingResources.cs",
    SSL_RUNTIME + "/ScreenSpaceLightingHistory.cs",
    SSL_RUNTIME + "/ScreenSpaceReflection.cs",
    SSL_SHADERS + "/ScreenSpaceLightingCommon.hlsl",
    SSL_SHADERS + "/RayMarching.hlsl",
    SSL_SHADERS + "/ScreenSpaceGlobalIllumination.compute",
    SSL_SHADERS + "/ScreenSpaceReflections.compute",
    COMMON_SHADERS + "/DepthPyramid.compute",
    CORE_SHADERS + "/ScreenSpaceLightingInput.hlsl",
    CORE_SHADERS + "/ScreenSpaceLightingBlend.hlsl",
    URP + "/ShaderLibrary/GlobalIllumination.hlsl",
    URP + "/Shaders/Lit.shader",
    URP + "/Shaders/ComplexLit.shader",
    URP + "/Runtime/UniversalRenderPipelineCore.cs",
    URP + "/Runtime/ScriptableRenderer.cs",
    URP + "/Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingSsrTests.cs",
]

MODE_NAMES = ["Forward", "ForwardPlus", "Deferred"]
MODE_PNGS = ["Reference", "SsrFullRes", "SsrRaw", "SsrWeight"]


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


def analyze_mode(mode):
    problems = []
    states = state_map(mode)

    def require(name):
        state = states.get(name)
        if state is None:
            problems.append(f"{name} capture is missing")
        return state

    if mode.get("ssrExecutions", 0) <= 0:
        problems.append("the SSR pass never executed")
    for key in ("traceDispatches", "reprojectDispatches"):
        if mode.get(key, 0) <= 0:
            problems.append(f"{key} is zero")
    if mode.get("setupExecutions", 0) <= 0 or mode.get("ssrBoundFrames", 0) <= 0:
        problems.append("the setup pass never bound the SSR texture and keyword")

    on = require("SsrFullRes")
    if on is not None:
        if not on.get("differsFromReference"):
            problems.append("SsrFullRes does not change the image")
        if on.get("magentaPixels", 0) or on.get("nonFinitePixels", 0):
            problems.append("SsrFullRes contains invalid pixels")
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
        problems.append("no zero weight pixel was compared with the reflection probe image")
    if mode.get("missPixelsDifferent", 0) != 0:
        problems.append(f"{mode.get('missPixelsDifferent')} zero weight pixels differ from the probe image")
    if mode.get("missMaxChannelDelta", 1.0) > 1e-4:
        problems.append("the zero weight pixels changed by more than float precision")

    weight = mode.get("weight", {})
    if weight.get("nonFinitePixels", 0) > 0:
        problems.append("the SSR output texture contains NaN or Inf")
    if weight.get("negativePixels", 0) > 0:
        problems.append("the SSR output texture contains negative radiance")
    if weight.get("coveredFraction", 0.0) < 0.0005:
        problems.append("the SSR output never reaches full weight on a measurable area")
    if weight.get("missFraction", 0.0) < 0.01:
        problems.append("no pixel fell back to the reflection probe")

    if mode.get("linearityPixels", 0) < 100:
        problems.append("not enough clean first-frame pixels to verify the intensity response")
    elif not (1.9 <= mode.get("linearityRatio", 0.0) <= 2.1) or mode.get("linearityRelativeError", 1.0) > 0.05:
        problems.append(
            "the intensity response is not linear (ratio {:.3f}, error {:.4f})".format(
                mode.get("linearityRatio", 0.0), mode.get("linearityRelativeError", 1.0)
            )
        )

    probes = mode.get("materialProbes", [])
    metal = sorted([p for p in probes if p.get("metallic")], key=lambda p: p.get("smoothness", 0.0), reverse=True)
    dielectric = [p for p in probes if not p.get("metallic") and p.get("smoothness", 0.0) >= 0.9]
    if not metal:
        problems.append("no metal material probe was measured")
    else:
        smooth_metal = [p for p in metal if p.get("smoothness", 0.0) >= 0.9]
        if not smooth_metal or smooth_metal[0].get("meanWeight", 0.0) <= 1e-4:
            problems.append("the smooth metal sphere received no screen-space reflection")
        previous = None
        for probe in metal:
            if probe.get("smoothness", 0.0) < 0.5:
                if probe.get("meanWeight", 0.0) > 1e-4:
                    problems.append(f"{probe['name']} is below minSmoothness but has weight")
                continue
            if previous is not None and probe.get("meanWeight", 0.0) > previous.get("meanWeight", 0.0) + 1e-4:
                problems.append(
                    f"roughness fade is not monotonic: {probe['name']} has more weight than {previous['name']}"
                )
            previous = probe
    if not dielectric or dielectric[0].get("meanWeight", 0.0) <= 1e-4:
        problems.append("the smooth dielectric sphere received no screen-space reflection")

    if mode.get("cameraCutResetCount", 0) <= 0:
        problems.append("the camera reset did not invalidate the color history")

    if mode.get("debugConsumptionPixels", 0) <= 0:
        problems.append("the debug view was not compared with the consumed SSR texture")
    elif mode.get("debugConsumptionMismatchPixels", 0) > mode.get("debugConsumptionPixels", 1) * 0.01:
        problems.append("the debug view and the consumed SSR texture disagree")

    for image in mode.get("debugImages", []):
        if image.get("magentaPixels", 0) or image.get("nonFinitePixels", 0):
            problems.append(f"{image['view']} debug view contains invalid pixels")
        if image.get("meanLuminance", 0.0) <= 0.0:
            problems.append(f"{image['view']} debug view is completely black")
        png = os.path.join(ROOT, image["file"])
        if not os.path.exists(png):
            problems.append(f"{image['file']} is missing")

    history = {snapshot["label"]: snapshot for snapshot in mode.get("historyChecks", [])}
    warm = history.get("AfterWarmup")
    if warm is None:
        problems.append("AfterWarmup history snapshot is missing")
    else:
        if not warm.get("ssrResourcesAllocated"):
            problems.append("the SSR output resources were not allocated")
        if warm.get("ssrWidth") != 1280 or warm.get("ssrHeight") != 720:
            problems.append("the SSR output size is wrong")
        if warm.get("readSlot", -1) < 0:
            problems.append("the previous color pyramid was never captured")

    return problems


def main():
    verification_path = os.path.join(ROOT, "T7Verification.json")
    report_path = os.path.join(ROOT, "T7Report.json")
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
            name = f"T7_{mode}_{view}.png"
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
            ssr = next((i for i, name in enumerate(names) if "Screen Space Reflections" in name), -1)
            opaque = next((i for i, name in enumerate(names) if i > ssr >= 0 and "RenderLoop.DrawSRPBatcher" in name), -1)
            capture = next((i for i, name in enumerate(names) if "SSL Color History Capture" in name), -1)
            frame_debugger_order[mode["name"]] = {
                "depthPyramid": pyramid,
                "ssr": ssr,
                "opaque": opaque,
                "historyCapture": capture,
                "ordered": 0 <= pyramid < ssr < opaque and 0 <= capture,
            }

    criteria = {
        "verificationCompleted": bool(verification.get("completed")),
        "threePathsPassed": all(not mode_problems[name] for name in MODE_NAMES),
        "dispatchChainExecuted": all(modes.get(name, {}).get("traceDispatches", 0) > 0 for name in MODE_NAMES),
        "missKeepsReflectionProbe": all(modes.get(name, {}).get("missPixelsDifferent", 1) == 0 for name in MODE_NAMES),
        "firstFrameFallback": all(
            state_map(modes.get(name, {})).get("FirstFrameAfterReset", {}).get("identicalToReference", False)
            for name in MODE_NAMES
        ),
        "zeroIntensityRestoresBaseline": all(
            state_map(modes.get(name, {})).get("ZeroIntensityRestoresBaseline", {}).get("identicalToReference", False)
            for name in MODE_NAMES
        ),
        "roughnessGradient": all(
            not [p for p in mode_problems[name] if "roughness" in p.lower() or "minSmoothness" in p]
            for name in MODE_NAMES
        ),
        "nonMetalSupported": all(
            any(
                not probe.get("metallic") and probe.get("smoothness", 0.0) >= 0.9 and probe.get("meanWeight", 0.0) > 1e-4
                for probe in modes.get(name, {}).get("materialProbes", [])
            )
            for name in MODE_NAMES
        ),
        "intensityAppliedOnce": all(
            modes.get(name, {}).get("linearityPixels", 0) >= 100
            and 1.9 <= modes.get(name, {}).get("linearityRatio", 0.0) <= 2.1
            and modes.get(name, {}).get("linearityRelativeError", 1.0) <= 0.05
            for name in MODE_NAMES
        ),
        "noInvalidPixels": all(
            modes.get(name, {}).get("weight", {}).get("nonFinitePixels", 1) == 0
            and modes.get(name, {}).get("weight", {}).get("negativePixels", 1) == 0
            for name in MODE_NAMES
        )
        and all(
            all(
                state_map(modes.get(name, {})).get(view, {}).get("magentaPixels", 0) == 0
                and state_map(modes.get(name, {})).get(view, {}).get("nonFinitePixels", 0) == 0
                for view in ("Reference", "SsrFullRes", "FirstFrameAfterReset", "CameraCut")
                if view in state_map(modes.get(name, {}))
            )
            for name in MODE_NAMES
        ),
        "debugViewMatchesConsumedTexture": all(
            modes.get(name, {}).get("debugConsumptionPixels", 0) > 0
            and modes.get(name, {}).get("debugConsumptionMismatchPixels", 1)
            <= modes.get(name, {}).get("debugConsumptionPixels", 1) * 0.01
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
                    "state": state_map(modes.get(name, {})),
                    "dispatches": {
                        key: modes.get(name, {}).get(key)
                        for key in (
                            "ssrExecutions",
                            "traceDispatches",
                            "reprojectDispatches",
                            "setupExecutions",
                            "ssrBoundFrames",
                            "cameraCutResetCount",
                        )
                    },
                    "weight": modes.get(name, {}).get("weight"),
                    "missPixelsCompared": modes.get(name, {}).get("missPixelsCompared"),
                    "missPixelsDifferent": modes.get(name, {}).get("missPixelsDifferent"),
                    "missMaxChannelDelta": modes.get(name, {}).get("missMaxChannelDelta"),
                    "linearityPixels": modes.get(name, {}).get("linearityPixels"),
                    "linearityRatio": modes.get(name, {}).get("linearityRatio"),
                    "linearityRelativeError": modes.get(name, {}).get("linearityRelativeError"),
                    "materialProbes": modes.get(name, {}).get("materialProbes"),
                    "debugConsumptionPixels": modes.get(name, {}).get("debugConsumptionPixels"),
                    "debugConsumptionMismatchPixels": modes.get(name, {}).get("debugConsumptionMismatchPixels"),
                    "outputTextureBytes": modes.get(name, {}).get("outputTextureBytes"),
                    "historyChecks": modes.get(name, {}).get("historyChecks"),
                    "debugImages": modes.get(name, {}).get("debugImages"),
                }
                for name in MODE_NAMES
            },
        },
        "designNotes": {
            "materialBlend": "ScreenSpaceLightingApplyIndirectSpecular lerps the environment specular "
            "with the SSR radiance before EnvironmentBRDF; EnvironmentBRDFSpecular/FGD is therefore "
            "applied to the combined result exactly once.",
            "resultContract": "RGBA16F un-premultiplied radiance with the blend weight in alpha. "
            "miss/off-screen/non-finite write weight 0 and keep the reflection probe.",
            "deferredSmoothness": "SSR runs at event 206, before the GBuffer overwrites GBuffer2, so it "
            "reads the same 0.5 + 0.5 * smoothness encoding the Forward depth-normals prepass writes.",
            "colorHistoryFeedback": "The color pyramid includes the previous frame's SSR contribution; "
            "intensity linearity is measured on the first SSR frame after a reset, which reads an "
            "SSR-free color history.",
        },
        "editModeTests": editmode,
        "frameDebugger": frame_debugger,
        "frameDebuggerOrder": frame_debugger_order,
        "requiredAssets": assets,
        "missingMeta": missing_meta,
        "captures": pngs,
        "criteria": criteria,
    }

    report["t7ExitCriteriaMet"] = all(criteria.values())
    with open(report_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)

    print(json.dumps({key: report[key] for key in ("criteria", "t7ExitCriteriaMet")}, indent=2))
    raise SystemExit(0 if report["t7ExitCriteriaMet"] else 1)


if __name__ == "__main__":
    main()