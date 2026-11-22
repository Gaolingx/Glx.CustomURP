using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering.Universal;
using UnityEngine;
using UnityEngine.Rendering;

// Builds the clustered deferred arena player for the A/B, crossover and RenderDoc captures.
// Called with -executeMethod ClusteredDeferredArenaBuild.BuildD3D11AndExit or BuildD3D12AndExit.
public static class ClusteredDeferredArenaBuild
{
    const string ScenePath = "Assets/Tests/ClusteredDeferred/Scenes/ClusteredDeferredArena.unity";
    const string ValidationRoot = "PLAN/Done/16-CleanupBaseline/c4/player";
    const string T4ValidationRoot = ValidationRoot + "/t4";

    public static void BuildD3D11AndExit()
    {
        Exit(Build(GraphicsDeviceType.Direct3D11, false));
    }

    public static void BuildD3D12AndExit()
    {
        Exit(Build(GraphicsDeviceType.Direct3D12, false));
    }

    public static void BuildAsyncD3D11AndExit()
    {
        Exit(Build(GraphicsDeviceType.Direct3D11, false, true));
    }

    public static void BuildAsyncD3D12AndExit()
    {
        Exit(Build(GraphicsDeviceType.Direct3D12, false, true));
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

    static UniversalRenderPipelineAsset CopyPipeline(string source, string destination, bool asyncRequest)
    {
        if (!AssetDatabase.CopyAsset(source, destination))
            throw new IOException("Cannot copy pipeline " + source);
        var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(destination);
        var serialized = new SerializedObject(asset);
        serialized.FindProperty("m_AsyncLightListBuild").boolValue = asyncRequest;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssetIfDirty(asset);
        return asset;
    }

    public static bool Build(GraphicsDeviceType api, bool t4, bool asyncRequest = false)
    {

        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var apiName = api.ToString();
        var buildRoot = t4 ? "ClusteredDeferredArenaC4T4" : "ClusteredDeferredArenaC4";
        if (asyncRequest) buildRoot += "Async";
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

        string stagingFolder = null;
        var buildScenePath = ScenePath;
        BuildReport report;
        try
        {
            // SetGraphicsAPIs only takes effect when the target stops using its default API list;
            // without this the "Direct3D12" player silently launches on D3D11.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
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
                    ClusteredDeferredTestAssetSetup.T4Prefix("Shadow") + "ClusteredDeferredPipeline.asset",
                    ClusteredDeferredTestAssetSetup.T4Prefix("ScreenShadow") + "ClusteredDeferredPipeline.asset",
                    ClusteredDeferredTestAssetSetup.T4Prefix("Feature") + "ClusteredDeferredPipeline.asset",
                }
                : new[]
                {
                    ClusteredDeferredTestAssetSetup.ClusteredPipelinePath,
                    ClusteredDeferredTestAssetSetup.ForwardPlusPipelinePath,
                };
            if (qualityLevelCount < pipelinePaths.Length)
                throw new InvalidOperationException("Not enough quality slots to retain every arena loadout.");
            var pipelines = new RenderPipelineAsset[pipelinePaths.Length];
            for (var i = 0; i < pipelinePaths.Length; i++)
            {
                pipelines[i] = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(pipelinePaths[i]);
                if (pipelines[i] == null)
                    throw new FileNotFoundException("Missing test pipeline " + pipelinePaths[i]);
            }

            if (!t4)
            {
                // Build from serialized copies. The scene and quality slots must reference the
                // same pair; changing just the quality slots is undone by the runner at launch.
                var guid = AssetDatabase.CreateFolder("Assets/Tests/ClusteredDeferred", "BuildStage_" + Guid.NewGuid().ToString("N"));
                if (string.IsNullOrEmpty(guid)) throw new IOException("Cannot create arena build staging folder.");
                stagingFolder = AssetDatabase.GUIDToAssetPath(guid);
                var clustered = CopyPipeline(pipelinePaths[0], stagingFolder + "/Clustered.asset", asyncRequest);
                var forward = CopyPipeline(pipelinePaths[1], stagingFolder + "/ForwardPlus.asset", asyncRequest);
                var alternate = CopyPipeline(pipelinePaths[0], stagingFolder + "/ParityAlternate.asset", !asyncRequest);
                pipelines[0] = clustered;
                pipelines[1] = forward;
                buildScenePath = stagingFolder + "/ClusteredDeferredArena.unity";
                if (!AssetDatabase.CopyAsset(ScenePath, buildScenePath)) throw new IOException("Cannot stage arena scene.");
                var scene = EditorSceneManager.OpenScene(buildScenePath, OpenSceneMode.Additive);
                try
                {
                    var root = Array.Find(scene.GetRootGameObjects(), go => go.GetComponent<ClusteredDeferredArenaRunner>() != null);
                    if (root == null) throw new InvalidOperationException("Staged scene is missing the arena runner.");
                    var runner = root.GetComponent<ClusteredDeferredArenaRunner>();
                    runner.clusteredDeferredPipeline = clustered;
                    runner.forwardPlusPipeline = forward;
                    runner.parityAlternateClusteredPipeline = alternate;
                    if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Cannot save staged arena scene.");
                }
                finally
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
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
                scenes = new[] { buildScenePath },
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
            if (stagingFolder != null && !AssetDatabase.DeleteAsset(stagingFolder))
                Debug.LogError("Could not remove arena build staging folder: " + stagingFolder);
        }

        var summaryDirectory = Path.Combine(projectRoot, t4 ? T4ValidationRoot : asyncRequest ? ValidationRoot + "/async" : ValidationRoot);
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
