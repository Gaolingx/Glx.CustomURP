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

// Round 08 / T0: attributes the Deferred + Accurate G-buffer normals noise to a single screen-space
// effect. Renders the T3 verification scene with GTAO only, Screen Space Lighting only and both, each
// with accurate normals off and on, and dumps the final frame, the effect debug views, the debug
// metrics and the compute keyword state. The renderer's accurateNormals flag is restored at the end.
public static class ScreenSpaceLightingAccurateNormalsAttribution
{
    const string RendererPath = "Assets/Tests/ScreenSpaceLighting/Renderers/SslT5DeferredRenderer.asset";
    const string PipelinePath = "Assets/Tests/ScreenSpaceLighting/Renderers/SslT5DeferredPipeline.asset";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string OutputRoot = "PLAN/Done/08-Accurate_normals_fixed/validation/Attribution";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 12;

    // Acceptance thresholds in normalized luminance units. 0.5/255 for the noise energy and the
    // frame-to-frame delta, 1/255 for the average brightness (round 08 HANDOFF section 4).
    const float k_HighFrequencyTolerance = 0.5f / 255f;
    const float k_LuminanceTolerance = 1f / 255f;
    const float k_FrameDeltaTolerance = 0.5f / 255f;

    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tests/Screen Space Lighting/Run Accurate Normals Attribution")]
    public static void RunAttribution()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);

        var report = new AttributionReport
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            rendererPath = RendererPath,
            scenePath = ScenePath,
            runs = new List<AttributionRun>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture target = null;
        RenderTexture debugTarget = null;
        UniversalRendererData rendererData = null;
        GroundTruthAmbientOcclusionRendererFeature gtao = null;
        ScreenSpaceLightingRendererFeature ssl = null;
        bool originalAccurateNormals = false;
        bool originalGtaoActive = false;
        bool originalSslActive = false;
        GameObject volumeObject = null;
        bool originalVolumeActive = false;

        try
        {
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

            volumeObject = GameObject.Find("Baseline Volume");
            if (volumeObject == null || volumeObject.GetComponent<Volume>() == null)
                throw new InvalidOperationException("The verification scene has no Baseline Volume.");
            originalVolumeActive = volumeObject.activeSelf;

            rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (rendererData == null)
                throw new FileNotFoundException("Missing renderer asset.", RendererPath);
            if (pipeline == null)
                throw new FileNotFoundException("Missing pipeline asset.", PipelinePath);

            gtao = rendererData.rendererFeatures.OfType<GroundTruthAmbientOcclusionRendererFeature>().FirstOrDefault();
            ssl = rendererData.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
            if (gtao == null)
                throw new InvalidOperationException("The renderer has no GTAO feature.");
            if (ssl == null)
                throw new InvalidOperationException("The renderer has no Screen Space Lighting feature.");

            originalAccurateNormals = rendererData.accurateGbufferNormals;
            originalGtaoActive = gtao.isActive;
            originalSslActive = ssl.isActive;
            ApplyReferenceGtao(gtao);
            SetSslDebug(ssl, ScreenSpaceLightingDebugMode.Off, 0);

            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;

            target = CreateTarget(ImageWidth, ImageHeight, "AttributionImage");
            debugTarget = CreateTarget(ImageWidth, ImageHeight, "AttributionDebug");
            camera.targetTexture = target;
            camera.aspect = (float)ImageWidth / ImageHeight;

            report.runtimeAccurateNormals = ReadRuntimeAccurateNormals(pipeline);
            report.luminanceTolerance = k_LuminanceTolerance;
            report.highFrequencyTolerance = k_HighFrequencyTolerance;
            report.frameDeltaTolerance = k_FrameDeltaTolerance;

            var variants = new[]
            {
                new Variant("Gtao", gtaoEnabled: true, volumeEnabled: false),
                new Variant("Ssl", gtaoEnabled: false, volumeEnabled: true),
                new Variant("Both", gtaoEnabled: true, volumeEnabled: true),
            };

            foreach (var variant in variants)
            {
                foreach (var accurate in new[] { false, true })
                {
                    var run = RunVariant(
                        variant,
                        accurate,
                        camera,
                        cameraData,
                        rendererData,
                        gtao,
                        ssl,
                        volumeObject,
                        target,
                        debugTarget,
                        outputDirectory);
                    report.runs.Add(run);
                    Debug.Log($"Attribution {run.name}: hf={run.highFrequency:F3} bottomHf={run.highFrequencyBottomHalf:F3} " +
                              $"frameDelta={run.frameDelta:F3} luma={run.meanLuminance:F3} keywords=[{DescribeKeywords(run)}]");
                }
            }

            // Cross-checks computed after all runs exist.
            foreach (var run in report.runs)
            {
                var counterpart = report.runs.FirstOrDefault(other =>
                    other.gtao == run.gtao && other.volume == run.volume && other.accurateNormals != run.accurateNormals);
                if (counterpart == null)
                    continue;

                run.luminanceDeltaVsCounterpart = Math.Abs(run.meanLuminance - counterpart.meanLuminance);
                run.highFrequencyDeltaVsCounterpart = Math.Abs(run.highFrequencyBottomHalf - counterpart.highFrequencyBottomHalf);

                if (run.luminanceDeltaVsCounterpart > k_LuminanceTolerance)
                    run.issues.Add($"Average brightness differs from the {(run.accurateNormals ? "signed" : "oct")} run by " +
                                   $"{run.luminanceDeltaVsCounterpart:F5} (> {k_LuminanceTolerance:F5}).");
                if (run.highFrequencyDeltaVsCounterpart > k_HighFrequencyTolerance)
                    run.issues.Add($"Floor high frequency differs from the {(run.accurateNormals ? "signed" : "oct")} run by " +
                                   $"{run.highFrequencyDeltaVsCounterpart:F5} (> {k_HighFrequencyTolerance:F5}).");
                if (run.frameDelta > k_FrameDeltaTolerance)
                    run.issues.Add($"Frame-to-frame delta {run.frameDelta:F5} is above {k_FrameDeltaTolerance:F5}.");
            }

            report.passed = report.runs.All(run => run.issues.Count == 0);
            report.completed = true;
        }
        catch (Exception exception)
        {
            report.completed = false;
            report.error = exception.ToString();
        }
        finally
        {
            if (rendererData != null)
            {
                rendererData.accurateGbufferNormals = originalAccurateNormals;
                rendererData.SetDirty();
            }
            if (gtao != null)
            {
                gtao.SetActive(originalGtaoActive);
                SetGtaoDebug(gtao, 0, 0);
            }
            if (ssl != null)
            {
                ssl.SetActive(originalSslActive);
                SetSslDebug(ssl, ScreenSpaceLightingDebugMode.Off, 0);
            }
            if (volumeObject != null)
                volumeObject.SetActive(originalVolumeActive);

            report.settingsRestored =
                rendererData != null && rendererData.accurateGbufferNormals == originalAccurateNormals &&
                gtao != null && gtao.isActive == originalGtaoActive &&
                ssl != null && ssl.isActive == originalSslActive &&
                volumeObject != null && volumeObject.activeSelf == originalVolumeActive;

            if (target != null)
            {
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
            if (debugTarget != null)
            {
                debugTarget.Release();
                UnityEngine.Object.DestroyImmediate(debugTarget);
            }

            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
            AssetDatabase.SaveAssets();

            File.WriteAllText(Path.Combine(outputDirectory, "Attribution.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"Accurate normals attribution finished: completed={report.completed} passed={report.passed} " +
                  $"restored={report.settingsRestored}");
        EditorApplication.Exit(report.completed && report.passed && report.settingsRestored ? 0 : 1);
    }

    static AttributionRun RunVariant(
        Variant variant,
        bool accurateNormals,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        UniversalRendererData rendererData,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        ScreenSpaceLightingRendererFeature ssl,
        GameObject volumeObject,
        RenderTexture target,
        RenderTexture debugTarget,
        string outputDirectory)
    {
        var run = new AttributionRun
        {
            name = variant.name + (accurateNormals ? "_Oct" : "_Signed"),
            gtao = variant.gtaoEnabled,
            volume = variant.volumeEnabled,
            accurateNormals = accurateNormals,
            issues = new List<string>(),
            keywords = new List<KeywordFlag>(),
            images = new List<string>()
        };

        gtao.SetActive(variant.gtaoEnabled);
        ssl.SetActive(variant.gtaoEnabled || variant.volumeEnabled);
        volumeObject.SetActive(variant.volumeEnabled);
        rendererData.accurateGbufferNormals = accurateNormals;
        rendererData.SetDirty();
        cameraData.resetHistory = true;
        SetTarget(camera, target);

        var frameA = RenderAndCapture(camera, target, WarmupFrames);
        var frameB = RenderAndCapture(camera, target, 1);

        run.sha256 = HashSamples(frameA);
        run.meanLuminance = MeanLuma(frameA);
        run.highFrequency = HighFrequency(frameA, ImageWidth, ImageHeight, 0, ImageHeight);
        run.highFrequencyBottomHalf = HighFrequency(frameA, ImageWidth, ImageHeight, ImageHeight / 2, ImageHeight);
        run.frameDelta = MeanAbsoluteLumaDelta(frameA, frameB);
        run.accurateNormalsRuntime = ReadRuntimeAccurateNormals(
            GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset);

        WriteImage(target, outputDirectory, "Attribution_" + run.name + ".png", run.images);

        CollectKeywords(gtao, ssl, run);

        if (run.frameDelta > k_FrameDeltaTolerance)
            run.issues.Add($"Frame-to-frame delta {run.frameDelta:F5} is above {k_FrameDeltaTolerance:F5}.");

        if (variant.gtaoEnabled)
        {
            SetGtaoDebug(gtao, 1, 0);
            SetTarget(camera, debugTarget);
            RenderAndCapture(camera, debugTarget, 4);
            WriteImage(debugTarget, outputDirectory, "Attribution_" + run.name + "_GtaoAO.png", run.images);
            SetGtaoDebug(gtao, 3, 0);
            RenderAndCapture(camera, debugTarget, 4);
            WriteImage(debugTarget, outputDirectory, "Attribution_" + run.name + "_GtaoRaw.png", run.images);
            SetGtaoDebug(gtao, 0, 0);
        }

        if (variant.volumeEnabled)
        {
            SetTarget(camera, debugTarget);
            CaptureSslDebug(ssl, camera, debugTarget, ScreenSpaceLightingDebugMode.SsgiRaw, "SsgiRaw", run, outputDirectory);
            CaptureSslDebug(ssl, camera, debugTarget, ScreenSpaceLightingDebugMode.SsgiDenoised, "SsgiDenoised", run, outputDirectory);
            CaptureSslDebug(ssl, camera, debugTarget, ScreenSpaceLightingDebugMode.SsrRaw, "SsrRaw", run, outputDirectory);
            CaptureSslDebug(ssl, camera, debugTarget, ScreenSpaceLightingDebugMode.SsrWeight, "SsrWeight", run, outputDirectory);
        }

        // Restore the frame target so the next run starts from the final image path.
        SetTarget(camera, target);
        return run;
    }

    static void CaptureSslDebug(
        ScreenSpaceLightingRendererFeature ssl,
        Camera camera,
        RenderTexture debugTarget,
        ScreenSpaceLightingDebugMode mode,
        string label,
        AttributionRun run,
        string outputDirectory)
    {
        SetSslDebug(ssl, mode, 0);
        RenderAndCapture(camera, debugTarget, 2);
        WriteImage(debugTarget, outputDirectory, "Attribution_" + run.name + "_" + label + ".png", run.images);
        SetSslDebug(ssl, ScreenSpaceLightingDebugMode.Off, 0);
    }

    static void CollectKeywords(
        GroundTruthAmbientOcclusionRendererFeature gtao,
        ScreenSpaceLightingRendererFeature ssl,
        AttributionRun run)
    {
        foreach (var shader in EnumerateComputes(gtao))
            AddKeywordFlags(run, shader);
        foreach (var shader in EnumerateComputes(ssl))
            AddKeywordFlags(run, shader);
    }

    static IEnumerable<ComputeShader> EnumerateComputes(ScriptableRendererFeature feature)
    {
        var settings = feature.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
        var resources = settings?.GetType().GetField("resources", InstanceFlags)?.GetValue(settings);
        if (resources == null)
            yield break;

        foreach (var field in resources.GetType().GetFields(InstanceFlags))
        {
            if (field.GetValue(resources) is ComputeShader shader)
                yield return shader;
        }
    }

    static void AddKeywordFlags(AttributionRun run, ComputeShader shader)
    {
        run.keywords.Add(new KeywordFlag
        {
            asset = shader.name,
            gtaoOct = shader.IsKeywordEnabled("GTAO_NORMALS_OCT"),
            ssgiOct = shader.IsKeywordEnabled("SSGI_NORMALS_OCT"),
            ssrOct = shader.IsKeywordEnabled("SSR_NORMALS_OCT")
        });
    }

    static string DescribeKeywords(AttributionRun run)
    {
        return string.Join(", ", run.keywords
            .Where(k => k.gtaoOct || k.ssgiOct || k.ssrOct)
            .Select(k => k.asset));
    }

    static bool ReadRuntimeAccurateNormals(UniversalRenderPipelineAsset pipeline)
    {
        if (pipeline == null)
            return false;

        var renderersField = typeof(UniversalRenderPipelineAsset).GetField("m_Renderers", InstanceFlags);
        if (renderersField?.GetValue(pipeline) is not ScriptableRenderer[] renderers || renderers.Length == 0)
            return false;

        var renderer = renderers[0];
        var property = renderer?.GetType().GetProperty("accurateGbufferNormals", InstanceFlags);
        return property != null && (bool)property.GetValue(renderer);
    }

    // ---------------------------------------------------------------------------------------------
    // Metrics
    // ---------------------------------------------------------------------------------------------

    static float Luma(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

    static float MeanLuma(Color[] samples)
    {
        double sum = 0.0;
        for (var i = 0; i < samples.Length; i++)
            sum += Luma(samples[i]);
        return samples.Length == 0 ? 0f : (float)(sum / samples.Length);
    }

    // Mean absolute difference between each pixel and the average of its 8 neighbours. Sky pixels are
    // smooth so the value reflects the screen-space effect noise; the rows are restricted so the floor
    // can be measured on its own.
    static float HighFrequency(Color[] samples, int width, int height, int rowStart, int rowEnd)
    {
        double sum = 0.0;
        long count = 0;
        rowStart = Mathf.Clamp(rowStart, 1, height - 2);
        rowEnd = Mathf.Clamp(rowEnd, rowStart + 1, height - 1);

        for (var y = rowStart; y < rowEnd; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var center = Luma(samples[y * width + x]);
                var neighbours = 0f;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        neighbours += Luma(samples[(y + dy) * width + (x + dx)]);
                    }
                }

                sum += Math.Abs(center - neighbours / 8f);
                count++;
            }
        }

        return count == 0 ? 0f : (float)(sum / count);
    }

    static float MeanAbsoluteLumaDelta(Color[] a, Color[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return float.PositiveInfinity;

        double sum = 0.0;
        for (var i = 0; i < a.Length; i++)
            sum += Math.Abs(Luma(a[i]) - Luma(b[i]));
        return (float)(sum / a.Length);
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

    // ---------------------------------------------------------------------------------------------
    // Capture plumbing (same conventions as the T5/T12 verification harnesses)
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
        if (camera == null)
            return;

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

    static void WriteImage(RenderTexture target, string outputDirectory, string fileName, List<string> images)
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
            var path = Path.Combine(outputDirectory, fileName);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            images?.Add(fileName);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Feature settings (reflection, mirrors the T5 harness)
    // ---------------------------------------------------------------------------------------------

    static void ApplyReferenceGtao(GroundTruthAmbientOcclusionRendererFeature gtao)
    {
        SetSetting(gtao, "intensity", 2f);
        SetSetting(gtao, "radius", 2f);
        SetSetting(gtao, "stepCount", 12);
        SetSetting(gtao, "directionCount", 2);
        SetSetting(gtao, "maximumRadiusInPixels", 40);
        SetSetting(gtao, "directLightingStrength", 0f);
        SetSetting(gtao, "spatialBilateralAggressiveness", 0.15f);
        SetSetting(gtao, "ghostingReduction", 0.5f);
        SetSetting(gtao, "blurSharpness", 0.1f);
        SetSetting(gtao, "temporalAccumulation", true);
        SetSetting(gtao, "bilateralUpsample", true);
        SetSetting(gtao, "fullResolution", false);
        SetGtaoDebug(gtao, 0, 0);
    }

    static object GetSettings(ScriptableRendererFeature feature)
    {
        return feature?.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
    }

    static void SetSetting(ScriptableRendererFeature feature, string name, object value)
    {
        var settings = GetSettings(feature);
        var field = settings?.GetType().GetField(name, InstanceFlags);
        if (field == null)
            return;

        field.SetValue(settings, value);
        EditorUtility.SetDirty(feature);
    }

    static void SetGtaoDebug(ScriptableRendererFeature gtao, int mode, int mip)
    {
        var settings = GetSettings(gtao);
        var modeField = settings?.GetType().GetField("debugMode", InstanceFlags);
        var mipField = settings?.GetType().GetField("debugMip", InstanceFlags);
        if (modeField != null)
            modeField.SetValue(settings, Enum.ToObject(modeField.FieldType, mode));
        if (mipField != null)
            mipField.SetValue(settings, mip);
        EditorUtility.SetDirty(gtao);
    }

    static void SetSslDebug(ScreenSpaceLightingRendererFeature ssl, ScreenSpaceLightingDebugMode mode, int mip)
    {
        SetSetting(ssl, "debugMode", mode);
        SetSetting(ssl, "debugMip", mip);
    }

    // ---------------------------------------------------------------------------------------------
    // Report
    // ---------------------------------------------------------------------------------------------

    sealed class Variant
    {
        public readonly string name;
        public readonly bool gtaoEnabled;
        public readonly bool volumeEnabled;

        public Variant(string name, bool gtaoEnabled, bool volumeEnabled)
        {
            this.name = name;
            this.gtaoEnabled = gtaoEnabled;
            this.volumeEnabled = volumeEnabled;
        }
    }

    [Serializable]
    sealed class AttributionReport
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public string rendererPath;
        public string scenePath;
        public bool runtimeAccurateNormals;
        public bool completed;
        public bool passed;
        public bool settingsRestored;
        public float luminanceTolerance;
        public float highFrequencyTolerance;
        public float frameDeltaTolerance;
        public string error;
        public List<AttributionRun> runs;
    }

    [Serializable]
    sealed class AttributionRun
    {
        public string name;
        public bool gtao;
        public bool volume;
        public bool accurateNormals;
        public bool accurateNormalsRuntime;
        public string sha256;
        public float meanLuminance;
        public float highFrequency;
        public float highFrequencyBottomHalf;
        public float frameDelta;
        public float luminanceDeltaVsCounterpart;
        public float highFrequencyDeltaVsCounterpart;
        public List<string> images;
        public List<KeywordFlag> keywords;
        public List<string> issues;
    }

    [Serializable]
    sealed class KeywordFlag
    {
        public string asset;
        public bool gtaoOct;
        public bool ssgiOct;
        public bool ssrOct;
    }
}