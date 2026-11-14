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

// T6 acceptance: the Screen Space Lighting feature produces screen-space global illumination in the
// Forward, Forward+ and Deferred renderers. The checks cover the dispatch chain, the validity weight
// contract (a miss keeps baked GI and never darkens the image), first frame and disocclusion
// fallbacks, half resolution plus bilateral upsample, camera cuts, the debug views and the
// consistency between the debug view and the texture materials consume.
public static class ScreenSpaceLightingT6Verification
{
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string OutputRoot = "PALN/validation/T6";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string T1RendererPathFormat = RendererRoot + "/Ssl{0}Renderer.asset";
    const string T1PipelinePathFormat = RendererRoot + "/Ssl{0}Pipeline.asset";
    const string T6RendererPathFormat = RendererRoot + "/SslT6{0}Renderer.asset";
    const string T6PipelinePathFormat = RendererRoot + "/SslT6{0}Pipeline.asset";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 32;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tests/Screen Space Lighting/Create T6 Assets")]
    public static void CreateT6Assets()
    {
        EnsureAssets(force: true);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [MenuItem("Tests/Screen Space Lighting/Run T6 Verification")]
    public static void RunT6Verification()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);
        var report = new T6Report
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            passed = true,
            modes = new List<T6ModeResult>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture imageTarget = null;
        RenderTexture smallTarget = null;
        RenderTexture referenceTarget = null;
        Volume volume = null;
        VolumeProfile originalProfile = null;
        VolumeProfile testProfile = null;
        GameObject bounceRoom = null;
        Material bounceMaterial = null;
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
            testProfile.name = originalProfile.name + " (T6)";
            volume.sharedProfile = testProfile;
            DisableOtherScreenSpaceEffects(testProfile);

            // The authored scene is open and most hemisphere rays escape to the sky. Add two static
            // bounce surfaces so on-screen screen-space hits are frequent enough to measure the
            // indirect contribution; both the reference and the SSGI captures use them.
            bounceRoom = CreateBounceRoom(out bounceMaterial);

            imageTarget = CreateTarget(ImageWidth, ImageHeight, "SslT6Image");
            smallTarget = CreateTarget(640, 360, "SslT6SmallImage");
            referenceTarget = CreateTarget(ImageWidth, ImageHeight, "SslT6Reference");
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
                    smallTarget,
                    referenceTarget,
                    projectRoot,
                    outputDirectory);
                report.modes.Add(mode);
                report.passed &= mode.passed;
                Debug.Log($"T6 {mode.name}: passed={mode.passed} issues={mode.issues.Count}");
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
            if (bounceRoom != null)
                UnityEngine.Object.DestroyImmediate(bounceRoom);
            if (bounceMaterial != null)
                UnityEngine.Object.DestroyImmediate(bounceMaterial);

            if (volume != null)
                volume.sharedProfile = originalProfile;
            if (testProfile != null)
                UnityEngine.Object.DestroyImmediate(testProfile);

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
            File.WriteAllText(Path.Combine(outputDirectory, "T6Verification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"T6 verification finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    static GameObject CreateBounceRoom(out Material material)
    {
        var root = new GameObject("T6 Bounce Room");
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            material = null;
            return root;
        }

        material = new Material(shader)
        {
            name = "T6 Bounce Surface",
            color = new Color(0.6f, 0.6f, 0.6f, 1f)
        };
        material.SetFloat("_Smoothness", 0.2f);
        material.SetFloat("_Metallic", 0f);

        AddBounceQuad(root, material, "T6 Back Wall", new Vector3(0f, 2f, 5.5f), new Vector3(16f, 4.5f, 0.2f));
        AddBounceQuad(root, material, "T6 Left Wall", new Vector3(-5.5f, 2f, 0f), new Vector3(0.2f, 4.5f, 12f));
        return root;
    }

    static void AddBounceQuad(GameObject parent, Material material, string name, Vector3 position, Vector3 size)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Cube);
        quad.name = name;
        quad.transform.SetParent(parent.transform, false);
        quad.transform.position = position;
        quad.transform.localScale = size;
        var renderer = quad.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = material;
    }

    static T6ModeResult CaptureMode(
        string modeName,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        VolumeProfile profile,
        RenderTexture imageTarget,
        RenderTexture smallTarget,
        RenderTexture referenceTarget,
        string projectRoot,
        string outputDirectory)
    {
        var result = new T6ModeResult
        {
            name = modeName,
            issues = new List<string>(),
            states = new List<T6StateCapture>(),
            historyChecks = new List<T6HistorySnapshot>(),
            debugImages = new List<T6DebugImage>()
        };

        var ssgi = profile.components.OfType<ScreenSpaceGlobalIllumination>().First();
        var pipeline = LoadPipeline(T6PipelinePathFormat, modeName);
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T6RendererPathFormat, modeName));
        if (renderer == null)
            throw new FileNotFoundException("Missing T6 renderer asset. Run Create T6 Assets first.");

        var feature = renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (feature == null)
            throw new InvalidOperationException("The T6 renderer is missing the Screen Space Lighting feature.");

        ApplyReferenceSettings(ssgi);

        // Reference: the same renderer and scene, SSGI volume disabled. The feature still enqueues
        // its depth and color pyramid passes, so this is the T4 debug-off baseline.
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        ssgi.enabled.Override(false);
        SetTarget(camera, referenceTarget);
        cameraData.resetHistory = true;
        var reference = RenderAndCapture(camera, referenceTarget, WarmupFrames);
        result.states.Add(DescribeState("Reference", reference, null));
        WriteImage(referenceTarget, outputDirectory, "T6_" + modeName + "_Reference.png");

        // SSGI on, full resolution, temporal accumulation and both denoise passes.
        ssgi.enabled.Override(true);
        SetTarget(camera, imageTarget);
        cameraData.resetHistory = true;

        var passType = GetPluginType("ScreenSpaceLightingSsgiPass");
        var setupType = GetPluginType("ScreenSpaceLightingSetupPass");
        int executions = ReadStaticInt(passType, "s_EditorExecuteCount");
        int trace = ReadStaticInt(passType, "s_EditorTraceDispatchCount");
        int reproject = ReadStaticInt(passType, "s_EditorReprojectDispatchCount");
        int validation = ReadStaticInt(passType, "s_EditorValidationDispatchCount");
        int accumulation = ReadStaticInt(passType, "s_EditorAccumulationDispatchCount");
        int diffuse = ReadStaticInt(passType, "s_EditorDiffuseDispatchCount");
        int upsample = ReadStaticInt(passType, "s_EditorUpsampleDispatchCount");
        int setupExecutions = ReadStaticInt(setupType, "s_EditorExecuteCount");
        int boundFrames = ReadStaticInt(setupType, "s_EditorSsgiBoundCount");

        var fullSamples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        var fullState = DescribeState("SsgiFullRes", fullSamples, reference);
        result.states.Add(fullState);
        WriteImage(imageTarget, outputDirectory, "T6_" + modeName + "_SsgiFullRes.png");

        result.ssgiExecutions = ReadStaticInt(passType, "s_EditorExecuteCount") - executions;
        result.traceDispatches = ReadStaticInt(passType, "s_EditorTraceDispatchCount") - trace;
        result.reprojectDispatches = ReadStaticInt(passType, "s_EditorReprojectDispatchCount") - reproject;
        result.validationDispatches = ReadStaticInt(passType, "s_EditorValidationDispatchCount") - validation;
        result.accumulationDispatches = ReadStaticInt(passType, "s_EditorAccumulationDispatchCount") - accumulation;
        result.diffuseDispatches = ReadStaticInt(passType, "s_EditorDiffuseDispatchCount") - diffuse;
        result.upsampleDispatches = ReadStaticInt(passType, "s_EditorUpsampleDispatchCount") - upsample;
        result.setupExecutions = ReadStaticInt(setupType, "s_EditorExecuteCount") - setupExecutions;
        result.ssgiBoundFrames = ReadStaticInt(setupType, "s_EditorSsgiBoundCount") - boundFrames;

        if (result.ssgiExecutions <= 0)
            result.issues.Add("SsgiFullRes: the SSGI pass never executed.");
        if (result.traceDispatches <= 0 || result.reprojectDispatches <= 0)
            result.issues.Add("SsgiFullRes: the trace or reproject kernel was never dispatched.");
        if (result.validationDispatches <= 0 || result.accumulationDispatches <= 0)
            result.issues.Add("SsgiFullRes: the temporal validation or accumulation was never dispatched.");
        if (result.diffuseDispatches <= 0)
            result.issues.Add("SsgiFullRes: the diffuse denoiser was never dispatched.");
        if (result.upsampleDispatches != 0)
            result.issues.Add("SsgiFullRes: full resolution ran the half resolution upsample.");
        if (result.ssgiBoundFrames <= 0)
            result.issues.Add("SsgiFullRes: the setup pass never bound the SSGI texture and keyword.");
        if (!fullState.differsFromReference)
            result.issues.Add("SsgiFullRes: the SSGI contribution did not change the image.");
        if (fullState.magentaPixels > 0 || fullState.nonFinitePixels > 0)
            result.issues.Add("SsgiFullRes: the image contains invalid pixels.");

        var history = GetHistory(feature, camera);
        if (history == null)
        {
            result.issues.Add("SsgiFullRes: the camera did not receive a Screen Space Lighting history entry.");
        }
        else
        {
            var snapshot = SnapshotHistory("AfterFullResWarmup", history);
            result.historyChecks.Add(snapshot);
            if (!snapshot.ssgiResourcesAllocated || !snapshot.ssgiHasValidHistory)
                result.issues.Add("SsgiFullRes: the SSGI resources or history never became valid.");
            if (snapshot.ssgiReadSlot == snapshot.ssgiWriteSlot)
                result.issues.Add("SsgiFullRes: the frame read and wrote the same SSGI history slot.");
            if (snapshot.ssgiTraceWidth != ImageWidth || snapshot.ssgiTraceHeight != ImageHeight)
                result.issues.Add($"SsgiFullRes: the trace buffer is {snapshot.ssgiTraceWidth}x{snapshot.ssgiTraceHeight}, expected {ImageWidth}x{ImageHeight}.");
        }

        var finalHandle = GetFinalTexture(feature);
        var finalPixels = ReadRTHandle(finalHandle);
        result.weight = DescribeWeight(finalPixels);
        DescribeIntermediateResources(history, result);
        result.finalTextureBytes = DescribeTextureBytes(finalHandle);
        if (result.weight.nonFinitePixels > 0)
            result.issues.Add("SsgiFullRes: the SSGI output texture contains NaN or Inf.");
        if (result.weight.coveredFraction < 0.001f)
            result.issues.Add($"SsgiFullRes: only {result.weight.coveredFraction:P2} of pixels received screen-space GI.");
        if (result.weight.missFraction < 0.01f)
            result.issues.Add("SsgiFullRes: no pixel fell back to baked GI; the validity weight is not used.");
        if (result.weight.meanWeight <= 0f)
            result.issues.Add("SsgiFullRes: the validity weight is zero everywhere.");

        CheckMissKeepsBakedGI(reference, fullSamples, finalPixels, result);
        CheckEmissionIsNotDoubled(camera, reference, fullSamples, result);
        CaptureDebugViews(camera, imageTarget, feature, finalPixels, result, outputDirectory, modeName);
        CheckHalfResolution(camera, imageTarget, feature, ssgi, reference, result, outputDirectory, modeName);
        CheckIntensityZero(camera, imageTarget, reference, ssgi, result);
        CheckFirstFrameFallback(camera, imageTarget, cameraData, reference, result);
        CheckCameraCut(camera, cameraData, imageTarget, feature, reference, result);

        // Restore deterministic settings for the next run and for the asset on disk.
        ApplyReferenceSettings(ssgi);
        result.passed = result.issues.Count == 0;
        return result;
    }

    static void DescribeIntermediateResources(object history, T6ModeResult result)
    {
        if (history == null)
            return;

        var raw = ReadHistoryHandle(history, "ssgiProjection");
        result.rawWeight = DescribeWeight(raw);

        var accumulated = ReadHistoryHandle(history, "ssgiAccumulation");
        result.accumulationWeight = DescribeWeight(accumulated);

        var hitPoints = ReadHistoryHandle(history, "ssgiHitPoint");
        if (hitPoints != null)
        {
            result.hitPointPixels = hitPoints.Length;
            foreach (var pixel in hitPoints)
            {
                if (pixel.r <= 1f && IsFinite(pixel.r))
                    result.hitPointHits++;
            }
        }

        var validation = ReadHistoryHandle(history, "ssgiValidation");
        if (validation != null)
        {
            result.validationPixels = validation.Length;
            foreach (var pixel in validation)
            {
                if (pixel.r < 0.5f && IsFinite(pixel.r))
                    result.validationValidPixels++;
            }
        }
    }

    static Color[] ReadHistoryHandle(object history, string name)
    {
        var handle = history.GetType().GetProperty(name, InstanceFlags)?.GetValue(history);
        return ReadRTHandle(handle);
    }

    static void CheckMissKeepsBakedGI(Color[] reference, Color[] ssgi, Color[] weightPixels, T6ModeResult result)
    {
        if (reference == null || ssgi == null || weightPixels == null ||
            reference.Length != ssgi.Length || weightPixels.Length != ssgi.Length)
        {
            result.issues.Add("MissKeepsBakedGI: the captures use different resolutions.");
            return;
        }

        var compared = 0;
        var different = 0;
        var maxDelta = 0f;
        for (var i = 0; i < ssgi.Length; i++)
        {
            // A weight below the quantization step of the filtered RGBA16F output means the material
            // lerp keeps bakedGI; those pixels must be bit identical to the SSGI-disabled image.
            if (weightPixels[i].a > 1e-6f)
                continue;

            compared++;
            var delta = Mathf.Max(
                Mathf.Abs(ssgi[i].r - reference[i].r),
                Mathf.Max(Mathf.Abs(ssgi[i].g - reference[i].g), Mathf.Abs(ssgi[i].b - reference[i].b)));
            if (delta > 1e-5f)
                different++;
            maxDelta = Mathf.Max(maxDelta, delta);
        }

        result.missPixelsCompared = compared;
        result.missPixelsDifferent = different;
        result.missMaxChannelDelta = maxDelta;
        if (compared == 0)
            result.issues.Add("MissKeepsBakedGI: no pixel had zero weight.");
        if (different > 0)
            result.issues.Add($"MissKeepsBakedGI: {different} zero weight pixels differ from baked GI (max delta {maxDelta}).");
    }

    static void CheckEmissionIsNotDoubled(Camera camera, Color[] reference, Color[] ssgi, T6ModeResult result)
    {
        var emissive = GameObject.Find("Emission Cube");
        if (emissive == null)
            return;

        var emissiveRenderer = emissive.GetComponent<Renderer>();
        if (emissiveRenderer == null || reference == null || ssgi == null || reference.Length != ssgi.Length)
            return;

        var bounds = emissiveRenderer.bounds;
        var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, 0f);
        var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, 0f);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? bounds.min.x : bounds.max.x,
                (i & 2) == 0 ? bounds.min.y : bounds.max.y,
                (i & 4) == 0 ? bounds.min.z : bounds.max.z);
            var screen = camera.WorldToScreenPoint(corner);
            min = Vector3.Min(min, screen);
            max = Vector3.Max(max, screen);
        }

        // Inset the rectangle so the measurement only covers the emissive surface, not its outline.
        float insetX = (max.x - min.x) * 0.25f;
        float insetY = (max.y - min.y) * 0.25f;
        int x0 = Mathf.Clamp(Mathf.RoundToInt(min.x + insetX), 0, ImageWidth - 1);
        int x1 = Mathf.Clamp(Mathf.RoundToInt(max.x - insetX), x0 + 1, ImageWidth);
        int y0 = Mathf.Clamp(Mathf.RoundToInt(min.y + insetY), 0, ImageHeight - 1);
        int y1 = Mathf.Clamp(Mathf.RoundToInt(max.y - insetY), y0 + 1, ImageHeight);

        double referenceSum = 0.0;
        double ssgiSum = 0.0;
        int counted = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int index = y * ImageWidth + x;
                referenceSum += Luminance(reference[index]);
                ssgiSum += Luminance(ssgi[index]);
                counted++;
            }
        }

        if (counted == 0)
            return;

        float ratio = referenceSum > 1e-9 ? (float)(ssgiSum / referenceSum) : 1f;
        result.emissivePixelCount = counted;
        result.emissiveLuminanceRatio = ratio;
        // Emission is not part of the indirect diffuse term, so the emitter's own surface must stay
        // close to the reference. A duplicated emission would roughly double it.
        if (ratio > 1.35f)
            result.issues.Add($"EmissionIsNotDoubled: the emissive surface brightened by {ratio:F2}x; emission may be added twice.");
        if (ratio < 0.65f)
            result.issues.Add($"EmissionIsNotDoubled: the emissive surface darkened to {ratio:F2}x; the SSGI blend replaced the emission.");
    }

    static void CaptureDebugViews(
        Camera camera,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        Color[] consumedPixels,
        T6ModeResult result,
        string outputDirectory,
        string modeName)
    {
        CaptureDebugView(camera, imageTarget, feature, 7, "SsgiRaw", result, outputDirectory, modeName);
        var denoised = CaptureDebugView(camera, imageTarget, feature, 8, "SsgiDenoised", result, outputDirectory, modeName);
        CaptureDebugView(camera, imageTarget, feature, 9, "SsgiHistory", result, outputDirectory, modeName);
        CaptureDebugView(camera, imageTarget, feature, 10, "SsgiWeight", result, outputDirectory, modeName);
        SetSslDebug(feature, 0, 0);

        // The debug view samples exactly the texture that was bound to materials. The final frame of
        // the SsgiDenoised view rendered that same texture, so both captures describe one frame.
        var finalHandle = GetFinalTexture(feature);
        var finalPixels = ReadRTHandle(finalHandle);
        if (denoised != null && finalPixels != null && finalPixels.Length == denoised.Length)
        {
            float maxDelta = 0f;
            int compared = 0;
            int mismatch = 0;
            for (int i = 0; i < finalPixels.Length; i++)
            {
                // The debug view returns rgb with alpha forced to 1; compare rgb only.
                float delta = Mathf.Max(
                    Mathf.Abs(finalPixels[i].r - denoised[i].r),
                    Mathf.Max(Mathf.Abs(finalPixels[i].g - denoised[i].g), Mathf.Abs(finalPixels[i].b - denoised[i].b)));
                if (delta > 1e-3f)
                    mismatch++;
                maxDelta = Mathf.Max(maxDelta, delta);
                compared++;
            }

            result.debugConsumptionPixels = compared;
            result.debugConsumptionMismatchPixels = mismatch;
            result.debugConsumptionMaxDelta = maxDelta;
            if (mismatch > compared / 100)
                result.issues.Add($"DebugViewConsistency: {mismatch} pixels of the debug view differ from the consumed SSGI texture.");
        }

        if (consumedPixels != null && finalPixels != null && consumedPixels.Length == finalPixels.Length)
        {
            double consumedMean = 0.0;
            double finalMean = 0.0;
            for (int i = 0; i < finalPixels.Length; i++)
            {
                consumedMean += Luminance(consumedPixels[i]);
                finalMean += Luminance(finalPixels[i]);
            }

            result.consumedLuminance = (float)(consumedMean / finalPixels.Length);
            result.finalLuminance = (float)(finalMean / finalPixels.Length);
        }
    }

    static Color[] CaptureDebugView(
        Camera camera,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        int debugMode,
        string viewName,
        T6ModeResult result,
        string outputDirectory,
        string modeName)
    {
        SetSslDebug(feature, debugMode, 0);
        RenderAndCapture(camera, imageTarget, 2);
        var samples = ReadPixels(imageTarget);
        var info = new T6DebugImage
        {
            view = viewName,
            file = "T6_" + modeName + "_" + viewName + ".png",
            sha256 = HashSamples(samples),
            magentaPixels = Count(samples, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f),
            nonFinitePixels = Count(samples, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)),
            meanLuminance = Mean(samples, Luminance),
            minLuminance = Min(samples, Luminance),
            maxLuminance = Max(samples, Luminance)
        };
        result.debugImages.Add(info);
        WriteImage(imageTarget, outputDirectory, info.file);

        if (info.magentaPixels > 0 || info.nonFinitePixels > 0)
            result.issues.Add(viewName + ": the debug view contains invalid pixels.");
        if (info.meanLuminance <= 0f)
            result.issues.Add(viewName + ": the debug view is completely black.");
        return samples;
    }

    static void CheckHalfResolution(
        Camera camera,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        ScreenSpaceGlobalIllumination ssgi,
        Color[] reference,
        T6ModeResult result,
        string outputDirectory,
        string modeName)
    {
        ssgi.fullResolution.Override(false);
        ssgi.bilateralUpsample.Override(true);
        camera.GetComponent<UniversalAdditionalCameraData>().resetHistory = true;

        var passType = GetPluginType("ScreenSpaceLightingSsgiPass");
        int upsampleBefore = ReadStaticInt(passType, "s_EditorUpsampleDispatchCount");
        var samples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        int upsampleAfter = ReadStaticInt(passType, "s_EditorUpsampleDispatchCount");
        result.halfResUpsampleDispatches = upsampleAfter - upsampleBefore;

        var state = DescribeState("SsgiHalfRes", samples, reference);
        result.states.Add(state);
        WriteImage(imageTarget, outputDirectory, "T6_" + modeName + "_SsgiHalfRes.png");
        if (state.magentaPixels > 0 || state.nonFinitePixels > 0)
            result.issues.Add("SsgiHalfRes: the image contains invalid pixels.");
        if (!state.differsFromReference)
            result.issues.Add("SsgiHalfRes: the half resolution contribution did not change the image.");
        if (result.halfResUpsampleDispatches <= 0)
            result.issues.Add("SsgiHalfRes: the bilateral upsample was never dispatched.");

        var history = GetHistory(feature, camera);
        if (history != null)
        {
            var snapshot = SnapshotHistory("AfterHalfResWarmup", history);
            result.historyChecks.Add(snapshot);
            if (snapshot.ssgiTraceWidth != ImageWidth / 2 || snapshot.ssgiTraceHeight != ImageHeight / 2)
                result.issues.Add($"SsgiHalfRes: the trace buffer is {snapshot.ssgiTraceWidth}x{snapshot.ssgiTraceHeight}, expected {ImageWidth / 2}x{ImageHeight / 2}.");
        }

        ssgi.fullResolution.Override(true);
        ssgi.bilateralUpsample.Override(true);
    }

    static void CheckIntensityZero(
        Camera camera,
        RenderTexture imageTarget,
        Color[] reference,
        ScreenSpaceGlobalIllumination settings,
        T6ModeResult result)
    {
        settings.intensity.Override(0f);
        camera.GetComponent<UniversalAdditionalCameraData>().resetHistory = true;

        var setupType = GetPluginType("ScreenSpaceLightingSetupPass");
        int boundBefore = ReadStaticInt(setupType, "s_EditorSsgiBoundCount");
        var samples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        int boundAfter = ReadStaticInt(setupType, "s_EditorSsgiBoundCount");

        var state = DescribeState("ZeroIntensityRestoresBaseline", samples, reference);
        result.states.Add(state);
        if (!state.identicalToReference)
            result.issues.Add($"ZeroIntensityRestoresBaseline: {state.differingPixels} pixels changed (max delta {state.maxChannelDelta}).");
        if (boundAfter <= boundBefore)
            result.issues.Add("ZeroIntensityRestoresBaseline: the SSGI texture was not bound, so the weight path was not exercised.");

        settings.intensity.Override(1f);
    }

    static void CheckFirstFrameFallback(
        Camera camera,
        RenderTexture imageTarget,
        UniversalAdditionalCameraData cameraData,
        Color[] reference,
        T6ModeResult result)
    {
        cameraData.resetHistory = true;
        var samples = RenderAndCapture(camera, imageTarget, 1);
        var state = DescribeState("FirstFrameAfterReset", samples, reference);
        result.states.Add(state);
        // Without a previous color pyramid the SSGI pass must skip and leave the baked GI untouched.
        if (!state.identicalToReference)
            result.issues.Add($"FirstFrameAfterReset: {state.differingPixels} pixels differ from baked GI (max delta {state.maxChannelDelta}).");

        RenderAndCapture(camera, imageTarget, WarmupFrames);
    }

    static void CheckCameraCut(
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        Color[] reference,
        T6ModeResult result)
    {
        var history = GetHistory(feature, camera);
        int resetCountBefore = history != null ? ReadInt(history, "resetCount") : 0;

        var previousPosition = camera.transform.position;
        camera.transform.position = previousPosition + new Vector3(2.5f, 0.5f, 0f);
        cameraData.resetHistory = true;
        var samples = RenderAndCapture(camera, imageTarget, WarmupFrames);
        camera.transform.position = previousPosition;

        var state = DescribeState("CameraCut", samples, reference);
        result.states.Add(state);
        result.cameraCutResetCount = history != null ? ReadInt(history, "resetCount") - resetCountBefore : 0;

        if (state.nonFinitePixels > 0 || state.magentaPixels > 0)
            result.issues.Add("CameraCut: the image contains invalid pixels.");
        if (result.cameraCutResetCount <= 0)
            result.issues.Add("CameraCut: the SSGI history was not invalidated by the camera reset.");

        // Return to the original view and let the history rebuild before the next check.
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, WarmupFrames);
    }

    // ---------------------------------------------------------------------------------------------
    // Asset creation
    // ---------------------------------------------------------------------------------------------

    static void EnsureAssets(bool force)
    {
        if (SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before creating the T6 assets.");

        AssetDatabase.Refresh();
        for (var i = 0; i < ModeNames.Length; i++)
        {
            var mode = ModeNames[i];
            var renderer = EnsureRenderer(
                string.Format(T6RendererPathFormat, mode),
                "SslT6" + mode + "Renderer",
                force,
                out bool recreated);

            EnsureFeatureResources(renderer);
            EnsurePipeline(string.Format(T6PipelinePathFormat, mode), "SslT6" + mode + "Pipeline", renderer, force || recreated);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static UniversalRendererData EnsureRenderer(string rendererPath, string rendererName, bool force, out bool recreated)
    {
        recreated = false;
        var t1Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(GetT1RendererPath(rendererName));
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

    static string GetT1RendererPath(string rendererName)
    {
        return RendererRoot + "/" + rendererName.Replace("SslT6", "Ssl").Replace("Renderer", "Renderer") + ".asset";
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
    // Settings and reflection helpers
    // ---------------------------------------------------------------------------------------------

    static void DisableOtherScreenSpaceEffects(VolumeProfile profile)
    {
        var ssr = profile.components.OfType<ScreenSpaceReflection>().FirstOrDefault();
        if (ssr != null)
            ssr.enabled.Override(false);
        var refraction = profile.components.OfType<ScreenSpaceRefraction>().FirstOrDefault();
        if (refraction != null)
            refraction.enabled.Override(false);
    }

    static void ApplyReferenceSettings(ScreenSpaceGlobalIllumination settings)
    {
        settings.enabled.Override(true);
        settings.fullResolution.Override(true);
        settings.maxRaySteps.Override(32);
        settings.depthBufferThickness.Override(0.1f);
        settings.intensity.Override(1f);
        settings.temporalAccumulation.Override(true);
        settings.denoise.Override(true);
        settings.denoiserRadius.Override(0.6f);
        settings.secondDenoiserPass.Override(true);
        settings.halfResolutionDenoiser.Override(false);
        settings.bilateralUpsample.Override(true);
    }

    static Type GetPluginType(string name)
    {
        var type = typeof(ScreenSpaceLightingRendererFeature).Assembly.GetType("UnityEngine.Rendering.Universal." + name);
        if (type == null)
            throw new TypeLoadException(name + " was not found.");
        return type;
    }

    static int ReadStaticInt(Type type, string name)
    {
        var field = type.GetField(name, StaticFlags);
        return field == null ? -1 : (int)field.GetValue(null);
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

    static object GetSsgiPass(ScreenSpaceLightingRendererFeature feature)
    {
        return feature.GetType().GetProperty("ssgiPass", InstanceFlags)?.GetValue(feature);
    }

    static object GetFinalTexture(ScreenSpaceLightingRendererFeature feature)
    {
        var pass = GetSsgiPass(feature);
        return pass?.GetType().GetProperty("finalTexture", InstanceFlags)?.GetValue(pass);
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

    static T6HistorySnapshot SnapshotHistory(string label, object history)
    {
        return new T6HistorySnapshot
        {
            label = label,
            width = ReadInt(history, "width"),
            height = ReadInt(history, "height"),
            resetCount = ReadInt(history, "resetCount"),
            validFrameCount = ReadInt(history, "validFrameCount"),
            readSlot = ReadInt(history, "readSlot"),
            writeSlot = ReadInt(history, "writeSlot"),
            ssgiReadSlot = ReadInt(history, "ssgiReadSlot"),
            ssgiWriteSlot = ReadInt(history, "ssgiWriteSlot"),
            ssgiBufferIndex = ReadInt(history, "ssgiBufferIndex"),
            ssgiTraceWidth = ReadInt(history, "ssgiTraceWidth"),
            ssgiTraceHeight = ReadInt(history, "ssgiTraceHeight"),
            ssgiResourceGeneration = ReadInt(history, "ssgiResourceGeneration"),
            ssgiHasValidHistory = ReadBool(history, "ssgiHasValidHistory"),
            ssgiResourcesAllocated = ReadBool(history, "ssgiResourcesAllocated"),
        };
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

    static T6TextureBytes DescribeTextureBytes(object handle)
    {
        var rt = handle?.GetType().GetProperty("rt", InstanceFlags)?.GetValue(handle) as RenderTexture;
        if (rt == null)
            return new T6TextureBytes();
        return new T6TextureBytes
        {
            width = rt.width,
            height = rt.height,
            volumeDepth = rt.volumeDepth,
            bytes = (long)rt.width * rt.height * 8L * Math.Max(1, rt.volumeDepth)
        };
    }

    static T6WeightStats DescribeWeight(Color[] samples)
    {
        var stats = new T6WeightStats();
        if (samples == null)
            return stats;

        double sum = 0.0;
        foreach (var pixel in samples)
        {
            var weight = pixel.a;
            if (!IsFinite(weight))
            {
                stats.nonFinitePixels++;
                continue;
            }

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

    static T6StateCapture DescribeState(string stateName, Color[] samples, Color[] reference)
    {
        var capture = new T6StateCapture
        {
            state = stateName,
            sha256 = HashSamples(samples),
            magentaPixels = Count(samples, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f),
            nonFinitePixels = Count(samples, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)),
            meanLuminance = Mean(samples, Luminance)
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

    // ---------------------------------------------------------------------------------------------
    // Report data
    // ---------------------------------------------------------------------------------------------

    [Serializable]
    public sealed class T6Report
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public bool completed;
        public bool passed;
        public string error;
        public List<T6ModeResult> modes;
    }

    [Serializable]
    public sealed class T6ModeResult
    {
        public string name;
        public bool passed;
        public List<string> issues;
        public int ssgiExecutions;
        public int traceDispatches;
        public int reprojectDispatches;
        public int validationDispatches;
        public int accumulationDispatches;
        public int diffuseDispatches;
        public int upsampleDispatches;
        public int setupExecutions;
        public int ssgiBoundFrames;
        public int halfResUpsampleDispatches;
        public int cameraCutResetCount;
        public int missPixelsCompared;
        public int missPixelsDifferent;
        public float missMaxChannelDelta;
        public int emissivePixelCount;
        public float emissiveLuminanceRatio;
        public int debugConsumptionPixels;
        public int debugConsumptionMismatchPixels;
        public float debugConsumptionMaxDelta;
        public float consumedLuminance;
        public float finalLuminance;
        public T6WeightStats weight = new T6WeightStats();
        public T6WeightStats rawWeight = new T6WeightStats();
        public T6WeightStats accumulationWeight = new T6WeightStats();
        public int hitPointPixels;
        public int hitPointHits;
        public int validationPixels;
        public int validationValidPixels;
        public T6TextureBytes finalTextureBytes = new T6TextureBytes();
        public List<T6StateCapture> states;
        public List<T6HistorySnapshot> historyChecks;
        public List<T6DebugImage> debugImages;
    }

    [Serializable]
    public sealed class T6StateCapture
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
    public sealed class T6HistorySnapshot
    {
        public string label;
        public int width;
        public int height;
        public int resetCount;
        public int validFrameCount;
        public int readSlot;
        public int writeSlot;
        public int ssgiReadSlot;
        public int ssgiWriteSlot;
        public int ssgiBufferIndex;
        public int ssgiTraceWidth;
        public int ssgiTraceHeight;
        public int ssgiResourceGeneration;
        public bool ssgiHasValidHistory;
        public bool ssgiResourcesAllocated;
    }

    [Serializable]
    public sealed class T6DebugImage
    {
        public string view;
        public string file;
        public string sha256;
        public int magentaPixels;
        public int nonFinitePixels;
        public float meanLuminance;
        public float minLuminance;
        public float maxLuminance;
    }

    [Serializable]
    public sealed class T6WeightStats
    {
        public int pixelCount;
        public int nonFinitePixels;
        public int coveredPixels;
        public int partialPixels;
        public int missPixels;
        public float meanWeight;
        public float coveredFraction;
        public float partialFraction;
        public float missFraction;
    }

    [Serializable]
    public sealed class T6TextureBytes
    {
        public int width;
        public int height;
        public int volumeDepth;
        public long bytes;
    }
}