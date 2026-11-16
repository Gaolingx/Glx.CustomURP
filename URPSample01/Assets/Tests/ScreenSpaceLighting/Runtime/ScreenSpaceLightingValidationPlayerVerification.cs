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
    public bool passed;
    public List<string> issues;
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