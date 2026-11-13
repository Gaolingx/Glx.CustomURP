Shader "Hidden/ScreenSpaceLighting/Probe"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            // The compatibility path binds this global in UniversalRenderer; the render graph path binds it after the pass.
            TEXTURE2D_X(_MotionVectorTexture);
            SAMPLER(sampler_MotionVectorTexture);

            float _ProbeMode;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = UnityStereoTransformScreenSpaceTex(input.uv);
                float rawDepth = SampleSceneDepth(input.uv);
                float linearDepth01 = Linear01Depth(rawDepth, _ZBufferParams);
                float3 normalWS = SampleSceneNormals(input.uv);
                float4 normalSample = SAMPLE_TEXTURE2D_X(_CameraNormalsTexture, sampler_CameraNormalsTexture, uv);
                float2 motion = SAMPLE_TEXTURE2D_X(_MotionVectorTexture, sampler_MotionVectorTexture, uv).rg;

                // Mode 0: depth01, motion.xy, encoded smoothness alpha.
                // Mode 1: decoded normal (encoded as 0.5 + 0.5 * n) and 1 in alpha when a normal is present.
                // Mode 2: constant alpha, verifies the probe target stores and returns alpha.
                // Mode 3: raw _CameraNormalsTexture RGBA, no decoding.
                if (_ProbeMode > 2.5)
                    return normalSample;
                if (_ProbeMode > 1.5)
                    return float4(0.1, 0.2, 0.3, 0.25);
                if (_ProbeMode > 0.5)
                {
                    float normalLength = length(normalWS);
                    float3 encodedNormal = normalLength > 1e-3 ? normalWS * 0.5 + 0.5 : 0.0;
                    return float4(encodedNormal, normalLength > 1e-3 ? 1.0 : 0.0);
                }

                return float4(linearDepth01, motion, normalSample.a);
            }
            ENDHLSL
        }
    }
}
