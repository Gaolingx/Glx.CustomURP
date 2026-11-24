#ifndef GLX_BASIC_PAYLOAD_INCLUDED
#define GLX_BASIC_PAYLOAD_INCLUDED

// Also the structured result ABI: byte offsets 0, 12, 16, 20; stride 24.
struct GlxBasicPayload
{
    float3 radiance;
    float hitT;
    uint flags;
    uint mode;
};

static const uint GlxPrimaryMode = 0u;
static const uint GlxShadowMode = 1u;
static const uint GlxPrimaryWithShadow = 2u;
static const uint GlxInvalid = 0u;
static const uint GlxMiss = 1u;
static const uint GlxHit = 2u;
static const uint GlxShadowed = 4u;

GlxBasicPayload GlxEmptyPayload(uint mode)
{
    GlxBasicPayload p;
    p.radiance = 0.0;
    p.hitT = -1.0;
    p.flags = GlxInvalid;
    p.mode = mode;
    return p;
}

RaytracingAccelerationStructure _GlxScene;

#endif
