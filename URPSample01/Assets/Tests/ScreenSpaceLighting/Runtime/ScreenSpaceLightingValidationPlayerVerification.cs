using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Generic validation player for the 06 all-effects round.
//
// A manifest selects the renderer, the Volume / renderer-feature settings and the camera pose for
// each case. Every case warms up, triggers one RenderDoc capture through the in-application API,
// saves a PNG of the same steady-state frame and records CPU-side metrics. When the case requests
// it, the binding probe kernel is read back and compared field by field against the values the
// Screen Space Lighting / GTAO passes produced for that frame.
public sealed class ScreenSpaceLightingValidationPlayerVerification : MonoBehaviour
{
    const int RenderWidth = 1280;
    const int RenderHeight = 720;

    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Header("Pipelines")]
    public UniversalRenderPipelineAsset forwardPipeline;
    public UniversalRenderPipelineAsset forwardPlusPipeline;
    public UniversalRenderPipelineAsset deferredPipeline;

    [Header("Renderer data")]
    public UniversalRendererData forwardRendererData;
    public UniversalRendererData forwardPlusRendererData;
    public UniversalRendererData deferredRendererData;

    [Header("Scene assets")]
    public VolumeProfile volumeProfile;
    public ComputeShader bindingProbeCompute;

    [Header("Rig materials")]
    public Material floorMaterial;
    public Material metal95Material;
    public Material metal80Material;
    public Material metal65Material;
    public Material metal40Material;
    public Material emissiveMaterial;
    public Material transparentSsrMaterial;
    public Material gradientMaterial;
    public Material glassMaterial;

    readonly SslValidationReport m_Report = new SslValidationReport();
    readonly StringBuilder m_Log = new StringBuilder();

    Camera m_Camera;
    UniversalAdditionalCameraData m_CameraData;
    Volume m_Volume;
    VolumeProfile m_ProfileInstance;
    ScreenSpaceGlobalIllumination m_Ssgi;
    ScreenSpaceReflection m_Ssr;
    ScreenSpaceRefraction m_Refraction;
    RenderDocApi m_RenderDoc;
    string m_OutputDirectory;
    string m_ManifestPath;
    GameObject m_Rig;
    bool m_OutputsWritten;
    float m_ProgressDeadline;

    void Awake()
    {
        m_OutputDirectory = GetArgument("-ssl-validation-out") ?? Path.Combine(Application.persistentDataPath, "SSLValidation");
        m_ManifestPath = GetArgument("-ssl-validation-manifest");
        Directory.CreateDirectory(m_OutputDirectory);
        Screen.SetResolution(RenderWidth, RenderHeight, FullScreenMode.Windowed);

        m_Camera = Camera.main != null ? Camera.main : FindObjectOfType<Camera>();
        if (m_Camera == null)
            throw new InvalidOperationException("The validation player scene has no camera.");
        m_CameraData = m_Camera.GetComponent<UniversalAdditionalCameraData>();
        if (m_CameraData == null)
            m_CameraData = m_Camera.gameObject.AddComponent<UniversalAdditionalCameraData>();

        m_Volume = FindObjectOfType<Volume>();
        if (m_Volume == null)
            throw new InvalidOperationException("The validation player scene has no volume.");
        if (volumeProfile == null)
            throw new InvalidOperationException("The validation player scene has no volume profile.");

        m_ProfileInstance = Instantiate(volumeProfile);
        m_ProfileInstance.name = volumeProfile.name + " (Validation Player)";
        foreach (var component in m_ProfileInstance.components)
            component.active = false;
        m_Ssgi = m_ProfileInstance.components.OfType<ScreenSpaceGlobalIllumination>().FirstOrDefault();
        m_Ssr = m_ProfileInstance.components.OfType<ScreenSpaceReflection>().FirstOrDefault();
        m_Refraction = m_ProfileInstance.components.OfType<ScreenSpaceRefraction>().FirstOrDefault();
        m_Volume.isGlobal = true;
        m_Volume.sharedProfile = m_ProfileInstance;

        DynamicGI.UpdateEnvironment();
    }

    IEnumerator Start()
    {
        m_Report.utcTimestamp = DateTime.UtcNow.ToString("O");
        m_Report.unityVersion = Application.unityVersion;
        m_Report.graphicsDeviceName = SystemInfo.graphicsDeviceName;
        m_Report.graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString();
        m_Report.outputDirectory = m_OutputDirectory;
        m_Report.manifestPath = m_ManifestPath;
        m_Report.renderWidth = Screen.width;
        m_Report.renderHeight = Screen.height;
        m_Report.cases = new List<SslValidationCaseResult>();
        m_Report.passed = false;

        try
        {
            yield return Run();
        }
        finally
        {
            if (!m_Report.completed && string.IsNullOrEmpty(m_Report.error))
                m_Report.error = "The validation run stopped before completing.";
            WriteOutputs();
            Application.Quit(m_Report.completed && m_Report.passed ? 0 : 1);
        }
    }

    // Unity stops a coroutine chain when a nested step throws; without a watchdog the player would
    // hang instead of writing a failure report. Every case refreshes the deadline.
    void Update()
    {
        if (m_OutputsWritten)
            return;
        if (m_ProgressDeadline <= 0f)
            return;
        if (Time.realtimeSinceStartup <= m_ProgressDeadline)
            return;

        m_Report.error = "The validation run timed out inside a case.";
        WriteOutputs();
        Application.Quit(1);
    }

    IEnumerator Run()
    {
        if (string.IsNullOrEmpty(m_ManifestPath) || !File.Exists(m_ManifestPath))
            throw new InvalidOperationException("Missing -ssl-validation-manifest <path>.");

        var manifest = JsonUtility.FromJson<SslValidationManifest>(File.ReadAllText(m_ManifestPath));
        if (manifest == null || manifest.cases == null || manifest.cases.Count == 0)
            throw new InvalidOperationException("The manifest has no cases.");

        m_Report.renderer = manifest.renderer;
        BuildRig();
        m_RenderDoc = RenderDocApi.Load(m_OutputDirectory, m_Log);
        m_Report.renderDocAvailable = m_RenderDoc != null;
        m_Report.renderDocVersion = m_RenderDoc != null ? m_RenderDoc.Version : "unavailable";
        m_Log.AppendLine("renderDoc=" + m_Report.renderDocVersion);

        // The reflection probes need one finished frame with all materials present.
        yield return WaitFrames(4, 0f);

        var passed = true;
        foreach (var testCase in manifest.cases)
        {
            var result = new SslValidationCaseResult
            {
                id = testCase.id,
                effect = testCase.effect,
                issues = new List<string>(),
                bindingProbeFailures = new List<string>(),
                passed = true,
            };
            yield return RunCase(manifest, testCase, result);
            m_Report.cases.Add(result);
            passed &= result.passed;
            m_Log.AppendLine($"case {result.id}: passed={result.passed} issues={result.issues.Count}");
        }

        m_Report.passed = passed;
        m_Report.completed = true;
    }

    IEnumerator RunCase(SslValidationManifest manifest, SslValidationCase testCase, SslValidationCaseResult result)
    {
        m_ProgressDeadline = Time.realtimeSinceStartup + 120f;
        var rendererName = string.IsNullOrEmpty(testCase.renderer) ? manifest.renderer : testCase.renderer;
        LogStep(testCase.id + ": start renderer=" + rendererName);
        var pipeline = ResolvePipeline(rendererName);
        var rendererData = ResolveRendererData(rendererName);
        if (pipeline == null || rendererData == null)
        {
            result.passed = false;
            result.issues.Add("Unknown renderer '" + rendererName + "'.");
            yield break;
        }

        result.renderer = rendererName;
        result.pipeline = pipeline.name;
        if (QualitySettings.renderPipeline != pipeline || GraphicsSettings.defaultRenderPipeline != pipeline)
        {
            QualitySettings.renderPipeline = pipeline;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            m_Log.AppendLine("switching to " + pipeline.name);
            yield return WaitFrames(3, 0f);
        }

        ApplyVolume(testCase.volume);
        ApplyFeatureSettings(rendererData, testCase.feature);
        ApplyGtaoSettings(rendererData, testCase.gtao);
        var probeFeature = FindFeature<ScreenSpaceLightingBindingProbeFeature>(rendererData);
        if (probeFeature != null)
            probeFeature.probeEnabled = testCase.bindingProbe;
        else if (testCase.bindingProbe)
        {
            result.issues.Add("The renderer has no binding probe feature.");
        }

        var camera = testCase.camera ?? new SslCameraCase();
        ApplyPose(camera);
        var warmup = Mathf.Max(1, manifest.warmupFrames);
        m_CameraData.resetHistory = true;
        yield return WaitFrames(2, 0f);
        m_CameraData.resetHistory = true;
        yield return WaitFrames(warmup, camera.panPixelsPerFrame);

        for (var i = 0; i < testCase.captureAfterFrames; i++)
        {
            ApplyPanStep(camera.panPixelsPerFrame);
            yield return new WaitForEndOfFrame();
        }

        result.frameIndex = Time.frameCount;
        result.cameraPosition = m_Camera.transform.position;
        result.cameraRotation = m_Camera.transform.rotation.eulerAngles;
        ReadEffectState(rendererData, result);
        ReadSsgiDispatchStats(testCase, rendererName, result);
        ReadSsrDenoiserStats(testCase, result);
        ReadSsrDebugViewStats(testCase, result);
        ReadTransparentSsrStats(testCase, result);
        LogStep(result.id + ": warmup done, effect state G=" + result.gtaoIntensity +
            " S=" + result.ssgiActive + " R=" + result.ssrActive + " T=" + result.refractionActive);

        if (testCase.bindingProbe && probeFeature != null)
            yield return ReadBindingProbe(probeFeature, testCase.id, result);

        if (testCase.capture)
            yield return CaptureWithRenderDoc(result, testCase.id);

        yield return WaitFrames(1, camera.panPixelsPerFrame);

        if (testCase.screenshot)
            CaptureScreenshot(result, testCase.id);
        LogStep(result.id + ": screenshot done");

        // A failed capture or a broken probe fail the case; the numerical acceptance lives in the
        // per-effect tasks, which consume the same report.
        if (testCase.capture && !result.captureSucceeded)
            result.issues.Add("The RenderDoc capture did not complete.");
        if (result.nonFinitePixels > 0)
            result.issues.Add("The frame contains non-finite pixels.");
        if (testCase.bindingProbe && probeFeature != null && !result.bindingProbeWritten)
            result.issues.Add("The binding probe produced no data: " + result.bindingProbeError);
        if (result.bindingProbeMismatches > 0)
            result.issues.Add("The binding probe found " + result.bindingProbeMismatches + " mismatching fields.");

        result.passed = result.issues.Count == 0;
    }

    UniversalRenderPipelineAsset ResolvePipeline(string rendererName)
    {
        if (string.Equals(rendererName, "Forward", StringComparison.OrdinalIgnoreCase))
            return forwardPipeline;
        if (string.Equals(rendererName, "ForwardPlus", StringComparison.OrdinalIgnoreCase))
            return forwardPlusPipeline;
        if (string.Equals(rendererName, "Deferred", StringComparison.OrdinalIgnoreCase))
            return deferredPipeline;
        return null;
    }

    UniversalRendererData ResolveRendererData(string rendererName)
    {
        if (string.Equals(rendererName, "Forward", StringComparison.OrdinalIgnoreCase))
            return forwardRendererData;
        if (string.Equals(rendererName, "ForwardPlus", StringComparison.OrdinalIgnoreCase))
            return forwardPlusRendererData;
        if (string.Equals(rendererName, "Deferred", StringComparison.OrdinalIgnoreCase))
            return deferredRendererData;
        return null;
    }

    static T FindFeature<T>(ScriptableRendererData data) where T : ScriptableRendererFeature
    {
        return data.rendererFeatures.OfType<T>().FirstOrDefault();
    }

    void ApplyVolume(SslVolumeCase volume)
    {
        volume = volume ?? new SslVolumeCase();

        if (m_Ssgi != null)
        {
            var ssgi = volume.ssgi;
            bool active = ssgi != null && ssgi.enabled;
            m_Ssgi.active = active;
            if (active)
            {
                m_Ssgi.enabled.Override(true);
                m_Ssgi.rayMiss.Override((RayMarchingFallbackHierarchy)ssgi.rayMiss);
                m_Ssgi.fullResolution.Override(ssgi.fullResolution);
                m_Ssgi.maxRaySteps.Override(ssgi.maxRaySteps);
                m_Ssgi.depthBufferThickness.Override(ssgi.depthBufferThickness);
                m_Ssgi.intensity.Override(ssgi.intensity);
                m_Ssgi.temporalAccumulation.Override(ssgi.temporalAccumulation);
                m_Ssgi.denoise.Override(ssgi.denoise);
                m_Ssgi.denoiserRadius.Override(ssgi.denoiserRadius);
                m_Ssgi.secondDenoiserPass.Override(ssgi.secondDenoiserPass);
                m_Ssgi.halfResolutionDenoiser.Override(ssgi.halfResolutionDenoiser);
                m_Ssgi.bilateralUpsample.Override(ssgi.bilateralUpsample);
            }
        }

        if (m_Ssr != null)
        {
            var ssr = volume.ssr;
            bool active = ssr != null && ssr.enabled;
            m_Ssr.active = active;
            if (active)
            {
                m_Ssr.enabled.Override(true);
                m_Ssr.enabledTransparent.Override(ssr.enabledTransparent);
                m_Ssr.usedAlgorithm.Override((ScreenSpaceReflectionAlgorithm)ssr.usedAlgorithm);
                m_Ssr.fullResolution.Override(ssr.fullResolution);
                m_Ssr.minSmoothness.Override(ssr.minSmoothness);
                m_Ssr.smoothnessFadeStart.Override(ssr.smoothnessFadeStart);
                m_Ssr.depthBufferThickness.Override(ssr.depthBufferThickness);
                m_Ssr.screenFadeDistance.Override(ssr.screenFadeDistance);
                m_Ssr.rayMaxIterations.Override(ssr.rayMaxIterations);
                m_Ssr.reflectSky.Override(ssr.reflectSky);
                m_Ssr.intensity.Override(ssr.intensity);
                m_Ssr.biasFactor.Override(ssr.biasFactor);
                m_Ssr.denoiserRadius.Override(ssr.denoiserRadius);
                m_Ssr.affectSmoothSurfaces.Override(ssr.affectSmoothSurfaces);
                m_Ssr.singleReflectionBounce.Override(ssr.singleReflectionBounce);
            }
        }

        if (m_Refraction != null)
        {
            var refraction = volume.refraction;
            bool active = refraction != null && refraction.enabled;
            m_Refraction.active = active;
            if (active)
            {
                m_Refraction.enabled.Override(true);
                m_Refraction.screenFadeDistance.Override(refraction.screenFadeDistance);
                m_Refraction.intensity.Override(refraction.intensity);
            }
        }
    }

    void ApplyFeatureSettings(UniversalRendererData rendererData, SslFeatureCase feature)
    {
        var ssl = FindFeature<ScreenSpaceLightingRendererFeature>(rendererData);
        if (ssl == null || feature == null)
            return;

        var settings = ssl.GetType().GetProperty("settings", InstanceFlags)?.GetValue(ssl);
        SetField(settings, "publishProbeAtlas", feature.publishProbeAtlas);
        SetEnumField(settings, "debugMode", feature.debugMode);
    }

    void ApplyGtaoSettings(UniversalRendererData rendererData, SslGtaoCase gtao)
    {
        var feature = FindFeature<GroundTruthAmbientOcclusionRendererFeature>(rendererData);
        if (feature == null || gtao == null)
            return;

        var settings = feature.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
        SetField(settings, "fullResolution", gtao.fullResolution);
        SetField(settings, "stepCount", gtao.stepCount);
        SetField(settings, "directionCount", gtao.directionCount);
        SetField(settings, "maximumRadiusInPixels", gtao.maximumRadiusInPixels);
        SetField(settings, "intensity", gtao.intensity);
        SetField(settings, "radius", gtao.radius);
        SetField(settings, "directLightingStrength", gtao.directLightingStrength);
        SetField(settings, "temporalAccumulation", gtao.temporalAccumulation);
        SetField(settings, "ghostingReduction", gtao.ghostingReduction);
        SetField(settings, "blurSharpness", gtao.blurSharpness);
        SetField(settings, "spatialBilateralAggressiveness", gtao.spatialBilateralAggressiveness);
        SetField(settings, "bilateralUpsample", gtao.bilateralUpsample);
    }

    static void SetField(object target, string name, object value)
    {
        var field = target?.GetType().GetField(name, InstanceFlags);
        field?.SetValue(target, value);
    }

    static void SetEnumField(object target, string name, int value)
    {
        var field = target?.GetType().GetField(name, InstanceFlags);
        if (field != null)
            field.SetValue(target, Enum.ToObject(field.FieldType, value));
    }

    void ApplyPose(SslCameraCase camera)
    {
        camera = camera ?? new SslCameraCase();
        m_Camera.transform.position = ToVector3(camera.position, new Vector3(0f, 2f, -7f));
        m_Camera.transform.rotation = Quaternion.Euler(ToVector3(camera.rotation, Vector3.zero));
        m_Camera.fieldOfView = 60f;
        m_Camera.nearClipPlane = 0.1f;
        m_Camera.farClipPlane = 100f;
    }

    void ApplyPanStep(float panPixelsPerFrame)
    {
        if (Mathf.Approximately(panPixelsPerFrame, 0f))
            return;
        var radiansPerPixel = 2f * Mathf.Tan(m_Camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.width);
        m_Camera.transform.Rotate(0f, panPixelsPerFrame * radiansPerPixel * Mathf.Rad2Deg, 0f, Space.World);
    }

    static Vector3 ToVector3(float[] values, Vector3 fallback)
    {
        return values != null && values.Length >= 3 ? new Vector3(values[0], values[1], values[2]) : fallback;
    }

    // Command-buffer keywords are not visible through Shader.IsKeywordEnabled on the CPU, so the
    // report records the Volume / renderer-feature state instead; the RDC capture shows the actual
    // keyword and dispatch state for the same frame.
    void ReadEffectState(UniversalRendererData rendererData, SslValidationCaseResult result)
    {
        var stack = VolumeManager.instance.stack;
        var ssgi = stack != null ? stack.GetComponent<ScreenSpaceGlobalIllumination>() : null;
        var ssr = stack != null ? stack.GetComponent<ScreenSpaceReflection>() : null;
        var refraction = stack != null ? stack.GetComponent<ScreenSpaceRefraction>() : null;
        result.ssgiActive = ssgi != null && ssgi.enabled.value;
        result.ssrActive = ssr != null && ssr.enabled.value;
        result.refractionActive = refraction != null && refraction.enabled.value && refraction.intensity.value > 0f;

        var gtaoFeature = FindFeature<GroundTruthAmbientOcclusionRendererFeature>(rendererData);
        var settings = gtaoFeature?.GetType().GetProperty("settings", InstanceFlags)?.GetValue(gtaoFeature);
        var intensity = settings?.GetType().GetField("intensity", InstanceFlags)?.GetValue(settings);
        result.gtaoIntensity = intensity != null ? Convert.ToSingle(intensity) : 0f;
    }

    // P2-1: compare the SSGI dispatch chain the pass actually submitted last frame against the
    // manifest configuration. Group sizes and keywords are only visible on the CPU; the RenderDoc
    // census covers the GPU side (dispatch order, bound history/LUT/atlas textures).
    void ReadSsgiDispatchStats(SslValidationCase testCase, string rendererName, SslValidationCaseResult result)
    {
        var ssgi = testCase.volume?.ssgi;
        if (ssgi == null || !ssgi.enabled)
            return;

        var passType = typeof(UniversalRendererData).Assembly.GetType(
            "UnityEngine.Rendering.Universal.ScreenSpaceLightingSsgiPass");
        var statsField = passType?.GetField("s_LastDispatchStats", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var stats = statsField?.GetValue(null);
        if (stats == null)
        {
            result.issues.Add("SSGI dispatch stats are unavailable; build the player with development mode.");
            return;
        }

        var report = new SslSsgiDispatchStats
        {
            frameIndex = ReadStatInt(stats, "frameIndex"),
            screenWidth = ReadStatInt(stats, "screenWidth"),
            screenHeight = ReadStatInt(stats, "screenHeight"),
            traceWidth = ReadStatInt(stats, "traceWidth"),
            traceHeight = ReadStatInt(stats, "traceHeight"),
            temporal = ReadStatBool(stats, "temporal"),
            denoise = ReadStatBool(stats, "denoise"),
            secondDenoiserPass = ReadStatBool(stats, "secondDenoiserPass"),
            halfResolutionDenoiser = ReadStatBool(stats, "halfResolutionDenoiser"),
            historyReadSlot = ReadStatInt(stats, "historyReadSlot"),
            historyWriteSlot = ReadStatInt(stats, "historyWriteSlot"),
            historyValid = ReadStatBool(stats, "historyValid"),
            traceDispatches = ReadStatInt(stats, "traceDispatches"),
            reprojectDispatches = ReadStatInt(stats, "reprojectDispatches"),
            validateDispatches = ReadStatInt(stats, "validateDispatches"),
            accumulationDispatches = ReadStatInt(stats, "accumulationDispatches"),
            copyHistoryDispatches = ReadStatInt(stats, "copyHistoryDispatches"),
            diffuseDenoiseDispatches = ReadStatInt(stats, "diffuseDenoiseDispatches"),
            gatherDispatches = ReadStatInt(stats, "gatherDispatches"),
            upsampleDispatches = ReadStatInt(stats, "upsampleDispatches"),
            pointDistributionDispatches = ReadStatInt(stats, "pointDistributionDispatches"),
            traceGroupsX = ReadStatInt(stats, "traceGroupsX"),
            traceGroupsY = ReadStatInt(stats, "traceGroupsY"),
            validateGroupsX = ReadStatInt(stats, "validateGroupsX"),
            validateGroupsY = ReadStatInt(stats, "validateGroupsY"),
            accumulationGroupsX = ReadStatInt(stats, "accumulationGroupsX"),
            accumulationGroupsY = ReadStatInt(stats, "accumulationGroupsY"),
            copyHistoryGroupsX = ReadStatInt(stats, "copyHistoryGroupsX"),
            copyHistoryGroupsY = ReadStatInt(stats, "copyHistoryGroupsY"),
            diffuseDenoiseGroupsX = ReadStatInt(stats, "diffuseDenoiseGroupsX"),
            diffuseDenoiseGroupsY = ReadStatInt(stats, "diffuseDenoiseGroupsY"),
            gatherGroupsX = ReadStatInt(stats, "gatherGroupsX"),
            gatherGroupsY = ReadStatInt(stats, "gatherGroupsY"),
            upsampleGroupsX = ReadStatInt(stats, "upsampleGroupsX"),
            upsampleGroupsY = ReadStatInt(stats, "upsampleGroupsY"),
            pointDistributionGroupsX = ReadStatInt(stats, "pointDistributionGroupsX"),
            pointDistributionGroupsY = ReadStatInt(stats, "pointDistributionGroupsY"),
            groupsZ = ReadStatInt(stats, "groupsZ"),
            halfResKeyword = ReadStatBool(stats, "halfResKeyword"),
            blueNoiseKeyword = ReadStatBool(stats, "blueNoiseKeyword"),
            probeAtlasKeyword = ReadStatBool(stats, "probeAtlasKeyword"),
            probeListKeyword = ReadStatBool(stats, "probeListKeyword"),
            normalsOctKeyword = ReadStatBool(stats, "normalsOctKeyword"),
            xrArrayKeyword = ReadStatBool(stats, "xrArrayKeyword"),
            historyReadSlots = ReadStatIntArray(stats, "historyReadSlots"),
            historyWriteSlots = ReadStatIntArray(stats, "historyWriteSlots"),
        };

        if (report.frameIndex < 0 || Time.frameCount - report.frameIndex > 3)
            result.issues.Add($"SSGI dispatch stats are stale: frame {report.frameIndex}, current {Time.frameCount}.");

        int screenWidth = Mathf.Max(1, report.screenWidth);
        int screenHeight = Mathf.Max(1, report.screenHeight);
        bool fullResolution = ssgi.fullResolution;
        int traceWidth = fullResolution ? screenWidth : Mathf.Max(1, screenWidth >> 1);
        int traceHeight = fullResolution ? screenHeight : Mathf.Max(1, screenHeight >> 1);
        bool temporal = ssgi.temporalAccumulation;
        bool denoise = ssgi.denoise;
        bool secondPass = denoise && ssgi.secondDenoiserPass && temporal;
        bool halfResolutionDenoise = ssgi.halfResolutionDenoiser;
        int traceGroupsX = (traceWidth + 7) / 8;
        int traceGroupsY = (traceHeight + 7) / 8;
        int screenGroupsX = (screenWidth + 7) / 8;
        int screenGroupsY = (screenHeight + 7) / 8;

        report.expectedTraceWidth = traceWidth;
        report.expectedTraceHeight = traceHeight;
        report.expectedTraceDispatches = 1;
        report.expectedReprojectDispatches = 1;
        report.expectedValidateDispatches = temporal ? 1 : 0;
        report.expectedAccumulationDispatches = temporal ? (secondPass ? 2 : 1) : 0;
        report.expectedCopyHistoryDispatches = report.expectedAccumulationDispatches;
        report.expectedDiffuseDenoiseDispatches = denoise ? (secondPass ? 2 : 1) : 0;
        report.expectedGatherDispatches = denoise && halfResolutionDenoise ? (secondPass ? 2 : 1) : 0;
        report.expectedUpsampleDispatches = fullResolution ? 0 : 1;
        report.expectedTraceGroupsX = traceGroupsX;
        report.expectedTraceGroupsY = traceGroupsY;
        report.expectedScreenGroupsX = screenGroupsX;
        report.expectedScreenGroupsY = screenGroupsY;

        CheckCount(result, "trace", report.traceDispatches, report.expectedTraceDispatches);
        CheckCount(result, "reproject", report.reprojectDispatches, report.expectedReprojectDispatches);
        CheckCount(result, "validate", report.validateDispatches, report.expectedValidateDispatches);
        CheckCount(result, "accumulation", report.accumulationDispatches, report.expectedAccumulationDispatches);
        CheckCount(result, "copyHistory", report.copyHistoryDispatches, report.expectedCopyHistoryDispatches);
        CheckCount(result, "diffuseDenoise", report.diffuseDenoiseDispatches, report.expectedDiffuseDenoiseDispatches);
        CheckCount(result, "gather", report.gatherDispatches, report.expectedGatherDispatches);
        CheckCount(result, "upsample", report.upsampleDispatches, report.expectedUpsampleDispatches);

        CheckGroups(result, "trace", report.traceGroupsX, report.traceGroupsY, traceGroupsX, traceGroupsY);
        if (report.accumulationDispatches > 0)
            CheckGroups(result, "accumulation", report.accumulationGroupsX, report.accumulationGroupsY, traceGroupsX, traceGroupsY);
        if (report.copyHistoryDispatches > 0)
            CheckGroups(result, "copyHistory", report.copyHistoryGroupsX, report.copyHistoryGroupsY, traceGroupsX, traceGroupsY);
        if (report.diffuseDenoiseDispatches > 0)
            CheckGroups(result, "diffuseDenoise", report.diffuseDenoiseGroupsX, report.diffuseDenoiseGroupsY, traceGroupsX, traceGroupsY);
        if (report.validateDispatches > 0)
            CheckGroups(result, "validate", report.validateGroupsX, report.validateGroupsY, screenGroupsX, screenGroupsY);
        if (report.upsampleDispatches > 0)
            CheckGroups(result, "upsample", report.upsampleGroupsX, report.upsampleGroupsY, screenGroupsX, screenGroupsY);
        if (report.gatherDispatches > 0)
            CheckGroups(result, "gather", report.gatherGroupsX, report.gatherGroupsY, traceGroupsX, traceGroupsY);

        if (report.traceWidth != traceWidth || report.traceHeight != traceHeight)
            result.issues.Add($"SSGI trace size {report.traceWidth}x{report.traceHeight} != expected {traceWidth}x{traceHeight}.");
        if (report.halfResKeyword == fullResolution)
            result.issues.Add("SSGI_HALF_RES keyword does not match the configured resolution.");
        if (!report.blueNoiseKeyword)
            result.issues.Add("SSGI_BLUE_NOISE is off; the blue-noise LUTs are not used.");

        bool forwardPlus = string.Equals(rendererName, "ForwardPlus", StringComparison.OrdinalIgnoreCase);
        bool probeList = !forwardPlus && testCase.feature != null && testCase.feature.publishProbeAtlas;
        if (report.probeAtlasKeyword != forwardPlus)
            result.issues.Add("SSL_PROBE_ATLAS does not match the Forward+ renderer.");
        if (report.probeListKeyword != probeList)
            result.issues.Add("SSL_PROBE_LIST does not match the probe atlas setting.");

        if (temporal && report.historyValid)
        {
            var last = LastHistorySlots(report.historyReadSlots, report.historyWriteSlots, 4);
            bool alternates = last.Count > 0 && last[last.Count - 1].x == report.historyReadSlot;
            for (int i = 0; i < last.Count; i++)
            {
                var slot = last[i];
                if (slot.x < 0 || (slot.x ^ slot.y) != 1)
                {
                    result.issues.Add($"SSGI history slots are not a ping-pong pair: read={slot.x} write={slot.y}.");
                    alternates = false;
                    break;
                }

                if (i > 0 && slot.x != last[i - 1].y)
                {
                    alternates = false;
                    break;
                }
            }

            if (!alternates && result.issues.Count == 0)
                result.issues.Add("SSGI history slots do not alternate between frames.");
        }

        report.totalDispatches =
            report.traceDispatches + report.reprojectDispatches + report.validateDispatches +
            report.accumulationDispatches + report.copyHistoryDispatches + report.diffuseDenoiseDispatches +
            report.gatherDispatches + report.upsampleDispatches + report.pointDistributionDispatches;
        result.ssgiDispatch = report;
    }

    // P3-1: ReflectionDenoiser.compute maps _PixelSpreadAngleTangent to _SsgiDenoiserParams.x, so the
    // SSR pass must upload that vector itself. Verify the value it computed for its own trace size and
    // fail if SSGI is enabled, which would make the proof ambiguous.
    void ReadSsrDenoiserStats(SslValidationCase testCase, SslValidationCaseResult result)
    {
        var ssr = testCase.volume?.ssr;
        if (ssr == null || !ssr.enabled || ssr.usedAlgorithm != 1)
            return;

        var passType = typeof(UniversalRendererData).Assembly.GetType(
            "UnityEngine.Rendering.Universal.ScreenSpaceLightingSsrPass");
        var tangentField = passType?.GetField(
            "s_LastPixelSpreadAngleTangent",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (tangentField == null)
        {
            result.issues.Add("The SSR denoiser spread angle stat is unavailable; build with development mode.");
            return;
        }

        var stats = new SslSsrDenoiserStats
        {
            pixelSpreadAngleTangent = Convert.ToSingle(tangentField.GetValue(null)),
            frameIndex = ReadStaticInt(passType, "s_LastPixelSpreadFrame"),
            traceWidth = ReadStaticInt(passType, "s_LastPixelSpreadTraceWidth"),
            traceHeight = ReadStaticInt(passType, "s_LastPixelSpreadTraceHeight"),
            expectedTraceWidth = ssr.fullResolution ? Screen.width : Mathf.Max(1, Screen.width >> 1),
            expectedTraceHeight = ssr.fullResolution ? Screen.height : Mathf.Max(1, Screen.height >> 1),
            ssgiActive = result.ssgiActive,
        };
        float minTrace = Mathf.Max(1, Mathf.Min(stats.traceWidth, stats.traceHeight));
        stats.expectedPixelSpreadAngleTangent =
            2f * Mathf.Tan(m_Camera.fieldOfView * Mathf.Deg2Rad * 0.5f) / minTrace;
        stats.passed = Mathf.Abs(stats.pixelSpreadAngleTangent - stats.expectedPixelSpreadAngleTangent) <= 1e-6f;

        if (!stats.passed)
            result.issues.Add(
                $"SSR pixel spread tangent {stats.pixelSpreadAngleTangent:R} != expected {stats.expectedPixelSpreadAngleTangent:R}.");
        if (stats.pixelSpreadAngleTangent <= 0f)
            result.issues.Add("SSR pixel spread tangent is zero.");
        if (stats.traceWidth != stats.expectedTraceWidth || stats.traceHeight != stats.expectedTraceHeight)
            result.issues.Add(
                $"SSR denoiser trace size {stats.traceWidth}x{stats.traceHeight} != expected {stats.expectedTraceWidth}x{stats.expectedTraceHeight}.");
        if (stats.ssgiActive)
            result.issues.Add("The SSR spread angle case ran with SSGI enabled; the upload cannot be attributed to SSR.");
        if (stats.frameIndex < 0 || Time.frameCount - stats.frameIndex > 3)
            result.issues.Add($"SSR spread angle stat is stale: frame {stats.frameIndex}, current {Time.frameCount}.");

        result.ssrDenoiser = stats;
    }

    static int ReadStaticInt(Type type, string fieldName)
    {
        var field = type?.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return field != null ? Convert.ToInt32(field.GetValue(null)) : -1;
    }

    static bool ReadStaticBool(Type type, string fieldName)
    {
        var field = type?.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return field != null && Convert.ToBoolean(field.GetValue(null));
    }

    static string ReadStaticString(Type type, string fieldName)
    {
        var field = type?.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return field != null ? field.GetValue(null) as string ?? string.Empty : string.Empty;
    }

    // F-04: the SsrPbrParams debug kernel must write its private texture, and the SSR pass must still
    // run the PBR chain so the material-facing texture keeps real radiance while the view is selected.
    void ReadSsrDebugViewStats(SslValidationCase testCase, SslValidationCaseResult result)
    {
        int debugMode = testCase.feature != null ? testCase.feature.debugMode : 0;
        if (debugMode != (int)ScreenSpaceLightingDebugMode.SsrPbrParams)
            return;

        var passType = typeof(UniversalRendererData).Assembly.GetType(
            "UnityEngine.Rendering.Universal.ScreenSpaceLightingSsrPass");
        if (passType == null)
        {
            result.issues.Add("The SSR pass type is unavailable; the debug view cannot be verified.");
            return;
        }

        var stats = new SslSsrDebugViewStats
        {
            frameIndex = ReadStaticInt(passType, "s_LastDebugParamsFrame"),
            debugTarget = ReadStaticString(passType, "s_LastDebugParamsTarget"),
            materialOutput = ReadStaticString(passType, "s_LastMaterialOutputTarget"),
        };
        stats.debugTargetIsPrivate = string.Equals(stats.debugTarget, "SslSsrDebugParams", StringComparison.Ordinal);
        stats.materialOutputIsReal =
            string.Equals(stats.materialOutput, "SslSsrPbrOriginal", StringComparison.Ordinal) ||
            string.Equals(stats.materialOutput, "SslSsrOutput", StringComparison.Ordinal);
        stats.materialOutputIsDebugTarget =
            !string.IsNullOrEmpty(stats.debugTarget) &&
            string.Equals(stats.materialOutput, stats.debugTarget, StringComparison.Ordinal);
        stats.denoiseChainRan = result.ssrDenoiser != null &&
                                result.ssrDenoiser.frameIndex >= 0 &&
                                Time.frameCount - result.ssrDenoiser.frameIndex <= 3;
        stats.passed = stats.debugTargetIsPrivate && stats.materialOutputIsReal &&
                       !stats.materialOutputIsDebugTarget && stats.denoiseChainRan;

        if (stats.frameIndex < 0 || Time.frameCount - stats.frameIndex > 3)
            result.issues.Add($"SSR debug params stat is stale: frame {stats.frameIndex}, current {Time.frameCount}.");
        if (!stats.debugTargetIsPrivate)
            result.issues.Add("The SSR debug kernel did not target its private SslSsrDebugParams texture.");
        if (!stats.materialOutputIsReal)
            result.issues.Add("The material-facing SSR output is not a real SSR texture: " + stats.materialOutput);
        if (stats.materialOutputIsDebugTarget)
            result.issues.Add("The material-facing SSR output is the debug texture.");
        if (!stats.denoiseChainRan)
            result.issues.Add("The PBR denoiser chain did not run while the debug view was selected.");

        result.ssrDebugView = stats;
    }

    // F-06/F-08: the depth seed follows the CopyDepthPass y-flip rule, and the transparent trace
    // explicitly owns every keyword it depends on instead of inheriting the opaque pass' state.
    void ReadTransparentSsrStats(SslValidationCase testCase, SslValidationCaseResult result)
    {
        var ssr = testCase.volume?.ssr;
        if (ssr == null || !ssr.enabled || !ssr.enabledTransparent)
            return;

        var assembly = typeof(UniversalRendererData).Assembly;
        var keywordType = assembly.GetType(
            "UnityEngine.Rendering.Universal.ScreenSpaceLightingTransparentSsrPass");
        var seedType = assembly.GetType(
            "UnityEngine.Rendering.Universal.ScreenSpaceLightingTransparentDepthNormalsPass");
        if (keywordType == null || seedType == null)
        {
            result.issues.Add("The transparent SSR pass types are unavailable.");
            return;
        }

        var stats = new SslTransparentSsrStats
        {
            keywordFrameIndex = ReadStaticInt(keywordType, "s_LastKeywordFrame"),
            depthSeedFrameIndex = ReadStaticInt(seedType, "s_LastDepthSeedFrame"),
            halfResKeyword = ReadStaticBool(keywordType, "s_LastHalfResKeyword"),
            blueNoiseKeyword = ReadStaticBool(keywordType, "s_LastBlueNoiseKeyword"),
            normalsOctKeyword = ReadStaticBool(keywordType, "s_LastNormalsOctKeyword"),
            approxKeyword = ReadStaticBool(keywordType, "s_LastApproxKeyword"),
            blueNoiseTexturesBound = ReadStaticBool(keywordType, "s_LastBlueNoiseTexturesBound"),
            isolatedCompute = ReadStaticBool(keywordType, "s_LastIsolatedCompute"),
            depthSeedYFlip = ReadStaticBool(seedType, "s_LastDepthSeedYFlip"),
            depthSeedSourceFlipped = ReadStaticBool(seedType, "s_LastDepthSeedSourceFlipped"),
            depthSeedDestinationFlipped = ReadStaticBool(seedType, "s_LastDepthSeedDestinationFlipped"),
        };
        stats.depthSeedYFlipExpected = stats.depthSeedSourceFlipped != stats.depthSeedDestinationFlipped;
        stats.passed = !stats.halfResKeyword && !stats.normalsOctKeyword && stats.approxKeyword &&
                       stats.isolatedCompute &&
                       stats.blueNoiseKeyword == stats.blueNoiseTexturesBound &&
                       stats.depthSeedYFlip == stats.depthSeedYFlipExpected;

        if (stats.keywordFrameIndex < 0 || Time.frameCount - stats.keywordFrameIndex > 3)
            result.issues.Add($"Transparent SSR keyword stat is stale: frame {stats.keywordFrameIndex}.");
        if (stats.halfResKeyword)
            result.issues.Add("SSR_HALF_RES is still enabled in the transparent pass.");
        if (stats.normalsOctKeyword)
            result.issues.Add("SSR_NORMALS_OCT is still enabled in the transparent pass.");
        if (!stats.approxKeyword)
            result.issues.Add("SSR_APPROX is not enabled in the transparent pass.");
        if (stats.blueNoiseKeyword != stats.blueNoiseTexturesBound)
            result.issues.Add("SSGI_BLUE_NOISE does not match the blue-noise LUT availability.");
        if (!stats.isolatedCompute)
            result.issues.Add("The transparent pass shares the opaque compute asset; its keywords can overwrite the opaque variant.");
        if (stats.depthSeedFrameIndex < 0 || Time.frameCount - stats.depthSeedFrameIndex > 3)
            result.issues.Add($"Transparent depth seed stat is stale: frame {stats.depthSeedFrameIndex}.");
        if (stats.depthSeedYFlip != stats.depthSeedYFlipExpected)
            result.issues.Add(
                $"Transparent depth seed yflip {stats.depthSeedYFlip} != expected {stats.depthSeedYFlipExpected} " +
                $"(source {stats.depthSeedSourceFlipped}, destination {stats.depthSeedDestinationFlipped}).");

        result.transparentSsr = stats;
    }

    static void CheckCount(SslValidationCaseResult result, string label, int actual, int expected)
    {        if (actual != expected)
            result.issues.Add($"SSGI {label} dispatches {actual} != expected {expected}.");
    }

    static void CheckGroups(SslValidationCaseResult result, string label, int x, int y, int expectedX, int expectedY)
    {
        if (x != expectedX || y != expectedY)
            result.issues.Add($"SSGI {label} groups {x}x{y} != expected {expectedX}x{expectedY}.");
    }

    static readonly List<Vector2Int> s_HistoryScratch = new List<Vector2Int>(8);

    static List<Vector2Int> LastHistorySlots(int[] readSlots, int[] writeSlots, int count)
    {
        s_HistoryScratch.Clear();
        if (readSlots == null || writeSlots == null)
            return s_HistoryScratch;

        int start = Mathf.Max(0, readSlots.Length - count);
        for (int i = start; i < readSlots.Length; i++)
            s_HistoryScratch.Add(new Vector2Int(readSlots[i], writeSlots[i]));
        return s_HistoryScratch;
    }

    static int ReadStatInt(object target, string name)
    {
        var field = target.GetType().GetField(name, InstanceFlags);
        return field != null ? Convert.ToInt32(field.GetValue(target)) : -1;
    }

    static bool ReadStatBool(object target, string name)
    {
        var field = target.GetType().GetField(name, InstanceFlags);
        return field != null && Convert.ToBoolean(field.GetValue(target));
    }

    static int[] ReadStatIntArray(object target, string name)
    {
        var field = target.GetType().GetField(name, InstanceFlags);
        return field?.GetValue(target) as int[];
    }

    IEnumerator ReadBindingProbe(ScreenSpaceLightingBindingProbeFeature feature, string caseId, SslValidationCaseResult result)
    {
        result.bindingProbeRequested = true;
        feature.RequestReadback();
        for (var i = 0; i < 10 && !feature.readbackDone; i++)
            yield return new WaitForEndOfFrame();

        result.bindingProbeWritten = feature.probeWritten;
        result.bindingProbeError = feature.probeError;
        if (!feature.readbackDone || !feature.TryTakeReadback(out var actual))
        {
            result.issues.Add("The binding probe readback did not complete: " + result.bindingProbeError);
            yield break;
        }

        var expected = feature.expectedValues;
        var applicable = feature.applicableFields;
        var entries = new List<SslBindingProbeEntry>(actual.Length);
        for (var i = 0; i < actual.Length; i++)
        {
            var entry = new SslBindingProbeEntry
            {
                field = ScreenSpaceLightingBindingProbeFeature.FieldNames[i],
                applicable = applicable[i],
                expected = expected[i],
                actual = actual[i],
                maxAbsoluteError = MaxError(expected[i], actual[i]),
                passed = true,
            };
            if (entry.applicable)
            {
                entry.passed = entry.maxAbsoluteError <= 1e-5f;
                result.bindingProbeComparisons++;
                if (!entry.passed)
                {
                    result.bindingProbeMismatches++;
                    result.bindingProbeFailures.Add(
                        $"{entry.field}: expected {Format(expected[i])} actual {Format(actual[i])}");
                }
            }

            entries.Add(entry);
        }

        File.WriteAllText(
            Path.Combine(m_OutputDirectory, "BindingProbe_" + SafeName(caseId) + ".json"),
            JsonUtility.ToJson(new SslBindingProbeReport
            {
                id = caseId,
                frameIndex = Time.frameCount,
                fields = entries,
            }, true));
    }

    static float MaxError(Vector4 expected, Vector4 actual)
    {
        var scale = Mathf.Max(1f, Mathf.Max(Mathf.Abs(expected.x), Mathf.Max(Mathf.Abs(expected.y),
            Mathf.Max(Mathf.Abs(expected.z), Mathf.Abs(expected.w)))));
        var error = Mathf.Max(
            Mathf.Abs(expected.x - actual.x),
            Mathf.Max(Mathf.Abs(expected.y - actual.y),
                Mathf.Max(Mathf.Abs(expected.z - actual.z), Mathf.Abs(expected.w - actual.w))));
        return error / scale;
    }

    static string Format(Vector4 value)
    {
        return $"({value.x:R}, {value.y:R}, {value.z:R}, {value.w:R})";
    }

    IEnumerator CaptureWithRenderDoc(SslValidationCaseResult result, string caseId)
    {
        if (m_RenderDoc == null)
        {
            result.captureTriggered = false;
            result.captureSucceeded = false;
            yield break;
        }

        m_RenderDoc.SetCaptureFilePathTemplate(Path.Combine(m_OutputDirectory, SafeName(caseId)));
        var before = m_RenderDoc.GetNumCaptures();
        LogStep(caseId + ": capture trigger");
        m_RenderDoc.TriggerCapture();
        result.captureTriggered = true;
        result.captureCountBefore = (int)before;

        for (var i = 0; i < 12 && m_RenderDoc.GetNumCaptures() <= before; i++)
            yield return new WaitForEndOfFrame();

        var after = m_RenderDoc.GetNumCaptures();
        result.captureCountAfter = (int)after;
        result.captureSucceeded = after > before;
        result.capture = result.captureSucceeded ? m_RenderDoc.GetCapturePath(after - 1) : string.Empty;
        m_Log.AppendLine($"capture {caseId}: succeeded={result.captureSucceeded} path={result.capture}");
        Debug.Log($"[SSL-VAL] capture {caseId}: succeeded={result.captureSucceeded} path={result.capture}");
    }

    void CaptureScreenshot(SslValidationCaseResult result, string caseId)
    {
        Debug.Log("[SSL-VAL] " + caseId + ": screenshot capture");
        var texture = ScreenCapture.CaptureScreenshotAsTexture();
        if (texture == null)
        {
            result.issues.Add("ScreenCapture returned no texture.");
            return;
        }

        try
        {
            var fileName = SafeName(caseId) + ".png";
            File.WriteAllBytes(Path.Combine(m_OutputDirectory, fileName), texture.EncodeToPNG());
            result.screenshot = fileName;

            var pixels = texture.GetPixels();
            result.nonFinitePixels = CountNonFinite(pixels);
            result.meanLuminance = MeanLuminance(pixels);
        }
        finally
        {
            Destroy(texture);
        }
    }

    static int CountNonFinite(Color[] pixels)
    {
        var count = 0;
        foreach (var color in pixels)
        {
            if (!IsFinite(color.r) || !IsFinite(color.g) || !IsFinite(color.b) || !IsFinite(color.a))
                count++;
        }

        return count;
    }

    static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    static float MeanLuminance(Color[] pixels)
    {
        if (pixels.Length == 0)
            return 0f;
        double sum = 0;
        foreach (var color in pixels)
            sum += color.r * 0.2126 + color.g * 0.7152 + color.b * 0.0722;
        return (float)(sum / pixels.Length);
    }

    static string SafeName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "case";
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character == '_' || character == '-' ? character : '_');
        return builder.ToString();
    }

    void LogStep(string message)
    {
        m_Log.AppendLine(message);
        Debug.Log("[SSL-VAL] " + message);
    }

    IEnumerator WaitFrames(int frames, float panPixelsPerFrame)
    {
        for (var i = 0; i < frames; i++)
        {
            ApplyPanStep(panPixelsPerFrame);
            yield return new WaitForEndOfFrame();
        }
    }

    void BuildRig()
    {
        m_Rig = new GameObject("SSL Validation Rig");

        AddBox(m_Rig.transform, floorMaterial, "Floor",
            new Vector3(0f, -0.05f, 4f), new Vector3(40f, 0.1f, 30f));

        AddSphere(m_Rig.transform, metal95Material, "Metal 0.95", new Vector3(-6f, 1f, 4f), 2f);
        AddSphere(m_Rig.transform, metal80Material, "Metal 0.80", new Vector3(-2f, 1f, 4f), 2f);
        AddSphere(m_Rig.transform, metal65Material, "Metal 0.65", new Vector3(2f, 1f, 4f), 2f);
        AddSphere(m_Rig.transform, metal40Material, "Metal 0.40", new Vector3(6f, 1f, 4f), 2f);

        AddBox(m_Rig.transform, emissiveMaterial, "Emissive Panel",
            new Vector3(0f, 2.4f, 11f), new Vector3(5f, 2.5f, 0.3f));

        var gradient = GameObject.CreatePrimitive(PrimitiveType.Quad);
        gradient.name = "Gradient Panel";
        gradient.transform.SetParent(m_Rig.transform, false);
        gradient.transform.position = new Vector3(0f, 2f, 13f);
        gradient.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        gradient.transform.localScale = new Vector3(14f, 8f, 1f);
        gradient.GetComponent<Renderer>().sharedMaterial = gradientMaterial;

        AddBox(m_Rig.transform, transparentSsrMaterial, "Transparent SSR Panel",
            new Vector3(0f, 1.5f, 1.2f), new Vector3(3.2f, 2.2f, 0.05f));

        AddSphere(m_Rig.transform, glassMaterial, "Glass Sphere", new Vector3(-5.5f, 1.4f, 2.8f), 2.8f);

        var probes = new[]
        {
            new Vector3(-6f, 3f, 8f),
            new Vector3(-2f, 3f, 8f),
            new Vector3(2f, 3f, 8f),
            new Vector3(6f, 3f, 8f),
        };
        for (var i = 0; i < probes.Length; i++)
        {
            var probeObject = new GameObject("Reflection Probe " + i);
            probeObject.transform.SetParent(m_Rig.transform, false);
            probeObject.transform.position = probes[i];
            var probe = probeObject.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
            probe.size = new Vector3(8f, 6f, 8f);
            probe.center = Vector3.zero;
            probe.boxProjection = true;
            probe.importance = i;
            probe.resolution = 128;
            probe.hdr = true;
            probe.clearFlags = ReflectionProbeClearFlags.Skybox;
            probe.RenderProbe();
        }
    }

    static GameObject AddBox(Transform parent, Material material, string name, Vector3 position, Vector3 scale)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent, false);
        box.transform.position = position;
        box.transform.localScale = scale;
        box.GetComponent<Renderer>().sharedMaterial = material;
        return box;
    }

    static GameObject AddSphere(Transform parent, Material material, string name, Vector3 position, float scale)
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = name;
        sphere.transform.SetParent(parent, false);
        sphere.transform.position = position;
        sphere.transform.localScale = Vector3.one * scale;
        sphere.GetComponent<Renderer>().sharedMaterial = material;
        return sphere;
    }

    void WriteOutputs()
    {
        if (m_OutputsWritten)
            return;
        m_OutputsWritten = true;

        File.WriteAllText(Path.Combine(m_OutputDirectory, "Report.json"), JsonUtility.ToJson(m_Report, true));
        m_Log.Insert(0, $"completed={m_Report.completed} passed={m_Report.passed}\n");
        File.WriteAllText(Path.Combine(m_OutputDirectory, "Player.log"), m_Log.ToString());
        if (!string.IsNullOrEmpty(m_Report.error))
            Debug.LogError(m_Report.error);
    }

    static string GetArgument(string name)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var i = 0; i < arguments.Length - 1; i++)
        {
            if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
                return arguments[i + 1];
        }

        return null;
    }

    sealed class RenderDocApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void SetCaptureFilePathTemplateDelegate(
            [MarshalAs(UnmanagedType.LPStr)] string pathTemplate);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint GetNumCapturesDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint GetCaptureDelegate(uint index, byte[] filename, ref uint pathLength, out ulong timestamp);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void TriggerCaptureDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GetApiDelegate(uint version, out IntPtr apiPointers);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [StructLayout(LayoutKind.Sequential)]
        struct ApiTable
        {
            public IntPtr GetAPIVersion;
            public IntPtr SetCaptureOptionU32;
            public IntPtr SetCaptureOptionF32;
            public IntPtr GetCaptureOptionU32;
            public IntPtr GetCaptureOptionF32;
            public IntPtr SetFocusToggleKeys;
            public IntPtr SetCaptureKeys;
            public IntPtr GetOverlayBits;
            public IntPtr MaskOverlayBits;
            public IntPtr Shutdown;
            public IntPtr UnloadCrashHandler;
            public IntPtr SetCaptureFilePathTemplate;
            public IntPtr GetCaptureFilePathTemplate;
            public IntPtr GetNumCaptures;
            public IntPtr GetCapture;
            public IntPtr TriggerCapture;
            public IntPtr IsTargetControlConnected;
            public IntPtr LaunchReplayUI;
            public IntPtr SetActiveWindow;
            public IntPtr StartFrameCapture;
            public IntPtr IsFrameCapturing;
            public IntPtr EndFrameCapture;
        }

        const uint ApiVersion = 10600;

        SetCaptureFilePathTemplateDelegate m_SetTemplate;
        GetNumCapturesDelegate m_GetNumCaptures;
        GetCaptureDelegate m_GetCapture;
        TriggerCaptureDelegate m_TriggerCapture;

        public string Version { get; private set; } = "unavailable";

        public static RenderDocApi Load(string outputDirectory, StringBuilder log)
        {
            var module = GetModuleHandle("renderdoc.dll");
            if (module == IntPtr.Zero)
            {
                log.AppendLine("renderdoc.dll not loaded; run the player through renderdoccmd capture.");
                return null;
            }

            var getApiAddress = GetProcAddress(module, "RENDERDOC_GetAPI");
            if (getApiAddress == IntPtr.Zero)
                throw new InvalidOperationException("renderdoc.dll has no RENDERDOC_GetAPI export.");
            var getApi = Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(getApiAddress);
            if (getApi(ApiVersion, out var apiPointers) == 0 || apiPointers == IntPtr.Zero)
                throw new InvalidOperationException("RENDERDOC_GetAPI rejected version 1.6.0.");

            var table = Marshal.PtrToStructure<ApiTable>(apiPointers);
            var api = new RenderDocApi
            {
                m_SetTemplate = Marshal.GetDelegateForFunctionPointer<SetCaptureFilePathTemplateDelegate>(
                    table.SetCaptureFilePathTemplate),
                m_GetNumCaptures = Marshal.GetDelegateForFunctionPointer<GetNumCapturesDelegate>(
                    table.GetNumCaptures),
                m_GetCapture = Marshal.GetDelegateForFunctionPointer<GetCaptureDelegate>(
                    table.GetCapture),
                m_TriggerCapture = Marshal.GetDelegateForFunctionPointer<TriggerCaptureDelegate>(
                    table.TriggerCapture),
            };
            var version = Marshal.GetDelegateForFunctionPointer<GetVersionDelegate>(table.GetAPIVersion);
            version(out var major, out var minor, out var patch);
            api.Version = $"{major}.{minor}.{patch}";
            log.AppendLine("renderdoc api " + api.Version + " output=" + outputDirectory);
            return api;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void GetVersionDelegate(out int major, out int minor, out int patch);

        public void SetCaptureFilePathTemplate(string pathTemplate)
        {
            m_SetTemplate(pathTemplate.Replace('\\', '/'));
        }

        public uint GetNumCaptures()
        {
            return m_GetNumCaptures();
        }

        public void TriggerCapture()
        {
            m_TriggerCapture();
        }

        public string GetCapturePath(uint index)
        {
            var buffer = new byte[1024];
            uint length = (uint)buffer.Length;
            if (m_GetCapture(index, buffer, ref length, out _) == 0)
                return string.Empty;
            var count = (int)Math.Min(length, (uint)buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, count).TrimEnd('\0');
        }
    }
}

[Serializable]
public sealed class SslValidationManifest
{
    public int schemaVersion = 1;
    public string renderer = "Forward";
    public int warmupFrames = 10;
    public List<SslValidationCase> cases = new List<SslValidationCase>();
}

[Serializable]
public sealed class SslValidationCase
{
    public string id;
    public string renderer;
    public string effect;
    public SslCameraCase camera = new SslCameraCase();
    public int captureAfterFrames;
    public bool capture = true;
    public bool screenshot = true;
    public bool bindingProbe;
    public SslVolumeCase volume = new SslVolumeCase();
    public SslGtaoCase gtao = new SslGtaoCase();
    public SslFeatureCase feature = new SslFeatureCase();
}

[Serializable]
public sealed class SslCameraCase
{
    public float[] position = { 0f, 2f, -7f };
    public float[] rotation = { 0f, 0f, 0f };
    public float panPixelsPerFrame;
}

[Serializable]
public sealed class SslVolumeCase
{
    public SslSsgiCase ssgi;
    public SslSsrCase ssr;
    public SslRefractionCase refraction;
}

[Serializable]
public sealed class SslSsgiCase
{
    // JsonUtility cannot represent a null nested object, so an omitted block still deserializes
    // into an instance; the effect stays off unless the manifest opts in explicitly.
    public bool enabled;
    public int rayMiss = 3;
    public bool fullResolution = true;
    public int maxRaySteps = 32;
    public float depthBufferThickness = 0.1f;
    public float intensity = 1f;
    public bool temporalAccumulation = true;
    public bool denoise = true;
    public float denoiserRadius = 0.6f;
    public bool secondDenoiserPass = true;
    public bool halfResolutionDenoiser;
    public bool bilateralUpsample = true;
}

[Serializable]
public sealed class SslSsrCase
{
    public bool enabled;
    public bool enabledTransparent;
    public int usedAlgorithm = 1;
    public bool fullResolution = true;
    public float minSmoothness = 0.9f;
    public float smoothnessFadeStart = 0.9f;
    public float depthBufferThickness = 0.01f;
    public float screenFadeDistance = 0.1f;
    public int rayMaxIterations = 64;
    public bool reflectSky = true;
    public float intensity = 1f;
    public float biasFactor = 0.5f;
    public int denoiserRadius = 8;
    public bool affectSmoothSurfaces;
    public bool singleReflectionBounce = true;
}

[Serializable]
public sealed class SslRefractionCase
{
    public bool enabled;
    public float screenFadeDistance = 0.1f;
    public float intensity = 1f;
}

[Serializable]
public sealed class SslGtaoCase
{
    public float intensity;
    public bool fullResolution;
    public int stepCount = 6;
    public int directionCount = 2;
    public int maximumRadiusInPixels = 40;
    public float radius = 2f;
    public float directLightingStrength;
    public bool temporalAccumulation = true;
    public float ghostingReduction = 0.5f;
    public float blurSharpness = 0.1f;
    public float spatialBilateralAggressiveness = 0.15f;
    public bool bilateralUpsample = true;
}

[Serializable]
public sealed class SslFeatureCase
{
    public bool publishProbeAtlas;
    public int debugMode;
}

[Serializable]
public sealed class SslValidationReport
{
    public string utcTimestamp;
    public string unityVersion;
    public string graphicsDeviceName;
    public string graphicsDeviceType;
    public string outputDirectory;
    public string manifestPath;
    public string renderer;
    public int renderWidth;
    public int renderHeight;
    public bool renderDocAvailable;
    public string renderDocVersion;
    public bool passed;
    public bool completed;
    public string error;
    public List<SslValidationCaseResult> cases;
}

[Serializable]
public sealed class SslValidationCaseResult
{
    public string id;
    public string renderer;
    public string effect;
    public string pipeline;
    public Vector3 cameraPosition;
    public Vector3 cameraRotation;
    public int frameIndex;
    public bool captureTriggered;
    public bool captureSucceeded;
    public int captureCountBefore;
    public int captureCountAfter;
    public string capture;
    public string screenshot;
    public float meanLuminance;
    public int nonFinitePixels;
    public bool ssgiActive;
    public bool ssrActive;
    public bool refractionActive;
    public float gtaoIntensity;
    public bool bindingProbeRequested;
    public bool bindingProbeWritten;
    public string bindingProbeError;
    public int bindingProbeComparisons;
    public int bindingProbeMismatches;
    public List<string> bindingProbeFailures;
    public SslSsgiDispatchStats ssgiDispatch;
    public SslSsrDenoiserStats ssrDenoiser;
    public SslSsrDebugViewStats ssrDebugView;
    public SslTransparentSsrStats transparentSsr;
    public bool passed;
    public List<string> issues;
}

[Serializable]
public sealed class SslSsrDenoiserStats
{
    public float pixelSpreadAngleTangent;
    public float expectedPixelSpreadAngleTangent;
    public int frameIndex;
    public int traceWidth;
    public int traceHeight;
    public int expectedTraceWidth;
    public int expectedTraceHeight;
    public bool ssgiActive;
    public bool passed;
}

[Serializable]
public sealed class SslSsrDebugViewStats
{
    public int frameIndex;
    public string debugTarget;
    public string materialOutput;
    public bool debugTargetIsPrivate;
    public bool materialOutputIsReal;
    public bool materialOutputIsDebugTarget;
    public bool denoiseChainRan;
    public bool passed;
}

[Serializable]
public sealed class SslTransparentSsrStats
{
    public int keywordFrameIndex;
    public int depthSeedFrameIndex;
    public bool halfResKeyword;
    public bool blueNoiseKeyword;
    public bool normalsOctKeyword;
    public bool approxKeyword;
    public bool blueNoiseTexturesBound;
    public bool isolatedCompute;
    public bool depthSeedYFlip;
    public bool depthSeedYFlipExpected;
    public bool depthSeedSourceFlipped;
    public bool depthSeedDestinationFlipped;
    public bool passed;
}

[Serializable]
public sealed class SslSsgiDispatchStats
{
    public int frameIndex;
    public int screenWidth;
    public int screenHeight;
    public int traceWidth;
    public int traceHeight;
    public bool temporal;
    public bool denoise;
    public bool secondDenoiserPass;
    public bool halfResolutionDenoiser;
    public int historyReadSlot;
    public int historyWriteSlot;
    public bool historyValid;
    public int[] historyReadSlots;
    public int[] historyWriteSlots;

    public int traceDispatches;
    public int reprojectDispatches;
    public int validateDispatches;
    public int accumulationDispatches;
    public int copyHistoryDispatches;
    public int diffuseDenoiseDispatches;
    public int gatherDispatches;
    public int upsampleDispatches;
    public int pointDistributionDispatches;
    public int totalDispatches;

    public int expectedTraceWidth;
    public int expectedTraceHeight;
    public int expectedTraceDispatches;
    public int expectedReprojectDispatches;
    public int expectedValidateDispatches;
    public int expectedAccumulationDispatches;
    public int expectedCopyHistoryDispatches;
    public int expectedDiffuseDenoiseDispatches;
    public int expectedGatherDispatches;
    public int expectedUpsampleDispatches;

    public int traceGroupsX;
    public int traceGroupsY;
    public int validateGroupsX;
    public int validateGroupsY;
    public int accumulationGroupsX;
    public int accumulationGroupsY;
    public int copyHistoryGroupsX;
    public int copyHistoryGroupsY;
    public int diffuseDenoiseGroupsX;
    public int diffuseDenoiseGroupsY;
    public int gatherGroupsX;
    public int gatherGroupsY;
    public int upsampleGroupsX;
    public int upsampleGroupsY;
    public int pointDistributionGroupsX;
    public int pointDistributionGroupsY;
    public int groupsZ;
    public int expectedTraceGroupsX;
    public int expectedTraceGroupsY;
    public int expectedScreenGroupsX;
    public int expectedScreenGroupsY;

    public bool halfResKeyword;
    public bool blueNoiseKeyword;
    public bool probeAtlasKeyword;
    public bool probeListKeyword;
    public bool normalsOctKeyword;
    public bool xrArrayKeyword;
}

[Serializable]
public sealed class SslBindingProbeReport
{
    public string id;
    public int frameIndex;
    public List<SslBindingProbeEntry> fields;
}

[Serializable]
public sealed class SslBindingProbeEntry
{
    public string field;
    public bool applicable;
    public Vector4 expected;
    public Vector4 actual;
    public float maxAbsoluteError;
    public bool passed;
}