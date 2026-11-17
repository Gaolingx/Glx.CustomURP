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

        internal int readSlot { get; private set; }
        internal int writeSlot { get; private set; }
        internal bool sameFrameConflict => hasValidHistory && readSlot == writeSlot;

        internal int slices { get; private set; }
        internal TextureDimension dimension { get; private set; }
        internal GraphicsFormat colorFormat { get; private set; }

        // The packed depth mip chain is shared with Ground Truth Ambient Occlusion. This history only
        // keeps the current frame's view so its consumers keep one binding path.
        internal ScreenSpaceDepthPyramid depthPyramidSource { get; set; }
        internal PackedMipChainInfo depthPyramidInfo => depthPyramidSource != null ? depthPyramidSource.info : default;
        internal RTHandle depthPyramid => depthPyramidSource?.texture;
        internal ComputeBuffer depthPyramidOffsets => depthPyramidSource?.offsets;

        internal RTHandle currentColorPyramid { get; private set; }
        internal int colorPyramidMipCount { get; private set; }
        internal RTHandle readColorPyramid { get; private set; }
        internal RTHandle readPreviousDepth { get; private set; }
        internal RTHandle readPreviousNormals { get; private set; }

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
        internal RTHandle ssgiDenoiseIntermediate { get; private set; }
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

            ssgiDenoiseIntermediate = AllocSsgiColor(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsgiDenoiseIntermediate");

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
        internal bool ssrResourcesAllocated => ssrHitPoint != null && ssrOutput != null;

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
        // SSR parameter debug view
        // ---------------------------------------------------------------------------------------

        // Diagnostic values are written here instead of the material-facing SSR output, so enabling
        // the SsrPbrParams debug view never changes what the opaque GBuffer / forward shading reads.
        internal RTHandle ssrDebugParams { get; private set; }

        internal void EnsureSsrDebugResources(int width, int height, int viewCount, TextureDimension resourceDimension)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            viewCount = Mathf.Max(1, viewCount);

            if (ssrDebugParams != null &&
                m_SsrDebugWidth == width &&
                m_SsrDebugHeight == height &&
                m_SsrDebugSlices == viewCount &&
                m_SsrDebugDimension == resourceDimension)
            {
                return;
            }

            RTHandles.Release(ssrDebugParams);

            m_SsrDebugWidth = width;
            m_SsrDebugHeight = height;
            m_SsrDebugSlices = viewCount;
            m_SsrDebugDimension = resourceDimension;

            ssrDebugParams = RTHandles.Alloc(
                width, height,
                TextureWrapMode.Clamp, TextureWrapMode.Clamp,
                slices: viewCount,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                filterMode: FilterMode.Point,
                dimension: resourceDimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: "SslSsrDebugParams");
        }

        int m_SsrDebugWidth;
        int m_SsrDebugHeight;
        int m_SsrDebugSlices;
        TextureDimension m_SsrDebugDimension;

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

        // PBR accumulation owns two RGBA16F histories plus the current reproject output, the hit point and
        // the denoise intermediates. They are only allocated while the PBR algorithm is selected and
        // follow the trace resolution: full resolution, or half the screen when the volume asks for
        // the half resolution denoiser.
        //
        // Frozen contract (PLAN/TODO/02-SSRImprove, T0/T3): a history slot stores the accumulated
        // un-premultiplied radiance in rgb and the latest frame's blend weight in a, because the
        // material consumes alpha as the lerp weight. The temporal sample count therefore lives in a
        // separate single channel texture per slot; it must never share history.a. The two denoise
        // intermediates hold the temporal output (which is also copied into the history slot) and the
        // horizontal bilateral result. The previous depth and normals used for validation stay full
        // resolution because they come from the color history capture.
        internal RTHandle ssrPbrHitPoint { get; private set; }
        internal RTHandle ssrPbrOriginal { get; private set; }
        internal RTHandle ssrDenoiseIntermediate0 { get; private set; }
        internal RTHandle ssrDenoiseIntermediate1 { get; private set; }
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
        internal bool ssrPbrResourcesAllocated =>
            ssrPbrHitPoint != null && ssrPbrOriginal != null && ssrDenoiseIntermediate0 != null &&
            ssrDenoiseIntermediate1 != null && m_SsrPbrHistory[0] != null;

        internal RTHandle GetSsrPbrHistory(int slot)
        {
            return slot >= 0 && slot < k_HistoryBufferCount ? m_SsrPbrHistory[slot] : null;
        }

        internal RTHandle GetSsrPbrSampleCount(int slot)
        {
            return slot >= 0 && slot < k_HistoryBufferCount ? m_SsrPbrSampleCount[slot] : null;
        }

        internal void EnsureSsrPbrResources(
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

            if (ssrPbrResourcesAllocated &&
                m_SsrPbrScreenWidth == screenWidth &&
                m_SsrPbrScreenHeight == screenHeight &&
                m_SsrPbrWidth == traceWidth &&
                m_SsrPbrHeight == traceHeight &&
                m_SsrPbrSlices == viewCount &&
                m_SsrPbrDimension == resourceDimension)
            {
                return;
            }

            ReleaseSsrPbrResources();

            m_SsrPbrScreenWidth = screenWidth;
            m_SsrPbrScreenHeight = screenHeight;
            m_SsrPbrWidth = traceWidth;
            m_SsrPbrHeight = traceHeight;
            m_SsrPbrSlices = viewCount;
            m_SsrPbrDimension = resourceDimension;
            ssrPbrWidth = traceWidth;
            ssrPbrHeight = traceHeight;

            ssrPbrHitPoint = AllocSsrHitPoint(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsrPbrHitPoint");
            ssrPbrOriginal = AllocSsrPbrTexture(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsrPbrOriginal");
            ssrDenoiseIntermediate0 = AllocSsrPbrTexture(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsrDenoiseIntermediate0");
            ssrDenoiseIntermediate1 = AllocSsrPbrTexture(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsrDenoiseIntermediate1");
            for (int i = 0; i < k_HistoryBufferCount; i++)
            {
                m_SsrPbrHistory[i] = AllocSsrPbrTexture(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsrPbrHistory" + i);
                m_SsrPbrSampleCount[i] = AllocSsrSampleCount(traceWidth, traceHeight, resourceDimension, viewCount, "SslSsrPbrSampleCount" + i);
            }

            ssrPbrResourceGeneration++;
            ResetSsrPbrHistory();
        }

        static RTHandle AllocSsrHitPoint(int width, int height, TextureDimension dimension, int slices, string name)
        {
            return RTHandles.Alloc(
                width, height,
                TextureWrapMode.Clamp, TextureWrapMode.Clamp,
                slices: slices,
                colorFormat: GraphicsFormat.R16G16_SFloat,
                filterMode: FilterMode.Point,
                dimension: dimension,
                enableRandomWrite: true,
                useMipMap: false,
                autoGenerateMips: false,
                name: name);
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

        static RTHandle AllocSsrSampleCount(int width, int height, TextureDimension dimension, int slices, string name)
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
        readonly RTHandle[] m_SsrPbrSampleCount = new RTHandle[k_HistoryBufferCount];
        int m_SsrPbrScreenWidth;
        int m_SsrPbrScreenHeight;
        int m_SsrPbrWidth;
        int m_SsrPbrHeight;
        int m_SsrPbrSlices;
        TextureDimension m_SsrPbrDimension;

        // Previous frame view projection per XR eye. URP's motion vector history stores a single
        // matrix that alternates eyes in multipass, so a per-eye entry is required for the SSGI
        // history validation and the SSR motion reconstruction to compare like with like.
        readonly Matrix4x4[] m_PreviousViewProj = new Matrix4x4[ScreenSpaceLightingXr.k_MaxViews];
        readonly bool[] m_HasPreviousViewProj = new bool[ScreenSpaceLightingXr.k_MaxViews];
        readonly RTHandle[] m_HistoryColorPyramids = new RTHandle[k_HistoryBufferCount];
        readonly RTHandle[] m_PreviousDepths = new RTHandle[k_HistoryBufferCount];
        readonly RTHandle[] m_PreviousNormals = new RTHandle[k_HistoryBufferCount];
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

        internal RTHandle GetPreviousNormal(int slot)
        {
            return slot >= 0 && slot < k_HistoryBufferCount ? m_PreviousNormals[slot] : null;
        }

        internal bool resourcesAllocated => depthPyramid != null && currentColorPyramid != null;

        public void BeginFrame()
        {
            frameSerial++;
            readSlot = hasValidHistory ? bufferIndex : -1;
            writeSlot = bufferIndex ^ 1;
            readColorPyramid = readSlot >= 0 ? m_HistoryColorPyramids[readSlot] : null;
            readPreviousDepth = readSlot >= 0 ? m_PreviousDepths[readSlot] : null;
            readPreviousNormals = readSlot >= 0 ? m_PreviousNormals[readSlot] : null;
            ssrTransparentOutputProduced = false;
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
            readSlot = -1;
            writeSlot = bufferIndex ^ 1;
            readColorPyramid = null;
            readPreviousDepth = null;
            readPreviousNormals = null;
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
        }

        internal void EnsureResources(int viewportWidth, int viewportHeight, int viewCount, TextureDimension resourceDimension, GraphicsFormat resourceColorFormat)
        {
            viewportWidth = Mathf.Max(1, viewportWidth);
            viewportHeight = Mathf.Max(1, viewportHeight);
            viewCount = Mathf.Max(1, viewCount);

            if (currentColorPyramid != null &&
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

                // Raw copy of the camera normals, encoding preserved (signed forward normals or the
                // octahedron encoded deferred normals), so the temporal filter decodes with the same
                // keyword the trace uses.
                m_PreviousNormals[i] = RTHandles.Alloc(
                    viewportWidth,
                    viewportHeight,
                    TextureWrapMode.Clamp,
                    TextureWrapMode.Clamp,
                    slices: slices,
                    colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                    filterMode: FilterMode.Point,
                    dimension: dimension,
                    enableRandomWrite: true,
                    useMipMap: false,
                    autoGenerateMips: false,
                    name: "SslPreviousNormal" + i);
            }

            int tempWidth = Mathf.Max(1, viewportWidth >> 1);
            int tempHeight = Mathf.Max(1, viewportHeight >> 1);
            m_TempDownsample = AllocTempColor(tempWidth, tempHeight, colorFormat, dimension, slices, "SslColorPyramidDownsample");
            m_TempBlur = AllocTempColor(tempWidth, tempHeight, colorFormat, dimension, slices, "SslColorPyramidBlur");

            // The release invalidated this frame's read handles. Treat the frame as history-less so
            // no pass binds a released texture; the next captured frame becomes valid again.
            hasValidHistory = false;
            readSlot = -1;
            writeSlot = bufferIndex ^ 1;
            readColorPyramid = null;
            readPreviousDepth = null;
            readPreviousNormals = null;
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
            readSlot = -1;
            writeSlot = 0;
            readColorPyramid = null;
            readPreviousDepth = null;
            readPreviousNormals = null;
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
            RTHandles.Release(ssgiDenoiseIntermediate);
            ssgiDenoiseIntermediate = null;
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
            RTHandles.Release(ssrDebugParams);
            ssrDebugParams = null;
            ssrWidth = 0;
            ssrHeight = 0;
            m_SsrWidth = 0;
            m_SsrHeight = 0;
            m_SsrSlices = 0;
            m_SsrDebugWidth = 0;
            m_SsrDebugHeight = 0;
            m_SsrDebugSlices = 0;
            ReleaseSsrPbrResources();
        }

        void ReleaseSsrPbrResources()
        {
            RTHandles.Release(ssrPbrHitPoint);
            ssrPbrHitPoint = null;
            RTHandles.Release(ssrPbrOriginal);
            ssrPbrOriginal = null;
            RTHandles.Release(ssrDenoiseIntermediate0);
            ssrDenoiseIntermediate0 = null;
            RTHandles.Release(ssrDenoiseIntermediate1);
            ssrDenoiseIntermediate1 = null;
            for (int i = 0; i < k_HistoryBufferCount; i++)
            {
                RTHandles.Release(m_SsrPbrHistory[i]);
                m_SsrPbrHistory[i] = null;
                RTHandles.Release(m_SsrPbrSampleCount[i]);
                m_SsrPbrSampleCount[i] = null;
            }

            m_SsrPbrScreenWidth = 0;
            m_SsrPbrScreenHeight = 0;
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
            depthPyramidSource = null;

            RTHandles.Release(currentColorPyramid);
            currentColorPyramid = null;

            for (int i = 0; i < k_HistoryBufferCount; i++)
            {
                RTHandles.Release(m_HistoryColorPyramids[i]);
                m_HistoryColorPyramids[i] = null;
                RTHandles.Release(m_PreviousDepths[i]);
                m_PreviousDepths[i] = null;
                RTHandles.Release(m_PreviousNormals[i]);
                m_PreviousNormals[i] = null;
            }

            RTHandles.Release(m_TempDownsample);
            m_TempDownsample = null;
            RTHandles.Release(m_TempBlur);
            m_TempBlur = null;

            colorPyramidMipCount = 0;
            m_AllocatedWidth = 0;
            m_AllocatedHeight = 0;
            m_AllocatedSlices = 0;
        }
    }
}