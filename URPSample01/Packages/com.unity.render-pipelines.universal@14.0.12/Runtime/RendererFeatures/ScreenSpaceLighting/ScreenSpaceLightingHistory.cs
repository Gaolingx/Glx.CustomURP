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
        // Monotonic per-camera render serial. The transparent SSR pass requires that the transparent
        // prepass produced its input with the same serial, which proves the 447 -> 449 order instead
        // of consuming the previous frame's depth and normals.
        internal int frameSerial { get; private set; }
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

        // ---------------------------------------------------------------------------------------
        // Opaque Screen Space Reflections (Approximation)
        // ---------------------------------------------------------------------------------------

        internal RTHandle ssrHitPoint { get; private set; }
        internal RTHandle ssrOutput { get; private set; }
        internal int ssrWidth { get; private set; }
        internal int ssrHeight { get; private set; }
        internal int ssrResourceGeneration { get; private set; }
        internal bool ssrOutputProduced { get; private set; }
        internal bool ssrResourcesAllocated => ssrHitPoint != null && ssrOutput != null;
        internal bool ssrHasValidHistory => hasValidHistory && readColorPyramid != null;

        internal void EnsureSsrResources(int width, int height, int viewCount, TextureDimension resourceDimension)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            viewCount = Mathf.Max(1, viewCount);

            if (ssrResourcesAllocated &&
                m_SsrWidth == width &&
                m_SsrHeight == height &&
                m_SsrSlices == viewCount &&
                m_SsrDimension == resourceDimension)
            {
                return;
            }

            ReleaseSsrResources();

            m_SsrWidth = width;
            m_SsrHeight = height;
            m_SsrSlices = viewCount;
            m_SsrDimension = resourceDimension;
            ssrWidth = width;
            ssrHeight = height;

            // The hit point holds NDC coordinates and writes 99 for a miss, so it cannot use UNorm.
            ssrHitPoint = RTHandles.Alloc(
                width, height,
                TextureWrapMode.Clamp, TextureWrapMode.Clamp,
                slices: viewCount,
                colorFormat: GraphicsFormat.R16G16_SFloat,
                filterMode: FilterMode.Point,
                dimension: resourceDimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: "SslSsrHitPoint");

            ssrOutput = RTHandles.Alloc(
                width, height,
                TextureWrapMode.Clamp, TextureWrapMode.Clamp,
                slices: viewCount,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                filterMode: FilterMode.Bilinear,
                dimension: resourceDimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: "SslSsrOutput");

            ssrResourceGeneration++;
        }

        int m_SsrWidth;
        int m_SsrHeight;
        int m_SsrSlices;
        TextureDimension m_SsrDimension;

        // ---------------------------------------------------------------------------------------
        // Transparent Screen Space Reflections (Approximation only)
        // ---------------------------------------------------------------------------------------

        // The prepass writes the transparent ray origin into a private depth/normal pair. The depth
        // target starts as a copy of the opaque camera depth, so transparent surfaces behind opaque
        // geometry are never used as an origin. The SSR output and hit point are separate from the
        // opaque ones so neither trace can overwrite the other.
        internal RTHandle transparentDepth { get; private set; }
        internal RTHandle transparentNormals { get; private set; }
        internal RTHandle ssrTransparentHitPoint { get; private set; }
        internal RTHandle ssrTransparentOutput { get; private set; }
        internal int transparentWidth { get; private set; }
        internal int transparentHeight { get; private set; }
        internal int transparentResourceGeneration { get; private set; }
        internal bool ssrTransparentOutputProduced { get; private set; }
        internal bool transparentResourcesAllocated =>
            transparentDepth != null && transparentNormals != null &&
            ssrTransparentHitPoint != null && ssrTransparentOutput != null;

        internal void EnsureTransparentSsrResources(int width, int height, int viewCount, TextureDimension resourceDimension)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            viewCount = Mathf.Max(1, viewCount);

            if (transparentResourcesAllocated &&
                m_TransparentWidth == width &&
                m_TransparentHeight == height &&
                m_TransparentSlices == viewCount &&
                m_TransparentDimension == resourceDimension)
            {
                return;
            }

            ReleaseTransparentResources();

            m_TransparentWidth = width;
            m_TransparentHeight = height;
            m_TransparentSlices = viewCount;
            m_TransparentDimension = resourceDimension;
            transparentWidth = width;
            transparentHeight = height;

            transparentDepth = RTHandles.Alloc(
                width,
                height,
                slices: viewCount,
                depthBufferBits: DepthBits.Depth32,
                colorFormat: GraphicsFormat.None,
                filterMode: FilterMode.Point,
                wrapMode: TextureWrapMode.Clamp,
                dimension: resourceDimension,
                enableRandomWrite: false,
                useMipMap: false,
                autoGenerateMips: false,
                name: "SslTransparentDepth");

            transparentNormals = RTHandles.Alloc(
                width,
                height,
                slices: viewCount,
                depthBufferBits: DepthBits.None,
                colorFormat: Internal.DepthNormalOnlyPass.GetGraphicsFormat(),
                filterMode: FilterMode.Point,
                wrapMode: TextureWrapMode.Clamp,
                dimension: resourceDimension,
                enableRandomWrite: false,
                useMipMap: false,
                autoGenerateMips: false,
                name: "SslTransparentNormals");

            ssrTransparentHitPoint = RTHandles.Alloc(
                width,
                height,
                slices: viewCount,
                depthBufferBits: DepthBits.None,
                colorFormat: GraphicsFormat.R16G16_SFloat,
                filterMode: FilterMode.Point,
                wrapMode: TextureWrapMode.Clamp,
                dimension: resourceDimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: "SslSsrTransparentHitPoint");

            ssrTransparentOutput = RTHandles.Alloc(
                width,
                height,
                slices: viewCount,
                depthBufferBits: DepthBits.None,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                filterMode: FilterMode.Bilinear,
                wrapMode: TextureWrapMode.Clamp,
                dimension: resourceDimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: "SslSsrTransparentOutput");

            transparentResourceGeneration++;
        }

        int m_TransparentWidth;
        int m_TransparentHeight;
        int m_TransparentSlices;
        TextureDimension m_TransparentDimension;

        // PBR accumulation owns two full resolution RGBA16F histories plus the current reproject
        // output. They are only allocated while the PBR algorithm is selected.
        internal RTHandle ssrPbrOriginal { get; private set; }
        internal int ssrPbrWidth { get; private set; }
        internal int ssrPbrHeight { get; private set; }
        internal int ssrPbrReadSlot { get; private set; }
        internal int ssrPbrWriteSlot { get; private set; }
        internal int ssrPbrBufferIndex { get; private set; }
        internal bool ssrPbrHasValidHistory { get; private set; }
        internal int ssrPbrResourceGeneration { get; private set; }
        internal int ssrPbrResetCount { get; private set; }
        internal ScreenSpaceReflectionAlgorithm ssrPbrAlgorithm { get; private set; }
        internal bool ssrPbrAlgorithmInitialized { get; private set; }
        internal bool ssrPbrResourcesAllocated => ssrPbrOriginal != null && m_SsrPbrHistory[0] != null;

        internal RTHandle GetSsrPbrHistory(int slot)
        {
            return slot >= 0 && slot < k_HistoryBufferCount ? m_SsrPbrHistory[slot] : null;
        }

        internal void EnsureSsrPbrResources(int width, int height, int viewCount, TextureDimension resourceDimension)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            viewCount = Mathf.Max(1, viewCount);

            if (ssrPbrResourcesAllocated &&
                m_SsrPbrWidth == width &&
                m_SsrPbrHeight == height &&
                m_SsrPbrSlices == viewCount &&
                m_SsrPbrDimension == resourceDimension)
            {
                return;
            }

            ReleaseSsrPbrResources();

            m_SsrPbrWidth = width;
            m_SsrPbrHeight = height;
            m_SsrPbrSlices = viewCount;
            m_SsrPbrDimension = resourceDimension;
            ssrPbrWidth = width;
            ssrPbrHeight = height;

            ssrPbrOriginal = AllocSsrPbrTexture(width, height, resourceDimension, viewCount, "SslSsrPbrOriginal");
            for (int i = 0; i < k_HistoryBufferCount; i++)
                m_SsrPbrHistory[i] = AllocSsrPbrTexture(width, height, resourceDimension, viewCount, "SslSsrPbrHistory" + i);

            ssrPbrResourceGeneration++;
            ResetSsrPbrHistory();
        }

        static RTHandle AllocSsrPbrTexture(int width, int height, TextureDimension dimension, int slices, string name)
        {
            return RTHandles.Alloc(
                width, height,
                TextureWrapMode.Clamp, TextureWrapMode.Clamp,
                slices: slices,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                filterMode: FilterMode.Point,
                dimension: dimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: name);
        }

        internal void BeginSsrPbrFrame()
        {
            ssrPbrReadSlot = ssrPbrHasValidHistory ? ssrPbrBufferIndex : -1;
            ssrPbrWriteSlot = ssrPbrBufferIndex ^ 1;
        }

        internal void SwapSsrPbrBuffers()
        {
            ssrPbrBufferIndex = ssrPbrWriteSlot;
            ssrPbrHasValidHistory = true;
        }

        internal void ResetSsrPbrHistory()
        {
            ssrPbrBufferIndex = 0;
            ssrPbrHasValidHistory = false;
            ssrPbrReadSlot = -1;
            ssrPbrWriteSlot = 1;
            ssrPbrResetCount++;
        }

        // Called once per frame with the algorithm the volume selected for this camera. Switching
        // between Approximation and PBR must start the accumulation from scratch.
        internal void SyncSsrPbrAlgorithm(ScreenSpaceReflectionAlgorithm algorithm)
        {
            if (!ssrPbrAlgorithmInitialized)
            {
                ssrPbrAlgorithm = algorithm;
                ssrPbrAlgorithmInitialized = true;
                return;
            }

            if (ssrPbrAlgorithm == algorithm)
                return;

            ssrPbrAlgorithm = algorithm;
            ResetSsrPbrHistory();
        }

        readonly RTHandle[] m_SsrPbrHistory = new RTHandle[k_HistoryBufferCount];
        int m_SsrPbrWidth;
        int m_SsrPbrHeight;
        int m_SsrPbrSlices;
        TextureDimension m_SsrPbrDimension;

        PackedMipChainInfo m_DepthPyramidInfo;
        // Previous frame view projection per XR eye. URP's motion vector history stores a single
        // matrix that alternates eyes in multipass, so a per-eye entry is required for the SSGI
        // history validation and the SSR motion reconstruction to compare like with like.
        readonly Matrix4x4[] m_PreviousViewProj = new Matrix4x4[ScreenSpaceLightingXr.k_MaxViews];
        readonly bool[] m_HasPreviousViewProj = new bool[ScreenSpaceLightingXr.k_MaxViews];
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
            frameSerial++;
            readSlot = hasValidHistory ? bufferIndex : -1;
            writeSlot = bufferIndex ^ 1;
            readColorPyramid = readSlot >= 0 ? m_HistoryColorPyramids[readSlot] : null;
            readPreviousDepth = readSlot >= 0 ? m_PreviousDepths[readSlot] : null;
            ssrOutputProduced = false;
            ssrTransparentOutputProduced = false;
        }

        // Called by ScreenSpaceLightingSsrPass once both kernels dispatched for this camera.
        internal void MarkSsrOutputProduced()
        {
            ssrOutputProduced = true;
        }

        // Called by ScreenSpaceLightingTransparentSsrPass; the transparent result is never reused
        // across frames, so this only marks the current frame.
        internal void MarkSsrTransparentOutputProduced()
        {
            ssrTransparentOutputProduced = true;
        }

        internal bool TryGetPreviousViewProj(int viewIndex, out Matrix4x4 viewProj)
        {
            viewProj = Matrix4x4.identity;
            if (viewIndex < 0 || viewIndex >= m_PreviousViewProj.Length || !m_HasPreviousViewProj[viewIndex])
                return false;

            viewProj = m_PreviousViewProj[viewIndex];
            return true;
        }

        // Recorded by the first Screen Space Lighting pass of the frame so the next frame (or the
        // other eye in multipass, which owns a different entry) sees this camera's matrices.
        internal void RecordViewProj(int viewIndex, Matrix4x4 viewProj)
        {
            if (viewIndex < 0 || viewIndex >= m_PreviousViewProj.Length)
                return;

            m_PreviousViewProj[viewIndex] = viewProj;
            m_HasPreviousViewProj[viewIndex] = true;
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
            ssrOutputProduced = false;
            ssrTransparentOutputProduced = false;
            ResetSsgiHistory();
            ResetSsrPbrHistory();
            for (int i = 0; i < m_HasPreviousViewProj.Length; i++)
                m_HasPreviousViewProj[i] = false;
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

            // The release invalidated this frame's read handles. Treat the frame as history-less so
            // no pass binds a released texture; the next captured frame becomes valid again.
            hasValidHistory = false;
            readSlot = -1;
            writeSlot = bufferIndex ^ 1;
            readColorPyramid = null;
            readPreviousDepth = null;
            ssrOutputProduced = false;
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
            ReleaseSsrResources();
            ReleaseTransparentResources();
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
            for (int i = 0; i < m_HasPreviousViewProj.Length; i++)
                m_HasPreviousViewProj[i] = false;
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

        void ReleaseSsrResources()
        {
            RTHandles.Release(ssrHitPoint);
            ssrHitPoint = null;
            RTHandles.Release(ssrOutput);
            ssrOutput = null;
            ssrWidth = 0;
            ssrHeight = 0;
            m_SsrWidth = 0;
            m_SsrHeight = 0;
            m_SsrSlices = 0;
            ssrOutputProduced = false;
            ReleaseSsrPbrResources();
        }

        void ReleaseSsrPbrResources()
        {
            RTHandles.Release(ssrPbrOriginal);
            ssrPbrOriginal = null;
            for (int i = 0; i < k_HistoryBufferCount; i++)
            {
                RTHandles.Release(m_SsrPbrHistory[i]);
                m_SsrPbrHistory[i] = null;
            }

            m_SsrPbrWidth = 0;
            m_SsrPbrHeight = 0;
            m_SsrPbrSlices = 0;
            ssrPbrWidth = 0;
            ssrPbrHeight = 0;
            ssrPbrHasValidHistory = false;
            ssrPbrReadSlot = -1;
            ssrPbrWriteSlot = 0;
            ssrPbrBufferIndex = 0;
        }

        void ReleaseTransparentResources()
        {
            RTHandles.Release(transparentDepth);
            transparentDepth = null;
            RTHandles.Release(transparentNormals);
            transparentNormals = null;
            RTHandles.Release(ssrTransparentHitPoint);
            ssrTransparentHitPoint = null;
            RTHandles.Release(ssrTransparentOutput);
            ssrTransparentOutput = null;

            transparentWidth = 0;
            transparentHeight = 0;
            m_TransparentWidth = 0;
            m_TransparentHeight = 0;
            m_TransparentSlices = 0;
            ssrTransparentOutputProduced = false;
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