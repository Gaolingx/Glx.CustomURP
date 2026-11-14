// Screen Space Lighting Gaussian color pyramid.
// Ported from HDRP 14.0.12 Runtime/RenderPipeline/RenderPass/ColorPyramidPS.shader and the
// separable blur scheduling in MipGenerator.RenderColorGaussianPyramid.
// The pyramid is stored in a linear HDR render texture with a full mip chain; pass 0 downsamples,
// passes 1 and 2 apply the horizontal and vertical 9-tap Gaussian.
Shader "Hidden/Universal Render Pipeline/ScreenSpaceLighting/ColorPyramid"
{
    HLSLINCLUDE
        #pragma editor_sync_compilation
        #pragma target 3.0

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

        TEXTURE2D_X(_SslSource);
        uniform float4 _SslSrcScaleBias; // {xy: scale, zw: bias} sampling the source mip
        uniform float4 _SslSrcUvLimits;  // {xy: last valid uv, zw: blur offset for one texel}
        uniform float _SslSourceMip;

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

        // 0 - Downsample / copy
        Pass
        {
            Name "SSLDownsample"
            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragDownsample

                half4 FragDownsample(Varyings input) : SV_Target
                {
                    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                    float2 uv = input.texcoord * _SslSrcScaleBias.xy + _SslSrcScaleBias.zw;
                    return SAMPLE_TEXTURE2D_X_LOD(_SslSource, sampler_LinearClamp, uv, _SslSourceMip);
                }
            ENDHLSL
        }

        // 1 - 9-tap Gaussian along the axis described by _SslSrcUvLimits.zw
        Pass
        {
            Name "SSLGaussian"
            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragGaussian

                half4 FragGaussian(Varyings input) : SV_Target
                {
                    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                    // Keep in sync with HDRP MipGenerator color pyramid weights.
                    const half gaussWeights[] = { 0.27343750, 0.21875000, 0.10937500, 0.03125000, 0.00390625 };

                    float2 uv = input.texcoord * _SslSrcScaleBias.xy + _SslSrcScaleBias.zw;
                    half2 offset = _SslSrcUvLimits.zw;
                    half2 offset1 = offset * (1.0 + (gaussWeights[2] / (gaussWeights[1] + gaussWeights[2])));
                    half2 offset2 = offset * (3.0 + (gaussWeights[4] / (gaussWeights[3] + gaussWeights[4])));

                    half2 uv_m2 = uv - offset2;
                    half2 uv_m1 = uv - offset1;
                    half2 uv_p0 = uv;
                    half2 uv_p1 = min(_SslSrcUvLimits.xy, uv + offset1);
                    half2 uv_p2 = min(_SslSrcUvLimits.xy, uv + offset2);

                    return
                        + SAMPLE_TEXTURE2D_X_LOD(_SslSource, sampler_LinearClamp, uv_m2, _SslSourceMip) * (gaussWeights[3] + gaussWeights[4])
                        + SAMPLE_TEXTURE2D_X_LOD(_SslSource, sampler_LinearClamp, uv_m1, _SslSourceMip) * (gaussWeights[1] + gaussWeights[2])
                        + SAMPLE_TEXTURE2D_X_LOD(_SslSource, sampler_LinearClamp, uv_p0, _SslSourceMip) * gaussWeights[0]
                        + SAMPLE_TEXTURE2D_X_LOD(_SslSource, sampler_LinearClamp, uv_p1, _SslSourceMip) * (gaussWeights[1] + gaussWeights[2])
                        + SAMPLE_TEXTURE2D_X_LOD(_SslSource, sampler_LinearClamp, uv_p2, _SslSourceMip) * (gaussWeights[3] + gaussWeights[4]);
                }
            ENDHLSL
        }
    }
    Fallback Off
}