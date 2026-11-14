// Screen Space Lighting intermediate debug view.
// Samples one private pyramid level (packed offset + scale) or the previous color/depth history.
// Outputs a value that the T4 evidence reader can verify numerically as well as visually.
Shader "Hidden/Universal Render Pipeline/ScreenSpaceLighting/Debug"
{
    HLSLINCLUDE
        #pragma editor_sync_compilation
        #pragma target 3.0

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

        TEXTURE2D_X(_SslDebugTexture);
        uniform float4 _SslDebugUvScaleOffset; // {xy: scale, zw: offset}
        uniform float _SslDebugMip;
        uniform int _SslDebugOutput; // 0: raw red channel, 1: rgb, 2: sampled atlas uv

        struct Attributes
        {
            uint vertexID : SV_VertexID;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 texcoord : TEXCOORD0;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings Vert(Attributes input)
        {
            Varyings output;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
            output.texcoord = GetFullScreenTriangleTexCoord(input.vertexID);
            return output;
        }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always Blend Off

        Pass
        {
            Name "SSLDebugView"
            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragDebug

                float4 FragDebug(Varyings input) : SV_Target
                {
                    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                    float2 uv = input.texcoord * _SslDebugUvScaleOffset.xy + _SslDebugUvScaleOffset.zw;
                    float4 value = SAMPLE_TEXTURE2D_X_LOD(_SslDebugTexture, sampler_LinearClamp, uv, _SslDebugMip);

                    if (_SslDebugOutput == 2)
                        return float4(uv, 0.0, 1.0);
                    if (_SslDebugOutput == 1)
                        return float4(value.rgb, 1.0);
                    return float4(value.rrr, 1.0);
                }
            ENDHLSL
        }

        // 1 - Camera depth source, used to prove the depth pyramid input is valid before the atlas
        // is written. The global is bound by URP; the pass does not use a property block texture.
        Pass
        {
            Name "SSLCameraDepthView"
            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragCameraDepth
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

                float4 FragCameraDepth(Varyings input) : SV_Target
                {
                    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                    float depth = SampleSceneDepth(input.texcoord);
                    return float4(depth.rrr, 1.0);
                }
            ENDHLSL
        }
    }
    Fallback Off
}