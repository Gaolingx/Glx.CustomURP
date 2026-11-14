using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// T5 acceptance: the independent GTAO feature must produce forward/Forward+/deferred ambient occlusion
// with its own depth pyramid and AO history, leave the image untouched when disabled, keep the same
// result regardless of the Screen Space Lighting feature order, yield to the built-in SSAO, and reset
// its history on camera cuts and resolution changes.
public static class ScreenSpaceLightingT5Verification
{
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string OutputRoot = "PALN/validation/T5";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string T1RendererPathFormat = RendererRoot + "/Ssl{0}Renderer.asset";
    const string T1PipelinePathFormat = RendererRoot + "/Ssl{0}Pipeline.asset";
    const string T5RendererPathFormat = RendererRoot + "/SslT5{0}Renderer.asset";
    const string T5ReversedRendererPathFormat = RendererRoot + "/SslT5{0}ReversedRenderer.asset";
    const string T5SsaoRendererPathFormat = RendererRoot + "/SslT5{0}SsaoRenderer.asset";
    const string T5PipelinePathFormat = RendererRoot + "/SslT5{0}Pipeline.asset";
    const string T5ReversedPipelinePathFormat = RendererRoot + "/SslT5{0}ReversedPipeline.asset";
    const string T5SsaoPipelinePathFormat = RendererRoot + "/SslT5{0}SsaoPipeline.asset";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 12;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tests/Screen Space Lighting/Create T5 Assets")]
    public static void CreateT5Assets()
    {
        EnsureAssets(force: true);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [MenuItem("Tests/Screen Space Lighting/Run T5 Verification")]
    public static void RunT5Verification()
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
            modes = new List<T5ModeResult>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture imageTarget = null;
        RenderTexture smallTarget = null;
        RenderTexture referenceTarget = null;
        GroundTruthAmbientOcclusionRendererFeature configuredFeature = null;
        try
        {
            EnsureAssets(force: false);
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            // A scene authored by script can keep an uninitialised skybox ambient probe; regenerate it
            // so the AO has indirect lighting to attenuate.
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

            imageTarget = CreateTarget(ImageWidth, ImageHeight, "SslT5Image");
            smallTarget = CreateTarget(640, 360, "SslT5SmallImage");
            referenceTarget = CreateTarget(ImageWidth, ImageHeight, "SslT5Reference");
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
                    volumeObject,
                    imageTarget,
                    smallTarget,
                    referenceTarget,
                    projectRoot,
                    outputDirectory);
                report.modes.Add(mode);
                report.passed &= mode.passed;
                Debug.Log($"T5 {mode.name}: passed={mode.passed} issues={mode.issues.Count}");
                configuredFeature = FindGtaoFeature(LoadPipeline(T5PipelinePathFormat, mode.name));
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
            if (configuredFeature != null)
            {
                SetGtaoDebug(configuredFeature, 0, 0);
                EditorUtility.SetDirty(configuredFeature);
            }

            if (imageTarget != null)
            {
                imageTarget.Release();
                UnityEngine.Object.DestroyImmediate(imageTarget);
            }
            if (smallTarget != null)
            {
                smallTarget.Release();
                UnityEngine.Object.DestroyImmediate(smallTarget);
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
            File.WriteAllText(Path.Combine(outputDirectory, "T5Verification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"T5 verification finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    static T5ModeResult CaptureMode(
        string modeName,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        GameObject volumeObject,
        RenderTexture imageTarget,
        RenderTexture smallTarget,
        RenderTexture referenceTarget,
        string projectRoot,
        string outputDirectory)
    {
        var result = new T5ModeResult
        {
            name = modeName,
            issues = new List<string>(),
            states = new List<T5StateCapture>(),
            historyChecks = new List<T5HistorySnapshot>(),
            debugImages = new List<T5DebugImage>()
        };

        var t1Pipeline = LoadPipeline(T1PipelinePathFormat, modeName);
        var t5Pipeline = LoadPipeline(T5PipelinePathFormat, modeName);
        var reversedPipeline = LoadPipeline(T5ReversedPipelinePathFormat, modeName);
        var ssaoPipeline = LoadPipeline(T5SsaoPipelinePathFormat, modeName);

        var t5Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T5RendererPathFormat, modeName));
        var reversedRenderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T5ReversedRendererPathFormat, modeName));
        var ssaoRenderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T5SsaoRendererPathFormat, modeName));
        if (t5Renderer == null || reversedRenderer == null || ssaoRenderer == null)
            throw new FileNotFoundException("Missing T5 renderer asset. Run Create T5 Assets first.");

        var gtao = FindGtaoFeature(t5Pipeline);
        var reversedGtao = FindGtaoFeature(reversedPipeline);
        var ssaoGtao = FindGtaoFeature(ssaoPipeline);
        if (gtao == null || reversedGtao == null || ssaoGtao == null)
            throw new InvalidOperationException("A T5 renderer is missing the GTAO feature.");
        result.featureOrder = DescribeFeatureOrder(t5Renderer);
        result.reversedFeatureOrder = DescribeFeatureOrder(reversedRenderer);
        result.ssaoFeatureOrder = DescribeFeatureOrder(ssaoRenderer);

        ApplyReferenceSettings(gtao);
        ApplyReferenceSettings(reversedGtao);
        ApplyReferenceSettings(ssaoGtao);

        // Reference: built-in renderer, no plugin features.
        GraphicsSettings.defaultRenderPipeline = t1Pipeline;
        QualitySettings.renderPipeline = t1Pipeline;
        volumeObject.SetActive(false);
        SetTarget(camera, referenceTarget);
        var reference = RenderAndCapture(camera, referenceTarget, WarmupFrames);
        var referenceCapture = DescribeState("Reference", reference, null);

        // GTAO on, half resolution, temporal accumulation, plugin volume disabled so the SSL feature
        // does not enqueue its own passes.
        GraphicsSettings.defaultRenderPipeline = t5Pipeline;
        QualitySettings.renderPipeline = t5Pipeline;
        volumeObject.SetActive(false);
        SetTarget(camera, imageTarget);
        cameraData.resetHistory = true;

        var pyramidType = GetPluginType("GroundTruthAmbientOcclusionDepthPyramidPass");
        var passType = GetPluginType("GroundTruthAmbientOcclusionPass");
        int pyramidExecutions = ReadStaticInt(pyramidType, "s_EditorExecuteCount");
        int pyramidCopies = ReadStaticInt(pyramidType, "s_EditorCopyDispatchCount");
        int mainDispatches = ReadStaticInt(passType, "s_EditorMainDispatchCount");
        int spatialDispatches = ReadStaticInt(passType, "s_EditorSpatialDispatchCount");
        int temporalDispatches = ReadStaticInt(passType, "s_EditorTemporalDispatchCount");
        int upsampleDispatches = ReadStaticInt(passType, "s_EditorUpsampleDispatchCount");

        var samples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.keywordEnabled = Shader.IsKeywordEnabled("_SCREEN_SPACE_OCCLUSION");
        result.ambientOcclusionParam = Shader.GetGlobalVector(Shader.PropertyToID("_AmbientOcclusionParam")).ToString();
        var onState = DescribeState("GtaoHalfResTemporal", samples, reference);
        result.states.Add(onState);
        WriteImage(imageTarget, outputDirectory, "T5_" + modeName + "_GtaoHalfResTemporal.png");

        result.pyramidExecutions = ReadStaticInt(pyramidType, "s_EditorExecuteCount") - pyramidExecutions;
        result.pyramidCopyDispatches = ReadStaticInt(pyramidType, "s_EditorCopyDispatchCount") - pyramidCopies;
        result.mainDispatches = ReadStaticInt(passType, "s_EditorMainDispatchCount") - mainDispatches;
        result.spatialDispatches = ReadStaticInt(passType, "s_EditorSpatialDispatchCount") - spatialDispatches;
        result.temporalDispatches = ReadStaticInt(passType, "s_EditorTemporalDispatchCount") - temporalDispatches;
        result.upsampleDispatches = ReadStaticInt(passType, "s_EditorUpsampleDispatchCount") - upsampleDispatches;

        if (!onState.differsFromReference)
            result.issues.Add("GtaoHalfResTemporal: the AO did not change the image; the effect did not run.");
        if (onState.meanLuminance >= referenceCapture.meanLuminance)
            result.issues.Add("GtaoHalfResTemporal: the image did not get darker, so the AO contribution is not visible.");
        if (onState.magentaPixels > 0)
            result.issues.Add("GtaoHalfResTemporal: image contains error-shader magenta.");
        if (onState.nonFinitePixels > 0)
            result.issues.Add("GtaoHalfResTemporal: image contains NaN or Inf.");

        if (result.pyramidExecutions <= 0)
            result.issues.Add("GtaoHalfResTemporal: the private depth pyramid pass did not execute.");
        if (result.pyramidCopyDispatches <= 0)
            result.issues.Add("GtaoHalfResTemporal: the depth pyramid copy was never dispatched.");
        if (result.mainDispatches <= 0 || result.spatialDispatches <= 0)
            result.issues.Add("GtaoHalfResTemporal: the horizon or spatial denoise dispatch did not execute.");
        if (result.temporalDispatches <= 0 || result.upsampleDispatches <= 0)
            result.issues.Add("GtaoHalfResTemporal: temporal accumulation or upsample did not execute.");

        var history = GetHistory(gtao, camera);
        if (history == null)
        {
            result.issues.Add("GtaoHalfResTemporal: the camera did not receive a GTAO history entry.");
        }
        else
        {
            var snapshot = SnapshotHistory("AfterWarmup", history);
            result.historyChecks.Add(snapshot);
            ValidateHalfResolutionSnapshot(snapshot, result);
            result.gtaoDepthPyramidBytes = ReadDepthPyramidBytes(history);

            if (!snapshot.resourcesAllocated || !snapshot.hasValidHistory)
                result.issues.Add("GtaoHalfResTemporal: the private resources or history never became valid.");
            if (snapshot.readSlot == snapshot.writeSlot)
                result.issues.Add("GtaoHalfResTemporal: the frame read and wrote the same history slot.");
        }

        CaptureDebugViews(camera, imageTarget, gtao, result, outputDirectory, modeName);

        CheckFullResolution(camera, imageTarget, gtao, samples, result, outputDirectory, modeName);
        CheckSpatialOnly(camera, imageTarget, gtao, result, outputDirectory, modeName, reference);
        CheckDisabledRestoresBaseline(camera, imageTarget, gtao, reference, result);
        CheckCameraCut(camera, cameraData, imageTarget, gtao, result);
        CheckSizeChange(camera, imageTarget, smallTarget, gtao, result, outputDirectory);
        CheckCameraIsolation(camera, cameraData, imageTarget, gtao, result);
        CheckFeatureOrderAndSslCombination(
            camera,
            cameraData,
            volumeObject,
            t5Pipeline,
            reversedPipeline,
            gtao,
            reversedGtao,
            reference,
            result,
            outputDirectory,
            modeName);
        CheckSsaoConflict(camera, volumeObject, ssaoPipeline, ssaoGtao, reference, result, outputDirectory, modeName);
        GraphicsSettings.defaultRenderPipeline = t5Pipeline;
        QualitySettings.renderPipeline = t5Pipeline;
        CheckFeatureDisabled(camera, imageTarget, gtao, reference, result);

        // Restore deterministic asset settings for the next run.
        ApplyReferenceSettings(gtao);
        ApplyReferenceSettings(reversedGtao);
        ApplyReferenceSettings(ssaoGtao);
        volumeObject.SetActive(true);
        GraphicsSettings.defaultRenderPipeline = t1Pipeline;
        QualitySettings.renderPipeline = t1Pipeline;

        foreach (var state in result.states)
        {
            if (state.magentaPixels > 0 && !state.state.Contains("Reference"))
                result.issues.Add($"{state.state}: image contains error-shader magenta.");
            if (state.nonFinitePixels > 0)
                result.issues.Add($"{state.state}: image contains NaN or Inf.");
        }

        result.passed = result.issues.Count == 0;
        return result;
    }

    static void ValidateHalfResolutionSnapshot(T5HistorySnapshot snapshot, T5ModeResult result)
    {
        if (snapshot.width != ImageWidth || snapshot.height != ImageHeight)
            result.issues.Add($"AfterWarmup: history is {snapshot.width}x{snapshot.height}, expected {ImageWidth}x{ImageHeight}");
        if (snapshot.aoWidth != ImageWidth / 2 || snapshot.aoHeight != ImageHeight / 2)
            result.issues.Add($"AfterWarmup: half resolution AO is {snapshot.aoWidth}x{snapshot.aoHeight}, expected {ImageWidth / 2}x{ImageHeight / 2}");
        if (snapshot.depthPyramidMipCount != 12)
            result.issues.Add($"AfterWarmup: the GTAO depth pyramid has {snapshot.depthPyramidMipCount} mips, expected 12.");
        if (snapshot.depthPyramidWidth <= 0 || snapshot.depthPyramidHeight <= 0)
            result.issues.Add("AfterWarmup: the GTAO depth pyramid has no allocated size.");
    }

    static void CaptureDebugViews(
        Camera camera,
        RenderTexture imageTarget,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        T5ModeResult result,
        string outputDirectory,
        string modeName)
    {
        CaptureDebugView(camera, imageTarget, gtao, 3, 0, "RawAmbientOcclusion", result, outputDirectory, modeName);
        CaptureDebugView(camera, imageTarget, gtao, 1, 0, "AmbientOcclusion", result, outputDirectory, modeName);
        CaptureDebugView(camera, imageTarget, gtao, 4, 0, "GlobalAmbientOcclusion", result, outputDirectory, modeName);
        CaptureDebugView(camera, imageTarget, gtao, 2, 0, "DepthPyramidMip0", result, outputDirectory, modeName);
        CaptureDebugView(camera, imageTarget, gtao, 2, 2, "DepthPyramidMip2", result, outputDirectory, modeName);
        SetGtaoDebug(gtao, 0, 0);
    }

    static void CaptureDebugView(
        Camera camera,
        RenderTexture imageTarget,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        int debugMode,
        int mip,
        string viewName,
        T5ModeResult result,
        string outputDirectory,
        string modeName)
    {
        SetGtaoDebug(gtao, debugMode, mip);
        RenderAndCapture(camera, imageTarget, 2);
        var samples = ReadPixels(imageTarget);
        var imageInfo = new T5DebugImage
        {
            view = viewName,
            mip = mip,
            file = "T5_" + modeName + "_" + viewName + ".png",
            sha256 = HashSamples(samples),
            magentaPixels = Count(samples, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f),
            nonFinitePixels = Count(samples, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)),
            meanLuminance = Mean(samples, c => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b),
            minR = Min(samples, c => c.r),
            maxR = Max(samples, c => c.r)
        };
        result.debugImages.Add(imageInfo);
        WriteImage(imageTarget, outputDirectory, imageInfo.file);

        if (imageInfo.magentaPixels > 0)
            result.issues.Add(viewName + ": debug view contains error-shader magenta.");
        if (imageInfo.nonFinitePixels > 0)
            result.issues.Add(viewName + ": debug view contains NaN or Inf.");
        if (imageInfo.meanLuminance <= 0f)
            result.issues.Add(viewName + ": debug view is completely black.");

        if (viewName == "AmbientOcclusion" || viewName == "GlobalAmbientOcclusion")
        {
            if (imageInfo.maxR < 0.99f)
                result.issues.Add($"{viewName}: no visible neutral pixel (max R {imageInfo.maxR:F3}); visibility output is not white where unoccluded.");
            if (imageInfo.minR > 0.9f)
                result.issues.Add($"{viewName}: no occluded pixel (min R {imageInfo.minR:F3}); the horizon search produced no visible AO.");
        }
        else if (viewName == "RawAmbientOcclusion")
        {
            if (imageInfo.maxR < 0.9f)
                result.issues.Add($"{viewName}: the raw horizon integration never reaches an unoccluded value (max R {imageInfo.maxR:F3}).");
            if (imageInfo.minR > 0.95f)
                result.issues.Add($"{viewName}: the raw horizon integration is white everywhere (min R {imageInfo.minR:F3}); no occlusion was found.");
        }
    }

    static void CheckFullResolution(
        Camera camera,
        RenderTexture imageTarget,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        Color[] halfResolutionSamples,
        T5ModeResult result,
        string outputDirectory,
        string modeName)
    {
        var history = GetHistory(gtao, camera);
        if (history == null)
            return;

        int generationBefore = ReadInt(history, "resourceGeneration");
        SetGtaoBool(gtao, "fullResolution", true);
        var samples = RenderAndCapture(camera, imageTarget, 6);
        var state = DescribeState("GtaoFullRes", samples, halfResolutionSamples);
        result.states.Add(state);
        WriteImage(imageTarget, outputDirectory, "T5_" + modeName + "_GtaoFullRes.png");

        var snapshot = SnapshotHistory("GtaoFullRes", history);
        result.historyChecks.Add(snapshot);
        if (snapshot.aoWidth != ImageWidth || snapshot.aoHeight != ImageHeight)
            result.issues.Add($"GtaoFullRes: AO is {snapshot.aoWidth}x{snapshot.aoHeight}, expected {ImageWidth}x{ImageHeight}.");
        if (snapshot.resourceGeneration <= generationBefore)
            result.issues.Add("GtaoFullRes: switching to full resolution did not reallocate the private resources.");
        if (state.magentaPixels > 0 || state.nonFinitePixels > 0)
            result.issues.Add("GtaoFullRes: image is invalid.");
        if (state.meanLuminance <= 0f)
            result.issues.Add("GtaoFullRes: image is completely black.");
        if (!state.differsFromReference)
            result.issues.Add("GtaoFullRes: full resolution AO produced the same image as half resolution.");

        SetGtaoBool(gtao, "fullResolution", false);
        RenderAndCapture(camera, imageTarget, 2);
    }

    static void CheckSpatialOnly(
        Camera camera,
        RenderTexture imageTarget,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        T5ModeResult result,
        string outputDirectory,
        string modeName,
        Color[] reference)
    {
        var passType = GetPluginType("GroundTruthAmbientOcclusionPass");
        int temporalBefore = ReadStaticInt(passType, "s_EditorTemporalDispatchCount");
        int upsampleBefore = ReadStaticInt(passType, "s_EditorUpsampleDispatchCount");

        SetGtaoBool(gtao, "temporalAccumulation", false);
        var samples = RenderAndCapture(camera, imageTarget, 6);
        var state = DescribeState("GtaoSpatialOnly", samples, reference);
        result.states.Add(state);
        WriteImage(imageTarget, outputDirectory, "T5_" + modeName + "_GtaoSpatialOnly.png");

        int temporalDelta = ReadStaticInt(passType, "s_EditorTemporalDispatchCount") - temporalBefore;
        int upsampleDelta = ReadStaticInt(passType, "s_EditorUpsampleDispatchCount") - upsampleBefore;
        result.spatialOnlyTemporalDispatches = temporalDelta;
        result.spatialOnlyUpsampleDispatches = upsampleDelta;

        if (temporalDelta != 0)
            result.issues.Add("GtaoSpatialOnly: the temporal kernel ran while temporal accumulation was disabled.");
        if (upsampleDelta <= 0)
            result.issues.Add("GtaoSpatialOnly: the non temporal smart blur/upsample kernel did not run.");
        if (!state.differsFromReference)
            result.issues.Add("GtaoSpatialOnly: the spatial-only image equals the reference; AO did not run.");
        if (state.magentaPixels > 0 || state.nonFinitePixels > 0)
            result.issues.Add("GtaoSpatialOnly: image is invalid.");

        SetGtaoBool(gtao, "temporalAccumulation", true);
        RenderAndCapture(camera, imageTarget, 2);
    }

    static void CheckDisabledRestoresBaseline(
        Camera camera,
        RenderTexture imageTarget,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        Color[] reference,
        T5ModeResult result)
    {
        SetGtaoFloat(gtao, "intensity", 0f);
        var samples = RenderAndCapture(camera, imageTarget, 6);
        result.states.Add(DescribeState("ZeroIntensityRestoresBaseline", samples, reference));
        var state = result.states[result.states.Count - 1];
        if (!state.identicalToReference)
            result.issues.Add(
                $"ZeroIntensityRestoresBaseline: image differs from the baseline ({state.differingPixels} pixels, " +
                $"max channel delta {state.maxChannelDelta:G4}).");

        SetGtaoFloat(gtao, "intensity", 2f);
        RenderAndCapture(camera, imageTarget, 2);
    }

    static void CheckCameraCut(
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        RenderTexture imageTarget,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        T5ModeResult result)
    {
        var history = GetHistory(gtao, camera);
        if (history == null)
            return;

        cameraData.resetHistory = true;
        int resetBefore = ReadInt(history, "resetCount");
        RenderAndCapture(camera, imageTarget, 1);
        var first = SnapshotHistory("CameraCutReset_FirstFrame", history);
        result.historyChecks.Add(first);

        if (first.resetCount <= resetBefore)
            result.issues.Add("CameraCutReset: the first render after the cut did not bump the reset generation.");
        if (first.readSlot != -1)
            result.issues.Add("CameraCutReset: the first frame read history instead of starting empty.");

        for (int frame = 2; frame <= 4; frame++)
        {
            RenderAndCapture(camera, imageTarget, 1);
            var settled = SnapshotHistory("CameraCutReset_Frame" + frame, history);
            result.historyChecks.Add(settled);
            if (settled.readSlot == settled.writeSlot)
                result.issues.Add($"CameraCutReset: frame {frame} read and wrote the same history slot.");
        }
    }

    static void CheckSizeChange(
        Camera camera,
        RenderTexture imageTarget,
        RenderTexture smallTarget,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        T5ModeResult result,
        string outputDirectory)
    {
        var history = GetHistory(gtao, camera);
        if (history == null)
            return;

        int resetBefore = ReadInt(history, "resetCount");
        int generationBefore = ReadInt(history, "resourceGeneration");
        SetTarget(camera, smallTarget);
        RenderAndCapture(camera, smallTarget, 2);
        var snapshot = SnapshotHistory("SizeChange640x360", history);
        result.historyChecks.Add(snapshot);
        WriteImage(smallTarget, outputDirectory, "T5_" + result.name + "_SizeChange640x360.png");
        result.states.Add(DescribeState("SizeChange640x360", ReadPixels(smallTarget), null));

        if (snapshot.width != 640 || snapshot.height != 360)
            result.issues.Add($"SizeChange640x360: history is {snapshot.width}x{snapshot.height}, expected 640x360.");
        if (snapshot.aoWidth != 320 || snapshot.aoHeight != 180)
            result.issues.Add($"SizeChange640x360: AO buffer is {snapshot.aoWidth}x{snapshot.aoHeight}, expected 320x180.");
        if (snapshot.resetCount <= resetBefore)
            result.issues.Add("SizeChange640x360: the size change did not reset the history.");
        if (snapshot.resourceGeneration <= generationBefore)
            result.issues.Add("SizeChange640x360: the size change did not reallocate the private resources.");

        SetTarget(camera, imageTarget);
        RenderAndCapture(camera, imageTarget, 2);
        result.historyChecks.Add(SnapshotHistory("SizeRestored1280x720", history));
    }

    static void CheckCameraIsolation(
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        RenderTexture imageTarget,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        T5ModeResult result)
    {
        var store = gtao.GetType().GetProperty("historyStore", InstanceFlags)?.GetValue(gtao);
        if (store == null)
        {
            result.issues.Add("CameraIsolation: history store was not found.");
            return;
        }

        var mainHistory = GetHistory(gtao, camera);
        int mainResetCount = mainHistory != null ? ReadInt(mainHistory, "resetCount") : -1;
        int mainEntries = ReadInt(store, "entryCount");

        var tempObject = new GameObject("T5 Temporary Camera");
        try
        {
            var tempCamera = tempObject.AddComponent<Camera>();
            tempCamera.CopyFrom(camera);
            tempCamera.targetTexture = imageTarget;
            tempCamera.depth = camera.depth + 1f;
            var tempData = tempObject.AddComponent<UniversalAdditionalCameraData>();
            tempData.renderPostProcessing = cameraData.renderPostProcessing;
            tempData.antialiasing = cameraData.antialiasing;

            RenderAndCapture(tempCamera, imageTarget, 1);
            var tempHistory = GetHistory(gtao, tempCamera);
            if (tempHistory == null)
                result.issues.Add("CameraIsolation: the temporary camera did not receive its own history entry.");
            else
            {
                if (ReferenceEquals(tempHistory, mainHistory))
                    result.issues.Add("CameraIsolation: both cameras share one history entry.");
                result.historyChecks.Add(SnapshotHistory("TemporaryCamera_FirstFrame", tempHistory));
                if (ReadInt(tempHistory, "readSlot") != -1)
                    result.issues.Add("CameraIsolation: the new camera's first frame read another camera's history.");
            }

            if (ReadInt(store, "entryCount") != mainEntries + 1)
                result.issues.Add($"CameraIsolation: expected {mainEntries + 1} entries with the temporary camera.");

            RenderAndCapture(camera, imageTarget, 2);
            if (mainHistory != null && ReadInt(mainHistory, "resetCount") != mainResetCount)
                result.issues.Add("CameraIsolation: rendering another camera reset the main camera history.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(tempObject);
        }

        RenderAndCapture(camera, imageTarget, 2);
        result.entriesAfterCameraDestroy = ReadInt(store, "entryCount");
        if (result.entriesAfterCameraDestroy != mainEntries)
            result.issues.Add($"CameraIsolation: a destroyed camera entry was not pruned ({result.entriesAfterCameraDestroy}).");
    }

    static void CheckFeatureOrderAndSslCombination(
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        GameObject volumeObject,
        UniversalRenderPipelineAsset t5Pipeline,
        UniversalRenderPipelineAsset reversedPipeline,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        GroundTruthAmbientOcclusionRendererFeature reversedGtao,
        Color[] reference,
        T5ModeResult result,
        string outputDirectory,
        string modeName)
    {
        // The verification scene volume enables the SSL overrides, so both features execute and the
        // only variable is their order in the renderer feature list. Temporal accumulation is disabled
        // so every frame is deterministic and the two orders can be compared byte for byte.
        SetGtaoBool(gtao, "temporalAccumulation", false);
        SetGtaoBool(reversedGtao, "temporalAccumulation", false);
        volumeObject.SetActive(true);

        GraphicsSettings.defaultRenderPipeline = t5Pipeline;
        QualitySettings.renderPipeline = t5Pipeline;
        var normal = RenderAndCapture(camera, imageTargetFor(camera), 12);
        var normalState = DescribeState("CombinedOrder_SSLThenGtao", normal, reference);
        result.states.Add(normalState);
        WriteImage(camera.targetTexture, outputDirectory, "T5_" + modeName + "_CombinedOrder_SSLThenGtao.png");

        var normalHistory = GetHistory(gtao, camera);
        bool combinedAllocated = normalHistory != null && ReadBool(normalHistory, "resourcesAllocated");
        result.sslGtaoCombinedPyramidBytes = ReadDepthPyramidBytes(normalHistory);
        if (normalHistory != null)
            result.historyChecks.Add(SnapshotHistory("CombinedOrder_SSLThenGtao", normalHistory));

        GraphicsSettings.defaultRenderPipeline = reversedPipeline;
        QualitySettings.renderPipeline = reversedPipeline;
        var reversed = RenderAndCapture(camera, imageTargetFor(camera), 12);
        var reversedState = DescribeState("CombinedOrder_GtaoThenSsl", reversed, normal);
        result.states.Add(reversedState);
        WriteImage(camera.targetTexture, outputDirectory, "T5_" + modeName + "_CombinedOrder_GtaoThenSsl.png");

        result.orderIndependent = reversedState.identicalToReference;
        if (!normalState.differsFromReference)
            result.issues.Add("CombinedOrder: the combined image equals the baseline; AO did not run.");
        if (!result.orderIndependent)
            result.issues.Add(
                $"CombinedOrder: swapping the GTAO/SSL feature order changed the image ({reversedState.differingPixels} pixels).");

        if (normalHistory == null)
            result.issues.Add("CombinedOrder: the GTAO feature has no history entry for the camera while SSL is active.");
        else if (!combinedAllocated)
            result.issues.Add("CombinedOrder: the GTAO resources were not allocated with SSL active.");
        if (result.sslGtaoCombinedPyramidBytes <= 0)
            result.issues.Add("CombinedOrder: the GTAO private depth pyramid was not allocated.");
        result.duplicateHiZBytes = 2L * result.sslGtaoCombinedPyramidBytes;

        if (reversedGtao == null)
            result.issues.Add("CombinedOrder: the reversed renderer has no GTAO feature.");

        SetGtaoBool(gtao, "temporalAccumulation", true);
        SetGtaoBool(reversedGtao, "temporalAccumulation", true);
        volumeObject.SetActive(true);
        RenderAndCapture(camera, imageTargetFor(camera), 2);
    }

    static void CheckSsaoConflict(
        Camera camera,
        GameObject volumeObject,
        UniversalRenderPipelineAsset ssaoPipeline,
        GroundTruthAmbientOcclusionRendererFeature ssaoGtao,
        Color[] reference,
        T5ModeResult result,
        string outputDirectory,
        string modeName)
    {
        var passType = GetPluginType("GroundTruthAmbientOcclusionPass");
        int mainBefore = ReadStaticInt(passType, "s_EditorMainDispatchCount");
        int pyramidBefore = ReadStaticInt(GetPluginType("GroundTruthAmbientOcclusionDepthPyramidPass"), "s_EditorExecuteCount");

        volumeObject.SetActive(false);
        GraphicsSettings.defaultRenderPipeline = ssaoPipeline;
        QualitySettings.renderPipeline = ssaoPipeline;
        var samples = RenderAndCapture(camera, imageTargetFor(camera), 6);
        var withGtao = DescribeState("NativeSsaoAndGtao", samples, reference);
        result.states.Add(withGtao);
        WriteImage(camera.targetTexture, outputDirectory, "T5_" + modeName + "_NativeSsaoAndGtao.png");

        // With the GTAO feature disabled the renderer must produce exactly the same image, which proves
        // GTAO did not add a second AO term on top of the built-in SSAO.
        ssaoGtao.SetActive(false);
        var withoutGtao = RenderAndCapture(camera, imageTargetFor(camera), 3);
        ssaoGtao.SetActive(true);
        var withoutGtaoState = DescribeState("NativeSsaoWithoutGtao", withoutGtao, samples);
        result.states.Add(withoutGtaoState);
        result.ssaoImageDiffersFromBaseline = withGtao.differsFromReference;

        int mainDelta = ReadStaticInt(passType, "s_EditorMainDispatchCount") - mainBefore;
        int pyramidDelta = ReadStaticInt(GetPluginType("GroundTruthAmbientOcclusionDepthPyramidPass"), "s_EditorExecuteCount") - pyramidBefore;
        result.ssaoConflictGtaoDispatches = mainDelta + pyramidDelta;
        result.ssaoConflictLogged = ReadBool(ssaoGtao, "loggedSsaoConflict");
        var ssaoFeatureType = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion");
        var ssaoRendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GetAssetPath(ssaoPipeline).Replace("Pipeline.asset", "Renderer.asset"));
        var ssaoFeature = ssaoRendererData?.rendererFeatures.FirstOrDefault(feature => feature != null && feature.GetType() == ssaoFeatureType);
        result.ssaoShaderAssigned = ssaoFeature != null && ssaoFeature.GetType().GetField("m_Shader", InstanceFlags)?.GetValue(ssaoFeature) != null;
        result.ssaoMaterialAssigned = ssaoFeature != null && ssaoFeature.GetType().GetField("m_Material", InstanceFlags)?.GetValue(ssaoFeature) != null;

        if (result.ssaoConflictGtaoDispatches != 0)
            result.issues.Add("NativeSsaoAndGtao: GTAO executed while the built-in SSAO was active (double AO).");
        if (!result.ssaoConflictLogged)
            result.issues.Add("NativeSsaoAndGtao: the SSAO conflict warning was not recorded.");
        if (!withoutGtaoState.identicalToReference)
            result.issues.Add(
                $"NativeSsaoAndGtao: disabling GTAO changed the SSAO image by {withoutGtaoState.differingPixels} pixels; " +
                "the two AO producers overlap.");
        if (withGtao.magentaPixels > 0 || withGtao.nonFinitePixels > 0)
            result.issues.Add("NativeSsaoAndGtao: image is invalid.");

        volumeObject.SetActive(true);
        RenderAndCapture(camera, imageTargetFor(camera), 2);
    }

    static void CheckFeatureDisabled(
        Camera camera,
        RenderTexture imageTarget,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        Color[] reference,
        T5ModeResult result)
    {
        gtao.SetActive(false);
        try
        {
            var passType = GetPluginType("GroundTruthAmbientOcclusionPass");
            int before = ReadStaticInt(passType, "s_EditorExecuteCount");
            var samples = RenderAndCapture(camera, imageTarget, 4);
            int executed = ReadStaticInt(passType, "s_EditorExecuteCount") - before;
            result.featureDisabledExecutions = executed;
            if (executed != 0)
                result.issues.Add("FeatureDisabled: GTAO executed while the feature was inactive.");
            result.states.Add(DescribeState("FeatureDisabled", samples, reference));
        }
        finally
        {
            gtao.SetActive(true);
        }
    }

    static RenderTexture imageTargetFor(Camera camera)
    {
        return camera.targetTexture;
    }

    // ---------------------------------------------------------------------------------------------
    // Asset creation
    // ---------------------------------------------------------------------------------------------

    static void EnsureAssets(bool force)
    {
        if (SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before creating the T5 assets.");

        AssetDatabase.Refresh();
        for (var i = 0; i < ModeNames.Length; i++)
        {
            var mode = ModeNames[i];
            var normal = EnsureRenderer(
                string.Format(T5RendererPathFormat, mode),
                "SslT5" + mode + "Renderer",
                new[] { "ssl", "gtao" },
                force,
                out bool normalChanged);
            var reversed = EnsureRenderer(
                string.Format(T5ReversedRendererPathFormat, mode),
                "SslT5" + mode + "ReversedRenderer",
                new[] { "gtao", "ssl" },
                force,
                out bool reversedChanged);
            var ssao = EnsureRenderer(
                string.Format(T5SsaoRendererPathFormat, mode),
                "SslT5" + mode + "SsaoRenderer",
                new[] { "ssao", "gtao" },
                force,
                out bool ssaoChanged);

            EnsureFeatureResources(normal);
            EnsureFeatureResources(reversed);
            EnsureFeatureResources(ssao);

            EnsurePipeline(string.Format(T5PipelinePathFormat, mode), "SslT5" + mode + "Pipeline", normal, force || normalChanged);
            EnsurePipeline(string.Format(T5ReversedPipelinePathFormat, mode), "SslT5" + mode + "ReversedPipeline", reversed, force || reversedChanged);
            EnsurePipeline(string.Format(T5SsaoPipelinePathFormat, mode), "SslT5" + mode + "SsaoPipeline", ssao, force || ssaoChanged);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static UniversalRendererData EnsureRenderer(string rendererPath, string rendererName, string[] featureKinds, bool force, out bool recreated)
    {
        recreated = false;
        var t1Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(GetT1RendererPath(rendererName));
        if (t1Renderer == null)
            throw new FileNotFoundException("Missing T1 renderer asset. Create the T1 baseline first.", rendererName);

        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        bool matches = renderer != null && renderer.rendererFeatures.Count == featureKinds.Length;
        if (matches)
        {
            for (int i = 0; i < featureKinds.Length; i++)
            {
                if (!FeatureMatchesKind(renderer.rendererFeatures[i], featureKinds[i]))
                    matches = false;
            }
        }

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
            for (int i = 0; i < featureKinds.Length; i++)
            {
                var feature = CreateFeature(featureKinds[i], rendererName + featureKinds[i]);
                AssetDatabase.AddObjectToAsset(feature, renderer);
                renderer.rendererFeatures.Add(feature);
            }

            EnsureFeatureMap(renderer);
            EditorUtility.SetDirty(renderer);
            renderer.SetDirty();
        }

        ApplyReferenceSettings(FindGtaoFeature(renderer));
        return renderer;
    }

    static string GetT1RendererPath(string rendererName)
    {
        var suffix = rendererName.Replace("SslT5", "Ssl").Replace("ReversedRenderer", "Renderer").Replace("SsaoRenderer", "Renderer");
        return RendererRoot + "/" + suffix + ".asset";
    }

    static bool FeatureMatchesKind(ScriptableRendererFeature feature, string kind)
    {
        switch (kind)
        {
            case "ssl": return feature is ScreenSpaceLightingRendererFeature;
            case "gtao": return feature is GroundTruthAmbientOcclusionRendererFeature;
            case "ssao": return feature != null && feature.GetType().Name == "ScreenSpaceAmbientOcclusion";
            default: return false;
        }
    }

    static ScriptableRendererFeature CreateFeature(string kind, string name)
    {
        ScriptableRendererFeature feature;
        switch (kind)
        {
            case "ssl":
                feature = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
                break;
            case "gtao":
                feature = ScriptableObject.CreateInstance<GroundTruthAmbientOcclusionRendererFeature>();
                break;
            case "ssao":
                var type = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion");
                if (type == null)
                    throw new TypeLoadException("ScreenSpaceAmbientOcclusion was not found.");
                feature = (ScriptableRendererFeature)ScriptableObject.CreateInstance(type);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        feature.name = name;
        // Features created outside the inspector need their editor resource reload (SSAO loads its
        // shader in Create) before they can enqueue passes.
        feature.GetType().GetMethod("Create", InstanceFlags)?.Invoke(feature, null);
        return feature;
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

        foreach (var gtao in renderer.rendererFeatures.OfType<GroundTruthAmbientOcclusionRendererFeature>())
        {
            var settings = gtao.GetType().GetProperty("settings", InstanceFlags)?.GetValue(gtao);
            var resources = settings?.GetType().GetField("resources", InstanceFlags)?.GetValue(settings);
            var locator = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.GroundTruthAmbientOcclusionResourceLocator");
            locator?.GetMethod("AssignDefaults", StaticFlags)?.Invoke(null, new[] { resources });
            ApplyReferenceSettings(gtao);
            EditorUtility.SetDirty(gtao);
        }
    }

    static void ApplyReferenceSettings(GroundTruthAmbientOcclusionRendererFeature gtao)
    {
        if (gtao == null)
            return;

        SetGtaoFloat(gtao, "intensity", 2f);
        SetGtaoFloat(gtao, "radius", 2f);
        SetGtaoInt(gtao, "stepCount", 12);
        SetGtaoInt(gtao, "directionCount", 2);
        SetGtaoInt(gtao, "maximumRadiusInPixels", 40);
        SetGtaoFloat(gtao, "directLightingStrength", 0f);
        SetGtaoFloat(gtao, "spatialBilateralAggressiveness", 0.15f);
        SetGtaoFloat(gtao, "ghostingReduction", 0.5f);
        SetGtaoFloat(gtao, "blurSharpness", 0.1f);
        SetGtaoBool(gtao, "temporalAccumulation", true);
        SetGtaoBool(gtao, "bilateralUpsample", true);
        SetGtaoBool(gtao, "fullResolution", false);
        SetGtaoDebug(gtao, 0, 0);
    }

    // ---------------------------------------------------------------------------------------------
    // Reflection helpers
    // ---------------------------------------------------------------------------------------------

    static object GetSettings(GroundTruthAmbientOcclusionRendererFeature feature)
    {
        return feature?.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
    }

    static FieldInfo GetSettingField(GroundTruthAmbientOcclusionRendererFeature feature, string name)
    {
        var settings = GetSettings(feature);
        return settings?.GetType().GetField(name, InstanceFlags);
    }

    static void SetGtaoFloat(GroundTruthAmbientOcclusionRendererFeature feature, string name, float value)
    {
        var field = GetSettingField(feature, name);
        if (field != null)
        {
            field.SetValue(GetSettings(feature), value);
            EditorUtility.SetDirty(feature);
        }
    }

    static void SetGtaoInt(GroundTruthAmbientOcclusionRendererFeature feature, string name, int value)
    {
        var field = GetSettingField(feature, name);
        if (field != null)
        {
            field.SetValue(GetSettings(feature), value);
            EditorUtility.SetDirty(feature);
        }
    }

    static void SetGtaoBool(GroundTruthAmbientOcclusionRendererFeature feature, string name, bool value)
    {
        var field = GetSettingField(feature, name);
        if (field != null)
        {
            field.SetValue(GetSettings(feature), value);
            EditorUtility.SetDirty(feature);
        }
    }

    static void SetGtaoDebug(GroundTruthAmbientOcclusionRendererFeature feature, int mode, int mip)
    {
        var modeField = GetSettingField(feature, "debugMode");
        var mipField = GetSettingField(feature, "debugMip");
        if (modeField != null)
            modeField.SetValue(GetSettings(feature), Enum.ToObject(modeField.FieldType, mode));
        if (mipField != null)
            mipField.SetValue(GetSettings(feature), mip);
        EditorUtility.SetDirty(feature);
    }

    static string DescribeFeatureOrder(UniversalRendererData renderer)
    {
        return string.Join(">", renderer.rendererFeatures.Select(feature => feature.GetType().Name));
    }

    static object GetHistory(GroundTruthAmbientOcclusionRendererFeature gtao, Camera camera)
    {
        var store = gtao.GetType().GetProperty("historyStore", InstanceFlags)?.GetValue(gtao);
        if (store == null)
            return null;
        var tryGet = store.GetType().GetMethod("TryGet", InstanceFlags);
        if (tryGet == null)
            return null;
        var arguments = new object[] { camera, 0, null };
        bool found = tryGet.Invoke(store, arguments) is bool result && result;
        return found ? arguments[2] : null;
    }

    static T5HistorySnapshot SnapshotHistory(string label, object history)
    {
        return new T5HistorySnapshot
        {
            label = label,
            width = ReadInt(history, "width"),
            height = ReadInt(history, "height"),
            aoWidth = ReadInt(history, "aoWidth"),
            aoHeight = ReadInt(history, "aoHeight"),
            resetCount = ReadInt(history, "resetCount"),
            validFrameCount = ReadInt(history, "validFrameCount"),
            bufferIndex = ReadInt(history, "bufferIndex"),
            readSlot = ReadInt(history, "readSlot"),
            writeSlot = ReadInt(history, "writeSlot"),
            resourceGeneration = ReadInt(history, "resourceGeneration"),
            slices = ReadInt(history, "slices"),
            depthPyramidMipCount = ReadInt(history, "depthPyramidMipCount"),
            depthPyramidWidth = ReadRenderTextureDimension(history, "depthPyramid", true),
            depthPyramidHeight = ReadRenderTextureDimension(history, "depthPyramid", false),
            hasValidHistory = ReadBool(history, "hasValidHistory"),
            resourcesAllocated = ReadBool(history, "resourcesAllocated"),
        };
    }

    static int ReadRenderTextureDimension(object history, string property, bool width)
    {
        var handle = history.GetType().GetProperty(property, InstanceFlags)?.GetValue(history);
        var rt = handle?.GetType().GetProperty("rt", InstanceFlags)?.GetValue(handle) as RenderTexture;
        if (rt == null)
            return 0;
        return width ? rt.width : rt.height;
    }

    static long ReadDepthPyramidBytes(object history)
    {
        if (history == null)
            return 0;
        var handle = history.GetType().GetProperty("depthPyramid", InstanceFlags)?.GetValue(history);
        var rt = handle?.GetType().GetProperty("rt", InstanceFlags)?.GetValue(handle) as RenderTexture;
        if (rt == null)
            return 0;
        return (long)rt.width * rt.height * 4L * Math.Max(1, rt.volumeDepth);
    }

    static GroundTruthAmbientOcclusionRendererFeature FindGtaoFeature(UniversalRenderPipelineAsset pipeline)
    {
        var path = AssetDatabase.GetAssetPath(pipeline);
        var rendererPath = path.Replace("Pipeline.asset", "Renderer.asset");
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        return renderer?.rendererFeatures.OfType<GroundTruthAmbientOcclusionRendererFeature>().FirstOrDefault();
    }

    static GroundTruthAmbientOcclusionRendererFeature FindGtaoFeature(UniversalRendererData renderer)
    {
        return renderer?.rendererFeatures.OfType<GroundTruthAmbientOcclusionRendererFeature>().FirstOrDefault();
    }

    static Type GetPluginType(string name)
    {
        var type = typeof(GroundTruthAmbientOcclusionRendererFeature).Assembly.GetType("UnityEngine.Rendering.Universal." + name);
        if (type == null)
            throw new TypeLoadException(name + " was not found.");
        return type;
    }

    static int ReadStaticInt(Type type, string name)
    {
        var field = type.GetField(name, StaticFlags);
        return field == null ? -1 : (int)field.GetValue(null);
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

    static T5StateCapture DescribeState(string stateName, Color[] samples, Color[] reference)
    {
        var capture = new T5StateCapture
        {
            state = stateName,
            sha256 = HashSamples(samples),
            magentaPixels = Count(samples, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f),
            nonFinitePixels = Count(samples, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)),
            meanLuminance = Mean(samples, c => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b)
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

    [Serializable]
    sealed class T5Report
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public bool completed;
        public bool passed = true;
        public string error;
        public List<T5ModeResult> modes;
    }

    [Serializable]
    sealed class T5ModeResult
    {
        public string name;
        public bool passed;
        public List<string> issues;
        public string featureOrder;
        public string reversedFeatureOrder;
        public string ssaoFeatureOrder;
        public int pyramidExecutions;
        public int pyramidCopyDispatches;
        public int mainDispatches;
        public int spatialDispatches;
        public int temporalDispatches;
        public int upsampleDispatches;
        public int spatialOnlyTemporalDispatches;
        public int spatialOnlyUpsampleDispatches;
        public int entriesAfterCameraDestroy;
        public bool orderIndependent;
        public long gtaoDepthPyramidBytes;
        public long sslGtaoCombinedPyramidBytes;
        public long duplicateHiZBytes;
        public int ssaoConflictGtaoDispatches;
        public bool ssaoConflictLogged;
        public bool ssaoShaderAssigned;
        public bool ssaoMaterialAssigned;
        public bool ssaoImageDiffersFromBaseline;
        public int featureDisabledExecutions;
        public bool keywordEnabled;
        public string ambientOcclusionParam;
        public List<T5StateCapture> states;
        public List<T5HistorySnapshot> historyChecks;
        public List<T5DebugImage> debugImages;
    }

    [Serializable]
    sealed class T5StateCapture
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
    sealed class T5HistorySnapshot
    {
        public string label;
        public int width;
        public int height;
        public int aoWidth;
        public int aoHeight;
        public int resetCount;
        public int validFrameCount;
        public int bufferIndex;
        public int readSlot;
        public int writeSlot;
        public int resourceGeneration;
        public int slices;
        public int depthPyramidMipCount;
        public int depthPyramidWidth;
        public int depthPyramidHeight;
        public bool hasValidHistory;
        public bool resourcesAllocated;
    }

    [Serializable]
    sealed class T5DebugImage
    {
        public string view;
        public int mip;
        public string file;
        public string sha256;
        public int magentaPixels;
        public int nonFinitePixels;
        public float meanLuminance;
        public float minR;
        public float maxR;
    }
}