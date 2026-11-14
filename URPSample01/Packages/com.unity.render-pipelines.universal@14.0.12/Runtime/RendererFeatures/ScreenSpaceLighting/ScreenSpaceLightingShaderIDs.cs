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
    }
}