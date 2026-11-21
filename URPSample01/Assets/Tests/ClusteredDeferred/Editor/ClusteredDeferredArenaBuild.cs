using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

// Builds the clustered deferred arena player for the A/B, crossover and RenderDoc captures.
// Called with -executeMethod ClusteredDeferredArenaBuild.BuildD3D11AndExit or BuildD3D12AndExit.
public static class ClusteredDeferredArenaBuild
{
    const string ScenePath = "Assets/Tests/ClusteredDeferred/Scenes/ClusteredDeferredArena.unity";
    const string ValidationRoot = "PLAN/TODO/11-LightCulling/validation/T5-runtime";
    const string ExecutableName = "CdArena.exe";

    public static void BuildD3D11AndExit()
    {
        Exit(Build(GraphicsDeviceType.Direct3D11));
    }

    public static void BuildD3D12AndExit()
    {
        Exit(Build(GraphicsDeviceType.Direct3D12));
    }

    static void Exit(bool ok)
    {
        EditorApplication.Exit(ok ? 0 : 1);
    }

    public static bool Build(GraphicsDeviceType api)
    {
        if (!ClusteredDeferredTestAssetSetup.CreateAll())
            return false;

        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var apiName = api.ToString();
        var outputDirectory = Path.Combine(projectRoot, "Builds", "ClusteredDeferredArena", apiName);
        Directory.CreateDirectory(outputDirectory);
        var executablePath = Path.Combine(outputDirectory, ExecutableName);

        var previousApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        var previousFullScreenMode = PlayerSettings.fullScreenMode;
        var previousWidth = PlayerSettings.defaultScreenWidth;
        var previousHeight = PlayerSettings.defaultScreenHeight;
        var previousRunInBackground = PlayerSettings.runInBackground;
        var previousGraphicsPipeline = GraphicsSettings.defaultRenderPipeline;

        var previousQualityLevel = QualitySettings.GetQualityLevel();
        var qualityLevelCount = Mathf.Max(1, QualitySettings.names.Length);
        var previousQualityPipelines = new RenderPipelineAsset[qualityLevelCount];
        for (var i = 0; i < qualityLevelCount; i++)
        {
            QualitySettings.SetQualityLevel(i, true);
            previousQualityPipelines[i] = QualitySettings.renderPipeline;
        }

        QualitySettings.SetQualityLevel(previousQualityLevel, true);

        BuildReport report;
        try
        {
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { api });
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            PlayerSettings.runInBackground = true;

            // ShaderBuildPreprocessor only collects features from pipelines parked on quality
            // levels; without this the player loses the deferred and Forward+ variants.
            var pipelines = new[]
            {
                AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(
                    "Assets/Tests/ClusteredDeferred/Renderers/CdStencilDeferredPipeline.asset"),
                AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(
                    "Assets/Tests/ClusteredDeferred/Renderers/CdClusteredDeferredPipeline.asset"),
                AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(
                    "Assets/Tests/ClusteredDeferred/Renderers/CdForwardPlusPipeline.asset"),
            };
            if (pipelines[0] == null || pipelines[1] == null || pipelines[2] == null)
                throw new FileNotFoundException("Missing one of the clustered deferred test pipelines.");

            for (var i = 0; i < qualityLevelCount; i++)
            {
                QualitySettings.SetQualityLevel(i, true);
                QualitySettings.renderPipeline = pipelines[i % pipelines.Length];
            }

            QualitySettings.SetQualityLevel(previousQualityLevel, true);
            GraphicsSettings.defaultRenderPipeline = pipelines[0];

            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = executablePath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.Development,
            });
        }
        finally
        {
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, previousApis);
            PlayerSettings.fullScreenMode = previousFullScreenMode;
            PlayerSettings.defaultScreenWidth = previousWidth;
            PlayerSettings.defaultScreenHeight = previousHeight;
            PlayerSettings.runInBackground = previousRunInBackground;
            GraphicsSettings.defaultRenderPipeline = previousGraphicsPipeline;
            for (var i = 0; i < qualityLevelCount; i++)
            {
                QualitySettings.SetQualityLevel(i, true);
                QualitySettings.renderPipeline = previousQualityPipelines[i];
            }

            QualitySettings.SetQualityLevel(previousQualityLevel, true);
        }

        var summaryDirectory = Path.Combine(projectRoot, ValidationRoot);
        Directory.CreateDirectory(summaryDirectory);
        var summary = new BuildSummary
        {
            result = report.summary.result.ToString(),
            totalErrors = report.summary.totalErrors,
            executablePath = executablePath,
            graphicsApis = apiName,
            scene = ScenePath,
            unityVersion = Application.unityVersion,
            totalSizeBytes = (long)report.summary.totalSize,
            totalTimeSeconds = report.summary.totalTime.TotalSeconds,
        };
        File.WriteAllText(Path.Combine(summaryDirectory, "CdArenaBuildSummary-" + apiName + ".json"),
            JsonUtility.ToJson(summary, true));

        Debug.Log("Clustered deferred " + apiName + " player: " + report.summary.result + " -> " + executablePath);
        return report.summary.result == BuildResult.Succeeded;
    }

    [Serializable]
    public class BuildSummary
    {
        public string result;
        public int totalErrors;
        public string executablePath;
        public string graphicsApis;
        public string scene;
        public string unityVersion;
        public long totalSizeBytes;
        public double totalTimeSeconds;
    }
}
