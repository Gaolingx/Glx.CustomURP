// G5 probe feedback fixture. Part A drives the production probe raygen at one registered quad
// and reads the closest hit's feedback term (debug channel 20) against the G1 reference; part B
// runs two production probe producers, feedback off and on, and measures the light-step decay.
// No camera rendering, no SSGI/SSR consumer.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif

public sealed class G5Player : MonoBehaviour
{
    const string Pkg = "Packages/com.unity.render-pipelines.universal/Shaders/RayTracing/";
    const RayTracingRequests Demand = RayTracingRequests.GI;
    const int IrrTile = 8, DistTile = 16;
    const int IrrWidth = 64, IrrHeight = 256, DistWidth = 128, DistHeight = 512;
    const float Ambient = 2f, SnapshotIrr = 6f, FeedbackCap = .9f;
    const float MinusOneOverPi = .31830988618f;
    // Same tracked probe as G2's settle control: grid (-2.5, 2.5, 0.5), next to the point light.
    const int SettleProbe = 1 + 8 * (2 + 4 * 4);

    [SerializeField] Shader lit;
    [SerializeField] RayTracingShader trace;
    [SerializeField] ComputeShader update;

    [StructLayout(LayoutKind.Sequential)]
    struct ProbeRay { public Vector4 originTMin, directionTMax; public uint mode, slot, ray, valid; }
    [StructLayout(LayoutKind.Sequential)]
    struct Payload { public Vector3 radiance; public float hitT; public uint flags, mode; }

    [Serializable] class Shot
    {
        public string name, expectedSource;
        public float expected, actual, tolerance = 1e-3f;
        public uint flags;
        public bool hit, finite, passed;
    }
    [Serializable] class Step
    {
        public string name;
        public bool feedback;
        public int batches, active, traced, fixedRays, rotated, shadowRays;
        public float pre, final, residual;
        public bool passed;
    }
    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(),
            adapter = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion, denial, error;
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed, released;
        public int payloadBytes, requestBytes, dispatches, buildCount, feedbackCapBits;
        public bool gpuTimingMeasured;
        public bool giProducerAllocated, ssrPassInstantiated;
        public List<Shot> shots = new List<Shot>();
        public List<Step> steps = new List<Step>();
        public List<string> checks = new List<string>();
    }

    Report report;
    readonly List<RayTracingMember> members = new List<RayTracingMember>();
    readonly HashSet<uint> ids = new HashSet<uint>();
    RayTracingSceneResources scene;
    RayTracingAccelerationStructure snapshot;
    CameraData view;
    Material witness, quadMaterial;
    RayTracingProbeUpdate producerOff, producerOn;
    ComputeBuffer rays, results, counters, state, published;
    Texture2D irradiance, moments;
    GameObject quad, room;
    Light point;
    int frame, dispatches;

    static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, name);
        if (i < 0 || i + 1 == args.Length) throw new ArgumentException(name);
        return args[i + 1];
    }
    static uint[] ZeroCounters() => new uint[8];

    IEnumerator Start()
    {
        report = new Report { feedbackCapBits = BitConverter.SingleToInt32Bits(FeedbackCap) };
        var run = Suite();
        while (true)
        {
            try { if (!run.MoveNext()) break; }
            catch (Exception ex) { report.error = ex.ToString(); break; }
            yield return run.Current;
        }
        try
        {
            producerOff?.Dispose();
            producerOn?.Dispose();
            rays?.Release(); results?.Release(); counters?.Release(); state?.Release(); published?.Release();
            if (irradiance) Destroy(irradiance);
            if (moments) Destroy(moments);
            if (scene != null)
            {
                report.buildCount = scene.buildCount;
                scene.SetRequests(RayTracingRequests.None);
                report.released = scene.liveResources == 0 && scene.instanceCount == 0 && scene.subscribers == 0;
                scene.Dispose();
            }
            report.passed = report.error == null && report.released && report.passed;
        }
        catch (Exception ex) { report.error += ex.ToString(); }
        try { File.WriteAllText(Arg("-result"), JsonUtility.ToJson(report, true)); }
        finally { Application.Quit(report.passed ? 0 : 1); }
    }

    void Setup()
    {
        var camera = new GameObject("G5 disabled camera").AddComponent<Camera>();
        camera.enabled = false; camera.cullingMask = 0;
        view = new CameraData
        {
            camera = camera, cameraType = CameraType.Game, renderType = CameraRenderType.Base,
            cameraTargetDescriptor = new RenderTextureDescriptor(32, 32)
        };
        var mesh = new Mesh { name = "G5 matte quad" };
        mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        quad = new GameObject("G5 matte target");
        quad.transform.position = new Vector3(0, 0, 2);
        quad.AddComponent<MeshFilter>().sharedMesh = mesh;
        quadMaterial = Mat("G5 matte", Color.white);
        var renderer = quad.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = quadMaterial;
        renderer.rayTracingMode = RayTracingMode.Static;
        var geometry = quad.AddComponent<SsrRayTracingGeometry>();
        geometry.instanceId = 1; geometry.enableTriangleCulling = false;

        rays = new ComputeBuffer(1, 48);
        results = new ComputeBuffer(1, 24);
        counters = new ComputeBuffer(8, sizeof(uint));
        scene = new RayTracingSceneResources();
        scene.SetRequests(Demand);
    }

    Material Mat(string name, Color baseColor)
    {
        var material = new Material(lit) { name = name };
        material.SetColor("_BaseColor", baseColor);
        material.SetFloat("_Metallic", 0);
        material.SetFloat("_Smoothness", .2f);
        material.SetFloat("_WorkflowMode", 1);
        material.SetColor("_EmissionColor", Color.black);
        material.DisableKeyword("_EMISSION");
        return material;
    }

    IEnumerator WarmShaders()
    {
        // A scene material asset reports a placeholder one-pass shader until first use.
        var warm = new GameObject("G5 warm camera").AddComponent<Camera>();
        warm.clearFlags = CameraClearFlags.SolidColor;
        warm.backgroundColor = Color.black;
        warm.cullingMask = ~0;
        warm.targetTexture = new RenderTexture(64, 64, 16);
        for (int i = 0; i < 4; ++i) yield return null;
        warm.targetTexture = null;
        Destroy(warm.gameObject);
    }

    bool Collect()
    {
        witness = SsrRayTracingGeometry.Collect(view.camera.gameObject.scene, members, ids, out int revision);
        lastRevision = revision;
        return witness != null;
    }
    int lastRevision;

    bool BuildScene()
    {
        if (!Collect()) return false;
        return scene.TryBuild(view, true, trace, witness, lastRevision, ~0, Demand, members, out snapshot);
    }

    Texture2D ConstantIrr(float e)
    {
        var texture = new Texture2D(IrrWidth, IrrHeight, TextureFormat.RGBAHalf, false, true) { name = "G5 constant irradiance" };
        var pixels = new Color[IrrWidth * IrrHeight];
        for (int i = 0; i < pixels.Length; ++i) pixels[i] = new Color(e, e, e, 1);
        texture.SetPixels(pixels); texture.Apply(false, false);
        return texture;
    }

    Texture2D ConstantMoments(float distance)
    {
        var texture = new Texture2D(DistWidth, DistHeight, TextureFormat.RG16, false, true) { name = "G5 constant moments" };
        var bytes = new byte[DistWidth * DistHeight * 4];
        ushort mean = Mathf.FloatToHalf(distance), square = Mathf.FloatToHalf(distance * distance);
        for (int i = 0; i < DistWidth * DistHeight; ++i)
        {
            BitConverter.GetBytes(mean).CopyTo(bytes, i * 4);
            BitConverter.GetBytes(square).CopyTo(bytes, i * 4 + 2);
        }
        texture.LoadRawTextureData(bytes); texture.Apply(false, false);
        return texture;
    }

    void BuildSnapshot(float e, bool valid, bool publishedAll, bool inactive)
    {
        ReleaseSnapshot();
        irradiance = ConstantIrr(e);
        moments = ConstantMoments(1);
        state = new ComputeBuffer(256, 16);
        var states = new Vector4[256];
        for (int i = 0; i < states.Length; ++i) states[i] = new Vector4(0, 0, 0, inactive ? 0 : 1);
        state.SetData(states);
        published = new ComputeBuffer(256, 4);
        var mask = new uint[256];
        if (publishedAll) for (int i = 0; i < mask.Length; ++i) mask[i] = 1;
        published.SetData(mask);
        var cmd = CommandBufferPool.Get("G5 snapshot globals");
        try
        {
            cmd.SetGlobalInt("_GlxProbeSnapshotValid", valid ? 1 : 0);
            cmd.SetGlobalVector("_GlxProbeIrrSize", new Vector4(IrrWidth, IrrHeight, 1f / IrrWidth, 1f / IrrHeight));
            cmd.SetGlobalVector("_GlxProbeDistSize", new Vector4(DistWidth, DistHeight, 1f / DistWidth, 1f / DistHeight));
            cmd.SetGlobalInt("_GlxProbeCountX", 8); cmd.SetGlobalInt("_GlxProbeCountY", 4); cmd.SetGlobalInt("_GlxProbeCountZ", 8);
            cmd.SetGlobalVector("_GlxProbeOrigin", new Vector4(-3.5f, .5f, -3.5f, 1));
            cmd.SetGlobalFloat("_GlxProbeSpacing", 1);
            cmd.SetGlobalBuffer("_GlxProbeStateSnapshot", state);
            cmd.SetGlobalBuffer("_GlxProbePublishedSnapshot", published);
            cmd.SetGlobalTexture("_GlxProbeIrrSnapshot", irradiance);
            cmd.SetGlobalTexture("_GlxProbeDistSnapshot", moments);
            Graphics.ExecuteCommandBuffer(cmd);
        }
        finally { CommandBufferPool.Release(cmd); }
    }

    void ReleaseSnapshot()
    {
        state?.Release(); published?.Release(); state = published = null;
        if (irradiance) Destroy(irradiance);
        if (moments) Destroy(moments);
        irradiance = moments = null;
    }

    void Dispatch(uint mode, bool feedback, float ambient)
    {
        var request = new ProbeRay
        {
            originTMin = new Vector4(0, 0, 0, .002f),
            directionTMax = new Vector4(0, 0, 1, 10),
            mode = mode, slot = 0, ray = 0, valid = 1
        };
        rays.SetData(new[] { request });
        results.SetData(new[] { new Payload { radiance = Vector3.one * -999, hitT = -1, flags = uint.MaxValue, mode = mode } });
        var cmd = CommandBufferPool.Get("G5 probe ray");
        try
        {
            cmd.SetBufferData(counters, ZeroCounters());
            cmd.SetGlobalInt("_GlxProbeFeedbackEnabled", feedback ? 1 : 0);
            cmd.SetGlobalFloat("_GlxProbeFeedbackAlbedoCap", FeedbackCap);
            cmd.SetGlobalInt("_GlxHitLowFrequencySource", 0);
            cmd.SetGlobalVector("_GlxHitAmbientIrradiance", new Vector4(ambient, ambient, ambient, 0));
            cmd.SetRayTracingShaderPass(trace, "GlxDXR");
            cmd.SetRayTracingAccelerationStructure(trace, Shader.PropertyToID("_GlxScene"), snapshot);
            cmd.SetRayTracingBufferParam(trace, Shader.PropertyToID("_GlxProbeRays"), rays);
            cmd.SetRayTracingBufferParam(trace, Shader.PropertyToID("_GlxProbeResults"), results);
            cmd.SetRayTracingBufferParam(trace, Shader.PropertyToID("_GlxProbeCounters"), counters);
            scene.BindLights(cmd, trace);
            cmd.DispatchRays(trace, "TraceProbeRays", 1, 1, 1, null);
            Graphics.ExecuteCommandBuffer(cmd);
        }
        finally { CommandBufferPool.Release(cmd); }
        ++dispatches;
    }

    Shot Trace(string name, uint mode, bool feedback, float expected, string expectedSource)
    {
        Dispatch(mode, feedback, Ambient);
        var output = new Payload[1];
        results.GetData(output);
        var probeCounters = new uint[8];
        counters.GetData(probeCounters);
        var p = output[0];
        var row = new Shot
        {
            name = name, expectedSource = expectedSource, expected = expected, actual = p.radiance.x, flags = p.flags
        };
        row.hit = (p.flags & 2u) != 0;
        row.finite = Finite(p.radiance.x) && Finite(p.radiance.y) && Finite(p.radiance.z);
        row.passed = row.hit && row.finite && Mathf.Abs(p.radiance.x - expected) <= row.tolerance &&
            probeCounters[0] == 1 && probeCounters[3] == 1 && probeCounters[2] == 0;
        report.shots.Add(row);
        Check(row.passed, $"{name}: expected {expectedSource} {expected} but got {p.radiance} flags={p.flags} traced={probeCounters[0]}");
        return row;
    }

    void BuildRoom()
    {
        room = new GameObject("G5 room");
        var matte = Mat("G5 room matte", new Color(.5f, .5f, .5f));
        Box("floor", new Vector3(0, -.05f, 0), new Vector3(8, .1f, 8), matte, 11);
        Box("ceiling", new Vector3(0, 4.05f, 0), new Vector3(8, .1f, 8), matte, 12);
        Box("back", new Vector3(0, 2, -4.05f), new Vector3(8, 4, .1f), matte, 13);
        Box("front", new Vector3(0, 2, 4.05f), new Vector3(8, 4, .1f), matte, 14);
        Box("left", new Vector3(-4.05f, 2, 0), new Vector3(.1f, 4, 8), matte, 15);
        Box("right", new Vector3(4.05f, 2, 0), new Vector3(.1f, 4, 8), matte, 16);
        var lightObject = new GameObject("G5 point");
        lightObject.transform.SetParent(room.transform, false);
        lightObject.transform.position = new Vector3(-2, 2, 0);
        point = lightObject.AddComponent<Light>();
        point.type = LightType.Point; point.color = Color.white; point.intensity = 4; point.range = 6;
        point.shadows = LightShadows.Hard;
        // The analytic pass already bound an empty table, and authored lights are discovered only at
        // the first Bind. Register the producer-only light explicitly.
        scene.RegisterLight(point);
    }

    GameObject Box(string label, Vector3 position, Vector3 scale, Material material, int id)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = label; go.transform.SetParent(room.transform, false);
        go.transform.position = position; go.transform.localScale = scale;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = material; r.rayTracingMode = RayTracingMode.DynamicTransform;
        var geometry = go.AddComponent<SsrRayTracingGeometry>();
        geometry.instanceId = id;
        return go;
    }

    struct BatchCounters { public int active, traced, fixedRays, rotated, shadowRays; }
    BatchCounters lastOff, lastOn;

    bool RunBatch(RayTracingProbeUpdate target, string name, bool record)
    {
        if (!Collect()) { Check(false, name + ": no registered geometry witness"); return false; }
        bool ok = target.TryUpdate(frame, view, scene, witness, lastRevision, ~0, members, out snapshot);
        if (!ok) return false;
        ++frame;
        var probeCounters = new uint[8];
        target.ReadCounters(probeCounters);
        var light = new uint[4];
        scene.lightCounters.GetData(light);
        bool passed = probeCounters[0] <= 16 * 128 && probeCounters[1] == 32 * 16 &&
            probeCounters[2] == 96 * 16 && probeCounters[3] + probeCounters[4] == probeCounters[0] &&
            probeCounters[5] == 16 && probeCounters[6] == 0 && light[2] <= probeCounters[0] * 2 &&
            scene.registeredLights == 1 && scene.uploadedLights == 1;
        Check(passed, $"{name}: producer counters violate the frozen budget " +
            $"(traced={probeCounters[0]} fixed={probeCounters[1]} rotated={probeCounters[2]} " +
            $"active={probeCounters[5]} inactive={probeCounters[6]} shadows={light[2]})");
        var snapshotCounters = new BatchCounters
        {
            active = (int)probeCounters[5], traced = (int)probeCounters[0], fixedRays = (int)probeCounters[1],
            rotated = (int)probeCounters[2], shadowRays = (int)light[2]
        };
        if (target == producerOn) lastOn = snapshotCounters; else lastOff = snapshotCounters;
        if (record)
            report.steps.Add(new Step
            {
                name = name, feedback = target == producerOn, batches = target.updateCount,
                active = (int)probeCounters[5], traced = (int)probeCounters[0], fixedRays = (int)probeCounters[1],
                rotated = (int)probeCounters[2], shadowRays = (int)light[2], passed = passed
            });
        return true;
    }

    float ReadProbe(RayTracingProbeUpdate target)
    {
        var irr = new Vector4[IrrTile * IrrTile];
        var dist = new Vector4[DistTile * DistTile];
        target.ReadProbe(SettleProbe, irr, dist, out _, out _);
        Vector4 center = Vector4.zero;
        for (int y = 1; y <= 6; ++y)
        for (int x = 1; x <= 6; ++x) center += irr[y * IrrTile + x];
        center /= 36f;
        Check(Finite(center.x) && Finite(center.y) && Finite(center.z), "Probe readback is not finite");
        return center.x;
    }

    IEnumerator Suite()
    {
        Check(report.development && report.api == Arg("-expectedApi") && Application.platform == RuntimePlatform.WindowsPlayer &&
            IntPtr.Size == 8 && QualitySettings.activeColorSpace == ColorSpace.Linear && lit && trace && update,
            "Invalid Player configuration");
        report.payloadBytes = Marshal.SizeOf(typeof(Payload));
        report.requestBytes = Marshal.SizeOf(typeof(ProbeRay));
        Check(report.payloadBytes == 24 && report.requestBytes == 48, "Unexpected ABI");
        Setup();
        yield return WarmShaders();

        if (report.api == "Direct3D11")
        {
            // The D3D11 shader build strips the GlxDXR pass, so Collect may report no witness;
            // the gate must deny on the backend before any material or pass is considered.
            Collect();
            report.denial = RayTracingSceneGate.Capture(view, true, trace, witness).Evaluate(Demand).ToString();
            Check(report.denial == "RayTracingUnsupported", "D3D11 gate decision changed");
            Check(!scene.TryBuild(view, true, trace, witness, lastRevision, ~0, Demand, members, out snapshot),
                "D3D11 allocated a ray tracing scene");
            Check(scene.liveResources == 0 && scene.buildCount == 0, "D3D11 built RT resources");
            report.passed = true;
            yield break;
        }
        Check(report.api == "Direct3D12" && report.supportsRayTracing, "DXR requires D3D12");
        Check(quadMaterial.FindPass("GlxDXR") >= 0, "Lit material has no GlxDXR pass");
        Check(BuildScene(), "RT scene build failed");

        // Part A: the production probe closest hit adds min(albedo, .9) * Eprev / pi once.
        var expectedWhite = FeedbackCap * SnapshotIrr * MinusOneOverPi;
        // _BaseColor is a [Gamma] property: the material stores the linear value of this color.
        var gray = new Color(.5f, .5f, .5f);
        var expectedGray = gray.linear.r * SnapshotIrr * MinusOneOverPi;
        BuildSnapshot(SnapshotIrr, true, true, false);
        Trace("white albedo capped", 20u, true, expectedWhite, "probe feedback");
        quadMaterial.SetColor("_BaseColor", gray);
        Trace("gray albedo below cap", 20u, true, expectedGray, "probe feedback");
        quadMaterial.SetColor("_BaseColor", Color.white);
        Trace("feedback disabled", 20u, false, 0f, "no feedback");
        // Reflection mode must keep its explicit ambient term with feedback both on and off.
        var reflection = .96f * Ambient * MinusOneOverPi;
        Trace("reflection ambient, feedback on", 3u, true, reflection, "ambient");
        Trace("reflection ambient, feedback off", 3u, false, reflection, "ambient");
        BuildSnapshot(SnapshotIrr, false, true, false);
        Trace("invalid snapshot", 20u, true, 0f, "no feedback");
        BuildSnapshot(SnapshotIrr, true, true, true);
        Trace("inactive neighbours", 20u, true, 0f, "no feedback");
        BuildSnapshot(SnapshotIrr, true, false, false);
        Trace("unpublished neighbours", 20u, true, 0f, "no feedback");
        ReleaseSnapshot();
        report.checks.Add("analytic feedback term");

        // Part B: producer light-step with feedback off and on. Freeze directions and disable
        // relocation/classification so both producers trace the same deterministic rays.
        DestroyImmediate(quad);
        yield return null;
        BuildRoom();
        var config = RayTracingProbeConfig.Default;
        config.relocation = false;
        config.classification = false;
        config.freezeDirections = true;
        producerOff = new RayTracingProbeUpdate(config, trace, update);
        var onConfig = config;
        onConfig.feedback = true;
        producerOn = new RayTracingProbeUpdate(onConfig, trace, update);
        yield return null;

        const int Warmup = 128;
        for (int i = 0; i < Warmup; ++i)
        {
            RunBatch(producerOff, "feedback off warmup", false);
            RunBatch(producerOn, "feedback on warmup", false);
        }
        var preOff = ReadProbe(producerOff);
        var preOn = ReadProbe(producerOn);
        report.steps.Add(new Step
        {
            name = "steady feedback off", feedback = false, batches = Warmup, pre = preOff,
            active = lastOff.active, traced = lastOff.traced, fixedRays = lastOff.fixedRays,
            rotated = lastOff.rotated, shadowRays = lastOff.shadowRays, passed = preOff > .01f
        });
        report.steps.Add(new Step
        {
            name = "steady feedback on", feedback = true, batches = Warmup, pre = preOn,
            active = lastOn.active, traced = lastOn.traced, fixedRays = lastOn.fixedRays,
            rotated = lastOn.rotated, shadowRays = lastOn.shadowRays, passed = preOn > .01f
        });
        Check(preOff > .01f, "Feedback-off steady irradiance is not measurable");
        Check(preOn > preOff * 1.01f, $"Feedback did not raise steady irradiance ({preOn} vs {preOff})");
        report.checks.Add("feedback raises steady irradiance");

        point.intensity = 0;
        const int Decay = 256;
        for (int i = 0; i < Decay; ++i)
        {
            RunBatch(producerOff, "feedback off decay", false);
            RunBatch(producerOn, "feedback on decay", false);
            if ((i + 1) % 32 == 0) yield return null;
        }
        var finalOff = ReadProbe(producerOff);
        var finalOn = ReadProbe(producerOn);
        var residualOff = Mathf.Abs(finalOff) / Mathf.Max(Mathf.Abs(preOff), .01f);
        var residualOn = Mathf.Abs(finalOn) / Mathf.Max(Mathf.Abs(preOn), .01f);
        report.steps.Add(new Step
        {
            name = "decay feedback off", feedback = false, batches = Decay, pre = preOff, final = finalOff,
            residual = residualOff, active = lastOff.active, traced = lastOff.traced, fixedRays = lastOff.fixedRays,
            rotated = lastOff.rotated, shadowRays = lastOff.shadowRays, passed = residualOff <= .05f
        });
        report.steps.Add(new Step
        {
            name = "decay feedback on", feedback = true, batches = Decay, pre = preOn, final = finalOn,
            residual = residualOn, active = lastOn.active, traced = lastOn.traced, fixedRays = lastOn.fixedRays,
            rotated = lastOn.rotated, shadowRays = lastOn.shadowRays, passed = residualOn <= .05f
        });
        Check(residualOff <= .05f, $"Feedback-off residual exceeds 5% ({residualOff})");
        Check(residualOn <= .05f, $"Feedback-on residual exceeds 5% ({residualOn})");
        report.checks.Add("feedback decays within 256 batches");

        report.giProducerAllocated = producerOn.allocated;
        report.ssrPassInstantiated = false;
        report.gpuTimingMeasured = false;
        report.dispatches = dispatches;
        report.passed = report.error == null;
        yield return null;
    }

#if UNITY_EDITOR
    public static void Build()
    {
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
        var scratch = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var driver = new GameObject("G5 feedback fixture").AddComponent<G5Player>();
        driver.lit = Shader.Find("Universal Render Pipeline/Lit");
        driver.trace = AssetDatabase.LoadAssetAtPath<RayTracingShader>(Pkg + "GlxProbeTrace.raytrace");
        driver.update = AssetDatabase.LoadAssetAtPath<ComputeShader>(Pkg + "GlxProbeUpdate.compute");
        Check(driver.lit && driver.trace && driver.update && EditorSceneManager.SaveScene(scratch, "Assets/G5.unity"),
            "Cannot prepare G5 fixture scene");
        var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/G5.unity" }, target = target, locationPathName = Arg("-exe"), options = BuildOptions.Development
        });
        Check(build.summary.result == BuildResult.Succeeded, "Win64 G5 build failed");
    }
#endif
}
