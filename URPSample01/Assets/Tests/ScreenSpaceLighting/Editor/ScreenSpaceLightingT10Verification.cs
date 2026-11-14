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

// T10 acceptance: transparent prepass and Transparent SSR.
//
// The checks cover:
// - ray origin: the private transparent depth is nearer than the opaque depth at opted-in objects
//   and equal everywhere else, and two overlapping transparent layers resolve to the nearest one;
// - independent output: opaque SSR weight is byte identical with transparent SSR on and off, and
//   only opted-in transparent regions change;
// - no history misuse: the transparent pass always uses Approximation and never dispatches an
//   accumulation kernel, even when the opaque SSR algorithm is PBRAccumulation;
// - main transparent depth semantics: the opaque camera depth is byte identical before and after
//   the transparent prepass runs;
// - opt-in contract: materials without Receive Transparent SSR keep the reflection probe, and
//   unsupported transparent shaders never enter the prepass;
// - transparent object motion is not exported to the motion vector texture; the limitation is
//   measured instead of being hidden.
public static class ScreenSpaceLightingT10Verification
{
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string MaterialRoot = "Assets/Tests/ScreenSpaceLighting/Materials";
    const string OutputRoot = "PALN/validation/T10";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string T1RendererPathFormat = RendererRoot + "/Ssl{0}Renderer.asset";
    const string T10RendererPathFormat = RendererRoot + "/SslT10{0}Renderer.asset";
    const string T10PipelinePathFormat = RendererRoot + "/SslT10{0}Pipeline.asset";
    const string ReceiverMaterialPath = MaterialRoot + "/T10TransparentReceiver.mat";
    const string OptOutMaterialPath = MaterialRoot + "/T10TransparentNoReceiver.mat";
    const string UnsupportedMaterialPath = MaterialRoot + "/T10TransparentSimpleLit.mat";
    const string CoatMaterialPath = MaterialRoot + "/T10TransparentComplexLit.mat";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 16;
    const int PbrProbeFrames = 4;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tests/Screen Space Lighting/Create T10 Assets")]
    public static void CreateT10Assets()
    {
        EnsureAssets(force: true);
        EnsureMaterialAssets();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [MenuItem("Tests/Screen Space Lighting/Run T10 Verification")]
    public static void RunT10Verification()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);
        var report = new T10Report
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            passed = true,
            modes = new List<T10ModeResult>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture imageTarget = null;
        Volume volume = null;
        VolumeProfile originalProfile = null;
        VolumeProfile testProfile = null;
        GameObject rig = null;
        T10Rig rigParts = null;
        List<Material> rigMaterials = new List<Material>();
        try
        {
            EnsureAssets(force: false);
            EnsureMaterialAssets();
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
            testProfile.name = originalProfile.name + " (T10)";
            volume.sharedProfile = testProfile;
            DisableScreenSpaceEffects(testProfile);

            rigParts = CreateTransparentRig(camera, rigMaterials);
            rig = rigParts.root;

            imageTarget = CreateTarget(ImageWidth, ImageHeight, "SslT10Image");
            camera.targetTexture = imageTarget;
            camera.aspect = (float)ImageWidth / ImageHeight;
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;
            cameraData.resetHistory = true;

            for (int modeIndex = 0; modeIndex < ModeNames.Length; modeIndex++)
            {
                var mode = CaptureMode(ModeNames[modeIndex], camera, cameraData, testProfile, imageTarget, rigParts,
                    outputDirectory);
                report.modes.Add(mode);
                report.passed &= mode.passed;
                Debug.Log($"T10 {mode.name}: passed={mode.passed} issues={mode.issues.Count}");
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
            foreach (var material in rigMaterials)
            {
                if (material != null)
                    UnityEngine.Object.DestroyImmediate(material);
            }
            if (rig != null)
                UnityEngine.Object.DestroyImmediate(rig);

            if (volume != null)
                volume.sharedProfile = originalProfile;
            if (testProfile != null)
                UnityEngine.Object.DestroyImmediate(testProfile);
            if (imageTarget != null)
            {
                imageTarget.Release();
                UnityEngine.Object.DestroyImmediate(imageTarget);
            }

            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
            AssetDatabase.SaveAssets();
            File.WriteAllText(Path.Combine(outputDirectory, "T10Verification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"T10 verification finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    // ---------------------------------------------------------------------------------------------
    // Per renderer Transparent SSR coverage
    // ---------------------------------------------------------------------------------------------

    static T10ModeResult CaptureMode(
        string modeName,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        VolumeProfile profile,
        RenderTexture imageTarget,
        T10Rig rigParts,
        string outputDirectory)
    {
        var result = new T10ModeResult
        {
            name = modeName,
            issues = new List<string>(),
            states = new List<T10StateCapture>(),
        };

        var ssr = profile.components.OfType<ScreenSpaceReflection>().First();
        var pipeline = LoadPipeline(T10PipelinePathFormat, modeName);
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T10RendererPathFormat, modeName));
        if (renderer == null)
            throw new FileNotFoundException("Missing T10 renderer asset. Run Create T10 Assets first.");
        var feature = renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (feature == null)
            throw new InvalidOperationException("The T10 renderer is missing the Screen Space Lighting feature.");

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        EnsureSupportedEffectsOnly(profile);
        ApplyBaseSsrSettings(ssr, minSmoothness: 0.4f);
        rigParts.ResetVisibility();

        // -----------------------------------------------------------------------------------------
        // Probe-only reference. Screen-space effects are off.
        // -----------------------------------------------------------------------------------------
        ssr.enabled.Override(false);
        ssr.enabledTransparent.Override(false);
        cameraData.resetHistory = true;
        var reference = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("Reference", reference, null));
        WriteImage(imageTarget, outputDirectory, "T10_" + modeName + "_Reference.png");

        // -----------------------------------------------------------------------------------------
        // Opaque SSR only. This is the baseline the transparent states must not disturb.
        // -----------------------------------------------------------------------------------------
        ssr.enabled.Override(true);
        ssr.intensity.Override(1f);
        ssr.enabledTransparent.Override(false);
        cameraData.resetHistory = true;
        var opaqueOnly = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("OpaqueOnly", opaqueOnly, reference));
        WriteImage(imageTarget, outputDirectory, "T10_" + modeName + "_OpaqueOnly.png");
        var opaqueWeight = CaptureDebug(camera, imageTarget, feature, ScreenSpaceLightingDebugMode.SsrWeight,
            outputDirectory, "T10_" + modeName + "_SsrWeight.png");
        var opaqueCameraDepth = CaptureDebug(camera, imageTarget, feature, ScreenSpaceLightingDebugMode.CameraDepth,
            outputDirectory, "T10_" + modeName + "_CameraDepth.png");

        // -----------------------------------------------------------------------------------------
        // Transparent SSR on.
        // -----------------------------------------------------------------------------------------
        ssr.enabledTransparent.Override(true);
        cameraData.resetHistory = true;
        var transparentOn = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("TransparentOn", transparentOn, opaqueOnly));
        WriteImage(imageTarget, outputDirectory, "T10_" + modeName + "_TransparentOn.png");
        var transparentWeight = CaptureDebug(camera, imageTarget, feature,
            ScreenSpaceLightingDebugMode.SsrTransparentWeight, outputDirectory,
            "T10_" + modeName + "_SsrTransparentWeight.png");
        CaptureDebug(camera, imageTarget, feature, ScreenSpaceLightingDebugMode.SsrTransparentRaw, outputDirectory,
            "T10_" + modeName + "_SsrTransparentRaw.png");
        var transparentDepth = CaptureDebug(camera, imageTarget, feature, ScreenSpaceLightingDebugMode.TransparentDepth,
            outputDirectory, "T10_" + modeName + "_TransparentDepth.png");
        var transparentCameraDepth = CaptureDebug(camera, imageTarget, feature, ScreenSpaceLightingDebugMode.CameraDepth,
            outputDirectory, "T10_" + modeName + "_CameraDepthWithTransparentPrepass.png");

        // -----------------------------------------------------------------------------------------
        // Ray origin: the transparent depth is nearer than the opaque background at the receiver,
        // and identical to the opaque depth where no opted-in transparent surface exists.
        // -----------------------------------------------------------------------------------------
        result.rayOrigin = EvaluateRayOrigin(camera, imageTarget, feature, transparentDepth, transparentCameraDepth, rigParts);
        if (!result.rayOrigin.passed)
            result.issues.Add("The transparent ray origin did not follow the transparent surface.");

        // -----------------------------------------------------------------------------------------
        // Intensity 0 must restore the probe-only reference byte for byte.
        // -----------------------------------------------------------------------------------------
        ssr.intensity.Override(0f);
        cameraData.resetHistory = true;
        var intensityZero = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("TransparentIntensity0", intensityZero, reference));
        WriteImage(imageTarget, outputDirectory, "T10_" + modeName + "_TransparentIntensity0.png");
        if (!result.states[result.states.Count - 1].identicalToReference)
            result.issues.Add("Transparent SSR intensity 0 did not restore the probe-only reference; a probe or BRDF term is applied twice.");

        // -----------------------------------------------------------------------------------------
        // Off again must be byte identical to the opaque-only state (no residual keyword/output).
        // -----------------------------------------------------------------------------------------
        ssr.intensity.Override(1f);
        ssr.enabledTransparent.Override(false);
        cameraData.resetHistory = true;
        var offAgain = RenderAndCapture(camera, imageTarget, WarmupFrames);
        result.states.Add(DescribeState("TransparentOffAgain", offAgain, opaqueOnly));
        WriteImage(imageTarget, outputDirectory, "T10_" + modeName + "_TransparentOffAgain.png");
        if (!result.states[result.states.Count - 1].identicalToReference)
            result.issues.Add("Disabling transparent SSR did not restore the opaque-only image.");

        // -----------------------------------------------------------------------------------------
        // Independent outputs and main depth semantics.
        // -----------------------------------------------------------------------------------------
        ssr.enabledTransparent.Override(true);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, WarmupFrames);
        var opaqueWeightWithTransparent = CaptureDebug(camera, imageTarget, feature,
            ScreenSpaceLightingDebugMode.SsrWeight, outputDirectory, null);
        int opaqueWeightDiff = CountDifferences(opaqueWeight, opaqueWeightWithTransparent, 0f);
        result.opaqueWeightDiffPixels = opaqueWeightDiff;
        result.opaqueWeightMaxDelta = MaxDifference(opaqueWeight, opaqueWeightWithTransparent);
        if (opaqueWeightDiff != 0)
            result.issues.Add("Transparent SSR changed the opaque SSR output.");

        int cameraDepthDiff = CountDifferences(opaqueCameraDepth, transparentCameraDepth, 0f);
        result.cameraDepthDiffPixels = cameraDepthDiff;
        result.cameraDepthMaxDelta = MaxDifference(opaqueCameraDepth, transparentCameraDepth);
        if (cameraDepthDiff != 0)
            result.issues.Add("The transparent prepass changed the opaque camera depth (main transparent depth semantics).");

        // -----------------------------------------------------------------------------------------
        // Opt-in contract: only the receiver changes; the opt-out and unsupported regions keep the
        // probe exactly like the opaque-only state.
        // -----------------------------------------------------------------------------------------
        var receiverRect = ProjectBounds(camera, rigParts.receiver, 2);
        var optOutRect = ProjectBounds(camera, rigParts.optOut, 2);
        var unsupportedRect = ProjectBounds(camera, rigParts.unsupported, 2);
        result.receiverChangedPixels = CountDifferencesInRect(transparentOn, opaqueOnly, receiverRect, 0f);
        result.receiverMaxDelta = MaxDifferenceInRect(transparentOn, opaqueOnly, receiverRect);
        result.optOutChangedPixels = CountDifferencesInRect(transparentOn, opaqueOnly, optOutRect, 0f);
        result.optOutMaxDelta = MaxDifferenceInRect(transparentOn, opaqueOnly, optOutRect);
        result.unsupportedChangedPixels = CountDifferencesInRect(transparentOn, opaqueOnly, unsupportedRect, 0f);
        result.unsupportedMaxDelta = MaxDifferenceInRect(transparentOn, opaqueOnly, unsupportedRect);
        result.receiverWeight = SampleMaxInBounds(camera, transparentWeight, rigParts.receiver);
        result.optOutWeight = SampleMaxInBounds(camera, transparentWeight, rigParts.optOut);
        result.unsupportedWeight = SampleMaxInBounds(camera, transparentWeight, rigParts.unsupported);
        var coatRect = ProjectBounds(camera, rigParts.receiverCoat, 2);
        result.coatReceiverChangedPixels = CountDifferencesInRect(transparentOn, opaqueOnly, coatRect, 0f);
        result.coatReceiverMaxDelta = MaxDifferenceInRect(transparentOn, opaqueOnly, coatRect);
        result.coatReceiverWeight = SampleMaxInBounds(camera, transparentWeight, rigParts.receiverCoat);

        if (result.receiverChangedPixels == 0 || result.receiverWeight <= 0f)
            result.issues.Add("The opted-in transparent material did not receive transparent SSR.");
        if (result.optOutChangedPixels != 0)
            result.issues.Add("A material without Receive Transparent SSR changed when transparent SSR was enabled.");
        if (result.optOutWeight > 0f)
            result.issues.Add("A material without Receive Transparent SSR contributed a transparent ray origin.");
        if (result.unsupportedChangedPixels != 0 || result.unsupportedWeight > 0f)
            result.issues.Add("An unsupported transparent shader received transparent SSR data.");
        if (result.coatReceiverChangedPixels == 0 || result.coatReceiverWeight <= 0f)
            result.issues.Add("The opted-in Complex Lit (clear coat) transparent material did not receive transparent SSR.");

        // -----------------------------------------------------------------------------------------
        // No accumulation history misuse: PBR opaque algorithm must not propagate to transparents.
        // -----------------------------------------------------------------------------------------
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.PBRAccumulation);
        ssr.enabledTransparent.Override(true);
        cameraData.resetHistory = true;
        // Warm one reset frame so the previous color pyramid and PBR accumulation are valid before
        // the measured frames; otherwise the first frame legitimately produces no output.
        RenderAndCapture(camera, imageTarget, 2);
        var opaqueSsrPassType = FindPackageType("ScreenSpaceLightingSsrPass");
        var transparentSsrPassType = FindPackageType("ScreenSpaceLightingTransparentSsrPass");
        ResetEditorEvidence(opaqueSsrPassType);
        ResetEditorEvidence(transparentSsrPassType);
        RenderAndCapture(camera, imageTarget, PbrProbeFrames);
        result.transparentExecuteCount = ReadEditorInt(transparentSsrPassType, "s_EditorExecuteCount");
        result.transparentTraceDispatchCount = ReadEditorInt(transparentSsrPassType, "s_EditorTraceDispatchCount");
        result.transparentReprojectDispatchCount = ReadEditorInt(transparentSsrPassType, "s_EditorReprojectDispatchCount");
        result.transparentAccumulationDispatchCount = ReadEditorInt(transparentSsrPassType, "s_EditorAccumulationDispatchCount");
        result.opaqueAccumulationKernelCount = ReadEditorInt(opaqueSsrPassType, "s_EditorAccumulationKernelCount");
        result.transparentPassedWithPbr = result.transparentTraceDispatchCount == PbrProbeFrames &&
                                          result.transparentReprojectDispatchCount == PbrProbeFrames;
        if (result.transparentAccumulationDispatchCount != 0)
            result.issues.Add("Transparent SSR dispatched an accumulation kernel.");
        if (!result.transparentPassedWithPbr)
            result.issues.Add("Transparent SSR did not trace once per frame while the opaque algorithm was PBR.");
        if (result.opaqueAccumulationKernelCount == 0)
            result.issues.Add("The opaque PBR path did not run in the accumulation test.");

        var pbrTransparentWeight = CaptureDebug(camera, imageTarget, feature,
            ScreenSpaceLightingDebugMode.SsrTransparentWeight, outputDirectory,
            "T10_" + modeName + "_SsrTransparentWeightPbr.png");
        result.pbrTransparentWeight = SampleMaxInBounds(camera, pbrTransparentWeight, rigParts.receiver);
        if (result.pbrTransparentWeight <= 0f)
            result.issues.Add("Transparent SSR produced no weight with the PBR opaque algorithm.");

        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 2);

        // -----------------------------------------------------------------------------------------
        // Transparent object motion: URP does not write transparent object motion into
        // _MotionVectorTexture. A moved transparent object must still provide a fresh ray origin;
        // the missing motion is a recorded limitation, not an extra test-only path.
        // -----------------------------------------------------------------------------------------
        result.motion = EvaluateTransparentMotion(camera, imageTarget, feature, rigParts);
        if (!result.motion.passed)
            result.issues.Add("A moving transparent object produced an invalid ray origin or invalid SSR output.");

        result.motionLimitation =
            "URP 14.0.12 does not render transparent object motion into _MotionVectorTexture; the trace origin is " +
            "refreshed every frame from the prepass, but the reprojection uses the hit pixel / camera motion only. " +
            "Moving transparent objects can therefore lag in the reflected result.";

        result.passed = result.issues.Count == 0;
        return result;
    }

    static T10RayOrigin EvaluateRayOrigin(
        Camera camera,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        Color[] transparentDepth,
        Color[] cameraDepth,
        T10Rig rig)
    {
        var origin = new T10RayOrigin();
        if (transparentDepth == null || cameraDepth == null)
            return origin;

        var receiverPixel = ToPixel(camera, rig.receiver.transform.position);
        origin.receiverTransparentDepth = transparentDepth[receiverPixel.y * ImageWidth + receiverPixel.x].r;
        origin.receiverOpaqueDepth = cameraDepth[receiverPixel.y * ImageWidth + receiverPixel.x].r;
        // Reversed Z: a nearer surface has a larger raw device depth.
        origin.receiverNearerThanOpaque = origin.receiverTransparentDepth > origin.receiverOpaqueDepth + 1e-5f;

        int differing = 0;
        for (int i = 0; i < transparentDepth.Length; i++)
        {
            if (Mathf.Abs(transparentDepth[i].r - cameraDepth[i].r) > 1e-5f)
                differing++;
        }

        origin.differingFromOpaqueDepthPixels = differing;

        // Multi-layer ordering: at the centre of the near layer the closest surface wins. Each
        // configuration is captured through the same transparent depth debug view.
        rig.SetLayerVisibility(showNear: true, showFar: false);
        var nearOnly = CaptureDebug(camera, imageTarget, feature, ScreenSpaceLightingDebugMode.TransparentDepth, null, null);
        rig.SetLayerVisibility(showNear: false, showFar: true);
        var farOnly = CaptureDebug(camera, imageTarget, feature, ScreenSpaceLightingDebugMode.TransparentDepth, null, null);
        rig.SetLayerVisibility(showNear: true, showFar: true);
        var both = CaptureDebug(camera, imageTarget, feature, ScreenSpaceLightingDebugMode.TransparentDepth, null, null);

        var layerPixel = ToPixel(camera, rig.multiNear.transform.position);
        int layerIndex = layerPixel.y * ImageWidth + layerPixel.x;
        origin.nearOnlyDepth = nearOnly[layerIndex].r;
        origin.farOnlyDepth = farOnly[layerIndex].r;
        origin.bothLayersDepth = both[layerIndex].r;
        origin.nearestLayerWins = Mathf.Abs(origin.bothLayersDepth - origin.nearOnlyDepth) < 1e-6f &&
                                  origin.nearOnlyDepth > origin.farOnlyDepth + 1e-5f;

        origin.passed = origin.receiverNearerThanOpaque && origin.differingFromOpaqueDepthPixels > 0 &&
                        origin.nearestLayerWins;
        return origin;
    }

    static T10Motion EvaluateTransparentMotion(
        Camera camera,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        T10Rig rig)
    {
        var motion = new T10Motion();
        var start = rig.receiver.transform.position;
        var startPixel = ToPixel(camera, start);
        try
        {
            var depthBefore = CaptureDebug(camera, imageTarget, feature,
                ScreenSpaceLightingDebugMode.TransparentDepth, null, null);

            rig.receiver.transform.position = start + camera.transform.right * 0.8f;
            var movedPixel = ToPixel(camera, rig.receiver.transform.position);
            var moved = RenderAndCapture(camera, imageTarget, 4);
            var weight = CaptureDebug(camera, imageTarget, feature,
                ScreenSpaceLightingDebugMode.SsrTransparentWeight, null, null);
            var depthAfter = CaptureDebug(camera, imageTarget, feature,
                ScreenSpaceLightingDebugMode.TransparentDepth, null, null);

            motion.nonFinitePixels = Count(moved, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b));
            motion.maxWeight = SampleMaxInBounds(camera, weight, rig.receiver);
            motion.weightInRange = motion.maxWeight >= 0f && motion.maxWeight <= 1f;
            motion.startDepth = depthBefore[startPixel.y * ImageWidth + startPixel.x].r;
            motion.movedDepth = depthAfter[movedPixel.y * ImageWidth + movedPixel.x].r;
            motion.vacatedDepth = depthAfter[startPixel.y * ImageWidth + startPixel.x].r;
            // The object moved away, so the old pixel falls back to the opaque background depth and
            // the new pixel must carry the transparent surface it now covers.
            motion.rayOriginFollowsObject =
                motion.startDepth > motion.vacatedDepth + 1e-5f && motion.movedDepth > 0f && motion.movedDepth <= 1f;
            motion.passed = motion.nonFinitePixels == 0 && motion.weightInRange && motion.rayOriginFollowsObject;
        }
        finally
        {
            rig.receiver.transform.position = start;
            camera.GetComponent<UniversalAdditionalCameraData>().resetHistory = true;
        }

        return motion;
    }

    // ---------------------------------------------------------------------------------------------
    // Rig
    // ---------------------------------------------------------------------------------------------

    sealed class T10Rig
    {
        public GameObject root;
        public GameObject receiver;
        public GameObject receiverCoat;
        public GameObject optOut;
        public GameObject unsupported;
        public GameObject multiNear;
        public GameObject multiFar;

        public void SetLayerVisibility(bool showNear, bool showFar)
        {
            multiNear.SetActive(showNear);
            multiFar.SetActive(showFar);
        }

        public void ResetVisibility()
        {
            multiNear.SetActive(true);
            multiFar.SetActive(true);
            receiver.SetActive(true);
            receiverCoat.SetActive(true);
            optOut.SetActive(true);
            unsupported.SetActive(true);
        }
    }

    static T10Rig CreateTransparentRig(Camera camera, List<Material> materials)
    {
        var rig = new T10Rig();
        var root = new GameObject("T10 Transparent Rig");
        rig.root = root;
        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        var simpleLitShader = Shader.Find("Universal Render Pipeline/Simple Lit");
        if (litShader == null || simpleLitShader == null)
            throw new InvalidOperationException("The required URP shaders are missing.");

        var viewDirection = camera.transform.forward;
        var right = Vector3.Cross(Vector3.up, viewDirection).normalized;
        var center = camera.transform.position + viewDirection * 15f;
        center.y = 1.6f;

        var floorMaterial = CreateLitMaterial(litShader, "T10 Mirror Floor", new Color(0.72f, 0.75f, 0.8f), 1f, 0.95f);
        var backdropMaterial = CreateLitMaterial(litShader, "T10 Reflect Backdrop", new Color(0.04f, 0.04f, 0.04f), 0f, 0.2f);
        backdropMaterial.EnableKeyword("_EMISSION");
        backdropMaterial.SetColor("_EmissionColor", new Color(2.4f, 1.4f, 0.5f));
        materials.Add(floorMaterial);
        materials.Add(backdropMaterial);
        AddBox(root, floorMaterial, "T10 Mirror Floor", center + new Vector3(0f, -1.55f, 0f), new Vector3(30f, 0.05f, 18f));
        AddBox(root, backdropMaterial, "T10 Reflect Backdrop", center + viewDirection * 9f + Vector3.up * 2f, new Vector3(30f, 12f, 0.2f));

        var receiverMaterial = AssetDatabase.LoadAssetAtPath<Material>(ReceiverMaterialPath);
        var optOutMaterial = AssetDatabase.LoadAssetAtPath<Material>(OptOutMaterialPath);
        var unsupportedMaterial = AssetDatabase.LoadAssetAtPath<Material>(UnsupportedMaterialPath);
        var coatMaterial = AssetDatabase.LoadAssetAtPath<Material>(CoatMaterialPath);
        if (receiverMaterial == null || optOutMaterial == null || unsupportedMaterial == null || coatMaterial == null)
            throw new FileNotFoundException("Missing T10 transparent material assets. Run Create T10 Assets first.");

        rig.receiver = AddSphere(root, receiverMaterial, "T10 Transparent Receiver", center + right * -3.2f, 1.1f);
        rig.optOut = AddSphere(root, optOutMaterial, "T10 Transparent NoReceiver", center + right * 0f, 1.1f);
        rig.unsupported = AddSphere(root, unsupportedMaterial, "T10 Transparent SimpleLit", center + right * 3.2f, 1.1f);
        // Complex Lit with clear coat: the transparent prepass must write the coat SSR smoothness and
        // the material must consume the transparent trace through the coat hierarchy blend.
        rig.receiverCoat = AddSphere(root, coatMaterial, "T10 Transparent ComplexLit",
            center + Vector3.up * 2.6f + right * 1.4f, 1.4f);

        // Two overlapping layers: the near sphere is smaller so the far sphere stays visible around
        // its silhouette. The private depth must resolve to the near surface at the centre.
        var multiRoot = new GameObject("T10 Multi Layer");
        multiRoot.transform.SetParent(root.transform, false);
        multiRoot.transform.position = center + right * 6.6f;
        rig.multiNear = AddSphere(multiRoot, receiverMaterial, "T10 Transparent Near Layer", multiRoot.transform.position + viewDirection * 0.2f, 1.0f);
        rig.multiFar = AddSphere(multiRoot, receiverMaterial, "T10 Transparent Far Layer", multiRoot.transform.position + viewDirection * 3.2f, 2.4f);

        return rig;
    }

    static GameObject AddBox(GameObject parent, Material material, string name, Vector3 position, Vector3 size)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent.transform, false);
        box.transform.position = position;
        box.transform.localScale = size;
        box.GetComponent<Renderer>().sharedMaterial = material;
        return box;
    }

    static GameObject AddSphere(GameObject parent, Material material, string name, Vector3 position, float scale)
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = name;
        sphere.transform.SetParent(parent.transform, false);
        sphere.transform.position = position;
        sphere.transform.localScale = Vector3.one * scale;
        sphere.GetComponent<Renderer>().sharedMaterial = material;
        return sphere;
    }

    static Material CreateLitMaterial(Shader shader, string name, Color color, float metallic, float smoothness)
    {
        var material = new Material(shader) { name = name, color = color };
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        return material;
    }

    // ---------------------------------------------------------------------------------------------
    // Transparent materials
    // ---------------------------------------------------------------------------------------------

    static void EnsureMaterialAssets()
    {
        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        var simpleLitShader = Shader.Find("Universal Render Pipeline/Simple Lit");
        var complexLitShader = Shader.Find("Universal Render Pipeline/Complex Lit");
        EnsureMaterialAsset(ReceiverMaterialPath, "T10 Transparent Receiver", litShader, receive: true, simpleLit: false);
        EnsureMaterialAsset(OptOutMaterialPath, "T10 Transparent NoReceiver", litShader, receive: false, simpleLit: false);
        EnsureMaterialAsset(UnsupportedMaterialPath, "T10 Transparent SimpleLit", simpleLitShader, receive: false, simpleLit: true);
        EnsureMaterialAsset(CoatMaterialPath, "T10 Transparent ComplexLit", complexLitShader, receive: true, simpleLit: false);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static void EnsureMaterialAsset(string path, string name, Shader shader, bool receive, bool simpleLit)
    {
        if (shader == null)
            throw new InvalidOperationException("The URP Lit shader is missing.");

        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(material, path);
        }

        material.shader = shader;
        ApplyTransparentMaterial(material, receive, simpleLit);
        EditorUtility.SetDirty(material);
    }

    static void ApplyTransparentMaterial(Material material, bool receive, bool simpleLit)
    {
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_Blend", 0f);
        material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0f);
        material.SetFloat("_Cull", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.SetOverrideTag("RenderType", "Transparent");
        material.renderQueue = 3000;
        material.SetColor("_BaseColor", new Color(0.55f, 0.7f, 0.85f, 0.55f));
        if (material.HasProperty("_Metallic"))
            material.SetFloat("_Metallic", 1f);
        material.SetFloat("_Smoothness", 0.95f);
        if (material.HasProperty("_EnvironmentReflections"))
            material.SetFloat("_EnvironmentReflections", 1f);
        if (material.HasProperty("_ClearCoat"))
        {
            material.SetFloat("_ClearCoat", 1f);
            material.SetFloat("_ClearCoatMask", 1f);
            material.SetFloat("_ClearCoatSmoothness", 1f);
            material.EnableKeyword("_CLEARCOAT");
        }

        if (!simpleLit)
        {
            material.SetFloat("_TransparentSSRReceive", receive ? 1f : 0f);
            if (receive)
                material.EnableKeyword(ShaderKeywordStrings.TransparentSSRReceive);
            else
                material.DisableKeyword(ShaderKeywordStrings.TransparentSSRReceive);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Assets
    // ---------------------------------------------------------------------------------------------

    static void EnsureAssets(bool force)
    {
        if (SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before creating the T10 assets.");

        AssetDatabase.Refresh();
        for (var i = 0; i < ModeNames.Length; i++)
        {
            var mode = ModeNames[i];
            var renderer = EnsureRenderer(string.Format(T10RendererPathFormat, mode), "SslT10" + mode + "Renderer", mode, force, out bool recreated);
            EnsureFeatureResources(renderer);
            EnsurePipeline(string.Format(T10PipelinePathFormat, mode), "SslT10" + mode + "Pipeline", renderer, force || recreated);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static UniversalRendererData EnsureRenderer(string rendererPath, string rendererName, string mode, bool force, out bool recreated)
    {
        recreated = false;
        var t1Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T1RendererPathFormat, mode));
        if (t1Renderer == null)
            throw new FileNotFoundException("Missing T1 renderer asset. Create the T1 baseline first.", mode);

        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        var existingFeature = renderer != null ? renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault() : null;
        bool matches = existingFeature != null;

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
    // Volume and feature helpers
    // ---------------------------------------------------------------------------------------------

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

    static void EnsureSupportedEffectsOnly(VolumeProfile profile)
    {
        var ssgi = profile.components.OfType<ScreenSpaceGlobalIllumination>().FirstOrDefault();
        if (ssgi != null)
            ssgi.enabled.Override(false);
        var refraction = profile.components.OfType<ScreenSpaceRefraction>().FirstOrDefault();
        if (refraction != null)
            refraction.enabled.Override(false);
    }

    static void ApplyBaseSsrSettings(ScreenSpaceReflection ssr, float minSmoothness)
    {
        ssr.enabled.Override(true);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
        ssr.minSmoothness.Override(minSmoothness);
        ssr.smoothnessFadeStart.Override(minSmoothness);
        ssr.depthBufferThickness.Override(0.05f);
        ssr.screenFadeDistance.Override(0.1f);
        ssr.rayMaxIterations.Override(64);
        ssr.reflectSky.Override(true);
        ssr.intensity.Override(1f);
        ssr.enabledTransparent.Override(false);
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
    // Editor evidence access
    // ---------------------------------------------------------------------------------------------

    static Type FindPackageType(string typeName)
    {
        var type = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal." + typeName);
        if (type == null)
            throw new InvalidOperationException("Could not find package type " + typeName + ".");
        return type;
    }

    static void ResetEditorEvidence(Type type)
    {
        type.GetMethod("ResetEditorEvidenceCounters", StaticFlags)?.Invoke(null, null);
    }

    static int ReadEditorInt(Type type, string fieldName)
    {
        var field = type.GetField(fieldName, StaticFlags);
        return field != null ? (int)field.GetValue(null) : -1;
    }

    // ---------------------------------------------------------------------------------------------
    // Debug capture
    // ---------------------------------------------------------------------------------------------

    static Color[] CaptureDebug(
        Camera camera,
        RenderTexture imageTarget,
        ScreenSpaceLightingRendererFeature feature,
        ScreenSpaceLightingDebugMode mode,
        string outputDirectory,
        string fileName)
    {
        SetSslDebug(feature, (int)mode, 0);
        var samples = RenderAndCapture(camera, imageTarget, 2);
        SetSslDebug(feature, (int)ScreenSpaceLightingDebugMode.Off, 0);
        if (!string.IsNullOrEmpty(fileName))
            WriteImage(imageTarget, outputDirectory, fileName);
        return samples;
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

    // ---------------------------------------------------------------------------------------------
    // Image helpers
    // ---------------------------------------------------------------------------------------------

    struct ScreenRect
    {
        public int xMin;
        public int yMin;
        public int xMax;
        public int yMax;
        public bool IsEmpty => xMax < xMin || yMax < yMin;
    }

    static ScreenRect ProjectBounds(Camera camera, GameObject gameObject, int inset)
    {
        var renderer = gameObject.GetComponent<Renderer>();
        var bounds = renderer != null ? renderer.bounds : new Bounds(gameObject.transform.position, Vector3.one);
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? bounds.min.x : bounds.max.x,
                (i & 2) == 0 ? bounds.min.y : bounds.max.y,
                (i & 4) == 0 ? bounds.min.z : bounds.max.z);
            var viewport = camera.WorldToViewportPoint(corner);
            min = Vector3.Min(min, viewport);
            max = Vector3.Max(max, viewport);
        }

        var rect = new ScreenRect
        {
            xMin = Mathf.Clamp(Mathf.RoundToInt(min.x * ImageWidth) + inset, 0, ImageWidth - 1),
            yMin = Mathf.Clamp(Mathf.RoundToInt(min.y * ImageHeight) + inset, 0, ImageHeight - 1),
            xMax = Mathf.Clamp(Mathf.RoundToInt(max.x * ImageWidth) - inset, 0, ImageWidth - 1),
            yMax = Mathf.Clamp(Mathf.RoundToInt(max.y * ImageHeight) - inset, 0, ImageHeight - 1),
        };
        return rect;
    }

    static Vector2Int ToPixel(Camera camera, Vector3 worldPosition)
    {
        var viewport = camera.WorldToViewportPoint(worldPosition);
        return new Vector2Int(
            Mathf.Clamp(Mathf.RoundToInt(viewport.x * ImageWidth), 0, ImageWidth - 1),
            Mathf.Clamp(Mathf.RoundToInt(viewport.y * ImageHeight), 0, ImageHeight - 1));
    }

    static float SampleMaxInBounds(Camera camera, Color[] samples, GameObject gameObject)
    {
        if (samples == null)
            return 0f;
        var rect = ProjectBounds(camera, gameObject, 3);
        float best = 0f;
        for (int y = rect.yMin; y <= rect.yMax; y++)
        {
            for (int x = rect.xMin; x <= rect.xMax; x++)
                best = Mathf.Max(best, samples[y * ImageWidth + x].r);
        }

        return best;
    }

    static int CountDifferencesInRect(Color[] a, Color[] b, ScreenRect rect, float threshold)
    {
        if (a == null || b == null || a.Length != b.Length || rect.IsEmpty)
            return -1;
        int count = 0;
        for (int y = rect.yMin; y <= rect.yMax; y++)
        {
            for (int x = rect.xMin; x <= rect.xMax; x++)
            {
                int index = y * ImageWidth + x;
                if (MaxChannelDelta(a[index], b[index]) > threshold)
                    count++;
            }
        }

        return count;
    }

    static float MaxDifferenceInRect(Color[] a, Color[] b, ScreenRect rect)
    {
        if (a == null || b == null || a.Length != b.Length || rect.IsEmpty)
            return float.PositiveInfinity;
        float max = 0f;
        for (int y = rect.yMin; y <= rect.yMax; y++)
        {
            for (int x = rect.xMin; x <= rect.xMax; x++)
            {
                int index = y * ImageWidth + x;
                max = Mathf.Max(max, MaxChannelDelta(a[index], b[index]));
            }
        }

        return max;
    }

    static float MaxChannelDelta(Color a, Color b)
    {
        return Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
    }

    static RenderTexture CreateTarget(int width, int height, string name)
    {
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { name = name };
        target.Create();
        return target;
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

    static void WriteImage(RenderTexture target, string outputDirectory, string fileName)
    {
        if (string.IsNullOrEmpty(outputDirectory))
            return;
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

    static T10StateCapture DescribeState(string stateName, Color[] samples, Color[] reference)
    {
        var capture = new T10StateCapture
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
            if (MaxChannelDelta(a[i], b[i]) > threshold)
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
            max = Mathf.Max(max, MaxChannelDelta(a[i], b[i]));
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
        foreach (var sample in samples)
            sum += selector(sample);
        return samples.Length == 0 ? 0f : (float)(sum / samples.Length);
    }

    // ---------------------------------------------------------------------------------------------
    // Report data
    // ---------------------------------------------------------------------------------------------

    [Serializable]
    public sealed class T10Report
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public bool completed;
        public bool passed;
        public string error;
        public List<T10ModeResult> modes;
    }

    [Serializable]
    public sealed class T10ModeResult
    {
        public string name;
        public bool passed;
        public List<string> issues;
        public List<T10StateCapture> states;
        public T10RayOrigin rayOrigin;
        public int opaqueWeightDiffPixels;
        public float opaqueWeightMaxDelta;
        public int cameraDepthDiffPixels;
        public float cameraDepthMaxDelta;
        public int receiverChangedPixels;
        public float receiverMaxDelta;
        public int optOutChangedPixels;
        public float optOutMaxDelta;
        public int unsupportedChangedPixels;
        public float unsupportedMaxDelta;
        public float receiverWeight;
        public float optOutWeight;
        public float unsupportedWeight;
        public int coatReceiverChangedPixels;
        public float coatReceiverMaxDelta;
        public float coatReceiverWeight;
        public int transparentExecuteCount;
        public int transparentTraceDispatchCount;
        public int transparentReprojectDispatchCount;
        public int transparentAccumulationDispatchCount;
        public int opaqueAccumulationKernelCount;
        public bool transparentPassedWithPbr;
        public float pbrTransparentWeight;
        public T10Motion motion;
        public string motionLimitation;
    }

    [Serializable]
    public sealed class T10StateCapture
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
    public sealed class T10RayOrigin
    {
        public bool passed;
        public float receiverTransparentDepth;
        public float receiverOpaqueDepth;
        public bool receiverNearerThanOpaque;
        public int differingFromOpaqueDepthPixels;
        public float nearOnlyDepth;
        public float farOnlyDepth;
        public float bothLayersDepth;
        public bool nearestLayerWins;
    }

    [Serializable]
    public sealed class T10Motion
    {
        public bool passed;
        public int nonFinitePixels;
        public float maxWeight;
        public bool weightInRange;
        public float movedDepth;
        public float startDepth;
        public float vacatedDepth;
        public bool rayOriginFollowsObject;
    }
}