Shader "Hidden/Universal Render Pipeline/Contact Shadow Debug"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ContactShadows.hlsl"

            struct Attributes { uint vertexID : SV_VertexID; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            int _ContactShadowDebugMode, _ContactShadowDebugSlot;
            float4 _ContactShadowScreenSize;

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                uint2 size = (uint2)_ContactShadowScreenSize.xy;
                uint2 pixel = min((uint2)(saturate(input.uv) * size), size - 1u);
                uint packed = ContactShadowLoad(pixel);
                uint mask = packed & 0xffffffu;
                float fade = (packed >> 24) / 255.0;
                if (_ContactShadowDebugMode == 4) // Raw: little-endian bytes in RGBA.
                    return float4(mask & 255u, (mask >> 8) & 255u, mask >> 16, packed >> 24) / 255.0;
                float value = _ContactShadowDebugMode == 2 ? fade :
                    (_ContactShadowDebugMode == 3 ? ((mask >> _ContactShadowDebugSlot) & 1u) : (mask != 0u ? 1u : 0u));
                return float4(value.xxx, 1.0);
            }
            ENDHLSL
        }
    }
}
