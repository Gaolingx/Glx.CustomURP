#ifndef UNIVERSAL_SCREEN_SPACE_REFRACTION_INCLUDED
#define UNIVERSAL_SCREEN_SPACE_REFRACTION_INCLUDED

// Core-owned material-side screen-space refraction contract.
//
// A transparent Lit surface that selects a refraction model becomes a transmissive surface: the
// ray that leaves the approximated volume is intersected with the active box-projected reflection
// probe, the current frame color pyramid (opaque + sky, generated before transparents) is sampled
// at the projected hit, attenuated by the material transmittance and added to the shaded color,
// and the surface alpha is forced to 1. This mirrors HDRP's refractive Lit behavior, where opacity
// is used as a transmittance mask instead of as alpha.
//
// The plugin only provides the color pyramid, the opaque depth and the volume parameters. Any
// missing input (no refraction keyword, no box-projected probe proxy, off-screen or behind-camera
// hit, no pyramid) returns weight 0 so the original URP transparent probe path is preserved
// byte-for-byte.

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GeometricTools.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Refraction.hlsl"

// The built-in Lit/Complex Lit shaders declare these in LitInput's UnityPerMaterial CBUFFER so the
// material stays SRP Batcher compatible. ShaderGraph generated shaders include this header without
// LitInput, so the uniforms are declared here only for the refractive variants.
#if !defined(URP_REFRACTION_MATERIAL_PROPERTIES_DECLARED)
#if defined(_REFRACTION_PLANE) || defined(_REFRACTION_SPHERE) || defined(_REFRACTION_THIN)
float _Ior;
float _Thickness;
half4 _TransmittanceColor;
float _TransmittanceDistance;
#endif
#endif

TEXTURE2D_X(_SslRefractionColorPyramid);
TEXTURE2D_X_FLOAT(_SslRefractionDepthTexture);

// xy: viewport scale, zw: full resolution uv limit (same convention as HDRP's
// _ColorPyramidUvScaleAndLimitCurrentFrame).
float4 _SslRefractionColorPyramidScaleAndLimit;
// x: last color pyramid mip, y: 1 / screen fade distance, z: intensity, w: unused
float4 _SslRefractionParams;

// Scene proxy. URP has no per-material proxy volume, so the plugin binds the world AABB of the
// active box-projected reflection probe (the same probe box the material uses for reflections).
// BoxMin.w is 1 when a proxy is available; 0 disables refraction and keeps the original path.
float4 _SslRefractionProxyBoxMin;
float4 _SslRefractionProxyBoxMax;

struct ScreenSpaceRefractionInput
{
    float3 positionWS;
    float3 normalWS;
    float3 viewDirectionWS;
    float2 screenUV;
    float2 screenSize;
    float deviceDepth;
    half3 specularFGD;
    half perceptualRoughness;
    float ior;
    float thickness;
    half3 transmittanceColor;
    float transmittanceDistance;
};

float ScreenSpaceRefractionEdgeFade(float2 coordNDC, float fadeRcpLength)
{
    float2 coordCS = coordNDC * 2.0 - 1.0;
    float2 t = Remap10(abs(coordCS), fadeRcpLength, fadeRcpLength);
    return Smoothstep01(t.x) * Smoothstep01(t.y);
}

// HDRP's TransmittanceColorAtDistanceToAbsorption.
half3 ScreenSpaceRefractionToAbsorption(half3 transmittanceColor, float atDistance)
{
    return -log(max(transmittanceColor, half3(1e-5, 1e-5, 1e-5))) / max(atDistance, 1e-5);
}

// Returns the transmitted radiance (already attenuated by (1 - specularFGD), transmittance, edge
// fade and the volume intensity). weight is the blend weight used to force the surface alpha to 1;
// weight 0 means "not a refracted pixel" and the caller must not change the material output.
half3 ScreenSpaceRefractionEvaluate(ScreenSpaceRefractionInput input, out half weight)
{
    weight = 0.0h;

    // URP has no per-material proxy volume. The plugin binds the box of the active box-projected
    // reflection probe; without one there is nothing to intersect.
    if (_SslRefractionProxyBoxMin.w <= 0.0)
        return half3(0.0, 0.0, 0.0);

    float3 boxMin = _SslRefractionProxyBoxMin.xyz;
    float3 boxMax = _SslRefractionProxyBoxMax.xyz;
    if (any(boxMax <= boxMin))
        return half3(0.0, 0.0, 0.0);

    int pyramidMipCount = (int)_SslRefractionParams.x;
    float intensity = max(_SslRefractionParams.z, 0.0);
    if (pyramidMipCount <= 0 || intensity <= 0.0)
        return half3(0.0, 0.0, 0.0);

    float ior = max(input.ior, 1.0);
    float thickness = max(input.thickness, 1e-4);
#if defined(_REFRACTION_THIN)
    thickness = 0.005;
#endif

    // HDRP approximation: the surface is locally a plane/sphere of the given thickness. For the
    // plane and thin models the exited ray continues along the view direction, the refraction is
    // encoded by the shifted origin.
    RefractionModelResult refraction;
#if defined(_REFRACTION_SPHERE)
    refraction = RefractionModelSphere(input.viewDirectionWS, input.positionWS, input.normalWS, ior, thickness);
#else
    refraction = RefractionModelBox(input.viewDirectionWS, input.positionWS, input.normalWS, ior, thickness);
#endif

    // Proxy raycast against the probe box.
    float projectionDistance = IntersectRayAABBSimple(refraction.positionWS, refraction.rayWS, boxMin, boxMax);
    // The comparisons also reject NaN and infinite intersections (parallel rays in a degenerate box).
    if (!(projectionDistance > 0.0) || !(projectionDistance < 1e6))
        return half3(0.0, 0.0, 0.0);

    float3 hitPositionWS = refraction.positionWS + refraction.rayWS * projectionDistance;
    float4 hitPositionCS = ComputeClipSpacePosition(hitPositionWS, GetWorldToHClipMatrix());
    if (hitPositionCS.w <= 0.0)
        return half3(0.0, 0.0, 0.0);

    float2 hitNDC = ComputeNormalizedDeviceCoordinates(hitPositionWS, GetWorldToHClipMatrix());
    if (any(hitNDC < 0.0) || any(hitNDC > 1.0))
        return half3(0.0, 0.0, 0.0);

    // Screen edge fade, identical to HDRP's EdgeOfScreenFade(hit.positionNDC, ...).
    weight = (half)ScreenSpaceRefractionEdgeFade(hitNDC, _SslRefractionParams.y) * (half)intensity;
    if (weight <= 0.0h)
        return half3(0.0, 0.0, 0.0);

    uint2 hitPositionSS = (uint2)min(hitNDC * input.screenSize, input.screenSize - 1.0);
    float hitDeviceDepth = LOAD_TEXTURE2D_X(_SslRefractionDepthTexture, hitPositionSS).r;
    float hitLinearDepth = LinearEyeDepth(hitDeviceDepth, _ZBufferParams);
    float surfaceLinearDepth = LinearEyeDepth(input.deviceDepth, _ZBufferParams);

    // Depth rejection: if the sampled background pixel belongs to geometry in front of the
    // refracting surface, keep the un-offset surface UV instead of leaking that foreground color.
    float mipLevel = PositivePow(input.perceptualRoughness, 1.3) * (float)max(pyramidMipCount - 1, 0);
    float refractionOffsetMultiplier = max(0.0, 1.0 - mipLevel * 0.08);
    refractionOffsetMultiplier *= (hitLinearDepth > surfaceLinearDepth) ? 1.0 : 0.0;

    float2 samplingNDC = lerp(input.screenUV, hitNDC, refractionOffsetMultiplier);
    float2 samplingUV = samplingNDC * _SslRefractionColorPyramidScaleAndLimit.xy;

    // Clamp to the mip footprint, following HDRP's color pyramid uv limit. Rough materials can push
// the limit below the pixel centre bound; keep the clamp range valid.
    float2 scaleMinusLimit = _SslRefractionColorPyramidScaleAndLimit.xy - _SslRefractionColorPyramidScaleAndLimit.zw;
    float2 mipLimit = _SslRefractionColorPyramidScaleAndLimit.xy - scaleMinusLimit * pow(2.0, 2.0 + ceil(mipLevel));
    mipLimit = max(mipLimit, scaleMinusLimit);
    samplingUV = clamp(samplingUV, scaleMinusLimit, mipLimit);

    float3 colorPyramid = SAMPLE_TEXTURE2D_X_LOD(_SslRefractionColorPyramid, sampler_LinearClamp, samplingUV, mipLevel).rgb;

    // Absorption through the approximated volume.
    half3 absorption = ScreenSpaceRefractionToAbsorption(input.transmittanceColor, input.transmittanceDistance);
    half3 transmittance = exp(-absorption * refraction.dist);

    // (1 - FGD) transmits what the surface does not reflect, exactly like HDRP's
    // EvaluateBSDF_ScreenspaceRefraction.
    half3 transmitted = (1.0h - input.specularFGD) * half3(colorPyramid) * transmittance * weight;
    return max(transmitted, half3(0.0, 0.0, 0.0));
}

#endif