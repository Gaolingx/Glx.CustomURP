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

// T3 acceptance: the empty GTAO/SSL framework must be configurable through serialized renderer
// features and volume overrides, must enqueue only when configured, and must leave the baseline
// image byte-identical in all three renderer modes.
public static class ScreenSpaceLightingT3Verification
{
    const string Root = "Assets/Tests/ScreenSpaceLighting";
    const string RendererRoot = Root + "/Renderers";
    const string ProfileRoot = Root + "/Profiles";
    const string SceneRoot = Root + "/Scenes";
    const string OutputRoot = "PALN/validation/T3";

    public const string T3ScenePath = SceneRoot + "/ScreenSpaceLightingT3Verification.unity";
    const string T3ProfilePath = ProfileRoot + "/ScreenSpaceLightingT3Profile.asset";
    const string T3RendererPathFormat = RendererRoot + "/SslT3{0}Renderer.asset";
    const string T3PipelinePathFormat = RendererRoot + "/SslT3{0}Pipeline.asset";
    const string T1RendererPathFormat = RendererRoot + "/Ssl{0}Renderer.asset";
    const string T1PipelinePathFormat = RendererRoot + "/Ssl{0}Pipeline.asset";

    const int ImageWidth = 1280;
    const int ImageHeight = 720;
    const int WarmupFrames = 12;

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };

    [MenuItem("Tests/Screen Space Lighting/Create T3 Assets")]
    public static void CreateT3Assets()
    {
        EnsureAssets(force: true);
    }

    [MenuItem("Tests/Screen Space Lighting/Run T3 Verification")]
    public static void RunT3Verification()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);
        var report = new T3Report
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            modes = new List<T3ModeResult>()
        };

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var previousScenePath = SceneManager.GetActiveScene().path;
        RenderTexture imageTarget = null;
        try
        {
            EnsureAssets(force: false);
            EditorSceneManager.OpenScene(T3ScenePath, OpenSceneMode.Single);

            var camera = UnityEngine.Object.FindObjectOfType<Camera>();
            if (camera == null)
                throw new InvalidOperationException("The T3 verification scene has no camera.");
            var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData == null)
                throw new InvalidOperationException("The T3 verification camera has no UniversalAdditionalCameraData.");
            var runner = UnityEngine.Object.FindObjectOfType<ScreenSpaceLightingBaselineRunner>();
            if (runner != null)
                runner.enabled = false;

            var volumeObject = GameObject.Find("Baseline Volume");
            if (volumeObject == null || volumeObject.GetComponent<Volume>() == null)
                throw new InvalidOperationException("The T3 verification scene has no Baseline Volume.");
            var t3Profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(T3ProfilePath);
            if (t3Profile == null)
                throw new FileNotFoundException("Missing T3 volume profile.", T3ProfilePath);
            ValidateVolumeProfile(t3Profile);

            imageTarget = new RenderTexture(ImageWidth, ImageHeight, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "SslT3Image"
            };
            imageTarget.Create();
            camera.targetTexture = imageTarget;
            camera.aspect = (float)ImageWidth / ImageHeight;
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;

            for (var modeIndex = 0; modeIndex < ModeNames.Length; modeIndex++)
            {
                var mode = CaptureMode(
                    ModeNames[modeIndex],
                    camera,
                    cameraData,
                    volumeObject,
                    imageTarget,
                    projectRoot,
                    outputDirectory);
                report.modes.Add(mode);
                report.passed &= mode.passed;
                Debug.Log($"T3 {mode.name}: passed={mode.passed} states={mode.states.Count} issues={mode.issues.Count}");
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
            if (imageTarget != null)
            {
                imageTarget.Release();
                UnityEngine.Object.DestroyImmediate(imageTarget);
            }
            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
            File.WriteAllText(Path.Combine(outputDirectory, "T3Verification.json"), JsonUtility.ToJson(report, true));
        }

        Debug.Log($"T3 verification finished: completed={report.completed} passed={report.passed}");
        EditorApplication.Exit(report.completed && report.passed ? 0 : 1);
    }

    static void ValidateVolumeProfile(VolumeProfile profile)
    {
        if (!profile.Has<ScreenSpaceGlobalIllumination>() ||
            !profile.Has<ScreenSpaceReflection>() ||
            !profile.Has<ScreenSpaceRefraction>())
            throw new InvalidOperationException("The T3 volume profile is missing one of the Screen Space Lighting overrides.");
    }

    static T3ModeResult CaptureMode(
        string modeName,
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        GameObject volumeObject,
        RenderTexture imageTarget,
        string projectRoot,
        string outputDirectory)
    {
        var result = new T3ModeResult { name = modeName, issues = new List<string>(), states = new List<T3StateCapture>() };

        var t1Pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(string.Format(T1PipelinePathFormat, modeName));
        if (t1Pipeline == null)
            throw new FileNotFoundException("Missing T1 pipeline asset. Create the T1 baseline first.");
        var t3Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T3RendererPathFormat, modeName));
        var t3Pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(string.Format(T3PipelinePathFormat, modeName));
        if (t3Renderer == null || t3Pipeline == null)
            throw new FileNotFoundException("Missing T3 renderer or pipeline asset for " + modeName + ".");

        var gtao = t3Renderer.rendererFeatures.OfType<GroundTruthAmbientOcclusionRendererFeature>().FirstOrDefault();
        var ssl = t3Renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        var gtaoPassType = typeof(GroundTruthAmbientOcclusionRendererFeature).Assembly
            .GetType("UnityEngine.Rendering.Universal.GroundTruthAmbientOcclusionPass");
        var sslPassType = typeof(ScreenSpaceLightingRendererFeature).Assembly
            .GetType("UnityEngine.Rendering.Universal.ScreenSpaceLightingSetupPass");
        if (gtaoPassType == null || sslPassType == null)
        {
            result.issues.Add("skeleton pass types were not found for execution counting");
            result.passed = false;
            return result;
        }
        result.featureCount = t3Renderer.rendererFeatures.Count;
        result.gtaoFeatureFound = gtao != null;
        result.sslFeatureFound = ssl != null;
        if (gtao == null || ssl == null)
        {
            result.issues.Add("T3 renderer does not contain both features");
            result.passed = false;
            return result;
        }

        cameraData.renderPostProcessing = false;
        cameraData.antialiasing = AntialiasingMode.None;

        // Reference: the T1 renderer without any plugin feature and without plugin volume overrides.
        GraphicsSettings.defaultRenderPipeline = t1Pipeline;
        QualitySettings.renderPipeline = t1Pipeline;
        volumeObject.SetActive(false);
        var gtaoExecutions = ReadExecuteCount(gtaoPassType);
        var sslExecutions = ReadExecuteCount(sslPassType);
        var reference = RenderAndCapture(camera, imageTarget, warmupFrames: WarmupFrames);
        result.states.Add(DescribeState(
            "T1Baseline", reference, null,
            ReadExecuteCount(gtaoPassType) - gtaoExecutions,
            ReadExecuteCount(sslPassType) - sslExecutions));
        WriteImage(imageTarget, projectRoot, outputDirectory, "T3_" + modeName + "_T1Baseline.png");

        GraphicsSettings.defaultRenderPipeline = t3Pipeline;
        QualitySettings.renderPipeline = t3Pipeline;

        CaptureState(
            result, "FeaturesOn_VolumeOff", gtao, ssl, gtaoActive: true, sslActive: true, volumeActive: false,
            camera, imageTarget, volumeObject, reference, projectRoot, outputDirectory, gtaoPassType, sslPassType);
        CaptureState(
            result, "FeaturesOff_VolumeOn", gtao, ssl, gtaoActive: false, sslActive: false, volumeActive: true,
            camera, imageTarget, volumeObject, reference, projectRoot, outputDirectory, gtaoPassType, sslPassType);
        CaptureState(
            result, "FeaturesOn_VolumeOn", gtao, ssl, gtaoActive: true, sslActive: true, volumeActive: true,
            camera, imageTarget, volumeObject, reference, projectRoot, outputDirectory, gtaoPassType, sslPassType);
        CaptureState(
            result, "GtaoOnly", gtao, ssl, gtaoActive: true, sslActive: false, volumeActive: true,
            camera, imageTarget, volumeObject, reference, projectRoot, outputDirectory, gtaoPassType, sslPassType);
        CaptureState(
            result, "SslOnly", gtao, ssl, gtaoActive: false, sslActive: true, volumeActive: true,
            camera, imageTarget, volumeObject, reference, projectRoot, outputDirectory, gtaoPassType, sslPassType);

        volumeObject.SetActive(false);
        gtao.SetActive(true);
        ssl.SetActive(true);

        CheckCameraDestruction(camera, cameraData, volumeObject, gtao, ssl, imageTarget, result);
        Evaluate(result);
        return result;
    }

    static void CheckCameraDestruction(
        Camera camera,
        UniversalAdditionalCameraData cameraData,
        GameObject volumeObject,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        ScreenSpaceLightingRendererFeature ssl,
        RenderTexture imageTarget,
        T3ModeResult result)
    {
        volumeObject.SetActive(true);
        RenderAndCapture(camera, imageTarget, warmupFrames: 2);
        result.gtaoHistoryEntries = ReadHistoryEntryCount(gtao);
        result.sslHistoryEntries = ReadHistoryEntryCount(ssl);

        var tempObject = new GameObject("T3 Temporary Camera");
        try
        {
            var tempCamera = tempObject.AddComponent<Camera>();
            tempCamera.CopyFrom(camera);
            tempCamera.targetTexture = imageTarget;
            tempCamera.depth = camera.depth + 1f;
            var tempData = tempObject.AddComponent<UniversalAdditionalCameraData>();
            tempData.renderPostProcessing = cameraData.renderPostProcessing;
            tempData.antialiasing = cameraData.antialiasing;
            RenderAndCapture(tempCamera, imageTarget, warmupFrames: 2);
            result.gtaoHistoryEntriesWithTempCamera = ReadHistoryEntryCount(gtao);
            result.sslHistoryEntriesWithTempCamera = ReadHistoryEntryCount(ssl);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(tempObject);
        }

        RenderAndCapture(camera, imageTarget, warmupFrames: 2);
        result.gtaoHistoryEntriesAfterCameraDestroy = ReadHistoryEntryCount(gtao);
        result.sslHistoryEntriesAfterCameraDestroy = ReadHistoryEntryCount(ssl);
        volumeObject.SetActive(false);
    }

    static int ReadHistoryEntryCount(ScriptableRendererFeature feature)
    {
        var store = feature.GetType()
            .GetProperty("historyStore", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(feature);
        if (store == null)
            return -1;
        var count = store.GetType()
            .GetProperty("entryCount", BindingFlags.Instance | BindingFlags.NonPublic);
        return count == null ? -1 : (int)count.GetValue(store);
    }

    static void CaptureState(
        T3ModeResult result,
        string stateName,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        ScreenSpaceLightingRendererFeature ssl,
        bool gtaoActive,
        bool sslActive,
        bool volumeActive,
        Camera camera,
        RenderTexture imageTarget,
        GameObject volumeObject,
        Color[] reference,
        string projectRoot,
        string outputDirectory,
        Type gtaoPassType,
        Type sslPassType)
    {
        gtao.SetActive(gtaoActive);
        ssl.SetActive(sslActive);
        volumeObject.SetActive(volumeActive);
        if (gtao.isActive != gtaoActive || ssl.isActive != sslActive)
            result.issues.Add(stateName + ": feature active state did not change");

        var gtaoExecutions = ReadExecuteCount(gtaoPassType);
        var sslExecutions = ReadExecuteCount(sslPassType);
        var samples = RenderAndCapture(camera, imageTarget, warmupFrames: WarmupFrames);
        result.states.Add(DescribeState(
            stateName, samples, reference,
            ReadExecuteCount(gtaoPassType) - gtaoExecutions,
            ReadExecuteCount(sslPassType) - sslExecutions));
        WriteImage(imageTarget, projectRoot, outputDirectory, "T3_" + result.name + "_" + stateName + ".png");
    }

    static int ReadExecuteCount(Type passType)
    {
        var field = passType.GetField("s_EditorExecuteCount", BindingFlags.Static | BindingFlags.NonPublic);
        return field == null ? -1 : (int)field.GetValue(null);
    }

    static Color[] RenderAndCapture(Camera camera, RenderTexture imageTarget, int warmupFrames)
    {
        for (var i = 0; i < warmupFrames; i++)
            camera.Render();
        return ReadPixels(imageTarget);
    }

    static T3StateCapture DescribeState(string stateName, Color[] samples, Color[] reference, int gtaoExecutions, int sslExecutions)
    {
        var capture = new T3StateCapture
        {
            state = stateName,
            sha256 = HashSamples(samples),
            gtaoExecutions = gtaoExecutions,
            sslExecutions = sslExecutions,
            magentaPixels = Count(samples, c => c.r > 0.98f && c.g < 0.02f && c.b > 0.98f),
            meanLuminance = Mean(samples, c => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b)
        };

        if (reference != null)
        {
            if (reference.Length != samples.Length)
            {
                capture.differingPixels = Math.Max(reference.Length, samples.Length);
                capture.maxChannelDelta = float.PositiveInfinity;
                capture.meanChannelDelta = float.PositiveInfinity;
            }
            else
            {
                double sum = 0.0;
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
                    sum += dr + dg + db;
                }
                capture.differingPixels = differing;
                capture.maxChannelDelta = maxDelta;
                capture.meanChannelDelta = (float)(sum / (samples.Length * 3.0));
            }
        }

        capture.identicalToReference = capture.differingPixels == 0;
        return capture;
    }

    static void Evaluate(T3ModeResult result)
    {
        if (result.featureCount != 2)
            result.issues.Add($"expected exactly two renderer features, found {result.featureCount}");
        if (!result.gtaoFeatureFound || !result.sslFeatureFound)
            result.issues.Add("renderer feature types are incomplete");
        if (result.states.Count != 6)
            result.issues.Add($"expected six states, captured {result.states.Count}");

        foreach (var state in result.states)
        {
            if (state.state == "T1Baseline")
                continue;
            if (state.magentaPixels > 0)
                result.issues.Add($"{state.state}: image contains error-shader magenta ({state.magentaPixels} pixels)");
            if (!state.identicalToReference)
                result.issues.Add($"{state.state}: image differs from the T1 baseline ({state.differingPixels} pixels, max channel delta {state.maxChannelDelta:G4})");

            switch (state.state)
            {
                case "T1Baseline":
                case "FeaturesOff_VolumeOn":
                    if (state.gtaoExecutions != 0 || state.sslExecutions != 0)
                        result.issues.Add($"{state.state}: skeleton passes executed while disabled ({state.gtaoExecutions}/{state.sslExecutions})");
                    break;
                case "FeaturesOn_VolumeOff":
                    if (state.gtaoExecutions <= 0)
                        result.issues.Add("FeaturesOn_VolumeOff: the GTAO pass did not execute");
                    if (state.sslExecutions != 0)
                        result.issues.Add("FeaturesOn_VolumeOff: the SSL pass executed without an enabled volume effect");
                    break;
                case "FeaturesOn_VolumeOn":
                    if (state.gtaoExecutions <= 0 || state.sslExecutions <= 0)
                        result.issues.Add("FeaturesOn_VolumeOn: both skeleton passes must execute");
                    break;
                case "GtaoOnly":
                    if (state.gtaoExecutions <= 0)
                        result.issues.Add("GtaoOnly: the GTAO pass did not execute");
                    if (state.sslExecutions != 0)
                        result.issues.Add("GtaoOnly: the SSL pass executed while its feature was disabled");
                    break;
                case "SslOnly":
                    if (state.gtaoExecutions != 0)
                        result.issues.Add("SslOnly: the GTAO pass executed while its feature was disabled");
                    if (state.sslExecutions <= 0)
                        result.issues.Add("SslOnly: the SSL pass did not execute");
                    break;
            }
        }

        var referenceState = result.states.FirstOrDefault(state => state.state == "T1Baseline");
        if (referenceState == null || referenceState.magentaPixels > 0)
            result.issues.Add("the T1 reference image is missing or invalid");

        var emptyPassState = result.states.FirstOrDefault(state => state.state == "FeaturesOn_VolumeOn");
        if (emptyPassState == null)
            result.issues.Add("the effects-enabled state was not captured");

        if (result.gtaoHistoryEntries != 1 || result.sslHistoryEntries != 1)
            result.issues.Add($"main camera history entries are wrong ({result.gtaoHistoryEntries}/{result.sslHistoryEntries})");
        if (result.gtaoHistoryEntriesWithTempCamera != 2 || result.sslHistoryEntriesWithTempCamera != 2)
            result.issues.Add($"second camera did not add history entries ({result.gtaoHistoryEntriesWithTempCamera}/{result.sslHistoryEntriesWithTempCamera})");
        if (result.gtaoHistoryEntriesAfterCameraDestroy != 1 || result.sslHistoryEntriesAfterCameraDestroy != 1)
            result.issues.Add($"destroyed camera history was not pruned ({result.gtaoHistoryEntriesAfterCameraDestroy}/{result.sslHistoryEntriesAfterCameraDestroy})");

        result.passed = result.issues.Count == 0;
    }

    static void EnsureAssets(bool force)
    {
        if (SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before creating the T3 assets.");

        AssetDatabase.Refresh();
        var t3Profile = EnsureT3Profile();
        var t3Pipelines = new UniversalRenderPipelineAsset[ModeNames.Length];
        for (var i = 0; i < ModeNames.Length; i++)
        {
            var renderer = EnsureRenderer(ModeNames[i], force);
            t3Pipelines[i] = EnsurePipeline(ModeNames[i], renderer, force);
        }

        EnsureT3Scene(t3Pipelines, t3Profile);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static UniversalRendererData EnsureRenderer(string modeName, bool force)
    {
        var t1Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(string.Format(T1RendererPathFormat, modeName));
        if (t1Renderer == null)
            throw new FileNotFoundException("Missing T1 renderer asset. Create the T1 baseline first.");

        var rendererPath = string.Format(T3RendererPathFormat, modeName);
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        if (renderer == null || force)
        {
            if (renderer != null)
                AssetDatabase.DeleteAsset(rendererPath);
            renderer = UnityEngine.Object.Instantiate(t1Renderer);
            renderer.name = "SslT3" + modeName + "Renderer";
            AssetDatabase.CreateAsset(renderer, rendererPath);
        }

        renderer.name = "SslT3" + modeName + "Renderer";
        var gtao = renderer.rendererFeatures.OfType<GroundTruthAmbientOcclusionRendererFeature>().FirstOrDefault();
        var ssl = renderer.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
        if (gtao != null && ssl != null && renderer.rendererFeatures.Count == 2 && !force)
        {
            // Keep the serialized objects so a fresh editor domain proves they deserialize.
            renderer.rendererFeatures.Clear();
            renderer.rendererFeatures.Add(gtao);
            renderer.rendererFeatures.Add(ssl);
            EnsureFeatureMap(renderer, gtao, ssl);
            return renderer;
        }

        foreach (var existing in AssetDatabase.LoadAllAssetsAtPath(rendererPath))
        {
            if (existing is GroundTruthAmbientOcclusionRendererFeature || existing is ScreenSpaceLightingRendererFeature)
                AssetDatabase.RemoveObjectFromAsset(existing);
        }

        gtao = ScriptableObject.CreateInstance<GroundTruthAmbientOcclusionRendererFeature>();
        gtao.name = "Ground Truth Ambient Occlusion";
        AssetDatabase.AddObjectToAsset(gtao, renderer);
        ssl = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
        ssl.name = "Screen Space Lighting";
        AssetDatabase.AddObjectToAsset(ssl, renderer);

        renderer.rendererFeatures.Clear();
        renderer.rendererFeatures.Add(gtao);
        renderer.rendererFeatures.Add(ssl);
        EnsureFeatureMap(renderer, gtao, ssl);
        EditorUtility.SetDirty(renderer);
        renderer.SetDirty();
        return renderer;
    }

    static void EnsureFeatureMap(
        UniversalRendererData renderer,
        GroundTruthAmbientOcclusionRendererFeature gtao,
        ScreenSpaceLightingRendererFeature ssl)
    {
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(gtao, out _, out long gtaoId);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ssl, out _, out long sslId);
        var serialized = new SerializedObject(renderer);
        var map = serialized.FindProperty("m_RendererFeatureMap");
        if (map == null)
            return;
        map.arraySize = 2;
        map.GetArrayElementAtIndex(0).longValue = gtaoId;
        map.GetArrayElementAtIndex(1).longValue = sslId;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(renderer);
    }

    static UniversalRenderPipelineAsset EnsurePipeline(string modeName, UniversalRendererData renderer, bool force)
    {
        var pipelinePath = string.Format(T3PipelinePathFormat, modeName);
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
        if (pipeline == null || force)
        {
            var template = UniversalRenderPipelineAsset.Create(renderer);
            if (pipeline == null)
            {
                pipeline = template;
                AssetDatabase.CreateAsset(pipeline, pipelinePath);
            }
            else
            {
                EditorUtility.CopySerialized(template, pipeline);
                UnityEngine.Object.DestroyImmediate(template);
            }
            pipeline.name = "SslT3" + modeName + "Pipeline";
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

    static VolumeProfile EnsureT3Profile()
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(T3ProfilePath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "ScreenSpaceLightingT3Profile";
            AssetDatabase.CreateAsset(profile, T3ProfilePath);
        }

        EnsureVolumeComponent<ScreenSpaceGlobalIllumination>(profile).enabled.Override(true);
        EnsureVolumeComponent<ScreenSpaceReflection>(profile).enabled.Override(true);
        EnsureVolumeComponent<ScreenSpaceRefraction>(profile).enabled.Override(true);
        EditorUtility.SetDirty(profile);
        return profile;
    }

    static T EnsureVolumeComponent<T>(VolumeProfile profile) where T : VolumeComponent
    {
        var component = profile.components.OfType<T>().FirstOrDefault();
        if (component != null)
            return component;
        component = profile.Add<T>();
        AssetDatabase.AddObjectToAsset(component, profile);
        return component;
    }

    static void EnsureT3Scene(UniversalRenderPipelineAsset[] pipelines, VolumeProfile profile)
    {
        var previousScenePath = SceneManager.GetActiveScene().path;
        var scene = EditorSceneManager.OpenScene(ScreenSpaceLightingBaselineSetup.ScenePath, OpenSceneMode.Single);

        var volumeObject = GameObject.Find("Baseline Volume");
        var volume = volumeObject != null ? volumeObject.GetComponent<Volume>() : null;
        if (volume == null)
            throw new InvalidOperationException("The T1 baseline scene has no Baseline Volume.");
        volume.sharedProfile = profile;
        volume.isGlobal = true;

        var runner = UnityEngine.Object.FindObjectOfType<ScreenSpaceLightingBaselineRunner>();
        if (runner == null)
            throw new InvalidOperationException("The T1 baseline scene has no baseline runner.");
        runner.pipelines = pipelines;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, T3ScenePath);
        if (!string.IsNullOrEmpty(previousScenePath))
            EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
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

    static void WriteImage(RenderTexture target, string projectRoot, string outputDirectory, string fileName)
    {
        var samples = ReadPixels(target);
        var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, false);
        try
        {
            var pixels = new Color32[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                // The render target is linear HDR; encode to displayable sRGB for visual review.
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

    [Serializable]
    sealed class T3Report
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public bool completed;
        public bool passed = true;
        public string error;
        public List<T3ModeResult> modes;
    }

    [Serializable]
    sealed class T3ModeResult
    {
        public string name;
        public bool passed;
        public List<string> issues;
        public int featureCount;
        public bool gtaoFeatureFound;
        public bool sslFeatureFound;
        public int gtaoHistoryEntries;
        public int sslHistoryEntries;
        public int gtaoHistoryEntriesWithTempCamera;
        public int sslHistoryEntriesWithTempCamera;
        public int gtaoHistoryEntriesAfterCameraDestroy;
        public int sslHistoryEntriesAfterCameraDestroy;
        public List<T3StateCapture> states;
    }

    [Serializable]
    sealed class T3StateCapture
    {
        public string state;
        public string sha256;
        public bool identicalToReference;
        public int gtaoExecutions;
        public int sslExecutions;
        public int differingPixels;
        public float maxChannelDelta;
        public float meanChannelDelta;
        public int magentaPixels;
        public float meanLuminance;
    }
}
