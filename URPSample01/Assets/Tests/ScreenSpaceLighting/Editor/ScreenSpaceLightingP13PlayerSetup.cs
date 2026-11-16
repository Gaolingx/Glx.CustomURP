using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Builds the standalone player used by the P1-3 proxy verification. The player renders the same rig
// as the editor test and captures its frames through the RenderDoc in-application API, so the
// evidence comes from a real player build instead of editor screenshots.
public static class ScreenSpaceLightingP13PlayerSetup
{
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingP13Player.unity";
    const string PipelinePath = "Assets/Tests/ScreenSpaceLighting/Renderers/SslT11ForwardPipeline.asset";
    const string ProfilePath = "Assets/Tests/ScreenSpaceLighting/Profiles/ScreenSpaceLightingT3Profile.asset";
    const string MaterialRoot = "Assets/Tests/ScreenSpaceLighting/Materials";
    const string OutputRoot = "PLAN/Done/05-ScreenSpaceRefractionImprove/validation/P1-3/Player";

    [MenuItem("Tests/Screen Space Lighting/Create P1-3 Player Scene")]
    public static void CreateScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        RenderSettings.skybox = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "/BaselineSkybox.mat");
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.fog = false;

        var cameraObject = new GameObject("P1-3 Camera");
        cameraObject.tag = "MainCamera";
        cameraObject.transform.position = new Vector3(0f, 1.6f, -15f);
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

        var lightObject = new GameObject("P1-3 Directional Light");
        var directional = lightObject.AddComponent<Light>();
        directional.type = LightType.Directional;
        directional.color = new Color(1f, 0.93f, 0.82f);
        directional.intensity = 1.4f;
        directional.shadows = LightShadows.Soft;
        lightObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
        RenderSettings.sun = directional;

        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        var volumeObject = new GameObject("P1-3 Volume");
        var volume = volumeObject.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.sharedProfile = profile;

        var rigObject = new GameObject("P1-3 Player Verification");
        var verification = rigObject.AddComponent<ScreenSpaceLightingP13PlayerVerification>();
        verification.pipelineAsset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
        verification.volumeProfile = profile;
        verification.floorMaterial = CreateLitMaterial(
            MaterialRoot + "/P13PlayerFloor.mat", "P13PlayerFloor",
            new Color(0.55f, 0.58f, 0.62f), 0f, 0.85f, false);
        verification.backdropMaterial = CreateLitMaterial(
            MaterialRoot + "/P13PlayerBackdrop.mat", "P13PlayerBackdrop",
            new Color(0.02f, 0.02f, 0.02f), 0f, 0.3f, true);
        verification.glassMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "/T11RefractionBox.mat");
        verification.unlitMaterial = CreateUnlitMaterial(
            MaterialRoot + "/P13PlayerPattern.mat", "P13PlayerPattern");

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("P1-3 player scene created at " + ScenePath);
    }

    [MenuItem("Tests/Screen Space Lighting/Build P1-3 Player")]
    public static void BuildPlayer()
    {
        CreateScene();

        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);
        var executablePath = Path.Combine(outputDirectory, "SslP13Player.exe");

        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = executablePath,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.Development | BuildOptions.IncludeTestAssemblies,
        };

        // RenderDoc captures D3D11 deterministically; the other APIs are not part of this check.
        var previousApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        var previousFullScreenMode = PlayerSettings.fullScreenMode;
        var previousWidth = PlayerSettings.defaultScreenWidth;
        var previousHeight = PlayerSettings.defaultScreenHeight;
        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;

        BuildReport report;
        try
        {
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            report = BuildPipeline.BuildPlayer(options);
        }
        finally
        {
            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, previousApis);
            PlayerSettings.fullScreenMode = previousFullScreenMode;
            PlayerSettings.defaultScreenWidth = previousWidth;
            PlayerSettings.defaultScreenHeight = previousHeight;
        }

        var summary = report.summary;
        var summaryJson = JsonUtility.ToJson(new P13PlayerBuildSummary
        {
            result = summary.result.ToString(),
            totalErrors = summary.totalErrors,
            totalWarnings = summary.totalWarnings,
            totalSizeBytes = (long)summary.totalSize,
            totalTimeSeconds = summary.totalTime.TotalSeconds,
            executablePath = executablePath,
            graphicsDeviceType = GraphicsDeviceType.Direct3D11.ToString(),
            unityVersion = Application.unityVersion,
        }, true);
        File.WriteAllText(Path.Combine(outputDirectory, "P13PlayerBuildSummary.json"), summaryJson);

        Debug.Log($"P1-3 player build: {summary.result} errors={summary.totalErrors} path={executablePath}");
        EditorApplication.Exit(summary.result == BuildResult.Succeeded && summary.totalErrors == 0 ? 0 : 1);
    }

    static Material CreateLitMaterial(
        string path, string name, Color color, float metallic, float smoothness, bool emissive)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            material = new Material(shader) { name = name };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            if (emissive)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", new Color(2.2f, 1.2f, 0.35f));
            }

            AssetDatabase.CreateAsset(material, path);
        }

        return material;
    }

    static Material CreateUnlitMaterial(string path, string name)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            material = new Material(shader) { name = name };
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_ZWrite", 1f);
            material.renderQueue = (int)RenderQueue.Geometry;
            AssetDatabase.CreateAsset(material, path);
        }

        return material;
    }
}

[Serializable]
public sealed class P13PlayerBuildSummary
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
