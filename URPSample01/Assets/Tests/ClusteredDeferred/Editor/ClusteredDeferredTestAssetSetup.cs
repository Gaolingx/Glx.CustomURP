using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// Creates the dedicated pipelines/renderers and the multi-light arena scene used by the
// clustered deferred A/B and crossover evidence. The stencil and clustered renderers share every
// setting except UniversalRendererData.clusteredDeferredLighting.
//
// Renderer features are stripped from the copies so both modes render the same base lighting path
// (GTAO/SSGI would only add identical overhead to both sides of the A/B).
public static class ClusteredDeferredTestAssetSetup
{
    const string Root = "Assets/Tests/ClusteredDeferred";
    const string SceneFolder = Root + "/Scenes";
    const string RendererFolder = Root + "/Renderers";
    const string TextureFolder = Root + "/Textures";
    const string ScenePath = SceneFolder + "/ClusteredDeferredArena.unity";
    const string StencilRendererPath = RendererFolder + "/CdStencilDeferredRenderer.asset";
    const string ClusteredRendererPath = RendererFolder + "/CdClusteredDeferredRenderer.asset";
    const string StencilPipelinePath = RendererFolder + "/CdStencilDeferredPipeline.asset";
    const string ClusteredPipelinePath = RendererFolder + "/CdClusteredDeferredPipeline.asset";
    const string ForwardPlusPipelinePath = RendererFolder + "/CdForwardPlusPipeline.asset";

    // T4 shadow subset: same renderer pair, pipelines with main/additional shadows enabled.
    const string ShadowStencilRendererPath = RendererFolder + "/CdShadowStencilDeferredRenderer.asset";
    const string ShadowClusteredRendererPath = RendererFolder + "/CdShadowClusteredDeferredRenderer.asset";
    const string ShadowStencilPipelinePath = RendererFolder + "/CdShadowStencilDeferredPipeline.asset";
    const string ShadowClusteredPipelinePath = RendererFolder + "/CdShadowClusteredDeferredPipeline.asset";
    const string ScreenShadowStencilRendererPath = RendererFolder + "/CdScreenShadowStencilDeferredRenderer.asset";
    const string ScreenShadowClusteredRendererPath = RendererFolder + "/CdScreenShadowClusteredDeferredRenderer.asset";
    const string ScreenShadowStencilPipelinePath = RendererFolder + "/CdScreenShadowStencilDeferredPipeline.asset";
    const string ScreenShadowClusteredPipelinePath = RendererFolder + "/CdScreenShadowClusteredDeferredPipeline.asset";

    // T4 feature subset (cookie / light layers / GTAO direct AO): one renderer pair with light
    // layers enabled and a GTAO feature whose direct-lighting strength the runner toggles.
    const string FeatureStencilRendererPath = RendererFolder + "/CdFeatureStencilDeferredRenderer.asset";
    const string FeatureClusteredRendererPath = RendererFolder + "/CdFeatureClusteredDeferredRenderer.asset";
    const string FeatureStencilPipelinePath = RendererFolder + "/CdFeatureStencilDeferredPipeline.asset";
    const string FeatureClusteredPipelinePath = RendererFolder + "/CdFeatureClusteredDeferredPipeline.asset";

    const string SourceDeferredRenderer = "Assets/Tests/ScreenSpaceLighting/Renderers/SslValidationDeferredRenderer.asset";
    const string SourceDeferredPipeline = "Assets/Tests/ScreenSpaceLighting/Renderers/SslValidationDeferredPipeline.asset";
    const string SourceForwardPlusRenderer = "Assets/Tests/ScreenSpaceLighting/Renderers/SslValidationForwardPlusRenderer.asset";
    const string SourceForwardPlusPipeline = "Assets/Tests/ScreenSpaceLighting/Renderers/SslValidationForwardPlusPipeline.asset";
    const string ReceiveShadowsOffMaterialPath = Root + "/Materials/CdReceiveShadowsOff.mat";
    // Loaded by name from ClusteredDeferredArenaRunner for the --cd-transparent probe; a Resources
    // asset keeps the transparent Lit variants in the player without adding a scene field (the
    // arena scene is regenerated on every build, so extra serialized fields churn its diff).
    const string TransparentMaterialPath = Root + "/Resources/CdTransparent.mat";
    const string CookieTexturePath = TextureFolder + "/CdSpotCookie.png";
    const string FloorMaterial = "Assets/Tests/ScreenSpaceLighting/Materials/Ground.mat";
    const string SphereMaterial = "Assets/Tests/ScreenSpaceLighting/Materials/SmoothDielectric.mat";
    const string MetalMaterial = "Assets/Tests/ScreenSpaceLighting/Materials/RoughMetal.mat";

    [MenuItem("Tests/Clustered Deferred/Create Arena Test Assets")]
    public static void CreateFromMenu()
    {
        CreateAll();
    }

    public static void CreateAndExit()
    {
        bool ok;
        try
        {
            ok = CreateAll();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ok = false;
        }

        EditorApplication.Exit(ok ? 0 : 1);
    }

    public static bool CreateAll()
    {
        EnsureFolder("Assets/Tests", "ClusteredDeferred");
        EnsureFolder(Root, "Scenes");
        EnsureFolder(Root, "Renderers");
        EnsureFolder(Root, "Materials");
        EnsureFolder(Root, "Textures");
        EnsureFolder(Root, "Resources");

        var deferredRenderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(SourceDeferredRenderer);
        var deferredPipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(SourceDeferredPipeline);
        var forwardPlusRenderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(SourceForwardPlusRenderer);
        var forwardPlusPipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(SourceForwardPlusPipeline);
        if (deferredRenderer == null || deferredPipeline == null || forwardPlusRenderer == null || forwardPlusPipeline == null)
            throw new FileNotFoundException("Missing one of the SSL validation renderer/pipeline assets.");

        var stencilRenderer = CreateRendererCopy(deferredRenderer, StencilRendererPath, false, false);
        var clusteredRenderer = CreateRendererCopy(deferredRenderer, ClusteredRendererPath, true, false);
        var stencilPipeline = CreatePipelineCopy(deferredPipeline, StencilPipelinePath, stencilRenderer);
        var clusteredPipeline = CreatePipelineCopy(deferredPipeline, ClusteredPipelinePath, clusteredRenderer);
        var forwardPipeline = CreatePipelineCopy(forwardPlusPipeline, ForwardPlusPipelinePath, forwardPlusRenderer);

        var shadowStencilRenderer = CreateRendererCopy(deferredRenderer, ShadowStencilRendererPath, false, false);
        var shadowClusteredRenderer = CreateRendererCopy(deferredRenderer, ShadowClusteredRendererPath, true, false);
        var shadowStencilPipeline = CreateShadowPipeline(deferredPipeline, ShadowStencilPipelinePath, shadowStencilRenderer, 4);
        var shadowClusteredPipeline = CreateShadowPipeline(deferredPipeline, ShadowClusteredPipelinePath, shadowClusteredRenderer, 4);

        var screenShadowStencilRenderer = CreateRendererCopy(deferredRenderer, ScreenShadowStencilRendererPath, false, true);
        var screenShadowClusteredRenderer = CreateRendererCopy(deferredRenderer, ScreenShadowClusteredRendererPath, true, true);
        var screenShadowStencilPipeline = CreateShadowPipeline(deferredPipeline, ScreenShadowStencilPipelinePath, screenShadowStencilRenderer, 1);
        var screenShadowClusteredPipeline = CreateShadowPipeline(deferredPipeline, ScreenShadowClusteredPipelinePath, screenShadowClusteredRenderer, 1);

        var featureStencilRenderer = CreateRendererCopy(deferredRenderer, FeatureStencilRendererPath, false, true, true);
        var featureClusteredRenderer = CreateRendererCopy(deferredRenderer, FeatureClusteredRendererPath, true, true, true);
        var featureStencilPipeline = CreateFeaturePipeline(deferredPipeline, FeatureStencilPipelinePath, featureStencilRenderer);
        var featureClusteredPipeline = CreateFeaturePipeline(deferredPipeline, FeatureClusteredPipelinePath, featureClusteredRenderer);
        var cookieTexture = CreateCookieTexture();

        CreateScene(stencilPipeline, clusteredPipeline, forwardPipeline,
            shadowStencilPipeline, shadowClusteredPipeline,
            screenShadowStencilPipeline, screenShadowClusteredPipeline,
            featureStencilPipeline, featureClusteredPipeline,
            cookieTexture);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Clustered deferred arena assets created under " + Root);
        return true;
    }

    static UniversalRendererData CreateRendererCopy(UniversalRendererData source, string path, bool clustered,
        bool screenSpaceShadows, bool gtao = false)
    {
        AssetDatabase.DeleteAsset(path);
        var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
        EditorUtility.CopySerialized(source, renderer);
        renderer.name = Path.GetFileNameWithoutExtension(path);

        var serialized = new SerializedObject(renderer);
        serialized.FindProperty("m_RendererFeatures").ClearArray();
        serialized.FindProperty("m_RendererFeatureMap").ClearArray();
        serialized.ApplyModifiedPropertiesWithoutUndo();

        renderer.clusteredDeferredLighting = clustered;
        ResourceReloader.TryReloadAllNullIn(renderer, UniversalRenderPipelineAsset.packagePath);
        AssetDatabase.CreateAsset(renderer, path);

        var saved = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
        if (saved == null || saved.shaders.clusteredDeferredCS == null ||
            saved.shaders.clusteredDeferredMaskPS == null || saved.shaders.clusteredDeferredResolvePS == null)
        {
            throw new InvalidOperationException("Clustered deferred shader resources did not reload on " + path);
        }

        if (saved.clusteredDeferredLighting != clustered)
            throw new InvalidOperationException("The clustered toggle did not persist on " + path);

        if (screenSpaceShadows)
            AddScreenSpaceShadowsFeature(saved);
        if (gtao)
            AddGtaoFeature(saved);

        return saved;
    }

    // GTAO is a public renderer feature in this fork. Create() resolves the package compute
    // shaders through the editor-only resource locator so the player build keeps them.
    static void AddGtaoFeature(UniversalRendererData renderer)
    {
        var feature = ScriptableObject.CreateInstance<GroundTruthAmbientOcclusionRendererFeature>();
        feature.name = "Ground Truth Ambient Occlusion";
        feature.hideFlags = HideFlags.HideInHierarchy;
        feature.Create();

        var serializedFeature = new SerializedObject(feature);
        var settings = serializedFeature.FindProperty("m_Settings");
        settings.FindPropertyRelative("intensity").floatValue = 1f;
        // Indirect AO stays on for every case; the AO case toggles direct-light AO at runtime.
        settings.FindPropertyRelative("directLightingStrength").floatValue = 0f;
        // Temporal accumulation depends on the frame phase, so two pipelines captured at
        // different frame counts would compare different AO noise. Spatial-only AO is
        // deterministic and makes the stencil/clustered A/B meaningful.
        settings.FindPropertyRelative("temporalAccumulation").boolValue = false;
        serializedFeature.ApplyModifiedPropertiesWithoutUndo();

        AssetDatabase.AddObjectToAsset(feature, renderer);
        AppendRendererFeature(renderer, feature, 2);
    }

    static void AppendRendererFeature(UniversalRendererData renderer, ScriptableRendererFeature feature, long mapValue)
    {
        var serialized = new SerializedObject(renderer);
        var features = serialized.FindProperty("m_RendererFeatures");
        var count = features.arraySize;
        features.arraySize = count + 1;
        features.GetArrayElementAtIndex(count).objectReferenceValue = feature;
        var featureMap = serialized.FindProperty("m_RendererFeatureMap");
        featureMap.arraySize = count + 1;
        featureMap.GetArrayElementAtIndex(count).longValue = mapValue;
        serialized.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(renderer);
        EditorUtility.SetDirty(feature);
        AssetDatabase.SaveAssets();
    }

    // ScreenSpaceShadows is internal to the URP runtime assembly, so the test asset is created
    // through reflection. The serialized shader reference keeps the shader in the player build.
    static void AddScreenSpaceShadowsFeature(UniversalRendererData renderer)
    {
        var featureType = Type.GetType(
            "UnityEngine.Rendering.Universal.ScreenSpaceShadows, Unity.RenderPipelines.Universal.Runtime");
        if (featureType == null)
            throw new TypeLoadException("ScreenSpaceShadows could not be resolved from the URP runtime assembly.");

        var feature = (ScriptableRendererFeature)ScriptableObject.CreateInstance(featureType);
        feature.name = "Screen Space Shadows";
        feature.hideFlags = HideFlags.HideInHierarchy;

        var serializedFeature = new SerializedObject(feature);
        var shaderProperty = serializedFeature.FindProperty("m_Shader");
        if (shaderProperty != null)
            shaderProperty.objectReferenceValue = Shader.Find("Hidden/Universal Render Pipeline/ScreenSpaceShadows");
        serializedFeature.ApplyModifiedPropertiesWithoutUndo();

        AssetDatabase.AddObjectToAsset(feature, renderer);
        AppendRendererFeature(renderer, feature, 1);
    }

    // Light layers need the pipeline flag: it decides whether the deferred GBuffer allocates the
    // rendering-layer slot and whether DeferredLights enables the _LIGHT_LAYERS keyword.
    static UniversalRenderPipelineAsset CreateFeaturePipeline(UniversalRenderPipelineAsset source, string path,
        UniversalRendererData renderer)
    {
        var pipeline = CreateShadowPipeline(source, path, renderer, 2);
        var serialized = new SerializedObject(pipeline);
        serialized.FindProperty("m_SupportsLightLayers").boolValue = true;
        serialized.FindProperty("m_SupportsLightCookies").boolValue = true;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(pipeline);
        AssetDatabase.SaveAssets();
        return pipeline;
    }

    // Spot cookie: soft radial falloff with an 8-px checker so the pattern is unambiguous at 720p.
    static Texture2D CreateCookieTexture()
    {
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x + 0.5f) / size * 2f - 1f;
                var dy = (y + 0.5f) / size * 2f - 1f;
                var radial = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                var checker = ((x / 8 + y / 8) & 1) == 0 ? 1f : 0.3f;
                var value = (byte)Mathf.RoundToInt(255f * radial * checker);
                pixels[y * size + x] = new Color32(value, value, value, 255);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, false);

        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        Directory.CreateDirectory(Path.Combine(projectRoot, TextureFolder));
        File.WriteAllBytes(Path.Combine(projectRoot, CookieTexturePath), texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);

        AssetDatabase.ImportAsset(CookieTexturePath, ImportAssetOptions.ForceUpdate);
        var importer = (TextureImporter)AssetImporter.GetAtPath(CookieTexturePath);
        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = true;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.alphaIsTransparency = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();

        var cookie = AssetDatabase.LoadAssetAtPath<Texture2D>(CookieTexturePath);
        if (cookie == null)
            throw new InvalidOperationException("Unable to import the cookie texture at " + CookieTexturePath);
        return cookie;
    }

    static UniversalRenderPipelineAsset CreateShadowPipeline(UniversalRenderPipelineAsset source, string path,
        UniversalRendererData renderer, int cascadeCount)    {
        var pipeline = CreatePipelineCopy(source, path, renderer);
        var serialized = new SerializedObject(pipeline);
        serialized.FindProperty("m_MainLightShadowsSupported").boolValue = true;
        serialized.FindProperty("m_MainLightShadowmapResolution").intValue = 2048;
        serialized.FindProperty("m_ShadowDistance").floatValue = 60f;
        serialized.FindProperty("m_ShadowCascadeCount").intValue = cascadeCount;
        serialized.FindProperty("m_ShadowDepthBias").floatValue = 1f;
        serialized.FindProperty("m_ShadowNormalBias").floatValue = 1f;
        serialized.FindProperty("m_SoftShadowsSupported").boolValue = true;
        serialized.FindProperty("m_SoftShadowQuality").intValue = 2; // Medium: 5x5 tent
        serialized.FindProperty("m_AdditionalLightShadowsSupported").boolValue = true;
        serialized.FindProperty("m_AdditionalLightsShadowmapResolution").intValue = 2048;
        // URP's build prefiltering collects the pipeline assets parked on quality levels; only as
        // many as there are quality levels. Every T4 pipeline therefore advertises light layers,
        // otherwise the _LIGHT_LAYERS/_WRITE_RENDERING_LAYERS variants are stripped from the player
        // and the deferred layer slot stays empty.
        serialized.FindProperty("m_SupportsLightLayers").boolValue = true;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(pipeline);
        AssetDatabase.SaveAssets();
        return AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
    }

    static UniversalRenderPipelineAsset CreatePipelineCopy(UniversalRenderPipelineAsset source, string path,
        ScriptableRendererData renderer)
    {
        AssetDatabase.DeleteAsset(path);
        var pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
        EditorUtility.CopySerialized(source, pipeline);
        pipeline.name = Path.GetFileNameWithoutExtension(path);

        var serialized = new SerializedObject(pipeline);
        var rendererList = serialized.FindProperty("m_RendererDataList");
        rendererList.arraySize = 1;
        rendererList.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
        serialized.FindProperty("m_DefaultRendererIndex").intValue = 0;
        serialized.ApplyModifiedPropertiesWithoutUndo();

        AssetDatabase.CreateAsset(pipeline, path);
        return AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
    }

    static void CreateScene(UniversalRenderPipelineAsset stencil, UniversalRenderPipelineAsset clustered,
        UniversalRenderPipelineAsset forwardPlus,
        UniversalRenderPipelineAsset shadowStencil, UniversalRenderPipelineAsset shadowClustered,
        UniversalRenderPipelineAsset screenShadowStencil, UniversalRenderPipelineAsset screenShadowClustered,
        UniversalRenderPipelineAsset featureStencil, UniversalRenderPipelineAsset featureClustered,
        Texture2D cookieTexture)
    {
        var floor = AssetDatabase.LoadAssetAtPath<Material>(FloorMaterial);
        var sphere = AssetDatabase.LoadAssetAtPath<Material>(SphereMaterial);
        var metal = AssetDatabase.LoadAssetAtPath<Material>(MetalMaterial);
        if (floor == null || sphere == null || metal == null)
            throw new FileNotFoundException("Missing one of the arena materials.");

        var receiveShadowsOff = CreateReceiveShadowsOffMaterial(sphere);
        CreateTransparentMaterial(sphere);

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        RenderSettings.skybox = null;
        RenderSettings.fog = false;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.16f, 0.17f, 0.2f);
        RenderSettings.reflectionIntensity = 0f;

        var cameraObject = new GameObject("Arena Camera");
        cameraObject.transform.position = new Vector3(0f, 7f, -17f);
        cameraObject.transform.LookAt(new Vector3(0f, 2f, 0f));
        var camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.05f, 0.06f, 0.08f);
        camera.fieldOfView = 55f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 80f;
        var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.renderPostProcessing = false;
        cameraData.antialiasing = AntialiasingMode.None;

        var mainLightObject = new GameObject("Arena Main Light");
        mainLightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        var mainLight = mainLightObject.AddComponent<Light>();
        mainLight.type = LightType.Directional;
        mainLight.color = new Color(1f, 0.95f, 0.9f);
        mainLight.intensity = 0.6f;
        mainLight.shadows = LightShadows.None;

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Arena Floor";
        ground.transform.localScale = new Vector3(4.5f, 1f, 4.5f);
        ground.GetComponent<Renderer>().sharedMaterial = floor;

        var backWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        backWall.name = "Arena Back Wall";
        backWall.transform.position = new Vector3(0f, 5f, 16f);
        backWall.transform.localScale = new Vector3(45f, 10f, 1f);
        backWall.GetComponent<Renderer>().sharedMaterial = floor;

        var runnerObject = new GameObject("Clustered Deferred Arena Runner");
        var runner = runnerObject.AddComponent<ClusteredDeferredArenaRunner>();
        runner.stencilDeferredPipeline = stencil;
        runner.clusteredDeferredPipeline = clustered;
        runner.forwardPlusPipeline = forwardPlus;
        runner.shadowStencilPipeline = shadowStencil;
        runner.shadowClusteredPipeline = shadowClustered;
        runner.screenShadowStencilPipeline = screenShadowStencil;
        runner.screenShadowClusteredPipeline = screenShadowClustered;
        runner.featureStencilPipeline = featureStencil;
        runner.featureClusteredPipeline = featureClustered;
        runner.cookieTexture = cookieTexture;
        runner.arenaMainLight = mainLight;
        runner.arenaCamera = camera;
        runner.sphereMaterial = sphere;
        runner.metalMaterial = metal;
        runner.receiveShadowsOffMaterial = receiveShadowsOff;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    // A material asset that carries _RECEIVE_SHADOWS_OFF so the Lit GBuffer variant is part of the
    // player build; the runner swaps it onto half the receivers for the flag A/B. CopyAsset keeps
    // the serialized properties byte-identical to the source (a new Material() would fill in
    // defaults the source asset does not have, which changes the rendered result).
    static Material CreateReceiveShadowsOffMaterial(Material source)
    {
        AssetDatabase.DeleteAsset(ReceiveShadowsOffMaterialPath);
        var sourcePath = AssetDatabase.GetAssetPath(source);
        if (!AssetDatabase.CopyAsset(sourcePath, ReceiveShadowsOffMaterialPath))
            throw new InvalidOperationException("Unable to copy " + sourcePath);

        var material = AssetDatabase.LoadAssetAtPath<Material>(ReceiveShadowsOffMaterialPath);
        material.EnableKeyword("_RECEIVE_SHADOWS_OFF");
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        return material;
    }

    // A transparent copy of the arena's Lit material. The runner spawns it only for the
    // --cd-transparent probe, but the asset is in Resources so the player build keeps the
    // _SURFACE_TYPE_TRANSPARENT variants of the forward and Forward+ passes.
    static Material CreateTransparentMaterial(Material source)
    {
        AssetDatabase.DeleteAsset(TransparentMaterialPath);
        var sourcePath = AssetDatabase.GetAssetPath(source);
        if (!AssetDatabase.CopyAsset(sourcePath, TransparentMaterialPath))
            throw new InvalidOperationException("Unable to copy " + sourcePath);

        var material = AssetDatabase.LoadAssetAtPath<Material>(TransparentMaterialPath);
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_Blend", 0f);
        material.SetFloat("_BlendModePreserveSpecular", 0f);
        material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0f);
        material.SetFloat("_AlphaClip", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)RenderQueue.Transparent;
        material.SetOverrideTag("RenderType", "Transparent");
        var color = material.GetColor("_BaseColor");
        color.a = 0.55f;
        material.SetColor("_BaseColor", color);
        material.SetColor("_Color", color);
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        return material;
    }

    static void EnsureFolder(string parent, string name)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + name))
            AssetDatabase.CreateFolder(parent, name);
    }
}
