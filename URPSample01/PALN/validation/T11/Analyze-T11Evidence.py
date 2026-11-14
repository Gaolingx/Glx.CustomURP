#!/usr/bin/env python3
"""Builds the T11 exit report from the Unity refraction verification and EditMode log."""

import hashlib
import json
import os
import re
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(ROOT, "..", "..", ".."))
URP = "Packages/com.unity.render-pipelines.universal@14.0.12"

REQUIRED_ASSETS = [
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT11Verification.cs",
    "Assets/Tests/ScreenSpaceLighting/Shaders/SslT11Lit.shadergraph",
    "Assets/Tests/ScreenSpaceLighting/Materials/T11RefractionBox.mat",
    "Assets/Tests/ScreenSpaceLighting/Materials/T11RefractionSphere.mat",
    "Assets/Tests/ScreenSpaceLighting/Materials/T11RefractionThin.mat",
    "Assets/Tests/ScreenSpaceLighting/Materials/T11RefractionNone.mat",
    "Assets/Tests/ScreenSpaceLighting/Materials/T11RefractionTint.mat",
    "Assets/Tests/ScreenSpaceLighting/Materials/T11RefractionComplexLit.mat",
    "Assets/Tests/ScreenSpaceLighting/Materials/T11RefractionShaderGraph.mat",
    "Assets/Tests/ScreenSpaceLighting/Materials/T11LegacyTransparent.mat",
    URP + "/ShaderLibrary/ScreenSpaceLighting/ScreenSpaceRefraction.hlsl",
    URP + "/Shaders/Lit.shader",
    URP + "/Shaders/ComplexLit.shader",
    URP + "/Shaders/LitInput.hlsl",
    URP + "/Shaders/LitForwardPass.hlsl",
    URP + "/Editor/ShaderGraph/Includes/PBRForwardPass.hlsl",
    URP + "/Editor/ShaderGraph/Includes/DepthNormalsOnlyPass.hlsl",
    URP + "/Editor/ShaderGraph/Targets/UniversalLitSubTarget.cs",
    URP + "/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceRefraction.cs",
    URP + "/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/RefractionPass.cs",
    URP + "/Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingRefractionTests.cs",
    URP + "/Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingMaterialTests.cs",
]

MODE_NAMES = ["Forward", "ForwardPlus", "Deferred"]
MODE_VIEWS = [
    "Reference",
    "RefractionOn",
    "ModelsNone",
    "Intensity0",
    "OffAgain",
    "NoProxyFallback",
    "Tinted",
    "BackdropMoved",
    "TransparentPanelOff",
    "TransparentPanelOn",
    "CurrentColorPyramidPanelOff",
    "CurrentColorPyramidPanelOn",
    "ShaderGraphTransparentSsr",
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
    on = require("RefractionOn")
    none_state = require("ModelsNone")
    intensity_zero = require("Intensity0")
    off_again = require("OffAgain")
    no_proxy = require("NoProxyFallback")

    if none_state is not None and not none_state.get("identicalToReference", False):
        problems.append(
            "refraction model None changed the image by "
            f"{none_state.get('differingPixels')} pixels; the old transparent path was not preserved"
        )

    if intensity_zero is not None and not intensity_zero.get("identicalToReference", False):
        problems.append(
            "refraction intensity 0 differs from the reference by "
            f"{intensity_zero.get('differingPixels')} pixels"
        )

    if off_again is not None and not off_again.get("identicalToReference", False):
        problems.append(
            f"refraction off again differs from the reference by {off_again.get('differingPixels')} pixels"
        )

    if no_proxy is not None and not no_proxy.get("identicalToReference", False):
        problems.append(
            "without a box-projected probe the refraction did not fall back to the reference by "
            f"{no_proxy.get('differingPixels')} pixels"
        )

    if on is not None and on.get("differingPixels", 0) <= 0:
        problems.append("refraction on is identical to the reference")

    if mode.get("boxChangedPixels", 0) <= 0:
        problems.append("the box refraction material did not change")
    if mode.get("legacyChangedPixels", 1) != 0:
        problems.append("a transparent material without a refraction model changed")
    if mode.get("complexChangedPixels", 0) <= 0:
        problems.append("the Complex Lit refraction material did not change")
    if mode.get("sphereChangedPixels", 0) <= 0:
        problems.append("the sphere refraction model did not change the image")
    if mode.get("shaderGraphChangedPixels", 0) <= 0:
        problems.append("the ShaderGraph Lit refraction material did not change")
    if mode.get("shaderGraphTransparentSsrChangedPixels", 0) <= 0:
        problems.append("the ShaderGraph transparent prepass did not feed Transparent SSR")

    if not mode.get("tintApplied", False):
        problems.append("transmittance absorption did not tint the transmitted light")
    if not mode.get("currentFramePyramidFollowsScene", False):
        problems.append("the transmitted color did not follow the current frame scene")

    ordering = mode.get("preRefractionDifference") or {}
    if ordering.get("panelChangedPixels", 0) <= 0:
        problems.append("the pre-refraction transparent panel was not visible")
    if ordering.get("glassChangedPixels", 1) != 0:
        problems.append("the refraction included a pre-refraction transparent surface")

    if not mode.get("limitations"):
        problems.append("the refraction limitations are not recorded")

    for state_name, state in states.items():
        if state.get("magentaPixels", 0):
            problems.append(f"{state_name} contains error-shader pixels")
        if state.get("nonFinitePixels", 0):
            problems.append(f"{state_name} contains non-finite pixels")

    return problems


def main():
    verification_path = os.path.join(ROOT, "T11Verification.json")
    report_path = os.path.join(ROOT, "T11Report.json")
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
            name = f"T11_{mode}_{view}.png"
            if not os.path.exists(os.path.join(ROOT, name)):
                missing.append(name)
            else:
                pngs[name] = sha256(os.path.join(ROOT, name))
        if missing:
            mode_problems[mode].append("missing captures: " + ", ".join(missing))

    criteria = {
        "verificationCompleted": bool(verification.get("completed")),
        "threePathsPassed": all(not mode_problems[name] for name in MODE_NAMES),
        "oldMaterialsUnchanged": all(
            state_map(modes.get(name, {})).get("ModelsNone", {}).get("identicalToReference", False)
            and modes.get(name, {}).get("legacyChangedPixels", 1) == 0
            for name in MODE_NAMES
        ),
        "intensityAndOffRestoreReference": all(
            state_map(modes.get(name, {})).get("Intensity0", {}).get("identicalToReference", False)
            and state_map(modes.get(name, {})).get("OffAgain", {}).get("identicalToReference", False)
            for name in MODE_NAMES
        ),
        "proxyFallbackRestoresProbePath": all(
            state_map(modes.get(name, {})).get("NoProxyFallback", {}).get("identicalToReference", False)
            for name in MODE_NAMES
        ),
        "currentFramePyramidOnly": all(
            modes.get(name, {}).get("currentFramePyramidFollowsScene", False)
            and (modes.get(name, {}).get("preRefractionDifference") or {}).get("glassChangedPixels", 1) == 0
            and (modes.get(name, {}).get("preRefractionDifference") or {}).get("pyramidExcludesTransparents", False)
            for name in MODE_NAMES
        ),
        "transmittanceApplied": all(modes.get(name, {}).get("tintApplied", False) for name in MODE_NAMES),
        "supportedMaterialsCovered": all(
            modes.get(name, {}).get("boxChangedPixels", 0) > 0
            and modes.get(name, {}).get("sphereChangedPixels", 0) > 0
            and modes.get(name, {}).get("complexChangedPixels", 0) > 0
            for name in MODE_NAMES
        ),
        "shaderGraphCovered": all(
            modes.get(name, {}).get("shaderGraphChangedPixels", 0) > 0
            and modes.get(name, {}).get("shaderGraphTransparentSsrChangedPixels", 0) > 0
            for name in MODE_NAMES
        ),
        "preRefractionOrderingRecorded": all(
            bool(modes.get(name, {}).get("limitations"))
            and (modes.get(name, {}).get("preRefractionDifference") or {}).get("panelChangedPixels", 0) > 0
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
                    "boxChangedPixels": modes.get(name, {}).get("boxChangedPixels"),
                    "legacyChangedPixels": modes.get(name, {}).get("legacyChangedPixels"),
                    "complexChangedPixels": modes.get(name, {}).get("complexChangedPixels"),
                    "sphereChangedPixels": modes.get(name, {}).get("sphereChangedPixels"),
                    "shaderGraphChangedPixels": modes.get(name, {}).get("shaderGraphChangedPixels"),
                    "shaderGraphTransparentSsrChangedPixels": modes.get(name, {}).get("shaderGraphTransparentSsrChangedPixels"),
                    "noProxyChangedPixels": modes.get(name, {}).get("noProxyChangedPixels"),
                    "tint": modes.get(name, {}).get("tint"),
                    "moveBeforeMean": modes.get(name, {}).get("moveBeforeMean"),
                    "moveAfterMean": modes.get(name, {}).get("moveAfterMean"),
                    "currentFramePyramidFollowsScene": modes.get(name, {}).get("currentFramePyramidFollowsScene"),
                    "preRefractionDifference": modes.get(name, {}).get("preRefractionDifference"),
                    "pyramidWithPanelChangedPixels": modes.get(name, {}).get("pyramidWithPanelChangedPixels"),
                    "panelDiagnostics": modes.get(name, {}).get("panelDiagnostics"),
                    "limitations": modes.get(name, {}).get("limitations"),
                }
                for name in MODE_NAMES
            },
        },
        "criteria": criteria,
        "t11ExitCriteriaMet": all(criteria.values()),
        "assets": assets,
        "missingMeta": missing_meta,
        "editMode": editmode,
        "captures": pngs,
    }

    with open(report_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)

    print(json.dumps({
        "t11ExitCriteriaMet": report["t11ExitCriteriaMet"],
        "criteria": criteria,
        "modeProblems": mode_problems,
    }, indent=2))
    if not report["t11ExitCriteriaMet"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()