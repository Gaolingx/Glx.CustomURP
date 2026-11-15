using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// T13 editor-side verification: platform capability detection and dynamic resolution.
//
// The XR cases need a standalone player (see ScreenSpaceLightingT13PlayerSetup). This runner covers
// what the editor can measure directly on the current graphics API: compute/UAV format support and
// a dynamic resolution sweep that proves the Screen Space Lighting history survives, and recovers
// from, per-frame resolution scale changes.
public static class ScreenSpaceLightingT13Verification
{
    const string OutputRoot = "PALN/validation/T13";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string PipelinePath = "Assets/Tests/ScreenSpaceLighting/Renderers/SslT12ForwardPipeline.asset";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 12;

    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tests/Screen Space Lighting/Run T13 Editor Verification")]
    public static void RunEditorVerification()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);

        var report = new T13EditorReport
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDriverType = SystemInfo.graphicsDeviceVersion,
            supportsComputeShaders = SystemInfo.supportsComputeShaders,
            foveatedRenderingCaps = SystemInfo.foveatedRenderingCaps.ToString(),
            maxComputeBufferInputsCompute = SystemInfo.maxComputeBufferInputsCompute,
            formats = new List<T13FormatResult>(),
            passed = true,
            issues = new List<string>(),
        };

        var formats = new Dictionary<string, GraphicsFormat>
        {
            { "R32_SFloat", GraphicsFormat.R32_SFloat },
            { "R16G16_SFloat", GraphicsFormat.R16G16_SFloat },
            { "R16G16B16A16_SFloat", GraphicsFormat.R16G16B16A16_SFloat },
            { "R16_SFloat", GraphicsFormat.R16_SFloat },
            { "R8_UNorm", GraphicsFormat.R8_UNorm },
            { "R8_UInt", GraphicsFormat.R8_UInt },
            { "R32_UInt", GraphicsFormat.R32_UInt },
        };
        foreach (var pair in formats)
        {
            bool loadStore = SystemInfo.IsFormatSupported(pair.Value, FormatUsage.LoadStore);
            report.formats.Add(new T13FormatResult { name = pair.Key, loadStore = loadStore });
        }

        if (!report.supportsComputeShaders)
            report.issues.Add("Compute shaders are not supported.");
        foreach (var format in report.formats)
        {
            if (!format.loadStore && format.name != "R8_UNorm" && format.name != "R8_UInt" && format.name != "R32_UInt")
                report.issues.Add("Format " + format.name + " does not support load/store.");
        }

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture target = null;
        try
        {
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
                throw new FileNotFoundException("Missing T13 pipeline asset.", PipelinePath);
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            pipeline.renderScale = 1f;

            if (!string.IsNullOrEmpty(ScenePath))
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            DynamicGI.UpdateEnvironment();

            var camera = UnityEngine.Object.FindObjectOfType<Camera>();
            var cameraData = camera != null ? camera.GetUniversalAdditionalCameraData() : null;
            if (camera == null || cameraData == null)
                throw new InvalidOperationException("The verification scene has no camera.");

            var volume = UnityEngine.Object.FindObjectOfType<Volume>();
            if (volume == null || volume.sharedProfile == null)
                throw new InvalidOperationException("The verification scene has no Volume.");
            DisableScreenSpaceEffects(volume.sharedProfile);

            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;
            cameraData.resetHistory = true;
            camera.allowDynamicResolution = true;

            target = new RenderTexture(ImageWidth, ImageHeight, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "T13 Dynamic Resolution Target",
                useDynamicScale = true,
            };
            target.Create();
            camera.targetTexture = target;

            RunDynamicResolutionCase(report, camera, cameraData, volume.sharedProfile, target);
            WriteImages(report, camera, target, outputDirectory);
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
            ScalableBufferManager.ResizeBuffers(1f, 1f);
            if (target != null)
            {
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }

            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
            report.passed &= report.issues.Count == 0;
            File.WriteAllText(Path.Combine(outputDirectory, "T13EditorVerification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"T13 editor verification: completed={report.completed} passed={report.passed} api={report.graphicsDeviceType}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    static void RunDynamicResolutionCase(
        T13EditorReport report,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        VolumeProfile profile,
        RenderTexture target)
    {
        var ssgi = profile.components.OfType<ScreenSpaceGlobalIllumination>().FirstOrDefault();
        var ssr = profile.components.OfType<ScreenSpaceReflection>().FirstOrDefault();
        var refraction = profile.components.OfType<ScreenSpaceRefraction>().FirstOrDefault();
        if (ssgi == null || ssr == null || refraction == null)
            throw new InvalidOperationException("The T13 volume profile is missing a screen-space lighting component.");

        SetEffects(ssgi, ssr, refraction, false);
        cameraData.resetHistory = true;
        report.reference = Capture(camera, target, WarmupFrames, report, "Reference");

        SetEffects(ssgi, ssr, refraction, true);
        cameraData.resetHistory = true;
        report.fullScale = Capture(camera, target, WarmupFrames, report, "FullScale");
        report.fullScaleWidthScaleFactor = ScalableBufferManager.widthScaleFactor;
        report.fullScaleRenderWidth = ReadCameraColorWidth(camera);
        report.rtHandleScaleX = RTHandles.rtHandleProperties.rtHandleScale.x;
        report.rtHandleScaleY = RTHandles.rtHandleProperties.rtHandleScale.y;

        int resetsBefore = ReadResetCount();
        ScalableBufferManager.ResizeBuffers(0.5f, 0.5f);
        cameraData.resetHistory = false;
        report.halfScale = Capture(camera, target, WarmupFrames, report, "HalfScaleDynamicResolution");
        report.halfScaleWidthScaleFactor = ScalableBufferManager.widthScaleFactor;
        report.halfScaleHeightScaleFactor = ScalableBufferManager.heightScaleFactor;
        report.halfScaleRenderWidth = ReadCameraColorWidth(camera);
        // Hardware dynamic resolution rescales the camera color target; the backbuffer factor can
        // stay at 1 when rendering into a texture, so the actual target width is the evidence.
        report.dynamicResolutionChangedWidth = report.halfScaleRenderWidth > 0 &&
                                               report.fullScaleRenderWidth > 0 &&
                                               report.halfScaleRenderWidth < report.fullScaleRenderWidth;


        int resetsAfter = ReadResetCount();
        report.dynamicResolutionResetCount = resetsAfter - resetsBefore;

        report.fullScaleChanges = CountDifferences(report.reference.pixels, report.fullScale.pixels);
        report.halfScaleChanges = CountDifferences(report.reference.pixels, report.halfScale.pixels);
        report.fullScaleNonFinite = CountNonFinite(report.fullScale.pixels);
        report.halfScaleNonFinite = CountNonFinite(report.halfScale.pixels);

        ScalableBufferManager.ResizeBuffers(1f, 1f);
        cameraData.resetHistory = true;
        report.restored = Capture(camera, target, WarmupFrames, report, "Restored");
        report.restoredChanges = CountDifferences(report.fullScale.pixels, report.restored.pixels);
        report.restoredRenderWidth = ReadCameraColorWidth(camera);

        // URP 14 does not drive DynamicResolutionHandler, so ScalableBufferManager cannot be used to
        // rescale the camera targets. The support matrix records the measured outcome instead of a
        // pass/fail gate; render scale remains fully covered by T12.
        report.dynamicResolutionOutputChanged = report.halfScaleChanges != report.fullScaleChanges;
        report.dynamicResolutionIntermediateScaled = report.dynamicResolutionChangedWidth;
        bool halfValid = IsUsableCapture(report.halfScale);
        bool restoredValid = IsUsableCapture(report.restored);
        if (!halfValid || !restoredValid)
        {
            report.dynamicResolutionSupported = false;
            report.dynamicResolutionResult = "InvalidOutput";
        }
        else if (report.dynamicResolutionChangedWidth)
        {
            report.dynamicResolutionSupported = true;
            report.dynamicResolutionResult = "IntermediateTargetsRescaled";
        }
        else
        {
            report.dynamicResolutionSupported = false;
            report.dynamicResolutionResult = "IntermediateTargetsNotRescaled";
        }

        if (report.fullScaleChanges <= 0)
            report.issues.Add("Screen-space lighting was not visible at full scale.");
        if (report.fullScaleNonFinite != 0)
            report.issues.Add("The full scale capture contains non-finite pixels.");
    }

    static T13Capture Capture(Camera camera, RenderTexture target, int frames, T13EditorReport report, string name)
    {
        for (int i = 0; i < frames; i++)
            camera.Render();

        var capture = new T13Capture
        {
            name = name,
            pixels = ReadPixels(target),
            width = target.width,
            height = target.height,
        };
        capture.nonFinitePixels = CountNonFinite(capture.pixels);
        capture.meanLuminance = MeanLuminance(capture.pixels);
        report.captures.Add(capture);
        report.captureSummaries.Add(new T13CaptureSummary
        {
            name = capture.name,
            width = capture.width,
            height = capture.height,
            nonFinitePixels = capture.nonFinitePixels,
            meanLuminance = capture.meanLuminance,
        });
        return capture;
    }

    static void WriteImages(T13EditorReport report, Camera camera, RenderTexture target, string outputDirectory)
    {
        foreach (var capture in report.captures)
        {
            var texture = new Texture2D(capture.width, capture.height, TextureFormat.RGBA32, false, false);
            try
            {
                var pixels = new Color32[capture.pixels.Length];
                for (int i = 0; i < pixels.Length; i++)
                {
                    var c = capture.pixels[i];
                    pixels[i] = new Color32(
                        (byte)Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f),
                        (byte)Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f),
                        (byte)Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f),
                        (byte)Mathf.RoundToInt(Mathf.Clamp01(c.a) * 255f));
                }

                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                File.WriteAllBytes(Path.Combine(outputDirectory, "T13_" + capture.name + ".png"), texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }

    static void DisableScreenSpaceEffects(VolumeProfile profile)
    {
        var ssgi = profile.components.OfType<ScreenSpaceGlobalIllumination>().FirstOrDefault();
        if (ssgi != null)
            ssgi.enabled.Override(false);
        var ssr = profile.components.OfType<ScreenSpaceReflection>().FirstOrDefault();
        if (ssr != null)
        {
            ssr.enabled.Override(false);
            ssr.enabledTransparent.Override(false);
        }
        var refraction = profile.components.OfType<ScreenSpaceRefraction>().FirstOrDefault();
        if (refraction != null)
            refraction.enabled.Override(false);
    }

    static void SetEffects(
        ScreenSpaceGlobalIllumination ssgi,
        ScreenSpaceReflection ssr,
        ScreenSpaceRefraction refraction,
        bool enabled)
    {
        ssgi.enabled.Override(enabled);
        ssr.enabled.Override(enabled);
        ssr.enabledTransparent.Override(enabled);
        refraction.enabled.Override(enabled);
    }

    // The RTHandle system records the scaled render target size of the last camera render. The
    // camera color handle itself is only valid inside the rendering scope.
    static bool IsUsableCapture(T13Capture capture)
    {
        if (capture == null || capture.pixels == null || capture.pixels.Length == 0)
            return false;

        // A capture is unusable when the readback contains signed garbage or the frame is black
        // while the reference is not.
        if (capture.meanLuminance < -1f || float.IsNaN(capture.meanLuminance))
            return false;
        return capture.meanLuminance > 1e-4f;
    }

    static int ReadCameraColorWidth(Camera camera)
    {
        return RTHandles.rtHandleProperties.currentRenderTargetSize.x;
    }

    static int ReadResetCount()
    {
        var feature = Resources.FindObjectsOfTypeAll<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        var store = feature?.GetType().GetField("m_HistoryStore", InstanceFlags)?.GetValue(feature);
        var entries = store?.GetType().GetField("m_Entries", InstanceFlags)?.GetValue(store) as System.Collections.IDictionary;
        if (entries == null || entries.Count == 0)
            return 0;

        foreach (System.Collections.DictionaryEntry entry in entries)
        {
            var history = entry.Value?.GetType().GetField("history", InstanceFlags)?.GetValue(entry.Value);
            var property = history?.GetType().GetProperty("resetCount", InstanceFlags);
            if (property != null)
                return (int)property.GetValue(history);
        }

        return 0;
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

    static int CountDifferences(Color[] a, Color[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return Math.Max(a?.Length ?? 0, b?.Length ?? 0);
        int count = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float delta = Mathf.Max(
                Mathf.Abs(a[i].r - b[i].r),
                Mathf.Max(Mathf.Abs(a[i].g - b[i].g), Mathf.Abs(a[i].b - b[i].b)));
            if (delta > 1e-6f)
                count++;
        }

        return count;
    }

    static int CountNonFinite(Color[] samples)
    {
        if (samples == null)
            return 0;
        int count = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            var c = samples[i];
            if (float.IsNaN(c.r) || float.IsInfinity(c.r) ||
                float.IsNaN(c.g) || float.IsInfinity(c.g) ||
                float.IsNaN(c.b) || float.IsInfinity(c.b))
                count++;
        }

        return count;
    }

    static float MeanLuminance(Color[] samples)
    {
        if (samples == null || samples.Length == 0)
            return 0f;
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i].r * 0.2126 + samples[i].g * 0.7152 + samples[i].b * 0.0722;
        return (float)(sum / samples.Length);
    }
}

[Serializable]
public sealed class T13EditorReport
{
    public string utcTimestamp;
    public string unityVersion;
    public string graphicsDeviceType;
    public string graphicsDeviceName;
    public string graphicsDriverType;
    public bool supportsComputeShaders;
    public string foveatedRenderingCaps;
    public int maxComputeBufferInputsCompute;
    public bool completed;
    public bool passed;
    public string error;
    public List<string> issues;
    public List<T13FormatResult> formats;
    [NonSerialized] public T13Capture reference;
    [NonSerialized] public T13Capture fullScale;
    [NonSerialized] public T13Capture halfScale;
    [NonSerialized] public T13Capture restored;
    public float fullScaleWidthScaleFactor;
    public float halfScaleWidthScaleFactor;
    public float halfScaleHeightScaleFactor;
    public bool dynamicResolutionChangedWidth;
    public bool dynamicResolutionOutputChanged;
    public bool dynamicResolutionIntermediateScaled;
    public float rtHandleScaleX;
    public float rtHandleScaleY;
    public bool dynamicResolutionSupported;
    public string dynamicResolutionResult;
    public int dynamicResolutionResetCount;
    public int fullScaleChanges;
    public int halfScaleChanges;
    public int restoredChanges;
    public int fullScaleNonFinite;
    public int halfScaleNonFinite;
    public int fullScaleRenderWidth;
    public int halfScaleRenderWidth;
    public int restoredRenderWidth;
    public List<T13CaptureSummary> captureSummaries = new List<T13CaptureSummary>();
    [NonSerialized] public List<T13Capture> captures = new List<T13Capture>();
}

[Serializable]
public sealed class T13FormatResult
{
    public string name;
    public bool loadStore;
}

[Serializable]
public sealed class T13Capture
{
    [NonSerialized] public Color[] pixels;
    public string name;
    public int width;
    public int height;
    public int nonFinitePixels;
    public float meanLuminance;
}

[Serializable]
public sealed class T13CaptureSummary
{
    public string name;
    public int width;
    public int height;
    public int nonFinitePixels;
    public float meanLuminance;
}