#!/usr/bin/env python3
"""Builds the T10 exit report from the Unity verification and EditMode log."""

import hashlib
import json
import os
import re
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(ROOT, "..", "..", "..", "..", ".."))
URP = "Packages/com.unity.render-pipelines.universal@14.0.12"

REQUIRED_ASSETS = [
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT10Verification.cs",
    "Assets/Tests/ScreenSpaceLighting/Materials/T10TransparentReceiver.mat",
    "Assets/Tests/ScreenSpaceLighting/Materials/T10TransparentNoReceiver.mat",
    "Assets/Tests/ScreenSpaceLighting/Materials/T10TransparentSimpleLit.mat",
    "Assets/Tests/ScreenSpaceLighting/Materials/T10TransparentComplexLit.mat",
    URP + "/ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingInput.hlsl",
    URP + "/ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingBlend.hlsl",
    URP + "/Shaders/Utils/TransparentDepthNormalsPass.hlsl",
    URP + "/Shaders/Lit.shader",
    URP + "/Shaders/ComplexLit.shader",
    URP + "/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/TransparentDepthNormalRenderPass.cs",
    URP + "/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/TransparentSsrPass.cs",
    URP + "/Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingTransparentSsrTests.cs",
    URP + "/Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingMaterialTests.cs",
]

MODE_NAMES = ["Forward", "ForwardPlus", "Deferred"]
MODE_VIEWS = [
    "Reference",
    "OpaqueOnly",
    "TransparentOn",
    "TransparentIntensity0",
    "TransparentOffAgain",
    "SsrWeight",
    "SsrTransparentWeight",
    "SsrTransparentRaw",
    "TransparentDepth",
    "CameraDepth",
    "CameraDepthWithTransparentPrepass",
    "SsrTransparentWeightPbr",
]
PBR_FRAMES = 4
WEIGHT_TOLERANCE = 1e-4


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
    states = state_map(mode)

    def require(state_name):
        state = states.get(state_name)
        if state is None:
            problems.append(f"{state_name} capture is missing")
        return state

    require("Reference")
    require("OpaqueOnly")
    require("TransparentOn")

    intensity_zero = require("TransparentIntensity0")
    if intensity_zero is not None and not intensity_zero.get("identicalToReference", False):
        problems.append(
            "transparent intensity 0 differs from the probe reference by "
            f"{intensity_zero.get('differingPixels')} pixels"
        )

    off_again = require("TransparentOffAgain")
    if off_again is not None and not off_again.get("identicalToReference", False):
        problems.append(
            f"transparent off again differs from the opaque-only image by {off_again.get('differingPixels')} pixels"
        )

    ray = mode.get("rayOrigin") or {}
    if not ray.get("passed", False):
        problems.append("ray origin evidence failed")
    if not ray.get("receiverNearerThanOpaque", False):
        problems.append("the transparent depth is not nearer than the opaque background at the receiver")
    if ray.get("differingFromOpaqueDepthPixels", 0) <= 0:
        problems.append("the transparent depth equals the opaque depth everywhere")
    if not ray.get("nearestLayerWins", False):
        problems.append("two overlapping transparent layers did not resolve to the nearest surface")

    if mode.get("opaqueWeightDiffPixels", 1) != 0:
        problems.append("transparent SSR changed the opaque SSR output")
    if mode.get("cameraDepthDiffPixels", 1) != 0:
        problems.append("the transparent prepass changed the opaque camera depth")

    if mode.get("receiverChangedPixels", 0) <= 0:
        problems.append("the opted-in transparent material did not change with transparent SSR")
    if mode.get("receiverWeight", 0.0) <= WEIGHT_TOLERANCE:
        problems.append("the opted-in transparent material has no transparent SSR weight")
    if mode.get("optOutChangedPixels", 1) != 0:
        problems.append("the material without Receive Transparent SSR changed with transparent SSR")
    if mode.get("coatReceiverChangedPixels", 0) <= 0:
        problems.append("the opted-in Complex Lit (clear coat) transparent material did not change with transparent SSR")
    if mode.get("coatReceiverWeight", 0.0) <= WEIGHT_TOLERANCE:
        problems.append("the opted-in Complex Lit (clear coat) transparent material has no transparent SSR weight")
    if mode.get("optOutWeight", 1.0) > WEIGHT_TOLERANCE:
        problems.append("the material without Receive Transparent SSR contributed a ray origin")
    if mode.get("unsupportedChangedPixels", 1) != 0:
        problems.append("an unsupported transparent shader changed with transparent SSR")
    if mode.get("unsupportedWeight", 1.0) > WEIGHT_TOLERANCE:
        problems.append("an unsupported transparent shader contributed a ray origin")

    if mode.get("transparentAccumulationDispatchCount", 1) != 0:
        problems.append("transparent SSR dispatched an accumulation kernel")
    if mode.get("transparentTraceDispatchCount", 0) != PBR_FRAMES:
        problems.append("transparent SSR did not trace once per frame with the PBR opaque algorithm")
    if mode.get("transparentReprojectDispatchCount", 0) != PBR_FRAMES:
        problems.append("transparent SSR did not reproject once per frame with the PBR opaque algorithm")
    if not mode.get("transparentPassedWithPbr", False):
        problems.append("transparent SSR stopped while the opaque algorithm was PBR")
    if mode.get("opaqueAccumulationKernelCount", 0) <= 0:
        problems.append("the opaque PBR path did not run in the accumulation test")
    if mode.get("pbrTransparentWeight", 0.0) <= WEIGHT_TOLERANCE:
        problems.append("transparent SSR produced no weight with the PBR opaque algorithm")

    motion = mode.get("motion") or {}
    if not motion.get("passed", False):
        problems.append("the moving transparent object produced an invalid ray origin or invalid output")
    if not mode.get("motionLimitation"):
        problems.append("the transparent motion limitation is not recorded")

    for state_name, state in states.items():
        if state.get("magentaPixels", 0):
            problems.append(f"{state_name} contains error-shader pixels")
        if state.get("nonFinitePixels", 0):
            problems.append(f"{state_name} contains non-finite pixels")

    return problems


def main():
    verification_path = os.path.join(ROOT, "T10Verification.json")
    report_path = os.path.join(ROOT, "T10Report.json")
    editmode = parse_editmode(os.path.join(ROOT, "UnityTests.log"))

    verification = load_json(verification_path)
    modes = {mode.get("name"): mode for mode in verification.get("modes", [])}

    mode_problems = {name: analyze_mode(name, modes.get(name, {})) for name in MODE_NAMES}

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
        for view in MODE_VIEWS:
            name = f"T10_{mode}_{view}.png"
            if not os.path.exists(os.path.join(ROOT, name)):
                missing.append(name)
            else:
                pngs[name] = sha256(os.path.join(ROOT, name))
        if missing:
            mode_problems[mode].append("missing captures: " + ", ".join(missing))

    criteria = {
        "verificationCompleted": bool(verification.get("completed")),
        "threePathsPassed": all(not mode_problems[name] for name in MODE_NAMES),
        "rayOriginFromTransparentSurface": all(
            (modes.get(name, {}).get("rayOrigin") or {}).get("passed", False) for name in MODE_NAMES
        ),
        "nearestTransparentLayerWins": all(
            (modes.get(name, {}).get("rayOrigin") or {}).get("nearestLayerWins", False) for name in MODE_NAMES
        ),
        "mainTransparentDepthUnchanged": all(
            modes.get(name, {}).get("cameraDepthDiffPixels", 1) == 0 for name in MODE_NAMES
        ),
        "opaqueAndTransparentOutputsAreIndependent": all(
            modes.get(name, {}).get("opaqueWeightDiffPixels", 1) == 0
            and modes.get(name, {}).get("receiverChangedPixels", 0) > 0
            for name in MODE_NAMES
        ),
        "noAccumulationHistoryMisuse": all(
            modes.get(name, {}).get("transparentAccumulationDispatchCount", 1) == 0
            and modes.get(name, {}).get("transparentTraceDispatchCount", 0) == PBR_FRAMES
            for name in MODE_NAMES
        ),
        "clearCoatTransparentCoverage": all(
            modes.get(name, {}).get("coatReceiverChangedPixels", 0) > 0
            and modes.get(name, {}).get("coatReceiverWeight", 0.0) > WEIGHT_TOLERANCE
            for name in MODE_NAMES
        ),
        "unsupportedMaterialsFallBackToProbe": all(
            modes.get(name, {}).get("optOutChangedPixels", 1) == 0
            and modes.get(name, {}).get("unsupportedChangedPixels", 1) == 0
            and modes.get(name, {}).get("optOutWeight", 1.0) <= WEIGHT_TOLERANCE
            and modes.get(name, {}).get("unsupportedWeight", 1.0) <= WEIGHT_TOLERANCE
            for name in MODE_NAMES
        ),
        "transparentMotionLimitationRecorded": all(
            (modes.get(name, {}).get("motion") or {}).get("passed", False)
            and bool(modes.get(name, {}).get("motionLimitation"))
            for name in MODE_NAMES
        ),
        "noDoubleProbeOrBrdf": all(
            state_map(modes.get(name, {})).get("TransparentIntensity0", {}).get("identicalToReference", False)
            and state_map(modes.get(name, {})).get("TransparentOffAgain", {}).get("identicalToReference", False)
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
                    "rayOrigin": modes.get(name, {}).get("rayOrigin"),
                    "opaqueWeightDiffPixels": modes.get(name, {}).get("opaqueWeightDiffPixels"),
                    "cameraDepthDiffPixels": modes.get(name, {}).get("cameraDepthDiffPixels"),
                    "receiverChangedPixels": modes.get(name, {}).get("receiverChangedPixels"),
                    "optOutChangedPixels": modes.get(name, {}).get("optOutChangedPixels"),
                    "unsupportedChangedPixels": modes.get(name, {}).get("unsupportedChangedPixels"),
                    "receiverWeight": modes.get(name, {}).get("receiverWeight"),
                    "optOutWeight": modes.get(name, {}).get("optOutWeight"),
                    "unsupportedWeight": modes.get(name, {}).get("unsupportedWeight"),
                    "coatReceiverChangedPixels": modes.get(name, {}).get("coatReceiverChangedPixels"),
                    "coatReceiverWeight": modes.get(name, {}).get("coatReceiverWeight"),
                    "transparentTraceDispatchCount": modes.get(name, {}).get("transparentTraceDispatchCount"),
                    "transparentAccumulationDispatchCount": modes.get(name, {}).get("transparentAccumulationDispatchCount"),
                    "opaqueAccumulationKernelCount": modes.get(name, {}).get("opaqueAccumulationKernelCount"),
                    "pbrTransparentWeight": modes.get(name, {}).get("pbrTransparentWeight"),
                    "motion": modes.get(name, {}).get("motion"),
                    "motionLimitation": modes.get(name, {}).get("motionLimitation"),
                }
                for name in MODE_NAMES
            },
        },
        "criteria": criteria,
        "t10ExitCriteriaMet": all(criteria.values()),
        "assets": assets,
        "missingMeta": missing_meta,
        "editMode": editmode,
        "captures": pngs,
    }

    with open(report_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)

    print(json.dumps({
        "t10ExitCriteriaMet": report["t10ExitCriteriaMet"],
        "criteria": criteria,
        "modeProblems": mode_problems,
    }, indent=2))
    if not report["t10ExitCriteriaMet"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()