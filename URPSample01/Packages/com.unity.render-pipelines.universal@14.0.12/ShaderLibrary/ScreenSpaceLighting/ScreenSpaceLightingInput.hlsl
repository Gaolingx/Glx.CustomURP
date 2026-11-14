#ifndef UNIVERSAL_SCREEN_SPACE_LIGHTING_INPUT_INCLUDED
#define UNIVERSAL_SCREEN_SPACE_LIGHTING_INPUT_INCLUDED

// Core-owned input for the Screen Space Lighting plugin. The plugin binds the final SSGI radiance
// with an enabled keyword; when the keyword is off this header declares nothing that changes the
// original URP lighting path.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

TEXTURE2D_X(_ScreenSpaceGiTexture);
// x: weight scale (volume intensity), y..w: reserved, always zero
float4 _ScreenSpaceGiParams;

TEXTURE2D_X(_ScreenSpaceSsrTexture);
// x: weight scale (volume intensity), y..w: reserved, always zero
float4 _ScreenSpaceSsrParams;

// Transparent reflections use their own result: the opaque trace never covers transparent ray
// origins, and the opaque material keyword is disabled before transparents are drawn.
TEXTURE2D_X(_ScreenSpaceTransparentSsrTexture);
// x: weight scale (volume intensity), y..w: reserved, always zero
float4 _ScreenSpaceTransparentSsrParams;

#endif