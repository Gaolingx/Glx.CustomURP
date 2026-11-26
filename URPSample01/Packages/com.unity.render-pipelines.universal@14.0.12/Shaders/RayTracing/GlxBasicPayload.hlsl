#ifndef GLX_BASIC_PAYLOAD_INCLUDED
#define GLX_BASIC_PAYLOAD_INCLUDED

// Ray payload. Offsets 0, 12, 16, 20 are the published result (C2); offsets 24..63 carry
// the reflection surface transport (C2.7). Material closest-hit passes cannot write UAVs
// in Unity's raytracing pipeline, so the surface comes back here and the SSR raygen writes
// the 80-byte hit-surface record. Published stride 24; payload stride 64.
// Transport offsets: roughness @24, metallic @28, normal octahedral @32,
// baseColor @40, emissive @52. The float2 sits on an 8-byte boundary so 4- and 8-byte
// member alignment agree; float3 members keep the 4-byte packing the published head proves.
struct GlxBasicPayload
{
    float3 radiance;
    float hitT;
    uint flags;
    uint mode;
    float ssrRoughness;
    float ssrMetallic;
    float2 ssrNormalOct;
    float3 ssrBaseColor;
    float3 ssrEmissive;
};

#include "GlxPublishedHit.hlsl"

static const uint GlxPrimaryMode = 0u;
static const uint GlxShadowMode = 1u;
static const uint GlxPrimaryWithShadow = 2u;
// Table modes: direct light comes from GlxHitLightTable, not the outbound payload slots.
static const uint GlxReflectionMode = 3u;
static const uint GlxProbeMode = 4u;
// Maintenance mode: the first 32 fixed probe rays return the shading normal instead of
// radiance so relocation/classification can test front/back faces inside the 24-byte ABI.
static const uint GlxProbeGeoMode = 5u;
static const uint GlxInvalid = 0u;
static const uint GlxMiss = 1u;
static const uint GlxHit = 2u;
static const uint GlxShadowed = 4u;

bool GlxUsesLightTable(uint mode) { return mode == GlxReflectionMode || mode == GlxProbeMode; }

GlxBasicPayload GlxEmptyPayload(uint mode)
{
    GlxBasicPayload p;
    p.radiance = 0.0;
    p.hitT = -1.0;
    p.flags = GlxInvalid;
    p.mode = mode;
    p.ssrBaseColor = 0.0;
    p.ssrNormalOct = 0.0;
    p.ssrRoughness = 1.0;
    p.ssrMetallic = 0.0;
    p.ssrEmissive = 0.0;
    return p;
}

GlxPublishedHit GlxPublishPayload(in GlxBasicPayload p)
{
    GlxPublishedHit r;
    r.radiance = p.radiance;
    r.hitT = p.hitT;
    r.flags = p.flags;
    r.mode = p.mode;
    return r;
}

RaytracingAccelerationStructure _GlxScene;

#endif
