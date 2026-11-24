#ifndef UNIVERSAL_CONTACT_SHADOWS_INCLUDED
#define UNIVERSAL_CONTACT_SHADOWS_INCLUDED

// The deferred kernel intentionally does not include URP's per-draw Input.hlsl.
#if defined(UNIVERSAL_CLUSTERED_DEFERRED_INCLUDED)
    #define CONTACT_SHADOW_MAX_LIGHTS CD_MAX_VISIBLE_LIGHTS
#else
    #define CONTACT_SHADOW_MAX_LIGHTS MAX_VISIBLE_LIGHTS
#endif

CBUFFER_START(ContactShadows)
    float _ContactShadowOpacity;
    uint  _MainLightContactShadowMask;
    float _AdditionalLightsContactShadowMasks[CONTACT_SHADOW_MAX_LIGHTS];
CBUFFER_END

#if defined(SHADER_API_D3D11) || defined(SHADER_API_D3D12) || defined(SHADER_API_VULKAN) || defined(SHADER_API_METAL)
#define URP_CONTACT_SHADOWS_SUPPORTED

#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
Texture2DArray<uint> _ContactShadowTexture;
#define ContactShadowLoad(pixel) _ContactShadowTexture.Load(int4(pixel, unity_StereoEyeIndex, 0))
#else
Texture2D<uint> _ContactShadowTexture;
#define ContactShadowLoad(pixel) _ContactShadowTexture.Load(int3(pixel, 0))
#endif

uint GetAdditionalLightContactShadowMask(int lightIndex)
{
    // Numeric float masks preserve all 24 bits even when a clustered loop flushes denormals.
    if (lightIndex < 0 || lightIndex >= CONTACT_SHADOW_MAX_LIGHTS)
        return 0u;
    return (uint)_AdditionalLightsContactShadowMasks[lightIndex];
}

half GetContactShadowAtPixel(uint2 pixel, uint mask)
{
    half attenuation = 1.0h;
    if (mask != 0u && _ContactShadowOpacity > 0.0)
    {
        uint packed = ContactShadowLoad(pixel);
        if ((packed & mask & 0x00ffffffu) != 0u)
            attenuation -= half((packed >> 24) * (_ContactShadowOpacity / 255.0));
    }
    return attenuation;
}

#if defined(SHADER_STAGE_FRAGMENT)
half GetContactShadowAttenuation(float3 positionWS, uint mask)
{
    // Project with this eye's camera VP, not the light's shadow coordinates.
    float2 uv = ComputeNormalizedDeviceCoordinates(positionWS, GetWorldToHClipMatrix());
    uint2 size = (uint2)_ScaledScreenParams.xy;
    uint2 pixel = min((uint2)(saturate(uv) * size), size - 1u);
    return GetContactShadowAtPixel(pixel, mask);
}
#endif
#endif // supported API
#endif // UNIVERSAL_CONTACT_SHADOWS_INCLUDED
