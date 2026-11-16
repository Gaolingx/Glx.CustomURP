#ifndef UNIVERSAL_SCREEN_SPACE_LIGHTING_SAMPLING_INCLUDED
#define UNIVERSAL_SCREEN_SPACE_LIGHTING_SAMPLING_INCLUDED

// Sampling helpers for the Screen Space Lighting kernels.
//
// HDRP drives its ray directions and denoiser taps with the blue-noise distributed Owen scrambled
// Sobol sequence (BND, Heitz et al.). URP ships the same three LUTs as package resources, so the
// SSGI and SSR kernels use the identical sequence (SSR binds the 1SPP tiles, SSGI the 8SPP ones).
// When the resources are missing (for example a player built from a renderer that never resolved
// them) the kernels stay self contained and fall back to the interleaved gradient noise + R2
// sequence this port used before.

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/Sampling.hlsl"

#if defined(SSGI_BLUE_NOISE)
// Ported from HDRP 14.0.12 Runtime/RenderPipeline/Raytracing/Shaders/RaytracingSampling.hlsl.
Texture2D<float> _SsgiOwenScrambledTexture;
Texture2D<float> _SsgiScramblingTileXSPP;
Texture2D<float> _SsgiRankingTileXSPP;

// Mirrors GetBNDSequenceSample: one blue-noise distributed sample in [0,1) per pixel, frame, dimension.
float SsgiGetSequenceSample(uint2 pixelCoord, int frameIndex, uint dimension)
{
    uint sampleIndex = (uint)max(frameIndex, 0);

    // Wrap arguments exactly like HDRP.
    pixelCoord = pixelCoord & 127;
    sampleIndex = sampleIndex & 255;
    dimension = dimension & 255;

    // XOR the index based on the optimized ranking tile.
    uint rankingIndex = (pixelCoord.x + pixelCoord.y * 128) * 8 + (dimension & 7);
    uint rankedSampleIndex = sampleIndex ^ clamp(
        (uint)(_SsgiRankingTileXSPP[uint2(rankingIndex & 127, rankingIndex / 128)] * 256.0), 0, 255);

    // Fetch the scrambled Sobol value.
    uint value = clamp((uint)(_SsgiOwenScrambledTexture[uint2(dimension, rankedSampleIndex)] * 256.0), 0, 255);

    // XOR the value with the optimized scrambling tile.
    uint scramblingIndex = (pixelCoord.x + pixelCoord.y * 128) * 8 + (dimension & 7);
    float scramblingValue = min(_SsgiScramblingTileXSPP[uint2(scramblingIndex & 127, scramblingIndex / 128)], 0.999);
    value = value ^ uint(scramblingValue * 256.0);

    return (scramblingValue + value) / 256.0;
}

// Mirrors GetLDSequenceSampleFloat: the denoiser point distribution reads the Sobol LUT directly.
float SsgiGetPointSequenceSample(uint index, uint dimension)
{
    return _SsgiOwenScrambledTexture[uint2(dimension & 255, index & 255)];
}
#else
static const float2 k_SsgiR2Sequence = float2(0.7548776662466927, 0.5698402909980532);

// Mirrors GetBNDSequenceSample: one float in [0,1) per pixel, frame and dimension.
float SsgiGetSequenceSample(uint2 pixelCoord, int frameIndex, uint dimension)
{
    float ign = InterleavedGradientNoise(float2(pixelCoord), frameIndex + (int)dimension * 13);
    float sequence = frac((frameIndex + 1) * k_SsgiR2Sequence[dimension & 1u]);
    return frac(sequence + ign);
}

// Mirrors GetLDSequenceSampleFloat for the denoiser's point distribution buffer.
float SsgiGetPointSequenceSample(uint index, uint dimension)
{
    return frac((index + 1) * k_SsgiR2Sequence[dimension & 1u]);
}
#endif

#endif