#ifndef UNIVERSAL_SCREEN_SPACE_REFRACTION_INCLUDED
#define UNIVERSAL_SCREEN_SPACE_REFRACTION_INCLUDED

// Core-owned material-side screen-space refraction contract.
//
// A transparent Lit surface that selects a refraction model becomes a transmissive surface: the
// ray that leaves the approximated volume is intersected with the active proxy (an arbitrary
// reflection probe box or an infinite/sky fallback), the current frame color pyramid (opaque + sky,
// generated before transparents) is sampled at the projected hit, attenuated by the material
// transmittance and combined with the shaded color through the transmittance mask (1 - opacity).
//
// When the screen-space path misses, the material continues with the reflection probe (or sky)
// cubemap along the refracted ray for the weight the screen trace did not consume, which mirrors
// HDRP's remaining refraction hierarchy weight. The plugin only provides the color pyramid, the
// opaque depth and the proxy parameters.
//
// Any missing input (no refraction keyword, no pyramid binding, zero intensity) returns valid=false
// so the original URP transparent probe path is preserved.

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GeometricTools.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Refraction.hlsl"

// The built-in Lit/Complex Lit shaders declare these in LitInput's UnityPerMaterial CBUFFER so the
// material stays SRP Batcher compatible. ShaderGraph generated shaders include this header without
// LitInput, so the uniforms are declared here only for the refractive variants.
#if !defined(URP_REFRACTION_MATERIAL_PROPERTIES_DECLARED)
#if defined(_REFRACTION_PLANE) || defined(_REFRACTION_SPHERE) || defined(_REFRACTION_THIN)
#if !defined(URP_REFRACTION_IOR_DECLARED)
float _Ior;
#endif
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
// x: color pyramid mip count (same value as HDRP's _ColorPyramidLodCount),
// y: 1 / screen fade distance, z: intensity, w: unused
float4 _SslRefractionParams;

// Scene proxy. URP has no per-material proxy volume, so the plugin binds the world AABB of a
// reflection probe and, when no probe exists, an infinite proxy that mirrors HDRP's sky env light.
// BoxMin.w is the proxy mode (see the defines below); None keeps the legacy transparent output.
#define SSL_REFRACTION_PROXY_MODE_NONE 0
#define SSL_REFRACTION_PROXY_MODE_BOX 1
#define SSL_REFRACTION_PROXY_MODE_INFINITE 2

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

struct ScreenSpaceRefractionResult
{
    // Screen-space radiance, already scaled by weight.
    half3 transmitted;
    // Screen-space coverage in [0, 1]. The probe fallback owns the remaining 1 - weight.
    half weight;
    // Beer-Lambert transmittance through the approximated volume.
    half3 transmittance;
    // Ray leaving the volume (world space).
    float3 refractV;
    float3 refractPosWS;
    // False when the effect inputs are unavailable; the caller must keep the legacy output.
    bool valid;
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

// Traces the refracted ray through the proxy and samples the current frame color pyramid. The
// returned transmittance and ray are valid even when the screen path misses, so the caller can
// continue with the reflection probe fallback (ScreenSpaceRefractionTransmission).
ScreenSpaceRefractionResult ScreenSpaceRefractionEvaluate(ScreenSpaceRefractionInput input)
{
    ScreenSpaceRefractionResult result;
    result.transmitted = half3(0.0, 0.0, 0.0);
    result.weight = 0.0h;
    result.transmittance = half3(1.0, 1.0, 1.0);
    result.refractV = -input.viewDirectionWS;
    result.refractPosWS = input.positionWS;
    result.valid = false;

    int pyramidMipCount = (int)_SslRefractionParams.x;
    float intensity = max(_SslRefractionParams.z, 0.0);
    if (pyramidMipCount <= 0 || intensity <= 0.0)
        return result;

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

    // HDRP clamps the Thin model absorption distance to the same 0.005 used for its thickness;
    // otherwise a coloured thin glass would absorb far less than the reference.
#if defined(_REFRACTION_THIN)
    float absorptionDistance = 0.005;
#else
    float absorptionDistance = input.transmittanceDistance;
#endif
    half3 absorption = ScreenSpaceRefractionToAbsorption(input.transmittanceColor, absorptionDistance);
    result.transmittance = exp(-absorption * refraction.dist);
    result.refractV = refraction.rayWS;
    result.refractPosWS = refraction.positionWS;
    result.valid = true;

    int proxyMode = (int)(_SslRefractionProxyBoxMin.w + 0.5);
    if (proxyMode == SSL_REFRACTION_PROXY_MODE_NONE)
        return result;

    // Proxy raycast.
    float projectionDistance;
    if (proxyMode == SSL_REFRACTION_PROXY_MODE_BOX)
    {
        float3 boxMin = _SslRefractionProxyBoxMin.xyz;
        float3 boxMax = _SslRefractionProxyBoxMax.xyz;
        if (any(boxMax <= boxMin))
            return result;

        projectionDistance = IntersectRayAABBSimple(refraction.positionWS, refraction.rayWS, boxMin, boxMax);
        // The comparisons also reject NaN and infinite intersections (parallel rays in a degenerate box).
        if (!(projectionDistance > 0.0) || !(projectionDistance < 1e6))
            return result;
    }
    else
    {
        // Infinite proxy: HDRP's sky env light sets minProjectionDistance to 65504 (half max).
        projectionDistance = 65504.0;
    }

    float3 hitPositionWS = refraction.positionWS + refraction.rayWS * projectionDistance;
    float4 hitPositionCS = ComputeClipSpacePosition(hitPositionWS, GetWorldToHClipMatrix());
    if (hitPositionCS.w <= 0.0)
        return result;

    // Kept as a hard reject until the screen-edge fade semantics task; a miss falls back to the
    // reflection probe instead of leaking clamped edge texels.
    float2 hitNDC = ComputeNormalizedDeviceCoordinates(hitPositionWS, GetWorldToHClipMatrix());
    if (any(hitNDC < 0.0) || any(hitNDC > 1.0))
        return result;

    // Screen edge fade, identical to HDRP's EdgeOfScreenFade(hit.positionNDC, ...).
    half screenWeight = (half)ScreenSpaceRefractionEdgeFade(hitNDC, _SslRefractionParams.y) * (half)intensity;
    if (!(screenWeight > 0.0h))
        return result;

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

    // (1 - FGD) transmits what the surface does not reflect, exactly like HDRP's
    // EvaluateBSDF_ScreenspaceRefraction.
    half3 transmitted = (1.0h - input.specularFGD) * half3(colorPyramid) * result.transmittance * screenWeight;
    result.transmitted = max(transmitted, half3(0.0, 0.0, 0.0));
    result.weight = screenWeight;
    return result;
}

// Combines the screen-space contribution with the reflection probe fallback. HDRP continues the
// reflection hierarchy with the same env light (or the sky) along the refracted ray for whatever
// weight the screen trace did not consume, which keeps the transition continuous at screen edges
// and on misses. GlossyEnvironmentReflection already includes URP's sky cubemap fallback.
half3 ScreenSpaceRefractionTransmission(ScreenSpaceRefractionResult result, ScreenSpaceRefractionInput input)
{
    if (!result.valid)
        return half3(0.0, 0.0, 0.0);

    half3 transmitted = result.transmitted;

#if defined(UNIVERSAL_LIGHTING_INCLUDED)
    half remainingWeight = saturate(1.0h - result.weight);
    if (remainingWeight > 0.0h)
    {
        half3 probeRadiance = GlossyEnvironmentReflection(
            result.refractV, result.refractPosWS, input.perceptualRoughness, 1.0h, input.screenUV);
        transmitted += (1.0h - input.specularFGD) * result.transmittance * probeRadiance * remainingWeight;
    }
#endif

    return max(transmitted, half3(0.0, 0.0, 0.0));
}

#endif