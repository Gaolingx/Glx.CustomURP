#ifndef UNIVERSAL_GTAO_COMMON_INCLUDED
#define UNIVERSAL_GTAO_COMMON_INCLUDED

// Ground Truth Ambient Occlusion, ported from HDRP 14.0.12
// Runtime/Lighting/ScreenSpaceLighting/GTAOCommon.hlsl.
// Changes from the HDRP source:
// - the packed depth pyramid, normal texture and motion texture are bound by URP explicitly;
// - the AO result is converted from HDRP's inverse AO (0 = unoccluded) to URP's visibility
//   factor (1 = unoccluded, white neutral);
// - all private resource names carry the _Gtao prefix;
// - neighbour fetches use Load instead of GatherRed: the sampler HDRP relies on is not bound in the
//   URP compatibility compute path and GatherRed then returns a degenerate screen-column pattern.

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
#include "Packages/com.unity.render-pipelines.universal/Runtime/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionShaderVariables.cs.hlsl"

#define _GtaoFOVCorrection _GtaoParams0.y
#define _GtaoRadiusInWorld _GtaoParams0.z
#define _GtaoStepCount (uint)_GtaoParams0.w
#define _GtaoIntensity _GtaoParams1.x
#define _GtaoInvRadiusSq _GtaoParams1.y
#define _GtaoTemporalOffsetIdx _GtaoParams1.z
#define _GtaoTemporalRotationIdx _GtaoParams1.w
#define _GtaoInvStepCountPlusOne _GtaoParams2.z
#define _GtaoMaxRadiusInPixels (int)_GtaoParams2.w
#define _GtaoHistorySize _GtaoParams2.xy
#define _GtaoDirectionCount _GtaoParams4.x
#define _GtaoFirstDepthMipOffset _GtaoFirstTwoDepthMipOffsets.xy
#define _GtaoSecondDepthMipOffset _GtaoFirstTwoDepthMipOffsets.zw

// Denoise parameters, temporal or not.
#define _GtaoBlurTolerance _GtaoParams3.x
#define _GtaoUpsampleTolerance _GtaoParams3.y
#define _GtaoNoiseFilterStrength _GtaoParams3.z
#define _GtaoStepSize _GtaoParams3.w
#define _GtaoTemporalUpperNudgeLimit _GtaoParams4.y
#define _GtaoTemporalLowerNudgeLimit _GtaoParams4.z
#define _GtaoSpatialBilateralAggressiveness _GtaoParams4.w

// Every level of the packed atlas is addressed with the same 2D coordinate macros as a plain texture.
#if defined(GTAO_XR_ARRAY)
    static uint gtaoViewIndex;
    #define GTAO_ASSIGN_VIEW_INDEX(idx) gtaoViewIndex = (idx)
    #define GTAO_SLICE gtaoViewIndex
    #define GTAO_TEXTURE2D_FLOAT(name) Texture2DArray<float> name
    #define GTAO_TEXTURE2D_FLOAT2(name) Texture2DArray<float2> name
    #define GTAO_TEXTURE2D_FLOAT4(name) Texture2DArray<float4> name
    #define GTAO_RW_TEXTURE2D_FLOAT(name) RWTexture2DArray<float> name
    #define GTAO_RW_TEXTURE2D_FLOAT4(name) RWTexture2DArray<float4> name
    #define GTAO_LOAD(t, p) t.Load(int4((int2)(p), GTAO_SLICE, 0))
    #define GTAO_STORE(t, p, v) t[int3((int2)(p), GTAO_SLICE)] = (v)

    // Single-pass instanced XR renders both eyes in one pass. Each eye has its own view rotation,
    // so the world-to-view rows are supplied per view and selected with the dispatch slice.
    float4 _GtaoXrViewRow0[2];
    float4 _GtaoXrViewRow1[2];
    float4 _GtaoXrViewRow2[2];
    #define GTAO_VIEW_ROW0 (_GtaoXrViewRow0[GTAO_SLICE])
    #define GTAO_VIEW_ROW1 (_GtaoXrViewRow1[GTAO_SLICE])
    #define GTAO_VIEW_ROW2 (_GtaoXrViewRow2[GTAO_SLICE])
#else
    #define GTAO_ASSIGN_VIEW_INDEX(idx)
    #define GTAO_SLICE 0
    #define GTAO_TEXTURE2D_FLOAT(name) Texture2D<float> name
    #define GTAO_TEXTURE2D_FLOAT2(name) Texture2D<float2> name
    #define GTAO_TEXTURE2D_FLOAT4(name) Texture2D<float4> name
    #define GTAO_RW_TEXTURE2D_FLOAT(name) RWTexture2D<float> name
    #define GTAO_RW_TEXTURE2D_FLOAT4(name) RWTexture2D<float4> name
    #define GTAO_LOAD(t, p) t.Load(int3((int2)(p), 0))
    #define GTAO_STORE(t, p, v) t[(int2)(p)] = (v)
    #define GTAO_VIEW_ROW0 _GtaoViewRow0
    #define GTAO_VIEW_ROW1 _GtaoViewRow1
    #define GTAO_VIEW_ROW2 _GtaoViewRow2
#endif

// Private packed depth pyramid of this feature. It never aliases the Screen Space Lighting pyramid.
GTAO_TEXTURE2D_FLOAT(_GtaoDepthPyramid);

float GTAOLoadDepth(int2 pixel)
{
    return GTAO_LOAD(_GtaoDepthPyramid, pixel);
}

float GTAOGetDepthForCentral(float2 positionSS)
{
#ifdef FULL_RES
    return GTAOLoadDepth((int2)positionSS);
#else
    return GTAOLoadDepth((int2)(_GtaoFirstDepthMipOffset + positionSS));
#endif
}

float GTAOGetDepthSample(float2 positionSS, bool lowerRes)
{
#ifdef FULL_RES
    // HALF_RES_DEPTH_WHEN_FULL_RES: full resolution AO reads the half resolution pyramid level.
    return GTAOLoadDepth((int2)(_GtaoFirstDepthMipOffset + (int2)positionSS / 2));
#else
    if (lowerRes)
        return GTAOLoadDepth((int2)(_GtaoSecondDepthMipOffset + (int2)positionSS / 2));

    return GTAOLoadDepth((int2)(_GtaoFirstDepthMipOffset + (int2)positionSS));
#endif
}

float3 GTAOGetPositionVS(float2 positionSS, float rawDepth)
{
    float linearDepth = LinearEyeDepth(rawDepth, _GtaoZBufferParams);
#ifdef GTAO_ORTHO
    float2 ndc = positionSS * _GtaoBufferSize.zw * 2.0 - 1.0;
    return float3(ndc.x * _GtaoOrthoHalfSize.x, ndc.y * _GtaoOrthoHalfSize.y, linearDepth);
#else
    return float3((positionSS * _GtaoDepthToViewParams.xy - _GtaoDepthToViewParams.zw) * linearDepth, linearDepth);
#endif
}

float3 GTAOTransformWorldToView(float3 v)
{
    return float3(
        dot(GTAO_VIEW_ROW0.xyz, v),
        dot(GTAO_VIEW_ROW1.xyz, v),
        dot(GTAO_VIEW_ROW2.xyz, v));
}

float3 GTAOGetNormalVS(float4 normalBufferData)
{
#if defined(GTAO_NORMALS_OCT)
    float2 remappedOctNormalWS = Unpack888ToFloat2(normalBufferData.rgb);
    float3 normalWS = UnpackNormalOctQuadEncode(remappedOctNormalWS * 2.0 - 1.0);
#else
    float3 rawNormalWS = normalBufferData.rgb;
    float lenSq = dot(rawNormalWS, rawNormalWS);
    // A zero normal means the prepass did not cover the pixel; fall back to a camera facing normal
    // instead of propagating NaNs through the horizon integration.
    float3 normalWS = lenSq > 1e-8 ? rawNormalWS * rsqrt(lenSq) : float3(0.0, 0.0, 1.0);
#endif
    // HDRP oriented its view space with +z pointing away from the camera; URP keeps the Unity view
    // matrix, so the z component is flipped to stay in the same convention as GTAOGetPositionVS.
    float3 normalVS = GTAOTransformWorldToView(normalWS);
    return float3(normalVS.xy, -normalVS.z);
}

float GTAOFastAcos(float x)
{
    float outVal = -0.156583 * abs(x) + HALF_PI;
    outVal *= sqrt(1.0 - abs(x));
    return x >= 0 ? outVal : PI - outVal;
}

// --------------------------------------------
// Output functions
// --------------------------------------------
float GTAOPackAOOutput(float AO, float depth)
{
    uint packedDepth = PackFloatToUInt(depth, 0, 23);
    uint packedAO = PackFloatToUInt(AO, 24, 8);
    uint packedVal = packedAO | packedDepth;
    // A NaN would not keep its bit pattern through the sampler; invalidate the depth instead so the
    // bilateral passes skip the sample.
    if ((packedVal & 0x7FFFFFFF) > 0x7F800000)
        packedVal = packedAO;

    // Gather4 on integer textures is not portable, so the packed value travels as a float.
    return asfloat(packedVal);
}

void GTAOUnpackData(float data, out float AO, out float depth)
{
    depth = UnpackUIntToFloat(asuint(data), 0, 23);
    AO = UnpackUIntToFloat(asuint(data), 24, 8);
}

void GTAOUnpackGatheredData(float4 data, out float4 AOs, out float4 depths)
{
    GTAOUnpackData(data.x, AOs.x, depths.x);
    GTAOUnpackData(data.y, AOs.y, depths.y);
    GTAOUnpackData(data.z, AOs.z, depths.z);
    GTAOUnpackData(data.w, AOs.w, depths.w);
}

// URP consumers expect a visibility factor: 1 is unoccluded (white neutral), 0 is fully occluded.
// HDRP returned the inverse (1 - integration); the conversion happens here so the URP AO contract
// stays unchanged.
float GTAOOutputFinalAO(float AO)
{
    return saturate(PositivePow(saturate(AO), _GtaoIntensity));
}

#endif