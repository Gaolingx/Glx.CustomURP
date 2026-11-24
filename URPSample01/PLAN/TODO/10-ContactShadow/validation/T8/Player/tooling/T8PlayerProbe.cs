using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif

// Installed only in the scratch project. One player, one baseline scene, three build-visible pipelines.
public sealed class T8PlayerProbe : MonoBehaviour
{
    const int W = 256, H = 192;
    static readonly string[] Modes = { "Forward", "ForwardPlus", "Deferred" };
    static string Arg(string key)
    {
        var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException(key);
    }
    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    public ScreenSpaceContactShadowsRendererFeature[] features;

#if UNITY_EDITOR
    public static void Build()
    {
        const string root = "Assets/Tests/ContactShadow/", scenePath = root + "Scenes/ContactShadowBaseline.unity";
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        Require(QualitySettings.names.Length == Modes.Length, "Expected three sample quality levels");
        var scene = EditorSceneManager.OpenScene(scenePath);
        var probe = FindObjectOfType<T8PlayerProbe>() ?? new GameObject("T8 player probe").AddComponent<T8PlayerProbe>();
        probe.features = new ScreenSpaceContactShadowsRendererFeature[Modes.Length];
        for (int i = 0; i < Modes.Length; ++i)
        {
            string prefix = root + "Pipelines/" + Modes[i];
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(prefix + ".asset");
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(prefix + "Renderer.asset");
            Require(pipeline && data && data.renderingMode.ToString() == Modes[i], prefix);
            probe.features[i] = data.rendererFeatures.OfType<ScreenSpaceContactShadowsRendererFeature>().Single();
            QualitySettings.SetQualityLevel(i, true);
            QualitySettings.renderPipeline = pipeline;
            if (i == 0) GraphicsSettings.defaultRenderPipeline = pipeline;
        }
        // Check the same platform-specific quality set consulted by the URP shader stripper.
        QualitySettings.GetRenderPipelineAssetsForPlatform<UniversalRenderPipelineAsset>("Standalone", out var visible);
        Require(Modes.All(m => visible.Any(p => p && p.name == m)), "T7 pipeline excluded from the Windows build");
        Require(EditorSceneManager.SaveScene(scene), "Cannot save scratch scene");
        AssetDatabase.SaveAssets();
        PlayerSettings.productName = "T8ContactShadows";
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { (GraphicsDeviceType)Enum.Parse(typeof(GraphicsDeviceType), Arg("-probeApi")) });
        bool dev = Arg("-probeConfig") == "Development";
        EditorUserBuildSettings.development = dev;
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { target = target, scenes = new[] { scenePath },
            options = dev ? BuildOptions.Development : BuildOptions.None, locationPathName = Arg("-probeExe") });
        var b = report.summary;
        var messages = report.steps.SelectMany(s => s.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception || m.type == LogType.Assert).Select(m => s.name + ": " + m.content)).Distinct().ToArray();
        Require(b.result == BuildResult.Succeeded && b.platform == target && ((b.options & BuildOptions.Development) != 0) == dev,
            $"Build: {b.result}, {b.platform}, {b.options}, errors={b.totalErrors}");
        File.WriteAllText(Arg("-probeJson"), JsonUtility.ToJson(new BuildInfo { target = b.platform.ToString(), options = b.options.ToString(),
            errorMessages = messages, errors = b.totalErrors, warnings = b.totalWarnings, guid = b.guid.ToString(), qualityPipelines = Modes, scene = scenePath }));
    }
    [Serializable] sealed class BuildInfo { public string target, options, guid, scene; public string[] qualityPipelines, errorMessages; public int errors, warnings; }
#endif

    [Serializable] sealed class Row
    {
        public string requested, actual, depth, contact, debug;
        public bool kernels, debugSupported; public int debugChanged;
        public int darkenedByTwo, facingChangedRGBA, inactiveChangedRGBA;
        public float meanOffLuma, meanOnLuma;
        public bool passed;
    }
    [Serializable] sealed class Result
    {
        public string api, platform, gpu, unity, buildGuid, error;
        public bool development, passed;
        public List<string> warnings = new List<string>();
        public List<Row> paths = new List<Row>();
    }
    readonly Result result = new Result();
    Camera view;
    ContactShadowsVolume volume;
    Texture2D readback;
    Color32[] disabled, off;
    PropertyInfo actual;
    int index, step;
    bool finished;
    Row row;
    void Awake() { Application.logMessageReceived += Logged; }
    void Logged(string msg, string stack, LogType type)
    {
        if (type == LogType.Warning && !result.warnings.Contains(msg)) result.warnings.Add(msg);
        if (string.IsNullOrEmpty(result.error) && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)) result.error = msg;
    }
    void Start()
    {
        try
        {
            result.platform = Application.platform.ToString(); result.api = SystemInfo.graphicsDeviceType.ToString();
            result.development = Debug.isDebugBuild; result.gpu = SystemInfo.graphicsDeviceName;
            result.unity = Application.unityVersion; result.buildGuid = Application.buildGUID;
            Require(Application.platform == RuntimePlatform.WindowsPlayer && IntPtr.Size == 8 && result.development == (Arg("-probeConfig") == "Development") && result.api == Arg("-probeApi"), "Not the requested Win64 configuration/API");
            actual = typeof(UniversalRenderer).GetProperty("renderingModeActual", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(actual != null, "Actual renderer mode getter unavailable");
            foreach (string name in new[] { "Main", "Point", "Spot" })
            {
                var light = GameObject.Find(name).GetComponent<Light>();
                light.enabled = light.GetComponent<ContactShadowLight>().castContactShadows = name == "Main";
                light.shadows = LightShadows.None;
            }
            Require(GameObject.Find("ContactVolume").GetComponent<Volume>().profile.TryGet(out volume), "Missing T7 Volume");
            view = GameObject.Find("ContactCamera").GetComponent<Camera>();
            view.enabled = view.allowMSAA = view.allowDynamicResolution = false;
            view.aspect = (float)W / H;
            view.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.EveryFrame);
            view.GetUniversalAdditionalCameraData().antialiasing = AntialiasingMode.None;
            view.targetTexture = new RenderTexture(new RenderTextureDescriptor(W, H, GraphicsFormat.R8G8B8A8_UNorm, 24) { msaaSamples = 1 });
            Require(view.targetTexture.Create(), "Missing RGBA8 camera attachment");
            readback = new Texture2D(W, H, TextureFormat.RGBA32, false, true);
        }
        catch (Exception ex) { Finish(ex.ToString()); }
    }

    // Separate frames for pipeline recreation, camera warm-up and each Volume/feature transition.
    void Update()
    {
        if (finished) return;
        try
        {
            if (!string.IsNullOrEmpty(result.error)) throw new Exception(result.error);
            var feature = features[index];
            switch (step++)
            {
                case 0:
                    row = new Row { requested = Modes[index] }; result.paths.Add(row);
                    QualitySettings.SetQualityLevel(index, true);
                    Require(GraphicsSettings.currentRenderPipeline.name == row.requested && feature.isActive, "Wrong quality pipeline or inactive T7 feature");
                    var resources = Field(feature,"m_Resources");
                    var depth = (ComputeShader)Field(resources,"depthPyramidCompute");
                    var contact = (ComputeShader)Field(resources,"contactShadowsCompute");
                    var debug = (Shader)Field(resources,"debugShader");
                    row.depth = depth.name; row.contact = contact.name; row.debug = debug.name;
                    row.debugSupported = debug.isSupported && debug.passCount > 0;
                    row.kernels = depth.HasKernel("KDepthPyramidCopy") && depth.HasKernel("KDepthPyramidDownsample") && contact.HasKernel("KContactShadow");
                    Require(row.kernels && row.debugSupported,"Stripped feature resources");
                    volume.enabled.Override(true); break;
                case 1: Capture(); feature.SetActive(false); break;
                case 2: disabled = Capture(); feature.SetActive(true); volume.enabled.Override(false); break;
                case 3: off = Capture(); volume.enabled.Override(true); break;
                case 4:
                    var on = Capture();
                    // T7 contact ROI and light-facing control; bottom-left origin, linear RGBA8 byte codes.
                    for (int y = 123; y < 137; ++y) for (int x = 115; x < 143; ++x)
                    {
                        int i = W*y+x;
                        float a = Luma(off[i]), b = Luma(on[i]);
                        row.meanOffLuma += a / 392; row.meanOnLuma += b / 392;
                        if (a-b >= 2) row.darkenedByTwo++;
                        if (!off[i+42].Equals(on[i+42])) row.facingChangedRGBA++;
                    }
                    volume.enabled.Override(false); break;
                case 5:
                    var inactive = Capture();
                    for (int i = 0; i < inactive.Length; ++i) if (!inactive[i].Equals(disabled[i])) row.inactiveChangedRGBA++;
                    row.passed = row.darkenedByTwo >= 32 && row.meanOffLuma-row.meanOnLuma > .5f && row.facingChangedRGBA == 0 && row.inactiveChangedRGBA == 0;
                    volume.enabled.Override(true); DebugMode(feature, "Mask"); break;
                case 6:
                    var mask = Capture();
                    for (int i=0;i<mask.Length;i++) if (!mask[i].Equals(off[i])) row.debugChanged++;
                    row.passed &= row.debugChanged > 392; DebugMode(feature,"Off");
                    step = 0;
                    if (++index == Modes.Length) Finish(result.paths.All(p => p.passed) ? null : "Camera color comparison failed");
                    break;
            }
        }
        catch (Exception ex) { Finish(ex.ToString()); }
    }
    static object Field(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(obj);
    static void DebugMode(object feature, string mode) { var settings = Field(feature,"m_Settings"); var field = settings.GetType().GetField("debugMode"); field.SetValue(settings, Enum.Parse(field.FieldType,mode)); }
    static float Luma(Color32 c) => .2126f*c.r + .7152f*c.g + .0722f*c.b;
    Color32[] Capture()
    {
        var previous = RenderTexture.active;
        try
        {
            view.Render();
            row.actual = actual.GetValue(view.GetUniversalAdditionalCameraData().scriptableRenderer).ToString();
            Require(row.actual == row.requested, $"Actual {row.actual}, requested {row.requested}");
            RenderTexture.active = view.targetTexture;
            readback.ReadPixels(new Rect(0, 0, W, H), 0, 0, false);
            return readback.GetPixels32();
        }
        finally { RenderTexture.active = previous; }
    }
    void Finish(string error)
    {
        finished = true;
        Application.logMessageReceived -= Logged;
        result.error = error ?? result.error; result.passed = string.IsNullOrEmpty(result.error);
        int exit = 1;
        try { File.WriteAllText(Arg("-probeJson"), JsonUtility.ToJson(result, true)); if (result.passed) exit = 0; }
        finally { Application.Quit(exit); }
    }
}
