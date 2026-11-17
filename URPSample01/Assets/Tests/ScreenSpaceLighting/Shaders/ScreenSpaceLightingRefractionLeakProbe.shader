// F-05 test surface: a reflection camera is asked to draw this opaque quad after the main camera
// left refraction state behind. The fragment encodes the state that reached the draw:
//   mode 0: R = _SCREEN_SPACE_REFRACTION variant selected, G = _SslRefractionParams non-zero,
//           B = _SslRefractionProbeCount non-zero
//   mode 1: R = _SslRefractionColorPyramid samples non-black, G/B = 0
//   A = 0.5 on every written pixel, so the verification can tell the quad from the camera clear.
// A cleared per-camera state therefore reads (0, 0, 0, 128) in both modes.
Shader "Hidden/Screen Space Lighting/Refraction Leak Probe"
{
    Properties
    {
        // 0: keyword/parameter/count channels, 1: color-pyramid binding channel.
        _LeakProbeMode ("Leak Probe Mode", Float) = 0
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
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fragment _ _SCREEN_SPACE_REFRACTION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

            float4 _SslRefractionParams;
            int _SslRefractionProbeCount;
            TEXTURE2D(_SslRefractionColorPyramid);

            CBUFFER_START(UnityPerMaterial)
                float _LeakProbeMode;
            CBUFFER_END

            // Half alpha marks a written surface pixel; the camera clear leaves alpha at 1.
            static const float k_SurfaceMarker = 0.5;

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
            #if defined(_SCREEN_SPACE_REFRACTION)
                float keyword = 1.0;
            #else
                float keyword = 0.0;
            #endif

                if (_LeakProbeMode > 0.5)
                {
                    float3 pyramid = SAMPLE_TEXTURE2D_LOD(
                        _SslRefractionColorPyramid, sampler_LinearClamp, float2(0.5, 0.5), 0.0).rgb;
                    float pyramidBound = max(pyramid.r, max(pyramid.g, pyramid.b)) > 0.001 ? 1.0 : 0.0;
                    return float4(pyramidBound, 0.0, 0.0, k_SurfaceMarker);
                }

                float parameters = any(abs(_SslRefractionParams) > 1e-4) ? 1.0 : 0.0;
                float probeCount = _SslRefractionProbeCount > 0 ? 1.0 : 0.0;
                return float4(keyword, parameters, probeCount, k_SurfaceMarker);
            }
            ENDHLSL
        }
    }
}
