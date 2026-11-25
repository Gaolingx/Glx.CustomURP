#ifndef GLX_PROBE_SAMPLING_INCLUDED
#define GLX_PROBE_SAMPLING_INCLUDED

// Producer/reflection-side probe terms. The shared published-atlas lookup lives in
// ShaderLibrary/RayTracing/GlxProbeQuery.hlsl, which the material diffuse consumer also includes.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RayTracing/GlxProbeQuery.hlsl"

float4 _GlxHitAmbientIrradiance;
uint _GlxHitLowFrequencySource; // 0 ambient, 1 published probe snapshot when valid
uint _GlxProbeFeedbackEnabled;  // producer-only: probe-mode hits read one previous snapshot
float _GlxProbeFeedbackAlbedoCap;

// G1 Feedback(): min(albedo, cap) * Eprev / pi added to the hit radiance once. The convolution
// then contributes min(albedo, cap) * Eprev to the probe's irradiance. Reflection hits never call
// this: their low-frequency term is GlxHitLowFrequencyIrradiance above.
float3 GlxProbeFeedbackRadiance(float3 position, float3 normal, float3 albedo)
{
    if (_GlxProbeFeedbackEnabled == 0) return 0;
    float3 irradiance;
    if (!GlxProbeQuery(position, normal, irradiance)) return 0;
    return min(albedo, _GlxProbeFeedbackAlbedoCap.xxx) * max(irradiance, 0) * 0.31830988618;
}

float3 GlxHitLowFrequencyIrradiance(float3 position, float3 normal)
{
    float3 result = _GlxHitAmbientIrradiance.xyz;
    if (_GlxHitLowFrequencySource != 0)
    {
        float3 probe;
        if (GlxProbeQuery(position, normal, probe)) result = probe;
    }
    return max(result, 0);
}

#endif
