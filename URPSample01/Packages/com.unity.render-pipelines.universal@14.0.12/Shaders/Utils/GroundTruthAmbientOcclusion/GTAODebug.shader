// GTAO intermediate debug view: final AO visibility or one packed depth pyramid level.
Shader "Hidden/Universal Render Pipeline/GroundTruthAmbientOcclusion/Debug"
{
    HLSLINCLUDE
        #pragma editor_sync_compilation
        #pragma target 3.0

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

        TEXTURE2D_X(_GtaoDebugTexture);
        TEXTURE2D_X(_ScreenSpaceOcclusionTexture);
        uniform float4 _GtaoDebugUvScaleOffset; // {xy: scale, zw: offset}
        uniform int _GtaoDebugUnpack;           // 1: value is a packed AO/depth pair
        uniform int _GtaoDebugUseGlobal;        // 1: sample the material facing global binding

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
            Name "GTAODebugView"
            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragDebug

                float4 FragDebug(Varyings input) : SV_Target
                {
                    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                    float2 uv = input.texcoord * _GtaoDebugUvScaleOffset.xy + _GtaoDebugUvScaleOffset.zw;
                    float value = _GtaoDebugUseGlobal != 0
                        ? SAMPLE_TEXTURE2D_X(_ScreenSpaceOcclusionTexture, sampler_LinearClamp, input.texcoord).r
                        : SAMPLE_TEXTURE2D_X_LOD(_GtaoDebugTexture, sampler_LinearClamp, uv, 0).r;
                    if (_GtaoDebugUnpack != 0)
                    {
                        // The packed value stores 23 bits of depth below 8 bits of AO.
                        value = saturate(UnpackUIntToFloat(asuint(value), 24, 8));
                    }
                    return float4(value.rrr, 1.0);
                }
            ENDHLSL
        }
    }
    Fallback Off
}