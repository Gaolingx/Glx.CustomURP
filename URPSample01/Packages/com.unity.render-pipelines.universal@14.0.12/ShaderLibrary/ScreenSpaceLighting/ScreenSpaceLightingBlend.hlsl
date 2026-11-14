#ifndef UNIVERSAL_SCREEN_SPACE_LIGHTING_BLEND_INCLUDED
#define UNIVERSAL_SCREEN_SPACE_LIGHTING_BLEND_INCLUDED

// Material-side mixing of the Screen Space Lighting plugin output. Core owns the blend contract; the
// plugin only provides a valid texture and a weight.
//
// SSGI replaces the baked diffuse term only where the ray produced a valid hit. A miss (weight 0)
// keeps bakedGI, which is what keeps lightmaps, SH and APV on their original path and prevents a
// miss from darkening the image. The radiance is already one-bounce incoming light, so it is blended
// before EnvironmentBRDF, exactly like bakedGI; emission is never part of this term.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingInput.hlsl"

half3 ScreenSpaceLightingApplyIndirectDiffuse(half3 bakedGI, float2 normalizedScreenSpaceUV)
{
#if defined(_SCREEN_SPACE_GI)
    half4 ssgi = SAMPLE_TEXTURE2D_X(_ScreenSpaceGiTexture, sampler_LinearClamp, UnityStereoTransformScreenSpaceTex(normalizedScreenSpaceUV));
    half weight = saturate(ssgi.a * _ScreenSpaceGiParams.x);
    half3 radiance = max(ssgi.rgb, half3(0.0, 0.0, 0.0));
    return lerp(bakedGI, radiance, weight);
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
half3 ScreenSpaceLightingApplyIndirectSpecular(half3 environmentSpecular, float2 normalizedScreenSpaceUV)
{
#if defined(_SCREEN_SPACE_SSR)
    half4 ssr = SAMPLE_TEXTURE2D_X(_ScreenSpaceSsrTexture, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(normalizedScreenSpaceUV));
    half weight = saturate(ssr.a * _ScreenSpaceSsrParams.x);
    half3 radiance = max(ssr.rgb, half3(0.0, 0.0, 0.0));
    return lerp(environmentSpecular, radiance, weight);
#else
    return environmentSpecular;
#endif
}

#endif