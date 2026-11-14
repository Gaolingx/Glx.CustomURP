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

// T8 acceptance: SSR PBR Accumulation in the Forward, Forward+ and Deferred renderers.
//
// The checks cover:
// - algorithm selection (Approximation vs PBR accumulation) and the dispatch counters;
// - every public speed rejection combination selecting its own accumulate kernel;
// - surface/hit/both and world/screen speed rejection changing the result on a moving scene;
// - the speed rejection debug view (hard red/green, smooth hue);
// - algorithm switch and camera reset invalidating the accumulation history;
// - the T7 weight contract still holding for PBR (miss keeps the probe, intensity 0 restores the
//   baseline, first frame fallback, no NaN/Inf).
public static class ScreenSpaceLightingT8Verification
{
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string OutputRoot = "PALN/validation/T8";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string T1RendererPathFormat = RendererRoot + "/Ssl{0}Renderer.asset";
    const string T8RendererPathFormat = RendererRoot + "/SslT8{0}Renderer.asset";
    const string T8PipelinePathFormat = RendererRoot + "/SslT8{0}Pipeline.asset";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 16;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    sealed class Combination
    {
        public string name;
        public string kernel;
        public bool world;
        public bool smooth;
        public bool surface;
        public bool target;
    }

    static readonly Combination[] k_Combinations =
    {
        new Combination { name = "NoWorld_Both", kernel = "ScreenSpaceReflectionsAccumulateNoWorldSpeedRejectionBoth", world = false, surface = true, target = true },
        new Combination { name = "NoWorld_SurfaceOnly", kernel = "ScreenSpaceReflectionsAccumulateNoWorldSpeedRejectionSourceOnly", world = false, surface = true, target = false },
        new Combination { name = "NoWorld_TargetOnly", kernel = "ScreenSpaceReflectionsAccumulateNoWorldSpeedRejectionTargetOnly", world = false, surface = false, target = true },
        new Combination { name = "Hard_Both", kernel = "ScreenSpaceReflectionsAccumulateHardThresholdSpeedRejectionBoth", world = true, surface = true, target = true },
        new Combination { name = "Hard_SurfaceOnly", kernel = "ScreenSpaceReflectionsAccumulateHardThresholdSpeedRejectionSourceOnly", world = true, surface = true, target = false },
        new Combination { name = "Hard_TargetOnly", kernel = "ScreenSpaceReflectionsAccumulateHardThresholdSpeedRejectionTargetOnly", world = true, surface = false, target = true },
        new Combination { name = "Smooth_Both", kernel = "ScreenSpaceReflectionsAccumulateSmoothSpeedRejectionBoth", world = true, smooth = true, surface = true, target = true },
        new Combination { name = "Smooth_SurfaceOnly", kernel = "ScreenSpaceReflectionsAccumulateSmoothSpeedRejectionSourceOnly", world = true, smooth = true, surface = true, target = false },
        new Combination { name = "Smooth_TargetOnly", kernel = "ScreenSpaceReflectionsAccumulateSmoothSpeedRejectionTargetOnly", world = true, smooth = true, surface = false, target = true },
    };

    [MenuItem("Tests/Screen Space Lighting/Create T8 Assets")]
    public static void CreateT8Assets()
    {
        EnsureAssets(force: true);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [MenuItem("Tests/Screen Space Lighting/Run T8 Verification")]
    public static void RunT8Verification()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);
        var report = new T8Report
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            passed = true,
            modes = new List<T8ModeResult>()
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
            testProfile.name = originalProfile.name + " (T8)";
            volume.sharedProfile = testProfile;
            DisableOtherScreenSpaceEffects(testProfile);

            reflectionRig = CreateReflectionRig(camera, rigMaterials);

            imageTarget = CreateTarget(ImageWidth, ImageHeight, "SslT8Image");
            referenceTarget = CreateTarget(ImageWidth, ImageHeight, "SslT8Reference");
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
                Debug.Log($"T8 {mode.name}: passed={mode.passed} issues={mode.issues.Count}");
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
            File.WriteAllText(Path.Combine(outputDirectory, "T8Verification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"T8 verification finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    static T8ModeResult CaptureMode(
        string modeName,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        VolumeProfile profile,
        RenderTexture imageTarget,
        RenderTexture referenceTarget,
        GameObject rig,
        string outputDirectory)
    {
        var result = new T8ModeResult
        {
            name = modeName,
            issues = new List<string>(),
            states = new List<T8StateCapture>(),
            combinations = new List<T8CombinationResult>(),
            historyChecks = new List<T8HistorySnapshot>()
        };

        var ssr = profile.components.OfType<ScreenSpaceReflection>().First();
        var pipeline = LoadPipeline(T8PipelinePathFormat, modeName);
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T8RendererPathFormat, modeName));
        if (renderer == null)
            throw new FileNotFoundException("Missing T8 renderer asset. Run Create T8 Assets first.");
        var feature = renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (feature == null)
            throw new InvalidOperationException("The T8 renderer is missing the Screen Space Lighting feature.");

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        // Reference: SSR disabled, same renderer and scene.
        ApplyBaseSettings(ssr);
        ssr.enabled.Override(false);
        SetTarget(camera, referenceTarget);
        cameraData.resetHistory = true;
        var reference = RenderAndCapture(camera, referenceTarget, WarmupFrames);
        result.states.Add(DescribeState("Reference", reference, null));
        WriteImage(referenceTarget, outputDirectory, "T8_" + modeName + "_Reference.png");

        SetTarget(camera, imageTarget);
        var passType = GetPluginType("ScreenSpaceLightingSsrPass");
        var setupType = GetPluginType("ScreenSpaceLightingSetupPass");

        // -----------------------------------------------------------------------------------------
        // Approximation
        // -----------------------------------------------------------------------------------------
        ssr.enabled.Override(true);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
        ResetCounters(passType);
        cameraData.resetHistory = true;
        var approxSamples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.approxExecutions = ReadStaticInt(passType, "s_EditorExecuteCount");
        result.approxAccumulations = ReadStaticInt(passType, "s_EditorAccumulationDispatchCount");
        result.approxUsingPbr = ReadPassBool(feature, "usingPbrAccumulation");
        result.states.Add(DescribeState("Approximation", approxSamples, reference));
        WriteImage(imageTarget, outputDirectory, "T8_" + modeName + "_Approximation.png");
        if (result.approxAccumulations != 0)
            result.issues.Add("Approximation dispatched an accumulation kernel.");
        if (result.approxUsingPbr)
            result.issues.Add("Approximation reported the PBR output path.");

        // -----------------------------------------------------------------------------------------
        // PBR default
        // -----------------------------------------------------------------------------------------
        ApplyBaseSettings(ssr);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.PBRAccumulation);
        ResetCounters(passType);
        cameraData.resetHistory = true;
        var pbrSamples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        var pbrState = DescribeState("PbrDefault", pbrSamples, reference);
        result.states.Add(pbrState);
        WriteImage(imageTarget, outputDirectory, "T8_" + modeName + "_PbrDefault.png");

        result.pbrExecutions = ReadStaticInt(passType, "s_EditorExecuteCount");
        result.pbrTraceDispatches = ReadStaticInt(passType, "s_EditorTraceDispatchCount");
        result.pbrReprojectDispatches = ReadStaticInt(passType, "s_EditorReprojectDispatchCount");
        result.pbrAccumulationDispatches = ReadStaticInt(passType, "s_EditorAccumulationDispatchCount");
        result.pbrClearDispatches = ReadStaticInt(passType, "s_EditorClearDispatchCount");
        result.pbrUsingPbr = ReadPassBool(feature, "usingPbrAccumulation");
        var defaultKernelCounts = SnapshotKernelCounts(passType);
        result.pbrDefaultKernelCounts = ToKernelCountList(defaultKernelCounts);

        if (!result.pbrUsingPbr)
            result.issues.Add("PBR algorithm reported the Approximation output path.");
        if (result.pbrExecutions <= 0 || result.pbrTraceDispatches <= 0 || result.pbrReprojectDispatches <= 0)
            result.issues.Add("PBR trace or reproject never executed.");
        if (result.pbrAccumulationDispatches <= 0)
            result.issues.Add("PBR never dispatched an accumulation kernel.");
        if (!pbrState.differsFromReference)
            result.issues.Add("PBR accumulation did not change the image.");
        CheckKernelCounts(defaultKernelCounts, k_Combinations[0].kernel, "PbrDefault", result);

        var pbrOutput = ReadRTHandle(GetSsrOutputTexture(feature));
        result.weight = DescribeWeight(pbrOutput);
        CheckMissKeepsProbe(reference, pbrSamples, pbrOutput, result);
        if (result.weight.nonFinitePixels > 0 || result.weight.negativePixels > 0)
            result.issues.Add("PBR output contains NaN, Inf or negative radiance.");

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
                result.issues.Add("PBR accumulation resources were not allocated.");
            if (!snapshot.ssrPbrHasValidHistory)
                result.issues.Add("PBR accumulation history never became valid.");
            if (snapshot.ssrPbrReadSlot == snapshot.ssrPbrWriteSlot && snapshot.ssrPbrHasValidHistory)
                result.issues.Add("PBR reads and writes the same history slot.");
            if (snapshot.ssrPbrWidth != ImageWidth || snapshot.ssrPbrHeight != ImageHeight)
                result.issues.Add("PBR history has the wrong size.");
        }

        // -----------------------------------------------------------------------------------------
        // Every public combination selects its own kernel
        // -----------------------------------------------------------------------------------------
        for (int i = 0; i < k_Combinations.Length; i++)
        {
            var combination = k_Combinations[i];
            var comboResult = new T8CombinationResult { name = combination.name, expectedKernel = combination.kernel };
            ApplyCombination(ssr, combination);
            ResetCounters(passType);
            cameraData.resetHistory = true;
            RenderAndCapture(camera, imageTarget, 5);
            comboResult.dispatches = ReadStaticInt(passType, "s_EditorAccumulationDispatchCount");
            var comboKernelCounts = SnapshotKernelCounts(passType);
            comboResult.kernelCounts = ToKernelCountList(comboKernelCounts);
            comboResult.lastKernel = ReadStaticString(passType, "s_EditorLastAccumulateKernel");
            var comboOutput = ReadRTHandle(GetSsrOutputTexture(feature));
            comboResult.weight = DescribeWeight(comboOutput);

            if (comboResult.dispatches < 3)
                result.issues.Add($"{combination.name}: accumulation dispatched only {comboResult.dispatches} times.");
            if (!KernelCountsAreExclusive(comboKernelCounts, combination.kernel))
                result.issues.Add($"{combination.name}: dispatched an unexpected accumulate kernel: {FormatKernelCounts(comboKernelCounts)}.");
            if (comboResult.weight.nonFinitePixels > 0 || comboResult.weight.negativePixels > 0)
                result.issues.Add($"{combination.name}: the accumulation output is invalid.");
            result.combinations.Add(comboResult);
        }

        // -----------------------------------------------------------------------------------------
        // Speed rejection behaviour on a moving scene
        // -----------------------------------------------------------------------------------------
        var surfaceOnly = RunAnimatedSequence(camera, rig, imageTarget, ssr, cameraData, combinationIndex: 1);
        var targetOnly = RunAnimatedSequence(camera, rig, imageTarget, ssr, cameraData, combinationIndex: 2);
        result.surfaceVsTargetPixels = CountDifferences(surfaceOnly, targetOnly, 1e-5f);
        result.surfaceVsTargetMaxDelta = MaxDifference(surfaceOnly, targetOnly);
        WriteImage(imageTarget, outputDirectory, "T8_" + modeName + "_MovingTargetOnly.png");
        if (result.surfaceVsTargetPixels < ImageWidth * ImageHeight / 1000)
            result.issues.Add($"SurfaceOnly and TargetOnly differ on only {result.surfaceVsTargetPixels} pixels; the motion source setting has no effect.");

        var noRejection = RunAnimatedSequence(camera, rig, imageTarget, ssr, cameraData, combinationIndex: 0, speedRejection: 0f);
        var rejectAll = RunAnimatedSequence(camera, rig, imageTarget, ssr, cameraData, combinationIndex: 0, speedRejection: 1f);
        result.rejectionPixels = CountDifferences(noRejection, rejectAll, 1e-5f);
        result.rejectionMaxDelta = MaxDifference(noRejection, rejectAll);
        WriteImage(imageTarget, outputDirectory, "T8_" + modeName + "_MovingRejectAll.png");
        if (result.rejectionPixels < ImageWidth * ImageHeight / 1000)
            result.issues.Add($"Speed rejection changes only {result.rejectionPixels} pixels; rejection has no effect.");
        RecordMovingState(result, "MovingNoRejection", noRejection, reference);
        RecordMovingState(result, "MovingRejectAll", rejectAll, reference);

        var smooth = RunAnimatedSequence(camera, rig, imageTarget, ssr, cameraData, combinationIndex: 6);
        RecordMovingState(result, "MovingSmooth", smooth, reference);
        if (Count(smooth, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)) > 0)
            result.issues.Add("The smooth rejection image contains NaN or Inf.");

        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, WarmupFrames);

        // -----------------------------------------------------------------------------------------
        // Speed rejection debug view
        // -----------------------------------------------------------------------------------------
        var hardCombo = k_Combinations[3];
        ApplyCombination(ssr, hardCombo);
        ssr.accumulationFactor.Override(0f);
        ssr.speedRejectionParam.Override(1f);
        SetSslDebug(feature, 13, 0);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 4);
        var rejectDebug = ReadRTHandle(GetSsrOutputTexture(feature));
        var rejectColors = DescribeSpeedColors(rejectDebug);
        result.debugRejectRedFraction = rejectColors.redFraction;
        result.debugRejectGreenFraction = rejectColors.greenFraction;
        result.debugRejectColoredPixels = rejectColors.coloredPixels;
        result.debugRejectSpeedRejection = ReadStaticFloat(passType, "s_EditorLastSpeedRejection");
        result.debugRejectAccumulationAmount = ReadStaticFloat(passType, "s_EditorLastAccumulationAmount");
        result.debugRejectPbrParams = ReadStaticVector4(passType, "s_EditorLastPbrParams");
        result.debugRejectPbrFlags = ReadStaticVector4(passType, "s_EditorLastPbrFlags");
        WriteImage(imageTarget, outputDirectory, "T8_" + modeName + "_DebugSpeedRejectAll.png");
        if (rejectColors.coloredPixels < 100 || rejectColors.redFraction < 0.5f)
            result.issues.Add($"DebugSpeedRejectAll is not predominantly red (red {rejectColors.redFraction:P1}, colored {rejectColors.coloredPixels}).");

        ssr.speedRejectionParam.Override(0f);
        // The screen-space kernels use the speed directly, so an accept test needs a scene without
        // motion and the world-speed kernels would saturate from the reconstructed motion baseline.
        ApplyCombination(ssr, k_Combinations[0]);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 4);
        var acceptDebug = ReadRTHandle(GetSsrOutputTexture(feature));
        var acceptColors = DescribeSpeedColors(acceptDebug);
        result.debugAcceptRedFraction = acceptColors.redFraction;
        result.debugAcceptGreenFraction = acceptColors.greenFraction;
        result.debugAcceptColoredPixels = acceptColors.coloredPixels;
        result.debugAcceptSpeedRejection = ReadStaticFloat(passType, "s_EditorLastSpeedRejection");
        result.debugAcceptAccumulationAmount = ReadStaticFloat(passType, "s_EditorLastAccumulationAmount");
        result.debugAcceptPbrParams = ReadStaticVector4(passType, "s_EditorLastPbrParams");
        result.debugAcceptPbrFlags = ReadStaticVector4(passType, "s_EditorLastPbrFlags");
        WriteImage(imageTarget, outputDirectory, "T8_" + modeName + "_DebugSpeedNoReject.png");
        if (acceptColors.coloredPixels < 100 || acceptColors.greenFraction < 0.5f)
            result.issues.Add($"DebugSpeedNoReject is not predominantly green (green {acceptColors.greenFraction:P1}, colored {acceptColors.coloredPixels}).");

        SetSslDebug(feature, 0, 0);
        ApplyBaseSettings(ssr);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.PBRAccumulation);

        // Diagnostic: read the constant buffer back through the DebugSsrParams kernel.
        ApplyCombination(ssr, hardCombo);
        SetSslDebug(feature, 14, 0);
        RenderAndCapture(camera, imageTarget, 3);
        var diagPixels = ReadRTHandle(GetSsrOutputTexture(feature));
        if (diagPixels != null && diagPixels.Length > 0)
        {
            result.diagPbrParams = new Vector4(diagPixels[0].r, diagPixels[0].g, diagPixels[0].b, diagPixels[0].a);
        }
        SetSslDebug(feature, 0, 0);
        ApplyBaseSettings(ssr);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.PBRAccumulation);

        // -----------------------------------------------------------------------------------------
        // Algorithm switch invalidates the accumulation history
        // -----------------------------------------------------------------------------------------
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 6);
        int resetsBefore = ReadInt(GetHistory(feature, camera), "ssrPbrResetCount");
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
        RenderAndCapture(camera, imageTarget, 3);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.PBRAccumulation);
        RenderAndCapture(camera, imageTarget, 3);
        int resetsAfter = ReadInt(GetHistory(feature, camera), "ssrPbrResetCount");
        result.algorithmSwitchResets = resetsAfter - resetsBefore;
        if (result.algorithmSwitchResets < 2)
            result.issues.Add($"Two algorithm switches reset the accumulation history only {result.algorithmSwitchResets} times.");
        var switchedOutput = ReadRTHandle(GetSsrOutputTexture(feature));
        if (DescribeWeight(switchedOutput).nonFinitePixels > 0)
            result.issues.Add("The image is invalid after switching algorithms.");
        result.historyChecks.Add(SnapshotHistory("AfterAlgorithmSwitch", GetHistory(feature, camera)));

        // -----------------------------------------------------------------------------------------
        // T7 contract still holds for PBR
        // -----------------------------------------------------------------------------------------
        cameraData.resetHistory = true;
        var firstFrame = RenderAndCapture(camera, imageTarget, 1);
        result.states.Add(DescribeState("PbrFirstFrameAfterReset", firstFrame, reference));
        if (CountDifferences(firstFrame, reference, 0f) != 0)
            result.issues.Add("PbrFirstFrameAfterReset differs from the probe image.");
        RenderAndCapture(camera, imageTarget, WarmupFrames);

        ssr.intensity.Override(0f);
        cameraData.resetHistory = true;
        var zeroIntensity = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("PbrZeroIntensityRestoresBaseline", zeroIntensity, reference));
        if (CountDifferences(zeroIntensity, reference, 0f) != 0)
            result.issues.Add("PbrZeroIntensityRestoresBaseline differs from the probe image.");
        ssr.intensity.Override(1f);

        var cutHistory = GetHistory(feature, camera);
        int cutResetsBefore = ReadInt(cutHistory, "resetCount");
        var previousPosition = camera.transform.position;
        camera.transform.position = previousPosition + new Vector3(2.5f, 0.5f, 0f);
        cameraData.resetHistory = true;
        var cutSamples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        camera.transform.position = previousPosition;
        result.cameraCutResets = ReadInt(GetHistory(feature, camera), "resetCount") - cutResetsBefore;
        result.states.Add(DescribeState("CameraCut", cutSamples, reference));
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

    static void RecordMovingState(T8ModeResult result, string name, Color[] samples, Color[] reference)
    {
        if (samples == null)
            return;
        result.states.Add(DescribeState(name, samples, reference));
    }

    static Color[] RunAnimatedSequence(
        Camera camera,
        GameObject rig,
        RenderTexture imageTarget,
        ScreenSpaceReflection settings,
        UniversalAdditionalCameraData cameraData,
        int combinationIndex,
        float speedRejection = 0.5f)
    {
        ApplyCombination(settings, k_Combinations[combinationIndex]);
        settings.accumulationFactor.Override(0.9f);
        settings.speedRejectionParam.Override(speedRejection);
        var passType = GetPluginType("ScreenSpaceLightingSsrPass");
        ResetCounters(passType);
        cameraData.resetHistory = true;

        var origin = rig != null ? rig.transform.position : Vector3.zero;
        const int frames = 10;
        for (int frame = 0; frame < frames; frame++)
        {
            if (rig != null)
                rig.transform.position = origin + Vector3.right * (0.2f * Mathf.Sin(frame * 0.9f));
            camera.Render();
        }

        if (rig != null)
            rig.transform.position = origin;

        return ReadPixels(imageTarget);
    }

    static void ApplyCombination(ScreenSpaceReflection settings, Combination combination)
    {
        settings.enabled.Override(true);
        settings.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.PBRAccumulation);
        settings.enableWorldSpeedRejection.Override(combination.world);
        settings.speedSmoothReject.Override(combination.smooth);
        settings.speedSurfaceOnly.Override(combination.surface);
        settings.speedTargetOnly.Override(combination.target);
    }

    static void ApplyBaseSettings(ScreenSpaceReflection settings)
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
        settings.accumulationFactor.Override(0.75f);
        settings.biasFactor.Override(0.5f);
        settings.speedRejectionParam.Override(0.5f);
        settings.speedRejectionScalerFactor.Override(0.2f);
        settings.speedSmoothReject.Override(false);
        settings.speedSurfaceOnly.Override(true);
        settings.speedTargetOnly.Override(true);
        settings.enableWorldSpeedRejection.Override(false);
    }

    // ---------------------------------------------------------------------------------------------
    // Rig
    // ---------------------------------------------------------------------------------------------

    static GameObject CreateReflectionRig(Camera camera, List<Material> materials)
    {
        var root = new GameObject("T8 Reflection Rig");
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            return root;

        var floorMaterial = CreateLitMaterial(shader, "T8 Mirror Floor", new Color(0.75f, 0.78f, 0.82f), 1.0f, 0.95f);
        var backdropMaterial = CreateLitMaterial(shader, "T8 Reflect Backdrop", new Color(0.05f, 0.05f, 0.05f), 0f, 0.15f);
        backdropMaterial.EnableKeyword("_EMISSION");
        backdropMaterial.SetColor("_EmissionColor", new Color(3.0f, 1.8f, 0.6f));
        materials.Add(floorMaterial);
        materials.Add(backdropMaterial);

        AddBox(root, floorMaterial, "T8 Mirror Floor", new Vector3(0f, 0.04f, 0.5f), new Vector3(16f, 0.04f, 12f));
        AddBox(root, backdropMaterial, "T8 Reflect Backdrop", new Vector3(0f, 3.0f, 4.2f), new Vector3(11f, 6f, 0.2f));

        var viewDirection = (Vector3.zero - camera.transform.position).normalized;
        var right = Vector3.Cross(Vector3.up, viewDirection).normalized;
        var center = camera.transform.position + viewDirection * 13f;
        center.y = 1.4f;
        var smoothness = new[] { 0.95f, 0.80f, 0.65f, 0.40f };
        for (int i = 0; i < smoothness.Length; i++)
        {
            var offset = right * ((i - 1.5f) * 2.2f);
            var material = CreateLitMaterial(shader, "T8 Metal " + smoothness[i].ToString("0.00"), Color.white, 1.0f, smoothness[i]);
            materials.Add(material);
            AddSphere(root, material, "T8 Metal Sphere " + i, center + offset, 1.1f);
        }

        var dielectric = CreateLitMaterial(shader, "T8 Dielectric", new Color(0.15f, 0.25f, 0.45f), 0f, 0.95f);
        materials.Add(dielectric);
        AddSphere(root, dielectric, "T8 Dielectric Sphere", center + right * 4.4f + Vector3.up * 1.2f, 0.9f);
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
    // Asset creation
    // ---------------------------------------------------------------------------------------------

    static void EnsureAssets(bool force)
    {
        if (SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before creating the T8 assets.");

        AssetDatabase.Refresh();
        for (var i = 0; i < ModeNames.Length; i++)
        {
            var mode = ModeNames[i];
            var renderer = EnsureRenderer(
                string.Format(T8RendererPathFormat, mode),
                "SslT8" + mode + "Renderer",
                force,
                out bool recreated);

            EnsureFeatureResources(renderer);
            EnsurePipeline(string.Format(T8PipelinePathFormat, mode), "SslT8" + mode + "Pipeline", renderer, force || recreated);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static UniversalRendererData EnsureRenderer(string rendererPath, string rendererName, bool force, out bool recreated)
    {
        recreated = false;
        var t1Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(
            string.Format(T1RendererPathFormat, rendererName.Replace("SslT8", string.Empty).Replace("Renderer", string.Empty)));
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

    static float ReadStaticFloat(Type type, string name)
    {
        var field = type.GetField(name, StaticFlags);
        return field == null ? float.NaN : (float)field.GetValue(null);
    }

    static Vector4 ReadStaticVector4(Type type, string name)
    {
        var field = type.GetField(name, StaticFlags);
        return field == null ? Vector4.zero : (Vector4)field.GetValue(null);
    }

    static Dictionary<string, int> SnapshotKernelCounts(Type passType)
    {
        var field = passType.GetField("s_EditorAccumulateKernelCounts", StaticFlags);
        var source = field?.GetValue(null) as Dictionary<string, int>;
        return source == null ? new Dictionary<string, int>() : new Dictionary<string, int>(source);
    }

    static bool KernelCountsAreExclusive(Dictionary<string, int> counts, string expected)
    {
        foreach (var pair in counts)
        {
            if (pair.Value <= 0)
                continue;
            if (pair.Key != expected)
                return false;
        }

        return counts.TryGetValue(expected, out int value) && value > 0;
    }

    static string FormatKernelCounts(Dictionary<string, int> counts)
    {
        return string.Join(", ", counts.Where(pair => pair.Value > 0).Select(pair => pair.Key + "=" + pair.Value));
    }

    static List<T8KernelCount> ToKernelCountList(Dictionary<string, int> counts)
    {
        return counts
            .Where(pair => pair.Value > 0)
            .Select(pair => new T8KernelCount { kernel = pair.Key, count = pair.Value })
            .ToList();
    }

    static void CheckKernelCounts(Dictionary<string, int> counts, string expected, string label, T8ModeResult result)
    {
        if (!KernelCountsAreExclusive(counts, expected))
            result.issues.Add($"{label}: expected only {expected}, got {FormatKernelCounts(counts)}.");
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

    static bool ReadPassBool(ScreenSpaceLightingRendererFeature feature, string name)
    {
        var pass = GetSsrPass(feature);
        var property = pass?.GetType().GetProperty(name, InstanceFlags);
        return property != null && (bool)property.GetValue(pass);
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

    static T8HistorySnapshot SnapshotHistory(string label, object history)
    {
        return new T8HistorySnapshot
        {
            label = label,
            width = ReadInt(history, "width"),
            height = ReadInt(history, "height"),
            resetCount = ReadInt(history, "resetCount"),
            ssrPbrWidth = ReadInt(history, "ssrPbrWidth"),
            ssrPbrHeight = ReadInt(history, "ssrPbrHeight"),
            ssrPbrReadSlot = ReadInt(history, "ssrPbrReadSlot"),
            ssrPbrWriteSlot = ReadInt(history, "ssrPbrWriteSlot"),
            ssrPbrBufferIndex = ReadInt(history, "ssrPbrBufferIndex"),
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

    static UniversalRenderPipelineAsset LoadPipeline(string format, string modeName)
    {
        var path = string.Format(format, modeName);
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
            throw new FileNotFoundException("Missing pipeline asset.", path);
        return pipeline;
    }

    static void CheckMissKeepsProbe(Color[] reference, Color[] image, Color[] weightPixels, T8ModeResult result)
    {
        if (reference == null || image == null || weightPixels == null ||
            reference.Length != image.Length || weightPixels.Length != image.Length)
        {
            result.issues.Add("MissKeepsProbe: the captures use different resolutions.");
            return;
        }

        var compared = 0;
        var different = 0;
        var maxDelta = 0f;
        for (var i = 0; i < image.Length; i++)
        {
            if (weightPixels[i].a > 1e-6f)
                continue;
            compared++;
            var delta = Mathf.Max(
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
            result.issues.Add($"MissKeepsProbe: {different} zero weight pixels differ from the probe image (max delta {maxDelta}).");
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

    static T8WeightStats DescribeWeight(Color[] samples)
    {
        var stats = new T8WeightStats();
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

    static T8SpeedColors DescribeSpeedColors(Color[] samples)
    {
        var colors = new T8SpeedColors();
        if (samples == null)
            return colors;

        int colored = 0;
        int red = 0;
        int green = 0;
        foreach (var pixel in samples)
        {
            if (!IsFinite(pixel.r) || !IsFinite(pixel.g) || !IsFinite(pixel.b) || !IsFinite(pixel.a))
                continue;
            if (pixel.a <= 1e-4f)
                continue;
            if (pixel.r + pixel.g + pixel.b <= 1e-6f)
                continue;

            colored++;
            if (pixel.r > pixel.g && pixel.r > pixel.b)
                red++;
            else if (pixel.g > pixel.r && pixel.g > pixel.b)
                green++;
        }

        colors.coloredPixels = colored;
        colors.redFraction = colored > 0 ? red / (float)colored : 0f;
        colors.greenFraction = colored > 0 ? green / (float)colored : 0f;
        return colors;
    }

    static T8StateCapture DescribeState(string stateName, Color[] samples, Color[] reference)
    {
        var capture = new T8StateCapture
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
        foreach (var pixel in samples)
            sum += selector(pixel);
        return samples.Length == 0 ? 0f : (float)(sum / samples.Length);
    }

    // ---------------------------------------------------------------------------------------------
    // Report data
    // ---------------------------------------------------------------------------------------------

    [Serializable]
    public sealed class T8Report
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public bool completed;
        public bool passed;
        public string error;
        public List<T8ModeResult> modes;
    }

    [Serializable]
    public sealed class T8ModeResult
    {
        public string name;
        public bool passed;
        public List<string> issues;
        public int approxExecutions;
        public int approxAccumulations;
        public bool approxUsingPbr;
        public int pbrExecutions;
        public int pbrTraceDispatches;
        public int pbrReprojectDispatches;
        public int pbrAccumulationDispatches;
        public int pbrClearDispatches;
        public bool pbrUsingPbr;
        public List<T8KernelCount> pbrDefaultKernelCounts;
        public List<T8CombinationResult> combinations;
        public int surfaceVsTargetPixels;
        public float surfaceVsTargetMaxDelta;
        public int rejectionPixels;
        public float rejectionMaxDelta;
        public float debugRejectRedFraction;
        public float debugRejectGreenFraction;
        public int debugRejectColoredPixels;
        public float debugRejectSpeedRejection;
        public float debugRejectAccumulationAmount;
        public Vector4 debugRejectPbrParams;
        public Vector4 debugRejectPbrFlags;
        public float debugAcceptRedFraction;
        public float debugAcceptGreenFraction;
        public int debugAcceptColoredPixels;
        public float debugAcceptSpeedRejection;
        public float debugAcceptAccumulationAmount;
        public Vector4 debugAcceptPbrParams;
        public Vector4 debugAcceptPbrFlags;
        public Vector4 diagPbrParams;
        public int algorithmSwitchResets;
        public int cameraCutResets;
        public int missPixelsCompared;
        public int missPixelsDifferent;
        public float missMaxChannelDelta;
        public T8WeightStats weight = new T8WeightStats();
        public List<T8StateCapture> states;
        public List<T8HistorySnapshot> historyChecks;
    }

    [Serializable]
    public sealed class T8KernelCount
    {
        public string kernel;
        public int count;
    }

    [Serializable]
    public sealed class T8CombinationResult
    {
        public string name;
        public string expectedKernel;
        public string lastKernel;
        public int dispatches;
        public List<T8KernelCount> kernelCounts;
        public T8WeightStats weight = new T8WeightStats();
    }

    [Serializable]
    public sealed class T8StateCapture
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
    public sealed class T8HistorySnapshot
    {
        public string label;
        public int width;
        public int height;
        public int resetCount;
        public int ssrPbrWidth;
        public int ssrPbrHeight;
        public int ssrPbrReadSlot;
        public int ssrPbrWriteSlot;
        public int ssrPbrBufferIndex;
        public int ssrPbrResourceGeneration;
        public int ssrPbrResetCount;
        public bool ssrPbrHasValidHistory;
        public bool ssrPbrResourcesAllocated;
    }

    [Serializable]
    public sealed class T8WeightStats
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

    public struct T8SpeedColors
    {
        public int coloredPixels;
        public float redFraction;
        public float greenFraction;
    }
}