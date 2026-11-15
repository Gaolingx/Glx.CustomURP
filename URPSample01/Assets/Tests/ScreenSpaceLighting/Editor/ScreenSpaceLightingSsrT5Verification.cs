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

// SSR T5 acceptance: denoiser evidence in the Forward, Forward+ and Deferred renderers.
//
// The checks cover:
// - reference vs Approximation vs PBR full resolution vs PBR half resolution captures;
// - the T7/T8 weight contract under the denoiser (miss keeps the probe, intensity 0 and the first
//   frame after a history reset restore the probe baseline, no NaN/Inf/negative radiance);
// - denoiser dispatch evidence (temporal, CopyHistory, bilateral H/V, FullRes/HalfRes kernels);
// - half resolution trace size, half radius and upsample dispatch;
// - history invalidation on an algorithm switch and a camera cut;
// - a moving reflection rig converging back to the reset baseline without persistent ghosting.
public static class ScreenSpaceLightingSsrT5Verification
{
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string OutputRoot = "PLAN/TODO/02-SSRImprove/validation/T5";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string RendererPathFormat = RendererRoot + "/SslT8{0}Renderer.asset";
    const string PipelinePathFormat = RendererRoot + "/SslT8{0}Pipeline.asset";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 32;
    const int SettleFrames = 48;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tests/Screen Space Lighting/Run SSR T5 Verification")]
    public static void RunSsrT5Verification()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);
        var report = new T5Report
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            passed = true,
            modes = new List<T5ModeResult>()
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
        var rigMaterials = new List<Material>();
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

            var volumeObject = GameObject.Find("Baseline Volume");
            if (volumeObject == null || volumeObject.GetComponent<Volume>() == null)
                throw new InvalidOperationException("The verification scene has no Baseline Volume.");
            volume = volumeObject.GetComponent<Volume>();
            originalProfile = volume.sharedProfile;
            testProfile = ScriptableObject.Instantiate(originalProfile);
            testProfile.name = originalProfile.name + " (SSR T5)";
            volume.sharedProfile = testProfile;
            DisableOtherScreenSpaceEffects(testProfile);

            reflectionRig = CreateReflectionRig(camera, rigMaterials);

            imageTarget = CreateTarget(ImageWidth, ImageHeight, "SslSsrT5Image");
            referenceTarget = CreateTarget(ImageWidth, ImageHeight, "SslSsrT5Reference");
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
                    outputDirectory);
                report.modes.Add(mode);
                report.passed &= mode.passed;
                Debug.Log($"SSR T5 {mode.name}: passed={mode.passed} issues={mode.issues.Count}");
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
            File.WriteAllText(Path.Combine(outputDirectory, "T5Verification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"SSR T5 verification finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    static T5ModeResult CaptureMode(
        string modeName,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        VolumeProfile profile,
        RenderTexture imageTarget,
        RenderTexture referenceTarget,
        GameObject rig,
        string outputDirectory)
    {
        var result = new T5ModeResult
        {
            name = modeName,
            issues = new List<string>(),
            states = new List<T5StateCapture>(),
            historyChecks = new List<T5HistorySnapshot>()
        };

        var ssr = profile.components.OfType<ScreenSpaceReflection>().First();
        var passType = GetPluginType("ScreenSpaceLightingSsrPass");
        var pipeline = LoadPipeline(modeName);
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(RendererPathFormat, modeName));
        if (renderer == null)
            throw new FileNotFoundException("Missing SSR T5 renderer asset. Create the T8 assets first.");
        var feature = renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (feature == null)
            throw new InvalidOperationException("The SSR T5 renderer is missing the Screen Space Lighting feature.");
        EnsureFeatureResources(feature);

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        // Reference: SSR disabled, same renderer and scene.
        ApplyBaseSettings(ssr);
        ssr.enabled.Override(false);
        SetTarget(camera, referenceTarget);
        cameraData.resetHistory = true;
        var reference = RenderAndCapture(camera, referenceTarget, WarmupFrames);
        result.states.Add(DescribeState("Reference", reference, null));
        WriteImage(referenceTarget, outputDirectory, "T5_" + modeName + "_Reference.png");

        SetTarget(camera, imageTarget);

        // -----------------------------------------------------------------------------------------
        // Approximation
        // -----------------------------------------------------------------------------------------
        ssr.enabled.Override(true);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
        ResetCounters(passType);
        cameraData.resetHistory = true;
        var approxSamples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("Approximation", approxSamples, reference));
        WriteImage(imageTarget, outputDirectory, "T5_" + modeName + "_Approximation.png");
        result.approxExecutions = ReadStaticInt(passType, "s_EditorExecuteCount");
        result.approxTraceDispatches = ReadStaticInt(passType, "s_EditorTraceDispatchCount");
        result.approxReprojectDispatches = ReadStaticInt(passType, "s_EditorReprojectDispatchCount");
        result.approxTemporalDispatches = ReadStaticInt(passType, "s_EditorTemporalDispatchCount");
        result.approxUpsampleDispatches = ReadStaticInt(passType, "s_EditorUpsampleDispatchCount");
        var approxOutput = ReadRTHandle(GetSsrOutputTexture(feature));
        result.approxWeight = DescribeWeight(approxOutput);
        CheckMissKeepsProbe(reference, approxSamples, approxOutput, "Approximation", result);
        if (result.approxTraceDispatches <= 0 || result.approxReprojectDispatches <= 0)
            result.issues.Add("Approximation: trace or reproject never executed.");
        if (result.approxTemporalDispatches != 0)
            result.issues.Add("Approximation dispatched the reflection denoiser.");
        if (result.approxUpsampleDispatches != 0)
            result.issues.Add("Approximation dispatched the half resolution upsample.");
        if (result.approxWeight.nonFinitePixels > 0 || result.approxWeight.negativePixels > 0)
            result.issues.Add("Approximation output contains NaN, Inf or negative radiance.");

        // -----------------------------------------------------------------------------------------
        // PBR full resolution
        // -----------------------------------------------------------------------------------------
        ApplyBaseSettings(ssr);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.PBRAccumulation);
        ssr.fullResolution.Override(true);
        ResetCounters(passType);
        cameraData.resetHistory = true;
        var pbrSamples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        var pbrState = DescribeState("PbrFullResolution", pbrSamples, reference);
        result.states.Add(pbrState);
        WriteImage(imageTarget, outputDirectory, "T5_" + modeName + "_PbrFullRes.png");
        result.pbrExecutions = ReadStaticInt(passType, "s_EditorExecuteCount");
        result.pbrTraceDispatches = ReadStaticInt(passType, "s_EditorTraceDispatchCount");
        result.pbrReprojectDispatches = ReadStaticInt(passType, "s_EditorReprojectDispatchCount");
        result.pbrTemporalDispatches = ReadStaticInt(passType, "s_EditorTemporalDispatchCount");
        result.pbrCopyHistoryDispatches = ReadStaticInt(passType, "s_EditorCopyHistoryDispatchCount");
        result.pbrBilateralHDispatches = ReadStaticInt(passType, "s_EditorBilateralH_DispatchCount");
        result.pbrBilateralVDispatches = ReadStaticInt(passType, "s_EditorBilateralV_DispatchCount");
        result.pbrUpsampleDispatches = ReadStaticInt(passType, "s_EditorUpsampleDispatchCount");
        result.pbrLastDenoiseKernel = ReadStaticString(passType, "s_EditorLastDenoiseKernel");
        result.pbrDenoiseParams = ReadStaticVector4(passType, "s_EditorLastDenoiseParams");
        var pbrOutput = ReadRTHandle(GetSsrOutputTexture(feature));
        result.pbrWeight = DescribeWeight(pbrOutput);
        CheckMissKeepsProbe(reference, pbrSamples, pbrOutput, "PbrFullResolution", result);
        if (result.pbrWeight.nonFinitePixels > 0 || result.pbrWeight.negativePixels > 0)
            result.issues.Add("PBR output contains NaN, Inf or negative radiance.");
        if (result.pbrTraceDispatches <= 0 || result.pbrReprojectDispatches <= 0)
            result.issues.Add("PbrFullResolution: trace or reproject never executed.");
        if (!pbrState.differsFromReference)
            result.issues.Add("PBR did not change the image.");
        if (result.pbrTemporalDispatches <= 0 || result.pbrCopyHistoryDispatches <= 0 ||
            result.pbrBilateralHDispatches <= 0 || result.pbrBilateralVDispatches <= 0)
            result.issues.Add("PBR denoiser did not dispatch every pass.");
        if (!result.pbrLastDenoiseKernel.EndsWith("_FR"))
            result.issues.Add($"PBR full resolution ended on {result.pbrLastDenoiseKernel} instead of an _FR filter.");
        if (result.pbrDenoiseParams.x != 8f)
            result.issues.Add($"PBR full resolution radius is {result.pbrDenoiseParams.x} instead of the volume's 8.");
        if (result.pbrUpsampleDispatches != 0)
            result.issues.Add("Full resolution PBR dispatched the half resolution upsample.");

        var history = GetHistory(feature, camera);
        if (history == null)
        {
            result.issues.Add("PBR camera has no Screen Space Lighting history entry.");
        }
        else
        {
            var snapshot = SnapshotHistory("AfterPbrWarmup", history);
            result.historyChecks.Add(snapshot);
            if (!snapshot.ssrPbrResourcesAllocated)
                result.issues.Add("PBR resources were not allocated.");
            if (!snapshot.ssrPbrHasValidHistory)
                result.issues.Add("PBR history never became valid.");
            if (snapshot.ssrPbrWidth != ImageWidth || snapshot.ssrPbrHeight != ImageHeight)
                result.issues.Add($"Full resolution PBR history is {snapshot.ssrPbrWidth}x{snapshot.ssrPbrHeight}, expected {ImageWidth}x{ImageHeight}.");
        }

        // -----------------------------------------------------------------------------------------
        // PBR half resolution
        // -----------------------------------------------------------------------------------------
        ssr.fullResolution.Override(false);
        ResetCounters(passType);
        cameraData.resetHistory = true;
        var halfSamples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        var halfState = DescribeState("PbrHalfResolution", halfSamples, reference);
        result.states.Add(halfState);
        WriteImage(imageTarget, outputDirectory, "T5_" + modeName + "_PbrHalfRes.png");
        result.halfTraceDispatches = ReadStaticInt(passType, "s_EditorTraceDispatchCount");
        result.halfReprojectDispatches = ReadStaticInt(passType, "s_EditorReprojectDispatchCount");
        result.halfTemporalDispatches = ReadStaticInt(passType, "s_EditorTemporalDispatchCount");
        result.halfCopyHistoryDispatches = ReadStaticInt(passType, "s_EditorCopyHistoryDispatchCount");
        result.halfBilateralHDispatches = ReadStaticInt(passType, "s_EditorBilateralH_DispatchCount");
        result.halfBilateralVDispatches = ReadStaticInt(passType, "s_EditorBilateralV_DispatchCount");
        result.halfUpsampleDispatches = ReadStaticInt(passType, "s_EditorUpsampleDispatchCount");
        result.halfLastDenoiseKernel = ReadStaticString(passType, "s_EditorLastDenoiseKernel");
        result.halfDenoiseParams = ReadStaticVector4(passType, "s_EditorLastDenoiseParams");
        var halfOutput = ReadRTHandle(GetSsrOutputTexture(feature));
        result.halfWeight = DescribeWeight(halfOutput);
        CheckMissKeepsProbe(reference, halfSamples, halfOutput, "PbrHalfResolution", result);
        if (result.halfWeight.nonFinitePixels > 0 || result.halfWeight.negativePixels > 0)
            result.issues.Add("Half resolution PBR output contains NaN, Inf or negative radiance.");
        if (result.halfTraceDispatches <= 0 || result.halfReprojectDispatches <= 0)
            result.issues.Add("PbrHalfResolution: trace or reproject never executed.");
        if (!result.halfLastDenoiseKernel.EndsWith("_HR"))
            result.issues.Add($"PBR half resolution ended on {result.halfLastDenoiseKernel} instead of an _HR filter.");
        if (result.halfDenoiseParams.x != 4f)
            result.issues.Add($"PBR half resolution radius is {result.halfDenoiseParams.x} instead of 4.");
        if (result.halfUpsampleDispatches <= 0)
            result.issues.Add("Half resolution PBR did not dispatch the upsample.");

        var halfHistory = GetHistory(feature, camera);
        if (halfHistory == null)
        {
            result.issues.Add("Half resolution PBR has no history entry.");
        }
        else
        {
            var snapshot = SnapshotHistory("AfterHalfResolutionWarmup", halfHistory);
            result.historyChecks.Add(snapshot);
            if (snapshot.ssrPbrWidth != ImageWidth / 2 || snapshot.ssrPbrHeight != ImageHeight / 2)
                result.issues.Add($"Half resolution PBR history is {snapshot.ssrPbrWidth}x{snapshot.ssrPbrHeight}, expected {ImageWidth / 2}x{ImageHeight / 2}.");
        }

        result.fullVsHalfDifferingPixels = CountDifferences(pbrSamples, halfSamples, 1e-3f);
        result.fullVsHalfMaxDelta = MaxDifference(pbrSamples, halfSamples);
        result.fullVsHalfToneMappedMaxDelta = ToneMappedMaxDifference(pbrSamples, halfSamples);
        result.fullVsHalfToneMappedPixelsOver = CountToneMappedDifferences(pbrSamples, halfSamples, 0.2f);
        result.fullVsHalfPixelFraction = result.fullVsHalfDifferingPixels / (float)Mathf.Max(1, pbrSamples.Length);
        result.fullVsHalfToneMappedPixelFraction = result.fullVsHalfToneMappedPixelsOver / (float)Mathf.Max(1, pbrSamples.Length);
        // Half resolution loses thin, very bright reflections, so a handful of pixels can still flip.
        // The acceptance is about the visible area: less than half a percent of the image may move
        // by more than 0.2 after tone mapping.
        if (result.fullVsHalfToneMappedPixelFraction > 0.005f)
            result.issues.Add($"Full and half resolution differ on {result.fullVsHalfToneMappedPixelFraction:P3} of the pixels by more than 0.2 tone mapped; the upsample may be wrong.");

        // -----------------------------------------------------------------------------------------
        // Baseline recovery: intensity 0 and the first frame after a reset
        // -----------------------------------------------------------------------------------------
        ssr.fullResolution.Override(true);
        ssr.intensity.Override(0f);
        cameraData.resetHistory = true;
        var zeroIntensity = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("PbrZeroIntensityRestoresBaseline", zeroIntensity, reference));
        WriteImage(imageTarget, outputDirectory, "T5_" + modeName + "_PbrIntensityZero.png");
        result.zeroIntensityDifferingPixels = CountDifferences(zeroIntensity, reference, 0f);
        if (result.zeroIntensityDifferingPixels != 0)
            result.issues.Add($"intensity=0 differs from the probe baseline on {result.zeroIntensityDifferingPixels} pixels.");
        ssr.intensity.Override(1f);

        cameraData.resetHistory = true;
        var firstFrame = RenderAndCapture(camera, imageTarget, 1);
        result.states.Add(DescribeState("PbrFirstFrameAfterReset", firstFrame, reference));
        result.firstFrameDifferingPixels = CountDifferences(firstFrame, reference, 0f);
        if (result.firstFrameDifferingPixels != 0)
            result.issues.Add($"The first frame after a history reset differs from the probe baseline on {result.firstFrameDifferingPixels} pixels.");
        RenderAndCapture(camera, imageTarget, WarmupFrames);

        // -----------------------------------------------------------------------------------------
        // Moving rig: history must follow the object and converge after it stops
        // -----------------------------------------------------------------------------------------
        cameraData.resetHistory = true;
        var origin = rig != null ? rig.transform.position : Vector3.zero;
        for (int frame = 0; frame < 12; frame++)
        {
            if (rig != null)
                rig.transform.position = origin + Vector3.right * (0.25f * Mathf.Sin(frame * 0.8f));
            camera.Render();
        }

        var moving = ReadPixels(imageTarget);
        result.states.Add(DescribeState("PbrMovingRig", moving, reference));
        WriteImage(imageTarget, outputDirectory, "T5_" + modeName + "_PbrMoving.png");
        result.movingNonFinitePixels = Count(moving, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b));
        if (result.movingNonFinitePixels > 0)
            result.issues.Add("The moving rig image contains NaN or Inf.");

        if (rig != null)
            rig.transform.position = origin;
        var settled = RenderAndCapture(camera, imageTarget, SettleFrames);
        cameraData.resetHistory = true;
        var resetBaseline = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.settleVsResetDifferingPixels = CountDifferences(settled, resetBaseline, 0.02f);
        result.settleVsResetMaxDelta = MaxDifference(settled, resetBaseline);
        result.settleVsResetToneMappedMaxDelta = ToneMappedMaxDifference(settled, resetBaseline);
        result.settleVsResetPixelFraction = result.settleVsResetDifferingPixels / (float)Mathf.Max(1, settled.Length);
        result.states.Add(DescribeState("PbrSettledAfterMotion", settled, resetBaseline));
        WriteImage(imageTarget, outputDirectory, "T5_" + modeName + "_PbrSettled.png");
        if (result.settleVsResetPixelFraction > 0.001f || result.settleVsResetToneMappedMaxDelta > 0.15f)
            result.issues.Add($"The settled image differs from the reset baseline on {result.settleVsResetPixelFraction:P3} of the pixels; history may be ghosting.");

        // -----------------------------------------------------------------------------------------
        // Algorithm switch and camera cut invalidate the history
        // -----------------------------------------------------------------------------------------
        int pbrResetsBefore = ReadInt(GetHistory(feature, camera), "ssrPbrResetCount");
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
        RenderAndCapture(camera, imageTarget, 2);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.PBRAccumulation);
        RenderAndCapture(camera, imageTarget, 2);
        result.algorithmSwitchResets = ReadInt(GetHistory(feature, camera), "ssrPbrResetCount") - pbrResetsBefore;
        if (result.algorithmSwitchResets < 2)
            result.issues.Add($"Two algorithm switches reset the denoiser history only {result.algorithmSwitchResets} times.");

        var cutHistory = GetHistory(feature, camera);
        int cutResetsBefore = ReadInt(cutHistory, "resetCount");
        var previousPosition = camera.transform.position;
        camera.transform.position = previousPosition + new Vector3(2.5f, 0.5f, 0f);
        cameraData.resetHistory = true;
        var cutSamples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        camera.transform.position = previousPosition;
        result.cameraCutResets = ReadInt(GetHistory(feature, camera), "resetCount") - cutResetsBefore;
        result.states.Add(DescribeState("CameraCut", cutSamples, reference));
        WriteImage(imageTarget, outputDirectory, "T5_" + modeName + "_CameraCut.png");
        if (result.cameraCutResets <= 0)
            result.issues.Add("The camera reset did not invalidate the effect history.");
        if (Count(cutSamples, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)) > 0)
            result.issues.Add("CameraCut contains NaN or Inf.");

        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, WarmupFrames);
        ApplyBaseSettings(ssr);
        result.passed = result.issues.Count == 0;
        return result;
    }

    static void ApplyBaseSettings(ScreenSpaceReflection settings)
    {
        settings.enabled.Override(true);
        settings.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
        settings.fullResolution.Override(true);
        settings.minSmoothness.Override(0.5f);
        settings.smoothnessFadeStart.Override(0.9f);
        settings.depthBufferThickness.Override(0.05f);
        settings.screenFadeDistance.Override(0.1f);
        settings.rayMaxIterations.Override(64);
        settings.reflectSky.Override(true);
        settings.intensity.Override(1f);
        settings.biasFactor.Override(0.5f);
        settings.denoiserRadius.Override(8);
        settings.affectSmoothSurfaces.Override(false);
        settings.singleReflectionBounce.Override(true);
    }

    // ---------------------------------------------------------------------------------------------
    // Rig
    // ---------------------------------------------------------------------------------------------

    static GameObject CreateReflectionRig(Camera camera, List<Material> materials)
    {
        var root = new GameObject("SSR T5 Reflection Rig");
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            return root;

        var floorMaterial = CreateLitMaterial(shader, "SSR T5 Mirror Floor", new Color(0.75f, 0.78f, 0.82f), 1.0f, 0.95f);
        var backdropMaterial = CreateLitMaterial(shader, "SSR T5 Reflect Backdrop", new Color(0.05f, 0.05f, 0.05f), 0f, 0.15f);
        backdropMaterial.EnableKeyword("_EMISSION");
        backdropMaterial.SetColor("_EmissionColor", new Color(3.0f, 1.8f, 0.6f));
        materials.Add(floorMaterial);
        materials.Add(backdropMaterial);

        AddBox(root, floorMaterial, "SSR T5 Mirror Floor", new Vector3(0f, 0.04f, 0.5f), new Vector3(16f, 0.04f, 12f));
        AddBox(root, backdropMaterial, "SSR T5 Reflect Backdrop", new Vector3(0f, 3.0f, 4.2f), new Vector3(11f, 6f, 0.2f));

        var viewDirection = (Vector3.zero - camera.transform.position).normalized;
        var right = Vector3.Cross(Vector3.up, viewDirection).normalized;
        var center = camera.transform.position + viewDirection * 13f;
        center.y = 1.4f;
        var smoothness = new[] { 0.95f, 0.80f, 0.65f, 0.40f };
        for (int i = 0; i < smoothness.Length; i++)
        {
            var offset = right * ((i - 1.5f) * 2.2f);
            var material = CreateLitMaterial(shader, "SSR T5 Metal " + smoothness[i].ToString("0.00"), Color.white, 1.0f, smoothness[i]);
            materials.Add(material);
            AddSphere(root, material, "SSR T5 Metal Sphere " + i, center + offset, 1.1f);
        }

        var dielectric = CreateLitMaterial(shader, "SSR T5 Dielectric", new Color(0.15f, 0.25f, 0.45f), 0f, 0.95f);
        materials.Add(dielectric);
        AddSphere(root, dielectric, "SSR T5 Dielectric Sphere", center + right * 4.4f + Vector3.up * 1.2f, 0.9f);
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

    // ---------------------------------------------------------------------------------------------
    // Reflection and evidence helpers
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

    static Type GetPluginType(string name)
    {
        var type = typeof(ScreenSpaceLightingRendererFeature).Assembly.GetType("UnityEngine.Rendering.Universal." + name);
        if (type == null)
            throw new TypeLoadException(name + " was not found.");
        return type;
    }

    static void EnsureFeatureResources(ScreenSpaceLightingRendererFeature feature)
    {
        var settings = feature.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
        var resources = settings?.GetType().GetField("resources", InstanceFlags)?.GetValue(settings);
        var locator = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceLightingResourceLocator");
        locator?.GetMethod("AssignDefaults", StaticFlags)?.Invoke(null, new[] { resources });
    }

    static void ResetCounters(Type passType)
    {
        passType.GetMethod("ResetEditorEvidenceCounters", StaticFlags)?.Invoke(null, null);
    }

    static int ReadStaticInt(Type type, string name)
    {
        var field = type.GetField(name, StaticFlags);
        return field == null ? -1 : (int)field.GetValue(null);
    }

    static string ReadStaticString(Type type, string name)
    {
        var field = type.GetField(name, StaticFlags);
        return field == null ? string.Empty : (string)field.GetValue(null);
    }

    static Vector4 ReadStaticVector4(Type type, string name)
    {
        var field = type.GetField(name, StaticFlags);
        return field == null ? Vector4.zero : (Vector4)field.GetValue(null);
    }

    static object GetHistory(ScreenSpaceLightingRendererFeature feature, Camera camera)
    {
        var store = feature.GetType().GetProperty("historyStore", InstanceFlags)?.GetValue(feature);
        var tryGet = store?.GetType().GetMethod("TryGet", InstanceFlags);
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

    static T5HistorySnapshot SnapshotHistory(string label, object history)
    {
        return new T5HistorySnapshot
        {
            label = label,
            width = ReadInt(history, "width"),
            height = ReadInt(history, "height"),
            resetCount = ReadInt(history, "resetCount"),
            ssrPbrWidth = ReadInt(history, "ssrPbrWidth"),
            ssrPbrHeight = ReadInt(history, "ssrPbrHeight"),
            ssrPbrReadSlot = ReadInt(history, "ssrPbrReadSlot"),
            ssrPbrWriteSlot = ReadInt(history, "ssrPbrWriteSlot"),
            ssrPbrResourceGeneration = ReadInt(history, "ssrPbrResourceGeneration"),
            ssrPbrResetCount = ReadInt(history, "ssrPbrResetCount"),
            ssrPbrHasValidHistory = ReadBool(history, "ssrPbrHasValidHistory"),
            ssrPbrResourcesAllocated = ReadBool(history, "ssrPbrResourcesAllocated")
        };
    }

    static int ReadInt(object instance, string name)
    {
        if (instance == null)
            return 0;
        var property = instance.GetType().GetProperty(name, InstanceFlags);
        if (property != null)
            return (int)property.GetValue(instance);
        var field = instance.GetType().GetField(name, InstanceFlags);
        return field == null ? 0 : (int)field.GetValue(instance);
    }

    static bool ReadBool(object instance, string name)
    {
        if (instance == null)
            return false;
        var property = instance.GetType().GetProperty(name, InstanceFlags);
        if (property != null)
            return (bool)property.GetValue(instance);
        var field = instance.GetType().GetField(name, InstanceFlags);
        return field != null && (bool)field.GetValue(instance);
    }

    static UniversalRenderPipelineAsset LoadPipeline(string modeName)
    {
        var path = string.Format(PipelinePathFormat, modeName);
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
            throw new FileNotFoundException("Missing SSR T5 pipeline asset.", path);
        return pipeline;
    }

    static void CheckMissKeepsProbe(Color[] reference, Color[] image, Color[] weightPixels, string label, T5ModeResult result)
    {
        var stats = new T5MissStats();
        if (reference == null || image == null || weightPixels == null ||
            reference.Length != image.Length || weightPixels.Length != image.Length)
        {
            result.issues.Add($"{label}: the captures use different resolutions.");
            return;
        }

        for (var i = 0; i < image.Length; i++)
        {
            if (weightPixels[i].a > 1e-6f)
                continue;
            stats.compared++;
            var delta = Mathf.Max(
                Mathf.Abs(image[i].r - reference[i].r),
                Mathf.Max(Mathf.Abs(image[i].g - reference[i].g), Mathf.Abs(image[i].b - reference[i].b)));
            if (delta > 1e-5f)
                stats.different++;
            stats.maxChannelDelta = Mathf.Max(stats.maxChannelDelta, delta);
        }

        if (label == "Approximation")
            result.approxMiss = stats;
        else if (label == "PbrFullResolution")
            result.pbrFullMiss = stats;
        else
            result.pbrHalfMiss = stats;

        if (stats.compared == 0)
            result.issues.Add($"{label}: no zero weight pixel was found.");
        if (stats.different > 0)
            result.issues.Add($"{label}: {stats.different} zero weight pixels differ from the probe baseline (max delta {stats.maxChannelDelta}).");
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

    static T5WeightStats DescribeWeight(Color[] samples)
    {
        var stats = new T5WeightStats();
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

    static T5StateCapture DescribeState(string stateName, Color[] samples, Color[] reference)
    {
        var capture = new T5StateCapture
        {
            state = stateName,
            nonFinitePixels = Count(samples, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)),
            meanLuminance = Mean(samples, Luminance),
            differingPixels = reference != null ? CountDifferences(samples, reference, 0f) : 0,
            maxChannelDelta = reference != null ? MaxDifference(samples, reference) : 0f
        };
        capture.differsFromReference = capture.differingPixels > 0;
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

    // HDR reflections can exceed the display range by orders of magnitude, so raw maxima are not a
    // useful similarity metric. Comparing Reinhard tone mapped values keeps bright specular pixels
    // from dominating the comparison.
    static float ToneMappedMaxDifference(Color[] a, Color[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return float.PositiveInfinity;
        float max = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            max = Mathf.Max(max, Mathf.Max(
                Mathf.Abs(ToneMap(a[i].r) - ToneMap(b[i].r)),
                Mathf.Max(Mathf.Abs(ToneMap(a[i].g) - ToneMap(b[i].g)), Mathf.Abs(ToneMap(a[i].b) - ToneMap(b[i].b)))));
        }

        return max;
    }

    static float ToneMap(float value)
    {
        float positive = Mathf.Max(0f, value);
        return positive / (1f + positive);
    }

    static int CountToneMappedDifferences(Color[] a, Color[] b, float threshold)
    {
        if (a == null || b == null || a.Length != b.Length)
            return Math.Max(a?.Length ?? 0, b?.Length ?? 0);
        int count = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float delta = Mathf.Max(
                Mathf.Abs(ToneMap(a[i].r) - ToneMap(b[i].r)),
                Mathf.Max(Mathf.Abs(ToneMap(a[i].g) - ToneMap(b[i].g)), Mathf.Abs(ToneMap(a[i].b) - ToneMap(b[i].b))));
            if (delta > threshold)
                count++;
        }

        return count;
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
        foreach (var pixel in samples)
            sum += selector(pixel);
        return samples.Length == 0 ? 0f : (float)(sum / samples.Length);
    }

    // ---------------------------------------------------------------------------------------------
    // Report data
    // ---------------------------------------------------------------------------------------------

    [Serializable]
    public sealed class T5Report
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public bool completed;
        public bool passed;
        public string error;
        public List<T5ModeResult> modes;
    }

    [Serializable]
    public sealed class T5ModeResult
    {
        public string name;
        public bool passed;
        public List<string> issues;
        public int approxExecutions;
        public int approxTraceDispatches;
        public int approxReprojectDispatches;
        public int approxTemporalDispatches;
        public int approxUpsampleDispatches;
        public int pbrExecutions;
        public int pbrTraceDispatches;
        public int pbrReprojectDispatches;
        public int pbrTemporalDispatches;
        public int pbrCopyHistoryDispatches;
        public int pbrBilateralHDispatches;
        public int pbrBilateralVDispatches;
        public int pbrUpsampleDispatches;
        public string pbrLastDenoiseKernel;
        public Vector4 pbrDenoiseParams;
        public int halfTraceDispatches;
        public int halfReprojectDispatches;
        public int halfTemporalDispatches;
        public int halfCopyHistoryDispatches;
        public int halfBilateralHDispatches;
        public int halfBilateralVDispatches;
        public int halfUpsampleDispatches;
        public string halfLastDenoiseKernel;
        public Vector4 halfDenoiseParams;
        public int fullVsHalfDifferingPixels;
        public float fullVsHalfMaxDelta;
        public float fullVsHalfToneMappedMaxDelta;
        public int fullVsHalfToneMappedPixelsOver;
        public float fullVsHalfPixelFraction;
        public float fullVsHalfToneMappedPixelFraction;
        public int zeroIntensityDifferingPixels;
        public int firstFrameDifferingPixels;
        public int movingNonFinitePixels;
        public int settleVsResetDifferingPixels;
        public float settleVsResetMaxDelta;
        public float settleVsResetToneMappedMaxDelta;
        public float settleVsResetPixelFraction;
        public int algorithmSwitchResets;
        public int cameraCutResets;
        public T5WeightStats approxWeight = new T5WeightStats();
        public T5WeightStats pbrWeight = new T5WeightStats();
        public T5WeightStats halfWeight = new T5WeightStats();
        public T5MissStats approxMiss = new T5MissStats();
        public T5MissStats pbrFullMiss = new T5MissStats();
        public T5MissStats pbrHalfMiss = new T5MissStats();
        public List<T5StateCapture> states;
        public List<T5HistorySnapshot> historyChecks;
    }

    [Serializable]
    public sealed class T5StateCapture
    {
        public string state;
        public bool differsFromReference;
        public int differingPixels;
        public float maxChannelDelta;
        public int nonFinitePixels;
        public float meanLuminance;
    }

    [Serializable]
    public sealed class T5HistorySnapshot
    {
        public string label;
        public int width;
        public int height;
        public int resetCount;
        public int ssrPbrWidth;
        public int ssrPbrHeight;
        public int ssrPbrReadSlot;
        public int ssrPbrWriteSlot;
        public int ssrPbrResourceGeneration;
        public int ssrPbrResetCount;
        public bool ssrPbrHasValidHistory;
        public bool ssrPbrResourcesAllocated;
    }

    [Serializable]
    public sealed class T5WeightStats
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
    public sealed class T5MissStats
    {
        public int compared;
        public int different;
        public float maxChannelDelta;
    }
}