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
// the baseline fixture and the evidence. The B7 tests additionally freeze the decal-safe SSR event
// (221/222), the 225 availability gate, the reason-6 hardware complement and the A2 probe
// environment source. Keep tests and code in one change.
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
        StringAssert.Contains("staticconstuintSsrHitSurfaceSourceMask=3u;", normalized,
            "misc.w must keep two source bits (0 = RT, 1 = SS, 2 = GBuffer); other bits stay reserved (C2.7, P3).");
        StringAssert.Contains("staticconstuintSsrHitSurfaceSourceRt=0u;", normalized,
            "A ray traced hit must publish source 0.");
        StringAssert.Contains("staticconstuintSsrHitSurfaceSourceSs=1u;", normalized,
            "The screen-space source value is frozen at 1.");
        StringAssert.Contains("staticconstuintSsrHitSurfaceSourceGbuffer=2u;", normalized,
            "P3 SS hits that read the current-frame GBuffer must publish source 2 (C2.7).");

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
        StringAssert.Contains("SsrShadeSurface.hlsl", source,
            "The hit kernel must reuse the shared ClusteredDeferred-based shading, not a second lighting model.");
        StringAssert.Contains("SsrShadeSurface(", source, "Direct lighting must go through the shared SsrShadeSurface.");
        var shadeSource = ReadPackage("Shaders/RayTracing/SsrShadeSurface.hlsl");
        StringAssert.Contains("ClusteredDeferred.hlsl", shadeSource,
            "The shared shading must reuse the ClusteredDeferred code.");
        StringAssert.Contains("CdShade(", shadeSource, "Direct lighting must go through CdShade.");
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
    public void SsrPass_LegacyEventStaysPrepass6_ForRollback()
    {
        var type = typeof(ScreenSpaceLightingRendererFeature).Assembly.GetType(
            "UnityEngine.Rendering.Universal.ScreenSpaceLightingSsrPass", throwOnError: false);
        Assert.IsNotNull(type, "ScreenSpaceLightingSsrPass was not found; the pass class was renamed.");
        var field = type.GetField("k_RenderPassEvent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.IsNotNull(field, "k_RenderPassEvent is not declared as a const any more.");

        var raw = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
        var expected = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 6);
        Assert.AreEqual((int)expected, Convert.ToInt32(raw),
            "The legacy SSR injection point must stay 206 for the ssrAfterGbuffer=false rollback path (C1).");
    }

    // --- P3 contract assertions. These are intentionally RED until B2–B4 implement the P3 path;
    // each failure names the artifact B2/B3/B4 must add. Keep them in the same change as the code.
    [Test]
    public void SsrPass_P3InjectsAfterDecalGbuffer_IsSwitched()
    {
        var type = typeof(ScreenSpaceLightingRendererFeature).Assembly.GetType(
            "UnityEngine.Rendering.Universal.ScreenSpaceLightingSsrPass", throwOnError: false);
        Assert.IsNotNull(type, "ScreenSpaceLightingSsrPass was not found.");
        var field = type.GetField("k_RenderPassEventAfterGbuffer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.IsNotNull(field, "P3 needs k_RenderPassEventAfterGbuffer; the pass must inject right after the Decal GBuffer event (C1).");
        var raw = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
        Assert.AreEqual((int)RenderPassEvent.AfterRenderingGbuffer + 1, Convert.ToInt32(raw),
            "The P3 SSR injection point must be AfterRenderingGbuffer + 1 (221), strictly after Decal GBuffer (220).");

        var pass = Normalize(ReadPackage("Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs"));
        StringAssert.Contains("ssrAfterGbuffer", pass,
            "The pass must choose its event from the ssrAfterGbuffer switch (C1/D6).");
    }

    [Test]
    public void SsrIndirectSpecularPass_InjectsBetweenGbufferAndDeferred()
    {
        var type = typeof(ScreenSpaceLightingRendererFeature).Assembly.GetType(
            "UnityEngine.Rendering.Universal.ScreenSpaceLightingSsrIndirectSpecularPass", throwOnError: false);
        Assert.IsNotNull(type,
            "P3 D1-A needs ScreenSpaceLightingSsrIndirectSpecularPass; the deferred receiver's indirect specular moves out of the GBuffer pass.");
        var field = type.GetField("k_RenderPassEvent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.IsNotNull(field, "The indirect-specular pass must expose k_RenderPassEvent.");
        var raw = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
        var expected = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingGbuffer + 5);
        Assert.AreEqual((int)expected, Convert.ToInt32(raw),
            "The indirect-specular composite must run at 225 (>220 and <230), before ClusteredDeferred (C1).");
    }

    [Test]
    public void SsrShadeSurface_IsSharedByHitKernels()
    {
        // The closest-hit material shader cannot include the compute ClusteredDeferred kernel (it
        // runs with per-draw material headers), so the shared single-point shading covers the two
        // compute hit kernels: the RT hit kernel and the P3 screen-space hit kernel. It stays the
        // P1 fallback used only when hitDeferredShading is off.
        var surface = ReadPackage("Shaders/RayTracing/SsrShadeSurface.hlsl");
        StringAssert.Contains("SsrShadeSurface(", surface,
            "The single-point shading function must live in SsrShadeSurface.hlsl (D3).");
        StringAssert.Contains("SsrShadedSample", surface,
            "The shared shader must expose the SsrShadedSample { radiance, weight } result (D3).");

        var kernel = Normalize(ReadPackage("Shaders/Utils/ScreenSpaceLighting/SsrHitDeferred.compute"));
        StringAssert.Contains("SsrShadeSurface.hlsl", kernel,
            "The RT hit kernel must call the shared SsrShadeSurface, not a second shading path (C8/D3).");
        StringAssert.Contains("SsrShadeSurface(", kernel,
            "The RT hit kernel must actually invoke SsrShadeSurface.");
    }

    [Test]
    public void SsrHitSs_ShadesScreenSpaceHitsFromCurrentGbuffer()
    {
        // P3 SS hits are re-shaded from the current-frame GBuffer by a dedicated compute kernel and
        // published with source 2; the RT hit kernel keeps reading the request-indexed records.
        var ss = Normalize(ReadPackage("Shaders/Utils/ScreenSpaceLighting/SsrHitSs.compute"));
        StringAssert.Contains("SsrHitSurfaceSourceGbuffer", ss,
            "SS hits read the current-frame GBuffer and must publish source 2 (C2.7/C8).");
        StringAssert.Contains("_GBuffer0", ss, "The SS hit kernel must read the GBuffer surface.");
        StringAssert.Contains("SsrShadeSurface(", ss, "The SS hit kernel must use the shared shading (D3).");

        var reproject = Normalize(ReadPackage("Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute"));
        StringAssert.Contains("SSR_HIT_FLAG_SS", reproject,
            "The temporal must classify SS-flagged hits: they never publish SS radiance as hardware fill, " +
            "but a slot with requestShare > 0 keeps its reprojected hardware complement (D4).");
    }

    // --- B5 orchestration tripwires: pass order, switch default and marker/keyword names.
    [Test]
    public void SsrP3_PassOrderIsMonotonic()
    {
        var asm = typeof(ScreenSpaceLightingRendererFeature).Assembly;
        int EventOf(string typeName, string fieldName)
        {
            var type = asm.GetType("UnityEngine.Rendering.Universal." + typeName, throwOnError: false);
            Assert.IsNotNull(type, typeName + " was not found.");
            var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(field, typeName + "." + fieldName + " is missing.");
            var raw = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
            return Convert.ToInt32(raw);
        }

        int ssr = EventOf("ScreenSpaceLightingSsrPass", "k_RenderPassEventAfterGbuffer"); // 221
        int bind = EventOf("ScreenSpaceLightingSsrBindPass", "k_RenderPassEvent");          // 222
        int indirect = EventOf("ScreenSpaceLightingSsrIndirectSpecularPass", "k_RenderPassEvent"); // 225
        int deferred = (int)RenderPassEvent.BeforeRenderingDeferredLights;                  // 230

        Assert.AreEqual((int)RenderPassEvent.AfterRenderingGbuffer + 1, ssr,
            "SSR must inject after the Decal GBuffer event (220), at 221.");
        Assert.AreEqual(ssr + 1, bind, "The SSR material binding must run at 222.");
        Assert.AreEqual(ssr + 4, indirect, "The indirect-specular composite must stay at 225 (>222 and <230).");
        Assert.Less(indirect, deferred, "The composite must run before ClusteredDeferred (230).");
        Assert.Less(ssr, bind, "Binding must follow the SSR pass.");
        Assert.Greater(ssr, (int)RenderPassEvent.AfterRenderingGbuffer,
            "Decal GBuffer injects exactly at AfterRenderingGbuffer; SSR must not share that event.");
    }

    // --- B7 audit corrections: decal-safe order, receiver availability, reason-6 complement and
    // the A2 probe environment source.
    [Test]
    public void SsrP3_ReceiverRerouteRequiresTheComposite()
    {
        // The GBuffer may only drop its indirect specular when the 225 pass can composite it.
        var setup = Normalize(ReadPackage("Runtime/RendererFeatures/ScreenSpaceLighting/Passes/ScreenSpaceLightingSetupPass.cs"));
        StringAssert.Contains("m_SsrIndirectPass.CanExecute(renderingData)", setup,
            "The receiver switch must be published from the 225 availability, not the raw feature bool.");
        StringAssert.Contains("routeReceiver?1f:0f", setup,
            "The published _GlxSsrAfterGbuffer must follow the availability result.");

        var pass = Normalize(ReadPackage("Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrIndirectSpecularPass.cs"));
        StringAssert.Contains("internalboolCanExecute(inRenderingDatarenderingData)", pass,
            "The composite must expose CanExecute so the GBuffer can degrade to the P1 path.");
        StringAssert.Contains("m_History.ssrIndirectSpecular==null", pass,
            "CanExecute must check the composite target.");
        StringAssert.Contains("GetCameraDepthTexture(renderer)==null", pass,
            "CanExecute must check the depth source the kernel loads from.");
        StringAssert.Contains("m_History.ssrWidth==width", pass,
            "CanExecute must reject a stale target size so the composite never samples the wrong extent.");
        StringAssert.Contains("if(!m_ReceiverRouted)", pass,
            "Execute must follow the Setup pass's routing decision instead of re-evaluating mid-frame.");
        StringAssert.Contains("SetReceiverRouted(routeReceiver)", setup,
            "The Setup pass must record the routing decision for the composite.");
    }

    [Test]
    public void SsrTemporal_ReprojectsTheHardwareShareForSsSlots()
    {
        // reason 6 (screen-edge partial coverage) keeps its hardware complement: an SS-flagged slot
        // no longer clears the hardware history; requestShare > 0 reprojects it for the merge.
        var compute = Normalize(ReadPackage("Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute"));
        StringAssert.Contains("boolisSs=(hit.flags&SSR_HIT_FLAG_SS)!=0u;", compute,
            "The temporal must classify SS-flagged hits for the history rule.");
        StringAssert.Contains("if(hit.flags!=0u&&!isSs)", compute,
            "SS-flagged hits must fall through to the hardware-history reprojection instead of clearing it.");
        StringAssert.Contains("floatssShare", Normalize(ReadPackage("Shaders/Utils/ScreenSpaceLighting/SsrRayRequest.hlsl")),
            "The edge share must stay in R1Sample.ssShare (C3).");
    }

    [Test]
    public void SsrIndirectSpecular_SelectsProbeEnvOnlyWhenBound()
    {
        var kernel = ReadPackage("Shaders/Utils/ScreenSpaceLighting/SsrIndirectSpecular.compute");
        var normalized = Normalize(kernel);
        StringAssert.Contains("#pragma multi_compile _ _GLX_SSR_PROBE_ENV", kernel.Replace("\r\n", "\n"),
            "A2 needs a keyword so the probe declarations are only compiled when the atlas is bound.");
        StringAssert.Contains("#ifdefined(_GLX_SSR_PROBE_ENV)", normalized,
            "The probe atlas include must be gated, or an unbound declared resource drops the dispatch.");
        StringAssert.Contains("GlxProbeQuery(positionWS,reflectVector,irradiance)", normalized.Replace(" ", ""),
            "A2 must sample the published atlas along the reflection direction.");
        StringAssert.Contains("_GlossyEnvironmentColor", normalized,
            "A3 must fall back to the uniform ambient colour.");

        var pass = Normalize(ReadPackage("Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrIndirectSpecularPass.cs"));
        StringAssert.Contains("ssrIndirectSource==SsrIndirectSource.A2", pass,
            "The composite must select A2 from the feature setting.");
        StringAssert.Contains("SetProbeEnvBound(bindProbe)", Normalize(ReadPackage(
            "Runtime/RendererFeatures/ScreenSpaceLighting/Passes/ScreenSpaceLightingSetupPass.cs")),
            "The Setup pass must report whether the published probe generation is bound.");
    }

    // --- B8 audit follow-up: both hit kernels must compile the same shadow set, and P3 cannot leave
    // the RT side on the closest-hit approximation while the SS side uses the shared kernel.
    [Test]
    public void SsrHitKernels_ShareTheShadowKeywords()
    {
        foreach (var file in new[] { "SsrHitDeferred.compute", "SsrHitSs.compute" })
        {
            var source = ReadPackage("Shaders/Utils/ScreenSpaceLighting/" + file).Replace("\r\n", "\n");
            StringAssert.Contains("#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE", source,
                file + " must compile the main-light shadow variants; without them the shared shading sees no shadows (D3).");
            StringAssert.Contains("#pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS", source,
                file + " must compile the additional-light shadow variant; without it every additional light is unshadowed (D3).");
        }
    }

    [Test]
    public void SsrP3_ForcesTheDeferredHitKernel()
    {
        var pass = Normalize(ReadPackage("Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs"));
        StringAssert.Contains("m_Settings.hitDeferredShading||m_Settings.ssrAfterGbuffer", pass,
            "P3 must run the RT hit kernel even when hitDeferredShading is off, or SS and RT hits use different shading.");
        StringAssert.Contains("s_HitDeferredSettings=useDeferredHitShading;", pass,
            "The evidence flag must record the effective decision, not only the serialized switch.");
    }

    // --- B6.1: the coverage ramp (merge borrow + silhouette confidence) and the miss-weighted R1
    // quota are P3-only. The P1 rollback path has frozen hashes, so none of this may run there.
    [Test]
    public void SsrB61_CoverageRampAndQuotaStayP3Only()
    {
        var compute = Normalize(ReadPackage("Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute"));
        var mergeGate = compute.IndexOf("#ifdefined(SSR_PBR_MERGE)", StringComparison.Ordinal);
        var borrow = compute.IndexOf("boolSsrBorrowMergedNeighbour(uint2coord,R1Samplecenter,", StringComparison.Ordinal);
        Assert.GreaterOrEqual(mergeGate, 0, "The merge kernel must stay behind SSR_PBR_MERGE.");
        Assert.Greater(borrow, mergeGate,
            "The neighbourhood borrow must compile only in the merge kernel; other variants cannot see _SsrMergeSource.");
        StringAssert.Contains("SsrBorrowMergedNeighbour(coord,q,radiance,borrowedOpacity);", compute,
            "A merge slot with zero coverage must borrow from its same-surface neighbours.");
        StringAssert.Contains("opacity=saturate(0.5*(weightSum/accepted));", compute,
            "The borrowed alpha must stay capped at 0.5 so the environment probe keeps its share.");

        var queue = Normalize(ReadPackage("Shaders/Utils/ScreenSpaceLighting/SsrRequestQueue.hlsl"));
        StringAssert.Contains("floatsilhouette=_R1WeightedQuota!=0u?SsrHitEdgeConfidence(hit):1.0;", queue,
            "The P3 silhouette confidence must be folded into the R1 coverage split, not the reproject,");
        StringAssert.Contains("s.ssShare*=silhouette;", queue,
            "so the faded share becomes a hardware request (reason 6) instead of empty coverage.");
        StringAssert.Contains("uintR1TileQuota(uintwanted,uinttotalWanted)", queue,
            "The R1 budget must be shared by wanted count (B6.1).");
        StringAssert.Contains("if(_R1WeightedQuota==0u||_R1K<=tiles)", queue,
            "The weighted quota must be gated: the P1 rollback path keeps its frozen even-split frames.");
        StringAssert.Contains("local+=R1TileQuota(_R1Tile[t].x,totalWanted);", queue,
            "The offsets scan must use the per-tile quota.");
        StringAssert.Contains("uintquota=_R1WeightedQuota!=0u?_R1Tile[t].x:R1Quota();", queue,
            "The scatter window stride must use the tile's served count only on the P3 path.");

        var pass = Normalize(ReadPackage("Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs"));
        StringAssert.Contains("SetComputeTextureParam(compute,mergeKernel,ScreenSpaceLightingShaderIDs.SsrDepthTexture,depthTexture);", pass,
            "The merge kernel needs the receiver depth the borrow acceptance reads.");
        StringAssert.Contains("SetComputeTextureParam(compute,mergeKernel,ScreenSpaceLightingShaderIDs.SsrNormalTexture,normalTexture);", pass,
            "The merge kernel needs the receiver normals the borrow acceptance reads.");
    }

    [Test]
    public void SsrIndirectKernels_Compile()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            Assert.Ignore("No graphics device; rerun without -nographics to compile the P3 compute kernels.");

        foreach (var item in new[]
        {
            ("Indirect specular", "Shaders/Utils/ScreenSpaceLighting/SsrIndirectSpecular.compute", "SsrIndirectSpecular"),
            ("SS hit", "Shaders/Utils/ScreenSpaceLighting/SsrHitSs.compute", "SsrHitSs"),
        })
        {
            ComputeShader shader = null;
            foreach (var root in new[]
            {
                PackageRoot,
                "Packages/com.unity.render-pipelines.universal",
            })
            {
                var path = root + "/" + item.Item2;
                if (!File.Exists(Path.Combine(ProjectRoot, path))) continue;
                UnityEditor.AssetDatabase.ImportAsset(path, UnityEditor.ImportAssetOptions.ForceSynchronousImport);
                shader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (shader != null) break;
            }
            Assert.IsNotNull(shader, item.Item1 + " compute shader did not load: " + item.Item2);
            Assert.IsTrue(shader.HasKernel(item.Item3),
                item.Item1 + " kernel '" + item.Item3 + "' is missing; the shader failed to compile on " +
                SystemInfo.graphicsDeviceType + ".");
        }
    }

    [Test]
    public void SsrP3_SwitchDefaultsOffAndKeywordsDeclared()
    {
        var settings = Normalize(ReadPackage("Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingSettings.cs"));
        StringAssert.Contains("internalboolssrAfterGbuffer=false;", settings,
            "ssrAfterGbuffer must default off until the B6 gates pass (D6).");

        StringAssert.Contains("_GLX_SSR_SPECULAR", ReadPackage("Shaders/Utils/ClusteredDeferred.compute"),
            "The deferred kernel must declare the gated specular variant.");
        StringAssert.Contains("_GLX_SSR_SHADE_SS", ReadPackage("Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute"),
            "The reproject must declare the gated SS-shading variant.");

        var pass = ReadPackage("Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs");
        StringAssert.Contains("SSR Hit Shading (RT)", pass, "The RT hit kernel marker must be named (RT).");
        StringAssert.Contains("SSR Hit Shading (SS)", pass, "The SS hit kernel marker must be named (SS).");
    }
}
