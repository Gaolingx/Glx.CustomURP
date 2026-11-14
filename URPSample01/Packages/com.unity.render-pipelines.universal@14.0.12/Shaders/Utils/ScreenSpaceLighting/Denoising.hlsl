#ifndef UNIVERSAL_SCREEN_SPACE_LIGHTING_DENOISING_INCLUDED
#define UNIVERSAL_SCREEN_SPACE_LIGHTING_DENOISING_INCLUDED

// Denoising helpers ported from HDRP 14.0.12
// Runtime/RenderPipeline/Raytracing/Shaders/Denoising/DenoisingUtils.hlsl and BilateralFilter.hlsl.
// The HDRP stencil "unlit" test is not available on every URP path, so background pixels are
// rejected with the far-plane depth value and a zero normal instead.

#include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceLightingCommon.hlsl"

#define SSGI_MAX_REPROJECTION_DISTANCE 0.1
#define SSGI_MAX_PIXEL_TOLERANCE 4
#define SSGI_PROJECTION_EPSILON 0.000001

float SsgiComputeMaxReprojectionWorldRadius(float3 positionWS, float3 normalWS, float pixelSpreadAngleTangent, float maxDistance, float pixelTolerance)
{
    const float3 viewWS = normalize(_SsgiCameraPositionWS.xyz - positionWS);
    float parallelPixelFootPrint = pixelSpreadAngleTangent * length(positionWS - _SsgiCameraPositionWS.xyz);
    float realPixelFootPrint = parallelPixelFootPrint / max(abs(dot(normalWS, viewWS)), SSGI_PROJECTION_EPSILON);
    return max(maxDistance, realPixelFootPrint * pixelTolerance);
}

float SsgiComputeMaxReprojectionWorldRadius(float3 positionWS, float3 normalWS, float pixelSpreadAngleTangent)
{
    return SsgiComputeMaxReprojectionWorldRadius(positionWS, normalWS, pixelSpreadAngleTangent, SSGI_MAX_REPROJECTION_DISTANCE, SSGI_MAX_PIXEL_TOLERANCE);
}

float SsgiGaussian(float radius, float sigma)
{
    float ratio = radius / sigma;
    return exp(-(ratio * ratio));
}

struct SsgiBilateralData
{
    float3 position;
    float z01;
    float3 normal;
};

SsgiBilateralData SsgiTapBilateralData(
    SSGI_TEXTURE_TYPE_FLOAT depthTexture,
    SSGI_TEXTURE_TYPE_FLOAT4 normalTexture,
    uint2 coordSS)
{
    SsgiBilateralData key;
    float deviceDepth = SSGI_LOAD(depthTexture, coordSS);
    key.z01 = Linear01Depth(deviceDepth, _SsgiZBufferParams);
    key.position = SsgiReconstructPositionWS((float2)coordSS, deviceDepth);
    key.normal = SsgiDecodeNormalSS(SSGI_LOAD(normalTexture, coordSS));
    return key;
}

float SsgiComputeBilateralWeight(SsgiBilateralData center, SsgiBilateralData tap)
{
    float depthWeight = max(0.0, 1.0 - abs(tap.z01 - center.z01));

    float normalCloseness = max(0.0, dot(tap.normal, center.normal));
    normalCloseness *= normalCloseness;
    normalCloseness *= normalCloseness;
    float normalWeight = max(0.0, 1.0 - (1.0 - normalCloseness));

    float3 dq = center.position - tap.position;
    float distance2 = dot(dq, dq);
    float planeError = max(abs(dot(dq, tap.normal)), abs(dot(dq, center.normal)));
    float planeWeight = (distance2 < 0.0001) ? 1.0
        : pow(max(0.0, 1.0 - 2.0 * planeError / sqrt(distance2)), 2.0);

    return depthWeight * normalWeight * planeWeight;
}

#endif