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

#endif