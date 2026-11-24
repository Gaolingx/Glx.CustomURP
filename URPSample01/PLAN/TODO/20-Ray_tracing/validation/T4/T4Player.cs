// Scratch Player only. No camera rendering pass, SSR, SSGI, visibleLights or synthetic RTAS.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif

public sealed class T4Player : MonoBehaviour
{
    const string AssetBase = "Packages/com.unity.render-pipelines.universal/Shaders/RayTracing/GlxBasicHit";
    const int Layer = 31;
    const RayTracingRequests Demand = RayTracingRequests.GI; // infrastructure subscriber only
    static readonly Vector3 Hit = new Vector3(0, 0, 4), N = Vector3.back;
    static readonly Vector3 Blocked = new Vector3(.6f, 0, -.8f);
    [SerializeField] Shader surface;
    [SerializeField] RayTracingShader ray;

    [StructLayout(LayoutKind.Sequential)] struct Request { public Vector4 originTMin, directionTMax; }
    [StructLayout(LayoutKind.Sequential)] struct Payload { public Vector3 rgb; public float t; public uint flags, mode; }
    [Serializable] class Counts
    {
        public int registered, uploaded, uploadOverflow, selected, hitOverflow, shadowRays, shadowOverflow;
        public Counts() { }
        public Counts(int registered, int eligible, int shadows)
        {
            this.registered = registered; uploaded = Math.Min(registered, 64); uploadOverflow = registered - uploaded;
            selected = Math.Min(eligible, 4); hitOverflow = eligible - selected;
            shadowRays = Math.Min(shadows, 2); shadowOverflow = shadows - shadowRays;
        }
        public bool Same(Counts other) => registered == other.registered && uploaded == other.uploaded &&
            uploadOverflow == other.uploadOverflow && selected == other.selected && hitOverflow == other.hitOverflow &&
            shadowRays == other.shadowRays && shadowOverflow == other.shadowOverflow;
    }
    [Serializable] class Batch
    {
        public string name;
        public int frame, buildCount, instances;
        public Vector3 origin, direction, expectedRgb, actualRgb;
        public float expectedT, actualT;
        public uint expectedFlags, actualFlags, actualMode;
        public Counts expected, actual;
        public bool finite, passed;
    }
    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(),
            adapter = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion, denial, error;
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed, released;
        public int buildCount, dispatches, buffersAllocated, buffersReleased, liveAfterRelease;
        public List<Batch> batches = new List<Batch>();
    }
    Report report;
    readonly List<Object> owned = new List<Object>();
    readonly List<RayTracingMember> members = new List<RayTracingMember>();
    RayTracingSceneResources scene;
    RayTracingAccelerationStructure snapshot, initial;
    CameraData view;
    ComputeBuffer rays, results;
    Material mat;
    Transform root;
    T Own<T>(T item) where T : Object { owned.Add(item); return item; }
    static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
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
        // Let each light mutation reach the next Player frame (including deferred Destroy).
        // Exceptions from MoveNext must still write a failed report instead of timing out.
        while (true)
        {
            try { if (!run.MoveNext()) break; }
            catch (Exception ex) { report.error = ex.ToString(); break; }
            yield return run.Current;
        }
        try
        {
            if (results != null) { results.Release(); ++report.buffersReleased; }
            if (rays != null) { rays.Release(); ++report.buffersReleased; }
            if (scene != null)
            {
                report.buildCount = scene.buildCount;
                scene.SetRequests(RayTracingRequests.None);
                report.liveAfterRelease = scene.liveResources;
                report.released = report.liveAfterRelease == 0 && scene.instanceCount == 0 && scene.subscribers == 0 &&
                    LightsReleased() && report.buffersAllocated == report.buffersReleased;
                scene.Dispose();
            }
            for (int i = owned.Count - 1; i >= 0; --i) if (owned[i]) Destroy(owned[i]);
            report.passed = report.error == null && report.released;
        }
        catch (Exception ex) { report.error += ex.ToString(); }
        try { File.WriteAllText(Arg("-result"), JsonUtility.ToJson(report, true)); }
        finally { Application.Quit(report.passed ? 0 : 1); }
    }

    bool LightsReleased() => scene.lightCounters == null && scene.registeredLights == 0 && scene.uploadedLights == 0 && scene.lightUploadCount == 0;

    Counts ReadCounts()
    {
        var words = new uint[4]; scene.lightCounters.GetData(words);
        // Actual counts come only from scene telemetry / production GPU counters (one primary per dispatch).
        return new Counts { registered = scene.registeredLights, uploaded = scene.uploadedLights, uploadOverflow = scene.tableOverflow,
            selected = checked((int)words[0]), hitOverflow = checked((int)words[1]),
            shadowRays = checked((int)words[2]), shadowOverflow = checked((int)words[3]) };
    }

    void Dispatch()
    {
        var cmd = CommandBufferPool.Get("T4 known ray");
        try
        {
            cmd.SetRayTracingShaderPass(ray,"GlxDXR");
            cmd.SetRayTracingAccelerationStructure(ray,Shader.PropertyToID("_GlxScene"),snapshot);
            cmd.SetRayTracingBufferParam(ray,Shader.PropertyToID("_GlxRays"),rays);
            cmd.SetRayTracingBufferParam(ray,Shader.PropertyToID("_GlxResults"),results);
            scene.BindLights(cmd, ray);
            cmd.DispatchRays(ray,"T4Trace",1,1,1,null);
            Graphics.ExecuteCommandBuffer(cmd);
        }
        finally { CommandBufferPool.Release(cmd); }
    }

    bool BuildScene(bool enabled) => scene.TryBuild(view, enabled, true, ray, mat, 1, 1 << Layer, Demand, members, out snapshot);

    Light Lamp(string label, LightType type, Color rgb, float intensity, Vector3 toLight, float distance = 2)
    {
        var go = new GameObject(label); go.transform.SetParent(root, false); go.layer = Layer;
        go.transform.position = Hit + distance * toLight.normalized;
        go.transform.rotation = Quaternion.LookRotation(-toLight.normalized);
        var l = go.AddComponent<Light>(); l.type = type; l.color = rgb; l.intensity = intensity;
        l.range = 8; l.spotAngle = 60; l.innerSpotAngle = 30;
        l.shadows = LightShadows.Hard;
        // The sun predates first Bind and must be discovered. Runtime spawns register explicitly.
        if (initial != null) scene.RegisterLight(l);
        return l;
    }

    void Setup()
    {
        var camera = Own(new GameObject("T4 disabled camera")).AddComponent<Camera>();
        camera.enabled = false; camera.cullingMask = 0; camera.transform.rotation = Quaternion.LookRotation(Vector3.back);
        view = new CameraData { camera = camera, cameraType = CameraType.Game, renderType = CameraRenderType.Base,
            cameraTargetDescriptor = new RenderTextureDescriptor(32,32) };
        root = Own(new GameObject("T4 lights")).transform;
        // Fixed receiver and small blocker. Primary at x=0 clears the blocker. A secondary
        // towards (.6,0,-.8) crosses its interior at (1.5,0,2), 2.5 metres from the receiver.
        var mesh = Own(new Mesh { name = "T4 triangle" });
        mesh.vertices = new[] { new Vector3(-1,-1,0), new Vector3(0,1,0), new Vector3(1,-1,0) };
        mesh.triangles = new[] { 0,1,2 }; mesh.RecalculateNormals(); mesh.RecalculateBounds();
        mat = Own(new Material(surface)); mat.SetVector("_GlxAlbedo", Vector3.one); mat.SetVector("_GlxEmission", Vector3.zero);
        void Plate(Vector3 pos, float scale)
        {
            var go = Own(new GameObject("T4 plate")); go.layer = Layer; go.transform.position = pos; go.transform.localScale = Vector3.one * scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = mat; r.rayTracingMode = RayTracingMode.Static;
            members.Add(new RayTracingMember(r, (uint)members.Count + 1, 0xff, false));
        }
        Plate(Hit, 1); Plate(new Vector3(1.5f, 0, 2), .25f);
        scene = new RayTracingSceneResources(); scene.SetRequests(Demand);
    }

    static float DistanceGain(float d, float range = 8)
    {
        float x = d / range, window = Mathf.Clamp01(1 - x*x*x*x);
        return window * window / (d*d);
    }
    static float ConeGain(float degrees)
    {
        float cosOuter = Mathf.Cos(30 * Mathf.Deg2Rad), cosInner = Mathf.Cos(15 * Mathf.Deg2Rad);
        float a = Mathf.Clamp01((Mathf.Cos(degrees * Mathf.Deg2Rad) - cosOuter) / (cosInner - cosOuter));
        return a*a;
    }
    void Sample(string name, Vector3 irradiance, Counts counts, bool blocked = false, bool miss = false)
    {
        Check(BuildScene(true) && snapshot != null && (initial == null || ReferenceEquals(initial, snapshot)) &&
            scene.buildCount == 1 && scene.instanceCount == 2, name + ": light change rebuilt or replaced geometry");
        initial = snapshot;
        var row = new Batch { name = name, expected = counts, buildCount = scene.buildCount, frame = Time.frameCount,
            instances = scene.instanceCount, origin = miss ? new Vector3(8,0,0) : Vector3.zero, direction = Vector3.forward,
            expectedRgb = irradiance / Mathf.PI, expectedT = miss ? -1 : Hit.z, expectedFlags = miss ? 1u : blocked ? 6u : 2u };
        report.batches.Add(row);
        rays.SetData(new[] { new Request { originTMin = new Vector4(row.origin.x,0,0,.002f), directionTMax = new Vector4(0,0,1,10) } });
        var output = new[] { new Payload { flags = uint.MaxValue, mode = uint.MaxValue, t = -999, rgb = Vector3.one * -999 } };
        results.SetData(output);
        Dispatch(); ++report.dispatches;
        results.GetData(output); row.actual = ReadCounts();
        var p = output[0]; row.actualMode = p.mode; row.actualFlags = p.flags;
        row.finite = Finite(p.rgb.x) && Finite(p.rgb.y) && Finite(p.rgb.z) && Finite(p.t);
        if (row.finite) { row.actualRgb = p.rgb; row.actualT = p.t; }
        row.passed = row.finite && row.actualMode == 0 && row.actualFlags == row.expectedFlags &&
            Mathf.Abs(p.t - row.expectedT) <= .001f && counts.Same(row.actual);
        for (int i = 0; i < 3; ++i) row.passed &= Mathf.Abs(p.rgb[i] - row.expectedRgb[i]) <= .03f * Mathf.Max(.01f, row.expectedRgb[i]);
        Check(row.passed, name + ": payload or measured production counters do not match oracle");
    }
    static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);

    IEnumerator Suite()
    {
        Check(report.development && report.api == Arg("-expectedApi") && Application.platform == RuntimePlatform.WindowsPlayer &&
            IntPtr.Size == 8 && QualitySettings.activeColorSpace == ColorSpace.Linear && surface && ray, "Invalid Player configuration");
        Setup();
        var sun = Lamp("sun", LightType.Directional, Color.red, 3, Blocked);
        Check(!BuildScene(false) && snapshot == null && scene.liveResources == 0 && LightsReleased(), "Disabled gate allocated resources");
        if (report.api == "Direct3D11")
        {
            report.denial = RayTracingSceneGate.Capture(view,true,true,ray,mat).Evaluate(Demand).ToString();
            Check(report.denial == "Backend" && !BuildScene(true) && snapshot == null && scene.liveResources == 0 &&
                scene.buildCount == 0 && LightsReleased(), "D3D11 did not reject before allocation");
            yield break;
        }
        Check(report.api == "Direct3D12" && report.supportsRayTracing, "DXR required");
        Check(Marshal.SizeOf(typeof(Request)) == 32 && Marshal.SizeOf(typeof(Payload)) == 24, "Unexpected ray/payload ABI");
        rays = new ComputeBuffer(1,32); ++report.buffersAllocated; results = new ComputeBuffer(1,24); ++report.buffersAllocated;
        yield return null;
        Sample("sun blocked", Vector3.zero, new Counts(1,1,1), true);
        sun.shadows = LightShadows.None; yield return null;
        Sample("sun shadows off", Vector3.right * 2.4f, new Counts(1,1,0));
        sun.enabled = false; yield return null;
        Sample("sun disabled", Vector3.zero, new Counts(0,0,0));
        var point = Lamp("point", LightType.Point, Color.green, 12, Blocked, 1); yield return null;
        Sample("point before blocker", Vector3.up * (12*.8f*DistanceGain(1)), new Counts(1,1,1));
        point.transform.position = Hit + 4*Blocked; yield return null;
        Sample("point beyond blocker", Vector3.zero, new Counts(1,1,1), true);
        point.shadows = LightShadows.None; yield return null;
        Sample("moved point unshadowed", Vector3.up * (12*.8f*DistanceGain(4)), new Counts(1,1,0));
        point.transform.position = Hit + 9*Blocked; yield return null;
        Sample("point outside range", Vector3.zero, new Counts(1,0,0));
        Destroy(point.gameObject);
        var spot = Lamp("spot", LightType.Spot, Color.blue, 8, N); spot.shadows = LightShadows.None; yield return null;
        var spotRgb = Vector3.forward * (8*DistanceGain(2));
        Sample("spot inside inner cone", spotRgb, new Counts(1,1,0));
        spot.transform.rotation = Quaternion.Euler(0,22,0); yield return null;
        Sample("spot penumbra", spotRgb * ConeGain(22), new Counts(1,1,0));
        spot.transform.rotation = Quaternion.Euler(0,40,0); yield return null;
        Sample("spot outside cone", Vector3.zero, new Counts(1,0,0));
        spot.transform.rotation = Quaternion.identity; yield return null;
        Sample("spot restored", spotRgb, new Counts(1,1,0));
        Destroy(spot.gameObject); yield return null;
        Sample("active spot removed", Vector3.zero, new Counts(0,0,0));

        // Reverse construction vs sibling order. The early bright spot and third point must
        // NOT displace directionals or either of the first two point keys in the upload.
        var excludedSpot = Lamp("excluded bright spot", LightType.Spot, Color.white, 1000, N);
        Lamp("excluded bright point", LightType.Point, Color.white, 1000, N);
        var far = Lamp("ineligible uploaded point", LightType.Point, Color.white, 1000, N, 9);
        var green = Lamp("winning uploaded point", LightType.Point, Color.green, 80, N);
        green.transform.SetAsFirstSibling(); far.transform.SetSiblingIndex(1);
        Light thirdTie = null;
        for (int i = 61; i >= 0; --i)
        {
            Vector3 dir = i == 0 || i == 61 ? Blocked : i == 2 ? new Vector3(-.96f,0,-.28f) : N;
            var l = Lamp("directional " + i, LightType.Directional, i == 61 ? Color.blue : Color.red,
                i == 61 ? 32 : i <= 2 ? 4 : .125f, dir);
            l.transform.SetAsFirstSibling(); if (i == 2) thirdTie = l;
        }
        // Put the excluded spot first in hierarchy, so a key-only table sort cannot pass.
        excludedSpot.transform.SetAsFirstSibling();
        yield return null;
        // 66 registered, 64 uploaded, 63 in range, 4 selected, 4 shadow-eligible, 2 traced.
        // Green and blue rank above the 3 equal red lights. Blue alone is occluded;
        // red[0] would also be occluded but must contribute because its shadow is over budget.
        var budget = new Counts(66,63,4);
        var winners = Vector3.right * (4*(.8f + 1)) + Vector3.up * (80*DistanceGain(2));
        Sample("64/4/2 stable winners", winners, budget, true); yield return null;
        Sample("stable winners repeated", winners, budget, true);
        green.shadows = LightShadows.None; yield return null;
        Sample("shadow budget skips unshadowed winner", winners - Vector3.right * (4*.8f), new Counts(66,63,3), true);
        green.shadows = LightShadows.Hard;
        thirdTie.transform.SetAsFirstSibling(); yield return null;
        Sample("tie broken by new sibling key", winners + Vector3.right*(4*(.28f-1)), budget, true);
        yield return null;
        Sample("miss does not select lights", Vector3.zero, new Counts(66,0,0), false, true);
    }

#if UNITY_EDITOR
    public static void Build()
    {
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        const string scenePath = "Assets/T4.unity";
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
        var scratch = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var driver = new GameObject("T4 independent lighting fixture").AddComponent<T4Player>();
        driver.surface = AssetDatabase.LoadAssetAtPath<Shader>(AssetBase + ".shader");
        driver.ray = AssetDatabase.LoadAssetAtPath<RayTracingShader>("Assets/Fixture/T4Probe.raytrace");
        Check(driver.surface && driver.ray && EditorSceneManager.SaveScene(scratch, scenePath), "Cannot prepare fixture scene");
        var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scenePath }, target = target,
            locationPathName = Arg("-exe"), options = BuildOptions.Development });
        Check(build.summary.result == BuildResult.Succeeded, "Win64 T4 build failed");
    }
#endif
}
