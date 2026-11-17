using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Constant buffer shared by the GTAO kernels. The parameter packing follows HDRP's
    // ShaderVariablesAmbientOcclusion with URP-specific bindings appended; the companion .cs.hlsl is
    // produced by the render pipeline shader include generator and the layout is pinned by the GTAO
    // EditMode tests.
    [GenerateHLSL(needAccessors = false, generateCBuffer = true)]
    internal struct GroundTruthAmbientOcclusionShaderVariables
    {
        public Vector4 _GtaoBufferSize;             // running AO size xy, reciprocal zw
        public Vector4 _GtaoScreenSize;             // full camera size xy, reciprocal zw
        public Vector4 _GtaoParams0;                // x unused, fovCorrection, radius, stepCount
        public Vector4 _GtaoParams1;                // intensity, 1/radius^2, temporalOffsetIdx, temporalRotationIdx
        public Vector4 _GtaoParams2;                // historySize.xy, 1/(stepCount+1), maximumRadiusInPixels
        public Vector4 _GtaoParams3;                // blurTolerance, upsampleTolerance, noiseFilterStrength, stepSize
        public Vector4 _GtaoParams4;                // directionCount, upperNudge, lowerNudge, spatialBilateralAggressiveness
        public Vector4 _GtaoFirstTwoDepthMipOffsets; // packed depth pyramid mip1 xy, mip2 zw
        public Vector4 _GtaoDepthToViewParams;      // perspective screen position to view position
        public Vector4 _GtaoZBufferParams;          // Unity perspective linear depth parameters
        public Vector4 _GtaoOrthoHalfSize;          // orthographic camera half extents
        public Vector4 _GtaoOrthoDepthParams;       // orthographic window depth scale, bias, unused, unused
        public Vector4 _GtaoViewRow0;               // world to view matrix rows (rotation part only)
        public Vector4 _GtaoViewRow1;
        public Vector4 _GtaoViewRow2;
    }
}