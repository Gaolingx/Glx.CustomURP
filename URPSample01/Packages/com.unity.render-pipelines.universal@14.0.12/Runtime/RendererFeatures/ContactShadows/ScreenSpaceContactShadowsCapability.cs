using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    internal static class ScreenSpaceContactShadowsCapability
    {
        internal static ScreenSpaceCapabilityResult EvaluatePlatform() => Evaluate(
            ScreenSpaceCapability.EvaluatePlatform(), SystemInfo.IsFormatSupported(GraphicsFormat.R32_UInt, FormatUsage.LoadStore));

        internal static ScreenSpaceCapabilityResult Evaluate(ScreenSpaceCapabilityResult shared, bool packedStore)
        {
            if (!shared.supported)
                return shared;
            return packedStore ? shared : ScreenSpaceCapabilityResult.Unsupported(ScreenSpaceCapabilityStatus.RequiredFormatUnsupported,
                "Screen Space Contact Shadows requires R32_UInt load/store support.");
        }
    }
}
