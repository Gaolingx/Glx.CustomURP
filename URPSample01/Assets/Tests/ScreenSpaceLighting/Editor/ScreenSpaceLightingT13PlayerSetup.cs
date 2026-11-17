using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Builds the standalone player used by the T13 XR verification. Mock HMD only produces render
// passes while a player renders, so the XR cases run in this player, not in the editor.
public static class ScreenSpaceLightingT13PlayerSetup
{
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT13XrPlayer.unity";
    const string PipelinePath = "Assets/Tests/ScreenSpaceLighting/Renderers/SslT12ForwardPipeline.asset";
    const string OutputRoot = "PLAN/Done/01-ScreenSpaceLighting/validation/T13";

    [MenuItem("Tests/Screen Space Lighting/Create T13 XR Player Scene")]
    public static void CreateScene()
    {
        EnsureScene();
    }

    [MenuItem("Tests/Screen Space Lighting/Build T13 XR Player")]
    public static void BuildPlayer()
    {
        EnsureScene();

        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot, "Player");
        Directory.CreateDirectory(outputDirectory);
        var executablePath = Path.Combine(outputDirectory, "SslT13XrPlayer.exe");

        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = executablePath,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.Development | BuildOptions.IncludeTestAssemblies,
        };

        // The player must carry every API the T13 matrix exercises.
        var previousApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[]
        {
            GraphicsDeviceType.Direct3D11,
            GraphicsDeviceType.Direct3D12,
            GraphicsDeviceType.Vulkan,
        });

        var previousPipeline = GraphicsSettings.defaultRenderPipeline;
        var previousQualityPipeline = QualitySettings.renderPipeline;
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
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
        }

        var summary = report.summary;
        var summaryPath = Path.Combine(projectRoot, OutputRoot, "T13PlayerBuildSummary.json");
        File.WriteAllText(summaryPath, JsonUtility.ToJson(new T13PlayerBuildSummary
        {
            result = summary.result.ToString(),
            totalErrors = summary.totalErrors,
            totalWarnings = summary.totalWarnings,
            totalSizeBytes = (long)summary.totalSize,
            totalTimeSeconds = summary.totalTime.TotalSeconds,
            executablePath = executablePath,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            unityVersion = Application.unityVersion,
        }, true));

        Debug.Log($"T13 XR player build: {summary.result} errors={summary.totalErrors} path={executablePath}");
        EditorApplication.Exit(summary.result == BuildResult.Succeeded && summary.totalErrors == 0 ? 0 : 1);
    }

        static void EnsureScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var probeObject = new GameObject("T13 XR Player Verification");
        var probe = probeObject.AddComponent<ScreenSpaceLightingT13PlayerVerification>();
        probe.pipelineAsset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
        probe.metalMaterial = CreateLitMaterial(
            "Assets/Tests/ScreenSpaceLighting/Materials/T13ProbeMetal.mat",
            "T13ProbeMetal", new Color(0.9f, 0.86f, 0.7f), 1f, 0.95f, false);
        probe.floorMaterial = CreateLitMaterial(
            "Assets/Tests/ScreenSpaceLighting/Materials/T13ProbeFloor.mat",
            "T13ProbeFloor", new Color(0.55f, 0.57f, 0.6f), 0f, 0.6f, false);
        probe.backdropMaterial = CreateLitMaterial(
            "Assets/Tests/ScreenSpaceLighting/Materials/T13ProbeBackdrop.mat",
            "T13ProbeBackdrop", new Color(0.05f, 0.05f, 0.05f), 0f, 0.2f, true);
        probe.glassMaterial = AssetDatabase.LoadAssetAtPath<Material>(
            "Assets/Tests/ScreenSpaceLighting/Materials/T11RefractionBox.mat");
        probe.transparentReceiverMaterial = AssetDatabase.LoadAssetAtPath<Material>(
            "Assets/Tests/ScreenSpaceLighting/Materials/T10TransparentReceiver.mat");
        probe.arrayProbeCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/Tests/ScreenSpaceLighting/Shaders/T13XrArrayProbe.compute");

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    // The player has no Shader.Find lookup for stripped shaders, so every probe material is a real
    // asset referenced by the scene.
    static Material CreateLitMaterial(string path, string name, Color color, float metallic, float smoothness, bool emissive)
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
                material.SetColor("_EmissionColor", new Color(1.6f, 0.9f, 0.3f));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AssetDatabase.CreateAsset(material, path);
        }

        return material;
    }
}

[Serializable]
public sealed class T13PlayerBuildSummary
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