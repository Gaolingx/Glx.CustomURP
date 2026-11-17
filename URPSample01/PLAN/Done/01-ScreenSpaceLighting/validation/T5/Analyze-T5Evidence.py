#!/usr/bin/env python3
"""Builds the T5 exit report from the Unity verification, Frame Debugger and EditMode logs."""

import hashlib
import json
import os
import re
import sys
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(ROOT, "..", "..", "..", "..", ".."))
URP = "Packages/com.unity.render-pipelines.universal@14.0.12"

GTAO_RUNTIME = URP + "/Runtime/RendererFeatures/GroundTruthAmbientOcclusion"
GTAO_SHADERS = URP + "/Shaders/Utils/GroundTruthAmbientOcclusion"

REQUIRED_META = [
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT5Verification.cs",
    GTAO_RUNTIME + "/GroundTruthAmbientOcclusionRendererFeature.cs",
    GTAO_RUNTIME + "/GroundTruthAmbientOcclusionSettings.cs",
    GTAO_RUNTIME + "/GroundTruthAmbientOcclusionResources.cs",
    GTAO_RUNTIME + "/GroundTruthAmbientOcclusionShaderIDs.cs",
    GTAO_RUNTIME + "/GroundTruthAmbientOcclusionShaderVariables.cs",
    GTAO_RUNTIME + "/GroundTruthAmbientOcclusionShaderVariables.cs.hlsl",
    GTAO_RUNTIME + "/GroundTruthAmbientOcclusionHistory.cs",
    GTAO_RUNTIME + "/GroundTruthAmbientOcclusionPass.cs",
    GTAO_RUNTIME + "/GroundTruthAmbientOcclusionDepthPyramidPass.cs",
    GTAO_RUNTIME + "/GroundTruthAmbientOcclusionDebugPass.cs",
    GTAO_RUNTIME + "/GroundTruthAmbientOcclusionDebugMode.cs",
    GTAO_SHADERS + "/GTAOCommon.hlsl",
    GTAO_SHADERS + "/GTAO.compute",
    GTAO_SHADERS + "/GTAOSpatialDenoise.compute",
    GTAO_SHADERS + "/GTAOTemporalDenoise.compute",
    GTAO_SHADERS + "/GTAOCopyHistory.compute",
    GTAO_SHADERS + "/GTAOBlurAndUpsample.compute",
    GTAO_SHADERS + "/GTAODebug.shader",
    URP + "/Editor/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionEditor.cs",
    URP + "/Tests/Editor/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionRendererFeatureTests.cs",
    URP + "/Tests/Editor/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionHistoryTests.cs",
    URP + "/Tests/Editor/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionCBufferTests.cs",
]

MODE_NAMES = ["Forward", "ForwardPlus", "Deferred"]


def load_json(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def find_meta(path):
    return os.path.exists(os.path.join(PROJECT, path + ".meta"))


def state_map(mode):
    return {state["state"]: state for state in mode.get("states", [])}


def history_map(mode):
    return {snapshot["label"]: snapshot for snapshot in mode.get("historyChecks", [])}


def analyze_mode(mode):
    problems = []
    states = state_map(mode)
    histories = history_map(mode)

    def require(name):
        state = states.get(name)
        if state is None:
            problems.append(f"{name} capture is missing")
        return state

    def require_history(label):
        snapshot = histories.get(label)
        if snapshot is None:
            problems.append(f"{label} history snapshot is missing")
        return snapshot

    on = require("GtaoHalfResTemporal")
    if on is not None:
        if not on.get("differsFromReference"):
            problems.append("GtaoHalfResTemporal does not change the image")
        if on.get("magentaPixels", 0) or on.get("nonFinitePixels", 0):
            problems.append("GtaoHalfResTemporal contains invalid pixels")

    full = require("GtaoFullRes")
    if full is not None and not full.get("differsFromReference"):
        problems.append("GtaoFullRes matches the half resolution image")

    spatial = require("GtaoSpatialOnly")
    if spatial is not None and not spatial.get("differsFromReference"):
        problems.append("GtaoSpatialOnly does not change the image")

    zero = require("ZeroIntensityRestoresBaseline")
    if zero is not None and not zero.get("identicalToReference"):
        problems.append(f"ZeroIntensityRestoresBaseline differs by {zero.get('differingPixels')} pixels")

    disabled = require("FeatureDisabled")
    if disabled is not None and not disabled.get("identicalToReference"):
        problems.append(f"FeatureDisabled differs by {disabled.get('differingPixels')} pixels")

    combined = require("CombinedOrder_GtaoThenSsl")
    if combined is not None and not combined.get("identicalToReference"):
        problems.append(f"swapping the GTAO/SSL order changed {combined.get('differingPixels')} pixels")
    if not mode.get("orderIndependent"):
        problems.append("the feature order comparison did not report order independence")

    if mode.get("pyramidExecutions", 0) <= 0:
        problems.append("the GTAO depth pyramid pass never executed")
    for key in ("mainDispatches", "spatialDispatches", "temporalDispatches", "upsampleDispatches"):
        if mode.get(key, 0) <= 0:
            problems.append(f"{key} was never dispatched")
    if mode.get("spatialOnlyTemporalDispatches") != 0:
        problems.append("the temporal kernel ran with temporal accumulation disabled")
    if mode.get("spatialOnlyUpsampleDispatches", 0) <= 0:
        problems.append("the non temporal blur/upsample kernel never ran")

    warmup = require_history("AfterWarmup")
    if warmup is not None:
        if warmup.get("aoWidth") != 640 or warmup.get("aoHeight") != 360:
            problems.append("the half resolution AO buffer is not 640x360")
        if warmup.get("depthPyramidMipCount") != 12:
            problems.append("the private depth pyramid does not have 12 mips")
        if not warmup.get("resourcesAllocated") or not warmup.get("hasValidHistory"):
            problems.append("the private resources or history never became valid")

    cut = require_history("CameraCutReset_FirstFrame")
    if cut is not None:
        if cut.get("readSlot") != -1:
            problems.append("the first frame after a camera cut read stale history")

    size = require_history("SizeChange640x360")
    if size is not None:
        if size.get("width") != 640 or size.get("aoWidth") != 320:
            problems.append("the size change did not resize the camera/AO buffers")
        if size.get("resourceGeneration", 0) <= 1:
            problems.append("the size change did not reallocate the private resources")

    temp = require_history("TemporaryCamera_FirstFrame")
    if temp is not None and temp.get("readSlot") != -1:
        problems.append("a new camera read another camera's AO history")

    if mode.get("entriesAfterCameraDestroy") != 1:
        problems.append("the destroyed temporary camera was not pruned")

    if not mode.get("debugImages"):
        problems.append("no debug images were captured")
    for image in mode.get("debugImages", []):
        if image.get("magentaPixels", 0) or image.get("nonFinitePixels", 0):
            problems.append(f"debug view {image.get('view')} contains invalid pixels")

    return problems


def analyze_frame_debugger(report):
    problems = []
    modes = {}
    if not report.get("completed"):
        return False, {"error": report.get("error")}, ["Frame Debugger did not complete"]

    for mode in report.get("modes", []):
        names = mode.get("eventNames", [])
        mode_problems = []
        pyramid = [i for i, name in enumerate(names) if "GTAO Depth Pyramid" in name]
        dispatch = [i for i, name in enumerate(names) if "Ground Truth Ambient Occlusion" in name]
        motion = [i for i, name in enumerate(names) if "MotionVectors" in name]

        if not pyramid:
            mode_problems.append("GTAO Depth Pyramid never appears")
        if not dispatch:
            mode_problems.append("Ground Truth Ambient Occlusion never appears")

        if pyramid and dispatch and pyramid[0] > dispatch[0]:
            mode_problems.append("the GTAO depth pyramid runs after the GTAO dispatch")
        if motion and pyramid and motion[0] > pyramid[0]:
            mode_problems.append("the GTAO depth pyramid runs before MotionVectors")

        if dispatch:
            last_dispatch = dispatch[-1]
            deferred = "Deferred" in mode.get("name", "")
            consumer_markers = ["GBuffer"] if deferred else ["RenderLoop.DrawSRPBatcher", "Camera.RenderSkybox"]
            consumer = [i for i, name in enumerate(names) if any(marker in name for marker in consumer_markers) and i > last_dispatch]
            if not consumer:
                mode_problems.append("no opaque/GBuffer consumer runs after the GTAO dispatch")

            if deferred:
                deferred_shading = [i for i, name in enumerate(names) if "Deferred Shading" in name]
                if deferred_shading and deferred_shading[0] < last_dispatch:
                    mode_problems.append("GTAO runs after deferred shading (must be pre-GBuffer)")

        modes[mode.get("name")] = {
            "passed": not mode_problems,
            "issues": mode_problems,
            "eventCount": len(names),
            "pyramidEvents": len(pyramid),
            "dispatchEvents": len(dispatch),
        }
        problems.extend(mode.get("name", "?") + ": " + problem for problem in mode_problems)

    return len(problems) == 0, modes, problems


def analyze_editmode(log_path):
    if not os.path.exists(log_path):
        return False, {"summary": "missing UnityTests.log"}, ["UnityTests.log was not found"]
    with open(log_path, encoding="utf-8", errors="replace") as handle:
        text = handle.read()
    match = re.search(r"EditMode tests finished: status=(\w+) passed=(\d+) failed=(\d+) skipped=(\d+)", text)
    if not match:
        return False, {"summary": "no EditMode summary"}, ["EditMode test summary was not found"]
    status, passed, failed, skipped = match.group(1), int(match.group(2)), int(match.group(3)), int(match.group(4))
    ok = status == "Passed" and failed == 0
    return ok, {
        "summary": f"EditMode tests finished: status={status} passed={passed} failed={failed} skipped={skipped}",
        "passed": passed,
        "failed": failed,
        "skipped": skipped,
    }, [] if ok else ["EditMode tests did not pass"]


def build_manifest():
    entries = []
    for name in sorted(os.listdir(ROOT)):
        path = os.path.join(ROOT, name)
        if not os.path.isfile(path) or name == "T5Report.json":
            continue
        entries.append({"file": name, "bytes": os.path.getsize(path), "sha256": sha256(path)})
    for sub in ("FrameDebugger",):
        subdir = os.path.join(ROOT, sub)
        if not os.path.isdir(subdir):
            continue
        for name in sorted(os.listdir(subdir)):
            path = os.path.join(subdir, name)
            if os.path.isfile(path):
                entries.append({"file": sub + "/" + name, "bytes": os.path.getsize(path), "sha256": sha256(path)})
    return entries


def main():
    verification_path = os.path.join(ROOT, "T5Verification.json")
    fd_path = os.path.join(ROOT, "FrameDebugger", "FrameDebuggerReport.json")
    if not os.path.exists(verification_path):
        print("Missing T5Verification.json", file=sys.stderr)
        return 1

    verification = load_json(verification_path)
    problems = []
    if not verification.get("completed"):
        problems.append("T5 verification did not complete")
    if not verification.get("passed"):
        problems.append("T5 verification reported failures")

    mode_reports = {}
    for mode in verification.get("modes", []):
        mode_problems = analyze_mode(mode)
        problems.extend(f"{mode.get('name')}: {issue}" for issue in mode_problems)
        mode_reports[mode.get("name")] = {
            "passed": not mode_problems and bool(mode.get("passed")),
            "issues": mode_problems + list(mode.get("issues", [])),
            "featureOrder": mode.get("featureOrder"),
            "reversedFeatureOrder": mode.get("reversedFeatureOrder"),
            "pyramidExecutions": mode.get("pyramidExecutions"),
            "mainDispatches": mode.get("mainDispatches"),
            "spatialDispatches": mode.get("spatialDispatches"),
            "temporalDispatches": mode.get("temporalDispatches"),
            "upsampleDispatches": mode.get("upsampleDispatches"),
            "orderIndependent": mode.get("orderIndependent"),
            "duplicateHiZBytes": mode.get("duplicateHiZBytes"),
            "featureDisabledExecutions": mode.get("featureDisabledExecutions"),
            "states": mode.get("states"),
            "historyChecks": mode.get("historyChecks"),
            "debugImages": mode.get("debugImages"),
        }

    fd_ok, fd_modes, fd_problems = (
        analyze_frame_debugger(load_json(fd_path)) if os.path.exists(fd_path) else (False, {}, ["FrameDebuggerReport.json was not found"])
    )
    problems.extend(fd_problems)

    tests_ok, tests, test_problems = analyze_editmode(os.path.join(ROOT, "UnityTests.log"))
    problems.extend(test_problems)

    missing_meta = [path for path in REQUIRED_META if not find_meta(path)]
    if missing_meta:
        problems.append("missing .meta files: " + ", ".join(missing_meta))

    report = {
        "utcTimestamp": datetime.now(timezone.utc).isoformat(),
        "unityVersion": verification.get("unityVersion"),
        "graphicsDeviceName": verification.get("graphicsDeviceName"),
        "graphicsDeviceType": verification.get("graphicsDeviceType"),
        "verification": {
            "passed": bool(verification.get("passed")),
            "completed": bool(verification.get("completed")),
            "modes": mode_reports,
        },
        "frameDebugger": {"passed": fd_ok, "modes": fd_modes},
        "editModeTests": {"passed": tests_ok, **tests},
        "metaCompleteness": {"passed": not missing_meta, "missing": missing_meta},
        "knownDifferences": [
            "GTAO owns a private packed depth pyramid and AO history; SSL keeps its own pyramid, so enabling both duplicates the Hi-Z allocation (recorded as duplicateHiZBytes).",
            "The AO output is URP visibility (1 = unoccluded, white neutral); HDRP's inverse AO is converted in GTAOOutputFinalAO.",
            "The verification scene needs DynamicGI.UpdateEnvironment so the skybox ambient probe provides indirect lighting for AO to attenuate.",
            "The built-in SSAO feature (pixel shader based) was removed on request; GTAO is the renderer's only ambient occlusion producer and owns the generic _SCREEN_SPACE_OCCLUSION contract.",
            "Only the Compatibility ScriptableRenderPass path is implemented; the RenderGraph path is not registered by these passes.",
            "XR single-pass reuses one camera view matrix for both eyes (array textures are allocated); per-eye matrices are deferred to T13.",
            "The GTAO normals come from the DepthNormals prepass and therefore use pre-decal normal/roughness on the deferred path.",
        ],
        "problems": problems,
        "manifest": build_manifest(),
    }
    report["t5ExitCriteriaMet"] = len(problems) == 0

    output = os.path.join(ROOT, "T5Report.json")
    with open(output, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)
        handle.write("\n")

    print(f"T5 report written: {output}")
    print(f"t5ExitCriteriaMet={report['t5ExitCriteriaMet']}")
    for problem in problems:
        print("  PROBLEM:", problem)
    return 0 if report["t5ExitCriteriaMet"] else 1


if __name__ == "__main__":
    sys.exit(main())