#ifndef GLX_LIT_HIT_INCLUDED
#define GLX_LIT_HIT_INCLUDED

// Share the complete raster UnityPerMaterial layout, including local refraction fields.
#include "../LitInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/BRDF.hlsl"
#include "UnityRayTracingMeshUtils.cginc"
#include "GlxBasicPayload.hlsl"
#include "GlxProbeSampling.hlsl"
#include "SsrHitSurface.hlsl"

struct GlxHitSurface { BRDFData brdf; float3 albedo; };
float3 GlxLightBrdf(GlxHitSurface s, float3 n, float3 l, uint mode)
{
    if (mode == GlxProbeMode) return s.albedo;
    return DirectBRDF(s.brdf, n, l, -WorldRayDirection());
}
#include "GlxHitLights.hlsl"

float3 GlxWeights(float2 bary)
{
    return float3(1.0 - bary.x - bary.y, bary);
}

float2 GlxBaseUV(uint3 tri, float3 w)
{
    float2 uv = w.x * UnityRayTracingFetchVertexAttribute2(tri.x, kVertexAttributeTexCoord0)
              + w.y * UnityRayTracingFetchVertexAttribute2(tri.y, kVertexAttributeTexCoord0)
              + w.z * UnityRayTracingFetchVertexAttribute2(tri.z, kVertexAttributeTexCoord0);
    return TRANSFORM_TEX(uv, _BaseMap);
}

float4 GlxSampleBase(float2 uv)
{
    return SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, uv, 0) * _BaseColor;
}

bool GlxDebugMode(uint mode)
{
    uint channel = mode & 255u;
    return channel >= 16u && channel <= 20u;
}

[shader("anyhit")]
void GlxLitAnyHit(inout GlxBasicPayload payload : SV_RayPayload,
                  BuiltInTriangleIntersectionAttributes attr : SV_IntersectionAttributes)
{
    if (GlxDebugMode(payload.mode))
        payload.mode += 256u; // Count actual traversal invocations, including rejected candidates.
#ifdef _ALPHATEST_ON
    uint3 tri = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
    if (GlxSampleBase(GlxBaseUV(tri, GlxWeights(attr.barycentrics))).a < _Cutoff)
        IgnoreHit();
#endif
}

float3 GlxNormalWS(uint3 tri, float3 w, float2 uv)
{
    float3x3 worldToObject = (float3x3)WorldToObject3x4();
    float3 n = 0;
#ifdef _NORMALMAP
    float3x3 objectToWorld = (float3x3)ObjectToWorld3x4();
    float4 t = 0;
#endif
    // Lit transforms and normalizes each vertex frame before raster interpolation.
    [unroll] for (uint i = 0; i < 3; ++i)
    {
        float3 normalOS = UnityRayTracingFetchVertexAttribute3(tri[i], kVertexAttributeNormal);
        n += w[i] * normalize(mul(normalOS, worldToObject));
#ifdef _NORMALMAP
        float4 tangentOS = UnityRayTracingFetchVertexAttribute4(tri[i], kVertexAttributeTangent);
        t += w[i] * float4(normalize(mul(objectToWorld, tangentOS.xyz)), tangentOS.w);
#endif
    }
#ifdef _NORMALMAP
    float3 b = cross(n, t.xyz) * (t.w * sign(determinant(objectToWorld)));
    float3 ts = UnpackNormalScale(SAMPLE_TEXTURE2D_LOD(_BumpMap, sampler_BumpMap, uv, 0), _BumpScale);
    n = t.xyz * ts.x + b * ts.y + n * ts.z;
#endif
    return normalize(n);
}

[shader("closesthit")]
void GlxLitClosestHit(inout GlxBasicPayload payload : SV_RayPayload,
                      BuiltInTriangleIntersectionAttributes attr : SV_IntersectionAttributes)
{
    if (payload.mode == GlxShadowMode)
    {
        payload.flags = GlxHit; // T3 visibility does not distinguish front and back faces.
        return;
    }

    if (payload.mode == GlxProbeGeoMode)
    {
        uint3 gt = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
        float3 gw = GlxWeights(attr.barycentrics);
        float2 guv = GlxBaseUV(gt, gw);
        payload.flags = GlxHit;
        payload.hitT = RayTCurrent();
        payload.radiance = GlxNormalWS(gt, gw, guv);
        return;
    }

    uint3 tri = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
    float3 w = GlxWeights(attr.barycentrics);
    float2 uv = GlxBaseUV(tri, w);
    float4 base = GlxSampleBase(uv);
    float metallic = _Metallic;
    float smoothness = _Smoothness;
#ifdef _METALLICSPECGLOSSMAP
    float4 gloss = SAMPLE_TEXTURE2D_LOD(_MetallicGlossMap, sampler_MetallicGlossMap, uv, 0);
    metallic = gloss.r;
    smoothness *= gloss.a;
#endif
    float3 emission = 0;
#ifdef _EMISSION
    emission = SAMPLE_TEXTURE2D_LOD(_EmissionMap, sampler_EmissionMap, uv, 0).rgb * _EmissionColor.rgb;
#endif
    float3 n = GlxNormalWS(tri, w, uv);
    if (GlxDebugMode(payload.mode))
    {
        uint channel = payload.mode & 255u;
        if (channel == 20u)
        {
            float3 hitPosition = WorldRayOrigin() + RayTCurrent() * WorldRayDirection();
            float3 hitNormal = dot(n, WorldRayDirection()) > 0 ? -n : n;
            payload.radiance = GlxProbeFeedbackRadiance(hitPosition, hitNormal, base.rgb);
        }
        else
        {
            payload.radiance = channel == 16u ? base.rgb : channel == 17u ? n :
                               channel == 18u ? float3(metallic, smoothness, base.a) : emission;
        }
        payload.hitT = RayTCurrent();
        payload.flags = GlxHit;
        return; // Preserve the caller's channel and traversal count.
    }

    if (GlxUsesLightTable(payload.mode))
    {
        uint mode = payload.mode;
        payload.flags = GlxHit;
        payload.hitT = RayTCurrent();
        payload.radiance = emission;
        GlxHitSurface surface;
        surface.albedo = base.rgb;
        half alpha = base.a;
        InitializeBRDFData(base.rgb, metallic, 0, smoothness, alpha, surface.brdf);
        float3 hitNormal = dot(n, WorldRayDirection()) > 0 ? -n : n;
        if (mode == GlxReflectionMode)
        {
            // C2.7 transport: the raygen publishes the surface record; shadow and probe
            // modes must leave these fields untouched.
            payload.ssrBaseColor = base.rgb;
            payload.ssrNormalOct = SsrEncodeOctNormal(hitNormal);
            payload.ssrRoughness = 1.0 - smoothness;
            payload.ssrMetallic = metallic;
            payload.ssrEmissive = emission;
        }
        GlxShadeLights(payload, hitNormal, surface);
        // Reflection hits take the explicit ambient/probe low-frequency term (never feedback).
        // Probe hits add the single 1-hop feedback term from the previous published generation;
        // it is read once here and never traced again, so this stays a cache update, not a path.
        float3 hitPosition = WorldRayOrigin() + RayTCurrent() * WorldRayDirection();
        if (mode == GlxReflectionMode)
        {
            payload.radiance += surface.brdf.diffuse *
                (GlxHitLowFrequencyIrradiance(hitPosition, hitNormal) * 0.31830988618);
        }
        else
        {
            payload.radiance += GlxProbeFeedbackRadiance(hitPosition, hitNormal, surface.albedo);
        }
        return;
    }
    float3 toLight = normalize(float3(f16tof32(payload.flags & 0xffffu), f16tof32(payload.flags >> 16), payload.hitT));
    float3 lightRgb = payload.radiance;
    bool traceShadow = payload.mode == GlxPrimaryWithShadow;
    payload.mode = GlxPrimaryMode;
    payload.flags = GlxHit;
    payload.hitT = RayTCurrent();
    payload.radiance = emission;
    float3 viewWS = -WorldRayDirection();
    n = dot(n, viewWS) < 0 ? -n : n;
    float nDotL = saturate(dot(n, toLight));
    if (nDotL <= 0)
        return;

    if (traceShadow)
    {
        RayDesc shadow;
        shadow.Origin = WorldRayOrigin() + payload.hitT * WorldRayDirection() + n * 0.002;
        shadow.Direction = toLight;
        shadow.TMin = 0;
        shadow.TMax = 3.402823466e+38F;
        GlxBasicPayload visibility = GlxEmptyPayload(GlxShadowMode);
        TraceRay(_GlxScene, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH, 0xff, 0, 1, 0, shadow, visibility);
        if (visibility.flags == GlxHit)
        {
            payload.flags |= GlxShadowed;
            return;
        }
    }

    BRDFData brdf;
    half alpha = base.a;
    InitializeBRDFData(base.rgb, metallic, 0, smoothness, alpha, brdf);
    // URP direct metallic BRDF; T3 supplies irradiance rather than pi-normalized light color.
    payload.radiance += DirectBRDF(brdf, n, toLight, viewWS) * lightRgb * (nDotL * INV_PI);
}

#endif
