// R5 producer fixture: reflection-mode hits use the production Lit GlxDXR pass (full
// metallic/smoothness/normal/cutout SurfaceData) with the scene light table (directional/point/spot
// + per-light shadows) and its frozen budgets. Known rays are compared to a CPU oracle. No SSR
// pass, no GI producer, no probe feedback. Image-level SS<->HW seams stay in R3.
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

public sealed class R5Player : MonoBehaviour
{
    const RayTracingRequests Demand = RayTracingRequests.GI;
    const int RayCount = 1024;
    const float Pi = Mathf.PI, InvPi = 1f / Mathf.PI, DiffuseScale = .96f;
    static readonly Vector3 Back = new Vector3(0, 0, -1), View = Back;
    static readonly Vector3 NormalMapNormal = new Vector3(.6f, .3f, -.7416198f); // (0.6, 0.3, -sqrt(0.55))
    static readonly Vector3 SunDirection = NormalMapNormal; // normal incidence on the normal-mapped plate
    static readonly Color DielectricColor = new Color(.9f, .55f, .35f);
    static readonly Color MetalColor = new Color(.95f, .8f, .35f);
    static readonly Color NormalColor = new Color(.7f, .7f, .7f);
    static readonly Color CutoutColor = new Color(.2f, .9f, .3f);
    static readonly Color BlockerColor = new Color(.5f, .5f, .5f);
    const float MetalSmoothness = .64f, DielectricSmoothness = .45f, NormalSmoothness = .5f, CutoutSmoothness = .3f;

    static readonly Vector3 DielectricHit = new Vector3(-6, 0, 2);
    static readonly Vector3 dW = new Vector3(.75f, .5f, -.43f).normalized;
    static readonly Vector3 dG = new Vector3(-.15f, -.65f, -.745f).normalized;
    static readonly Vector3 dR = new Vector3(-.75f, .5f, -.43f).normalized;
    static readonly Vector3 dB = new Vector3(.05f, -.3f, -.952f).normalized;
    static readonly Vector3 dP = new Vector3(.8f, .4f, -.45f).normalized;
    static readonly Vector3 dS = new Vector3(.75f, .35f, -.56f).normalized;

    [SerializeField] Material[] materials; // dielectric, metal, normal, cutout, blocker, budgetBlocker
    [SerializeField] RayTracingShader ray;
    [SerializeField] Texture cutoutMap;

    [StructLayout(LayoutKind.Sequential)] struct Request { public Vector4 originTMin, directionTMax; public uint mode, index, a, b; }
    [StructLayout(LayoutKind.Sequential)] struct Payload { public Vector3 rgb; public float t; public uint flags, mode; }
    [Serializable] class Counts
    {
        public int selected, rejected, shadowRays, untraced;
        public bool Same(Counts o) => selected == o.selected && rejected == o.rejected && shadowRays == o.shadowRays && untraced == o.untraced;
    }
    [Serializable] class MaterialRow
    {
        public string name, shader, keywords; public int dxrPass, queue, surface; public bool passEnabled, supported;
    }
    [Serializable] class Shot
    {
        public string name, expectedSource;
        public Vector3 origin, expectedRgb, actualRgb, relativeError;
        public float expectedT, actualT, tolerance = .03f, maxRelativeError;
        public uint expectedFlags, actualFlags;
        public Counts expectedCounts = new Counts(), actualCounts = new Counts();
        public bool hit, finite, countsMatched, passed;
    }
    [Serializable] class ContourResult
    {
        public int hits, expectedHits, misses, expectedMisses, mismatches;
        public List<Shot> rows = new List<Shot>();
        public bool passed;
    }
    [Serializable] class Seam
    {
        public string name; public Vector3 leftActual, leftReference, rightActual, rightReference, crossLeft, crossRight;
        public float ownError, crossError; public bool passed;
    }
    [Serializable] class Report
    {
        public string guid = Application.buildGUID, api = SystemInfo.graphicsDeviceType.ToString(),
            adapter = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion, denial, error;
        public bool development = Debug.isDebugBuild, supportsRayTracing = SystemInfo.supportsRayTracing, passed, released;
        public int buildCount, instances, members, disabledSubmeshes, payloadBytes, requestBytes, dispatchCount;
        public int registeredLights, uploadedLights, tableOverflow;
        public int gpuSamples;
        public bool gpuTimingMeasured, probeProducerAllocated, surfaceCacheAllocated, noRebuild, feedbackIdentical;
        public double submitAverageMs, gpuMs, gpuMsPerDispatch, feedbackDelta;
        public List<MaterialRow> materialRows = new List<MaterialRow>();
        public List<Shot> shots = new List<Shot>();
        public ContourResult contour = new ContourResult();
        public Seam seam = new Seam();
    }

    Report report;
    readonly List<RayTracingMember> members = new List<RayTracingMember>();
    readonly HashSet<uint> ids = new HashSet<uint>();
    readonly List<Lamp> lamps = new List<Lamp>();
    readonly List<Lamp> budget = new List<Lamp>();
    readonly HashSet<Lamp> blocked = new HashSet<Lamp>();
    readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
    RayTracingSceneResources scene;
    RayTracingAccelerationStructure snapshot;
    CameraData view;
    Transform root, lightRoot;
    Material blockerMaterial;
    ComputeBuffer rays, results, probeState, probePublished;
    Texture2D irradiance, moments;
    int dispatches, nextInstanceId = 1;
    double submitTotal;
    Lamp sun, point, spot;
    Mesh quad;

    sealed class Lamp
    {
        public Light light; public string name; public int kind;
        public bool Shadowed => light.shadows != LightShadows.None;
        public void Evaluate(Vector3 position, out Vector3 direction, out float distance, out Vector3 rgb)
        {
            var t = light.transform;
            if (kind == 0)
            {
                direction = -t.forward; distance = float.MaxValue; rgb = Linear(light.color) * light.intensity;
                return;
            }
            Vector3 delta = t.position - position;
            float d2 = delta.sqrMagnitude;
            distance = Mathf.Sqrt(d2);
            direction = delta / Mathf.Max(distance, 1e-10f);
            d2 = Mathf.Max(d2, 6.103515625e-5f);
            float f = d2 / Mathf.Max(0.0001f, light.range * light.range);
            float window = Mathf.Clamp01(1f - f * f);
            float attenuation = window * window / d2;
            if (kind == 2)
            {
                float cosOuter = Mathf.Cos(light.spotAngle * Mathf.Deg2Rad * .5f);
                float cosInner = Mathf.Cos(light.innerSpotAngle * Mathf.Deg2Rad * .5f);
                float inv = 1f / Mathf.Max(0.001f, cosInner - cosOuter);
                float cone = Mathf.Clamp01(Vector3.Dot(-t.forward, direction) * inv - cosOuter * inv);
                attenuation *= cone * cone;
            }
            rgb = Linear(light.color) * light.intensity * attenuation;
        }
    }

    struct Surface
    {
        public Vector3 albedo, normal; public float metallic, smoothness;
    }

    struct Brdf
    {
        public Vector3 diffuse, specular; public float roughness2, normalizationTerm, roughness2MinusOne;
        public static Brdf Create(in Surface s)
        {
            float oneMinusReflectivity = DiffuseScale * (1f - s.metallic);
            float perceptual = 1f - s.smoothness;
            float roughness = Mathf.Max(perceptual * perceptual, .0078125f);
            Brdf b;
            b.diffuse = s.albedo * oneMinusReflectivity;
            b.specular = Vector3.Lerp(new Vector3(.04f, .04f, .04f), s.albedo, s.metallic);
            b.roughness2 = Mathf.Max(roughness * roughness, 6.103515625e-5f);
            b.normalizationTerm = roughness * 4f + 2f;
            b.roughness2MinusOne = b.roughness2 - 1f;
            return b;
        }
        public Vector3 Direct(Vector3 n, Vector3 l, Vector3 v)
        {
            Vector3 h = (l + v).normalized;
            float noH = Mathf.Clamp01(Vector3.Dot(n, h)), loH = Mathf.Clamp01(Vector3.Dot(l, h));
            float d = noH * noH * roughness2MinusOne + 1.00001f;
            float specularTerm = roughness2 / (d * d * Mathf.Max(.1f, loH * loH) * normalizationTerm);
            return diffuse + specularTerm * specular;
        }
    }

    static Vector3 Linear(Color c) { Color l = c.linear; return new Vector3(l.r, l.g, l.b); }
    static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    static string Arg(string key)
    {
        var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key);
        if (i < 0 || i + 1 == args.Length) throw new ArgumentException(key);
        return args[i + 1];
    }
    T Own<T>(T item) where T : UnityEngine.Object { owned.Add(item); return item; }

    IEnumerator Start()
    {
        report = new Report();
        Application.targetFrameRate = 60;
        Application.runInBackground = true;
        var run = Suite();
        while (true)
        {
            try { if (!run.MoveNext()) break; }
            catch (Exception ex) { report.error = ex.ToString(); break; }
            yield return run.Current;
        }
        try
        {
            rays?.Release(); results?.Release();
            ReleaseSnapshot();
            if (scene != null)
            {
                report.buildCount = scene.buildCount;
                scene.SetRequests(RayTracingRequests.None);
                report.released = scene.liveResources == 0 && scene.instanceCount == 0 && scene.subscribers == 0 &&
                    scene.lightCounters == null && scene.lightUploadCount == 0;
                scene.Dispose();
            }
            for (int i = owned.Count - 1; i >= 0; --i) if (owned[i]) Destroy(owned[i]);
            report.passed = report.error == null && report.released && report.passed;
        }
        catch (Exception ex) { report.error += ex.ToString(); }
        try { File.WriteAllText(Arg("-result"), JsonUtility.ToJson(report, true)); }
        finally { Application.Quit(report.passed ? 0 : 1); }
    }

    // ---------- scene ----------

    void Setup()
    {
        var camera = Own(new GameObject("R5 disabled camera")).AddComponent<Camera>();
        camera.enabled = false; camera.cullingMask = 0;
        view = new CameraData
        {
            camera = camera, cameraType = CameraType.Game, renderType = CameraRenderType.Base,
            cameraTargetDescriptor = new RenderTextureDescriptor(32, 32)
        };
        root = Own(new GameObject("R5 geometry")).transform;
        lightRoot = Own(new GameObject("R5 lights")).transform;
        quad = QuadMesh("R5 quad");
        Plate("dielectric", new Vector3(-6, 0, 2), 0);
        Plate("metal", new Vector3(-4, 0, 2), 1); // touches dielectric at x=-5: seam pair
        Plate("normal map", new Vector3(0, 0, 2), 2);
        Plate("cutout", new Vector3(3, 0, 2), 3);

        blockerMaterial = materials[4];
        Blocker("directional blocker", DielectricHit + 0.8f * SunDirection, SunDirection, 5);
        Blocker("point blocker", DielectricHit + 0.8f * dP, dP, 6);
        Blocker("spot blocker", DielectricHit + 1.0f * dS, dS, 7);
        Blocker("white budget blocker", DielectricHit + 0.8f * dW, dW, 8, materials[5]);
        Blocker("red budget blocker", DielectricHit + 0.8f * dR, dR, 9, materials[5]);

        sun = LampAt("sun", LightType.Directional, Color.white, 2, Vector3.zero, SunDirection);
        point = LampAt("point", LightType.Point, Color.green, 12, DielectricHit + 2.5f * dP, dP);
        spot = LampAt("spot", LightType.Spot, Color.blue, 8, DielectricHit + 3f * dS, dS);

        // Budget: 66 registered directionals; the last two (bright) exceed the frozen 64 upload cap.
        Budget("budget white", Color.white, 10, dW);
        Budget("budget green", Color.green, 6, dG);
        Budget("budget red", Color.red, 8, dR);
        Budget("budget blue", Color.blue, 4, dB);
        Budget("budget rejected a", Color.white, .2f, Back);
        Budget("budget rejected b", Color.white, .1f, Back);
        for (int i = 0; i < 58; ++i) Budget("budget dim " + i, Color.white, .01f, Back);
        Budget("budget dropped a", Color.white, 1000, Back);
        Budget("budget dropped b", Color.white, 500, Back);

        SetBlockerAlpha(blockerMaterial, 1);
        SetBlockerAlpha(materials[5], 0);
        AssignTextures();
        scene = new RayTracingSceneResources();
        scene.SetRequests(Demand);
    }

    Mesh QuadMesh(string label)
    {
        var mesh = Own(new Mesh { name = label });
        mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        var tangent = new Vector4(1, 0, 0, -1);
        mesh.tangents = new[] { tangent, tangent, tangent, tangent };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        return mesh;
    }

    void Plate(string label, Vector3 position, int material)
    {
        var go = Own(new GameObject(label)); go.transform.SetParent(root, false);
        go.transform.position = position;
        go.AddComponent<MeshFilter>().sharedMesh = quad;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = materials[material];
        renderer.rayTracingMode = RayTracingMode.Static;
        var geometry = go.AddComponent<SsrRayTracingGeometry>();
        geometry.instanceId = nextInstanceId++; geometry.enableTriangleCulling = false;
    }

    void Blocker(string label, Vector3 center, Vector3 along, int id, Material material = null)
    {
        var go = Own(new GameObject(label)); go.transform.SetParent(root, false);
        go.transform.position = center;
        go.transform.rotation = Quaternion.LookRotation(along, Vector3.up);
        go.transform.localScale = new Vector3(.15f, .15f, 1);
        go.AddComponent<MeshFilter>().sharedMesh = quad;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material ? material : blockerMaterial;
        renderer.rayTracingMode = RayTracingMode.Static;
        var geometry = go.AddComponent<SsrRayTracingGeometry>();
        geometry.instanceId = id; geometry.enableTriangleCulling = false;
    }

    Lamp LampAt(string label, LightType type, Color color, float intensity, Vector3 position, Vector3 toLight)
    {
        var go = Own(new GameObject(label)); go.transform.SetParent(lightRoot, false);
        go.transform.position = position;
        go.transform.rotation = Quaternion.LookRotation(-toLight.normalized);
        var light = go.AddComponent<Light>();
        light.type = type; light.color = color; light.intensity = intensity;
        light.range = 8; light.spotAngle = 60; light.innerSpotAngle = 30;
        light.shadows = LightShadows.None; light.enabled = false;
        var lamp = new Lamp { light = light, name = label, kind = type == LightType.Directional ? 0 : type == LightType.Point ? 1 : 2 };
        lamps.Add(lamp);
        return lamp;
    }

    Lamp Budget(string label, Color color, float intensity, Vector3 toLight)
    {
        var lamp = LampAt(label, LightType.Directional, color, intensity, Vector3.zero, toLight);
        lamp.light.shadows = LightShadows.Hard;
        budget.Add(lamp);
        return lamp;
    }

    void SetBlockerAlpha(Material material, float alpha)
    {
        if (material) material.SetColor("_BaseColor", new Color(BlockerColor.r, BlockerColor.g, BlockerColor.b, alpha));
    }

    void AssignTextures()
    {
        var gloss = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp });
        gloss.SetPixel(0, 0, new Color(1, 0, 0, .8f)); gloss.Apply(false, false);
        materials[1].SetTexture("_MetallicGlossMap", gloss);
        var bump = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp });
        bump.SetPixel(0, 0, new Color(.8f, .65f, 0, 1)); bump.Apply(false, false);
        materials[2].SetTexture("_BumpMap", bump);
    }

    Surface SurfaceOf(int index) => index switch
    {
        0 => new Surface { albedo = Linear(DielectricColor), metallic = 0, smoothness = DielectricSmoothness, normal = Back },
        1 => new Surface { albedo = Linear(MetalColor), metallic = 1, smoothness = MetalSmoothness, normal = Back },
        2 => new Surface { albedo = Linear(NormalColor), metallic = 0, smoothness = NormalSmoothness, normal = NormalMapNormal },
        3 => new Surface { albedo = Linear(CutoutColor), metallic = 0, smoothness = CutoutSmoothness, normal = Back },
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    List<Lamp> ActiveLamps()
    {
        var active = new List<Lamp>();
        foreach (var lamp in lamps) if (lamp.light.isActiveAndEnabled) active.Add(lamp);
        active.Sort((a, b) =>
        {
            int kind = a.kind.CompareTo(b.kind);
            return kind != 0 ? kind : a.light.transform.GetSiblingIndex().CompareTo(b.light.transform.GetSiblingIndex());
        });
        return active;
    }

    // ---------- CPU oracle ----------

    static Vector3 Shade(in Brdf brdf, Vector3 n, Vector3 v, Vector3 position, IReadOnlyList<Lamp> table,
        ISet<Lamp> blockedBy, out Counts counts, out bool shadowed)
    {
        var top = new List<(float score, uint slot)>();
        int count = Mathf.Min(table.Count, 64); // shader GLX_HIT_LIGHT_CAPACITY
        for (int i = 0; i < count; ++i)
        {
            Lamp lamp = table[i];
            lamp.Evaluate(position, out Vector3 direction, out _, out Vector3 rgb);
            float score = Vector3.Dot(rgb, new Vector3(.2126f, .7152f, .0722f));
            if (score <= 0 || Vector3.Dot(n, direction) <= 0) continue;
            top.Add((score, (uint)i));
        }
        top.Sort((a, b) => a.score == b.score ? a.slot.CompareTo(b.slot) : b.score.CompareTo(a.score));
        int selected = Mathf.Min(top.Count, 4);
        uint rank0 = uint.MaxValue, rank1 = uint.MaxValue;
        int shadowEligible = 0;
        for (int k = 0; k < selected; ++k)
        {
            if (!table[(int)top[k].slot].Shadowed) continue;
            if (rank0 == uint.MaxValue) rank0 = (uint)k;
            else if (rank1 == uint.MaxValue) rank1 = (uint)k;
            ++shadowEligible;
        }
        counts = new Counts
        {
            selected = selected, rejected = top.Count - selected,
            shadowRays = rank1 != uint.MaxValue ? 2 : rank0 != uint.MaxValue ? 1 : 0
        };
        counts.untraced = shadowEligible - counts.shadowRays;
        shadowed = false;
        Vector3 radiance = Vector3.zero;
        for (int k = 0; k < selected; ++k)
        {
            uint slot = top[k].slot;
            Lamp lamp = table[(int)slot];
            if ((k == rank0 || k == rank1) && blockedBy.Contains(lamp)) { shadowed = true; continue; }
            lamp.Evaluate(position, out Vector3 direction, out _, out Vector3 rgb);
            radiance += Vector3.Scale(brdf.Direct(n, direction, v), rgb) *
                        (Mathf.Clamp01(Vector3.Dot(n, direction)) * InvPi);
        }
        return radiance;
    }

    Vector3 Reference(int material, Vector3 position, IReadOnlyList<Lamp> table, ISet<Lamp> blockedBy,
        float ambient, int source, float probe, out Counts counts, out bool shadowed)
    {
        Surface surface = SurfaceOf(material);
        Vector3 n = Vector3.Dot(surface.normal, View) < 0 ? -surface.normal : surface.normal;
        Brdf brdf = Brdf.Create(surface);
        Vector3 radiance = Shade(brdf, n, View, position, table, blockedBy, out counts, out shadowed);
        radiance += brdf.diffuse * ((source == 0 ? ambient : probe) * InvPi);
        return radiance;
    }

    // ---------- snapshot ----------

    Texture2D ConstantIrr(float e)
    {
        var texture = Own(new Texture2D(64, 256, TextureFormat.RGBAHalf, false, true) { name = "R5 constant irradiance" });
        var pixels = new Color[texture.width * texture.height];
        for (int i = 0; i < pixels.Length; ++i) pixels[i] = new Color(e, e, e, 1);
        texture.SetPixels(pixels); texture.Apply(false, false);
        return texture;
    }

    Texture2D ConstantMoments(float distance)
    {
        var texture = Own(new Texture2D(128, 512, TextureFormat.RG16, false, true) { name = "R5 constant moments" });
        var bytes = new byte[texture.width * texture.height * 4];
        ushort mean = Mathf.FloatToHalf(distance), square = Mathf.FloatToHalf(distance * distance);
        for (int i = 0; i < texture.width * texture.height; ++i)
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
        probeState = new ComputeBuffer(256, 16);
        var states = new Vector4[256];
        for (int i = 0; i < states.Length; ++i) states[i] = new Vector4(0, 0, 0, inactive ? 0 : 1);
        probeState.SetData(states);
        probePublished = new ComputeBuffer(256, 4);
        var mask = new uint[256];
        if (publishedAll) for (int i = 0; i < mask.Length; ++i) mask[i] = 1;
        probePublished.SetData(mask);
        var cmd = CommandBufferPool.Get("R5 snapshot globals");
        try
        {
            cmd.SetGlobalInt("_GlxProbeSnapshotValid", valid ? 1 : 0);
            cmd.SetGlobalVector("_GlxProbeIrrSize", new Vector4(64, 256, 1f / 64, 1f / 256));
            cmd.SetGlobalVector("_GlxProbeDistSize", new Vector4(128, 512, 1f / 128, 1f / 512));
            cmd.SetGlobalInt("_GlxProbeCountX", 8); cmd.SetGlobalInt("_GlxProbeCountY", 4); cmd.SetGlobalInt("_GlxProbeCountZ", 8);
            cmd.SetGlobalVector("_GlxProbeOrigin", new Vector4(-8.5f, .5f, -3.5f, 1)); // covers the budget hit at (-6,0,2)
            cmd.SetGlobalFloat("_GlxProbeSpacing", 1);
            cmd.SetGlobalBuffer("_GlxProbeStateSnapshot", probeState);
            cmd.SetGlobalBuffer("_GlxProbePublishedSnapshot", probePublished);
            cmd.SetGlobalTexture("_GlxProbeIrrSnapshot", irradiance);
            cmd.SetGlobalTexture("_GlxProbeDistSnapshot", moments);
            Graphics.ExecuteCommandBuffer(cmd);
        }
        finally { CommandBufferPool.Release(cmd); }
    }

    void ReleaseSnapshot()
    {
        probeState?.Release(); probePublished?.Release(); probeState = probePublished = null;
        if (irradiance) Destroy(irradiance);
        if (moments) Destroy(moments);
        irradiance = moments = null;
    }

    // ---------- execution ----------

    IEnumerator Warm()
    {
        var warm = Own(new GameObject("R5 warm camera").AddComponent<Camera>());
        warm.clearFlags = CameraClearFlags.SolidColor; warm.backgroundColor = Color.black;
        warm.cullingMask = ~0; warm.targetTexture = new RenderTexture(64, 64, 16);
        for (int i = 0; i < 4; ++i) yield return null;
        warm.targetTexture = null;
        Destroy(warm.gameObject);
        AuditMaterials();
    }

    void AuditMaterials()
    {
        report.materialRows.Clear();
        var names = new[] { "dielectric", "metal gloss", "normal map", "cutout", "blocker", "budget blocker" };
        for (int i = 0; i < materials.Length; ++i)
        {
            var material = materials[i];
            report.materialRows.Add(new MaterialRow
            {
                name = names[i], shader = material.shader.name, keywords = string.Join(" ", material.shaderKeywords),
                dxrPass = material.FindPass("GlxDXR"), passEnabled = material.GetShaderPassEnabled("GlxDXR"),
                queue = material.renderQueue, surface = material.HasProperty("_Surface") ? Mathf.RoundToInt(material.GetFloat("_Surface")) : 0,
                supported = material.FindPass("GlxDXR") >= 0
            });
        }
    }

    bool BuildScene()
    {
        Material witness = SsrRayTracingGeometry.Collect(view.camera.gameObject.scene, members, ids, out int revision);
        if (witness == null) return false;
        if (!scene.TryBuild(view, true, true, ray, witness, revision, ~0, Demand, members, out snapshot)) return false;
        report.instances = scene.instanceCount;
        report.members = members.Count;
        report.disabledSubmeshes = scene.disabledSubmeshes;
        return true;
    }

    void Dispatch(int count, float ambient, int source, bool feedback, ProfilingSampler sampler = null)
    {
        var cmd = CommandBufferPool.Get("R5 reflection rays");
        try
        {
            cmd.SetGlobalInt("_GlxHitLowFrequencySource", source);
            cmd.SetGlobalVector("_GlxHitAmbientIrradiance", new Vector4(ambient, ambient, ambient, 0));
            cmd.SetGlobalInt("_GlxProbeFeedbackEnabled", feedback ? 1 : 0);
            cmd.SetGlobalFloat("_GlxProbeFeedbackAlbedoCap", .9f);
            cmd.SetRayTracingShaderPass(ray, "GlxDXR");
            cmd.SetRayTracingAccelerationStructure(ray, Shader.PropertyToID("_GlxScene"), snapshot);
            cmd.SetRayTracingBufferParam(ray, Shader.PropertyToID("_R5Rays"), rays);
            cmd.SetRayTracingBufferParam(ray, Shader.PropertyToID("_R5Results"), results);
            cmd.SetRayTracingIntParam(ray, Shader.PropertyToID("_R5Count"), count);
            scene.BindLights(cmd, ray);
            if (sampler != null)
            {
                using (new ProfilingScope(cmd, sampler))
                    cmd.DispatchRays(ray, "R5Trace", (uint)count, 1, 1, null);
            }
            else cmd.DispatchRays(ray, "R5Trace", (uint)count, 1, 1, null);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Graphics.ExecuteCommandBuffer(cmd);
            watch.Stop();
            submitTotal += watch.Elapsed.TotalMilliseconds;
        }
        finally { CommandBufferPool.Release(cmd); }
        ++dispatches;
    }

    Counts ReadCounts()
    {
        var words = new uint[4];
        if (scene.lightCounters != null) scene.lightCounters.GetData(words);
        return new Counts
        {
            selected = (int)words[0], rejected = (int)words[1], shadowRays = (int)words[2], untraced = (int)words[3]
        };
    }

    Shot Trace(string name, int material, float x, float y, IReadOnlyList<Lamp> table, ISet<Lamp> blockedBy,
        float ambient, int source, float probe, bool feedback, string expectedSource, Vector3 emission)
    {
        var request = new Request
        {
            originTMin = new Vector4(x, y, 0, .002f), directionTMax = new Vector4(0, 0, 1, 10), mode = 3
        };
        rays.SetData(new[] { request });
        results.SetData(new[] { new Payload { rgb = Vector3.one * -999, t = -1, flags = uint.MaxValue } });
        Dispatch(1, ambient, source, feedback);
        var output = new Payload[1];
        results.GetData(output);
        var p = output[0];
        var row = new Shot
        {
            name = name, expectedSource = expectedSource, origin = new Vector3(x, y, 0),
            expectedRgb = Reference(material, new Vector3(x, y, 2), table, blockedBy, ambient, source, probe,
                out Counts expectedCounts, out bool shadowed) + emission,
            expectedT = 2, expectedFlags = 2u | (shadowed ? 4u : 0u), expectedCounts = expectedCounts
        };
        row.actualRgb = p.rgb; row.actualT = p.t; row.actualFlags = p.flags; row.actualCounts = ReadCounts();
        row.hit = (p.flags & 2u) != 0;
        row.finite = Finite(p.rgb.x) && Finite(p.rgb.y) && Finite(p.rgb.z) && Finite(p.t);
        row.relativeError = Relative(p.rgb, row.expectedRgb);
        row.maxRelativeError = Mathf.Max(row.relativeError.x, Mathf.Max(row.relativeError.y, row.relativeError.z));
        row.countsMatched = expectedCounts.Same(row.actualCounts);
        bool close = true;
        for (int i = 0; i < 3; ++i)
            close &= Mathf.Abs(row.actualRgb[i] - row.expectedRgb[i]) <= row.tolerance * Mathf.Max(.01f, Mathf.Abs(row.expectedRgb[i]));
        row.passed = row.finite && row.hit && close && row.countsMatched &&
            row.actualFlags == row.expectedFlags && Mathf.Abs(row.actualT - 2) <= .001f;
        report.shots.Add(row);
        Check(row.passed, $"{name}: got rgb={row.actualRgb} t={row.actualT} flags={row.actualFlags} " +
            $"counts={row.actualCounts.selected}/{row.actualCounts.rejected}/{row.actualCounts.shadowRays}/{row.actualCounts.untraced} " +
            $"expected rgb={row.expectedRgb} flags={row.expectedFlags} counts={expectedCounts.selected}/{expectedCounts.rejected}/" +
            $"{expectedCounts.shadowRays}/{expectedCounts.untraced}");
        return row;
    }

    static Vector3 Relative(Vector3 a, Vector3 b) => new Vector3(
        Mathf.Abs(a.x - b.x) / Mathf.Max(.01f, Mathf.Abs(b.x)),
        Mathf.Abs(a.y - b.y) / Mathf.Max(.01f, Mathf.Abs(b.y)),
        Mathf.Abs(a.z - b.z) / Mathf.Max(.01f, Mathf.Abs(b.z)));

    void Contour()
    {
        var requests = new List<Request>();
        for (int i = 0; i < 20; ++i)
            requests.Add(new Request { originTMin = new Vector4(2.05f + .1f * i, 0, 0, .002f), directionTMax = new Vector4(0, 0, 1, 10), mode = 3 });
        rays.SetData(requests);
        results.SetData(new Payload[requests.Count]);
        Dispatch(requests.Count, 0, 0, false);
        var output = new Payload[requests.Count];
        results.GetData(output);
        report.contour.rows.Clear(); report.contour.hits = report.contour.misses = report.contour.mismatches = 0;
        for (int i = 0; i < requests.Count; ++i)
        {
            float x = 2.05f + .1f * i;
            bool expectedHit = x >= 3f; // alpha split at u=0.5
            var p = output[i];
            var row = new Shot
            {
                name = "cutout contour " + x.ToString("F2"), expectedSource = expectedHit ? "alpha 1" : "alpha 0",
                origin = new Vector3(x, 0, 0), expectedT = expectedHit ? 2 : -1,
                expectedRgb = expectedHit ? Vector3.one : Vector3.zero,
                expectedFlags = expectedHit ? 2u : 1u,
                actualRgb = p.rgb, actualT = p.t, actualFlags = p.flags
            };
            row.hit = (p.flags & 2u) != 0;
            row.finite = Finite(p.rgb.x) && Finite(p.rgb.y) && Finite(p.rgb.z) && Finite(p.t);
            bool hitMatches = row.hit == expectedHit;
            bool rgbMatches = !expectedHit || (Mathf.Abs(p.rgb.x - 1) <= .03f && Mathf.Abs(p.rgb.y - 1) <= .03f && Mathf.Abs(p.rgb.z - 1) <= .03f);
            row.passed = row.finite && hitMatches && rgbMatches && p.flags == row.expectedFlags &&
                (!expectedHit || Mathf.Abs(p.t - 2) <= .001f);
            if (expectedHit) ++report.contour.expectedHits; else ++report.contour.expectedMisses;
            if (row.hit) ++report.contour.hits; else ++report.contour.misses;
            if (!row.passed) ++report.contour.mismatches;
            report.contour.rows.Add(row);
        }
        report.contour.passed = report.contour.mismatches == 0 && report.contour.hits == 10 && report.contour.misses == 10;
        Check(report.contour.passed, $"cutout contour mismatched ({report.contour.mismatches})");
    }

    IEnumerator Suite()
    {
        Check(report.development && report.api == Arg("-expectedApi") && Application.platform == RuntimePlatform.WindowsPlayer &&
            IntPtr.Size == 8 && QualitySettings.activeColorSpace == ColorSpace.Linear && ray && materials.Length == 6 && cutoutMap,
            "Invalid Player configuration");
        report.payloadBytes = Marshal.SizeOf(typeof(Payload));
        report.requestBytes = Marshal.SizeOf(typeof(Request));
        Check(report.payloadBytes == 24 && report.requestBytes == 48, "Unexpected ABI");
        Setup();
        yield return Warm();
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
        Check(BuildScene() && snapshot != null, "RT scene build failed");
        report.probeProducerAllocated = false;
        report.surfaceCacheAllocated = false;
        Check(report.members == 9 && report.instances == 9 && report.disabledSubmeshes == 0,
            $"Registration changed (members={report.members} instances={report.instances})");
        rays = new ComputeBuffer(RayCount, 48);
        results = new ComputeBuffer(RayCount, 24);
        yield return null;

        // 0. Cutout contour with no lights: hits return emission only, holes are misses.
        Contour();
        Check(report.contour.passed, "cutout contour mismatched");

        // 1. Directional sun, PBR materials and the adjacent-material seam.
        sun.light.enabled = true;
        var table = ActiveLamps();
        Check(table.Count == 1 && table[0] == sun, "sun table is not a single directional");
        Trace("dielectric sun", 0, -6, 0, table, blocked, 0, 0, 0, false, "analytic direct", Vector3.zero);
        Trace("metal sun", 1, -4, 0, table, blocked, 0, 0, 0, false, "analytic direct", Vector3.zero);
        Trace("normal map sun", 2, 0, 0, table, blocked, 0, 0, 0, false, "analytic direct", Vector3.zero);
        report.seam = SeamPair(table);
        Check(report.seam.passed, "adjacent dielectric/metal seam did not match its own material");

        // 2. Directional shadows: the blocker is between the dielectric hit and the sun only.
        sun.light.shadows = LightShadows.Hard;
        yield return null;
        blocked.Clear(); blocked.Add(sun);
        Trace("dielectric sun shadowed", 0, -6, 0, table, blocked, 0, 0, 0, false, "blocked direct", Vector3.zero);
        blocked.Clear();
        Trace("metal sun unblocked", 1, -4, 0, table, blocked, 0, 0, 0, false, "analytic direct", Vector3.zero);
        SetBlockerAlpha(blockerMaterial, 0);
        Trace("dielectric sun reopened", 0, -6, 0, table, blocked, 0, 0, 0, false, "analytic direct", Vector3.zero);
        SetBlockerAlpha(blockerMaterial, 1);

        // 3. Point light: distance attenuation, then a per-light shadow.
        sun.light.enabled = false;
        point.light.enabled = true;
        yield return null;
        table = ActiveLamps();
        Trace("dielectric point", 0, -6, 0, table, blocked, 0, 0, 0, false, "analytic direct", Vector3.zero);
        Trace("metal point", 1, -4, 0, table, blocked, 0, 0, 0, false, "analytic direct", Vector3.zero);
        point.light.shadows = LightShadows.Hard;
        yield return null;
        blocked.Add(point);
        Trace("dielectric point shadowed", 0, -6, 0, table, blocked, 0, 0, 0, false, "blocked direct", Vector3.zero);
        blocked.Clear();
        SetBlockerAlpha(blockerMaterial, 0);
        Trace("dielectric point reopened", 0, -6, 0, table, blocked, 0, 0, 0, false, "analytic direct", Vector3.zero);
        SetBlockerAlpha(blockerMaterial, 1);

        // 4. Spot light: cone attenuation, penumbra, outside, and a per-light shadow.
        point.light.enabled = false;
        spot.light.enabled = true;
        yield return null;
        table = ActiveLamps();
        Trace("dielectric spot center", 0, -6, 0, table, blocked, 0, 0, 0, false, "analytic direct", Vector3.zero);
        var spotRotation = Quaternion.LookRotation(-dS); // 20 degrees off the hit direction stays in the penumbra
        spot.light.transform.rotation = spotRotation * Quaternion.Euler(0, 20, 0);
        yield return null;
        Trace("dielectric spot penumbra", 0, -6, 0, table, blocked, 0, 0, 0, false, "analytic penumbra", Vector3.zero);
        spot.light.transform.rotation = spotRotation * Quaternion.Euler(0, 40, 0);
        yield return null;
        Trace("dielectric spot outside", 0, -6, 0, table, blocked, 0, 0, 0, false, "zero", Vector3.zero);
        spot.light.transform.rotation = spotRotation;
        spot.light.shadows = LightShadows.Hard;
        yield return null;
        blocked.Add(spot);
        Trace("dielectric spot shadowed", 0, -6, 0, table, blocked, 0, 0, 0, false, "blocked direct", Vector3.zero);
        blocked.Clear();
        SetBlockerAlpha(blockerMaterial, 0);
        Trace("dielectric spot reopened", 0, -6, 0, table, blocked, 0, 0, 0, false, "analytic direct", Vector3.zero);
        SetBlockerAlpha(blockerMaterial, 1);

        // 5. Frozen budgets: 66 registered/64 uploaded, 4 of 64 selected, 2 of 4 shadow rays.
        spot.light.enabled = false;
        foreach (var lamp in budget) lamp.light.enabled = true;
        SetBlockerAlpha(blockerMaterial, 0); // unrelated blockers leave the TLAS traversal
        SetBlockerAlpha(materials[5], 1);    // white (rank 0) and red (rank 2) are physically blocked
        yield return null;
        table = ActiveLamps();
        Check(table.Count == 66, $"budget table registered {table.Count} lights");
        blocked.Clear(); blocked.Add(budget[0]); blocked.Add(budget[2]);
        var blockedShot = Trace("budget white blocked, red untraced", 0, -6, 0, table, blocked, 0, 0, 0, false,
            "top 4 of 64, red untraced", Vector3.zero);
        report.registeredLights = scene.registeredLights;
        report.uploadedLights = scene.uploadedLights;
        report.tableOverflow = scene.tableOverflow;
        Check(blockedShot.actualCounts.selected == 4 && blockedShot.actualCounts.rejected == 60 &&
            blockedShot.actualCounts.shadowRays == 2 && blockedShot.actualCounts.untraced == 2,
            "frozen light/shadow budget counters changed");
        Check(report.registeredLights == 66 && report.uploadedLights == 64 && report.tableOverflow == 2,
            "upload cap did not drop exactly the last two lights");
        // The two dropped lights are intensity 1000/500; if the cap leaked they would dominate this hit.
        Check(blockedShot.actualRgb.magnitude < 10f, "upload-capped lights contributed to the hit");
        SetBlockerAlpha(materials[5], 0);
        blocked.Clear();
        Trace("budget unblocked", 0, -6, 0, table, blocked, 0, 0, 0, false, "all four selected", Vector3.zero);

        // 6. Reflection hits never add the probe feedback term; probe low-frequency is added once.
        yield return null;
        BuildSnapshot(6, true, true, false);
        var off = Trace("reflection feedback off", 0, -6, 0, table, blocked, 0, 0, 0, false, "direct only", Vector3.zero);
        var on = Trace("reflection feedback on", 0, -6, 0, table, blocked, 0, 0, 0, true, "direct only", Vector3.zero);
        report.feedbackDelta = Mathf.Max(Relative(on.actualRgb, off.actualRgb).magnitude, 0);
        report.feedbackIdentical = report.feedbackDelta <= 1e-4;
        Check(report.feedbackIdentical, $"reflection radiance changed under feedback ({report.feedbackDelta})");
        var probeRow = Trace("reflection probe source", 0, -6, 0, table, blocked, 0, 1, 6, true,
            "direct + diffuse*E/pi", Vector3.zero);
        Check(probeRow.passed, "probe low-frequency source did not add exactly one term");
        ReleaseSnapshot();

        // 7. Hit-shading cost over the full 64-entry table (GPU samples may stay zero in a Player).
        SetBlockerAlpha(materials[5], 0);
        var marker = new ProfilingSampler("R5 Reflection Hit Shading") { enableRecording = true };
        var batch = new Request[RayCount];
        for (int i = 0; i < batch.Length; ++i)
            batch[i] = new Request { originTMin = new Vector4(-6, 0, 0, .002f), directionTMax = new Vector4(0, 0, 1, 10), mode = 3 };
        rays.SetData(batch);
        for (int frame = 0; frame < 32; ++frame)
        {
            Dispatch(RayCount, 0, 0, false, marker);
            yield return null;
        }
        for (int i = 0; i < 16; ++i) yield return null;
        report.gpuSamples = marker.gpuSampleCount;
        report.gpuMs = marker.gpuElapsedTime;
        report.gpuTimingMeasured = marker.gpuSampleCount > 0 && Finite(marker.gpuElapsedTime) && marker.gpuElapsedTime > 0;
        report.gpuMsPerDispatch = report.gpuTimingMeasured ? report.gpuMs / 32.0 : 0;
        marker.enableRecording = false;

        report.dispatchCount = dispatches;
        report.submitAverageMs = submitTotal / dispatches;
        report.noRebuild = scene.buildCount == 1;
        Check(report.noRebuild, $"geometry rebuilt for light/shadow changes ({scene.buildCount})");
        report.passed = true;
        yield return null;
    }

    Seam SeamPair(IReadOnlyList<Lamp> table)
    {
        var left = Trace("seam dielectric", 0, -5.5f, 0, table, blocked, 0, 0, 0, false, "dielectric", Vector3.zero);
        var right = Trace("seam metal", 1, -4.5f, 0, table, blocked, 0, 0, 0, false, "metal", Vector3.zero);
        var leftRef = Reference(0, new Vector3(-5.5f, 0, 2), table, blocked, 0, 0, 0, out _, out _);
        var rightRef = Reference(1, new Vector3(-4.5f, 0, 2), table, blocked, 0, 0, 0, out _, out _);
        var seam = new Seam
        {
            name = "dielectric | metal", leftActual = left.actualRgb, leftReference = leftRef,
            rightActual = right.actualRgb, rightReference = rightRef,
            crossLeft = Relative(left.actualRgb, rightRef), crossRight = Relative(right.actualRgb, leftRef)
        };
        seam.ownError = Mathf.Max(left.maxRelativeError, right.maxRelativeError);
        seam.crossError = Mathf.Min(Mathf.Max(seam.crossLeft.x, Mathf.Max(seam.crossLeft.y, seam.crossLeft.z)),
                                    Mathf.Max(seam.crossRight.x, Mathf.Max(seam.crossRight.y, seam.crossRight.z)));
        seam.passed = left.passed && right.passed && seam.crossError > .05f;
        return seam;
    }

#if UNITY_EDITOR
    public static void Build()
    {
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
        var scratch = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var driver = new GameObject("R5 reflection material fixture").AddComponent<R5Player>();
        driver.ray = AssetDatabase.LoadAssetAtPath<RayTracingShader>("Assets/Fixture/R5Probe.raytrace");
        var folder = "Assets/Fixture/R5";
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/Fixture", "R5");

        Material Mat(string name, Color color, float metallic, float smoothness, params string[] keywords)
        {
            var path = $"{folder}/{name}.mat";
            AssetDatabase.DeleteAsset(path);
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            AssetDatabase.CreateAsset(material, path);
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_WorkflowMode", 1);
            material.SetFloat("_BumpScale", 1);
            foreach (string keyword in keywords) material.EnableKeyword(keyword);
            if (Array.IndexOf(keywords, "_EMISSION") >= 0) material.SetColor("_EmissionColor", Color.white);
            if (Array.IndexOf(keywords, "_ALPHATEST_ON") >= 0) material.SetFloat("_Cutoff", .5f);
            EditorUtility.SetDirty(material);
            return material;
        }

        var dielectric = Mat("dielectric", DielectricColor, 0, DielectricSmoothness);
        var metal = Mat("metal", MetalColor, 1, .8f, "_METALLICSPECGLOSSMAP");
        var normal = Mat("normal", NormalColor, 0, NormalSmoothness, "_NORMALMAP");
        var cutout = Mat("cutout", CutoutColor, 0, CutoutSmoothness, "_ALPHATEST_ON", "_EMISSION");
        var blocker = Mat("blocker", BlockerColor, 0, 0, "_ALPHATEST_ON");
        var budgetBlocker = Mat("budget blocker", BlockerColor, 0, 0, "_ALPHATEST_ON");
        AssetDatabase.SaveAssets(); // flush keyword changes before the texture import can reload assets

        var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false) { name = "cutout alpha", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < 8; ++y)
            for (int x = 0; x < 8; ++x) texture.SetPixel(x, y, new Color(1, 1, 1, x < 4 ? 0 : 1));
        texture.Apply();
        var texturePath = $"{folder}/cutout.asset";
        AssetDatabase.DeleteAsset(texturePath);
        AssetDatabase.CreateAsset(texture, texturePath);
        cutout.SetTexture("_BaseMap", texture);
        cutout.SetFloat("_Cutoff", .5f);
        cutout.SetColor("_EmissionColor", Color.white);
        cutout.EnableKeyword("_ALPHATEST_ON");
        cutout.EnableKeyword("_EMISSION");
        EditorUtility.SetDirty(cutout);
        AssetDatabase.SaveAssets();
        Check(cutout.shaderKeywords.Length == 2, "cutout keywords were not serialized");

        driver.materials = new[] { dielectric, metal, normal, cutout, blocker, budgetBlocker };
        driver.cutoutMap = texture;
        Check(driver.ray && EditorSceneManager.SaveScene(scratch, "Assets/R5.unity"), "Cannot prepare R5 fixture scene");
        var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/R5.unity" }, target = target, locationPathName = Arg("-exe"), options = BuildOptions.Development
        });
        Check(build.summary.result == BuildResult.Succeeded, "Win64 R5 build failed");
    }
#endif
}
