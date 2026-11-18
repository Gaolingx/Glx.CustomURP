// Editor-only camera motion vectors for cameras that URP's MotionVectorRenderPass skips (those
// without a UniversalAdditionalCameraData, including Scene View). Shared by GTAO and Screen Space
// Lighting so both temporal effects reproject from the same camera motion.
Shader "Hidden/Universal Render Pipeline/ScreenSpaceCommon/EditorCameraMotion"
{
    HLSLINCLUDE
        #pragma editor_sync_compilation
        #pragma target 3.0

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

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
            Name "Editor Camera Motion Vectors"
            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragEditorCameraMotion

                float4 FragEditorCameraMotion(Varyings input) : SV_Target
                {
                    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                    float2 uv = input.texcoord;
                    float depth = SampleSceneDepth(uv);

                #if !UNITY_REVERSED_Z
                    depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
                #endif

                    float3 posWS = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                    float4 posCS = mul(_NonJitteredViewProjMatrix, float4(posWS, 1.0));
                    float4 prevPosCS = mul(_PrevViewProjMatrix, float4(posWS, 1.0));

                    float2 posNDC = posCS.xy * rcp(posCS.w);
                    float2 prevPosNDC = prevPosCS.xy * rcp(prevPosCS.w);

                    float2 velocity = posNDC - prevPosNDC;

                #if UNITY_UV_STARTS_AT_TOP
                    velocity.y = -velocity.y;
                #endif

                    // Convert NDC (-1..1) to screen UV (0..1) offsets, matching CameraMotionVectors.shader.
                    velocity *= 0.5;
                    return float4(velocity, 0.0, 0.0);
                }
            ENDHLSL
        }
    }
    Fallback Off
}
