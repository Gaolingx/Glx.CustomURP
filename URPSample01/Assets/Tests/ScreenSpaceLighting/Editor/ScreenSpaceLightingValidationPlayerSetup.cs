using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Builds the shared 06 validation player: three renderers with GTAO, Screen Space Lighting and the
// binding probe, one scene that covers every effect, and a D3D11 development player that the
// RenderDoc driver starts with -ssl-validation-manifest.
public static class ScreenSpaceLightingValidationPlayerSetup
{
    const string Root = "Assets/Tests/ScreenSpaceLighting";
    const string RendererRoot = Root + "/Renderers";
    const string MaterialRoot = Root + "/Materials";
    const string ProfileRoot = Root + "/Profiles";
    const string ScenePath = Root + "/Scenes/ScreenSpaceLightingValidationPlayer.unity";
    const string SourceRendererPath = "Assets/Settings/URP-HighFidelity-Renderer.asset";
    const string ProbeComputePath = Root + "/Shaders/ScreenSpaceLightingBindingProbe.compute";
    const string LeakProbeShaderPath = Root + "/Shaders/ScreenSpaceLightingRefractionLeakProbe.shader";
    const string GradientTexturePath = MaterialRoot + "/SslValidationGradient.png";
    const string OutputRoot = "Builds/SslValidationPlayer";

    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };

    [MenuItem("Tests/Screen Space Lighting/Create Validation Player Scene")]
    public static void CreateScene()
    {
        AssetDatabase.Refresh();

        var pipelines = new UniversalRenderPipelineAsset[ModeNames.Length];
        var renderers = new UniversalRendererData[ModeNames.Length];
        for (var i = 0; i < ModeNames.Length; i++)
        {
            renderers[i] = EnsureRenderer(ModeNames[i]);
            pipelines[i] = EnsurePipeline(ModeNames[i], renderers[i]);
        }

        var profile = EnsureProfile();
        var materials = EnsureMaterials();
        AssetDatabase.SaveAssets();

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        RenderSettings.skybox = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "/BaselineSkybox.mat");
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.fog = false;

        var cameraObject = new GameObject("Validation Camera");
        cameraObject.tag = "MainCamera";
        cameraObject.transform.position = new Vector3(0f, 2f, -7f);
        cameraObject.transform.rotation = Quaternion.identity;
        var camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 60f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 100f;
        camera.allowHDR = true;
        camera.allowMSAA = false;
        camera.clearFlags = CameraClearFlags.Skybox;
        var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.renderPostProcessing = false;
        cameraData.antialiasing = AntialiasingMode.None;

        var lightObject = new GameObject("Validation Directional Light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = new Color(1f, 0.93f, 0.82f);
        light.intensity = 1.4f;
        light.shadows = LightShadows.Soft;
        lightObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
        RenderSettings.sun = light;

        var volumeObject = new GameObject("Validation Volume");
        var volume = volumeObject.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.sharedProfile = profile;

        var rigObject = new GameObject("Validation Player");
        var verification = rigObject.AddComponent<ScreenSpaceLightingValidationPlayerVerification>();
        verification.forwardPipeline = pipelines[0];
        verification.forwardPlusPipeline = pipelines[1];
        verification.deferredPipeline = pipelines[2];
        verification.forwardRendererData = renderers[0];
        verification.forwardPlusRendererData = renderers[1];
        verification.deferredRendererData = renderers[2];
        verification.volumeProfile = profile;
        verification.bindingProbeCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ProbeComputePath);
        verification.floorMaterial = materials.floor;
        verification.metal95Material = materials.metal95;
        verification.metal80Material = materials.metal80;
        verification.metal65Material = materials.metal65;
        verification.metal40Material = materials.metal40;
        verification.emissiveMaterial = materials.emissive;
        verification.transparentSsrMaterial = materials.transparentSsr;
        verification.mirrorMaterial = materials.mirror;
        verification.gradientMaterial = materials.gradient;
        verification.glassMaterial = materials.glass;
        verification.leakProbeMaterial = materials.refractionLeakProbe;
        verification.complexLitMaterial = materials.complexLit;
        verification.simpleLitMaterial = materials.simpleLit;
        verification.bakedLitMaterial = materials.bakedLit;
        verification.unlitMaterial = materials.unlit;
        verification.shaderGraphLitMaterial = materials.shaderGraphLit;
        verification.roomFloorMaterial = materials.roomFloor;
        verification.terrainMaterial = materials.terrain;
        verification.coverageTerrainData = EnsureCoverageTerrainData(materials.terrainAlbedo);

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Validation player scene created at " + ScenePath);
    }

    [MenuItem("Tests/Screen Space Lighting/Build Validation Player")]
    public static void BuildPlayer()
    {
        CreateScene();

        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);
        var executablePath = Path.Combine(outputDirectory, "SslValidationPlayer.exe");

        // URP strips shader variants that the build-time pipeline does not use. ShaderBuildPreprocessor
        // only collects features from the pipeline assets referenced by the build target's quality
        // levels, so building from one asset drops the other renderers (Forward+ in particular). Park
        // the three validation pipelines on the quality levels for the build so all variants survive.
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(
            RendererRoot + "/SslValidationDeferredPipeline.asset");
        var validationPipelines = new[]
        {
            pipeline,
            AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(
                RendererRoot + "/SslValidationForwardPipeline.asset"),
            AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(
                RendererRoot + "/SslValidationForwardPlusPipeline.asset"),
        };
        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = executablePath,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.Development | BuildOptions.IncludeTestAssemblies,
        };

        var previousApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        var previousFullScreenMode = PlayerSettings.fullScreenMode;
        var previousWidth = PlayerSettings.defaultScreenWidth;
        var previousHeight = PlayerSettings.defaultScreenHeight;
        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousStripping = PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone);

        // renderPipeline is a property of the active quality level, so snapshot and restore each level.
        int previousQualityLevel = QualitySettings.GetQualityLevel();
        int qualityLevelCount = Mathf.Max(1, QualitySettings.names.Length);
        var previousQualityPipelines = new RenderPipelineAsset[qualityLevelCount];
        for (int i = 0; i < qualityLevelCount; i++)
        {
            QualitySettings.SetQualityLevel(i, true);
            previousQualityPipelines[i] = QualitySettings.renderPipeline;
        }
        QualitySettings.SetQualityLevel(previousQualityLevel, true);

        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, ManagedStrippingLevel.Disabled);

        BuildReport report;
        try
        {
            GraphicsSettings.defaultRenderPipeline = pipeline;
            for (int i = 0; i < qualityLevelCount; i++)
            {
                QualitySettings.SetQualityLevel(i, true);
                QualitySettings.renderPipeline = validationPipelines[i % validationPipelines.Length];
            }
            QualitySettings.SetQualityLevel(previousQualityLevel, true);
            report = BuildPipeline.BuildPlayer(options);
        }
        finally
        {
            for (int i = 0; i < qualityLevelCount; i++)
            {
                QualitySettings.SetQualityLevel(i, true);
                QualitySettings.renderPipeline = previousQualityPipelines[i];
            }
            QualitySettings.SetQualityLevel(previousQualityLevel, true);
            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, previousApis);
            PlayerSettings.fullScreenMode = previousFullScreenMode;
            PlayerSettings.defaultScreenWidth = previousWidth;
            PlayerSettings.defaultScreenHeight = previousHeight;
            PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, previousStripping);
        }

        var summary = report.summary;
        File.WriteAllText(Path.Combine(outputDirectory, "BuildSummary.json"), JsonUtility.ToJson(new SslValidationBuildSummary
        {
            result = summary.result.ToString(),
            totalErrors = summary.totalErrors,
            totalWarnings = summary.totalWarnings,
            totalSizeBytes = (long)summary.totalSize,
            totalTimeSeconds = summary.totalTime.TotalSeconds,
            executablePath = executablePath,
            graphicsDeviceType = GraphicsDeviceType.Direct3D11.ToString(),
            unityVersion = Application.unityVersion,
        }, true));

        Debug.Log($"Validation player build: {summary.result} errors={summary.totalErrors} path={executablePath}");
        EditorApplication.Exit(summary.result == BuildResult.Succeeded && summary.totalErrors == 0 ? 0 : 1);
    }

    static UniversalRendererData EnsureRenderer(string modeName)
    {
        var path = $"{RendererRoot}/SslValidation{modeName}Renderer.asset";
        var source = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(SourceRendererPath);
        if (source == null)
            throw new FileNotFoundException("Missing source renderer.", SourceRendererPath);

        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
        if (renderer == null)
        {
            renderer = UnityEngine.Object.Instantiate(source);
            renderer.name = "SslValidation" + modeName + "Renderer";
            AssetDatabase.CreateAsset(renderer, path);
        }

        foreach (var existing in AssetDatabase.LoadAllAssetsAtPath(path))
        {
            if (existing is ScriptableRendererFeature)
                AssetDatabase.RemoveObjectFromAsset(existing);
        }

        renderer.rendererFeatures.Clear();
        renderer.renderingMode = modeName == "Deferred"
            ? RenderingMode.Deferred
            : modeName == "ForwardPlus" ? RenderingMode.ForwardPlus : RenderingMode.Forward;
        renderer.name = "SslValidation" + modeName + "Renderer";

        var gtao = ScriptableObject.CreateInstance<GroundTruthAmbientOcclusionRendererFeature>();
        gtao.name = renderer.name + "gtao";
        gtao.Create();
        AssetDatabase.AddObjectToAsset(gtao, renderer);
        renderer.rendererFeatures.Add(gtao);

        var ssl = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
        ssl.name = renderer.name + "ssl";
        ssl.Create();
        AssetDatabase.AddObjectToAsset(ssl, renderer);
        renderer.rendererFeatures.Add(ssl);

        var probe = ScriptableObject.CreateInstance<ScreenSpaceLightingBindingProbeFeature>();
        probe.name = renderer.name + "probe";
        probe.probeCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ProbeComputePath);
        probe.lightingFeature = ssl;
        probe.ambientOcclusionFeature = gtao;
        probe.Create();
        AssetDatabase.AddObjectToAsset(probe, renderer);
        renderer.rendererFeatures.Add(probe);

        EnsureFeatureMap(renderer);
        EditorUtility.SetDirty(renderer);
        renderer.SetDirty();
        return renderer;
    }

    static void EnsureFeatureMap(UniversalRendererData renderer)
    {
        var serialized = new SerializedObject(renderer);
        var map = serialized.FindProperty("m_RendererFeatureMap");
        if (map == null)
            return;

        map.arraySize = renderer.rendererFeatures.Count;
        for (var i = 0; i < renderer.rendererFeatures.Count; i++)
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(renderer.rendererFeatures[i], out _, out long id);
            map.GetArrayElementAtIndex(i).longValue = id;
        }

        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(renderer);
    }

    static UniversalRenderPipelineAsset EnsurePipeline(string modeName, UniversalRendererData renderer)
    {
        var path = $"{RendererRoot}/SslValidation{modeName}Pipeline.asset";
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
        {
            pipeline = UniversalRenderPipelineAsset.Create(renderer);
            pipeline.name = "SslValidation" + modeName + "Pipeline";
            AssetDatabase.CreateAsset(pipeline, path);
        }
        else
        {
            var template = UniversalRenderPipelineAsset.Create(renderer);
            EditorUtility.CopySerialized(template, pipeline);
            UnityEngine.Object.DestroyImmediate(template);
            pipeline.name = "SslValidation" + modeName + "Pipeline";
        }

        pipeline.supportsHDR = true;
        pipeline.msaaSampleCount = 1;
        pipeline.renderScale = 1f;
        pipeline.shadowDistance = 50f;
        pipeline.supportsCameraDepthTexture = false;
        pipeline.supportsCameraOpaqueTexture = false;
        EditorUtility.SetDirty(pipeline);
        return pipeline;
    }

    static VolumeProfile EnsureProfile()
    {
        var path = ProfileRoot + "/SslValidationProfile.asset";
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "SslValidationProfile";
            AssetDatabase.CreateAsset(profile, path);
        }

        EnsureVolumeComponent<ScreenSpaceGlobalIllumination>(profile).enabled.Override(false);
        EnsureVolumeComponent<ScreenSpaceReflection>(profile).enabled.Override(false);
        EnsureVolumeComponent<ScreenSpaceRefraction>(profile).enabled.Override(false);
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

    static MaterialSet EnsureMaterials()
    {
        return new MaterialSet
        {
            floor = EnsureLitMaterial("SslValidationFloor", new Color(0.55f, 0.58f, 0.62f), 0f, 0.8f),
            metal95 = EnsureLitMaterial("SslValidationMetal95", new Color(0.9f, 0.9f, 0.9f), 1f, 0.95f),
            metal80 = EnsureLitMaterial("SslValidationMetal80", new Color(0.9f, 0.9f, 0.9f), 1f, 0.8f),
            metal65 = EnsureLitMaterial("SslValidationMetal65", new Color(0.9f, 0.9f, 0.9f), 1f, 0.65f),
            metal40 = EnsureLitMaterial("SslValidationMetal40", new Color(0.9f, 0.9f, 0.9f), 1f, 0.4f),
            emissive = EnsureEmissiveMaterial("SslValidationEmissive", new Color(2.2f, 1.2f, 0.35f)),
            transparentSsr = EnsureTransparentSsrMaterial(),
            gradient = EnsureGradientMaterial(),
            mirror = EnsureLitMaterial("SslValidationMirror", new Color(0.02f, 0.02f, 0.02f), 1f, 0.9f),
            glass = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "/T11RefractionBox.mat"),
            complexLit = EnsureShaderMaterial("SslValidationComplexLit", "Universal Render Pipeline/Complex Lit",
                new Color(0.4f, 0.55f, 0.75f), 0f, 0.3f),
            simpleLit = EnsureShaderMaterial("SslValidationSimpleLit", "Universal Render Pipeline/Simple Lit",
                new Color(0.45f, 0.7f, 0.45f), 0f, 0.3f),
            bakedLit = EnsureShaderMaterial("SslValidationBakedLit", "Universal Render Pipeline/Baked Lit",
                new Color(0.75f, 0.5f, 0.4f), 0f, 0.3f),
            unlit = EnsureShaderMaterial("SslValidationUnlit", "Universal Render Pipeline/Unlit",
                new Color(0.8f, 0.6f, 0.2f), 0f, 0.3f),
            shaderGraphLit = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "/SslT9LitT14.mat"),
            roomFloor = EnsureLitMaterial("SslValidationRoomFloor", new Color(0.7f, 0.7f, 0.7f), 0f, 0.05f),
            refractionLeakProbe = EnsureRefractionLeakProbeMaterial(),
            terrain = EnsureTerrainMaterial(),
            terrainAlbedo = EnsureTerrainAlbedo(),
        };
    }

    // Materials that GTAO coverage must distinguish by shader family (P1-4/G6). The colors only
    // make the regions identifiable in the screenshots; AO is measured as an on/off ratio.
    static Material EnsureShaderMaterial(string name, string shaderName, Color color, float metallic, float smoothness)
    {
        var path = MaterialRoot + "/" + name + ".mat";
        var shader = Shader.Find(shaderName);
        if (shader == null)
            throw new FileNotFoundException("Missing shader.", shaderName);

        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(material, path);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }

        material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Metallic"))
            material.SetFloat("_Metallic", metallic);
        if (material.HasProperty("_Smoothness"))
            material.SetFloat("_Smoothness", smoothness);
        material.SetFloat("_Surface", 0f);
        material.SetFloat("_ZWrite", 1f);
        material.renderQueue = (int)RenderQueue.Geometry;
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material EnsureTerrainMaterial()
    {
        var path = MaterialRoot + "/SslValidationTerrain.mat";
        var shader = Shader.Find("Universal Render Pipeline/Terrain/Lit");
        if (shader == null)
            throw new FileNotFoundException("Missing shader.", "Universal Render Pipeline/Terrain/Lit");

        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader) { name = "SslValidationTerrain" };
            AssetDatabase.CreateAsset(material, path);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }

        EditorUtility.SetDirty(material);
        return material;
    }

    static Material EnsureLitMaterial(string name, Color color, float metallic, float smoothness)
    {
        var path = MaterialRoot + "/" + name + ".mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            AssetDatabase.CreateAsset(material, path);
        }

        material.SetColor("_BaseColor", color);
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        material.DisableKeyword("_EMISSION");
        material.SetFloat("_Surface", 0f);
        material.SetFloat("_ZWrite", 1f);
        material.renderQueue = (int)RenderQueue.Geometry;
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material EnsureEmissiveMaterial(string name, Color emission)
    {
        var material = EnsureLitMaterial(name, new Color(0.04f, 0.04f, 0.04f), 0f, 0.5f);
        material.EnableKeyword("_EMISSION");
        material.SetColor("_EmissionColor", emission);
        material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material EnsureTransparentSsrMaterial()
    {
        var material = EnsureLitMaterial("SslValidationTransparentSsr", new Color(0.85f, 0.9f, 1f, 0.3f), 0f, 0.9f);
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_Blend", 0f);
        material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0f);
        material.SetFloat("_TransparentSSRReceive", 1f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.EnableKeyword("_TRANSPARENT_SSR_RECEIVE");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)RenderQueue.Transparent;
        material.SetShaderPassEnabled("SHADOWCASTER", false);
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material EnsureRefractionLeakProbeMaterial()
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(LeakProbeShaderPath);
        if (shader == null)
            throw new FileNotFoundException("Missing shader.", LeakProbeShaderPath);

        var path = MaterialRoot + "/SslValidationRefractionLeakProbe.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader) { name = "SslValidationRefractionLeakProbe" };
            AssetDatabase.CreateAsset(material, path);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }

        material.SetFloat("_LeakProbeMode", 0f);
        material.SetFloat("_LeakProbeMarker", 0.5f);
        material.renderQueue = (int)RenderQueue.Geometry;
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material EnsureGradientMaterial()
    {
        EnsureGradientTexture();
        var path = MaterialRoot + "/SslValidationGradient.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "SslValidationGradient" };
            AssetDatabase.CreateAsset(material, path);
        }

        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Surface", 0f);
        material.SetFloat("_ZWrite", 1f);
        material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(GradientTexturePath));
        material.renderQueue = (int)RenderQueue.Geometry;
        EditorUtility.SetDirty(material);
        return material;
    }

    static void EnsureGradientTexture()    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var absolutePath = Path.Combine(projectRoot, GradientTexturePath);
        if (!File.Exists(absolutePath))
        {
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false, false);
            try
            {
                var pixels = new Color32[64 * 64];
                for (var y = 0; y < 64; y++)
                {
                    Color32 color = y < 16
                        ? new Color32(20, 40, 220, 255)
                        : y < 32 ? new Color32(20, 200, 60, 255)
                        : y < 48 ? new Color32(220, 40, 40, 255)
                        : new Color32(235, 235, 235, 255);
                    if (y == 32)
                        color = new Color32(0, 0, 0, 255);
                    for (var x = 0; x < 64; x++)
                        pixels[y * 64 + x] = x == 32 ? new Color32(255, 255, 255, 255) : color;
                }

                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
                File.WriteAllBytes(absolutePath, texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(GradientTexturePath, ImportAssetOptions.ForceUpdate);
        }

        var importer = AssetImporter.GetAtPath(GradientTexturePath) as TextureImporter;
        if (importer == null)
            return;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.filterMode = FilterMode.Point;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.sRGBTexture = true;
        importer.mipmapEnabled = false;
        importer.SaveAndReimport();
    }

    // Solid mid-grey albedo for the flat terrain patch. The value only needs to be non-black so the
    // GTAO on/off ratio on TerrainLit is measurable.
    static Texture2D EnsureTerrainAlbedo()
    {
        var assetPath = MaterialRoot + "/SslValidationTerrainAlbedo.png";
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var absolutePath = Path.Combine(projectRoot, assetPath);
        if (!File.Exists(absolutePath))
        {
            var texture = new Texture2D(16, 16, TextureFormat.RGBA32, false, false);
            try
            {
                var pixels = new Color32[16 * 16];
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(128, 128, 128, 255);
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
                File.WriteAllBytes(absolutePath, texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer != null)
        {
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    // Flat 8x8 terrain: the surface is the TerrainLit coverage sample, and the box the runtime rig
    // places on it supplies the contact occlusion.
    static TerrainData EnsureCoverageTerrainData(Texture2D albedo)
    {
        var layerPath = MaterialRoot + "/SslValidationTerrainLayer.terrainlayer";
        var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPath);
        if (layer == null)
        {
            layer = new TerrainLayer();
            AssetDatabase.CreateAsset(layer, layerPath);
        }

        layer.diffuseTexture = albedo;
        layer.tileSize = new Vector2(8f, 8f);
        layer.tileOffset = Vector2.zero;
        EditorUtility.SetDirty(layer);

        var path = MaterialRoot + "/SslValidationTerrain.asset";
        var data = AssetDatabase.LoadAssetAtPath<TerrainData>(path);
        if (data == null)
        {
            data = new TerrainData();
            data.heightmapResolution = 33;
            data.size = new Vector3(8f, 1f, 8f);
            data.SetHeights(0, 0, new float[33, 33]);
            AssetDatabase.CreateAsset(data, path);
        }

        // A single layer still needs an explicit all-one alphamap; without it the splat weight is
        // zero and TerrainLit renders black.
        data.alphamapResolution = 16;
        var weights = new float[16, 16, 1];
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
                weights[y, x, 0] = 1f;
        data.SetAlphamaps(0, 0, weights);

        if (data.terrainLayers == null || data.terrainLayers.Length == 0 ||
            data.terrainLayers[0] != layer)
        {
            data.terrainLayers = new[] { layer };
            EditorUtility.SetDirty(data);
        }

        EditorUtility.SetDirty(layer);
        EditorUtility.SetDirty(data);
        return data;
    }

    sealed class MaterialSet
    {
        public Material floor;
        public Material metal95;
        public Material metal80;
        public Material metal65;
        public Material metal40;
        public Material emissive;
        public Material transparentSsr;
        public Material gradient;
        public Material mirror;
        public Material glass;
        public Material complexLit;
        public Material simpleLit;
        public Material bakedLit;
        public Material unlit;
        public Material shaderGraphLit;
        public Material roomFloor;
        public Material refractionLeakProbe;
        public Material terrain;
        public Texture2D terrainAlbedo;
    }
}

[Serializable]
public sealed class SslValidationBuildSummary
{
    public string result;
    public int totalErrors;
    public int totalWarnings;
    public long totalSizeBytes;
    public double totalTimeSeconds;
    public string executablePath;
    public string graphicsDeviceType;
    public string unityVersion;
}