// Resolves the clustered deferred lighting UAV into GBuffer3.
//
// A compute dispatch cannot use fixed-function blending, so the kernel writes direct lighting to
// _CdLightingTexture and this fullscreen pass adds it to the lighting attachment with
// Blend One One, Zero One (same semantics as the stencil deferred punctual pass).
Shader "Hidden/Universal/ClusteredDeferredResolve"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Clustered Deferred Resolve"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend One One, Zero One

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_CdLightingTexture);

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                uint2 pixel = (uint2)input.positionCS.xy;
                return half4(LOAD_TEXTURE2D(_CdLightingTexture, pixel).rgb, 0.0);
            }
            ENDHLSL
        }
    }
}