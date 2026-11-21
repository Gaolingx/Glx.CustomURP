using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// Builds a temporary copy of the SSL baseline scene as a D3D12 standalone player for RenderDoc.
// The runner's three pipeline slots are all pointed at the deferred pipeline and the sampling window
// is shortened, so a capture at frame ~100 lands in the deferred stencil path instead of waiting
// ~4000 frames for Forward/ForwardPlus to finish. The temporary scene is deleted after the build.
public static class ClusteredDeferredD3D12CaptureSetup
{
    const string ScenePath = "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingBaseline.unity";
    const string CaptureSceneFolder = "Assets/Tests/ClusteredDeferred/Capture";
    const string CaptureScenePath = CaptureSceneFolder + "/SslD3D12CaptureScene.unity";
    const string DeferredPipelinePath = "Assets/Tests/ScreenSpaceLighting/Renderers/SslDeferredPipeline.asset";
    const string ForwardPipelinePath = "Assets/Tests/ScreenSpaceLighting/Renderers/SslForwardPipeline.asset";
    const string ForwardPlusPipelinePath = "Assets/Tests/ScreenSpaceLighting/Renderers/SslForwardPlusPipeline.asset";
    const string OutputRoot = "Builds/ClusteredDeferredD3D12";
    const string ExecutableName = "SslCaptureD3D12.exe";

    [MenuItem("Tests/Clustered Deferred/Build D3D12 Capture Player")]
    public static void BuildFromMenu()
    {
        Build();
    }

    public static void BuildAndExit()
    {
        bool ok;
        try
        {
            ok = Build();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ok = false;
        }

        EditorApplication.Exit(ok ? 0 : 1);
    }

    public static bool Build()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outputDirectory = Path.Combine(projectRoot, OutputRoot);
        Directory.CreateDirectory(outputDirectory);
        var executablePath = Path.Combine(outputDirectory, ExecutableName);

        var previousApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        var previousFullScreenMode = PlayerSettings.fullScreenMode;
        var previousWidth = PlayerSettings.defaultScreenWidth;
        var previousHeight = PlayerSettings.defaultScreenHeight;
        var previousRunInBackground = PlayerSettings.runInBackground;
        var previousGraphicsPipeline = GraphicsSettings.defaultRenderPipeline;
        var buildApis = new[] { GraphicsDeviceType.Direct3D12 };

        // ShaderBuildPreprocessor only collects features from pipelines referenced by the build
        // target's quality levels. Park the three validation pipelines there so the deferred
        // variants survive the build; otherwise the player renders the URP error (magenta) shader.
        var previousQualityLevel = QualitySettings.GetQualityLevel();
        var qualityLevelCount = Mathf.Max(1, QualitySettings.names.Length);
        var previousQualityPipelines = new RenderPipelineAsset[qualityLevelCount];
        for (var i = 0; i < qualityLevelCount; i++)
        {
            QualitySettings.SetQualityLevel(i, true);
            previousQualityPipelines[i] = QualitySettings.renderPipeline;
        }
        QualitySettings.SetQualityLevel(previousQualityLevel, true);

        PrepareCaptureScene();
        BuildReport report;
        try
        {
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, buildApis);
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            PlayerSettings.runInBackground = true;

            var pipelines = new[]
            {
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(ForwardPipelinePath),
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(ForwardPlusPipelinePath),
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(DeferredPipelinePath),
            };
            if (pipelines[0] == null || pipelines[1] == null || pipelines[2] == null)
                throw new FileNotFoundException("Missing one of the SSL validation pipelines.");

            for (var i = 0; i < qualityLevelCount; i++)
            {
                QualitySettings.SetQualityLevel(i, true);
                QualitySettings.renderPipeline = pipelines[i % pipelines.Length];
            }
            QualitySettings.SetQualityLevel(previousQualityLevel, true);
            GraphicsSettings.defaultRenderPipeline = pipelines[2];

            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { CaptureScenePath },
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
            CleanupCaptureScene();
        }

        var summary = new BuildSummary
        {
            result = report.summary.result.ToString(),
            totalErrors = report.summary.totalErrors,
            executablePath = executablePath,
            graphicsApis = string.Join(",", buildApis),
            scene = CaptureScenePath,
            unityVersion = Application.unityVersion,
            totalSizeBytes = (long)report.summary.totalSize,
            totalTimeSeconds = report.summary.totalTime.TotalSeconds,
        };
        File.WriteAllText(Path.Combine(outputDirectory, "D3D12BuildSummary.json"), JsonUtility.ToJson(summary, true));

        Debug.Log("D3D12 capture player: " + report.summary.result + " -> " + executablePath);
        return report.summary.result == BuildResult.Succeeded;
    }

    static void PrepareCaptureScene()
    {
        if (!AssetDatabase.IsValidFolder(CaptureSceneFolder))
            AssetDatabase.CreateFolder("Assets/Tests/ClusteredDeferred", "Capture");

        AssetDatabase.DeleteAsset(CaptureScenePath);
        if (!AssetDatabase.CopyAsset(ScenePath, CaptureScenePath))
            throw new InvalidOperationException("Unable to copy the baseline scene to " + CaptureScenePath);

        var previousScenePath = SceneManager.GetActiveScene().path;
        var scene = EditorSceneManager.OpenScene(CaptureScenePath, OpenSceneMode.Single);
        var runner = UnityEngine.Object.FindObjectOfType<ScreenSpaceLightingBaselineRunner>();
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(DeferredPipelinePath);
        if (runner == null || pipeline == null)
            throw new InvalidOperationException("The capture scene is missing its baseline runner or pipeline.");

        runner.pipelines = new[] { pipeline, pipeline, pipeline };
        runner.warmupFrames = 20;
        runner.sampleFrames = 600;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        if (!string.IsNullOrEmpty(previousScenePath))
            EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
    }

    static void CleanupCaptureScene()
    {
        AssetDatabase.DeleteAsset(CaptureScenePath);
        if (AssetDatabase.IsValidFolder(CaptureSceneFolder) &&
            AssetDatabase.FindAssets(string.Empty, new[] { CaptureSceneFolder }).Length == 0)
        {
            AssetDatabase.DeleteAsset(CaptureSceneFolder);
        }
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