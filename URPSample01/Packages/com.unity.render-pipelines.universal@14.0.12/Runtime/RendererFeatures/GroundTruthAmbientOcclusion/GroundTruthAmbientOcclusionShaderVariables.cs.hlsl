//
// This file was automatically generated from GroundTruthAmbientOcclusionShaderVariables.cs.
// Regenerate it with [ Edit > Rendering > Generate Shader Includes ] and do not edit it by hand.
//

#ifndef GROUNDTRUTHAMBIENTOCCLUSIONSHADERVARIABLES_CS_HLSL
#define GROUNDTRUTHAMBIENTOCCLUSIONSHADERVARIABLES_CS_HLSL

// Generated from UnityEngine.Rendering.Universal.GroundTruthAmbientOcclusionShaderVariables
// PackingRules = Exact
CBUFFER_START(GroundTruthAmbientOcclusionShaderVariables)
    float4 _GtaoBufferSize;
    float4 _GtaoScreenSize;
    float4 _GtaoParams0;
    float4 _GtaoParams1;
    float4 _GtaoParams2;
    float4 _GtaoParams3;
    float4 _GtaoParams4;
    float4 _GtaoFirstTwoDepthMipOffsets;
    float4 _GtaoDepthToViewParams;
    float4 _GtaoZBufferParams;
    float4 _GtaoOrthoHalfSize;
    float4 _GtaoOrthoDepthParams;
    float4 _GtaoViewRow0;
    float4 _GtaoViewRow1;
    float4 _GtaoViewRow2;
CBUFFER_END

#endif