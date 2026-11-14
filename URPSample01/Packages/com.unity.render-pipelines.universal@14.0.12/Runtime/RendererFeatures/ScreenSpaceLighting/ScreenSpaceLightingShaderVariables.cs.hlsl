//
// This file was automatically generated from ScreenSpaceLightingShaderVariables.cs.
// Regenerate it with [ Edit > Rendering > Generate Shader Includes ] and do not edit it by hand.
//

#ifndef SCREENSPACELIGHTINGSHADERVARIABLES_CS_HLSL
#define SCREENSPACELIGHTINGSHADERVARIABLES_CS_HLSL

// Generated from UnityEngine.Rendering.Universal.ScreenSpaceLightingSsgiShaderVariables
// PackingRules = Exact
CBUFFER_START(ScreenSpaceLightingSsgiShaderVariables)
    float4 _SsgiScreenSize;
    float4 _SsgiTraceSize;
    float4 _SsgiRayMarchingParams;
    float4 _SsgiRayMarchingParams2;
    float4 _SsgiZBufferParams;
    float4 _SsgiColorPyramidScaleAndLimitPrev;
    float4 _SsgiTemporalParams;
    float4 _SsgiDenoiserParams;
    float4 _SsgiCameraPositionWS;
    float4x4 _SsgiPrevInvViewProj;
CBUFFER_END

// Generated from UnityEngine.Rendering.Universal.ScreenSpaceLightingSsrShaderVariables
// PackingRules = Exact
CBUFFER_START(ScreenSpaceLightingSsrShaderVariables)
    float4 _SsrScreenSize;
    float4 _SsrRayMarchingParams;
    float4 _SsrRoughnessFadeParams;
    float4 _SsrPyramidParams;
    float4 _SsrColorPyramidScaleAndLimitPrev;
    float4 _SsrCameraPositionWS;
CBUFFER_END

#endif