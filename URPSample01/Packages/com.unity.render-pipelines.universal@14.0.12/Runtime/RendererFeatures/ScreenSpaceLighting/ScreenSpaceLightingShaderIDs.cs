using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    internal static class ScreenSpaceLightingShaderIDs
    {
        internal static readonly int DepthMipChain = Shader.PropertyToID("_SslDepthMipChain");
        internal static readonly int SrcOffsetAndLimit = Shader.PropertyToID("_SslSrcOffsetAndLimit");
        internal static readonly int DstOffset = Shader.PropertyToID("_SslDstOffset");
        internal static readonly int DstSize = Shader.PropertyToID("_SslDstSize");

        internal static readonly int Source = Shader.PropertyToID("_SslSource");
        internal static readonly int SrcScaleBias = Shader.PropertyToID("_SslSrcScaleBias");
        internal static readonly int SrcUvLimits = Shader.PropertyToID("_SslSrcUvLimits");
        internal static readonly int SourceMip = Shader.PropertyToID("_SslSourceMip");

        internal static readonly int DebugTexture = Shader.PropertyToID("_SslDebugTexture");
        internal static readonly int DebugUvScaleOffset = Shader.PropertyToID("_SslDebugUvScaleOffset");
        internal static readonly int DebugMip = Shader.PropertyToID("_SslDebugMip");
        internal static readonly int DebugOutput = Shader.PropertyToID("_SslDebugOutput");

        internal static readonly string XrArrayKeyword = "SSL_XR_ARRAY";

        // ---------------------------------------------------------------------------------------
        // Screen Space Global Illumination
        // ---------------------------------------------------------------------------------------

        // Inputs.
        internal static readonly int SsgiDepthTexture = Shader.PropertyToID("_SsgiDepthTexture");
        internal static readonly int SsgiNormalTexture = Shader.PropertyToID("_SsgiNormalTexture");
        internal static readonly int SsgiMotionTexture = Shader.PropertyToID("_SsgiMotionTexture");
        internal static readonly int SsgiColorPyramid = Shader.PropertyToID("_SsgiColorPyramid");
        internal static readonly int SsgiPreviousDepthTexture = Shader.PropertyToID("_SsgiPreviousDepthTexture");
        internal static readonly int SsgiDepthPyramid = Shader.PropertyToID("_SsgiDepthPyramid");
        internal static readonly int SsgiDepthPyramidMipLevelOffsets = Shader.PropertyToID("_SsgiDepthPyramidMipLevelOffsets");

        // Trace / reproject.
        internal static readonly int SsgiHitPointTextureRW = Shader.PropertyToID("_SsgiHitPointTextureRW");
        internal static readonly int SsgiProjectionTextureRW = Shader.PropertyToID("_SsgiProjectionTextureRW");

        // Temporal filter.
        internal static readonly int SsgiValidationTextureRW = Shader.PropertyToID("_SsgiValidationTextureRW");
        internal static readonly int SsgiValidationTexture = Shader.PropertyToID("_SsgiValidationTexture");
        internal static readonly int SsgiDenoiseInputTexture = Shader.PropertyToID("_SsgiDenoiseInputTexture");
        internal static readonly int SsgiHistoryColorTexture = Shader.PropertyToID("_SsgiHistoryColorTexture");
        internal static readonly int SsgiHistorySampleCountTexture = Shader.PropertyToID("_SsgiHistorySampleCountTexture");
        internal static readonly int SsgiAccumulationTextureRW = Shader.PropertyToID("_SsgiAccumulationTextureRW");
        internal static readonly int SsgiOutputHistoryTextureRW = Shader.PropertyToID("_SsgiOutputHistoryTextureRW");
        internal static readonly int SsgiOutputSampleCountTextureRW = Shader.PropertyToID("_SsgiOutputSampleCountTextureRW");

        // Diffuse denoiser.
        internal static readonly int SsgiDenoiseOutputTextureRW = Shader.PropertyToID("_SsgiDenoiseOutputTextureRW");
        internal static readonly int SsgiPointDistributionRW = Shader.PropertyToID("_SsgiPointDistributionRW");

        // Upsample.
        internal static readonly int SsgiLowResolutionTexture = Shader.PropertyToID("_SsgiLowResolutionTexture");
        internal static readonly int SsgiUpsampledTextureRW = Shader.PropertyToID("_SsgiUpsampledTextureRW");

        // Constant buffer members.
        internal static readonly int SsgiScreenSize = Shader.PropertyToID("_SsgiScreenSize");
        internal static readonly int SsgiTraceSize = Shader.PropertyToID("_SsgiTraceSize");
        internal static readonly int SsgiRayMarchingParams = Shader.PropertyToID("_SsgiRayMarchingParams");
        internal static readonly int SsgiRayMarchingParams2 = Shader.PropertyToID("_SsgiRayMarchingParams2");
        internal static readonly int SsgiZBufferParams = Shader.PropertyToID("_SsgiZBufferParams");
        internal static readonly int SsgiColorPyramidScaleAndLimitPrev = Shader.PropertyToID("_SsgiColorPyramidScaleAndLimitPrev");
        internal static readonly int SsgiTemporalParams = Shader.PropertyToID("_SsgiTemporalParams");
        internal static readonly int SsgiDenoiserParams = Shader.PropertyToID("_SsgiDenoiserParams");
        internal static readonly int SsgiCameraPositionWS = Shader.PropertyToID("_SsgiCameraPositionWS");
        internal static readonly int SsgiPrevInvViewProj = Shader.PropertyToID("_SsgiPrevInvViewProj");

        // Material-facing result consumed by the Core GlobalIllumination blend.
        internal static readonly int ScreenSpaceGlobalIlluminationTexture = Shader.PropertyToID("_ScreenSpaceGiTexture");
        internal static readonly int ScreenSpaceGlobalIlluminationParams = Shader.PropertyToID("_ScreenSpaceGiParams");
        internal static readonly string ScreenSpaceGlobalIlluminationKeyword = "_SCREEN_SPACE_GI";

        internal static readonly string SsgiHalfResKeyword = "SSGI_HALF_RES";
        internal static readonly string SsgiNormalsOctKeyword = "SSGI_NORMALS_OCT";

        // ---------------------------------------------------------------------------------------
        // Opaque Screen Space Reflections (Approximation)
        // ---------------------------------------------------------------------------------------

        // Inputs.
        internal static readonly int SsrDepthTexture = Shader.PropertyToID("_SsrDepthTexture");
        internal static readonly int SsrNormalTexture = Shader.PropertyToID("_SsrNormalTexture");
        internal static readonly int SsrMotionTexture = Shader.PropertyToID("_SsrMotionTexture");
        internal static readonly int SsrColorPyramid = Shader.PropertyToID("_SsrColorPyramid");

        // Trace / reproject.
        internal static readonly int SsrHitPointTextureRW = Shader.PropertyToID("_SsrHitPointTextureRW");
        internal static readonly int SsrOutputTextureRW = Shader.PropertyToID("_SsrOutputTextureRW");

        // Constant buffer members.
        internal static readonly int SsrScreenSize = Shader.PropertyToID("_SsrScreenSize");
        internal static readonly int SsrRayMarchingParams = Shader.PropertyToID("_SsrRayMarchingParams");
        internal static readonly int SsrRoughnessFadeParams = Shader.PropertyToID("_SsrRoughnessFadeParams");
        internal static readonly int SsrPyramidParams = Shader.PropertyToID("_SsrPyramidParams");
        internal static readonly int SsrColorPyramidScaleAndLimitPrev = Shader.PropertyToID("_SsrColorPyramidScaleAndLimitPrev");
        internal static readonly int SsrCameraPositionWS = Shader.PropertyToID("_SsrCameraPositionWS");

        // Material-facing result consumed by the Core GlobalIllumination blend.
        internal static readonly int ScreenSpaceReflectionTexture = Shader.PropertyToID("_ScreenSpaceSsrTexture");
        internal static readonly int ScreenSpaceReflectionParams = Shader.PropertyToID("_ScreenSpaceSsrParams");
        internal static readonly string ScreenSpaceReflectionKeyword = "_SCREEN_SPACE_SSR";

        internal static readonly string SsrNormalsOctKeyword = "SSR_NORMALS_OCT";
    }
}