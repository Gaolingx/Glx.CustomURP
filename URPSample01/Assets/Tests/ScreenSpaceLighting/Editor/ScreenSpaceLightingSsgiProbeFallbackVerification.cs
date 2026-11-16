using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// P4 verification: the non-Forward+ probe atlas toggle makes the SSGI miss fallback sample the
// scene reflection probe. Each renderer is captured with the toggle off and on for
// rayMiss=ReflectionProbes, plus the ProbesAndSky remainder and a baked-GI reference.
public static class ScreenSpaceLightingSsgiProbeFallbackVerification
{
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string RendererPathFormat = RendererRoot + "/SslT8{0}Renderer.asset";
    const string PipelinePathFormat = RendererRoot + "/SslT8{0}Pipeline.asset";
    const string DefaultOutputRoot = "PLAN/Done/04-SSGIFallbackImprove/validation/P4";
    const string TempScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/~SsgiProbeFallbackVerificationTemp.unity";
    const string TempStressScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/~SsgiProbeFallbackStressTemp.unity";
    const string ProbeCubemapPath = "Assets/Tests/ScreenSpaceLighting/Scenes/~SsgiProbeFallbackVerificationCubemap.asset";

    const int ImageWidth = 320;
    const int ImageHeight = 180;
    const int WarmupFrames = 8;
    static readonly int[] StressProbeCounts = { 0, 1, 4, 32 };

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tests/Screen Space Lighting/Run SSGI Probe Fallback Verification")]
    public static void RunSsgiProbeFallbackVerification()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputRoot = Environment.GetEnvironmentVariable("SSL_SSGI_PROBE_FALLBACK_OUT");
        var outputDirectory = Path.Combine(projectRoot, string.IsNullOrEmpty(outputRoot) ? DefaultOutputRoot : outputRoot);
        Directory.CreateDirectory(outputDirectory);

        var report = new VerificationReport
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            imageWidth = ImageWidth,
            imageHeight = ImageHeight,
            passed = true,
            modes = new List<ModeResult>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture imageTarget = null;
        Volume volume = null;
        VolumeProfile originalProfile = null;
        VolumeProfile testProfile = null;
        Cubemap probeCubemap = null;

        try
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var baselineRunner = UnityEngine.Object.FindObjectOfType<ScreenSpaceLightingBaselineRunner>();
            if (baselineRunner != null)
                baselineRunner.enabled = false;

            var probeCamera = UnityEngine.Object.FindObjectOfType<Camera>();
            if (probeCamera == null)
                throw new InvalidOperationException("The verification scene has no camera.");

            AssetDatabase.DeleteAsset(ProbeCubemapPath);
            probeCubemap = new Cubemap(32, TextureFormat.RGBAFloat, false) { name = "SsgiProbeFallbackCubemap" };
            AssetDatabase.CreateAsset(probeCubemap, ProbeCubemapPath);
            FillCubemap(probeCubemap, new Color(0.15f, 3.0f, 0.15f));
            AssetDatabase.SaveAssets();

            CreateProbeObjects(probeCamera, probeCubemap, 1);
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), TempScenePath, true);
            EditorSceneManager.OpenScene(TempScenePath, OpenSceneMode.Single);
            DynamicGI.UpdateEnvironment();

            var camera = UnityEngine.Object.FindObjectOfType<Camera>();
            var cameraData = camera != null ? camera.GetComponent<UniversalAdditionalCameraData>() : null;
            if (camera == null || cameraData == null)
                throw new InvalidOperationException("The verification camera is missing.");

            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;
            cameraData.resetHistory = true;

            var volumeObject = GameObject.Find("Baseline Volume");
            volume = volumeObject != null ? volumeObject.GetComponent<Volume>() : null;
            if (volume == null)
                throw new InvalidOperationException("The verification scene has no Baseline Volume.");

            originalProfile = volume.sharedProfile;
            testProfile = ScriptableObject.Instantiate(originalProfile);
            testProfile.name = originalProfile.name + " (SSGI probe fallback)";
            volume.sharedProfile = testProfile;
            DisableOtherScreenSpaceEffects(testProfile);
            var ssgi = testProfile.components.OfType<ScreenSpaceGlobalIllumination>().FirstOrDefault()
                       ?? testProfile.Add<ScreenSpaceGlobalIllumination>(true);
            ApplySsgiSettings(ssgi);

            imageTarget = CreateTarget(ImageWidth, ImageHeight, "SslSsgiProbeFallbackImage");
            camera.targetTexture = imageTarget;
            camera.aspect = (float)ImageWidth / ImageHeight;

            foreach (var modeName in ModeNames)
            {
                var result = VerifyMode(modeName, camera, cameraData, ssgi, probeCubemap, imageTarget, outputDirectory);
                report.modes.Add(result);
                report.passed &= result.passed;
                Debug.Log($"SSGI probe fallback {modeName}: passed={result.passed} issues={string.Join("; ", result.issues)}");
            }

            report.stress = VerifyProbeCounts(outputDirectory, probeCubemap, originalProfile, imageTarget, out var performance);
            report.passed &= report.stress.passed;
            Debug.Log($"SSGI probe fallback stress: passed={report.stress.passed} issues={string.Join("; ", report.stress.issues)}");
            report.performance = performance;
            if (report.performance != null)
            {
                report.passed &= report.performance.passed;
                Debug.Log($"SSGI probe fallback perf: off={report.performance.offMedianMs:F3}ms " +
                          $"on={report.performance.onMedianMs:F3}ms delta={report.performance.deltaMs:F3}ms " +
                          $"max={report.performance.deltaMaxMs:F3}ms passed={report.performance.passed}");
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
            if (volume != null)
                volume.sharedProfile = originalProfile;
            if (testProfile != null)
                UnityEngine.Object.DestroyImmediate(testProfile);
            if (imageTarget != null)
            {
                imageTarget.Release();
                UnityEngine.Object.DestroyImmediate(imageTarget);
            }

            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
            else
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(TempScenePath);
            AssetDatabase.DeleteAsset(TempStressScenePath);
            AssetDatabase.DeleteAsset(ProbeCubemapPath);
            File.WriteAllText(Path.Combine(outputDirectory, "SsgiProbeFallbackVerification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"SSGI probe fallback verification finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    static ModeResult VerifyMode(
        string modeName,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        ScreenSpaceGlobalIllumination ssgi,
        Cubemap probeCubemap,
        RenderTexture imageTarget,
        string outputDirectory)
    {
        var result = new ModeResult { name = modeName, issues = new List<string>() };
        var pipeline = LoadPipeline(modeName);
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(RendererPathFormat, modeName));
        if (renderer == null)
            throw new FileNotFoundException("Missing SSGI probe fallback renderer fixture.", string.Format(RendererPathFormat, modeName));
        var feature = renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (feature == null)
            throw new InvalidOperationException("The renderer is missing the Screen Space Lighting feature.");
        EnsureFeatureResources(feature);

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        FillCubemap(probeCubemap, new Color(0.15f, 3.0f, 0.15f));

        // Reference: SSGI off, so the image shows the baked GI the fallback replaces.
        ssgi.enabled.Override(false);
        cameraData.resetHistory = true;
        Render(camera, WarmupFrames);
        WriteImage(imageTarget, outputDirectory, $"{modeName}_Reference.png");
        ssgi.enabled.Override(true);

        ssgi.rayMiss.Override(RayMarchingFallbackHierarchy.ReflectionProbes);

        SetPublishProbeAtlas(feature, false);
        cameraData.resetHistory = true;
        Render(camera, WarmupFrames);
        var offSamples = ReadSsgiOutput(feature);
        var off = Summarize(offSamples);
        result.off = off;
        WriteColors(offSamples, ImageWidth, ImageHeight, outputDirectory, $"{modeName}_Off.png");

        SetPublishProbeAtlas(feature, true);
        cameraData.resetHistory = true;
        Render(camera, WarmupFrames);
        var onSamples = ReadSsgiOutput(feature);
        var on = Summarize(onSamples);
        result.on = on;
        WriteColors(onSamples, ImageWidth, ImageHeight, outputDirectory, $"{modeName}_On.png");

        // ProbesAndSky must fill the uncovered remainder with the ambient probe.
        ssgi.rayMiss.Override(RayMarchingFallbackHierarchy.ReflectionProbesAndSky);
        cameraData.resetHistory = true;
        Render(camera, WarmupFrames);
        var bothSamples = ReadSsgiOutput(feature);
        var both = Summarize(bothSamples);
        result.both = both;
        WriteColors(bothSamples, ImageWidth, ImageHeight, outputDirectory, $"{modeName}_On_ProbesAndSky.png");

        // Pure sky for the ProbesAndSky comparison.
        ssgi.rayMiss.Override(RayMarchingFallbackHierarchy.Sky);
        cameraData.resetHistory = true;
        Render(camera, WarmupFrames);
        result.sky = Summarize(ReadSsgiOutput(feature));

        ssgi.rayMiss.Override(RayMarchingFallbackHierarchy.ReflectionProbes);

        bool forwardPlus = modeName == "ForwardPlus";

        if (off.nonFinitePixels > 0 || on.nonFinitePixels > 0 || both.nonFinitePixels > 0 || result.sky.nonFinitePixels > 0)
            result.issues.Add("A capture contains NaN/Inf pixels.");
        if (on.coveredFraction < 0.2f)
            result.issues.Add($"The probe circulation covers only {on.coveredFraction:P2} of the pixels.");
        if (on.probeGreenTint < 0.05f)
            result.issues.Add($"The toggle-on fallback is not tinted by the green probe (G-max(R,B)={on.probeGreenTint:F4}).");
        if (result.both.probeGreenTint < 0.02f)
            result.issues.Add($"ProbesAndSky is not tinted by the green probe (G-max(R,B)={result.both.probeGreenTint:F4}).");
        if (Mathf.Abs(result.both.premultipliedMean - result.sky.premultipliedMean) < 0.002f)
            result.issues.Add("ProbesAndSky and Sky are nearly identical; the sky remainder is missing.");

        if (forwardPlus)
        {
            // Forward+ ignores the toggle: both captures must be byte-identical.
            result.toggleImagesIdentical = ImagesEqual(offSamples, onSamples);
            if (!result.toggleImagesIdentical)
                result.issues.Add("Forward+ output changed with the non-Forward+ toggle.");
            if (off.probeGreenTint < 0.05f)
                result.issues.Add($"Forward+ toggle-off fallback is not probe-tinted (G-max(R,B)={off.probeGreenTint:F4}).");
        }
        else
        {
            // Toggle off keeps the default cubemap, which the scene sky tints blue.
            if (off.probeGreenTint > 0.02f)
                result.issues.Add($"Toggle off unexpectedly samples the green probe (G-max(R,B)={off.probeGreenTint:F4}).");
            // The default cubemap reports weight 1, while the probe list only covers its influence
            // box; the uncovered pixels must fall back to weight 0 for rayMiss=ReflectionProbes.
            if (on.coveredFraction >= off.coveredFraction - 0.01f)
                result.issues.Add($"Toggle on does not narrow the miss weight (off {off.coveredFraction:P2}, on {on.coveredFraction:P2}).");
        }

        SetPublishProbeAtlas(feature, false);
        result.passed = result.issues.Count == 0;
        return result;
    }

    // Forward only: the flat list loop is the same in Deferred, but one renderer is enough to prove
    // the count handling and the weight saturation. Each probe count gets its own serialized scene
    // because the culling list is not rebuilt when probes are enabled at runtime.
    static StressResult VerifyProbeCounts(
        string outputDirectory,
        Cubemap probeCubemap,
        VolumeProfile originalProfile,
        RenderTexture imageTarget,
        out PerformanceResult performance)
    {
        performance = null;
        var stress = new StressResult { issues = new List<string>(), counts = new List<ProbeCountResult>() };
        var pipeline = LoadPipeline("Forward");
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(RendererPathFormat, "Forward"));
        if (renderer == null)
            throw new FileNotFoundException("Missing SSGI probe fallback renderer fixture.", string.Format(RendererPathFormat, "Forward"));
        var feature = renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (feature == null)
            throw new InvalidOperationException("The renderer is missing the Screen Space Lighting feature.");
        EnsureFeatureResources(feature);
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        SetPublishProbeAtlas(feature, true);

        try
        {
            foreach (int count in StressProbeCounts)
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                var baselineRunner = UnityEngine.Object.FindObjectOfType<ScreenSpaceLightingBaselineRunner>();
                if (baselineRunner != null)
                    baselineRunner.enabled = false;

                var probeCamera = UnityEngine.Object.FindObjectOfType<Camera>();
                if (probeCamera == null)
                    throw new InvalidOperationException("The verification scene has no camera.");

                CreateProbeObjects(probeCamera, probeCubemap, count);
                EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), TempStressScenePath, true);
                EditorSceneManager.OpenScene(TempStressScenePath, OpenSceneMode.Single);

                var camera = UnityEngine.Object.FindObjectOfType<Camera>();
                var cameraData = camera != null ? camera.GetComponent<UniversalAdditionalCameraData>() : null;
                if (camera == null || cameraData == null)
                    throw new InvalidOperationException("The stress scene lost its camera.");
                cameraData.renderPostProcessing = false;
                cameraData.antialiasing = AntialiasingMode.None;
                cameraData.resetHistory = true;
                camera.targetTexture = imageTarget;

                var volumeObject = GameObject.Find("Baseline Volume");
                var volume = volumeObject != null ? volumeObject.GetComponent<Volume>() : null;
                if (volume == null)
                    throw new InvalidOperationException("The stress scene has no Baseline Volume.");
                var profile = ScriptableObject.Instantiate(originalProfile);
                profile.name = originalProfile.name + " (SSGI probe fallback stress)";
                volume.sharedProfile = profile;
                DisableOtherScreenSpaceEffects(profile);
                var ssgi = profile.components.OfType<ScreenSpaceGlobalIllumination>().FirstOrDefault()
                           ?? profile.Add<ScreenSpaceGlobalIllumination>(true);
                ApplySsgiSettings(ssgi);
                ssgi.rayMiss.Override(RayMarchingFallbackHierarchy.ReflectionProbes);

                Render(camera, WarmupFrames);
                var samples = ReadSsgiOutput(feature);
                var stats = Summarize(samples);
                int visible = CountVisibleProbes(camera);
                var outputSize = GetSsgiOutputSize(feature);
                stress.counts.Add(new ProbeCountResult
                {
                    count = count,
                    visibleProbeCount = visible,
                    sampleCount = samples?.Length ?? 0,
                    outputWidth = outputSize.x,
                    outputHeight = outputSize.y,
                    stats = stats
                });
                Debug.Log($"SSGI probe fallback stress {count}: visible={visible} samples={samples?.Length} " +
                          $"ssgi={outputSize.x}x{outputSize.y} target={ImageWidth}x{ImageHeight} coverage={stats.coveredFraction:P2} " +
                          $"meanWeight={stats.coveredMeanWeight:F3} tint={stats.probeGreenTint:F4}");
                if (samples != null && samples.Length == ImageWidth * ImageHeight)
                    WriteColors(samples, ImageWidth, ImageHeight, outputDirectory, $"Stress_{count:00}.png");

                if (stats.nonFinitePixels > 0)
                    stress.issues.Add($"{count} probes produced NaN/Inf pixels.");
                if (count == 0)
                {
                    // Screen-space ray hits keep their radiance, so the capture is not empty; the
                    // probe fallback must contribute nothing without probes.
                    if (stats.coveredFraction > 0.1f)
                        stress.issues.Add($"Zero probes still cover {stats.coveredFraction:P2} of the pixels (expected only screen-space hits).");
                    if (stats.probeGreenTint > 0.02f)
                        stress.issues.Add($"Zero probes still sample the green probe (G-max(R,B)={stats.probeGreenTint:F4}).");
                }
                else
                {
                    if (visible < count)
                        stress.issues.Add($"{count} probes requested but only {visible} were culled.");
                    if (stats.coveredFraction < 0.2f)
                        stress.issues.Add($"{count} probes cover only {stats.coveredFraction:P2} of the pixels.");
                    if (stats.probeGreenTint < 0.05f)
                        stress.issues.Add($"{count} probes are not green-tinted (G-max(R,B)={stats.probeGreenTint:F4}).");
                    if (stats.coveredMeanWeight < 0.98f)
                        stress.issues.Add($"{count} probes do not saturate the covered weight ({stats.coveredMeanWeight:F3}).");
                }

                if (count == StressProbeCounts[StressProbeCounts.Length - 1])
                    performance = MeasureProbeFallbackCost(camera, cameraData, ssgi, feature);

                volume.sharedProfile = originalProfile;
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }
        finally
        {
            SetPublishProbeAtlas(feature, false);
        }

        stress.passed = stress.issues.Count == 0;
        return stress;
    }

    // Wall-clock cost of the enabled toggle at 1280x720 with 32 visible probes. Each battery is a
    // blocking 1x1 readback of the camera target, so the GPU has finished the submitted frames when
    // the timer stops. Off/on batteries alternate to cancel drift; the reported delta is the median
    // of the paired differences. The SSGI dispatch count is identical in both states, so the delta
    // is the probe-list fallback plus the per-frame atlas upload.
    static PerformanceResult MeasureProbeFallbackCost(
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        ScreenSpaceGlobalIllumination ssgi,
        ScreenSpaceLightingRendererFeature feature)
    {
        const int width = 1280;
        const int height = 720;
        const int framesPerBattery = 30;
        const int batteryCount = 8;

        var result = new PerformanceResult
        {
            width = width,
            height = height,
            probeCount = StressProbeCounts[StressProbeCounts.Length - 1],
            framesPerBattery = framesPerBattery,
            batteryCount = batteryCount,
            budgetMs = 0.2f,
            offMs = new List<float>(batteryCount),
            onMs = new List<float>(batteryCount),
            deltaSamplesMs = new List<float>(batteryCount),
            method = "camera.Render() x frames, then a blocking 1x1 ReadPixels of the 1280x720 target; " +
                     "alternating off/on batteries; per-frame ms per battery; delta = median of paired " +
                     "differences; temporal/denoise off; no new SSGI dispatch."
        };

        var target = CreateTarget(width, height, "SslSsgiProbeFallbackPerf");
        var previousTarget = camera.targetTexture;
        try
        {
            camera.targetTexture = target;
            ssgi.rayMiss.Override(RayMarchingFallbackHierarchy.ReflectionProbes);

            SetPublishProbeAtlas(feature, false);
            cameraData.resetHistory = true;
            Render(camera, WarmupFrames);
            SyncReadback(target);
            SetPublishProbeAtlas(feature, true);
            Render(camera, WarmupFrames);
            SyncReadback(target);

            for (int battery = 0; battery < batteryCount; battery++)
            {
                result.offMs.Add(MeasureBattery(camera, feature, target, false, framesPerBattery));
                result.onMs.Add(MeasureBattery(camera, feature, target, true, framesPerBattery));
                result.deltaSamplesMs.Add(result.onMs[battery] - result.offMs[battery]);
            }

            result.offMedianMs = Median(result.offMs);
            result.onMedianMs = Median(result.onMs);
            result.deltaMs = Median(result.deltaSamplesMs);
            result.deltaMaxMs = Max(result.deltaSamplesMs);
            result.passed = result.deltaMs <= result.budgetMs;
        }
        finally
        {
            SetPublishProbeAtlas(feature, true);
            camera.targetTexture = previousTarget;
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
        }

        return result;
    }

    static float MeasureBattery(
        Camera camera,
        ScreenSpaceLightingRendererFeature feature,
        RenderTexture target,
        bool publishProbeAtlas,
        int frames)
    {
        SetPublishProbeAtlas(feature, publishProbeAtlas);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Render(camera, frames);
        SyncReadback(target);
        stopwatch.Stop();
        return (float)stopwatch.Elapsed.TotalMilliseconds / frames;
    }

    static float Median(List<float> values)
    {
        var sorted = new List<float>(values);
        sorted.Sort();
        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) * 0.5f
            : sorted[middle];
    }

    static float Max(List<float> values)
    {
        float max = float.MinValue;
        foreach (var value in values)
            max = Mathf.Max(max, value);
        return max;
    }

    static void SyncReadback(RenderTexture target)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
        }
        finally
        {
            RenderTexture.active = previous;
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    static int CountVisibleProbes(Camera camera)
    {
        int count = -1;
        void Handler(ScriptableRenderContext context, Camera renderedCamera)
        {
            if (renderedCamera != camera)
                return;
            if (!camera.TryGetCullingParameters(out var parameters))
                return;
            parameters.cullingOptions |= CullingOptions.NeedsLighting | CullingOptions.NeedsReflectionProbes;
            parameters.reflectionProbeSortingCriteria = ReflectionProbeSortingCriteria.ImportanceThenSize;
            count = context.Cull(ref parameters).visibleReflectionProbes.Length;
        }

        RenderPipelineManager.beginCameraRendering += Handler;
        try
        {
            camera.Render();
        }
        finally
        {
            RenderPipelineManager.beginCameraRendering -= Handler;
        }

        return count;
    }

    static void SetPublishProbeAtlas(ScreenSpaceLightingRendererFeature feature, bool enabled)
    {
        var settings = feature.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
        var field = settings?.GetType().GetField("publishProbeAtlas", InstanceFlags);
        if (field == null)
            throw new MissingFieldException("ScreenSpaceLightingSettings.publishProbeAtlas");
        field.SetValue(settings, enabled);
    }

    static SampleStats Summarize(Color[] samples)
    {
        var result = new SampleStats();
        if (samples == null)
        {
            result.missing = true;
            return result;
        }

        double sumR = 0.0, sumG = 0.0, sumB = 0.0, sumWeight = 0.0, sumPremul = 0.0;
        int covered = 0;
        foreach (var pixel in samples)
        {
            if (!IsFinite(pixel.r) || !IsFinite(pixel.g) || !IsFinite(pixel.b) || !IsFinite(pixel.a))
            {
                result.nonFinitePixels++;
                continue;
            }

            float weight = Mathf.Clamp01(pixel.a);
            sumWeight += weight;
            sumPremul += weight * (pixel.r + pixel.g + pixel.b) / 3.0;
            if (weight > 0.01f)
            {
                sumR += pixel.r;
                sumG += pixel.g;
                sumB += pixel.b;
                covered++;
            }
        }

        result.coveredFraction = covered / (float)Mathf.Max(1, samples.Length);
        result.meanWeight = (float)(sumWeight / Mathf.Max(1, samples.Length));
        result.coveredMeanWeight = covered > 0 ? (float)(sumWeight / covered) : 0f;
        result.premultipliedMean = (float)(sumPremul / Mathf.Max(1, samples.Length));
        if (covered > 0)
        {
            result.meanR = (float)(sumR / covered);
            result.meanG = (float)(sumG / covered);
            result.meanB = (float)(sumB / covered);
            result.probeGreenTint = result.meanG - Mathf.Max(result.meanR, result.meanB);
        }

        return result;
    }

    static bool ImagesEqual(Color[] a, Color[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a)
                return false;
        }
        return true;
    }

    static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    static void ApplySsgiSettings(ScreenSpaceGlobalIllumination ssgi)
    {
        ssgi.enabled.Override(true);
        ssgi.rayMiss.Override(RayMarchingFallbackHierarchy.ReflectionProbes);
        ssgi.fullResolution.Override(true);
        ssgi.maxRaySteps.Override(24);
        ssgi.depthBufferThickness.Override(0.1f);
        ssgi.intensity.Override(1f);
        ssgi.temporalAccumulation.Override(false);
        ssgi.denoise.Override(false);
        ssgi.secondDenoiserPass.Override(false);
        ssgi.halfResolutionDenoiser.Override(false);
        ssgi.bilateralUpsample.Override(true);
    }

    static void DisableOtherScreenSpaceEffects(VolumeProfile profile)
    {
        var ssr = profile.components.OfType<ScreenSpaceReflection>().FirstOrDefault();
        if (ssr != null)
            ssr.enabled.Override(false);
        var refraction = profile.components.OfType<ScreenSpaceRefraction>().FirstOrDefault();
        if (refraction != null)
            refraction.enabled.Override(false);
    }

    static GameObject CreateProbeObjects(Camera camera, Cubemap cubemap, int count)
    {
        var root = new GameObject("SSGI Probe Fallback Probes");
        var viewDirection = camera.transform.forward;
        var right = Vector3.Cross(Vector3.up, viewDirection).normalized;
        var up = Vector3.Cross(viewDirection, right).normalized;

        for (int i = 0; i < count; i++)
        {
            var probeObject = new GameObject($"SSGI Probe Fallback Probe {i:00}");
            probeObject.transform.SetParent(root.transform, false);
            if (i == 0)
            {
                // Cover the right half of the view so ProbesAndSky has uncovered pixels to fill.
                probeObject.transform.position = camera.transform.position + viewDirection * 12f + right * 6f;
            }
            else
            {
                // A visible grid of smaller probes for the 4 / 32 probe stress captures.
                int column = (i - 1) % 8;
                int row = (i - 1) / 8;
                probeObject.transform.position = camera.transform.position +
                    viewDirection * (11f + row * 1.2f) +
                    right * (2f + column * 1.0f) +
                    up * (row - 1.5f);
            }

            var probe = probeObject.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Custom;
            probe.customBakedTexture = cubemap;
            probe.size = i == 0 ? new Vector3(14f, 12f, 40f) : new Vector3(1.6f, 1.6f, 6f);
            probe.center = Vector3.zero;
            probe.blendDistance = 0.01f;
            probe.boxProjection = true;
            probe.importance = 1;
            probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.cullingMask = ~0;
            probe.enabled = true;
        }

        return root;
    }

    static void FillCubemap(Cubemap cubemap, Color color)
    {
        var pixels = new Color[cubemap.width * cubemap.width];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = color;
        for (int face = 0; face < 6; face++)
            cubemap.SetPixels(pixels, (CubemapFace)face);
        cubemap.Apply(false, false);
    }

    static UniversalRenderPipelineAsset LoadPipeline(string modeName)
    {
        var path = string.Format(PipelinePathFormat, modeName);
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
            throw new FileNotFoundException("Missing SSGI probe fallback pipeline fixture.", path);
        return pipeline;
    }

    static void EnsureFeatureResources(ScreenSpaceLightingRendererFeature feature)
    {
        var settings = feature.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
        var resources = settings?.GetType().GetField("resources", InstanceFlags)?.GetValue(settings);
        var locator = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceLightingResourceLocator");
        locator?.GetMethod("AssignDefaults", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, new[] { resources });
    }

    static RenderTexture CreateTarget(int width, int height, string name)
    {
        // The stress cases reload scenes between captures; without DontSave the render texture is
        // destroyed together with the scene that referenced it and the camera falls back to the
        // default screen size.
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave
        };
        target.Create();
        return target;
    }

    static void Render(Camera camera, int frames)
    {
        for (var i = 0; i < frames; i++)
            camera.Render();
    }

    static Color[] ReadSsgiOutput(ScreenSpaceLightingRendererFeature feature)
    {
        var pass = feature.GetType().GetProperty("ssgiPass", InstanceFlags)?.GetValue(feature);
        var handle = pass?.GetType().GetProperty("finalTexture", InstanceFlags)?.GetValue(pass);
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

    static Vector2Int GetSsgiOutputSize(ScreenSpaceLightingRendererFeature feature)
    {
        var pass = feature.GetType().GetProperty("ssgiPass", InstanceFlags)?.GetValue(feature);
        var handle = pass?.GetType().GetProperty("finalTexture", InstanceFlags)?.GetValue(pass);
        var rt = handle?.GetType().GetProperty("rt", InstanceFlags)?.GetValue(handle) as RenderTexture;
        return rt != null ? new Vector2Int(rt.width, rt.height) : Vector2Int.zero;
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

    static void WriteImage(RenderTexture target, string outputDirectory, string fileName)
    {
        WriteColors(ReadPixels(target), target.width, target.height, outputDirectory, fileName);
    }

    static void WriteColors(Color[] samples, int width, int height, string outputDirectory, string fileName)
    {
        if (samples == null)
            return;

        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        try
        {
            var pixels = new Color32[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                float weight = Mathf.Clamp01(samples[i].a);
                pixels[i] = new Color32(
                    ToByte(samples[i].r * weight),
                    ToByte(samples[i].g * weight),
                    ToByte(samples[i].b * weight),
                    255);
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

    static byte ToByte(float linear)
    {
        return (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(Mathf.Clamp01(linear)) * 255f);
    }

    [Serializable]
    public sealed class VerificationReport
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public int imageWidth;
        public int imageHeight;
        public bool completed;
        public bool passed;
        public string error;
        public List<ModeResult> modes;
        public StressResult stress;
        public PerformanceResult performance;
    }

    [Serializable]
    public sealed class ModeResult
    {
        public string name;
        public bool passed;
        public bool toggleImagesIdentical;
        public List<string> issues;
        public SampleStats off;
        public SampleStats on;
        public SampleStats both;
        public SampleStats sky;
    }

    [Serializable]
    public sealed class StressResult
    {
        public bool passed;
        public List<string> issues;
        public List<ProbeCountResult> counts;
    }

    [Serializable]
    public sealed class PerformanceResult
    {
        public int width;
        public int height;
        public int probeCount;
        public int framesPerBattery;
        public int batteryCount;
        public float budgetMs;
        public float offMedianMs;
        public float onMedianMs;
        public float deltaMs;
        public float deltaMaxMs;
        public bool passed;
        public string method;
        public List<float> offMs;
        public List<float> onMs;
        public List<float> deltaSamplesMs;
    }

    [Serializable]
    public sealed class ProbeCountResult
    {
        public int count;
        public int visibleProbeCount;
        public int sampleCount;
        public int outputWidth;
        public int outputHeight;
        public SampleStats stats;
    }

    [Serializable]
    public sealed class SampleStats
    {
        public bool missing;
        public int nonFinitePixels;
        public float coveredFraction;
        public float meanWeight;
        public float coveredMeanWeight;
        public float premultipliedMean;
        public float meanR;
        public float meanG;
        public float meanB;
        public float probeGreenTint;
    }
}
