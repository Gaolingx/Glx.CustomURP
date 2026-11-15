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

        // Per-view matrices and eye centres. Only bound for single-pass instanced XR, where the
        // kernels pick the view with the dispatch slice.
        internal static readonly int SslXrViewProj = Shader.PropertyToID("_SslXrViewProj");
        internal static readonly int SslXrInvViewProj = Shader.PropertyToID("_SslXrInvViewProj");
        internal static readonly int SslXrPrevViewProj = Shader.PropertyToID("_SslXrPrevViewProj");
        internal static readonly int SslXrPrevInvViewProj = Shader.PropertyToID("_SslXrPrevInvViewProj");
        internal static readonly int SslXrCameraPositionWS = Shader.PropertyToID("_SslXrCameraPositionWS");

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

        // Denoising pass inputs (HDRP TemporalFilter / DiffuseDenoiser parameter names).
        internal static readonly int DenoiseInputTexture = Shader.PropertyToID("_DenoiseInputTexture");
        internal static readonly int DenoiseOutputTextureRW = Shader.PropertyToID("_DenoiseOutputTextureRW");
        internal static readonly int DenoiseDepthTexture = Shader.PropertyToID("_DepthTexture");
        internal static readonly int DenoiseNormalTexture = Shader.PropertyToID("_NormalBufferTexture");
        internal static readonly int DenoiseMotionTexture = Shader.PropertyToID("_CameraMotionVectorsTexture");
        internal static readonly int HistoryDepthTexture = Shader.PropertyToID("_HistoryDepthTexture");
        internal static readonly int HistoryNormalTexture = Shader.PropertyToID("_HistoryNormalTexture");
        internal static readonly int HistoryBuffer = Shader.PropertyToID("_HistoryBuffer");
        internal static readonly int HistorySampleCountTexture = Shader.PropertyToID("_HistorySampleCount");
        internal static readonly int HistorySampleCountRW = Shader.PropertyToID("_HistorySampleCountRW");
        internal static readonly int DenoiseSampleCountRW = Shader.PropertyToID("_SampleCountTextureRW");
        internal static readonly int ReflectionFilterMapping = Shader.PropertyToID("_ReflectionFilterMapping");
        internal static readonly int ValidationBuffer = Shader.PropertyToID("_ValidationBuffer");
        internal static readonly int ValidationBufferRW = Shader.PropertyToID("_ValidationBufferRW");
        internal static readonly int AccumulationOutputTextureRW = Shader.PropertyToID("_AccumulationOutputTextureRW");
        internal static readonly int PointDistribution = Shader.PropertyToID("_PointDistribution");
        internal static readonly int PointDistributionRW = Shader.PropertyToID("_PointDistributionRW");

        // Denoising pass scalars (bound per stage like HDRP).
        internal static readonly int DenoiserFilterRadius = Shader.PropertyToID("_DenoiserFilterRadius");
        internal static readonly int JitterFramePeriod = Shader.PropertyToID("_JitterFramePeriod");
        internal static readonly int HalfResolutionFilter = Shader.PropertyToID("_HalfResolutionFilter");
        internal static readonly int PixelSpreadAngleTangent = Shader.PropertyToID("_PixelSpreadAngleTangent");
        internal static readonly int ReceiverMotionRejection = Shader.PropertyToID("_ReceiverMotionRejection");
        internal static readonly int OccluderMotionRejection = Shader.PropertyToID("_OccluderMotionRejection");
        internal static readonly int EnableExposureControl = Shader.PropertyToID("_EnableExposureControl");
        internal static readonly int PreviousExposureMultiplier = Shader.PropertyToID("_PreviousExposureMultiplier");
        internal static readonly int CurrentExposureMultiplier = Shader.PropertyToID("_CurrentExposureMultiplier");
        internal static readonly int AffectSmoothSurfaces = Shader.PropertyToID("_AffectSmoothSurfaces");
        internal static readonly int SingleReflectionBounce = Shader.PropertyToID("_SingleReflectionBounce");
        internal static readonly int RoughnessBasedDenoising = Shader.PropertyToID("_RoughnessBasedDenoising");
        internal static readonly int RaytracingReflectionMinSmoothness = Shader.PropertyToID("_RaytracingReflectionMinSmoothness");

        // URP extension: captures the camera normals into the temporal history.
        internal static readonly int SsgiSourceNormalTexture = Shader.PropertyToID("_SsgiSourceNormalTexture");
        internal static readonly int SsgiOutputHistoryNormalTextureRW = Shader.PropertyToID("_SsgiOutputHistoryNormalTextureRW");
        internal static readonly int SsgiHistoryCopySize = Shader.PropertyToID("_SsgiHistoryCopySize");

        // Upsample.
        internal static readonly int SsgiLowResolutionTexture = Shader.PropertyToID("_SsgiLowResolutionTexture");
        internal static readonly int SsgiUpsampledTextureRW = Shader.PropertyToID("_SsgiUpsampledTextureRW");

        // Miss fallback and blue-noise sequence resources (globals, not constant buffer members).
        internal static readonly int SsgiFallbackParams = Shader.PropertyToID("_SsgiFallbackParams");
        internal static readonly int SsgiAmbientProbeSH = Shader.PropertyToID("_SsgiAmbientProbeSH");
        internal static readonly int SsgiReflectionProbeCubemap = Shader.PropertyToID("_SsgiReflectionProbeCubemap");
        internal static readonly int SsgiReflectionProbeHDRDecode = Shader.PropertyToID("_SsgiReflectionProbeHDRDecode");
        internal static readonly int SsgiOwenScrambledTexture = Shader.PropertyToID("_SsgiOwenScrambledTexture");
        internal static readonly int SsgiScramblingTileXSPP = Shader.PropertyToID("_SsgiScramblingTileXSPP");
        internal static readonly int SsgiRankingTileXSPP = Shader.PropertyToID("_SsgiRankingTileXSPP");

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
        internal static readonly string SsgiBlueNoiseKeyword = "SSGI_BLUE_NOISE";

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

        // PBR reproject output, consumed by the reflection denoiser.
        internal static readonly int SsrReprojectTextureRW = Shader.PropertyToID("_SsrReprojectTextureRW");

        // Constant buffer members.
        internal static readonly int SsrScreenSize = Shader.PropertyToID("_SsrScreenSize");
        internal static readonly int SsrRayMarchingParams = Shader.PropertyToID("_SsrRayMarchingParams");
        internal static readonly int SsrRoughnessFadeParams = Shader.PropertyToID("_SsrRoughnessFadeParams");
        internal static readonly int SsrPyramidParams = Shader.PropertyToID("_SsrPyramidParams");
        internal static readonly int SsrColorPyramidScaleAndLimitPrev = Shader.PropertyToID("_SsrColorPyramidScaleAndLimitPrev");
        internal static readonly int SsrCameraPositionWS = Shader.PropertyToID("_SsrCameraPositionWS");
        internal static readonly int SsrPbrParams = Shader.PropertyToID("_SsrPbrParams");
        internal static readonly int SsrPbrFlags = Shader.PropertyToID("_SsrPbrFlags");

        // Material-facing result consumed by the Core GlobalIllumination blend.
        internal static readonly int ScreenSpaceReflectionTexture = Shader.PropertyToID("_ScreenSpaceSsrTexture");
        internal static readonly int ScreenSpaceReflectionParams = Shader.PropertyToID("_ScreenSpaceSsrParams");
        internal static readonly string ScreenSpaceReflectionKeyword = "_SCREEN_SPACE_SSR";

        internal static readonly string SsrNormalsOctKeyword = "SSR_NORMALS_OCT";
        internal static readonly string SsrApproxKeyword = "SSR_APPROX";

        // ---------------------------------------------------------------------------------------
        // Transparent Screen Space Reflections
        // ---------------------------------------------------------------------------------------

        internal static readonly int CameraDepthAttachment = Shader.PropertyToID("_CameraDepthAttachment");
        internal const string OutputDepthKeyword = "_OUTPUT_DEPTH";

        internal static readonly int ScreenSpaceTransparentReflectionTexture = Shader.PropertyToID("_ScreenSpaceTransparentSsrTexture");
        internal static readonly int ScreenSpaceTransparentReflectionParams = Shader.PropertyToID("_ScreenSpaceTransparentSsrParams");
        internal static readonly string ScreenSpaceTransparentReflectionKeyword = "_SCREEN_SPACE_TRANSPARENT_SSR";

        // ---------------------------------------------------------------------------------------
        // Screen Space Refraction
        // ---------------------------------------------------------------------------------------

        // The current frame color pyramid (opaque + sky, generated before transparents) and the
        // opaque depth used to reject offsets that would sample foreground geometry.
        internal static readonly int RefractionColorPyramid = Shader.PropertyToID("_SslRefractionColorPyramid");
        internal static readonly int RefractionDepthTexture = Shader.PropertyToID("_SslRefractionDepthTexture");
        internal static readonly int RefractionColorPyramidScaleAndLimit = Shader.PropertyToID("_SslRefractionColorPyramidScaleAndLimit");
        internal static readonly int RefractionParams = Shader.PropertyToID("_SslRefractionParams");

        // Scene proxy: the world AABB of the active box-projected reflection probe. w of BoxMin is
        // 1 when a proxy is available (URP has no per-material proxy volume).
        internal static readonly int RefractionProxyBoxMin = Shader.PropertyToID("_SslRefractionProxyBoxMin");
        internal static readonly int RefractionProxyBoxMax = Shader.PropertyToID("_SslRefractionProxyBoxMax");

        internal static readonly string ScreenSpaceRefractionKeyword = "_SCREEN_SPACE_REFRACTION";
    }
}