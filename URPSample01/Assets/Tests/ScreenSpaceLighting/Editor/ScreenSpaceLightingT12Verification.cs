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

// T12 acceptance: cross-combination hardening.
//
// Every case records a pass/fail/unsupported result. The checks focus on:
// - renderer coverage (Forward, Forward+, Deferred, forward-only materials in Deferred, and the
//   Deferred renderer's forward fallback under MSAA);
// - feature/module combinations and runtime switching, including GTAO order independence;
// - Volume cases (missing component, override state, two volumes, camera mask, post processing);
// - resolution/history lifecycle (render scale, target resize, same-frame render, camera cut,
//   SSR algorithm switch);
// - URP combinations (DBuffer/ScreenSpace decals, depth priming, opaque texture, TAA, motion blur,
//   HDR/MSAA) and unsupported materials/geometry;
// - camera stacks, preview/reflection cameras, offscreen targets;
// - global keyword cleanup after every camera and the private resource cost.
public static class ScreenSpaceLightingT12Verification
{
    const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
    const string MaterialRoot = "Assets/Tests/ScreenSpaceLighting/Materials";
    const string OutputRoot = "PALN/validation/T12";
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity";
    const string T1RendererPathFormat = RendererRoot + "/Ssl{0}Renderer.asset";
    const string T12RendererPathFormat = RendererRoot + "/SslT12{0}Renderer.asset";
    const string T12PipelinePathFormat = RendererRoot + "/SslT12{0}Pipeline.asset";
    const string T11GlassMaterialPath = MaterialRoot + "/T11RefractionBox.mat";
    const string T10ReceiverMaterialPath = MaterialRoot + "/T10TransparentReceiver.mat";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 10;
    const float k_IdenticalThreshold = 1e-6f;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly string[] SslGlobalKeywords = { "_SCREEN_SPACE_GI", "_SCREEN_SPACE_SSR", "_SCREEN_SPACE_TRANSPARENT_SSR", "_SCREEN_SPACE_REFRACTION" };

    [MenuItem("Tests/Screen Space Lighting/Create T12 Assets")]
    public static void CreateT12Assets()
    {
        EnsureAssets(force: true);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    // The T14 release regression replays this matrix. It must not overwrite the T12 evidence, so the
    // output directory can be redirected with -ssl-t12-output <relative-or-absolute-path>.
    static string ResolveOutputRoot()
    {
        var arguments = Environment.GetCommandLineArgs();
        for (int i = 0; i < arguments.Length - 1; i++)
        {
            if (arguments[i] == "-ssl-t12-output")
                return arguments[i + 1];
        }

        return OutputRoot;
    }

    [MenuItem("Tests/Screen Space Lighting/Run T12 Verification")]
    public static void RunT12Verification()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, ResolveOutputRoot());
        Directory.CreateDirectory(outputDirectory);
        var report = new T12Report
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            passed = true,
            modes = new List<T12ModeResult>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture imageTarget = null;
        Volume volume = null;
        VolumeProfile originalProfile = null;
        VolumeProfile testProfile = null;
        GameObject rig = null;
        T12Rig rigParts = null;
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
            testProfile.name = originalProfile.name + " (T12)";
            volume.sharedProfile = testProfile;
            DisableScreenSpaceEffects(testProfile);

            rigParts = CreateRig(camera);
            rig = rigParts.root;

            imageTarget = CreateTarget(ImageWidth, ImageHeight, "SslT12Image", msaa: 1);
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
                Debug.Log($"T12 {mode.name}: passed={mode.passed} cases={mode.cases.Count}");
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
            File.WriteAllText(Path.Combine(outputDirectory, "T12Verification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"T12 verification finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    // ---------------------------------------------------------------------------------------------
    // Per renderer matrix
    // ---------------------------------------------------------------------------------------------

    sealed class T12Context
    {
        public string modeName;
        public Camera camera;
        public UniversalAdditionalCameraData cameraData;
        public VolumeProfile profile;
        public RenderTexture imageTarget;
        public T12Rig rig;
        public UniversalRendererData rendererData;
        public ScreenSpaceLightingRendererFeature ssl;
        public GroundTruthAmbientOcclusionRendererFeature gtao;
        public ScriptableRendererFeature decal;
        public UniversalRenderPipelineAsset pipeline;
        public T12ModeResult result;
        public Color[] reference;

        public ScreenSpaceGlobalIllumination ssgi;
        public ScreenSpaceReflection ssr;
        public ScreenSpaceRefraction refraction;
    }

    static T12ModeResult CaptureMode(
        string modeName,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        VolumeProfile profile,
        RenderTexture imageTarget,
        T12Rig rigParts,
        string outputDirectory)
    {
        var result = new T12ModeResult
        {
            name = modeName,
            cases = new List<T12CaseResult>(),
            issues = new List<string>(),
        };

        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(string.Format(T12PipelinePathFormat, modeName));
        var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T12RendererPathFormat, modeName));
        if (pipeline == null || rendererData == null)
            throw new FileNotFoundException("Missing T12 assets. Run Create T12 Assets first.");
        var ssl = rendererData.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        var gtao = rendererData.rendererFeatures.OfType<GroundTruthAmbientOcclusionRendererFeature>().FirstOrDefault();
        var decal = rendererData.rendererFeatures.FirstOrDefault(f => f.GetType().Name == "DecalRendererFeature");
        if (ssl == null || gtao == null || decal == null)
            throw new InvalidOperationException("The T12 renderer is missing the SSL, GTAO or Decal feature.");

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        pipeline.msaaSampleCount = 1;
        pipeline.supportsCameraOpaqueTexture = false;
        pipeline.renderScale = 1f;
        pipeline.supportsHDR = true;
        camera.allowMSAA = true;
        cameraData.renderPostProcessing = false;
        cameraData.antialiasing = AntialiasingMode.None;
        cameraData.volumeLayerMask = ~0;
        cameraData.resetHistory = true;
        rigParts.ResetVisibility();

        var context = new T12Context
        {
            modeName = modeName,
            camera = camera,
            cameraData = cameraData,
            profile = profile,
            imageTarget = imageTarget,
            rig = rigParts,
            rendererData = rendererData,
            ssl = ssl,
            gtao = gtao,
            decal = decal,
            pipeline = pipeline,
            result = result,
            ssgi = profile.components.OfType<ScreenSpaceGlobalIllumination>().FirstOrDefault(),
            ssr = profile.components.OfType<ScreenSpaceReflection>().FirstOrDefault(),
            refraction = profile.components.OfType<ScreenSpaceRefraction>().FirstOrDefault(),
        };
        if (context.ssgi == null || context.ssr == null || context.refraction == null)
            throw new InvalidOperationException("The T12 volume profile is missing a screen-space lighting component.");

        // -----------------------------------------------------------------------------------------
        // Reference: every screen-space effect is off and GTAO intensity is zero.
        // -----------------------------------------------------------------------------------------
        SetEffects(context, ssgi: false, ssr: false, refraction: false, gtao: false);
        context.reference = RenderAndCapture(camera, imageTarget, WarmupFrames);
        Record(context, "Reference", context.reference, null, expectedChange: false, outputDirectory);

        // -----------------------------------------------------------------------------------------
        // All on, all off again, and per-module combinations.
        // -----------------------------------------------------------------------------------------
        SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: true);
        var allOn = Capture(context, "AllOn", context.reference, expectedChange: true, outputDirectory);
        RecordResourceCost(context);

        SetEffects(context, ssgi: false, ssr: false, refraction: false, gtao: false);
        var allOff = Capture(context, "AllOffAgain", context.reference, expectedChange: false, outputDirectory);
        if (!IsIdentical(allOff, context.reference))
            AddIssue(context, "AllOffAgain", "Turning every effect off did not restore the reference image.");

        CaptureCombo(context, "SsgiOnly", ssgi: true, ssr: false, refraction: false, gtao: false, outputDirectory);
        CaptureCombo(context, "SsrOnly", ssgi: false, ssr: true, refraction: false, gtao: false, outputDirectory);
        CaptureCombo(context, "RefractionOnly", ssgi: false, ssr: false, refraction: true, gtao: false, outputDirectory);
        CaptureCombo(context, "GtaoOnly", ssgi: false, ssr: false, refraction: false, gtao: true, outputDirectory);
        CaptureCombo(context, "SsgiSsr", ssgi: true, ssr: true, refraction: false, gtao: false, outputDirectory);
        CaptureCombo(context, "GtaoAndSsl", ssgi: true, ssr: true, refraction: true, gtao: true, outputDirectory);

        // Runtime switching twice must not accumulate state.
        SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: true);
        RenderAndCapture(camera, imageTarget, 3);
        SetEffects(context, ssgi: false, ssr: false, refraction: false, gtao: false);
        RenderAndCapture(camera, imageTarget, 3);
        SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: true);
        RenderAndCapture(camera, imageTarget, 3);
        SetEffects(context, ssgi: false, ssr: false, refraction: false, gtao: false);
        var toggledOff = RenderAndCapture(camera, imageTarget, 4);
        Record(context, "RuntimeToggleOff", toggledOff, context.reference, expectedChange: false, outputDirectory);
        if (!IsIdentical(toggledOff, context.reference))
            AddIssue(context, "RuntimeToggleOff", "Runtime effect switching left residual state.");

        // -----------------------------------------------------------------------------------------
        // Feature order: GTAO before SSL must not change the all-on image.
        // -----------------------------------------------------------------------------------------
        SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: true);
        var gtaoOriginalIndex = rendererData.rendererFeatures.IndexOf(gtao);
        ReorderFeature(rendererData, gtao, 0);
        cameraData.resetHistory = true;
        var gtaoFirst = RenderAndCapture(camera, imageTarget, WarmupFrames);
        Record(context, "GtaoFirst", gtaoFirst, allOn, expectedChange: false, outputDirectory);
        if (!IsIdentical(gtaoFirst, allOn))
            AddIssue(context, "GtaoFirst", "Swapping the GTAO/SSL feature order changed the all-on image.");
        ReorderFeature(rendererData, gtao, gtaoOriginalIndex);

        // -----------------------------------------------------------------------------------------
        // Volume cases.
        // -----------------------------------------------------------------------------------------
        // A profile that keeps every other volume component but removes the SSL components.
        var noSslProfile = ScriptableObject.Instantiate(context.profile);
        noSslProfile.name = "T12 No SSL Components";
        noSslProfile.Remove<ScreenSpaceGlobalIllumination>();
        noSslProfile.Remove<ScreenSpaceReflection>();
        noSslProfile.Remove<ScreenSpaceRefraction>();
        // GTAO is configured on the renderer feature, not in the volume, so disable it explicitly.
        SetGtaoIntensity(context.gtao, 0f);
        // VolumeManager reads the volume's shared profile, so swap the scene volume temporarily.
        var sceneVolume = UnityEngine.Object.FindObjectOfType<Volume>();
        var savedProfile = sceneVolume.sharedProfile;
        sceneVolume.sharedProfile = noSslProfile;
        cameraData.resetHistory = true;
        var noComponents = RenderAndCapture(camera, imageTarget, WarmupFrames);
        Record(context, "NoVolumeComponents", noComponents, context.reference, expectedChange: false, outputDirectory);
        if (!IsIdentical(noComponents, context.reference))
            AddIssue(context, "NoVolumeComponents", "A profile without SSL components changed the reference image.");
        sceneVolume.sharedProfile = savedProfile;
        UnityEngine.Object.DestroyImmediate(noSslProfile);

        // overrideState off behaves like the default (disabled) value. GTAO is a renderer feature with
        // its own settings, so it must be disabled explicitly to compare against the reference.
        SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: false);
        context.ssgi.enabled.overrideState = false;
        context.ssr.enabled.overrideState = false;
        context.refraction.enabled.overrideState = false;
        cameraData.resetHistory = true;
        var overrideOff = RenderAndCapture(camera, imageTarget, WarmupFrames);
        Record(context, "OverrideStateOff", overrideOff, context.reference, expectedChange: false, outputDirectory);
        if (!IsIdentical(overrideOff, context.reference))
            AddIssue(context, "OverrideStateOff", "overrideState=false did not fall back to the disabled default.");
        context.ssgi.enabled.overrideState = true;
        context.ssr.enabled.overrideState = true;
        context.refraction.enabled.overrideState = true;

        // Two volumes: a higher priority volume with a lower SSR intensity must blend over the scene.
        SetEffects(context, ssgi: false, ssr: true, refraction: false, gtao: false);
        context.ssr.intensity.Override(1f);
        cameraData.resetHistory = true;
        var singleVolume = RenderAndCapture(camera, imageTarget, WarmupFrames);
        var secondVolumeObject = new GameObject("T12 Second Volume");
        var secondVolume = secondVolumeObject.AddComponent<Volume>();
        var secondProfile = ScriptableObject.CreateInstance<VolumeProfile>();
        secondProfile.name = "T12 Second Volume Profile";
        var secondSsr = secondProfile.Add<ScreenSpaceReflection>(true);
        secondSsr.enabled.Override(true);
        secondSsr.intensity.Override(0.25f);
        secondSsr.minSmoothness.Override(0f);
        secondSsr.smoothnessFadeStart.Override(0f);
        secondVolume.isGlobal = true;
        secondVolume.priority = 10f;
        secondVolume.sharedProfile = secondProfile;
        cameraData.resetHistory = true;
        var twoVolumes = RenderAndCapture(camera, imageTarget, WarmupFrames);
        Record(context, "TwoVolumes", twoVolumes, singleVolume, expectedChange: true, outputDirectory);
        UnityEngine.Object.DestroyImmediate(secondVolumeObject);
        UnityEngine.Object.DestroyImmediate(secondProfile);

        // Camera volume mask excludes the scene volume. GTAO is renderer-configured, not volume
        // configured, so it is disabled to keep the reference comparison meaningful.
        SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: false);
        cameraData.volumeLayerMask = 1 << 31;
        cameraData.resetHistory = true;
        var masked = RenderAndCapture(camera, imageTarget, WarmupFrames);
        Record(context, "CameraVolumeMask", masked, context.reference, expectedChange: false, outputDirectory);
        if (!IsIdentical(masked, context.reference))
            AddIssue(context, "CameraVolumeMask", "A camera mask that excludes the volume still applied effects.");
        cameraData.volumeLayerMask = ~0;

        // Post processing on with all effects. The post-processing pass changes the output color
        // pipeline, so the comparison is against the same post-processing path with effects off.
        cameraData.renderPostProcessing = true;
        SetEffects(context, ssgi: false, ssr: false, refraction: false, gtao: false);
        cameraData.resetHistory = true;
        var ppOffEffects = RenderAndCapture(camera, imageTarget, WarmupFrames);
        SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: true);
        cameraData.resetHistory = true;
        var ppOnEffects = RenderAndCapture(camera, imageTarget, WarmupFrames);
        Record(context, "PostProcessingOn", ppOnEffects, ppOffEffects, expectedChange: true, outputDirectory);
        cameraData.renderPostProcessing = false;

        // -----------------------------------------------------------------------------------------
        // Resolution and history lifecycle.
        // -----------------------------------------------------------------------------------------
        SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: true);
        int resetsBeforeScale = ReadSslResetCount(context);
        pipeline.renderScale = 0.75f;
        cameraData.resetHistory = true;
        var scaled = RenderAndCapture(camera, imageTarget, WarmupFrames);
        Record(context, "RenderScale75", scaled, null, expectedChange: true, outputDirectory);
        int resetsAfterScale = ReadSslResetCount(context);
        result.renderScaleResetCount = resetsAfterScale - resetsBeforeScale;
        if (resetsAfterScale <= resetsBeforeScale)
            AddIssue(context, "RenderScale75", "Changing render scale did not reset the SSL history.");
        pipeline.renderScale = 1f;
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, WarmupFrames);

        // Target resize (window change).
        int resetsBeforeResize = ReadSslResetCount(context);
        imageTarget.Release();
        imageTarget.width = ImageWidth / 2;
        imageTarget.height = ImageHeight / 2;
        imageTarget.Create();
        cameraData.resetHistory = true;
        var resized = RenderAndCapture(camera, imageTarget, 4);
        Record(context, "TargetResize", resized, null, expectedChange: true, outputDirectory);
        int resetsAfterResize = ReadSslResetCount(context);
        result.targetResizeResetCount = resetsAfterResize - resetsBeforeResize;
        if (resetsAfterResize <= resetsBeforeResize)
            AddIssue(context, "TargetResize", "Resizing the camera target did not reset the SSL history.");
        imageTarget.Release();
        imageTarget.width = ImageWidth;
        imageTarget.height = ImageHeight;
        imageTarget.Create();
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, WarmupFrames);

        // Same-frame repeated render.
        camera.Render();
        var repeatedA = ReadPixels(imageTarget);
        camera.Render();
        var repeatedB = ReadPixels(imageTarget);
        Record(context, "SameFrameDoubleRender", repeatedB, repeatedA, expectedChange: false, outputDirectory);

        // Camera cut.
        int resetsBeforeCut = ReadSslResetCount(context);
        var cameraStart = camera.transform.position;
        camera.transform.position = cameraStart + new Vector3(1.5f, 0f, 0f);
        cameraData.resetHistory = true;
        var cut = RenderAndCapture(camera, imageTarget, 2);
        int resetsAfterCut = ReadSslResetCount(context);
        result.cameraCutResetCount = resetsAfterCut - resetsBeforeCut;
        Record(context, "CameraCut", cut, null, expectedChange: true, outputDirectory);
        if (resetsAfterCut <= resetsBeforeCut)
            AddIssue(context, "CameraCut", "A camera cut did not reset the SSL history.");
        camera.transform.position = cameraStart;

        // SSR algorithm switch.
        SetEffects(context, ssgi: false, ssr: true, refraction: false, gtao: false);
        context.ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.PBRAccumulation);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 4);
        int resetsBeforeAlgorithm = ReadSslPbrResetCount(context);
        context.ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
        cameraData.resetHistory = true;
        var switched = RenderAndCapture(camera, imageTarget, 4);
        int resetsAfterAlgorithm = ReadSslPbrResetCount(context);
        result.algorithmSwitchResetCount = resetsAfterAlgorithm - resetsBeforeAlgorithm;
        Record(context, "AlgorithmSwitch", switched, null, expectedChange: true, outputDirectory);
        if (resetsAfterAlgorithm <= resetsBeforeAlgorithm)
            AddIssue(context, "AlgorithmSwitch", "Switching the SSR algorithm did not reset the accumulation history.");

        // -----------------------------------------------------------------------------------------
        // URP combinations.
        // -----------------------------------------------------------------------------------------
        SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: true);

        // Depth priming forced.
        rendererData.depthPrimingMode = DepthPrimingMode.Forced;
        rendererData.SetDirty();
        cameraData.resetHistory = true;
        var primed = RenderAndCapture(camera, imageTarget, WarmupFrames);
        Record(context, "DepthPrimingForced", primed, context.reference, expectedChange: true, outputDirectory);
        result.depthPrimingChanged = !IsIdentical(primed, context.reference);
        rendererData.depthPrimingMode = DepthPrimingMode.Disabled;
        rendererData.SetDirty();
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 4);

        // Opaque texture: URP changes the intermediate color format when the opaque texture is enabled,
        // so the check is that the effects remain visible and finite, not that the image is identical
        // to the no-opaque-texture path.
        pipeline.supportsCameraOpaqueTexture = true;
        SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: true);
        cameraData.resetHistory = true;
        var opaqueOnAllOn = RenderAndCapture(camera, imageTarget, WarmupFrames);
        SetEffects(context, ssgi: false, ssr: false, refraction: false, gtao: false);
        cameraData.resetHistory = true;
        var opaqueOnAllOff = RenderAndCapture(camera, imageTarget, WarmupFrames);
        Record(context, "OpaqueTexture", opaqueOnAllOn, opaqueOnAllOff, expectedChange: true, outputDirectory);
        pipeline.supportsCameraOpaqueTexture = false;
        SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: true);

        // TAA.
        cameraData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
        cameraData.resetHistory = true;
        var taa = RenderAndCapture(camera, imageTarget, WarmupFrames);
        Record(context, "TemporalAA", taa, context.reference, expectedChange: true, outputDirectory);
        cameraData.antialiasing = AntialiasingMode.None;

        // Motion blur with post processing; rotate the camera so there is real screen motion.
        cameraData.renderPostProcessing = true;
        var motionBlur = context.profile.Add<MotionBlur>(true);
        motionBlur.mode.Override(MotionBlurMode.CameraOnly);
        motionBlur.intensity.Override(0.8f);
        cameraData.resetHistory = true;
        var cameraRotation = camera.transform.rotation;
        for (int i = 0; i < WarmupFrames; i++)
        {
            camera.transform.Rotate(0f, 0.35f, 0f);
            camera.Render();
        }

        var motionBlurred = ReadPixels(imageTarget);
        Record(context, "MotionBlur", motionBlurred, allOn, expectedChange: true, outputDirectory);
        camera.transform.rotation = cameraRotation;
        context.profile.Remove<MotionBlur>();
        cameraData.renderPostProcessing = false;
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 4);

        // Decals: DBuffer and screen space.
        rigParts.decal.SetActive(true);
        SetDecalTechnique(context, "DBuffer");
        cameraData.resetHistory = true;
        var dbuffer = RenderAndCapture(camera, imageTarget, WarmupFrames);
        Record(context, "DecalDBuffer", dbuffer, allOn, expectedChange: true, outputDirectory);
        SetDecalTechnique(context, "ScreenSpace");
        cameraData.resetHistory = true;
        var screenSpaceDecal = RenderAndCapture(camera, imageTarget, WarmupFrames);
        Record(context, "DecalScreenSpace", screenSpaceDecal, allOn, expectedChange: true, outputDirectory);
        rigParts.decal.SetActive(false);
        SetDecalTechnique(context, "Automatic");

        // HDR + MSAA: recreate the target with 4x MSAA.
        var msaaTarget = CreateTarget(ImageWidth, ImageHeight, "SslT12ImageMSAA", msaa: 4);
        camera.targetTexture = msaaTarget;
        pipeline.msaaSampleCount = 4;
        cameraData.resetHistory = true;
        var msaa = RenderAndCapture(camera, msaaTarget, WarmupFrames);
        WriteImage(msaaTarget, outputDirectory, "T12_" + modeName + "_MSAA4x.png");
        var msaaCase = RecordSamples(context, "MSAA4x", msaa, context.reference, expectedChange: true);
        context.result.cases.Add(msaaCase);
        // URP's deferred compatibility path ignores MSAA; the flag records whether MSAA actually
        // changed the image instead of claiming MSAA support on that path.
        result.msaaChangedFromAllOn = !IsIdentical(msaa, allOn);
        if (!msaaCase.nonFinitePixels.Equals(0) || msaaCase.magentaPixels != 0)
            AddIssue(context, "MSAA4x", "MSAA 4x produced invalid pixels.");
        if (!msaaCase.differsFromReference)
            AddIssue(context, "MSAA4x", "MSAA 4x removed every screen-space effect.");
        camera.targetTexture = imageTarget;
        pipeline.msaaSampleCount = 1;
        msaaTarget.Release();
        UnityEngine.Object.DestroyImmediate(msaaTarget);

        // Unsupported material: SimpleLit keeps the SSL weight at 0. GTAO is excluded from both
        // captures because it is a standard URP AO consumer and legitimately darkens SimpleLit.
        var unsupportedRect = ProjectBounds(camera, rigParts.unsupported, 20);
        SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: false);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, WarmupFrames);
        var withEffects = ReadPixels(imageTarget);
        SetEffects(context, ssgi: false, ssr: false, refraction: false, gtao: false);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, WarmupFrames);
        var withoutEffects = ReadPixels(imageTarget);
        result.unsupportedChangedPixels = CountDifferencesInRect(withEffects, withoutEffects, unsupportedRect, k_IdenticalThreshold);
        if (result.unsupportedChangedPixels != 0)
            AddIssue(context, "UnsupportedMaterial", "A SimpleLit material changed with screen-space lighting enabled.");
        context.result.cases.Add(new T12CaseResult
        {
            name = "UnsupportedMaterial",
            passed = result.unsupportedChangedPixels == 0,
            differingPixels = result.unsupportedChangedPixels,
        });
        SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: true);

        // Forward-only material in Deferred: the ComplexLit glass region must change with refraction.
        var forwardOnlyRect = ProjectBounds(camera, rigParts.forwardOnly, 2);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, WarmupFrames);
        var refractionOnImage = ReadPixels(imageTarget);
        context.refraction.enabled.Override(false);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, WarmupFrames);
        var refractionOffImage = ReadPixels(imageTarget);
        context.refraction.enabled.Override(true);
        result.forwardOnlyChangedPixels = CountDifferencesInRect(refractionOnImage, refractionOffImage, forwardOnlyRect, 0f);
        result.forwardOnlySupported = result.forwardOnlyChangedPixels > 0;
        context.result.cases.Add(new T12CaseResult
        {
            name = modeName == "Deferred" ? "DeferredForwardOnly" : "ForwardOnlyComplexLit",
            passed = result.forwardOnlySupported,
            differingPixels = result.forwardOnlyChangedPixels,
        });
        if (!result.forwardOnlySupported)
            AddIssue(context, "ForwardOnly", "The forward-only Complex Lit object did not receive screen-space refraction.");

        // -----------------------------------------------------------------------------------------
        // Camera stack, preview, reflection and offscreen depth.
        // -----------------------------------------------------------------------------------------
        var stackResult = RunCameraStackCase(context);
        result.cameraStackPassed = stackResult.passed;
        context.result.cases.Add(new T12CaseResult { name = "CameraStack", passed = stackResult.passed });
        if (!stackResult.passed)
            AddIssue(context, "CameraStack", stackResult.message);

        var previewResult = RunCameraTypeCase(context, "PreviewCamera", CameraType.Preview, CameraClearFlags.SolidColor);
        result.previewCameraPassed = previewResult.passed;
        context.result.cases.Add(new T12CaseResult { name = "PreviewCamera", passed = previewResult.passed });
        if (!previewResult.passed)
            AddIssue(context, "PreviewCamera", previewResult.message);

        var reflectionResult = RunReflectionProbeCase(context);
        result.reflectionCameraPassed = reflectionResult.passed;
        context.result.cases.Add(new T12CaseResult { name = "ReflectionProbeCamera", passed = reflectionResult.passed });
        if (!reflectionResult.passed)
            AddIssue(context, "ReflectionProbeCamera", reflectionResult.message);

        // Deferred renderer actual forward fallback. URP switches renderingModeActual to Forward for
        // wireframe/debug/unsupported deferred cases; GL.wireframe is the deterministic editor switch.
        if (modeName == "Deferred")
        {
            bool previousWireframe = GL.wireframe;
            GL.wireframe = true;
            cameraData.resetHistory = true;
            var fallbackForward = RenderAndCapture(camera, imageTarget, 4);
            GL.wireframe = previousWireframe;
            Record(context, "DeferredFallbackForward", fallbackForward, null, expectedChange: true, outputDirectory);
            cameraData.resetHistory = true;
            RenderAndCapture(camera, imageTarget, 4);
        }

        // -----------------------------------------------------------------------------------------
        // Global keyword cleanup: after the last camera cleanup every SSL global keyword is off.
        // -----------------------------------------------------------------------------------------
        SetEffects(context, ssgi: false, ssr: false, refraction: false, gtao: false);
        cameraData.resetHistory = true;
        RenderAndCapture(camera, imageTarget, 2);
        result.globalKeywordsClean = true;
        foreach (var keyword in SslGlobalKeywords)
        {
            if (Shader.IsKeywordEnabled(keyword))
            {
                result.globalKeywordsClean = false;
                AddIssue(context, "GlobalKeywordCleanup", $"Global keyword {keyword} leaked after camera cleanup.");
            }
        }

        result.passed = result.issues.Count == 0;
        context.result.cases.Add(new T12CaseResult
        {
            name = "GlobalKeywordCleanup",
            passed = result.globalKeywordsClean,
        });

        // GTAO intensity lives on the renderer feature asset; restore the default so the evidence
        // run does not persist a disabled feature into the serialized renderer.
        SetGtaoIntensity(context.gtao, 1f);
        return result;
    }

    static void CaptureCombo(T12Context context, string name, bool ssgi, bool ssr, bool refraction, bool gtao, string outputDirectory)
    {
        SetEffects(context, ssgi, ssr, refraction, gtao);
        context.cameraData.resetHistory = true;
        var samples = RenderAndCapture(context.camera, context.imageTarget, WarmupFrames);
        Record(context, name, samples, context.reference, expectedChange: true, outputDirectory);
        SetEffects(context, false, false, false, false);
        context.cameraData.resetHistory = true;
        RenderAndCapture(context.camera, context.imageTarget, 3);
    }

    static Color[] Capture(T12Context context, string name, Color[] compareTo, bool expectedChange, string outputDirectory)
    {
        context.cameraData.resetHistory = true;
        var samples = RenderAndCapture(context.camera, context.imageTarget, WarmupFrames);
        Record(context, name, samples, compareTo, expectedChange, outputDirectory);
        return samples;
    }

    static T12CaseResult Record(T12Context context, string name, Color[] samples, Color[] compareTo, bool expectedChange, string outputDirectory)
    {
        var result = RecordSamples(context, name, samples, compareTo, expectedChange);
        context.result.cases.Add(result);
        if (!string.IsNullOrEmpty(outputDirectory))
            WriteImage(context.imageTarget, outputDirectory, "T12_" + context.modeName + "_" + name + ".png");
        return result;
    }

    static T12CaseResult RecordSamples(T12Context context, string name, Color[] samples, Color[] compareTo, bool expectedChange)
    {
        var capture = new T12CaseResult
        {
            name = name,
            sha256 = HashSamples(samples),
            nonFinitePixels = Count(samples, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)),
            magentaPixels = Count(samples, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f),
            meanLuminance = Mean(samples, Luminance),
            differingPixels = compareTo != null ? CountDifferences(samples, compareTo, k_IdenticalThreshold) : 0,
            maxChannelDelta = compareTo != null ? MaxDifference(samples, compareTo) : 0f,
            passed = true,
        };
        capture.differsFromReference = capture.differingPixels > 0;
        capture.identicalToReference = compareTo != null && capture.differingPixels == 0;
        if (capture.nonFinitePixels != 0)
        {
            capture.passed = false;
            AddIssue(context, name, "Case produced non-finite pixels.");
        }
        if (capture.magentaPixels != 0)
        {
            capture.passed = false;
            AddIssue(context, name, "Case produced error-shader pixels.");
        }
        if (compareTo != null && expectedChange && capture.identicalToReference)
        {
            capture.passed = false;
            AddIssue(context, name, "Case was expected to change the image but was identical.");
        }
        return capture;
    }

    static void AddIssue(T12Context context, string caseName, string message)
    {
        context.result.issues.Add(caseName + ": " + message);
    }

    // ---------------------------------------------------------------------------------------------
    // Effect and feature control
    // ---------------------------------------------------------------------------------------------

    static void SetEffects(T12Context context, bool ssgi, bool ssr, bool refraction, bool gtao)
    {
        context.ssgi.enabled.Override(ssgi);
        context.ssgi.intensity.Override(1f);
        context.ssr.enabled.Override(ssr);
        context.ssr.enabledTransparent.Override(ssr);
        context.ssr.intensity.Override(1f);
        context.ssr.minSmoothness.Override(0.4f);
        context.ssr.smoothnessFadeStart.Override(0.4f);
        context.ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
        context.refraction.enabled.Override(refraction);
        context.refraction.intensity.Override(1f);
        SetGtaoIntensity(context.gtao, gtao ? 1f : 0f);
    }

    static void SetGtaoIntensity(GroundTruthAmbientOcclusionRendererFeature gtao, float intensity)
    {
        var settings = gtao.GetType().GetProperty("settings", InstanceFlags)?.GetValue(gtao);
        var field = settings?.GetType().GetField("intensity", InstanceFlags);
        field?.SetValue(settings, intensity);
        EditorUtility.SetDirty(gtao);
    }

    static void SetDecalTechnique(T12Context context, string techniqueName)
    {
        var settings = context.decal.GetType().GetField("m_Settings", InstanceFlags)?.GetValue(context.decal);
        var field = settings?.GetType().GetField("technique", InstanceFlags);
        if (field == null)
            throw new InvalidOperationException("The Decal feature has no technique field.");
        field.SetValue(settings, Enum.Parse(field.FieldType, techniqueName));
        context.decal.SetActive(techniqueName != "Off");
        context.rendererData.SetDirty();
    }

    static void ReorderFeature(UniversalRendererData rendererData, ScriptableRendererFeature feature, int index)
    {
        var features = rendererData.rendererFeatures;
        features.Remove(feature);
        features.Insert(Mathf.Clamp(index, 0, features.Count), feature);
        EnsureFeatureMap(rendererData);
        rendererData.SetDirty();
    }

    // ---------------------------------------------------------------------------------------------
    // History and resource accounting
    // ---------------------------------------------------------------------------------------------

    static object GetSslHistory(T12Context context)
    {
        // The depth pyramid is shared, so there is no per-feature pyramid pass to read the history
        // from; every SSL consumer pass keeps the same per-camera history reference.
        var ssgiPass = context.ssl.GetType().GetProperty("ssgiPass", InstanceFlags)?.GetValue(context.ssl);
        return ssgiPass?.GetType().GetField("m_History", InstanceFlags)?.GetValue(ssgiPass);
    }

    static int ReadSslResetCount(T12Context context)
    {
        var history = GetSslHistory(context);
        var property = history?.GetType().GetProperty("resetCount", InstanceFlags);
        return property != null ? (int)property.GetValue(history) : -1;
    }

    static int ReadSslPbrResetCount(T12Context context)
    {
        var history = GetSslHistory(context);
        var property = history?.GetType().GetProperty("ssrPbrResetCount", InstanceFlags);
        return property != null ? (int)property.GetValue(history) : -1;
    }

    static void RecordResourceCost(T12Context context)
    {
        var sslHistory = GetSslHistory(context);
        var gtaoPass = context.gtao.GetType().GetProperty("pass", InstanceFlags)?.GetValue(context.gtao);
        var gtaoHistory = gtaoPass?.GetType().GetField("m_History", InstanceFlags)?.GetValue(gtaoPass);
        context.result.sslPrivateBytes = SumResourceBytes(sslHistory);
        context.result.gtaoPrivateBytes = SumResourceBytes(gtaoHistory);
        context.result.sslColorPyramidMips = ReadInt(sslHistory, "colorPyramidMipCount");
        context.result.sslDepthPyramidMips = 0;

        var info = sslHistory?.GetType().GetProperty("depthPyramidInfo", InstanceFlags)?.GetValue(sslHistory);
        if (info != null)
        {
            var textureSize = (Vector2Int)info.GetType().GetField("textureSize").GetValue(info);
            context.result.sslDepthPyramidMips = (int)info.GetType().GetField("mipLevelCount").GetValue(info);
            context.result.sslDepthPyramidBytes = (long)textureSize.x * textureSize.y * 4;
        }

        // Per-frame pass counts: one transparent depth/normal prepass and the packed Hi-Z dispatches.
        var prepassType = FindPackageType("ScreenSpaceLightingTransparentDepthNormalsPass");
        var depthPassType = FindPackageType("ScreenSpaceDepthPyramidPass");
        int prepassBefore = ReadEditorInt(prepassType, "s_EditorExecuteCount");
        int copyBefore = ReadEditorInt(depthPassType, "s_EditorCopyDispatchCount");
        int downsampleBefore = ReadEditorInt(depthPassType, "s_EditorDownsampleDispatchCount");
        context.camera.Render();
        context.result.transparentPrepassExecutions = ReadEditorInt(prepassType, "s_EditorExecuteCount") - prepassBefore;
        context.result.depthPyramidDispatches =
            ReadEditorInt(depthPassType, "s_EditorCopyDispatchCount") - copyBefore +
            ReadEditorInt(depthPassType, "s_EditorDownsampleDispatchCount") - downsampleBefore;
    }

    static Type FindPackageType(string typeName)
    {
        var type = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal." + typeName);
        if (type == null)
            throw new InvalidOperationException("Could not find package type " + typeName + ".");
        return type;
    }

    static int ReadEditorInt(Type type, string fieldName)
    {
        var field = type.GetField(fieldName, StaticFlags);
        return field != null ? (int)field.GetValue(null) : -1;
    }

    static int ReadInt(object target, string name)
    {
        if (target == null)
            return -1;
        var property = target.GetType().GetProperty(name, InstanceFlags);
        if (property != null)
            return (int)property.GetValue(target);
        var field = target.GetType().GetField(name, InstanceFlags);
        return field != null ? (int)field.GetValue(target) : -1;
    }

    static long SumResourceBytes(object history)
    {
        if (history == null)
            return 0;
        long total = 0;
        foreach (var field in history.GetType().GetFields(InstanceFlags))
        {
            object value = field.GetValue(history);
            if (value is RTHandle handle && handle.rt != null)
                total += RTHandleBaseBytes(handle);
            else if (value is ComputeBuffer buffer && buffer != null)
                total += (long)buffer.count * buffer.stride;
        }

        return total;
    }

    static long RTHandleBaseBytes(RTHandle handle)
    {
        var rt = handle.rt;
        var format = rt.graphicsFormat;
        int blockSize = (int)GraphicsFormatUtility.GetBlockSize(format);
        int blockWidth = Mathf.Max(1, (int)GraphicsFormatUtility.GetBlockWidth(format));
        int blockHeight = Mathf.Max(1, (int)GraphicsFormatUtility.GetBlockHeight(format));
        if (blockSize <= 0)
            blockSize = 4;
        long blocks = (long)Mathf.CeilToInt(rt.width / (float)blockWidth) *
                      Mathf.CeilToInt(rt.height / (float)blockHeight);
        return blocks * blockSize;
    }

    // ---------------------------------------------------------------------------------------------
    // Camera cases
    // ---------------------------------------------------------------------------------------------

    struct CameraCaseResult
    {
        public bool passed;
        public string message;
    }

    static CameraCaseResult RunCameraStackCase(T12Context context)
    {
        var result = new CameraCaseResult { passed = true, message = string.Empty };
        GameObject overlayObject = null;
        try
        {
            var baseCamera = context.camera;
            var baseData = context.cameraData;

            overlayObject = new GameObject("T12 Overlay Camera");
            var overlayCamera = overlayObject.AddComponent<Camera>();
            overlayCamera.CopyFrom(baseCamera);
            overlayCamera.clearFlags = CameraClearFlags.Nothing;
            // The overlay must contribute no geometry; the case proves the stack renders the base
            // camera with the effects still visible and the overlay is rejected by the capability
            // check (coverage of the rejection is in ScreenSpaceCapabilityTests).
            overlayCamera.cullingMask = 0;
            overlayCamera.depth = baseCamera.depth + 1f;
            var overlayData = overlayCamera.GetUniversalAdditionalCameraData();
            overlayData.renderType = CameraRenderType.Overlay;
            overlayData.renderPostProcessing = false;
            baseData.cameraStack.Add(overlayCamera);

            SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: true);
            baseData.resetHistory = true;
            var stackAllOn = RenderAndCapture(baseCamera, context.imageTarget, WarmupFrames);
            SetEffects(context, ssgi: false, ssr: false, refraction: false, gtao: false);
            baseData.resetHistory = true;
            var stackAllOff = RenderAndCapture(baseCamera, context.imageTarget, WarmupFrames);
            context.result.cameraStackChangedPixels = CountDifferences(stackAllOn, stackAllOff, k_IdenticalThreshold);
            context.result.cameraStackNonFinitePixels = Count(stackAllOn, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b));
            if (context.result.cameraStackNonFinitePixels != 0)
            {
                result.passed = false;
                result.message = "The camera stack produced non-finite pixels.";
            }
            else if (context.result.cameraStackChangedPixels <= 0)
            {
                result.passed = false;
                result.message = "Screen-space effects were not visible through the camera stack.";
            }

            baseData.cameraStack.Remove(overlayCamera);
            SetEffects(context, ssgi: true, ssr: true, refraction: true, gtao: true);
            baseData.resetHistory = true;
            RenderAndCapture(baseCamera, context.imageTarget, 4);
        }
        catch (Exception exception)
        {
            result.passed = false;
            result.message = exception.ToString();
        }
        finally
        {
            if (overlayObject != null)
                UnityEngine.Object.DestroyImmediate(overlayObject);
        }

        return result;
    }

    static CameraCaseResult RunCameraTypeCase(T12Context context, string name, CameraType type, CameraClearFlags clearFlags)
    {
        var result = new CameraCaseResult { passed = true, message = string.Empty };
        GameObject cameraObject = null;
        RenderTexture target = null;
        try
        {
            cameraObject = new GameObject("T12 " + name);
            var cam = cameraObject.AddComponent<Camera>();
            cam.CopyFrom(context.camera);
            cam.cameraType = type;
            cam.clearFlags = clearFlags;
            cam.targetTexture = target = CreateTarget(320, 180, "T12 " + name + "Target", msaa: 1);
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = false;
            cam.Render();
            var pixels = ReadPixels(target);
            if (Count(pixels, c => !IsFinite(c.r) || !IsFinite(c.g) || !IsFinite(c.b)) != 0)
            {
                result.passed = false;
                result.message = "Camera produced non-finite pixels.";
            }
        }
        catch (Exception exception)
        {
            result.passed = false;
            result.message = exception.ToString();
        }
        finally
        {
            if (cameraObject != null)
                UnityEngine.Object.DestroyImmediate(cameraObject);
            if (target != null)
            {
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        return result;
    }

    static CameraCaseResult RunReflectionProbeCase(T12Context context)
    {
        var result = new CameraCaseResult { passed = true, message = string.Empty };
        GameObject probeObject = null;
        try
        {
            probeObject = new GameObject("T12 Reflection Probe");
            probeObject.transform.position = context.camera.transform.position + context.camera.transform.forward * 10f;
            var probe = probeObject.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.size = new Vector3(20f, 10f, 20f);
            probe.boxProjection = true;
            probe.resolution = 64;
            // No exception is the observable contract here: reflection cameras are rejected by the
            // screen-space capability check and must not disturb the probe rendering.
            probe.RenderProbe();
        }
        catch (Exception exception)
        {
            result.passed = false;
            result.message = exception.ToString();
        }
        finally
        {
            if (probeObject != null)
                UnityEngine.Object.DestroyImmediate(probeObject);
        }

        return result;
    }

    // ---------------------------------------------------------------------------------------------
    // Rig
    // ---------------------------------------------------------------------------------------------

    sealed class T12Rig
    {
        public GameObject root;
        public GameObject metalSphere;
        public GameObject glass;
        public GameObject receiver;
        public GameObject forwardOnly;
        public GameObject unsupported;
        public GameObject alphaTest;
        public GameObject decal;
        public GameObject backdrop;
        public ReflectionProbe probe;

        public void ResetVisibility()
        {
            metalSphere.SetActive(true);
            glass.SetActive(true);
            receiver.SetActive(true);
            forwardOnly.SetActive(true);
            unsupported.SetActive(true);
            alphaTest.SetActive(true);
            decal.SetActive(false);
            backdrop.SetActive(true);
        }
    }

    static T12Rig CreateRig(Camera camera)
    {
        var rig = new T12Rig();
        var root = new GameObject("T12 Combination Rig");
        rig.root = root;
        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        var simpleLitShader = Shader.Find("Universal Render Pipeline/Simple Lit");
        var complexLitShader = Shader.Find("Universal Render Pipeline/Complex Lit");
        var decalShader = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Decal.shadergraph");
        if (litShader == null || simpleLitShader == null || complexLitShader == null)
            throw new InvalidOperationException("The URP shaders are missing.");

        var viewDirection = camera.transform.forward;
        var right = Vector3.Cross(Vector3.up, viewDirection).normalized;
        var center = camera.transform.position + viewDirection * 15f;
        center.y = 1.6f;

        var floorMaterial = CreateLitMaterial(litShader, "T12 Floor", new Color(0.5f, 0.52f, 0.55f), 0f, 0.6f);
        var backdropMaterial = CreateLitMaterial(litShader, "T12 Backdrop", new Color(0.02f, 0.02f, 0.02f), 0f, 0.2f);
        backdropMaterial.EnableKeyword("_EMISSION");
        backdropMaterial.SetColor("_EmissionColor", new Color(2.0f, 1.1f, 0.3f));
        AddBox(root, floorMaterial, "T12 Floor", center + new Vector3(0f, -1.55f, 0f), new Vector3(34f, 0.05f, 22f));
        rig.backdrop = AddBox(root, backdropMaterial, "T12 Backdrop", center + viewDirection * 9f + Vector3.up * 2f, new Vector3(32f, 12f, 0.2f));

        // Metallic sphere (SSR) and diffuse cube (SSGI).
        var metalMaterial = CreateLitMaterial(litShader, "T12 Metal", new Color(0.9f, 0.85f, 0.7f), 1f, 0.95f);
        rig.metalSphere = AddSphere(root, metalMaterial, "T12 Metal Sphere", center + right * 2.8f, 1.4f);

        // Refraction (Lit) and transparent SSR receiver.
        rig.glass = AddSphere(root, LoadMaterial(T11GlassMaterialPath), "T12 Glass", center + right * -2.4f, 1.2f);
        rig.receiver = AddSphere(root, LoadMaterial(T10ReceiverMaterialPath), "T12 Transparent Receiver", center + right * 0.2f, 1.1f);

        // Forward-only Complex Lit refraction (also the Deferred ForwardOnly case).
        var complexMaterial = LoadMaterial(MaterialRoot + "/T11RefractionComplexLit.mat");
        rig.forwardOnly = AddSphere(root, complexMaterial, "T12 ForwardOnly ComplexLit", center + right * -4.8f, 1.1f);

        // Unsupported PBR material.
        var unsupportedMaterial = CreateLitMaterial(simpleLitShader, "T12 SimpleLit", new Color(0.7f, 0.3f, 0.3f), 0f, 0.4f);
        rig.unsupported = AddSphere(root, unsupportedMaterial, "T12 SimpleLit Sphere", center + right * 4.9f, 1.1f);

        // Alpha-tested plane.
        var alphaMaterial = CreateLitMaterial(litShader, "T12 AlphaTest", Color.white, 0f, 0.4f);
        alphaMaterial.SetFloat("_AlphaClip", 1f);
        alphaMaterial.SetFloat("_Cutoff", 0.5f);
        alphaMaterial.EnableKeyword("_ALPHATEST_ON");
        alphaMaterial.SetTexture("_BaseMap", CreateAlphaCheckerTexture());
        rig.alphaTest = AddBox(root, alphaMaterial, "T12 AlphaTest Plane", center + Vector3.up * 2.8f + right * 0.4f, new Vector3(3f, 1.5f, 0.05f));

        // Decal projector on the floor.
        var decalObject = new GameObject("T12 Decal Projector");
        decalObject.transform.SetParent(root.transform, false);
        decalObject.transform.position = center + new Vector3(0f, -1.2f, 0f);
        decalObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        var projector = decalObject.AddComponent<DecalProjector>();
        if (decalShader != null)
        {
            var decalMaterial = new Material(decalShader) { name = "T12 Decal Material" };
            projector.material = decalMaterial;
        }
        projector.size = new Vector3(4f, 4f, 4f);
        projector.pivot = new Vector3(0f, 0f, 2f);
        rig.decal = decalObject;
        decalObject.SetActive(false);

        // Box-projected reflection probe: the refraction proxy and the SSR/SSGI probe fallback.
        // ViaScripting keeps the cubemap frozen, so scene-color changes come from the color pyramid.
        var probeObject = new GameObject("T12 Reflection Probe");
        probeObject.transform.SetParent(root.transform, false);
        probeObject.transform.position = center;
        var probe = probeObject.AddComponent<ReflectionProbe>();
        probe.mode = ReflectionProbeMode.Realtime;
        probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
        probe.size = new Vector3(70f, 40f, 40f);
        probe.center = Vector3.zero;
        probe.boxProjection = true;
        probe.resolution = 64;
        probe.hdr = true;
        probe.clearFlags = ReflectionProbeClearFlags.Skybox;
        probe.cullingMask = ~0;
        probe.RenderProbe();
        rig.probe = probe;

        return rig;
    }

    static Texture2D CreateAlphaCheckerTexture()
    {
        var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "T12 Alpha Checker" };
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
                texture.SetPixel(x, y, ((x + y) & 1) == 0 ? new Color(1, 1, 1, 1) : new Color(1, 1, 1, 0));
        }

        texture.Apply(false, false);
        return texture;
    }

    static Material LoadMaterial(string path)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
            throw new FileNotFoundException("Missing T12 material asset.", path);
        return new Material(material) { name = material.name + " (T12 Runtime)" };
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
        material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Metallic"))
            material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        return material;
    }

    // ---------------------------------------------------------------------------------------------
    // Assets
    // ---------------------------------------------------------------------------------------------

    static void EnsureAssets(bool force)
    {
        if (SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before creating the T12 assets.");

        AssetDatabase.Refresh();
        for (var i = 0; i < ModeNames.Length; i++)
        {
            var mode = ModeNames[i];
            var renderer = EnsureRenderer(string.Format(T12RendererPathFormat, mode), "SslT12" + mode + "Renderer", mode, force, out bool recreated);
            EnsurePipeline(string.Format(T12PipelinePathFormat, mode), "SslT12" + mode + "Pipeline", renderer, force || recreated);
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
        bool matches = renderer != null &&
                       renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().Any() &&
                       renderer.rendererFeatures.OfType<GroundTruthAmbientOcclusionRendererFeature>().Any() &&
                       renderer.rendererFeatures.Any(f => f.GetType().Name == "DecalRendererFeature");
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

            var ssl = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
            ssl.name = rendererName + "ssl";
            ssl.GetType().GetMethod("Create", InstanceFlags)?.Invoke(ssl, null);
            AssetDatabase.AddObjectToAsset(ssl, renderer);
            renderer.rendererFeatures.Add(ssl);

            var gtao = ScriptableObject.CreateInstance<GroundTruthAmbientOcclusionRendererFeature>();
            gtao.name = rendererName + "gtao";
            gtao.GetType().GetMethod("Create", InstanceFlags)?.Invoke(gtao, null);
            AssetDatabase.AddObjectToAsset(gtao, renderer);
            renderer.rendererFeatures.Add(gtao);

            var decalType = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.DecalRendererFeature");
            if (decalType != null)
            {
                var decal = ScriptableObject.CreateInstance(decalType) as ScriptableRendererFeature;
                decal.name = rendererName + "decal";
                decal.GetType().GetMethod("Create", InstanceFlags)?.Invoke(decal, null);
                AssetDatabase.AddObjectToAsset(decal, renderer);
                renderer.rendererFeatures.Add(decal);
            }

            var probe = ScriptableObject.CreateInstance<ScreenSpaceLightingPassProbeFeature>();
            probe.name = rendererName + "probe";
            probe.probeEvent = (RenderPassEvent)203;
            probe.probeMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "/SslProbe.mat");
            AssetDatabase.AddObjectToAsset(probe, renderer);
            renderer.rendererFeatures.Add(probe);

            EnsureFeatureMap(renderer);
            EnsureFeatureResources(renderer);
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

        var serialized = new SerializedObject(pipeline);
        var boxProjection = serialized.FindProperty("m_ReflectionProbeBoxProjection");
        if (boxProjection != null && !boxProjection.boolValue)
        {
            boxProjection.boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);
        }

        return pipeline;
    }

    static void EnsureFeatureResources(UniversalRendererData renderer)
    {
        var assembly = typeof(UniversalRendererData).Assembly;
        foreach (var feature in renderer.rendererFeatures)
        {
            string locatorName = null;
            string typeName = feature.GetType().Name;
            if (typeName == "ScreenSpaceLightingRendererFeature")
                locatorName = "UnityEngine.Rendering.Universal.ScreenSpaceLightingResourceLocator";
            else if (typeName == "GroundTruthAmbientOcclusionRendererFeature")
                locatorName = "UnityEngine.Rendering.Universal.GroundTruthAmbientOcclusionResourceLocator";
            if (locatorName == null)
                continue;

            var settings = feature.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
            var resources = settings?.GetType().GetField("resources", InstanceFlags)?.GetValue(settings);
            if (resources == null)
                continue;

            var locator = assembly.GetType(locatorName);
            locator?.GetMethod("AssignDefaults", StaticFlags)?.Invoke(null, new[] { resources });
            EditorUtility.SetDirty(feature);
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

        return new ScreenRect
        {
            xMin = Mathf.Clamp(Mathf.RoundToInt(min.x * ImageWidth) + inset, 0, ImageWidth - 1),
            yMin = Mathf.Clamp(Mathf.RoundToInt(min.y * ImageHeight) + inset, 0, ImageHeight - 1),
            xMax = Mathf.Clamp(Mathf.RoundToInt(max.x * ImageWidth) - inset, 0, ImageWidth - 1),
            yMax = Mathf.Clamp(Mathf.RoundToInt(max.y * ImageHeight) - inset, 0, ImageHeight - 1),
        };
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

    static float MaxChannelDelta(Color a, Color b)
    {
        return Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
    }

    static RenderTexture CreateTarget(int width, int height, string name, int msaa)
    {
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
        {
            name = name,
            antiAliasing = Mathf.Max(1, msaa),
        };
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

    static bool IsIdentical(Color[] a, Color[] b)
    {
        return a != null && b != null && a.Length == b.Length && CountDifferences(a, b, k_IdenticalThreshold) == 0;
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

    static int Count(Color[] samples, Func<Color, bool> predicate)
    {
        if (samples == null)
            return 0;
        int count = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            if (predicate(samples[i]))
                count++;
        }

        return count;
    }

    static float Mean(Color[] samples, Func<Color, float> selector)
    {
        if (samples == null || samples.Length == 0)
            return 0f;
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += selector(samples[i]);
        return (float)(sum / samples.Length);
    }

    static float Luminance(Color color) => color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f;

    static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    static string HashSamples(Color[] samples)
    {
        if (samples == null)
            return string.Empty;
        using (var sha = SHA256.Create())
        {
            var bytes = new byte[samples.Length * 16];
            for (int i = 0; i < samples.Length; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(samples[i].r), 0, bytes, i * 16, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(samples[i].g), 0, bytes, i * 16 + 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(samples[i].b), 0, bytes, i * 16 + 8, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(samples[i].a), 0, bytes, i * 16 + 12, 4);
            }

            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
        }
    }
}

[Serializable]
public sealed class T12Report
{
    public string utcTimestamp;
    public string unityVersion;
    public string graphicsDeviceName;
    public string graphicsDeviceType;
    public bool completed;
    public bool passed;
    public string error;
    public List<T12ModeResult> modes;
}

[Serializable]
public sealed class T12ModeResult
{
    public string name;
    public bool passed;
    public List<string> issues;
    public List<T12CaseResult> cases;
    public int renderScaleResetCount;
    public int targetResizeResetCount;
    public int cameraCutResetCount;
    public int algorithmSwitchResetCount;
    public bool depthPrimingChanged;
    public bool msaaChangedFromAllOn;
    public int unsupportedChangedPixels;
    public int forwardOnlyChangedPixels;
    public bool forwardOnlySupported;
    public bool cameraStackPassed;
    public int cameraStackChangedPixels;
    public int cameraStackNonFinitePixels;
    public bool previewCameraPassed;
    public bool reflectionCameraPassed;
    public bool globalKeywordsClean;
    public long sslPrivateBytes;
    public long gtaoPrivateBytes;
    public long sslDepthPyramidBytes;
    public int transparentPrepassExecutions;
    public int depthPyramidDispatches;
    public int sslColorPyramidMips;
    public int sslDepthPyramidMips;
}

[Serializable]
public sealed class T12CaseResult
{
    public string name;
    public bool passed;
    public string sha256;
    public int nonFinitePixels;
    public int magentaPixels;
    public float meanLuminance;
    public int differingPixels;
    public float maxChannelDelta;
    public bool differsFromReference;
    public bool identicalToReference;
}