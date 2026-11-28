#ifndef UNIVERSAL_SCREEN_SPACE_LIGHTING_INPUT_INCLUDED
#define UNIVERSAL_SCREEN_SPACE_LIGHTING_INPUT_INCLUDED

// Core-owned input for the Screen Space Lighting plugin. The plugin binds the final SSGI radiance
// with an enabled keyword; when the keyword is off this header declares nothing that changes the
// original URP lighting path.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

TEXTURE2D_X(_ScreenSpaceGiTexture);
// x: weight scale (volume intensity), y..w: reserved, always zero
float4 _ScreenSpaceGiParams;

// Dynamic probe fallback (GI). x: probe-volume coverage/validity c in [0,1], zero when the
// fallback is off or no probe generation has been published; y..w reserved.
float4 _ScreenSpaceGiProbeParams;

TEXTURE2D_X(_ScreenSpaceSsrTexture);
// x: weight scale (volume intensity), y..w: reserved, always zero
float4 _ScreenSpaceSsrParams;

// 1 when the 225 composite owns the deferred receiver's indirect specular. The Lit GBuffer fragment then writes
// only the indirect-diffuse term into GBuffer3 and leaves indirect specular to the 225 composite;
// the forward path ignores this and keeps the material-side blend. Set before the GBuffer pass.
half _GlxSsrAfterGbuffer;

// Transparent reflections use their own result: the opaque trace never covers transparent ray
// origins, and the opaque material keyword is disabled before transparents are drawn.
TEXTURE2D_X(_ScreenSpaceTransparentSsrTexture);
// x: weight scale (volume intensity), y..w: reserved, always zero
float4 _ScreenSpaceTransparentSsrParams;

#endif