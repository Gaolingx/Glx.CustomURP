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

// G3a: the dynamic probe fallback consumed by the real Lit material through the Screen Space
// Lighting feature. A sealed, constant-radiance room makes Pgi = L analytic: probes see emission L in
// every direction, E = pi*L, the receiver (albedo 0.5) gets 0.5*L in the diffuse term. The receiver
// mesh is not registered in the TLAS so it never occludes a probe ray.
public sealed class G3Player : MonoBehaviour
{
    public const int Width = 960, Height = 540;
    const float Fov = 60f, CamY = 1.6f, CamZ = -2.5f;
    const float ReceiverZ = 1f, ReceiverHalf = 1f, ReceiverAlbedo = 1f;
    const float Emission = 2f;
    // White dielectric base: EnvironmentBRDF multiplies Pgi by (1 - 0.04) and no direct light or
    // specular probe survives, so the analytic diffuse term is 0.96 * E/pi = 0.96 * L.
    const float OneMinusReflectivity = .96f, ExpectedY = OneMinusReflectivity * Emission;
    const float GiErrorMax = .10f, AbsoluteFloor = .02f;
    const int WarmSegments = 40, BothSegments = 24, ShortSegments = 4;

    public Material emissive, receiverMaterial;
    public ScreenSpaceLightingRendererFeature[] features = new ScreenSpaceLightingRendererFeature[3];
    public UniversalRenderPipelineAsset pipeline;

    [Serializable] class Stage
    {
        public string stage, api, guid, gate, requests, frameHash, notes;
        public int renderer, frames, coverage, ssgiIntensity, instances, builds, subscribers, updates, generation;
        public long producerBytes;
        public bool producerAllocated, ssgiProbeFlag, probeSnapshotReady, supported;
        public float roiMeanR, roiMeanG, roiMeanB, roiMeanY, expectedY, relativeError, halfScaleMax;
        public int roiX, roiY, roiW, roiH, nonBlack;
    }

    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(),
            adapter = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion, error;
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed;
        public int width = Width, height = Height, sceneBuilds, sceneInstances, subscribers;
        public string sceneRequests;
        public float fov = Fov, camY = CamY, camZ = CamZ, receiverZ = ReceiverZ, receiverAlbedo = ReceiverAlbedo,
            emission = Emission, expectedY = ExpectedY, giErrorMax = GiErrorMax;
        public float gridSpacing = 1f;
        public List<string> renderers = new List<string> { "Forward", "ForwardPlus", "Deferred" };
        public List<string> knownDifferences = new List<string>();
        public List<Stage> stages = new List<Stage>();
    }

    Report report;
    Camera view;
    RenderTexture target, secondTarget;
    Texture2D readback;
    Volume volume;
    ScreenSpaceGlobalIllumination ssgi;
    GameObject receiver, secondCameraObject;
    float tanV, tanH;
    int roiX, roiY, roiW, roiH;
    Color[] probePixels;
    float[] probeScaleError;
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
                Gate(s.producerAllocated == false, $"{s.stage}/{s.renderer} allocated a producer on D3D11");
                Gate(s.updates == 0, $"{s.stage}/{s.renderer} dispatched a probe batch on D3D11");
            }
        }
    }

    IEnumerator Suite()
    {
        Check(report.development && Application.platform == RuntimePlatform.WindowsPlayer && IntPtr.Size == 8 &&
            QualitySettings.activeColorSpace == ColorSpace.Linear && emissive && receiverMaterial &&
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
        Check(UniversalRenderPipeline.asset == pipeline, "G3 pipeline is not active");

        tanV = Mathf.Tan(Fov * Mathf.Deg2Rad * .5f);
        tanH = tanV * (Width / (float)Height);
        BuildScene();
        BuildVolume();
        BuildRoi();
        yield return Warmup();

        for (int renderer = 0; renderer < 3; ++renderer)
        {
            yield return Off(renderer);
            yield return Cold(renderer);
            yield return Probe(renderer);
            yield return Half(renderer);
            yield return Both(renderer);
            yield return Outside(renderer);
            yield return OffAgain(renderer);
        }
        Evaluate();
    }

    // ---------------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------------

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
        go.transform.position = position;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        return go;
    }

    GameObject Wall(string name, Vector3 position, Vector3 size, int id)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.position = position;
        go.transform.localScale = size;
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = emissive;
        renderer.rayTracingMode = RayTracingMode.Static;
        var geometry = go.AddComponent<SsrRayTracingGeometry>();
        geometry.instanceId = id;
        geometry.enableTriangleCulling = false;
        return go;
    }

    void BuildScene()
    {
        Wall("floor", new Vector3(0, -.1f, 0), new Vector3(8.2f, .2f, 8.2f), 1);
        Wall("ceiling", new Vector3(0, 4.1f, 0), new Vector3(8.2f, .2f, 8.2f), 2);
        Wall("wall-x-", new Vector3(-4.1f, 2, 0), new Vector3(.2f, 4.2f, 8.2f), 3);
        Wall("wall-x+", new Vector3(4.1f, 2, 0), new Vector3(.2f, 4.2f, 8.2f), 4);
        Wall("wall-z-", new Vector3(0, 2, -4.1f), new Vector3(8.2f, 4.2f, .2f), 5);
        Wall("wall-z+", new Vector3(0, 2, 4.1f), new Vector3(8.2f, 4.2f, .2f), 6);
        // Not registered: it is only a raster receiver, so probe rays never see it.
        receiver = Quad("receiver", new Vector3(0, CamY, ReceiverZ), new Vector2(2 * ReceiverHalf, 2 * ReceiverHalf),
            receiverMaterial);
    }

    void BuildVolume()
    {
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;
        var black = new Cubemap(1, TextureFormat.RGBAHalf, false);
        for (int face = 0; face < 6; ++face) black.SetPixel((CubemapFace)face, 0, 0, new Color(0, 0, 0, 0));
        black.Apply();
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        RenderSettings.customReflectionTexture = black;

        var go = new GameObject("G3 volume");
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

    IEnumerator Warmup()
    {
        // A material asset reports one placeholder pass until its shader is used once; the RT gate
        // needs FindPass("GlxDXR") on the registered witness.
        var warm = new GameObject("G3 warm camera").AddComponent<Camera>();
        warm.clearFlags = CameraClearFlags.SolidColor;
        warm.backgroundColor = Color.black;
        warm.cullingMask = ~0;
        warm.targetTexture = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        for (int i = 0; i < 4; ++i) yield return null;
        warm.targetTexture = null;
        Destroy(warm.gameObject);
    }

    Vector2 Project(Vector3 world)
    {
        float depth = world.z - CamZ;
        float nx = world.x / (tanH * depth);
        float ny = (world.y - CamY) / (tanV * depth);
        return new Vector2((nx + 1) * .5f * Width, (ny + 1) * .5f * Height);
    }

    void BuildRoi()
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int corner = 0; corner < 4; ++corner)
        {
            var p = Project(new Vector3(
                (corner & 1) == 0 ? -ReceiverHalf : ReceiverHalf,
                CamY + ((corner & 2) == 0 ? -ReceiverHalf : ReceiverHalf), ReceiverZ));
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        }
        const int inset = 6;
        roiX = Mathf.Clamp(Mathf.CeilToInt(minX) + inset, 0, Width - 2);
        roiY = Mathf.Clamp(Mathf.CeilToInt(minY) + inset, 0, Height - 2);
        roiW = Mathf.Clamp(Mathf.FloorToInt(maxX) - inset - roiX, 1, Width - roiX);
        roiH = Mathf.Clamp(Mathf.FloorToInt(maxY) - inset - roiY, 1, Height - roiY);
    }

    // ---------------------------------------------------------------------------------------------
    // Stages
    // ---------------------------------------------------------------------------------------------

    // All three renderers compose the probe in the material: Forward/Forward+ in ForwardLit and
    // Deferred in the GBuffer fragment, before the clustered kernel adds direct light.
    static bool ProbeConsumed(int renderer) => true;

    void ConfigureFeature(int renderer, bool fallback, int coveragePercent, bool gridOutside)    {
        var feature = features[renderer];
        feature.settings.rayTracingProbeFallback = fallback;
        feature.settings.probeCoverage = coveragePercent / 100f;
        feature.settings.probeGridOrigin = gridOutside ? new Vector3(-103.5f, .5f, -3.5f) : new Vector3(-3.5f, .5f, -3.5f);
        // B0 baselineG3: the maintenance and feedback toggles stay off for the composition fixture.
        feature.settings.probeFeedback = false;
        feature.settings.probeRelocation = false;
        feature.settings.probeClassification = false;
    }

    Camera NewCamera(int renderer, string label)
    {
        var camera = new GameObject("G3 camera " + label).AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.fieldOfView = Fov;
        camera.nearClipPlane = .1f;
        camera.farClipPlane = 50f;
        camera.allowMSAA = false;
        camera.allowHDR = true;
        camera.cullingMask = ~0;
        camera.transform.SetPositionAndRotation(new Vector3(0, CamY, CamZ), Quaternion.identity);
        var data = camera.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = false;
        data.antialiasing = AntialiasingMode.None;
        data.SetRenderer(renderer);
        return camera;
    }

    void EnsureTargets()
    {
        if (target == null)
        {
            target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            target.Create();
        }
        if (readback == null) readback = new Texture2D(roiW, roiH, TextureFormat.RGBAHalf, false, true);
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

    string FullHash()
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
        lastNonBlack = nonBlack;
        Destroy(tex);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    int lastNonBlack;

    Stage Begin(int renderer, string kind, int frames, int coverage, int intensity)
    {
        EnsureTargets();
        lastNonBlack = 0;
        view = NewCamera(renderer, kind);
        view.targetTexture = target;
        ssgi.intensity.Override(intensity);
        return new Stage
        {
            stage = kind, api = report.api, guid = report.guid, renderer = renderer, frames = frames,
            coverage = coverage, ssgiIntensity = intensity, roiX = roiX, roiY = roiY, roiW = roiW, roiH = roiH,
            supported = report.api == "Direct3D12"
        };
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
        stage.probeSnapshotReady = fb?.ready ?? false;
        stage.ssgiProbeFlag = ProbeFlag(feature);
        stage.gate = stage.supported ? GateString(feature) : "Backend";
        report.stages.Add(stage);
        view.targetTexture = null;
        Destroy(view.gameObject);
        view = null;
        stage.nonBlack = lastNonBlack;
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
        }, true, true, feature.settings.resources.rayTracingGi, emissive);
        return gate.Evaluate(RayTracingRequests.GI).ToString();
    }

    void Measure(Stage stage, Color[] pixels)
    {
        double r = 0, g = 0, b = 0, y = 0;
        foreach (var p in pixels) { r += p.r; g += p.g; b += p.b; y += Luminance(p); }
        double n = pixels.Length;
        stage.roiMeanR = (float)(r / n); stage.roiMeanG = (float)(g / n); stage.roiMeanB = (float)(b / n);
        stage.roiMeanY = (float)(y / n);
        stage.expectedY = ProbeConsumed(stage.renderer) ? ExpectedY * stage.coverage / 100f : 0f;
        stage.relativeError = Mathf.Abs(stage.roiMeanY - stage.expectedY) / Mathf.Max(Mathf.Abs(stage.expectedY), .01f);
    }

    IEnumerator Render(int frames)
    {
        for (int i = 0; i < frames; ++i) yield return new WaitForEndOfFrame();
    }

    IEnumerator Off(int renderer)
    {
        ConfigureFeature(renderer, false, 100, false);
        var stage = Begin(renderer, "off", ShortSegments, 0, 0);
        yield return Render(stage.frames);
        var pixels = ReadRoi();
        stage.frameHash = FullHash();
        Measure(stage, pixels);
        End(stage);
    }

    IEnumerator OffAgain(int renderer)
    {
        ConfigureFeature(renderer, false, 100, false);
        var stage = Begin(renderer, "offAgain", ShortSegments, 0, 0);
        yield return Render(stage.frames);
        var pixels = ReadRoi();
        stage.frameHash = FullHash();
        Measure(stage, pixels);
        End(stage);
    }

    IEnumerator ResetProducer(int renderer)
    {
        // One rendered frame with the switch off runs the feature reset, which releases the atlas and
        // the scene request so the next stage starts from a genuine cold state.
        ConfigureFeature(renderer, false, 100, false);
        var reset = NewCamera(renderer, "reset");
        reset.targetTexture = target;
        yield return new WaitForEndOfFrame();
        reset.targetTexture = null;
        Destroy(reset.gameObject);
    }

    IEnumerator Cold(int renderer)
    {
        yield return ResetProducer(renderer);
        ConfigureFeature(renderer, true, 100, false);
        var stage = Begin(renderer, "cold", 1, 100, 0);
        yield return new WaitForEndOfFrame();
        var pixels = ReadRoi();
        Measure(stage, pixels);
        // The first frame must be either the untouched baked GI or a ready probe generation, never a
        // half-published blend.
        float value = stage.roiMeanY;
        bool baked = Mathf.Abs(value) <= AbsoluteFloor;
        bool ready = Mathf.Abs(value - ExpectedY) <= GiErrorMax * ExpectedY;
        stage.notes = baked ? "baked" : ready ? "probeReady" : "partial";
        Gate(baked || ready, $"cold start {renderer} blended a partial generation ({value:0.###})");
        End(stage);
    }

    IEnumerator Probe(int renderer)
    {
        ConfigureFeature(renderer, true, 100, false);
        var stage = Begin(renderer, "probe", WarmSegments, 100, 0);
        int updates0 = features[renderer].probeFallback?.updateCount ?? 0;
        // A second eligible base camera renders every frame; the scene frame must still batch once.
        secondTarget = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        secondTarget.Create();
        secondCameraObject = NewCamera(renderer, "second").gameObject;
        secondCameraObject.GetComponent<Camera>().targetTexture = secondTarget;
        int lastUpdates = updates0;
        for (int f = 0; f < stage.frames; ++f)
        {
            yield return new WaitForEndOfFrame();
            int updates = features[renderer].probeFallback?.updateCount ?? 0;
            Gate(updates - lastUpdates <= 1, $"{renderer} dispatched more than one probe batch in frame {f}");
            lastUpdates = updates;
        }
        var pixels = ReadRoi();
        stage.frameHash = FullHash();
        Measure(stage, pixels);
        probePixels = pixels;
        Destroy(secondCameraObject);
        secondCameraObject = null;
        secondTarget.Release();
        Destroy(secondTarget);
        secondTarget = null;
        End(stage);
        if (!stage.supported) yield break;
        Gate(stage.relativeError <= GiErrorMax, $"probe ROI error {stage.relativeError:0.###} ({renderer})");
        Gate(stage.updates > 0 && stage.generation > 0, $"producer never ran ({renderer})");
        Gate(stage.probeSnapshotReady, $"no published probe generation ({renderer})");
        Gate(stage.ssgiProbeFlag, $"SSGI fallback was not forced to None ({renderer})");
        if (ProbeConsumed(renderer))
        {
            Gate(stage.subscribers == 1 && stage.requests == "GI", $"GI-only scene subscription changed ({renderer})");
            Gate(stage.instances > 0 && stage.builds > 0, $"RT scene was never built ({renderer})");
            Gate(stage.producerBytes > 0, $"producer allocated no memory ({renderer})");
        }
    }

    IEnumerator Half(int renderer)
    {
        ConfigureFeature(renderer, true, 50, false);
        var stage = Begin(renderer, "probeHalf", ShortSegments, 50, 0);
        yield return Render(stage.frames);
        var pixels = ReadRoi();
        Measure(stage, pixels);
        End(stage);
        if (stage.supported && ProbeConsumed(renderer) && probePixels != null)
        {
            float max = 0;
            for (int i = 0; i < pixels.Length && i < probePixels.Length; ++i)
            {
                max = Mathf.Max(max, Mathf.Abs(pixels[i].r - .5f * probePixels[i].r));
                max = Mathf.Max(max, Mathf.Abs(pixels[i].g - .5f * probePixels[i].g));
                max = Mathf.Max(max, Mathf.Abs(pixels[i].b - .5f * probePixels[i].b));
            }
            stage.halfScaleMax = max;
            Gate(max <= AbsoluteFloor, $"coverage 0.5 is not half of c=1 (max {max:0.####}, {renderer})");
        }
    }

    IEnumerator Both(int renderer)
    {
        ConfigureFeature(renderer, true, 100, false);
        var stage = Begin(renderer, "probeBoth", BothSegments, 100, 1);
        yield return Render(stage.frames);
        var pixels = ReadRoi();
        Measure(stage, pixels);
        End(stage);
        if (stage.supported && ProbeConsumed(renderer))
            Gate(stage.relativeError <= GiErrorMax, $"probe + SSGI ROI error {stage.relativeError:0.###} ({renderer})");
    }

    IEnumerator Outside(int renderer)
    {
        // The producer captures the grid at construction, so the volume has to be rebuilt with the
        // receiver outside it.
        yield return ResetProducer(renderer);
        ConfigureFeature(renderer, true, 100, true);
        var stage = Begin(renderer, "probeOutside", ShortSegments, 100, 0);
        yield return Render(stage.frames);
        var pixels = ReadRoi();
        Measure(stage, pixels);
        End(stage);
        if (stage.supported && ProbeConsumed(renderer))
            Gate(Mathf.Abs(stage.roiMeanY) <= AbsoluteFloor, $"outside the volume did not fall back to baked GI ({renderer})");
    }

    // ---------------------------------------------------------------------------------------------
    // Evaluation
    // ---------------------------------------------------------------------------------------------

    void Evaluate()
    {
        for (int renderer = 0; renderer < 3; ++renderer)
        {
            var off = Find("off", renderer);
            var offAgain = Find("offAgain", renderer);
            var probe = Find("probe", renderer);
            if (off == null || offAgain == null || probe == null) continue;

            if (report.api == "Direct3D11")
            {
                Gate(off.frameHash == offAgain.frameHash, $"D3D11 off/offAgain differ ({renderer})");
                Gate(off.frameHash == probe.frameHash, $"D3D11 probe changed the image ({renderer})");
            }
            else
            {
                Gate(off.frameHash == offAgain.frameHash, $"off/offAgain differ ({renderer})");
                Gate(off.frameHash != probe.frameHash, $"probe never changed the image ({renderer})");
                Gate(off.nonBlack > 1000, $"off frame is blank ({renderer})");
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
        const string folder = "Assets/Fixture/G3";
        if (!AssetDatabase.IsValidFolder(folder))
        {
            if (!AssetDatabase.IsValidFolder("Assets/Fixture")) AssetDatabase.CreateFolder("Assets", "Fixture");
            AssetDatabase.CreateFolder("Assets/Fixture", "G3");
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

        var emissive = Mat("emissive", Color.black, new Color(Emission, Emission, Emission));
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
            renderer.name = mode + " G3 Renderer";
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
        var pipelinePath = folder + "/G3Pipeline.asset";
        AssetDatabase.DeleteAsset(pipelinePath);
        var pipeline = UnityEngine.Object.Instantiate(templatePipeline);
        pipeline.name = "G3 Pipeline";
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
        var driver = new GameObject("G3 fixture").AddComponent<G3Player>();
        driver.emissive = emissive;
        driver.receiverMaterial = receiver;
        driver.features = features;
        driver.pipeline = pipeline;
        Check(EditorSceneManager.SaveScene(scene, "Assets/G3.unity"), "Cannot save G3 scene");

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
                scenes = new[] { "Assets/G3.unity" }, target = target,
                locationPathName = Arg("-exe"), options = BuildOptions.Development
            });
            Check(build.summary.result == BuildResult.Succeeded, "Win64 G3 build failed");
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
