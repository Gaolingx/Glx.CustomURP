#ifndef UNIVERSAL_DENOISING_UTILS_INCLUDED
#define UNIVERSAL_DENOISING_UTILS_INCLUDED

// Denoising helpers shared by the Screen Space Lighting temporal filter and diffuse denoiser.
// Ported from HDRP 14.0.12 Runtime/RenderPipeline/Raytracing/Shaders/Denoising/DenoisingUtils.hlsl.
// The HDRP stencil "unlit" test is not available on every URP path, so background pixels are
// rejected with the far-plane depth value and a zero normal instead.

#include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceLightingCommon.hlsl"

#define SSGI_MAX_REPROJECTION_DISTANCE 0.1
#define SSGI_MAX_PIXEL_TOLERANCE 4
#define SSGI_PROJECTION_EPSILON 0.000001
// HDRP MAX_NORMAL_DIFFERENCE: history is rejected below this dot product.
#define SSGI_MAX_NORMAL_DIFFERENCE 0.65
// HDRP denoiser tap rejection: the tap must lie on the same surface as the projected sample point.
#define SSGI_TAP_DEPTH_TOLERANCE 0.1
// HDRP MINIMAL_MOTION_DISTANCE for the occluder motion rejection.
#define SSGI_MINIMAL_MOTION_DISTANCE 0.0001

float SsgiComputeMaxReprojectionWorldRadius(float3 positionWS, float3 normalWS, float pixelSpreadAngleTangent, float maxDistance, float pixelTolerance)
{
    const float3 viewWS = normalize(SSL_SSGI_CAMERA_POSITION_WS - positionWS);
    float parallelPixelFootPrint = pixelSpreadAngleTangent * length(positionWS - SSL_SSGI_CAMERA_POSITION_WS);
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

// HDRP BilUpColor3_Uniform extended to the RGBA signal: rgb keeps HDRP's depth weighted blend (with
// the same noise floor), alpha is filtered with the same weights so the validity weight is preserved.
float4 SsgiBilinearUpSampleUniform(float hiDepth, float4 lowDepths, float4 low0, float4 low1, float4 low2, float4 low3)
{
    const float upsampleTolerance = 1e-5;
    const float noiseFilterStrength = 0.99999999;

    float4 weights = float4(3.0, 3.0, 3.0, 3.0) / (abs(hiDepth - lowDepths) + upsampleTolerance);
    float totalWeight = dot(weights, float4(1.0, 1.0, 1.0, 1.0));
    if (totalWeight <= 0.0)
        return low0;

    float4 weightedSum = low0 * weights.x + low1 * weights.y + low2 * weights.z + low3 * weights.w;

    float4 result;
    result.rgb = (weightedSum.rgb + noiseFilterStrength) / (totalWeight + noiseFilterStrength);
    result.a = weightedSum.a / totalWeight;
    return result;
}

#endif