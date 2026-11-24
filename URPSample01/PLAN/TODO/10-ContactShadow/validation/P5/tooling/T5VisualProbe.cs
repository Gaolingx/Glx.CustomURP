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
public sealed class T5VisualProbe : MonoBehaviour
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
        var probe = new GameObject("T5 release probe").AddComponent<T5VisualProbe>();
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
        PlayerSettings.productName = "T5VisualSmoke";
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12 });
        EditorUserBuildSettings.development = false;
        var b = BuildPipeline.BuildPlayer(new BuildPlayerOptions { target = target, scenes = new[] { scenePath },
            options = BuildOptions.None, locationPathName = Arg("-probeExe") }).summary;
        Require(b.result == BuildResult.Succeeded && b.totalErrors == 0 && b.platform == target && (b.options & BuildOptions.Development) == 0,
            $"Build: {b.result}, {b.platform}, {b.options}, errors={b.totalErrors}");
        File.WriteAllText(Arg("-probeJson"), JsonUtility.ToJson(new BuildInfo { target = b.platform.ToString(), options = b.options.ToString(),
            errors = b.totalErrors, warnings = b.totalWarnings, guid = b.guid.ToString(), qualityPipelines = Modes, scene = scenePath }));
    }
    [Serializable] sealed class BuildInfo { public string target, options, guid, scene; public string[] qualityPipelines; public int errors, warnings; }
#endif

    [Serializable] sealed class Row
    {
        public string requested, actual;
        public int darkenedByTwo, facingChangedRGBA, inactiveChangedRGBA;
        public float meanOffLuma, meanOnLuma;
        public bool passed;
    }
    [Serializable] sealed class Result
    {
        public string api, platform, gpu, unity, buildGuid, error;
        public bool development, passed;
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
        if (string.IsNullOrEmpty(result.error) && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)) result.error = msg;
    }
    void Start()
    {
        try
        {
            result.platform = Application.platform.ToString(); result.api = SystemInfo.graphicsDeviceType.ToString();
            result.development = Debug.isDebugBuild; result.gpu = SystemInfo.graphicsDeviceName;
            result.unity = Application.unityVersion; result.buildGuid = Application.buildGUID;
            Require(Application.platform == RuntimePlatform.WindowsPlayer && IntPtr.Size == 8 && !result.development && result.api == Arg("-probeApi"), "Not the requested release Win64 device");
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
                    step = 0;
                    if (++index == Modes.Length) Finish(result.paths.All(p => p.passed) ? null : "Camera color comparison failed");
                    break;
            }
        }
        catch (Exception ex) { Finish(ex.ToString()); }
    }
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
