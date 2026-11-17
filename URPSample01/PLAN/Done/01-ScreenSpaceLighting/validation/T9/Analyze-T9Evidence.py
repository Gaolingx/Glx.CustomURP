#!/usr/bin/env python3
"""Builds the T9 exit report from the Unity verification and EditMode log."""

import hashlib
import json
import os
import re
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(ROOT, "..", "..", "..", "..", ".."))
URP = "Packages/com.unity.render-pipelines.universal@14.0.12"

CORE_SHADERS = URP + "/ShaderLibrary/ScreenSpaceLighting"
REQUIRED_ASSETS = [
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT9Verification.cs",
    URP + "/ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingBlend.hlsl",
    URP + "/ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingInput.hlsl",
    URP + "/ShaderLibrary/GlobalIllumination.hlsl",
    URP + "/Shaders/Lit.shader",
    URP + "/Shaders/ComplexLit.shader",
    URP + "/Shaders/LitDepthNormalsPass.hlsl",
    URP + "/Shaders/SimpleLitDepthNormalsPass.hlsl",
    URP + "/Shaders/Particles/ParticlesDepthNormalsPass.hlsl",
    URP + "/Shaders/Terrain/TerrainLitDepthNormalsPass.hlsl",
    URP + "/Editor/ShaderGraph/Targets/UniversalTarget.cs",
    URP + "/Editor/ShaderGraph/Targets/UniversalLitSubTarget.cs",
    URP + "/Editor/ShaderGraph/Includes/PBRForwardPass.hlsl",
    URP + "/Editor/ShaderGraph/Includes/PBRGBufferPass.hlsl",
    URP + "/Editor/ShaderGraph/Includes/DepthNormalsOnlyPass.hlsl",
    URP + "/Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingMaterialTests.cs",
    "Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingTestRunner.cs",
]

MODE_NAMES = ["Forward", "ForwardPlus", "Deferred"]
MODE_PNGS = [
    "Reference",
    "CoatIntensity0",
    "CoatMiss",
    "CoatHits",
    "CoatMaskOff",
    "CoatRoughness",
    "AlphaTestWeight",
    "Lod1",
    "ShaderGraphSsr",
    "SsrWeight",
    "NormalsAlpha",
]
LIGHTMAP_PNGS = ["T9_Lightmap_{mode}.png".format(mode=mode) for mode in MODE_NAMES]

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


def analyze_mode(mode):
    problems = []
    states = state_map(mode)

    def require(name):
        state = states.get(name)
        if state is None:
            problems.append(f"{name} capture is missing")
        return state

    zero = require("CoatIntensity0")
    if zero is not None and not zero.get("identicalToReference", False):
        problems.append(
            f"SSR intensity 0 differs from the probe reference by {zero.get('differingPixels')} pixels"
        )

    miss = require("CoatMiss")
    if miss is not None and miss.get("magentaPixels", 0):
        problems.append("the all-miss capture contains error-shader pixels")

    if mode.get("missPixelsCompared", 0) <= 0:
        problems.append("no zero weight pixel was compared with the probe image")
    if mode.get("missPixelsDifferent", 1) != 0:
        problems.append(
            f"{mode.get('missPixelsDifferent')} zero weight pixels differ from the probe image"
        )

    hits = require("CoatHits")
    if hits is not None and not hits.get("differsFromReference", False):
        problems.append("the coat state did not consume the screen-space trace")

    for state in states.values():
        if state.get("magentaPixels", 0):
            problems.append(f"{state.get('state')} contains error-shader pixels")
        if state.get("nonFinitePixels", 0):
            problems.append(f"{state.get('state')} contains NaN or Inf")

    objects = {entry["name"]: entry for entry in mode.get("objects", [])}
    expected_objects = {
        "CoatSmoothBase": True,
        "CoatRoughBase": True,
        "CoatRoughCoat": True,
        "CoatMaskOff": False,
        "SimpleLitUnsupported": False,
        "ParticlesUnsupported": False,
        "TerrainUnsupported": False,
        "ShaderGraphLit": True,
        "ShaderGraphCoat": True,
        "Lod0": True,
        "Lod1": True,
    }
    for name, covered in expected_objects.items():
        entry = objects.get(name)
        if entry is None:
            problems.append(f"{name} weight sample is missing")
            continue
        weight = entry.get("weight", 0.0)
        if covered and weight < 0.05:
            problems.append(f"{name} has no screen-space weight ({weight})")
        if not covered and weight > WEIGHT_TOLERANCE:
            problems.append(f"{name} has a non zero weight ({weight}), expected 0")

    expected_alpha = {
        "CoatSmoothBase": 1.0,
        "CoatRoughBase": 1.0,
        "CoatRoughCoat": 1.0,
        "CoatMaskOff": 0.65,
        "SimpleLitUnsupported": 0.0,
        "ParticlesUnsupported": 0.0,
        "TerrainUnsupported": 0.0,
        "ShaderGraphLit": 0.75,
        "ShaderGraphCoat": 1.0,
    }
    for name, expected in expected_alpha.items():
        entry = objects.get(name)
        if entry is None:
            continue
        alpha = entry.get("normalsAlpha", -1.0)
        if alpha < 0.0 or abs(alpha - expected) > 0.03:
            problems.append(
                f"{name}: depth normals alpha is {alpha}, expected {expected}"
            )

    stats = mode.get("weight") or {}
    if stats.get("coveredFraction", 0.0) <= 0.0:
        problems.append("the SSR output has no covered pixel")

    if mode.get("roughBaseWeight", 0.0) < 0.5:
        problems.append(
            "the rough base coat has no trace weight; the coat SSR roughness was not encoded in the depth normals pass"
        )
    if mode.get("smoothBaseWeight", 0.0) < 0.5:
        problems.append("the smooth base coat has no trace weight")

    if mode.get("maskOnVsOffPixels", 0) < 64:
        problems.append("the clear coat mask does not change the rendered image")
    if mode.get("coatSmoothnessPixels", 0) < 64:
        problems.append("the independent coat smoothness does not change the rendered image")

    alpha = mode.get("alphaTest") or {}
    if alpha.get("solidWeight", 0.0) < 0.5:
        problems.append("alpha test solid pixels stopped consuming SSR")
    if alpha.get("cutWeight", 1.0) > WEIGHT_TOLERANCE:
        problems.append("alpha test cut pixels produced a screen-space weight")
    if alpha.get("solidPixels", 0) <= 0 or alpha.get("cutPixels", 0) <= 0:
        problems.append("the alpha test checker did not produce both solid and cut pixels")

    lod = mode.get("lod") or {}
    if lod.get("lod0Weight", 0.0) < 0.05 or lod.get("lod1Weight", 0.0) < 0.05:
        problems.append("a forced LOD level has no screen-space weight")
    if lod.get("levelPixels", 0) < 64:
        problems.append("the forced LOD switch did not change the image")

    graph = mode.get("shaderGraph") or {}
    if not graph.get("ssrDiffers", False):
        problems.append("ShaderGraph Lit did not consume SSR")
    if not graph.get("ssgiDiffers", False):
        problems.append("ShaderGraph Lit did not consume SSGI")
    if not graph.get("coatIntensityZeroIdentical", False):
        problems.append("ShaderGraph Complex Lit double counts the probe or BRDF at intensity 0")
    if graph.get("ssrWeight", 0.0) < 0.05:
        problems.append("ShaderGraph Lit has no screen-space weight")

    return problems


def main():
    verification_path = os.path.join(ROOT, "T9Verification.json")
    report_path = os.path.join(ROOT, "T9Report.json")
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
        full = os.path.join(PROJECT, path)
        exists = os.path.exists(full)
        has_meta = os.path.exists(full + ".meta")
        assets.append({"path": path, "exists": exists, "meta": has_meta})
        if not exists or not has_meta:
            missing_meta.append(path)

    editmode = parse_editmode(os.path.join(ROOT, "UnityTests.log"))

    pngs = {}
    for mode in MODE_NAMES:
        missing = []
        for view in MODE_PNGS:
            name = f"T9_{mode}_{view}.png"
            if not os.path.exists(os.path.join(ROOT, name)):
                missing.append(name)
            else:
                pngs[name] = sha256(os.path.join(ROOT, name))
        if missing:
            mode_problems[mode].append("missing captures: " + ", ".join(missing))
    for name in LIGHTMAP_PNGS:
        if not os.path.exists(os.path.join(ROOT, name)):
            mode_problems["Forward"].append(f"missing capture: {name}")
        else:
            pngs[name] = sha256(os.path.join(ROOT, name))

    lightmap = verification.get("lightmap") or {}
    lightmap_problems = list(lightmap.get("issues", []))
    if not lightmap.get("baked", False):
        lightmap_problems.append("the lightmap bake did not complete")
    if lightmap.get("lightmapCount", 0) <= 0:
        lightmap_problems.append("no lightmap was produced")
    for mode in lightmap.get("modes", []):
        for issue in mode.get("issues", []):
            lightmap_problems.append(f"{mode.get('name')}: {issue}")
        if not mode.get("intensityZeroIdentical", False):
            lightmap_problems.append(f"{mode.get('name')}: intensity 0 did not restore the lightmapped reference")
        if not mode.get("effectsDiffer", False):
            lightmap_problems.append(f"{mode.get('name')}: screen-space effects did not change the lightmapped image")
        if mode.get("nonFinitePixels", 0) or mode.get("magentaPixels", 0):
            lightmap_problems.append(f"{mode.get('name')}: the lightmapped image contains invalid pixels")

    criteria = {
        "verificationCompleted": bool(verification.get("completed")),
        "threePathsPassed": all(not mode_problems[name] for name in MODE_NAMES),
        "coatLobeConsumesIndependentData": all(
            modes.get(name, {}).get("roughBaseWeight", 0.0) >= 0.5
            and modes.get(name, {}).get("coatSmoothnessPixels", 0) >= 64
            and modes.get(name, {}).get("maskOnVsOffPixels", 0) >= 64
            for name in MODE_NAMES
        ),
        "depthNormalsEncoding": all(
            next(
                (entry for entry in modes.get(name, {}).get("objects", []) if entry["name"] == object_name),
                {"normalsAlpha": -1.0},
            ).get("normalsAlpha", -1.0) >= 0.0
            and abs(
                next(
                    (entry for entry in modes.get(name, {}).get("objects", []) if entry["name"] == object_name),
                    {"normalsAlpha": -2.0},
                ).get("normalsAlpha", -2.0) - expected
            ) <= 0.03
            for name in MODE_NAMES
            for object_name, expected in (
                ("CoatSmoothBase", 1.0),
                ("CoatRoughBase", 1.0),
                ("CoatRoughCoat", 1.0),
                ("CoatMaskOff", 0.65),
                ("SimpleLitUnsupported", 0.0),
                ("ParticlesUnsupported", 0.0),
                ("TerrainUnsupported", 0.0),
                ("ShaderGraphLit", 0.75),
                ("ShaderGraphCoat", 1.0),
            )
        ),
        "noDoubleProbeOrBrdf": all(
            state_map(modes.get(name, {})).get("CoatIntensity0", {}).get("identicalToReference", False)
            and modes.get(name, {}).get("missPixelsDifferent", 1) == 0
            for name in MODE_NAMES
        ),
        "unsupportedMaterialsHaveZeroWeight": all(
            next(
                (entry for entry in modes.get(name, {}).get("objects", []) if entry["name"] == object_name),
                {"weight": 1.0},
            ).get("weight", 1.0)
            <= WEIGHT_TOLERANCE
            for name in MODE_NAMES
            for object_name in ("SimpleLitUnsupported", "ParticlesUnsupported", "TerrainUnsupported", "CoatMaskOff")
        ),
        "alphaTestRegression": all(
            (modes.get(name, {}).get("alphaTest") or {}).get("solidWeight", 0.0) >= 0.5
            and (modes.get(name, {}).get("alphaTest") or {}).get("cutWeight", 1.0) <= WEIGHT_TOLERANCE
            for name in MODE_NAMES
        ),
        "lodRegression": all(
            (modes.get(name, {}).get("lod") or {}).get("lod0Weight", 0.0) >= 0.05
            and (modes.get(name, {}).get("lod") or {}).get("lod1Weight", 0.0) >= 0.05
            for name in MODE_NAMES
        ),
        "shaderGraphCoverage": all(
            (modes.get(name, {}).get("shaderGraph") or {}).get("ssrDiffers", False)
            and (modes.get(name, {}).get("shaderGraph") or {}).get("ssgiDiffers", False)
            and (modes.get(name, {}).get("shaderGraph") or {}).get("coatIntensityZeroIdentical", False)
            for name in MODE_NAMES
        ),
        "lightmapAndMixedLightingRegression": not lightmap_problems,
        "noInvalidPixels": all(
            all(state.get("magentaPixels", 0) == 0 and state.get("nonFinitePixels", 0) == 0
                for state in modes.get(name, {}).get("states", []))
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
                    "objects": modes.get(name, {}).get("objects", []),
                    "roughBaseWeight": modes.get(name, {}).get("roughBaseWeight"),
                    "smoothBaseWeight": modes.get(name, {}).get("smoothBaseWeight"),
                    "maskOnVsOffPixels": modes.get(name, {}).get("maskOnVsOffPixels"),
                    "coatSmoothnessPixels": modes.get(name, {}).get("coatSmoothnessPixels"),
                    "alphaTest": modes.get(name, {}).get("alphaTest"),
                    "lod": modes.get(name, {}).get("lod"),
                    "shaderGraph": modes.get(name, {}).get("shaderGraph"),
                }
                for name in MODE_NAMES
            },
            "lightmap": {
                "passed": not lightmap_problems,
                "issues": lightmap_problems,
                "baked": lightmap.get("baked"),
                "lightmapCount": lightmap.get("lightmapCount"),
                "mixedBakeMode": lightmap.get("mixedBakeMode"),
                "apvAvailable": lightmap.get("apvAvailable"),
                "modes": lightmap.get("modes", []),
            },
            "apvSupport": verification.get("apvSupport"),
        },
        "criteria": criteria,
        "t9ExitCriteriaMet": all(criteria.values()),
        "assets": assets,
        "missingMeta": missing_meta,
        "editMode": editmode,
        "captures": pngs,
    }

    with open(report_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)

    print(json.dumps({
        "t9ExitCriteriaMet": report["t9ExitCriteriaMet"],
        "criteria": criteria,
        "modeProblems": mode_problems,
        "lightmapProblems": lightmap_problems,
    }, indent=2))
    if not report["t9ExitCriteriaMet"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()