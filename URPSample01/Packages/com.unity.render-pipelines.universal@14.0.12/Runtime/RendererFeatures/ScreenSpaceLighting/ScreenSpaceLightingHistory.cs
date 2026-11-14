using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Owned by ScreenSpaceLightingRendererFeature. Every resource that depends on the camera size,
    // XR layout or history generation lives here so a reset releases exactly one camera's data.
    internal sealed class ScreenSpaceLightingHistory : IScreenSpaceHistoryEntry
    {
        internal const int k_HistoryBufferCount = 2;

        public int width { get; private set; }
        public int height { get; private set; }
        public int resetCount { get; private set; }
        public int lastResetFrame { get; private set; }
        public int bufferIndex { get; private set; }
        public bool hasValidHistory { get; private set; }
        public int validFrameCount { get; private set; }

        internal int resourceGeneration { get; private set; }
        internal int readSlot { get; private set; }
        internal int writeSlot { get; private set; }
        internal bool sameFrameConflict => hasValidHistory && readSlot == writeSlot;

        internal int slices { get; private set; }
        internal TextureDimension dimension { get; private set; }
        internal GraphicsFormat colorFormat { get; private set; }

        internal PackedMipChainInfo depthPyramidInfo => m_DepthPyramidInfo;
        internal RTHandle depthPyramid { get; private set; }
        internal ComputeBuffer depthPyramidOffsets { get; private set; }
        internal int depthPyramidMipCount => m_DepthPyramidInfo.mipLevelCount;

        internal RTHandle currentColorPyramid { get; private set; }
        internal int colorPyramidMipCount { get; private set; }
        internal RTHandle readColorPyramid { get; private set; }
        internal RTHandle readPreviousDepth { get; private set; }

        PackedMipChainInfo m_DepthPyramidInfo;
        readonly RTHandle[] m_HistoryColorPyramids = new RTHandle[k_HistoryBufferCount];
        readonly RTHandle[] m_PreviousDepths = new RTHandle[k_HistoryBufferCount];
        RTHandle m_TempDownsample;
        RTHandle m_TempBlur;
        int m_AllocatedWidth;
        int m_AllocatedHeight;
        int m_AllocatedSlices;
        TextureDimension m_AllocatedDimension;

        internal RTHandle GetHistoryColorPyramid(int slot)
        {
            return slot >= 0 && slot < k_HistoryBufferCount ? m_HistoryColorPyramids[slot] : null;
        }

        internal RTHandle GetPreviousDepth(int slot)
        {
            return slot >= 0 && slot < k_HistoryBufferCount ? m_PreviousDepths[slot] : null;
        }

        internal int historyColorPyramidCount => m_HistoryColorPyramids.Length;
        internal int previousDepthCount => m_PreviousDepths.Length;
        internal bool resourcesAllocated => depthPyramid != null && currentColorPyramid != null;

        public void BeginFrame()
        {
            readSlot = hasValidHistory ? bufferIndex : -1;
            writeSlot = bufferIndex ^ 1;
            readColorPyramid = readSlot >= 0 ? m_HistoryColorPyramids[readSlot] : null;
            readPreviousDepth = readSlot >= 0 ? m_PreviousDepths[readSlot] : null;
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
            writeSlot = bufferIndex ^ 1;
            readColorPyramid = null;
            readPreviousDepth = null;
        }

        // Called by the history capture pass after it recorded the current color and depth.
        // The ping-pong keeps the slot that was read this frame untouched.
        public void SwapBuffers()
        {
            bufferIndex = writeSlot;
            hasValidHistory = true;
            validFrameCount++;
        }

        internal void EnsureResources(int viewportWidth, int viewportHeight, int viewCount, TextureDimension resourceDimension, GraphicsFormat resourceColorFormat)
        {
            viewportWidth = Mathf.Max(1, viewportWidth);
            viewportHeight = Mathf.Max(1, viewportHeight);
            viewCount = Mathf.Max(1, viewCount);

            if (depthPyramid != null &&
                m_AllocatedWidth == viewportWidth &&
                m_AllocatedHeight == viewportHeight &&
                m_AllocatedSlices == viewCount &&
                m_AllocatedDimension == resourceDimension &&
                colorFormat == resourceColorFormat)
            {
                return;
            }

            ReleaseResources();

            m_AllocatedWidth = viewportWidth;
            m_AllocatedHeight = viewportHeight;
            m_AllocatedSlices = viewCount;
            m_AllocatedDimension = resourceDimension;
            slices = viewCount;
            dimension = resourceDimension;
            colorFormat = resourceColorFormat;

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
                name: "SslDepthPyramid");

            depthPyramidOffsets = new ComputeBuffer(PackedMipChainInfo.k_MaxMipLevelCount, sizeof(int) * 2);
            m_DepthPyramidInfo.GetOffsetBufferData(depthPyramidOffsets);

            colorPyramidMipCount = ComputeColorPyramidMipCount(viewportWidth, viewportHeight);
            currentColorPyramid = AllocColorPyramid(viewportWidth, viewportHeight, colorFormat, dimension, slices, "SslCurrentColorPyramid");
            for (int i = 0; i < k_HistoryBufferCount; i++)
            {
                m_HistoryColorPyramids[i] = AllocColorPyramid(viewportWidth, viewportHeight, colorFormat, dimension, slices, "SslHistoryColorPyramid" + i);
                m_PreviousDepths[i] = RTHandles.Alloc(
                    viewportWidth,
                    viewportHeight,
                    TextureWrapMode.Clamp,
                    TextureWrapMode.Clamp,
                    slices: slices,
                    colorFormat: ScreenSpaceCapability.k_DepthPyramidFormat,
                    filterMode: FilterMode.Point,
                    dimension: dimension,
                    enableRandomWrite: true,
                    useMipMap: false,
                    autoGenerateMips: false,
                    name: "SslPreviousDepth" + i);
            }

            int tempWidth = Mathf.Max(1, viewportWidth >> 1);
            int tempHeight = Mathf.Max(1, viewportHeight >> 1);
            m_TempDownsample = AllocTempColor(tempWidth, tempHeight, colorFormat, dimension, slices, "SslColorPyramidDownsample");
            m_TempBlur = AllocTempColor(tempWidth, tempHeight, colorFormat, dimension, slices, "SslColorPyramidBlur");

            resourceGeneration++;
        }

        internal RTHandle tempDownsample => m_TempDownsample;
        internal RTHandle tempBlur => m_TempBlur;

        static RTHandle AllocColorPyramid(int width, int height, GraphicsFormat format, TextureDimension dimension, int slices, string name)
        {
            return RTHandles.Alloc(
                width,
                height,
                TextureWrapMode.Clamp,
                TextureWrapMode.Clamp,
                slices: slices,
                colorFormat: format,
                filterMode: FilterMode.Bilinear,
                dimension: dimension,
                enableRandomWrite: false,
                useMipMap: true,
                autoGenerateMips: false,
                name: name);
        }

        static RTHandle AllocTempColor(int width, int height, GraphicsFormat format, TextureDimension dimension, int slices, string name)
        {
            return RTHandles.Alloc(
                width,
                height,
                TextureWrapMode.Clamp,
                TextureWrapMode.Clamp,
                slices: slices,
                colorFormat: format,
                filterMode: FilterMode.Bilinear,
                dimension: dimension,
                enableRandomWrite: false,
                useMipMap: false,
                autoGenerateMips: false,
                name: name);
        }

        internal static int ComputeColorPyramidMipCount(int width, int height)
        {
            // HDRP stops once both dimensions fall below the 8x8 Gaussian block.
            int mipCount = 1;
            int mipWidth = Mathf.Max(1, width);
            int mipHeight = Mathf.Max(1, height);
            while (mipWidth >= 8 || mipHeight >= 8)
            {
                mipWidth = Mathf.Max(1, mipWidth >> 1);
                mipHeight = Mathf.Max(1, mipHeight >> 1);
                mipCount++;
            }

            return mipCount;
        }

        public void Dispose()
        {
            ReleaseResources();
            width = 0;
            height = 0;
            bufferIndex = 0;
            hasValidHistory = false;
            validFrameCount = 0;
            readSlot = -1;
            writeSlot = 0;
            readColorPyramid = null;
            readPreviousDepth = null;
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

            RTHandles.Release(currentColorPyramid);
            currentColorPyramid = null;

            for (int i = 0; i < k_HistoryBufferCount; i++)
            {
                RTHandles.Release(m_HistoryColorPyramids[i]);
                m_HistoryColorPyramids[i] = null;
                RTHandles.Release(m_PreviousDepths[i]);
                m_PreviousDepths[i] = null;
            }

            RTHandles.Release(m_TempDownsample);
            m_TempDownsample = null;
            RTHandles.Release(m_TempBlur);
            m_TempBlur = null;

            m_DepthPyramidInfo = default;
            colorPyramidMipCount = 0;
            m_AllocatedWidth = 0;
            m_AllocatedHeight = 0;
            m_AllocatedSlices = 0;
        }
    }
}