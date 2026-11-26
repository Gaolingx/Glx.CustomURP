using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Freezes the round 23 contracts documented in PLAN/TODO/23-Ray_Tracing_Inprove_2/contracts.md.
// A failure here means an ABI or pass-order change landed without updating the contract document,
// the baseline fixture and the evidence. P3 is expected to update SsrPass_StaysBeforeGBuffer_UntilP3
// together with the injection point; no other test in this file may change silently.
public sealed class SsrP3ContractTests
{
    const string PackageRoot = "Packages/com.unity.render-pipelines.universal@14.0.12";

    static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

    static string ReadPackage(string relativePath)
    {
        var path = Path.Combine(ProjectRoot, PackageRoot, relativePath);
        Assert.IsTrue(File.Exists(path), "Missing contract source: " + path);
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    static string StructBody(string source, string structName)
    {
        var start = source.IndexOf("struct " + structName, StringComparison.Ordinal);
        Assert.GreaterOrEqual(start, 0, "Struct not found: " + structName);
        var open = source.IndexOf('{', start);
        var close = source.IndexOf("};", open, StringComparison.Ordinal);
        Assert.Greater(close, open, "Unterminated struct: " + structName);
        return source.Substring(open + 1, close - open - 1);
    }

    static string[] Declarations(string body)
    {
        var declarations = new List<string>();
        foreach (var raw in body.Split(';'))
        {
            var line = raw;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                line = line.Substring(0, comment);
            line = line.Trim();
            if (line.Length == 0)
                continue;

            var parts = line.Split(',');
            var first = parts[0].Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (first.Length != 2)
                Assert.Fail("Unexpected declaration: " + line);
            declarations.Add(first[0] + " " + first[1]);
            for (var i = 1; i < parts.Length; i++)
                declarations.Add(first[0] + " " + parts[i].Trim());
        }
        return declarations.ToArray();
    }

    static int SizeOf(string type)
    {
        switch (type)
        {
            case "float":
            case "uint":
            case "int":
                return 4;
            case "float2":
            case "uint2":
            case "int2":
                return 8;
            case "float3":
                return 12;
            case "float4":
            case "uint4":
                return 16;
            default:
                Assert.Fail("Unknown contract field type: " + type);
                return 0;
        }
    }

    static string Normalize(string source)
    {
        return new string(source.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    [Test]
    public void GlxBasicPayload_IsTheFrozen64ByteTransport()
    {
        var source = ReadPackage("Shaders/RayTracing/GlxBasicPayload.hlsl");
        var fields = Declarations(StructBody(source, "GlxBasicPayload"));
        CollectionAssert.AreEqual(new[]
        {
            "float3 radiance", "float hitT", "uint flags", "uint mode",
            "float ssrRoughness", "float ssrMetallic", "float2 ssrNormalOct",
            "float3 ssrBaseColor", "float3 ssrEmissive"
        }, fields, "GlxBasicPayload layout changed; the C2 transport contract was broken.");
        Assert.AreEqual(64, fields.Sum(f => SizeOf(f.Split(' ')[0])), "The ray payload must stay 64 bytes.");
        StringAssert.Contains("payload stride 64", source, "The 64-byte payload stride note must stay next to the struct.");
        StringAssert.Contains("Published stride 24", source, "The published result must stay 24 bytes.");
        StringAssert.DoesNotContain("SsrHitSurface", source,
            "The hit surface must stay in its own buffer; the payload (C2) cannot carry the record itself.");
    }

    [Test]
    public void SsrRayPayload_CompilesToTheFrozen64ByteSize()
    {
        UnityEngine.Experimental.Rendering.RayTracingShader shader = null;
        foreach (var path in new[]
        {
            PackageRoot + "/Shaders/Utils/ScreenSpaceLighting/RayTracingSsr.raytrace",
            "Packages/com.unity.render-pipelines.universal/Shaders/Utils/ScreenSpaceLighting/RayTracingSsr.raytrace"
        })
        {
            if (!File.Exists(Path.Combine(ProjectRoot, path))) continue;
            UnityEditor.AssetDatabase.ImportAsset(path, UnityEditor.ImportAssetOptions.ForceSynchronousImport);
            shader = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Experimental.Rendering.RayTracingShader>(path);
            if (shader != null) break;
        }
        Assert.IsNotNull(shader, "RayTracingSsr.raytrace did not load; the payload size tripwire cannot run.");
        // The miss shader shares the pipeline payload, so its reflected size is the real stride.
        // A D3D11 editor cannot compile ray tracing shaders and reports 0, so only assert where
        // the reflection exists: run the suite with -force-d3d12 to exercise this check.
        var size = UnityEditor.ShaderUtil.GetMissShaderRayPayloadSize(shader, 0);
        if (size == 0)
            Assert.Ignore("Ray tracing is unavailable on this editor API; rerun with -force-d3d12.");
        Assert.AreEqual(64, size,
            "The compiled ray payload is not 64 bytes; member alignment (float2/float3) shifted the transport offsets.");
    }

    [Test]
    public void GlxPublishedHit_IsTheFrozen24ByteResult()
    {
        var source = ReadPackage("Shaders/RayTracing/GlxPublishedHit.hlsl");
        var fields = Declarations(StructBody(source, "GlxPublishedHit"));
        CollectionAssert.AreEqual(new[] { "float3 radiance", "float hitT", "uint flags", "uint mode" }, fields,
            "GlxPublishedHit fields changed; the published 24-byte result (C2) was broken.");
        Assert.AreEqual(24, fields.Sum(f => SizeOf(f.Split(' ')[0])), "The published hit must stay 24 bytes.");
        StringAssert.Contains("stride 24", source, "The 24-byte stride note must stay next to the struct.");
    }

    [Test]
    public void SsrHitSurface_IsTheFrozen80ByteReflectionRecord()
    {
        var source = ReadPackage("Shaders/RayTracing/SsrHitSurface.hlsl");
        var fields = Declarations(StructBody(source, "SsrHitSurface"));
        CollectionAssert.AreEqual(new[] { "float4 gb0", "float4 gb1", "float4 gb2", "float4 gb3", "float4 misc" }, fields,
            "SsrHitSurface fields changed; the C2.7 record was broken. Bump the version and the contract together.");
        Assert.AreEqual(80, fields.Sum(f => SizeOf(f.Split(' ')[0])), "SsrHitSurface must stay 80 bytes.");
        StringAssert.Contains("stride 80", source, "The 80-byte stride note must stay next to the struct.");

        var normalized = Normalize(source);
        StringAssert.Contains("staticconstuintSsrHitSurfaceVersion=1u;", normalized,
            "Any semantic change to SsrHitSurface must bump SsrHitSurfaceVersion (C2.7).");
        StringAssert.Contains("staticconstuintSsrSurfaceTypeLit=0u;", normalized, "surfaceType Lit is frozen at 0.");
        StringAssert.Contains("staticconstuintSsrSurfaceTypeSimpleLit=1u;", normalized, "surfaceType SimpleLit is frozen at 1.");
        StringAssert.Contains("staticconstuintSsrSurfaceTypeUnlit=2u;", normalized, "surfaceType Unlit is frozen at 2.");
        StringAssert.Contains("staticconstuintSsrHitSurfaceSourceMask=1u;", normalized,
            "misc.w keeps only the source bit (0 = RT, 1 = SS); other bits are reserved (C2.7).");
        StringAssert.Contains("staticconstuintSsrHitSurfaceSourceRt=0u;", normalized,
            "A ray traced hit must publish source 0; only P3 may write the screen-space value.");
        StringAssert.Contains("staticconstuintSsrHitSurfaceSourceSs=1u;", normalized,
            "The screen-space source bit is frozen at 1.");

        var ray = Normalize(ReadPackage("Shaders/Utils/ScreenSpaceLighting/RayTracingSsr.raytrace"));
        StringAssert.Contains("RWStructuredBuffer<GlxPublishedHit>_R2Hits;", ray,
            "The published R2 buffer must keep the 24-byte record, not the 64-byte payload.");
        StringAssert.Contains("RWStructuredBuffer<SsrHitSurface>_R2Surfaces;", ray,
            "The raygen owns the hit-surface buffer (a closest-hit pass cannot write UAVs).");

        var queue = Normalize(ReadPackage("Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrRequestQueue.cs"));
        StringAssert.Contains("Ensure(refsurfaces,k,80)", queue, "The hit-surface buffer is request indexed at stride 80.");
    }

    [Test]
    public void SsrHitDeferred_ReusesClusteredDeferredAndKeepsTheLegacyFallback()
    {
        // The record header must stay include-safe for compute shaders: no AS, no payload.
        var record = ReadPackage("Shaders/RayTracing/SsrHitSurface.hlsl");
        StringAssert.DoesNotContain("GlxBasicPayload", record,
            "SsrHitSurface.hlsl must not pull the ray payload (and its acceleration structure) into compute kernels.");
        StringAssert.DoesNotContain("RaytracingAccelerationStructure", record,
            "A compute shader cannot declare the acceleration structure.");

        var source = ReadPackage("Shaders/Utils/ScreenSpaceLighting/SsrHitDeferred.compute");
        StringAssert.Contains("ClusteredDeferred.hlsl", source,
            "The hit kernel must reuse the ClusteredDeferred shading code, not a second lighting model.");
        StringAssert.Contains("CdShade(", source, "Direct lighting must go through CdShade.");
        var normalized = Normalize(source);
        StringAssert.Contains("StructuredBuffer<SsrHitSurface>_R2Surfaces;", normalized,
            "The hit kernel reads the C2.7 record buffer.");
        StringAssert.Contains("RWStructuredBuffer<GlxPublishedHit>_R2Hits;", normalized,
            "The hit kernel publishes the frozen 24-byte result.");
        StringAssert.Contains("uint(s.misc.z+0.5)!=SsrHitSurfaceVersion", normalized,
            "Invalid or unwritten records must keep the closest-hit radiance.");

        var pass = Normalize(ReadPackage("Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs"));
        StringAssert.Contains("DispatchHitDeferred(cmd);", pass,
            "The pass must run the hit kernel between DispatchRays and the temporal merge.");
    }

    [Test]
    public void HitShading_IsASwitchAndRejectsUnsupportedMaterials()
    {
        var settings = ReadPackage("Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingSettings.cs");
        StringAssert.Contains("internal bool hitDeferredShading = false;", settings,
            "Hit shading must stay off by default; the P0 baseline is the closest-hit path.");

        var pass = Normalize(ReadPackage("Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs"));
        StringAssert.Contains("m_Settings.hitDeferredShading", pass,
            "The kernel must follow the serialized feature switch, not a static default.");

        var instances = Normalize(ReadPackage("Runtime/RendererFeatures/RayTracing/RayTracingSceneInstances.cs"));
        StringAssert.Contains("ReportUnsupportedSurface", instances,
            "Materials that advertise the hit pass but lack the standard fields need the C2.7 report.");
        StringAssert.Contains("cannotprovidethestandardGBufferfields", instances,
            "The rejection must say why the material was skipped.");
    }

    [Test]
    public void R1Sample_IsTheFrozen80ByteRecord()
    {
        var source = ReadPackage("Shaders/Utils/ScreenSpaceLighting/SsrRayRequest.hlsl");
        var fields = Declarations(StructBody(source, "R1Sample"));
        var expected = new[]
        {
            "uint slot", "uint sequenceFrame", "uint reason", "uint sourcePixel",
            "float3 position", "float roughness", "float3 normal", "float tMin",
            "float3 direction", "float tMax", "float ssShare", "float requestShare",
            "uint materialId", "uint instanceId"
        };
        CollectionAssert.AreEqual(expected, fields, "R1Sample fields changed; the 80-byte record (C8) was broken.");
        Assert.AreEqual(80, fields.Sum(f => SizeOf(f.Split(' ')[0])), "R1Sample must stay 80 bytes.");
    }

    [Test]
    public void QueueBuffers_KeepTheFrozenStridesAndTileGrid()
    {
        var queue = Normalize(ReadPackage("Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrRequestQueue.cs"));
        StringAssert.Contains("Ensure(reforiginals,width*height,80)", queue, "R1 originals stride is frozen at 80.");
        StringAssert.Contains("Ensure(refrequests,k,80)", queue, "R1 request queue stride is frozen at 80.");
        StringAssert.Contains("Ensure(refhits,width*height,24)", queue, "R2 hit stride is frozen at 24.");
        StringAssert.Contains("Ensure(refcounters,11,4)", queue, "R1 counters stay 11 x uint.");
        StringAssert.Contains("(width+7)/8", queue, "R1 uses 8x8 tiles.");

        var compute = Normalize(ReadPackage("Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute"));
        StringAssert.Contains("#defineSSR_TILE_SIZE8", compute, "SSR tile size is frozen at 8.");
    }

    [Test]
    public void SsrPass_StaysBeforeGBuffer_UntilP3()
    {
        var type = typeof(ScreenSpaceLightingRendererFeature).Assembly.GetType(
            "UnityEngine.Rendering.Universal.ScreenSpaceLightingSsrPass", throwOnError: false);
        Assert.IsNotNull(type, "ScreenSpaceLightingSsrPass was not found; the pass class was renamed.");
        var field = type.GetField("k_RenderPassEvent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.IsNotNull(field, "k_RenderPassEvent is not declared as a const any more.");

        var raw = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
        var expected = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 6);
        Assert.AreEqual((int)expected, Convert.ToInt32(raw),
            "The SSR injection point moved. P3 must update contracts.md C1, the P0 baseline and this assertion in one change.");
    }
}
