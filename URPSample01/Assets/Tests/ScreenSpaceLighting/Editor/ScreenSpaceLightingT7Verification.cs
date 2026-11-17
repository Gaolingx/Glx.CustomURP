using System;
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

// T7 acceptance: opaque Screen Space Reflections (Approximation) in the Forward, Forward+ and
// Deferred renderers. The checks cover the trace/reproject dispatch chain, the radiance + weight
// contract (a miss keeps the reflection probe and never darkens the image), the roughness fade
// gradient for metal and non-metal materials, first frame and camera cut fallbacks, the debug views
// and the consistency between the debug view and the texture materials consume.
public static class ScreenSpaceLightingT7Verification
{
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string OutputRoot = "PALN/validation/T7";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string T1RendererPathFormat = RendererRoot + "/Ssl{0}Renderer.asset";
    const string T7RendererPathFormat = RendererRoot + "/SslT7{0}Renderer.asset";
    const string T7PipelinePathFormat = RendererRoot + "/SslT7{0}Pipeline.asset";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 32;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tests/Screen Space Lighting/Create T7 Assets")]
    public static void CreateT7Assets()
    {
        EnsureAssets(force: true);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [MenuItem("Tests/Screen Space Lighting/Run T7 Verification")]
    public static void RunT7Verification()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);
        var report = new T7Report
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            passed = true,
            modes = new List<T7ModeResult>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture imageTarget = null;
        RenderTexture referenceTarget = null;
        Volume volume = null;
        VolumeProfile originalProfile = null;
        VolumeProfile testProfile = null;
        GameObject reflectionRig = null;
        List<Material> rigMaterials = new List<Material>();
        try
        {
            EnsureAssets(force: false);
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
            testProfile.name = originalProfile.name + " (T7)";
            volume.sharedProfile = testProfile;
            DisableOtherScreenSpaceEffects(testProfile);

            // The authored scene has no large mirror surface. The rig adds a reflective floor, four
            // metal spheres with different smoothness, one smooth dielectric sphere and an emissive
            // backdrop, so the trace hits on-screen geometry instead of escaping to the sky only.
            reflectionRig = CreateReflectionRig(camera, rigMaterials);

            imageTarget = CreateTarget(ImageWidth, ImageHeight, "SslT7Image");
            referenceTarget = CreateTarget(ImageWidth, ImageHeight, "SslT7Reference");
            camera.targetTexture = imageTarget;
            camera.aspect = (float)ImageWidth / ImageHeight;
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;
            cameraData.resetHistory = true;

            for (int modeIndex = 0; modeIndex < ModeNames.Length; modeIndex++)
            {
                var mode = CaptureMode(
                    ModeNames[modeIndex],
                    camera,
                    cameraData,
                    testProfile,
                    imageTarget,
                    referenceTarget,
                    reflectionRig,
                    projectRoot,
                    outputDirectory);
                report.modes.Add(mode);
                report.passed &= mode.passed;
                Debug.Log($"T7 {mode.name}: passed={mode.passed} issues={mode.issues.Count}");
            }

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
            if (reflectionRig != null)
                UnityEngine.Object.DestroyImmediate(reflectionRig);
            foreach (var material in rigMaterials)
            {
                if (material != null)
                    UnityEngine.Object.DestroyImmediate(material);
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
            if (referenceTarget != null)
            {
                referenceTarget.Release();
                UnityEngine.Object.DestroyImmediate(referenceTarget);
            }

            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
            AssetDatabase.SaveAssets();
            File.WriteAllText(Path.Combine(outputDirectory, "T7Verification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"T7 verification finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    static GameObject CreateReflectionRig(Camera camera, List<Material> materials)
    {
        var root = new GameObject("T7 Reflection Rig");
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            return root;

        var floorMaterial = CreateLitMaterial(shader, "T7 Mirror Floor", new Color(0.75f, 0.78f, 0.82f), 1.0f, 0.95f);
        var backdropMaterial = CreateLitMaterial(shader, "T7 Reflect Backdrop", new Color(0.05f, 0.05f, 0.05f), 0f, 0.15f);
        backdropMaterial.EnableKeyword("_EMISSION");
        backdropMaterial.SetColor("_EmissionColor", new Color(3.0f, 1.8f, 0.6f));
        materials.Add(floorMaterial);
        materials.Add(backdropMaterial);

        AddBox(root, floorMaterial, "T7 Mirror Floor", new Vector3(0f, 0.04f, 0.5f), new Vector3(16f, 0.04f, 12f));
        AddBox(root, backdropMaterial, "T7 Reflect Backdrop", new Vector3(0f, 3.0f, 4.2f), new Vector3(11f, 6f, 0.2f));

        // Row perpendicular to the camera view. The smoothness values produce a measurable fade
        // gradient between minSmoothness (0.5) and smoothnessFadeStart (0.9).
        var viewDirection = (Vector3.zero - camera.transform.position).normalized;
        var right = Vector3.Cross(Vector3.up, viewDirection).normalized;
        var center = camera.transform.position + viewDirection * 13f;
        center.y = 1.4f;
        var smoothness = new[] { 0.95f, 0.80f, 0.65f, 0.40f };
        for (int i = 0; i < smoothness.Length; i++)
        {
            var offset = right * ((i - 1.5f) * 2.2f);
            var material = CreateLitMaterial(shader, "T7 Metal " + smoothness[i].ToString("0.00"), Color.white, 1.0f, smoothness[i]);
            materials.Add(material);
            AddSphere(root, material, "T7 Metal Sphere " + i, center + offset, 1.1f);
        }

        // A smooth dielectric sphere proves that the effect is not gated on metallic.
        var dielectric = CreateLitMaterial(shader, "T7 Dielectric", new Color(0.15f, 0.25f, 0.45f), 0f, 0.95f);
        materials.Add(dielectric);
        AddSphere(root, dielectric, "T7 Dielectric Sphere", center + right * 4.4f + Vector3.up * 1.2f, 0.9f);
        return root;
    }

    static Material CreateLitMaterial(Shader shader, string name, Color color, float metallic, float smoothness)
    {
        var material = new Material(shader) { name = name, color = color };
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        return material;
    }

    static void AddBox(GameObject parent, Material material, string name, Vector3 position, Vector3 size)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent.transform, false);
        box.transform.position = position;
        box.transform.localScale = size;
        var renderer = box.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = material;
    }

    static void AddSphere(GameObject parent, Material material, string name, Vector3 position, float scale)
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = name;
        sphere.transform.SetParent(parent.transform, false);
        sphere.transform.position = position;
        sphere.transform.localScale = Vector3.one * scale;
        var renderer = sphere.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = material;
    }

    static T7ModeResult CaptureMode(
        string modeName,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        VolumeProfile profile,
        RenderTexture imageTarget,
        RenderTexture referenceTarget,
        GameObject reflectionRig,
        string projectRoot,
        string outputDirectory)
    {
        var result = new T7ModeResult
        {
            name = modeName,
            issues = new List<string>(),
            states = new List<T7StateCapture>(),
            historyChecks = new List<T7HistorySnapshot>(),
            debugImages = new List<T7DebugImage>(),
            materialProbes = new List<T7MaterialProbe>()
        };

        var ssr = profile.components.OfType<ScreenSpaceReflection>().First();
        var pipeline = LoadPipeline(T7PipelinePathFormat, modeName);
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T7RendererPathFormat, modeName));
        if (renderer == null)
            throw new FileNotFoundException("Missing T7 renderer asset. Run Create T7 Assets first.");

        var feature = renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (feature == null)
            throw new InvalidOperationException("The T7 renderer is missing the Screen Space Lighting feature.");

        ApplyReferenceSettings(ssr);

        // Reference: same renderer and scene, SSR volume disabled. The feature still runs its depth
        // and color history passes, so this is the projection / sky path the material must keep.
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        ssr.enabled.Override(false);
        SetTarget(camera, referenceTarget);
        cameraData.resetHistory = true;
        var reference = RenderAndCapture(camera, referenceTarget, WarmupFrames);
        result.states.Add(DescribeState("Reference", reference, null));
        WriteImage(referenceTarget, outputDirectory, "T7_" + modeName + "_Reference.png");

        ssr.enabled.Override(true);
        SetTarget(camera, imageTarget);
        cameraData.resetHistory = true;

        var passType = GetPluginType("ScreenSpaceLightingSsrPass");
        var setupType = GetPluginType("ScreenSpaceLightingSetupPass");
        int executions = ReadStaticInt(passType, "s_EditorExecuteCount");
        int trace = ReadStaticInt(passType, "s_EditorTraceDispatchCount");
        int reproject = ReadStaticInt(passType, "s_EditorReprojectDispatchCount");
        int setupExecutions = ReadStaticInt(setupType, "s_EditorExecuteCount");
        int boundFrames = ReadStaticInt(setupType, "s_EditorSsrBoundCount");

        var samples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        var state = DescribeState("SsrFullRes", samples, reference);
        result.states.Add(state);
        WriteImage(imageTarget, outputDirectory, "T7_" + modeName + "_SsrFullRes.png");

        result.ssrExecutions = ReadStaticInt(passType, "s_EditorExecuteCount") - executions;
        result.traceDispatches = ReadStaticInt(passType, "s_EditorTraceDispatchCount") - trace;
        result.reprojectDispatches = ReadStaticInt(passType, "s_EditorReprojectDispatchCount") - reproject;
        result.setupExecutions = ReadStaticInt(setupType, "s_EditorExecuteCount") - setupExecutions;
        result.ssrBoundFrames = ReadStaticInt(setupType, "s_EditorSsrBoundCount") - boundFrames;

        if (result.ssrExecutions <= 0)
            result.issues.Add("SsrFullRes: the SSR pass never executed.");
        if (result.traceDispatches <= 0 || result.reprojectDispatches <= 0)
            result.issues.Add("SsrFullRes: the trace or reproject kernel was never dispatched.");
        if (result.ssrBoundFrames <= 0)
            result.issues.Add("SsrFullRes: the setup pass never bound the SSR texture and keyword.");
        if (!state.differsFromReference)
            result.issues.Add("SsrFullRes: the SSR contribution did not change the image.");
        if (state.magentaPixels > 0 || state.nonFinitePixels > 0)
            result.issues.Add("SsrFullRes: the image contains invalid pixels.");

        var history = GetHistory(feature, camera);
        if (history == null)
        {
            result.issues.Add("SsrFullRes: the camera did not receive a Screen Space Lighting history entry.");
        }
        else
        {
            var snapshot = SnapshotHistory("AfterWarmup", history);
            result.historyChecks.Add(snapshot);
            if (!snapshot.ssrResourcesAllocated)
                result.issues.Add("SsrFullRes: the SSR output resources were not allocated.");
            if (snapshot.ssrWidth != ImageWidth || snapshot.ssrHeight != ImageHeight)
                result.issues.Add($"SsrFullRes: the SSR buffer is {snapshot.ssrWidth}x{snapshot.ssrHeight}, expected {ImageWidth}x{ImageHeight}.");
            if (snapshot.readSlot < 0)
                result.issues.Add("SsrFullRes: the previous color pyramid was never captured.");
        }

        var outputHandle = GetSsrOutputTexture(feature);
        var outputPixels = ReadRTHandle(outputHandle);
        result.weight = DescribeWeight(outputPixels);
        result.outputTextureBytes = DescribeTextureBytes(outputHandle);
        if (result.weight.nonFinitePixels > 0)
            result.issues.Add("SsrFullRes: the SSR output texture contains NaN or Inf.");
        if (result.weight.negativePixels > 0)
            result.issues.Add("SsrFullRes: the SSR output texture contains negative radiance.");
        if (result.weight.coveredFraction < 0.0005f)
            result.issues.Add($"SsrFullRes: only {result.weight.coveredFraction:P2} of pixels received a screen-space reflection.");
        if (result.weight.missFraction < 0.01f)
            result.issues.Add("SsrFullRes: no pixel fell back to the reflection probe; the weight is not used.");

        CheckMissKeepsProbe(reference, samples, outputPixels, result);
        CheckMaterialProbes(camera, reflectionRig, reference, outputPixels, ssr, result);
        CaptureDebugViews(camera, imageTarget, feature, outputPixels, result, outputDirectory, modeName);
        CheckIntensityZero(camera, imageTarget, reference, ssr, result);
        CheckIntensityLinearity(camera, imageTarget, reference, ssr, feature, result);
        CheckFirstFrameFallback(camera, imageTarget, cameraData, reference, result);
        CheckCameraCut(camera, cameraData, imageTarget, feature, reference, result);

        ApplyReferenceSettings(ssr);
        result.passed = result.issues.Count == 0;
        return result;
    }

    static void CheckMissKeepsProbe(Color[] reference, Color[] ssr, Color[] weightPixels, T7ModeResult result)
    {
        if (reference == null || ssr == null || weightPixels == null ||
            reference.Length != ssr.Length || weightPixels.Length != ssr.Length)
        {
            result.issues.Add("MissKeepsProbe: the captures use different resolutions.");
            return;
        }

        var compared = 0;
        var different = 0;
        var maxDelta = 0f;
        for (var i = 0; i < ssr.Length; i++)
        {
            if (weightPixels[i].a > 1e-6f)
                continue;

            compared++;
            var delta = Mathf.Max(
                Mathf.Abs(ssr[i].r - reference[i].r),
                Mathf.Max(Mathf.Abs(ssr[i].g - reference[i].g), Mathf.Abs(ssr[i].b - reference[i].b)));
            if (delta > 1e-5f)
                different++;
            maxDelta = Mathf.Max(maxDelta, delta);
        }

        result.missPixelsCompared = compared;
        result.missPixelsDifferent = different;
        result.missMaxChannelDelta = maxDelta;
        if (compared == 0)
            result.issues.Add("MissKeepsProbe: no pixel had zero weight.");
        if (different > 0)
            result.issues.Add($"MissKeepsProbe: {different} zero weight pixels differ from the probe image (max delta {maxDelta}).");
    }

    static void CheckMaterialProbes(Camera camera, GameObject rig, Color[] reference, Color[] weights,
        ScreenSpaceReflection settings, T7ModeResult result)
    {
        // The trace only covers rays that stay on screen, so the fraction of a sphere that receives a
        // reflection depends on where the sphere sits, not on its smoothness. The roughness fade is
        // therefore asserted on the per-pixel weight (maximum over the probe rect) and against the
        // fade the shader receives, which is the invariant HDRP defines.
        float minSmoothness = settings.minSmoothness.value;
        float roughnessFadeEnd = 1f - minSmoothness;
        float roughnessFadeStart = 1f - settings.smoothnessFadeStart.value;
        float roughnessFadeLength = roughnessFadeEnd - roughnessFadeStart;
        float fadeRcpLength = roughnessFadeLength != 0f ? 1f / roughnessFadeLength : 0f;
        float fadeEndTimesRcpLength = roughnessFadeLength != 0f ? roughnessFadeEnd * fadeRcpLength : 1f;

        if (rig == null || reference == null || weights == null || reference.Length != weights.Length)
            return;

        var renderers = new List<(string name, float smoothness, bool metal, Renderer renderer)>();
        foreach (var transform in rig.GetComponentsInChildren<Transform>())
        {
            if (!transform.name.StartsWith("T7 Metal Sphere", StringComparison.Ordinal) &&
                !transform.name.StartsWith("T7 Dielectric Sphere", StringComparison.Ordinal))
                continue;

            var renderer = transform.GetComponent<Renderer>();
            if (renderer == null)
                continue;

            var smoothness = renderer.sharedMaterial != null ? renderer.sharedMaterial.GetFloat("_Smoothness") : 0f;
            var metallic = renderer.sharedMaterial != null ? renderer.sharedMaterial.GetFloat("_Metallic") : 0f;
            renderers.Add((transform.name, smoothness, metallic > 0.5f, renderer));
        }

        renderers.Sort((a, b) => b.smoothness.CompareTo(a.smoothness));
        foreach (var item in renderers)
        {
            if (!TryGetScreenRect(camera, item.renderer, out int x0, out int y0, out int x1, out int y1))
                continue;

            double weightSum = 0.0;
            double referenceSum = 0.0;
            float maxWeight = 0f;
            int counted = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int index = y * ImageWidth + x;
                    weightSum += weights[index].a;
                    maxWeight = Mathf.Max(maxWeight, weights[index].a);
                    referenceSum += Luminance(reference[index]);
                    counted++;
                }
            }

            if (counted == 0)
                continue;

            var probe = new T7MaterialProbe
            {
                name = item.name,
                smoothness = item.smoothness,
                metallic = item.metal,
                pixels = counted,
                meanWeight = (float)(weightSum / counted),
                maxWeight = maxWeight,
                expectedFade = Smoothstep01(Mathf.Clamp01(fadeEndTimesRcpLength - (1f - item.smoothness) * fadeRcpLength)),
                meanReferenceLuminance = (float)(referenceSum / counted)
            };
            result.materialProbes.Add(probe);
        }

        var metal = result.materialProbes.Where(p => p.metallic).OrderByDescending(p => p.smoothness).ToList();
        if (metal.Count >= 2)
        {
            for (int i = 1; i < metal.Count; i++)
            {
                if (metal[i].smoothness >= minSmoothness - 1e-4f && metal[i].maxWeight > metal[i - 1].maxWeight + 1e-4f)
                {
                    result.issues.Add(
                        $"RoughnessFade: {metal[i].name} (smoothness {metal[i].smoothness:F2}) has weight " +
                        $"{metal[i].maxWeight:F4} above {metal[i - 1].name} ({metal[i - 1].maxWeight:F4}).");
                }
            }
        }

        foreach (var probe in metal.Where(p => p.smoothness >= minSmoothness - 1e-4f))
        {
            // A covered pixel carries exactly the fade, so no pixel may exceed it.
            if (probe.maxWeight > probe.expectedFade + 0.02f)
                result.issues.Add($"RoughnessFade: {probe.name} has weight {probe.maxWeight:F4} above the roughness fade {probe.expectedFade:F4}.");
        }

        var rough = metal.Where(p => p.smoothness < minSmoothness).ToList();
        foreach (var probe in rough)
        {
            if (probe.maxWeight > 1e-4f)
                result.issues.Add($"RoughnessFade: {probe.name} is below minSmoothness but has weight {probe.maxWeight:F4}.");
        }

        var dielectric = result.materialProbes.FirstOrDefault(p => !p.metallic && p.smoothness >= 0.9f);
        if (dielectric == null)
            result.issues.Add("NonMetal: the dielectric probe sphere was not measured.");
        else if (dielectric.meanWeight <= 1e-4f)
            result.issues.Add("NonMetal: the smooth dielectric sphere received no screen-space reflection.");

        var smoothMetal = metal.FirstOrDefault(p => p.smoothness >= 0.9f);
        if (smoothMetal == null)
            result.issues.Add("Metal: the smooth metal probe sphere was not measured.");
        else if (smoothMetal.maxWeight < 0.9f)
            result.issues.Add($"Metal: the smooth metal sphere weight {smoothMetal.maxWeight:F4} does not reach the full fade.");
    }

    static bool TryGetScreenRect(Camera camera, Renderer renderer, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = y0 = 0;
        x1 = y1 = 1;
        var bounds = renderer.bounds;
        var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, 0f);
        var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, 0f);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? bounds.min.x : bounds.max.x,
                (i & 2) == 0 ? bounds.min.y : bounds.max.y,
                (i & 4) == 0 ? bounds.min.z : bounds.max.z);
            var screen = camera.WorldToScreenPoint(corner);
            if (screen.z <= 0f)
                return false;
            min = Vector3.Min(min, screen);
            max = Vector3.Max(max, screen);
        }

        float insetX = (max.x - min.x) * 0.3f;
        float insetY = (max.y - min.y) * 0.3f;
        x0 = Mathf.Clamp(Mathf.RoundToInt(min.x + insetX), 0, ImageWidth - 1);
        x1 = Mathf.Clamp(Mathf.RoundToInt(max.x - insetX), x0 + 1, ImageWidth);
        y0 = Mathf.Clamp(Mathf.RoundToInt(min.y + insetY), 0, ImageHeight - 1);
        y1 = Mathf.Clamp(Mathf.RoundToInt(max.y - insetY), y0 + 1, ImageHeight);
        return x1 > x0 && y1 > y0;
    }

    static void CaptureDebugViews(
        Camera camera,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        Color[] consumedPixels,
        T7ModeResult result,
        string outputDirectory,
        string modeName)
    {
        var raw = CaptureDebugView(camera, imageTarget, feature, 11, "SsrRaw", result, outputDirectory, modeName);
        CaptureDebugView(camera, imageTarget, feature, 12, "SsrWeight", result, outputDirectory, modeName);
        SetSslDebug(feature, 0, 0);

        var outputHandle = GetSsrOutputTexture(feature);
        var outputPixels = ReadRTHandle(outputHandle);
        if (raw != null && outputPixels != null && outputPixels.Length == raw.Length)
        {
            float maxDelta = 0f;
            int compared = 0;
            int mismatch = 0;
            for (int i = 0; i < outputPixels.Length; i++)
            {
                float delta = Mathf.Max(
                    Mathf.Abs(outputPixels[i].r - raw[i].r),
                    Mathf.Max(Mathf.Abs(outputPixels[i].g - raw[i].g), Mathf.Abs(outputPixels[i].b - raw[i].b)));
                if (delta > 1e-3f)
                    mismatch++;
                maxDelta = Mathf.Max(maxDelta, delta);
                compared++;
            }

            result.debugConsumptionPixels = compared;
            result.debugConsumptionMismatchPixels = mismatch;
            result.debugConsumptionMaxDelta = maxDelta;
            if (mismatch > compared / 100)
                result.issues.Add($"DebugViewConsistency: {mismatch} pixels of the debug view differ from the consumed SSR texture.");
        }

        if (consumedPixels != null && outputPixels != null && consumedPixels.Length == outputPixels.Length)
        {
            double consumedMean = 0.0;
            double outputMean = 0.0;
            for (int i = 0; i < outputPixels.Length; i++)
            {
                consumedMean += Luminance(consumedPixels[i]);
                outputMean += Luminance(outputPixels[i]);
            }

            result.consumedLuminance = (float)(consumedMean / outputPixels.Length);
            result.outputLuminance = (float)(outputMean / outputPixels.Length);
        }
    }

    static Color[] CaptureDebugView(
        Camera camera,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        int debugMode,
        string viewName,
        T7ModeResult result,
        string outputDirectory,
        string modeName)
    {
        SetSslDebug(feature, debugMode, 0);
        RenderAndCapture(camera, imageTarget, 2);
        var samples = ReadPixels(imageTarget);
        var info = new T7DebugImage
        {
            view = viewName,
            file = "T7_" + modeName + "_" + viewName + ".png",
            sha256 = HashSamples(samples),
            magentaPixels = Count(samples, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f),
            nonFinitePixels = Count(samples, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)),
            meanLuminance = Mean(samples, Luminance),
            minLuminance = Min(samples, Luminance),
            maxLuminance = Max(samples, Luminance)
        };
        result.debugImages.Add(info);
        WriteImage(imageTarget, outputDirectory, info.file);

        if (info.magentaPixels > 0 || info.nonFinitePixels > 0)
            result.issues.Add(viewName + ": the debug view contains invalid pixels.");
        if (info.meanLuminance <= 0f)
            result.issues.Add(viewName + ": the debug view is completely black.");
        return samples;
    }

    static void CheckIntensityZero(
        Camera camera,
        RenderTexture imageTarget,
        Color[] reference,
        ScreenSpaceReflection settings,
        T7ModeResult result)
    {
        settings.intensity.Override(0f);
        camera.GetComponent<UniversalAdditionalCameraData>().resetHistory = true;

        var setupType = GetPluginType("ScreenSpaceLightingSetupPass");
        int boundBefore = ReadStaticInt(setupType, "s_EditorSsrBoundCount");
        var samples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        int boundAfter = ReadStaticInt(setupType, "s_EditorSsrBoundCount");

        var state = DescribeState("ZeroIntensityRestoresBaseline", samples, reference);
        result.states.Add(state);
        if (!state.identicalToReference)
            result.issues.Add($"ZeroIntensityRestoresBaseline: {state.differingPixels} pixels changed (max delta {state.maxChannelDelta}).");
        if (boundAfter <= boundBefore)
            result.issues.Add("ZeroIntensityRestoresBaseline: the SSR texture was not bound, so the weight path was not exercised.");

        settings.intensity.Override(1f);
    }

    // The material blend is linear in the volume intensity: the final image must be
    // reference + intensity * (ssr - reference), which proves the weight is applied once and the
    // reflection radiance is not scaled by any other factor between compute and material.
    //
    // Both samples are captured on the first frame after a history reset that actually produced SSR.
    // That frame reads the color history of the preceding (SSR-free) frame, so the reflection
    // radiance does not contain last frame's reflection and the comparison is not contaminated by
    // the intentional color-history feedback of the approximation.
    static void CheckIntensityLinearity(
        Camera camera,
        RenderTexture imageTarget,
        Color[] reference,
        ScreenSpaceReflection settings,
        ScreenSpaceLightingRendererFeature feature,
        T7ModeResult result)
    {
        var halfIntensity = CaptureFirstSsrFrame(camera, imageTarget, feature, settings, 0.5f, result);
        var fullIntensity = CaptureFirstSsrFrame(camera, imageTarget, feature, settings, 1.0f, result);
        settings.intensity.Override(1f);

        if (reference == null || halfIntensity == null || fullIntensity == null ||
            reference.Length != halfIntensity.Length || halfIntensity.Length != fullIntensity.Length)
        {
            if (result.linearityPixels == 0)
                result.issues.Add("IntensityLinearity: no clean first SSR frame could be captured.");
            return;
        }

        double errorSum = 0.0;
        double signalSum = 0.0;
        double signalHalfSum = 0.0;
        double signalFullSum = 0.0;
        int compared = 0;
        for (int i = 0; i < halfIntensity.Length; i++)
        {
            var deltaHalf = halfIntensity[i].r - reference[i].r;
            var deltaFull = fullIntensity[i].r - reference[i].r;
            // Ignore pixels where the half precision render target quantizes the signal.
            if (Mathf.Abs(deltaHalf) < 1e-3f)
                continue;
            errorSum += Mathf.Abs(deltaFull - 2f * deltaHalf);
            signalSum += Mathf.Abs(deltaHalf);
            signalHalfSum += Mathf.Abs(deltaHalf);
            signalFullSum += Mathf.Abs(deltaFull);
            compared++;
        }

        result.linearityPixels = compared;
        result.linearityRelativeError = signalSum > 0.0 ? (float)(errorSum / signalSum) : 0f;
        result.linearityMeanHalf = compared > 0 ? (float)(signalHalfSum / compared) : 0f;
        result.linearityMeanFull = compared > 0 ? (float)(signalFullSum / compared) : 0f;
        result.linearityRatio = signalHalfSum > 0.0 ? (float)(signalFullSum / signalHalfSum) : 0f;
        if (compared < 100)
            result.issues.Add("IntensityLinearity: not enough changed pixels to measure the intensity response.");
        if (result.linearityRelativeError > 0.05f)
            result.issues.Add($"IntensityLinearity: doubling intensity changed the signal by {result.linearityRelativeError:P1}, expected 2x.");
    }

    static Color[] CaptureFirstSsrFrame(
        Camera camera,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        ScreenSpaceReflection settings,
        float intensity,
        T7ModeResult result)
    {
        settings.intensity.Override(intensity);
        var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
        cameraData.resetHistory = true;

        var pass = GetSsrPass(feature);
        var producedProperty = pass?.GetType().GetProperty("producedOutput", InstanceFlags);
        for (int frame = 0; frame < 4; frame++)
        {
            camera.Render();
            if (producedProperty != null && (bool)producedProperty.GetValue(pass))
                return ReadPixels(imageTarget);
        }

        return null;
    }

    static void CheckFirstFrameFallback(
        Camera camera,
        RenderTexture imageTarget,
        UniversalAdditionalCameraData cameraData,
        Color[] reference,
        T7ModeResult result)
    {
        cameraData.resetHistory = true;
        var samples = RenderAndCapture(camera, imageTarget, 1);
        var state = DescribeState("FirstFrameAfterReset", samples, reference);
        result.states.Add(state);
        if (!state.identicalToReference)
            result.issues.Add($"FirstFrameAfterReset: {state.differingPixels} pixels differ from the probe image (max delta {state.maxChannelDelta}).");

        RenderAndCapture(camera, imageTarget, WarmupFrames);
    }

    static void CheckCameraCut(
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        Color[] reference,
        T7ModeResult result)
    {
        var history = GetHistory(feature, camera);
        int resetCountBefore = history != null ? ReadInt(history, "resetCount") : 0;

        var previousPosition = camera.transform.position;
        camera.transform.position = previousPosition + new Vector3(2.5f, 0.5f, 0f);
        cameraData.resetHistory = true;
        var samples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        camera.transform.position = previousPosition;

        var state = DescribeState("CameraCut", samples, reference);
        result.states.Add(state);
        result.cameraCutResetCount = history != null ? ReadInt(history, "resetCount") - resetCountBefore : 0;

        if (state.nonFinitePixels > 0 || state.magentaPixels > 0)
            result.issues.Add("CameraCut: the image contains invalid pixels.");
        if (result.cameraCutResetCount <= 0)
            result.issues.Add("CameraCut: the SSR history was not invalidated by the camera reset.");

        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, WarmupFrames);
    }

    // ---------------------------------------------------------------------------------------------
    // Asset creation
    // ---------------------------------------------------------------------------------------------

    static void EnsureAssets(bool force)
    {
        if (SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before creating the T7 assets.");

        AssetDatabase.Refresh();
        for (var i = 0; i < ModeNames.Length; i++)
        {
            var mode = ModeNames[i];
            var renderer = EnsureRenderer(
                string.Format(T7RendererPathFormat, mode),
                "SslT7" + mode + "Renderer",
                force,
                out bool recreated);

            EnsureFeatureResources(renderer);
            EnsurePipeline(string.Format(T7PipelinePathFormat, mode), "SslT7" + mode + "Pipeline", renderer, force || recreated);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static UniversalRendererData EnsureRenderer(string rendererPath, string rendererName, bool force, out bool recreated)
    {
        recreated = false;
        var t1Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(
            string.Format(T1RendererPathFormat, rendererName.Replace("SslT7", string.Empty).Replace("Renderer", string.Empty)));
        if (t1Renderer == null)
            throw new FileNotFoundException("Missing T1 renderer asset. Create the T1 baseline first.", rendererName);

        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        bool matches = renderer != null &&
                       renderer.rendererFeatures.Count == 1 &&
                       renderer.rendererFeatures[0] is ScreenSpaceLightingRendererFeature;

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
    // Settings and reflection helpers
    // ---------------------------------------------------------------------------------------------

    static void DisableOtherScreenSpaceEffects(VolumeProfile profile)
    {
        var ssgi = profile.components.OfType<ScreenSpaceGlobalIllumination>().FirstOrDefault();
        if (ssgi != null)
            ssgi.enabled.Override(false);
        var refraction = profile.components.OfType<ScreenSpaceRefraction>().FirstOrDefault();
        if (refraction != null)
            refraction.enabled.Override(false);
    }

    static void ApplyReferenceSettings(ScreenSpaceReflection settings)
    {
        settings.enabled.Override(true);
        settings.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
        settings.minSmoothness.Override(0.5f);
        settings.smoothnessFadeStart.Override(0.9f);
        settings.depthBufferThickness.Override(0.05f);
        settings.screenFadeDistance.Override(0.1f);
        settings.rayMaxIterations.Override(64);
        settings.reflectSky.Override(true);
        settings.intensity.Override(1f);
    }

    static Type GetPluginType(string name)
    {
        var type = typeof(ScreenSpaceLightingRendererFeature).Assembly.GetType("UnityEngine.Rendering.Universal." + name);
        if (type == null)
            throw new TypeLoadException(name + " was not found.");
        return type;
    }

    static int ReadStaticInt(Type type, string name)
    {
        var field = type.GetField(name, StaticFlags);
        return field == null ? -1 : (int)field.GetValue(null);
    }

    static object GetHistory(ScreenSpaceLightingRendererFeature feature, Camera camera)
    {
        var store = feature.GetType().GetProperty("historyStore", InstanceFlags)?.GetValue(feature);
        if (store == null)
            return null;
        var tryGet = store.GetType().GetMethod("TryGet", InstanceFlags);
        if (tryGet == null)
            return null;
        var arguments = new object[] { camera, 0, null };
        bool found = tryGet.Invoke(store, arguments) is bool value && value;
        return found ? arguments[2] : null;
    }

    static object GetSsrPass(ScreenSpaceLightingRendererFeature feature)
    {
        return feature.GetType().GetProperty("ssrPass", InstanceFlags)?.GetValue(feature);
    }

    static object GetSsrOutputTexture(ScreenSpaceLightingRendererFeature feature)
    {
        var pass = GetSsrPass(feature);
        return pass?.GetType().GetProperty("outputTexture", InstanceFlags)?.GetValue(pass);
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

    static T7HistorySnapshot SnapshotHistory(string label, object history)
    {
        return new T7HistorySnapshot
        {
            label = label,
            width = ReadInt(history, "width"),
            height = ReadInt(history, "height"),
            resetCount = ReadInt(history, "resetCount"),
            frameSerial = ReadInt(history, "frameSerial"),
            readSlot = ReadInt(history, "readSlot"),
            writeSlot = ReadInt(history, "writeSlot"),
            ssrWidth = ReadInt(history, "ssrWidth"),
            ssrHeight = ReadInt(history, "ssrHeight"),
            ssrResourceGeneration = ReadInt(history, "ssrResourceGeneration"),
            ssrResourcesAllocated = ReadBool(history, "ssrResourcesAllocated"),
            ssrOutputProduced = ReadBool(history, "ssrOutputProduced")
        };
    }

    static int ReadInt(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name, InstanceFlags);
        if (property != null)
            return (int)property.GetValue(instance);
        var field = instance.GetType().GetField(name, InstanceFlags);
        return field == null ? 0 : (int)field.GetValue(instance);
    }

    static bool ReadBool(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name, InstanceFlags);
        if (property != null)
            return (bool)property.GetValue(instance);
        var field = instance.GetType().GetField(name, InstanceFlags);
        return field != null && (bool)field.GetValue(instance);
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
    // Capture helpers
    // ---------------------------------------------------------------------------------------------

    static RenderTexture CreateTarget(int width, int height, string name)
    {
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
        {
            name = name
        };
        target.Create();
        return target;
    }

    static void SetTarget(Camera camera, RenderTexture target)
    {
        camera.targetTexture = target;
        camera.aspect = (float)target.width / target.height;
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

    static Color[] ReadRTHandle(object handle)
    {
        if (handle == null)
            return null;
        var rt = handle.GetType().GetProperty("rt", InstanceFlags)?.GetValue(handle) as RenderTexture;
        if (rt == null)
            return null;

        var temporary = RenderTexture.GetTemporary(rt.width, rt.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        try
        {
            Graphics.Blit(rt, temporary);
            return ReadPixels(temporary);
        }
        finally
        {
            RenderTexture.ReleaseTemporary(temporary);
        }
    }

    static T7TextureBytes DescribeTextureBytes(object handle)
    {
        var rt = handle?.GetType().GetProperty("rt", InstanceFlags)?.GetValue(handle) as RenderTexture;
        if (rt == null)
            return new T7TextureBytes();
        return new T7TextureBytes
        {
            width = rt.width,
            height = rt.height,
            volumeDepth = rt.volumeDepth,
            bytes = (long)rt.width * rt.height * 8L * Math.Max(1, rt.volumeDepth)
        };
    }

    static T7WeightStats DescribeWeight(Color[] samples)
    {
        var stats = new T7WeightStats();
        if (samples == null)
            return stats;

        double sum = 0.0;
        foreach (var pixel in samples)
        {
            if (!IsFinite(pixel.r) || !IsFinite(pixel.g) || !IsFinite(pixel.b) || !IsFinite(pixel.a))
            {
                stats.nonFinitePixels++;
                continue;
            }

            if (pixel.r < 0f || pixel.g < 0f || pixel.b < 0f)
                stats.negativePixels++;

            var weight = pixel.a;
            sum += weight;
            if (weight > 0.9f)
                stats.coveredPixels++;
            else if (weight > 0.001f)
                stats.partialPixels++;
            else
                stats.missPixels++;
        }

        int total = Mathf.Max(1, samples.Length);
        stats.pixelCount = samples.Length;
        stats.meanWeight = (float)(sum / total);
        stats.coveredFraction = stats.coveredPixels / (float)total;
        stats.partialFraction = stats.partialPixels / (float)total;
        stats.missFraction = stats.missPixels / (float)total;
        return stats;
    }

    static T7StateCapture DescribeState(string stateName, Color[] samples, Color[] reference)
    {
        var capture = new T7StateCapture
        {
            state = stateName,
            sha256 = HashSamples(samples),
            magentaPixels = Count(samples, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f),
            nonFinitePixels = Count(samples, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)),
            meanLuminance = Mean(samples, Luminance)
        };

        if (reference != null)
        {
            if (reference.Length != samples.Length)
            {
                capture.differingPixels = Math.Max(reference.Length, samples.Length);
                capture.maxChannelDelta = float.PositiveInfinity;
            }
            else
            {
                var differing = 0;
                var maxDelta = 0f;
                for (var i = 0; i < samples.Length; i++)
                {
                    var dr = Math.Abs(samples[i].r - reference[i].r);
                    var dg = Math.Abs(samples[i].g - reference[i].g);
                    var db = Math.Abs(samples[i].b - reference[i].b);
                    var da = Math.Abs(samples[i].a - reference[i].a);
                    var pixelDelta = Math.Max(Math.Max(dr, dg), Math.Max(db, da));
                    if (pixelDelta > 0f)
                        differing++;
                    maxDelta = Mathf.Max(maxDelta, pixelDelta);
                }

                capture.differingPixels = differing;
                capture.maxChannelDelta = maxDelta;
            }
        }

        capture.differsFromReference = capture.differingPixels > 0;
        capture.identicalToReference = reference != null && capture.differingPixels == 0;
        return capture;
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

    static float Smoothstep01(float value)
    {
        return value * value * (3f - 2f * value);
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
        foreach (var pixel in samples)
            sum += selector(pixel);
        return samples.Length == 0 ? 0f : (float)(sum / samples.Length);
    }

    static float Min(Color[] samples, Func<Color, float> selector)
    {
        var value = float.PositiveInfinity;
        foreach (var pixel in samples)
            value = Mathf.Min(value, selector(pixel));
        return value;
    }

    static float Max(Color[] samples, Func<Color, float> selector)
    {
        var value = float.NegativeInfinity;
        foreach (var pixel in samples)
            value = Mathf.Max(value, selector(pixel));
        return value;
    }

    // ---------------------------------------------------------------------------------------------
    // Report data
    // ---------------------------------------------------------------------------------------------

    [Serializable]
    public sealed class T7Report
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public bool completed;
        public bool passed;
        public string error;
        public List<T7ModeResult> modes;
    }

    [Serializable]
    public sealed class T7ModeResult
    {
        public string name;
        public bool passed;
        public List<string> issues;
        public int ssrExecutions;
        public int traceDispatches;
        public int reprojectDispatches;
        public int setupExecutions;
        public int ssrBoundFrames;
        public int cameraCutResetCount;
        public int missPixelsCompared;
        public int missPixelsDifferent;
        public float missMaxChannelDelta;
        public int linearityPixels;
        public float linearityRelativeError;
        public float linearityMeanHalf;
        public float linearityMeanFull;
        public float linearityRatio;
        public int debugConsumptionPixels;
        public int debugConsumptionMismatchPixels;
        public float debugConsumptionMaxDelta;
        public float consumedLuminance;
        public float outputLuminance;
        public T7WeightStats weight = new T7WeightStats();
        public T7TextureBytes outputTextureBytes = new T7TextureBytes();
        public List<T7MaterialProbe> materialProbes;
        public List<T7StateCapture> states;
        public List<T7HistorySnapshot> historyChecks;
        public List<T7DebugImage> debugImages;
    }

    [Serializable]
    public sealed class T7MaterialProbe
    {
        public string name;
        public float smoothness;
        public bool metallic;
        public int pixels;
        public float meanWeight;
        // A covered pixel's weight is exactly the roughness fade. The mean over a sphere also
        // depends on how much of its reflection leaves the screen, which only varies with the
        // sphere's position, so the fade checks read the maximum instead.
        public float maxWeight;
        public float expectedFade;
        public float meanReferenceLuminance;
    }

    [Serializable]
    public sealed class T7StateCapture
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
    public sealed class T7HistorySnapshot
    {
        public string label;
        public int width;
        public int height;
        public int resetCount;
        public int frameSerial;
        public int readSlot;
        public int writeSlot;
        public int ssrWidth;
        public int ssrHeight;
        public int ssrResourceGeneration;
        public bool ssrResourcesAllocated;
        public bool ssrOutputProduced;
    }

    [Serializable]
    public sealed class T7DebugImage
    {
        public string view;
        public string file;
        public string sha256;
        public int magentaPixels;
        public int nonFinitePixels;
        public float meanLuminance;
        public float minLuminance;
        public float maxLuminance;
    }

    [Serializable]
    public sealed class T7WeightStats
    {
        public int pixelCount;
        public int nonFinitePixels;
        public int negativePixels;
        public int coveredPixels;
        public int partialPixels;
        public int missPixels;
        public float meanWeight;
        public float coveredFraction;
        public float partialFraction;
        public float missFraction;
    }

    [Serializable]
    public sealed class T7TextureBytes
    {
        public int width;
        public int height;
        public int volumeDepth;
        public long bytes;
    }
}