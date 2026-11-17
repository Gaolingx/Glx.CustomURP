// Leak probe for the material-facing Screen Space Lighting / GTAO state. A capability-rejected
// camera draws these surfaces after a previous camera used every effect, and the fragment encodes
// what the draw observed. Two screen regions keep the opaque and transparent queues apart:
//   mode 0: R = keyword mask  (1 refraction, 2 GI, 4 SSR, 8 transparent SSR, 16 occlusion)
//   mode 1: R = binding mask  (1 refraction pyramid, 2 refraction depth, 4 GI, 8 SSR, 16 transparent SSR, 32 occlusion)
//   mode 2: R = uniform mask  (1 refraction params, 2 refraction probe count, 4 GI params, 8 SSR params, 16 transparent SSR params)
//   A = _LeakProbeMarker, so the verification can tell which camera wrote the readback pixel.
// Masks are written as mask / 255 so an 8-bit linear target reports the mask verbatim.
Shader "Hidden/Screen Space Lighting/Leak Probe"
{
    Properties
    {
        // 0: keyword mask, 1: texture binding mask, 2: uniform mask.
        _LeakProbeMode ("Leak Probe Mode", Float) = 0
        // Written to alpha; the overlay camera uses a different value than the base camera.
        _LeakProbeMarker ("Leak Probe Marker", Float) = 0.5
        // A texture at least this large is a camera result, not the built-in black fallback.
        _LeakProbeMinBoundSize ("Leak Probe Min Bound Size", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "Leak Probe"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Off
            ZWrite Off
            // The overlay camera draws over the base camera's probe at the same depth.
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fragment _ _SCREEN_SPACE_REFRACTION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_GI
            #pragma multi_compile_fragment _ _SCREEN_SPACE_SSR
            #pragma multi_compile_fragment _ _SCREEN_SPACE_TRANSPARENT_SSR
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

            float4 _SslRefractionParams;
            int _SslRefractionProbeCount;
            TEXTURE2D(_SslRefractionColorPyramid);
            TEXTURE2D(_SslRefractionDepthTexture);
            float4 _ScreenSpaceGiParams;
            TEXTURE2D(_ScreenSpaceGiTexture);
            float4 _ScreenSpaceSsrParams;
            TEXTURE2D(_ScreenSpaceSsrTexture);
            float4 _ScreenSpaceTransparentSsrParams;
            TEXTURE2D(_ScreenSpaceTransparentSsrTexture);
            TEXTURE2D(_ScreenSpaceOcclusionTexture);

            CBUFFER_START(UnityPerMaterial)
                float _LeakProbeMode;
                float _LeakProbeMarker;
                float _LeakProbeMinBoundSize;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            // The result textures are black on a flat probe scene, so content cannot tell "bound" from
            // "reset to the built-in black fallback"; the dimensions can. The player passes the
            // fallback size plus one, so both the black texture and an unbound sampler read zero.
            float IsBound(Texture2D probeTexture)
            {
                uint width = 0;
                uint height = 0;
                probeTexture.GetDimensions(width, height);
                return (width >= _LeakProbeMinBoundSize && height >= _LeakProbeMinBoundSize) ? 1.0 : 0.0;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float mask;
                if (_LeakProbeMode > 1.5)
                {
                    mask = 0.0;
                    mask += any(abs(_SslRefractionParams) > 1e-4) ? 1.0 : 0.0;
                    mask += _SslRefractionProbeCount > 0 ? 2.0 : 0.0;
                    mask += any(abs(_ScreenSpaceGiParams) > 1e-4) ? 4.0 : 0.0;
                    mask += any(abs(_ScreenSpaceSsrParams) > 1e-4) ? 8.0 : 0.0;
                    mask += any(abs(_ScreenSpaceTransparentSsrParams) > 1e-4) ? 16.0 : 0.0;
                }
                else if (_LeakProbeMode > 0.5)
                {
                    mask = 0.0;
                    mask += IsBound(_SslRefractionColorPyramid);
                    mask += IsBound(_SslRefractionDepthTexture) * 2.0;
                    mask += IsBound(_ScreenSpaceGiTexture) * 4.0;
                    mask += IsBound(_ScreenSpaceSsrTexture) * 8.0;
                    mask += IsBound(_ScreenSpaceTransparentSsrTexture) * 16.0;
                    mask += IsBound(_ScreenSpaceOcclusionTexture) * 32.0;
                }
                else
                {
                    mask = 0.0;
                #if defined(_SCREEN_SPACE_REFRACTION)
                    mask += 1.0;
                #endif
                #if defined(_SCREEN_SPACE_GI)
                    mask += 2.0;
                #endif
                #if defined(_SCREEN_SPACE_SSR)
                    mask += 4.0;
                #endif
                #if defined(_SCREEN_SPACE_TRANSPARENT_SSR)
                    mask += 8.0;
                #endif
                #if defined(_SCREEN_SPACE_OCCLUSION)
                    mask += 16.0;
                #endif
                }

                return float4(mask / 255.0, 0.0, 0.0, _LeakProbeMarker);
            }
            ENDHLSL
        }
    }
}
