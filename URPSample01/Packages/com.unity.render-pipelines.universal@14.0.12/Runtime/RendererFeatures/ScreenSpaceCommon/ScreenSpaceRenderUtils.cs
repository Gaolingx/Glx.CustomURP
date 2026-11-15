namespace UnityEngine.Rendering.Universal
{
    internal static class ScreenSpaceRenderUtils
    {
        // Every screen-space consumer samples the same camera depth attachment: forward paths keep
        // it in m_DepthTexture, deferred paths expose it through the camera depth target.
        internal static RTHandle GetCameraDepthTexture(ScriptableRenderer renderer)
        {
            if (renderer is UniversalRenderer universalRenderer && universalRenderer.m_DepthTexture != null)
                return universalRenderer.m_DepthTexture;

            return renderer != null ? renderer.cameraDepthTargetHandle : null;
        }
    }
}