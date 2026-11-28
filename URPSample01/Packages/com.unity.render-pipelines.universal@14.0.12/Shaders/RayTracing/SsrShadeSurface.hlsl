#ifndef GLX_SSR_SHADE_SURFACE_INCLUDED
#define GLX_SSR_SHADE_SURFACE_INCLUDED

// P3 (D3): the single-point hit-shading formula shared by the compute hit kernels.
//
// The RT hit kernel (SsrHitDeferred.compute) and the screen-space hit kernel (SsrHitSs, B3) both
// decode a SsrHitSurface and must light it identically, so the reflected surface looks the same on
// either side of the SS/RT seam. This is the ClusteredDeferred BRDF (CdShade) with the single-tap
// shadow and the explicit ambient low-frequency term.
//
// The material closest-hit (GlxLitClosestHit) is NOT routed through this header: it runs with the
// full per-draw material headers and cannot include the compute-safe ClusteredDeferred kernel. The
// two hit kernels below own all SSR hit shading; closest-hit remains the probe-GI path.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ClusteredDeferred.hlsl"
#include "SsrHitSurface.hlsl"
#include "GlxProbeSampling.hlsl"

#define SSR_HIT_LIGHT_CAP 64u

struct SsrShadedSample
{
    float3 radiance;
    float weight;
};

// Lights the reflection-hit surface seen along viewDirWS. additionalLightCount is the transition
// cap (P1 still visits every additional light up to SSR_HIT_LIGHT_CAP; P2 replaces it with a
// world-space cluster). The result is non-premultiplied radiance; weight is left at 1 and the
// callers apply coverage downstream.
SsrShadedSample SsrShadeSurface(SsrHitSurface s, float3 positionWS, float3 viewDirWS, uint additionalLightCount)
{
    float3 normalWS = normalize(s.gb1.xyz);
    half smoothness = half(1.0 - saturate(s.gb1.w));

    // Synthetic GBuffer, same encoding ClusteredDeferred decodes. gbuffer1 carries the reflectivity
    // the material published; the packed flag word stays 0 (receive shadows, specular on).
    uint materialType = 1u;
    half4 gbuffer0 = half4(s.gb0.rgb, 0.0);
    half4 gbuffer1 = half4(s.gb2.rgb, 0.0);
    half4 gbuffer2 = half4(normalWS, smoothness);

    CdInputData inputData;
    inputData.positionWS = positionWS;
    inputData.normalWS = normalWS;
    // The hit surface is seen along the reversed reflection ray, not from the camera.
    inputData.viewDirectionWS = half3(viewDirWS);
    inputData.normalizedScreenSpaceUV = 0.0;

    half4 shadowMask = half4(1.0, 1.0, 1.0, 1.0);
    half3 color = 0.0;

    CdLight mainLight = CdMainLight(positionWS);
    mainLight.shadowAttenuation = CdMainLightShadow(
        CdTransformWorldToShadowCoord(positionWS, float2(0.0, 0.0)), positionWS, shadowMask, half4(1.0, 1.0, 1.0, 1.0));
    color += CdShade(materialType, gbuffer0, gbuffer1, gbuffer2, mainLight, inputData);

    uint lightCount = min(additionalLightCount, SSR_HIT_LIGHT_CAP);
    [loop] for (uint i = 0; i < lightCount; ++i)
    {
        CdLight light = CdGetAdditionalLight(i, positionWS);
        light.shadowAttenuation = CdAdditionalLightShadow(i, positionWS, light.direction, shadowMask, half4(1.0, 1.0, 1.0, 1.0));
        color += CdShade(materialType, gbuffer0, gbuffer1, gbuffer2, light, inputData);
    }

    // Ambient: the probe low-frequency irradiance shared with the probe-GI path. The specular
    // share reuses the same irradiance until P2 brings the probe/sky hierarchy.
    BRDFData brdf = CdBRDFDataFromGbuffer(gbuffer0, gbuffer1, gbuffer2);
    half3 ambientRadiance = half3(GlxHitLowFrequencyIrradiance(positionWS, normalWS)) * INV_PI;
    color += brdf.diffuse * ambientRadiance + brdf.specular * ambientRadiance + s.gb3.rgb;

    SsrShadedSample sample;
    sample.radiance = max(color, 0.0);
    sample.weight = 1.0;
    return sample;
}

#endif
