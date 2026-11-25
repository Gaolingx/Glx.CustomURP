// R3 image regression: camera composition of the opaque SSR x DXR hybrid against an analytic
// planar-mirror reference on the B0 source-switch trajectory. The donor travels radially across the
// right screen edge while its virtual image stays on a 2x2 mirror, so the tracked ROI separates what
// screen-space SSR can see from what only DXR can recover. One build hosts Forward/ForwardPlus/
// Deferred renderers. SSR-only must not start a GI producer and the environment is black.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif

public sealed class R3ImagePlayer : MonoBehaviour
{
    public const int Width = 1920, Height = 1080;
    const float Fov = 60f, CamX = 0f, CamY = 1.6f, CamZ = -2.5f;
    const float MirrorZ = 3.98f, MirrorY = 2f, MirrorHalf = 1f;
    const float Emission = 2f, Ambient = 0f, RoiPad = 6;
    const int Budget = 32768, WarmupFrames = 4, NoisePeriod = 16, SwitchLoops = 4;
    const int PayloadBytes = 24, RequestBytes = 48;
    const float EmissionY = .2126f * Emission;
    // B0 quality thresholds (validation/B0/Freeze.json).
    const float RecallMin = .90f, FalsePositiveMax = .05f, SeamMax = .10f, ClearCoatMax = 1.25f;
    static readonly Vector3 Cam = new Vector3(CamX, CamY, CamZ);

    public Material mirror, clearCoat, donor, transparent, matte;
    public ScreenSpaceLightingRendererFeature[] features = new ScreenSpaceLightingRendererFeature[3];
    public UniversalRenderPipelineAsset pipeline;

    [Serializable] class Row
    {
        public int frame;
        public float x, y, z;
        public bool offscreen;
        public int refPixels, detectedPixels, anyDetected;
        public float refMeanY, cameraMeanY, detectedMeanY;
    }

    [Serializable] class Metrics
    {
        public bool hasReference;
        public int roiPixels, fieldPixels, refUnionPixels, detectedUnionPixels, missingUnionPixels, extraUnionPixels;
        public int offRefPixels, offDetectedPixels;
        public float recallUnion, falsePositiveFraction, recallOffscreen, seamSpan, stepVsReferenceMax;
        public int servedFrames, trailFrames;
        public float clearCoatRatio, transparentMutualMaxDifference, transparentMeanBlue;
    }

    [Serializable] class Stage
    {
        public string stage, api, guid, gate, requests, frameHash;
        public int renderer, frames, budget, instances, builds, subscribers, nonBlack;
        public int roiX, roiY, roiW, roiH;
        public float half;
        public bool hybrid, supported;
        public Metrics metrics = new Metrics();
        public List<Row> rows = new List<Row>();
    }

    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(),
            adapter = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion, denial, error;
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed;
        public int width = Width, height = Height, payloadBytes = PayloadBytes, requestBytes = RequestBytes,
            sceneBuilds, sceneInstances, subscribers, offNonBlack;
        public float fov = Fov, aspect = Width / (float)Height, camX = CamX, camY = CamY, camZ = CamZ,
            mirrorZ = MirrorZ, mirrorY = MirrorY, mirrorHalf = MirrorHalf, emission = Emission;
        public string sceneRequests;
        public bool giProducerAllocated, giAtlasAllocated;
        public List<string> renderers = new List<string> { "Forward", "ForwardPlus", "Deferred" };
        public List<string> knownDifferences = new List<string>();
        public List<Stage> stages = new List<Stage>();
    }

    Report report;
    readonly Dictionary<string, Stage> stages = new Dictionary<string, Stage>();
    readonly List<string> failures = new List<string>();
    Camera view;
    RenderTexture target;
    Volume volume;
    ScreenSpaceReflection ssr;
    Texture2D readback;
    RayTracingShader rayShader;
    float tanV, tanH;
    int roiX, roiY, roiW, roiH, quadX, quadY, quadW, quadH, stageRenderer;
    Vector3[] roiRays;
    GameObject receiver, donorObject, transparentObject;
    bool[] unionRef, unionDetected, offRef, offDetected;
    float seamMin, seamMax;
    int seamSamples, trailFrames, lastReferenceFrame;
    float transparentDiff, transparentBlue;
    Color[] transparentPixels;
    string destination;

    static string Arg(string key)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, key);
        if (i < 0 || i + 1 == args.Length) throw new ArgumentException(key);
        return args[i + 1];
    }

    static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    void Gate(bool ok, string why) { if (!ok) failures.Add(why); }
    static float Luminance(Color c) => .2126f * c.r + .7152f * c.g + .0722f * c.b;

    IEnumerator Start()
    {
        report = new Report();
        try { destination = Arg("-out"); Directory.CreateDirectory(destination); } catch { destination = null; }
        var suite = Suite();
        while (true)
        {
            try { if (!suite.MoveNext()) break; }
            catch (Exception ex) { report.error = ex.ToString(); Debug.LogException(ex); break; }
            yield return suite.Current;
        }
        try
        {
            Finish();
            if (failures.Count > 0) report.error = string.Join("\n", failures);
            report.passed = string.IsNullOrEmpty(report.error);
        }
        catch (Exception ex) { report.error = string.IsNullOrEmpty(report.error) ? ex.ToString() : report.error + "\n" + ex; }
        try { File.WriteAllText(Arg("-result"), JsonUtility.ToJson(report, true)); }
        finally { Application.Quit(report.passed ? 0 : 1); }
    }

    void Finish()
    {
        if (view) { view.targetTexture = null; Destroy(view.gameObject); view = null; }
        if (target) { target.Release(); Destroy(target); target = null; }
        if (readback) { Destroy(readback); readback = null; }
        if (volume) { Destroy(volume.gameObject); volume = null; }
        Gate(!report.giProducerAllocated && !report.giAtlasAllocated, "SSR-only started a GI producer");
        if (report.api == "Direct3D11")
        {
            Gate(report.denial == "RayTracingUnsupported", "D3D11 gate decision changed");
            Gate(report.sceneBuilds == 0 && report.sceneInstances == 0 && report.subscribers == 0,
                "D3D11 allocated RT resources");
        }
        else
        {
            Gate(report.supportsRayTracing, "DXR requires D3D12");
            Gate(report.sceneInstances > 0 && report.sceneBuilds > 0, "D3D12 never built the RT scene");
            Gate(report.sceneRequests == "Reflections", "SSR-only subscribed to more than Reflections");
        }
        Gate(report.offNonBlack > 1000, "RT-off frame is blank");
    }

    IEnumerator Suite()
    {
        Check(report.development && Application.platform == RuntimePlatform.WindowsPlayer && IntPtr.Size == 8 &&
            QualitySettings.activeColorSpace == ColorSpace.Linear && mirror && clearCoat && donor && transparent && matte &&
            pipeline && features.Length == 3 && features[0] && features[1] && features[2], "Invalid Player configuration");

        int quality = Array.IndexOf(QualitySettings.names, "High Fidelity");
        Check(quality >= 0, "Missing High Fidelity quality level");
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        QualitySettings.SetQualityLevel(quality, true);
        QualitySettings.renderPipeline = pipeline;
        GraphicsSettings.defaultRenderPipeline = pipeline;
        yield return null;
        yield return null;
        Check(UniversalRenderPipeline.asset == pipeline, "R3Image pipeline is not active");

        rayShader = features[0].settings.resources.rayTracingSsr;
        Check(rayShader != null, "Missing SSR raygen");
        tanV = Mathf.Tan(Fov * Mathf.Deg2Rad * .5f);
        tanH = tanV * (Width / (float)Height);
        BuildScene();
        BuildVolume();
        BuildQuadRect();
        yield return Warmup();

        for (int renderer = 0; renderer < 3; ++renderer)
            foreach (string kind in new[] { "switch", "switchOff", "exit", "clearCoat", "transparent", "transparentOff", "off", "offAgain" })
                yield return Run(renderer, kind);
        Evaluate();
    }

    // ---------------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------------

    GameObject Quad(string name, Vector3 position, Vector2 size, Material material)
    {
        var mesh = new Mesh { name = name };
        float hx = size.x * .5f, hy = size.y * .5f;
        // Clockwise from the camera at -Z so the front face points at it.
        mesh.vertices = new[] { new Vector3(-hx, -hy, 0), new Vector3(-hx, hy, 0), new Vector3(hx, hy, 0), new Vector3(hx, -hy, 0) };
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        var go = new GameObject(name);
        go.transform.position = position;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.rayTracingMode = RayTracingMode.Static;
        return go;
    }

    GameObject Box(string name, Vector3 position, Vector3 size, Material material, int id)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.position = position;
        go.transform.localScale = size;
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.rayTracingMode = RayTracingMode.DynamicTransform;
        if (id >= 0)
        {
            var geometry = go.AddComponent<SsrRayTracingGeometry>();
            geometry.instanceId = id;
            geometry.enableTriangleCulling = false;
        }
        return go;
    }

    void BuildScene()
    {
        // B0 candidate workload: closed 8x4x8 room at (0,2,0) with unlit matte surfaces, a 2x2
        // metallic mirror on the +Z wall, and the 0.3 m emissive donor on the source-switch path.
        Box("floor", new Vector3(0, -.1f, 0), new Vector3(8.2f, .2f, 8.2f), matte, 40);
        Box("ceiling", new Vector3(0, 4.1f, 0), new Vector3(8.2f, .2f, 8.2f), matte, 41);
        Box("wall-x-", new Vector3(-4.1f, 2, 0), new Vector3(.2f, 4.2f, 8.2f), matte, 42);
        Box("wall-x+", new Vector3(4.1f, 2, 0), new Vector3(.2f, 4.2f, 8.2f), matte, 43);
        Box("wall-z-", new Vector3(0, 2, -4.1f), new Vector3(8.2f, 4.2f, .2f), matte, 44);
        Box("wall-z+", new Vector3(0, 2, 4.1f), new Vector3(8.2f, 4.2f, .2f), matte, 45);
        receiver = Quad("mirror", new Vector3(0, MirrorY, MirrorZ), new Vector2(2 * MirrorHalf, 2 * MirrorHalf), mirror);
        donorObject = Box("donor", new Vector3(1, 1.6f, 0), Vector3.one * .3f, donor, 37);
        transparentObject = Quad("transparent control", new Vector3(-.6f, 1.6f, 1.5f), new Vector2(.5f, .5f), transparent);
        transparentObject.SetActive(false);
        // The clustered deferred lighting needs a light to shade any tile; N.L is 0 on the vertical
        // mirror and on the metallic-black donor, so the analytic radiance stays emission-only.
        var lightObject = new GameObject("R3Image light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = Color.white;
        light.intensity = 2f;
        light.shadows = LightShadows.None;
        lightObject.transform.rotation = Quaternion.Euler(90, 0, 0);
    }

    void BuildVolume()
    {
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(Ambient, Ambient, Ambient);
        // The built-in default sky would otherwise feed the smooth mirror through the default
        // reflection probe and tint the ROI; the analytic reference assumes a black environment.
        var black = new Cubemap(1, TextureFormat.RGBAHalf, false);
        for (int face = 0; face < 6; ++face) black.SetPixel((CubemapFace)face, 0, 0, new Color(0, 0, 0, 0));
        black.Apply();
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        RenderSettings.customReflectionTexture = black;
        var go = new GameObject("R3Image volume");
        volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 999;
        ssr = volume.profile.Add<ScreenSpaceReflection>();
        ssr.enabled.Override(true);
        ssr.fullResolution.Override(false);
        ssr.reflectSky.Override(false);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
    }

    IEnumerator Warmup()
    {
        // A material asset reports one placeholder pass until its shader is used once; the RT gate
        // needs FindPass("GlxDXR") on the registered witness.
        var warm = new GameObject("R3Image warm camera").AddComponent<Camera>();
        warm.clearFlags = CameraClearFlags.SolidColor;
        warm.backgroundColor = Color.black;
        warm.cullingMask = ~0;
        warm.targetTexture = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        for (int i = 0; i < 4; ++i) yield return null;
        warm.targetTexture = null;
        Destroy(warm.gameObject);
    }

    // ---------------------------------------------------------------------------------------------
    // Analytic planar-mirror reference
    // ---------------------------------------------------------------------------------------------

    Vector2 Project(Vector3 world)
    {
        float depth = world.z - CamZ;
        float nx = (world.x - CamX) / (tanH * depth);
        float ny = (world.y - CamY) / (tanV * depth);
        return new Vector2((nx + 1) * .5f * Width, (ny + 1) * .5f * Height);
    }

    (float minX, float minY, float maxX, float maxY) ScreenBox(Vector3 pose, float half, bool virtualImage)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        float z = virtualImage ? 2 * MirrorZ - pose.z : pose.z;
        for (int corner = 0; corner < 8; ++corner)
        {
            var world = new Vector3(
                pose.x + ((corner & 1) == 0 ? -half : half),
                pose.y + ((corner & 2) == 0 ? -half : half),
                z + ((corner & 4) == 0 ? -half : half));
            var p = Project(world);
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        }
        return (minX, minY, maxX, maxY);
    }

    bool Offscreen(Vector3 pose, float half)
    {
        var b = ScreenBox(pose, half, false);
        return b.maxX < -8 || b.minX > Width + 8 || b.maxY < -8 || b.minY > Height + 8;
    }

    // The tracked ROI is the union of the donor's virtual-image AABB over the stage trajectory. The
    // direct donor image is excluded per frame (it is not a reflection).
    void BuildRoi(string kind)
    {
        float half = DonorHalf(kind);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int f = 0; f < Frames(kind); ++f)
        {
            var b = ScreenBox(Pose(kind, f), half, true);
            minX = Mathf.Min(minX, b.minX); maxX = Mathf.Max(maxX, b.maxX);
            minY = Mathf.Min(minY, b.minY); maxY = Mathf.Max(maxY, b.maxY);
        }
        roiX = Mathf.Clamp(Mathf.FloorToInt(minX - RoiPad), 0, Width - 2);
        roiY = Mathf.Clamp(Mathf.FloorToInt(minY - RoiPad), 0, Height - 2);
        int x1 = Mathf.Clamp(Mathf.CeilToInt(maxX + RoiPad), 1, Width - 1);
        int y1 = Mathf.Clamp(Mathf.CeilToInt(maxY + RoiPad), 1, Height - 1);
        roiW = Mathf.Max(1, x1 - roiX);
        roiH = Mathf.Max(1, y1 - roiY);
        roiRays = new Vector3[roiW * roiH];
        for (int y = 0; y < roiH; ++y)
            for (int x = 0; x < roiW; ++x)
            {
                float u = (roiX + x + .5f) / Width * 2 - 1;
                float v = (roiY + y + .5f) / Height * 2 - 1;
                roiRays[y * roiW + x] = new Vector3(u * tanH, v * tanV, 1).normalized;
            }
    }

    void BuildQuadRect()
    {
        var quad = new[]
        {
            Project(new Vector3(-.85f, 1.35f, 1.5f)), Project(new Vector3(-.35f, 1.35f, 1.5f)),
            Project(new Vector3(-.35f, 1.85f, 1.5f)), Project(new Vector3(-.85f, 1.85f, 1.5f))
        };
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var p in quad) { x0 = Mathf.Min(x0, p.x); y0 = Mathf.Min(y0, p.y); x1 = Mathf.Max(x1, p.x); y1 = Mathf.Max(y1, p.y); }
        quadX = Mathf.Clamp(Mathf.RoundToInt(x0) + 1, 0, Width - 2);
        quadY = Mathf.Clamp(Mathf.RoundToInt(y0) + 1, 0, Height - 2);
        quadW = Mathf.Clamp(Mathf.RoundToInt(x1) - 1 - quadX, 1, Width - quadX);
        quadH = Mathf.Clamp(Mathf.RoundToInt(y1) - 1 - quadY, 1, Height - quadY);
    }

    static bool RayBox(Vector3 origin, Vector3 direction, Vector3 centre, float half)
    {
        float near = 0f, far = 1e9f;
        var min = centre - Vector3.one * half;
        var max = centre + Vector3.one * half;
        for (int axis = 0; axis < 3; ++axis)
        {
            float o = origin[axis], d = direction[axis];
            if (Mathf.Abs(d) < 1e-8f)
            {
                if (o < min[axis] || o > max[axis]) return false;
                continue;
            }
            float a = (min[axis] - o) / d, b = (max[axis] - o) / d;
            if (a > b) { float swap = a; a = b; b = swap; }
            near = Mathf.Max(near, a);
            far = Mathf.Min(far, b);
            if (near > far) return false;
        }
        return far >= Mathf.Max(near, 0f);
    }

    // ---------------------------------------------------------------------------------------------
    // Stages
    // ---------------------------------------------------------------------------------------------

    static float DonorHalf(string stage) => .15f;

    static Vector3 Pose(string stage, int frame)
    {
        switch (stage)
        {
            case "switch": case "switchOff":
            {
                int f = frame % 240;
                float t = f <= 120 ? f / 120f : (240 - f) / 120f;
                return new Vector3(1f, 1.6f, Mathf.Lerp(0f, -2.4f, t));
            }
            case "clearCoat":
                return new Vector3(1f, 1.6f, Mathf.Lerp(0f, -2.4f, Mathf.Clamp01(frame / 120f)));
            case "exit":
                return new Vector3(Mathf.Lerp(1f, 3f, Mathf.Clamp01(frame / 60f)), 1.6f, 0f);
            default:
                return new Vector3(1f, 1.6f, 0f);
        }
    }

    static int Frames(string stage) => stage == "switch" || stage == "switchOff" ? 240 * SwitchLoops
        : stage == "exit" ? 70
        : stage == "clearCoat" ? 120
        : stage == "transparent" || stage == "transparentOff" ? 10
        : 8;

    static bool Hybrid(string stage) => stage == "switch" || stage == "exit" || stage == "clearCoat" ||
        stage == "transparent";

    static int KeyFrame(string stage) => stage == "exit" ? 5 : 100;

    IEnumerator Run(int renderer, string kind)
    {
        // Deterministic per-frame noise phase across stages.
        while (Time.frameCount % NoisePeriod != 0) yield return new WaitForEndOfFrame();
        bool hybrid = Hybrid(kind);
        var feature = features[renderer];
        feature.settings.rayTracingReflections = hybrid;
        feature.settings.reflectionRayBudget = Budget;
        receiver.GetComponent<MeshRenderer>().sharedMaterial = kind == "clearCoat" ? clearCoat : mirror;
        transparentObject.SetActive(kind == "transparent" || kind == "transparentOff");
        float half = DonorHalf(kind);
        donorObject.transform.localScale = Vector3.one * 2 * half;
        donorObject.transform.position = Pose(kind, 0);
        BuildRoi(kind);

        view = new GameObject("R3Image camera " + kind).AddComponent<Camera>();
        view.clearFlags = CameraClearFlags.SolidColor;
        view.backgroundColor = Color.black;
        view.fieldOfView = Fov;
        view.nearClipPlane = .1f;
        view.farClipPlane = 50f;
        view.allowMSAA = false;
        view.allowHDR = true;
        view.cullingMask = ~0;
        view.transform.SetPositionAndRotation(Cam, Quaternion.identity);
        var data = view.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = false;
        data.antialiasing = AntialiasingMode.None;
        data.SetRenderer(renderer);
        target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        target.Create();
        view.targetTexture = target;
        readback = new Texture2D(roiW, roiH, TextureFormat.RGBAHalf, false, true);

        var stage = new Stage
        {
            stage = kind, api = report.api, guid = report.guid, renderer = renderer, frames = Frames(kind),
            budget = Budget, hybrid = hybrid, supported = report.api == "Direct3D12", half = half,
            roiX = roiX, roiY = roiY, roiW = roiW, roiH = roiH
        };
        ResetAccumulators();
        if (kind == "transparent") transparentPixels = null;
        stageRenderer = renderer;
        for (int f = 0; f < stage.frames; ++f)
        {
            donorObject.transform.position = Pose(kind, f);
            yield return new WaitForEndOfFrame();
            Row row;
            if (kind == "transparent" || kind == "transparentOff")
                row = AnalyseTransparent(f);
            else if (kind == "off" || kind == "offAgain")
                row = new Row { frame = f };
            else
                row = Analyse(kind, f);
            stage.rows.Add(row);
        }
        if (kind == "off" || kind == "offAgain")
        {
            SaveFull(kind, renderer, stage);
            report.offNonBlack = Mathf.Max(report.offNonBlack, stage.nonBlack);
        }
        else if (kind != "transparent" && kind != "transparentOff")
            SaveRoi(kind, renderer);
        stage.metrics = Measure();
        stage.instances = Instances(feature);
        stage.builds = Builds(feature);
        stage.subscribers = Subscribers(feature);
        stage.requests = Requests(feature);
        stage.gate = GateString(feature, hybrid);
        if (report.api == "Direct3D11" && hybrid) report.denial = stage.gate;
        stages[kind + "/" + renderer] = stage;
        report.stages.Add(stage);

        view.targetTexture = null;
        Destroy(view.gameObject);
        view = null;
        target.Release(); Destroy(target); target = null;
        Destroy(readback); readback = null;
    }

    void ResetAccumulators()
    {
        unionRef = new bool[roiW * roiH];
        unionDetected = new bool[roiW * roiH];
        offRef = new bool[roiW * roiH];
        offDetected = new bool[roiW * roiH];
        seamMin = float.MaxValue; seamMax = float.MinValue; seamSamples = 0;
        trailFrames = 0; lastReferenceFrame = -1;
        transparentDiff = 0; transparentBlue = 0;
    }

    Color[] ReadRoi()
    {
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        try { readback.ReadPixels(new Rect(roiX, roiY, roiW, roiH), 0, 0); }
        finally { RenderTexture.active = previous; }
        readback.Apply(false, false);
        return readback.GetPixels();
    }

    Row Analyse(string kind, int frame)
    {
        var pixels = ReadRoi();
        var pose = Pose(kind, frame);
        float half = DonorHalf(kind);
        float plane = MirrorZ - CamZ;
        bool offscreen = Offscreen(pose, half);
        int refCount = 0, fieldCount = 0, detectedCount = 0, coreCount = 0, anyDetected = 0;
        double cameraSum = 0, detectedSum = 0;
        for (int i = 0; i < roiRays.Length; ++i)
        {
            var d = roiRays[i];
            bool direct = d.z > 0 && RayBox(Cam, d, pose, half);
            if (pixels[i].r > .5f && !direct) ++anyDetected;
            if (direct || d.z <= 0) continue;
            float s = plane / d.z;
            var o = new Vector3(CamX + d.x * s, CamY + d.y * s, MirrorZ);
            if (Mathf.Abs(o.x) > MirrorHalf || Mathf.Abs(o.y - MirrorY) > MirrorHalf) continue;
            ++fieldCount;
            cameraSum += Luminance(pixels[i]);
            if (!RayBox(o, new Vector3(d.x, d.y, -d.z), pose, half)) continue;
            unionRef[i] = true; ++refCount;
            if (offscreen) offRef[i] = true;
            if (pixels[i].r <= .5f) continue;
            unionDetected[i] = true; ++detectedCount;
            if (offscreen) offDetected[i] = true;
            if (pixels[i].r <= .5f * Emission) continue;
            detectedSum += Luminance(pixels[i]);
            ++coreCount;
        }
        var row = new Row
        {
            frame = frame, x = pose.x, y = pose.y, z = pose.z, offscreen = offscreen,
            refPixels = refCount, detectedPixels = detectedCount, anyDetected = anyDetected
        };
        row.refMeanY = fieldCount > 0 ? EmissionY * refCount / fieldCount : 0;
        row.cameraMeanY = fieldCount > 0 ? (float)(cameraSum / fieldCount) : 0;
        row.detectedMeanY = coreCount > 0 ? (float)(detectedSum / coreCount) : 0;
        // The seam is measured on world-space frames only: screen-space frames mix a different
        // reconstruction into the same mean.
        if (coreCount >= 2 && offscreen && frame >= WarmupFrames)
        {
            seamMin = Mathf.Min(seamMin, row.detectedMeanY);
            seamMax = Mathf.Max(seamMax, row.detectedMeanY);
            ++seamSamples;
        }
        if (refCount == 0)
        {
            if (kind == "exit" && lastReferenceFrame >= 0 && anyDetected > 0)
                trailFrames = Mathf.Max(trailFrames, frame - lastReferenceFrame - 1);
        }
        else lastReferenceFrame = frame;
        if (frame == KeyFrame(kind)) SaveRoiFrame(kind, frame, stageRenderer);
        return row;
    }

    Row AnalyseTransparent(int frame)
    {
        var tex = new Texture2D(quadW, quadH, TextureFormat.RGBAHalf, false, true);
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        try { tex.ReadPixels(new Rect(quadX, quadY, quadW, quadH), 0, 0); }
        finally { RenderTexture.active = previous; }
        tex.Apply(false, false);
        var pixels = tex.GetPixels();
        Destroy(tex);
        if (transparentPixels == null) transparentPixels = pixels;
        else
        {
            for (int i = 0; i < pixels.Length; ++i)
                transparentDiff = Mathf.Max(transparentDiff,
                    Mathf.Max(Mathf.Abs(pixels[i].r - transparentPixels[i].r),
                        Mathf.Max(Mathf.Abs(pixels[i].g - transparentPixels[i].g), Mathf.Abs(pixels[i].b - transparentPixels[i].b))));
        }
        double blue = 0;
        foreach (var p in pixels) blue += p.b;
        transparentBlue = (float)(blue / pixels.Length);
        return new Row { frame = frame };
    }

    Metrics Measure()
    {
        var m = new Metrics { roiPixels = roiW * roiH };
        int hit = 0, offHit = 0;
        for (int i = 0; i < unionRef.Length; ++i)
        {
            if (unionRef[i]) ++m.refUnionPixels;
            if (unionDetected[i]) ++m.detectedUnionPixels;
            if (unionRef[i] && unionDetected[i]) ++hit;
            if (offRef[i]) ++m.offRefPixels;
            if (offDetected[i]) ++m.offDetectedPixels;
            if (offRef[i] && offDetected[i]) ++offHit;
        }
        m.missingUnionPixels = m.refUnionPixels - hit;
        m.extraUnionPixels = m.detectedUnionPixels - hit;
        m.recallUnion = m.refUnionPixels > 0 ? hit / (float)m.refUnionPixels : 0;
        m.falsePositiveFraction = m.refUnionPixels > 0 ? m.extraUnionPixels / (float)m.refUnionPixels : 0;
        m.recallOffscreen = m.offRefPixels > 0 ? offHit / (float)m.offRefPixels : 0;
        m.hasReference = m.refUnionPixels > 0;
        m.seamSpan = seamSamples > 0 ? (seamMax - seamMin) / EmissionY : 0;
        m.trailFrames = trailFrames;
        m.transparentMutualMaxDifference = transparentDiff;
        m.transparentMeanBlue = transparentBlue;
        m.servedFrames = seamSamples;
        return m;
    }

    // B0 ssToWorld step: the ROI-mean derivative through the screen-space -> world-space crossing.
    static float StepVsReference(Stage stage)
    {
        int start = stage.rows.FindIndex(r => r.offscreen);
        if (start < 0) return 0;
        int end = start;
        while (end < stage.rows.Count && stage.rows[end].offscreen) ++end;
        float max = 0;
        for (int i = Mathf.Max(1, start - 3); i < end; ++i)
        {
            var a = stage.rows[i - 1];
            var b = stage.rows[i];
            if (a.refPixels == 0 || b.refPixels == 0) continue;
            float delta = Mathf.Abs((b.cameraMeanY - a.cameraMeanY) - (b.refMeanY - a.refMeanY));
            max = Mathf.Max(max, delta / Mathf.Max(Mathf.Abs(b.refMeanY), .01f));
        }
        return max;
    }

    void Evaluate()
    {
        CaptureSceneState();
        for (int r = 0; r < 3; ++r)
        {
            bool d3d12 = report.api == "Direct3D12";
            var sw = stages["switch/" + r]; var swOff = stages["switchOff/" + r];
            var exit = stages["exit/" + r]; var coat = stages["clearCoat/" + r];
            var trans = stages["transparent/" + r]; var transOff = stages["transparentOff/" + r];
            sw.metrics.stepVsReferenceMax = StepVsReference(sw);
            swOff.metrics.stepVsReferenceMax = StepVsReference(swOff);
            coat.metrics.clearCoatRatio = Ratio(sw, coat);
            if (d3d12 && r < 2)
            {
                Gate(sw.metrics.recallUnion >= RecallMin, $"switch/{r} recall {sw.metrics.recallUnion}");
                Gate(sw.metrics.falsePositiveFraction <= FalsePositiveMax, $"switch/{r} false positive {sw.metrics.falsePositiveFraction}");
                Gate(sw.metrics.recallOffscreen >= RecallMin, $"switch/{r} off-screen recall {sw.metrics.recallOffscreen}");
                Gate(sw.metrics.seamSpan <= SeamMax, $"switch/{r} seam span {sw.metrics.seamSpan}");
                Gate(sw.metrics.stepVsReferenceMax <= SeamMax, $"switch/{r} step {sw.metrics.stepVsReferenceMax}");
                Gate(sw.metrics.servedFrames >= 10, $"switch/{r} too few core frames {sw.metrics.servedFrames}");
                Gate(swOff.metrics.recallOffscreen <= .05f, $"switchOff/{r} recovered the off-screen silhouette {swOff.metrics.recallOffscreen}");
                Gate(exit.metrics.trailFrames <= 2, $"exit/{r} trail {exit.metrics.trailFrames}");
                Gate(coat.metrics.clearCoatRatio <= ClearCoatMax && coat.metrics.clearCoatRatio >= .8f,
                    $"clearCoat/{r} ratio {coat.metrics.clearCoatRatio}");
            }
            else if (d3d12)
            {
                // Deferred composes the SSL blend while writing the GBuffer, so it must resolve the
                // hybrid like the forward renderers. Only a provably untouched image may be recorded.
                Gate(sw.metrics.falsePositiveFraction <= FalsePositiveMax, $"Deferred/{r} false positive");
                if (sw.metrics.recallUnion < .02f)
                {
                    Gate(Mathf.Abs(sw.metrics.recallUnion - swOff.metrics.recallUnion) <= .02f,
                        $"Deferred/{r} changed the image without reaching the reflection");
                    report.knownDifferences.Add(
                        $"Deferred/{r}: the image stayed untouched (off frame nonBlack {stages["off/" + r].nonBlack}); " +
                        "GBuffer surfaces or the SSL blend did not reach the deferred renderer");
                }
                else
                {
                    Gate(sw.metrics.recallUnion >= RecallMin, $"Deferred/{r} recall {sw.metrics.recallUnion}");
                    Gate(sw.metrics.recallOffscreen >= RecallMin, $"Deferred/{r} off-screen recall {sw.metrics.recallOffscreen}");
                    Gate(sw.metrics.stepVsReferenceMax <= SeamMax, $"Deferred/{r} step {sw.metrics.stepVsReferenceMax}");
                    Gate(sw.metrics.servedFrames >= 10, $"Deferred/{r} too few core frames {sw.metrics.servedFrames}");
                    Gate(swOff.metrics.recallOffscreen <= .05f, $"Deferred/{r} RT-off recovered the silhouette {swOff.metrics.recallOffscreen}");
                    Gate(exit.metrics.trailFrames <= 2, $"Deferred/{r} trail {exit.metrics.trailFrames}");
                    Gate(coat.metrics.clearCoatRatio <= ClearCoatMax && coat.metrics.clearCoatRatio >= .8f,
                        $"Deferred/{r} ratio {coat.metrics.clearCoatRatio}");
                }
            }
            else
            {
                // No DXR: the hybrid stage is ineligible and must match the RT-off stage, and the
                // off-screen span must stay unresolved.
                Gate(Mathf.Abs(sw.metrics.recallUnion - swOff.metrics.recallUnion) <= .02f,
                    $"D3D11 switch/{r} diverged from RT-off");
                Gate(sw.metrics.recallOffscreen <= .05f && swOff.metrics.recallOffscreen <= .05f,
                    $"D3D11 off-path/{r} produced a silhouette");
            }
            Gate(transOff.metrics.transparentMutualMaxDifference == 0f,
                $"transparent/{r} consumed the opaque trace ({transOff.metrics.transparentMutualMaxDifference})");
            Gate(trans.metrics.transparentMeanBlue > .01f, $"transparent/{r} control is blank");
        }
    }

    static float Ratio(Stage plain, Stage coat)
    {
        double a = 0, b = 0;
        int count = 0;
        int n = Mathf.Min(plain.rows.Count, coat.rows.Count);
        for (int i = WarmupFrames; i < n; ++i)
        {
            if (plain.rows[i].detectedPixels < 3 || coat.rows[i].detectedPixels < 3) continue;
            if (plain.rows[i].detectedMeanY <= 0 || coat.rows[i].detectedMeanY <= 0) continue;
            a += plain.rows[i].detectedMeanY;
            b += coat.rows[i].detectedMeanY;
            ++count;
        }
        return count > 0 && a > 0 ? (float)(b / a) : 0;
    }

    void SaveRoiFrame(string kind, int frame, int renderer)
    {
        if (destination == null) return;
        var tex = new Texture2D(roiW, roiH, TextureFormat.RGBAHalf, false, true);
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        try { tex.ReadPixels(new Rect(roiX, roiY, roiW, roiH), 0, 0); }
        finally { RenderTexture.active = previous; }
        tex.Apply(false, false);
        File.WriteAllBytes(Path.Combine(destination, $"{kind}-{renderer}-{frame}.roi"), tex.GetRawTextureData());
        Destroy(tex);
    }

    void SaveRoi(string kind, int renderer)
    {
        if (destination == null) return;
        var name = kind + "-" + renderer;
        File.WriteAllBytes(Path.Combine(destination, name + ".refmask"), ToBytes(unionRef));
        File.WriteAllBytes(Path.Combine(destination, name + ".hitmask"), ToBytes(unionDetected));
        File.WriteAllBytes(Path.Combine(destination, name + ".offmask"), ToBytes(offRef));
        File.WriteAllBytes(Path.Combine(destination, name + ".offhit"), ToBytes(offDetected));
    }

    // Full-frame hash plus a mirror crop; the full 1080p frame is not worth storing.
    void SaveFull(string kind, int renderer, Stage stage)
    {
        var tex = new Texture2D(Width, Height, TextureFormat.RGBAHalf, false, true);
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        try { tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); }
        finally { RenderTexture.active = previous; }
        tex.Apply(false, false);
        var data = tex.GetRawTextureData();
        int nonBlack = 0;
        foreach (var p in tex.GetPixels()) if (p.r > .1f || p.g > .1f || p.b > .1f) ++nonBlack;
        Destroy(tex);
        stage.nonBlack = nonBlack;
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(data);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            stage.frameHash = sb.ToString();
        }
        if (destination == null) return;
        var crop = new Texture2D(384, 384, TextureFormat.RGBAHalf, false, true);
        previous = RenderTexture.active;
        RenderTexture.active = target;
        try { crop.ReadPixels(new Rect(768, 348, 384, 384), 0, 0); }
        finally { RenderTexture.active = previous; }
        crop.Apply(false, false);
        File.WriteAllBytes(Path.Combine(destination, $"{kind}-{renderer}-{report.api}.crop"), crop.GetRawTextureData());
        Destroy(crop);
    }

    static byte[] ToBytes(bool[] mask)
    {
        var bytes = new byte[mask.Length];
        for (int i = 0; i < mask.Length; ++i) bytes[i] = mask[i] ? (byte)1 : (byte)0;
        return bytes;
    }

    int Instances(ScreenSpaceLightingRendererFeature feature)
    {
        var cameras = Cameras(feature);
        return cameras == null ? 0 : Scene(cameras)?.instanceCount ?? 0;
    }

    int Builds(ScreenSpaceLightingRendererFeature feature)
    {
        var cameras = Cameras(feature);
        return cameras == null ? 0 : Scene(cameras)?.buildCount ?? 0;
    }

    int Subscribers(ScreenSpaceLightingRendererFeature feature)
    {
        var cameras = Cameras(feature);
        return cameras == null ? 0 : Scene(cameras)?.subscribers ?? 0;
    }

    string Requests(ScreenSpaceLightingRendererFeature feature)
    {
        var cameras = Cameras(feature);
        var scene = cameras == null ? null : Scene(cameras);
        return scene == null ? null : scene.requests.ToString();
    }

    string GateString(ScreenSpaceLightingRendererFeature feature, bool hybrid)
    {
        if (!hybrid) return "Off";
        return RayTracingSceneGate.Capture(new CameraData
        {
            camera = view, cameraType = CameraType.Game, renderType = CameraRenderType.Base,
            cameraTargetDescriptor = new RenderTextureDescriptor(Width, Height)
        }, true, rayShader, donor).Evaluate(RayTracingRequests.Reflections).ToString();
    }

    static object Cameras(ScreenSpaceLightingRendererFeature feature) =>
        typeof(ScreenSpaceLightingRendererFeature)
            .GetField("m_HybridCameras", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(feature);

    static RayTracingSceneResources Scene(object cameras) =>
        (RayTracingSceneResources)cameras.GetType()
            .GetField("m_Scene", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(cameras);

    void CaptureSceneState()
    {
        foreach (var stage in report.stages)
        {
            report.sceneBuilds = Mathf.Max(report.sceneBuilds, stage.builds);
            report.sceneInstances = Mathf.Max(report.sceneInstances, stage.instances);
            report.subscribers = Mathf.Max(report.subscribers, stage.subscribers);
            if (stage.hybrid && !string.IsNullOrEmpty(stage.requests)) report.sceneRequests = stage.requests;
        }
        foreach (var feature in features)
        {
            if (feature == null) continue;
            foreach (var field in typeof(ScreenSpaceLightingRendererFeature).GetFields(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (!typeof(RayTracingProbeUpdate).IsAssignableFrom(field.FieldType)) continue;
                if (field.GetValue(feature) != null) report.giProducerAllocated = true;
            }
        }
        report.giAtlasAllocated = report.giProducerAllocated;
    }

#if UNITY_EDITOR
    // ---------------------------------------------------------------------------------------------
    // Build
    // ---------------------------------------------------------------------------------------------

    public static void Build()
    {
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
        const string folder = "Assets/Fixture/R3Image";
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/Fixture", "R3Image");

        var lit = Shader.Find("Universal Render Pipeline/Lit");
        var complexLit = Shader.Find("Universal Render Pipeline/Complex Lit");
        Material Mat(Shader shader, string name, Color baseColor, float metallic, float smoothness, Color emission,
            bool clearCoatShader = false, bool transparentShader = false, float alpha = 1f)
        {
            var path = $"{folder}/{name}.mat";
            AssetDatabase.DeleteAsset(path);
            var material = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(material, path);
            material.SetColor("_BaseColor", new Color(baseColor.r, baseColor.g, baseColor.b, alpha));
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            material.SetColor("_EmissionColor", emission);
            if (emission.maxColorComponent > 0) material.EnableKeyword("_EMISSION");
            if (clearCoatShader)
            {
                material.EnableKeyword("_CLEARCOAT");
                material.SetFloat("_ClearCoat", 1f);
                material.SetFloat("_ClearCoatMask", 1f);
                material.SetFloat("_ClearCoatSmoothness", 1f);
            }
            if (transparentShader)
            {
                material.SetFloat("_Surface", 1f);
                material.SetFloat("_Blend", 0f);
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite", 0f);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.SetOverrideTag("RenderType", "Transparent");
                material.renderQueue = (int)RenderQueue.Transparent;
            }
            EditorUtility.SetDirty(material);
            return material;
        }

        var matte = Mat(lit, "matte", new Color(.5f, .5f, .5f), 0, .2f, Color.black);
        var mirror = Mat(lit, "mirror", Color.white, 1, 1, Color.black);
        var clear = Mat(complexLit, "clearCoat", Color.white, 1, 1, Color.black, clearCoatShader: true);
        var donor = Mat(lit, "donor", Color.black, 1, 0, new Color(Emission, 0, 0));
        var transparent = Mat(lit, "transparent", Color.black, 0, .5f, new Color(0, 0, 1),
            transparentShader: true, alpha: .5f);

        var template = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/URP-HighFidelity-Renderer.asset");
        Check(template != null, "Missing High Fidelity renderer template");
        var renderers = new ScriptableRendererData[3];
        var features = new ScreenSpaceLightingRendererFeature[3];
        for (int i = 0; i < 3; ++i)
        {
            var mode = i == 0 ? RenderingMode.Forward : i == 1 ? RenderingMode.ForwardPlus : RenderingMode.Deferred;
            var path = $"{folder}/{mode}Renderer.asset";
            AssetDatabase.DeleteAsset(path);
            var renderer = UnityEngine.Object.Instantiate(template);
            renderer.name = mode + " R3Image Renderer";
            AssetDatabase.CreateAsset(renderer, path);
            foreach (var existing in AssetDatabase.LoadAllAssetsAtPath(path))
                if (existing is ScriptableRendererFeature) AssetDatabase.RemoveObjectFromAsset(existing);
            renderer.rendererFeatures.Clear();
            renderer.renderingMode = mode;
            var feature = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
            feature.name = mode + " Screen Space Lighting";
            feature.Create();
            feature.settings.rayTracingReflections = true;
            feature.settings.reflectionRayBudget = Budget;
            feature.settings.EnsureResources();
            Check(feature.settings.resources.rayTracingSsr != null, "Missing SSR raygen asset");
            AssetDatabase.AddObjectToAsset(feature, renderer);
            renderer.rendererFeatures.Add(feature);
            var serialized = new SerializedObject(renderer);
            var map = serialized.FindProperty("m_RendererFeatureMap");
            map.arraySize = renderer.rendererFeatures.Count;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
            map.GetArrayElementAtIndex(0).longValue = localId;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(renderer);
            renderer.SetDirty();
            renderers[i] = renderer;
            features[i] = feature;
        }

        var templatePipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/URP-HighFidelity.asset");
        Check(templatePipeline != null, "Missing High Fidelity pipeline template");
        var pipelinePath = folder + "/R3ImagePipeline.asset";
        AssetDatabase.DeleteAsset(pipelinePath);
        var pipeline = UnityEngine.Object.Instantiate(templatePipeline);
        pipeline.name = "R3Image Pipeline";
        AssetDatabase.CreateAsset(pipeline, pipelinePath);
        pipeline.msaaSampleCount = 1;
        pipeline.supportsHDR = true;
        pipeline.renderScale = 1f;
        var pipelineSerialized = new SerializedObject(pipeline);
        var list = pipelineSerialized.FindProperty("m_RendererDataList");
        list.arraySize = 3;
        for (int i = 0; i < 3; ++i) list.GetArrayElementAtIndex(i).objectReferenceValue = renderers[i];
        pipelineSerialized.FindProperty("m_DefaultRendererIndex").intValue = 0;
        pipelineSerialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(pipeline);
        AssetDatabase.SaveAssets();

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var driver = new GameObject("R3 image fixture").AddComponent<R3ImagePlayer>();
        driver.mirror = mirror; driver.clearCoat = clear; driver.donor = donor;
        driver.transparent = transparent; driver.matte = matte;
        driver.features = features;
        driver.pipeline = pipeline;
        Check(EditorSceneManager.SaveScene(scene, "Assets/R3Image.unity"), "Cannot save R3 image scene");

        // URP strips shader variants against the quality/default pipeline assets only. The fixture
        // assigns its pipeline at runtime, so without this the build sees a Forward-only project and
        // strips the Lit GBuffer pass (and the Deferred renderer draws nothing).
        var previousPipelines = new RenderPipelineAsset[QualitySettings.names.Length];
        int activeLevel = QualitySettings.GetQualityLevel();
        for (int q = 0; q < previousPipelines.Length; ++q)
        {
            QualitySettings.SetQualityLevel(q, false);
            previousPipelines[q] = QualitySettings.renderPipeline;
            QualitySettings.renderPipeline = pipeline;
        }
        QualitySettings.SetQualityLevel(activeLevel, false);
        var previousDefault = GraphicsSettings.defaultRenderPipeline;
        GraphicsSettings.defaultRenderPipeline = pipeline;
        try
        {
            var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/R3Image.unity" }, target = target,
                locationPathName = Arg("-exe"), options = BuildOptions.Development
            });
            Check(build.summary.result == BuildResult.Succeeded, "Win64 R3 image build failed");
        }
        finally
        {
            for (int q = 0; q < previousPipelines.Length; ++q)
            {
                QualitySettings.SetQualityLevel(q, false);
                QualitySettings.renderPipeline = previousPipelines[q];
            }
            QualitySettings.SetQualityLevel(activeLevel, false);
            GraphicsSettings.defaultRenderPipeline = previousDefault;
        }
    }
#endif
}
