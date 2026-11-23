namespace UnityEngine.Rendering.Universal
{
    internal static class ScreenSpaceContactShadowsCapability
    {
        internal static bool SupportsApi(GraphicsDeviceType api) =>
            api == GraphicsDeviceType.Direct3D11 || api == GraphicsDeviceType.Direct3D12 ||
            api == GraphicsDeviceType.Vulkan || api == GraphicsDeviceType.Metal;
    }
}
