using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class ScreenSpaceLightingFrameDebuggerCapture
{
    static readonly string[] DefaultModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly string[] DefaultPipelinePaths =
    {
        "Assets/Tests/ScreenSpaceLighting/Renderers/SslForwardPipeline.asset",
        "Assets/Tests/ScreenSpaceLighting/Renderers/SslForwardPlusPipeline.asset",
        "Assets/Tests/ScreenSpaceLighting/Renderers/SslDeferredPipeline.asset"
    };
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const int ProbeWidth = 320;
    const int ProbeHeight = 180;

    static string[] modeNames = DefaultModeNames;
    static string[] pipelinePaths = DefaultPipelinePaths;
    static Material probeMaterial;
    static RenderTexture probeTarget;

    static Type utilityType;
    static RenderPipelineAsset previousGraphicsPipeline;
    static RenderPipelineAsset previousQualityPipeline;
    static bool previousEnterPlayModeOptionsEnabled;
    static EnterPlayModeOptions previousEnterPlayModeOptions;
    static string previousScenePath;
    static string outputRoot;
    static CaptureReport report;
    static int modeIndex;
    static int waitFrames;
    static bool finishing;
    static int exitCode;
    static int timeoutFrames;
    static string lastDiagnostics;
    static bool ssrPbrRequested;
    static bool volumeOverridden;
    static VolumeProfile clonedProfile;

    [MenuItem("Tests/Screen Space Lighting/Capture Frame Debugger")]
    public static void Capture()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Leave Play Mode before capturing the Frame Debugger baseline.");
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before capturing the Frame Debugger baseline.");

        modeNames = DefaultModeNames;
        pipelinePaths = DefaultPipelinePaths;
        var defaultOutput = Path.Combine("PALN", "validation", "T1", "FrameDebugger");
        var outputArgument = GetArgument("--ssl-fd-output") ?? defaultOutput;
        var namesArgument = GetArgument("--ssl-fd-names");
        var pipelinesArgument = GetArgument("--ssl-fd-pipelines");
        var sceneArgument = GetArgument("--ssl-fd-scene") ?? ScreenSpaceLightingBaselineSetup.ScenePath;
        ssrPbrRequested = GetArgument("--ssl-fd-ssr-pbr") != null;
        if (namesArgument != null)
            modeNames = namesArgument.Split(',');
        if (pipelinesArgument != null)
            pipelinePaths = pipelinesArgument.Split(',');
        if (modeNames.Length != pipelinePaths.Length || modeNames.Length == 0)
            throw new ArgumentException("--ssl-fd-names and --ssl-fd-pipelines must have the same length.");

        previousGraphicsPipeline = GraphicsSettings.defaultRenderPipeline;
        previousQualityPipeline = QualitySettings.renderPipeline;
        previousScenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
        outputRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", outputArgument));
        Directory.CreateDirectory(outputRoot);
        EnsureProbeResources();
        report = new CaptureReport
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            scene = sceneArgument,
            modes = new List<ModeEvents>()
        };
        utilityType = typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
        if (utilityType == null)
            throw new TypeLoadException("Unity FrameDebuggerUtility type was not found.");

        EditorSceneManager.OpenScene(sceneArgument, OpenSceneMode.Single);
        modeIndex = 0;
        waitFrames = 30;
        finishing = false;
        exitCode = 0;
        timeoutFrames = 3600;
        SetPipeline(modeIndex);
        OpenFrameDebugger();
        previousEnterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        previousEnterPlayModeOptions = EditorSettings.enterPlayModeOptions;
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = previousEnterPlayModeOptions | EnterPlayModeOptions.DisableDomainReload;
        EditorApplication.update += Update;
        Debug.Log($"[SslFd] capture start output={outputRoot} modes={string.Join(",", modeNames)} pbr={ssrPbrRequested}");
        EditorApplication.EnterPlaymode();
    }

    static void Update()
    {
        try
        {
            if (finishing)
            {
                if (!EditorApplication.isPlaying)
                    CleanupAndExit();
                return;
            }
            if (!EditorApplication.isPlaying)
                return;
            ApplySsrPbrVolume();
            if (waitFrames == 30)
                Debug.Log("[SslFd] play mode detected, waiting for frames");
            if (timeoutFrames-- <= 0)
                throw new TimeoutException("Timed out while waiting for Frame Debugger events. " + lastDiagnostics);
            if (waitFrames-- > 0)
                return;

            EnsureProbeResources();
            SetFrameDebuggerEnabled(true);
            var count = (int)utilityType.GetProperty("count", StaticFlags).GetValue(null);
            if (count <= 0)
            {
                var locallySupported = (bool)utilityType.GetProperty("locallySupported", StaticFlags).GetValue(null);
                var receivingRemote = (bool)utilityType.GetProperty("receivingRemoteFrameEventData", StaticFlags).GetValue(null);
                var remoteGuid = (int)utilityType.GetMethod("GetRemotePlayerGUID", StaticFlags).Invoke(null, null);
                lastDiagnostics = $"count={count}, locallySupported={locallySupported}, receivingRemote={receivingRemote}, remoteGuid={remoteGuid}, paused={EditorApplication.isPaused}, playing={EditorApplication.isPlaying}";
                Debug.Log("[SslFd] no events yet: " + lastDiagnostics);
                waitFrames = 10;
                return;
            }

            var mode = new ModeEvents
            {
                name = modeNames[modeIndex],
                eventCount = count,
                eventsHash = (int)utilityType.GetProperty("eventsHash", StaticFlags).GetValue(null),
                eventDataHash = (uint)utilityType.GetProperty("eventDataHash", StaticFlags).GetValue(null),
                eventNames = new List<string>()
            };
            var getName = utilityType.GetMethod("GetFrameEventInfoName", StaticFlags);
            for (var i = 0; i < count; i++)
                mode.eventNames.Add((string)getName.Invoke(null, new object[] { i }));
            report.modes.Add(mode);
            File.WriteAllText(Path.Combine(outputRoot, modeNames[modeIndex] + ".json"), JsonUtility.ToJson(mode, true));
            Debug.Log($"[SslFd] captured {mode.name} events={mode.eventCount}");

            modeIndex++;
            if (modeIndex < modeNames.Length)
            {
                SetFrameDebuggerEnabled(false);
                EditorApplication.isPaused = false;
                SetPipeline(modeIndex);
                waitFrames = 30;
                return;
            }

            report.completed = true;
            File.WriteAllText(Path.Combine(outputRoot, "FrameDebuggerReport.json"), JsonUtility.ToJson(report, true));
            SetFrameDebuggerEnabled(false);
            finishing = true;
            EditorApplication.isPlaying = false;
        }
        catch (Exception exception)
        {
            report.completed = false;
            report.error = exception.ToString();
            Directory.CreateDirectory(outputRoot);
            File.WriteAllText(Path.Combine(outputRoot, "FrameDebuggerError.txt"), exception.ToString());
            File.WriteAllText(Path.Combine(outputRoot, "FrameDebuggerReport.json"), JsonUtility.ToJson(report, true));
            exitCode = 2;
            finishing = true;
            EditorApplication.isPlaying = false;
        }
    }

    static void ApplySsrPbrVolume()
    {
        if (!ssrPbrRequested || volumeOverridden)
            return;
        var volumeObject = GameObject.Find("Baseline Volume");
        var volume = volumeObject != null ? volumeObject.GetComponent<Volume>() : null;
        if (volume == null || volume.sharedProfile == null)
            return;

        clonedProfile = UnityEngine.Object.Instantiate(volume.sharedProfile);
        clonedProfile.name = volume.sharedProfile.name + " (SSR T5 Frame Debugger)";
        var ssr = clonedProfile.components.OfType<ScreenSpaceReflection>().FirstOrDefault();
        if (ssr != null)
        {
            ssr.enabled.Override(true);
            ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.PBRAccumulation);
            ssr.fullResolution.Override(true);
            ssr.minSmoothness.Override(0.5f);
            ssr.smoothnessFadeStart.Override(0.9f);
            ssr.denoiserRadius.Override(8);
        }

        volume.sharedProfile = clonedProfile;
        volumeOverridden = true;
    }

    static void OpenFrameDebugger()
    {
        var windowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.FrameDebuggerWindow");
        if (windowType == null)
            throw new TypeLoadException("Unity FrameDebuggerWindow type was not found.");
        windowType.GetMethod("OpenWindow", StaticFlags).Invoke(null, null);
        SetFrameDebuggerEnabled(true);
    }

    static void SetFrameDebuggerEnabled(bool enabled)
    {
        int remotePlayerGuid;
        if (enabled)
        {
            var profilerDriverType = typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.ProfilerDriver");
            remotePlayerGuid = (int)profilerDriverType.GetProperty("connectedProfiler", StaticFlags).GetValue(null);
            if (EditorApplication.isPlaying && !EditorApplication.isPaused)
                EditorApplication.isPaused = true;
        }
        else
        {
            remotePlayerGuid = (int)utilityType.GetMethod("GetRemotePlayerGUID", StaticFlags).Invoke(null, null);
        }
        utilityType.GetMethod("SetEnabled", StaticFlags).Invoke(null, new object[] { enabled, remotePlayerGuid });
    }

    static void SetPipeline(int index)
    {
        var path = pipelinePaths[index];
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
            throw new FileNotFoundException("Missing Pipeline Asset.", path);
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        // The probe feature only enqueues when it has a material and target; without them the capture
        // would silently record the baseline schedule instead of the early-input schedule.
        EnsureProbeResources();
        var rendererPath = path.Replace("Pipeline.asset", "Renderer.asset");
        var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        if (rendererData == null)
            return;
        EnsureSslFeatureResources(rendererData);
        foreach (var rendererFeature in rendererData.rendererFeatures)
        {
            if (rendererFeature is ScreenSpaceLightingPassProbeFeature probe)
            {
                probe.probeMaterial = probeMaterial;
                probe.SetActive(true);
            }
        }
        ScreenSpaceLightingPassProbeFeature.probeTarget = probeTarget;
    }

    static void EnsureSslFeatureResources(UniversalRendererData rendererData)
    {
        var locator = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceLightingResourceLocator");
        var assignDefaults = locator?.GetMethod("AssignDefaults", StaticFlags);
        if (assignDefaults == null)
            return;

        // Verification renderers were serialized before the denoiser compute existed; fill the
        // package defaults in memory so the PBR SSR passes can enqueue without rewriting the asset.
        foreach (var feature in rendererData.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>())
        {
            var settings = feature.GetType().GetProperty("settings", InstanceFlags)?.GetValue(feature);
            var resources = settings?.GetType().GetField("resources", InstanceFlags)?.GetValue(settings);
            if (resources != null)
                assignDefaults.Invoke(null, new[] { resources });
        }
    }

    static void EnsureProbeResources()
    {
        if (probeMaterial == null)
        {
            probeMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Tests/ScreenSpaceLighting/Materials/SslProbe.mat");
            if (probeMaterial == null)
                throw new FileNotFoundException("Missing probe material.", "Assets/Tests/ScreenSpaceLighting/Materials/SslProbe.mat");
        }
        if (probeTarget == null)
        {
            probeTarget = new RenderTexture(ProbeWidth, ProbeHeight, 0, GraphicsFormat.R32G32B32A32_SFloat) { name = "SslT2Probe" };
            probeTarget.Create();
        }
        ScreenSpaceLightingPassProbeFeature.probeTarget = probeTarget;
    }

    static string GetArgument(string name)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var i = 0; i < arguments.Length - 1; i++)
        {
            if (arguments[i] == name)
                return arguments[i + 1];
        }
        return null;
    }

    static void CleanupAndExit()
    {
        EditorApplication.update -= Update;
        try
        {
            SetFrameDebuggerEnabled(false);
            DisposeClonedProfile();
            GraphicsSettings.defaultRenderPipeline = previousGraphicsPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            EditorSettings.enterPlayModeOptionsEnabled = previousEnterPlayModeOptionsEnabled;
            EditorSettings.enterPlayModeOptions = previousEnterPlayModeOptions;
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
            ScreenSpaceLightingPassProbeFeature.probeTarget = null;
            if (probeTarget != null)
            {
                probeTarget.Release();
                UnityEngine.Object.DestroyImmediate(probeTarget);
                probeTarget = null;
            }
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(outputRoot, "FrameDebuggerCleanupError.txt"), exception.ToString());
            exitCode = 3;
        }
        EditorApplication.Exit(exitCode);
    }

    static void DisposeClonedProfile()
    {
        if (clonedProfile != null)
        {
            UnityEngine.Object.DestroyImmediate(clonedProfile);
            clonedProfile = null;
        }
    }

    [Serializable]
    sealed class CaptureReport
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public string scene;
        public bool completed;
        public string error;
        public List<ModeEvents> modes;
    }

    [Serializable]
    sealed class ModeEvents
    {
        public string name;
        public int eventCount;
        public int eventsHash;
        public uint eventDataHash;
        public List<string> eventNames;
    }
}
