using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    internal enum ScreenSpaceCapabilityStatus
    {
        Supported,
        ComputeShadersUnsupported,
        DepthPyramidFormatUnsupported,
        CameraUnsupported,
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
        // The packed depth pyramid samples two depth-linearising values; the compute shaders need
        // load/store access to it, which is what separates supported graphics devices.
        internal static readonly GraphicsFormat k_DepthPyramidFormat = GraphicsFormat.R32G32_SFloat;

        static bool s_PlatformEvaluated;
        static ScreenSpaceCapabilityResult s_PlatformResult;

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

            return ScreenSpaceCapabilityResult.Supported();
        }
    }
}
