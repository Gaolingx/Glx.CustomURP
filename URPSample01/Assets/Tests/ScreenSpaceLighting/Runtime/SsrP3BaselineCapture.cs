using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// P0 baseline capture for round 23 (PLAN/TODO/23-Ray_Tracing_Inprove_2).
// The component is inert in the Editor; the fixture Player renders a fixed camera into a linear
// RGBA16F target, warms the SSR/denoiser history for a fixed number of camera renders and records
// the capture render as frame.raw + frame.png + Player.json.
public sealed class SsrP3BaselineCapture : MonoBehaviour
{
    public const int DefaultWarmupRenders = 120;
    public const int DefaultCaptureRender = 121;

    public UniversalRenderPipelineAsset pipeline;
    public Camera captureCamera;
    public Volume captureVolume;
    public int width = 1920;
    public int height = 1080;
    public int warmupRenders = DefaultWarmupRenders;
    public int captureRender = DefaultCaptureRender;

    string m_OutputRoot;
    string m_Variant;
    string m_Run;
    RenderTexture m_Target;
    bool m_Done;
    int m_RenderEnds;
    int m_FirstUnityFrame = -1;
    string m_Error;
    // Capture-frame evidence, sampled by the pass (see RequestRayTracingEvidence).
    int m_EvidenceTlasInstances = -1;
    uint[] m_EvidenceCounters;
    string m_EvidenceError;
    int m_HitDebug;
    float m_HitAmbientScale;
    // P3 harness switches (B0). They are applied to the active feature's serialized settings
    // before the first render; the pass consumes them from B2 on.
    bool m_P3SsrAfterGbuffer;
    bool m_P3SsrRollback;
    string m_P3SsrIndirect;

    [Serializable]
    sealed class PlayerReport
    {
        public string variant;
        public string run;
        public bool passed;
        public string error;
        public string utcTimestamp;
        public string unityVersion;
        public string buildGuid;
        public string os;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public string graphicsDeviceVersion;
        public bool supportsRayTracing;
        public bool supportsComputeShaders;
        public string colorSpace;
        public string pipeline;
        public string rendererModeActual;
        public string scene;
        public string cameraName;
        public Vector3 cameraPosition;
        public Quaternion cameraRotation;
        public float fieldOfView;
        public float nearClip;
        public float farClip;
        public int width;
        public int height;
        public string targetFormat;
        public bool targetSRGB;
        public int targetSamples;
        public bool hdr;
        public bool postProcessing;
        public string antialiasing;
        public int captureFramerate;
        public int warmupRenders;
        public int captureRender;
        public int hitDebug;
        public float hitAmbientScale;
        public int unityFrameAtCapture;
        public int firstUnityFrame;
        public string variantApplied;
        public bool ssrEnabled;
        public string ssrAlgorithm;
        public bool ssrFullResolution;
        public bool ssrDenoise;
        public int ssrDenoiserRadius;
        public float ssrMinSmoothness;
        public float ssrSmoothnessFadeStart;
        public float ssrDepthBufferThickness;
        public float ssrScreenFadeDistance;
        public int ssrRayMaxIterations;
        public bool ssrReflectSky;
        public float ssrRayLength;
        public float ssrIntensity;
        public float ssrBiasFactor;
        public bool ssrAffectSmoothSurfaces;
        public bool ssrSingleReflectionBounce;
        public bool featureRayTracingReflections;
        public int featureReflectionRayBudget;
        public bool featureHitDeferredShading;
        // P3 harness: the requested switches and the state read back from the feature settings.
        public bool p3SsrAfterGbufferRequested;
        public bool p3SsrRollbackRequested;
        public string p3SsrIndirectRequested;
        public bool featureSsrAfterGbuffer;
        public string featureSsrIndirectSource;
        public string featureReadback;
        // Direct DXR evidence: the TLAS the reflection dispatch used and the R1 request
        // counters of the captured frame (0 eligible/requested/served/overflow when skipped).
        public int tlasInstances;
        public int tlasDisabledSubmeshes;
        public int tlasBuilds;
        public int tlasRegisteredLights;
        public int tlasUploadedLights;
        public int tlasOverflowLights;
        public int r1Eligible;
        public int r1Requested;
        public int r1Served;
        public int r1Overflow;
        public string rayTracingEvidence;
        // P1 diagnostics: the hit-deferred gate, kernel and group count the pass actually saw.
        public int hitDeferredCalls;
        public int hitDeferredDispatches;
        public int hitDeferredSkipsSettings;
        public int hitDeferredSkipsCompute;
        public int hitDeferredGroups;
        public int hitDeferredKernel;
        public bool hitDeferredSettings;
        public bool hitDeferredComputeBound;
        public string frameRaw;
        public string framePng;
        public long frameRawBytes;
        public string frameSHA256;
        public double lumaMin;
        public double lumaMean;
        public double lumaMax;
        public int nonFiniteSamples;
        public SsrDispatchStats ssrDispatch;
    }

    [Serializable]
    sealed class SsrDispatchStats
    {
        public int frameIndex;
        public bool pbr;
        public bool halfResolution;
        public int traceWidth;
        public int traceHeight;
        public int traceDispatches;
        public int reprojectDispatches;
        public int hwMergeDispatches;
        public int temporalDispatches;
        public int copyHistoryDispatches;
        public int bilateralHDispatches;
        public int bilateralVDispatches;
        public int upsampleDispatches;
        public bool historyValid;
        public int historyResetCount;
    }

    static string Arg(string key)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == key)
                return args[i + 1];
        }
        return null;
    }

    static bool HasFlag(string key)
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg == key)
                return true;
        }
        return false;
    }

    void Awake()
    {
        Application.runInBackground = true;
        Time.captureFramerate = 60;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;

        m_OutputRoot = Arg("-p3-output");
        m_Variant = Arg("-p3-variant") ?? "base";
        m_Run = Arg("-p3-run") ?? "run-01";
        m_HitDebug = int.TryParse(Arg("-p3-hit-debug"), out var debugMode) ? debugMode : 0;
        m_HitAmbientScale = float.TryParse(Arg("-p3-hit-ambient"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ambientScale) ? ambientScale : 1f;
        m_P3SsrAfterGbuffer = HasFlag("-p3-ssr-after-gbuffer");
        m_P3SsrRollback = HasFlag("-p3-ssr-rollback");
        m_P3SsrIndirect = Arg("-p3-ssr-indirect");
        if (int.TryParse(Arg("-p3-warmup"), out var warmupOverride) && warmupOverride > 0)
        {
            warmupRenders = warmupOverride;
            captureRender = warmupOverride + 1;
        }
        SetHitDebug(m_HitDebug);
        SetHitAmbientScale(m_HitAmbientScale);
        if (string.IsNullOrEmpty(m_OutputRoot))
        {
            Debug.LogError("SsrP3BaselineCapture: -p3-output is required.");
            m_Error = "missing -p3-output";
            Finish();
            return;
        }

        try
        {
            if (pipeline == null || captureCamera == null || captureVolume == null)
                throw new InvalidOperationException("Fixture references are not assigned.");

            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            ApplyVariant();
            ApplyP3Switches();

            foreach (var camera in FindObjectsOfType<Camera>(true))
                camera.enabled = false;
            captureCamera.enabled = false;
            captureCamera.allowHDR = true;
            captureCamera.allowMSAA = false;
            captureCamera.allowDynamicResolution = false;
            captureCamera.aspect = (float)width / height;
            var data = captureCamera.GetUniversalAdditionalCameraData();
            data.antialiasing = AntialiasingMode.None;
            data.renderPostProcessing = false;

            var descriptor = new RenderTextureDescriptor(width, height, UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, 24)
            {
                msaaSamples = 1,
                useDynamicScale = false
            };
            m_Target = new RenderTexture(descriptor);
            if (!m_Target.Create())
                throw new InvalidOperationException("Linear RGBA16F target was not created.");
            captureCamera.targetTexture = m_Target;
            RenderPipelineManager.endCameraRendering += EndCamera;
        }
        catch (Exception exception)
        {
            m_Error = exception.ToString();
            Finish();
        }
    }

    void ApplyVariant()
    {
        var profile = captureVolume.sharedProfile;
        if (profile == null || !profile.TryGet<ScreenSpaceReflection>(out var ssr))
        {
            m_Error = "The fixture volume does not carry a ScreenSpaceReflection override.";
            return;
        }

        switch (m_Variant)
        {
            case "ssroff":
                ssr.enabled.Override(false);
                break;
            case "nodenoise":
                ssr.enabled.Override(true);
                ssr.denoise.Override(false);
                break;
            case "base":
                ssr.enabled.Override(true);
                ssr.denoise.Override(true);
                break;
            default:
                m_Error = "Unknown -p3-variant: " + m_Variant;
                break;
        }
    }

    void EndCamera(ScriptableRenderContext context, Camera camera)
    {
        if (camera != captureCamera)
            return;
        if (m_FirstUnityFrame < 0)
            m_FirstUnityFrame = Time.frameCount;
        m_RenderEnds++;
    }

    void Update()
    {
        if (m_Done)
            return;

        try
        {
            if (!string.IsNullOrEmpty(m_Error))
            {
                Finish();
                return;
            }

            var before = m_RenderEnds;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (m_RenderEnds + 1 == captureRender)
                RequestRayTracingEvidence();
#endif
            captureCamera.Render();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (m_RenderEnds >= captureRender)
                CompleteRayTracingEvidence();
#endif
            if (m_RenderEnds != before + 1)
                throw new InvalidOperationException("Expected one finished camera render.");

            if (m_RenderEnds < captureRender)
                return;

            WriteCapture();
            Finish();
        }
        catch (Exception exception)
        {
            m_Error = exception.ToString();
            Finish();
        }
    }

    static void SetHitDebug(int mode)
    {
        try
        {
            var type = FindType("UnityEngine.Rendering.Universal.ScreenSpaceLightingSsrPass");
            type?.GetField("s_HitDebug", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, mode);
        }
        catch (Exception)
        {
        }
    }

    static void SetHitAmbientScale(float scale)
    {
        try
        {
            var type = FindType("UnityEngine.Rendering.Universal.ScreenSpaceLightingSsrPass");
            type?.GetField("s_HitAmbientScale", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, scale);
        }
        catch (Exception)
        {
        }
    }

    // The capture frame's R1 counters and TLAS size must be sampled by the pass itself: after
    // the frame the hybrid queue is detached, and reading inside the pass would race the
    // recorded commands. The pass requests an async readback on this frame only.
    void RequestRayTracingEvidence()
    {
        try
        {
            var type = FindType("UnityEngine.Rendering.Universal.ScreenSpaceLightingSsrPass");
            type?.GetMethod("ResetEvidence", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(null, null);
            var flag = type?.GetField("s_RequestEvidence", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            flag?.SetValue(null, true);
        }
        catch (Exception exception)
        {
            m_EvidenceError = "request: " + exception.GetType().Name;
        }
    }

    void CompleteRayTracingEvidence()
    {
        try
        {
            UnityEngine.Rendering.AsyncGPUReadback.WaitAllRequests();
            var type = FindType("UnityEngine.Rendering.Universal.ScreenSpaceLightingSsrPass");
            var tlas = type?.GetField("s_EvidenceTlasInstances", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var counters = type?.GetField("s_EvidenceCounters", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (tlas != null) m_EvidenceTlasInstances = (int)tlas.GetValue(null);
            m_EvidenceCounters = counters?.GetValue(null) as uint[];
        }
        catch (Exception exception)
        {
            m_EvidenceError = "complete: " + exception.GetType().Name;
        }
    }

    void WriteCapture()
    {
        Directory.CreateDirectory(m_OutputRoot);

        var readback = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);
        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = m_Target;
            readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            readback.Apply(false);
        }
        finally
        {
            RenderTexture.active = previous;
        }

        var raw = readback.GetRawTextureData();
        var rawPath = Path.Combine(m_OutputRoot, "frame.raw");
        File.WriteAllBytes(rawPath, raw);

        var pixels = readback.GetPixels();
        Destroy(readback);

        var report = new PlayerReport
        {
            variant = m_Variant,
            run = m_Run,
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            buildGuid = Application.buildGUID,
            os = SystemInfo.operatingSystem,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
            supportsRayTracing = SystemInfo.supportsRayTracing,
            supportsComputeShaders = SystemInfo.supportsComputeShaders,
            colorSpace = QualitySettings.activeColorSpace.ToString(),
            pipeline = pipeline.name,
            scene = captureCamera.gameObject.scene.path,
            cameraName = captureCamera.name,
            cameraPosition = captureCamera.transform.position,
            cameraRotation = captureCamera.transform.rotation,
            fieldOfView = captureCamera.fieldOfView,
            nearClip = captureCamera.nearClipPlane,
            farClip = captureCamera.farClipPlane,
            width = width,
            height = height,
            targetFormat = m_Target.graphicsFormat.ToString(),
            targetSRGB = m_Target.sRGB,
            targetSamples = m_Target.antiAliasing,
            hdr = captureCamera.allowHDR && pipeline.supportsHDR,
            postProcessing = captureCamera.GetUniversalAdditionalCameraData().renderPostProcessing,
            antialiasing = captureCamera.GetUniversalAdditionalCameraData().antialiasing.ToString(),
            captureFramerate = Time.captureFramerate,
            warmupRenders = warmupRenders,
            captureRender = m_RenderEnds,
            hitDebug = m_HitDebug,
            hitAmbientScale = m_HitAmbientScale,
            p3SsrAfterGbufferRequested = m_P3SsrAfterGbuffer,
            p3SsrRollbackRequested = m_P3SsrRollback,
            p3SsrIndirectRequested = m_P3SsrIndirect,
            unityFrameAtCapture = Time.frameCount,
            firstUnityFrame = m_FirstUnityFrame,
            variantApplied = m_Variant,
            frameRaw = Path.GetFileName(rawPath),
            frameRawBytes = raw.LongLength,
            frameSHA256 = Hash(raw),
            ssrDispatch = ReadDispatchStats()
        };

        var lumaMin = double.MaxValue;
        var lumaMax = double.MinValue;
        var lumaSum = 0.0;
        var nonFinite = 0;
        var encoded = new Color32[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            if (float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b) ||
                float.IsInfinity(c.r) || float.IsInfinity(c.g) || float.IsInfinity(c.b))
            {
                nonFinite++;
                c = Color.black;
            }
            var luma = 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
            lumaMin = Math.Min(lumaMin, luma);
            lumaMax = Math.Max(lumaMax, luma);
            lumaSum += luma;
            encoded[i] = ToDisplay(c);
        }
        report.lumaMin = lumaMin;
        report.lumaMean = lumaSum / pixels.Length;
        report.lumaMax = lumaMax;
        report.nonFiniteSamples = nonFinite;

        var pngPath = Path.Combine(m_OutputRoot, "frame.png");
        var display = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        try
        {
            display.SetPixels32(encoded);
            display.Apply(false);
            File.WriteAllBytes(pngPath, display.EncodeToPNG());
        }
        finally
        {
            Destroy(display);
        }
        report.framePng = Path.GetFileName(pngPath);

        var data = captureCamera.GetUniversalAdditionalCameraData();
        report.rendererModeActual = ReadRendererMode(data);
        ReadVolume(report);
        ReadFeature(report);
        ReadRayTracingEvidence(report);
        ReadHitDeferredDiagnostics(report);
        if (m_EvidenceCounters != null && m_EvidenceCounters.Length >= 4)
        {
            report.r1Eligible = (int)m_EvidenceCounters[0];
            report.r1Requested = (int)m_EvidenceCounters[1];
            report.r1Served = (int)m_EvidenceCounters[2];
            report.r1Overflow = (int)m_EvidenceCounters[3];
            report.rayTracingEvidence = "ok (capture frame readback)";
        }
        if (m_EvidenceTlasInstances >= 0)
            report.tlasInstances = m_EvidenceTlasInstances;
        if (!string.IsNullOrEmpty(m_EvidenceError))
            report.rayTracingEvidence = m_EvidenceError;
        report.passed = string.IsNullOrEmpty(m_Error);
        File.WriteAllText(Path.Combine(m_OutputRoot, "Player.json"), JsonUtility.ToJson(report, true));
    }

    static Color32 ToDisplay(Color c)
    {
        // Review-only tonemap; frame.raw stays the authoritative linear signal.
        var r = Encode(c.r);
        var g = Encode(c.g);
        var b = Encode(c.b);
        return new Color32(r, g, b, 255);
    }

    static byte Encode(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
            return 0;
        var mapped = value / (1f + value);
        var gamma = Mathf.Pow(mapped, 1f / 2.2f);
        return (byte)Mathf.Clamp(Mathf.RoundToInt(gamma * 255f), 0, 255);
    }

    static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(bytes);
        var text = new System.Text.StringBuilder(digest.Length * 2);
        foreach (var b in digest)
            text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return text.ToString();
    }

    static void ReadVolume(PlayerReport report)
    {
        if (VolumeManager.instance == null || VolumeManager.instance.stack == null)
            return;
        var ssr = VolumeManager.instance.stack.GetComponent<ScreenSpaceReflection>();
        if (ssr == null)
            return;
        report.ssrEnabled = ssr.enabled.value;
        report.ssrAlgorithm = ssr.usedAlgorithm.value.ToString();
        report.ssrFullResolution = ssr.fullResolution.value;
        report.ssrDenoise = ssr.denoise.value;
        report.ssrDenoiserRadius = ssr.denoiserRadius.value;
        report.ssrMinSmoothness = ssr.minSmoothness.value;
        report.ssrSmoothnessFadeStart = ssr.smoothnessFadeStart.value;
        report.ssrDepthBufferThickness = ssr.depthBufferThickness.value;
        report.ssrScreenFadeDistance = ssr.screenFadeDistance.value;
        report.ssrRayMaxIterations = ssr.rayMaxIterations.value;
        report.ssrReflectSky = ssr.reflectSky.value;
        report.ssrRayLength = ssr.rayLength.value;
        report.ssrIntensity = ssr.intensity.value;
        report.ssrBiasFactor = ssr.biasFactor.value;
        report.ssrAffectSmoothSurfaces = ssr.affectSmoothSurfaces.value;
        report.ssrSingleReflectionBounce = ssr.singleReflectionBounce.value;
    }

    static string ReadRendererMode(UniversalAdditionalCameraData data)
    {
        try
        {
            var renderer = data.scriptableRenderer;
            var property = renderer.GetType().GetProperty("renderingModeActual", BindingFlags.Instance | BindingFlags.NonPublic);
            return property?.GetValue(renderer)?.ToString() ?? "unknown";
        }
        catch (Exception exception)
        {
            return "error: " + exception.GetType().Name;
        }
    }

    // Applies the P3 harness switches to the active feature's serialized settings before the first
    // render. The switches are inert until B2 wires them into the pass; the report records both the
    // requested value and the state read back from the feature so evidence never conflates them.
    void ApplyP3Switches()
    {
        if (!TryGetFeatureSettings(out var settings) || settings == null)
            return;

        try
        {
            var type = settings.GetType();
            var afterGbuffer = type.GetField("ssrAfterGbuffer", BindingFlags.Instance | BindingFlags.NonPublic);
            if (afterGbuffer != null)
            {
                bool value = m_P3SsrRollback
                    ? false
                    : m_P3SsrAfterGbuffer
                        ? true
                        : (bool)afterGbuffer.GetValue(settings);
                afterGbuffer.SetValue(settings, value);
            }

            var indirect = type.GetField("ssrIndirectSource", BindingFlags.Instance | BindingFlags.NonPublic);
            int index = ParseIndirect(m_P3SsrIndirect);
            if (indirect != null && index >= 0)
                indirect.SetValue(settings, Enum.ToObject(indirect.FieldType, index));
        }
        catch (Exception exception)
        {
            m_Error = "ApplyP3Switches: " + exception;
        }
    }

    static int ParseIndirect(string value)
    {
        switch ((value ?? string.Empty).ToLowerInvariant())
        {
            case "a1": return 0;
            case "a2": return 1;
            case "a3": return 2;
            default: return -1;
        }
    }

    static bool TryGetFeatureSettings(out object settings)
    {
        settings = null;
        try
        {
            var pipelineField = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic);
            var list = pipelineField?.GetValue(GraphicsSettings.currentRenderPipeline) as Array;
            var data = list != null && list.Length > 0 ? list.GetValue(0) as ScriptableRendererData : null;
            if (data == null)
                return false;
            foreach (var feature in data.rendererFeatures)
            {
                if (feature == null || feature.GetType().Name != "ScreenSpaceLightingRendererFeature")
                    continue;
                var settingsField = feature.GetType().GetField("m_Settings", BindingFlags.Instance | BindingFlags.NonPublic);
                settings = settingsField?.GetValue(feature);
                return settings != null;
            }
        }
        catch (Exception)
        {
        }
        return false;
    }

    static void ReadFeature(PlayerReport report)
    {
        try
        {
            if (!TryGetFeatureSettings(out var settings) || settings == null)
            {
                report.featureReadback = "feature not found";
                return;
            }

            var type = settings.GetType();
            var reflections = type.GetField("rayTracingReflections", BindingFlags.Instance | BindingFlags.NonPublic);
            var budget = type.GetField("reflectionRayBudget", BindingFlags.Instance | BindingFlags.NonPublic);
            var hitDeferred = type.GetField("hitDeferredShading", BindingFlags.Instance | BindingFlags.NonPublic);
            var afterGbuffer = type.GetField("ssrAfterGbuffer", BindingFlags.Instance | BindingFlags.NonPublic);
            var indirect = type.GetField("ssrIndirectSource", BindingFlags.Instance | BindingFlags.NonPublic);
            report.featureRayTracingReflections = reflections != null && (bool)reflections.GetValue(settings);
            report.featureReflectionRayBudget = budget != null ? (int)budget.GetValue(settings) : -1;
            report.featureHitDeferredShading = hitDeferred != null && (bool)hitDeferred.GetValue(settings);
            report.featureSsrAfterGbuffer = afterGbuffer != null && (bool)afterGbuffer.GetValue(settings);
            report.featureSsrIndirectSource = indirect != null ? indirect.GetValue(settings).ToString() : "unknown";
            report.featureReadback = "ok";
        }
        catch (Exception exception)
        {
            report.featureReadback = "error: " + exception.GetType().Name;
        }
    }

    // Direct evidence for the P0 OPEN gates: the TLAS the reflection dispatch used, and the
    // captured frame's R1 counters (eligible/requested/served/overflow). The queue is internal,
    // so this mirrors ReadFeature's reflection style.
    static void ReadRayTracingEvidence(PlayerReport report)
    {
        report.rayTracingEvidence = "not found";
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var pipelineField = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic);
            var list = pipelineField?.GetValue(GraphicsSettings.currentRenderPipeline) as Array;
            var data = list != null && list.Length > 0 ? list.GetValue(0) as ScriptableRendererData : null;
            object feature = null;
            if (data != null)
            {
                foreach (var candidate in data.rendererFeatures)
                {
                    if (candidate != null && candidate.GetType().Name == "ScreenSpaceLightingRendererFeature")
                    {
                        feature = candidate;
                        break;
                    }
                }
            }
            if (feature == null)
            {
                report.rayTracingEvidence = "feature not found";
                return;
            }

            var scene = feature.GetType().GetField("m_RayTracingScene", flags)?.GetValue(feature);
            if (scene != null)
            {
                var t = scene.GetType();
                report.tlasInstances = (int)t.GetProperty("instanceCount", flags).GetValue(scene);
                report.tlasDisabledSubmeshes = (int)t.GetProperty("disabledSubmeshes", flags).GetValue(scene);
                report.tlasBuilds = (int)t.GetProperty("buildCount", flags).GetValue(scene);
                report.tlasRegisteredLights = (int)t.GetProperty("registeredLights", flags).GetValue(scene);
                report.tlasUploadedLights = (int)t.GetProperty("uploadedLights", flags).GetValue(scene);
                report.tlasOverflowLights = (int)t.GetProperty("tableOverflow", flags).GetValue(scene);
            }

            var pass = feature.GetType().GetField("m_SsrPass", flags)?.GetValue(feature);
            var queue = pass?.GetType().GetField("requestQueue", flags)?.GetValue(pass);
            var counters = queue?.GetType().GetField("counters", flags)?.GetValue(queue) as ComputeBuffer;
            if (counters == null || counters.count < 4)
            {
                report.rayTracingEvidence = "no R1 queue (DXR path skipped)";
                return;
            }

            var values = new uint[counters.count];
            counters.GetData(values);
            report.r1Eligible = (int)values[0];
            report.r1Requested = (int)values[1];
            report.r1Served = (int)values[2];
            report.r1Overflow = (int)values[3];
            report.rayTracingEvidence = "ok";
        }
        catch (Exception exception)
        {
            report.rayTracingEvidence = "error: " + exception.GetType().Name;
        }
    }

    // P1: the GPU capture showed the hit-shading marker with no dispatch, so the report has to
    // say whether the pass gated the dispatch or issued it. Statics are read by name here for
    // the same reason as the R1 counters: the Player strips unused internal members.
    static void ReadHitDeferredDiagnostics(PlayerReport report)
    {
        try
        {
            var type = FindType("UnityEngine.Rendering.Universal.ScreenSpaceLightingSsrPass");
            if (type == null)
                return;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            report.hitDeferredCalls = (int)type.GetField("s_HitDeferredCalls", flags).GetValue(null);
            report.hitDeferredDispatches = (int)type.GetField("s_HitDeferredDispatches", flags).GetValue(null);
            report.hitDeferredSkipsSettings = (int)type.GetField("s_HitDeferredSkipsSettings", flags).GetValue(null);
            report.hitDeferredSkipsCompute = (int)type.GetField("s_HitDeferredSkipsCompute", flags).GetValue(null);
            report.hitDeferredGroups = (int)type.GetField("s_HitDeferredGroups", flags).GetValue(null);
            report.hitDeferredKernel = (int)type.GetField("s_HitDeferredKernel", flags).GetValue(null);
            report.hitDeferredSettings = (bool)type.GetField("s_HitDeferredSettings", flags).GetValue(null);
            report.hitDeferredComputeBound = (bool)type.GetField("s_HitDeferredComputeBound", flags).GetValue(null);
        }
        catch (Exception)
        {
        }
    }

    static SsrDispatchStats ReadDispatchStats()    {        try
        {
            var type = FindType("UnityEngine.Rendering.Universal.ScreenSpaceLightingSsrPass");
            var field = type?.GetField("s_LastDispatchStats", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var source = field?.GetValue(null);
            if (source == null)
                return null;

            var result = new SsrDispatchStats();
            foreach (var target in typeof(SsrDispatchStats).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                var origin = source.GetType().GetField(target.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (origin == null || origin.FieldType != target.FieldType)
                    continue;
                target.SetValue(result, origin.GetValue(source));
            }
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static Type FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName);
            if (type != null)
                return type;
        }
        return null;
    }

    void Finish()
    {
        m_Done = true;
        RenderPipelineManager.endCameraRendering -= EndCamera;
        if (captureCamera != null)
            captureCamera.targetTexture = null;
        if (m_Target != null)
        {
            m_Target.Release();
            Destroy(m_Target);
            m_Target = null;
        }

        if (!string.IsNullOrEmpty(m_Error) && !string.IsNullOrEmpty(m_OutputRoot))
        {
            Directory.CreateDirectory(m_OutputRoot);
            File.WriteAllText(Path.Combine(m_OutputRoot, "CaptureError.txt"), m_Error);
            Debug.LogError("SsrP3BaselineCapture failed: " + m_Error);
        }
        Application.Quit(string.IsNullOrEmpty(m_Error) ? 0 : 1);
    }
}
