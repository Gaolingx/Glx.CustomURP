Shader "Hidden/T3b/Raster"
{
    Properties
    {
        [HideInInspector] _T3bMode("Diagnostic mode", Integer) = 16
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        _Cutoff("Cutoff", Range(0,1)) = 0.5
        _Metallic("Metallic", Range(0,1)) = 0
        _Smoothness("Smoothness", Range(0,1)) = 0.5
        _MetallicGlossMap("Metallic Gloss Map", 2D) = "white" {}
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 1
        _EmissionMap("Emission Map", 2D) = "white" {}
        [HDR] _EmissionColor("Emission Color", Color) = (0,0,0,0)
    }
    SubShader
    {
        Pass
        {
            Cull Off ZWrite On ZTest LEqual Blend Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ _NORMALMAP
            #pragma multi_compile_local_fragment _ _ALPHATEST_ON
            #pragma multi_compile_local_fragment _ _METALLICSPECGLOSSMAP
            #pragma multi_compile_local_fragment _ _EMISSION
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"

            int _T3bMode;
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                half4 tangentWS : TEXCOORD2;
            };

            Varyings Vert(float3 positionOS : POSITION, float3 normalOS : NORMAL,
                          float4 tangentOS : TANGENT, float2 uv : TEXCOORD0)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(positionOS);
                o.uv = TRANSFORM_TEX(uv, _BaseMap);
                VertexNormalInputs n = GetVertexNormalInputs(normalOS, tangentOS);
                o.normalWS = n.normalWS;
                o.tangentWS = half4(n.tangentWS, tangentOS.w * GetOddNegativeScale());
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                SurfaceData s;
                InitializeStandardLitSurfaceData(i.uv, s);
                if (_T3bMode == 16) return float4(s.albedo, 1);
                if (_T3bMode == 18) return float4(s.metallic, s.smoothness, s.alpha, 1);
                if (_T3bMode == 19) return float4(s.emission, 1);
                half3 normalWS = i.normalWS;
                #if defined(_NORMALMAP)
                    float3 bitangent = i.tangentWS.w * cross(i.normalWS, i.tangentWS.xyz);
                    normalWS = TransformTangentToWorld(s.normalTS, half3x3(i.tangentWS.xyz, bitangent, i.normalWS));
                #endif
                return float4(NormalizeNormalPerPixel(normalWS), 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
