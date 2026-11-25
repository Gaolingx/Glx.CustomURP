// I1 integration matrix: the shared RT scene under "both off / GI only / SSR only / both", the
// maintenance-toggle sweep, the T3b ray-budget switch and the T4 light switches, plus the camera
// cut, resolution change, base+overlay stacking, dynamic donor and runtime shutdown.
//
// One scene hosts both consumers. The receiver cell of the emissive room is enclosed like G3c, so
// classification has something to drop; the metallic mirror reflects a green donor that sits
// outside the camera frustum, so only hardware hit shading can put its image on the mirror.
// GI is read on the diffuse receiver (analytic 0.96 * 2), SSR is read as the green excess (G - R)
// on the mirror. With SSR off the mirror falls back to the black reflection probe, with GI off the
// receiver stays black because SSGI intensity is zero in every stage.
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

public sealed class I1Player : MonoBehaviour
{
    const float Fov = 60f;
    const int InvisibleLayer = 31;

    const float CameraX = 0f, CameraY = 2f, CameraZ = -2.5f;
    const float ReceiverX = 0f, ReceiverY = 2f, ReceiverZ = 3f, ReceiverHalf = 1f;
    const float MirrorX = 2f, MirrorY = 2f, MirrorZ = 3.98f, MirrorHalf = .6f;
    const float DonorX = 3f, DonorY = 2f, DonorZ = 0f, DonorHalf = .15f;
    const float Emission = 2f, DonorEmission = 4f, WallAlbedo = .25f, OneMinusReflectivity = .96f;
    // _BaseColor is an sRGB/gamma property (see G5): 0.537 sRGB stores the linear 0.25 albedo.
    static readonly Color WallColor = new Color(.537f, .537f, .537f);
    const float ProbeY = OneMinusReflectivity * Emission;                       // 1.92
    const float GiErrorMax = .10f, MirrorOn = .5f, MirrorOff = .1f;
    const int SettleFrames = 40, SweepFrames = 40, ShortFrames = 4, CutFrames = 4,
        ResFrames = 16, StackFrames = 12, DynamicFrames = 8, Budget = 32768;

    // Receiver cell at (0, 2, 3): the z=2.5 layer is enclosed and classified inactive, the z=3.5
    // layer near the wall stays active.
    static readonly int[] EnclosedProbes = { 203, 204, 211, 212 };
    static readonly int[] BackProbes = { 235, 236, 243, 244 };

    public Material emissive, emissiveOff, black, receiverMaterial, mirrorMaterial, donorMaterial;
    public ScreenSpaceLightingRendererFeature[] features = new ScreenSpaceLightingRendererFeature[3];
    public UniversalRenderPipelineAsset pipeline;

    [Serializable] class Stage
    {
        public string stage, api, guid, gateGi, gateSsr, requests, stateHash, notes;
        public int renderer, frames, nonBlack, instances, builds, subscribers, liveResources, lightUploads,
            lightRegistered, lightUploaded, updates, generation, historyCount, prevHistoryCount;
        public long producerBytes;
        public bool supported, probeAllocated, ssrQueue, ssrProduced, historyOk, configMatched;
        public bool gi, ssr, feedback, relocation, classification;
        public int ssrBudget, ssrBudgetActual, active, inactive, relocated;
        public float offsetMax, activeStoredIrr, inactiveStoredIrr, giMeanY, giExpected, giError,
            mirrorSignal, mirrorSignalPixels, firstGiMeanY, firstMirrorSignal, ratio;
        public int giX, giY, giW, giH, mirX, mirY, mirW, mirH, targetWidth, targetHeight;
        public string frameHash;
        [NonSerialized] public int baseUpdates, baseBuilds, baseUploads, baseHistory;
    }

    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(),
            adapter = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion, error;
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed;
        public int width = Width, height = Height, sceneBuilds, sceneInstances, subscribers;
        public string sceneRequests;
        public bool giProducerAllocated;
        public float fov = Fov, camX = CameraX, camY = CameraY, camZ = CameraZ, receiverY = ReceiverY,
            receiverZ = ReceiverZ, mirrorX = MirrorX, mirrorY = MirrorY, mirrorZ = MirrorZ, mirrorHalf = MirrorHalf,
            donorX = DonorX, donorY = DonorY, donorZ = DonorZ, donorHalf = DonorHalf,
            emission = Emission, donorEmission = DonorEmission, wallAlbedo = WallAlbedo,
            expectedGi = ProbeY, giErrorMax = GiErrorMax, mirrorOn = MirrorOn, mirrorOff = MirrorOff;
        public float gridSpacing = 1f;
        public List<string> renderers = new List<string> { "Forward", "ForwardPlus", "Deferred" };
        public List<Stage> stages = new List<Stage>();
    }

    static int Width => currentWidth;
    static int Height => currentHeight;
    static int currentWidth = 960, currentHeight = 540;

    Report report;
    Camera view;
    RenderTexture target, secondTarget;
    Texture2D readback;
    Volume volume;
    ScreenSpaceGlobalIllumination ssgi;
    ScreenSpaceReflection ssr;
    Light probeLight;
    GameObject sceneRoot, donorObject, overlayObject;
    readonly List<MeshRenderer> emissiveRenderers = new List<MeshRenderer>();
    readonly List<GameObject> enclosures = new List<GameObject>();
    float tanV, tanH;
    int roiX, roiY, roiW, roiH, giX, giY, giW, giH, mirX, mirY, mirW, mirH;
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
        if (probeLight) { Destroy(probeLight.gameObject); probeLight = null; }

        foreach (var s in report.stages)
        {
            report.sceneBuilds = Mathf.Max(report.sceneBuilds, s.instances > 0 ? s.builds : 0);
            report.sceneInstances = Mathf.Max(report.sceneInstances, s.instances);
            report.subscribers = Mathf.Max(report.subscribers, s.subscribers);
            if (!string.IsNullOrEmpty(s.requests) && s.requests != "None") report.sceneRequests = s.requests;
            if (s.probeAllocated) report.giProducerAllocated = true;
        }

        if (report.api == "Direct3D11")
        {
            Gate(!report.supportsRayTracing, "D3D11 must not report DXR support");
            foreach (var s in report.stages)
            {
                Gate(!s.probeAllocated, $"{s.stage}/{s.renderer} allocated a GI producer on D3D11");
                Gate(!s.ssrQueue, $"{s.stage}/{s.renderer} prepared an SSR queue on D3D11");
                Gate(s.updates == 0, $"{s.stage}/{s.renderer} dispatched a probe batch on D3D11");
                Gate(s.instances == 0 && s.builds == 0 && s.subscribers == 0 && s.liveResources == 0,
                    $"{s.stage}/{s.renderer} allocated RT scene resources on D3D11");
            }
        }
    }

    IEnumerator Suite()
    {
        Check(report.development && Application.platform == RuntimePlatform.WindowsPlayer && IntPtr.Size == 8 &&
            QualitySettings.activeColorSpace == ColorSpace.Linear && emissive && emissiveOff && black &&
            receiverMaterial && mirrorMaterial && donorMaterial && pipeline && features.Length == 3 &&
            features[0] && features[1] && features[2], "Invalid Player configuration");

        int quality = Array.IndexOf(QualitySettings.names, "High Fidelity");
        Check(quality >= 0, "Missing High Fidelity quality level");
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        QualitySettings.SetQualityLevel(quality, true);
        QualitySettings.renderPipeline = pipeline;
        GraphicsSettings.defaultRenderPipeline = pipeline;
        yield return null;
        yield return null;
        Check(UniversalRenderPipeline.asset == pipeline, "I1 pipeline is not active");

        tanV = Mathf.Tan(Fov * Mathf.Deg2Rad * .5f);
        tanH = tanV * (Width / (float)Height);
        BuildVolume();
        yield return Warmup();

        for (int renderer = 0; renderer < 3; ++renderer)
        {
            BuildScene();
            yield return Warmup();
            yield return Matrix(renderer, "off", false, false, 0);
            yield return Matrix(renderer, "gi", true, false, 0);
            yield return Matrix(renderer, "ssr", false, true, Budget);
            yield return Matrix(renderer, "both", true, true, Budget);
            yield return BudgetToggle(renderer);
            yield return LightToggle(renderer);
            yield return Sweep(renderer);
            yield return RuntimeOff(renderer);
            yield return Cut(renderer);
            yield return Resolution(renderer);
            yield return Stack(renderer);
            yield return Dynamic(renderer);
            DestroyScene();
            yield return Warmup();
        }
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

        var go = new GameObject("I1 volume");
        volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 999;
        ssgi = volume.profile.Add<ScreenSpaceGlobalIllumination>();
        ssgi.enabled.Override(false);
        ssgi.intensity.Override(0f);
        ssgi.fullResolution.Override(true);
        ssgi.temporalAccumulation.Override(true);
        ssgi.denoise.Override(false);
        ssr = volume.profile.Add<ScreenSpaceReflection>();
        ssr.enabled.Override(false);
        ssr.fullResolution.Override(false);
        ssr.reflectSky.Override(false);
        ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
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

    void BuildScene()
    {
        currentWidth = 960; currentHeight = 540;
        tanV = Mathf.Tan(Fov * Mathf.Deg2Rad * .5f);
        tanH = tanV * (Width / (float)Height);
        sceneRoot = new GameObject("I1 scene");
        emissiveRenderers.Clear();
        Box("floor", new Vector3(0, -.1f, 0), new Vector3(8.2f, .2f, 8.2f), emissive, 1, true);
        Box("ceiling", new Vector3(0, 4.1f, 0), new Vector3(8.2f, .2f, 8.2f), emissive, 2, true);
        Box("wall-x-", new Vector3(-4.1f, 2, 0), new Vector3(.2f, 4.2f, 8.2f), emissive, 3, true);
        Box("wall-x+", new Vector3(4.1f, 2, 0), new Vector3(.2f, 4.2f, 8.2f), emissive, 4, true);
        Box("wall-z-", new Vector3(0, 2, -4.1f), new Vector3(8.2f, 4.2f, .2f), emissive, 5, true);
        Box("wall-z+", new Vector3(0, 2, 4.1f), new Vector3(8.2f, 4.2f, .2f), emissive, 6, true);
        // Enclosures on the receiver's front cell layer. Both live on the camera's culled layer, so
        // the receiver stays visible while the TLAS blacks out the four enclosed probes.
        Enclosure("enclosure-0", new Vector3(-.5f, 1.5f, 2.5f), 7);
        Enclosure("enclosure-1", new Vector3(.5f, 1.5f, 2.5f), 9);
        Enclosure("enclosure-2", new Vector3(-.5f, 2.5f, 2.5f), 11);
        Enclosure("enclosure-3", new Vector3(.5f, 2.5f, 2.5f), 13);
        Quad("receiver", new Vector3(ReceiverX, ReceiverY, ReceiverZ), new Vector2(2 * ReceiverHalf, 2 * ReceiverHalf),
            receiverMaterial);
        Quad("mirror", new Vector3(MirrorX, MirrorY, MirrorZ), new Vector2(2 * MirrorHalf, 2 * MirrorHalf),
            mirrorMaterial);
        donorObject = Box("donor", new Vector3(DonorX, DonorY, DonorZ), Vector3.one * (2 * DonorHalf),
            donorMaterial, 37, true);
        donorObject.GetComponent<MeshRenderer>().rayTracingMode = RayTracingMode.DynamicTransform;

        // T4 switch target: intensity zero so the analytic GI and the mirror stay unchanged, but the
        // scene light table still registers/uploads it and must react to enable/shadow changes.
        var lamp = new GameObject("I1 light");
        lamp.transform.SetParent(sceneRoot.transform, false);
        lamp.transform.position = new Vector3(0, 3.8f, 0);
        probeLight = lamp.AddComponent<Light>();
        probeLight.type = LightType.Point;
        probeLight.color = Color.white;
        probeLight.intensity = 0f;
        probeLight.range = 1f;
        probeLight.shadows = LightShadows.None;
    }

    void Enclosure(string name, Vector3 center, int id)
    {
        enclosures.Add(Box(name + " outer", center, Vector3.one * .9f, emissive, id, false));
        enclosures.Add(Box(name + " inner", center, Vector3.one * .7f, black, id + 1, false));
    }

    void DestroyScene()
    {
        if (sceneRoot) Destroy(sceneRoot);
        sceneRoot = null;
        emissiveRenderers.Clear();
        enclosures.Clear();
        donorObject = null;
        probeLight = null;
    }

    IEnumerator Warmup()
    {
        // A material asset reports one placeholder pass until its shader is used once; the RT gate
        // needs FindPass("GlxDXR") on the registered witness.
        var warm = new GameObject("I1 warm camera").AddComponent<Camera>();
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

    Vector2 Project(Vector3 cam, Vector3 world)
    {
        float depth = world.z - cam.z;
        float nx = (world.x - cam.x) / (tanH * depth);
        float ny = (world.y - cam.y) / (tanV * depth);
        return new Vector2((nx + 1) * .5f * Width, (ny + 1) * .5f * Height);
    }

    void SetRoi(Vector3 cam, Vector3 center, float halfX, float halfY,
        out int x, out int y, out int w, out int h)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int corner = 0; corner < 4; ++corner)
        {
            var p = Project(cam, center + new Vector3(
                (corner & 1) == 0 ? -halfX : halfX, (corner & 2) == 0 ? -halfY : halfY, 0));
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        }
        const int inset = 6;
        x = Mathf.Clamp(Mathf.CeilToInt(minX) + inset, 0, Width - 2);
        y = Mathf.Clamp(Mathf.CeilToInt(minY) + inset, 0, Height - 2);
        w = Mathf.Clamp(Mathf.FloorToInt(maxX) - inset - x, 1, Width - x);
        h = Mathf.Clamp(Mathf.FloorToInt(maxY) - inset - y, 1, Height - y);
    }

    void SetRois(Vector3 cam)
    {
        SetRoi(cam, new Vector3(ReceiverX, ReceiverY, ReceiverZ), ReceiverHalf, ReceiverHalf,
            out giX, out giY, out giW, out giH);
        SetRoi(cam, new Vector3(MirrorX, MirrorY, MirrorZ), MirrorHalf, MirrorHalf,
            out mirX, out mirY, out mirW, out mirH);
    }

    void EnsureTargets()
    {
        if (target == null || target.width != Width || target.height != Height)
        {
            if (target) { target.Release(); Destroy(target); }
            target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            target.Create();
        }
        if (readback == null || readback.width != giW || readback.height != giH)
        {
            if (readback) Destroy(readback);
            readback = new Texture2D(giW, giH, TextureFormat.RGBAHalf, false, true);
        }
    }

    Texture2D full;
    int lastNonBlack;

    Color[] ReadFull(out int nonBlack)
    {
        if (full == null || full.width != Width || full.height != Height)
        {
            if (full) Destroy(full);
            full = new Texture2D(Width, Height, TextureFormat.RGBAHalf, false, true);
        }
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        try { full.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); }
        finally { RenderTexture.active = previous; }
        full.Apply(false, false);
        var pixels = full.GetPixels();
        nonBlack = 0;
        foreach (var p in pixels) if (p.r > .1f || p.g > .1f || p.b > .1f) ++nonBlack;
        lastNonBlack = nonBlack;
        return pixels;
    }

    string HashFull()
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(full.GetRawTextureData());
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    float Mean(Color[] pixels, int x, int y, int w, int h)
    {
        double sum = 0;
        for (int row = 0; row < h; ++row)
            for (int column = 0; column < w; ++column)
                sum += Luminance(pixels[(y + row) * Width + x + column]);
        return (float)(sum / Mathf.Max(1, w * h));
    }

    void MirrorStats(Color[] pixels, out float maxGap, out int count)
    {
        maxGap = 0;
        count = 0;
        for (int row = 0; row < mirH; ++row)
            for (int column = 0; column < mirW; ++column)
            {
                var c = pixels[(mirY + row) * Width + mirX + column];
                float gap = c.g - c.r;
                if (gap > maxGap) maxGap = gap;
                if (gap > .5f) ++count;
            }
    }

    Camera NewCamera(int renderer, string label, Vector3 position, float yaw)
    {
        var camera = new GameObject("I1 camera " + label).AddComponent<Camera>();
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

    void Configure(int renderer, bool gi, bool ssrOn, int budget, bool feedback, bool relocation,
        bool classification)
    {
        var feature = features[renderer];
        feature.settings.rayTracingProbeFallback = gi;
        feature.settings.probeCoverage = 1f;
        feature.settings.probeGridOrigin = new Vector3(-3.5f, .5f, -3.5f);
        feature.settings.probeGridSpacing = 1f;
        feature.settings.probeGridCounts = new Vector3Int(8, 4, 8);
        feature.settings.probeBatchSize = 16;
        feature.settings.probeRaysPerProbe = 128;
        feature.settings.probeFeedback = feedback;
        feature.settings.probeRelocation = relocation;
        feature.settings.probeClassification = classification;
        feature.settings.rayTracingReflections = ssrOn;
        feature.settings.reflectionRayBudget = ssrOn ? budget : 0;
        // SSGI stays enabled with zero intensity in every stage: the screen-space pass must run so
        // its setup/cleanup pair keeps the probe/reflection keywords in a defined state even when
        // the RT extensions are off. Zero intensity keeps the image on the baked path.
        ssgi.enabled.Override(true);
        ssgi.intensity.Override(0f);
        ssr.enabled.Override(ssrOn);
    }

    IEnumerator ResetFeature(int renderer)
    {
        // Two frames with both switches off release the atlas and the scene request, so the next
        // stage starts from a genuinely cold producer instead of the previous stage's tiles.
        Configure(renderer, false, false, 0, false, true, true);
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

    Stage Begin(int renderer, string kind, int frames, bool gi, bool ssrOn, int budget,
        bool feedback, bool relocation, bool classification)
    {
        var feature = features[renderer];
        var stage = new Stage
        {
            stage = kind, api = report.api, guid = report.guid, renderer = renderer, frames = frames,
            gi = gi, ssr = ssrOn, ssrBudget = budget, feedback = feedback, relocation = relocation,
            classification = classification, supported = report.api == "Direct3D12"
        };
        stage.baseUpdates = feature.probeFallback?.updateCount ?? 0;
        stage.baseBuilds = feature.rayTracingScene?.buildCount ?? 0;
        stage.baseUploads = feature.rayTracingScene?.lightUploadCount ?? 0;
        stage.baseHistory = feature.historyStore?.entryCount ?? 0;
        return stage;
    }

    void Capture(Stage stage, bool accumulateMirror = false)
    {
        var pixels = ReadFull(out int nonBlack);
        stage.nonBlack = nonBlack;
        stage.frameHash = HashFull();
        stage.giMeanY = Mean(pixels, giX, giY, giW, giH);
        stage.giExpected = ProbeY;
        stage.giError = Mathf.Abs(stage.giMeanY - ProbeY) / ProbeY;
        MirrorStats(pixels, out float gap, out int count);
        if (accumulateMirror)
        {
            stage.mirrorSignal = Mathf.Max(stage.mirrorSignal, gap);
            stage.mirrorSignalPixels = Mathf.Max(stage.mirrorSignalPixels, count);
        }
        else
        {
            stage.mirrorSignal = gap;
            stage.mirrorSignalPixels = count;
        }
        stage.giX = giX; stage.giY = giY; stage.giW = giW; stage.giH = giH;
        stage.mirX = mirX; stage.mirY = mirY; stage.mirW = mirW; stage.mirH = mirH;
        stage.targetWidth = Width; stage.targetHeight = Height;
    }

    void End(Stage stage, bool keepCamera = false)
    {
        var feature = features[stage.renderer];
        var scene = feature.rayTracingScene;
        var fallback = feature.probeFallback;
        var queue = feature.ssrPass?.requestQueue;
        stage.instances = scene?.instanceCount ?? 0;
        stage.builds = (scene?.buildCount ?? 0) - stage.baseBuilds;
        stage.subscribers = scene?.subscribers ?? 0;
        stage.liveResources = scene?.liveResources ?? 0;
        stage.requests = scene?.requests.ToString() ?? "None";
        stage.lightUploads = (scene?.lightUploadCount ?? 0) - stage.baseUploads;
        stage.lightRegistered = scene?.registeredLights ?? 0;
        stage.lightUploaded = scene?.uploadedLights ?? 0;
        stage.updates = (fallback?.updateCount ?? 0) - stage.baseUpdates;
        stage.generation = fallback?.generation ?? 0;
        stage.producerBytes = fallback?.producerBytes ?? 0;
        stage.probeAllocated = fallback?.producerAllocated ?? false;
        stage.ssrQueue = queue != null;
        stage.ssrBudgetActual = queue?.budget ?? 0;
        stage.ssrProduced = feature.ssrPass != null && feature.ssrPass.producedOutput;
        stage.historyCount = feature.historyStore?.entryCount ?? 0;
        stage.historyOk = view != null && feature.historyStore != null &&
            feature.historyStore.TryGet(view, 0, out _);
        stage.gateGi = GateString(feature, RayTracingRequests.GI);
        stage.gateSsr = GateString(feature, RayTracingRequests.Reflections);
        stage.configMatched = fallback != null &&
            fallback.config.feedback == stage.feedback && fallback.config.relocation == stage.relocation &&
            fallback.config.classification == stage.classification;
        if (fallback != null && fallback.producerAllocated)
        {
            var data = fallback.ReadProbeData();
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
            stage.activeStoredIrr = StoredIrr(fallback, BackProbes[0]);
            stage.inactiveStoredIrr = StoredIrr(fallback, EnclosedProbes[0]);
        }
        report.stages.Add(stage);
        if (view && !keepCamera) { view.targetTexture = null; Destroy(view.gameObject); view = null; }
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

    string GateString(ScreenSpaceLightingRendererFeature feature, RayTracingRequests request)
    {
        var shader = request == RayTracingRequests.GI
            ? feature.settings.resources.rayTracingGi
            : feature.settings.resources.rayTracingSsr;
        return RayTracingSceneGate.Capture(new CameraData
        {
            camera = view, cameraType = CameraType.Game, renderType = CameraRenderType.Base,
            cameraTargetDescriptor = new RenderTextureDescriptor(Width, Height)
        }, true, true, shader, donorMaterial).Evaluate(request).ToString();
    }

    IEnumerator Render(int frames)
    {
        for (int i = 0; i < frames; ++i) yield return new WaitForEndOfFrame();
    }

    // ---------------------------------------------------------------------------------------------
    // Matrix and toggle stages
    // ---------------------------------------------------------------------------------------------

    IEnumerator Plain(int renderer, string kind, bool gi, bool ssrOn, int budget,
        bool feedback, bool relocation, bool classification, int frames)
    {
        Configure(renderer, gi, ssrOn, budget, feedback, relocation, classification);
        var pose = new Vector3(CameraX, CameraY, CameraZ);
        SetRois(pose);
        var stage = Begin(renderer, kind, frames, gi, ssrOn, budget, feedback, relocation, classification);
        EnsureTargets();
        view = NewCamera(renderer, kind, pose, 0f);
        view.targetTexture = target;
        yield return Render(frames);
        if (!ssrOn)
        {
            Capture(stage);
        }
        else
        {
            // The fixed-K hardware dispatch serves a rotating subset of the mirror pixels per
            // frame; keep the best of four frames so an off-screen donor is not reported missing
            // just because this frame's phase skipped its tiles.
            Capture(stage);
            for (int extra = 0; extra < 3; ++extra)
            {
                yield return new WaitForEndOfFrame();
                Capture(stage, true);
            }
        }
        End(stage);
    }

    IEnumerator Matrix(int renderer, string kind, bool gi, bool ssrOn, int budget)
    {
        // Always cold: the warmup camera may have left a producer behind for renderer 0.
        yield return ResetFeature(renderer);
        yield return Plain(renderer, kind, gi, ssrOn, budget, false, true, true, SettleFrames);
    }

    IEnumerator BudgetToggle(int renderer)
    {
        yield return ResetFeature(renderer);
        yield return Plain(renderer, "t3bOn", true, true, Budget, false, true, true, SettleFrames);
        yield return Plain(renderer, "t3bOff", true, true, 0, false, true, true, ShortFrames * 2);
        yield return Plain(renderer, "t3bOn2", true, true, Budget, false, true, true, ShortFrames * 2);
    }

    IEnumerator LightToggle(int renderer)
    {
        yield return ResetFeature(renderer);
        yield return Plain(renderer, "t4Base", true, true, Budget, false, true, true, SettleFrames);
        probeLight.shadows = LightShadows.Hard;
        yield return Plain(renderer, "t4ShadowOn", true, true, Budget, false, true, true, ShortFrames);
        probeLight.enabled = false;
        yield return Plain(renderer, "t4LightOff", true, true, Budget, false, true, true, ShortFrames);
        probeLight.enabled = true;
        probeLight.shadows = LightShadows.None;
        yield return Plain(renderer, "t4LightOn", true, true, Budget, false, true, true, ShortFrames);
    }

    IEnumerator Sweep(int renderer)
    {
        // No reset between combos: the producer must notice each settings change and replace itself
        // (ba73060), not silently keep the config it captured at construction.
        yield return ResetFeature(renderer);
        for (int combo = 0; combo < 8; ++combo)
        {
            bool feedback = (combo & 4) != 0, relocation = (combo & 2) != 0, classification = (combo & 1) != 0;
            yield return Plain(renderer, "sweep" + combo, true, false, 0, feedback, relocation,
                classification, SweepFrames);
        }
    }

    IEnumerator RuntimeOff(int renderer)
    {
        yield return ResetFeature(renderer);
        yield return Plain(renderer, "runtimeOn", true, true, Budget, false, true, true, SettleFrames);
        yield return Plain(renderer, "runtimeOff", false, false, 0, false, true, true, SettleFrames);
    }

    // ---------------------------------------------------------------------------------------------
    // Camera and resource isolation stages
    // ---------------------------------------------------------------------------------------------

    IEnumerator Cut(int renderer)
    {
        yield return ResetFeature(renderer);
        Configure(renderer, true, true, Budget, false, true, true);
        var poseA = new Vector3(CameraX, CameraY, CameraZ);
        SetRois(poseA);
        var settle = Begin(renderer, "cutSettle", SettleFrames, true, true, Budget, false, true, true);
        EnsureTargets();
        view = NewCamera(renderer, "cut", poseA, 0f);
        view.targetTexture = target;
        yield return Render(SettleFrames);
        Capture(settle);
        End(settle, true);

        // Same camera object, translated: the screen-space history must not blend the old pose.
        var poseB = poseA + new Vector3(.6f, .2f, .7f);
        view.transform.position = poseB;
        SetRois(poseB);
        EnsureTargets();
        var stage = Begin(renderer, "cut", CutFrames + 1, true, true, Budget, false, true, true);
        yield return new WaitForEndOfFrame();
        var first = ReadFull(out _);
        stage.firstGiMeanY = Mean(first, giX, giY, giW, giH);
        MirrorStats(first, out float firstSignal, out _);
        stage.firstMirrorSignal = firstSignal;
        yield return Render(CutFrames);
        Capture(stage);
        for (int extra = 0; extra < 3; ++extra)
        {
            yield return new WaitForEndOfFrame();
            Capture(stage, true);
        }
        End(stage);
        if (!stage.supported) yield break;
        Gate(stage.builds == 0, $"cut/{renderer} rebuilt the TLAS on a camera move");
        Gate(stage.updates <= stage.frames + 4, $"cut/{renderer} dispatched more than one batch per frame");
        Gate(Mathf.Abs(stage.firstGiMeanY - stage.giMeanY) <= GiErrorMax * stage.giMeanY,
            $"cut/{renderer} first frame {stage.firstGiMeanY:0.###} vs {stage.giMeanY:0.###}");
        Gate(stage.firstMirrorSignal >= .5f * stage.mirrorSignal,
            $"cut/{renderer} lost the reflection on the first frame ({stage.firstMirrorSignal:0.###})");
    }

    IEnumerator Resolution(int renderer)
    {
        yield return ResetFeature(renderer);
        Configure(renderer, true, true, Budget, false, true, true);
        var pose = new Vector3(CameraX, CameraY, CameraZ);
        SetRois(pose);
        var big = Begin(renderer, "resBig", SettleFrames, true, true, Budget, false, true, true);
        EnsureTargets();
        view = NewCamera(renderer, "res", pose, 0f);
        view.targetTexture = target;
        yield return Render(SettleFrames);
        Capture(big);
        End(big, true);
        int instances = features[renderer].rayTracingScene?.instanceCount ?? 0;

        currentWidth = 640; currentHeight = 360;
        tanV = Mathf.Tan(Fov * Mathf.Deg2Rad * .5f);
        tanH = tanV * (Width / (float)Height);
        SetRois(pose);
        var small = Begin(renderer, "resSmall", ResFrames, true, true, Budget, false, true, true);
        EnsureTargets();
        view.targetTexture = target;
        yield return Render(ResFrames);
        Capture(small);
        End(small, true);
        if (small.supported)
        {
            Gate(small.builds == 0, $"resSmall/{renderer} rebuilt the TLAS");
            Gate(small.instances == instances, $"resSmall/{renderer} changed the instance count");
            Gate(small.liveResources == 1, $"resSmall/{renderer} dropped the shared scene");
            Gate(small.historyCount == 1, $"resSmall/{renderer} kept {small.historyCount} histories");
            Gate(small.giError <= GiErrorMax, $"resSmall/{renderer} GI moved ({small.giMeanY:0.###})");
            Gate(small.targetWidth == 640 && small.targetHeight == 360, "resSmall target size");
        }

        currentWidth = 960; currentHeight = 540;
        tanV = Mathf.Tan(Fov * Mathf.Deg2Rad * .5f);
        tanH = tanV * (Width / (float)Height);
        SetRois(pose);
        var back = Begin(renderer, "resBack", ResFrames, true, true, Budget, false, true, true);
        EnsureTargets();
        view.targetTexture = target;
        yield return Render(ResFrames);
        Capture(back);
        End(back);
        if (back.supported) Gate(back.giError <= GiErrorMax, $"resBack/{renderer} GI moved ({back.giMeanY:0.###})");
    }

    IEnumerator Stack(int renderer)
    {
        yield return ResetFeature(renderer);
        Configure(renderer, true, true, Budget, false, true, true);
        var pose = new Vector3(CameraX, CameraY, CameraZ);
        SetRois(pose);
        var single = Begin(renderer, "stackBase", SettleFrames, true, true, Budget, false, true, true);
        EnsureTargets();
        view = NewCamera(renderer, "stackBase", pose, 0f);
        view.targetTexture = target;
        yield return Render(SettleFrames);
        Capture(single);
        for (int extra = 0; extra < 3; ++extra)
        {
            yield return new WaitForEndOfFrame();
            Capture(single, true);
        }
        End(single, true);
        float singleSignal = single.mirrorSignal;

        var overlay = new GameObject("I1 overlay camera").AddComponent<Camera>();
        overlay.clearFlags = CameraClearFlags.Nothing;
        overlay.cullingMask = 0;
        overlay.fieldOfView = Fov;
        overlay.nearClipPlane = .1f;
        overlay.farClipPlane = 50f;
        overlay.allowMSAA = false;
        overlay.allowHDR = true;
        overlay.transform.SetPositionAndRotation(pose, Quaternion.identity);
        var overlayData = overlay.GetUniversalAdditionalCameraData();
        overlayData.renderType = CameraRenderType.Overlay;
        overlayData.renderPostProcessing = false;
        overlayData.antialiasing = AntialiasingMode.None;
        overlayData.SetRenderer(renderer);
        var baseData = view.GetUniversalAdditionalCameraData();
        baseData.cameraStack.Add(overlay);

        int lastUpdates = features[renderer].probeFallback?.updateCount ?? 0;
        var stage = Begin(renderer, "stack", StackFrames, true, true, Budget, false, true, true);
        for (int frame = 0; frame < StackFrames; ++frame)
        {
            yield return new WaitForEndOfFrame();
            if (!stage.supported) continue;
            int updates = features[renderer].probeFallback?.updateCount ?? 0;
            Gate(updates - lastUpdates <= 1, $"stack/{renderer} dispatched more than one batch in frame {frame}");
            lastUpdates = updates;
        }
        Capture(stage);
        for (int extra = 0; extra < 3; ++extra)
        {
            yield return new WaitForEndOfFrame();
            Capture(stage, true);
        }
        End(stage, true);
        if (stage.supported)
        {
            Gate(stage.updates <= stage.frames + 4, $"stack/{renderer} duplicated the probe batch");
            Gate(stage.subscribers == 2 && stage.requests == "GI, Reflections", $"stack/{renderer} subscriptions changed");
            Gate(stage.historyOk, $"stack/{renderer} lost the base camera history");
            Gate(stage.historyCount >= 1 && stage.historyCount <= 2,
                $"stack/{renderer} kept {stage.historyCount} camera histories");
            Gate(stage.giError <= GiErrorMax, $"stack/{renderer} GI moved ({stage.giMeanY:0.###})");
            Gate(stage.mirrorSignal >= .5f * singleSignal,
                $"stack/{renderer} reflection changed ({stage.mirrorSignal:0.###} vs {singleSignal:0.###})");
        }
        baseData.cameraStack.Remove(overlay);
        overlay.targetTexture = null;
        Destroy(overlay.gameObject);
        if (view) { view.targetTexture = null; Destroy(view.gameObject); view = null; }
    }

    IEnumerator Dynamic(int renderer)
    {
        yield return ResetFeature(renderer);
        Configure(renderer, true, true, Budget, false, true, true);
        var pose = new Vector3(CameraX, CameraY, CameraZ);
        SetRois(pose);
        var before = Begin(renderer, "dynamicBefore", SettleFrames, true, true, Budget, false, true, true);
        EnsureTargets();
        view = NewCamera(renderer, "dynamic", pose, 0f);
        view.targetTexture = target;
        yield return Render(SettleFrames);
        Capture(before);
        for (int extra = 0; extra < 3; ++extra)
        {
            yield return new WaitForEndOfFrame();
            Capture(before, true);
        }
        End(before, true);

        donorObject.transform.position = new Vector3(-DonorX, DonorY, DonorZ);
        var moved = Begin(renderer, "dynamicMoved", DynamicFrames, true, true, Budget, false, true, true);
        yield return Render(DynamicFrames);
        Capture(moved);
        for (int extra = 0; extra < 3; ++extra)
        {
            yield return new WaitForEndOfFrame();
            Capture(moved, true);
        }
        End(moved, true);
        if (moved.supported)
        {
            Gate(moved.instances == before.instances, $"dynamic/{renderer} changed the instance count");
            Gate(moved.liveResources == 1 && moved.subscribers == 2, $"dynamic/{renderer} lost the shared scene");
            Gate(moved.mirrorSignal <= MirrorOff,
                $"dynamic/{renderer} kept a ghost reflection ({moved.mirrorSignal:0.###})");
            Gate(moved.updates <= moved.frames + 4, $"dynamic/{renderer} duplicated the probe batch");
        }

        donorObject.transform.position = new Vector3(DonorX, DonorY, DonorZ);
        var restored = Begin(renderer, "dynamicRestored", DynamicFrames, true, true, Budget, false, true, true);
        yield return Render(DynamicFrames);
        Capture(restored);
        for (int extra = 0; extra < 3; ++extra)
        {
            yield return new WaitForEndOfFrame();
            Capture(restored, true);
        }
        End(restored);
        if (restored.supported)
            Gate(restored.mirrorSignal >= MirrorOn,
                $"dynamic/{renderer} did not recover the reflection ({restored.mirrorSignal:0.###})");
    }

    // ---------------------------------------------------------------------------------------------
    // Evaluation
    // ---------------------------------------------------------------------------------------------

    Stage Find(string kind, int renderer)
    {
        foreach (var stage in report.stages)
            if (stage.stage == kind && stage.renderer == renderer) return stage;
        return null;
    }

    static bool Near(float value, float expected, float tolerance) =>
        Mathf.Abs(value - expected) <= tolerance * Mathf.Abs(expected);

    void Evaluate()
    {
        for (int renderer = 0; renderer < 3; ++renderer)
        {
            var off = Find("off", renderer);
            var gi = Find("gi", renderer);
            var ssr = Find("ssr", renderer);
            var both = Find("both", renderer);
            var runtimeOff = Find("runtimeOff", renderer);
            if (off == null || gi == null || ssr == null || both == null || runtimeOff == null)
            {
                Gate(false, $"missing matrix stage for renderer {renderer}");
                continue;
            }
            if (report.api == "Direct3D11")
            {
                Gate(off.frameHash == gi.frameHash, $"D3D11/gi/{renderer} changed the image");
                Gate(ssr.frameHash == both.frameHash, $"D3D11/both/{renderer} changed the image");
                Gate(runtimeOff.frameHash == off.frameHash, $"D3D11/runtimeOff/{renderer} left a residue");
                foreach (var stage in report.stages)
                    if (stage.renderer == renderer &&
                        (stage.gateGi != "Backend" || stage.gateSsr != "Backend"))
                        Gate(false, $"D3D11/{stage.stage}/{renderer} gate {stage.gateGi}/{stage.gateSsr}");
                continue;
            }

            // Isolation: one consumer never prepares or dispatches the other.
            Gate(off.instances == 0 && off.builds == 0 && off.subscribers == 0 && off.liveResources == 0,
                $"off/{renderer} allocated the RT scene");
            Gate(off.updates == 0 && !off.probeAllocated && !off.ssrQueue,
                $"off/{renderer} kept a consumer");
            Gate(off.requests == "None", $"off/{renderer} requests {off.requests}");

            Gate(gi.updates > 0 && gi.probeAllocated && gi.producerBytes > 900000,
                $"gi/{renderer} did not run the probe producer");
            Gate(!gi.ssrQueue && gi.subscribers == 1 && gi.requests == "GI",
                $"gi/{renderer} subscribed beyond GI ({gi.requests}, queue {gi.ssrQueue})");
            Gate(gi.instances > 0 && gi.builds >= 1 && gi.liveResources == 1, $"gi/{renderer} RT scene");
            Gate(Near(gi.giMeanY, ProbeY, GiErrorMax), $"gi/{renderer} GI {gi.giMeanY:0.###} vs {ProbeY}");
            Gate(gi.mirrorSignal <= MirrorOff, $"gi/{renderer} mirror is not black ({gi.mirrorSignal:0.###})");

            Gate(ssr.updates == 0 && !ssr.probeAllocated && ssr.producerBytes == 0,
                $"ssr/{renderer} ran the probe producer");
            Gate(ssr.ssrQueue && ssr.ssrBudgetActual == Budget, $"ssr/{renderer} has no SSR queue");
            Gate(ssr.subscribers == 1 && ssr.requests == "Reflections",
                $"ssr/{renderer} subscribed beyond Reflections ({ssr.requests})");
            Gate(ssr.instances == gi.instances && ssr.builds >= 1, $"ssr/{renderer} RT scene");
            Gate(ssr.giMeanY <= .1f, $"ssr/{renderer} lit the receiver without GI ({ssr.giMeanY:0.###})");
            Gate(ssr.mirrorSignal >= MirrorOn, $"ssr/{renderer} has no reflected donor ({ssr.mirrorSignal:0.###})");

            // Both: one shared scene and one shared light-table version.
            Gate(both.instances == gi.instances && both.liveResources == 1, $"both/{renderer} scene not shared");
            Gate(both.builds == 1, $"both/{renderer} built the shared TLAS {both.builds} times");
            Gate(both.updates > 0 && both.ssrQueue, $"both/{renderer} lost a consumer");
            Gate(both.subscribers == 2 && both.requests == "GI, Reflections", $"both/{renderer} requests {both.requests}");
            Gate(both.lightRegistered == 1 && both.lightUploaded == 1 && both.lightUploads <= 2,
                $"both/{renderer} light table {both.lightRegistered}/{both.lightUploaded}/{both.lightUploads}");
            Gate(Near(both.giMeanY, ProbeY, GiErrorMax), $"both/{renderer} GI {both.giMeanY:0.###}");
            Gate(both.mirrorSignal >= MirrorOn, $"both/{renderer} mirror {both.mirrorSignal:0.###}");
            Gate(both.frameHash != off.frameHash && gi.frameHash != ssr.frameHash &&
                both.frameHash != gi.frameHash && both.frameHash != ssr.frameHash,
                $"both/{renderer} configs are not distinguishable");

            // T3b ray budget switch.
            var on = Find("t3bOn", renderer);
            var budgetOff = Find("t3bOff", renderer);
            var onAgain = Find("t3bOn2", renderer);
            if (on != null && budgetOff != null && onAgain != null)
            {
                Gate(on.ssrQueue && on.mirrorSignal >= MirrorOn && on.requests == "GI, Reflections", $"t3bOn/{renderer}");
                Gate(!budgetOff.ssrQueue && budgetOff.requests == "GI" && budgetOff.updates > 0,
                    $"t3bOff/{renderer} did not drop only Reflections ({budgetOff.requests})");
                Gate(budgetOff.mirrorSignal <= MirrorOff,
                    $"t3bOff/{renderer} kept the hardware reflection ({budgetOff.mirrorSignal:0.###})");
                Gate(onAgain.ssrQueue && onAgain.mirrorSignal >= MirrorOn && onAgain.requests == "GI, Reflections",
                    $"t3bOn2/{renderer} did not resume");
            }

            // T4 light enable/shadow switch on the shared table.
            var t4Base = Find("t4Base", renderer);
            var shadow = Find("t4ShadowOn", renderer);
            var lightOff = Find("t4LightOff", renderer);
            var lightOn = Find("t4LightOn", renderer);
            if (t4Base != null && shadow != null && lightOff != null && lightOn != null)
            {
                Gate(t4Base.lightRegistered == 1 && t4Base.lightUploaded == 1, $"t4Base/{renderer} light");
                Gate(shadow.lightUploads >= 1 && shadow.builds == 0 && shadow.requests == "GI, Reflections" &&
                    shadow.lightRegistered == 1 && shadow.lightUploaded == 1, $"t4ShadowOn/{renderer} shared table");
                Gate(lightOff.lightUploaded == 0 && lightOff.lightUploads >= 1 && lightOff.builds == 0 &&
                    lightOff.subscribers == 2, $"t4LightOff/{renderer}");
                Gate(lightOn.lightUploaded == 1 && lightOn.lightUploads >= 1 && lightOn.builds == 0 &&
                    lightOn.requests == "GI, Reflections", $"t4LightOn/{renderer}");
            }

            // Maintenance sweep: each combo reaches a replaced producer and keeps the other
            // consumer off; classification and relocation effects are visible.
            Stage darkOn = null, darkOff = null;
            for (int combo = 0; combo < 8; ++combo)
            {
                var stage = Find("sweep" + combo, renderer);
                if (stage == null) { Gate(false, $"sweep{combo}/{renderer} missing"); continue; }
                Gate(stage.configMatched, $"sweep{combo}/{renderer} ignored a toggle");
                Gate(Mathf.Abs(stage.generation - stage.frames) <= 2,
                    $"sweep{combo}/{renderer} generation {stage.generation} for {stage.frames} frames");
                Gate(!stage.ssrQueue && stage.requests == "GI" && stage.subscribers == 1,
                    $"sweep{combo}/{renderer} left SSR subscribed");
                // With relocation on, a probe pushed out of the enclosure may legitimately become
                // active again, so the inactive count is only a gate when relocation is off.
                if (!stage.classification)
                    Gate(stage.inactive == 0, $"sweep{combo}/{renderer} inactive {stage.inactive}");
                else if (!stage.relocation)
                    Gate(stage.inactive > 0, $"sweep{combo}/{renderer} inactive {stage.inactive}");
                Gate(stage.relocation ? stage.relocated > 0 : stage.relocated == 0,
                    $"sweep{combo}/{renderer} relocated {stage.relocated}");
                if (stage.relocation && stage.relocated > 0)
                    Gate(stage.offsetMax <= .45f + 1e-3f,
                        $"sweep{combo}/{renderer} offset {stage.offsetMax:0.###}");
                if (stage.classification && !stage.relocation && !stage.feedback) darkOn = stage;
                if (!stage.classification && !stage.relocation && !stage.feedback) darkOff = stage;
            }
            if (darkOn != null && darkOff != null)
                Gate(darkOn.giMeanY >= 1.2f * darkOff.giMeanY,
                    $"sweep/{renderer} classification did not lift the image ({darkOn.giMeanY:0.###} vs {darkOff.giMeanY:0.###})");

            // Runtime shutdown and camera/resource isolation.
            Gate(runtimeOff.liveResources == 0 && runtimeOff.subscribers == 0 && runtimeOff.requests == "None",
                $"runtimeOff/{renderer} kept the scene");
            Gate(runtimeOff.instances == 0 && runtimeOff.generation == 0 && runtimeOff.producerBytes == 0 &&
                !runtimeOff.probeAllocated && !runtimeOff.ssrQueue, $"runtimeOff/{renderer} kept a consumer");
            Gate(runtimeOff.frameHash == off.frameHash, $"runtimeOff/{renderer} did not return to the off image");

            var cut = Find("cut", renderer);
            var resSmall = Find("resSmall", renderer);
            var stack = Find("stack", renderer);
            var dynBefore = Find("dynamicBefore", renderer);
            var dynMoved = Find("dynamicMoved", renderer);
            var dynRestored = Find("dynamicRestored", renderer);
            if (cut != null) Gate(cut.giError <= GiErrorMax, $"cut/{renderer} settled GI");
            if (resSmall != null) Gate(resSmall.giError <= GiErrorMax, $"resSmall/{renderer} settled GI");
            if (stack != null) Gate(stack.giError <= GiErrorMax, $"stack/{renderer} settled GI");
            if (dynBefore != null && dynMoved != null && dynRestored != null &&
                dynBefore.mirrorSignal < MirrorOn) Gate(false, $"dynamic/{renderer} donor never reflected");
        }

        foreach (var stage in report.stages)
        {
            if (report.api != "Direct3D12") continue;
            Gate(stage.gateGi == "Accepted" && stage.gateSsr == "Accepted",
                $"{stage.stage}/{stage.renderer} gate {stage.gateGi}/{stage.gateSsr}");
        }
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
        const string folder = "Assets/Fixture/I1";
        if (!AssetDatabase.IsValidFolder(folder))
        {
            if (!AssetDatabase.IsValidFolder("Assets/Fixture")) AssetDatabase.CreateFolder("Assets", "Fixture");
            AssetDatabase.CreateFolder("Assets/Fixture", "I1");
        }

        var lit = Shader.Find("Universal Render Pipeline/Lit");

        Material Mat(string name, Color baseColor, float metallic, float smoothness, Color emission)
        {
            var path = $"{folder}/{name}.mat";
            AssetDatabase.DeleteAsset(path);
            var material = new Material(lit) { name = name };
            AssetDatabase.CreateAsset(material, path);
            material.SetColor("_BaseColor", baseColor);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            material.SetColor("_EmissionColor", emission);
            if (emission.maxColorComponent > 0) material.EnableKeyword("_EMISSION");
            EditorUtility.SetDirty(material);
            return material;
        }

        var emissive = Mat("emissive", WallColor, 0f, .2f, new Color(Emission, Emission, Emission));
        var emissiveOff = Mat("emissiveOff", WallColor, 0f, .2f, Color.black);
        var black = Mat("black", Color.black, 0f, .2f, Color.black);
        var receiver = Mat("receiver", Color.white, 0f, .2f, Color.black);
        var mirror = Mat("mirror", Color.white, 1f, 1f, Color.black);
        var donor = Mat("donor", Color.black, 1f, 0f, new Color(0f, DonorEmission, 0f));

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
            renderer.name = mode + " I1 Renderer";
            AssetDatabase.CreateAsset(renderer, path);
            foreach (var existing in AssetDatabase.LoadAllAssetsAtPath(path))
                if (existing is ScriptableRendererFeature) AssetDatabase.RemoveObjectFromAsset(existing);
            renderer.rendererFeatures.Clear();
            renderer.renderingMode = mode;
            var feature = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
            feature.name = mode + " Screen Space Lighting";
            feature.Create();
            feature.settings.rayTracingProbeFallback = true;
            feature.settings.rayTracingReflections = true;
            feature.settings.reflectionRayBudget = Budget;
            feature.settings.EnsureResources();
            Check(feature.settings.resources.rayTracingGi != null, "Missing GI raygen asset");
            Check(feature.settings.resources.giProbeUpdateCompute != null, "Missing probe update compute");
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
        var pipelinePath = folder + "/I1Pipeline.asset";
        AssetDatabase.DeleteAsset(pipelinePath);
        var pipeline = UnityEngine.Object.Instantiate(templatePipeline);
        pipeline.name = "I1 Pipeline";
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
        var driver = new GameObject("I1 fixture").AddComponent<I1Player>();
        driver.emissive = emissive;
        driver.emissiveOff = emissiveOff;
        driver.black = black;
        driver.receiverMaterial = receiver;
        driver.mirrorMaterial = mirror;
        driver.donorMaterial = donor;
        driver.features = features;
        driver.pipeline = pipeline;
        Check(EditorSceneManager.SaveScene(scene, "Assets/I1.unity"), "Cannot save I1 scene");

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
                scenes = new[] { "Assets/I1.unity" }, target = target,
                locationPathName = Arg("-exe"), options = BuildOptions.Development
            });
            Check(build.summary.result == BuildResult.Succeeded, "Win64 I1 build failed");
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
