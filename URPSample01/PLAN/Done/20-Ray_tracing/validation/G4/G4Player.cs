// G4 producer fixture: default-on relocation and classification. No camera rendering, no
// SSGI/SSR consumer. Drives the production probe cache and reads back offsets, state and tiles.
using System;
using System.Collections;
using System.Collections.Generic;
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

public sealed class G4Player : MonoBehaviour
{
    const string Pkg = "Packages/com.unity.render-pipelines.universal/Shaders/RayTracing/";
    const RayTracingRequests Demand = RayTracingRequests.GI;
    const int IrrTile = 8, DistTile = 16;
    static readonly int CenterProbe = 3 + 8 * (1 + 4 * 3);    // (-0.5, 1.5, -0.5), empty room centre
    static readonly int NearWallProbe = 3 + 8 * (0 + 4 * 0);  // (-0.5, 0.5, -3.5), near the back wall
    static readonly int RelocatedProbe = 6 + 8 * (1 + 4 * 4); // (2.5, 1.5, 0.5), enclosed by a box
    static readonly int[] RelocatedNeighbours = { 141, 143, 134, 150, 110, 174 };

    [SerializeField] Shader lit;
    [SerializeField] RayTracingShader trace;
    [SerializeField] ComputeShader update;

    [Serializable] class Probe
    {
        public string name;
        public int index;
        public uint published, state;
        public float offsetX, offsetY, offsetZ, length, irradianceRed;
        public bool finite, passed;
    }
    [Serializable] class Batch
    {
        public string name;
        public uint traced, fixedRays, rotated, hits, misses, active, inactive, shadows;
        public int relocated;
        public bool passed;
    }
    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(),
            adapter = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion, denial, error;
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed, released;
        public int buildCount, batches, probes = 256, budget = 16, raysPerProbe = 128;
        public uint lightsRegistered, lightsUploaded;
        public bool allocated;
        public List<Batch> frames = new List<Batch>();
        public List<Probe> views = new List<Probe>();
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
    Transform root;
    int frame, lastRevision;

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
            if (producer != null) { report.allocated = producer.allocated; producer.Dispose(); }
            if (scene != null)
            {
                report.buildCount = scene.buildCount;
                report.lightsRegistered = (uint)scene.registeredLights;
                report.lightsUploaded = (uint)scene.uploadedLights;
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
        var camera = new GameObject("G4 disabled camera").AddComponent<Camera>();
        camera.enabled = false; camera.cullingMask = 0;
        view = new CameraData
        {
            camera = camera, cameraType = CameraType.Game, renderType = CameraRenderType.Base,
            cameraTargetDescriptor = new RenderTextureDescriptor(32, 32)
        };
        root = new GameObject("G4 scene").transform;
        var matte = Mat("matte", new Color(.5f, .5f, .5f));
        var dark = Mat("dark", Color.black);
        Box("floor", new Vector3(0, -.05f, 0), new Vector3(8, .1f, 8), matte, 1);
        Box("ceiling", new Vector3(0, 4.05f, 0), new Vector3(8, .1f, 8), matte, 2);
        Box("back", new Vector3(0, 2, -4.05f), new Vector3(8, 4, .1f), matte, 3);
        Box("front", new Vector3(0, 2, 4.05f), new Vector3(8, 4, .1f), matte, 4);
        Box("left", new Vector3(-4.05f, 2, 0), new Vector3(.1f, 4, 8), matte, 5);
        Box("right", new Vector3(4.05f, 2, 0), new Vector3(.1f, 4, 8), matte, 6);
        Box("relocation box", GridPosition(RelocatedProbe), new Vector3(.8f, .8f, .8f), dark, 7)
            .GetComponent<SsrRayTracingGeometry>().enableTriangleCulling = false;
        // The room is closed, so the directional light is always shadowed inside; the interior
        // point light keeps the probe tiles measurable for the preservation check.
        var sun = new GameObject("sun"); sun.transform.SetParent(root, false);
        var light = sun.AddComponent<Light>();
        light.type = LightType.Directional; light.color = Color.white; light.intensity = 1;
        light.transform.rotation = Quaternion.Euler(50, 30, 0); light.shadows = LightShadows.Hard;
        var lamp = new GameObject("lamp"); lamp.transform.SetParent(root, false);
        lamp.transform.position = new Vector3(0, 2, 0);
        var point = lamp.AddComponent<Light>();
        point.type = LightType.Point; point.color = Color.white; point.intensity = 4;
        point.range = 10; point.shadows = LightShadows.Hard;
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

    GameObject Box(string label, Vector3 position, Vector3 scale, Material material, int id)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = label; go.transform.SetParent(root, false);
        go.transform.position = position; go.transform.localScale = scale;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = material; r.rayTracingMode = RayTracingMode.DynamicTransform;
        go.AddComponent<SsrRayTracingGeometry>().instanceId = id;
        return go;
    }

    bool Collect()
    {
        witness = SsrRayTracingGeometry.Collect(view.camera.gameObject.scene, members, ids, out int revision);
        lastRevision = revision;
        return witness != null;
    }

    bool RunBatch(string name)
    {
        if (!Collect()) { Check(false, name + ": no registered geometry witness"); return false; }
        bool ok = producer.TryUpdate(frame, view, scene, witness, lastRevision, ~0, members, out snapshot);
        if (!ok) return false;
        ++frame;
        var counters = new uint[8];
        producer.ReadCounters(counters);
        var light = new uint[4];
        scene.lightCounters.GetData(light);
        var offsets = producer.ReadProbeData();
        int relocated = 0;
        foreach (var data in offsets)
            if (new Vector3(data.x, data.y, data.z).sqrMagnitude > 1e-8f) ++relocated;
        var row = new Batch
        {
            name = name, traced = counters[0], fixedRays = counters[1], rotated = counters[2],
            hits = counters[3], misses = counters[4], active = counters[5], inactive = counters[6],
            shadows = light[2], relocated = relocated
        };
        report.frames.Add(row);
        ++report.batches;
        row.passed = row.active + row.inactive == report.budget && row.fixedRays == 32 * report.budget &&
            row.rotated == 96 * row.active && row.traced == row.fixedRays + row.rotated &&
            row.traced <= report.budget * report.raysPerProbe && row.hits + row.misses == row.traced &&
            row.shadows <= row.traced * 2 && scene.registeredLights == 2 && scene.uploadedLights == 2;
        Check(row.passed, $"{name}: producer counters violate the frozen budget (traced={row.traced} " +
            $"fixed={row.fixedRays} rotated={row.rotated} active={row.active} inactive={row.inactive})");
        return true;
    }

    void Run(int count, string label) { for (int i = 0; i < count; ++i) RunBatch(label + " " + i); }

    Probe ReadProbe(int probe, string name)
    {
        var irr = new Vector4[IrrTile * IrrTile];
        var dist = new Vector4[DistTile * DistTile];
        producer.ReadProbe(probe, irr, dist, out uint published, out uint state);
        var data = producer.ReadProbeData()[probe];
        Vector4 center = Vector4.zero;
        for (int y = 1; y <= 6; ++y)
        for (int x = 1; x <= 6; ++x) center += irr[y * IrrTile + x];
        center /= 36f;
        var row = new Probe
        {
            name = name, index = probe, published = published, state = state,
            offsetX = data.x, offsetY = data.y, offsetZ = data.z,
            length = new Vector3(data.x, data.y, data.z).magnitude, irradianceRed = center.x
        };
        row.finite = Finite(row.length) && Finite(row.irradianceRed);
        if (name != null) report.views.Add(row);
        return row;
    }

    static Vector3 GridPosition(int probe)
    {
        return new Vector3(-3.5f, .5f, -3.5f) + new Vector3(probe % 8, probe / 8 % 4, probe / 32);
    }

    IEnumerator Suite()
    {
        Check(report.development && report.api == Arg("-expectedApi") && Application.platform == RuntimePlatform.WindowsPlayer &&
            IntPtr.Size == 8 && QualitySettings.activeColorSpace == ColorSpace.Linear && lit && trace && update,
            "Invalid Player configuration");
        Setup();
        var config = RayTracingProbeConfig.Default;
        Check(config.relocation && config.classification, "G4 toggles are not on by default");
        producer = new RayTracingProbeUpdate(config, trace, update);

        if (report.api == "Direct3D11")
        {
            Collect(); // the D3D11 shader build strips GlxDXR, so a witness is not required
            report.denial = RayTracingSceneGate.Capture(view, true, true, trace, witness).Evaluate(Demand).ToString();
            Check(report.denial == "Backend", "D3D11 gate decision changed");
            Check(!producer.TryUpdate(0, view, scene, witness, lastRevision, ~0, members, out snapshot),
                "D3D11 dispatched a probe batch");
            Check(!producer.allocated && scene.liveResources == 0 && scene.buildCount == 0,
                "D3D11 allocated RT resources");
            report.passed = true;
            yield break;
        }
        Check(report.api == "Direct3D12" && report.supportsRayTracing, "DXR requires D3D12");
        yield return null;

        // One revisit with the enclosing box: the probe is pushed out within 0.45 x spacing,
        // its six axis neighbours keep zero offsets, and the empty centre is inactive.
        Run(17, "enclosed");
        var relocated = ReadProbe(RelocatedProbe, "enclosed probe");
        Check(relocated.published == 1 && relocated.length > .2f && relocated.length <= config.spacing * config.relocationLimit + 1e-3f,
            $"Relocation offset outside (0.2, 0.45]: {relocated.length}");
        foreach (int neighbour in RelocatedNeighbours)
        {
            var view = ReadProbe(neighbour, "relocated neighbour " + neighbour);
            Check(view.length <= 1e-4f, $"Relocation offset leaked into probe {neighbour} ({view.length})");
        }
        var center = ReadProbe(CenterProbe, "empty centre");
        var near = ReadProbe(NearWallProbe, "near wall");
        Check(center.published == 1 && center.state == 0, "Empty-region probe was not classified inactive");
        Check(near.published == 1 && near.state == 1, "Near-wall probe was not classified active");
        Check(center.irradianceRed >= 0 && near.irradianceRed >= 0 && center.finite && near.finite, "Probe readback is not finite");
        int active = 0;
        foreach (var data in producer.ReadProbeData()) if (data.w > .5f) ++active;
        report.views.Add(new Probe { name = "active count", index = -1, irradianceRed = active, finite = true });
        Check(active > 0 && active < report.probes, "Classification did not split active/inactive probes");
        report.checks.Add("enclosed relocation and classification");

        // The offset must be stable, not oscillating.
        Run(4, "enclosed stable");
        var stable = ReadProbe(RelocatedProbe, "enclosed probe stable");
        Check(Mathf.Abs(stable.length - relocated.length) <= 1e-3f, "Relocation offset oscillates");

        // An inactive tile is preserved, not rewritten: the empty centre keeps the same
        // irradiance across batches while its geometry stays unchanged.
        Run(8, "inactive preserve");
        var centerAgain = ReadProbe(CenterProbe, "empty centre preserved");
        Check(centerAgain.state == 0 && Mathf.Abs(centerAgain.irradianceRed - center.irradianceRed) <= 1e-6f,
            "An inactive probe tile was rewritten");

        // Removing the enclosing box clears the offset within one revisit.
        var enclosed = root.Find("relocation box");
        DestroyImmediate(enclosed.gameObject);
        Run(17, "relocation removal");
        var restored = ReadProbe(RelocatedProbe, "relocation removed");
        Check(restored.length <= 1e-3f, "Relocation offset did not return after geometry removal");

        // Geometry enters next to the inactive centre: it must reactivate within one revisit
        // (B0 allows 17 batches), then go inactive again after removal.
        var emissive = Mat("reactivate emissive", new Color(1, .1f, .1f));
        emissive.EnableKeyword("_EMISSION");
        emissive.SetColor("_EmissionColor", new Color(2, .2f, .2f));
        var reactivate = Box("reactivate box", new Vector3(.5f, 1.5f, -.5f), Vector3.one, emissive, 8);
        Run(17, "reactivation");
        var reactivated = ReadProbe(CenterProbe, "reactivated centre");
        Check(reactivated.state == 1, "Probe did not reactivate after geometry appeared");
        // The state flips after this batch's trace. The tile is only written on the next revisit,
        // so a full extra revisit is needed before the new geometry can appear in irradiance.
        Run(16, "reactivation measure");
        var measured = ReadProbe(CenterProbe, "reactivated centre measured");
        Check(measured.state == 1 && measured.irradianceRed > 0, "Reactivated probe did not measure the new geometry");
        DestroyImmediate(reactivate);
        Run(17, "reactivation removal");
        var deactivated = ReadProbe(CenterProbe, "deactivated centre");
        Check(deactivated.state == 0, "Probe did not deactivate after geometry removal");
        report.checks.Add("reactivation and deactivation");

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
        var driver = new GameObject("G4 relocation fixture").AddComponent<G4Player>();
        driver.lit = Shader.Find("Universal Render Pipeline/Lit");
        driver.trace = AssetDatabase.LoadAssetAtPath<RayTracingShader>(Pkg + "GlxProbeTrace.raytrace");
        driver.update = AssetDatabase.LoadAssetAtPath<ComputeShader>(Pkg + "GlxProbeUpdate.compute");
        Check(driver.lit && driver.trace && driver.update && EditorSceneManager.SaveScene(scratch, "Assets/G4.unity"),
            "Cannot prepare G4 fixture scene");
        var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/G4.unity" }, target = target, locationPathName = Arg("-exe"), options = BuildOptions.Development
        });
        Check(build.summary.result == BuildResult.Succeeded, "Win64 G4 build failed");
    }
#endif
}
