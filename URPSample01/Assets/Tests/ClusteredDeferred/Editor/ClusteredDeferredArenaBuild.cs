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
    const string T4ValidationRoot = "PLAN/TODO/11-LightCulling/validation/T4-shadows";

    public static void BuildD3D11AndExit()
    {
        Exit(Build(GraphicsDeviceType.Direct3D11, false));
    }

    public static void BuildD3D12AndExit()
    {
        Exit(Build(GraphicsDeviceType.Direct3D12, false));
    }

    public static void BuildT4D3D11AndExit()
    {
        Exit(Build(GraphicsDeviceType.Direct3D11, true));
    }

    public static void BuildT4D3D12AndExit()
    {
        Exit(Build(GraphicsDeviceType.Direct3D12, true));
    }

    static void Exit(bool ok)
    {
        EditorApplication.Exit(ok ? 0 : 1);
    }

    public static bool Build(GraphicsDeviceType api, bool t4)
    {
        if (!ClusteredDeferredTestAssetSetup.CreateAll())
            return false;

        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var apiName = api.ToString();
        var buildRoot = t4 ? "ClusteredDeferredArenaT4" : "ClusteredDeferredArena";
        var executableName = t4 ? "CdArenaT4.exe" : "CdArena.exe";
        var outputDirectory = Path.Combine(projectRoot, "Builds", buildRoot, apiName);
        Directory.CreateDirectory(outputDirectory);
        var executablePath = Path.Combine(outputDirectory, executableName);

        var previousUseDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
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
            // levels; without this the player loses the deferred and Forward+ variants. The T4
            // build parks the shadow pipelines instead so their keywords (cascade, soft, atlas,
            // screen space) survive stripping.
            var pipelinePaths = t4
                ? new[]
                {
                    "Assets/Tests/ClusteredDeferred/Renderers/CdShadowStencilDeferredPipeline.asset",
                    "Assets/Tests/ClusteredDeferred/Renderers/CdShadowClusteredDeferredPipeline.asset",
                    "Assets/Tests/ClusteredDeferred/Renderers/CdScreenShadowStencilDeferredPipeline.asset",
                    "Assets/Tests/ClusteredDeferred/Renderers/CdFeatureStencilDeferredPipeline.asset",
                }
                : new[]
                {
                    "Assets/Tests/ClusteredDeferred/Renderers/CdStencilDeferredPipeline.asset",
                    "Assets/Tests/ClusteredDeferred/Renderers/CdClusteredDeferredPipeline.asset",
                    "Assets/Tests/ClusteredDeferred/Renderers/CdForwardPlusPipeline.asset",
                };
            var pipelines = new RenderPipelineAsset[pipelinePaths.Length];
            for (var i = 0; i < pipelinePaths.Length; i++)
            {
                pipelines[i] = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(pipelinePaths[i]);
                if (pipelines[i] == null)
                    throw new FileNotFoundException("Missing test pipeline " + pipelinePaths[i]);
            }

            for (var i = 0; i < qualityLevelCount; i++)
            {
                QualitySettings.SetQualityLevel(i, true);
                QualitySettings.renderPipeline = pipelines[i % pipelines.Length];
            }

            QualitySettings.SetQualityLevel(previousQualityLevel, true);
            // URP's shader prefiltering is driven by the active (default) pipeline asset and its
            // renderer features. The T4 build needs the screen-space shadow, GTAO and light-layer
            // variants, so the feature pipeline (both features attached) is the active asset.
            GraphicsSettings.defaultRenderPipeline = t4 ? pipelines[pipelines.Length - 1] : pipelines[0];

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
            if (previousUseDefaultApis)
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, true);
            else
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

        var summaryDirectory = Path.Combine(projectRoot, t4 ? T4ValidationRoot : ValidationRoot);
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
        var summaryName = (t4 ? "CdArenaT4BuildSummary-" : "CdArenaBuildSummary-") + apiName + ".json";
        File.WriteAllText(Path.Combine(summaryDirectory, summaryName), JsonUtility.ToJson(summary, true));

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
