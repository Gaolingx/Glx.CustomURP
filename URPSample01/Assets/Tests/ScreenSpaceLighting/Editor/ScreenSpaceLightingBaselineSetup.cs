using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public static class ScreenSpaceLightingBaselineSetup
{
    public const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingBaseline.unity";
    public const int Seed = 14012;

    const string Root = "Assets/Tests/ScreenSpaceLighting";
    const string RendererRoot = Root + "/Renderers";
    const string MaterialRoot = Root + "/Materials";
    const string ProfileRoot = Root + "/Profiles";
    const string SourceRendererPath = "Assets/Settings/URP-HighFidelity-Renderer.asset";

    [MenuItem("Tests/Screen Space Lighting/Create T1 Baseline")]
    public static void CreateBaseline()
    {
        if (SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before generating the T1 baseline.");

        var previousScenePath = SceneManager.GetActiveScene().path;
        AssetDatabase.Refresh();

        var modes = new[]
        {
            new ModeDefinition("Forward", RenderingMode.Forward),
            new ModeDefinition("ForwardPlus", RenderingMode.ForwardPlus),
            new ModeDefinition("Deferred", RenderingMode.Deferred)
        };
        var pipelines = new UniversalRenderPipelineAsset[modes.Length];

        for (var i = 0; i < modes.Length; i++)
        {
            var renderer = CreateRenderer(modes[i]);
            pipelines[i] = CreatePipeline(modes[i].Name, renderer);
        }

        var profile = GetOrCreateAsset<VolumeProfile>(ProfileRoot + "/ScreenSpaceLightingBaselineProfile.asset");
        var materials = CreateMaterials();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        BuildScene(scene, pipelines, profile, materials);
        EditorSceneManager.SaveScene(scene, ScenePath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (!string.IsNullOrEmpty(previousScenePath))
            EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);

        Debug.Log("T1 baseline assets created with seed " + Seed + ". Existing project settings were not changed.");
    }

    [MenuItem("Tests/Screen Space Lighting/Build T1 Baseline Player")]
    public static void BuildBaselinePlayer()
    {
        CreateBaseline();

        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, "PLAN", "Done", "01-ScreenSpaceLighting", "validation", "T1", "Player");
        Directory.CreateDirectory(outputDirectory);
        var executablePath = Path.Combine(outputDirectory, "SslBaselinePlayer.exe");
        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = executablePath,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.Development
        };
        var previousFrameTimingStats = PlayerSettings.enableFrameTimingStats;
        var previousGraphicsPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityLevel = QualitySettings.GetQualityLevel();
        var previousQualityPipelines = new RenderPipelineAsset[3];
        var pipelines = new[]
        {
            AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(RendererRoot + "/SslForwardPipeline.asset"),
            AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(RendererRoot + "/SslForwardPlusPipeline.asset"),
            AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(RendererRoot + "/SslDeferredPipeline.asset")
        };
        BuildReport report;
        try
        {
            for (var i = 0; i < pipelines.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                previousQualityPipelines[i] = QualitySettings.renderPipeline;
                QualitySettings.renderPipeline = pipelines[i];
            }
            QualitySettings.SetQualityLevel(previousQualityLevel, false);
            GraphicsSettings.defaultRenderPipeline = pipelines[0];
            PlayerSettings.enableFrameTimingStats = true;
            report = BuildPipeline.BuildPlayer(options);
        }
        finally
        {
            for (var i = 0; i < pipelines.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = previousQualityPipelines[i];
            }
            QualitySettings.SetQualityLevel(previousQualityLevel, false);
            GraphicsSettings.defaultRenderPipeline = previousGraphicsPipeline;
            PlayerSettings.enableFrameTimingStats = previousFrameTimingStats;
        }
        var summary = report.summary;
        var summaryPath = Path.Combine(projectRoot, "PLAN", "Done", "01-ScreenSpaceLighting", "validation", "T1", "BuildSummary.json");
        var data = new BuildSummaryData
        {
            result = summary.result.ToString(),
            totalErrors = summary.totalErrors,
            totalWarnings = summary.totalWarnings,
            totalSizeBytes = summary.totalSize,
            totalTimeSeconds = summary.totalTime.TotalSeconds,
            executablePath = executablePath
        };
        File.WriteAllText(summaryPath, JsonUtility.ToJson(data, true));

        if (summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("T1 baseline Player build failed. See " + summaryPath);

        Debug.Log("T1 baseline Player built at " + executablePath);
    }

    static UniversalRendererData CreateRenderer(ModeDefinition mode)
    {
        var path = RendererRoot + "/Ssl" + mode.Name + "Renderer.asset";
        var source = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(SourceRendererPath);
        if (source == null)
            throw new FileNotFoundException("Missing source Renderer Data.", SourceRendererPath);

        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
        if (renderer == null)
        {
            renderer = UnityEngine.Object.Instantiate(source);
            AssetDatabase.CreateAsset(renderer, path);
        }
        else
        {
            EditorUtility.CopySerialized(source, renderer);
        }

        renderer.name = "Ssl" + mode.Name + "Renderer";
        renderer.renderingMode = mode.RenderingMode;
        renderer.rendererFeatures.Clear();
        var serializedRenderer = new SerializedObject(renderer);
        var featureMap = serializedRenderer.FindProperty("m_RendererFeatureMap");
        if (featureMap != null)
            featureMap.longValue = 0;
        serializedRenderer.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(renderer);
        return renderer;
    }

    static UniversalRenderPipelineAsset CreatePipeline(string name, UniversalRendererData renderer)
    {
        var path = RendererRoot + "/Ssl" + name + "Pipeline.asset";
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
        {
            pipeline = UniversalRenderPipelineAsset.Create(renderer);
            AssetDatabase.CreateAsset(pipeline, path);
        }
        else
        {
            var template = UniversalRenderPipelineAsset.Create(renderer);
            EditorUtility.CopySerialized(template, pipeline);
            UnityEngine.Object.DestroyImmediate(template);
        }

        pipeline.name = "Ssl" + name + "Pipeline";
        pipeline.supportsHDR = true;
        pipeline.msaaSampleCount = 1;
        pipeline.renderScale = 1f;
        pipeline.shadowDistance = 50f;
        pipeline.supportsCameraDepthTexture = false;
        pipeline.supportsCameraOpaqueTexture = false;
        EditorUtility.SetDirty(pipeline);
        return pipeline;
    }

    static MaterialSet CreateMaterials()
    {
        return new MaterialSet
        {
            Ground = CreateLitMaterial("Ground", new Color(0.32f, 0.34f, 0.38f), 0f, 0.25f),
            Diffuse = CreateLitMaterial("Diffuse", new Color(0.65f, 0.12f, 0.08f), 0f, 0f),
            Metal = CreateLitMaterial("Metal", new Color(0.75f, 0.78f, 0.82f), 1f, 0.8f),
            RoughMetal = CreateLitMaterial("RoughMetal", new Color(0.58f, 0.42f, 0.18f), 1f, 0.25f),
            SmoothDielectric = CreateLitMaterial("SmoothDielectric", new Color(0.08f, 0.32f, 0.7f), 0f, 0.95f),
            Emission = CreateLitMaterial("Emission", new Color(0.05f, 0.05f, 0.05f), 0f, 0.5f, new Color(0.1f, 1.5f, 3f)),
            Skybox = GetOrCreateMaterial(MaterialRoot + "/BaselineSkybox.mat", "Skybox/Procedural")
        };
    }

    static void BuildScene(
        Scene scene,
        UniversalRenderPipelineAsset[] pipelines,
        VolumeProfile profile,
        MaterialSet materials)
    {
        RenderSettings.skybox = materials.Skybox;
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.fog = false;

        var cameraObject = new GameObject("Baseline Camera");
        var camera = cameraObject.AddComponent<Camera>();
        cameraObject.tag = "MainCamera";
        cameraObject.transform.position = new Vector3(7.5f, 5.2f, -8.5f);
        cameraObject.transform.LookAt(new Vector3(0f, 0.8f, 1.5f));
        camera.fieldOfView = 55f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 100f;
        camera.allowHDR = true;
        camera.allowMSAA = false;
        var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.renderPostProcessing = false;
        cameraData.antialiasing = AntialiasingMode.None;

        var lightObject = new GameObject("Directional Light");
        var directional = lightObject.AddComponent<Light>();
        directional.type = LightType.Directional;
        directional.color = new Color(1f, 0.93f, 0.82f);
        directional.intensity = 1.4f;
        directional.shadows = LightShadows.Soft;
        lightObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
        RenderSettings.sun = directional;

        CreatePointLight("Point Light Red", new Vector3(-3.5f, 2.5f, 1f), new Color(1f, 0.18f, 0.08f), 3f);
        CreatePointLight("Point Light Blue", new Vector3(3.5f, 2.2f, 3f), new Color(0.08f, 0.35f, 1f), 3f);

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(1.4f, 1f, 1.4f);
        ground.GetComponent<Renderer>().sharedMaterial = materials.Ground;

        var random = new System.Random(Seed);
        var sphereMaterials = new[] { materials.Diffuse, materials.Metal, materials.RoughMetal, materials.SmoothDielectric };
        for (var z = 0; z < 3; z++)
        {
            for (var x = 0; x < 4; x++)
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "Material Sphere " + z + "_" + x;
                var jitter = (float)random.NextDouble() * 0.08f;
                sphere.transform.position = new Vector3((x - 1.5f) * 1.55f, 0.65f + jitter, z * 1.65f);
                sphere.GetComponent<Renderer>().sharedMaterial = sphereMaterials[(x + z) % sphereMaterials.Length];
            }
        }

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Emission Cube";
        cube.transform.position = new Vector3(0f, 0.75f, 5.2f);
        cube.transform.rotation = Quaternion.Euler(0f, 35f, 0f);
        cube.GetComponent<Renderer>().sharedMaterial = materials.Emission;

        var volumeObject = new GameObject("Baseline Volume");
        var volume = volumeObject.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 0f;
        volume.sharedProfile = profile;

        var runnerObject = new GameObject("T1 Baseline Runner");
        var runner = runnerObject.AddComponent<ScreenSpaceLightingBaselineRunner>();
        runner.pipelines = pipelines;
        runner.baselineCamera = camera;
        runner.warmupFrames = 60;
        runner.sampleFrames = ScreenSpaceLightingBaselineRunner.DefaultSampleFrames;
        runner.randomSeed = Seed;

        if (!scene.IsValid())
            throw new InvalidOperationException("T1 baseline scene was not created correctly.");
    }

    static void CreatePointLight(string name, Vector3 position, Color color, float intensity)
    {
        var lightObject = new GameObject(name);
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = color;
        light.intensity = intensity;
        light.range = 8f;
        light.shadows = LightShadows.None;
        lightObject.transform.position = position;
    }

    static Material CreateLitMaterial(string name, Color color, float metallic, float smoothness, Color emission = default)
    {
        var material = GetOrCreateMaterial(MaterialRoot + "/" + name + ".mat", "Universal Render Pipeline/Lit");
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        material.SetColor("_EmissionColor", emission);
        if (emission.maxColorComponent > 0f)
            material.EnableKeyword("_EMISSION");
        else
            material.DisableKeyword("_EMISSION");
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material GetOrCreateMaterial(string path, string shaderName)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find(shaderName));
            AssetDatabase.CreateAsset(material, path);
        }
        else
        {
            material.shader = Shader.Find(shaderName);
        }
        material.name = Path.GetFileNameWithoutExtension(path);
        return material;
    }

    static T GetOrCreateAsset<T>(string path) where T : ScriptableObject
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset != null)
            return asset;
        asset = ScriptableObject.CreateInstance<T>();
        asset.name = Path.GetFileNameWithoutExtension(path);
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    readonly struct ModeDefinition
    {
        public readonly string Name;
        public readonly RenderingMode RenderingMode;

        public ModeDefinition(string name, RenderingMode renderingMode)
        {
            Name = name;
            RenderingMode = renderingMode;
        }
    }

    sealed class MaterialSet
    {
        public Material Ground;
        public Material Diffuse;
        public Material Metal;
        public Material RoughMetal;
        public Material SmoothDielectric;
        public Material Emission;
        public Material Skybox;
    }

    [Serializable]
    sealed class BuildSummaryData
    {
        public string result;
        public int totalErrors;
        public int totalWarnings;
        public ulong totalSizeBytes;
        public double totalTimeSeconds;
        public string executablePath;
    }
}
