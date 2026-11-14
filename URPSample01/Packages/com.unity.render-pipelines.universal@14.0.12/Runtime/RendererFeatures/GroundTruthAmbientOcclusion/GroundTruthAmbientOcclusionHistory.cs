using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Owned by GroundTruthAmbientOcclusionRendererFeature. Every resource that depends on the camera size,
    // the AO resolution or the history generation lives here so a reset releases exactly one camera's data.
    internal sealed class GroundTruthAmbientOcclusionHistory : IScreenSpaceHistoryEntry
    {
        internal const int k_HistoryBufferCount = 2;

        public int width { get; private set; }
        public int height { get; private set; }
        public int resetCount { get; private set; }
        public int lastResetFrame { get; private set; }
        public int bufferIndex { get; private set; }
        public bool hasValidHistory { get; private set; }
        public int validFrameCount { get; private set; }

        internal int aoWidth { get; private set; }
        internal int aoHeight { get; private set; }
        internal int slices { get; private set; }
        internal TextureDimension dimension { get; private set; }
        internal bool fullResolution { get; private set; }
        internal int resourceGeneration { get; private set; }

        internal int readSlot { get; private set; }
        internal int writeSlot { get; private set; }
        internal bool sameFrameConflict => hasValidHistory && readSlot == writeSlot;

        // Reaches 0 on the first frame and keeps counting so temporal noise patterns stay stable
        // across camera cuts, matching HDRP's camera frame counter behaviour.
        internal int effectFrameCount { get; private set; }

        internal PackedMipChainInfo depthPyramidInfo => m_DepthPyramidInfo;
        internal RTHandle depthPyramid { get; private set; }
        internal ComputeBuffer depthPyramidOffsets { get; private set; }
        internal int depthPyramidMipCount => m_DepthPyramidInfo.mipLevelCount;

        internal RTHandle packedData { get; private set; }
        internal RTHandle packedBlurred { get; private set; }
        internal RTHandle packedTemporalOutput { get; private set; }
        internal RTHandle occlusionTexture { get; private set; }

        internal bool resourcesAllocated =>
            depthPyramid != null && packedData != null && m_HistoryTextures[0] != null && occlusionTexture != null;

        PackedMipChainInfo m_DepthPyramidInfo;
        readonly RTHandle[] m_HistoryTextures = new RTHandle[k_HistoryBufferCount];
        int m_AllocatedWidth;
        int m_AllocatedHeight;
        int m_AllocatedAoWidth;
        int m_AllocatedAoHeight;
        int m_AllocatedSlices;
        TextureDimension m_AllocatedDimension;
        bool m_AllocatedFullResolution;

        internal RTHandle GetHistory(int slot)
        {
            return slot >= 0 && slot < k_HistoryBufferCount ? m_HistoryTextures[slot] : null;
        }

        internal int historyTextureCount => m_HistoryTextures.Length;

        public void BeginFrame()
        {
            readSlot = hasValidHistory ? bufferIndex : -1;
            writeSlot = bufferIndex ^ 1;
        }

        internal void AdvanceEffectFrame()
        {
            effectFrameCount++;
        }

        public void Reset(int width, int height, int frameIndex)
        {
            this.width = width;
            this.height = height;
            lastResetFrame = frameIndex;
            resetCount++;
            bufferIndex = 0;
            hasValidHistory = false;
            validFrameCount = 0;
            readSlot = -1;
            writeSlot = 1;
        }

        // Called after the temporal pass wrote the current slot so the next frame can read it.
        public void SwapBuffers()
        {
            bufferIndex = writeSlot;
            hasValidHistory = true;
            validFrameCount++;
        }

        internal void EnsureResources(
            int viewportWidth,
            int viewportHeight,
            bool useFullResolution,
            int viewCount,
            TextureDimension resourceDimension)
        {
            viewportWidth = Mathf.Max(1, viewportWidth);
            viewportHeight = Mathf.Max(1, viewportHeight);
            viewCount = Mathf.Max(1, viewCount);
            int requestedAoWidth = useFullResolution ? viewportWidth : (viewportWidth + 1) >> 1;
            int requestedAoHeight = useFullResolution ? viewportHeight : (viewportHeight + 1) >> 1;

            if (resourcesAllocated &&
                m_AllocatedWidth == viewportWidth &&
                m_AllocatedHeight == viewportHeight &&
                m_AllocatedAoWidth == requestedAoWidth &&
                m_AllocatedAoHeight == requestedAoHeight &&
                m_AllocatedSlices == viewCount &&
                m_AllocatedDimension == resourceDimension &&
                m_AllocatedFullResolution == useFullResolution)
            {
                return;
            }

            ReleaseResources();

            m_AllocatedWidth = viewportWidth;
            m_AllocatedHeight = viewportHeight;
            m_AllocatedAoWidth = requestedAoWidth;
            m_AllocatedAoHeight = requestedAoHeight;
            m_AllocatedSlices = viewCount;
            m_AllocatedDimension = resourceDimension;
            m_AllocatedFullResolution = useFullResolution;

            width = viewportWidth;
            height = viewportHeight;
            aoWidth = requestedAoWidth;
            aoHeight = requestedAoHeight;
            slices = viewCount;
            dimension = resourceDimension;
            fullResolution = useFullResolution;

            m_DepthPyramidInfo.ComputePackedMipChainInfo(new Vector2Int(viewportWidth, viewportHeight));
            depthPyramid = RTHandles.Alloc(
                m_DepthPyramidInfo.textureSize.x,
                m_DepthPyramidInfo.textureSize.y,
                TextureWrapMode.Clamp,
                TextureWrapMode.Clamp,
                slices: slices,
                colorFormat: ScreenSpaceCapability.k_DepthPyramidFormat,
                filterMode: FilterMode.Point,
                dimension: dimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: "GtaoDepthPyramid");

            depthPyramidOffsets = new ComputeBuffer(PackedMipChainInfo.k_MaxMipLevelCount, sizeof(int) * 2);
            m_DepthPyramidInfo.GetOffsetBufferData(depthPyramidOffsets);

            packedData = AllocPacked(aoWidth, aoHeight, "GtaoPackedData");
            packedBlurred = AllocPacked(aoWidth, aoHeight, "GtaoPackedBlurred");
            packedTemporalOutput = AllocPacked(aoWidth, aoHeight, "GtaoPackedTemporal");

            for (int i = 0; i < k_HistoryBufferCount; i++)
            {
                m_HistoryTextures[i] = RTHandles.Alloc(
                    aoWidth,
                    aoHeight,
                    TextureWrapMode.Clamp,
                    TextureWrapMode.Clamp,
                    slices: slices,
                    colorFormat: GraphicsFormat.R8G8B8A8_UNorm,
                    filterMode: FilterMode.Bilinear,
                    dimension: dimension,
                    enableRandomWrite: true,
                    useMipMap: false,
                    autoGenerateMips: false,
                    name: "GtaoHistory" + i);
            }

            occlusionTexture = RTHandles.Alloc(
                viewportWidth,
                viewportHeight,
                TextureWrapMode.Clamp,
                TextureWrapMode.Clamp,
                slices: slices,
                colorFormat: ScreenSpaceCapability.occlusionFormat,
                filterMode: FilterMode.Bilinear,
                dimension: dimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: "GtaoOcclusion");

            resourceGeneration++;
        }

        RTHandle AllocPacked(int aoW, int aoH, string name)
        {
            return RTHandles.Alloc(
                aoW,
                aoH,
                TextureWrapMode.Clamp,
                TextureWrapMode.Clamp,
                slices: slices,
                colorFormat: GraphicsFormat.R32_SFloat,
                filterMode: FilterMode.Point,
                dimension: dimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: name);
        }

        public void Dispose()
        {
            ReleaseResources();
            width = 0;
            height = 0;
            aoWidth = 0;
            aoHeight = 0;
            bufferIndex = 0;
            hasValidHistory = false;
            validFrameCount = 0;
            readSlot = -1;
            writeSlot = 0;
            effectFrameCount = 0;
        }

        void ReleaseResources()
        {
            RTHandles.Release(depthPyramid);
            depthPyramid = null;

            if (depthPyramidOffsets != null)
            {
                depthPyramidOffsets.Release();
                depthPyramidOffsets = null;
            }

            RTHandles.Release(packedData);
            packedData = null;
            RTHandles.Release(packedBlurred);
            packedBlurred = null;
            RTHandles.Release(packedTemporalOutput);
            packedTemporalOutput = null;
            RTHandles.Release(occlusionTexture);
            occlusionTexture = null;

            for (int i = 0; i < k_HistoryBufferCount; i++)
            {
                RTHandles.Release(m_HistoryTextures[i]);
                m_HistoryTextures[i] = null;
            }

            m_DepthPyramidInfo = default;
            m_AllocatedWidth = 0;
            m_AllocatedHeight = 0;
            m_AllocatedAoWidth = 0;
            m_AllocatedAoHeight = 0;
            m_AllocatedSlices = 0;
        }
    }
}