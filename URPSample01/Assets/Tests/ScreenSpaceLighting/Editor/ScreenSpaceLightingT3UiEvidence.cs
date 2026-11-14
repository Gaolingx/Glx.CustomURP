using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.Universal;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Bitmap = System.Drawing.Bitmap;
using GdiGraphics = System.Drawing.Graphics;
using GdiColor = System.Drawing.Color;
using GdiImageFormat = System.Drawing.Imaging.ImageFormat;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

// T3 UI acceptance helper. It builds an empty demo renderer, pipeline and volume profile, then drives
// the real Editor entry points the URP menus use: ScriptableRendererDataEditor.AddComponent,
// VolumeComponentListEditor.AddComponent and the matching FilterWindow dropdowns. Screenshots are
// captured from inside the editor with PrintWindow, so the acceptance does not depend on the editor
// being foreground and does not use synthetic mouse coordinates.
public static class ScreenSpaceLightingT3UiEvidence
{
    const string Root = "Assets/Tests/ScreenSpaceLighting";
    public const string DemoRendererPath = Root + "/Renderers/SslT3UiDemoRenderer.asset";
    public const string DemoPipelinePath = Root + "/Renderers/SslT3UiDemoPipeline.asset";
    public const string DemoProfilePath = Root + "/Profiles/SslT3UiDemoProfile.asset";
    const string T1RendererPath = Root + "/Renderers/SslForwardRenderer.asset";

    static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    static readonly string UiRoot = Path.Combine(ProjectRoot, "PALN/validation/T3/UI");
    static readonly string EvidenceReportPath = Path.Combine(UiRoot, "T3UiEvidence.json");

    static VolumeComponentListEditor s_VolumeListEditor;

    [Serializable]
    public class EvidenceReport
    {
        public string utcTimestamp;
        public string unityVersion;
        public string graphicsDevice;
        public string status = "running";
        public string scenePath;
        public string demoRendererPath;
        public string demoPipelinePath;
        public string demoProfilePath;
        public List<string> rendererFeatures = new List<string>();
        public List<VolumeComponentRecord> volumeComponents = new List<VolumeComponentRecord>();
        public string cameraName;
        public int cameraVolumeMask;
        public string cameraVolumeMaskLayers;
        public string globalVolumeProfile;
        public List<EvidenceCapture> captures = new List<EvidenceCapture>();
        public List<string> errors = new List<string>();
    }

    [Serializable]
    public class VolumeComponentRecord
    {
        public string type;
        public bool enabled;
    }

    [Serializable]
    public class EvidenceCapture
    {
        public string step;
        public string file;
        public int width;
        public int height;
        public long bytes;
        public bool filterWindowOpen;
    }

    class EvidenceStep
    {
        public string name;
        public string capture;
        public Action action;
        public Action prepare;
        public int scrollTicks;
        public bool expectPopup;
        public float preDelay = 0.3f;
        public float captureDelay = 0.9f;
    }

    static readonly List<EvidenceStep> s_Steps = new List<EvidenceStep>();
    static EvidenceReport s_Report;
    static int s_StepIndex = -1;
    static int s_Phase;
    static double s_NextTime;

    [InitializeOnLoadMethod]
    static void OnLoad()
    {
        EditorApplication.update -= Update;
        EditorApplication.update += Update;
    }

    [MenuItem("Tests/Screen Space Lighting/Open T3 UI Evidence Session")]
    public static void OpenSession()
    {
        if (SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before opening the UI evidence session.");

        PrepareDemoAssets();
        EditorSceneManager.OpenScene(ScreenSpaceLightingT3Verification.T3ScenePath, OpenSceneMode.Single);
        Select(DemoRendererPath);
        Directory.CreateDirectory(UiRoot);
        EditorApplication.update -= Update;
        EditorApplication.update += Update;
        Debug.Log("T3 UI evidence session ready.");
    }

    [MenuItem("Tests/Screen Space Lighting/Run T3 UI Evidence")]
    public static void RunEvidence()
    {
        try
        {
            StartEvidence();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            s_Report = s_Report ?? new EvidenceReport();
            s_Report.utcTimestamp = DateTime.UtcNow.ToString("O");
            s_Report.unityVersion = Application.unityVersion;
            s_Report.status = "failed";
            s_Report.errors.Add(exception.ToString());
            WriteReport();
            EditorApplication.Exit(1);
        }
    }

    static void StartEvidence()
    {
        Directory.CreateDirectory(UiRoot);
        foreach (var file in Directory.GetFiles(UiRoot, "T3_UI_*.png"))
            File.Delete(file);
        foreach (var file in Directory.GetFiles(UiRoot, "*.flag"))
            File.Delete(file);
        ClearConsole();

        if (SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save or discard the active scene before running the UI evidence session.");

        PrepareDemoAssets();
        EditorSceneManager.OpenScene(ScreenSpaceLightingT3Verification.T3ScenePath, OpenSceneMode.Single);
        AssetDatabase.SaveAssets();

        s_Report = new EvidenceReport
        {
            utcTimestamp = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            graphicsDevice = SystemInfo.graphicsDeviceName + " / " + SystemInfo.graphicsDeviceType,
            scenePath = ScreenSpaceLightingT3Verification.T3ScenePath,
            demoRendererPath = DemoRendererPath,
            demoPipelinePath = DemoPipelinePath,
            demoProfilePath = DemoProfilePath
        };

        s_Steps.Clear();
        AddStep("layout-reset", null, () =>
        {
            EditorApplication.ExecuteMenuItem("Window/Layouts/Default");
            UnlockInspectors();
            UnmaximizeInspectors();
        }, preDelay: 1.5f, captureDelay: 3.5f);
        AddStep("pipeline-selected", "T3_UI_01_pipeline_renderer_list.png",
            () => SelectForUi(DemoPipelinePath), preDelay: 1.0f, captureDelay: 1.5f,
            prepare: () => SelectForUi(DemoPipelinePath), scrollTicks: -100);
        AddStep("renderer-selected", "T3_UI_02_renderer_features_empty.png",
            () => SelectForUi(DemoRendererPath), preDelay: 1.0f, captureDelay: 1.5f,
            prepare: () => SelectForUi(DemoRendererPath), scrollTicks: 14);
        AddStep("renderer-popup-open", "T3_UI_03_add_renderer_feature.png", () =>
        {
            SelectForUi(DemoRendererPath);
            OpenRendererFeaturePopup();
        }, preDelay: 1.0f, captureDelay: 0.8f, expectPopup: true);
        AddStep("ssl-added", "T3_UI_04_ssl_added.png", () =>
        {
            AddRendererFeature(nameof(ScreenSpaceLightingRendererFeature));
            SelectForUi(DemoRendererPath);
        }, preDelay: 1.0f, captureDelay: 1.2f,
            prepare: () => SelectForUi(DemoRendererPath), scrollTicks: 14);
        AddStep("renderer-popup-again", "T3_UI_05_ssl_not_duplicated.png", () =>
        {
            SelectForUi(DemoRendererPath);
            OpenRendererFeaturePopup();
        }, preDelay: 1.0f, captureDelay: 0.8f, expectPopup: true);
        AddStep("gtao-added", "T3_UI_06_gtao_added.png", () =>
        {
            AddRendererFeature(nameof(GroundTruthAmbientOcclusionRendererFeature));
            SelectForUi(DemoRendererPath);
        }, preDelay: 1.0f, captureDelay: 1.2f,
            prepare: () => SelectForUi(DemoRendererPath), scrollTicks: 14);
        AddStep("gtao-selected", "T3_UI_07_gtao_feature_inspector.png",
            () => SelectFeatureForUi<GroundTruthAmbientOcclusionRendererFeature>(), preDelay: 1.0f, captureDelay: 1.2f,
            prepare: () => SelectFeatureForUi<GroundTruthAmbientOcclusionRendererFeature>());
        AddStep("profile-selected", "T3_UI_08_volume_profile_empty.png",
            () => SelectForUi(DemoProfilePath), preDelay: 1.0f, captureDelay: 1.2f,
            prepare: () => SelectForUi(DemoProfilePath));
        AddStep("volume-popup-open", "T3_UI_09_add_override_lighting.png", () =>
        {
            SelectForUi(DemoProfilePath);
            OpenVolumePopup();
        }, preDelay: 1.0f, captureDelay: 0.8f, expectPopup: true);
        AddStep("overrides-added", "T3_UI_10_volume_overrides_added.png", () =>
        {
            AddVolumeComponent(typeof(ScreenSpaceGlobalIllumination));
            AddVolumeComponent(typeof(ScreenSpaceReflection));
            AddVolumeComponent(typeof(ScreenSpaceRefraction));
            SelectForUi(DemoProfilePath);
        }, preDelay: 1.0f, captureDelay: 1.4f,
            prepare: () => SelectForUi(DemoProfilePath));
        AddStep("overrides-enabled", "T3_UI_11_volume_overrides_enabled.png", () =>
        {
            EnableVolumeEffects();
            SelectForUi(DemoProfilePath);
        }, preDelay: 1.0f, captureDelay: 1.4f,
            prepare: () => SelectForUi(DemoProfilePath));
        AddStep("global-volume-selected", "T3_UI_12_global_volume.png", () =>
        {
            var volume = FindGlobalVolume();
            if (volume != null)
                SelectObjectForUi(volume.gameObject);
        }, preDelay: 1.0f, captureDelay: 1.2f, prepare: () =>
        {
            var volume = FindGlobalVolume();
            if (volume != null)
                SelectObjectForUi(volume.gameObject);
        });
        AddStep("camera-selected", "T3_UI_13_camera_volume_mask.png", () =>
        {
            var camera = UnityEngine.Object.FindObjectOfType<Camera>();
            if (camera != null)
                SelectObjectForUi(camera.gameObject);
        }, preDelay: 1.0f, captureDelay: 1.2f, prepare: () =>
        {
            var camera = UnityEngine.Object.FindObjectOfType<Camera>();
            if (camera != null)
                SelectObjectForUi(camera.gameObject);
        }, scrollTicks: 22);

        s_StepIndex = 0;
        s_Phase = 0;
        s_NextTime = EditorApplication.timeSinceStartup + s_Steps[0].preDelay;
        EditorApplication.update -= Update;
        EditorApplication.update += Update;
        Debug.Log("T3 UI evidence run started.");
    }

    static void AddStep(string name, string capture, Action action, float preDelay = 0.3f,
        float captureDelay = 0.9f, bool expectPopup = false, Action prepare = null, int scrollTicks = 0)
    {
        s_Steps.Add(new EvidenceStep
        {
            name = name,
            capture = capture,
            action = action,
            prepare = prepare,
            scrollTicks = scrollTicks,
            preDelay = preDelay,
            captureDelay = captureDelay,
            expectPopup = expectPopup
        });
    }

    static void ClearConsole()
    {
        var logEntries = typeof(Editor).Assembly.GetType("UnityEditor.LogEntries");
        logEntries?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(null, null);
    }

    static void PrepareDemoAssets()
    {
        AssetDatabase.DeleteAsset(DemoRendererPath);
        AssetDatabase.DeleteAsset(DemoPipelinePath);
        AssetDatabase.DeleteAsset(DemoProfilePath);

        var t1Renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(T1RendererPath);
        if (t1Renderer == null)
            throw new FileNotFoundException("Missing T1 Forward renderer.", T1RendererPath);

        var renderer = UnityEngine.Object.Instantiate(t1Renderer);
        renderer.name = "SslT3UiDemoRenderer";
        renderer.rendererFeatures.Clear();
        var serializedRenderer = new SerializedObject(renderer);
        var featureMap = serializedRenderer.FindProperty("m_RendererFeatureMap");
        if (featureMap != null)
            featureMap.arraySize = 0;
        serializedRenderer.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.CreateAsset(renderer, DemoRendererPath);

        var pipeline = UniversalRenderPipelineAsset.Create(renderer);
        pipeline.name = "SslT3UiDemoPipeline";
        pipeline.supportsHDR = true;
        pipeline.msaaSampleCount = 1;
        pipeline.renderScale = 1f;
        pipeline.shadowDistance = 50f;
        pipeline.supportsCameraDepthTexture = false;
        pipeline.supportsCameraOpaqueTexture = false;
        AssetDatabase.CreateAsset(pipeline, DemoPipelinePath);

        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.name = "SslT3UiDemoProfile";
        AssetDatabase.CreateAsset(profile, DemoProfilePath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("T3 UI demo assets are empty and ready for the manual UI path.");
    }

    static void Update()
    {
        HandleFlags();
        PopupHostWindow.ParkIfDue();
        TickEvidence();
    }

    static void HandleFlags()
    {
        if (ConsumeFlag("DefaultLayout.flag"))
            EditorApplication.ExecuteMenuItem("Window/Layouts/Default");
        if (ConsumeFlag("UnlockInspector.flag"))
            UnlockInspectors();
        if (ConsumeFlag("OpenInspector.flag"))
            OpenFloatingInspector();
        if (ConsumeFlag("SelectRenderer.flag"))
            Select(DemoRendererPath);
        if (ConsumeFlag("SelectPipeline.flag"))
            Select(DemoPipelinePath);
        if (ConsumeFlag("SelectProfile.flag"))
            Select(DemoProfilePath);
        if (ConsumeFlag("SelectCamera.flag"))
        {
            var camera = UnityEngine.Object.FindObjectOfType<Camera>();
            if (camera != null)
                Selection.activeGameObject = camera.gameObject;
        }
        if (ConsumeFlag("SelectGtaoFeature.flag"))
            SelectFeature<GroundTruthAmbientOcclusionRendererFeature>();
        if (ConsumeFlag("SelectSslFeature.flag"))
            SelectFeature<ScreenSpaceLightingRendererFeature>();
        if (ConsumeFlag("OpenRendererFeaturePopup.flag"))
            OpenRendererFeaturePopup();
        if (ConsumeFlag("AddSsl.flag"))
            AddRendererFeature(nameof(ScreenSpaceLightingRendererFeature));
        if (ConsumeFlag("AddGtao.flag"))
            AddRendererFeature(nameof(GroundTruthAmbientOcclusionRendererFeature));
        if (ConsumeFlag("OpenVolumePopup.flag"))
            OpenVolumePopup();
        if (ConsumeFlag("AddSsgi.flag"))
            AddVolumeComponent(typeof(ScreenSpaceGlobalIllumination));
        if (ConsumeFlag("AddSsr.flag"))
            AddVolumeComponent(typeof(ScreenSpaceReflection));
        if (ConsumeFlag("AddRefraction.flag"))
            AddVolumeComponent(typeof(ScreenSpaceRefraction));
        if (ConsumeFlag("Exit.flag"))
        {
            EditorApplication.update -= Update;
            EditorApplication.Exit(0);
        }
    }

    static void TickEvidence()
    {
        if (s_StepIndex < 0)
            return;

        var now = EditorApplication.timeSinceStartup;
        if (now < s_NextTime)
            return;

        if (s_StepIndex >= s_Steps.Count)
        {
            FinishEvidence();
            return;
        }

        var step = s_Steps[s_StepIndex];
        switch (s_Phase)
        {
            case 0:
                s_Phase = 1;
                s_NextTime = now + step.captureDelay;
                RunStep(step);
                break;
            case 1:
                s_Phase = 2;
                s_NextTime = now + 0.5;
                RunPrepare(step);
                break;
            case 2:
                s_Phase = 3;
                s_NextTime = now + 0.6;
                ScrollInspector(step.scrollTicks);
                break;
            case 3:
                s_Phase = 0;
                CaptureStep(step);
                s_StepIndex++;
                s_NextTime = now + (s_StepIndex < s_Steps.Count ? s_Steps[s_StepIndex].preDelay : 0.25);
                break;
        }
    }

    static void RunPrepare(EvidenceStep step)
    {
        if (step.expectPopup || step.prepare == null)
            return;

        try
        {
            step.prepare();
            RepaintInspector();
            LogInspectorState($"prepare '{step.name}'");
        }
        catch (Exception exception)
        {
            s_Report.errors.Add($"{step.name}: prepare failed: {exception}");
            Debug.LogException(exception);
        }
    }

    static void RunStep(EvidenceStep step)
    {
        try
        {
            step.action?.Invoke();
            Debug.Log($"T3 UI evidence: step '{step.name}' done.");
        }
        catch (Exception exception)
        {
            s_Report.errors.Add($"{step.name}: {exception}");
            Debug.LogException(exception);
        }
    }

    static void CaptureStep(EvidenceStep step)
    {
        if (step == null || string.IsNullOrEmpty(step.capture))
            return;

        try
        {
            EnsureEditorForeground();
            LogInspectorState($"capture '{step.name}'");
            LogPopupState($"capture '{step.name}'");
            var capture = CaptureComposite(step);
            s_Report.captures.Add(capture);
            if (step.expectPopup && !capture.filterWindowOpen)
                s_Report.errors.Add($"{step.name}: the filter dropdown was not open when the screenshot was taken.");
            Debug.Log($"T3 UI evidence: captured {capture.file} ({capture.width}x{capture.height}, {capture.bytes} bytes).");
        }
        catch (Exception exception)
        {
            s_Report.errors.Add($"{step.name}: screenshot failed: {exception}");
            Debug.LogException(exception);
        }
        finally
        {
            if (step.expectPopup)
                PopupHostWindow.CloseHost();
        }
    }

    static void FinishEvidence()
    {
        s_StepIndex = -1;
        s_Phase = 0;

        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(DemoRendererPath);
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(DemoProfilePath);
        if (renderer != null)
            s_Report.rendererFeatures = renderer.rendererFeatures
                .Select(feature => feature != null ? feature.GetType().Name : "null").ToList();
        if (profile != null)
            s_Report.volumeComponents = profile.components
                .Where(component => component != null)
                .Select(component => new VolumeComponentRecord
                {
                    type = component.GetType().Name,
                    enabled = ReadComponentEnabled(component)
                }).ToList();

        var camera = UnityEngine.Object.FindObjectOfType<Camera>();
        if (camera != null)
        {
            s_Report.cameraName = camera.name;
            var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData != null)
            {
                s_Report.cameraVolumeMask = cameraData.volumeLayerMask.value;
                s_Report.cameraVolumeMaskLayers = DescribeLayerMask(cameraData.volumeLayerMask);
            }
        }

        var globalVolume = FindGlobalVolume();
        if (globalVolume != null && globalVolume.sharedProfile != null)
            s_Report.globalVolumeProfile = globalVolume.sharedProfile.name;

        if (s_Report.rendererFeatures.Count != 2 ||
            !s_Report.rendererFeatures.Contains(nameof(ScreenSpaceLightingRendererFeature)) ||
            !s_Report.rendererFeatures.Contains(nameof(GroundTruthAmbientOcclusionRendererFeature)))
            s_Report.errors.Add("The demo renderer does not contain exactly the SSL and GTAO features.");

        if (s_Report.volumeComponents.Count != 3 || s_Report.volumeComponents.Any(component => !component.enabled))
            s_Report.errors.Add("The demo profile does not contain three enabled overrides.");

        if (s_Report.captures.Count != s_Steps.Count(step => !string.IsNullOrEmpty(step.capture)))
            s_Report.errors.Add($"Captured {s_Report.captures.Count} of {s_Steps.Count(step => !string.IsNullOrEmpty(step.capture))} screenshots.");

        s_Report.status = s_Report.errors.Count == 0 ? "completed" : "completed-with-errors";
        WriteReport();
        Debug.Log($"T3 UI evidence finished: {s_Report.status}.");
        EditorApplication.update -= Update;
        EditorApplication.Exit(s_Report.errors.Count == 0 ? 0 : 2);
    }

    static void WriteReport()
    {
        Directory.CreateDirectory(UiRoot);
        s_Report.utcTimestamp = s_Report.utcTimestamp ?? DateTime.UtcNow.ToString("O");
        File.WriteAllText(EvidenceReportPath, JsonUtility.ToJson(s_Report, true));
    }

    static void RecordError(string message)
    {
        s_Report?.errors.Add(message);
        Debug.LogError("T3 UI evidence: " + message);
    }

    static bool ReadComponentEnabled(VolumeComponent component)
    {
        var field = component.GetType().GetField("enabled");
        return field != null && field.GetValue(component) is BoolParameter parameter && parameter.value;
    }

    static string DescribeLayerMask(LayerMask mask)
    {
        var names = new List<string>();
        for (var i = 0; i < 32; i++)
        {
            if ((mask.value & (1 << i)) != 0)
            {
                var name = LayerMask.LayerToName(i);
                names.Add(string.IsNullOrEmpty(name) ? i.ToString() : $"{name} ({i})");
            }
        }
        return string.Join(", ", names);
    }

    static Volume FindGlobalVolume()
    {
        foreach (var volume in UnityEngine.Object.FindObjectsOfType<Volume>())
        {
            if (volume.isGlobal)
                return volume;
        }
        return null;
    }

    static bool ConsumeFlag(string fileName)
    {
        var path = Path.Combine(UiRoot, fileName);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    static void Select(string assetPath)
    {
        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
        if (asset == null)
        {
            Debug.LogWarning($"T3 UI evidence: asset not found at {assetPath}.");
            return;
        }
        Selection.activeObject = asset;
        EditorGUIUtility.PingObject(asset);
        Debug.Log($"T3 UI evidence: selected {assetPath} -> {Selection.activeObject}.");
    }

    static void SelectForUi(string assetPath)
    {
        EnsureEditorForeground();
        UnmaximizeInspectors();
        Select(assetPath);
        UnlockInspectors();
        ActiveEditorTracker.sharedTracker.ForceRebuild();
    }

    static void SelectFeatureForUi<T>() where T : ScriptableRendererFeature
    {
        EnsureEditorForeground();
        UnmaximizeInspectors();
        SelectFeature<T>();
        UnlockInspectors();
        ActiveEditorTracker.sharedTracker.ForceRebuild();
    }

    static void SelectObjectForUi(UnityEngine.Object target)
    {
        EnsureEditorForeground();
        UnmaximizeInspectors();
        Selection.activeObject = null;
        Selection.activeObject = target;
        EditorGUIUtility.PingObject(target);
        UnlockInspectors();
        ActiveEditorTracker.sharedTracker.ForceRebuild();
    }

    static EditorWindow FindInspectorWindow()
    {
        EditorWindow fallback = null;
        foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
        {
            if (window.GetType().Name != "InspectorWindow")
                continue;
            if (window.hasFocus)
                return window;
            if (fallback == null)
                fallback = window;
        }
        return fallback;
    }

    static void UnmaximizeInspectors()
    {
        foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
        {
            if (window.GetType().Name != "InspectorWindow")
                continue;
            if (window.maximized)
                window.maximized = false;
        }
    }

    static void RepaintInspector()
    {
        var inspector = FindInspectorWindow();
        if (inspector != null)
            inspector.Repaint();
    }

    static void LogInspectorState(string phase)
    {
        var selection = Selection.activeObject;
        Debug.Log($"T3 UI evidence: {phase} selection={DescribeObject(selection)} inspectors={DescribeInspectors()}");
    }

    static void LogPopupState(string phase)
    {
        var windows = Resources.FindObjectsOfTypeAll<FilterWindow>();
        if (windows.Length == 0)
        {
            Debug.Log($"T3 UI evidence: {phase} popups=<none>");
            return;
        }

        var builder = new StringBuilder();
        foreach (var window in windows)
        {
            if (builder.Length > 0)
                builder.Append("; ");
            var position = window.position;
            builder.Append($"pos=({position.x:0},{position.y:0},{position.width:0}x{position.height:0}) " +
                           $"focused={window.hasFocus}");
        }
        Debug.Log($"T3 UI evidence: {phase} popups={builder}");
    }

    static string DescribeObject(UnityEngine.Object target)
    {
        return target == null ? "<none>" : $"{target.name} ({target.GetType().Name})";
    }

    static string DescribeInspectors()
    {
        var builder = new StringBuilder();
        foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
        {
            if (window.GetType().Name != "InspectorWindow")
                continue;
            if (builder.Length > 0)
                builder.Append("; ");
            var tracker = GetInspectorTracker(window);
            builder.Append($"id={window.GetInstanceID()} docked={window.docked} focused={window.hasFocus} " +
                           $"maximized={window.maximized} locked={DescribeTrackerLock(tracker)}");
        }
        return builder.Length == 0 ? "<none>" : builder.ToString();
    }

    static string DescribeTrackerLock(ActiveEditorTracker tracker)
    {
        if (tracker == null)
            return "<no tracker>";
        var property = typeof(ActiveEditorTracker).GetProperty(
            "isLocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return property != null ? property.GetValue(tracker)?.ToString() ?? "?" : "<no isLocked>";
    }

    static ActiveEditorTracker GetInspectorTracker(EditorWindow window)
    {
        var type = window.GetType();
        var property = type.GetProperty("tracker", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.GetValue(window) is ActiveEditorTracker tracked)
            return tracked;

        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType == typeof(ActiveEditorTracker) &&
                    field.GetValue(window) is ActiveEditorTracker tracker)
                    return tracker;
            }
        }
        return null;
    }

    static void SetTrackerLock(ActiveEditorTracker tracker, bool locked)
    {
        if (tracker == null)
            return;

        var property = typeof(ActiveEditorTracker).GetProperty(
            "isLocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanWrite)
        {
            property.SetValue(tracker, locked);
            return;
        }

        var field = typeof(ActiveEditorTracker).GetField(
            "m_IsLocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        field?.SetValue(tracker, locked);
    }

    static void ScrollInspector(int ticks)
    {
        if (ticks == 0)
            return;

        var inspector = FindInspectorWindow();
        if (inspector == null)
        {
            RecordError("No Inspector window was found while scrolling.");
            return;
        }

        inspector.Focus();
        var size = inspector.position.size;
        var point = new Vector2(Mathf.Max(40f, size.x * 0.6f), Mathf.Max(60f, size.y * 0.5f));
        var delta = new Vector2(0f, ticks > 0 ? 3f : -3f);
        for (var i = 0; i < Mathf.Abs(ticks); i++)
            inspector.SendEvent(new Event
            {
                type = EventType.ScrollWheel,
                delta = delta,
                mousePosition = point
            });
        inspector.Repaint();
        Debug.Log($"T3 UI evidence: scrolled inspector {ticks} ticks.");
    }

    static void UnlockInspectors()
    {
        SetTrackerLock(ActiveEditorTracker.sharedTracker, false);

        foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
        {
            if (window.GetType().Name != "InspectorWindow")
                continue;

            SetTrackerLock(GetInspectorTracker(window), false);
            window.Repaint();
        }
        Debug.Log("T3 UI evidence: inspector lock cleared.");
    }

    static void OpenFloatingInspector()
    {
        var inspectorType = typeof(EditorWindow).Assembly.GetType("UnityEditor.InspectorWindow");
        if (inspectorType == null)
        {
            Debug.LogWarning("T3 UI evidence: InspectorWindow type not found.");
            return;
        }

        var window = EditorWindow.GetWindow(inspectorType, utility: true,
            title: "T3 UI Evidence Inspector", focus: true);
        window.position = new Rect(1460f, 90f, 760f, 1160f);
        window.Repaint();
        Debug.Log("T3 UI evidence: floating inspector opened.");
    }

    static void SelectFeature<T>() where T : ScriptableRendererFeature
    {
        var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(DemoRendererPath);
        foreach (var feature in data.rendererFeatures)
        {
            if (feature is T)
            {
                Selection.activeObject = feature;
                EditorGUIUtility.PingObject(feature);
                return;
            }
        }
        RecordError($"Feature {typeof(T).Name} is not present on the demo renderer.");
    }

    static void OpenRendererFeaturePopup()
    {
        var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(DemoRendererPath);
        Selection.activeObject = data;

        var editor = (ScriptableRendererDataEditor)Editor.CreateEditor(data);
        EnsureRendererEditorInitialized(editor);
        var providerType = typeof(ScriptableRendererDataEditor).Assembly.GetType("UnityEditor.Rendering.Universal.ScriptableRendererFeatureProvider");
        var constructor = providerType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new[] { typeof(ScriptableRendererDataEditor) }, null);
        var provider = (FilterWindow.IProvider)constructor.Invoke(new object[] { editor });

        ShowFilterPopup(provider);
    }

    // UniversalRendererDataEditor declares its own private OnEnable, which hides the base OnEnable
    // that initializes m_RendererFeatures. The inspector only runs that initialization lazily from
    // ScriptableRendererDataEditor.OnInspectorGUI, so a programmatically created editor must do it.
    static void EnsureRendererEditorInitialized(ScriptableRendererDataEditor editor)
    {
        var field = typeof(ScriptableRendererDataEditor).GetField(
            "m_RendererFeatures", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null || field.GetValue(editor) != null)
            return;

        var onEnable = typeof(ScriptableRendererDataEditor).GetMethod(
            "OnEnable", BindingFlags.Instance | BindingFlags.NonPublic);
        onEnable?.Invoke(editor, null);
    }

    static void ShowFilterPopup(FilterWindow.IProvider provider)
    {
        // FilterWindow.Show calls Event.current.Use(); it must run inside OnGUI, exactly like a
        // button click in the inspector does.
        PopupHostWindow.ShowPopup(new Vector2(0f, 34f), provider);
    }

    static void AddRendererFeature(string typeName)
    {
        var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(DemoRendererPath);
        var editor = (ScriptableRendererDataEditor)Editor.CreateEditor(data);
        EnsureRendererEditorInitialized(editor);
        var method = typeof(ScriptableRendererDataEditor).GetMethod(
            "AddComponent", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(string) }, null);

        CloseFilterWindow();
        method.Invoke(editor, new object[] { typeName });
        AssetDatabase.SaveAssets();
        UnityEngine.Object.DestroyImmediate(editor);
    }

    static void OpenVolumePopup()
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(DemoProfilePath);
        Selection.activeObject = profile;

        var baseEditor = Editor.CreateEditor(profile);
        s_VolumeListEditor = new VolumeComponentListEditor(baseEditor);
        s_VolumeListEditor.Init(profile, new SerializedObject(profile));

        var providerType = typeof(VolumeComponentListEditor).Assembly.GetType("UnityEditor.Rendering.VolumeComponentProvider");
        var constructor = providerType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new[] { typeof(VolumeProfile), typeof(VolumeComponentListEditor) }, null);
        var provider = (FilterWindow.IProvider)constructor.Invoke(new object[] { profile, s_VolumeListEditor });

        ShowFilterPopup(provider);
    }

    static void AddVolumeComponent(Type type)
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(DemoProfilePath);
        if (s_VolumeListEditor == null)
        {
            var baseEditor = Editor.CreateEditor(profile);
            s_VolumeListEditor = new VolumeComponentListEditor(baseEditor);
            s_VolumeListEditor.Init(profile, new SerializedObject(profile));
        }

        var method = typeof(VolumeComponentListEditor).GetMethod(
            "AddComponent", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(Type) }, null);

        CloseFilterWindow();
        method.Invoke(s_VolumeListEditor, new object[] { type });
        AssetDatabase.SaveAssets();
    }

    static void EnableVolumeEffects()
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(DemoProfilePath);
        foreach (var component in profile.components)
        {
            if (component == null)
                continue;
            var field = component.GetType().GetField("enabled");
            if (field != null && field.GetValue(component) is BoolParameter parameter)
            {
                parameter.Override(true);
                parameter.value = true;
            }
            EditorUtility.SetDirty(component);
        }
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();
    }

    static void CloseFilterWindow()
    {
        foreach (var window in Resources.FindObjectsOfTypeAll<FilterWindow>())
            window.Close();
    }

    // ---- Editor window capture -------------------------------------------------------------

    static EvidenceCapture CaptureComposite(EvidenceStep step)
    {
        InternalEditorUtility.RepaintAllViews();
        var windows = CollectUnityWindows();
        OrderPopupsFirst(windows);
        if (windows.Count == 0)
            throw new InvalidOperationException("No visible Unity editor window was found.");

        var left = windows.Min(w => w.Rect.Left);
        var top = windows.Min(w => w.Rect.Top);
        var right = windows.Max(w => w.Rect.Right);
        var bottom = windows.Max(w => w.Rect.Bottom);
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);

        var file = Path.Combine(UiRoot, step.capture);
        using (var canvas = new Bitmap(width, height, GdiPixelFormat.Format32bppRgb))
        using (var graphics = GdiGraphics.FromImage(canvas))
        {
            graphics.Clear(GdiColor.FromArgb(28, 28, 28));
            // EnumWindows returns top-most first; painting in reverse keeps the z-order.
            for (var i = windows.Count - 1; i >= 0; i--)
            {
                var window = windows[i];
                using (var bitmap = CaptureWindow(window.Handle, window.Rect))
                {
                    graphics.DrawImage(bitmap,
                        window.Rect.Left - left,
                        window.Rect.Top - top,
                        bitmap.Width,
                        bitmap.Height);
                }
            }
            canvas.Save(file, GdiImageFormat.Png);
        }

        return new EvidenceCapture
        {
            step = step.name,
            file = file,
            width = width,
            height = height,
            bytes = new FileInfo(file).Length,
            filterWindowOpen = Resources.FindObjectsOfTypeAll<FilterWindow>().Length > 0
        };
    }

    static Bitmap CaptureWindow(IntPtr handle, Win32.RECT rect)
    {
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var bitmap = new Bitmap(width, height, GdiPixelFormat.Format32bppRgb);
        using (var graphics = GdiGraphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            try
            {
                // PW_RENDERFULLCONTENT captures DWM content even when the window is occluded.
                if (!Win32.PrintWindow(handle, hdc, 2))
                    Debug.LogWarning($"T3 UI evidence: PrintWindow failed for 0x{handle.ToInt64():X}.");
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }
        return bitmap;
    }

    class CapturedWindow
    {
        public IntPtr Handle;
        public Win32.RECT Rect;
        public string ClassName;
    }

    // Dropdown windows are owned by the (offscreen) helper window, so their z-order against the main
    // editor can end up below it. Composite the live UnityPopupWndClass window on top regardless of
    // EnumWindows order; the dropdown really is focused and open at capture time.
    static void OrderPopupsFirst(List<CapturedWindow> windows)
    {
        var popups = Resources.FindObjectsOfTypeAll<FilterWindow>();
        if (popups.Length == 0)
            return;

        var top = new List<CapturedWindow>();
        var rest = new List<CapturedWindow>();
        foreach (var window in windows)
        {
            (window.ClassName == "UnityPopupWndClass" ? top : rest).Add(window);
        }
        if (top.Count == 0)
        {
            LogCompositeWindows(windows);
            return;
        }

        windows.Clear();
        windows.AddRange(top);
        windows.AddRange(rest);

        LogCompositeWindows(windows);
    }

    static void LogCompositeWindows(List<CapturedWindow> windows)
    {
        var builder = new StringBuilder();
        foreach (var window in windows)
        {
            if (builder.Length > 0)
                builder.Append("; ");
            builder.Append($"{window.ClassName}@({window.Rect.Left},{window.Rect.Top}," +
                           $"{window.Rect.Right - window.Rect.Left}x{window.Rect.Bottom - window.Rect.Top})");
        }
        Debug.Log($"T3 UI evidence: composite windows={builder}");
    }

    static List<CapturedWindow> CollectUnityWindows()
    {
        var processId = (uint)Process.GetCurrentProcess().Id;
        var windows = new List<CapturedWindow>();
        Win32.EnumWindows((handle, _) =>
        {
            Win32.GetWindowThreadProcessId(handle, out var windowProcessId);
            if (windowProcessId != processId || !Win32.IsWindowVisible(handle))
                return true;

            if (!Win32.GetWindowRect(handle, out var rect))
                return true;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 40 || height < 40 || rect.Left <= -10000)
                return true;

            windows.Add(new CapturedWindow { Handle = handle, Rect = rect, ClassName = GetWindowClass(handle) });
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    static string GetWindowClass(IntPtr handle)
    {
        var className = new StringBuilder(128);
        Win32.GetClassName(handle, className, className.Capacity);
        return className.ToString();
    }

    static IntPtr FindEditorWindow()
    {
        var processId = (uint)Process.GetCurrentProcess().Id;
        var result = IntPtr.Zero;
        Win32.EnumWindows((handle, _) =>
        {
            Win32.GetWindowThreadProcessId(handle, out var windowProcessId);
            if (windowProcessId != processId || !Win32.IsWindowVisible(handle))
                return true;

            var className = new StringBuilder(128);
            Win32.GetClassName(handle, className, className.Capacity);
            if (className.ToString() != "UnityContainerWndClass")
                return true;

            if (!Win32.GetWindowRect(handle, out var rect))
                return true;
            if (rect.Right - rect.Left < 400 || rect.Bottom - rect.Top < 300 || rect.Left <= -10000)
                return true;

            result = handle;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    static void FocusEditor()
    {
        var handle = FindEditorWindow();
        if (handle == IntPtr.Zero)
            return;
        if (Win32.GetForegroundWindow() == handle)
            return;

        Win32.ShowWindow(handle, 9); // SW_RESTORE
        Win32.BringWindowToTop(handle);
        Win32.SetForegroundWindow(handle);
    }

    static void EnsureEditorForeground()
    {
        var handle = FindEditorWindow();
        if (handle == IntPtr.Zero)
            return;

        var foreground = Win32.GetForegroundWindow();
        if (foreground == handle)
            return;

        Win32.GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        if (foregroundProcessId == (uint)Process.GetCurrentProcess().Id)
            return; // another editor window (for example the dropdown) owns focus

        var foregroundThread = Win32.GetWindowThreadProcessId(foreground, out _);
        var currentThread = Win32.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                       Win32.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            Win32.ShowWindow(handle, 9);
            Win32.BringWindowToTop(handle);
            Win32.SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
                Win32.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    // FilterWindow.Show requires a real input GUI event (it calls Event.current.Use() and
    // ShowAsDropDown). Driving the dropdown from a tiny utility window reproduces the click path:
    // CreateComponentTree, ShowAsDropDown and Focus all run inside OnGUI, as from the real buttons.
    class PopupHostWindow : EditorWindow
    {
        static PopupHostWindow s_Instance;
        static Vector2 s_Position;
        static FilterWindow.IProvider s_Provider;
        static double s_ParkAt = double.MaxValue;

        public static void ParkIfDue()
        {
            if (s_ParkAt > EditorApplication.timeSinceStartup)
                return;
            s_ParkAt = double.MaxValue;

            // Keep the helper out of the screenshot without closing it (closing could steal focus
            // from the dropdown). CloseHost disposes it after the capture.
            var host = Win32.FindWindow(null, "T3 Popup Host");
            if (host != IntPtr.Zero)
                Win32.SetWindowPos(host, IntPtr.Zero, -32000, -32000, 0, 0, 0x0001 | 0x0010);
        }

        public static void ShowPopup(Vector2 position, FilterWindow.IProvider provider)
        {
            s_Position = position;
            s_Provider = provider;

            if (s_Instance == null)
            {
                s_Instance = CreateInstance<PopupHostWindow>();
                s_Instance.titleContent = new GUIContent("T3 Popup Host");
                s_Instance.ShowUtility();
            }

            var anchor = new Vector2(60f, 120f);
            var handle = FindEditorWindow();
            if (handle != IntPtr.Zero && Win32.GetWindowRect(handle, out var rect))
                anchor = new Vector2(rect.Left + 60f, rect.Top + 120f);
            s_Instance.position = new Rect(anchor.x, anchor.y, 8f, 8f);
            s_Instance.Repaint();

            // Repaint events cannot drive ShowAsDropDown; click the helper window with real input.
            var host = Win32.FindWindow(null, "T3 Popup Host");
            if (host == IntPtr.Zero || !Win32.GetWindowRect(host, out var hostRect))
            {
                Debug.LogWarning("T3 UI evidence: popup host window handle was not found.");
                return;
            }
            Win32.GetCursorPos(out var previousCursor);
            Win32.SetCursorPos((hostRect.Left + hostRect.Right) / 2, (hostRect.Top + hostRect.Bottom) / 2);
            Win32.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            Win32.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            Win32.SetCursorPos(previousCursor.X, previousCursor.Y);
        }

        public static void CloseHost()
        {
            s_ParkAt = double.MaxValue;
            if (s_Instance == null)
                return;
            var instance = s_Instance;
            s_Instance = null;
            s_Provider = null;
            if (instance)
                instance.Close();
        }

        void OnGUI()
        {
            if (s_Provider == null || Event.current.type != EventType.MouseDown)
                return;

            var provider = s_Provider;
            s_Provider = null;
            FilterWindow.Show(s_Position, provider);

            // Park the helper out of the screenshot on a later update tick; moving it in the same
            // frame can shift the freshly created dropdown while it is being positioned.
            s_ParkAt = EditorApplication.timeSinceStartup + 0.2;
        }
    }

    static class Win32
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y,
            int width, int height, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
    }
}
