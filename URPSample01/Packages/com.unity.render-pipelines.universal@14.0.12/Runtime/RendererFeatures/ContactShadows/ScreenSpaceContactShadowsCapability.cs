using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    internal static class ScreenSpaceContactShadowsCapability
    {
        internal static ScreenSpaceCapabilityResult EvaluatePlatform() => Evaluate(SystemInfo.graphicsDeviceType,
            ScreenSpaceCapability.EvaluatePlatform(), SystemInfo.IsFormatSupported(GraphicsFormat.R32_UInt, FormatUsage.LoadStore));

        internal static ScreenSpaceCapabilityResult Evaluate(GraphicsDeviceType api, ScreenSpaceCapabilityResult shared, bool packedStore)
        {
            if (!SupportsApi(api))
                return ScreenSpaceCapabilityResult.Unsupported(ScreenSpaceCapabilityStatus.RequiredFormatUnsupported,
                    $"Screen Space Contact Shadows does not support {api}.");
            if (!shared.supported)
                return shared;
            return packedStore ? shared : ScreenSpaceCapabilityResult.Unsupported(ScreenSpaceCapabilityStatus.RequiredFormatUnsupported,
                "Screen Space Contact Shadows requires R32_UInt load/store support.");
        }

        internal static bool SupportsApi(GraphicsDeviceType api) =>
            api == GraphicsDeviceType.Direct3D11 || api == GraphicsDeviceType.Direct3D12 ||
            api == GraphicsDeviceType.Vulkan || api == GraphicsDeviceType.Metal;
    }
}
