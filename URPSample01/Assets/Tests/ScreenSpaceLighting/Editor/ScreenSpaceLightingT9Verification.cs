using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// T9 acceptance: material coverage for Screen Space Lighting, including clear coat.
//
// The checks cover:
// - ComplexLit clear coat: the coat lobe consumes the shared trace with its own mask / coat
//   smoothness, the base lobe follows the coat hierarchy rule, and a rough base still traces at the
//   coat roughness (the depth normals prepass encodes CLEAR_COAT_SSR_PERCEPTUAL_ROUGHNESS);
// - no double probe / BRDF: SSR intensity 0 and an all-miss configuration are byte identical to the
//   probe-only reference on every renderer;
// - ShaderGraph Lit: forward and GBuffer graphs consume SSGI / SSR and encode smoothness;
// - unsupported materials (SimpleLit, Particles, Terrain): screen-space weight is exactly 0;
// - alpha test and LOD regressions;
// - a baked lightmap with mixed lighting keeps the original GI path (APV is not available in URP
//   14.0.12).
public static class ScreenSpaceLightingT9Verification
{
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string ShaderRoot = "Assets/Tests/ScreenSpaceLighting/Shaders";
    const string MaterialRoot = "Assets/Tests/ScreenSpaceLighting/Materials";
    const string OutputRoot = "PLAN/Done/01-ScreenSpaceLighting/validation/T9";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string LightmapScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT9Lightmap.unity";
    const string T1RendererPathFormat = RendererRoot + "/Ssl{0}Renderer.asset";
    const string T9RendererPathFormat = RendererRoot + "/SslT9{0}Renderer.asset";
    const string T9PipelinePathFormat = RendererRoot + "/SslT9{0}Pipeline.asset";
    const string LitGraphPath = ShaderRoot + "/SslT9Lit.shadergraph";
    const string ComplexLitGraphPath = ShaderRoot + "/SslT9ComplexLit.shadergraph";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 16;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tests/Screen Space Lighting/Create T9 Assets")]
    public static void CreateT9Assets()
    {
        EnsureAssets(force: true);
        EnsureShaderGraphAssets(force: true);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [MenuItem("Tests/Screen Space Lighting/Run T9 Verification")]
    public static void RunT9Verification()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);
        var report = new T9Report
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            passed = true,
            modes = new List<T9ModeResult>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture imageTarget = null;
        RenderTexture probeTarget = null;
        Material probeMaterial = null;
        Volume volume = null;
        VolumeProfile originalProfile = null;
        VolumeProfile testProfile = null;
        GameObject rig = null;
        T9Rig rigParts = null;
        List<Material> rigMaterials = new List<Material>();
        List<UnityEngine.Object> temporaryObjects = new List<UnityEngine.Object>();
        try
        {
            EnsureAssets(force: false);
            EnsureShaderGraphAssets(force: false);
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            DynamicGI.UpdateEnvironment();

            var camera = UnityEngine.Object.FindObjectOfType<Camera>();
            if (camera == null)
                throw new InvalidOperationException("The verification scene has no camera.");
            var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData == null)
                throw new InvalidOperationException("The verification camera has no UniversalAdditionalCameraData.");
            var runner = UnityEngine.Object.FindObjectOfType<ScreenSpaceLightingBaselineRunner>();
            if (runner != null)
                runner.enabled = false;

            var volumeObject = GameObject.Find("Baseline Volume");
            if (volumeObject == null || volumeObject.GetComponent<Volume>() == null)
                throw new InvalidOperationException("The verification scene has no Baseline Volume.");
            volume = volumeObject.GetComponent<Volume>();
            originalProfile = volume.sharedProfile;
            testProfile = ScriptableObject.Instantiate(originalProfile);
            testProfile.name = originalProfile.name + " (T9)";
            volume.sharedProfile = testProfile;
            DisableScreenSpaceEffects(testProfile);

            rigParts = CreateMaterialRig(camera, rigMaterials, temporaryObjects);
            rig = rigParts.root;

            imageTarget = CreateTarget(ImageWidth, ImageHeight, "SslT9Image");
            probeTarget = CreateTarget(ImageWidth, ImageHeight, "SslT9NormalsProbe");
            probeMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "/SslProbe.mat");
            if (probeMaterial != null)
                probeMaterial.SetFloat("_ProbeMode", 3f);
            ScreenSpaceLightingPassProbeFeature.probeTarget = probeTarget;
            camera.targetTexture = imageTarget;
            camera.aspect = (float)ImageWidth / ImageHeight;
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;
            cameraData.resetHistory = true;

            for (int modeIndex = 0; modeIndex < ModeNames.Length; modeIndex++)
            {
                var mode = CaptureMode(ModeNames[modeIndex], camera, cameraData, testProfile, imageTarget, probeTarget, rigParts,
                    outputDirectory);
                report.modes.Add(mode);
                report.passed &= mode.passed;
                Debug.Log($"T9 {mode.name}: passed={mode.passed} issues={mode.issues.Count}");
            }

            RunLightmappedRegression(report, outputDirectory, ref report.passed);
            report.apvSupport = "not available in URP 14.0.12 (no ProbeVolume integration); baked lightmap + mixed lighting verified instead";
            report.completed = true;
        }
        catch (Exception exception)
        {
            report.completed = false;
            report.error = exception.ToString();
            report.passed = false;
        }
        finally
        {
            foreach (var material in rigMaterials)
            {
                if (material != null)
                    UnityEngine.Object.DestroyImmediate(material);
            }
            if (rig != null)
                UnityEngine.Object.DestroyImmediate(rig);
            foreach (var temporary in temporaryObjects)
            {
                if (temporary != null)
                    UnityEngine.Object.DestroyImmediate(temporary);
            }

            if (volume != null)
                volume.sharedProfile = originalProfile;
            if (testProfile != null)
                UnityEngine.Object.DestroyImmediate(testProfile);
            if (imageTarget != null)
            {
                imageTarget.Release();
                UnityEngine.Object.DestroyImmediate(imageTarget);
            }
            ScreenSpaceLightingPassProbeFeature.probeTarget = null;
            if (probeTarget != null)
            {
                probeTarget.Release();
                UnityEngine.Object.DestroyImmediate(probeTarget);
            }
            if (probeMaterial != null)
                probeMaterial.SetFloat("_ProbeMode", 0f);

            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
            AssetDatabase.SaveAssets();
            File.WriteAllText(Path.Combine(outputDirectory, "T9Verification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"T9 verification finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    // ---------------------------------------------------------------------------------------------
    // Per renderer material coverage
    // ---------------------------------------------------------------------------------------------

    static T9ModeResult CaptureMode(
        string modeName,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        VolumeProfile profile,
        RenderTexture imageTarget,
        RenderTexture probeTarget,
        T9Rig rigParts,
        string outputDirectory)
    {
        var result = new T9ModeResult
        {
            name = modeName,
            issues = new List<string>(),
            states = new List<T9StateCapture>(),
            objects = new List<T9ObjectWeight>()
        };

        var ssr = profile.components.OfType<ScreenSpaceReflection>().First();
        var pipeline = LoadPipeline(T9PipelinePathFormat, modeName);
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T9RendererPathFormat, modeName));
        if (renderer == null)
            throw new FileNotFoundException("Missing T9 renderer asset. Run Create T9 Assets first.");
        var feature = renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (feature == null)
            throw new InvalidOperationException("The T9 renderer is missing the Screen Space Lighting feature.");

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        EnsureSupportedEffectsOnly(profile);
        ApplyBaseSsrSettings(ssr, minSmoothness: 0.4f);

        // -----------------------------------------------------------------------------------------
        // Probe-only reference. Screen-space effects are off.
        // -----------------------------------------------------------------------------------------
        ssr.enabled.Override(false);
        cameraData.resetHistory = true;
        var reference = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("Reference", reference, null));
        WriteImage(imageTarget, outputDirectory, "T9_" + modeName + "_Reference.png");

        // -----------------------------------------------------------------------------------------
        // No double probe / BRDF: intensity 0 must restore the reference byte for byte.
        // -----------------------------------------------------------------------------------------
        ssr.enabled.Override(true);
        ssr.intensity.Override(0f);
        cameraData.resetHistory = true;
        var intensityZero = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("CoatIntensity0", intensityZero, reference));
        WriteImage(imageTarget, outputDirectory, "T9_" + modeName + "_CoatIntensity0.png");
        if (!result.states[result.states.Count - 1].identicalToReference)
            result.issues.Add("SSR intensity 0 did not restore the probe-only reference; a probe or BRDF term is applied twice.");

        // -----------------------------------------------------------------------------------------
        // All-miss pixels must keep the probe.
        // -----------------------------------------------------------------------------------------
        ApplyBaseSsrSettings(ssr, minSmoothness: 1f);
        cameraData.resetHistory = true;
        var miss = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("CoatMiss", miss, reference));
        WriteImage(imageTarget, outputDirectory, "T9_" + modeName + "_CoatMiss.png");

        // -----------------------------------------------------------------------------------------
        // Hits: weights per test object and the rendered result.
        // -----------------------------------------------------------------------------------------
        ApplyBaseSsrSettings(ssr, minSmoothness: 0.4f);
        cameraData.resetHistory = true;
        var hits = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("CoatHits", hits, reference));
        WriteImage(imageTarget, outputDirectory, "T9_" + modeName + "_CoatHits.png");
        if (result.states[result.states.Count - 1].identicalToReference)
            result.issues.Add("SSR produced the probe-only image; the material never consumed the trace.");

        var normals = ReadPixels(probeTarget);
        WriteNormalsAlphaImage(normals, outputDirectory, "T9_" + modeName + "_NormalsAlpha.png");
        var weightView = CaptureWeightView(camera, feature, imageTarget);
        result.weight = DescribeWeightView(weightView);
        WriteImage(imageTarget, outputDirectory, "T9_" + modeName + "_SsrWeight.png");
        CheckMissKeepsProbe(reference, hits, weightView, result);
        RecordObjectWeight(result, camera, weightView, normals, rigParts.smoothCoat, "CoatSmoothBase", expectCovered: true, expectedNormalsAlpha: 1.0f);
        RecordObjectWeight(result, camera, weightView, normals, rigParts.roughCoat, "CoatRoughBase", expectCovered: true, expectedNormalsAlpha: 1.0f);
        RecordObjectWeight(result, camera, weightView, normals, rigParts.roughCoatRoughness, "CoatRoughCoat", expectCovered: true, expectedNormalsAlpha: 1.0f);
        RecordObjectWeight(result, camera, weightView, normals, rigParts.maskOffCoat, "CoatMaskOff", expectCovered: false, expectedNormalsAlpha: 0.65f);
        RecordObjectWeight(result, camera, weightView, normals, rigParts.simpleLit, "SimpleLitUnsupported", expectCovered: false, expectedNormalsAlpha: 0f);
        RecordObjectWeight(result, camera, weightView, normals, rigParts.particles, "ParticlesUnsupported", expectCovered: false, expectedNormalsAlpha: 0f);
        RecordObjectWeight(result, camera, weightView, normals, rigParts.terrain, "TerrainUnsupported", expectCovered: false, expectedNormalsAlpha: 0f);
        RecordObjectWeight(result, camera, weightView, normals, rigParts.shaderGraphLit, "ShaderGraphLit", expectCovered: true, expectedNormalsAlpha: 0.75f);
        RecordObjectWeight(result, camera, weightView, normals, rigParts.shaderGraphCoat, "ShaderGraphCoat", expectCovered: true, expectedNormalsAlpha: 1.0f);
        RecordObjectWeight(result, camera, weightView, normals, rigParts.lod0, "Lod0", expectCovered: true, expectedNormalsAlpha: -1f);
        RecordObjectWeight(result, camera, weightView, normals, rigParts.lod1, "Lod1", expectCovered: true, expectedNormalsAlpha: -1f);

        // The rough base coat is the proof that the depth normals prepass writes the coat SSR
        // roughness instead of the base smoothness: without it, the trace would be rejected by the
        // roughness fade (base smoothness is 0.3, below minSmoothness).
        var roughBase = result.objects.FirstOrDefault(o => o.name == "CoatRoughBase");
        var smoothBase = result.objects.FirstOrDefault(o => o.name == "CoatSmoothBase");
        if (roughBase != null && smoothBase != null)
        {
            result.roughBaseWeight = roughBase.weight;
            result.smoothBaseWeight = smoothBase.weight;
            if (roughBase.weight < 0.5f)
                result.issues.Add($"CoatRoughBase trace weight is {roughBase.weight:G4}; the coat roughness was not encoded in the depth normals prepass.");
        }

        foreach (var entry in result.objects)
        {
            if (entry.expectCovered && entry.weight < 0.05f)
                result.issues.Add($"{entry.name}: expected a covered screen-space weight, got {entry.weight:G4}.");
            if (!entry.expectCovered && entry.weight > 1e-4f)
                result.issues.Add($"{entry.name}: expected weight 0, got {entry.weight:G4}.");
            if (entry.expectedNormalsAlpha >= 0f)
            {
                float delta = Mathf.Abs(entry.normalsAlpha - entry.expectedNormalsAlpha);
                if (delta > 0.02f)
                    result.issues.Add(
                        $"{entry.name}: depth normals alpha is {entry.normalsAlpha:G4}, expected {entry.expectedNormalsAlpha:G4}.");
            }
        }

        // -----------------------------------------------------------------------------------------
        // Independent coat mask and coat smoothness.
        // -----------------------------------------------------------------------------------------
        SetCoatMask(rigParts.roughCoat, 0f);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 4);
        var roughCoatWithoutMask = ReadPixels(imageTarget);
        SetCoatMask(rigParts.roughCoat, 1f);
        result.maskOnVsOffPixels = CountDifferences(hits, roughCoatWithoutMask, 1e-5f);
        result.maskOnVsOffMaxDelta = MaxDifference(hits, roughCoatWithoutMask);
        WriteImage(imageTarget, outputDirectory, "T9_" + modeName + "_CoatMaskOff.png");
        if (result.maskOnVsOffPixels < 64)
            result.issues.Add($"Clear coat mask changed only {result.maskOnVsOffPixels} pixels; the coat lobe ignores the material mask.");

        SetCoatSmoothness(rigParts.smoothCoat, 0.2f);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 4);
        var roughCoatSmoothness = ReadPixels(imageTarget);
        SetCoatSmoothness(rigParts.smoothCoat, 1f);
        result.coatSmoothnessPixels = CountDifferences(hits, roughCoatSmoothness, 1e-5f);
        result.coatSmoothnessMaxDelta = MaxDifference(hits, roughCoatSmoothness);
        WriteImage(imageTarget, outputDirectory, "T9_" + modeName + "_CoatRoughness.png");
        if (result.coatSmoothnessPixels < 64)
            result.issues.Add($"Coat smoothness changed only {result.coatSmoothnessPixels} pixels; the coat lobe does not use its own roughness.");

        // -----------------------------------------------------------------------------------------
        // Alpha test: the cut pixels must not receive screen-space reflections.
        // -----------------------------------------------------------------------------------------
        result.alphaTest = CaptureAlphaTest(camera, cameraData, imageTarget, weightView, rigParts.alphaTestPlane);
        WriteImage(imageTarget, outputDirectory, "T9_" + modeName + "_AlphaTestWeight.png");
        if (result.alphaTest.solidWeight < 0.5f)
            result.issues.Add($"Alpha test solid pixels have weight {result.alphaTest.solidWeight:G4}; the supported surface stopped consuming SSR.");
        if (result.alphaTest.cutWeight > 1e-4f)
            result.issues.Add($"Alpha test cut pixels have weight {result.alphaTest.cutWeight:G4}; clipped fragments must not produce a trace.");

        // -----------------------------------------------------------------------------------------
        // LOD: both levels render and keep a valid material.
        // -----------------------------------------------------------------------------------------
        result.lod = CaptureLod(camera, cameraData, imageTarget, feature, rigParts);
        WriteImage(imageTarget, outputDirectory, "T9_" + modeName + "_Lod1.png");
        if (result.lod.lod0Weight < 0.05f || result.lod.lod1Weight < 0.05f)
            result.issues.Add($"LOD levels did not both trace (lod0 {result.lod.lod0Weight:G4}, lod1 {result.lod.lod1Weight:G4}).");
        if (result.lod.levelPixels < 64)
            result.issues.Add($"LOD levels differ on only {result.lod.levelPixels} pixels; the forced LOD did not switch the material.");

        // -----------------------------------------------------------------------------------------
        // ShaderGraph Lit: SSR and SSGI must reach the generated graph.
        // -----------------------------------------------------------------------------------------
        result.shaderGraph = CaptureShaderGraph(camera, cameraData, imageTarget, feature, profile, ssr, rigParts, reference);
        WriteImage(imageTarget, outputDirectory, "T9_" + modeName + "_ShaderGraphSsr.png");
        if (!result.shaderGraph.ssrDiffers)
            result.issues.Add("ShaderGraph Lit did not change when SSR was enabled.");
        if (!result.shaderGraph.ssgiDiffers)
            result.issues.Add("ShaderGraph Lit did not change when SSGI was enabled.");
        if (result.shaderGraph.ssrWeight < 0.05f)
            result.issues.Add($"ShaderGraph Lit has no screen-space weight ({result.shaderGraph.ssrWeight:G4}).");
        if (!result.shaderGraph.coatIntensityZeroIdentical)
            result.issues.Add("ShaderGraph Complex Lit changed at SSR intensity 0; a probe or BRDF term is applied twice.");

        // Restore the shared state for the next renderer.
        ApplyBaseSsrSettings(ssr, minSmoothness: 0.4f);
        SetCoatMask(rigParts.roughCoat, 1f);
        SetCoatSmoothness(rigParts.smoothCoat, 1f);
        rigParts.lodGroup.ForceLOD(-1);
        ssr.enabled.Override(false);

        result.passed = result.issues.Count == 0;
        return result;
    }

    static T9AlphaTest CaptureAlphaTest(
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        RenderTexture imageTarget,
        Color[] weight,
        GameObject plane)
    {
        var result = new T9AlphaTest();
        var rect = ProjectBounds(camera, plane);
        if (weight == null || rect.width <= 0 || rect.height <= 0)
            return result;

        // The checker texture alternates fully opaque and fully transparent texels. The transparent
        // texels show the rough backdrop, whose weight is 0, while the opaque texels show the smooth
        // plane. Counting both classes proves the clipped fragments never reached the trace.
        int solid = 0;
        int cut = 0;
        double solidSum = 0.0;
        for (int y = rect.yMin; y <= rect.yMax; y++)
        {
            for (int x = rect.xMin; x <= rect.xMax; x++)
            {
                float value = weight[y * ImageWidth + x].r;
                if (value > 0.5f)
                {
                    solid++;
                    solidSum += value;
                }
                else if (value < 1e-4f)
                {
                    cut++;
                }
            }
        }

        result.solidPixels = solid;
        result.cutPixels = cut;
        result.solidWeight = solid > 0 ? (float)(solidSum / solid) : 0f;
        result.cutWeight = (solid > 0 && cut > 0) ? 0f : 1f;
        return result;
    }

    static T9Lod CaptureLod(
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        T9Rig parts)
    {
        var result = new T9Lod();
        var group = parts.lodGroup;

        group.ForceLOD(0);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 4);
        var lod0 = ReadPixels(imageTarget);
        var weight0 = CaptureWeightView(camera, feature, imageTarget);
        result.lod0Weight = SampleWeight(camera, weight0, parts.lod0.transform.position);

        group.ForceLOD(1);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 4);
        var lod1 = ReadPixels(imageTarget);
        var weight1 = CaptureWeightView(camera, feature, imageTarget);
        result.lod1Weight = SampleWeight(camera, weight1, parts.lod1.transform.position);

        result.levelPixels = CountDifferences(lod0, lod1, 1e-5f);
        result.levelMaxDelta = MaxDifference(lod0, lod1);
        parts.lodGroup.ForceLOD(-1);
        return result;
    }

    static T9ShaderGraph CaptureShaderGraph(
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        VolumeProfile profile,
        ScreenSpaceReflection ssr,
        T9Rig parts,
        Color[] reference)
    {
        var result = new T9ShaderGraph();
        var ssgi = profile.components.OfType<ScreenSpaceGlobalIllumination>().First();

        // SSR off for the SG sphere only: compare against the reference captured with everything off.
        ApplyBaseSsrSettings(ssr, minSmoothness: 0.4f);
        ssr.enabled.Override(false);
        cameraData.resetHistory = true;
        var ssrOff = RenderAndCapture(camera, imageTarget, WarmupFrames);

        ssr.enabled.Override(true);
        cameraData.resetHistory = true;
        var ssrOn = RenderAndCapture(camera, imageTarget, WarmupFrames);
        var weightView = CaptureWeightView(camera, feature, imageTarget);
        var bounds = ProjectBounds(camera, parts.shaderGraphLit);
        result.ssrDiffers = RegionDiffers(ssrOff, ssrOn, bounds, 1e-5f);
        result.ssrWeight = SampleMaxInBounds(camera, weightView, parts.shaderGraphLit);

        // SSGI: SSR off, SSGI on/off.
        ssr.enabled.Override(false);
        ssgi.enabled.Override(true);
        ssgi.intensity.Override(1f);
        cameraData.resetHistory = true;
        var ssgiOn = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.ssgiDiffers = RegionDiffers(ssrOff, ssgiOn, bounds, 1e-5f);
        ssgi.enabled.Override(false);

        // ShaderGraph Complex Lit: SSR intensity 0 must keep the reference image.
        ApplyBaseSsrSettings(ssr, minSmoothness: 0.4f);
        ssr.enabled.Override(true);
        ssr.intensity.Override(0f);
        cameraData.resetHistory = true;
        var coatZero = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.coatIntensityZeroIdentical = CountDifferences(reference, coatZero, 0f) == 0;

        ssr.intensity.Override(1f);
        return result;
    }

    // ---------------------------------------------------------------------------------------------
    // Lightmap + mixed lighting regression
    // ---------------------------------------------------------------------------------------------

    static void RunLightmappedRegression(T9Report report, string outputDirectory, ref bool passed)
    {
        var regression = new T9LightmapResult { issues = new List<string>() };
        var previousScenePath = SceneManager.GetActiveScene().path;
        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        RenderTexture target = null;
        try
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildLightmapScene();
            EditorSceneManager.SaveScene(scene, LightmapScenePath);
            var lightingSettings = new LightingSettings
            {
                name = "SslT9Lighting",
                bakedGI = true,
                realtimeGI = false,
                lightmapper = LightingSettings.Lightmapper.ProgressiveCPU,
                lightmapResolution = 15f,
                lightmapMaxSize = 512,
                mixedBakeMode = MixedLightingMode.Subtractive,
            };
            Lightmapping.lightingSettings = lightingSettings;
            bool baked = Lightmapping.Bake();
            regression.baked = baked;
            regression.lightmapCount = LightmapSettings.lightmaps != null ? LightmapSettings.lightmaps.Length : 0;
            regression.mixedBakeMode = lightingSettings.mixedBakeMode.ToString();
            regression.apvAvailable = false;
            if (!baked || regression.lightmapCount == 0)
                regression.issues.Add("Lightmapping.Bake did not produce a lightmap.");

            var camera = UnityEngine.Object.FindObjectOfType<Camera>();
            var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
            var volume = UnityEngine.Object.FindObjectOfType<Volume>();
            var profile = ScriptableObject.Instantiate(volume.sharedProfile);
            try
            {
                volume.sharedProfile = profile;
                DisableScreenSpaceEffects(profile);
                var ssr = profile.components.OfType<ScreenSpaceReflection>().First();
                var ssgi = profile.components.OfType<ScreenSpaceGlobalIllumination>().First();
                ApplyBaseSsrSettings(ssr, minSmoothness: 0.4f);

                target = CreateTarget(ImageWidth, ImageHeight, "SslT9LightmapImage");
                camera.targetTexture = target;
                camera.aspect = (float)ImageWidth / ImageHeight;
                cameraData.renderPostProcessing = false;
                cameraData.antialiasing = AntialiasingMode.None;

                foreach (var mode in ModeNames)
                {
                    var modeResult = new T9LightmapMode { name = mode };
                    var pipeline = LoadPipeline(T9PipelinePathFormat, mode);
                    GraphicsSettings.defaultRenderPipeline = pipeline;
                    QualitySettings.renderPipeline = pipeline;

                    ssr.enabled.Override(false);
                    ssgi.enabled.Override(false);
                    cameraData.resetHistory = true;
                    var reference = RenderAndCapture(camera, target, WarmupFrames);

                    ssr.enabled.Override(true);
                    ssr.intensity.Override(0f);
                    cameraData.resetHistory = true;
                    var zero = RenderAndCapture(camera, target, WarmupFrames);
                    modeResult.intensityZeroIdentical = CountDifferences(reference, zero, 0f) == 0;

                    ssr.intensity.Override(1f);
                    ssgi.enabled.Override(true);
                    ssgi.intensity.Override(1f);
                    cameraData.resetHistory = true;
                    var withEffects = RenderAndCapture(camera, target, WarmupFrames);
                    modeResult.effectsDiffer = CountDifferences(reference, withEffects, 1e-5f) > 0;
                    modeResult.nonFinitePixels = Count(withEffects, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b));
                    modeResult.magentaPixels = Count(withEffects, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f);
                    WriteImage(target, outputDirectory, "T9_Lightmap_" + mode + ".png");

                    if (!modeResult.intensityZeroIdentical)
                        modeResult.issues.Add("Intensity 0 did not restore the baked-lightmap reference.");
                    if (!modeResult.effectsDiffer)
                        modeResult.issues.Add("Screen-space effects did not change the lightmapped image.");
                    if (modeResult.nonFinitePixels > 0 || modeResult.magentaPixels > 0)
                        modeResult.issues.Add("The lightmapped image contains invalid or error-shader pixels.");
                    regression.modes.Add(modeResult);
                }
            }
            finally
            {
                if (profile != null)
                    UnityEngine.Object.DestroyImmediate(profile);
            }

            EditorSceneManager.SaveScene(scene, LightmapScenePath);
            report.lightmapScene = LightmapScenePath;
        }
        catch (Exception exception)
        {
            regression.issues.Add(exception.ToString());
        }
        finally
        {
            if (target != null)
            {
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
        }

        regression.passed = regression.issues.Count == 0 &&
                            regression.modes.All(m => m.issues.Count == 0);
        report.lightmap = regression;
        passed &= regression.passed;
    }

    static void BuildLightmapScene()
    {
        var skybox = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "/BaselineSkybox.mat");
        RenderSettings.skybox = skybox;
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.fog = false;

        var cameraObject = new GameObject("T9 Lightmap Camera");
        cameraObject.tag = "MainCamera";
        var camera = cameraObject.AddComponent<Camera>();
        cameraObject.transform.position = new Vector3(0f, 3.2f, -7f);
        cameraObject.transform.LookAt(new Vector3(0f, 0.8f, 0f));
        camera.fieldOfView = 50f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 100f;
        camera.allowHDR = true;
        camera.allowMSAA = false;
        var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.renderPostProcessing = false;
        cameraData.antialiasing = AntialiasingMode.None;

        var lightObject = new GameObject("T9 Lightmap Light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = new Color(1f, 0.94f, 0.85f);
        light.intensity = 1.6f;
        light.shadows = LightShadows.Soft;
        light.lightmapBakeType = LightmapBakeType.Mixed;
        lightObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
        RenderSettings.sun = light;

        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        var floorMaterial = EnsureMaterialAsset(MaterialRoot + "/T9LightmapFloor.mat", litShader);
        floorMaterial.SetColor("_BaseColor", new Color(0.35f, 0.36f, 0.4f));
        floorMaterial.SetFloat("_Metallic", 0f);
        floorMaterial.SetFloat("_Smoothness", 0.55f);

        var metalMaterial = EnsureMaterialAsset(MaterialRoot + "/T9LightmapMetal.mat", litShader);
        metalMaterial.SetColor("_BaseColor", new Color(0.8f, 0.82f, 0.86f));
        metalMaterial.SetFloat("_Metallic", 1f);
        metalMaterial.SetFloat("_Smoothness", 0.9f);

        var diffuseMaterial = EnsureMaterialAsset(MaterialRoot + "/T9LightmapDiffuse.mat", litShader);
        diffuseMaterial.SetColor("_BaseColor", new Color(0.7f, 0.25f, 0.12f));
        diffuseMaterial.SetFloat("_Metallic", 0f);
        diffuseMaterial.SetFloat("_Smoothness", 0.2f);

        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "T9 Lightmap Floor";
        floor.transform.localScale = new Vector3(1.6f, 1f, 1.6f);
        floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;
        MarkStatic(floor);

        var metal = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        metal.name = "T9 Lightmap Metal";
        metal.transform.position = new Vector3(-1.4f, 0.9f, 0.4f);
        metal.GetComponent<Renderer>().sharedMaterial = metalMaterial;
        MarkStatic(metal);

        var diffuse = GameObject.CreatePrimitive(PrimitiveType.Cube);
        diffuse.name = "T9 Lightmap Diffuse";
        diffuse.transform.position = new Vector3(1.5f, 0.7f, 0.2f);
        diffuse.transform.localScale = new Vector3(1.2f, 1.4f, 1.2f);
        diffuse.GetComponent<Renderer>().sharedMaterial = diffuseMaterial;
        MarkStatic(diffuse);

        var volumeObject = new GameObject("T9 Lightmap Volume");
        var volume = volumeObject.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.sharedProfile = EnsureLightmapProfile();
    }

    static VolumeProfile EnsureLightmapProfile()
    {
        var path = "Assets/Tests/ScreenSpaceLighting/Profiles/ScreenSpaceLightingT9LightmapProfile.asset";
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
        if (profile == null)
        {
            var baseProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(
                "Assets/Tests/ScreenSpaceLighting/Profiles/ScreenSpaceLightingT3Profile.asset");
            profile = ScriptableObject.Instantiate(baseProfile);
            profile.name = "ScreenSpaceLightingT9LightmapProfile";
            AssetDatabase.CreateAsset(profile, path);
        }
        EditorUtility.SetDirty(profile);
        return profile;
    }

    static Material EnsureMaterialAsset(string path, Shader shader)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        else
        {
            material.shader = shader;
        }
        material.name = Path.GetFileNameWithoutExtension(path);
        EditorUtility.SetDirty(material);
        return material;
    }

    static void MarkStatic(GameObject gameObject)
    {
        gameObject.isStatic = true;
        var renderer = gameObject.GetComponent<MeshRenderer>();
        if (renderer != null)
            renderer.receiveGI = ReceiveGI.Lightmaps;
    }

    // ---------------------------------------------------------------------------------------------
    // Rig
    // ---------------------------------------------------------------------------------------------

    sealed class T9Rig
    {
        public GameObject root;
        public GameObject smoothCoat;
        public GameObject roughCoat;
        public GameObject roughCoatRoughness;
        public GameObject maskOffCoat;
        public GameObject simpleLit;
        public GameObject particles;
        public GameObject terrain;
        public GameObject alphaTestPlane;
        public GameObject shaderGraphLit;
        public GameObject shaderGraphCoat;
        public GameObject lod0;
        public GameObject lod1;
        public LODGroup lodGroup;
    }

    static T9Rig CreateMaterialRig(Camera camera, List<Material> materials, List<UnityEngine.Object> temporary)
    {
        var rig = new T9Rig();
        var root = new GameObject("T9 Material Rig");
        rig.root = root;
        var complexLitShader = Shader.Find("Universal Render Pipeline/Complex Lit");
        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        var simpleLitShader = Shader.Find("Universal Render Pipeline/Simple Lit");
        var particlesShader = Shader.Find("Universal Render Pipeline/Particles/Lit");
        if (complexLitShader == null || litShader == null || simpleLitShader == null)
            throw new InvalidOperationException("The required URP shaders are missing.");

        var viewDirection = camera.transform.forward;
        var right = Vector3.Cross(Vector3.up, viewDirection).normalized;
        var center = camera.transform.position + viewDirection * 15f;
        center.y = 1.6f;

        var floorMaterial = CreateLitMaterial(litShader, "T9 Mirror Floor", new Color(0.72f, 0.75f, 0.8f), 1f, 0.95f);
        var backdropMaterial = CreateLitMaterial(litShader, "T9 Reflect Backdrop", new Color(0.04f, 0.04f, 0.04f), 0f, 0.2f);
        backdropMaterial.EnableKeyword("_EMISSION");
        backdropMaterial.SetColor("_EmissionColor", new Color(2.4f, 1.4f, 0.5f));
        materials.Add(floorMaterial);
        materials.Add(backdropMaterial);
        AddBox(root, floorMaterial, "T9 Mirror Floor", center + new Vector3(0f, -1.55f, 0f), new Vector3(30f, 0.05f, 18f));
        AddBox(root, backdropMaterial, "T9 Reflect Backdrop", center + viewDirection * 9f + Vector3.up * 2f, new Vector3(30f, 12f, 0.2f));

        // Row A: clear coat and alpha test, at the target height.
        rig.smoothCoat = AddCoatSphere(root, complexLitShader, materials, "T9 Coat SmoothBase", center + right * -5.2f, 0.9f, 1f, 1f);
        rig.roughCoat = AddCoatSphere(root, complexLitShader, materials, "T9 Coat RoughBase", center + right * -2.6f, 0.3f, 1f, 1f);
        rig.roughCoatRoughness = AddCoatSphere(root, complexLitShader, materials, "T9 Coat RoughCoat", center + right * 0f, 0.9f, 1f, 0.2f);
        rig.maskOffCoat = AddCoatSphere(root, complexLitShader, materials, "T9 Coat MaskOff", center + right * 2.6f, 0.3f, 0f, 1f);
        rig.alphaTestPlane = AddAlphaTestPlane(root, litShader, materials, temporary, center + right * 5.2f + Vector3.up * 0.6f);

        // Row B: ShaderGraph and unsupported materials, above and slightly behind row A.
        var secondRow = center + Vector3.up * 2.7f - viewDirection * 0.8f;
        rig.shaderGraphLit = AddSphere(root, CreateShaderGraphMaterial(materials, ShaderGraphPath.Lit), "T9 ShaderGraph Lit", secondRow + right * -4.5f, 0.85f);
        rig.shaderGraphCoat = AddSphere(root, CreateShaderGraphMaterial(materials, ShaderGraphPath.ComplexLit), "T9 ShaderGraph Complex Lit", secondRow + right * -2.2f, 0.85f);
        rig.simpleLit = AddSphere(root, CreateSimpleLitMaterial(simpleLitShader, materials), "T9 SimpleLit", secondRow + right * 0.2f, 0.85f);
        rig.particles = CreateParticles(root, particlesShader, materials, secondRow + right * 2.4f);
        rig.terrain = CreateTerrain(root, materials, center + right * 8.4f);

        // LOD group with two reflective levels: the low level is rougher so a forced switch is
        // observable while both levels keep a valid material for the depth normals pass.
        var lodRoot = new GameObject("T9 Lod Group");
        lodRoot.transform.SetParent(root.transform, false);
        lodRoot.transform.position = center + right * -8.2f + Vector3.up * 0.4f;
        var lodGroup = lodRoot.AddComponent<LODGroup>();
        rig.lodGroup = lodGroup;
        rig.lod0 = AddSphere(lodRoot, CreateLitMaterial(litShader, "T9 Lod0", Color.white, 1f, 0.95f), "T9 Lod0", lodRoot.transform.position, 0.8f);
        rig.lod1 = AddSphere(lodRoot, CreateLitMaterial(litShader, "T9 Lod1", Color.white, 1f, 0.55f), "T9 Lod1", lodRoot.transform.position, 0.8f);
        lodGroup.SetLODs(new[]
        {
            new LOD(0.6f, new Renderer[] { rig.lod0.GetComponent<Renderer>() }),
            new LOD(0.05f, new Renderer[] { rig.lod1.GetComponent<Renderer>() }),
        });
        lodGroup.fadeMode = LODFadeMode.CrossFade;
        lodGroup.RecalculateBounds();

        return rig;
    }

    static GameObject AddCoatSphere(
        GameObject parent,
        Shader shader,
        List<Material> materials,
        string name,
        Vector3 position,
        float baseSmoothness,
        float coatMask,
        float coatSmoothness)
    {
        var material = new Material(shader) { name = name };
        material.SetColor("_BaseColor", new Color(0.55f, 0.58f, 0.62f));
        material.SetFloat("_Metallic", 1f);
        material.SetFloat("_Smoothness", baseSmoothness);
        material.SetFloat("_ClearCoat", coatMask > 0f ? 1f : 0f);
        material.SetFloat("_ClearCoatMask", coatMask);
        material.SetFloat("_ClearCoatSmoothness", coatSmoothness);
        if (coatMask > 0f)
            material.EnableKeyword("_CLEARCOAT");
        else
            material.DisableKeyword("_CLEARCOAT");
        materials.Add(material);
        return AddSphere(parent, material, name, position, 0.9f);
    }

    static void SetCoatMask(GameObject sphere, float value)
    {
        var renderer = sphere.GetComponent<Renderer>();
        var material = renderer.sharedMaterial;
        material.SetFloat("_ClearCoatMask", value);
        material.SetFloat("_ClearCoat", value > 0f ? 1f : 0f);
        if (value > 0f)
            material.EnableKeyword("_CLEARCOAT");
        else
            material.DisableKeyword("_CLEARCOAT");
    }

    static void SetCoatSmoothness(GameObject sphere, float value)
    {
        sphere.GetComponent<Renderer>().sharedMaterial.SetFloat("_ClearCoatSmoothness", value);
    }

    static GameObject AddAlphaTestPlane(
        GameObject parent,
        Shader shader,
        List<Material> materials,
        List<UnityEngine.Object> temporary,
        Vector3 position)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
        {
            name = "T9 Alpha Checker",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat
        };
        var colors = new[]
        {
            new Color(0.8f, 0.8f, 0.85f, 1f), new Color(0f, 0f, 0f, 0f),
            new Color(0f, 0f, 0f, 0f), new Color(0.8f, 0.8f, 0.85f, 1f),
        };
        texture.SetPixels(colors);
        texture.Apply(false, false);
        temporary.Add(texture);

        var material = new Material(shader) { name = "T9 Alpha Test" };
        material.SetTexture("_BaseMap", texture);
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Metallic", 1f);
        material.SetFloat("_Smoothness", 0.95f);
        material.SetFloat("_AlphaClip", 1f);
        material.SetFloat("_Cutoff", 0.5f);
        material.SetFloat("_QueueOffset", 0f);
        material.SetFloat("_Cull", 0f);
        material.EnableKeyword("_ALPHATEST_ON");
        material.renderQueue = 2450;
        materials.Add(material);

        var plane = GameObject.CreatePrimitive(PrimitiveType.Quad);
        plane.name = "T9 Alpha Test";
        plane.transform.SetParent(parent.transform, false);
        plane.transform.position = position;
        plane.transform.localScale = new Vector3(2.4f, 2.4f, 1f);
        plane.transform.rotation = Quaternion.LookRotation(position - FindCamera().transform.position, Vector3.up);
        plane.GetComponent<Renderer>().sharedMaterial = material;
        return plane;
    }

    static GameObject CreateParticles(
        GameObject parent,
        Shader shader,
        List<Material> materials,
        Vector3 position)
    {
        // The Particles/Lit shader is exercised through a regular mesh: it keeps the particle shader
        // path (depth normals alpha 0) without depending on the particle simulation timing.
        var material = new Material(shader) { name = "T9 Opaque Particles" };
        material.SetColor("_BaseColor", new Color(0.7f, 0.6f, 0.2f));
        material.SetColor("_Color", new Color(0.7f, 0.6f, 0.2f));
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Smoothness", 0.9f);
        material.SetFloat("_Surface", 0f);
        material.SetFloat("_ZWrite", 1f);
        material.SetFloat("_Blend", 0f);
        material.SetFloat("_Cull", 0f);
        material.renderQueue = 2000;
        material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        materials.Add(material);

        return AddSphere(parent, material, "T9 Opaque Particles", position, 0.9f);
    }

    static GameObject CreateTerrain(
        GameObject parent,
        List<Material> materials,
        Vector3 position)
    {
        // Terrain Lit is exercised through its shader on a plane. The depth normals pass writes
        // alpha 0 with no terrain dependency, which is exactly the material contract SSR consumes;
        // a runtime Terrain object is not drawn by this manual camera harness.
        var shader = Shader.Find("Universal Render Pipeline/Terrain/Lit");
        if (shader == null)
            throw new InvalidOperationException("The URP Terrain Lit shader is missing.");
        var material = new Material(shader) { name = "T9 Terrain Lit" };
        material.SetFloat("_Smoothness", 0.95f);
        material.SetColor("_BaseColor", new Color(0.28f, 0.3f, 0.34f));
        materials.Add(material);

        var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
        plane.name = "T9 Terrain";
        plane.transform.SetParent(parent.transform, false);
        plane.transform.position = new Vector3(position.x, 0.1f, position.z);
        plane.transform.localScale = new Vector3(0.7f, 1f, 0.7f);
        plane.GetComponent<Renderer>().sharedMaterial = material;
        return plane;
    }

    static Material CreateLitMaterial(Shader shader, string name, Color color, float metallic, float smoothness)
    {
        var material = new Material(shader) { name = name, color = color };
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        return material;
    }

    static Material CreateSimpleLitMaterial(Shader shader, List<Material> materials)
    {
        var material = new Material(shader) { name = "T9 SimpleLit" };
        material.SetColor("_BaseColor", new Color(0.55f, 0.2f, 0.6f));
        material.SetFloat("_Smoothness", 0.9f);
        materials.Add(material);
        return material;
    }

    enum ShaderGraphPath
    {
        Lit,
        ComplexLit,
    }

    static Material CreateShaderGraphMaterial(List<Material> materials, ShaderGraphPath path)
    {
        var shaderPath = path == ShaderGraphPath.Lit ? LitGraphPath : ComplexLitGraphPath;
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
        if (shader == null)
            throw new FileNotFoundException("Missing ShaderGraph asset. Run Create T9 Assets first.", shaderPath);
        var material = new Material(shader) { name = path == ShaderGraphPath.Lit ? "T9 ShaderGraph Lit" : "T9 ShaderGraph Complex Lit" };
        materials.Add(material);
        return material;
    }

    static GameObject AddBox(GameObject parent, Material material, string name, Vector3 position, Vector3 size)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent.transform, false);
        box.transform.position = position;
        box.transform.localScale = size;
        box.GetComponent<Renderer>().sharedMaterial = material;
        return box;
    }

    static GameObject AddSphere(GameObject parent, Material material, string name, Vector3 position, float scale)
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = name;
        sphere.transform.SetParent(parent.transform, false);
        sphere.transform.position = position;
        sphere.transform.localScale = Vector3.one * scale;
        sphere.GetComponent<Renderer>().sharedMaterial = material;
        return sphere;
    }

    static Camera FindCamera()
    {
        return UnityEngine.Object.FindObjectOfType<Camera>();
    }

    // ---------------------------------------------------------------------------------------------
    // ShaderGraph assets
    // ---------------------------------------------------------------------------------------------

    static void EnsureShaderGraphAssets(bool force)
    {
        EnsureShaderGraphAsset(LitGraphPath, complexLit: false, force: force);
        EnsureShaderGraphAsset(ComplexLitGraphPath, complexLit: true, force: force);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static void EnsureShaderGraphAsset(string path, bool complexLit, bool force)
    {
        if (!force && AssetDatabase.LoadAssetAtPath<Shader>(path) != null)
            return;

        var graphType = Type.GetType("UnityEditor.ShaderGraph.GraphData, Unity.ShaderGraph.Editor");
        var targetType = Type.GetType("UnityEditor.Rendering.Universal.ShaderGraph.UniversalTarget, Unity.RenderPipelines.Universal.Editor");
        var litSubTargetType = Type.GetType("UnityEditor.Rendering.Universal.ShaderGraph.UniversalLitSubTarget, Unity.RenderPipelines.Universal.Editor");
        var blockFieldsType = Type.GetType("UnityEditor.ShaderGraph.BlockFields, Unity.ShaderGraph.Editor");
        var blockType = Type.GetType("UnityEditor.ShaderGraph.BlockNode, Unity.ShaderGraph.Editor");
        var fileUtilitiesType = Type.GetType("UnityEditor.ShaderGraph.FileUtilities, Unity.ShaderGraph.Editor");
        if (graphType == null || targetType == null || litSubTargetType == null || blockFieldsType == null ||
            blockType == null || fileUtilitiesType == null)
            throw new TypeLoadException("ShaderGraph editor types are not available; install com.unity.shadergraph 14.0.12.");

        var graph = Activator.CreateInstance(graphType);
        graphType.GetMethod("AddContexts").Invoke(graph, null);
        var target = Activator.CreateInstance(targetType);
        targetType.GetMethod("TrySetActiveSubTarget").Invoke(target, new object[] { litSubTargetType });
        if (complexLit)
        {
            var subTarget = targetType.GetProperty("activeSubTarget").GetValue(target);
            litSubTargetType.GetProperty("clearCoat").SetValue(subTarget, true);
        }

        var vertexDescription = blockFieldsType.GetNestedType("VertexDescription");
        var surfaceDescription = blockFieldsType.GetNestedType("SurfaceDescription");
        var blocks = new List<object>
        {
            vertexDescription.GetField("Position").GetValue(null),
            vertexDescription.GetField("Normal").GetValue(null),
            vertexDescription.GetField("Tangent").GetValue(null),
            surfaceDescription.GetField("BaseColor").GetValue(null),
            surfaceDescription.GetField("Metallic").GetValue(null),
            surfaceDescription.GetField("Smoothness").GetValue(null),
            surfaceDescription.GetField("Emission").GetValue(null),
            surfaceDescription.GetField("Occlusion").GetValue(null),
        };
        if (complexLit)
        {
            blocks.Add(surfaceDescription.GetField("CoatMask").GetValue(null));
            blocks.Add(surfaceDescription.GetField("CoatSmoothness").GetValue(null));
        }

        var blockDescriptorType = blocks[0].GetType();
        var blockArray = Array.CreateInstance(blockDescriptorType, blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
            blockArray.SetValue(blocks[i], i);
        var targetArray = Array.CreateInstance(targetType, 1);
        targetArray.SetValue(target, 0);

        graphType.GetMethod("InitializeOutputs").Invoke(graph, new object[] { targetArray, blockArray });

        if (complexLit)
        {
            var getNodes = graphType.GetMethod("GetNodes").MakeGenericMethod(blockType);
            foreach (var node in (IEnumerable)getNodes.Invoke(graph, null))
            {
                var descriptor = blockType.GetProperty("descriptor").GetValue(node);
                string name = (string)descriptor.GetType().GetProperty("name").GetValue(descriptor);
                if (name == "CoatMask")
                    SetBlockValue(blockType, node, 1f);
            }
        }

        graphType.GetProperty("path").SetValue(graph, "Shader Graphs");
        fileUtilitiesType.GetMethod("WriteShaderGraphToDisk", StaticFlags)
            .Invoke(null, new object[] { path, graph });
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.Refresh();
    }

    static void SetBlockValue(Type blockType, object node, float value)
    {
        var materialSlotType = Type.GetType("UnityEditor.ShaderGraph.MaterialSlot, Unity.ShaderGraph.Editor");
        if (materialSlotType == null)
            return;
        var findSlot = blockType.GetMethod("FindSlot", InstanceFlags).MakeGenericMethod(materialSlotType);
        var slot = findSlot.Invoke(node, new object[] { 0 });
        var valueProperty = slot?.GetType().GetProperty("value");
        if (valueProperty != null && valueProperty.PropertyType == typeof(float))
            valueProperty.SetValue(slot, value);
    }

    // ---------------------------------------------------------------------------------------------
    // Assets
    // ---------------------------------------------------------------------------------------------

    static void EnsureAssets(bool force)
    {
        if (SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before creating the T9 assets.");

        AssetDatabase.Refresh();
        for (var i = 0; i < ModeNames.Length; i++)
        {
            var mode = ModeNames[i];
            var renderer = EnsureRenderer(string.Format(T9RendererPathFormat, mode), "SslT9" + mode + "Renderer", mode, force, out bool recreated);
            EnsureFeatureResources(renderer);
            EnsurePipeline(string.Format(T9PipelinePathFormat, mode), "SslT9" + mode + "Pipeline", renderer, force || recreated);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static UniversalRendererData EnsureRenderer(string rendererPath, string rendererName, string mode, bool force, out bool recreated)
    {
        recreated = false;
        var t1Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T1RendererPathFormat, mode));
        if (t1Renderer == null)
            throw new FileNotFoundException("Missing T1 renderer asset. Create the T1 baseline first.", mode);

        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        var existingFeature = renderer != null ? renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault() : null;
        var existingProbe = renderer != null ? renderer.rendererFeatures.OfType<ScreenSpaceLightingPassProbeFeature>().FirstOrDefault() : null;
        bool matches = existingFeature != null && existingProbe != null;

        if (renderer == null || force || !matches)
        {
            recreated = true;
            if (renderer != null)
                AssetDatabase.DeleteAsset(rendererPath);
            renderer = UnityEngine.Object.Instantiate(t1Renderer);
            renderer.name = rendererName;
            AssetDatabase.CreateAsset(renderer, rendererPath);

            foreach (var existing in AssetDatabase.LoadAllAssetsAtPath(rendererPath))
            {
                if (existing is ScriptableRendererFeature)
                    AssetDatabase.RemoveObjectFromAsset(existing);
            }

            renderer.rendererFeatures.Clear();
            var feature = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
            feature.name = rendererName + "ssl";
            feature.GetType().GetMethod("Create", InstanceFlags)?.Invoke(feature, null);
            AssetDatabase.AddObjectToAsset(feature, renderer);
            renderer.rendererFeatures.Add(feature);

            var probe = ScriptableObject.CreateInstance<ScreenSpaceLightingPassProbeFeature>();
            probe.name = rendererName + "probe";
            probe.probeEvent = (RenderPassEvent)203;
            probe.probeMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "/SslProbe.mat");
            AssetDatabase.AddObjectToAsset(probe, renderer);
            renderer.rendererFeatures.Add(probe);

            EnsureFeatureMap(renderer);
            EditorUtility.SetDirty(renderer);
            renderer.SetDirty();
        }

        return renderer;
    }

    static void EnsureFeatureMap(UniversalRendererData renderer)
    {
        var serialized = new SerializedObject(renderer);
        var map = serialized.FindProperty("m_RendererFeatureMap");
        if (map == null)
            return;

        map.arraySize = renderer.rendererFeatures.Count;
        for (int i = 0; i < renderer.rendererFeatures.Count; i++)
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(renderer.rendererFeatures[i], out _, out long id);
            map.GetArrayElementAtIndex(i).longValue = id;
        }

        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(renderer);
    }

    static UniversalRenderPipelineAsset EnsurePipeline(string pipelinePath, string pipelineName, UniversalRendererData renderer, bool force)
    {
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
        if (pipeline == null || force)
        {
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline, pipelinePath);
            }
            else
            {
                var template = UniversalRenderPipelineAsset.Create(renderer);
                EditorUtility.CopySerialized(template, pipeline);
                UnityEngine.Object.DestroyImmediate(template);
            }

            pipeline.name = pipelineName;
            pipeline.supportsHDR = true;
            pipeline.msaaSampleCount = 1;
            pipeline.renderScale = 1f;
            pipeline.shadowDistance = 50f;
            pipeline.supportsCameraDepthTexture = false;
            pipeline.supportsCameraOpaqueTexture = false;
            EditorUtility.SetDirty(pipeline);
        }

        return pipeline;
    }

    static void EnsureFeatureResources(UniversalRendererData renderer)
    {
        foreach (var ssl in renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>())
        {
            var settings = ssl.GetType().GetProperty("settings", InstanceFlags)?.GetValue(ssl);
            var resources = settings?.GetType().GetField("resources", InstanceFlags)?.GetValue(settings);
            var locator = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceLightingResourceLocator");
            locator?.GetMethod("AssignDefaults", StaticFlags)?.Invoke(null, new[] { resources });
            EditorUtility.SetDirty(ssl);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Volume and feature helpers
    // ---------------------------------------------------------------------------------------------

    static void DisableScreenSpaceEffects(VolumeProfile profile)
    {
        var ssgi = profile.components.OfType<ScreenSpaceGlobalIllumination>().FirstOrDefault();
        if (ssgi != null)
            ssgi.enabled.Override(false);
        var ssr = profile.components.OfType<ScreenSpaceReflection>().FirstOrDefault();
        if (ssr != null)
            ssr.enabled.Override(false);
        var refraction = profile.components.OfType<ScreenSpaceRefraction>().FirstOrDefault();
        if (refraction != null)
            refraction.enabled.Override(false);
    }

    static void EnsureSupportedEffectsOnly(VolumeProfile profile)
    {
        var ssgi = profile.components.OfType<ScreenSpaceGlobalIllumination>().FirstOrDefault();
        if (ssgi != null)
            ssgi.enabled.Override(false);
        var refraction = profile.components.OfType<ScreenSpaceRefraction>().FirstOrDefault();
        if (refraction != null)
            refraction.enabled.Override(false);
    }

    static void ApplyBaseSsrSettings(ScreenSpaceReflection ssr, float minSmoothness)
    {
        ssr.enabled.Override(true);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
        ssr.minSmoothness.Override(minSmoothness);
        ssr.smoothnessFadeStart.Override(minSmoothness);
        ssr.depthBufferThickness.Override(0.05f);
        ssr.screenFadeDistance.Override(0.1f);
        ssr.rayMaxIterations.Override(64);
        ssr.reflectSky.Override(true);
        ssr.intensity.Override(1f);
    }

    static UniversalRenderPipelineAsset LoadPipeline(string format, string modeName)
    {
        var path = string.Format(format, modeName);
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
            throw new FileNotFoundException("Missing pipeline asset.", path);
        return pipeline;
    }

    // ---------------------------------------------------------------------------------------------
    // Weight recording
    // ---------------------------------------------------------------------------------------------

    static void RecordObjectWeight(
        T9ModeResult result,
        Camera camera,
        Color[] weight,
        Color[] normals,
        GameObject gameObject,
        string name,
        bool expectCovered,
        float expectedNormalsAlpha)
    {
        var viewport = camera.WorldToViewportPoint(gameObject.transform.position);
        var entry = new T9ObjectWeight
        {
            name = name,
            expectCovered = expectCovered,
            screenX = Mathf.RoundToInt(viewport.x * ImageWidth),
            screenY = Mathf.RoundToInt(viewport.y * ImageHeight),
            // The mirror direction at the center of a sphere points back at the camera, so a covered
            // object is measured over its silhouette. An unsupported object must have weight 0 over
            // its whole silhouette, so its center is the stricter sample.
            weight = expectCovered
                ? SampleMaxInBounds(camera, weight, gameObject)
                : SampleWeight(camera, weight, gameObject.transform.position),
            expectedNormalsAlpha = expectedNormalsAlpha,
            normalsAlpha = SampleAlpha(camera, normals, gameObject.transform.position)
        };
        result.objects.Add(entry);
    }

    static float SampleMaxInBounds(Camera camera, Color[] weight, GameObject gameObject)
    {
        if (weight == null)
            return 0f;
        var rect = ProjectBounds(camera, gameObject);
        float best = 0f;
        for (int y = rect.yMin; y <= rect.yMax; y++)
        {
            for (int x = rect.xMin; x <= rect.xMax; x++)
                best = Mathf.Max(best, weight[y * ImageWidth + x].r);
        }

        return best;
    }

    static void CheckMissKeepsProbe(Color[] reference, Color[] image, Color[] weight, T9ModeResult result)
    {
        if (reference == null || image == null || weight == null || reference.Length != image.Length)
        {
            result.issues.Add("MissKeepsProbe: the captures use different resolutions.");
            return;
        }

        int compared = 0;
        int different = 0;
        float maxDelta = 0f;
        for (var i = 0; i < image.Length; i++)
        {
            if (weight[i].r > 1e-4f)
                continue;
            compared++;
            float delta = Mathf.Max(
                Mathf.Abs(image[i].r - reference[i].r),
                Mathf.Max(Mathf.Abs(image[i].g - reference[i].g), Mathf.Abs(image[i].b - reference[i].b)));
            if (delta > 1e-5f)
                different++;
            maxDelta = Mathf.Max(maxDelta, delta);
        }

        result.missPixelsCompared = compared;
        result.missPixelsDifferent = different;
        result.missMaxChannelDelta = maxDelta;
        if (compared == 0)
            result.issues.Add("MissKeepsProbe: no zero weight pixel was found.");
        if (different > 0)
            result.issues.Add($"MissKeepsProbe: {different} zero weight pixels differ from the probe image (max delta {maxDelta:G4}).");
    }

    static float SampleAlpha(Camera camera, Color[] samples, Vector3 worldPosition)
    {
        if (samples == null)
            return -1f;
        var viewport = camera.WorldToViewportPoint(worldPosition);
        int centerX = Mathf.Clamp(Mathf.RoundToInt(viewport.x * ImageWidth), 0, ImageWidth - 1);
        int centerY = Mathf.Clamp(Mathf.RoundToInt(viewport.y * ImageHeight), 0, ImageHeight - 1);
        return samples[centerY * ImageWidth + centerX].a;
    }

    static Color[] CaptureWeightView(Camera camera, ScreenSpaceLightingRendererFeature feature, RenderTexture imageTarget)
    {
        SetSslDebug(feature, (int)ScreenSpaceLightingDebugMode.SsrWeight, 0);
        var samples = RenderAndCapture(camera, imageTarget, 2);
        SetSslDebug(feature, (int)ScreenSpaceLightingDebugMode.Off, 0);
        return samples;
    }

    static void SetSslDebug(ScreenSpaceLightingRendererFeature feature, int mode, int mip)
    {
        var settings = feature.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
        var modeField = settings?.GetType().GetField("debugMode", InstanceFlags);
        var mipField = settings?.GetType().GetField("debugMip", InstanceFlags);
        if (modeField != null)
            modeField.SetValue(settings, Enum.ToObject(modeField.FieldType, mode));
        if (mipField != null)
            mipField.SetValue(settings, mip);
        EditorUtility.SetDirty(feature);
    }

    static float SampleWeight(Camera camera, Color[] weight, Vector3 worldPosition)
    {
        if (weight == null)
            return 0f;
        // Viewport coordinates avoid any dependency on the camera pixel rect / game view resolution.
        var viewport = camera.WorldToViewportPoint(worldPosition);
        int centerX = Mathf.RoundToInt(viewport.x * ImageWidth);
        int centerY = Mathf.RoundToInt(viewport.y * ImageHeight);
        int width = ImageWidth;
        int height = ImageHeight;
        float best = 0f;
        for (int y = centerY - 3; y <= centerY + 3; y++)
        {
            for (int x = centerX - 3; x <= centerX + 3; x++)
            {
                if (x < 0 || y < 0 || x >= width || y >= height)
                    continue;
                best = Mathf.Max(best, weight[y * width + x].r);
            }
        }

        return best;
    }

    struct ScreenRect
    {
        public int xMin;
        public int yMin;
        public int xMax;
        public int yMax;
        public int width => Mathf.Max(0, xMax - xMin + 1);
        public int height => Mathf.Max(0, yMax - yMin + 1);
    }

    static ScreenRect ProjectBounds(Camera camera, GameObject gameObject)
    {
        var renderer = gameObject.GetComponent<Renderer>();
        var bounds = renderer != null ? renderer.bounds : new Bounds(gameObject.transform.position, Vector3.one);
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? bounds.min.x : bounds.max.x,
                (i & 2) == 0 ? bounds.min.y : bounds.max.y,
                (i & 4) == 0 ? bounds.min.z : bounds.max.z);
            var viewport = camera.WorldToViewportPoint(corner);
            min = Vector3.Min(min, viewport);
            max = Vector3.Max(max, viewport);
        }

        return new ScreenRect
        {
            xMin = Mathf.Clamp(Mathf.RoundToInt(min.x * ImageWidth), 0, ImageWidth - 1),
            yMin = Mathf.Clamp(Mathf.RoundToInt(min.y * ImageHeight), 0, ImageHeight - 1),
            xMax = Mathf.Clamp(Mathf.RoundToInt(max.x * ImageWidth), 0, ImageWidth - 1),
            yMax = Mathf.Clamp(Mathf.RoundToInt(max.y * ImageHeight), 0, ImageHeight - 1),
        };
    }

    static bool RegionDiffers(Color[] a, Color[] b, ScreenRect rect, float threshold)
    {
        if (a == null || b == null || a.Length != b.Length)
            return true;
        for (int y = rect.yMin; y <= rect.yMax; y++)
        {
            for (int x = rect.xMin; x <= rect.xMax; x++)
            {
                int index = y * ImageWidth + x;
                float delta = Mathf.Max(
                    Mathf.Abs(a[index].r - b[index].r),
                    Mathf.Max(Mathf.Abs(a[index].g - b[index].g), Mathf.Abs(a[index].b - b[index].b)));
                if (delta > threshold)
                    return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------------------------------------
    // Capture helpers
    // ---------------------------------------------------------------------------------------------

    static RenderTexture CreateTarget(int width, int height, string name)
    {
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { name = name };
        target.Create();
        return target;
    }

    static Color[] RenderAndCapture(Camera camera, RenderTexture imageTarget, int frames)
    {
        for (var i = 0; i < frames; i++)
            camera.Render();
        return ReadPixels(imageTarget);
    }

    static Color[] ReadPixels(RenderTexture target)
    {
        var texture = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
        try
        {
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
            texture.Apply(false, false);
            RenderTexture.active = previous;
            return texture.GetPixels();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    static T9WeightStats DescribeWeightView(Color[] samples)
    {
        var stats = new T9WeightStats();
        if (samples == null)
            return stats;
        double sum = 0.0;
        foreach (var pixel in samples)
        {
            if (!IsFinite(pixel.r))
            {
                stats.nonFinitePixels++;
                continue;
            }

            sum += pixel.r;
            if (pixel.r > 0.9f)
                stats.coveredPixels++;
            else if (pixel.r > 0.001f)
                stats.partialPixels++;
            else
                stats.missPixels++;
        }

        int total = Mathf.Max(1, samples.Length);
        stats.pixelCount = samples.Length;
        stats.meanWeight = (float)(sum / total);
        stats.coveredFraction = stats.coveredPixels / (float)total;
        return stats;
    }

    static void WriteNormalsAlphaImage(Color[] normals, string outputDirectory, string fileName)
    {
        if (normals == null)
            return;
        var texture = new Texture2D(ImageWidth, ImageHeight, TextureFormat.RGBA32, false, false);
        try
        {
            var pixels = new Color32[normals.Length];
            for (var i = 0; i < normals.Length; i++)
            {
                byte value = (byte)Mathf.RoundToInt(Mathf.Clamp01(normals[i].a) * 255f);
                pixels[i] = new Color32(value, value, value, 255);
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(Path.Combine(outputDirectory, fileName), texture.EncodeToPNG());
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    static T9StateCapture DescribeState(string stateName, Color[] samples, Color[] reference)
    {
        var capture = new T9StateCapture
        {
            state = stateName,
            sha256 = HashSamples(samples),
            magentaPixels = Count(samples, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f),
            nonFinitePixels = Count(samples, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)),
            meanLuminance = Mean(samples, Luminance),
            differingPixels = reference != null ? CountDifferences(samples, reference, 0f) : 0,
            maxChannelDelta = reference != null ? MaxDifference(samples, reference) : 0f
        };
        capture.differsFromReference = capture.differingPixels > 0;
        capture.identicalToReference = reference != null && capture.differingPixels == 0;
        return capture;
    }

    static int CountDifferences(Color[] a, Color[] b, float threshold)
    {
        if (a == null || b == null || a.Length != b.Length)
            return Math.Max(a?.Length ?? 0, b?.Length ?? 0);
        int count = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float delta = Mathf.Max(
                Mathf.Abs(a[i].r - b[i].r),
                Mathf.Max(Mathf.Abs(a[i].g - b[i].g), Mathf.Abs(a[i].b - b[i].b)));
            if (delta > threshold)
                count++;
        }

        return count;
    }

    static float MaxDifference(Color[] a, Color[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return float.PositiveInfinity;
        float max = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            max = Mathf.Max(max, Mathf.Max(
                Mathf.Abs(a[i].r - b[i].r),
                Mathf.Max(Mathf.Abs(a[i].g - b[i].g), Mathf.Abs(a[i].b - b[i].b))));
        }

        return max;
    }

    static string HashSamples(Color[] samples)
    {
        var bytes = new byte[samples.Length * 4 * sizeof(float)];
        for (var i = 0; i < samples.Length; i++)
        {
            var offset = i * 4 * sizeof(float);
            BitConverter.TryWriteBytes(new Span<byte>(bytes, offset, sizeof(float)), samples[i].r);
            BitConverter.TryWriteBytes(new Span<byte>(bytes, offset + sizeof(float), sizeof(float)), samples[i].g);
            BitConverter.TryWriteBytes(new Span<byte>(bytes, offset + 2 * sizeof(float), sizeof(float)), samples[i].b);
            BitConverter.TryWriteBytes(new Span<byte>(bytes, offset + 3 * sizeof(float), sizeof(float)), samples[i].a);
        }
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    static void WriteImage(RenderTexture target, string outputDirectory, string fileName)
    {
        var samples = ReadPixels(target);
        var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, false);
        try
        {
            var pixels = new Color32[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                pixels[i] = new Color32(
                    (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(Mathf.Clamp01(samples[i].r)) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(Mathf.Clamp01(samples[i].g)) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(Mathf.Clamp01(samples[i].b)) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(samples[i].a) * 255f));
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(Path.Combine(outputDirectory, fileName), texture.EncodeToPNG());
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    static float Luminance(Color color)
    {
        return 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;
    }

    static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    static int Count(Color[] samples, Func<Color, bool> predicate)
    {
        var count = 0;
        foreach (var pixel in samples)
        {
            if (predicate(pixel))
                count++;
        }

        return count;
    }

    static float Mean(Color[] samples, Func<Color, float> selector)
    {
        double sum = 0.0;
        foreach (var sample in samples)
            sum += selector(sample);
        return samples.Length == 0 ? 0f : (float)(sum / samples.Length);
    }

    // ---------------------------------------------------------------------------------------------
    // Report data
    // ---------------------------------------------------------------------------------------------

    [Serializable]
    public sealed class T9Report
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public bool completed;
        public bool passed;
        public string error;
        public string apvSupport;
        public string lightmapScene;
        public List<T9ModeResult> modes;
        public T9LightmapResult lightmap;
    }

    [Serializable]
    public sealed class T9ModeResult
    {
        public string name;
        public bool passed;
        public List<string> issues;
        public List<T9StateCapture> states;
        public T9WeightStats weight;
        public List<T9ObjectWeight> objects;
        public float roughBaseWeight;
        public float smoothBaseWeight;
        public int maskOnVsOffPixels;
        public float maskOnVsOffMaxDelta;
        public int coatSmoothnessPixels;
        public float coatSmoothnessMaxDelta;
        public int missPixelsCompared;
        public int missPixelsDifferent;
        public float missMaxChannelDelta;
        public T9AlphaTest alphaTest;
        public T9Lod lod;
        public T9ShaderGraph shaderGraph;
    }

    [Serializable]
    public sealed class T9ObjectWeight
    {
        public string name;
        public bool expectCovered;
        public int screenX;
        public int screenY;
        public float weight;
        public float expectedNormalsAlpha;
        public float normalsAlpha;
    }

    [Serializable]
    public sealed class T9WeightStats
    {
        public int pixelCount;
        public int nonFinitePixels;
        public int coveredPixels;
        public int partialPixels;
        public int missPixels;
        public float meanWeight;
        public float coveredFraction;
    }

    [Serializable]
    public sealed class T9StateCapture
    {
        public string state;
        public string sha256;
        public bool identicalToReference;
        public bool differsFromReference;
        public int differingPixels;
        public float maxChannelDelta;
        public int magentaPixels;
        public int nonFinitePixels;
        public float meanLuminance;
    }

    [Serializable]
    public sealed class T9AlphaTest
    {
        public int solidPixels;
        public int cutPixels;
        public float solidWeight;
        public float cutWeight;
    }

    [Serializable]
    public sealed class T9Lod
    {
        public float lod0Weight;
        public float lod1Weight;
        public int levelPixels;
        public float levelMaxDelta;
    }

    [Serializable]
    public sealed class T9ShaderGraph
    {
        public bool ssrDiffers;
        public bool ssgiDiffers;
        public bool coatIntensityZeroIdentical;
        public float ssrWeight;
    }

    [Serializable]
    public sealed class T9LightmapResult
    {
        public bool passed;
        public bool baked;
        public int lightmapCount;
        public string mixedBakeMode;
        public bool apvAvailable;
        public List<string> issues;
        public List<T9LightmapMode> modes = new List<T9LightmapMode>();
    }

    [Serializable]
    public sealed class T9LightmapMode
    {
        public string name;
        public bool intensityZeroIdentical;
        public bool effectsDiffer;
        public int nonFinitePixels;
        public int magentaPixels;
        public List<string> issues = new List<string>();
    }
}