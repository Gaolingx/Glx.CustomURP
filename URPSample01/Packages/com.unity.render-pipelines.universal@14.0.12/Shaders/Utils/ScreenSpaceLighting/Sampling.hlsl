#ifndef UNIVERSAL_SCREEN_SPACE_LIGHTING_SAMPLING_INCLUDED
#define UNIVERSAL_SCREEN_SPACE_LIGHTING_SAMPLING_INCLUDED

// Sampling helpers for the Screen Space Global Illumination kernels. HDRP drives its ray directions
// and denoiser taps with a blue noise texture set; URP keeps the effect self contained and derives
// the same kind of low-discrepancy, per-pixel decorrelated values from an interleaved gradient noise
// scramble plus an R2 sequence.

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/Sampling.hlsl"

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