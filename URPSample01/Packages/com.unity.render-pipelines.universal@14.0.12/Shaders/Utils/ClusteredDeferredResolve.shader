// Resolves the clustered deferred lighting UAV into GBuffer3.
//
// A compute dispatch cannot use fixed-function blending, so the kernel writes direct lighting to
// _CdLightingTexture and this fullscreen pass adds it to the lighting attachment. The blend is
// Blend One SrcAlpha, Zero One: the kernel's alpha is the SrcAlpha factor the stencil deferred
// path applies to the baked indirect lighting on its first light pass (StencilDeferred.hlsl:286-295),
// and it is 1 unless screen-space AO needs to cancel baked occlusion.
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
            Blend One SrcAlpha, Zero One

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
                return LOAD_TEXTURE2D(_CdLightingTexture, pixel);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Clustered Deferred Fog"
            ZTest NotEqual
            ZWrite Off
            Cull Off
            Blend OneMinusSrcAlpha SrcAlpha, Zero One

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex VertFog
            #pragma fragment FragFog
            #pragma multi_compile FOG_LINEAR FOG_EXP FOG_EXP2
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float4 VertFog(uint vertexID : SV_VertexID) : SV_POSITION
            {
                return GetFullScreenTriangleVertexPosition(vertexID, UNITY_RAW_FAR_CLIP_VALUE);
            }

            half4 FragFog(float4 positionCS : SV_POSITION) : SV_Target
            {
                float eyeZ = LinearEyeDepth(LoadSceneDepth(uint2(positionCS.xy)), _ZBufferParams);
                float clipZ = UNITY_MATRIX_P[2][2] * -eyeZ + UNITY_MATRIX_P[2][3];
                half fogFactor = ComputeFogFactor(clipZ);
                return half4(unity_FogColor.rgb, ComputeFogIntensity(fogFactor));
            }
            ENDHLSL
        }
    }
}