using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    internal sealed class ScreenSpaceContactShadowsHistory : IScreenSpaceHistoryEntry
    {
        internal ScreenSpaceDepthPyramid depthPyramidSource { get; set; }
        internal RTHandle packedTexture { get; private set; }
        // Valid only after this render's compute dispatch has been recorded.
        internal bool packedReady { get; set; }
        internal RectInt traceMip => GetMipRect(1);
        internal RectInt confirmMip => GetMipRect(0);
        int m_Width, m_Height;

        RectInt GetMipRect(int level)
        {
            var info = depthPyramidSource.info;
            return new RectInt(info.mipLevelOffsets[level], info.mipLevelSizes[level]);
        }

        public void Reset(int width, int height, int frameIndex)
        {
            Dispose();
            m_Width = width;
            m_Height = height;
        }

        internal bool EnsurePackedTexture()
        {
            var pyramid = depthPyramidSource;
            if (pyramid == null || !pyramid.ready || pyramid.texture.rt == null || !pyramid.texture.rt.IsCreated() || pyramid.mipCount < 2)
            {
                ReleasePackedTexture();
                return false;
            }
            if (packedTexture?.rt != null && packedTexture.rt.IsCreated() &&
                packedTexture.rt.dimension == pyramid.dimension && packedTexture.rt.volumeDepth == pyramid.viewCount)
                return true;

            ReleasePackedTexture();
            packedTexture = RTHandles.Alloc(m_Width, m_Height, slices: pyramid.viewCount,
                colorFormat: GraphicsFormat.R32_UInt, filterMode: FilterMode.Point,
                dimension: pyramid.dimension, enableRandomWrite: true, name: "_ContactShadowTexture");
            if (packedTexture?.rt != null && packedTexture.rt.IsCreated())
                return true;
            ReleasePackedTexture();
            return false;
        }

        void ReleasePackedTexture()
        {
            packedReady = false;
            RTHandles.Release(packedTexture);
            packedTexture = null;
        }

        public void Dispose()
        {
            ReleasePackedTexture();
            depthPyramidSource = null;
        }
    }
}
