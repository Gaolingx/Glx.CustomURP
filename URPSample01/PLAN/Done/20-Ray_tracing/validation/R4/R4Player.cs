// R4 producer fixture: H-A registration scope, unsupported-material accounting, payload and
// hit-shading cost. No renderer feature, no GI producer, no surface cache resources.
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

public sealed class R4Player : MonoBehaviour
{
    const string Package = "Packages/com.unity.render-pipelines.universal/Shaders/";
    const RayTracingRequests Demand = RayTracingRequests.GI;
    const int RayCount = 2048;
    [SerializeField] RayTracingShader ray;
    [SerializeField] Material[] materials; // emission, cutout, simpleLit, complexLit, transparent, unregistered
    [SerializeField] Texture cutoutMap;

    [StructLayout(LayoutKind.Sequential)] struct Request { public Vector4 originTMin, directionTMax; public uint mode, index, a, b; }
    [StructLayout(LayoutKind.Sequential)] struct Payload { public Vector3 rgb; public float t; public uint flags, mode; }
    [Serializable] class MaterialRow
    {
        public string name, shader; public bool hasDxrPass, supported, instantiated;
        public int dxrPasses, queue, surface; public string keywords;
        public int passes; public string passNames;
    }
    [Serializable] class Shot
    {
        public string name, expected; public Vector3 origin, expectedRgb, actualRgb;
        public float actualT, expectedT, rgbTolerance = .03f;
        public uint flags; public bool hit, finite, passed;
    }
    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(),
            adapter = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion, denial, error;
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed, released;
        public int buildCount, instances, disabledSubmeshes, members, registered, unsupportedInstances, skinnedSkipped;
        public int payloadBytes, requestBytes, dispatchCount, gpuSamples;
        public bool gpuTimingMeasured, probeProducerAllocated, surfaceCacheAllocated;
        public double submitAverageMs, gpuMs, gpuMsPerDispatch;
        public uint lightSelected, lightShadowRays, lightOverflow;
        public string pipeline, foundPassNames;
        public int foundPasses;
        public bool sameShader, hasRayTracingPass, hasDxrName;
        public List<MaterialRow> materialRows = new List<MaterialRow>();
        public List<Shot> shots = new List<Shot>();
    }

    Report report;
    readonly List<RayTracingMember> members = new List<RayTracingMember>();
    readonly HashSet<uint> ids = new HashSet<uint>();
    RayTracingSceneResources scene;
    RayTracingAccelerationStructure snapshot;
    CameraData view;
    Material witness;
    ComputeBuffer rays, results;
    Light sun;
    int dispatches;
    double submitTotal;
    Transform root;

    static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    void AuditShader()
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        report.sameShader = shader && materials.Length > 0 && materials[0] && materials[0].shader == shader;
        if (shader)
        {
            report.foundPasses = shader.passCount;
            var probe = new Material(shader);
            var passInfo = new List<string>();
            for (int p = 0; p < probe.passCount; ++p) passInfo.Add($"{p}:{probe.GetPassName(p)}");
            report.foundPassNames = string.Join("|", passInfo);
            report.hasRayTracingPass = probe.FindPass("RayTracing") >= 0;
            report.hasDxrName = probe.FindPass("GlxDXR") >= 0;
            Destroy(probe);
        }
        var pipeline = GraphicsSettings.currentRenderPipeline;
        report.pipeline = pipeline ? pipeline.GetType().Name : "null";
    }
    static string Arg(string key)
    {
        var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key);
        if (i < 0 || i + 1 == args.Length) throw new ArgumentException(key);
        return args[i + 1];
    }

    IEnumerator Start()
    {
        report = new Report();
        AuditShader();
        var run = Suite();
        while (true)
        {
            try { if (!run.MoveNext()) break; }
            catch (Exception ex) { report.error = ex.ToString(); break; }
            yield return run.Current;
        }        try
        {
            rays?.Release(); results?.Release();
            if (scene != null)
            {
                report.buildCount = scene.buildCount;
                scene.SetRequests(RayTracingRequests.None);
                report.released = scene.liveResources == 0 && scene.instanceCount == 0 && scene.subscribers == 0;
                scene.Dispose();
            }
            report.submitAverageMs = dispatches > 0 ? submitTotal / dispatches : 0;
            report.passed = report.error == null && report.released && report.passed;
        }
        catch (Exception ex) { report.error += ex.ToString(); }
        try { File.WriteAllText(Arg("-result"), JsonUtility.ToJson(report, true)); }
        finally { Application.Quit(report.passed ? 0 : 1); }
    }

    void Setup()
    {
        var camera = new GameObject("R4 disabled camera").AddComponent<Camera>();
        camera.enabled = false; camera.cullingMask = 0;
        view = new CameraData
        {
            camera = camera, cameraType = CameraType.Game, renderType = CameraRenderType.Base,
            cameraTargetDescriptor = new RenderTextureDescriptor(32, 32)
        };
        root = new GameObject("R4 scene").transform;

        var quad = QuadMesh("R4 quad");
        var emission = Plate(quad, new Vector3(0, 0, 2), materials[0], 1);
        var cutout = Plate(quad, new Vector3(3, 0, 2), materials[1], 2);
        Plate(quad, new Vector3(6, 0, 2), materials[2], 3);
        Plate(quad, new Vector3(9, 0, 2), materials[3], 4);
        Plate(quad, new Vector3(12, 0, 2), materials[4], 5);
        var unregistered = Plate(quad, new Vector3(15, 0, 2), materials[5], -1); // no component
        var multi = MultiMesh("R4 multi submesh");
        var multiGo = new GameObject("R4 multi submesh");
        multiGo.transform.SetParent(root, false);
        multiGo.transform.position = new Vector3(18, 0, 2);
        multiGo.AddComponent<MeshFilter>().sharedMesh = multi;
        var multiRenderer = multiGo.AddComponent<MeshRenderer>();
        multiRenderer.sharedMaterials = new[] { materials[0], materials[2] };
        multiRenderer.rayTracingMode = RayTracingMode.Static;
        var multiGeometry = multiGo.AddComponent<SsrRayTracingGeometry>();
        multiGeometry.instanceId = 6; multiGeometry.enableTriangleCulling = false;

        var skinnedGo = new GameObject("R4 skinned");
        var skinned = skinnedGo.AddComponent<SkinnedMeshRenderer>();
        skinned.sharedMesh = quad;
        report.skinnedSkipped = 1;

        var sunGo = new GameObject("R4 sun"); sunGo.transform.SetParent(root, false);
        sun = sunGo.AddComponent<Light>();
        sun.type = LightType.Directional; sun.color = Color.white; sun.intensity = 2;
        sun.transform.rotation = Quaternion.Euler(50, 30, 0);
        sun.shadows = LightShadows.Hard;
        sun.enabled = false; // emission/cutout stages need a light-free hit

        scene = new RayTracingSceneResources();
        scene.SetRequests(Demand);
    }

    Mesh QuadMesh(string name)
    {
        var mesh = new Mesh { name = name };
        // Counter-clockwise from -Z; normals face the +Z travelling test ray.
        mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        return mesh;
    }

    Mesh MultiMesh(string name)
    {
        var mesh = new Mesh { name = name };
        mesh.vertices = new[]
        {
            new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0),
            new Vector3(2, -1, 0), new Vector3(4, -1, 0), new Vector3(4, 1, 0), new Vector3(2, 1, 0)
        };
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back, Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.uv = new[]
        {
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1)
        };
        mesh.subMeshCount = 2;
        mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        mesh.SetTriangles(new[] { 4, 5, 6, 4, 6, 7 }, 1);
        mesh.RecalculateBounds();
        return mesh;
    }

    GameObject Plate(Mesh mesh, Vector3 position, Material material, int id)
    {
        var go = new GameObject(material.name); go.transform.SetParent(root, false);
        go.transform.position = position; go.transform.localScale = new Vector3(.5f, .5f, 1);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material; renderer.rayTracingMode = RayTracingMode.Static;
        if (id >= 0)
        {
            var geometry = go.AddComponent<SsrRayTracingGeometry>();
            geometry.instanceId = id; geometry.enableTriangleCulling = false;
        }
        return go;
    }

    bool BuildScene()
    {
        witness = SsrRayTracingGeometry.Collect(view.camera.gameObject.scene, members, ids, out int revision);
        if (witness == null) return false;
        report.registered = 6;
        report.members = members.Count;
        report.unsupportedInstances = report.registered - report.members;
        if (!scene.TryBuild(view, true, true, ray, witness, revision, ~0, Demand, members, out snapshot)) return false;
        report.instances = scene.instanceCount;
        report.disabledSubmeshes = scene.disabledSubmeshes;
        return true;
    }

    void Dispatch(int width)
    {
        var cmd = CommandBufferPool.Get("R4 known rays");
        try
        {
            cmd.SetRayTracingShaderPass(ray, "GlxDXR");
            cmd.SetRayTracingAccelerationStructure(ray, Shader.PropertyToID("_GlxScene"), snapshot);
            cmd.SetRayTracingBufferParam(ray, Shader.PropertyToID("_R4Rays"), rays);
            cmd.SetRayTracingBufferParam(ray, Shader.PropertyToID("_R4Results"), results);
            cmd.SetRayTracingIntParam(ray, Shader.PropertyToID("_R4Count"), width);
            scene.BindLights(cmd, ray);
            cmd.DispatchRays(ray, "R4Trace", (uint)width, 1, 1, null);
            Graphics.ExecuteCommandBuffer(cmd);
        }
        finally { CommandBufferPool.Release(cmd); }
        ++dispatches;
    }

    Shot Trace(string name, float x, uint mode, string expected)
    {
        var request = new Request
        {
            originTMin = new Vector4(x, 0, 0, .002f), directionTMax = new Vector4(0, 0, 1, 10),
            mode = mode
        };
        rays.SetData(new[] { request });
        results.SetData(new[] { new Payload { rgb = Vector3.one * -999, t = -1, flags = uint.MaxValue } });
        Dispatch(1);
        report.dispatchCount = dispatches;
        var output = new Payload[1];
        results.GetData(output);
        var p = output[0];
        var row = new Shot
        {
            name = name, expected = expected, origin = new Vector3(x, 0, 0), actualRgb = p.rgb,
            actualT = p.t, flags = p.flags
        };
        row.hit = (p.flags & 2u) != 0 && p.t >= 0;
        row.finite = Finite(p.rgb.x) && Finite(p.rgb.y) && Finite(p.rgb.z) && Finite(p.t);
        report.shots.Add(row);
        return row;
    }

    void ExpectMiss(Shot shot, uint flags, string why)
    {
        shot.passed = shot.finite && !shot.hit && shot.flags == flags;
        Check(shot.passed, why);
    }

    void Expect(Shot shot, Vector3 rgb, float hitT, bool hit)
    {
        shot.expectedRgb = rgb;
        shot.expectedT = hitT;
        bool close = true;
        if (hit)
            for (int i = 0; i < 3; ++i)
                close &= Mathf.Abs(shot.actualRgb[i] - rgb[i]) <= shot.rgbTolerance * Mathf.Max(.01f, Mathf.Abs(rgb[i]));
        shot.passed = shot.finite && shot.hit == hit && close && (!hit || Mathf.Abs(shot.actualT - hitT) <= .001f);
        Check(shot.passed, $"{shot.name}: expected {shot.expected} but got rgb={shot.actualRgb} t={shot.actualT} flags={shot.flags}");
    }

    IEnumerator Suite()
    {
        Check(report.development && report.api == Arg("-expectedApi") && Application.platform == RuntimePlatform.WindowsPlayer &&
            IntPtr.Size == 8 && QualitySettings.activeColorSpace == ColorSpace.Linear && ray && materials.Length == 6 && cutoutMap,
            "Invalid Player configuration");
        report.payloadBytes = Marshal.SizeOf(typeof(Payload));
        report.requestBytes = Marshal.SizeOf(typeof(Request));
        Check(report.payloadBytes == 24 && report.requestBytes == 48, "Unexpected payload/request ABI");
        Setup();
        CollectMaterials();
        // Scene material assets expose a placeholder shader until the shader is first used, so a
        // FindPass before any rendering returns -1. Render the fixture once to load it.
        var warm = new GameObject("R4 warm camera").AddComponent<Camera>();
        warm.clearFlags = CameraClearFlags.SolidColor;
        warm.backgroundColor = Color.black;
        warm.cullingMask = ~0;
        warm.targetTexture = new RenderTexture(64, 64, 16);
        for (int i = 0; i < 4; ++i) yield return null;
        warm.targetTexture = null;
        Destroy(warm.gameObject);
        AuditShader();
        CollectMaterials();
        if (report.api == "Direct3D11")
        {
            report.denial = RayTracingSceneGate.Capture(view, true, true, ray, materials[0]).Evaluate(Demand).ToString();
            Check(report.denial == "Backend", "D3D11 gate decision changed");
            Check(!BuildScene() && scene.liveResources == 0 && scene.buildCount == 0 && scene.registeredLights == 0,
                "D3D11 allocated RT resources");
            report.passed = true;
            yield break;
        }
        Check(report.api == "Direct3D12" && report.supportsRayTracing, "DXR requires D3D12");
        Check(BuildScene(), "RT scene build failed");
        report.probeProducerAllocated = false;
        report.surfaceCacheAllocated = false;

        // Registered subset only: emission, cutout and the partially supported multi-submesh cube.
        Check(report.members == 3 && report.instances == 3 && report.unsupportedInstances == 3 && report.disabledSubmeshes == 1,
            $"Registration scope changed (members={report.members} instances={report.instances} unsupported={report.unsupportedInstances} disabled={report.disabledSubmeshes})");
        var support = report.materialRows;
        Check(support.Count == 6 && support[0].supported && support[1].supported && !support[2].supported &&
            !support[3].supported && !support[4].supported && support[5].supported, "Material support table changed");

        rays = new ComputeBuffer(RayCount, 48);
        results = new ComputeBuffer(RayCount, 24);
        yield return null;

        uint probe = 4u; // GlxProbeMode: emission + light table (no lights yet)
        var shot = Trace("lit emission", 0, probe, "hit emission");
        Expect(shot, new Vector3(2, .5f, .125f), 2, true);
        shot = Trace("cutout solid", 3.4f, probe, "alpha 1 hit");
        Expect(shot, new Vector3(0, 1, 0), 2, true);
        shot = Trace("cutout hole", 2.6f, probe, "alpha 0 miss");
        ExpectMiss(shot, 1u, "Cutout hole was not a miss");
        shot = Trace("simple lit unsupported", 6, probe, "not registered");
        ExpectMiss(shot, 1u, "SimpleLit instance entered the TLAS");
        shot = Trace("complex lit unsupported", 9, probe, "not registered");
        ExpectMiss(shot, 1u, "ComplexLit instance entered the TLAS");
        shot = Trace("transparent unsupported", 12, probe, "not registered");
        ExpectMiss(shot, 1u, "Transparent instance entered the TLAS");
        shot = Trace("unregistered lit", 15, probe, "no registration component");
        ExpectMiss(shot, 1u, "Unregistered Lit entered the TLAS");
        shot = Trace("multi submesh lit", 17.5f, probe, "supported submesh hit");
        Expect(shot, new Vector3(2, .5f, .125f), 2, true);
        shot = Trace("multi submesh simple lit", 20.5f, probe, "unsupported submesh miss");
        ExpectMiss(shot, 1u, "Unsupported submesh entered the TLAS");

        // Hit shading cost: one registered quad, one shadowed directional light, 2048 rays.
        sun.enabled = true;
        yield return null;
        var marker = new ProfilingSampler("R4 Hit Shading") { enableRecording = true };
        var raysData = new Request[RayCount];
        for (int i = 0; i < RayCount; ++i)
            raysData[i] = new Request { originTMin = new Vector4(3.4f, 0, 0, .002f), directionTMax = new Vector4(0, 0, 1, 10), mode = probe };
        rays.SetData(raysData);
        for (int frame = 0; frame < 32; ++frame)
        {
            var cmd = CommandBufferPool.Get("R4 timing");
            try
            {
                cmd.SetRayTracingShaderPass(ray, "GlxDXR");
                cmd.SetRayTracingAccelerationStructure(ray, Shader.PropertyToID("_GlxScene"), snapshot);
                cmd.SetRayTracingBufferParam(ray, Shader.PropertyToID("_R4Rays"), rays);
                cmd.SetRayTracingBufferParam(ray, Shader.PropertyToID("_R4Results"), results);
                cmd.SetRayTracingIntParam(ray, Shader.PropertyToID("_R4Count"), RayCount);
                scene.BindLights(cmd, ray);
                using (new ProfilingScope(cmd, marker)) cmd.DispatchRays(ray, "R4Trace", RayCount, 1, 1, null);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Graphics.ExecuteCommandBuffer(cmd);
                watch.Stop(); submitTotal += watch.Elapsed.TotalMilliseconds; ++dispatches;
            }
            finally { CommandBufferPool.Release(cmd); }
            yield return null;
        }
        for (int i = 0; i < 16; ++i) yield return null;
        report.gpuSamples = marker.gpuSampleCount;
        report.gpuMs = marker.gpuElapsedTime;
        report.gpuTimingMeasured = marker.gpuSampleCount > 0 && Finite(marker.gpuElapsedTime) && marker.gpuElapsedTime > 0;
        report.gpuMsPerDispatch = report.gpuTimingMeasured ? report.gpuMs / 32.0 : 0;
        marker.enableRecording = false;
        var lightCounters = new uint[4];
        scene.lightCounters.GetData(lightCounters);
        report.lightSelected = lightCounters[0]; report.lightShadowRays = lightCounters[2]; report.lightOverflow = lightCounters[3];
        report.dispatchCount = dispatches;
        report.passed = true;
        yield return null;
    }

    void CollectMaterials()
    {
        report.materialRows.Clear();
        var names = new[] { "lit emission", "lit cutout", "simple lit", "complex lit", "transparent lit", "unregistered lit" };
        for (int i = 0; i < materials.Length; ++i)
        {
            var m = materials[i];
            var shader = m.shader;
            var passInfo = new List<string>();
            for (int p = 0; p < shader.passCount; ++p)
            {
                string passName = m.GetPassName(p);
                passInfo.Add($"{p}:{passName}:{(passName.Length > 0 && m.GetShaderPassEnabled(passName) ? "on" : "off")}");
            }
            var row = new MaterialRow
            {
                name = names[i], shader = m.shader.name, hasDxrPass = m.FindPass("GlxDXR") >= 0,
                dxrPasses = m.FindPass("GlxDXR") >= 0 ? 1 : 0, queue = m.renderQueue,
                surface = m.HasProperty("_Surface") ? Mathf.RoundToInt(m.GetFloat("_Surface")) : 0,
                keywords = string.Join(" ", m.shaderKeywords),
                passes = shader.passCount, passNames = string.Join("|", passInfo)
            };
            row.supported = row.hasDxrPass && row.surface == 0;
            row.instantiated = i < 6;
            report.materialRows.Add(row);
        }
    }

#if UNITY_EDITOR
    public static void Build()
    {
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
        var scratch = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var driver = new GameObject("R4 material coverage fixture").AddComponent<R4Player>();
        driver.ray = AssetDatabase.LoadAssetAtPath<RayTracingShader>("Assets/Fixture/R4Probe.raytrace");
        var folder = "Assets/Fixture/R4";
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/Fixture", "R4");

        Material Mat(string name, Shader shader, Color baseColor, Color emission, float metallic, float smoothness)
        {
            var path = $"{folder}/{name}.mat";
            AssetDatabase.DeleteAsset(path);
            var material = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(material, path);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", emission);
                if (emission.maxColorComponent > 0) material.EnableKeyword("_EMISSION");
            }
            EditorUtility.SetDirty(material);
            return material;
        }

        var lit = Shader.Find("Universal Render Pipeline/Lit");
        var simple = Shader.Find("Universal Render Pipeline/Simple Lit");
        var complex = Shader.Find("Universal Render Pipeline/Complex Lit");
        var emission = Mat("emission", lit, Color.white, new Color(2, .5f, .125f), 0, 0);
        var cutout = Mat("cutout", lit, Color.white, new Color(0, 1, 0), 0, 0);
        var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false) { name = "cutout alpha", filterMode = FilterMode.Point };
        for (int y = 0; y < 8; ++y) for (int x = 0; x < 8; ++x) texture.SetPixel(x, y, new Color(1, 1, 1, x < 4 ? 0 : 1));
        texture.Apply();
        var texturePath = $"{folder}/cutout.asset";
        AssetDatabase.DeleteAsset(texturePath);
        AssetDatabase.CreateAsset(texture, texturePath);
        cutout.SetTexture("_BaseMap", texture);
        cutout.SetFloat("_Cutoff", .5f);
        cutout.EnableKeyword("_ALPHATEST_ON");
        cutout.EnableKeyword("_EMISSION");
        EditorUtility.SetDirty(cutout);
        var simpleMat = Mat("simple", simple, Color.white, Color.black, 0, 0);
        var complexMat = Mat("complex", complex, Color.white, Color.black, 0, 0);
        var transparent = Mat("transparent", lit, new Color(1, 1, 1, .5f), Color.black, 0, .5f);
        transparent.SetFloat("_Surface", 1);
        transparent.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        transparent.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        transparent.SetFloat("_ZWrite", 0);
        transparent.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        transparent.renderQueue = (int)RenderQueue.Transparent;
        EditorUtility.SetDirty(transparent);
        var unregistered = Mat("unregistered", lit, Color.white, new Color(0, 0, 1), 0, 0);
        AssetDatabase.SaveAssets();
        driver.materials = new[] { emission, cutout, simpleMat, complexMat, transparent, unregistered };
        driver.cutoutMap = texture;
        Check(driver.ray && emission && cutout && simpleMat && complexMat && transparent && unregistered &&
            EditorSceneManager.SaveScene(scratch, "Assets/R4.unity"), "Cannot prepare R4 fixture scene");
        var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/R4.unity" }, target = target, locationPathName = Arg("-exe"), options = BuildOptions.Development
        });
        Check(build.summary.result == BuildResult.Succeeded, "Win64 R4 build failed");
    }
#endif
}
