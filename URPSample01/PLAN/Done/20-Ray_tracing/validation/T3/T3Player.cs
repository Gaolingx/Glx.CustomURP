// Copied by the runner into a scratch-only Unity.RayTracing.Tests.Editor friend assembly.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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

public sealed class T3Player : MonoBehaviour
{
    [SerializeField] Shader surface;
    [SerializeField] RayTracingShader ray;

    const string AssetBase = "Packages/com.unity.render-pipelines.universal/Shaders/RayTracing/GlxBasicHit";
    const int Layer = 31, RayStride = 32, ResultStride = 24;
    const float PlateZ = 4f, RgbLimit = .03f, DistanceLimit = .001f;
    // A single infrastructure subscription. No SSGI/SSR feature, volume or camera pass.
    const RayTracingRequests Demand = RayTracingRequests.GI;
    static readonly Vector3 ToLight = new Vector3(.6f, 0, -.8f);
    static readonly Vector3 LightRgb = new Vector3(3f, 2f, 1f);
    static readonly Vector3 Emission = new Vector3(2f, .5f, .125f);

    [Serializable, StructLayout(LayoutKind.Sequential)] struct Request
    {
        public Vector4 originTMin, directionTMax;
        public Request(float x, float y, float tMax = 10f)
        {
            originTMin = new Vector4(x, y, 0, .002f);
            directionTMax = new Vector4(0, 0, 1, tMax);
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct Payload
    {
        public Vector3 radiance;
        public float hitT;
        public uint flags, mode;
    }

    [Serializable] class VectorResult
    {
        public string batch, name, nonFiniteReadback;
        public Request input;
        public Vector3 expectedRgb, actualRgb, relativeRgbError;
        public float expectedT, actualT, absTError;
        public uint expectedFlags, actualFlags, expectedMode, actualMode;
        public bool finite, passed;
    }

    [Serializable] class MaterialInfo
    {
        public string renderer, shader;
        public Vector4 emission, albedo;
        public int pass, queue;
    }

    [Serializable] class GateResult
    {
        public string name, decision;
        public bool accepted, hasSnapshot;
        public int builds, instances, liveResources, buffersAllocated, submittedDispatches;
    }

    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(),
            adapter = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion,
            colorSpace = QualitySettings.activeColorSpace.ToString(), error;
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed;
        public Vector3 toLight = ToLight, lightRgb = LightRgb;
        public float nDotL = Vector3.Dot(Vector3.back, ToLight), rgbTolerance = RgbLimit, tToleranceMeters = DistanceLimit;
        public List<MaterialInfo> materials = new List<MaterialInfo>();
        public List<GateResult> gates = new List<GateResult>();
        public List<VectorResult> vectors = new List<VectorResult>();
        public int buildCount, instancesWithBlocker, instancesWithoutBlocker, buffersAllocated, buffersReleased,
            submittedDispatches, submittedRequests, failedVectors, nonFiniteComponents,
            liveSceneResourcesAfterRelease, subscribersAfterRelease, instancesAfterRelease;
        public bool released;
        public string marker = "Glx Basic Hit Trace", markerNote = "No dispatch requested.";
        public int markerGpuBlocks;
        public float markerGpuMs;
        public bool gpuTimingMeasured;
    }

    static string Arg(string key)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, key);
        if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Missing " + key);
        return args[i + 1];
    }

    static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    IEnumerator Start()
    {
        var report = new Report();
        // D3D12 Recorder needs presented frames before and after the sampled dispatch.
        var markerCamera = new GameObject("T3 marker frames").AddComponent<Camera>();
        markerCamera.cullingMask = 0;
        Application.targetFrameRate = 60;
        for (int i = 0; i < 12; ++i) yield return null;
        ProfilingSampler marker = null;
        try
        {
            Check(report.development && Application.platform == RuntimePlatform.WindowsPlayer && IntPtr.Size == 8 &&
                !string.IsNullOrEmpty(report.guid) && report.api == Arg("-expectedApi") &&
                (report.api == "Direct3D11" || report.api == "Direct3D12"), "Wrong build, platform or API");
            Check(QualitySettings.activeColorSpace == ColorSpace.Linear && surface && ray, "Missing linear setup or shader assets");
            Run(report, ref marker);
            Check(report.released && report.failedVectors == 0, "Resource release or vector comparison failed");
            report.passed = true;
        }
        catch (Exception ex) { report.error = ex.ToString(); }

        // Recorder GPU results are delayed (normally three frames). All batches were submitted
        // in one frame: keep a single frame's measurement, never sum repeated reads of it.
        if (marker != null)
        {
            for (int i = 0; i < 32; ++i)
            {
                yield return null;
                try
                {
                    int blocks = marker.gpuSampleCount;
                    float ms = marker.gpuElapsedTime;
                    if (blocks > 0 && Finite(ms) && ms > 0 && blocks >= report.markerGpuBlocks)
                    {
                        report.markerGpuBlocks = blocks; report.markerGpuMs = ms;
                        report.gpuTimingMeasured = true;
                        report.markerNote = "Recorder GPU blocks and aggregate GPU time for one frame, not per-vector timing.";
                    }
                }
                catch (Exception ex) { report.markerNote = ex.Message; break; }
            }
            try { marker.enableRecording = false; } catch (Exception ex) { report.markerNote += " " + ex.Message; }
        }
        int exitCode = 1;
        try
        {
            File.WriteAllText(Arg("-result"), JsonUtility.ToJson(report, true));
            exitCode = report.passed ? 0 : 1;
        }
        finally { Application.Quit(exitCode); }
    }

    void Run(Report report, ref ProfilingSampler marker)
    {
        var owned = new List<Object>();
        T Own<T>(T obj) where T : Object { owned.Add(obj); return obj; }
        var service = new RayTracingSceneResources();
        ComputeBuffer rays = null, results = null;
        try
        {
            var camera = Own(new GameObject("T3 gate camera")).AddComponent<Camera>();
            camera.enabled = false;
            var view = new CameraData { camera = camera, cameraType = CameraType.Game, renderType = CameraRenderType.Base,
                cameraTargetDescriptor = new RenderTextureDescriptor(32, 32) };
            var members = new List<RayTracingMember>();
            // Known triangles in XY, wound toward -Z. Each primary goes through an interior
            // point, never a shared edge. No primitive bounds or imported mesh assumptions.
            var mesh = Own(new Mesh { name = "T3 plate triangle" });
            mesh.vertices = new[] { new Vector3(-1,-1,0), new Vector3(0,1,0), new Vector3(1,-1,0) };
            mesh.triangles = new[] { 0, 1, 2 }; mesh.RecalculateNormals(); mesh.RecalculateBounds();
            MeshRenderer Plate(string name, Vector3 position, float scale, Vector3 emission, Vector3 albedo)
            {
                var go = Own(new GameObject(name)); go.layer = Layer;
                go.transform.position = position; go.transform.localScale = Vector3.one * scale;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                var material = Own(new Material(surface));
                material.SetVector("_GlxEmission", emission); material.SetVector("_GlxAlbedo", albedo);
                r.sharedMaterial = material; r.rayTracingMode = RayTracingMode.Static;
                members.Add(new RayTracingMember(r, (uint)members.Count + 1, 0xff, false));
                var m = r.sharedMaterial;
                report.materials.Add(new MaterialInfo { renderer = name, shader = m.shader.name, queue = m.renderQueue,
                    pass = m.FindPass("GlxDXR"), emission = m.GetVector("_GlxEmission"), albedo = m.GetVector("_GlxAlbedo") });
                return r;
            }
            var white = Plate("White diffuse", new Vector3(0, 0, PlateZ), 1, Vector3.zero, Vector3.one);
            Plate("Emission only", new Vector3(-4, 0, PlateZ), 1, Emission, Vector3.zero);
            Plate("Valid black", new Vector3(4, 0, PlateZ), 1, Vector3.zero, Vector3.zero);
            // At z=2, the receiver's secondary from (0,0,4) crosses x=1.5. Bias shifts
            // this by only .0015 m. Blocker spans x=[1.25,1.75], y=[-.25,.25]: no
            // primary enters it, and the white control ray at y=-.7 clears it as well.
            var blocker = Plate("Secondary blocker", new Vector3(1.5f, 0, 2), .25f, Vector3.zero, Vector3.zero);
            service.SetRequests(Demand);
            RayTracingAccelerationStructure snapshot = null;
            int revision = 1;
            bool BuildScene(bool enabled) => service.TryBuild(view, enabled, true, ray, white.sharedMaterial,
                revision, 1 << Layer, Demand, members, out snapshot);
            void Denied(string name, bool enabled, RayTracingDecision expected)
            {
                var decision = RayTracingSceneGate.Capture(view, enabled, true, ray, white.sharedMaterial).Evaluate(Demand);
                bool accepted = BuildScene(enabled);
                report.gates.Add(new GateResult { name = name, decision = decision.ToString(), accepted = accepted,
                    hasSnapshot = snapshot != null, builds = service.buildCount, liveResources = service.liveResources,
                    instances = service.instanceCount, buffersAllocated = report.buffersAllocated, submittedDispatches = report.submittedDispatches });
                Check(decision == expected && !accepted && snapshot == null && service.buildCount == 0 && service.liveResources == 0 &&
                    service.instanceCount == 0 && report.buffersAllocated == 0 && report.submittedDispatches == 0, name);
            }
            Denied("Disabled", false, RayTracingDecision.Off);
            if (report.api == "Direct3D11") { Denied("D3D11 opt-in", true, RayTracingDecision.Backend); return; }

            Check(BuildScene(true) && snapshot != null && service.buildCount == 1 && service.liveResources == 1,
                "Opt-in build failed: " + RayTracingSceneGate.Capture(view, true, true, ray, white.sharedMaterial).Evaluate(Demand));
            report.instancesWithBlocker = service.instanceCount;
            Check(report.instancesWithBlocker == members.Count, "Missing fixture instance");
            Check(Marshal.SizeOf(typeof(Request)) == RayStride && Marshal.SizeOf(typeof(Payload)) == ResultStride, "Buffer ABI mismatch");
            var input = new[] { new Request(-4, 0), new Request(0, -.7f), new Request(0, 0), new Request(4, 0),
                new Request(8, 0), new Request(0, 0, -1) };
            rays = new ComputeBuffer(input.Length, RayStride); ++report.buffersAllocated;
            results = new ComputeBuffer(input.Length, ResultStride); ++report.buffersAllocated;
            rays.SetData(input);
            report.markerNote = "No positive Recorder GPU timing available; submittedDispatches counts CPU submissions, not GPU samples.";
            try { marker = GlxBasicHitTrace.sampler; marker.enableRecording = true; }
            catch (Exception ex) { marker = null; report.markerNote += " " + ex.Message; }
            void Batch(string name, bool shadows, bool blocked)
            {
                // Poison every slot before EVERY dispatch. Neither zero-initialized results
                // nor a stale successful readback can satisfy the repeated controls.
                var output = new Payload[input.Length];
                for (int i = 0; i < output.Length; ++i) output[i] = new Payload { flags = uint.MaxValue, mode = uint.MaxValue, hitT = -999 };
                results.SetData(output);
                GlxBasicHitTrace.Dispatch(ray, snapshot, rays, results, input.Length, ToLight, LightRgb, shadows);
                ++report.submittedDispatches; report.submittedRequests += input.Length;
                results.GetData(output);
                var direct = LightRgb * (report.nDotL / Mathf.PI);
                void Compare(int i, string label, Vector3 rgb, float t, uint flags) =>
                    Record(report, name, label, input[i], output[i], rgb, t, flags);
                Compare(0, "Emission only", Emission, PlateZ, 2);
                Compare(1, "Unoccluded white", direct, PlateZ, 2);
                Compare(2, "White behind secondary blocker", blocked ? Vector3.zero : direct, PlateZ, blocked ? 6u : 2u);
                Compare(3, "Valid black", Vector3.zero, PlateZ, 2);
                Compare(4, "Miss", Vector3.zero, -1, 1);
                Compare(5, "Invalid interval", Vector3.zero, -1, 0);
            }
            Batch("Shadows on", true, true);
            Batch("Shadows off", false, false);
            blocker.enabled = false; ++revision;
            Check(BuildScene(true) && snapshot != null && service.buildCount == 2, "Blocker removal did not rebuild");
            report.instancesWithoutBlocker = service.instanceCount;
            Check(report.instancesWithoutBlocker == members.Count - 1, "Blocker not removed");
            Batch("Occluder disabled, shadows on", true, false);
        }
        finally
        {
            if (results != null) { results.Release(); ++report.buffersReleased; }
            if (rays != null) { rays.Release(); ++report.buffersReleased; }
            report.buildCount = service.buildCount;
            service.SetRequests(RayTracingRequests.None);
            report.liveSceneResourcesAfterRelease = service.liveResources; report.subscribersAfterRelease = service.subscribers;
            report.instancesAfterRelease = service.instanceCount;
            report.released = report.buffersReleased == report.buffersAllocated && service.liveResources == 0 &&
                service.instanceCount == 0 && service.subscribers == 0;
            service.Dispose();
            for (int i = owned.Count - 1; i >= 0; --i) if (owned[i]) Destroy(owned[i]);
        }
    }

    static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    static void Record(Report report, string batch, string label, Request input, Payload actual, Vector3 rgb, float t, uint flags)
    {
        var row = new VectorResult { name = label, batch = batch, input = input, expectedRgb = rgb, expectedT = t,
            expectedFlags = flags, expectedMode = 0, actualFlags = actual.flags, actualMode = actual.mode, finite = true };
        for (int i = 0; i < 4; ++i)
        {
            float v = i == 3 ? actual.hitT : actual.radiance[i];
            if (!Finite(v))
            {
                // JSON has no NaN/Infinity numbers. Mark the affected field explicitly
                // rather than letting NaN comparisons pass or emitting invalid JSON.
                row.finite = false; ++report.nonFiniteComponents;
                row.nonFiniteReadback += "RGBt"[i] + "=" + v.ToString(CultureInfo.InvariantCulture) + "; ";
                continue;
            }
            if (i == 3) { row.actualT = v; row.absTError = Mathf.Abs(v - t); }
            else { row.actualRgb[i] = v; row.relativeRgbError[i] = Mathf.Abs(v - rgb[i]) / Mathf.Max(Mathf.Abs(rgb[i]), .01f); }
        }
        row.passed = row.finite && row.actualFlags == flags && row.actualMode == 0 &&
            (flags < 2 ? row.actualT == -1 : row.absTError <= DistanceLimit) &&
            row.relativeRgbError.x <= RgbLimit && row.relativeRgbError.y <= RgbLimit && row.relativeRgbError.z <= RgbLimit;
        if (!row.passed) ++report.failedVectors;
        report.vectors.Add(row);
    }

#if UNITY_EDITOR
    public static void Build()
    {
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        const string scenePath = "Assets/T3.unity";
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var driver = new GameObject("T3 basic hit validation").AddComponent<T3Player>();
        driver.surface = AssetDatabase.LoadAssetAtPath<Shader>(AssetBase + ".shader");
        driver.ray = AssetDatabase.LoadAssetAtPath<RayTracingShader>(AssetBase + ".raytrace");
        Check(driver.surface && driver.ray, "Package GlxBasicHit shader assets missing");
        Check(EditorSceneManager.SaveScene(scene, scenePath), "Cannot save scratch scene");
        var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scenePath }, target = target,
            locationPathName = Arg("-exe"), options = BuildOptions.Development });
        Check(build.summary.result == BuildResult.Succeeded, "T3 development build failed");
    }
#endif
}
