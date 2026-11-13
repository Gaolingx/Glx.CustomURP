using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class ScreenSpaceLightingFrameDebuggerCapture
{
    static readonly string[] ModeNames = { "Forward", "ForwardPlus", "Deferred" };
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

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

    [MenuItem("Tests/Screen Space Lighting/Capture T1 Frame Debugger")]
    public static void Capture()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Leave Play Mode before capturing the T1 Frame Debugger baseline.");
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before capturing the T1 Frame Debugger baseline.");

        previousGraphicsPipeline = GraphicsSettings.defaultRenderPipeline;
        previousQualityPipeline = QualitySettings.renderPipeline;
        previousScenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
        outputRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "PALN", "validation", "T1", "FrameDebugger"));
        Directory.CreateDirectory(outputRoot);
        report = new CaptureReport
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            modes = new List<ModeEvents>()
        };
        utilityType = typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
        if (utilityType == null)
            throw new TypeLoadException("Unity FrameDebuggerUtility type was not found.");

        EditorSceneManager.OpenScene(ScreenSpaceLightingBaselineSetup.ScenePath, OpenSceneMode.Single);
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
            if (timeoutFrames-- <= 0)
                throw new TimeoutException("Timed out while waiting for Frame Debugger events. " + lastDiagnostics);
            if (waitFrames-- > 0)
                return;

            SetFrameDebuggerEnabled(true);
            var count = (int)utilityType.GetProperty("count", StaticFlags).GetValue(null);
            if (count <= 0)
            {
                var locallySupported = (bool)utilityType.GetProperty("locallySupported", StaticFlags).GetValue(null);
                var receivingRemote = (bool)utilityType.GetProperty("receivingRemoteFrameEventData", StaticFlags).GetValue(null);
                var remoteGuid = (int)utilityType.GetMethod("GetRemotePlayerGUID", StaticFlags).Invoke(null, null);
                lastDiagnostics = $"count={count}, locallySupported={locallySupported}, receivingRemote={receivingRemote}, remoteGuid={remoteGuid}, paused={EditorApplication.isPaused}, playing={EditorApplication.isPlaying}";
                waitFrames = 10;
                return;
            }

            var mode = new ModeEvents
            {
                name = ModeNames[modeIndex],
                eventCount = count,
                eventsHash = (int)utilityType.GetProperty("eventsHash", StaticFlags).GetValue(null),
                eventDataHash = (uint)utilityType.GetProperty("eventDataHash", StaticFlags).GetValue(null),
                eventNames = new List<string>()
            };
            var getName = utilityType.GetMethod("GetFrameEventInfoName", StaticFlags);
            for (var i = 0; i < count; i++)
                mode.eventNames.Add((string)getName.Invoke(null, new object[] { i }));
            report.modes.Add(mode);
            File.WriteAllText(Path.Combine(outputRoot, ModeNames[modeIndex] + ".json"), JsonUtility.ToJson(mode, true));

            modeIndex++;
            if (modeIndex < ModeNames.Length)
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
        var path = "Assets/Tests/ScreenSpaceLighting/Renderers/Ssl" + ModeNames[index] + "Pipeline.asset";
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
            throw new FileNotFoundException("Missing T1 Pipeline Asset.", path);
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
    }

    static void CleanupAndExit()
    {
        EditorApplication.update -= Update;
        try
        {
            SetFrameDebuggerEnabled(false);
            GraphicsSettings.defaultRenderPipeline = previousGraphicsPipeline;
            QualitySettings.renderPipeline = previousQualityPipeline;
            EditorSettings.enterPlayModeOptionsEnabled = previousEnterPlayModeOptionsEnabled;
            EditorSettings.enterPlayModeOptions = previousEnterPlayModeOptions;
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(outputRoot, "FrameDebuggerCleanupError.txt"), exception.ToString());
            exitCode = 3;
        }
        EditorApplication.Exit(exitCode);
    }

    [Serializable]
    sealed class CaptureReport
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
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
