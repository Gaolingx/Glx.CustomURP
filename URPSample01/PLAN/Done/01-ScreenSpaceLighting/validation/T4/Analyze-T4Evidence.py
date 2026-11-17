#!/usr/bin/env python3
"""Builds the T4 exit report from the Unity verification, Frame Debugger and EditMode logs."""

import hashlib
import json
import os
import re
import sys
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(ROOT, "..", "..", "..", "..", ".."))
URP = os.path.join(PROJECT, "Packages", "com.unity.render-pipelines.universal@14.0.12")

REQUIRED_META = [
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT4Verification.cs",
    "Assets/Tests/ScreenSpaceLighting/Renderers/SslT4ForwardRenderer.asset",
    "Assets/Tests/ScreenSpaceLighting/Renderers/SslT4ForwardPlusRenderer.asset",
    "Assets/Tests/ScreenSpaceLighting/Renderers/SslT4DeferredRenderer.asset",
    "Assets/Tests/ScreenSpaceLighting/Renderers/SslT4ForwardPipeline.asset",
    "Assets/Tests/ScreenSpaceLighting/Renderers/SslT4ForwardPlusPipeline.asset",
    "Assets/Tests/ScreenSpaceLighting/Renderers/SslT4DeferredPipeline.asset",
    "Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceCommon/DepthPyramid.compute",
    "Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ColorPyramid.shader",
    "Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceLightingDebug.shader",
    "Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingResources.cs",
    "Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingShaderIDs.cs",
    "Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingDebugMode.cs",
    "Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingRenderUtils.cs",
    "Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/DepthPyramidPass.cs",
    "Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/ColorPyramidPass.cs",
    "Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/ScreenSpaceLightingDebugPass.cs",
    "Packages/com.unity.render-pipelines.universal@14.0.12/Tests/Editor/RendererFeatures/ScreenSpaceCommon/PackedMipChainInfoTests.cs",
    "Packages/com.unity.render-pipelines.universal@14.0.12/Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingHistoryTests.cs",
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


def find_meta(path):
    return os.path.exists(os.path.join(PROJECT, path + ".meta"))


def analyze_frame_debugger(report):
    problems = []
    modes = {}
    if not report.get("completed"):
        return False, {"error": report.get("error")}, ["Frame Debugger did not complete"]

    required = ["SSL Depth Pyramid", "SSL Current Color Pyramid", "SSL Color History Capture"]
    for mode in report.get("modes", []):
        names = mode.get("eventNames", [])
        positions = {}
        for marker in required:
            indices = [i for i, name in enumerate(names) if marker in name]
            positions[marker] = indices
        mode_problems = []
        counts = {marker: len(positions[marker]) for marker in required}
        for marker in required:
            if counts[marker] == 0:
                mode_problems.append(marker + " never appears")

        if all(counts[marker] > 0 for marker in required):
            depth_first = positions[required[0]][0]
            color_first = positions[required[1]][0]
            history_first = positions[required[2]][0]
            if depth_first > color_first:
                mode_problems.append("the depth pyramid runs after the current color pyramid")
            if color_first > history_first:
                mode_problems.append("the current color pyramid runs after the history capture")

            motion = [i for i, name in enumerate(names) if "MotionVectors" in name]
            if motion and depth_first < motion[0]:
                mode_problems.append("the depth pyramid runs before MotionVectors")

            sky = [i for i, name in enumerate(names) if "RenderSkybox" in name]
            if sky and color_first < sky[0]:
                mode_problems.append("the current color pyramid runs before the skybox")

            copy_color = [i for i, name in enumerate(names) if "CopyColor" in name]
            if copy_color and not (color_first < copy_color[0] < history_first):
                mode_problems.append("CopyColor is not between the current color pyramid and the history capture")

            final_blit = [i for i, name in enumerate(names) if "FinalBlit" in name]
            if final_blit and history_first > final_blit[0]:
                mode_problems.append("the history capture runs after the final blit")

            if "Deferred" in mode.get("name", ""):
                deferred_shading = [i for i, name in enumerate(names) if "Deferred Shading" in name]
                if deferred_shading and depth_first > deferred_shading[0]:
                    mode_problems.append("the depth pyramid runs after deferred shading (must be pre-GBuffer)")
                copy_depth = [i for i, name in enumerate(names) if "CopyDepth" in name]
                if copy_depth and depth_first < copy_depth[0]:
                    mode_problems.append("the depth pyramid runs before the deferred depth copy")

        modes[mode.get("name")] = {
            "passed": not mode_problems,
            "issues": mode_problems,
            "eventCount": len(names),
            "depthPyramidEvents": counts[required[0]],
            "currentColorPyramidEvents": counts[required[1]],
            "historyCaptureEvents": counts[required[2]],
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
        if not os.path.isfile(path) or name == "T4Report.json":
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
    verification_path = os.path.join(ROOT, "T4Verification.json")
    fd_path = os.path.join(ROOT, "FrameDebugger", "FrameDebuggerReport.json")
    if not os.path.exists(verification_path):
        print("Missing T4Verification.json", file=sys.stderr)
        return 1

    verification = load_json(verification_path)
    problems = []
    if not verification.get("completed"):
        problems.append("T4 verification did not complete")
    if not verification.get("passed"):
        problems.append("T4 verification reported failures")

    for mode in verification.get("modes", []):
        if not mode.get("passed"):
            problems.extend(f"{mode.get('name')}: {issue}" for issue in mode.get("issues", []))

    fd_ok, fd_modes, fd_problems = analyze_frame_debugger(load_json(fd_path)) if os.path.exists(fd_path) else (False, {}, ["FrameDebuggerReport.json was not found"])
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
            "modes": {
                mode.get("name"): {
                    "passed": mode.get("passed"),
                    "depthPyramidExecutions": mode.get("depthPyramidExecutions"),
                    "depthCopyDispatches": mode.get("depthCopyDispatches"),
                    "currentColorPyramidExecutions": mode.get("currentColorPyramidExecutions"),
                    "historyCaptureExecutions": mode.get("historyCaptureExecutions"),
                    "entriesWithTemporaryCamera": mode.get("entriesWithTemporaryCamera"),
                    "entriesAfterCameraDestroy": mode.get("entriesAfterCameraDestroy"),
                    "states": mode.get("states"),
                    "historyChecks": mode.get("historyChecks"),
                    "debugImages": mode.get("debugImages"),
                }
                for mode in verification.get("modes", [])
            },
        },
        "frameDebugger": {"passed": fd_ok, "modes": fd_modes},
        "editModeTests": {"passed": tests_ok, **tests},
        "metaCompleteness": {"passed": not missing_meta, "missing": missing_meta},
        "knownDifferences": [
            "The depth pyramid uses R32_SFloat raw device depth, matching HDRP's single-channel layout; the T3 capability probe was narrowed from R32G32_SFloat.",
            "Only the Compatibility ScriptableRenderPass path is implemented; the RenderGraph path is not registered by these passes.",
            "The current color pyramid is built at AfterRenderingSkybox before transparents; the previous color/depth history is captured after transparents.",
            "URP's resetHistoryFrames counter has a rising and a falling edge, so one manual reset can invalidate two consecutive SSL generations.",
        ],
        "problems": problems,
        "manifest": build_manifest(),
    }
    report["t4ExitCriteriaMet"] = len(problems) == 0

    output = os.path.join(ROOT, "T4Report.json")
    with open(output, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)
        handle.write("\n")

    print(f"T4 report written: {output}")
    print(f"t4ExitCriteriaMet={report['t4ExitCriteriaMet']}")
    for problem in problems:
        print("  PROBLEM:", problem)
    return 0 if report["t4ExitCriteriaMet"] else 1


if __name__ == "__main__":
    sys.exit(main())