using System;
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

// Injected only in a disposable project containing the frozen Git fixture.
public sealed class B0Replay : MonoBehaviour
{
    const string Scene = "Assets/Tests/ContactShadow/Scenes/ContactShadowBaseline.unity";
    const int W = 1920, H = 1080, Seed = 20261125;
    public ScreenSpaceContactShadowsRendererFeature contact;
    static string Arg(string key) { var a = Environment.GetCommandLineArgs(); return a[Array.IndexOf(a, key) + 1]; }
    static void Check(bool ok, string msg) { if (!ok) throw new InvalidOperationException(msg); }
#if UNITY_EDITOR
    public static void Build()
    {
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        const string prefix = "Assets/Tests/ContactShadow/Pipelines/Forward";
        var scene = EditorSceneManager.OpenScene(Scene);
        var probe = new GameObject("B0 Replay").AddComponent<B0Replay>();
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(prefix + ".asset");
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(prefix + "Renderer.asset");
        Check(pipeline && renderer && renderer.renderingMode == RenderingMode.Forward && renderer.rendererFeatures.Count == 1, prefix);
        probe.contact = renderer.rendererFeatures.OfType<ScreenSpaceContactShadowsRendererFeature>().Single();
        GraphicsSettings.defaultRenderPipeline = pipeline;
        for (int i = 0; i < QualitySettings.names.Length; i++) { QualitySettings.SetQualityLevel(i, true); QualitySettings.renderPipeline = pipeline; }
        Check(EditorSceneManager.SaveScene(scene), "Saving scratch scene");
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.productName = "B0Replay";
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12 });
        AssetDatabase.SaveAssets();
        var b = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { Scene }, target = target,
            options = BuildOptions.Development, locationPathName = Arg("-probeExe") }).summary;
        Check(b.result == BuildResult.Succeeded && b.totalErrors == 0 && b.platform == target && (b.options & BuildOptions.Development) != 0, "Build: " + b.result);
        File.WriteAllText(Arg("-probeGuid"), b.guid.ToString());
    }
#endif
    [Serializable] sealed class Result
    {
        public bool passed, development, supportsRayTracing, hdr, post, dynamicResolution, sRGB, contactFeature, contactVolume, cameraShadows;
        public int bits, width, height, samples, seed, captureHz, warmup, render, unityFrame, firstUnityFrame, pid;
        public float renderScale, fixedDeltaTime, deltaTime, fov, near, far;
        public string api, driver, gpu, unity, os, buildGuid, scene, camera, actualRoute, pipeline, aa, colorSpace, targetFormat, readFormat, error;
        public string[] lightStates;
        public Vector3 position;
        public Quaternion rotation;
    }
    readonly Result r = new Result();
    Camera cam;
    RenderTexture rt;
    UniversalAdditionalCameraData data;
    ContactShadowsVolume vol;
    PropertyInfo route;
    int ends;
    bool done;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Clock() { Time.captureFramerate = 60; Time.fixedDeltaTime = 1f / 60; QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60; }
    void Awake()
    {
        Application.logMessageReceived += Logged;
        try
        {
            r.bits = IntPtr.Size * 8; r.development = Debug.isDebugBuild; r.unity = Application.unityVersion; r.os = SystemInfo.operatingSystem;
            r.buildGuid = Application.buildGUID; r.pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            r.api = SystemInfo.graphicsDeviceType.ToString(); r.driver = SystemInfo.graphicsDeviceVersion; r.gpu = SystemInfo.graphicsDeviceName;
            r.supportsRayTracing = SystemInfo.supportsRayTracing;
            Check(Application.platform == RuntimePlatform.WindowsPlayer && r.bits == 64 && r.development && r.api == Arg("-probeApi"), "Wrong player or device");
            r.seed = Seed; UnityEngine.Random.InitState(r.seed);
            var lights = FindObjectsOfType<Light>(true).OrderBy(l => l.name, StringComparer.Ordinal).ToArray();
            Check(string.Join(",", lights.Select(l => l.name)) == "Main,Point,Spot" && lights[0].type == LightType.Directional, "Light fixture");
            foreach (var l in lights) { l.enabled = l.name == "Main"; l.shadows = LightShadows.None; l.GetComponent<ContactShadowLight>().castContactShadows = false; }
            r.lightStates = lights.Select(l => $"{l.name}:{l.type}:enabled={l.isActiveAndEnabled}:shadows={l.shadows}:contact={l.GetComponent<ContactShadowLight>().castContactShadows}").ToArray();
            Check(lights[0].isActiveAndEnabled, "Main inactive");
            contact.SetActive(false);
            Check(GameObject.Find("ContactVolume").GetComponent<Volume>().profile.TryGet(out vol), "Missing contact volume"); vol.enabled.Override(false);
            cam = GameObject.Find("ContactCamera").GetComponent<Camera>();
            foreach (var c in FindObjectsOfType<Camera>(true)) c.enabled = false;
            r.scene = cam.gameObject.scene.path; r.camera = cam.name; r.position = cam.transform.position; r.rotation = cam.transform.rotation;
            Check(r.scene == Scene && r.position == new Vector3(0,5,0) && Quaternion.Angle(r.rotation, Quaternion.Euler(90,0,0)) < .1f, "Authored camera");
            cam.allowHDR = true; cam.allowDynamicResolution = cam.allowMSAA = false; cam.aspect = (float)W/H;
            data = cam.GetUniversalAdditionalCameraData(); data.antialiasing = AntialiasingMode.None; data.renderPostProcessing = data.renderShadows = false;
            rt = new RenderTexture(new RenderTextureDescriptor(W,H,GraphicsFormat.R16G16B16A16_SFloat,24) { msaaSamples = 1, useDynamicScale = false });
            Check(rt.Create(), "Missing HDR target"); cam.targetTexture = rt;
            route = typeof(UniversalRenderer).GetProperty("renderingModeActual", BindingFlags.Instance | BindingFlags.NonPublic);
            RenderPipelineManager.endCameraRendering += EndCamera;
        }
        catch (Exception e) { Finish(e.ToString()); }
    }
    void Logged(string msg, string stack, LogType type) { if (r.error == null && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)) r.error = msg; }
    void EndCamera(ScriptableRenderContext ctx, Camera c) { if (c == cam) ++ends; }
    void Update()
    {
        if (done) return;
        try
        {
            Check(string.IsNullOrEmpty(r.error), r.error);
            Check(cam.transform.position.Equals(r.position) && cam.transform.rotation.Equals(r.rotation), "Camera moved");
            if (ends == 0) r.firstUnityFrame = Time.frameCount;
            int before = ends; cam.Render(); Check(ends == before + 1, "Expected one finished camera render");
            if (ends < 33) return;
            r.render = ends; r.warmup = before; r.unityFrame = Time.frameCount; r.captureHz = Time.captureFramerate; r.deltaTime = Time.deltaTime; r.fixedDeltaTime = Time.fixedDeltaTime;
            Check(ends == 33 && r.unityFrame == r.firstUnityFrame + 32 && r.captureHz == 60 && Mathf.Approximately(r.deltaTime, 1f/60), "Replay clock");
            var pipe = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
            r.actualRoute = route.GetValue(data.scriptableRenderer).ToString(); r.pipeline = pipe.name; r.renderScale = pipe.renderScale;
            r.colorSpace = QualitySettings.activeColorSpace.ToString(); r.sRGB = rt.sRGB; r.targetFormat = rt.graphicsFormat.ToString(); r.width = rt.width; r.height = rt.height;
            r.samples = rt.antiAliasing; r.hdr = cam.allowHDR && pipe.supportsHDR; r.dynamicResolution = cam.allowDynamicResolution || rt.useDynamicScale;
            r.aa = data.antialiasing.ToString(); r.post = data.renderPostProcessing; r.cameraShadows = data.renderShadows; r.contactFeature = contact.isActive; r.contactVolume = vol.enabled.value;
            r.fov = cam.fieldOfView; r.near = cam.nearClipPlane; r.far = cam.farClipPlane;
            Check(r.actualRoute == "Forward" && r.pipeline == "Forward" && r.renderScale == 1 && r.hdr && !r.sRGB && r.colorSpace == "Linear" && r.targetFormat == "R16G16B16A16_SFloat" && r.samples == 1 && pipe.msaaSampleCount == 1 && !cam.allowMSAA && !r.dynamicResolution && r.aa == "None" && !r.post && !r.cameraShadows && !r.contactFeature && !r.contactVolume, "Invalid rendering configuration");
            var tex = new Texture2D(W,H,TextureFormat.RGBAHalf,false,true); r.readFormat = tex.graphicsFormat.ToString(); Check(r.readFormat == r.targetFormat, "Readback conversion");
            var prev = RenderTexture.active;
            try { RenderTexture.active = rt; tex.ReadPixels(new Rect(0,0,W,H),0,0,false); File.WriteAllBytes(Path.Combine(Arg("-probeDir"),"frame.raw"), tex.GetRawTextureData()); }
            finally { RenderTexture.active = prev; Destroy(tex); }
            Finish(r.error);
        }
        catch (Exception e) { Finish(e.ToString()); }
    }
    void Finish(string error)
    {
        done = true; Application.logMessageReceived -= Logged; RenderPipelineManager.endCameraRendering -= EndCamera;
        r.error = error; r.passed = string.IsNullOrEmpty(error);
        try { File.WriteAllText(Path.Combine(Arg("-probeDir"),"Player.json"), JsonUtility.ToJson(r,true)); }
        finally { Application.Quit(r.passed ? 0 : 1); }
    }
}
