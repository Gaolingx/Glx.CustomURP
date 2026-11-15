using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    internal static class GroundTruthAmbientOcclusionShaderIDs
    {
        // Private GTAO resources. They never alias the Screen Space Lighting pyramid or history.
        internal static readonly int DepthPyramid = Shader.PropertyToID("_GtaoDepthPyramid");
        internal static readonly int NormalTexture = Shader.PropertyToID("_GtaoNormalTexture");
        internal static readonly int MotionTexture = Shader.PropertyToID("_GtaoMotionTexture");
        internal static readonly int PackedData = Shader.PropertyToID("_GtaoPackedData");
        internal static readonly int PackedBlurred = Shader.PropertyToID("_GtaoPackedBlurred");
        internal static readonly int PackedHistory = Shader.PropertyToID("_GtaoPackedHistory");
        internal static readonly int OutputHistory = Shader.PropertyToID("_GtaoOutputHistory");
        internal static readonly int OcclusionTexture = Shader.PropertyToID("_GtaoOcclusionTexture");
        internal static readonly int InputTexture = Shader.PropertyToID("_GtaoInputTexture");
        internal static readonly int OutputTexture = Shader.PropertyToID("_GtaoOutputTexture");

        internal static readonly int DebugTexture = Shader.PropertyToID("_GtaoDebugTexture");
        internal static readonly int DebugMode = Shader.PropertyToID("_GtaoDebugMode");
        internal static readonly int DebugMip = Shader.PropertyToID("_GtaoDebugMip");
        internal static readonly int DebugUvScaleOffset = Shader.PropertyToID("_GtaoDebugUvScaleOffset");
        internal static readonly int DebugUnpack = Shader.PropertyToID("_GtaoDebugUnpack");
        internal static readonly int DebugUseGlobal = Shader.PropertyToID("_GtaoDebugUseGlobal");

        // Constant buffer members. They are declared in GroundTruthAmbientOcclusionShaderVariables.cs.hlsl.
        internal static readonly int BufferSize = Shader.PropertyToID("_GtaoBufferSize");
        internal static readonly int ScreenSize = Shader.PropertyToID("_GtaoScreenSize");
        internal static readonly int Params0 = Shader.PropertyToID("_GtaoParams0");
        internal static readonly int Params1 = Shader.PropertyToID("_GtaoParams1");
        internal static readonly int Params2 = Shader.PropertyToID("_GtaoParams2");
        internal static readonly int Params3 = Shader.PropertyToID("_GtaoParams3");
        internal static readonly int Params4 = Shader.PropertyToID("_GtaoParams4");
        internal static readonly int FirstTwoDepthMipOffsets = Shader.PropertyToID("_GtaoFirstTwoDepthMipOffsets");
        internal static readonly int DepthToViewParams = Shader.PropertyToID("_GtaoDepthToViewParams");
        internal static readonly int ZBufferParams = Shader.PropertyToID("_GtaoZBufferParams");
        internal static readonly int OrthoHalfSize = Shader.PropertyToID("_GtaoOrthoHalfSize");
        internal static readonly int DepthPyramidUvScale = Shader.PropertyToID("_GtaoDepthPyramidUvScale");
        internal static readonly int ViewRow0 = Shader.PropertyToID("_GtaoViewRow0");
        internal static readonly int ViewRow1 = Shader.PropertyToID("_GtaoViewRow1");
        internal static readonly int ViewRow2 = Shader.PropertyToID("_GtaoViewRow2");
        internal static readonly int XrViewRow0 = Shader.PropertyToID("_GtaoXrViewRow0");
        internal static readonly int XrViewRow1 = Shader.PropertyToID("_GtaoXrViewRow1");
        internal static readonly int XrViewRow2 = Shader.PropertyToID("_GtaoXrViewRow2");

        // Final material-facing AO input shared with the built-in URP AO contract.
        internal static readonly int ScreenSpaceOcclusionTexture = Shader.PropertyToID("_ScreenSpaceOcclusionTexture");
        internal static readonly int AmbientOcclusionParam = Shader.PropertyToID("_AmbientOcclusionParam");

        internal static readonly string XrArrayKeyword = "GTAO_XR_ARRAY";
        internal static readonly string OrthographicKeyword = "GTAO_ORTHO";
        internal static readonly string NormalsOctKeyword = "GTAO_NORMALS_OCT";
        internal static readonly string HalfResKeyword = "HALF_RES";
        internal static readonly string FullResKeyword = "FULL_RES";
        internal static readonly string TemporalKeyword = "TEMPORAL";
        internal static readonly string ToTemporalKeyword = "TO_TEMPORAL";
    }
}