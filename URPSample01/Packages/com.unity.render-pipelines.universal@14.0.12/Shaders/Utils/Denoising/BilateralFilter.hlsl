#ifndef UNIVERSAL_DENOISING_BILATERAL_FILTER_INCLUDED
#define UNIVERSAL_DENOISING_BILATERAL_FILTER_INCLUDED

// Bilateral tap data and weights shared by the temporal filter and the diffuse denoiser.
// Ported from HDRP 14.0.12 Runtime/RenderPipeline/Raytracing/Shaders/Denoising/BilateralFilter.hlsl.
// HDRP flags pixels as unlit through the stencil; URP has no common stencil bit, so the callers
// reject background pixels with the far-plane depth and a zero normal instead.

#include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/Denoising/DenoisingUtils.hlsl"

struct SsgiBilateralData
{
    float3 position;
    float z01;   // Linear 0..1 depth
    float zNF;   // Linear eye depth (matches the clip w of a projected tap)
    float3 normal;
    // The depth-normals prepass stores 0.5 + 0.5 * smoothness in alpha, the same encoding the SSR
    // trace decodes. SSGI ignores this field; the reflection denoiser uses it for the roughness
    // based filter radius and the min-smoothness tap rejection.
    float roughness;
};

SsgiBilateralData SsgiTapBilateralData(
    SSGI_TEXTURE_TYPE_FLOAT depthTexture,
    SSGI_TEXTURE_TYPE_FLOAT4 normalTexture,
    uint2 coordSS)
{
    SsgiBilateralData key;
    float deviceDepth = SSGI_LOAD(depthTexture, coordSS);
    float4 normalSample = SSGI_LOAD(normalTexture, coordSS);
    key.z01 = Linear01Depth(deviceDepth, _SsgiZBufferParams);
    key.zNF = LinearEyeDepth(deviceDepth, _SsgiZBufferParams);
    key.position = SsgiReconstructPositionWS((float2)coordSS, deviceDepth);
    key.normal = SsgiDecodeNormalSS(normalSample);
    key.roughness = 1.0 - saturate(normalSample.a * 2.0 - 1.0);
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