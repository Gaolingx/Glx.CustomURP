using System;
using Unity.Collections;
using Unity.Mathematics;

namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// Computes and submits lighting data to the GPU.
    /// </summary>
    public class ForwardLights
    {
        static class LightConstantBuffer
        {
            public static int _MainLightPosition;
            public static int _MainLightColor;
            public static int _MainLightOcclusionProbesChannel;    // Deferred?
            public static int _MainLightLayerMask;

            public static int _AdditionalLightsCount;
            public static int _AdditionalLightsPosition;
            public static int _AdditionalLightsColor;
            public static int _AdditionalLightsAttenuation;
            public static int _AdditionalLightsSpotDir;
            public static int _AdditionalLightOcclusionProbeChannel;
            public static int _AdditionalLightsLayerMasks;
        }

        int m_AdditionalLightsBufferId;
        int m_AdditionalLightsIndicesId;

        const string k_SetupLightConstants = "Setup Light Constants";
        private static readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(k_SetupLightConstants);
        private static readonly ProfilingSampler m_ProfilingSamplerFPSetup = new ProfilingSampler("Forward+ Setup");
        private static readonly ProfilingSampler m_ProfilingSamplerFPGpuBuild = new ProfilingSampler("Cluster List GPU");
        private static readonly ProfilingSampler m_ProfilingSamplerFPGpuPrune = new ProfilingSampler("Cluster List Prune");
        private static readonly ProfilingSampler m_ProfilingSamplerFPGpuCells = new ProfilingSampler("Cluster List Cells");
        MixedLightingSetup m_MixedLightingSetup;

        Vector4[] m_AdditionalLightPositions;
        Vector4[] m_AdditionalLightColors;
        Vector4[] m_AdditionalLightAttenuations;
        Vector4[] m_AdditionalLightSpotDirections;
        Vector4[] m_AdditionalLightOcclusionProbeChannels;
        float[] m_AdditionalLightsLayerMasks;  // Unity has no support for binding uint arrays. We will use asuint() in the shader instead.

        bool m_UseStructuredBuffer;

        bool m_UseForwardPlus;
        bool m_ClusteredDeferred;
        bool m_UseClusters;
        bool m_PublishProbeAtlas;
        int m_DirectionalLightCount;
        int m_ActualTileWidth;
        int2 m_TileResolution;

        GraphicsBuffer m_ClusterWords; // [z-bin headers and masks | tile masks], in uint words

        LightCookieManager m_LightCookieManager;
        ReflectionProbeManager m_ReflectionProbeManager;
        int m_WordsPerTile;
        float m_ZBinScale;
        float m_ZBinOffset;
        int m_LightCount;
        int m_BinCount;
        int m_LightOffset;
        int m_ReflectionProbeCount;

        ClusterListGpuBuilder m_ClusterBuilder;
        GraphicsBuffer m_EmptyCellBuffer;
        CommandBuffer m_ClusterAsyncCmd;
        GraphicsFence m_ClusterFence;
        bool m_ClusterFencePending;
        ClusterItemRecord[] m_ClusterItems = Array.Empty<ClusterItemRecord>();
        ClusterListGpuParams m_ClusterGpuParams;
        float4x4 m_ClusterWorldToView;
        float4 m_ClusterViewToViewportScaleBias;
        float2 m_ClusterTileScale;
        float m_ClusterNear;
        bool m_ClusterOrthographic;

        internal struct InitParams
        {
            public LightCookieManager lightCookieManager;
            public bool forwardPlus;
            // Builds the cluster list for clustered deferred even though _FORWARD_PLUS stays off.
            public bool clusteredDeferred;
            // Required by both clustered rendering modes.
            public ComputeShader clusterBuilder;
            // Publishes the probe atlas outside Forward+ so the SSGI miss fallback can iterate the
            // visible probe list. Cluster buffers and keywords stay Forward+ only.
            public bool publishProbeAtlas;

            static internal InitParams Create()
            {
                InitParams p;
                {
                    var settings = LightCookieManager.Settings.Create();
                    var asset = UniversalRenderPipeline.asset;
                    if (asset)
                    {
                        settings.atlas.format = asset.additionalLightsCookieFormat;
                        settings.atlas.resolution = asset.additionalLightsCookieResolution;
                    }

                    p.lightCookieManager = new LightCookieManager(ref settings);
                    p.forwardPlus = false;
                    p.clusteredDeferred = false;
                    p.clusterBuilder = null;
                    p.publishProbeAtlas = false;
                }
                return p;
            }
        }

        /// <summary>
        /// Creates a new <c>ForwardLights</c> instance.
        /// </summary>
        public ForwardLights() : this(InitParams.Create()) { }

        internal ForwardLights(InitParams initParams)
        {
            m_UseStructuredBuffer = RenderingUtils.useStructuredBuffer;
            m_UseForwardPlus = initParams.forwardPlus;
            m_ClusteredDeferred = initParams.clusteredDeferred;
            m_UseClusters = m_UseForwardPlus || m_ClusteredDeferred;
            m_PublishProbeAtlas = initParams.publishProbeAtlas;
            if (m_UseClusters && initParams.clusterBuilder != null)
                m_ClusterBuilder = new ClusterListGpuBuilder(initParams.clusterBuilder, bitmapOnly: m_UseForwardPlus);

            LightConstantBuffer._MainLightPosition = Shader.PropertyToID("_MainLightPosition");
            LightConstantBuffer._MainLightColor = Shader.PropertyToID("_MainLightColor");
            LightConstantBuffer._MainLightOcclusionProbesChannel = Shader.PropertyToID("_MainLightOcclusionProbes");
            LightConstantBuffer._MainLightLayerMask = Shader.PropertyToID("_MainLightLayerMask");
            LightConstantBuffer._AdditionalLightsCount = Shader.PropertyToID("_AdditionalLightsCount");

            if (m_UseStructuredBuffer)
            {
                m_AdditionalLightsBufferId = Shader.PropertyToID("_AdditionalLightsBuffer");
                m_AdditionalLightsIndicesId = Shader.PropertyToID("_AdditionalLightsIndices");
            }
            else
            {
                LightConstantBuffer._AdditionalLightsPosition = Shader.PropertyToID("_AdditionalLightsPosition");
                LightConstantBuffer._AdditionalLightsColor = Shader.PropertyToID("_AdditionalLightsColor");
                LightConstantBuffer._AdditionalLightsAttenuation = Shader.PropertyToID("_AdditionalLightsAttenuation");
                LightConstantBuffer._AdditionalLightsSpotDir = Shader.PropertyToID("_AdditionalLightsSpotDir");
                LightConstantBuffer._AdditionalLightOcclusionProbeChannel = Shader.PropertyToID("_AdditionalLightsOcclusionProbes");
                LightConstantBuffer._AdditionalLightsLayerMasks = Shader.PropertyToID("_AdditionalLightsLayerMasks");

                int maxLights = UniversalRenderPipeline.maxVisibleAdditionalLights;
                m_AdditionalLightPositions = new Vector4[maxLights];
                m_AdditionalLightColors = new Vector4[maxLights];
                m_AdditionalLightAttenuations = new Vector4[maxLights];
                m_AdditionalLightSpotDirections = new Vector4[maxLights];
                m_AdditionalLightOcclusionProbeChannels = new Vector4[maxLights];
                m_AdditionalLightsLayerMasks = new float[maxLights];
            }

            m_LightCookieManager = initParams.lightCookieManager;
        }

        internal bool hasClusterBuilder => m_ClusterBuilder != null && m_ClusterBuilder.isCreated;

        void EnsureClusterBuffers()
        {
            int bins = UniversalRenderPipeline.maxZBinWords, tiles = UniversalRenderPipeline.maxTileWords;
            if (m_ClusterWords == null || m_ClusterWords.count != bins + tiles)
            {
                m_ClusterWords?.Dispose();
                m_ClusterWords = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bins + tiles, sizeof(uint)) { name = "URP Cluster Words" };
            }
        }

        internal ReflectionProbeManager reflectionProbeManager => m_ReflectionProbeManager;

        internal bool publishProbeAtlas => m_PublishProbeAtlas;

        // Toggled from the renderer feature every frame. Forward+ always publishes the atlas and
        // ignores the requested state.
        internal void SetProbeAtlasEnabled(bool enabled)
        {
            m_PublishProbeAtlas = enabled;
        }

        // Use the same decision as culling and pass selection; eligibility alone does not account
        // for resource, debug or RenderGraph fallback.
        bool ForwardPlusActive(ref CameraData cameraData) => m_UseForwardPlus &&
            cameraData.renderer is UniversalRenderer { renderingModeActual: RenderingMode.ForwardPlus };

        bool ShouldBuildClusters(ref CameraData cameraData) => ForwardPlusActive(ref cameraData) ||
            (m_ClusteredDeferred && cameraData.renderer is UniversalRenderer { clusteredDeferredEnabled: true });

        static float4 GetViewToViewportScaleBias(Camera camera, float4x4 viewToClip)
        {
            var viewPlaneHalfSizeInv = math.float2(viewToClip[0][0], viewToClip[1][1]);
            var centerClipSpace = camera.orthographic ? -math.float2(viewToClip[3][0], viewToClip[3][1]): math.float2(viewToClip[2][0], viewToClip[2][1]);

            return math.float4(
                viewPlaneHalfSizeInv * 0.5f,
                -centerClipSpace * 0.5f + 0.5f
            );
        }

        internal void PreSetup(ref RenderingData renderingData)
        {
            long probeStart = ClusteredDeferredCpuProbe.Begin();
            m_ClusterFencePending = false;
            if (ShouldBuildClusters(ref renderingData.cameraData))
            {
                using var _ = new ProfilingScope(null, m_ProfilingSamplerFPSetup);

                ref var cameraData = ref renderingData.cameraData;
                var camera = cameraData.camera;

                var screenResolution = math.int2(cameraData.pixelWidth, cameraData.pixelHeight);
                m_LightCount = renderingData.lightData.visibleLights.Length;
                var lightOffset = 0;
                while (lightOffset < m_LightCount && renderingData.lightData.visibleLights[lightOffset].lightType == LightType.Directional)
                {
                    lightOffset++;
                }
                m_LightCount -= lightOffset;
                m_LightOffset = lightOffset;

                m_DirectionalLightCount = lightOffset;
                if (renderingData.lightData.mainLightIndex != -1 && m_DirectionalLightCount != 0) m_DirectionalLightCount -= 1;

                var reflectionProbes = renderingData.cullResults.visibleReflectionProbes;
                var reflectionProbeCount = math.min(reflectionProbes.Length, UniversalRenderPipeline.maxVisibleReflectionProbes);
                m_ReflectionProbeCount = reflectionProbeCount;
                var itemsPerTile = m_LightCount + reflectionProbeCount;
                m_WordsPerTile = (itemsPerTile + 31) / 32;

                m_ActualTileWidth = 8 >> 1;
                do
                {
                    m_ActualTileWidth <<= 1;
                    m_TileResolution = (screenResolution + m_ActualTileWidth - 1) / m_ActualTileWidth;
                }
                while ((m_TileResolution.x * m_TileResolution.y * m_WordsPerTile) > UniversalRenderPipeline.maxTileWords);

                if (!camera.orthographic)
                {
                    // Use to calculate binIndex = log2(z) * zBinScale + zBinOffset
                    m_ZBinScale = UniversalRenderPipeline.maxZBinWords / ((math.log2(camera.farClipPlane) - math.log2(camera.nearClipPlane)) * (2 + m_WordsPerTile));
                    m_ZBinOffset = -math.log2(camera.nearClipPlane) * m_ZBinScale;
                    m_BinCount = (int)(math.log2(camera.farClipPlane) * m_ZBinScale + m_ZBinOffset);
                }
                else
                {
                    // Use to calculate binIndex = z * zBinScale + zBinOffset
                    m_ZBinScale = UniversalRenderPipeline.maxZBinWords / ((camera.farClipPlane - camera.nearClipPlane) * (2 + m_WordsPerTile));
                    m_ZBinOffset = -camera.nearClipPlane * m_ZBinScale;
                    m_BinCount = (int)(camera.farClipPlane * m_ZBinScale + m_ZBinOffset);
                }

                // Necessary to avoid negative bin count when the farClipPlane is set to Infinity in the editor.
                m_BinCount = Math.Max(m_BinCount, 0);

                // Should probe come after otherProbe?
                static bool IsProbeGreater(VisibleReflectionProbe probe, VisibleReflectionProbe otherProbe)
                {
                    return probe.importance < otherProbe.importance ||
                        (probe.importance == otherProbe.importance && probe.bounds.extents.sqrMagnitude > otherProbe.bounds.extents.sqrMagnitude);
                }

                for (var i = 1; i < reflectionProbeCount; i++)
                {
                    var probe = reflectionProbes[i];
                    var j = i - 1;
                    while (j >= 0 && IsProbeGreater(reflectionProbes[j], probe))
                    {
                        reflectionProbes[j + 1] = reflectionProbes[j];
                        j--;
                    }

                    reflectionProbes[j + 1] = probe;
                }

                EnsureClusterBuffers();
                // SetupFinalPassDebug can inspect the Forward+ atlas before SetupLights.
                if (m_UseForwardPlus && !m_ReflectionProbeManager.isCreated)
                    m_ReflectionProbeManager = ReflectionProbeManager.Create();

                m_ClusterWorldToView = cameraData.GetViewMatrix(0);
                m_ClusterViewToViewportScaleBias = GetViewToViewportScaleBias(camera, cameraData.GetProjectionMatrix(0));
                m_ClusterTileScale = (float2)screenResolution / m_ActualTileWidth;
                m_ClusterNear = camera.nearClipPlane;
                m_ClusterOrthographic = camera.orthographic;
            }
            ClusteredDeferredCpuProbe.End(probeStart, ref ClusteredDeferredCpuProbe.preSetup);
        }

        // D3D11 has no async queue; there the dispatches stay on the graphics queue in the same
        // order, which produces the same bitmaps (G1c parity gate).
        bool ShouldRunClusterListAsync()
        {
            return ClusterListGpuBuilder.asyncCompute && SystemInfo.supportsAsyncCompute;
        }

        CommandBuffer GetClusterAsyncCommandBuffer()
        {
            if (m_ClusterAsyncCmd == null)
                m_ClusterAsyncCmd = new CommandBuffer { name = "CD Cluster List (Async)" };

            // Clear() drops the execution flags, so re-assert them on every reuse; without this the
            // player logs an async-queue error and shows the development console over the frame.
            m_ClusterAsyncCmd.SetExecutionFlags(CommandBufferExecutionFlags.AsyncCompute);
            return m_ClusterAsyncCmd;
        }

        // Consumers of the cluster word buffer call this before reading them. Only the first consumer
        // of a scheduled async build waits; later ones would only re-wait on a passed fence.
        internal void WaitForClusterList(CommandBuffer cmd)
        {
            if (!m_ClusterFencePending)
                return;

            cmd.WaitOnAsyncGraphicsFence(m_ClusterFence, SynchronisationStageFlags.ComputeProcessing);
            m_ClusterFencePending = false;
        }

        // G2: narrow the published tile bitmap with the tile depth slice. Must run after the build
        // fence (reads the built bitmap) and after GBuffer (reads its depth). Forward+ stays conservative.
        internal void PruneClusterList(CommandBuffer cmd, RenderTargetIdentifier depthTexture, Vector4 zBufferParams)
        {
            // G3 cells already resolve depth per (tile, zbin); refining the bitmap would only add a
            // second full-screen depth scan before the cell build reads it.
            if (!m_ClusteredDeferred || m_ClusterBuilder == null || m_ClusterWords == null ||
                !ClusterListGpuBuilder.pruneByDepth || ClusterListGpuBuilder.compactLists)
                return;

            using (new ProfilingScope(cmd, m_ProfilingSamplerFPGpuPrune))
                m_ClusterBuilder.Prune(cmd, m_ClusterGpuParams, depthTexture, zBufferParams, m_ClusterWords);
        }

        // The lighting kernel needs valid SRVs even when compact cells are disabled.
        void PublishCompactCellBindings(CommandBuffer cmd, in ClusterListGpuParams p, bool enableCells)
        {
            if (enableCells && p.items != null && m_ClusterBuilder != null && m_ClusterBuilder.isCreated)
            {
                m_ClusterBuilder.BindCompact(cmd, p);
                return;
            }

            if (m_EmptyCellBuffer == null)
                m_EmptyCellBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4) { name = "CD Empty Cell Buffer" };
            cmd.SetGlobalBuffer("_CdCellHeaders", m_EmptyCellBuffer);
            cmd.SetGlobalBuffer("_CdCellItems", m_EmptyCellBuffer);
            cmd.SetGlobalBuffer("_CdTileCellInfo", m_EmptyCellBuffer);
            cmd.SetGlobalInt("_CdClusterCellMax", 0);
        }

        // G3: builds the per-(tile, zbin) compact lists from the published bitmaps and the GBuffer
        // depth copy. Runs after the build fence and before the lighting dispatch.
        internal void BuildClusterCells(CommandBuffer cmd, RenderTargetIdentifier depthTexture, Vector4 zBufferParams)
        {
            if (!m_ClusteredDeferred || m_ClusterBuilder == null || m_ClusterWords == null ||
                !ClusterListGpuBuilder.compactLists)
                return;

            using (new ProfilingScope(cmd, m_ProfilingSamplerFPGpuCells))
                m_ClusterBuilder.BuildCells(cmd, m_ClusterGpuParams, depthTexture, zBufferParams,
                    m_ClusterWords);
        }

        /// <summary>
        /// Sets up the keywords and data for forward lighting.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="renderingData"></param>
        public void Setup(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            int additionalLightsCount = renderingData.lightData.additionalLightsCount;
            bool additionalLightsPerVertex = renderingData.lightData.shadeAdditionalLightsPerVertex;
            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(null, m_ProfilingSampler))
            {
                bool forwardPlus = ForwardPlusActive(ref renderingData.cameraData);
                if (forwardPlus || m_PublishProbeAtlas)
                {
                    // The feature toggle can be enabled after the renderer was created; allocate
                    // lazily instead of requiring a renderer rebuild.
                    if (!m_ReflectionProbeManager.isCreated)
                        m_ReflectionProbeManager = ReflectionProbeManager.Create();

                    m_ReflectionProbeManager.UpdateGpuData(cmd, ref renderingData);
                }

                bool buildClusters = ShouldBuildClusters(ref renderingData.cameraData);
                if (buildClusters)
                {
                    var fp0 = math.float4(m_ZBinScale, m_ZBinOffset, m_LightCount, m_DirectionalLightCount);
                    var fp1 = math.float4(renderingData.cameraData.pixelRect.size / m_ActualTileWidth, m_TileResolution.x, m_WordsPerTile);
                    var fp2 = math.float4(m_BinCount, m_TileResolution.x * m_TileResolution.y, 0, 0);
                    cmd.SetGlobalVector("_FPParams0", fp0);
                    cmd.SetGlobalVector("_FPParams1", fp1);
                    cmd.SetGlobalVector("_FPParams2", fp2);

                    using (new ProfilingScope(null, m_ProfilingSamplerFPGpuBuild))
                    {
                        int itemCount = ClusterItemStream.Build(ref renderingData, m_LightOffset, m_LightCount,
                            m_ReflectionProbeCount, ref m_ClusterItems);
                        var gpuParams = new ClusterListGpuParams
                        {
                            items = m_ClusterItems,
                            itemCount = itemCount,
                            lightCount = m_LightCount,
                            binCount = m_BinCount,
                            wordsPerTile = m_WordsPerTile,
                            zBinScale = m_ZBinScale,
                            zBinOffset = m_ZBinOffset,
                            zBinWordCount = UniversalRenderPipeline.maxZBinWords,
                            tileWordCount = UniversalRenderPipeline.maxTileWords,
                            tileCount = m_TileResolution,
                            tileWidth = m_ActualTileWidth,
                            screenResolution = math.int2(renderingData.cameraData.pixelWidth, renderingData.cameraData.pixelHeight),
                            tileScale = m_ClusterTileScale,
                            viewToViewportScaleBias = (Vector4)m_ClusterViewToViewportScaleBias,
                            worldToView = m_ClusterWorldToView,
                            near = m_ClusterNear,
                            orthographic = m_ClusterOrthographic,
                        };
                        m_ClusterGpuParams = gpuParams;

                        if (m_ClusteredDeferred && ShouldRunClusterListAsync())
                        {
                            // Self-contained async submission: upload and dispatches share the
                            // compute queue, so no cross-queue upload fence or mid-frame graphics
                            // flush is needed. Only the completion fence crosses back.
                            var asyncCmd = GetClusterAsyncCommandBuffer();
                            m_ClusterBuilder.Upload(asyncCmd, m_ClusterItems, itemCount);
                            m_ClusterBuilder.Dispatch(asyncCmd, gpuParams, m_ClusterWords);
                            m_ClusterFence = asyncCmd.CreateGraphicsFence(
                                GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.ComputeProcessing);
                            m_ClusterFencePending = true;
                            context.ExecuteCommandBufferAsync(asyncCmd, ComputeQueueType.Background);
                            asyncCmd.Clear();
                        }
                        else
                        {
                            m_ClusterBuilder.Upload(cmd, m_ClusterItems, itemCount);
                            m_ClusterBuilder.Dispatch(cmd, gpuParams, m_ClusterWords);
                        }

                        cmd.SetGlobalBuffer(ClusterListGpuBuilder.wordsId, m_ClusterWords);
                        cmd.SetGlobalInteger(ClusterListGpuBuilder.tileOffsetId, gpuParams.zBinWordCount);

                        // G3: the cell buffers and their enable switch are read by the lighting
                        // compute. Bound here so the later ClusteredDeferredPass dispatch does not
                        // depend on per-pass state; the cells themselves are filled after GBuffer.
                        if (m_ClusteredDeferred)
                            PublishCompactCellBindings(cmd, gpuParams, ClusterListGpuBuilder.compactLists);
                    }

                    if (ClusterListParityCapture.enabled)
                    {
                        var snapshot = CreateClusterSnapshot(ref renderingData, fp0, fp1, fp2);
                        // On the async path the readback must wait for the compute queue;
                        // the sync path is already ordered on this command buffer.
                        if (m_ClusterFencePending)
                            cmd.WaitOnAsyncGraphicsFence(m_ClusterFence, SynchronisationStageFlags.ComputeProcessing);
                        ClusterListParityCapture.RequestGpuCapture(cmd, snapshot, m_ClusterWords, m_ClusterGpuParams.zBinWordCount);
                    }
                }

                long constantsStart = ClusteredDeferredCpuProbe.Begin();
                SetupShaderLightConstants(cmd, ref renderingData);
                ClusteredDeferredCpuProbe.End(constantsStart, ref ClusteredDeferredCpuProbe.lightConstants);

                bool lightCountCheck = (renderingData.cameraData.renderer.stripAdditionalLightOffVariants && renderingData.lightData.supportsAdditionalLights) || additionalLightsCount > 0;
                // Forward-drawn objects read the cluster list only on frames that actually built it,
                // so cameras falling back to ordinary Forward keep per-object lights.
                bool clusterForwardLights = buildClusters && m_ClusteredDeferred && !forwardPlus &&
                                            ClusteredDeferredPass.forwardClusterLights;
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.ClusteredDeferredLights, clusterForwardLights);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.AdditionalLightsVertex,
                    lightCountCheck && additionalLightsPerVertex && !forwardPlus && !clusterForwardLights);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.AdditionalLightsPixel,
                    lightCountCheck && !forwardPlus && (!additionalLightsPerVertex || clusterForwardLights));
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.ForwardPlus, forwardPlus);

                bool isShadowMask = renderingData.lightData.supportsMixedLighting && m_MixedLightingSetup == MixedLightingSetup.ShadowMask;
                bool isShadowMaskAlways = isShadowMask && QualitySettings.shadowmaskMode == ShadowmaskMode.Shadowmask;
                bool isSubtractive = renderingData.lightData.supportsMixedLighting && m_MixedLightingSetup == MixedLightingSetup.Subtractive;
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.LightmapShadowMixing, isSubtractive || isShadowMaskAlways);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.ShadowsShadowMask, isShadowMask);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.MixedLightingSubtractive, isSubtractive); // Backward compatibility

                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.ReflectionProbeBlending, renderingData.lightData.reflectionProbeBlending);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.ReflectionProbeBoxProjection, renderingData.lightData.reflectionProbeBoxProjection);

                var asset = UniversalRenderPipeline.asset;

                // TODO: If we can robustly detect LIGHTMAP_ON, we can skip SH logic.
                var shMode = PlatformAutoDetect.ShAutoDetect(asset.shEvalMode);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.EVALUATE_SH_MIXED, shMode == ShEvalMode.Mixed);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.EVALUATE_SH_VERTEX, shMode == ShEvalMode.PerVertex);

                bool lightLayers = renderingData.lightData.supportsLightLayers;
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.LightLayers, lightLayers && !CoreUtils.IsSceneLightingDisabled(renderingData.cameraData.camera));

                if (m_LightCookieManager != null)
                {
                    m_LightCookieManager.Setup(context, cmd, ref renderingData.lightData);
                }
                else
                {
                    CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.LightCookies, false);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
        }

        // Capture metadata alongside the ordered GPU bitmap readback.
        ClusterListSnapshot CreateClusterSnapshot(ref RenderingData renderingData, float4 fp0, float4 fp1, float4 fp2)
        {
            var snapshot = new ClusterListSnapshot();
            snapshot.zBinScale = m_ZBinScale;
            snapshot.zBinOffset = m_ZBinOffset;
            snapshot.wordsPerTile = m_WordsPerTile;
            snapshot.binCount = m_BinCount;
            snapshot.lightCount = m_LightCount;
            snapshot.directionalLightCount = m_DirectionalLightCount;
            snapshot.viewCount = 1; // Multiview falls back before culling.
            snapshot.tileWidth = m_ActualTileWidth;
            snapshot.tileResolution = new Vector2Int(m_TileResolution.x, m_TileResolution.y);
            snapshot.screenResolution = new Vector2Int(renderingData.cameraData.pixelWidth, renderingData.cameraData.pixelHeight);
            snapshot.fpParams = new[] { fp0.x, fp0.y, fp0.z, fp0.w, fp1.x, fp1.y, fp1.z, fp1.w, fp2.x, fp2.y, fp2.z, fp2.w };
            snapshot.items = ClusterItemStream.Build(ref renderingData, m_LightOffset, m_LightCount, m_ReflectionProbeCount);
            return snapshot;
        }

        internal void Cleanup()
        {
            if (m_UseClusters)
            {
                m_ClusterWords?.Dispose();
                m_ClusterWords = null;
                m_ClusterAsyncCmd?.Dispose();
                m_ClusterAsyncCmd = null;
                m_ClusterFencePending = false;
                m_ClusterBuilder?.Dispose();
                m_EmptyCellBuffer?.Dispose();
                m_EmptyCellBuffer = null;
            }

            // The manager is shared by Forward+ and the non-clustered atlas path; release it
            // whenever it was created.
            if (m_ReflectionProbeManager.isCreated)
                m_ReflectionProbeManager.Dispose();

            m_LightCookieManager?.Dispose();
            m_LightCookieManager = null;
        }

        void InitializeLightConstants(NativeArray<VisibleLight> lights, int lightIndex, out Vector4 lightPos, out Vector4 lightColor, out Vector4 lightAttenuation, out Vector4 lightSpotDir, out Vector4 lightOcclusionProbeChannel, out uint lightLayerMask, out bool isSubtractive)
        {
            UniversalRenderPipeline.InitializeLightConstants_Common(lights, lightIndex, out lightPos, out lightColor, out lightAttenuation, out lightSpotDir, out lightOcclusionProbeChannel);
            lightLayerMask = 0;
            isSubtractive = false;

            // When no lights are visible, main light will be set to -1.
            // In this case we initialize it to default values and return
            if (lightIndex < 0)
                return;

            ref VisibleLight lightData = ref lights.UnsafeElementAtMutable(lightIndex);
            Light light = lightData.light;
            var lightBakingOutput = light.bakingOutput;
            isSubtractive = lightBakingOutput.isBaked && lightBakingOutput.lightmapBakeType == LightmapBakeType.Mixed && lightBakingOutput.mixedLightingMode == MixedLightingMode.Subtractive;

            if (light == null)
                return;

            if (lightBakingOutput.lightmapBakeType == LightmapBakeType.Mixed &&
                lightData.light.shadows != LightShadows.None &&
                m_MixedLightingSetup == MixedLightingSetup.None)
            {
                switch (lightBakingOutput.mixedLightingMode)
                {
                    case MixedLightingMode.Subtractive:
                        m_MixedLightingSetup = MixedLightingSetup.Subtractive;
                        break;
                    case MixedLightingMode.Shadowmask:
                        m_MixedLightingSetup = MixedLightingSetup.ShadowMask;
                        break;
                }
            }

            var additionalLightData = light.GetUniversalAdditionalLightData();
            lightLayerMask = RenderingLayerUtils.ToValidRenderingLayers(additionalLightData.renderingLayers);
        }

        void SetupShaderLightConstants(CommandBuffer cmd, ref RenderingData renderingData)
        {
            m_MixedLightingSetup = MixedLightingSetup.None;

            // Main light has an optimized shader path for main light. This will benefit games that only care about a single light.
            // Universal pipeline also supports only a single shadow light, if available it will be the main light.
            SetupMainLightConstants(cmd, ref renderingData.lightData);
            SetupAdditionalLightConstants(cmd, ref renderingData);
        }

        void SetupMainLightConstants(CommandBuffer cmd, ref LightData lightData)
        {
            Vector4 lightPos, lightColor, lightAttenuation, lightSpotDir, lightOcclusionChannel;
            uint lightLayerMask;
            bool isSubtractive;
            InitializeLightConstants(lightData.visibleLights, lightData.mainLightIndex, out lightPos, out lightColor, out lightAttenuation, out lightSpotDir, out lightOcclusionChannel, out lightLayerMask, out isSubtractive);
            lightColor.w = isSubtractive ? 0f : 1f;

            cmd.SetGlobalVector(LightConstantBuffer._MainLightPosition, lightPos);
            cmd.SetGlobalVector(LightConstantBuffer._MainLightColor, lightColor);
            cmd.SetGlobalVector(LightConstantBuffer._MainLightOcclusionProbesChannel, lightOcclusionChannel);
            cmd.SetGlobalInt(LightConstantBuffer._MainLightLayerMask, (int)lightLayerMask);
        }

        void SetupAdditionalLightConstants(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ref LightData lightData = ref renderingData.lightData;
            var cullResults = renderingData.cullResults;
            var lights = lightData.visibleLights;
            int maxAdditionalLightsCount = UniversalRenderPipeline.maxVisibleAdditionalLights;
            int additionalLightsCount = SetupPerObjectLightIndices(cullResults, ref lightData, ForwardPlusActive(ref renderingData.cameraData));
            if (additionalLightsCount > 0)
            {
                if (m_UseStructuredBuffer)
                {
                    NativeArray<ShaderInput.LightData> additionalLightsData = new NativeArray<ShaderInput.LightData>(additionalLightsCount, Allocator.Temp);
                    for (int i = 0, lightIter = 0; i < lights.Length && lightIter < maxAdditionalLightsCount; ++i)
                    {
                        VisibleLight light = lights[i];
                        if (lightData.mainLightIndex != i)
                        {
                            ShaderInput.LightData data;
                            InitializeLightConstants(lights, i,
                                out data.position, out data.color, out data.attenuation,
                                out data.spotDirection, out data.occlusionProbeChannels,
                                out data.layerMask, out _);
                            additionalLightsData[lightIter] = data;
                            lightIter++;
                        }
                    }

                    var lightDataBuffer = ShaderData.instance.GetLightDataBuffer(additionalLightsCount);
                    lightDataBuffer.SetData(additionalLightsData);

                    int lightIndices = cullResults.lightAndReflectionProbeIndexCount;
                    var lightIndicesBuffer = ShaderData.instance.GetLightIndicesBuffer(lightIndices);

                    cmd.SetGlobalBuffer(m_AdditionalLightsBufferId, lightDataBuffer);
                    cmd.SetGlobalBuffer(m_AdditionalLightsIndicesId, lightIndicesBuffer);

                    additionalLightsData.Dispose();
                }
                else
                {
                    for (int i = 0, lightIter = 0; i < lights.Length && lightIter < maxAdditionalLightsCount; ++i)
                    {
                        if (lightData.mainLightIndex != i)
                        {
                            InitializeLightConstants(
                                lights,
                                i,
                                out m_AdditionalLightPositions[lightIter],
                                out m_AdditionalLightColors[lightIter],
                                out m_AdditionalLightAttenuations[lightIter],
                                out m_AdditionalLightSpotDirections[lightIter],
                                out m_AdditionalLightOcclusionProbeChannels[lightIter],
                                out uint lightLayerMask,
                                out var isSubtractive);

                            m_AdditionalLightsLayerMasks[lightIter] = math.asfloat(lightLayerMask);
                            m_AdditionalLightColors[lightIter].w = isSubtractive ? 1f : 0f;
                            lightIter++;
                        }
                    }

                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsPosition, m_AdditionalLightPositions);
                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsColor, m_AdditionalLightColors);
                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsAttenuation, m_AdditionalLightAttenuations);
                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsSpotDir, m_AdditionalLightSpotDirections);
                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightOcclusionProbeChannel, m_AdditionalLightOcclusionProbeChannels);
                    cmd.SetGlobalFloatArray(LightConstantBuffer._AdditionalLightsLayerMasks, m_AdditionalLightsLayerMasks);
                }

                cmd.SetGlobalVector(LightConstantBuffer._AdditionalLightsCount, new Vector4(lightData.maxPerObjectAdditionalLightsCount,
                    0.0f, 0.0f, 0.0f));
            }
            else
            {
                cmd.SetGlobalVector(LightConstantBuffer._AdditionalLightsCount, Vector4.zero);
            }
        }

        int SetupPerObjectLightIndices(CullingResults cullResults, ref LightData lightData, bool forwardPlus)
        {
            if (lightData.additionalLightsCount == 0 || forwardPlus)
                return lightData.additionalLightsCount;

            var perObjectLightIndexMap = cullResults.GetLightIndexMap(Allocator.Temp);
            int globalDirectionalLightsCount = 0;
            int additionalLightsCount = 0;

            // Disable all directional lights from the perobject light indices
            // Pipeline handles main light globally and there's no support for additional directional lights atm.
            int maxVisibleAdditionalLightsCount = UniversalRenderPipeline.maxVisibleAdditionalLights;
            int len = lightData.visibleLights.Length;
            for (int i = 0; i < len; ++i)
            {
                if (additionalLightsCount >= maxVisibleAdditionalLightsCount)
                    break;

                if (i == lightData.mainLightIndex)
                {
                    perObjectLightIndexMap[i] = -1;
                    ++globalDirectionalLightsCount;
                }
                else
                {
                    perObjectLightIndexMap[i] -= globalDirectionalLightsCount;
                    ++additionalLightsCount;
                }
            }

            // Disable all remaining lights we cannot fit into the global light buffer.
            for (int i = globalDirectionalLightsCount + additionalLightsCount; i < perObjectLightIndexMap.Length; ++i)
                perObjectLightIndexMap[i] = -1;

            cullResults.SetLightIndexMap(perObjectLightIndexMap);

            if (m_UseStructuredBuffer && additionalLightsCount > 0)
            {
                int lightAndReflectionProbeIndices = cullResults.lightAndReflectionProbeIndexCount;
                Assertions.Assert.IsTrue(lightAndReflectionProbeIndices > 0, "Pipelines configures additional lights but per-object light and probe indices count is zero.");
                cullResults.FillLightAndReflectionProbeIndices(ShaderData.instance.GetLightIndicesBuffer(lightAndReflectionProbeIndices));
            }

            perObjectLightIndexMap.Dispose();
            return additionalLightsCount;
        }
    }
}
