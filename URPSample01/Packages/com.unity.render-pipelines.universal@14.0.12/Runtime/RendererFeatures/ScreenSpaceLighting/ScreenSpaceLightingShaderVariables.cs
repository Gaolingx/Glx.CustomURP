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
        public Vector4 _SsgiTemporalParams;                  // trace resolution multiplier, inverse, denoise filter radius, second pass flag
        public Vector4 _SsgiDenoiserParams;                  // pixel spread tangent, jitter frame period, half resolution filter, unused
        public Vector4 _SsgiCameraPositionWS;                // current camera world space position
        public Matrix4x4 _SsgiPrevInvViewProj;               // previous inverse non-jittered view projection
    }
}