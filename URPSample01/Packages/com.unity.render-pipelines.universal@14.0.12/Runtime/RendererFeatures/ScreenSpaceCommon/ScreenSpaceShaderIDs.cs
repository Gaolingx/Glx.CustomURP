using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    // Bindings owned by the shared screen-space infrastructure rather than by one effect.
    internal static class ScreenSpaceShaderIDs
    {
        internal static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");

        // Packed depth pyramid atlas and the per-dispatch copy window.
        internal static readonly int DepthMipChain = Shader.PropertyToID("_SslDepthMipChain");
        internal static readonly int SrcOffsetAndLimit = Shader.PropertyToID("_SslSrcOffsetAndLimit");
        internal static readonly int DstOffset = Shader.PropertyToID("_SslDstOffset");

        internal static readonly string XrArrayKeyword = "SSL_XR_ARRAY";
    }
}
