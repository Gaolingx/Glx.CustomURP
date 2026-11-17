using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Constant buffer shared by the Screen Space Global Illumination kernels. The layout mirrors the
    // subset of HDRP's UnityScreenSpaceGlobalIllumination and ShaderVariablesRaytracing data that the
    // migrated Trace/Reproject/Temporal/Denoise/Resolve chain needs. Camera matrices are pushed
    // explicitly so the computes never depend on global state whose owner is unclear.
    // The companion .cs.hlsl is produced by the render pipeline shader include generator and its
    // C#/HLSL layout is validated by the Screen Space Lighting EditMode tests.
    [GenerateHLSL(needAccessors = false, generateCBuffer = true)]
    internal struct ScreenSpaceLightingSsgiShaderVariables
    {
        public Vector4 _SsgiScreenSize;                      // full camera size xy, reciprocal zw
        public Vector4 _SsgiTraceSize;                       // trace size xy, reciprocal zw
        public Vector4 _SsgiRayMarchingParams;               // ray steps, thickness scale, thickness bias, reflects sky
        public Vector4 _SsgiRayMarchingParams2;              // inverse low resolution percentage, frame index, history validity, unused
        public Vector4 _SsgiZBufferParams;                   // Unity linear depth parameters
        public Vector4 _SsgiColorPyramidScaleAndLimitPrev;   // previous color pyramid uv scale xy, uv limit zw
        public Vector4 _SsgiTemporalParams;                  // trace resolution multiplier, inverse, unused, unused
        public Vector4 _SsgiDenoiserParams;                  // pixel spread tangent, unused, unused, unused
        public Vector4 _SsgiCameraPositionWS;                // current camera world space position
        public Matrix4x4 _SsgiPrevInvViewProj;               // previous inverse non-jittered view projection
    }

    // Constant buffer of the opaque SSR Approximation kernels. The layout mirrors the subset of
    // HDRP's ShaderVariablesScreenSpaceReflection the Trace/Reproject pair needs. Every value is a
    // Vector4 so pushing the buffer only requires SetComputeVectorParam.
    [GenerateHLSL(needAccessors = false, generateCBuffer = true)]
    internal struct ScreenSpaceLightingSsrShaderVariables
    {
        public Vector4 _SsrScreenSize;                       // full camera size xy, reciprocal zw
        public Vector4 _SsrTraceSize;                        // trace size xy, reciprocal zw (half the screen in the half resolution path)
        public Vector4 _SsrRayMarchingParams;                // step limit, thickness scale, thickness bias, reflects sky
        public Vector4 _SsrRoughnessFadeParams;              // roughness fade rcp length, fade end times rcp length, fade end, edge fade rcp length
        public Vector4 _SsrPyramidParams;                    // color pyramid max mip, unused, unused, unused
        public Vector4 _SsrColorPyramidScaleAndLimitPrev;    // previous color pyramid uv scale xy, uv limit zw
        public Vector4 _SsrCameraPositionWS;                 // current camera world space position
        public Vector4 _SsrPbrParams;                        // unused, sample bias, unused, unused
        public Vector4 _SsrPbrFlags;                         // frame index, valid accumulation history, unused, unused
    }
}