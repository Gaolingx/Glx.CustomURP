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

        // ---------------------------------------------------------------------------------------
        // Screen Space Global Illumination
        // ---------------------------------------------------------------------------------------

        internal int ssgiTraceWidth { get; private set; }
        internal int ssgiTraceHeight { get; private set; }
        internal int ssgiReadSlot { get; private set; }
        internal int ssgiWriteSlot { get; private set; }
        internal bool ssgiHasValidHistory { get; private set; }
        internal int ssgiBufferIndex { get; private set; }
        internal int ssgiResourceGeneration { get; private set; }
        internal bool ssgiResourcesAllocated => ssgiHitPoint != null && ssgiFinal != null && m_SsgiHistoryHF[0] != null;

        internal RTHandle ssgiHitPoint { get; private set; }
        internal RTHandle ssgiProjection { get; private set; }
        internal RTHandle ssgiValidation { get; private set; }
        internal RTHandle ssgiAccumulation { get; private set; }
        internal RTHandle ssgiDenoisedHF { get; private set; }
        internal RTHandle ssgiDenoisedLF { get; private set; }
        internal RTHandle ssgiFinal { get; private set; }
        internal ComputeBuffer ssgiPointDistribution { get; private set; }

        internal RTHandle GetSsgiHistoryHF(int slot)
        {
            return slot >= 0 && slot < k_HistoryBufferCount ? m_SsgiHistoryHF[slot] : null;
        }

        internal RTHandle GetSsgiSampleCountHF(int slot)
        {
            return slot >= 0 && slot < k_HistoryBufferCount ? m_SsgiSampleCountHF[slot] : null;
        }

        internal RTHandle GetSsgiHistoryLF(int slot)
        {
            return slot >= 0 && slot < k_HistoryBufferCount ? m_SsgiHistoryLF[slot] : null;
        }

        internal RTHandle GetSsgiSampleCountLF(int slot)
        {
            return slot >= 0 && slot < k_HistoryBufferCount ? m_SsgiSampleCountLF[slot] : null;
        }

        internal void BeginSsgiFrame()
        {
            ssgiReadSlot = ssgiHasValidHistory ? ssgiBufferIndex : -1;
            ssgiWriteSlot = ssgiBufferIndex ^ 1;
        }

        internal void SwapSsgiBuffers()
        {
            ssgiBufferIndex = ssgiWriteSlot;
            ssgiHasValidHistory = true;
        }

        internal void ResetSsgiHistory()
        {
            ssgiBufferIndex = 0;
            ssgiHasValidHistory = false;
            ssgiReadSlot = -1;
            ssgiWriteSlot = 1;
        }

        // The trace resolution follows the volume's full/half resolution switch, so the SSGI buffers
        // are managed separately from the color history but still owned by the same per-camera entry.
        internal void EnsureSsgiResources(
            int screenWidth,
            int screenHeight,
            int traceWidth,
            int traceHeight,
            int viewCount,
            TextureDimension resourceDimension)
        {
            screenWidth = Mathf.Max(1, screenWidth);
            screenHeight = Mathf.Max(1, screenHeight);
            traceWidth = Mathf.Max(1, traceWidth);
            traceHeight = Mathf.Max(1, traceHeight);
            viewCount = Mathf.Max(1, viewCount);

            if (ssgiResourcesAllocated &&
                m_SsgiScreenWidth == screenWidth &&
                m_SsgiScreenHeight == screenHeight &&
                m_SsgiTraceWidth == traceWidth &&
                m_SsgiTraceHeight == traceHeight &&
                m_SsgiSlices == viewCount &&
                m_SsgiDimension == resourceDimension)
            {
                return;
            }

            ReleaseSsgiResources();

            m_SsgiScreenWidth = screenWidth;
            m_SsgiScreenHeight = screenHeight;
            m_SsgiTraceWidth = traceWidth;
            m_SsgiTraceHeight = traceHeight;
            m_SsgiSlices = viewCount;
            m_SsgiDimension = resourceDimension;

            ssgiTraceWidth = traceWidth;
            ssgiTraceHeight = traceHeight;

            ssgiHitPoint = RTHandles.Alloc(
                traceWidth, traceHeight,
                TextureWrapMode.Clamp, TextureWrapMode.Clamp,
                slices: viewCount,
                colorFormat: GraphicsFormat.R16G16_SFloat,
                filterMode: FilterMode.Point,
                dimension: resourceDimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: "SslSsgiHitPoint");

            ssgiProjection = AllocSsgiColor(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsgiProjection");
            ssgiAccumulation = AllocSsgiColor(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsgiAccumulation");
            ssgiDenoisedHF = AllocSsgiColor(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsgiDenoisedHF");
            ssgiDenoisedLF = AllocSsgiColor(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsgiDenoisedLF");
            ssgiFinal = AllocSsgiColor(screenWidth, screenHeight, resourceDimension, viewCount, "SslSsgiFinal");

            ssgiValidation = RTHandles.Alloc(
                screenWidth, screenHeight,
                TextureWrapMode.Clamp, TextureWrapMode.Clamp,
                slices: viewCount,
                colorFormat: ScreenSpaceCapability.ssgiValidationFormat,
                filterMode: FilterMode.Point,
                dimension: resourceDimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: "SslSsgiValidation");

            for (int i = 0; i < k_HistoryBufferCount; i++)
            {
                m_SsgiHistoryHF[i] = AllocSsgiColor(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsgiHistoryHF" + i);
                m_SsgiHistoryLF[i] = AllocSsgiColor(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsgiHistoryLF" + i);
                m_SsgiSampleCountHF[i] = AllocSsgiSampleCount(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsgiSampleCountHF" + i);
                m_SsgiSampleCountLF[i] = AllocSsgiSampleCount(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsgiSampleCountLF" + i);
            }

            ssgiPointDistribution = new ComputeBuffer(64, sizeof(float) * 2);

            ssgiResourceGeneration++;
            ResetSsgiHistory();
        }

        static RTHandle AllocSsgiColor(int width, int height, TextureDimension dimension, int slices, string name)
        {
            return RTHandles.Alloc(
                width, height,
                TextureWrapMode.Clamp, TextureWrapMode.Clamp,
                slices: slices,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                filterMode: FilterMode.Bilinear,
                dimension: dimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: name);
        }

        static RTHandle AllocSsgiSampleCount(int width, int height, TextureDimension dimension, int slices, string name)
        {
            return RTHandles.Alloc(
                width, height,
                TextureWrapMode.Clamp, TextureWrapMode.Clamp,
                slices: slices,
                colorFormat: GraphicsFormat.R16_SFloat,
                filterMode: FilterMode.Point,
                dimension: dimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: name);
        }

        readonly RTHandle[] m_SsgiHistoryHF = new RTHandle[k_HistoryBufferCount];
        readonly RTHandle[] m_SsgiSampleCountHF = new RTHandle[k_HistoryBufferCount];
        readonly RTHandle[] m_SsgiHistoryLF = new RTHandle[k_HistoryBufferCount];
        readonly RTHandle[] m_SsgiSampleCountLF = new RTHandle[k_HistoryBufferCount];
        int m_SsgiScreenWidth;
        int m_SsgiScreenHeight;
        int m_SsgiTraceWidth;
        int m_SsgiTraceHeight;
        int m_SsgiSlices;
        TextureDimension m_SsgiDimension;

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
            ResetSsgiHistory();
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
            ReleaseSsgiResources();
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

        void ReleaseSsgiResources()
        {
            RTHandles.Release(ssgiHitPoint);
            ssgiHitPoint = null;
            RTHandles.Release(ssgiProjection);
            ssgiProjection = null;
            RTHandles.Release(ssgiValidation);
            ssgiValidation = null;
            RTHandles.Release(ssgiAccumulation);
            ssgiAccumulation = null;
            RTHandles.Release(ssgiDenoisedHF);
            ssgiDenoisedHF = null;
            RTHandles.Release(ssgiDenoisedLF);
            ssgiDenoisedLF = null;
            RTHandles.Release(ssgiFinal);
            ssgiFinal = null;

            for (int i = 0; i < k_HistoryBufferCount; i++)
            {
                RTHandles.Release(m_SsgiHistoryHF[i]);
                m_SsgiHistoryHF[i] = null;
                RTHandles.Release(m_SsgiHistoryLF[i]);
                m_SsgiHistoryLF[i] = null;
                RTHandles.Release(m_SsgiSampleCountHF[i]);
                m_SsgiSampleCountHF[i] = null;
                RTHandles.Release(m_SsgiSampleCountLF[i]);
                m_SsgiSampleCountLF[i] = null;
            }

            if (ssgiPointDistribution != null)
            {
                ssgiPointDistribution.Release();
                ssgiPointDistribution = null;
            }

            ssgiTraceWidth = 0;
            ssgiTraceHeight = 0;
            m_SsgiScreenWidth = 0;
            m_SsgiScreenHeight = 0;
            m_SsgiTraceWidth = 0;
            m_SsgiTraceHeight = 0;
            m_SsgiSlices = 0;
            ResetSsgiHistory();
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