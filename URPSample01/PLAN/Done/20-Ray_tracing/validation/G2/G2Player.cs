// G2 producer fixture: no camera rendering, no SSGI/SSR consumer. Drives the production
// probe cache directly and reads back its counters, publication mask, offsets and tiles.
using System;
using System.Collections;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif

public sealed class G2Player : MonoBehaviour
{
    const string Pkg = "Packages/com.unity.render-pipelines.universal/Shaders/RayTracing/";
    const RayTracingRequests Demand = RayTracingRequests.GI;
    const int IrrTile = 8, DistTile = 16;
    static readonly int CenterProbe = 3 + 8 * (1 + 4 * 3);   // grid (-0.5, 1.5, -0.5)
    static readonly int NearWallProbe = 3 + 8 * (0 + 4 * 0); // grid (-0.5, 0.5, -3.5)
    static readonly int RelocatedProbe = 6 + 8 * (1 + 4 * 4); // grid (2.5, 1.5, 0.5)
    static readonly int SettleProbe = 1 + 8 * (2 + 4 * 4);   // grid (-2.5, 2.5, 0.5)

    [SerializeField] Shader lit;
    [SerializeField] RayTracingShader trace;
    [SerializeField] ComputeShader update;

    [Serializable] class Batch
    {
        public string name;
        public int frame;
        public uint traced, fixedRays, rotated, hits, misses, active, inactive;
        public uint lightSelected, lightShadowRays, lightShadowOverflow;
        public bool passed;
        public double submitMs;
    }
    [Serializable] class ProbeView
    {
        public string name;
        public int index;
        public uint published, state;
        public float offsetX, offsetY, offsetZ, length;
        public float irradianceRed, irradianceGreen, irradianceBlue;
        public float momentMean, momentSquared;
        public bool finite, passed;
    }
    [Serializable] class SettlePoint { public int batch; public float red, other; public bool updated; }
    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(),
            adapter = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion, denial, error;
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed, released;
        public int buildCount, updateCount, batches, probes = 256, budget = 16, raysPerProbe = 128, gpuSamples;
        public long residentBytes; public double gpuMs, gpuMsPerBatch; public bool gpuTimingMeasured;
        public bool allocated;
        public double submitAverageMs, submitMaxMs;
        public int publishedAfterFirst, publishedAfterRevisit, revisitBatches, settleBatch, reactivationBatches, relocationBatches;
        public float settlePreRed, settleFinalRed, settleResidualAt256;
        public uint lightsRegistered, lightsUploaded;
        public List<Batch> frames = new List<Batch>();
        public List<ProbeView> views = new List<ProbeView>();
        public List<SettlePoint> settle = new List<SettlePoint>();
        public List<string> checks = new List<string>();
    }

    Report report;
    readonly List<RayTracingMember> members = new List<RayTracingMember>();
    readonly HashSet<uint> ids = new HashSet<uint>();
    RayTracingSceneResources scene;
    RayTracingAccelerationStructure snapshot;
    CameraData view;
    Material witness;
    RayTracingProbeUpdate producer;
    int frame;
    int dispatches;
    double submitTotal, submitMax;
    Light point;
    Transform root;

    static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, name);
        if (i < 0 || i + 1 == args.Length) throw new ArgumentException(name);
        return args[i + 1];
    }

    IEnumerator Start()
    {
        report = new Report();
        var run = Suite();
        while (true)
        {
            try { if (!run.MoveNext()) break; }
            catch (Exception ex) { report.error = ex.ToString(); break; }
            yield return run.Current;
        }
        try
        {
            if (producer != null)
            {
                report.allocated = producer.allocated;
                report.updateCount = Math.Max(report.updateCount, producer.updateCount);
                producer.Dispose();
            }
            if (scene != null)
            {
                report.buildCount = scene.buildCount;
                report.lightsRegistered = (uint)scene.registeredLights;
                report.lightsUploaded = (uint)scene.uploadedLights;
                scene.SetRequests(RayTracingRequests.None);
                report.released = scene.liveResources == 0 && scene.instanceCount == 0 && scene.subscribers == 0;
                scene.Dispose();
            }
            report.submitAverageMs = report.batches > 0 ? submitTotal / report.batches : 0;
            report.submitMaxMs = submitMax;
            report.passed = report.error == null && report.released && report.passed;
        }
        catch (Exception ex) { report.error += ex.ToString(); }
        try { File.WriteAllText(Arg("-result"), JsonUtility.ToJson(report, true)); }
        finally { Application.Quit(report.passed ? 0 : 1); }
    }

    void Setup()
    {
        var camera = new GameObject("G2 disabled camera").AddComponent<Camera>();
        camera.enabled = false; camera.cullingMask = 0;
        view = new CameraData
        {
            camera = camera, cameraType = CameraType.Game, renderType = CameraRenderType.Base,
            cameraTargetDescriptor = new RenderTextureDescriptor(32, 32)
        };
        root = new GameObject("G2 scene").transform;

        var matte = Mat("matte", new Color(.5f, .5f, .5f), 0, .2f, Color.black);
        var mirror = Mat("mirror", Color.white, 1, 1, Color.black);
        var dark = Mat("dark", Color.black, 0, 0, Color.black);
        var donor = Mat("donor", new Color(1, .1f, .1f), 0, 0, new Color(2, .2f, .2f));

        // Closed 8x4x8 room: six thin outward-facing boxes, so rays from inside hit front faces.
        Box("floor", new Vector3(0, -.05f, 0), new Vector3(8, .1f, 8), matte, 1);
        Box("ceiling", new Vector3(0, 4.05f, 0), new Vector3(8, .1f, 8), matte, 2);
        Box("back", new Vector3(0, 2, -4.05f), new Vector3(8, 4, .1f), matte, 3);
        Box("front", new Vector3(0, 2, 4.05f), new Vector3(8, 4, .1f), matte, 4);
        Box("left", new Vector3(-4.05f, 2, 0), new Vector3(.1f, 4, 8), matte, 5);
        Box("right", new Vector3(4.05f, 2, 0), new Vector3(.1f, 4, 8), matte, 6);
        Box("mirror", new Vector3(0, 2, 3.98f), new Vector3(2, 2, .04f), mirror, 7);
        Box("donor", new Vector3(-2, 2, -2), new Vector3(1, 1, .1f), donor, 8);
        Box("wall", new Vector3(2, 1, 0), new Vector3(.1f, 2, 2), dark, 9);
        Box("cube", new Vector3(-1, 1, 1), new Vector3(.5f, .5f, .5f), matte, 10);

        var sun = NewLight("sun", LightType.Directional, Color.white, 1, new Vector3(0, 0, 0));
        sun.transform.rotation = Quaternion.Euler(50, 30, 0);
        point = NewLight("point", LightType.Point, Color.white, 4, new Vector3(-2, 2, 0));
        point.range = 6;
        var spot = NewLight("spot", LightType.Spot, Color.white, 4, new Vector3(2, 3, -2));
        spot.range = 8; spot.spotAngle = 40; spot.innerSpotAngle = 20; spot.transform.LookAt(Vector3.zero);
        foreach (var l in new[] { sun, point, spot }) { l.shadows = LightShadows.Hard; }

        scene = new RayTracingSceneResources();
        scene.SetRequests(Demand);
    }

    Material Mat(string name, Color baseColor, float metallic, float smoothness, Color emission)
    {
        var material = new Material(lit) { name = name };
        material.SetColor("_BaseColor", baseColor);
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        material.SetFloat("_WorkflowMode", 1);
        material.SetColor("_EmissionColor", emission);
        if (emission.maxColorComponent > 0) material.EnableKeyword("_EMISSION");
        else material.DisableKeyword("_EMISSION");
        return material;
    }

    GameObject Box(string label, Vector3 position, Vector3 scale, Material material, int id)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = label; go.transform.SetParent(root, false);
        go.transform.position = position; go.transform.localScale = scale;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = material; r.rayTracingMode = RayTracingMode.DynamicTransform;
        var geometry = go.AddComponent<SsrRayTracingGeometry>();
        geometry.instanceId = id;
        return go;
    }

    Light NewLight(string label, LightType type, Color color, float intensity, Vector3 position)
    {
        var go = new GameObject(label); go.transform.SetParent(root, false); go.transform.position = position;
        var l = go.AddComponent<Light>();
        l.type = type; l.color = color; l.intensity = intensity;
        return l;
    }

    bool Collect()
    {
        witness = SsrRayTracingGeometry.Collect(view.camera.gameObject.scene, members, ids, out int revision);
        lastRevision = revision;
        return witness != null;
    }
    int lastRevision;

    bool RunBatch(string name, RayTracingProbeUpdate target)
    {
        if (!Collect()) { Check(false, name + ": no registered geometry witness"); return false; }
        var watch = Stopwatch.StartNew();
        bool ok = target.TryUpdate(frame, view, scene, witness, lastRevision, ~0, members, out snapshot);
        watch.Stop();
        if (!ok) return false;
        ++frame;
        ++dispatches;
        var counters = new uint[8];
        target.ReadCounters(counters);
        var light = new uint[4];
        scene.lightCounters.GetData(light);
        var row = new Batch
        {
            name = name, frame = frame, traced = counters[0], fixedRays = counters[1], rotated = counters[2],
            hits = counters[3], misses = counters[4], active = counters[5], inactive = counters[6],
            lightSelected = light[0], lightShadowRays = light[2], lightShadowOverflow = light[3],
            submitMs = watch.Elapsed.TotalMilliseconds
        };
        submitTotal += row.submitMs;
        submitMax = Math.Max(submitMax, row.submitMs);
        report.frames.Add(row);
        ++report.batches;
        row.passed = row.active + row.inactive == report.budget && row.fixedRays == 32 * report.budget &&
            row.rotated == 96 * row.active && row.traced == row.fixedRays + row.rotated &&
            row.traced <= report.budget * report.raysPerProbe && row.hits + row.misses == row.traced &&
            row.lightShadowRays <= row.traced * 2 && row.lightSelected <= row.traced * 4 &&
            scene.registeredLights == 3 && scene.uploadedLights == 3;
        Check(row.passed, $"{name}: producer counters violate the frozen budget (traced={row.traced} fixed={row.fixedRays} rotated={row.rotated} active={row.active} shadows={row.lightShadowRays})");
        return true;
    }

    void Run(int count, string label, RayTracingProbeUpdate target)
    {
        for (int i = 0; i < count; ++i) RunBatch(label + " " + i, target);
    }

    ProbeView ReadProbe(RayTracingProbeUpdate target, int probe, string name)
    {
        var irr = new Vector4[IrrTile * IrrTile];
        var dist = new Vector4[DistTile * DistTile];
        target.ReadProbe(probe, irr, dist, out uint published, out uint state);
        var data = target.ReadProbeData();
        Vector4 offset = data[probe];
        // The whole interior avoids making the light-step metric depend on one octahedral bin.
        Vector4 center = Vector4.zero;
        for (int y = 1; y <= 6; ++y)
        for (int x = 1; x <= 6; ++x) center += irr[y * IrrTile + x];
        center /= 36f;
        Vector4 moment = dist[7 * DistTile + 7];
        var row = new ProbeView
        {
            name = name, index = probe, published = published, state = state,
            offsetX = offset.x, offsetY = offset.y, offsetZ = offset.z,
            length = new Vector3(offset.x, offset.y, offset.z).magnitude,
            irradianceRed = center.x, irradianceGreen = center.y, irradianceBlue = center.z,
            momentMean = moment.x, momentSquared = moment.y
        };
        row.finite = Finite(row.irradianceRed) && Finite(row.irradianceGreen) && Finite(row.irradianceBlue) &&
            Finite(row.momentMean) && Finite(row.momentSquared) && Finite(row.length);
        if (name != null) report.views.Add(row);
        return row;
    }

    static void Mark(Report report, string check) => report.checks.Add(check);

    IEnumerator Suite()
    {
        Check(report.development && report.api == Arg("-expectedApi") && Application.platform == RuntimePlatform.WindowsPlayer &&
            IntPtr.Size == 8 && QualitySettings.activeColorSpace == ColorSpace.Linear && lit && trace && update,
            "Invalid Player configuration");
        Setup();
        var config = RayTracingProbeConfig.Default;
        producer = new RayTracingProbeUpdate(config, trace, update);

        if (report.api == "Direct3D11")
        {
            Collect();
            report.denial = RayTracingSceneGate.Capture(view, true, trace, witness).Evaluate(Demand).ToString();
            Check(report.denial == "RayTracingUnsupported", "D3D11 gate decision changed");
            Check(!producer.TryUpdate(0, view, scene, witness, lastRevision, ~0, members, out snapshot),
                "D3D11 dispatched a probe batch");
            Check(!producer.allocated && scene.liveResources == 0 && scene.buildCount == 0 && scene.registeredLights == 0,
                "D3D11 allocated RT resources");
            report.passed = true;
            yield break;
        }

        Check(report.api == "Direct3D12", "DXR requires D3D12");
        Check(report.supportsRayTracing, "Runtime reports no ray tracing support");
        yield return null;

        // Cold: one batch publishes exactly its 16 probes; untouched tiles are invalid, not black.
        Check(RunBatch("cold", producer), "Cold batch rejected");
        report.publishedAfterFirst = (int)CountPublished(producer);
        Check(report.publishedAfterFirst == report.budget, "Cold batch published the wrong probe count");
        var cold = ReadProbe(producer, 255, "unscheduled cold probe");
        Check(cold.published == 0 && cold.irradianceRed == 0 && cold.irradianceGreen == 0 && cold.irradianceBlue == 0 && cold.finite,
            "Unscheduled tile is not an invalid zero tile");
        Mark(report, "cold publication");

        // 15 more batches complete the first revisit: every probe has been published once.
        Run(15, "revisit", producer);
        report.revisitBatches = producer.updateCount;
        report.publishedAfterRevisit = (int)CountPublished(producer);
        Check(report.revisitBatches == 16, "Static revisit is not ceil(N/B) batches");
        Check(report.publishedAfterRevisit == report.probes, "A probe was never published after one revisit");
        Check(producer.generation == 16, "Publication generation drift");
        Mark(report, "revisit period");

        // Classification from the fixed 32 rays: centre of the empty room inactive, near-wall active.
        var data = producer.ReadProbeData();
        int active = 0;
        foreach (var d in data) if (d.w > 0.5) ++active;
        report.views.Add(new ProbeView { name = "active count", index = -1, irradianceRed = active, finite = true });
        Check(active > 0 && active < report.probes, "Classification did not split active/inactive probes");
        var center = ReadProbe(producer, CenterProbe, "room centre");
        var near = ReadProbe(producer, NearWallProbe, "near wall");
        Check(center.published == 1 && center.state == 0, "Empty-region probe was not classified inactive");
        Check(near.published == 1 && near.state == 1, "Near-wall probe was not classified active");
        Check(center.finite && near.finite && near.irradianceRed >= 0, "Probe readback is not finite");
        Mark(report, "classification");

        // Relocation: a culling-disabled box encloses a probe. It must be pushed out within the
        // 0.45 x spacing limit, stay within the limit on later revisits, then return after removal.
        var box = Box("relocation box", GridPosition(config, RelocatedProbe), new Vector3(.8f, .8f, .8f), witness, 70);
        box.GetComponent<SsrRayTracingGeometry>().enableTriangleCulling = false;
        Run(50, "relocation", producer);
        var relocated = ReadProbe(producer, RelocatedProbe, "relocated probe");
        var relocated2 = ReadProbe(producer, RelocatedProbe, "relocated probe stable");
        Check(relocated.length > 0.2f && relocated.length <= config.spacing * config.relocationLimit + 1e-3f,
            "Probe was not pushed out within the relocation limit");
        Check(Math.Abs(relocated2.length - relocated.length) <= 1e-3f, "Relocation offset oscillates");
        report.relocationBatches = 50;
        UnityEngine.Object.DestroyImmediate(box);
        Run(17, "relocation removal", producer);
        var restored = ReadProbe(producer, RelocatedProbe, "relocation removed");
        Check(restored.length <= 1e-3f, "Relocation offset did not return after geometry removal");
        Mark(report, "relocation");

        // Reactivation: geometry appears next to the inactive centre probe; it must reactivate
        // within one revisit (B0 allows 17 frames).
        var reactivate = Box("reactivate box", new Vector3(0.5f, 1.5f, -0.5f), Vector3.one, witness, 71);
        Run(17, "reactivation", producer);
        var reactivated = ReadProbe(producer, CenterProbe, "reactivated centre");
        Check(reactivated.state == 1, "Probe did not reactivate after geometry appeared");
        report.reactivationBatches = 17;
        UnityEngine.Object.DestroyImmediate(reactivate);
        Run(17, "reactivation removal", producer);
        Mark(report, "reactivation");

        // Dynamic settle: maintenance off so the tracked probe keeps updating every revisit.
        var settleConfig = config;
        settleConfig.relocation = false;
        settleConfig.classification = false;
        settleConfig.freezeDirections = true; // measurement control: removes per-revisit direction noise
        var settleProducer = new RayTracingProbeUpdate(settleConfig, trace, update);
        frame = 100000;
        point.intensity = 4;
        Run(32, "settle warmup", settleProducer);
        var pre = ReadProbe(settleProducer, SettleProbe, "settle pre-step");
        var repeat = ReadProbe(settleProducer, SettleProbe, null);
        Check(Math.Abs(repeat.irradianceRed - pre.irradianceRed) <= 1e-6f, "Probe readback is not stable without a batch");
        report.views.Add(new ProbeView { name = "settle frozen directions", index = settleProducer.directionsFrozen ? 1 : 0, finite = true });
        report.settlePreRed = pre.irradianceRed;
        point.intensity = 0;
        float final = 0;
        for (int i = 0; i < 400; ++i)
        {
            RunBatch("settle step " + i, settleProducer);
            var sample = ReadProbe(settleProducer, SettleProbe, null);
            if ((i + 1) % 16 == 0)
            {
                var other = ReadProbe(settleProducer, 0, null);
                report.settle.Add(new SettlePoint
                {
                    batch = i + 1, red = sample.irradianceRed, other = other.irradianceRed,
                    updated = Array.IndexOf(settleProducer.lastBatch, (uint)SettleProbe) >= 0
                });
            }
            final = sample.irradianceRed;
        }
        report.settleFinalRed = final;
        float amplitude = Mathf.Abs(report.settlePreRed - final);
        Check(amplitude > 0.01f, "Settle fixture produced no measurable light step");
        int settleBatch = -1; float residualAt256 = 1;
        for (int i = 0; i < report.settle.Count; ++i)
        {
            float residual = Mathf.Abs(report.settle[i].red - final) / amplitude;
            if (settleBatch < 0 && residual <= 0.10f) settleBatch = report.settle[i].batch;
            if (report.settle[i].batch == 256) residualAt256 = residual;
        }
        report.settleBatch = settleBatch;
        report.settleResidualAt256 = residualAt256;
        Check(settleBatch > 0 && settleBatch <= 256, "Light step did not settle within 256 batches");
        Check(residualAt256 <= 0.05f, "Old light signal fraction exceeds 5% at 256 batches");
        Mark(report, "dynamic settle");

        // Two eligible cameras in one scene frame must produce a single batch.
        int before = settleProducer.updateCount;
        int sharedFrame = 200000;
        Check(settleProducer.TryUpdate(sharedFrame, view, scene, witness, lastRevision, ~0, members, out snapshot),
            "First camera of the shared frame was rejected");
        var presented = new GameObject("G2 presented frames").AddComponent<Camera>();
        presented.cullingMask = 0;
        Application.targetFrameRate = 60;
        for (int i = 0; i < 8; ++i) yield return null; // a presented frame before GPU sampling starts
        RayTracingProbeUpdate.batchSampler.enableRecording = true;
        for (int i = 0; i < 32; ++i)
        {
            RunBatch("gpu timing", producer);
            yield return null;
        }
        for (int i = 0; i < 16; ++i) yield return null;
        report.gpuSamples = RayTracingProbeUpdate.batchSampler.gpuSampleCount;
        report.gpuMs = RayTracingProbeUpdate.batchSampler.gpuElapsedTime;
        report.gpuTimingMeasured = report.gpuSamples > 0 && Finite((float)report.gpuMs) && report.gpuMs > 0;
        report.gpuMsPerBatch = report.gpuTimingMeasured ? report.gpuMs / 32.0 : 0;
        RayTracingProbeUpdate.batchSampler.enableRecording = false;
        report.residentBytes = producer.residentBytes;
        var second = new GameObject("G2 second camera").AddComponent<Camera>();
        second.enabled = false; second.cullingMask = 0;
        var view2 = new CameraData
        {
            camera = second, cameraType = CameraType.Game, renderType = CameraRenderType.Base,
            cameraTargetDescriptor = new RenderTextureDescriptor(32, 32)
        };
        Check(!settleProducer.TryUpdate(sharedFrame, view2, scene, witness, lastRevision, ~0, members, out snapshot),
            "Second camera dispatched a duplicate probe batch");
        Check(settleProducer.updateCount == before + 1, "Shared frame performed more than one update");
        Mark(report, "two cameras one update");
        settleProducer.Dispose();

        report.passed = report.error == null;
        yield return null;
    }

    static Vector3 GridPosition(in RayTracingProbeConfig config, int probe)
    {
        return config.origin + new Vector3(probe % config.countX,
            probe / config.countX % config.countY, probe / (config.countX * config.countY)) * config.spacing;
    }

    static uint CountPublished(RayTracingProbeUpdate producer)
    {
        uint count = 0;
        foreach (var value in producer.ReadPublishedMask()) if (value != 0) ++count;
        return count;
    }

#if UNITY_EDITOR
    public static void Build()
    {
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
        var scratch = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var driver = new GameObject("G2 probe producer fixture").AddComponent<G2Player>();
        driver.lit = Shader.Find("Universal Render Pipeline/Lit");
        driver.trace = AssetDatabase.LoadAssetAtPath<RayTracingShader>(Pkg + "GlxProbeTrace.raytrace");
        driver.update = AssetDatabase.LoadAssetAtPath<ComputeShader>(Pkg + "GlxProbeUpdate.compute");
        Check(driver.lit && driver.trace && driver.update && EditorSceneManager.SaveScene(scratch, "Assets/G2.unity"),
            "Cannot prepare G2 fixture scene");
        var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/G2.unity" }, target = target, locationPathName = Arg("-exe"), options = BuildOptions.Development
        });
        Check(build.summary.result == BuildResult.Succeeded, "Win64 G2 build failed");
    }
#endif
}
