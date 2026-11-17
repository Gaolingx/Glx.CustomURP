using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // One packed depth mip chain per camera view, shared by every screen-space consumer.
    // HDRP builds the equivalent once in the prepass and hands the same texture to AO, SSGI and SSR;
    // here the frame's first consumer enqueues the build pass and the rest bind this attachment.
    internal sealed class ScreenSpaceDepthPyramid : IScreenSpaceHistoryEntry
    {
        internal PackedMipChainInfo info => m_Info;
        internal RTHandle texture { get; private set; }
        internal ComputeBuffer offsets { get; private set; }
        internal int viewCount { get; private set; }
        internal TextureDimension dimension { get; private set; }
        internal int mipCount => m_Info.mipLevelCount;
        internal bool ready => texture != null && m_Info.IsValid();

        PackedMipChainInfo m_Info;
        int m_Width;
        int m_Height;
        int m_ViewCount;
        TextureDimension m_Dimension;
        int m_AllocatedWidth;
        int m_AllocatedHeight;
        int m_AllocatedSlices;
        TextureDimension m_AllocatedDimension;
        int m_BuildFrame = -1;

        // The chain is rebuilt on every rendered frame, so a camera reset only changes the layout
        // the next build uses; the allocation is released by EnsureResources when it changes.
        public void Reset(int width, int height, int frameIndex)
        {
            m_Width = Mathf.Max(1, width);
            m_Height = Mathf.Max(1, height);
        }

        internal void Prepare(int width, int height, int viewCount, TextureDimension dimension)
        {
            m_Width = Mathf.Max(1, width);
            m_Height = Mathf.Max(1, height);
            m_ViewCount = Mathf.Max(1, viewCount);
            m_Dimension = dimension;
        }

        internal void EnsureResources()
        {
            if (texture != null &&
                m_AllocatedWidth == m_Width &&
                m_AllocatedHeight == m_Height &&
                m_AllocatedSlices == m_ViewCount &&
                m_AllocatedDimension == m_Dimension)
            {
                return;
            }

            ReleaseResources();

            m_AllocatedWidth = m_Width;
            m_AllocatedHeight = m_Height;
            m_AllocatedSlices = m_ViewCount;
            m_AllocatedDimension = m_Dimension;
            viewCount = m_ViewCount;
            dimension = m_Dimension;

            m_Info.ComputePackedMipChainInfo(new Vector2Int(m_Width, m_Height));
            texture = RTHandles.Alloc(
                m_Info.textureSize.x,
                m_Info.textureSize.y,
                TextureWrapMode.Clamp,
                TextureWrapMode.Clamp,
                slices: viewCount,
                colorFormat: ScreenSpaceCapability.k_DepthPyramidFormat,
                filterMode: FilterMode.Point,
                dimension: dimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: "ScreenSpaceDepthPyramid");

            offsets = new ComputeBuffer(PackedMipChainInfo.k_MaxMipLevelCount, sizeof(int) * 2);
            m_Info.GetOffsetBufferData(offsets);
        }

        // True exactly once per camera view and frame: the caller enqueues the shared build pass.
        internal bool ShouldBuild(int frameIndex)
        {
            if (m_BuildFrame == frameIndex)
                return false;

            m_BuildFrame = frameIndex;
            return true;
        }

        public void Dispose()
        {
            ReleaseResources();
        }

        void ReleaseResources()
        {
            RTHandles.Release(texture);
            texture = null;

            if (offsets != null)
            {
                offsets.Release();
                offsets = null;
            }

            m_Info = default;
            m_AllocatedWidth = 0;
            m_AllocatedHeight = 0;
            m_AllocatedSlices = 0;
        }
    }
}