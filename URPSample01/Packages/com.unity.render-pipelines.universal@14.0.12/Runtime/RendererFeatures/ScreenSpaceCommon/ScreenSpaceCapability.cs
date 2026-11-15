using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    internal enum ScreenSpaceCapabilityStatus
    {
        Supported,
        ComputeShadersUnsupported,
        DepthPyramidFormatUnsupported,
        RequiredFormatUnsupported,
        CameraUnsupported,
        XrLayoutUnsupported,
    }

    internal readonly struct ScreenSpaceCapabilityResult
    {
        public readonly bool supported;
        public readonly ScreenSpaceCapabilityStatus status;
        public readonly string message;

        ScreenSpaceCapabilityResult(bool supported, ScreenSpaceCapabilityStatus status, string message)
        {
            this.supported = supported;
            this.status = status;
            this.message = message;
        }

        internal static ScreenSpaceCapabilityResult Supported()
        {
            return new ScreenSpaceCapabilityResult(true, ScreenSpaceCapabilityStatus.Supported, string.Empty);
        }

        internal static ScreenSpaceCapabilityResult Unsupported(ScreenSpaceCapabilityStatus status, string message)
        {
            return new ScreenSpaceCapabilityResult(false, status, message);
        }
    }

    internal static class ScreenSpaceCapability
    {
        // The packed depth pyramid stores raw device depth in a single channel, matching the HDRP
        // pyramid layout; the compute shaders need load/store access to it.
        internal static readonly GraphicsFormat k_DepthPyramidFormat = GraphicsFormat.R32_SFloat;

        // Final AO visibility is a single channel in [0, 1]; R32_SFloat keeps the compute output
        // working on the rare targets that cannot store to R8_UNorm.
        internal static GraphicsFormat occlusionFormat =>
            SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, FormatUsage.LoadStore)
                ? GraphicsFormat.R8_UNorm
                : GraphicsFormat.R32_SFloat;

        // Temporal history validation is a flag mask; an integer target keeps the rejection bits exact.
        internal static GraphicsFormat ssgiValidationFormat =>
            SystemInfo.IsFormatSupported(GraphicsFormat.R8_UInt, FormatUsage.LoadStore)
                ? GraphicsFormat.R8_UInt
                : GraphicsFormat.R32_UInt;

        static bool s_PlatformEvaluated;
        static ScreenSpaceCapabilityResult s_PlatformResult;

        // Every private resource is written by a compute kernel, so each format needs UAV/load-store
        // support. Formats with a runtime fallback (AO visibility, validation mask) are not listed.
        internal static readonly GraphicsFormat[] k_RequiredStoreFormats =
        {
            GraphicsFormat.R32_SFloat,
            GraphicsFormat.R16G16_SFloat,
            GraphicsFormat.R16G16B16A16_SFloat,
            GraphicsFormat.R16_SFloat,
        };

        internal static ScreenSpaceCapabilityResult EvaluatePlatform()
        {
            if (!s_PlatformEvaluated)
            {
                s_PlatformResult = EvaluatePlatformUncached();
                s_PlatformEvaluated = true;
            }

            return s_PlatformResult;
        }

        static ScreenSpaceCapabilityResult EvaluatePlatformUncached()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                return ScreenSpaceCapabilityResult.Unsupported(
                    ScreenSpaceCapabilityStatus.ComputeShadersUnsupported,
                    "Screen-space lighting requires compute shader support.");
            }

            if (!SystemInfo.IsFormatSupported(k_DepthPyramidFormat, FormatUsage.LoadStore))
            {
                return ScreenSpaceCapabilityResult.Unsupported(
                    ScreenSpaceCapabilityStatus.DepthPyramidFormatUnsupported,
                    $"Screen-space lighting requires load/store support for {k_DepthPyramidFormat}.");
            }

            for (int i = 0; i < k_RequiredStoreFormats.Length; i++)
            {
                var format = k_RequiredStoreFormats[i];
                if (!SystemInfo.IsFormatSupported(format, FormatUsage.LoadStore))
                {
                    return ScreenSpaceCapabilityResult.Unsupported(
                        ScreenSpaceCapabilityStatus.RequiredFormatUnsupported,
                        $"Screen-space lighting requires load/store support for {format}.");
                }
            }

            return ScreenSpaceCapabilityResult.Supported();
        }

        internal static ScreenSpaceCapabilityResult EvaluateCamera(in CameraData cameraData)
        {
            if (cameraData.camera == null)
            {
                return ScreenSpaceCapabilityResult.Unsupported(
                    ScreenSpaceCapabilityStatus.CameraUnsupported,
                    "The camera is no longer valid.");
            }

            if (cameraData.cameraType == CameraType.Preview || cameraData.cameraType == CameraType.Reflection)
            {
                return ScreenSpaceCapabilityResult.Unsupported(
                    ScreenSpaceCapabilityStatus.CameraUnsupported,
                    $"Screen-space lighting does not support {cameraData.cameraType} cameras.");
            }

            if (cameraData.renderType == CameraRenderType.Overlay)
            {
                return ScreenSpaceCapabilityResult.Unsupported(
                    ScreenSpaceCapabilityStatus.CameraUnsupported,
                    "Screen-space lighting must be configured on the base camera of a stack.");
            }

            if (cameraData.cameraTargetDescriptor.width <= 0 || cameraData.cameraTargetDescriptor.height <= 0)
            {
                return ScreenSpaceCapabilityResult.Unsupported(
                    ScreenSpaceCapabilityStatus.CameraUnsupported,
                    "The camera target has no renderable size.");
            }

            if (cameraData.xr.enabled)
            {
                int viewCount = cameraData.xr.viewCount;
                if (viewCount <= 0 || viewCount > ScreenSpaceLightingXr.k_MaxViews)
                {
                    return ScreenSpaceCapabilityResult.Unsupported(
                        ScreenSpaceCapabilityStatus.XrLayoutUnsupported,
                        $"Screen-space lighting supports one or two XR views, the camera reports {viewCount}.");
                }

                if (cameraData.xr.singlePassEnabled && cameraData.cameraTargetDescriptor.dimension != TextureDimension.Tex2DArray)
                {
                    return ScreenSpaceCapabilityResult.Unsupported(
                        ScreenSpaceCapabilityStatus.XrLayoutUnsupported,
                        "Single-pass instanced XR must provide a texture array camera target.");
                }
            }

            return ScreenSpaceCapabilityResult.Supported();
        }
    }
}
