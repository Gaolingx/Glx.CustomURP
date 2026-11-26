#ifndef GLX_SSR_HIT_SURFACE_INCLUDED
#define GLX_SSR_HIT_SURFACE_INCLUDED

#include "GlxBasicPayload.hlsl"

// C2.7 reflection-hit surface ABI, written by the SSR raygen after TraceRay. Unity's
// raytracing pipeline forbids UAV writes from material closest-hit passes, so the CHS only
// fills the payload transport (C2, offsets 24..63) and the raygen assembles this record.
// Channel semantics are frozen; the packed storage width (8-bit vs 16F) is a P2 A/B
// decision and may change encode/decode only, never field order, field count or stride.
// stride 80
// gb0: xyz linear baseColor, w specular occlusion (1 when unknown)
// gb1: xyz world-space shading normal, w perceptual roughness
// gb2: xyz fresnel0, w coat mask (URP has no clear coat, always 0)
// gb3: xyz emissive including baked, w reserved (0)
// misc: x distance (RayTCurrent, > 0), y surfaceType, z version, w source flags
struct SsrHitSurface
{
    float4 gb0;
    float4 gb1;
    float4 gb2;
    float4 gb3;
    float4 misc;
};

// 0 marks a cleared or unwritten slot. Any semantic change must bump the version.
static const uint SsrHitSurfaceVersion = 1u;
static const uint SsrSurfaceTypeLit = 0u;
static const uint SsrSurfaceTypeSimpleLit = 1u;
static const uint SsrSurfaceTypeUnlit = 2u;
// misc.w bit 0: 0 = ray traced hit, 1 = screen-space hit (P3). Other bits stay reserved.
static const uint SsrHitSurfaceSourceMask = 1u;
static const uint SsrHitSurfaceSourceRt = 0u;
static const uint SsrHitSurfaceSourceSs = 1u;

// Octahedral normal in float2: full float precision would cost the payload 4 more bytes
// than the 64-byte cap allows.
float2 SsrEncodeOctNormal(float3 n)
{
    n /= max(abs(n.x) + abs(n.y) + abs(n.z), 1e-8);
    return n.z >= 0.0 ? n.xy : (1.0 - abs(n.yx)) * sign(n.xy);
}

float3 SsrDecodeOctNormal(float2 e)
{
    float3 n = float3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0)
        n.xy = (1.0 - abs(n.yx)) * (n.xy >= 0.0 ? 1.0 : -1.0);
    return normalize(n);
}

// Raygen only: assemble the published-surface record from the reflection payload.
SsrHitSurface SsrBuildHitSurface(in GlxBasicPayload p)
{
    SsrHitSurface s;
    float3 dielectric = float3(0.04, 0.04, 0.04); // URP kDielectricSpec.rgb
    s.gb0 = float4(p.ssrBaseColor, 1.0);
    s.gb1 = float4(SsrDecodeOctNormal(p.ssrNormalOct), p.ssrRoughness);
    s.gb2 = float4(lerp(dielectric, p.ssrBaseColor, p.ssrMetallic), 0.0);
    s.gb3 = float4(p.ssrEmissive, 0.0);
    s.misc = float4(p.hitT, (float)SsrSurfaceTypeLit, (float)SsrHitSurfaceVersion, (float)SsrHitSurfaceSourceRt);
    return s;
}

#endif
