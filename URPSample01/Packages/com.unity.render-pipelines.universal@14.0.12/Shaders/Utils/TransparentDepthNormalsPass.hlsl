#ifndef UNIVERSAL_TRANSPARENT_DEPTH_NORMALS_PASS_INCLUDED
#define UNIVERSAL_TRANSPARENT_DEPTH_NORMALS_PASS_INCLUDED

// Shared implementation of the UniversalTransparentDepthNormals light mode.
//
// The Screen Space Lighting transparent prepass draws transparent geometry into a private
// depth/normal target that is initialized from the opaque camera depth. The pass therefore never
// changes the depth test or depth write semantics of the main transparent pass.
//
// Only materials that explicitly opt in with _TRANSPARENT_SSR_RECEIVE are allowed to contribute a
// ray origin. An unrelated transparent surface must not become the reflection origin of an opted-in
// transparent object that is blended behind it, so the fragment is discarded otherwise.
//
// The depth/smoothness encoding is exactly the opaque DepthNormals contract: alpha is
// 0.5 + 0.5 * smoothness and 0 means "material does not provide SSR data".
#include "Packages/com.unity.render-pipelines.universal/Shaders/LitDepthNormalsPass.hlsl"

Varyings TransparentDepthNormalsVertex(Attributes input)
{
    return DepthNormalsVertex(input);
}

void TransparentDepthNormalsFragment(Varyings input, out half4 outNormalWS : SV_Target0)
{
#if defined(_TRANSPARENT_SSR_RECEIVE)
    DepthNormalsFragment(input, outNormalWS);
#else
    clip(-1.0);
    outNormalWS = half4(0.0, 0.0, 0.0, 0.0);
#endif
}

#endif