#ifndef GLX_PROBE_QUERY_INCLUDED
#define GLX_PROBE_QUERY_INCLUDED

// Read-only lookup of the published GI probe generation, shared by the ray-tracing hit shading
// (feedback / hit low-frequency term) and the material-side diffuse consumer. Never writes probes
// or triggers an update. Inactive/unpublished neighbours drop out and the weights are renormalised.
// Layout matches RayTracingProbeUpdate.cs and GlxProbeUpdate.compute.
//
// Requires SM 4.5: the per-probe offset/state and the published mask are structured buffers.
// Material shaders must only include this from a ≥4.5 variant (see Lit/ComplexLit probe pragma).

#define GLX_PROBE_PER_ROW 8
#define GLX_PROBE_IRR_TILE 8
#define GLX_PROBE_IRR_INTERIOR 6
#define GLX_PROBE_DIST_TILE 16
#define GLX_PROBE_DIST_INTERIOR 14

TEXTURE2D(_GlxProbeIrrSnapshot);
TEXTURE2D(_GlxProbeDistSnapshot);
SAMPLER(sampler_GlxProbeIrrSnapshot);
StructuredBuffer<float4> _GlxProbeStateSnapshot;        // offset.xyz, state.w
StructuredBuffer<uint> _GlxProbePublishedSnapshot;
float4 _GlxProbeIrrSize;   // xy size, zw inverse size
float4 _GlxProbeDistSize;
uint _GlxProbeCountX, _GlxProbeCountY, _GlxProbeCountZ;
float4 _GlxProbeOrigin;
float _GlxProbeSpacing;
uint _GlxProbeSnapshotValid;

float2 GlxOctEncode(float3 direction)
{
    direction /= abs(direction.x) + abs(direction.y) + abs(direction.z) + 1e-8;
    if (direction.z >= 0)
        return direction.xy;
    return float2(direction.x >= 0 ? 1.0 : -1.0, direction.y >= 0 ? 1.0 : -1.0) *
           float2(1.0 - abs(direction.y), 1.0 - abs(direction.x));
}

float3 GlxSampleProbeIrradiance(int probe, float2 oct)
{
    uint2 tile = uint2((probe % GLX_PROBE_PER_ROW) * GLX_PROBE_IRR_TILE,
                       (probe / GLX_PROBE_PER_ROW) * GLX_PROBE_IRR_TILE);
    float2 texel = tile + 1 + saturate(oct * 0.5 + 0.5) * GLX_PROBE_IRR_INTERIOR;
    return SAMPLE_TEXTURE2D_LOD(_GlxProbeIrrSnapshot, sampler_GlxProbeIrrSnapshot, texel * _GlxProbeIrrSize.zw, 0).rgb;
}

float2 GlxSampleProbeMoments(int probe, float2 oct)
{
    uint2 tile = uint2((probe % GLX_PROBE_PER_ROW) * GLX_PROBE_DIST_TILE,
                       (probe / GLX_PROBE_PER_ROW) * GLX_PROBE_DIST_TILE);
    float2 texel = tile + 1 + saturate(oct * 0.5 + 0.5) * GLX_PROBE_DIST_INTERIOR;
    return SAMPLE_TEXTURE2D_LOD(_GlxProbeDistSnapshot, sampler_GlxProbeIrrSnapshot, texel * _GlxProbeDistSize.zw, 0).rg;
}

// Trilinear x normal wrap x Chebyshev, in the G1 query convention (E stored, Pgi = E/pi).
bool GlxProbeQuery(float3 position, float3 normal, out float3 irradiance)
{
    irradiance = 0;
    if (_GlxProbeSnapshotValid == 0) return false;
    float3 grid = (position - _GlxProbeOrigin.xyz) / _GlxProbeSpacing;
    int3 cell = (int3)floor(grid);
    float3 fraction = grid - cell;
    float3 sum = 0;
    float total = 0;
    [unroll] for (uint corner = 0; corner < 8; ++corner)
    {
        int3 step = int3(corner & 1u, (corner >> 1u) & 1u, (corner >> 2u) & 1u);
        int3 coord = cell + step;
        if (any(coord < 0) || coord.x >= (int)_GlxProbeCountX ||
            coord.y >= (int)_GlxProbeCountY || coord.z >= (int)_GlxProbeCountZ) continue;
        int probe = coord.x + (int)_GlxProbeCountX * (coord.y + (int)_GlxProbeCountY * coord.z);
        float4 data = _GlxProbeStateSnapshot[probe];
        if (data.w <= 0.5 || _GlxProbePublishedSnapshot[probe] == 0) continue;
        float3 delta = _GlxProbeOrigin.xyz + coord * _GlxProbeSpacing + data.xyz - position;
        float distance = length(delta);
        float3 direction = distance > 1e-4 ? delta / distance : normal;
        float3 weight = float3(step.x == 0 ? 1.0 - fraction.x : fraction.x,
                               step.y == 0 ? 1.0 - fraction.y : fraction.y,
                               step.z == 0 ? 1.0 - fraction.z : fraction.z);
        float trilinear = weight.x * weight.y * weight.z;
        if (trilinear <= 0) continue;
        float2 moments = GlxSampleProbeMoments(probe, GlxOctEncode(direction));
        float variance = abs(moments.x * moments.x - moments.y);
        float delta2 = moments.x - distance;
        float chebyshev = variance / (variance + delta2 * delta2 + 1e-6);
        chebyshev = max(chebyshev, 0.05);
        float wrap = saturate(dot(normal, direction) * 0.5 + 0.5);
        float w = trilinear * wrap * chebyshev;
        if (w <= 0) continue;
        sum += w * GlxSampleProbeIrradiance(probe, GlxOctEncode(normal));
        total += w;
    }
    if (total <= 0) return false;
    irradiance = sum / total;
    return true;
}

#endif
