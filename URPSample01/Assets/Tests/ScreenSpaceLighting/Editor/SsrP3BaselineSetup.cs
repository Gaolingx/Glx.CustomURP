using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// Builds the round 23 P0 baseline fixture and its Player:
//   Assets/Tests/RayTracing/SSR/Fixture/SsrP3Baseline.unity
//   Assets/Tests/RayTracing/SSR/Fixture/SsrP3BaselineProfile.asset
//   Assets/Tests/RayTracing/SSR/Fixture/SsrP3DeferredRenderer.asset
//   Assets/Tests/RayTracing/SSR/Fixture/SsrP3DeferredPipeline.asset
// Evidence goes to PLAN/TODO/23-Ray_Tracing_Inprove_2/validation/P0.
public static class SsrP3BaselineSetup
{
    const string BaselineScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingBaseline.unity";
    const string SourceRendererPath = "Assets/Tests/ScreenSpaceLighting/Renderers/SslT3UiDemoRenderer.asset";
    const string FixtureRoot = "Assets/Tests/RayTracing/SSR/Fixture";
    const string FixtureScenePath = FixtureRoot + "/SsrP3Baseline.unity";
    const string ProfilePath = FixtureRoot + "/SsrP3BaselineProfile.asset";
    const string RendererPath = FixtureRoot + "/SsrP3DeferredRenderer.asset";
    const string PipelinePath = FixtureRoot + "/SsrP3DeferredPipeline.asset";
    const string EvidenceRoot = "PLAN/TODO/23-Ray_Tracing_Inprove_2/validation/P0";
    const string PlayerFolder = EvidenceRoot + "/Player";
    const string ExecutableName = "SsrP3Baseline.exe";
    const int FrameWidth = 1920;
    const int FrameHeight = 1080;
    const int WarmupRenders = 120;
    const int CaptureRender = 121;

    [MenuItem("Tests/Ray Tracing/SSR P3 Baseline/Create Fixture")]
    public static void CreateFixtureMenu()
    {
        CreateFixture();
        AssetDatabase.SaveAssets();
        Debug.Log("P3 baseline fixture created at " + FixtureRoot);
    }

    [MenuItem("Tests/Ray Tracing/SSR P3 Baseline/Build Player")]
    public static void BuildPlayerMenu()
    {
        BuildPlayer();
    }

    [MenuItem("Tests/Ray Tracing/SSR P3 Baseline/Render Editor Check")]
    public static void RenderEditorCheck()
    {
        var scene = EditorSceneManager.OpenScene(FixtureScenePath, OpenSceneMode.Single);
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
        if (pipeline == null)
            throw new FileNotFoundException("Missing P3 pipeline.", PipelinePath);
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        var camera = FindInScene<Camera>(scene, "Baseline Camera");
        if (camera == null)
            throw new InvalidOperationException("Missing baseline camera.");
        foreach (var other in UnityEngine.Object.FindObjectsOfType<Camera>(true))
            other.enabled = false;

        const int width = 960;
        const int height = 540;
        var target = new RenderTexture(new RenderTextureDescriptor(width, height,
            UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, 24)
        {
            msaaSamples = 1,
            useDynamicScale = false
        });
        if (!target.Create())
            throw new InvalidOperationException("Editor check target was not created.");
        camera.targetTexture = target;
        camera.aspect = (float)width / height;
        for (var i = 0; i < 130; i++)
            camera.Render();

        var readback = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);
        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            readback.Apply(false);
        }
        finally
        {
            RenderTexture.active = previous;
        }

        var pixels = readback.GetPixels();
        UnityEngine.Object.DestroyImmediate(readback);
        var display = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        var encoded = new Color32[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            encoded[i] = new Color32(
                EncodeDisplay(c.r), EncodeDisplay(c.g), EncodeDisplay(c.b), 255);
        }
        display.SetPixels32(encoded);
        display.Apply(false);

        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var path = Path.Combine(projectRoot, EvidenceRoot, "editor-check.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, display.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(display);

        camera.targetTexture = null;
        target.Release();
        UnityEngine.Object.DestroyImmediate(target);
        Debug.Log("P3 editor check written to " + path);
    }

    static byte EncodeDisplay(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
            return 0;
        var mapped = value / (1f + value);
        return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Pow(mapped, 1f / 2.2f) * 255f), 0, 255);
    }

    public static void CreateFixture()
    {
        EnsureFolder(FixtureRoot);
        var profile = CreateProfile();
        var renderer = CreateRenderer();
        var pipeline = CreatePipeline(renderer);
        CreateScene(profile, pipeline);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        WriteFixtureJson(profile, renderer, pipeline);
    }

    public static void BuildPlayer()
    {
        var previousApis = ReadWindowsApis(out var previousAutomaticApis);
        var previousDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityLevel = QualitySettings.GetQualityLevel();
        var previousQualityPipelines = new RenderPipelineAsset[QualitySettings.names.Length];
        for (var i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            previousQualityPipelines[i] = QualitySettings.renderPipeline;
        }
        QualitySettings.SetQualityLevel(previousQualityLevel, false);

        CreateFixture();

        var fixturePipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
        if (fixturePipeline == null)
            throw new FileNotFoundException("Missing P3 pipeline.", PipelinePath);

        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, PlayerFolder);
        Directory.CreateDirectory(outputDirectory);
        var executablePath = Path.Combine(outputDirectory, ExecutableName);

        const BuildTarget target = BuildTarget.StandaloneWindows64;
        var previousColorSpace = PlayerSettings.colorSpace;
        var previousFrameTimingStats = PlayerSettings.enableFrameTimingStats;
        var pipelineSnapshots = SnapshotAssets(
            "Assets/Settings/URP-Balanced.asset",
            "Assets/Settings/URP-HighFidelity.asset",
            "Assets/Settings/URP-Performant.asset");
        BuildReport report;
        try
        {
            if (PlayerSettings.colorSpace != ColorSpace.Linear)
                throw new InvalidOperationException("The P3 baseline requires the Linear color space.");
            // The project's automatic API list builds Windows Standalone for Direct3D11 only, which
            // leaves the Direct3D12 compute kernels out of the Player even when it is launched with
            // -force-d3d12. Include both APIs explicitly and restore the exact project value below.
            PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
            PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12 });
            PlayerSettings.enableFrameTimingStats = true;

            // The build strips shader variants from every pipeline the target references. The project
            // default is Forward, so a fixture that only assigns the Deferred pipeline at runtime
            // would ship without the deferred GBuffer variants and render black geometry.
            GraphicsSettings.defaultRenderPipeline = fixturePipeline;
            for (var i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = fixturePipeline;
            }
            QualitySettings.SetQualityLevel(previousQualityLevel, false);

            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { FixtureScenePath },
                locationPathName = executablePath,
                target = target,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.Development
            });
        }
        finally
        {
            for (var i = 0; i < previousQualityPipelines.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = previousQualityPipelines[i];
            }
            QualitySettings.SetQualityLevel(previousQualityLevel, false);
            GraphicsSettings.defaultRenderPipeline = previousDefaultPipeline;
            // Restore the explicit list first, then the automatic flag: setting the flag alone keeps
            // the build-time pair and changes the committed project value.
            PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
            if (previousApis.Length > 0)
                PlayerSettings.SetGraphicsAPIs(target, previousApis);
            PlayerSettings.SetUseDefaultGraphicsAPIs(target, previousAutomaticApis);
            PlayerSettings.colorSpace = previousColorSpace;
            PlayerSettings.enableFrameTimingStats = previousFrameTimingStats;
            AssetDatabase.SaveAssets();

            WriteRestoreReport(projectRoot, previousDefaultPipeline, previousQualityPipelines, previousAutomaticApis, previousApis);
            RestoreAssets(pipelineSnapshots);
        }

        var summary = report.summary;
        File.WriteAllText(Path.Combine(outputDirectory, "BuildSummary.json"), JsonUtility.ToJson(new BuildSummary
        {
            result = summary.result.ToString(),
            totalErrors = summary.totalErrors,
            totalWarnings = summary.totalWarnings,
            totalSizeBytes = summary.totalSize,
            totalTimeSeconds = summary.totalTime.TotalSeconds,
            executablePath = executablePath,
            fixtureScene = FixtureScenePath
        }, true));

        if (summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("P3 baseline Player build failed. See BuildSummary.json.");
        Debug.Log("P3 baseline Player built at " + executablePath);
    }

    static VolumeProfile CreateProfile()
    {
        if (AssetDatabase.LoadMainAssetAtPath(ProfilePath) != null)
            AssetDatabase.DeleteAsset(ProfilePath);

        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.name = "SsrP3BaselineProfile";
        AssetDatabase.CreateAsset(profile, ProfilePath);

        var ssr = profile.Add<ScreenSpaceReflection>(true);
        ssr.name = "ScreenSpaceReflection";
        ssr.enabled.Override(true);
        ssr.enabledTransparent.Override(false);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.PBRAccumulation);
        ssr.fullResolution.Override(false);
        ssr.minSmoothness.Override(0.9f);
        ssr.smoothnessFadeStart.Override(0.9f);
        ssr.depthBufferThickness.Override(0.01f);
        ssr.screenFadeDistance.Override(0.1f);
        ssr.rayMaxIterations.Override(64);
        ssr.reflectSky.Override(true);
        ssr.rayLength.Override(50f);
        ssr.intensity.Override(1f);
        ssr.biasFactor.Override(0.5f);
        ssr.denoise.Override(true);
        ssr.denoiserRadius.Override(8);
        ssr.affectSmoothSurfaces.Override(false);
        ssr.singleReflectionBounce.Override(false);

        AssetDatabase.AddObjectToAsset(ssr, profile);
        EditorUtility.SetDirty(profile);
        EditorUtility.SetDirty(ssr);
        AssetDatabase.SaveAssets();
        return profile;
    }

    static UniversalRendererData CreateRenderer()
    {
        if (AssetDatabase.LoadMainAssetAtPath(RendererPath) != null)
            AssetDatabase.DeleteAsset(RendererPath);

        var source = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(SourceRendererPath);
        if (source == null)
            throw new FileNotFoundException("Missing source renderer data.", SourceRendererPath);

        var renderer = UnityEngine.Object.Instantiate(source);
        renderer.name = "SsrP3DeferredRenderer";
        renderer.rendererFeatures.Clear();
        renderer.renderingMode = RenderingMode.Deferred;
        AssetDatabase.CreateAsset(renderer, RendererPath);

        var feature = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
        feature.name = "ScreenSpaceLightingRendererFeature";
        AssetDatabase.AddObjectToAsset(feature, renderer);
        renderer.rendererFeatures.Add(feature);

        var featureSettings = new SerializedObject(feature);
        var settings = featureSettings.FindProperty("m_Settings");
        if (settings == null)
            throw new InvalidOperationException("ScreenSpaceLightingRendererFeature.m_Settings was not found.");
        settings.FindPropertyRelative("rayTracingReflections").boolValue = true;
        settings.FindPropertyRelative("reflectionRayBudget").intValue = 32768;
        var hitDeferred = settings.FindPropertyRelative("hitDeferredShading");
        if (hitDeferred == null)
            throw new MissingFieldException("ScreenSpaceLightingSettings.hitDeferredShading was not found.");
        hitDeferred.boolValue = true;
        featureSettings.ApplyModifiedPropertiesWithoutUndo();

        EnsureFeatureResources(feature);

        var rendererObject = new SerializedObject(renderer);
        var map = rendererObject.FindProperty("m_RendererFeatureMap");
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out var featureGuid, out long localId);
        map.arraySize = 1;
        map.GetArrayElementAtIndex(0).longValue = localId;
        rendererObject.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(renderer);
        EditorUtility.SetDirty(feature);
        AssetDatabase.SaveAssets();
        return renderer;
    }

    static void EnsureFeatureResources(ScreenSpaceLightingRendererFeature feature)
    {
        var settingsField = typeof(ScreenSpaceLightingRendererFeature).GetField("m_Settings", BindingFlags.Instance | BindingFlags.NonPublic);
        var settings = settingsField?.GetValue(feature);
        if (settings == null)
            throw new InvalidOperationException("ScreenSpaceLightingRendererFeature.m_Settings was not readable.");
        var ensure = settings.GetType().GetMethod("EnsureResources", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (ensure == null)
            throw new MissingMethodException("ScreenSpaceLightingSettings.EnsureResources was not found.");
        ensure.Invoke(settings, null);

        var resources = settings.GetType().GetField("resources", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(settings);
        var rayShader = resources?.GetType().GetField("rayTracingSsr", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(resources);
        if (rayShader == null)
            throw new InvalidOperationException("resources.rayTracingSsr was not resolved; the Player would silently skip DXR.");

        // Reload-attribute defaults are filled when the feature is inspected, not when it is
        // created by script; resolve the hit kernel here so the Player never ships without it.
        var hitField = resources?.GetType().GetField("ssrHitDeferredCompute", BindingFlags.Instance | BindingFlags.NonPublic);
        if (hitField == null)
            throw new MissingFieldException("ScreenSpaceLightingResources.ssrHitDeferredCompute was not found.");
        var hitKernel = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/SsrHitDeferred.compute");
        if (hitKernel == null)
            throw new InvalidOperationException("SsrHitDeferred.compute was not found; hit shading would fall back to closest-hit.");
        if (hitField.GetValue(resources) == null)
            hitField.SetValue(resources, hitKernel);
    }

    static UniversalRenderPipelineAsset CreatePipeline(UniversalRendererData renderer)
    {
        if (AssetDatabase.LoadMainAssetAtPath(PipelinePath) != null)
            AssetDatabase.DeleteAsset(PipelinePath);

        const string sourcePipeline = "Assets/Settings/URP-HighFidelity.asset";
        UniversalRenderPipelineAsset pipeline;
        if (AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(sourcePipeline) != null &&
            AssetDatabase.CopyAsset(sourcePipeline, PipelinePath))
        {
            pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            var serialized = new SerializedObject(pipeline);
            var list = serialized.FindProperty("m_RendererDataList");
            list.arraySize = 1;
            list.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            serialized.FindProperty("m_DefaultRendererIndex").intValue = 0;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        else
        {
            pipeline = UniversalRenderPipelineAsset.Create(renderer);
            AssetDatabase.CreateAsset(pipeline, PipelinePath);
        }

        pipeline.name = "SsrP3DeferredPipeline";
        pipeline.supportsHDR = true;
        pipeline.msaaSampleCount = 1;
        pipeline.renderScale = 1f;
        pipeline.shadowDistance = 50f;
        EditorUtility.SetDirty(pipeline);
        AssetDatabase.SaveAssets();
        return pipeline;
    }

    static void CreateScene(VolumeProfile profile, UniversalRenderPipelineAsset pipeline)
    {
        var previousScenePath = SceneManager.GetActiveScene().path;
        var scene = EditorSceneManager.OpenScene(BaselineScenePath, OpenSceneMode.Single);

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var runner in root.GetComponentsInChildren<ScreenSpaceLightingBaselineRunner>(true))
                UnityEngine.Object.DestroyImmediate(runner.gameObject);
        }

        var camera = FindInScene<Camera>(scene, "Baseline Camera");
        if (camera == null)
            throw new InvalidOperationException("The baseline scene is missing 'Baseline Camera'.");
        camera.allowHDR = true;
        camera.allowMSAA = false;
        camera.allowDynamicResolution = false;
        camera.aspect = (float)FrameWidth / FrameHeight;
        var data = camera.GetUniversalAdditionalCameraData();
        data.antialiasing = AntialiasingMode.None;
        data.renderPostProcessing = false;

        var volume = FindInScene<Volume>(scene, "Baseline Volume");
        if (volume == null)
            throw new InvalidOperationException("The baseline scene is missing 'Baseline Volume'.");
        volume.sharedProfile = profile;

        var ground = FindInScene<MeshRenderer>(scene, "Ground");
        if (ground == null)
            throw new InvalidOperationException("The baseline scene is missing 'Ground'.");
        ground.sharedMaterial = CreateGroundMaterial();

        var rayTracingInstances = RegisterRayTracingGeometry(scene);

        var capture = GameObject.Find("P3 Baseline Capture");
        if (capture == null)
            capture = new GameObject("P3 Baseline Capture");
        var driver = capture.GetComponent<SsrP3BaselineCapture>();
        if (driver == null)
            driver = capture.AddComponent<SsrP3BaselineCapture>();
        driver.pipeline = pipeline;
        driver.captureCamera = camera;
        driver.captureVolume = volume;
        driver.width = FrameWidth;
        driver.height = FrameHeight;
        driver.warmupRenders = WarmupRenders;
        driver.captureRender = CaptureRender;

        EditorSceneManager.SaveScene(scene, FixtureScenePath);

        if (!string.IsNullOrEmpty(previousScenePath) && previousScenePath != FixtureScenePath)
            EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);

        m_LastRayTracingInstances = rayTracingInstances;
    }

    static int m_LastRayTracingInstances;

    static Material CreateGroundMaterial()
    {
        const string path = FixtureRoot + "/SsrP3Ground.mat";
        if (AssetDatabase.LoadMainAssetAtPath(path) != null)
            AssetDatabase.DeleteAsset(path);

        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            throw new InvalidOperationException("The Lit shader is not available.");
        var material = new Material(shader) { name = "SsrP3Ground" };
        material.SetColor("_BaseColor", new Color(0.32f, 0.34f, 0.38f));
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Smoothness", 0.95f);
        AssetDatabase.CreateAsset(material, path);
        EditorUtility.SetDirty(material);
        return material;
    }

    static int RegisterRayTracingGeometry(Scene scene)
    {
        var renderers = new List<MeshRenderer>();
        foreach (var root in scene.GetRootGameObjects())
            renderers.AddRange(root.GetComponentsInChildren<MeshRenderer>(true));
        renderers.Sort((a, b) => string.CompareOrdinal(a.gameObject.name, b.gameObject.name));

        var count = 0;
        foreach (var renderer in renderers)
        {
            var geometry = renderer.GetComponent<SsrRayTracingGeometry>();
            if (geometry == null)
                geometry = renderer.gameObject.AddComponent<SsrRayTracingGeometry>();
            geometry.instanceId = count;
            renderer.rayTracingMode = UnityEngine.Experimental.Rendering.RayTracingMode.Static;
            count++;
        }
        return count;
    }

    static T FindInScene<T>(Scene scene, string name) where T : Component
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var component in root.GetComponentsInChildren<T>(true))
            {
                if (component.gameObject.name == name)
                    return component;
            }
        }
        return null;
    }

    static void WriteFixtureJson(VolumeProfile profile, UniversalRendererData renderer, UniversalRenderPipelineAsset pipeline)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var evidence = Path.Combine(projectRoot, EvidenceRoot);
        Directory.CreateDirectory(evidence);

        var fixture = new FixtureManifest
        {
            fixtureScene = FixtureScenePath,
            volumeProfile = ProfilePath,
            renderer = RendererPath,
            pipeline = PipelinePath,
            width = FrameWidth,
            height = FrameHeight,
            warmupRenders = WarmupRenders,
            captureRender = CaptureRender,
            rayTracingReflections = true,
            reflectionRayBudget = 32768,
            hitDeferredShading = true,
            groundMaterial = FixtureRoot + "/SsrP3Ground.mat",
            rayTracingInstances = m_LastRayTracingInstances,
            frameSceneSHA256 = HashFile(Path.Combine(projectRoot, FixtureScenePath)),
            profileSHA256 = HashFile(Path.Combine(projectRoot, ProfilePath)),
            rendererSHA256 = HashFile(Path.Combine(projectRoot, RendererPath)),
            pipelineSHA256 = HashFile(Path.Combine(projectRoot, PipelinePath)),
            groundMaterialSHA256 = HashFile(Path.Combine(projectRoot, FixtureRoot + "/SsrP3Ground.mat"))
        };
        File.WriteAllText(Path.Combine(evidence, "Fixture.json"), JsonUtility.ToJson(fixture, true));
    }

    static string HashFile(string path)
    {
        if (!File.Exists(path))
            return "missing";
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(File.ReadAllBytes(path));
        var text = new System.Text.StringBuilder(digest.Length * 2);
        foreach (var b in digest)
            text.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return text.ToString();
    }

    // Reads the committed Windows Standalone API value. GetGraphicsAPIs resolves the automatic list
    // to the platform default, so the explicit list and the automatic flag must come from the file.
    static GraphicsDeviceType[] ReadWindowsApis(out bool automatic)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var path = Path.Combine(projectRoot, "ProjectSettings/ProjectSettings.asset");
        automatic = true;
        var result = new List<GraphicsDeviceType>();
        if (!File.Exists(path))
            return result.ToArray();

        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("m_BuildTarget: WindowsStandaloneSupport"))
                continue;
            for (var j = i + 1; j < Math.Min(lines.Length, i + 6); j++)
            {
                var apis = System.Text.RegularExpressions.Regex.Match(lines[j], @"m_APIs: ([0-9a-fA-F]+)");
                if (apis.Success)
                {
                    var hex = apis.Groups[1].Value;
                    for (var k = 0; k + 8 <= hex.Length; k += 8)
                    {
                        // The file stores each uint little-endian, so the hex digits are byte-reversed.
                        var group = hex.Substring(k, 8);
                        var raw = uint.Parse(
                            new string(new[] { group[6], group[7], group[4], group[5], group[2], group[3], group[0], group[1] }),
                            System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture);
                        if (raw == 2u)
                            result.Add(GraphicsDeviceType.Direct3D11);
                        else if (raw == 18u)
                            result.Add(GraphicsDeviceType.Direct3D12);
                    }
                }
                var auto = System.Text.RegularExpressions.Regex.Match(lines[j], @"m_Automatic: (\d)");
                if (auto.Success)
                    automatic = auto.Groups[1].Value != "0";
            }
            break;
        }
        return result.ToArray();
    }

    static void WriteRestoreReport(string projectRoot, RenderPipelineAsset previousDefault,
        RenderPipelineAsset[] previousQualities, bool previousAutomaticApis, GraphicsDeviceType[] previousApis)
    {
        var currentQualities = new RenderPipelineAsset[QualitySettings.names.Length];
        var previousLevel = QualitySettings.GetQualityLevel();
        for (var i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            currentQualities[i] = QualitySettings.renderPipeline;
        }
        QualitySettings.SetQualityLevel(previousLevel, false);

        var restored = GraphicsSettings.defaultRenderPipeline == previousDefault;
        for (var i = 0; i < previousQualities.Length; i++)
            restored &= currentQualities[i] == previousQualities[i];

        // The automatic flag resolves GetGraphicsAPIs to the platform default, so compare the stored
        // list from the file instead of the effective one.
        var restoredApis = ReadWindowsApis(out var restoredAutomaticApis);
        var report = new RestoreReport
        {
            restoredPipelines = restored,
            defaultPipeline = GraphicsSettings.defaultRenderPipeline != null ? GraphicsSettings.defaultRenderPipeline.name : "null",
            qualityPipelines = currentQualities.Select(p => p != null ? p.name : "null").ToArray(),
            graphicsApis = restoredApis.Select(a => a.ToString()).ToArray(),
            automaticApis = restoredAutomaticApis,
            effectiveApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64)
                .Select(a => a.ToString()).ToArray(),
            apisRestored = restoredApis.SequenceEqual(previousApis) && restoredAutomaticApis == previousAutomaticApis
        };
        var directory = Path.Combine(projectRoot, EvidenceRoot);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SettingsRestore.json"), JsonUtility.ToJson(report, true));
        if (!restored || !report.apisRestored)
            Debug.LogError("P3 baseline build did not restore every setting; see SettingsRestore.json.");
    }

    static string[] SnapshotAssets(params string[] paths)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var snapshots = new string[paths.Length];
        for (var i = 0; i < paths.Length; i++)
        {
            var absolute = Path.Combine(projectRoot, paths[i]);
            snapshots[i] = File.Exists(absolute) ? File.ReadAllText(absolute) : string.Empty;
        }
        return snapshots;
    }

    static void RestoreAssets(string[] snapshots)
    {
        var paths = new[]
        {
            "Assets/Settings/URP-Balanced.asset",
            "Assets/Settings/URP-HighFidelity.asset",
            "Assets/Settings/URP-Performant.asset"
        };
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        for (var i = 0; i < paths.Length && i < snapshots.Length; i++)
        {
            var absolute = Path.Combine(projectRoot, paths[i]);
            if (snapshots[i].Length > 0 && File.Exists(absolute) && File.ReadAllText(absolute) != snapshots[i])
            {
                File.WriteAllText(absolute, snapshots[i]);
                AssetDatabase.ImportAsset(paths[i], ImportAssetOptions.ForceUpdate);
            }
        }
    }

    static void EnsureFolder(string path)
    {
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(absolute);
        AssetDatabase.Refresh();
    }

    [Serializable]
    sealed class FixtureManifest
    {
        public string fixtureScene;
        public string volumeProfile;
        public string renderer;
        public string pipeline;
        public int width;
        public int height;
        public int warmupRenders;
        public int captureRender;
        public bool rayTracingReflections;
        public int reflectionRayBudget;
        public bool hitDeferredShading;
        public string groundMaterial;
        public int rayTracingInstances;
        public string frameSceneSHA256;
        public string profileSHA256;
        public string rendererSHA256;
        public string pipelineSHA256;
        public string groundMaterialSHA256;
    }

    [Serializable]
    sealed class RestoreReport
    {
        public bool restoredPipelines;
        public string defaultPipeline;
        public string[] qualityPipelines;
        public string[] graphicsApis;
        public bool automaticApis;
        public string[] effectiveApis;
        public bool apisRestored;
    }

    [Serializable]
    sealed class BuildSummary
    {
        public string result;
        public int totalErrors;
        public int totalWarnings;
        public ulong totalSizeBytes;
        public double totalTimeSeconds;
        public string executablePath;
        public string fixtureScene;
    }
}
