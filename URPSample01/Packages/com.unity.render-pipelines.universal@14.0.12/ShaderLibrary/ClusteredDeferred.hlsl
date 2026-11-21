#ifndef UNIVERSAL_CLUSTERED_DEFERRED_INCLUDED
#define UNIVERSAL_CLUSTERED_DEFERRED_INCLUDED

// Compute-safe subset for the clustered deferred lighting kernel.
//
// Mirrors the fragment path for the pieces the kernel needs:
// - Clustering.hlsl header 0 (punctual light list) with the camera basis uploaded explicitly;
// - Input.hlsl light constants (constant arrays; USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA == 0);
// - UnityGBuffer.hlsl decode plus Lighting.hlsl direct lighting (Lit and SimpleLit);
// while avoiding Input.hlsl / Lighting.hlsl / Core.hlsl / Shadows.hlsl, which pull per-draw state.
//
// BRDF.hlsl is safe to include: it only pulls the core BSDF/CommonMaterial headers and
// SurfaceData.hlsl. Light/InputData must stay local (CdLight/CdInputData) because their URP
// definitions live in RealtimeLights.hlsl / Input.hlsl.

#include "Packages/com.unity.render-pipelines.universal-config/Runtime/ShaderConfig.cs.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonLighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/BRDF.hlsl"

// Must match Input.hlsl / UniversalRenderPipeline.maxVisibleAdditionalLights.
#if defined(SHADER_API_MOBILE) && (defined(SHADER_API_GLES) || defined(SHADER_API_GLES30))
    #define CD_MAX_VISIBLE_LIGHTS MAX_VISIBLE_LIGHT_COUNT_LOW_END_MOBILE
#elif defined(SHADER_API_MOBILE) || (defined(SHADER_API_GLCORE) && !defined(SHADER_API_SWITCH)) || defined(SHADER_API_GLES) || defined(SHADER_API_GLES3)
    #define CD_MAX_VISIBLE_LIGHTS MAX_VISIBLE_LIGHT_COUNT_MOBILE
#else
    #define CD_MAX_VISIBLE_LIGHTS MAX_VISIBLE_LIGHT_COUNT_DESKTOP
#endif

#define CD_MAX_ZBIN_VEC4S 1024
#if CD_MAX_VISIBLE_LIGHTS <= 32
    #define CD_MAX_TILE_VEC4S 1024
#else
    #define CD_MAX_TILE_VEC4S 4096
#endif

// ----------------------------------------------------------------------------
// Globals uploaded by ClusteredDeferredPass (compute has no per-draw state).
// ----------------------------------------------------------------------------

TEXTURE2D(_CameraDepthTexture);
TEXTURE2D(_GBuffer0);
TEXTURE2D(_GBuffer1);
TEXTURE2D(_GBuffer2);
// URP has no generic UINT texture macro; SSGI declares Texture2D<uint> the same way.
Texture2D<uint> _MaterialTypeMask; // R8_UInt: 0 = skip, 1 = Lit, 2 = SimpleLit

float4 _ScreenSize;          // (width, height, 1/width, 1/height)
float4 _CdCameraPositionWS;  // xyz = camera position
float4 _CdViewForward;       // xyz = normalized camera forward
float4 _CdCameraParams;      // x = 1 perspective, 0 orthographic

// Unity's per-camera globals are not declared by the core headers inside a compute include;
// the pipeline still uploads them (same pattern as ScreenSpaceLightingCommon.hlsl:66-67).
float4x4 unity_MatrixInvVP;

// ----------------------------------------------------------------------------
// Cluster buffers (same layout/binding names as Input.hlsl).
// ----------------------------------------------------------------------------

float4 _FPParams0;
float4 _FPParams1;
float4 _FPParams2;

#define URP_FP_ZBIN_SCALE (_FPParams0.x)
#define URP_FP_ZBIN_OFFSET (_FPParams0.y)
#define URP_FP_PROBES_BEGIN ((uint)_FPParams0.z)
#define URP_FP_DIRECTIONAL_LIGHTS_COUNT ((uint)_FPParams0.w)
#define URP_FP_TILE_SCALE ((float2)_FPParams1.xy)
#define URP_FP_TILE_COUNT_X ((uint)_FPParams1.z)
#define URP_FP_WORDS_PER_TILE ((uint)_FPParams1.w)
#define URP_FP_ZBIN_COUNT ((uint)_FPParams2.x)
#define URP_FP_TILE_COUNT ((uint)_FPParams2.y)

CBUFFER_START(URP_ZBinBuffer)
    float4 URP_ZBins[CD_MAX_ZBIN_VEC4S];
CBUFFER_END
CBUFFER_START(urp_TileBuffer)
    float4 urp_Tiles[CD_MAX_TILE_VEC4S];
CBUFFER_END

// ----------------------------------------------------------------------------
// Light constants. Same cbuffer name and array layout as Input.hlsl so the
// global SetGlobalVectorArray uploads land here in a compute dispatch too.
// ----------------------------------------------------------------------------

CBUFFER_START(AdditionalLights)
    float4 _AdditionalLightsPosition[CD_MAX_VISIBLE_LIGHTS];
    half4 _AdditionalLightsColor[CD_MAX_VISIBLE_LIGHTS];      // .a: subtractive mixed flag
    half4 _AdditionalLightsAttenuation[CD_MAX_VISIBLE_LIGHTS];
    half4 _AdditionalLightsSpotDir[CD_MAX_VISIBLE_LIGHTS];
    half4 _AdditionalLightsOcclusionProbes[CD_MAX_VISIBLE_LIGHTS];
    float _AdditionalLightsLayerMasks[CD_MAX_VISIBLE_LIGHTS];
CBUFFER_END

float4 _MainLightPosition;
half4 _MainLightColor;
uint _MainLightLayerMask;

// ----------------------------------------------------------------------------
// Cluster iterator (Clustering.hlsl, header 0). General path only: the desktop
// light cap keeps MAX_LIGHTS_PER_TILE > 32, which is the branch that packs the
// next word index and the max index into the zbin header.
// ----------------------------------------------------------------------------

uint CdSelect4(uint4 v, uint i)
{
    uint mask0 = uint(int(i << 31) >> 31);
    uint mask1 = uint(int(i << 30) >> 31);
    return (((v.w & mask0) | (v.z & ~mask0)) & mask1) |
           (((v.y & mask0) | (v.x & ~mask0)) & ~mask1);
}

struct CdClusterIterator
{
    uint tileOffset;
    uint zBinOffset;
    uint tileMask;
    uint entityIndexNextMax;
};

CdClusterIterator CdClusterInit(float2 normalizedScreenSpaceUV, float3 positionWS)
{
    CdClusterIterator state = (CdClusterIterator)0;

    uint2 tileId = uint2(normalizedScreenSpaceUV * URP_FP_TILE_SCALE);
    state.tileOffset = (tileId.y * URP_FP_TILE_COUNT_X + tileId.x) * URP_FP_WORDS_PER_TILE;

    float3 cameraPositionWS = _CdCameraPositionWS.xyz;
    float3 viewForward = normalize(_CdViewForward.xyz);
    float viewZ = dot(viewForward, positionWS - cameraPositionWS);
    float depthTerm = _CdCameraParams.x > 0.5 ? log2(max(viewZ, 1e-6)) : viewZ;

    uint zBinBaseIndex = (uint)(depthTerm * URP_FP_ZBIN_SCALE + URP_FP_ZBIN_OFFSET);
    zBinBaseIndex = zBinBaseIndex * (2 + URP_FP_WORDS_PER_TILE);
    zBinBaseIndex = min(zBinBaseIndex, 4 * CD_MAX_ZBIN_VEC4S - (2 + URP_FP_WORDS_PER_TILE));

    uint zBinHeaderIndex = zBinBaseIndex; // header 0 = punctual light list
    state.zBinOffset = zBinBaseIndex + 2;
    state.entityIndexNextMax = CdSelect4(asuint(URP_ZBins[zBinHeaderIndex / 4]), zBinHeaderIndex % 4);
    return state;
}

bool CdClusterNext(inout CdClusterIterator it, out uint entityIndex)
{
    uint maxIndex = it.entityIndexNextMax >> 16;
    while (it.tileMask == 0 && (it.entityIndexNextMax & 0xFFFF) <= maxIndex)
    {
        uint wordIndex = ((it.entityIndexNextMax & 0xFFFF) >> 5);
        uint tileIndex = it.tileOffset + wordIndex;
        uint zBinIndex = it.zBinOffset + wordIndex;
        it.tileMask =
            CdSelect4(asuint(urp_Tiles[tileIndex / 4]), tileIndex % 4) &
            CdSelect4(asuint(URP_ZBins[zBinIndex / 4]), zBinIndex % 4) &
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

// ----------------------------------------------------------------------------
// GBuffer decode (UnityGBuffer.hlsl / BRDF.hlsl), trimmed to what the kernel uses.
// ----------------------------------------------------------------------------

#define CD_FLAG_RECEIVE_SHADOWS_OFF        1
#define CD_FLAG_SPECULAR_HIGHLIGHTS_OFF    2
#define CD_FLAG_SUBTRACTIVE_MIXED_LIGHTING 4
#define CD_FLAG_SPECULAR_SETUP             8

uint CdUnpackMaterialFlags(float packedMaterialFlags)
{
    return uint((packedMaterialFlags * 255.0h) + 0.5h);
}

half3 CdUnpackNormal(half3 packedNormalWS)
{
#ifdef _GBUFFER_NORMALS_OCT
    half2 remappedOctNormalWS = half2(Unpack888ToFloat2(packedNormalWS));
    half2 octNormalWS = remappedOctNormalWS * half(2.0) - half(1.0);
    return half3(UnpackNormalOctQuadEncode(octNormalWS));
#else
    return packedNormalWS;
#endif
}

BRDFData CdBRDFDataFromGbuffer(half4 gbuffer0, half4 gbuffer1, half4 gbuffer2)
{
    half3 albedo = gbuffer0.rgb;
    half3 specular = gbuffer1.rgb;
    uint materialFlags = CdUnpackMaterialFlags(gbuffer0.a);
    half smoothness = gbuffer2.a;

    BRDFData brdfData = (BRDFData)0;
    half alpha = half(1.0);

    half3 brdfDiffuse;
    half3 brdfSpecular;
    half reflectivity;
    half oneMinusReflectivity;

    if ((materialFlags & CD_FLAG_SPECULAR_SETUP) != 0)
    {
        reflectivity = ReflectivitySpecular(specular);
        oneMinusReflectivity = half(1.0) - reflectivity;
        brdfDiffuse = albedo * oneMinusReflectivity;
        brdfSpecular = specular;
    }
    else
    {
        reflectivity = specular.r;
        oneMinusReflectivity = 1.0 - reflectivity;
        half metallic = MetallicFromReflectivity(reflectivity);
        brdfDiffuse = albedo * oneMinusReflectivity;
        brdfSpecular = lerp(kDielectricSpec.rgb, albedo, metallic);
    }

    InitializeBRDFDataDirect(albedo, brdfDiffuse, brdfSpecular, reflectivity, oneMinusReflectivity, smoothness, alpha, brdfData);
    return brdfData;
}

struct CdInputData
{
    float3 positionWS;
    float3 normalWS;
    half3 viewDirectionWS;
    float2 normalizedScreenSpaceUV;
};

CdInputData CdInputDataFromGbufferAndWorldPosition(half4 gbuffer2, float3 positionWS, float2 normalizedScreenSpaceUV)
{
    CdInputData inputData;
    inputData.positionWS = positionWS;
    inputData.normalWS = normalize(CdUnpackNormal(gbuffer2.xyz));
    // Compute has no per-draw view constants; the pass uploads the camera position.
    inputData.viewDirectionWS = half3(normalize(_CdCameraPositionWS.xyz - positionWS));
    inputData.normalizedScreenSpaceUV = normalizedScreenSpaceUV;
    return inputData;
}

// ----------------------------------------------------------------------------
// Light construction and direct lighting.
// ----------------------------------------------------------------------------

struct CdLight
{
    half3 direction;
    half3 color;
    float distanceAttenuation;
    half shadowAttenuation;
    uint layerMask;
};

float CdDistanceAttenuation(float distanceSqr, half2 distanceAttenuation)
{
    float lightAtten = rcp(distanceSqr);
    float2 distanceAttenuationFloat = float2(distanceAttenuation);
    half factor = half(distanceSqr * distanceAttenuationFloat.x);
    half smoothFactor = saturate(half(1.0) - factor * factor);
    smoothFactor = smoothFactor * smoothFactor;
    return lightAtten * smoothFactor;
}

half CdAngleAttenuation(half3 spotDirection, half3 lightDirection, half2 spotAttenuation)
{
    half SdotL = dot(spotDirection, lightDirection);
    half atten = saturate(SdotL * spotAttenuation.x + spotAttenuation.y);
    return atten * atten;
}

CdLight CdGetAdditionalLight(uint lightIndex, float3 positionWS)
{
    float4 lightPositionWS = _AdditionalLightsPosition[lightIndex];
    half3 color = _AdditionalLightsColor[lightIndex].rgb;
    half4 distanceAndSpotAttenuation = _AdditionalLightsAttenuation[lightIndex];
    half4 spotDirection = _AdditionalLightsSpotDir[lightIndex];
    uint lightLayerMask = asuint(_AdditionalLightsLayerMasks[lightIndex]);

    // Directional lights store the direction in .xyz with .w == 0.
    float3 lightVector = lightPositionWS.xyz - positionWS * lightPositionWS.w;
    float distanceSqr = max(dot(lightVector, lightVector), HALF_MIN);
    half3 lightDirection = half3(lightVector * rsqrt(distanceSqr));

    float attenuation = CdDistanceAttenuation(distanceSqr, distanceAndSpotAttenuation.xy) *
                        CdAngleAttenuation(spotDirection.xyz, lightDirection, distanceAndSpotAttenuation.zw);

    CdLight light;
    light.direction = lightDirection;
    light.distanceAttenuation = attenuation;
    light.shadowAttenuation = 1.0;
    light.color = color;
    light.layerMask = lightLayerMask;
    return light;
}

CdLight CdMainLight()
{
    CdLight light;
    light.direction = half3(_MainLightPosition.xyz);
    light.distanceAttenuation = 1.0;
    light.shadowAttenuation = 1.0;
    light.color = _MainLightColor.rgb;
    light.layerMask = _MainLightLayerMask;
    return light;
}

half3 CdLightingLambert(half3 lightColor, half3 lightDir, half3 normalWS)
{
    half NdotL = saturate(dot(normalWS, lightDir));
    return lightColor * NdotL;
}

half3 CdLightingSpecular(half3 lightColor, half3 lightDir, half3 normalWS, half3 viewDirWS, half4 specular, half smoothness)
{
    float3 halfVec = SafeNormalize(float3(lightDir) + float3(viewDirWS));
    half NdotH = half(saturate(dot(normalWS, halfVec)));
    half modifier = pow(NdotH, smoothness);
    float3 specularReflection = specular.rgb * modifier;
    return lightColor * specularReflection;
}

half3 CdLightingPhysicallyBased(BRDFData brdfData, CdLight light, half3 normalWS, half3 viewDirWS, bool specularHighlightsOff)
{
    half NdotL = saturate(dot(normalWS, light.direction));
    half3 radiance = light.color * (light.distanceAttenuation * light.shadowAttenuation * NdotL);

    half3 brdf = brdfData.diffuse;
    [branch] if (!specularHighlightsOff)
    {
        brdf += brdfData.specular * DirectBRDFSpecular(brdfData, normalWS, light.direction, viewDirWS);
    }

    return brdf * radiance;
}

half3 CdShade(uint materialType, half4 gbuffer0, half4 gbuffer1, half4 gbuffer2, CdLight light, CdInputData inputData)
{
    if (materialType == 2u)
    {
        // SimpleLit: same terms as StencilDeferred.hlsl:309-318.
        half3 albedo = gbuffer0.rgb;
        half3 specular = gbuffer1.rgb;
        half smoothness = gbuffer2.a;

        half3 attenuatedLightColor = light.color * (light.distanceAttenuation * light.shadowAttenuation);
        half3 diffuseColor = CdLightingLambert(attenuatedLightColor, light.direction, inputData.normalWS);
        half smoothnessExp = exp2(10 * smoothness + 1);
        half3 specularColor = CdLightingSpecular(attenuatedLightColor, light.direction, inputData.normalWS,
            inputData.viewDirectionWS, half4(specular, 1), smoothnessExp);
        return diffuseColor * albedo + specularColor;
    }

    BRDFData brdfData = CdBRDFDataFromGbuffer(gbuffer0, gbuffer1, gbuffer2);
    uint materialFlags = CdUnpackMaterialFlags(gbuffer0.a);
    bool specularHighlightsOff = (materialFlags & CD_FLAG_SPECULAR_HIGHLIGHTS_OFF) != 0;
    return CdLightingPhysicallyBased(brdfData, light, inputData.normalWS, inputData.viewDirectionWS, specularHighlightsOff);
}

#endif // UNIVERSAL_CLUSTERED_DEFERRED_INCLUDED