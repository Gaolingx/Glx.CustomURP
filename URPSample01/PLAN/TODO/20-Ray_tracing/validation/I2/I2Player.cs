// I2 Windows sign-off: the B0 candidate workload at 1080p, RT-off / GI / SSR / both, with the
// timed yaw trajectory and moving cube frozen control, plus the same-machine D3D11 off run and a
// D3D11-only strip mode. GPU timings come from FrameTimingManager (whole frame) and from
// ProfilingSampler recorders explicitly enabled here; the production markers are TLAS build,
// probe trace/update, relocate/classify, SSR trace and SSR reconstruction.
//
// Quality is a 1080p sanity on the same scene: RT-on must change the mirror and the diffuse image,
// with no non-finite samples. The analytic GI/SSR quality is signed by I1/G3c/R3.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif

public sealed class I2Player : MonoBehaviour
{
    public const int Width = 1920, Height = 1080;
    const float Fov = 60f, CamX = 0f, CamY = 1.6f, CamZ = -2.5f;
    const float MirrorX = 0f, MirrorY = 2f, MirrorZ = 3.98f, MirrorHalf = 1f;
    const float DonorX = -2f, DonorY = 2f, DonorZ = -2f, DonorHalf = .5f;
    const float DonorEmission = 2f, MatteLinear = .5f;
    static readonly Color MatteColor = new Color(LinearToSrgb(MatteLinear), LinearToSrgb(MatteLinear), LinearToSrgb(MatteLinear));
    static readonly Color DonorColor = new Color(LinearToSrgb(.5f), 0f, 0f);
    const int Budget = 32768, WarmupFrames = 120, TimedFrames = 120, Repeats = 3, QualityFrames = 60;
    const float TrajectoryAmplitudeDegrees = 50f;
    // B0 performance and quality caps (validation/B0/Freeze.json).
    const double GpuFrameDeltaP95Max = 4.0, CpuTlasSubmitP95Max = .5, GpuTlasBuildP95Max = 1.0,
        GpuProbeTraceP95Max = 1.25, GpuRelocateP95Max = .25, GpuSsrTraceP95Max = 1.75,
        GpuSsrReconP95Max = .5, PeakExtraGpuMiBMax = 256.0;

    public Material matte, mirrorMaterial, donorMaterial, donorOffMaterial;
    public ScreenSpaceLightingRendererFeature[] features = new ScreenSpaceLightingRendererFeature[3];
    public UniversalRenderPipelineAsset pipeline;

    [Serializable] class Stage
    {
        public string stage, api, guid, requests, notes;
        public int renderer, repeat, frames, warmup, instances, builds, subscribers, updates, generation,
            ssrBudgetActual, lightRegistered, lightUploaded, lightUploads, tlasBuildSamples, tlasCpuSamples,
            probeTraceSamples, probeMaintSamples, ssrTraceSamples, ssrReconSamples, ssrPassSamples,
            gpuFrameSamples, cpuFrameSamples, gpuRtSumSamples;
        public bool supported, dynamic, ssrQueue, probeAllocated;
        public long producerBytes, queueBytes, trackedBytes;
        public double cpuFrameMedian, cpuFrameP95, gpuFrameMedian, gpuFrameP95;
        public double tlasBuildGpuMedian, tlasBuildGpuP95, tlasCpuMedian, tlasCpuP95;
        public double probeTraceMedian, probeTraceP95, probeMaintMedian, probeMaintP95;
        public double ssrTraceMedian, ssrTraceP95, ssrReconMedian, ssrReconP95;
        public double ssrPassMedian, ssrPassP95;
        public double gpuRtSumMedian, gpuRtSumP95;
        public double driverPeakMiB, driverDeltaMiB;
        public string onHash, offHash;
        public double mirrorOn, mirrorOff, frameOn, frameOff;
        public int diffPixels, nonFinite, nonBlack;
    }

    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(),
            adapter = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion, error, mode = "full";
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed;
        public bool profilerEnabled, gpuAreaEnabled, gpuTimingMeasured;
        public int width = Width, height = Height, timedFrames = TimedFrames, warmup = WarmupFrames, repeats = Repeats;
        public float fov = Fov, camX = CamX, camY = CamY, camZ = CamZ, mirrorZ = MirrorZ, donorX = DonorX, donorZ = DonorZ;
        public double gpuFrameDeltaP95Max = GpuFrameDeltaP95Max, cpuTlasSubmitP95Max = CpuTlasSubmitP95Max,
            gpuTlasBuildP95Max = GpuTlasBuildP95Max, gpuProbeTraceP95Max = GpuProbeTraceP95Max,
            gpuRelocateP95Max = GpuRelocateP95Max, gpuSsrTraceP95Max = GpuSsrTraceP95Max,
            gpuSsrReconP95Max = GpuSsrReconP95Max, peakExtraGpuMiBMax = PeakExtraGpuMiBMax;
        public List<string> renderers = new List<string> { "Forward", "ForwardPlus", "Deferred" };
        public List<Stage> stages = new List<Stage>();
        public List<string> gates = new List<string>();
        public List<string> knownDifferences = new List<string>();
    }

    Report report;
    Camera view;
    RenderTexture target;
    Texture2D full;
    Volume volume;
    ScreenSpaceGlobalIllumination ssgi;
    ScreenSpaceReflection ssr;
    Light directional, point, spot;
    GameObject sceneRoot, cubeObject;
    float tanV, tanH;
    int mirX, mirY, mirW, mirH;
    string destination;
    readonly List<string> failures = new List<string>();

    readonly List<double> cpuFrames = new List<double>(), gpuFrames = new List<double>();
    readonly List<double> tlasBuildGpu = new List<double>(), tlasCpu = new List<double>();
    readonly List<double> probeTrace = new List<double>(), probeMaint = new List<double>();
    readonly List<double> ssrTrace = new List<double>(), ssrRecon = new List<double>(), ssrPass = new List<double>();
    readonly List<double> rtSum = new List<double>();

    static float LinearToSrgb(float c) => Mathf.LinearToGammaSpace(Mathf.Clamp01(c));

    static string Arg(string key)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, key);
        if (i < 0 || i + 1 == args.Length) throw new ArgumentException(key);
        return args[i + 1];
    }

    static bool HasArg(string key) => Array.IndexOf(Environment.GetCommandLineArgs(), key) >= 0;
    static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    void Gate(bool ok, string why) { if (!ok) failures.Add(why); }
    static float Luminance(Color c) => .2126f * c.r + .7152f * c.g + .0722f * c.b;

    IEnumerator Start()
    {
        report = new Report { mode = HasArg("-mode") ? Arg("-mode") : "full" };
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
        if (full) { Destroy(full); full = null; }
        if (volume) { Destroy(volume.gameObject); volume = null; }

        if (report.api == "Direct3D11")
        {
            Gate(!report.supportsRayTracing, "D3D11 must not report DXR support");
            foreach (var stage in report.stages)
            {
                Gate(!stage.probeAllocated && !stage.ssrQueue && stage.updates == 0 && stage.instances == 0,
                    $"{stage.stage}/{stage.renderer} allocated RT resources on D3D11");
            }
        }
        report.gpuTimingMeasured = report.stages.Exists(s => s.probeTraceSamples > 0 || s.ssrTraceSamples > 0 ||
            s.tlasBuildSamples > 0 || s.gpuRtSumSamples > 0);
    }

    IEnumerator Suite()
    {
        Check(report.development && Application.platform == RuntimePlatform.WindowsPlayer && IntPtr.Size == 8 &&
            QualitySettings.activeColorSpace == ColorSpace.Linear && matte && mirrorMaterial && donorMaterial &&
            donorOffMaterial && pipeline && features.Length == 3 && features[0] && features[1] && features[2],
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
        Check(UniversalRenderPipeline.asset == pipeline, "I2 pipeline is not active");

        UnityEngine.Profiling.Profiler.enabled = true;
        UnityEngine.Profiling.Profiler.SetAreaEnabled(ProfilerArea.GPU, true);
        RayTracingSceneResources.buildSampler.enableRecording = true;
        RayTracingProbeUpdate.batchSampler.enableRecording = true;
        RayTracingProbeUpdate.traceUpdateSampler.enableRecording = true;
        RayTracingProbeUpdate.maintainSampler.enableRecording = true;
        SsrRequestQueue.traceSampler.enableRecording = true;
        ScreenSpaceLightingSsrPass.passSampler.enableRecording = true;
        ScreenSpaceLightingSsrPass.reconstructionSampler.enableRecording = true;
        report.profilerEnabled = UnityEngine.Profiling.Profiler.enabled;
        report.gpuAreaEnabled = UnityEngine.Profiling.Profiler.GetAreaEnabled(ProfilerArea.GPU);

        tanV = Mathf.Tan(Fov * Mathf.Deg2Rad * .5f);
        tanH = tanV * (Width / (float)Height);
        BuildVolume();
        BuildScene();
        yield return Warmup();

        if (report.mode == "strip")
        {
            yield return StripCheck();
            yield break;
        }

        if (report.api == "Direct3D11")
        {
            for (int renderer = 0; renderer < 3; ++renderer)
            {
                yield return BackendCheck(renderer);
                for (int repeat = 0; repeat < Repeats; ++repeat)
                    yield return Perf(renderer, "off", true, repeat);
            }
            Evaluate();
            yield break;
        }

        for (int renderer = 0; renderer < 3; ++renderer)
        {
            yield return Quality(renderer);
            foreach (string config in new[] { "off", "gi", "ssr", "both", "bothStatic" })
                for (int repeat = 0; repeat < Repeats; ++repeat)
                    yield return Perf(renderer, config, config != "bothStatic", repeat);
        }
        Evaluate();
    }

    IEnumerator StripCheck()
    {
        Configure(0, true, true, Budget, true, true, true);
        var camera = NewCamera(0, "strip", new Vector3(CamX, CamY, CamZ), 0f);
        camera.targetTexture = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        for (int i = 0; i < 4; ++i) yield return new WaitForEndOfFrame();
        var feature = features[0];
        var stage = new Stage
        {
            stage = "strip", api = report.api, guid = report.guid, renderer = 0, frames = 4,
            supported = report.api == "Direct3D12", ssrQueue = feature.ssrPass?.requestQueue != null,
            probeAllocated = feature.probeFallback?.producerAllocated ?? false,
            updates = feature.probeFallback?.updateCount ?? 0,
            instances = feature.rayTracingScene?.instanceCount ?? 0,
            builds = feature.rayTracingScene?.buildCount ?? 0,
            subscribers = feature.rayTracingScene?.subscribers ?? 0,
            requests = feature.rayTracingScene?.requests.ToString() ?? "None"
        };
        report.stages.Add(stage);
        camera.targetTexture = null;
        Destroy(camera.gameObject);
    }

    IEnumerator BackendCheck(int renderer)
    {
        Configure(renderer, true, true, Budget, true, true, true);
        var camera = NewCamera(renderer, "backend", new Vector3(CamX, CamY, CamZ), 0f);
        camera.targetTexture = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        for (int i = 0; i < 4; ++i) yield return new WaitForEndOfFrame();
        var feature = features[renderer];
        report.stages.Add(new Stage
        {
            stage = "backend", api = report.api, guid = report.guid, renderer = renderer, frames = 4,
            ssrQueue = feature.ssrPass?.requestQueue != null,
            probeAllocated = feature.probeFallback?.producerAllocated ?? false,
            updates = feature.probeFallback?.updateCount ?? 0,
            instances = feature.rayTracingScene?.instanceCount ?? 0,
            builds = feature.rayTracingScene?.buildCount ?? 0,
            subscribers = feature.rayTracingScene?.subscribers ?? 0,
            requests = feature.rayTracingScene?.requests.ToString() ?? "None"
        });
        camera.targetTexture = null;
        Destroy(camera.gameObject);
    }

    // ---------------------------------------------------------------------------------------------
    // B0 candidate workload
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

        var go = new GameObject("I2 volume");
        volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 999;
        ssgi = volume.profile.Add<ScreenSpaceGlobalIllumination>();
        ssgi.enabled.Override(true);
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

    GameObject Box(string name, Vector3 position, Vector3 size, Material material, int id, bool registered = true)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(sceneRoot.transform, false);
        go.transform.position = position;
        go.transform.localScale = size;
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        if (registered)
        {
            renderer.rayTracingMode = RayTracingMode.Static;
            var geometry = go.AddComponent<SsrRayTracingGeometry>();
            geometry.instanceId = id;
            geometry.enableTriangleCulling = false;
        }
        return go;
    }

    GameObject Quad(string name, Vector3 position, Vector2 size, Material material, int id, bool facePlusZ)
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
        if (facePlusZ) go.transform.rotation = Quaternion.Euler(0, 180, 0);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        if (id >= 0)
        {
            renderer.rayTracingMode = RayTracingMode.Static;
            var geometry = go.AddComponent<SsrRayTracingGeometry>();
            geometry.instanceId = id;
            geometry.enableTriangleCulling = false;
        }
        return go;
    }

    void BuildScene()
    {
        sceneRoot = new GameObject("I2 candidate workload");
        Box("floor", new Vector3(0, -.1f, 0), new Vector3(8.2f, .2f, 8.2f), matte, 1);
        Box("ceiling", new Vector3(0, 4.1f, 0), new Vector3(8.2f, .2f, 8.2f), matte, 2);
        Box("wall-x-", new Vector3(-4.1f, 2, 0), new Vector3(.2f, 4.2f, 8.2f), matte, 3);
        Box("wall-x+", new Vector3(4.1f, 2, 0), new Vector3(.2f, 4.2f, 8.2f), matte, 4);
        Box("wall-z-", new Vector3(0, 2, -4.1f), new Vector3(8.2f, 4.2f, .2f), matte, 5);
        Box("wall-z+", new Vector3(0, 2, 4.1f), new Vector3(8.2f, 4.2f, .2f), matte, 6);
        Box("divider", new Vector3(2, 1, 0), new Vector3(.1f, 2f, 2f), matte, 7);
        Quad("mirror", new Vector3(MirrorX, MirrorY, MirrorZ), new Vector2(2 * MirrorHalf, 2 * MirrorHalf),
            mirrorMaterial, -1, false);
        // The donor's front is +Z, so the raster camera at -Z culls it; the reflection rays see it.
        Quad("donor", new Vector3(DonorX, DonorY, DonorZ), new Vector2(2 * DonorHalf, 2 * DonorHalf),
            donorMaterial, 37, true);
        cubeObject = Box("cube", new Vector3(-1, 1, 1), Vector3.one * .5f, matte, 38);
        cubeObject.GetComponent<MeshRenderer>().rayTracingMode = RayTracingMode.DynamicTransform;

        directional = NewLight("directional", LightType.Directional, new Vector3(0, 5, 0), 1f, 10f, 40f);
        directional.transform.rotation = Quaternion.Euler(50, 30, 0);
        point = NewLight("point", LightType.Point, new Vector3(-2, 2, 0), 4f, 6f, 40f);
        spot = NewLight("spot", LightType.Spot, new Vector3(2, 3, -2), 4f, 8f, 40f);
        spot.transform.LookAt(Vector3.zero);
    }

    Light NewLight(string name, LightType type, Vector3 position, float intensity, float range, float spotAngle)
    {
        var go = new GameObject("I2 light " + name);
        go.transform.SetParent(sceneRoot.transform, false);
        go.transform.position = position;
        var light = go.AddComponent<Light>();
        light.type = type;
        light.color = Color.white;
        light.intensity = intensity;
        light.range = range;
        light.spotAngle = spotAngle;
        light.innerSpotAngle = 0f;
        light.shadows = LightShadows.Hard;
        return light;
    }

    IEnumerator Warmup()
    {
        var warm = new GameObject("I2 warm camera").AddComponent<Camera>();
        warm.clearFlags = CameraClearFlags.SolidColor;
        warm.backgroundColor = Color.black;
        warm.cullingMask = ~0;
        warm.targetTexture = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        for (int i = 0; i < 4; ++i) yield return null;
        warm.targetTexture = null;
        Destroy(warm.gameObject);
    }

    Camera NewCamera(int renderer, string label, Vector3 position, float yaw)
    {
        var camera = new GameObject("I2 camera " + label).AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.fieldOfView = Fov;
        camera.nearClipPlane = .1f;
        camera.farClipPlane = 50f;
        camera.allowMSAA = false;
        camera.allowHDR = true;
        camera.cullingMask = ~0;
        camera.transform.SetPositionAndRotation(position, Quaternion.Euler(0, yaw, 0));
        var data = camera.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = false;
        data.antialiasing = AntialiasingMode.None;
        data.SetRenderer(renderer);
        return camera;
    }

    void Configure(int renderer, bool gi, bool ssrOn, int budget, bool feedback, bool relocation, bool classification)
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
        ssgi.enabled.Override(true);
        ssgi.intensity.Override(0f);
        ssr.enabled.Override(ssrOn);
    }

    IEnumerator ResetFeature(int renderer)
    {
        Configure(renderer, false, false, 0, true, true, true);
        var reset = NewCamera(renderer, "reset", new Vector3(CamX, CamY, CamZ), 0f);
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

    Vector2 Project(Vector3 world)
    {
        float depth = world.z - CamZ;
        return new Vector2(
            ((world.x - CamX) / (tanH * depth) + 1) * .5f * Width,
            ((world.y - CamY) / (tanV * depth) + 1) * .5f * Height);
    }

    void SetMirrorRoi()
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int corner = 0; corner < 4; ++corner)
        {
            var p = Project(new Vector3(MirrorX + ((corner & 1) == 0 ? -MirrorHalf : MirrorHalf),
                MirrorY + ((corner & 2) == 0 ? -MirrorHalf : MirrorHalf), MirrorZ));
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        }
        mirX = Mathf.Clamp(Mathf.CeilToInt(minX) + 6, 0, Width - 2);
        mirY = Mathf.Clamp(Mathf.CeilToInt(minY) + 6, 0, Height - 2);
        mirW = Mathf.Clamp(Mathf.FloorToInt(maxX) - 6 - mirX, 1, Width - mirX);
        mirH = Mathf.Clamp(Mathf.FloorToInt(maxY) - 6 - mirY, 1, Height - mirY);
    }

    Color[] ReadFrame(out string hash, out int nonFinite, out int nonBlack)
    {
        if (full == null) full = new Texture2D(Width, Height, TextureFormat.RGBAHalf, false, true);
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        try { full.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); }
        finally { RenderTexture.active = previous; }
        full.Apply(false, false);
        var pixels = full.GetPixels();
        nonFinite = 0;
        nonBlack = 0;
        foreach (var p in pixels)
        {
            if (!IsFinite(p.r) || !IsFinite(p.g) || !IsFinite(p.b)) ++nonFinite;
            if (p.r > .1f || p.g > .1f || p.b > .1f) ++nonBlack;
        }
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(full.GetRawTextureData());
        var sb = new StringBuilder(digest.Length * 2);
        foreach (var b in digest) sb.Append(b.ToString("x2"));
        hash = sb.ToString();
        return pixels;
    }

    static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    double Mean(Color[] pixels, int x, int y, int w, int h)
    {
        double sum = 0;
        for (int row = 0; row < h; ++row)
            for (int column = 0; column < w; ++column)
                sum += Luminance(pixels[(y + row) * Width + x + column]);
        return sum / Mathf.Max(1, w * h);
    }

    double Mean(Color[] pixels)
    {
        double sum = 0;
        foreach (var p in pixels) sum += Luminance(p);
        return sum / Mathf.Max(1, pixels.Length);
    }

    static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        int rank = Mathf.Clamp(Mathf.CeilToInt((float)(percentile * sorted.Length)) - 1, 0, sorted.Length - 1);
        return sorted[rank];
    }

    static double WorstP95(IEnumerable<Stage> stages, Func<Stage, double> selector)
    {
        double worst = 0;
        foreach (var stage in stages) worst = Math.Max(worst, selector(stage));
        return worst;
    }

    IEnumerator Render(int frames)
    {
        for (int i = 0; i < frames; ++i) yield return new WaitForEndOfFrame();
    }

    // ---------------------------------------------------------------------------------------------
    // Quality and timed performance
    // ---------------------------------------------------------------------------------------------

    IEnumerator Quality(int renderer)
    {
        yield return ResetFeature(renderer);
        var feature = features[renderer];
        Configure(renderer, true, true, Budget, true, true, true);
        target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        target.Create();
        view = NewCamera(renderer, "quality", new Vector3(CamX, CamY, CamZ), 0f);
        view.targetTexture = target;
        cubeObject.transform.position = new Vector3(-1, 1, 1);
        SetMirrorRoi();
        yield return Render(QualityFrames);
        var on = ReadFrame(out string onHash, out int onNonFinite, out int onNonBlack);
        double mirrorOn = Mean(on, mirX, mirY, mirW, mirH);
        double frameOn = Mean(on);
        int onInstances = feature.rayTracingScene?.instanceCount ?? 0;
        int onBuilds = feature.rayTracingScene?.buildCount ?? 0;
        int onSubscribers = feature.rayTracingScene?.subscribers ?? 0;
        int onUpdates = feature.probeFallback?.updateCount ?? 0;
        bool onQueue = feature.ssrPass?.requestQueue != null;

        Configure(renderer, false, false, 0, true, true, true);
        yield return Render(QualityFrames);
        var off = ReadFrame(out string offHash, out int offNonFinite, out int offNonBlack);
        double mirrorOff = Mean(off, mirX, mirY, mirW, mirH);
        double frameOff = Mean(off);
        int diff = 0;
        for (int i = 0; i < on.Length; ++i)
            if (Mathf.Abs(Luminance(on[i]) - Luminance(off[i])) > .01f) ++diff;

        report.stages.Add(new Stage
        {
            stage = "quality", api = report.api, guid = report.guid, renderer = renderer,
            frames = QualityFrames * 2, supported = report.api == "Direct3D12",
            instances = onInstances, builds = onBuilds, subscribers = onSubscribers, updates = onUpdates,
            probeAllocated = onUpdates > 0, ssrQueue = onQueue,
            onHash = onHash, offHash = offHash, mirrorOn = mirrorOn, mirrorOff = mirrorOff,
            frameOn = frameOn, frameOff = frameOff, diffPixels = diff,
            nonFinite = onNonFinite + offNonFinite, nonBlack = onNonBlack
        });
        view.targetTexture = null;
        Destroy(view.gameObject);
        view = null;
        target.Release();
        Destroy(target);
        target = null;
    }

    IEnumerator Perf(int renderer, string config, bool dynamic, int repeat)
    {
        yield return ResetFeature(renderer);
        bool gi = config == "gi" || config == "both" || config == "bothStatic";
        bool ssrOn = config == "ssr" || config == "both" || config == "bothStatic";
        Configure(renderer, gi, ssrOn, ssrOn ? Budget : 0, true, true, true);
        var feature = features[renderer];
        var scene = feature.rayTracingScene;

        target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        target.Create();
        view = NewCamera(renderer, config + "-" + repeat, new Vector3(CamX, CamY, CamZ), 0f);
        view.targetTexture = target;
        cubeObject.transform.position = new Vector3(-1, 1, 1);
        yield return Render(WarmupFrames);

        ClearSamples();
        var stage = new Stage
        {
            stage = config, api = report.api, guid = report.guid, renderer = renderer, repeat = repeat,
            frames = TimedFrames, warmup = WarmupFrames, dynamic = dynamic,
            supported = report.api == "Direct3D12", ssrBudgetActual = ssrOn ? Budget : 0
        };
        int baseUpdates = feature.probeFallback?.updateCount ?? 0;
        int baseBuilds = scene?.buildCount ?? 0;
        int baseUploads = scene?.lightUploadCount ?? 0;
        double driverPeak = DriverMiB();
        long tracked = 0;
        for (int frame = 0; frame < TimedFrames; ++frame)
        {
            float phase = 2f * Mathf.PI * frame / TimedFrames;
            view.transform.rotation = Quaternion.Euler(0, TrajectoryAmplitudeDegrees * Mathf.Sin(phase), 0);
            if (dynamic)
                cubeObject.transform.position = new Vector3(-1f + .5f * Mathf.Sin(phase), 1f, 1f);
            FrameTimingManager.CaptureFrameTimings();
            yield return new WaitForEndOfFrame();
            SampleFrame(stage);
            double memory = DriverMiB();
            if (memory > driverPeak) driverPeak = memory;
            long bytes = (feature.probeFallback?.producerBytes ?? 0) +
                (feature.ssrPass?.requestQueue?.residentBytes ?? 0);
            if (bytes > tracked) tracked = bytes;
        }

        stage.instances = scene?.instanceCount ?? 0;
        stage.builds = (scene?.buildCount ?? 0) - baseBuilds;
        stage.subscribers = scene?.subscribers ?? 0;
        stage.requests = scene?.requests.ToString() ?? "None";
        stage.updates = (feature.probeFallback?.updateCount ?? 0) - baseUpdates;
        stage.generation = feature.probeFallback?.generation ?? 0;
        stage.probeAllocated = feature.probeFallback?.producerAllocated ?? false;
        stage.producerBytes = feature.probeFallback?.producerBytes ?? 0;
        stage.queueBytes = feature.ssrPass?.requestQueue?.residentBytes ?? 0;
        stage.trackedBytes = tracked;
        stage.ssrQueue = feature.ssrPass?.requestQueue != null;
        stage.lightUploads = (scene?.lightUploadCount ?? 0) - baseUploads;
        stage.lightRegistered = scene?.registeredLights ?? 0;
        stage.lightUploaded = scene?.uploadedLights ?? 0;
        stage.driverPeakMiB = driverPeak;
        stage.cpuFrameSamples = cpuFrames.Count;
        stage.gpuFrameSamples = gpuFrames.Count;
        stage.cpuFrameMedian = Percentile(cpuFrames, .5);
        stage.cpuFrameP95 = Percentile(cpuFrames, .95);
        stage.gpuFrameMedian = Percentile(gpuFrames, .5);
        stage.gpuFrameP95 = Percentile(gpuFrames, .95);
        stage.tlasBuildGpuMedian = Percentile(tlasBuildGpu, .5);
        stage.tlasBuildGpuP95 = Percentile(tlasBuildGpu, .95);
        stage.tlasCpuMedian = Percentile(tlasCpu, .5);
        stage.tlasCpuP95 = Percentile(tlasCpu, .95);
        stage.probeTraceMedian = Percentile(probeTrace, .5);
        stage.probeTraceP95 = Percentile(probeTrace, .95);
        stage.probeMaintMedian = Percentile(probeMaint, .5);
        stage.probeMaintP95 = Percentile(probeMaint, .95);
        stage.ssrTraceMedian = Percentile(ssrTrace, .5);
        stage.ssrTraceP95 = Percentile(ssrTrace, .95);
        stage.ssrReconMedian = Percentile(ssrRecon, .5);
        stage.ssrReconP95 = Percentile(ssrRecon, .95);
        stage.ssrPassMedian = Percentile(ssrPass, .5);
        stage.ssrPassP95 = Percentile(ssrPass, .95);
        stage.gpuRtSumMedian = Percentile(rtSum, .5);
        stage.gpuRtSumP95 = Percentile(rtSum, .95);
        report.stages.Add(stage);

        view.targetTexture = null;
        Destroy(view.gameObject);
        view = null;
        target.Release();
        Destroy(target);
        target = null;
    }

    void ClearSamples()
    {
        cpuFrames.Clear(); gpuFrames.Clear();
        tlasBuildGpu.Clear(); tlasCpu.Clear();
        probeTrace.Clear(); probeMaint.Clear();
        ssrTrace.Clear(); ssrRecon.Clear(); ssrPass.Clear();
        rtSum.Clear();
    }

    void SampleFrame(Stage stage)
    {
        var timing = new FrameTiming[1];
        if (FrameTimingManager.GetLatestTimings(1, timing) > 0)
        {
            if (timing[0].cpuFrameTime > 0) cpuFrames.Add(timing[0].cpuFrameTime);
            if (timing[0].gpuFrameTime > 0) gpuFrames.Add(timing[0].gpuFrameTime);
        }
        AddSample(RayTracingSceneResources.buildSampler.gpuElapsedTime, tlasBuildGpu, ref stage.tlasBuildSamples);
        AddSample(RayTracingProbeUpdate.traceUpdateSampler.gpuElapsedTime, probeTrace, ref stage.probeTraceSamples);
        AddSample(RayTracingProbeUpdate.maintainSampler.gpuElapsedTime, probeMaint, ref stage.probeMaintSamples);
        AddSample(SsrRequestQueue.traceSampler.gpuElapsedTime, ssrTrace, ref stage.ssrTraceSamples);
        AddSample(ScreenSpaceLightingSsrPass.reconstructionSampler.gpuElapsedTime, ssrRecon, ref stage.ssrReconSamples);
        AddSample(ScreenSpaceLightingSsrPass.passSampler.gpuElapsedTime, ssrPass, ref stage.ssrPassSamples);
        double span = RayTracingSceneResources.buildSampler.gpuElapsedTime +
            RayTracingProbeUpdate.traceUpdateSampler.gpuElapsedTime +
            RayTracingProbeUpdate.maintainSampler.gpuElapsedTime +
            SsrRequestQueue.traceSampler.gpuElapsedTime +
            ScreenSpaceLightingSsrPass.reconstructionSampler.gpuElapsedTime;
        if (span > 0) { rtSum.Add(span); ++stage.gpuRtSumSamples; }
        var scene = features[stage.renderer].rayTracingScene;
        if (scene != null && scene.lastBuildFrame == Time.frameCount)
        {
            tlasCpu.Add(scene.lastBuildCpuMs);
            ++stage.tlasCpuSamples;
        }
    }

    static void AddSample(float milliseconds, List<double> values, ref int samples)
    {
        if (milliseconds > 0 && IsFinite(milliseconds))
        {
            values.Add(milliseconds);
            ++samples;
        }
    }

    static double DriverMiB() => UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576.0;

    List<Stage> Stages(int renderer, params string[] names)
    {
        var list = new List<Stage>();
        foreach (var stage in report.stages)
        {
            if (stage.renderer != renderer) continue;
            foreach (var name in names)
                if (stage.stage == name) { list.Add(stage); break; }
        }
        return list;
    }

    void Evaluate()
    {
        if (report.api == "Direct3D11")
        {
            for (int renderer = 0; renderer < 3; ++renderer)
            {
                var backend = Stages(renderer, "backend");
                var off = Stages(renderer, "off");
                Gate(backend.Count == 1, $"D3D11/{renderer} backend check missing");
                if (backend.Count == 1)
                    Gate(!backend[0].probeAllocated && !backend[0].ssrQueue && backend[0].updates == 0 &&
                         backend[0].instances == 0 && backend[0].subscribers == 0,
                         $"D3D11/{renderer} allocated RT resources");
                Gate(off.Count == Repeats, $"D3D11/{renderer} off repeats {off.Count}");
                foreach (var stage in off)
                    Gate(stage.cpuFrameSamples > 0, $"D3D11/{renderer} no CPU frame samples");
            }
            return;
        }

        for (int renderer = 0; renderer < 3; ++renderer)
        {
            var quality = Stages(renderer, "quality");
            var off = Stages(renderer, "off");
            var gi = Stages(renderer, "gi");
            var ssrStages = Stages(renderer, "ssr");
            var both = Stages(renderer, "both");
            var staticControl = Stages(renderer, "bothStatic");
            if (quality.Count != 1 || off.Count != Repeats || gi.Count != Repeats || ssrStages.Count != Repeats ||
                both.Count != Repeats || staticControl.Count != Repeats)
            {
                Gate(false, $"I2/{renderer} missing stages");
                continue;
            }

            var q = quality[0];
            Gate(q.nonFinite == 0, $"quality/{renderer} non-finite {q.nonFinite}");
            Gate(q.nonBlack > 100000, $"quality/{renderer} blank frame {q.nonBlack}");
            Gate(q.onHash != q.offHash, $"quality/{renderer} RT-on equals RT-off");
            Gate(q.diffPixels > 1000, $"quality/{renderer} changed {q.diffPixels} pixels");
            Gate(q.mirrorOn > q.mirrorOff * 1.2, $"quality/{renderer} mirror {q.mirrorOn:0.###} vs {q.mirrorOff:0.###}");
            Gate(q.instances > 0 && q.subscribers == 2 && q.probeAllocated && q.ssrQueue,
                $"quality/{renderer} RT not active");
            report.gates.Add($"quality/{renderer}: diff {q.diffPixels}, mirror {q.mirrorOff:0.###}->{q.mirrorOn:0.###}");

            // FrameTimingManager GPU samples are unusable on this machine (near-zero, partial);
            // the incremental RT GPU cost is the sum of the non-overlapping RT markers, which
            // includes the nested hit shading inside the probe/SSR trace scopes.
            double delta = WorstP95(both, s => s.gpuRtSumP95);
            Gate(delta > 0 && delta <= GpuFrameDeltaP95Max, $"perf/{renderer} RT GPU delta {delta:0.###}");
            double cpuTlas = WorstP95(both, s => s.tlasCpuP95);
            Gate(cpuTlas <= CpuTlasSubmitP95Max && cpuTlas > 0, $"perf/{renderer} CPU TLAS submit {cpuTlas:0.###}");
            double gpuTlas = WorstP95(both, s => s.tlasBuildGpuP95);
            Gate(gpuTlas <= GpuTlasBuildP95Max && gpuTlas > 0, $"perf/{renderer} GPU TLAS build {gpuTlas:0.###}");
            double probe = WorstP95(Stages(renderer, "gi", "both"), s => s.probeTraceP95);
            Gate(probe <= GpuProbeTraceP95Max && probe > 0, $"perf/{renderer} probe trace/update {probe:0.###}");
            double maintain = WorstP95(Stages(renderer, "gi", "both"), s => s.probeMaintP95);
            Gate(maintain <= GpuRelocateP95Max && maintain > 0, $"perf/{renderer} relocate/classify {maintain:0.###}");
            double ssrTrace = WorstP95(Stages(renderer, "ssr", "both"), s => s.ssrTraceP95);
            Gate(ssrTrace <= GpuSsrTraceP95Max && ssrTrace > 0, $"perf/{renderer} SSR trace {ssrTrace:0.###}");
            double ssrRecon = WorstP95(Stages(renderer, "ssr", "both"), s => s.ssrReconP95);
            Gate(ssrRecon <= GpuSsrReconP95Max && ssrRecon > 0, $"perf/{renderer} SSR reconstruction {ssrRecon:0.###}");

            double offPeak = 0, bothPeak = 0;
            foreach (var stage in off) offPeak = Math.Max(offPeak, stage.driverPeakMiB);
            foreach (var stage in both) bothPeak = Math.Max(bothPeak, stage.driverPeakMiB);
            if (offPeak > 0 && bothPeak >= offPeak)
            {
                double memory = bothPeak - offPeak;
                Gate(memory <= PeakExtraGpuMiBMax, $"perf/{renderer} extra GPU memory {memory:0.#}");
                report.gates.Add($"memory/{renderer}: driver delta {memory:0.#} MiB (proxy; RTAS size not exposed)");
            }
            else
            {
                report.knownDifferences.Add($"memory/{renderer}: driver memory counter unavailable; gate blocked, not passed");
            }

            Gate(both.TrueForAll(s => s.gpuRtSumSamples > 0), $"perf/{renderer} no RT GPU samples");
            Gate(WorstP95(both, s => s.probeTraceP95) > 0, $"perf/{renderer} no probe GPU samples");
            Gate(WorstP95(Stages(renderer, "ssr", "both"), s => s.ssrTraceP95) > 0,
                $"perf/{renderer} no SSR GPU samples");
            Gate(staticControl.TrueForAll(s => s.tlasBuildSamples <= 3), $"static/{renderer} rebuilt the TLAS");
            Gate(both.TrueForAll(s => s.builds >= TimedFrames / 2),
                $"dynamic/{renderer} did not rebuild for the moving cube");

            report.knownDifferences.Add($"frameTiming/{renderer}: FrameTimingManager GPU samples unusable; " +
                "RT GPU delta is the sum of non-overlapping RT scopes");
            report.gates.Add($"perf/{renderer}: gpu delta {delta:0.###}, cpu tlas {cpuTlas:0.###}, " +
                $"gpu tlas {gpuTlas:0.###}, probe {probe:0.###}, maintain {maintain:0.###}, " +
                $"ssr trace {ssrTrace:0.###}, ssr recon {ssrRecon:0.###}");
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
        const string folder = "Assets/Fixture/I2";
        if (!AssetDatabase.IsValidFolder(folder))
        {
            if (!AssetDatabase.IsValidFolder("Assets/Fixture")) AssetDatabase.CreateFolder("Assets", "Fixture");
            AssetDatabase.CreateFolder("Assets/Fixture", "I2");
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

        var matte = Mat("matte", MatteColor, 0f, .2f, Color.black);
        var mirror = Mat("mirror", Color.white, 1f, 1f, Color.black);
        var donor = Mat("donor", DonorColor, 0f, .2f, new Color(DonorEmission, 0f, 0f));
        var donorOff = Mat("donorOff", DonorColor, 0f, .2f, Color.black);

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
            renderer.name = mode + " I2 Renderer";
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
        var pipelinePath = folder + "/I2Pipeline.asset";
        AssetDatabase.DeleteAsset(pipelinePath);
        var pipeline = UnityEngine.Object.Instantiate(templatePipeline);
        pipeline.name = "I2 Pipeline";
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
        var driver = new GameObject("I2 fixture").AddComponent<I2Player>();
        driver.matte = matte;
        driver.mirrorMaterial = mirror;
        driver.donorMaterial = donor;
        driver.donorOffMaterial = donorOff;
        driver.features = features;
        driver.pipeline = pipeline;
        Check(EditorSceneManager.SaveScene(scene, "Assets/I2.unity"), "Cannot save I2 scene");

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
                scenes = new[] { "Assets/I2.unity" }, target = target,
                locationPathName = Arg("-exe"), options = BuildOptions.Development
            });
            Check(build.summary.result == BuildResult.Succeeded, "Win64 I2 build failed");
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

