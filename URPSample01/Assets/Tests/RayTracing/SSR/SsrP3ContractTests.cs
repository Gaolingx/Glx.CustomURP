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
    public void GlxBasicPayload_IsTheFrozen24ByteAbi()
    {
        var source = ReadPackage("Shaders/RayTracing/GlxBasicPayload.hlsl");
        var fields = Declarations(StructBody(source, "GlxBasicPayload"));
        CollectionAssert.AreEqual(new[] { "float3 radiance", "float hitT", "uint flags", "uint mode" }, fields,
            "GlxBasicPayload fields changed; the 24-byte payload contract (C2) was broken.");
        Assert.AreEqual(24, fields.Sum(f => SizeOf(f.Split(' ')[0])), "GlxBasicPayload must stay 24 bytes.");
        StringAssert.Contains("stride 24", source, "The 24-byte stride note must stay next to the struct.");
        StringAssert.DoesNotContain("SsrHitSurface", source,
            "The hit surface must stay in its own buffer; the 24-byte payload (C2) cannot carry it.");
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
