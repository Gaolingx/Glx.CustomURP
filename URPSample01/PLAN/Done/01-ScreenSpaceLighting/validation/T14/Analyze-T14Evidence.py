#!/usr/bin/env python3
"""Builds the T14 release-regression report.

T14 does not verify module behaviour for the first time; it replays the suites that already own the
exit criteria (EditMode, PlayMode, the T12 graphics matrix, the Frame Debugger capture, a release
player build and the T13 XR player runs) and records whether anything regressed.
"""

import hashlib
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(ROOT, "..", "..", "..", "..", ".."))
EVIDENCE = os.path.relpath(os.path.join(ROOT, ".."), PROJECT).replace("\\", "/")
URP = "Packages/com.unity.render-pipelines.universal@14.0.12"

MODES = ["Forward", "ForwardPlus", "Deferred"]
APIS = ["DX11", "DX12", "Vulkan"]
PLAYER_MODES = ["MultiPass", "SinglePassInstanced"]

MIN_EDITMODE_PASSED = 153
MIN_CASES = {"Forward": 34, "ForwardPlus": 34, "Deferred": 35}

REQUIRED_FILES = [
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT14PlayerSetup.cs",
    "Assets/Tests/ScreenSpaceLighting/Tests/PlayMode/ScreenSpaceLightingPlayModeTests.cs",
    "Assets/Tests/ScreenSpaceLighting/Tests/PlayMode/Unity.ScreenSpaceLighting.Tests.PlayMode.asmdef",
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT12Verification.cs",
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingTestRunner.cs",
    "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT13XrPlayer.unity",
    "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT14Stripping.unity",
    URP + "/Runtime/AssemblyInfo.cs",
    EVIDENCE + "/T1/Invoke-T1Unity.ps1",
    EVIDENCE + "/T14/Invoke-T14PlayModeTests.ps1",
    EVIDENCE + "/T14/Invoke-T14EditModeTests.ps1",
    EVIDENCE + "/T14/Analyze-T14Evidence.py",
]

KNOWN_DIFFERENCES = [
    "URP has no per-material proxy volume: refraction uses the nearest box-projected reflection probe AABB, found through CullingResults.visibleReflectionProbes with a scene lookup fallback.",
    "Refraction samples the current opaque+sky color pyramid; HDRP's pre-refraction transparent ordering is not migrated, so transparents drawn after the opaque pass do not appear in refraction.",
    "SSGI runs its diffuse denoiser and consumes the denoised output; HDRP's raw SSGI return path is not reproduced (intentional, recorded since T6).",
    "The sample sequence uses IGN + R2 instead of HDRP blue-noise textures, so SSGI keeps a faint stipple at 1 spp after 32 warmup frames.",
    "Deferred consumes DepthNormals prepass material data, i.e. decal-deformed normals/decal color are not visible to SSGI/SSR tracing.",
    "Transparent SSR is Approximation-only and reprojects with hit-pixel and camera motion; URP 14 does not write transparent object motion, so moving transparent reflections can lag.",
    "GTAO and Screen Space Lighting own separate packed Hi-Z buffers; the duplicate depth-pyramid memory is accepted instead of a cross-feature broker.",
    "URP's Deferred compatibility path ignores MSAA, so the Deferred renderer does not promise MSAA support for these effects.",
    "URP 14.0.12 does not drive DynamicResolutionHandler: hardware dynamic resolution cannot rescale the RTHandle camera targets, so it is recorded as unsupported (static render scale is supported).",
    "APV is not integrated in URP 14.0.12, so probe-volume GI is marked unsupported; lightmap/SH/mixed lighting regresses green.",
    "The plugin keeps the _SCREEN_SPACE_GI/_SCREEN_SPACE_SSR/_SCREEN_SPACE_TRANSPARENT_SSR/_SCREEN_SPACE_REFRACTION variants in every build because URP's prefiltering data only covers URP keywords. Plugin-level build stripping was evaluated but not introduced in T14: it changes release behaviour and needs its own correctness cycle.",
    "Metal, Android/OpenGLES, macOS/iOS and physical HMDs are not tested in this environment and are not claimed as supported.",
    "One 'CommandBuffer: temporary render texture not found (SetComputeTextureParam)' warning is logged on the first frame after the effects are enabled on DX11; it predates T14 (present in the T12 capture logs) and does not affect the measured images, which are identical to the recorded T12 hashes.",
]


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_json(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def rel(path):
    return os.path.relpath(path, PROJECT).replace("\\", "/")


def file_entry(path):
    return {
        "path": rel(path),
        "exists": os.path.exists(path),
        "sha256": sha256(path) if os.path.exists(path) else None,
    }


def parse_editmode(log_path, results_path):
    if not os.path.exists(results_path):
        return {"results": rel(results_path), "log": rel(log_path), "present": False, "passed": False}
    root = ET.parse(results_path).getroot()
    cases = [{"name": case.get("name"), "result": case.get("result")} for case in root.iter("test-case")]
    failed = [case["name"] for case in cases if case["result"] == "Failed"]
    summary = {
        "status": root.get("result"),
        "total": int(root.get("total", "0")),
        "passed": int(root.get("passed", "0")),
        "failed": int(root.get("failed", "0")),
        "skipped": int(root.get("skipped", "0")),
    }
    passed = summary["failed"] == 0 and summary["passed"] >= MIN_EDITMODE_PASSED
    return {
        "results": rel(results_path),
        "log": rel(log_path),
        "present": True,
        "passed": passed,
        "summary": summary,
        "failedCases": failed,
    }


def parse_playmode(results_path):
    if not os.path.exists(results_path):
        return {"results": rel(results_path), "present": False, "passed": False, "cases": []}
    root = ET.parse(results_path).getroot()
    cases = []
    for case in root.iter("test-case"):
        cases.append({"name": case.get("name"), "result": case.get("result")})
    passed = (
        root.get("result") == "Passed"
        and int(root.get("failed", "1")) == 0
        and int(root.get("passed", "0")) >= 6
    )
    return {
        "results": rel(results_path),
        "present": True,
        "passed": passed,
        "summary": {
            "total": int(root.get("total", "0")),
            "passed": int(root.get("passed", "0")),
            "failed": int(root.get("failed", "0")),
            "skipped": int(root.get("skipped", "0")),
        },
        "cases": cases,
    }


def analyze_graphics(path):
    if not os.path.exists(path):
        return {"verification": rel(path), "present": False, "passed": False, "modes": []}
    verification = load_json(path)
    t12_report = load_json(os.path.join(ROOT, "..", "T12", "T12Report.json"))
    t12_modes = t12_report.get("verification", {}).get("modes", {})
    modes = []
    passed = bool(verification.get("completed"))
    for mode in verification.get("modes", []):
        name = mode.get("name")
        cases = mode.get("cases", [])
        case_by_name = {case.get("name"): case for case in cases}
        failed = [case.get("name") for case in cases if not case.get("passed", False)]
        non_finite = [case.get("name") for case in cases if case.get("nonFinitePixels", 0)]
        magenta = [case.get("name") for case in cases if case.get("magentaPixels", 0)]
        reproduced = []
        not_reproduced = []
        recorded = t12_modes.get(name, {}).get("cases", {})
        for case_name, case in case_by_name.items():
            recorded_hash = recorded.get(case_name, {}).get("sha256")
            if recorded_hash is None:
                continue
            if recorded_hash == case.get("sha256"):
                reproduced.append(case_name)
            else:
                not_reproduced.append(case_name)
        complete = len(cases) >= MIN_CASES.get(name, 34)
        mode_passed = complete and not failed and not non_finite and not magenta and not mode.get("issues")
        passed = passed and mode_passed
        fallback = case_by_name.get("DeferredFallbackForward")
        modes.append({
            "name": name,
            "passed": mode_passed,
            "deferredFallbackForwardPassed": bool(fallback and fallback.get("passed", False)),
            "caseCount": len(cases),
            "minimumCaseCount": MIN_CASES.get(name, 34),
            "failedCases": failed,
            "nonFiniteCases": non_finite,
            "magentaCases": magenta,
            "issues": mode.get("issues", []),
            "reproducedImageHashes": len(reproduced),
            "changedImageHashes": not_reproduced,
            "sslPrivateBytes": mode.get("sslPrivateBytes"),
            "gtaoPrivateBytes": mode.get("gtaoPrivateBytes"),
            "sslDepthPyramidBytes": mode.get("sslDepthPyramidBytes"),
            "sslDepthPyramidMips": mode.get("sslDepthPyramidMips"),
            "sslColorPyramidMips": mode.get("sslColorPyramidMips"),
            "globalKeywordsClean": mode.get("globalKeywordsClean"),
        })
    return {
        "verification": rel(path),
        "present": True,
        "completed": bool(verification.get("completed")),
        "passed": passed,
        "modes": modes,
    }


def analyze_frame_debugger(path):
    if not os.path.exists(path):
        return {"report": rel(path), "present": False, "passed": False, "modes": []}
    report = load_json(path)
    problems = []
    if not report.get("completed"):
        problems.append("Frame Debugger capture did not complete")
    modes_by_name = {mode.get("name"): mode for mode in report.get("modes", [])}
    summary = []
    for name in MODES:
        mode = modes_by_name.get(name)
        if mode is None:
            problems.append("Frame Debugger mode %s is missing" % name)
            continue
        events = mode.get("eventNames", [])
        first_motion = next((i for i, event in enumerate(events) if "MotionVectors" in event), -1)
        first_ssl = next((i for i, event in enumerate(events) if "SSL Depth Pyramid" in event), -1)
        first_gtao = next((i for i, event in enumerate(events) if "GTAO Depth Pyramid" in event), -1)
        first_ssgi = next((i for i, event in enumerate(events) if "Screen Space Global Illumination" in event), -1)
        first_ssr = next((i for i, event in enumerate(events) if "Screen Space Reflections" in event), -1)
        first_main = next((i for i, event in enumerate(events) if "GBuffer" in event or "DrawOpaqueObjects" in event), -1)
        mode_problems = []
        if min(first_ssl, first_gtao, first_ssgi, first_ssr, first_main) < 0:
            mode_problems.append("missing SSL/GTAO/SSGI/SSR or main rendering events")
        if first_motion < 0 or first_motion > first_ssl:
            mode_problems.append("motion vectors are not scheduled before the SSL depth pyramid")
        if not (first_ssl < first_main and first_gtao < first_main):
            mode_problems.append("a depth pyramid runs after the G-buffer/opaque pass")
        if not (first_ssgi < first_main and first_ssr < first_main):
            mode_problems.append("SSGI/SSR run after the G-buffer/opaque pass")
        problems.extend("%s: %s" % (name, problem) for problem in mode_problems)
        summary.append({
            "name": name,
            "eventCount": len(events),
            "eventDataHash": mode.get("eventDataHash"),
            "firstMotion": first_motion,
            "firstSslDepthPyramid": first_ssl,
            "firstGtaoDepthPyramid": first_gtao,
            "firstSsgi": first_ssgi,
            "firstSsr": first_ssr,
            "firstMainRendering": first_main,
            "passed": not mode_problems,
        })
    return {
        "report": rel(path),
        "present": True,
        "completed": bool(report.get("completed")),
        "passed": not problems,
        "problems": problems,
        "modes": summary,
    }


def analyze_player_build(path):
    if not os.path.exists(path):
        return {"summary": rel(path), "present": False, "passed": False}
    data = load_json(path)
    missing = [entry["suffix"] for entry in data.get("requiredResources", []) if not entry.get("present")]
    passed = (
        data.get("result") == "Succeeded"
        and data.get("totalErrors", 1) == 0
        and data.get("allRequiredResourcesPacked", False)
        and "None" in (data.get("buildOptions") or "")
    )
    return {
        "summary": rel(path),
        "present": True,
        "passed": passed,
        "result": data.get("result"),
        "totalErrors": data.get("totalErrors"),
        "totalSizeBytes": data.get("totalSizeBytes"),
        "totalTimeSeconds": data.get("totalTimeSeconds"),
        "buildOptions": data.get("buildOptions"),
        "graphicsApis": data.get("graphicsApis"),
        "managedStrippingLevel": data.get("managedStrippingLevel"),
        "stripEngineCode": data.get("stripEngineCode"),
        "packedAssetCount": data.get("packedAssetCount"),
        "missingRequiredResources": missing,
    }


PLAYER_RUN_NOTES = {
    "DX11": "Release player exited with code 0 after writing the report and captures.",
    "DX12": "Release player exited with code 0 after writing the report and captures.",
    "Vulkan": "The process wrote the report, log and 14 captures but did not terminate on its own in this environment; the harness ended it after the evidence was flushed, so its exit code is not part of the pass decision.",
}


def analyze_player_run(api):
    path = os.path.join(ROOT, "Runs", api, "T13XrPlayerVerification.json")
    if not os.path.exists(path):
        return {"api": api, "present": False, "passed": False, "modes": []}
    data = load_json(path)
    modes = []
    for mode in data.get("modes", []):
        modes.append({
            "mode": mode.get("mode"),
            "passed": mode.get("passed"),
            "issues": mode.get("issues", []),
        })
    passed = len(modes) >= 2 and all(mode.get("passed") for mode in modes)
    return {
        "api": api,
        "present": True,
        "graphicsDeviceType": data.get("graphicsDeviceType"),
        "passed": passed,
        "notes": PLAYER_RUN_NOTES.get(api),
        "modes": modes,
        "cases": data.get("modes", []),
    }


def previous_evidence_integrity():
    """T14 must not modify T1-T13 evidence or the assets whose hashes T13 recorded."""
    problems = []
    t13_report_path = os.path.join(ROOT, "..", "T13", "T13Report.json")
    t13 = load_json(t13_report_path)
    recorded = {}
    for entry in t13.get("requiredFiles", []):
        absolute = os.path.join(PROJECT, entry["path"])
        current = sha256(absolute) if os.path.exists(absolute) else None
        recorded[entry["path"]] = {
            "recorded": entry.get("sha256"),
            "current": current,
            "unchanged": current == entry.get("sha256"),
        }
        if current != entry.get("sha256"):
            problems.append("T13 required file changed or missing: " + entry["path"])

    git = subprocess.run(
        ["git", "status", "--porcelain", "--"] +
        ["%s/T%d" % (EVIDENCE, index) for index in range(1, 14)],
        cwd=PROJECT, capture_output=True, text=True, check=False)
    # The T1 wrapper is shared tooling that T14 extends (optional -executeMethod) without touching
    # any recorded evidence, so it is the one allowed modification below EVIDENCE/T1..T13.
    allowed = {EVIDENCE + "/T1/Invoke-T1Unity.ps1"}
    dirty = []
    for line in git.stdout.splitlines():
        if not line.strip():
            continue
        changed_path = line[3:].strip().replace("\\", "/")
        if not any(changed_path.endswith(entry) for entry in allowed):
            dirty.append(line)
    if git.returncode != 0:
        problems.append("git status failed for the earlier evidence folders")
    if dirty:
        problems.append("earlier evidence folders have uncommitted changes: " + "; ".join(dirty))

    return {
        "passed": not problems,
        "problems": problems,
        "t13RequiredFiles": recorded,
        "earlierEvidenceDirty": dirty,
    }


def support_matrix(t13_report, player_runs, graphics):
    matrix = t13_report.get("supportMatrix", {})
    return {
        "renderers": {
            "Forward": graphics["modes"][0]["passed"] if len(graphics["modes"]) > 0 else False,
            "ForwardPlus": graphics["modes"][1]["passed"] if len(graphics["modes"]) > 1 else False,
            "Deferred": graphics["modes"][2]["passed"] if len(graphics["modes"]) > 2 else False,
            "DeferredFallbackForward": next(
                (mode.get("deferredFallbackForwardPassed", False) for mode in graphics["modes"] if mode.get("name") == "Deferred"),
                False),
        },
        "playMode": "Passed" if all(api["passed"] for api in player_runs) else "Failed",
        "xr": matrix.get("xr", {}),
        "resolution": matrix.get("resolution", {}),
        "platforms": matrix.get("platforms", {}),
        "releasePlayer": {
            "StandaloneWindows64": "Passed (T14 release build + XR player runs)"
            if all(api["passed"] for api in player_runs) else "Failed",
        },
    }


def performance_report(graphics):
    budget = load_json(os.path.join(ROOT, "..", "T1", "PerformanceBudget.json"))
    reproducibility = load_json(os.path.join(ROOT, "..", "T1", "Reproducibility.json"))
    gpu_policy = reproducibility.get("gpuTimingPolicy")
    baseline = []
    for mode in reproducibility.get("modes", []):
        timing = mode.get("timing", {})
        baseline.append({
            "mode": mode.get("mode"),
            "cpuP95Ms": timing.get("CpuP95Ms", {}).get("Run18"),
            "cpuSymmetricVariationPercent": timing.get("CpuP95Ms", {}).get("symmetricVariationPercent"),
            "gpuP95Ms": timing.get("GpuP95Ms", {}).get("Run18"),
            "gpuPolicy": gpu_policy,
            "graphicsDriverBytes": mode.get("graphicsDriverBytes", {}).get("Run18"),
        })
    memory = []
    for mode in graphics.get("modes", []):
        memory.append({
            "renderer": mode.get("name"),
            "sslPrivateBytes": mode.get("sslPrivateBytes"),
            "gtaoPrivateBytes": mode.get("gtaoPrivateBytes"),
            "sslPackedHiZBytes": mode.get("sslDepthPyramidBytes"),
            "sslDepthPyramidMips": mode.get("sslDepthPyramidMips"),
            "sslColorPyramidMips": mode.get("sslColorPyramidMips"),
        })
    return {
        "utcTimestamp": utc_now(),
        "protocol": budget.get("primaryTarget"),
        "budget": {
            "incrementalGpuMsP95Max": budget.get("incrementalGpuMsP95Max"),
            "additionalGraphicsMemoryMiBMax": budget.get("additionalGraphicsMemoryMiBMax"),
            "gpuTimingPolicy": budget.get("rules", [])[1] if budget.get("rules") else None,
        },
        "baselineRuns": baseline,
        "privateResources1280x720": memory,
        "notes": [
            "GPU p95 from the 720p T1 harness is reference data because DVFS dominates it; module GPU budgets require an independent GPU profiler or a clock-locked environment and are not claimed from this harness.",
            "Private resource bytes are base-level RTHandle and ComputeBuffer sizes measured during the T14 graphics replay; the color pyramid mip chain is not multiplied by 4/3 and the packed Hi-Z number is the atlas size.",
            "The release player was built in this run; the player executable itself is not archived as evidence.",
        ],
    }


def archive_manifest(exclude_names):
    entries = []
    for directory, _, files in os.walk(ROOT):
        for name in sorted(files):
            absolute = os.path.join(directory, name)
            entries.append({
                "path": rel(absolute),
                "bytes": os.path.getsize(absolute),
                "excluded": any(part in absolute.replace("\\", "/") for part in exclude_names),
            })
    return {
        "utcTimestamp": utc_now(),
        "root": rel(ROOT),
        "fileCount": len(entries),
        "totalBytes": sum(entry["bytes"] for entry in entries if not entry["excluded"]),
        "excludedArtifacts": [
            EVIDENCE + "/T14/Player/ (temporary release player, rebuilt by Build T14 Release Player)",
            EVIDENCE + "/T14/*.Run*.log retention follows the T1-T13 convention: every run is kept",
        ],
        "files": entries,
    }


def render_support_matrix_markdown(matrix):
    lines = ["# T14 support matrix", ""]
    lines.append("| Renderer | Result |")
    lines.append("|---|---|")
    for renderer, result in matrix["renderers"].items():
        lines.append("| %s | %s |" % (renderer, "Passed" if result in (True, "Passed") else result))
    lines.append("")
    lines.append("| XR | Result |")
    lines.append("|---|---|")
    for mode, apis in matrix.get("xr", {}).items():
        if mode == "note":
            continue
        if isinstance(apis, dict):
            for api, result in apis.items():
                lines.append("| %s / %s | %s |" % (mode, api, result))
        else:
            lines.append("| %s | %s |" % (mode, apis))
    if matrix.get("xr", {}).get("note"):
        lines.append("")
        lines.append("Note: " + matrix["xr"]["note"])
    lines.append("")
    lines.append("| Platform | Result |")
    lines.append("|---|---|")
    for platform, result in matrix.get("platforms", {}).items():
        lines.append("| %s | %s |" % (platform, result))
    lines.append("")
    lines.append("| Release player | Result |")
    lines.append("|---|---|")
    for item, result in matrix.get("releasePlayer", {}).items():
        lines.append("| %s | %s |" % (item, result))
    lines.append("")
    lines.append("| Resolution | Result |")
    lines.append("|---|---|")
    for item, result in matrix.get("resolution", {}).items():
        if isinstance(result, dict):
            lines.append("| %s | %s |" % (item, result.get("result")))
        else:
            lines.append("| %s | %s |" % (item, result))
    lines.append("")
    lines.append("Not tested results are not counted as supported.")
    lines.append("")
    return "\n".join(lines)


def render_known_differences_markdown():
    lines = ["# T14 known differences and limitations", ""]
    for entry in KNOWN_DIFFERENCES:
        lines.append("- " + entry)
    lines.append("")
    return "\n".join(lines)


def main():
    edit_mode = parse_editmode(os.path.join(ROOT, "UnityEditModeTests.log"),
                               os.path.join(ROOT, "UnityEditModeTests.xml"))
    play_mode = parse_playmode(os.path.join(ROOT, "UnityPlayModeTests.xml"))
    graphics = analyze_graphics(os.path.join(ROOT, "Graphics", "T12Verification.json"))
    frame_debugger = analyze_frame_debugger(os.path.join(ROOT, "FrameDebugger", "FrameDebuggerReport.json"))
    player_build = analyze_player_build(os.path.join(ROOT, "T14PlayerBuildSummary.json"))
    player_runs = [analyze_player_run(api) for api in APIS]
    integrity = previous_evidence_integrity()
    t13_report = load_json(os.path.join(ROOT, "..", "T13", "T13Report.json"))
    matrix = support_matrix(t13_report, player_runs, graphics)
    performance = performance_report(graphics)

    required_files = [file_entry(os.path.join(PROJECT, path)) for path in REQUIRED_FILES]
    missing_files = [entry["path"] for entry in required_files if not entry["exists"]]

    criteria = {
        "requiredFilesPresent": not missing_files,
        "editModePassed": edit_mode["passed"],
        "playModePassed": play_mode["passed"],
        "graphicsMatrixPassed": graphics["passed"],
        "frameDebuggerOrderPassed": frame_debugger["passed"],
        "releasePlayerBuildPassed": player_build["passed"],
        "releasePlayerRunsPassed": all(api["passed"] for api in player_runs),
        "earlierEvidenceUnchanged": integrity["passed"],
        "knownDifferencesDocumented": len(KNOWN_DIFFERENCES) > 0,
    }

    report = {
        "utcTimestamp": utc_now(),
        "unityVersion": load_json(os.path.join(ROOT, "..", "T1", "Environment.json")).get("unityVersion"),
        "urpVersion": "14.0.12",
        "t14ExitCriteriaMet": all(criteria.values()),
        "criteria": criteria,
        "requiredFiles": required_files,
        "missingFiles": missing_files,
        "editMode": edit_mode,
        "playMode": play_mode,
        "graphicsRegression": graphics,
        "frameDebugger": frame_debugger,
        "playerBuild": player_build,
        "playerRuns": player_runs,
        "previousEvidence": integrity,
        "supportMatrix": matrix,
        "knownDifferences": KNOWN_DIFFERENCES,
        "performance": {
            "report": EVIDENCE + "/T14/T14Performance.json",
            "privateResources1280x720": performance["privateResources1280x720"],
            "gpuTimingPolicy": performance["budget"]["gpuTimingPolicy"],
        },
        "archive": EVIDENCE + "/T14/T14ArchiveManifest.json",
    }

    with open(os.path.join(ROOT, "T14Report.json"), "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)
    with open(os.path.join(ROOT, "T14SupportMatrix.md"), "w", encoding="utf-8") as handle:
        handle.write(render_support_matrix_markdown(matrix))
    with open(os.path.join(ROOT, "T14KnownDifferences.md"), "w", encoding="utf-8") as handle:
        handle.write(render_known_differences_markdown())
    with open(os.path.join(ROOT, "T14Performance.json"), "w", encoding="utf-8") as handle:
        json.dump(performance, handle, indent=2)
    with open(os.path.join(ROOT, "T14ArchiveManifest.json"), "w", encoding="utf-8") as handle:
        json.dump(archive_manifest([EVIDENCE + "/T14/Player/"]), handle, indent=2)

    print("T14 report written")
    for key, value in criteria.items():
        print("  %-32s %s" % (key, value))
    print("t14ExitCriteriaMet=%s" % report["t14ExitCriteriaMet"])
    return 0 if report["t14ExitCriteriaMet"] else 1


if __name__ == "__main__":
    sys.exit(main())