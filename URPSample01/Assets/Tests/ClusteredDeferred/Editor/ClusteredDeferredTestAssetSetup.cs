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
    const string ScenePath = SceneFolder + "/ClusteredDeferredArena.unity";
    const string StencilRendererPath = RendererFolder + "/CdStencilDeferredRenderer.asset";
    const string ClusteredRendererPath = RendererFolder + "/CdClusteredDeferredRenderer.asset";
    const string StencilPipelinePath = RendererFolder + "/CdStencilDeferredPipeline.asset";
    const string ClusteredPipelinePath = RendererFolder + "/CdClusteredDeferredPipeline.asset";
    const string ForwardPlusPipelinePath = RendererFolder + "/CdForwardPlusPipeline.asset";

    const string SourceDeferredRenderer = "Assets/Tests/ScreenSpaceLighting/Renderers/SslValidationDeferredRenderer.asset";
    const string SourceDeferredPipeline = "Assets/Tests/ScreenSpaceLighting/Renderers/SslValidationDeferredPipeline.asset";
    const string SourceForwardPlusRenderer = "Assets/Tests/ScreenSpaceLighting/Renderers/SslValidationForwardPlusRenderer.asset";
    const string SourceForwardPlusPipeline = "Assets/Tests/ScreenSpaceLighting/Renderers/SslValidationForwardPlusPipeline.asset";
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

        var deferredRenderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(SourceDeferredRenderer);
        var deferredPipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(SourceDeferredPipeline);
        var forwardPlusRenderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(SourceForwardPlusRenderer);
        var forwardPlusPipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(SourceForwardPlusPipeline);
        if (deferredRenderer == null || deferredPipeline == null || forwardPlusRenderer == null || forwardPlusPipeline == null)
            throw new FileNotFoundException("Missing one of the SSL validation renderer/pipeline assets.");

        var stencilRenderer = CreateRendererCopy(deferredRenderer, StencilRendererPath, false);
        var clusteredRenderer = CreateRendererCopy(deferredRenderer, ClusteredRendererPath, true);
        var stencilPipeline = CreatePipelineCopy(deferredPipeline, StencilPipelinePath, stencilRenderer);
        var clusteredPipeline = CreatePipelineCopy(deferredPipeline, ClusteredPipelinePath, clusteredRenderer);
        var forwardPipeline = CreatePipelineCopy(forwardPlusPipeline, ForwardPlusPipelinePath, forwardPlusRenderer);

        CreateScene(stencilPipeline, clusteredPipeline, forwardPipeline);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Clustered deferred arena assets created under " + Root);
        return true;
    }

    static UniversalRendererData CreateRendererCopy(UniversalRendererData source, string path, bool clustered)
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

        return saved;
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
        UniversalRenderPipelineAsset forwardPlus)
    {
        var floor = AssetDatabase.LoadAssetAtPath<Material>(FloorMaterial);
        var sphere = AssetDatabase.LoadAssetAtPath<Material>(SphereMaterial);
        var metal = AssetDatabase.LoadAssetAtPath<Material>(MetalMaterial);
        if (floor == null || sphere == null || metal == null)
            throw new FileNotFoundException("Missing one of the arena materials.");

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
        runner.arenaCamera = camera;
        runner.sphereMaterial = sphere;
        runner.metalMaterial = metal;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    static void EnsureFolder(string parent, string name)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + name))
            AssetDatabase.CreateFolder(parent, name);
    }
}
