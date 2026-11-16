#ifndef UNIVERSAL_SCREEN_SPACE_LIGHTING_REFLECTION_PROBE_FALLBACK_INCLUDED
#define UNIVERSAL_SCREEN_SPACE_LIGHTING_REFLECTION_PROBE_FALLBACK_INCLUDED

// Reflection probe fallback for the SSGI miss path.
//
// URP publishes the probe atlas and the per-probe boxes as globals (ReflectionProbeManager.cs). In
// Forward+ it also publishes the zbin/tile cluster buffers (ForwardLights.cs), so a compute dispatch
// can read the same per-pixel probe list the fragment path uses in GlobalIllumination.hlsl. This file
// repeats only the pieces of Input.hlsl and Clustering.hlsl that are compute safe: Input.hlsl pulls
// per-draw state and the full URP lighting library, which a compute kernel must not depend on.
//
// Differences from the fragment path:
// - the probe is sampled at mip 0 (HDRP samples its env atlas at LOD 0 for SSGI), not at the
//   roughness-derived specular mip;
// - box projection follows the per-probe flag instead of the per-material keyword, matching HDRP's
//   per-probe influence shape;
// - Forward and Deferred have no cluster list, so they iterate every visible probe instead (the box
//   weight early-outs and does not depend on the engine's probe ordering).
// - the cluster iterator receives the camera position and view forward explicitly because compute
//   kernels have no per-draw view constants.

#include "Packages/com.unity.render-pipelines.universal-config/Runtime/ShaderConfig.cs.hlsl"

#if defined(SHADER_API_MOBILE) && (defined(SHADER_API_GLES) || defined(SHADER_API_GLES30))
    #define SSL_MAX_VISIBLE_LIGHTS MAX_VISIBLE_LIGHT_COUNT_LOW_END_MOBILE
#elif defined(SHADER_API_MOBILE) || (defined(SHADER_API_GLCORE) && !defined(SHADER_API_SWITCH)) || defined(SHADER_API_GLES) || defined(SHADER_API_GLES3)
    #define SSL_MAX_VISIBLE_LIGHTS MAX_VISIBLE_LIGHT_COUNT_MOBILE
#else
    #define SSL_MAX_VISIBLE_LIGHTS MAX_VISIBLE_LIGHT_COUNT_DESKTOP
#endif

// Must match the values in Input.hlsl; the cbuffer layout depends on them.
#define SSL_MAX_ZBIN_VEC4S 1024
#if SSL_MAX_VISIBLE_LIGHTS <= 32
    #define SSL_MAX_REFLECTION_PROBES SSL_MAX_VISIBLE_LIGHTS
    #define SSL_MAX_TILE_VEC4S 1024
#else
    #define SSL_MAX_REFLECTION_PROBES 64
    #define SSL_MAX_TILE_VEC4S 4096
#endif

float4 _FPParams0;
float4 _FPParams1;
float4 _FPParams2;

#define URP_FP_ZBIN_SCALE (_FPParams0.x)
#define URP_FP_ZBIN_OFFSET (_FPParams0.y)
#define URP_FP_PROBES_BEGIN ((uint)_FPParams0.z)
#define URP_FP_TILE_SCALE ((float2)_FPParams1.xy)
#define URP_FP_TILE_COUNT_X ((uint)_FPParams1.z)
#define URP_FP_WORDS_PER_TILE ((uint)_FPParams1.w)
#define URP_FP_ZBIN_COUNT ((uint)_FPParams2.x)
#define URP_FP_TILE_COUNT ((uint)_FPParams2.y)

CBUFFER_START(URP_ZBinBuffer)
    float4 URP_ZBins[SSL_MAX_ZBIN_VEC4S];
CBUFFER_END
CBUFFER_START(urp_TileBuffer)
    float4 urp_Tiles[SSL_MAX_TILE_VEC4S];
CBUFFER_END

TEXTURE2D(urp_ReflProbes_Atlas);
SAMPLER(samplerurp_ReflProbes_Atlas);
float urp_ReflProbes_Count;

#ifndef SHADER_API_GLES3
CBUFFER_START(urp_ReflectionProbeBuffer)
#endif
float4 urp_ReflProbes_BoxMax[SSL_MAX_REFLECTION_PROBES];          // w: blend distance
float4 urp_ReflProbes_BoxMin[SSL_MAX_REFLECTION_PROBES];          // w: importance
float4 urp_ReflProbes_ProbePosition[SSL_MAX_REFLECTION_PROBES];   // w: > 0 box projection, |w| max mip
float4 urp_ReflProbes_MipScaleOffset[SSL_MAX_REFLECTION_PROBES * 7];
#ifndef SHADER_API_GLES3
CBUFFER_END
#endif

// x: 1 for a perspective projection, 0 for orthographic.
float4 _SsgiProbeAtlasParams;

// Select uint4 component by index. Copied from ShaderVariablesFunctions.hlsl because that file pulls
// the fragment-only URP state and cannot be included from a compute kernel.
uint SslSelect4(uint4 v, uint i)
{
    uint mask0 = uint(int(i << 31) >> 31);
    uint mask1 = uint(int(i << 30) >> 31);
    return
        (((v.w & mask0) | (v.z & ~mask0)) & mask1) |
        (((v.y & mask0) | (v.x & ~mask0)) & ~mask1);
}

struct SslProbeClusterIterator
{
    uint tileOffset;
    uint zBinOffset;
    uint tileMask;
    // Same packing as Clustering.hlsl: next entity index in the low 16 bits, max index in the high.
    uint entityIndexNextMax;
};

SslProbeClusterIterator SslProbeClusterInit(float2 normalizedScreenSpaceUV, float3 positionWS)
{
    SslProbeClusterIterator state = (SslProbeClusterIterator)0;

    uint2 tileId = uint2(normalizedScreenSpaceUV * URP_FP_TILE_SCALE);
    state.tileOffset = tileId.y * URP_FP_TILE_COUNT_X + tileId.x;
#if defined(SSL_XR_ARRAY)
    state.tileOffset += URP_FP_TILE_COUNT * SSGI_SLICE;
#endif
    state.tileOffset *= URP_FP_WORDS_PER_TILE;

    // A compute dispatch has no view matrix, so the forward direction comes from the centre ray of
    // the inverse view-projection, which is also correct for the per-eye XR matrices.
    float3 cameraPosWS = SSL_SSGI_CAMERA_POSITION_WS;
    float4 farPointCS = mul(SSL_INV_VIEW_PROJ, float4(0.0, 0.0, 1.0, 1.0));
    float3 viewForward = normalize(farPointCS.xyz / farPointCS.w - cameraPosWS);

    float viewZ = dot(viewForward, positionWS - cameraPosWS);
    float depthTerm = _SsgiProbeAtlasParams.x > 0.0 ? log2(max(viewZ, 1e-6)) : viewZ;
    uint zBinBaseIndex = (uint)(depthTerm * URP_FP_ZBIN_SCALE + URP_FP_ZBIN_OFFSET);
#if defined(SSL_XR_ARRAY)
    zBinBaseIndex += URP_FP_ZBIN_COUNT * SSGI_SLICE;
#endif
    zBinBaseIndex = zBinBaseIndex * (2 + URP_FP_WORDS_PER_TILE);
    zBinBaseIndex = min(zBinBaseIndex, 4 * SSL_MAX_ZBIN_VEC4S - (2 + URP_FP_WORDS_PER_TILE));

    // Header 1 is the reflection probe list; header 0 is the punctual light list.
    uint zBinHeaderIndex = zBinBaseIndex + 1;
    state.zBinOffset = zBinBaseIndex + 2;
    state.entityIndexNextMax = SslSelect4(asuint(URP_ZBins[zBinHeaderIndex / 4]), zBinHeaderIndex % 4);

    return state;
}

bool SslProbeClusterNext(inout SslProbeClusterIterator it, out uint entityIndex)
{
    uint maxIndex = it.entityIndexNextMax >> 16;
    while (it.tileMask == 0 && (it.entityIndexNextMax & 0xFFFF) <= maxIndex)
    {
        uint wordIndex = ((it.entityIndexNextMax & 0xFFFF) >> 5);
        uint tileIndex = it.tileOffset + wordIndex;
        uint zBinIndex = it.zBinOffset + wordIndex;
        it.tileMask =
            SslSelect4(asuint(urp_Tiles[tileIndex / 4]), tileIndex % 4) &
            SslSelect4(asuint(URP_ZBins[zBinIndex / 4]), zBinIndex % 4) &
            (0xFFFFFFFFu << (it.entityIndexNextMax & 0x1F)) &
            (0xFFFFFFFFu >> (31 - min(31, maxIndex - wordIndex * 32)));
        it.entityIndexNextMax = (it.entityIndexNextMax + 32) & ~31;
    }

    bool hasNext = it.tileMask != 0;
    uint bitIndex = firstbitlow(it.tileMask);
    it.tileMask ^= (1 << bitIndex);
    entityIndex = (((it.entityIndexNextMax - 32) & (0xFFFF & ~31))) + bitIndex;
    return hasNext;
}

float SslCalculateProbeWeight(float3 positionWS, float4 probeBoxMin, float4 probeBoxMax)
{
    float blendDistance = probeBoxMax.w;
    float3 weightDir = min(positionWS - probeBoxMin.xyz, probeBoxMax.xyz - positionWS) / blendDistance;
    return saturate(min(weightDir.x, min(weightDir.y, weightDir.z)));
}

// Same box projection as GlobalIllumination.hlsl; the per-probe flag decides whether it applies.
float3 SslBoxProjectedCubemapDirection(float3 reflectionWS, float3 positionWS, float4 cubemapPositionWS,
    float4 boxMin, float4 boxMax)
{
    if (cubemapPositionWS.w > 0.0)
    {
        float3 boxMinMax = (reflectionWS > 0.0) ? boxMax.xyz : boxMin.xyz;
        float3 rbMinMax = (boxMinMax - positionWS) / reflectionWS;
        float fa = min(min(rbMinMax.x, rbMinMax.y), rbMinMax.z);
        float3 worldPos = positionWS - cubemapPositionWS.xyz;
        return worldPos + reflectionWS * fa;
    }

    return reflectionWS;
}

// Returns the probe radiance and the accumulated weight (saturated to 1, like the fragment path).
// Callers add the sky ambient probe with the remaining weight when the hierarchy asks for it.
float3 SslSampleReflectionProbesAtlas(float3 directionWS, float3 positionWS,
    float2 normalizedScreenSpaceUV, out float totalWeight)
{
    totalWeight = 0.0;
    float3 color = float3(0.0, 0.0, 0.0);

    SslProbeClusterIterator it = SslProbeClusterInit(normalizedScreenSpaceUV, positionWS);
    uint probeIndex;
    [loop] while (SslProbeClusterNext(it, probeIndex) && totalWeight < 0.99)
    {
        probeIndex -= URP_FP_PROBES_BEGIN;
        if (probeIndex >= (uint)urp_ReflProbes_Count)
            continue;

        float weight = SslCalculateProbeWeight(positionWS, urp_ReflProbes_BoxMin[probeIndex],
            urp_ReflProbes_BoxMax[probeIndex]);
        weight = min(weight, 1.0 - totalWeight);
        if (weight <= 0.0)
            continue;

        float3 sampleVector = SslBoxProjectedCubemapDirection(directionWS, positionWS,
            urp_ReflProbes_ProbePosition[probeIndex], urp_ReflProbes_BoxMin[probeIndex],
            urp_ReflProbes_BoxMax[probeIndex]);

        float2 uv = saturate(PackNormalOctQuadEncode(sampleVector) * 0.5 + 0.5);
        float4 scaleOffset = urp_ReflProbes_MipScaleOffset[probeIndex * 7];
        float3 probeColor = SAMPLE_TEXTURE2D_LOD(urp_ReflProbes_Atlas, samplerurp_ReflProbes_Atlas,
            uv * scaleOffset.xy + scaleOffset.zw, 0.0).rgb;

        color += weight * probeColor;
        totalWeight += weight;
    }

    return color;
}

// Non-clustered path for Forward and Deferred: the atlas holds every visible probe, so the list is
// walked directly. The semantics match the cluster version (box weight, remaining-weight clamp,
// box projection and mip 0).
float3 SslSampleReflectionProbesList(float3 directionWS, float3 positionWS, out float totalWeight)
{
    totalWeight = 0.0;
    float3 color = float3(0.0, 0.0, 0.0);

    uint probeCount = (uint)urp_ReflProbes_Count;
    [loop] for (uint probeIndex = 0; probeIndex < probeCount && totalWeight < 0.99; probeIndex++)
    {
        float weight = SslCalculateProbeWeight(positionWS, urp_ReflProbes_BoxMin[probeIndex],
            urp_ReflProbes_BoxMax[probeIndex]);
        weight = min(weight, 1.0 - totalWeight);
        if (weight <= 0.0)
            continue;

        float3 sampleVector = SslBoxProjectedCubemapDirection(directionWS, positionWS,
            urp_ReflProbes_ProbePosition[probeIndex], urp_ReflProbes_BoxMin[probeIndex],
            urp_ReflProbes_BoxMax[probeIndex]);

        float2 uv = saturate(PackNormalOctQuadEncode(sampleVector) * 0.5 + 0.5);
        float4 scaleOffset = urp_ReflProbes_MipScaleOffset[probeIndex * 7];
        float3 probeColor = SAMPLE_TEXTURE2D_LOD(urp_ReflProbes_Atlas, samplerurp_ReflProbes_Atlas,
            uv * scaleOffset.xy + scaleOffset.zw, 0.0).rgb;

        color += weight * probeColor;
        totalWeight += weight;
    }

    return color;
}

#endif