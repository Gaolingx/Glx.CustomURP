// Material type mask for the clustered deferred compute.
//
// Stencil deferred selects _LIT / _SIMPLELIT variants from stencil bits [5,6] and skips
// MaterialUnlit (Unlit objects and ComplexLit, see GBufferPass.cs:58-61). A compute dispatch
// cannot read depth-stencil on D3D11, so this pass converts the material type into an R8_UInt
// mask (0 = skip, 1 = Lit, 2 = SimpleLit) with two stencil-tested fullscreen draws.
//
// Pass 0: Equal MaterialLit        (0x20) -> 1
// Pass 1: Equal MaterialSimpleLit  (0x40) -> 2
// Stencil read mask 0x60 (MaterialMask), write mask 0: the stencil itself is left untouched.
Shader "Hidden/Universal/ClusteredDeferredMask"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Material Type Mask (Lit)"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            Stencil
            {
                Ref 32          // StencilUsage.MaterialLit
                ReadMask 96     // StencilUsage.MaterialMask
                WriteMask 0
                Comp Equal
                Pass Keep
                Fail Keep
                ZFail Keep
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragLit
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

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

            uint FragLit(Varyings input) : SV_Target
            {
                return 1u;
            }
            ENDHLSL
        }

        Pass
        {
            Name "Material Type Mask (SimpleLit)"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            Stencil
            {
                Ref 64          // StencilUsage.MaterialSimpleLit
                ReadMask 96     // StencilUsage.MaterialMask
                WriteMask 0
                Comp Equal
                Pass Keep
                Fail Keep
                ZFail Keep
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragSimpleLit
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

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

            uint FragSimpleLit(Varyings input) : SV_Target
            {
                return 2u;
            }
            ENDHLSL
        }
    }
}