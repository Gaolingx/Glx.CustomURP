// R3 shade-source fixture: one registered matte quad hit by a reflection ray. The production
// CHS adds exactly one low-frequency term chosen from ambient, the published probe snapshot,
// or ambient again when the snapshot is invalid/unpublished/inactive. No lights, no GI/SSR pass.
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

public sealed class R3SwitchPlayer : MonoBehaviour
{
    const RayTracingRequests Demand = RayTracingRequests.GI;
    const int IrrWidth = 64, IrrHeight = 256, DistWidth = 128, DistHeight = 512;
    const float Ambient = 2f, Probe = 6f, Albedo = 1f;
    // URP metallic workflow: brdf.diffuse = albedo * (1 - kDielectricSpec.a).
    const float Diffuse = .96f;
    [SerializeField] Shader lit;
    [SerializeField] Material target;
    [SerializeField] RayTracingShader ray;

    [StructLayout(LayoutKind.Sequential)] struct Request { public Vector4 originTMin, directionTMax; public uint mode, index, a, b; }
    [StructLayout(LayoutKind.Sequential)] struct Payload { public Vector3 rgb; public float t; public uint flags, mode; }
    [Serializable] class Shot
    {
        public string name, expected; public float actual, reference, tolerance = 1e-4f;
        public bool hit, finite, passed; public uint flags;
    }
    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(),
            adapter = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion, denial, error;
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed, released;
        public int buildCount, instances, payloadBytes, requestBytes, dispatches;
        public bool giProducerAllocated, ssrPassInstantiated;
        public bool witness, targetAlive;
        public int members, workflow, dxrPass, queue;
        public bool passEnabled;
        public string decision;
        public bool gAsset, gHigh, gWin64, gRt, gCompute, gFormats, gCamera, gXr, gShader;
        public string gBackend;
        public List<Shot> shots = new List<Shot>();
    }

    Report report;
    readonly List<RayTracingMember> members = new List<RayTracingMember>();
    readonly HashSet<uint> ids = new HashSet<uint>();
    RayTracingSceneResources scene;
    RayTracingAccelerationStructure snapshot;
    CameraData view;
    ComputeBuffer rays, results, state, published;
    Texture2D irradiance, moments;
    int dispatches;

    static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    static string Arg(string key)
    {
        var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key);
        if (i < 0 || i + 1 == args.Length) throw new ArgumentException(key);
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
            rays?.Release(); results?.Release(); state?.Release(); published?.Release();
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
        var camera = new GameObject("R3 disabled camera").AddComponent<Camera>();
        camera.enabled = false; camera.cullingMask = 0;
        view = new CameraData
        {
            camera = camera, cameraType = CameraType.Game, renderType = CameraRenderType.Base,
            cameraTargetDescriptor = new RenderTextureDescriptor(32, 32)
        };
        var mesh = new Mesh { name = "R3 matte quad" };
        mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        var go = new GameObject("R3 matte target");
        go.transform.position = new Vector3(0, 0, 2);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = target;
        renderer.rayTracingMode = RayTracingMode.Static;
        AuditMaterial();
        var geometry = go.AddComponent<SsrRayTracingGeometry>();
        geometry.instanceId = 1; geometry.enableTriangleCulling = false;
        scene = new RayTracingSceneResources();
        scene.SetRequests(Demand);
    }

    void AuditMaterial()
    {
        report.targetAlive = target != null;
        if (target == null) return;
        report.workflow = target.HasProperty("_WorkflowMode") ? (int)target.GetFloat("_WorkflowMode") : -999;
        report.dxrPass = target.FindPass("GlxDXR");
        report.passEnabled = target.GetShaderPassEnabled("GlxDXR");
        report.queue = target.renderQueue;
    }

    IEnumerator WarmShaders()
    {
        // A scene material asset reports a placeholder one-pass shader until the shader is used.
        var warm = new GameObject("R3 warm camera").AddComponent<Camera>();
        warm.clearFlags = CameraClearFlags.SolidColor;
        warm.backgroundColor = Color.black;
        warm.cullingMask = ~0;
        warm.targetTexture = new RenderTexture(64, 64, 16);
        for (int i = 0; i < 4; ++i) yield return null;
        warm.targetTexture = null;
        Destroy(warm.gameObject);
        AuditMaterial();
    }

    bool BuildScene()
    {
        var witness = SsrRayTracingGeometry.Collect(view.camera.gameObject.scene, members, ids, out int revision);
        report.witness = witness != null;
        report.members = members.Count;
        if (witness != null)
        {
            report.workflow = witness.HasProperty("_WorkflowMode") ? (int)witness.GetFloat("_WorkflowMode") : -999;
            report.dxrPass = witness.FindPass("GlxDXR");
            report.passEnabled = witness.GetShaderPassEnabled("GlxDXR");
            report.queue = witness.renderQueue;
        }
        report.decision = scene.Prepare(view, true, true, ray, witness, revision, ~0, Demand).ToString();
        var gate = RayTracingSceneGate.Capture(view, true, true, ray, witness);
        report.gAsset = gate.assetEnabled; report.gHigh = gate.highQuality; report.gWin64 = gate.windows64;
        report.gRt = gate.supportsRayTracing; report.gCompute = gate.compute; report.gFormats = gate.formats;
        report.gCamera = gate.validCamera; report.gXr = gate.xr; report.gShader = gate.shaderAndPass;
        report.gBackend = gate.backend.ToString();
        if (witness == null) return false;
        return scene.TryBuild(view, true, true, ray, witness, revision, ~0, Demand, members, out snapshot);
    }

    Texture2D ConstantIrr(float e)
    {
        var texture = new Texture2D(IrrWidth, IrrHeight, TextureFormat.RGBAHalf, false, true) { name = "R3 constant irradiance" };
        var pixels = new Color[IrrWidth * IrrHeight];
        for (int i = 0; i < pixels.Length; ++i) pixels[i] = new Color(e, e, e, 1);
        texture.SetPixels(pixels); texture.Apply(false, false);
        return texture;
    }

    Texture2D ConstantMoments(float distance)
    {
        var texture = new Texture2D(DistWidth, DistHeight, TextureFormat.RG16, false, true) { name = "R3 constant moments" };
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
        var cmd = CommandBufferPool.Get("R3 snapshot globals");
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

    void Dispatch(int width, int source, float ambient)
    {
        var cmd = CommandBufferPool.Get("R3 source-switch ray");
        try
        {
            cmd.SetGlobalInt("_GlxHitLowFrequencySource", source);
            cmd.SetGlobalVector("_GlxHitAmbientIrradiance", new Vector4(ambient, ambient, ambient, 0));
            cmd.SetRayTracingShaderPass(ray, "GlxDXR");
            cmd.SetRayTracingAccelerationStructure(ray, Shader.PropertyToID("_GlxScene"), snapshot);
            cmd.SetRayTracingBufferParam(ray, Shader.PropertyToID("_R3Rays"), rays);
            cmd.SetRayTracingBufferParam(ray, Shader.PropertyToID("_R3Results"), results);
            cmd.SetRayTracingIntParam(ray, Shader.PropertyToID("_R3Count"), width);
            scene.BindLights(cmd, ray);
            cmd.DispatchRays(ray, "R3Trace", (uint)width, 1, 1, null);
            Graphics.ExecuteCommandBuffer(cmd);
        }
        finally { CommandBufferPool.Release(cmd); }
        ++dispatches;
    }

    Shot Trace(string name, int source, float ambient, float expected, string expectedSource)
    {
        var request = new Request
        {
            originTMin = new Vector4(0, 0, 0, .002f), directionTMax = new Vector4(0, 0, 1, 10), mode = 3
        };
        rays.SetData(new[] { request });
        results.SetData(new[] { new Payload { rgb = Vector3.one * -999, t = -1, flags = uint.MaxValue } });
        Dispatch(1, source, ambient);
        var output = new Payload[1];
        results.GetData(output);
        var p = output[0];
        var row = new Shot { name = name, expected = expectedSource, actual = p.rgb.x, reference = expected, flags = p.flags };
        row.hit = (p.flags & 2u) != 0;
        row.finite = Finite(p.rgb.x) && Finite(p.rgb.y) && Finite(p.rgb.z);
        row.passed = row.hit && row.finite && Mathf.Abs(p.rgb.x - expected) <= row.tolerance;
        report.shots.Add(row);
        Check(row.passed, $"{name}: expected {expectedSource} {expected} but got {p.rgb} flags={p.flags}");
        return row;
    }

    IEnumerator Suite()
    {
        Check(report.development && report.api == Arg("-expectedApi") && Application.platform == RuntimePlatform.WindowsPlayer &&
            IntPtr.Size == 8 && QualitySettings.activeColorSpace == ColorSpace.Linear && lit && target && ray,
            "Invalid Player configuration");
        report.payloadBytes = Marshal.SizeOf(typeof(Payload));
        report.requestBytes = Marshal.SizeOf(typeof(Request));
        Check(report.payloadBytes == 24 && report.requestBytes == 48, "Unexpected ABI");
        Setup();
        yield return WarmShaders();
        if (report.api == "Direct3D11")
        {
            var witness = target;
            report.denial = RayTracingSceneGate.Capture(view, true, true, ray, witness).Evaluate(Demand).ToString();
            Check(report.denial == "Backend", "D3D11 gate decision changed");
            Check(!BuildScene() && scene.liveResources == 0 && scene.buildCount == 0, "D3D11 allocated RT resources");
            report.passed = true;
            yield break;
        }
        Check(report.api == "Direct3D12" && report.supportsRayTracing, "DXR requires D3D12");
        Check(BuildScene(), "RT scene build failed");
        report.instances = scene.instanceCount;
        report.giProducerAllocated = false;
        report.ssrPassInstantiated = false;
        rays = new ComputeBuffer(1, 48);
        results = new ComputeBuffer(1, 24);
        yield return null;

        float ambient = Diffuse * Ambient / Mathf.PI;
        float probe = Diffuse * Probe / Mathf.PI;
        yield return null;

        // 1. Ambient only.
        BuildSnapshot(Probe, true, true, false);
        Trace("ambient", 0, Ambient, ambient, "ambient");
        // 2. Valid published probe snapshot.
        Trace("probe published", 1, Ambient, probe, "probe");
        // 3. Snapshot version invalid.
        ReleaseSnapshot(); BuildSnapshot(Probe, false, true, false);
        Trace("version mismatch", 1, Ambient, ambient, "ambient");
        // 4. Published mask empty.
        ReleaseSnapshot(); BuildSnapshot(Probe, true, false, false);
        Trace("unpublished neighbours", 1, Ambient, ambient, "ambient");
        // 5. All neighbours inactive.
        ReleaseSnapshot(); BuildSnapshot(Probe, true, true, true);
        Trace("inactive neighbours", 1, Ambient, ambient, "ambient");
        // 6. Restored: one term, not ambient + probe.
        ReleaseSnapshot(); BuildSnapshot(Probe, true, true, false);
        Trace("probe restored", 1, Ambient, probe, "probe");
        report.dispatches = dispatches;
        report.passed = true;
        yield return null;
    }

#if UNITY_EDITOR
    public static void Build()
    {
        const BuildTarget buildTarget = BuildTarget.StandaloneWindows64;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(buildTarget, false);
        PlayerSettings.SetGraphicsAPIs(buildTarget, new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
        var scratch = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var driver = new GameObject("R3 shade source fixture").AddComponent<R3SwitchPlayer>();
        driver.lit = Shader.Find("Universal Render Pipeline/Lit");
        driver.ray = AssetDatabase.LoadAssetAtPath<RayTracingShader>("Assets/Fixture/R3Probe.raytrace");
        var folder = "Assets/Fixture/R3";
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/Fixture", "R3");
        var path = $"{folder}/target.mat";
        AssetDatabase.DeleteAsset(path);
        var material = new Material(driver.lit) { name = "R3 matte" };
        AssetDatabase.CreateAsset(material, path);
        material.SetColor("_BaseColor", new Color(Albedo, Albedo, Albedo));
        material.SetFloat("_Metallic", 0);
        material.SetFloat("_Smoothness", .2f);
        material.SetFloat("_WorkflowMode", 1);
        material.SetColor("_EmissionColor", Color.black);
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        driver.target = material;
        Check(driver.lit && driver.ray && material && EditorSceneManager.SaveScene(scratch, "Assets/R3.unity"),
            "Cannot prepare R3 fixture scene");
        var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/R3.unity" }, target = buildTarget, locationPathName = Arg("-exe"), options = BuildOptions.Development
        });
        Check(build.summary.result == BuildResult.Succeeded, "Win64 R3 build failed");
    }
#endif
}
