Shader "ContactShadowTests/DirectMainLight"
{
    Properties { _BaseColor("Base Color", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
        CBUFFER_END
        struct Attributes { float3 positionOS : POSITION; half3 normalOS : NORMAL; };
        struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; half3 normalWS : TEXCOORD1; };
        Varyings Vert(Attributes v)
        {
            Varyings o;
            o.positionWS = TransformObjectToWorld(v.positionOS);
            o.positionCS = TransformWorldToHClip(o.positionWS);
            o.normalWS = TransformObjectToWorldNormal(v.normalOS);
            return o;
        }
        ENDHLSL
        Pass
        {
            Name "DirectMainLight"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            half4 Frag(Varyings v) : SV_Target
            {
                // Intentionally bypass the built-in Lit fragment path (T7 uncovered-material control).
                Light light = GetMainLight(TransformWorldToShadowCoord(v.positionWS));
                half nl = saturate(dot(normalize(v.normalWS), light.direction));
                return half4(_BaseColor.rgb * (0.15h + light.color * nl * light.shadowAttenuation * light.distanceAttenuation), 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDepth
            float FragDepth(Varyings v) : SV_Target { return v.positionCS.z; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode"="DepthNormalsOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragNormal
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            half4 FragNormal(Varyings v) : SV_Target
            {
                float3 n = normalize(v.normalWS);
                #if defined(_GBUFFER_NORMALS_OCT)
                    return half4(PackFloat2To888(saturate(PackNormalOctQuadEncode(n) * 0.5 + 0.5)), 0);
                #else
                    return half4(n, 0);
                #endif
            }
            ENDHLSL
        }
    }
}
