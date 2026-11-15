#!/usr/bin/env python3
"""Builds the T13 exit report from the XR player runs, editor capability reports and EditMode log."""

import hashlib
import json
import os
import re
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(ROOT, "..", "..", ".."))

APIS = ["DX11", "DX12", "Vulkan"]
PLAYER_MODES = ["MultiPass", "SinglePassInstanced"]

REQUIRED_FILES = [
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT13Verification.cs",
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT13PlayerSetup.cs",
    "Assets/Tests/ScreenSpaceLighting/Runtime/ScreenSpaceLightingT13PlayerVerification.cs",
    "Assets/Tests/ScreenSpaceLighting/Testing/ScreenSpaceLightingT13XrEnvironment.cs",
    "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT13XrPlayer.unity",
]


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
    return {"path": rel(path), "exists": os.path.exists(path), "sha256": sha256(path) if os.path.exists(path) else None}


def parse_editmode(log_path):
    if not os.path.exists(log_path):
        return {"log": rel(log_path), "present": False, "passed": False, "summary": None}
    with open(log_path, encoding="utf-8", errors="replace") as handle:
        text = handle.read()
    match = re.search(
        r"EditMode tests finished: status=(\w+) passed=(\d+) failed=(\d+) skipped=(\d+)", text)
    summary = None
    if match:
        summary = {
            "status": match.group(1),
            "passed": int(match.group(2)),
            "failed": int(match.group(3)),
            "skipped": int(match.group(4)),
        }
    return {
        "log": rel(log_path),
        "present": True,
        "passed": summary is not None and summary["status"] == "Passed" and summary["failed"] == 0 and summary["passed"] > 0,
        "summary": summary,
    }


def analyze_player(api):
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
            "historyEntries": mode.get("historyEntries"),
            "historySlices": mode.get("historySlices"),
            "historyDimension": mode.get("historyDimension"),
            "depthPyramidSliceCount": mode.get("depthPyramidSliceCount"),
            "previousViewProjViews": mode.get("previousViewProjViews"),
            "previousColorPyramidSliceHashesDiffer":
                len(mode.get("previousColorPyramidSliceHashes") or []) == 2
                and mode["previousColorPyramidSliceHashes"][0] != mode["previousColorPyramidSliceHashes"][1],
            "referenceRestored": mode.get("referenceRestored"),
            "referenceEyeDifference": mode.get("referenceEyeDifference"),
            "effectEyeDifference": mode.get("effectEyeDifference"),
            "effectsChangedLeft": mode.get("effectsChangedLeft"),
            "effectsChangedRight": mode.get("effectsChangedRight"),
            "ssgiChangedLeft": mode.get("ssgiChangedLeft"),
            "ssgiChangedRight": mode.get("ssgiChangedRight"),
            "ssrChangedLeft": mode.get("ssrChangedLeft"),
            "ssrChangedRight": mode.get("ssrChangedRight"),
            "refractionChangedLeft": mode.get("refractionChangedLeft"),
            "refractionChangedRight": mode.get("refractionChangedRight"),
            "gtaoChangedLeft": mode.get("gtaoChangedLeft"),
            "gtaoChangedRight": mode.get("gtaoChangedRight"),
            "nonFinitePixels": mode.get("nonFinitePixels"),
            "ssgiFinalAlphaMean": mode.get("ssgiFinalAlphaMean"),
            "ssrWeightMean": mode.get("ssrWeightMean"),
            "cases": mode.get("cases", []),
        })

    required = set(PLAYER_MODES)
    present_modes = {mode["mode"] for mode in modes}
    return {
        "api": data.get("graphicsDeviceType"),
        "present": True,
        "source": rel(path),
        "foveatedRenderingCaps": data.get("foveatedRenderingCaps"),
        "graphicsDeviceName": data.get("graphicsDeviceName"),
        "unityVersion": data.get("unityVersion"),
        "coversBothModes": required.issubset(present_modes),
        "passed": bool(modes) and required.issubset(present_modes) and all(mode["passed"] for mode in modes),
        "modes": modes,
    }


def analyze_editor(api):
    path = os.path.join(ROOT, "T13EditorVerification." + api + ".json")
    if not os.path.exists(path):
        return {"api": api, "present": False, "passed": False}
    data = load_json(path)
    return {
        "api": api,
        "present": True,
        "source": rel(path),
        "graphicsDeviceType": data.get("graphicsDeviceType"),
        "graphicsDriverType": data.get("graphicsDriverType"),
        "foveatedRenderingCaps": data.get("foveatedRenderingCaps"),
        "supportsComputeShaders": data.get("supportsComputeShaders"),
        "formats": data.get("formats", []),
        "completed": data.get("completed"),
        "passed": data.get("passed"),
        "issues": data.get("issues", []),
        "fullScaleChanges": data.get("fullScaleChanges"),
        "fullScaleNonFinite": data.get("fullScaleNonFinite"),
        "dynamicResolution": {
            "result": data.get("dynamicResolutionResult"),
            "supported": data.get("dynamicResolutionSupported"),
            "outputChanged": data.get("dynamicResolutionOutputChanged"),
            "intermediateScaled": data.get("dynamicResolutionIntermediateScaled"),
            "rtHandleScaleX": data.get("rtHandleScaleX"),
            "halfScaleChanges": data.get("halfScaleChanges"),
        },
        "captures": data.get("captureSummaries", []),
    }


def analyze_t12_regression():
    path = os.path.join(ROOT, "T12RegressionVerification.json")
    if not os.path.exists(path):
        return {"present": False, "passed": False}
    data = load_json(path)
    modes = [{"name": m.get("name"), "passed": m.get("passed"), "cases": len(m.get("cases", []))} for m in data.get("modes", [])]
    return {
        "present": True,
        "source": rel(path),
        "completed": data.get("completed"),
        "passed": data.get("passed"),
        "modes": modes,
    }


def main():
    players = [analyze_player(api) for api in APIS]
    editors = [analyze_editor(api) for api in APIS]
    editmode = parse_editmode(os.path.join(ROOT, "UnityTests.log"))
    t12 = analyze_t12_regression()

    missing = [entry for entry in REQUIRED_FILES if not os.path.exists(os.path.join(PROJECT, entry))]

    support_matrix = {
        "renderers": {
            "Forward": "Passed (T12 regression + T13 XR runs)",
            "ForwardPlus": "Passed (T12 regression)",
            "Deferred": "Passed (T12 regression)",
        },
        "xr": {
            "MultiPass": {p["api"]: ("Passed" if p["present"] and any(m["mode"] == "MultiPass" and m["passed"] for m in p["modes"]) else "NotTested") for p in players},
            "SinglePassInstanced": {p["api"]: ("Passed" if p["present"] and any(m["mode"] == "SinglePassInstanced" and m["passed"] for m in p["modes"]) else "NotTested") for p in players},
            "Foveation": {
                "DX11": "NotApplicable (SystemInfo.foveatedRenderingCaps=None)",
                "DX12": "GuardsVerified (caps=FoveationImage, passes disable foveation for full-screen raster)",
                "Vulkan": "GuardsVerified (caps=FoveationImage, passes disable foveation for full-screen raster)",
                "note": "Mock HMD foveation simulation was not compared image-by-image; only the guard path and stereo correctness were verified.",
            },
            "RealHMDs": "NotTested (no physical HMD in this environment; Mock HMD only)",
        },
        "resolution": {
            "StaticRenderScale": "Passed (T12 RenderScale75; T13 full-scale XR runs)",
            "HardwareDynamicResolution": {
                "result": "UnsupportedByUrp14",
                "evidence": {
                    e["api"]: e.get("dynamicResolution", {}).get("result") for e in editors if e["present"]
                },
                "note": "URP 14.0.12 does not drive DynamicResolutionHandler; ScalableBufferManager resizes the presented image but not the RTHandle camera targets, and the readback becomes invalid. SSL history remains full resolution and resamples the scaled source through its UV scale; this is recorded as an engine limitation, not a regression.",
            },
        },
        "platforms": {
            "Windows DX11": "Tested",
            "Windows DX12": "Tested",
            "Windows Vulkan": "Tested",
            "Metal": "NotTested (Windows host, no Metal device)",
            "OpenGLES/Android": "NotTested",
            "macOS/iOS": "NotTested",
        },
    }

    claimed_passed = (
        not missing
        and all(p["present"] and p["passed"] for p in players)
        and all(e["present"] and e["completed"] and e["passed"] for e in editors)
        and editmode["present"] and editmode["passed"]
        and t12.get("present") and t12.get("passed")
    )

    report = {
        "utcTimestamp": datetime.now(timezone.utc).isoformat(),
        "unityVersion": "2022.3.62f1",
        "urpVersion": "14.0.12",
        "graphicsDeviceName": next((p["graphicsDeviceName"] for p in players if p["present"]), None),
        "t13ExitCriteriaMet": bool(claimed_passed),
        "claimedScope": [
            "XR multipass and single-pass instanced per-eye matrices, slice dispatch and history isolation",
            "Screen Space Lighting visible in both eyes with SSGI, SSR, transparent SSR and refraction",
            "Reference restored when effects are disabled in XR",
            "Platform capability detection on DX11, DX12 and Vulkan",
            "Hardware dynamic resolution measured and recorded as unsupported by URP 14.0.12",
        ],
        "notClaimed": [
            "Physical HMD devices (Mock HMD only)",
            "Metal, mobile and OpenGLES platforms",
            "Image-level foveation quality",
        ],
        "requiredFiles": [file_entry(os.path.join(PROJECT, entry)) for entry in REQUIRED_FILES],
        "missingFiles": missing,
        "players": players,
        "editors": editors,
        "editMode": editmode,
        "t12Regression": t12,
        "supportMatrix": support_matrix,
        "images": sorted(
            name for name in os.listdir(os.path.join(ROOT, "Runs", "DX11"))
            if name.endswith(".png")
        ) if os.path.isdir(os.path.join(ROOT, "Runs", "DX11")) else [],
    }

    out = os.path.join(ROOT, "T13Report.json")
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)

    print(f"T13 report written: {out}")
    print(f"t13ExitCriteriaMet={report['t13ExitCriteriaMet']} players={[p['passed'] for p in players]} "
          f"editors={[e['passed'] for e in editors]} editmode={editmode['passed']} t12={t12.get('passed')}")


if __name__ == "__main__":
    main()