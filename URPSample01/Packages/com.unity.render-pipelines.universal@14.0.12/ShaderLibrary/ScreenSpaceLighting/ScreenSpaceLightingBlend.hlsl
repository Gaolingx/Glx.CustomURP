#ifndef UNIVERSAL_SCREEN_SPACE_LIGHTING_BLEND_INCLUDED
#define UNIVERSAL_SCREEN_SPACE_LIGHTING_BLEND_INCLUDED

// Material-side mixing of the Screen Space Lighting plugin output. Core owns the blend contract; the
// plugin only provides a valid texture and a weight.
//
// SSGI replaces the baked diffuse term with the blend weight. A hit carries the radiance of the hit
// point; a miss carries the fallback the volume selected (reflection probes / sky ambient, weight 1,
// exactly like HDRP replacing bakeDiffuseLighting) or weight 0 for rayMiss = None, which keeps the
// baked GI. The radiance is already one-bounce incoming light, so it is blended before
// EnvironmentBRDF, exactly like bakedGI; emission is never part of this term. intensity multiplies the
// radiance (not the weight, so values above 1 brighten the bounce) and zero is an exact no-op.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingInput.hlsl"

half3 ScreenSpaceLightingApplyIndirectDiffuse(half3 bakedGI, float2 normalizedScreenSpaceUV)
{
#if defined(_SCREEN_SPACE_GI)
    half intensity = max(_ScreenSpaceGiParams.x, 0.0h);
    if (intensity <= 0.0h)
        return bakedGI;

    half4 ssgi = SAMPLE_TEXTURE2D_X(_ScreenSpaceGiTexture, sampler_LinearClamp, UnityStereoTransformScreenSpaceTex(normalizedScreenSpaceUV));
    half3 radiance = max(ssgi.rgb, half3(0.0, 0.0, 0.0)) * intensity;
    return lerp(bakedGI, radiance, saturate(ssgi.a));
#else
    return bakedGI;
#endif
}

// Opaque screen-space reflections replace the environment specular where the trace produced a valid
// reflection. The plugin stores un-premultiplied radiance in rgb and the blend weight in a, so a miss
// (a = 0) keeps the reflection probe / sky exactly as the material computed it. The lerp happens
// before EnvironmentBRDF, therefore the BRDF and FGD terms are applied to the combined result only
// once, and the probe contribution keeps its (1 - weight) share.
// The result is point sampled: SSR is evaluated per full resolution pixel (HDRP uses a LOAD), and a
// bilinear tap would mix covered pixels with zero-weight miss pixels at the mask boundary.
half3 ScreenSpaceLightingEvaluateIndirectSpecular(half3 environmentSpecular, half4 ssrSample, half ssrIntensity)
{
    half weight = saturate(ssrSample.a * max(ssrIntensity, 0.0h));
    half3 radiance = max(ssrSample.rgb, half3(0.0, 0.0, 0.0));
    return lerp(environmentSpecular, radiance, weight);
}

// Transparent materials use a separate result and keyword. They are drawn after the opaque SSR
// keyword has been disabled, so a transparent surface never consumes the opaque trace (which was
// started from opaque depth) and an opaque material never consumes the transparent trace.
half3 ScreenSpaceLightingApplyIndirectSpecular(half3 environmentSpecular, float2 normalizedScreenSpaceUV)
{
#if defined(_SCREEN_SPACE_SSR)
    half4 ssr = SAMPLE_TEXTURE2D_X(_ScreenSpaceSsrTexture, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(normalizedScreenSpaceUV));
    return ScreenSpaceLightingEvaluateIndirectSpecular(environmentSpecular, ssr, _ScreenSpaceSsrParams.x);
#elif defined(_TRANSPARENT_SSR_RECEIVE) && defined(_SCREEN_SPACE_TRANSPARENT_SSR)
    half4 ssr = SAMPLE_TEXTURE2D_X(_ScreenSpaceTransparentSsrTexture, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(normalizedScreenSpaceUV));
    return ScreenSpaceLightingEvaluateIndirectSpecular(environmentSpecular, ssr, _ScreenSpaceTransparentSsrParams.x);
#else
    return environmentSpecular;
#endif
}

// Clear coat keeps a separate reflection hierarchy for the base and the coat lobe. The trace is
// shared (URP has a single opaque trace), but each lobe mixes it against its own environment
// reflection with its own weight:
// - the coat lobe consumes the trace with the SSR weight and its own mask / coat smoothness;
// - the base lobe only consumes the trace while it is not much rougher than the coat, mirroring
//   HDRP's "coat-traced light on the bottom layer" rule. A rough base keeps its probe.
// A miss (weight 0) leaves both lobes on their probes, so no probe or BRDF term is applied twice.
// The coat mask is never derived from the base smoothness; it is the independent material value.
void ScreenSpaceLightingEvaluateIndirectSpecularClearCoat(
    half4 ssrSample,
    half ssrIntensity,
    half3 environmentSpecular,
    half3 coatEnvironmentSpecular,
    half basePerceptualRoughness,
    half coatMask,
    out half3 baseSpecular,
    out half3 coatSpecular)
{
    half weight = saturate(ssrSample.a * max(ssrIntensity, 0.0h));
    half3 radiance = max(ssrSample.rgb, half3(0.0, 0.0, 0.0));

    if (coatMask > 0.0h)
    {
        half baseToCoatRoughness = saturate((basePerceptualRoughness - CLEAR_COAT_PERCEPTUAL_ROUGHNESS) / 0.2h);
        half baseHierarchyWeight = weight * lerp(1.0h, 0.0h, baseToCoatRoughness);
        baseSpecular = lerp(environmentSpecular, radiance, baseHierarchyWeight);
        coatSpecular = lerp(coatEnvironmentSpecular, radiance, weight);
    }
    else
    {
        baseSpecular = lerp(environmentSpecular, radiance, weight);
        coatSpecular = coatEnvironmentSpecular;
    }
}

void ScreenSpaceLightingApplyIndirectSpecularClearCoat(
    half3 environmentSpecular,
    half3 coatEnvironmentSpecular,
    half basePerceptualRoughness,
    half coatMask,
    float2 normalizedScreenSpaceUV,
    out half3 baseSpecular,
    out half3 coatSpecular)
{
#if defined(_SCREEN_SPACE_SSR)
    half4 ssr = SAMPLE_TEXTURE2D_X(_ScreenSpaceSsrTexture, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(normalizedScreenSpaceUV));
    ScreenSpaceLightingEvaluateIndirectSpecularClearCoat(ssr, _ScreenSpaceSsrParams.x,
        environmentSpecular, coatEnvironmentSpecular, basePerceptualRoughness, coatMask,
        baseSpecular, coatSpecular);
#elif defined(_TRANSPARENT_SSR_RECEIVE) && defined(_SCREEN_SPACE_TRANSPARENT_SSR)
    half4 ssr = SAMPLE_TEXTURE2D_X(_ScreenSpaceTransparentSsrTexture, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(normalizedScreenSpaceUV));
    ScreenSpaceLightingEvaluateIndirectSpecularClearCoat(ssr, _ScreenSpaceTransparentSsrParams.x,
        environmentSpecular, coatEnvironmentSpecular, basePerceptualRoughness, coatMask,
        baseSpecular, coatSpecular);
#else
    baseSpecular = environmentSpecular;
    coatSpecular = coatEnvironmentSpecular;
#endif
}

#endif
