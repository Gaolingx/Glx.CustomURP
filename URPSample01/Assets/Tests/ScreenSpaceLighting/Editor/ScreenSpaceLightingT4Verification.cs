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

// T4 acceptance: the private depth/color pyramids and the previous color/depth history must be built
// per camera and XR eye, reset on first frame, camera cut, size and render-scale changes, never read
// and write the same history slot in one frame, and keep the final image identical while no effect
// consumes them. GTAO must not be required for any of this.
public static class ScreenSpaceLightingT4Verification
{
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string OutputRoot = "PLAN/Done/01-ScreenSpaceLighting/validation/T4";
    const string T1RendererPathFormat = RendererRoot + "/Ssl{0}Renderer.asset";
    const string T1PipelinePathFormat = RendererRoot + "/Ssl{0}Pipeline.asset";
    const string T4RendererPathFormat = RendererRoot + "/SslT4{0}Renderer.asset";
    const string T4PipelinePathFormat = RendererRoot + "/SslT4{0}Pipeline.asset";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 12;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tests/Screen Space Lighting/Create T4 Assets")]
    public static void CreateT4Assets()
    {
        EnsureAssets(force: true);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [MenuItem("Tests/Screen Space Lighting/Run T4 Verification")]
    public static void RunT4Verification()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);
        var report = new T4Report
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            modes = new List<T4ModeResult>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture imageTarget = null;
        RenderTexture smallTarget = null;
        RenderTexture referenceTarget = null;
        ScreenSpaceLightingRendererFeature configuredFeature = null;
        try
        {
            EnsureAssets(force: false);
            EditorSceneManager.OpenScene(ScreenSpaceLightingT3Verification.T3ScenePath, OpenSceneMode.Single);

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

            imageTarget = CreateTarget(ImageWidth, ImageHeight, "SslT4Image");
            smallTarget = CreateTarget(640, 360, "SslT4SmallImage");
            referenceTarget = CreateTarget(ImageWidth, ImageHeight, "SslT4Reference");
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
                Debug.Log($"T4 {mode.name}: passed={mode.passed} issues={mode.issues.Count}");
                configuredFeature = FindSslFeature(LoadPipeline(T4PipelinePathFormat, mode.name));
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
                SetDebug(configuredFeature, 0, 0);
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
            File.WriteAllText(Path.Combine(outputDirectory, "T4Verification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"T4 verification finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    static T4ModeResult CaptureMode(
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
        var result = new T4ModeResult
        {
            name = modeName,
            issues = new List<string>(),
            states = new List<T4StateCapture>(),
            debugImages = new List<T4DebugImage>(),
            historyChecks = new List<T4HistorySnapshot>()
        };

        var t1Pipeline = LoadPipeline(T1PipelinePathFormat, modeName);
        var t4Pipeline = LoadPipeline(T4PipelinePathFormat, modeName);
        var t4Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T4RendererPathFormat, modeName));
        if (t4Renderer == null)
            throw new FileNotFoundException("Missing T4 renderer asset.", string.Format(T4RendererPathFormat, modeName));

        var ssl = t4Renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (ssl == null)
            throw new InvalidOperationException("The T4 renderer does not contain ScreenSpaceLightingRendererFeature.");
        result.featureCount = t4Renderer.rendererFeatures.Count;
        result.gtaoPresent = t4Renderer.rendererFeatures.OfType<GroundTruthAmbientOcclusionRendererFeature>().Any();
        EnsureFeatureResources(ssl);
        SetDebug(ssl, 0, 0);

        // Reference: no features, no plugin volume overrides.
        GraphicsSettings.defaultRenderPipeline = t1Pipeline;
        QualitySettings.renderPipeline = t1Pipeline;
        volumeObject.SetActive(false);
        SetTarget(camera, referenceTarget);
        var reference = RenderAndCapture(camera, referenceTarget, WarmupFrames);
        volumeObject.SetActive(true);

        GraphicsSettings.defaultRenderPipeline = t4Pipeline;
        QualitySettings.renderPipeline = t4Pipeline;
        SetTarget(camera, imageTarget);
        var depthPassType = GetPluginType("ScreenSpaceDepthPyramidPass");
        var currentColorPassType = GetPluginType("ScreenSpaceLightingCurrentColorPyramidPass");
        var historyPassType = GetPluginType("ScreenSpaceLightingColorHistoryCapturePass");

        cameraData.resetHistory = true;
        int depthExecutions = ReadExecuteCount(depthPassType);
        int depthDispatches = ReadStaticInt(depthPassType, "s_EditorCopyDispatchCount");
        int currentColorExecutions = ReadExecuteCount(currentColorPassType);
        int historyExecutions = ReadExecuteCount(historyPassType);
        var samples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("PyramidsBuilt_DebugOff", samples, reference));
        WriteImage(imageTarget, outputDirectory, "T4_" + modeName + "_BaselineDebugOff.png");

        result.depthPyramidExecutions = ReadExecuteCount(depthPassType) - depthExecutions;
        result.depthCopyDispatches = ReadStaticInt(depthPassType, "s_EditorCopyDispatchCount") - depthDispatches;
        result.currentColorPyramidExecutions = ReadExecuteCount(currentColorPassType) - currentColorExecutions;
        result.historyCaptureExecutions = ReadExecuteCount(historyPassType) - historyExecutions;

        var history = GetHistory(ssl, camera);
        if (history != null)
        {
            result.historyChecks.Add(SnapshotHistory("AfterWarmup", history));
            if (!ReadBool(history, "resourcesAllocated"))
                result.issues.Add("PyramidsBuilt_DebugOff: history resources were not allocated");
            if (!ReadBool(history, "hasValidHistory"))
                result.issues.Add("PyramidsBuilt_DebugOff: history never became valid");
            if (ReadInt(history, "readSlot") == ReadInt(history, "writeSlot"))
                result.issues.Add("PyramidsBuilt_DebugOff: the frame read and wrote the same history slot");
            if (ReadInt(history, "width") != ImageWidth || ReadInt(history, "height") != ImageHeight)
                result.issues.Add($"PyramidsBuilt_DebugOff: history is {ReadInt(history, "width")}x{ReadInt(history, "height")}, expected {ImageWidth}x{ImageHeight}");
            if (ReadInt(history, "colorPyramidMipCount") != 9)
                result.issues.Add($"PyramidsBuilt_DebugOff: color pyramid has {ReadInt(history, "colorPyramidMipCount")} mips, expected 9");
            if (ReadDepthPyramidMipCount(history) != 12)
                result.issues.Add($"PyramidsBuilt_DebugOff: depth pyramid has {ReadDepthPyramidMipCount(history)} mips, expected 12");
        }

        if (result.depthPyramidExecutions <= 0)
            result.issues.Add("PyramidsBuilt_DebugOff: the depth pyramid pass did not execute");
        if (result.currentColorPyramidExecutions <= 0)
            result.issues.Add("PyramidsBuilt_DebugOff: the current color pyramid pass did not execute");
        if (result.historyCaptureExecutions <= 0)
            result.issues.Add("PyramidsBuilt_DebugOff: the color history capture pass did not execute");
        if (result.depthCopyDispatches <= 0)
            result.issues.Add("PyramidsBuilt_DebugOff: the depth pyramid copy was never dispatched");
        if (result.gtaoPresent)
            result.issues.Add("the T4 renderer unexpectedly contains a GTAO feature");

        if (result.featureCount != 1)
            result.issues.Add($"expected exactly one renderer feature (SSL only), found {result.featureCount}");

        CheckResetSequence(camera, cameraData, imageTarget, reference, ssl, result, "CameraCutReset");
        CheckSizeAndRenderScale(camera, imageTarget, smallTarget, reference, ssl, t4Pipeline, result, outputDirectory);
        CheckCameraIsolation(camera, cameraData, imageTarget, volumeObject, ssl, result);
        CheckFeatureDisabled(camera, imageTarget, reference, ssl, result);
        CheckDebugViews(camera, imageTarget, ssl, result, outputDirectory, modeName);

        foreach (var state in result.states)
        {
            if (state.magentaPixels > 0)
                result.issues.Add($"{state.state}: image contains error-shader magenta ({state.magentaPixels} pixels)");
            if (!state.identicalToReference)
                result.issues.Add($"{state.state}: image differs from the T1 baseline ({state.differingPixels} pixels, max channel delta {state.maxChannelDelta:G4})");
        }

        result.passed = result.issues.Count == 0;
        return result;
    }

    static void CheckResetSequence(
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        RenderTexture imageTarget,
        Color[] reference,
        ScreenSpaceLightingRendererFeature ssl,
        T4ModeResult result,
        string prefix)
    {
        cameraData.resetHistory = true;
        var history = GetHistory(ssl, camera);
        if (history == null)
        {
            result.issues.Add(prefix + ": history entry disappeared");
            return;
        }

        int resetBefore = ReadInt(history, "resetCount");
        int serialBefore = ReadInt(history, "frameSerial");
        for (int frame = 1; frame <= 6; frame++)
        {
            var samples = RenderAndCapture(camera, imageTarget, 1);
            var snapshot = SnapshotHistory(prefix + "_Frame" + frame, history);
            result.historyChecks.Add(snapshot);

            if (frame == 1)
            {
                if (snapshot.resetCount <= resetBefore)
                    result.issues.Add(prefix + ": the first render after the cut did not bump the reset generation");
                if (snapshot.readSlot != -1)
                    result.issues.Add(prefix + ": first frame read history instead of starting empty");
                if (!snapshot.hasValidHistory || snapshot.frameSerial != serialBefore + 1)
                    result.issues.Add(prefix + ": first frame did not capture exactly one valid frame");
            }
            else if (frame > 3)
            {
                // URP's resetHistoryFrames counter produces a rising and a falling edge, so two
                // consecutive generations can be invalidated. From frame 4 on the entry must settle
                // into a clean ping-pong that never reads the slot it writes.
                if (snapshot.readSlot == snapshot.writeSlot)
                    result.issues.Add(prefix + $": frame {frame} read and wrote the same history slot");
                var previous = result.historyChecks[result.historyChecks.Count - 2];
                if (snapshot.frameSerial != previous.frameSerial + 1)
                    result.issues.Add(prefix + $": frame {frame} did not accumulate exactly one history frame");
                if (snapshot.resetCount != previous.resetCount)
                    result.issues.Add(prefix + $": frame {frame} unexpectedly reset the history again");
            }

            var state = DescribeState(prefix + "_Frame" + frame, samples, reference);
            result.states.Add(state);
        }
    }

    static void CheckSizeAndRenderScale(
        Camera camera,
        RenderTexture imageTarget,
        RenderTexture smallTarget,
        Color[] reference,
        ScreenSpaceLightingRendererFeature ssl,
        UniversalRenderPipelineAsset pipeline,
        T4ModeResult result,
        string outputDirectory)
    {
        var history = GetHistory(ssl, camera);
        if (history == null)
            return;

        int resetBefore = ReadInt(history, "resetCount");
        SetTarget(camera, smallTarget);
        RenderAndCapture(camera, smallTarget, 2);
        var smallSnapshot = SnapshotHistory("SizeChange640x360", history);
        result.historyChecks.Add(smallSnapshot);
        if (smallSnapshot.width != 640 || smallSnapshot.height != 360)
            result.issues.Add($"SizeChange640x360: history is {smallSnapshot.width}x{smallSnapshot.height}, expected 640x360");
        if (smallSnapshot.resetCount <= resetBefore)
            result.issues.Add("SizeChange640x360: size change did not reset the history generation");
        if (smallSnapshot.colorPyramidMipCount != 8)
            result.issues.Add($"SizeChange640x360: color pyramid has {smallSnapshot.colorPyramidMipCount} mips, expected 8");
        var smallSamples = ReadPixels(smallTarget);
        WriteImage(smallTarget, outputDirectory, "T4_" + result.name + "_SizeChange640x360.png");
        result.states.Add(DescribeState("SizeChange640x360", smallSamples, null));

        SetTarget(camera, imageTarget);
        RenderAndCapture(camera, imageTarget, 2);
        var restored = SnapshotHistory("SizeRestored1280x720", history);
        result.historyChecks.Add(restored);
        if (restored.width != ImageWidth || restored.height != ImageHeight)
            result.issues.Add($"SizeRestored1280x720: history is {restored.width}x{restored.height}, expected {ImageWidth}x{ImageHeight}");

        float previousScale = pipeline.renderScale;
        int resetBeforeScale = ReadInt(history, "resetCount");
        pipeline.renderScale = 0.5f;
        try
        {
            RenderAndCapture(camera, imageTarget, 2);
            var scaled = SnapshotHistory("RenderScale0.5", history);
            result.historyChecks.Add(scaled);
            if (scaled.width != ImageWidth / 2 || scaled.height != ImageHeight / 2)
                result.issues.Add($"RenderScale0.5: history is {scaled.width}x{scaled.height}, expected {ImageWidth / 2}x{ImageHeight / 2}");
            if (scaled.resetCount <= resetBeforeScale)
                result.issues.Add("RenderScale0.5: render scale change did not reset the history generation");
        }
        finally
        {
            pipeline.renderScale = previousScale;
        }

        RenderAndCapture(camera, imageTarget, 2);
        result.historyChecks.Add(SnapshotHistory("RenderScaleRestored", history));

        var samples = RenderAndCapture(camera, imageTarget, 1);
        result.states.Add(DescribeState("AfterSizeAndScaleRestore", samples, reference));
    }

    static void CheckCameraIsolation(
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        RenderTexture imageTarget,
        GameObject volumeObject,
        ScreenSpaceLightingRendererFeature ssl,
        T4ModeResult result)
    {
        volumeObject.SetActive(true);
        var store = ssl.GetType().GetProperty("historyStore", InstanceFlags)?.GetValue(ssl);
        if (store == null)
        {
            result.issues.Add("CameraIsolation: history store was not found");
            return;
        }

        var mainHistory = GetHistory(ssl, camera);
        int mainResetCount = mainHistory != null ? ReadInt(mainHistory, "resetCount") : -1;
        int mainEntryCount = ReadInt(store, "entryCount");

        var tempObject = new GameObject("T4 Temporary Camera");
        try
        {
            var tempCamera = tempObject.AddComponent<Camera>();
            tempCamera.CopyFrom(camera);
            tempCamera.targetTexture = imageTarget;
            tempCamera.depth = camera.depth + 1f;
            var tempData = tempObject.AddComponent<UniversalAdditionalCameraData>();
            tempData.renderPostProcessing = cameraData.renderPostProcessing;
            tempData.antialiasing = cameraData.antialiasing;

            // A newly created camera must start without readable history even if another camera has
            // valid data, and its first frame must still render the scene normally.
            var firstFrame = RenderAndCapture(tempCamera, imageTarget, 1);
            var tempHistory = GetHistory(ssl, tempCamera);
            result.entriesWithTemporaryCamera = ReadInt(store, "entryCount");
            if (tempHistory == null)
                result.issues.Add("CameraIsolation: the temporary camera did not receive its own history entry");
            else
            {
                if (ReferenceEquals(tempHistory, mainHistory))
                    result.issues.Add("CameraIsolation: both cameras share one history entry");
                result.historyChecks.Add(SnapshotHistory("TemporaryCamera_FirstFrame", tempHistory));
                var tempResetCount = ReadInt(tempHistory, "resetCount");
                if (tempResetCount != 1)
                    result.issues.Add($"CameraIsolation: the new camera started with {tempResetCount} resets, expected 1");
                if (ReadInt(tempHistory, "readSlot") != -1)
                    result.issues.Add("CameraIsolation: the new camera's first frame read another camera's history");
                result.states.Add(DescribeState("TemporaryCamera_FirstFrame", firstFrame, null));
            }
            if (result.entriesWithTemporaryCamera != mainEntryCount + 1)
                result.issues.Add($"CameraIsolation: expected {mainEntryCount + 1} entries with the temporary camera, found {result.entriesWithTemporaryCamera}");

            RenderAndCapture(tempCamera, imageTarget, 3);
            if (tempHistory != null)
            {
                var settled = SnapshotHistory("TemporaryCamera_Settled", tempHistory);
                result.historyChecks.Add(settled);
                if (settled.readSlot == settled.writeSlot)
                    result.issues.Add("CameraIsolation: the temporary camera read and wrote the same slot");
                if (settled.frameSerial != 4)
                    result.issues.Add($"CameraIsolation: the temporary camera accumulated {settled.frameSerial} frames, expected 4");
            }

            RenderAndCapture(camera, imageTarget, 2);
            if (mainHistory != null && ReadInt(mainHistory, "resetCount") != mainResetCount)
                result.issues.Add("CameraIsolation: rendering another camera reset the main camera history");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(tempObject);
        }

        volumeObject.SetActive(true);
        RenderAndCapture(camera, imageTarget, 2);
        result.entriesAfterCameraDestroy = ReadInt(store, "entryCount");
        if (result.entriesAfterCameraDestroy != mainEntryCount)
            result.issues.Add($"CameraIsolation: destroyed camera entry was not pruned ({result.entriesAfterCameraDestroy})");
    }

    static void CheckFeatureDisabled(
        Camera camera,
        RenderTexture imageTarget,
        Color[] reference,
        ScreenSpaceLightingRendererFeature ssl,
        T4ModeResult result)
    {
        ssl.SetActive(false);
        try
        {
            var depthPassType = GetPluginType("ScreenSpaceDepthPyramidPass");
            int before = ReadExecuteCount(depthPassType);
            var samples = RenderAndCapture(camera, imageTarget, 4);
            int executed = ReadExecuteCount(depthPassType) - before;
            if (executed != 0)
                result.issues.Add("FeatureDisabled: SSL passes executed while the feature was inactive");
            result.states.Add(DescribeState("FeatureDisabled_VolumeOn", samples, reference));
        }
        finally
        {
            ssl.SetActive(true);
        }
    }

    static void CheckDebugViews(
        Camera camera,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature ssl,
        T4ModeResult result,
        string outputDirectory,
        string modeName)
    {
        var history = GetHistory(ssl, camera);
        if (history == null)
            return;

        CaptureDebugView(camera, imageTarget, ssl, history, 1, 0, "DepthPyramidMip0", result, outputDirectory, modeName, expectUvRange: false);
        CaptureDebugView(camera, imageTarget, ssl, history, 1, 2, "DepthPyramidMip2", result, outputDirectory, modeName, expectUvRange: false);
        CaptureDebugView(camera, imageTarget, ssl, history, 2, 0, "CurrentColorMip0", result, outputDirectory, modeName, expectUvRange: false);
        CaptureDebugView(camera, imageTarget, ssl, history, 2, 1, "CurrentColorMip1", result, outputDirectory, modeName, expectUvRange: false);
        CaptureDebugView(camera, imageTarget, ssl, history, 3, 0, "PreviousColorMip0", result, outputDirectory, modeName, expectUvRange: false);
        CaptureDebugView(camera, imageTarget, ssl, history, 4, 0, "PreviousDepth", result, outputDirectory, modeName, expectUvRange: false);
        CaptureDebugView(camera, imageTarget, ssl, history, 6, 0, "CameraDepth", result, outputDirectory, modeName, expectUvRange: false);
        CaptureDebugView(camera, imageTarget, ssl, history, 5, 0, "DepthPyramidUvMip0", result, outputDirectory, modeName, expectUvRange: true);
        CaptureDebugView(camera, imageTarget, ssl, history, 5, 2, "DepthPyramidUvMip2", result, outputDirectory, modeName, expectUvRange: true);
        SetDebug(ssl, 0, 0);
    }

    static void CaptureDebugView(
        Camera camera,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature ssl,
        object history,
        int debugMode,
        int mip,
        string viewName,
        T4ModeResult result,
        string outputDirectory,
        string modeName,
        bool expectUvRange)
    {
        SetDebug(ssl, debugMode, mip);
        RenderAndCapture(camera, imageTarget, 2);
        var samples = ReadPixels(imageTarget);
        var imageInfo = new T4DebugImage
        {
            view = viewName,
            mip = mip,
            file = "T4_" + modeName + "_" + viewName + ".png",
            sha256 = HashSamples(samples),
            magentaPixels = Count(samples, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f),
            nonFinitePixels = Count(samples, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)),
            meanLuminance = Mean(samples, c => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b),
            minR = Min(samples, c => c.r),
            maxR = Max(samples, c => c.r),
            minG = Min(samples, c => c.g),
            maxG = Max(samples, c => c.g)
        };
        result.debugImages.Add(imageInfo);
        WriteImage(imageTarget, outputDirectory, imageInfo.file);

        if (imageInfo.magentaPixels > 0)
            result.issues.Add(viewName + ": debug view contains error-shader magenta");
        if (imageInfo.nonFinitePixels > 0)
            result.issues.Add(viewName + ": debug view contains NaN or Inf");
        if (imageInfo.meanLuminance <= 0f && !expectUvRange)
            result.issues.Add(viewName + ": debug view is completely black");

        if (expectUvRange && TryGetPackedUvRange(history, mip, out float minU, out float maxU, out float minV, out float maxV))
        {
            const float tolerance = 0.02f;
            if (Mathf.Abs(imageInfo.minR - minU) > tolerance || Mathf.Abs(imageInfo.maxR - maxU) > tolerance ||
                Mathf.Abs(imageInfo.minG - minV) > tolerance || Mathf.Abs(imageInfo.maxG - maxV) > tolerance)
            {
                result.issues.Add(
                    $"{viewName}: atlas uv range is R[{imageInfo.minR:F3},{imageInfo.maxR:F3}] G[{imageInfo.minG:F3},{imageInfo.maxG:F3}] " +
                    $"but the packed mip expects R[{minU:F3},{maxU:F3}] G[{minV:F3},{maxV:F3}]");
            }
        }
    }

    static bool TryGetPackedUvRange(object history, int mip, out float minU, out float maxU, out float minV, out float maxV)
    {
        minU = maxU = minV = maxV = 0f;
        var info = history.GetType().GetProperty("depthPyramidInfo", InstanceFlags)?.GetValue(history);
        if (info == null)
            return false;

        var type = info.GetType();
        var textureSize = (Vector2Int)type.GetField("textureSize", InstanceFlags).GetValue(info);
        int mipLevelCount = (int)type.GetField("mipLevelCount", InstanceFlags).GetValue(info);
        var sizes = (Vector2Int[])type.GetField("mipLevelSizes", InstanceFlags).GetValue(info);
        var offsets = (Vector2Int[])type.GetField("mipLevelOffsets", InstanceFlags).GetValue(info);
        if (sizes == null || mipLevelCount <= 0 || textureSize.x <= 0 || textureSize.y <= 0)
            return false;

        mip = Mathf.Clamp(mip, 0, mipLevelCount - 1);
        minU = offsets[mip].x / (float)textureSize.x;
        minV = offsets[mip].y / (float)textureSize.y;
        maxU = (offsets[mip].x + sizes[mip].x) / (float)textureSize.x;
        maxV = (offsets[mip].y + sizes[mip].y) / (float)textureSize.y;
        return true;
    }

    static object GetHistory(ScreenSpaceLightingRendererFeature ssl, Camera camera)
    {
        var store = ssl.GetType().GetProperty("historyStore", InstanceFlags)?.GetValue(ssl);
        if (store == null)
            return null;

        var tryGet = store.GetType().GetMethod("TryGet", InstanceFlags);
        if (tryGet == null)
            return null;

        var arguments = new object[] { camera, 0, null };
        var found = tryGet.Invoke(store, arguments) is bool result && result;
        return found ? arguments[2] : null;
    }

    static T4HistorySnapshot SnapshotHistory(string label, object history)
    {
        return new T4HistorySnapshot
        {
            label = label,
            width = ReadInt(history, "width"),
            height = ReadInt(history, "height"),
            resetCount = ReadInt(history, "resetCount"),
            frameSerial = ReadInt(history, "frameSerial"),
            bufferIndex = ReadInt(history, "bufferIndex"),
            readSlot = ReadInt(history, "readSlot"),
            writeSlot = ReadInt(history, "writeSlot"),
            slices = ReadInt(history, "slices"),
            colorPyramidMipCount = ReadInt(history, "colorPyramidMipCount"),
            depthPyramidMipCount = ReadDepthPyramidMipCount(history),
            hasValidHistory = ReadBool(history, "hasValidHistory"),
            resourcesAllocated = ReadBool(history, "resourcesAllocated"),
            depthPyramidWidth = ReadDepthPyramidDimension(history, "width"),
            depthPyramidHeight = ReadDepthPyramidDimension(history, "height"),
        };
    }

    static int ReadDepthPyramidMipCount(object history)
    {
        var info = history?.GetType().GetProperty("depthPyramidInfo", InstanceFlags)?.GetValue(history);
        if (info == null)
            return 0;
        return (int)info.GetType().GetField("mipLevelCount").GetValue(info);
    }

    static int ReadDepthPyramidDimension(object history, string dimension)
    {
        var handle = history.GetType().GetProperty("depthPyramid", InstanceFlags)?.GetValue(history);
        if (handle == null)
            return 0;
        var rt = handle.GetType().GetProperty("rt", InstanceFlags)?.GetValue(handle) as RenderTexture;
        if (rt == null)
            return 0;
        return dimension == "width" ? rt.width : rt.height;
    }

    static T4StateCapture DescribeState(string stateName, Color[] samples, Color[] reference)
    {
        var capture = new T4StateCapture
        {
            state = stateName,
            sha256 = HashSamples(samples),
            magentaPixels = Count(samples, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f),
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

        capture.identicalToReference = capture.differingPixels == 0;
        return capture;
    }

    static void EnsureAssets(bool force)
    {
        if (SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before creating the T4 assets.");

        AssetDatabase.Refresh();
        for (var i = 0; i < ModeNames.Length; i++)
        {
            var renderer = EnsureRenderer(ModeNames[i], force);
            EnsureFeatureResources(renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().First());
            EnsurePipeline(ModeNames[i], renderer, force);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static UniversalRendererData EnsureRenderer(string modeName, bool force)
    {
        var t1Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T1RendererPathFormat, modeName));
        if (t1Renderer == null)
            throw new FileNotFoundException("Missing T1 renderer asset. Create the T1 baseline first.");

        var rendererPath = string.Format(T4RendererPathFormat, modeName);
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        if (renderer == null || force)
        {
            if (renderer != null)
                AssetDatabase.DeleteAsset(rendererPath);
            renderer = UnityEngine.Object.Instantiate(t1Renderer);
            renderer.name = "SslT4" + modeName + "Renderer";
            AssetDatabase.CreateAsset(renderer, rendererPath);
        }

        renderer.name = "SslT4" + modeName + "Renderer";
        var existingSsl = renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (existingSsl != null && renderer.rendererFeatures.Count == 1 && !force)
            return renderer;

        foreach (var existing in AssetDatabase.LoadAllAssetsAtPath(rendererPath))
        {
            if (existing is ScriptableRendererFeature)
                AssetDatabase.RemoveObjectFromAsset(existing);
        }

        renderer.rendererFeatures.Clear();
        var ssl = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
        ssl.name = "Screen Space Lighting";
        AssetDatabase.AddObjectToAsset(ssl, renderer);
        renderer.rendererFeatures.Add(ssl);
        EnsureFeatureMap(renderer, ssl);
        EditorUtility.SetDirty(renderer);
        renderer.SetDirty();
        return renderer;
    }

    static void EnsureFeatureMap(UniversalRendererData renderer, ScreenSpaceLightingRendererFeature ssl)
    {
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ssl, out _, out long sslId);
        var serialized = new SerializedObject(renderer);
        var map = serialized.FindProperty("m_RendererFeatureMap");
        if (map == null)
            return;
        map.arraySize = 1;
        map.GetArrayElementAtIndex(0).longValue = sslId;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(renderer);
    }

    static UniversalRenderPipelineAsset EnsurePipeline(string modeName, UniversalRendererData renderer, bool force)
    {
        var pipelinePath = string.Format(T4PipelinePathFormat, modeName);
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
            pipeline.name = "SslT4" + modeName + "Pipeline";
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

    static void EnsureFeatureResources(ScreenSpaceLightingRendererFeature feature)
    {
        var settings = feature.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
        if (settings == null)
            throw new InvalidOperationException("The SSL feature settings were not found.");
        var resourcesField = settings.GetType().GetField("resources", InstanceFlags);
        var resources = resourcesField?.GetValue(settings);
        if (resources == null)
            throw new InvalidOperationException("The SSL resource container was not found.");

        var locator = feature.GetType().Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceLightingResourceLocator");
        locator?.GetMethod("AssignDefaults", StaticFlags)?.Invoke(null, new[] { resources });

        // Fallback when the locator cannot resolve embedded package paths.
        var computeField = resources.GetType().GetField("depthPyramidCompute", InstanceFlags);
        if (computeField.GetValue(resources) == null)
            computeField.SetValue(resources, FindPackageAsset<ComputeShader>("DepthPyramid t:ComputeShader", "/ScreenSpaceCommon/DepthPyramid.compute"));
        var colorField = resources.GetType().GetField("colorPyramidShader", InstanceFlags);
        if (colorField.GetValue(resources) == null)
            colorField.SetValue(resources, FindPackageAsset<Shader>("ColorPyramid t:Shader", "/ScreenSpaceLighting/ColorPyramid.shader"));
        var debugField = resources.GetType().GetField("debugShader", InstanceFlags);
        if (debugField.GetValue(resources) == null)
            debugField.SetValue(resources, FindPackageAsset<Shader>("ScreenSpaceLightingDebug t:Shader", "/ScreenSpaceLighting/ScreenSpaceLightingDebug.shader"));

        var isComplete = (bool)resources.GetType().GetProperty("isComplete", InstanceFlags).GetValue(resources);
        if (!isComplete)
            throw new InvalidOperationException("Could not assign the Screen Space Lighting package resources.");

        EditorUtility.SetDirty(feature);
    }

    static T FindPackageAsset<T>(string filter, string pathSuffix) where T : UnityEngine.Object
    {
        foreach (var guid in AssetDatabase.FindAssets(filter, new[] { "Packages" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.Replace('\\', '/').EndsWith(pathSuffix, StringComparison.Ordinal))
                return AssetDatabase.LoadAssetAtPath<T>(path);
        }

        return null;
    }

    static void SetDebug(ScreenSpaceLightingRendererFeature feature, int mode, int mip)
    {
        var settings = feature.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
        if (settings == null)
            return;
        var modeField = settings.GetType().GetField("debugMode", InstanceFlags);
        var mipField = settings.GetType().GetField("debugMip", InstanceFlags);
        modeField?.SetValue(settings, Enum.ToObject(modeField.FieldType, mode));
        mipField?.SetValue(settings, mip);
    }

    static Type GetPluginType(string name)
    {
        var type = typeof(ScreenSpaceLightingRendererFeature).Assembly.GetType("UnityEngine.Rendering.Universal." + name);
        if (type == null)
            throw new TypeLoadException(name + " was not found.");
        return type;
    }

    static int ReadExecuteCount(Type passType)
    {
        return ReadStaticInt(passType, "s_EditorExecuteCount");
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
        return property != null && (bool)property.GetValue(instance);
    }

    static UniversalRenderPipelineAsset LoadPipeline(string format, string modeName)
    {
        var path = string.Format(format, modeName);
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
            throw new FileNotFoundException("Missing pipeline asset.", path);
        return pipeline;
    }

    static ScreenSpaceLightingRendererFeature FindSslFeature(UniversalRenderPipelineAsset pipeline)
    {
        var path = AssetDatabase.GetAssetPath(pipeline);
        var rendererPath = path.Replace("Pipeline.asset", "Renderer.asset");
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        return renderer?.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
    }

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
    sealed class T4Report
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public bool completed;
        public bool passed = true;
        public string error;
        public List<T4ModeResult> modes;
    }

    [Serializable]
    sealed class T4ModeResult
    {
        public string name;
        public bool passed;
        public List<string> issues;
        public int featureCount;
        public bool gtaoPresent;
        public int depthPyramidExecutions;
        public int currentColorPyramidExecutions;
        public int historyCaptureExecutions;
        public int depthCopyDispatches;
        public int entriesWithTemporaryCamera;
        public int entriesAfterCameraDestroy;
        public List<T4StateCapture> states;
        public List<T4HistorySnapshot> historyChecks;
        public List<T4DebugImage> debugImages;
    }

    [Serializable]
    sealed class T4StateCapture
    {
        public string state;
        public string sha256;
        public bool identicalToReference;
        public int differingPixels;
        public float maxChannelDelta;
        public int magentaPixels;
        public float meanLuminance;
    }

    [Serializable]
    sealed class T4HistorySnapshot
    {
        public string label;
        public int width;
        public int height;
        public int resetCount;
        public int frameSerial;
        public int bufferIndex;
        public int readSlot;
        public int writeSlot;
        public int slices;
        public int colorPyramidMipCount;
        public int depthPyramidMipCount;
        public int depthPyramidWidth;
        public int depthPyramidHeight;
        public bool hasValidHistory;
        public bool resourcesAllocated;
    }

    [Serializable]
    sealed class T4DebugImage
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
        public float minG;
        public float maxG;
    }
}