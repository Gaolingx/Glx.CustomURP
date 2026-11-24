#ifndef GLX_HIT_LIGHTS_INCLUDED
#define GLX_HIT_LIGHTS_INCLUDED
#include "GlxBasicPayload.hlsl"

// Named constant data, not a material SRV/UAV: legacy CHS modes require no table binding.
// Capacity mirrors RayTracingSceneLights.Capacity; the shader clamps the uploaded count so a
// stale or unbound table can never turn the selection loop into an unbounded traversal.
#define GLX_HIT_LIGHT_CAPACITY 64u
struct GlxLightRecord
{
    float4 positionKind; // directional: unit vector TO light; punctual: position
    float4 rgbShadow;
    float4 spotAxis;     // negative forward
    float4 attenuation; // inverse squared range, spot scale, spot offset
};
// Unity's ShaderLab cbuffer parser rejects struct arrays, so the scene uploads four
// word vectors per light. Word 0 is the uploaded count.
cbuffer GlxHitLightTable
{
    float4 _GlxLightWords[257];
};

GlxLightRecord GlxFetchLight(uint slot)
{
    uint word = 1 + slot * 4;
    GlxLightRecord r;
    r.positionKind = _GlxLightWords[word];
    r.rgbShadow = _GlxLightWords[word + 1];
    r.spotAxis = _GlxLightWords[word + 2];
    r.attenuation = _GlxLightWords[word + 3];
    return r;
}

struct GlxHitLight
{
    float3 direction, rgb;
    float distance, shadow;
};

GlxHitLight GlxEvaluateLight(uint slot, float3 position)
{
    GlxLightRecord r = GlxFetchLight(slot);
    GlxHitLight light;
    light.direction = r.positionKind.xyz;
    light.distance = 3.402823466e+38F;
    light.shadow = r.rgbShadow.w;
    float attenuation = 1;
    if (r.positionKind.w != 0)
    {
        float3 delta = r.positionKind.xyz - position;
        float d2 = dot(delta, delta);
        light.distance = sqrt(d2);
        light.direction = delta / max(light.distance, 1e-10);
        // URP 14 DistanceAttenuation / AngleAttenuation, full precision in world units.
        d2 = max(d2, 6.103515625e-5);
        float f = d2 * r.attenuation.x;
        float window = saturate(1.0 - f * f);
        attenuation = window * window / d2;
        if (r.positionKind.w == 2)
        {
            float cone = saturate(dot(r.spotAxis.xyz, light.direction) * r.attenuation.y + r.attenuation.z);
            attenuation *= cone * cone;
        }
    }
    light.rgb = r.rgbShadow.xyz * attenuation;
    return light;
}

// One shadow segment. Called from two fixed sites in GlxShadeLights: nesting TraceRay in
// a variable-trip loop makes the compiled hit shader exceed the DXR stack, and the device
// is removed once three or more lights are selected. Keep both call sites explicit and
// keep max_recursion_depth 2 on every raygen that can reach a table-mode closest hit.
float GlxVisibility(float3 position, GlxHitLight light)
{
    const float bias = 0.002;
    RayDesc shadow;
    shadow.Origin = position;
    shadow.Direction = light.direction;
    shadow.TMin = min(bias, light.distance * 0.5);
    shadow.TMax = light.distance;
    GlxBasicPayload visibility = GlxEmptyPayload(GlxShadowMode);
    TraceRay(_GlxScene, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH, 0xff, 0, 1, 0, shadow, visibility);
    return (visibility.flags & GlxHit) != 0 ? 0.0 : 1.0;
}

// Both materials use the same selection, shadow traversal and count packing. The caller
// supplies GlxLightBrdf for its material (Basic: Lambert; Lit: probe Lambert or metallic BRDF).
void GlxShadeLights(inout GlxBasicPayload p, float3 n, GlxHitSurface surface)
{
    float3 position = WorldRayOrigin() + RayTCurrent() * WorldRayDirection();
    float4 scores = 0;
    uint4 slots = 0;
    uint eligible = 0;
    uint count = min((uint)_GlxLightWords[0].x, GLX_HIT_LIGHT_CAPACITY);
    [loop] for (uint i = 0; i < count; ++i)
    {
        GlxHitLight light = GlxEvaluateLight(i, position);
        float score = dot(light.rgb, float3(0.2126, 0.7152, 0.0722));
        if (score <= 0 || dot(n, light.direction) <= 0) continue;
        ++eligible;
        uint slot = i;
        // Displaced entries also participate in ties; compare slot explicitly.
        [unroll] for (uint j = 0; j < 4; ++j)
        {
            if (score > scores[j] || (score == scores[j] && slot < slots[j]))
            {
                float displaced = scores[j]; scores[j] = score; score = displaced;
                uint old = slots[j]; slots[j] = slot; slot = old;
            }
        }
    }
    uint selected = min(eligible, 4u);
    // Locate the first Smax shadow-enabled contributors without tracing.
    uint2 shadowRank = 0xffffffffu;
    uint shadowEligible = 0;
    [loop] for (uint k = 0; k < selected; ++k)
    {
        if (GlxFetchLight(slots[k]).rgbShadow.w == 0) continue;
        if (shadowRank.x == 0xffffffffu) shadowRank.x = k;
        else if (shadowRank.y == 0xffffffffu) shadowRank.y = k;
        ++shadowEligible;
    }
    float2 visibility = 1;
    if (shadowRank.x != 0xffffffffu)
        visibility.x = GlxVisibility(position, GlxEvaluateLight(slots[shadowRank.x], position));
    if (shadowRank.y != 0xffffffffu)
        visibility.y = GlxVisibility(position, GlxEvaluateLight(slots[shadowRank.y], position));
    uint rays = shadowRank.y != 0xffffffffu ? 2u : (shadowRank.x != 0xffffffffu ? 1u : 0u);
    uint untraced = shadowEligible - rays;
    [loop] for (uint k = 0; k < selected; ++k)
    {
        if ((k == shadowRank.x && visibility.x == 0) || (k == shadowRank.y && visibility.y == 0))
        {
            p.flags |= GlxShadowed;
            continue;
        }
        GlxHitLight light = GlxEvaluateLight(slots[k], position);
        p.radiance += GlxLightBrdf(surface, n, light.direction, p.mode) * light.rgb *
                      (saturate(dot(n, light.direction)) * 0.31830988618);
    }
    // Return telemetry to raygen in the existing mode word (not an extra payload field).
    p.mode |= (selected << 8) | ((eligible - selected) << 11) | (rays << 17) | (untraced << 19);
}
#endif
