#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif
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

// G3c: the probe fallback with the maintenance toggles, a dark dynamic step, the SSGI blend and the
// thin-wall rejection. Scene A is the constant-radiance room of G3a/G3b. The four probes on the
// receiver's front cell layer are enclosed by boxes that are invisible to the raster camera but
// present in the TLAS: without classification their tiles are black and the receiver darkens, with
// classification they drop out and the weights renormalise back to the analytic 1.92. The room is
// emissive with a 0.25 albedo so feedback has an analytic fixed point (E/pi = 2/(1-0.25) = 2.667).
// Scene B is a lit room split by a 6 cm divider: the rear face must not see the front probes.
public sealed class G3cPlayer : MonoBehaviour
{
    public const int Width = 960, Height = 540;
    const float Fov = 60f;
    const int InvisibleLayer = 31;

    const float CameraX = 0f, CameraY = 2f, CameraZ = -2.5f;
    const float ReceiverX = 0f, ReceiverY = 2f, ReceiverZ = 3f, ReceiverHalf = 1f;
    const float Emission = 2f, WallAlbedo = .25f, OneMinusReflectivity = .96f;
    // _BaseColor is an sRGB/gamma property (see G5): 0.537 sRGB stores the linear 0.25 the
    // feedback fixed point uses.
    static readonly Color WallColor = new Color(.537f, .537f, .537f);
    const float ProbeY = OneMinusReflectivity * Emission;                       // 1.92
    const float FeedbackY = ProbeY / (1f - WallAlbedo);                          // 2.56
    const float GiErrorMax = .10f, ThinWallRatioMax = .05f;
    const int SettleFrames = 40, DarkFrames = 192, FeedbackFrames = 256, ShortFrames = 4,
        BothFrames = 32, DualFrames = 24;

    // Receiver cell at (0, 2, 3): x and y split 0.5, z splits 2.5/3.5. The z=2.5 layer sits in front
    // of the receiver (normal wrap > 0) and is fully enclosed, the z=3.5 layer behind it is the
    // near-wall layer the classifier keeps active.
    static readonly int[] EnclosedProbes = { 203, 204, 211, 212 };
    static readonly int[] BackProbes = { 235, 236, 243, 244 };

    public Material emissive, emissiveOff, black, matte, receiverMaterial;
    public ScreenSpaceLightingRendererFeature[] features = new ScreenSpaceLightingRendererFeature[3];
    public UniversalRenderPipelineAsset pipeline;

    [Serializable] class Stage
    {
        public string stage, api, guid, gate, requests, frameHash, stateHash, notes;
        public int renderer, frames, nonBlack, instances, builds, subscribers, updates, generation;
        public long producerBytes;
        public bool producerAllocated, ssgiProbeFlag, supported, feedback, relocation, classification, historyOk,
            ssgiProduced;
        public float coverage, intensity, roiMeanY, expectedY, relativeError, pixelDiffMax, ratio, firstFrameY;
        public int roiX, roiY, roiW, roiH, active, inactive, relocated, historyCount;
        public float offsetMax, activeStoredIrr, inactiveStoredIrr;
    }

    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(),
            adapter = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion, error;
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed;
        public int width = Width, height = Height, sceneBuilds, sceneInstances, subscribers;
        public string sceneRequests;
        public float fov = Fov, camY = CameraY, camZ = CameraZ, receiverY = ReceiverY, receiverZ = ReceiverZ,
            receiverAlbedo = 1f, emission = Emission, wallAlbedo = WallAlbedo, expectedY = ProbeY,
            feedbackY = FeedbackY, giErrorMax = GiErrorMax, thinWallRatioMax = ThinWallRatioMax;
        public float gridSpacing = 1f;
        public List<string> renderers = new List<string> { "Forward", "ForwardPlus", "Deferred" };
        public List<Stage> stages = new List<Stage>();
    }

    Report report;
    Camera view;
    RenderTexture target, secondTarget;
    Texture2D readback;
    Volume volume;
    ScreenSpaceGlobalIllumination ssgi;
    GameObject sceneRoot;
    readonly List<MeshRenderer> emissiveRenderers = new List<MeshRenderer>();
    readonly List<GameObject> enclosures = new List<GameObject>();
    float tanV, tanH;
    int roiX, roiY, roiW, roiH;
    string destination;
    readonly List<string> failures = new List<string>();

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
    static float Luminance(Vector4 c) => .2126f * c.x + .7152f * c.y + .0722f * c.z;

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
        if (secondTarget) { secondTarget.Release(); Destroy(secondTarget); secondTarget = null; }
        if (readback) { Destroy(readback); readback = null; }
        if (volume) { Destroy(volume.gameObject); volume = null; }

        foreach (var s in report.stages)
        {
            report.sceneBuilds = Mathf.Max(report.sceneBuilds, s.builds);
            report.sceneInstances = Mathf.Max(report.sceneInstances, s.instances);
            report.subscribers = Mathf.Max(report.subscribers, s.subscribers);
            if (s.updates > 0 && !string.IsNullOrEmpty(s.requests)) report.sceneRequests = s.requests;
        }

        if (report.api == "Direct3D11")
        {
            Gate(!report.supportsRayTracing, "D3D11 must not report DXR support");
            foreach (var s in report.stages)
            {
                Gate(s.gate == "RayTracingUnsupported", $"{s.stage}/{s.renderer} gate {s.gate}");
                Gate(!s.producerAllocated, $"{s.stage}/{s.renderer} allocated a producer on D3D11");
                Gate(s.updates == 0, $"{s.stage}/{s.renderer} dispatched a probe batch on D3D11");
            }
        }
    }

    IEnumerator Suite()
    {
        Check(report.development && Application.platform == RuntimePlatform.WindowsPlayer && IntPtr.Size == 8 &&
            QualitySettings.activeColorSpace == ColorSpace.Linear && emissive && emissiveOff && black && matte &&
            receiverMaterial && pipeline && features.Length == 3 && features[0] && features[1] && features[2],
            "Invalid Player configuration");

        int quality = Array.IndexOf(QualitySettings.names, "High Fidelity");
        Check(quality >= 0, "Missing High Fidelity quality level");
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        QualitySettings.SetQualityLevel(quality, true);
        QualitySettings.renderPipeline = pipeline;
        GraphicsSettings.defaultRenderPipeline = pipeline;
        yield return null;
        yield return null;
        Check(UniversalRenderPipeline.asset == pipeline, "G3c pipeline is not active");

        tanV = Mathf.Tan(Fov * Mathf.Deg2Rad * .5f);
        tanH = tanV * (Width / (float)Height);
        BuildVolume();
        yield return Warmup();

        for (int renderer = 0; renderer < 3; ++renderer)
        {
            BuildSceneA();
            yield return Warmup();
            yield return Off(renderer);
            yield return Classification(renderer);
            yield return Relocation(renderer);
            yield return DarkStep(renderer);
            yield return Both(renderer);
            yield return Cut(renderer);
            yield return Dual(renderer);
            yield return Feedback(renderer);
            yield return FeedbackDecay(renderer);
            DestroyScene();
            yield return Warmup();

            BuildSceneB();
            yield return Warmup();
            yield return ThinWall(renderer);
            DestroyScene();
            yield return Warmup();
        }
        Evaluate();
    }

    // ---------------------------------------------------------------------------------------------
    // Scene construction
    // ---------------------------------------------------------------------------------------------

    void BuildVolume()
    {
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;
        var blackCube = new Cubemap(1, TextureFormat.RGBAHalf, false);
        for (int face = 0; face < 6; ++face) blackCube.SetPixel((CubemapFace)face, 0, 0, new Color(0, 0, 0, 0));
        blackCube.Apply();
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        RenderSettings.customReflectionTexture = blackCube;

        var go = new GameObject("G3c volume");
        volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 999;
        ssgi = volume.profile.Add<ScreenSpaceGlobalIllumination>();
        ssgi.enabled.Override(true);
        ssgi.intensity.Override(0f);
        ssgi.fullResolution.Override(true);
        ssgi.temporalAccumulation.Override(true);
        ssgi.denoise.Override(false);
    }

    GameObject Quad(string name, Vector3 position, Vector2 size, Material material)
    {
        var mesh = new Mesh { name = name };
        float hx = size.x * .5f, hy = size.y * .5f;
        mesh.vertices = new[] { new Vector3(-hx, -hy, 0), new Vector3(-hx, hy, 0), new Vector3(hx, hy, 0), new Vector3(hx, -hy, 0) };
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        var go = new GameObject(name);
        go.transform.SetParent(sceneRoot.transform, false);
        go.transform.position = position;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        return go;
    }

    GameObject Box(string name, Vector3 position, Vector3 size, Material material, int id, bool visible)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(sceneRoot.transform, false);
        go.transform.position = position;
        go.transform.localScale = size;
        if (!visible) go.layer = InvisibleLayer;
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.rayTracingMode = RayTracingMode.Static;
        var geometry = go.AddComponent<SsrRayTracingGeometry>();
        geometry.instanceId = id;
        geometry.enableTriangleCulling = false;
        if (material == emissive || material == emissiveOff) emissiveRenderers.Add(renderer);
        return go;
    }

    void BuildSceneA()
    {
        sceneRoot = new GameObject("G3c scene A");
        emissiveRenderers.Clear();
        Box("floor", new Vector3(0, -.1f, 0), new Vector3(8.2f, .2f, 8.2f), emissive, 1, true);
        Box("ceiling", new Vector3(0, 4.1f, 0), new Vector3(8.2f, .2f, 8.2f), emissive, 2, true);
        Box("wall-x-", new Vector3(-4.1f, 2, 0), new Vector3(.2f, 4.2f, 8.2f), emissive, 3, true);
        Box("wall-x+", new Vector3(4.1f, 2, 0), new Vector3(.2f, 4.2f, 8.2f), emissive, 4, true);
        Box("wall-z-", new Vector3(0, 2, -4.1f), new Vector3(8.2f, 4.2f, .2f), emissive, 5, true);
        Box("wall-z+", new Vector3(0, 2, 4.1f), new Vector3(8.2f, 4.2f, .2f), emissive, 6, true);
        // Enclosures on the receiver's front cell layer: an emissive outer box keeps the neighbours'
        // radiance at L, a black inner box blacks out the enclosed probe's tile. Both are on the
        // camera's culled layer, so the receiver quad stays fully visible while the TLAS sees them.
        Enclosure("enclosure-0", new Vector3(-.5f, 1.5f, 2.5f), 7);
        Enclosure("enclosure-1", new Vector3(.5f, 1.5f, 2.5f), 9);
        Enclosure("enclosure-2", new Vector3(-.5f, 2.5f, 2.5f), 11);
        Enclosure("enclosure-3", new Vector3(.5f, 2.5f, 2.5f), 13);
        Quad("receiver", new Vector3(ReceiverX, ReceiverY, ReceiverZ), new Vector2(2 * ReceiverHalf, 2 * ReceiverHalf),
            receiverMaterial);
    }

    void Enclosure(string name, Vector3 center, int id)
    {
        enclosures.Add(Box(name + " outer", center, Vector3.one * .9f, emissive, id, false));
        enclosures.Add(Box(name + " inner", center, Vector3.one * .7f, black, id + 1, false));
    }

    // Removing the enclosures leaves the receiver cell's front layer as ordinary interior probes:
    // inactive, but measured at the room radiance before classification froze them.
    void DestroyEnclosures()
    {
        foreach (var go in enclosures) if (go) Destroy(go);
        enclosures.Clear();
    }

    void BuildSceneB()
    {
        sceneRoot = new GameObject("G3c scene B");
        emissiveRenderers.Clear();
        Box("floor", new Vector3(0, -.1f, 0), new Vector3(8.2f, .2f, 8.2f), matte, 1, true);
        Box("ceiling", new Vector3(0, 4.1f, 0), new Vector3(8.2f, .2f, 8.2f), matte, 2, true);
        Box("wall-x-", new Vector3(-4.1f, 2, 0), new Vector3(.2f, 4.2f, 8.2f), matte, 3, true);
        Box("wall-x+", new Vector3(4.1f, 2, 0), new Vector3(.2f, 4.2f, 8.2f), matte, 4, true);
        Box("wall-z-", new Vector3(0, 2, -4.1f), new Vector3(8.2f, 4.2f, .2f), matte, 5, true);
        Box("wall-z+", new Vector3(0, 2, 4.1f), new Vector3(8.2f, 4.2f, .2f), matte, 6, true);
        Box("divider", new Vector3(0, 2, 1f), new Vector3(8.2f, 4.2f, .06f), matte, 7, true);
        var lamp = new GameObject("G3c lamp");
        lamp.transform.SetParent(sceneRoot.transform, false);
        lamp.transform.position = new Vector3(0, 2, 2.9f);
        var light = lamp.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = Color.white;
        light.intensity = 6f;
        light.range = 10f;
        light.shadows = LightShadows.Hard;
    }

    void DestroyScene()
    {
        if (sceneRoot) Destroy(sceneRoot);
        sceneRoot = null;
        emissiveRenderers.Clear();
        enclosures.Clear();
    }

    void SetEmission(bool on)
    {
        var material = on ? emissive : emissiveOff;
        foreach (var renderer in emissiveRenderers) if (renderer) renderer.sharedMaterial = material;
    }

    IEnumerator Warmup()
    {
        // A material asset reports one placeholder pass until its shader is used once; the RT gate
        // needs FindPass("GlxDXR") on the registered witness.
        var warm = new GameObject("G3c warm camera").AddComponent<Camera>();
        warm.clearFlags = CameraClearFlags.SolidColor;
        warm.backgroundColor = Color.black;
        warm.cullingMask = ~(1 << InvisibleLayer);
        warm.targetTexture = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        for (int i = 0; i < 4; ++i) yield return null;
        warm.targetTexture = null;
        Destroy(warm.gameObject);
    }

    // ---------------------------------------------------------------------------------------------
    // Projection, readback and reporting
    // ---------------------------------------------------------------------------------------------

    Vector2 Project(Vector3 cam, float forwardZ, Vector3 world)
    {
        float depth = (world.z - cam.z) * forwardZ;
        float nx = (world.x - cam.x) / (tanH * depth);
        float ny = (world.y - cam.y) / (tanV * depth);
        if (forwardZ < 0) nx = -nx;
        return new Vector2((nx + 1) * .5f * Width, (ny + 1) * .5f * Height);
    }

    void SetRoi(Vector3 cam, float forwardZ, Vector3 center, float halfX, float halfY)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int corner = 0; corner < 4; ++corner)
        {
            var p = Project(cam, forwardZ, center + new Vector3(
                (corner & 1) == 0 ? -halfX : halfX, (corner & 2) == 0 ? -halfY : halfY, 0));
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        }
        const int inset = 6;
        roiX = Mathf.Clamp(Mathf.CeilToInt(minX) + inset, 0, Width - 2);
        roiY = Mathf.Clamp(Mathf.CeilToInt(minY) + inset, 0, Height - 2);
        roiW = Mathf.Clamp(Mathf.FloorToInt(maxX) - inset - roiX, 1, Width - roiX);
        roiH = Mathf.Clamp(Mathf.FloorToInt(maxY) - inset - roiY, 1, Height - roiY);
    }

    void SetRoiA(Vector3 cam) => SetRoi(cam, 1f, new Vector3(ReceiverX, ReceiverY, ReceiverZ), ReceiverHalf, ReceiverHalf);

    void EnsureTargets()
    {
        if (target == null)
        {
            target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            target.Create();
        }
        if (roiW <= 0 || roiH <= 0) return;
        if (readback == null || readback.width != roiW || readback.height != roiH)
        {
            if (readback) Destroy(readback);
            readback = new Texture2D(roiW, roiH, TextureFormat.RGBAHalf, false, true);
        }
    }

    Color[] ReadRoi(RenderTexture source = null)
    {
        source ??= target;
        var previous = RenderTexture.active;
        RenderTexture.active = source;
        try { readback.ReadPixels(new Rect(roiX, roiY, roiW, roiH), 0, 0); }
        finally { RenderTexture.active = previous; }
        readback.Apply(false, false);
        return readback.GetPixels();
    }

    string FullHash(RenderTexture source = null)
    {
        source ??= target;
        var tex = new Texture2D(Width, Height, TextureFormat.RGBAHalf, false, true);
        var previous = RenderTexture.active;
        RenderTexture.active = source;
        try { tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); }
        finally { RenderTexture.active = previous; }
        tex.Apply(false, false);
        var data = tex.GetRawTextureData();
        int nonBlack = 0;
        foreach (var p in tex.GetPixels()) if (p.r > .1f || p.g > .1f || p.b > .1f) ++nonBlack;
        lastNonBlack = nonBlack;
        Destroy(tex);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    int lastNonBlack;

    Camera NewCamera(int renderer, string label, Vector3 position, float yaw)
    {
        var camera = new GameObject("G3c camera " + label).AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.fieldOfView = Fov;
        camera.nearClipPlane = .1f;
        camera.farClipPlane = 50f;
        camera.allowMSAA = false;
        camera.allowHDR = true;
        camera.cullingMask = ~(1 << InvisibleLayer);
        camera.transform.SetPositionAndRotation(position, Quaternion.Euler(0, yaw, 0));
        var data = camera.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = false;
        data.antialiasing = AntialiasingMode.None;
        data.SetRenderer(renderer);
        return camera;
    }

    void ConfigureFeature(int renderer, bool fallback, bool feedback, bool relocation, bool classification)
    {
        var feature = features[renderer];
        feature.settings.rayTracingProbeFallback = fallback;
        feature.settings.probeCoverage = 1f;
        feature.settings.probeGridOrigin = new Vector3(-3.5f, .5f, -3.5f);
        feature.settings.probeFeedback = feedback;
        feature.settings.probeRelocation = relocation;
        feature.settings.probeClassification = classification;
        feature.settings.rayTracingReflections = false;
        feature.settings.reflectionRayBudget = 0;
    }

    Stage Begin(int renderer, string kind, int frames, bool feedback, bool relocation, bool classification,
        float intensity)
    {
        lastNonBlack = 0;
        ssgi.intensity.Override(intensity);
        var stage = new Stage
        {
            stage = kind, api = report.api, guid = report.guid, renderer = renderer, frames = frames,
            feedback = feedback, relocation = relocation, classification = classification,
            coverage = 1f, intensity = intensity, roiX = roiX, roiY = roiY, roiW = roiW, roiH = roiH,
            supported = report.api == "Direct3D12"
        };
        return stage;
    }

    void End(Stage stage)
    {
        var feature = features[stage.renderer];
        var fb = feature.probeFallback;
        stage.instances = feature.rayTracingScene?.instanceCount ?? 0;
        stage.builds = feature.rayTracingScene?.buildCount ?? 0;
        stage.subscribers = feature.rayTracingScene?.subscribers ?? 0;
        stage.requests = feature.rayTracingScene?.requests.ToString();
        stage.updates = fb?.updateCount ?? 0;
        stage.generation = fb?.generation ?? 0;
        stage.producerBytes = fb?.producerBytes ?? 0;
        stage.producerAllocated = fb?.producerAllocated ?? false;
        stage.ssgiProbeFlag = ProbeFlag(feature);
        stage.ssgiProduced = feature.ssgiPass != null && feature.ssgiPass.producedOutput;
        stage.gate = stage.supported ? GateString(feature) : "RayTracingUnsupported";
        if (stage.supported && fb != null && fb.producerAllocated)
        {
            var data = fb.ReadProbeData();
            int active = 0, inactive = 0, relocated = 0;
            float max = 0;
            foreach (var probe in data)
            {
                if (probe.w > .5f) ++active; else ++inactive;
                var offset = new Vector3(probe.x, probe.y, probe.z);
                if (offset.sqrMagnitude > 1e-8f) ++relocated;
                max = Mathf.Max(max, offset.magnitude);
            }
            stage.active = active;
            stage.inactive = inactive;
            stage.relocated = relocated;
            stage.offsetMax = max;
            stage.stateHash = StateHash(data);
            stage.activeStoredIrr = StoredIrr(fb, BackProbes[0]);
            stage.inactiveStoredIrr = StoredIrr(fb, EnclosedProbes[0]);
        }
        report.stages.Add(stage);
        if (view) { view.targetTexture = null; Destroy(view.gameObject); view = null; }
        stage.nonBlack = lastNonBlack;
    }

    static string StateHash(Vector4[] data)
    {
        using var sha = SHA256.Create();
        var bytes = new byte[data.Length * 16];
        for (int i = 0; i < data.Length; ++i)
        {
            BitConverter.GetBytes(data[i].x).CopyTo(bytes, i * 16);
            BitConverter.GetBytes(data[i].y).CopyTo(bytes, i * 16 + 4);
            BitConverter.GetBytes(data[i].z).CopyTo(bytes, i * 16 + 8);
            BitConverter.GetBytes(data[i].w).CopyTo(bytes, i * 16 + 12);
        }
        var hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    static float StoredIrr(RayTracingProbeFallback fallback, int probe)
    {
        var irr = new Vector4[64];
        var dist = new Vector4[256];
        fallback.ReadProbe(probe, irr, dist, out _, out _);
        double sum = 0;
        for (int y = 1; y <= 6; ++y)
            for (int x = 1; x <= 6; ++x)
                sum += Luminance(irr[y * 8 + x]);
        return (float)(sum / 36.0);
    }

    static bool ProbeFlag(ScreenSpaceLightingRendererFeature feature)
    {
        var pass = typeof(ScreenSpaceLightingRendererFeature)
            .GetProperty("ssgiPass", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(feature);
        if (pass == null) return false;
        var field = pass.GetType().GetField("m_ProbeFallbackActive",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field != null && (bool)field.GetValue(pass);
    }

    string GateString(ScreenSpaceLightingRendererFeature feature)
    {
        var gate = RayTracingSceneGate.Capture(new CameraData
        {
            camera = view, cameraType = CameraType.Game, renderType = CameraRenderType.Base,
            cameraTargetDescriptor = new RenderTextureDescriptor(Width, Height)
        }, true, feature.settings.resources.rayTracingGi, matte);
        return gate.Evaluate(RayTracingRequests.GI).ToString();
    }

    void Measure(Stage stage, Color[] pixels, float expected)
    {
        double y = 0;
        foreach (var p in pixels) y += Luminance(p);
        stage.roiMeanY = (float)(y / pixels.Length);
        stage.expectedY = expected;
        stage.relativeError = Mathf.Abs(stage.roiMeanY - expected) / Mathf.Max(Mathf.Abs(expected), .01f);
    }

    IEnumerator Render(int frames)
    {
        for (int i = 0; i < frames; ++i) yield return new WaitForEndOfFrame();
    }

    IEnumerator ResetProducer(int renderer, bool feedback, bool relocation, bool classification)
    {
        // Two frames with the switch off release the atlas and the scene request, so the next stage
        // starts from a genuine cold state instead of the previous stage's converged tiles.
        ConfigureFeature(renderer, false, feedback, relocation, classification);
        var reset = NewCamera(renderer, "reset", new Vector3(CameraX, CameraY, CameraZ), 0f);
        var resetTarget = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        resetTarget.Create();
        reset.targetTexture = resetTarget;
        yield return new WaitForEndOfFrame();
        yield return new WaitForEndOfFrame();
        reset.targetTexture = null;
        Destroy(reset.gameObject);
        resetTarget.Release();
        Destroy(resetTarget);
    }

    // ---------------------------------------------------------------------------------------------
    // Scene A stages
    // ---------------------------------------------------------------------------------------------

    IEnumerator Off(int renderer)
    {
        yield return ResetProducer(renderer, false, false, false);
        ConfigureFeature(renderer, true, false, false, false);
        SetRoiA(new Vector3(CameraX, CameraY, CameraZ));
        var stage = Begin(renderer, "off", SettleFrames, false, false, false, 0f);
        EnsureTargets();
        view = NewCamera(renderer, "off", new Vector3(CameraX, CameraY, CameraZ), 0f);
        view.targetTexture = target;
        yield return Render(stage.frames);
        var pixels = ReadRoi();
        stage.frameHash = FullHash();
        Measure(stage, pixels, ProbeY * .55f);
        End(stage);
        if (!stage.supported) yield break;
        Gate(stage.active == 256 && stage.relocated == 0,
            $"off/{renderer} toggles were not off (active={stage.active} relocated={stage.relocated})");
        Gate(stage.roiMeanY <= .8f * ProbeY, $"off/{renderer} did not consume the black enclosures");
    }

    IEnumerator Classification(int renderer)
    {
        yield return ResetProducer(renderer, false, false, true);
        ConfigureFeature(renderer, true, false, false, true);
        SetRoiA(new Vector3(CameraX, CameraY, CameraZ));
        var stage = Begin(renderer, "classification", SettleFrames, false, false, true, 0f);
        EnsureTargets();
        view = NewCamera(renderer, "classification", new Vector3(CameraX, CameraY, CameraZ), 0f);
        view.targetTexture = target;
        yield return Render(stage.frames);
        var pixels = ReadRoi();
        stage.frameHash = FullHash();
        Measure(stage, pixels, ProbeY);
        End(stage);
        if (!stage.supported) yield break;
        Gate(stage.inactive > 0, $"classification/{renderer} classified nothing inactive");
        Gate(stage.roiMeanY >= .8f * ProbeY && stage.roiMeanY <= 1.2f * ProbeY,
            $"classification/{renderer} did not renormalise up to the analytic value ({stage.roiMeanY:0.###})");
        var off = Find("off", renderer);
        if (off != null) Gate(stage.roiMeanY >= 1.2f * off.roiMeanY,
            $"classification/{renderer} did not lift the black enclosure cells");
    }

    IEnumerator Relocation(int renderer)
    {
        yield return ResetProducer(renderer, false, true, true);
        ConfigureFeature(renderer, true, false, true, true);
        SetRoiA(new Vector3(CameraX, CameraY, CameraZ));
        var stage = Begin(renderer, "relocation", SettleFrames, false, true, true, 0f);
        EnsureTargets();
        view = NewCamera(renderer, "relocation", new Vector3(CameraX, CameraY, CameraZ), 0f);
        view.targetTexture = target;
        yield return Render(stage.frames);
        var pixels = ReadRoi();
        stage.frameHash = FullHash();
        Measure(stage, pixels, ProbeY);
        End(stage);
        if (!stage.supported) yield break;
        Gate(stage.relocated > 0 && stage.offsetMax > .2f && stage.offsetMax <= .45f + 1e-3f,
            $"relocation/{renderer} exposed offsets ({stage.relocated}, max {stage.offsetMax:0.###})");
        var classification = Find("classification", renderer);
        if (classification != null)
            Gate(Mathf.Abs(stage.roiMeanY - classification.roiMeanY) <= .02f * ProbeY,
                $"relocation/{renderer} changed the receiver ({stage.roiMeanY:0.####} vs {classification.roiMeanY:0.####})");
    }

    IEnumerator DarkStep(int renderer)
    {
        // With the enclosures gone the receiver cell's front layer is ordinary interior probes: they
        // are measured at the room radiance on the cold revisit, then frozen when classification
        // marks them inactive. Blacking the room out converges the active back layer to 0; a
        // consumer that used the frozen bright tiles would keep half the old energy in the image.
        DestroyEnclosures();
        yield return ResetProducer(renderer, false, true, true);
        ConfigureFeature(renderer, true, false, true, true);
        SetRoiA(new Vector3(CameraX, CameraY, CameraZ));
        var stage = Begin(renderer, "darkStep", DarkFrames, false, true, true, 0f);
        EnsureTargets();
        view = NewCamera(renderer, "darkStep", new Vector3(CameraX, CameraY, CameraZ), 0f);
        view.targetTexture = target;
        yield return Render(SettleFrames);
        SetEmission(false);
        yield return Render(stage.frames);
        var pixels = ReadRoi();
        stage.frameHash = FullHash();
        Measure(stage, pixels, 0f);
        End(stage);
        SetEmission(true);
        if (!stage.supported) yield break;
        float stale = stage.inactiveStoredIrr, live = stage.activeStoredIrr;
        stage.ratio = stage.roiMeanY / ProbeY;
        Gate(stale > .5f * 2f * Mathf.PI, $"darkStep/{renderer} inactive tile was rewritten ({stale:0.###})");
        Gate(live <= .1f * 2f * Mathf.PI, $"darkStep/{renderer} active tile kept the old energy ({live:0.###})");
        Gate(stage.ratio <= ThinWallRatioMax,
            $"darkStep/{renderer} consumed frozen inactive tiles ({stage.roiMeanY:0.####})");
    }

    IEnumerator Both(int renderer)
    {
        yield return ResetProducer(renderer, false, false, true);
        ConfigureFeature(renderer, true, false, false, true);
        SetRoiA(new Vector3(CameraX, CameraY, CameraZ));
        var stage = Begin(renderer, "both", SettleFrames, false, false, true, 0f);
        EnsureTargets();
        view = NewCamera(renderer, "both", new Vector3(CameraX, CameraY, CameraZ), 0f);
        view.targetTexture = target;
        yield return Render(stage.frames);
        var probe = ReadRoi();
        float probeY = 0;
        foreach (var p in probe) probeY += Luminance(p);
        probeY /= probe.Length;
        ssgi.intensity.Override(1f);
        yield return Render(BothFrames);
        var pixels = ReadRoi();
        stage.frameHash = FullHash();
        Measure(stage, pixels, ProbeY);
        float diff = 0;
        for (int i = 0; i < pixels.Length && i < probe.Length; ++i)
            diff = Mathf.Max(diff, Mathf.Abs(Luminance(pixels[i]) - Luminance(probe[i])));
        stage.pixelDiffMax = diff;
        stage.firstFrameY = probeY;
        End(stage);
        if (!stage.supported) yield break;
        Gate(stage.roiMeanY >= .8f * ProbeY && stage.roiMeanY <= 1.2f * ProbeY,
            $"both/{renderer} SSGI blend moved the ROI ({stage.roiMeanY:0.###})");
        Gate(diff <= GiErrorMax * ProbeY, $"both/{renderer} per-pixel seam {diff:0.###}");
        Gate(stage.ssgiProbeFlag, $"both/{renderer} SSGI fallback was not forced to None");
        Gate(stage.ssgiProduced, $"both/{renderer} SSGI produced no output, the blend was not exercised");
    }

    IEnumerator Cut(int renderer)
    {
        yield return ResetProducer(renderer, false, false, true);
        ConfigureFeature(renderer, true, false, false, true);
        var poseA = new Vector3(CameraX, CameraY, CameraZ);
        SetRoiA(poseA);
        var stage = Begin(renderer, "cut", SettleFrames, false, false, true, 0f);
        EnsureTargets();
        view = NewCamera(renderer, "cut", poseA, 0f);
        view.targetTexture = target;
        yield return Render(stage.frames);
        string before = StateHash(features[renderer].probeFallback?.ReadProbeData() ?? Array.Empty<Vector4>());
        int updatesBefore = features[renderer].probeFallback?.updateCount ?? 0;
        int generationBefore = features[renderer].probeFallback?.generation ?? 0;
        var poseB = new Vector3(CameraX + .6f, CameraY + .2f, CameraZ + .7f);
        view.transform.position = poseB;
        SetRoiA(poseB);
        EnsureTargets();
        yield return new WaitForEndOfFrame();
        var first = ReadRoi();
        float firstY = 0;
        foreach (var p in first) firstY += Luminance(p);
        firstY /= first.Length;
        stage.firstFrameY = firstY;
        yield return Render(ShortFrames);
        var pixels = ReadRoi();
        stage.frameHash = FullHash();
        Measure(stage, pixels, ProbeY);
        stage.roiX = roiX; stage.roiY = roiY; stage.roiW = roiW; stage.roiH = roiH;
        stage.stateHash = StateHash(features[renderer].probeFallback?.ReadProbeData() ?? Array.Empty<Vector4>());
        End(stage);
        if (!stage.supported) yield break;
        if (features[renderer].probeFallback != null)
        {
            int updates = features[renderer].probeFallback.updateCount;
            int generation = features[renderer].probeFallback.generation;
            Gate(updates - updatesBefore <= stage.frames + 1,
                $"cut/{renderer} dispatched more than one batch per frame");
            Gate(generation > generationBefore, $"cut/{renderer} stopped the producer");
        }
        Gate(before == stage.stateHash, $"cut/{renderer} reset the probe state/offsets");
        Gate(firstY >= .9f * stage.roiMeanY && firstY <= 1.1f * stage.roiMeanY,
            $"cut/{renderer} first frame after the cut blended a partial generation ({firstY:0.###})");
        Gate(firstY >= .8f * ProbeY && firstY <= 1.2f * ProbeY, $"cut/{renderer} first frame {firstY:0.###}");
    }

    IEnumerator Dual(int renderer)
    {
        yield return ResetProducer(renderer, false, false, true);
        ConfigureFeature(renderer, true, false, false, true);
        var poseA = new Vector3(CameraX, CameraY, CameraZ);
        var poseB = new Vector3(CameraX + .6f, CameraY + .2f, CameraZ + .7f);
        SetRoiA(poseA);
        var stage = Begin(renderer, "dual", DualFrames, false, false, true, 0f);
        EnsureTargets();
        view = NewCamera(renderer, "dual A", poseA, 0f);
        view.targetTexture = target;
        secondTarget = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        secondTarget.Create();
        var second = NewCamera(renderer, "dual B", poseB, 0f);
        second.targetTexture = secondTarget;
        int lastUpdates = features[renderer].probeFallback?.updateCount ?? 0;
        for (int f = 0; f < stage.frames; ++f)
        {
            yield return new WaitForEndOfFrame();
            if (!stage.supported) continue;
            int updates = features[renderer].probeFallback?.updateCount ?? 0;
            Gate(updates - lastUpdates <= 1, $"dual/{renderer} dispatched more than one batch in frame {f}");
            lastUpdates = updates;
        }
        stage.historyCount = features[renderer].historyStore?.entryCount ?? 0;
        stage.historyOk = stage.supported && features[renderer].historyStore != null &&
            features[renderer].historyStore.TryGet(view, 0, out _) &&
            features[renderer].historyStore.TryGet(second, 0, out _);
        var pixels = ReadRoi();
        stage.frameHash = FullHash();
        Measure(stage, pixels, ProbeY);
        End(stage);
        if (!stage.supported) yield break;
        Gate(stage.historyCount == 2, $"dual/{renderer} kept {stage.historyCount} camera histories");
        Gate(stage.historyOk, $"dual/{renderer} one camera has no history entry");
        // The second camera's own image must show the same published generation.
        SetRoiA(poseB);
        EnsureTargets();
        yield return Render(ShortFrames);
        var secondPixels = ReadRoi(secondTarget);
        float secondY = 0;
        foreach (var p in secondPixels) secondY += Luminance(p);
        secondY /= secondPixels.Length;
        Gate(secondY >= .8f * ProbeY && secondY <= 1.2f * ProbeY,
            $"dual/{renderer} second camera reads {secondY:0.###}");
        second.targetTexture = null;
        Destroy(second.gameObject);
        secondTarget.Release();
        Destroy(secondTarget);
        secondTarget = null;
    }

    IEnumerator Feedback(int renderer)
    {
        yield return ResetProducer(renderer, true, false, true);
        ConfigureFeature(renderer, true, true, false, true);
        SetRoiA(new Vector3(CameraX, CameraY, CameraZ));
        var stage = Begin(renderer, "feedback", FeedbackFrames, true, false, true, 0f);
        EnsureTargets();
        view = NewCamera(renderer, "feedback", new Vector3(CameraX, CameraY, CameraZ), 0f);
        view.targetTexture = target;
        yield return Render(stage.frames);
        var pixels = ReadRoi();
        stage.frameHash = FullHash();
        Measure(stage, pixels, FeedbackY);
        End(stage);
        if (!stage.supported) yield break;
        var feature = features[renderer];
        Gate(feature.probeFallback != null && feature.probeFallback.config.feedback,
            $"feedback/{renderer} toggle did not reach the producer");
        Gate(stage.roiMeanY >= .9f * FeedbackY && stage.roiMeanY <= 1.1f * FeedbackY,
            $"feedback/{renderer} fixed point {stage.roiMeanY:0.####} vs {FeedbackY:0.####}");
        var classification = Find("classification", renderer);
        Gate(stage.activeStoredIrr > (classification?.activeStoredIrr ?? 0f),
            $"feedback/{renderer} atlas did not gain ({stage.activeStoredIrr:0.###})");
    }

    IEnumerator FeedbackDecay(int renderer)
    {
        // Same producer, room turned dark: the only remaining term is the previous-generation
        // feedback, so the decay is alebdo * 0.25 per revisit on top of the 0.75 hysteresis.
        ConfigureFeature(renderer, true, true, false, true);
        SetRoiA(new Vector3(CameraX, CameraY, CameraZ));
        var stage = Begin(renderer, "feedbackDecay", FeedbackFrames, true, false, true, 0f);
        EnsureTargets();
        view = NewCamera(renderer, "feedbackDecay", new Vector3(CameraX, CameraY, CameraZ), 0f);
        view.targetTexture = target;
        float before = Find("feedback", renderer)?.roiMeanY ?? FeedbackY;
        SetEmission(false);
        yield return Render(stage.frames);
        var pixels = ReadRoi();
        stage.frameHash = FullHash();
        Measure(stage, pixels, 0f);
        stage.ratio = stage.roiMeanY / Mathf.Max(before, .01f);
        End(stage);
        SetEmission(true);
        if (!stage.supported) yield break;
        Gate(stage.ratio <= .05f, $"feedbackDecay/{renderer} left {stage.ratio:0.###} of the old signal");
    }

    // ---------------------------------------------------------------------------------------------
    // Scene B: thin wall
    // ---------------------------------------------------------------------------------------------

    IEnumerator ThinWall(int renderer)
    {
        yield return ResetProducer(renderer, false, false, true);
        ConfigureFeature(renderer, true, false, false, true);
        SetRoi(new Vector3(0, 2, 2.55f), -1f, new Vector3(0, 2, 1.03f), 1f, 1f);
        var front = Begin(renderer, "thinWallFront", SettleFrames, false, false, true, 0f);
        EnsureTargets();
        view = NewCamera(renderer, "thinWallFront", new Vector3(0, 2, 2.55f), 180f);
        view.targetTexture = target;
        yield return Render(front.frames);
        var frontPixels = ReadRoi();
        front.frameHash = FullHash();
        Measure(front, frontPixels, 0f);
        End(front);

        SetRoi(new Vector3(0, 2, -2.5f), 1f, new Vector3(0, 2, .97f), 1f, 1f);
        var rear = Begin(renderer, "thinWallRear", SettleFrames, false, false, true, 0f);
        EnsureTargets();
        view = NewCamera(renderer, "thinWallRear", new Vector3(0, 2, -2.5f), 0f);
        view.targetTexture = target;
        yield return Render(rear.frames);
        var rearPixels = ReadRoi();
        rear.frameHash = FullHash();
        Measure(rear, rearPixels, 0f);
        End(rear);

        if (!front.supported) yield break;
        rear.ratio = rear.roiMeanY / Mathf.Max(front.roiMeanY, .01f);
        Gate(front.roiMeanY > .02f, $"thinWall/{renderer} lit face is blank ({front.roiMeanY:0.###})");
        Gate(rear.ratio <= ThinWallRatioMax,
            $"thinWall/{renderer} rear face leaked {rear.ratio:0.###} of the lit face");
    }

    // ---------------------------------------------------------------------------------------------
    // Evaluation
    // ---------------------------------------------------------------------------------------------

    void Evaluate()
    {
        for (int renderer = 0; renderer < 3; ++renderer)
        {
            var off = Find("off", renderer);
            var classification = Find("classification", renderer);
            var relocation = Find("relocation", renderer);
            if (off == null || classification == null || relocation == null) continue;
            if (report.api == "Direct3D11")
            {
                Gate(off.frameHash == classification.frameHash, $"D3D11 classification changed the image ({renderer})");
                Gate(off.frameHash == relocation.frameHash, $"D3D11 relocation changed the image ({renderer})");
            }
        }
    }

    Stage Find(string kind, int renderer)
    {
        foreach (var s in report.stages) if (s.stage == kind && s.renderer == renderer) return s;
        return null;
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
        const string folder = "Assets/Fixture/G3c";
        if (!AssetDatabase.IsValidFolder(folder))
        {
            if (!AssetDatabase.IsValidFolder("Assets/Fixture")) AssetDatabase.CreateFolder("Assets", "Fixture");
            AssetDatabase.CreateFolder("Assets/Fixture", "G3c");
        }

        var lit = Shader.Find("Universal Render Pipeline/Lit");

        Material Mat(string name, Color baseColor, Color emission)
        {
            var path = $"{folder}/{name}.mat";
            AssetDatabase.DeleteAsset(path);
            var material = new Material(lit) { name = name };
            AssetDatabase.CreateAsset(material, path);
            material.SetColor("_BaseColor", baseColor);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", .2f);
            material.SetColor("_EmissionColor", emission);
            if (emission.maxColorComponent > 0) material.EnableKeyword("_EMISSION");
            EditorUtility.SetDirty(material);
            return material;
        }

        var emissive = Mat("emissive", WallColor, new Color(Emission, Emission, Emission));
        var emissiveOff = Mat("emissiveOff", WallColor, Color.black);
        var black = Mat("black", Color.black, Color.black);
        var matte = Mat("matte", new Color(.8f, .8f, .8f), Color.black);
        var receiver = Mat("receiver", Color.white, Color.black);

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
            renderer.name = mode + " G3c Renderer";
            AssetDatabase.CreateAsset(renderer, path);
            foreach (var existing in AssetDatabase.LoadAllAssetsAtPath(path))
                if (existing is ScriptableRendererFeature) AssetDatabase.RemoveObjectFromAsset(existing);
            renderer.rendererFeatures.Clear();
            renderer.renderingMode = mode;
            var feature = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
            feature.name = mode + " Screen Space Lighting";
            feature.Create();
            feature.settings.rayTracingProbeFallback = true;
            feature.settings.EnsureResources();
            Check(feature.settings.resources.rayTracingGi != null, "Missing GI raygen asset");
            Check(feature.settings.resources.giProbeUpdateCompute != null, "Missing probe update compute");
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
        var pipelinePath = folder + "/G3cPipeline.asset";
        AssetDatabase.DeleteAsset(pipelinePath);
        var pipeline = UnityEngine.Object.Instantiate(templatePipeline);
        pipeline.name = "G3c Pipeline";
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
        var driver = new GameObject("G3c fixture").AddComponent<G3cPlayer>();
        driver.emissive = emissive;
        driver.emissiveOff = emissiveOff;
        driver.black = black;
        driver.matte = matte;
        driver.receiverMaterial = receiver;
        driver.features = features;
        driver.pipeline = pipeline;
        Check(EditorSceneManager.SaveScene(scene, "Assets/G3c.unity"), "Cannot save G3c scene");

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
                scenes = new[] { "Assets/G3c.unity" }, target = target,
                locationPathName = Arg("-exe"), options = BuildOptions.Development
            });
            Check(build.summary.result == BuildResult.Succeeded, "Win64 G3c build failed");
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
