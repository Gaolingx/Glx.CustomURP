using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;

namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// Computes and submits lighting data to the GPU.
    /// </summary>
    public class ForwardLights
    {
        static class LightConstantBuffer
        {
            public static int _MainLightPosition;   // DeferredLights.LightConstantBuffer also refers to the same ShaderPropertyID - TODO: move this definition to a common location shared by other UniversalRP classes
            public static int _MainLightColor;      // DeferredLights.LightConstantBuffer also refers to the same ShaderPropertyID - TODO: move this definition to a common location shared by other UniversalRP classes
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
        private static readonly ProfilingSampler m_ProfilingSamplerFPComplete = new ProfilingSampler("Forward+ Complete");
        private static readonly ProfilingSampler m_ProfilingSamplerFPUpload = new ProfilingSampler("Forward+ Upload");
        private static readonly ProfilingSampler m_ProfilingSamplerFPGpuBuild = new ProfilingSampler("Cluster List GPU");
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

        JobHandle m_CullingHandle;
        NativeArray<uint> m_ZBins;
        GraphicsBuffer m_ZBinsBuffer;
        NativeArray<uint> m_TileMasks;
        GraphicsBuffer m_TileMasksBuffer;

        LightCookieManager m_LightCookieManager;
        ReflectionProbeManager m_ReflectionProbeManager;
        int m_WordsPerTile;
        float m_ZBinScale;
        float m_ZBinOffset;
        int m_LightCount;
        int m_BinCount;
        // PreSetup state the parity capture needs to describe the item stream; the jobs do not read it.
        int m_LightOffset;
        int m_ViewCount;
        int m_ReflectionProbeCount;

        // G1: when set, the two bitmaps are built by ClusterBuilder.compute instead of the CPU jobs.
        ClusterListGpuBuilder m_ClusterBuilder;
        bool m_GpuClusterList;
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
            // Builds the cluster list on the GPU (G1). Null keeps the CPU jobs in charge.
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
            if (m_ClusteredDeferred && initParams.clusterBuilder != null)
                m_ClusterBuilder = new ClusterListGpuBuilder(initParams.clusterBuilder);

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

            if (m_UseClusters)
                CreateForwardPlusBuffers();

            if (m_UseForwardPlus || m_PublishProbeAtlas)
                m_ReflectionProbeManager = ReflectionProbeManager.Create();

            m_LightCookieManager = initParams.lightCookieManager;
        }

        void CreateForwardPlusBuffers()
        {
            m_ZBins = new NativeArray<uint>(UniversalRenderPipeline.maxZBinWords, Allocator.Persistent);
            m_TileMasks = new NativeArray<uint>(UniversalRenderPipeline.maxTileWords, Allocator.Persistent);
            if (m_UseForwardPlus)
            {
                m_ZBinsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Constant, UniversalRenderPipeline.maxZBinWords / 4, UnsafeUtility.SizeOf<float4>());
                m_ZBinsBuffer.name = "URP Z-Bin Buffer";
                m_TileMasksBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Constant, UniversalRenderPipeline.maxTileWords / 4, UnsafeUtility.SizeOf<float4>());
                m_TileMasksBuffer.name = "URP Tile Buffer";
            }
            else
            {
                // Clustered deferred reads the same words through a structured buffer so the list can
                // be built by a compute dispatch; a constant buffer cannot be a UAV.
                m_ZBinsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, UniversalRenderPipeline.maxZBinWords, sizeof(uint));
                m_ZBinsBuffer.name = "URP Z-Bin Buffer";
                m_TileMasksBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, UniversalRenderPipeline.maxTileWords, sizeof(uint));
                m_TileMasksBuffer.name = "URP Tile Buffer";
            }
        }

        internal ReflectionProbeManager reflectionProbeManager => m_ReflectionProbeManager;

        internal bool publishProbeAtlas => m_PublishProbeAtlas;

        // Toggled from the renderer feature every frame. Forward+ always publishes the atlas and
        // ignores the requested state.
        internal void SetProbeAtlasEnabled(bool enabled)
        {
            if (m_UseForwardPlus)
                return;

            m_PublishProbeAtlas = enabled;
        }

        static int AlignByteCount(int count, int align) => align * ((count + align - 1) / align);

        // Forward+ always builds clusters. Clustered deferred only needs them for base game cameras;
        // preview and overlay cameras fall back to stencil deferred.
        bool ShouldBuildClusters(ref CameraData cameraData)
        {
            if (!m_UseClusters)
                return false;
            if (!m_ClusteredDeferred)
                return true;
            return ClusteredDeferredPass.IsEligibleCamera(ref cameraData);
        }

        // Completes the cluster jobs scheduled by PreSetup. Clustered deferred calls this before
        // the deferred setup reads the culling light/probe arrays; upstream Forward+ waits until
        // Setup because nothing else touches those arrays in between.
        internal void CompleteClusterJobs()
        {
            m_CullingHandle.Complete();
        }

        // Calculate view planes and viewToViewportScaleBias. This handles projection center in case the projection is off-centered
        void GetViewParams(Camera camera, float4x4 viewToClip, out float viewPlaneBot, out float viewPlaneTop, out float4 viewToViewportScaleBias)
        {
            // We want to calculate `fovHalfHeight = tan(fov / 2)`
            // `projection[1][1]` contains `1 / tan(fov / 2)`
            var viewPlaneHalfSizeInv = math.float2(viewToClip[0][0], viewToClip[1][1]);
            var viewPlaneHalfSize = math.rcp(viewPlaneHalfSizeInv);
            var centerClipSpace = camera.orthographic ? -math.float2(viewToClip[3][0], viewToClip[3][1]): math.float2(viewToClip[2][0], viewToClip[2][1]);

            viewPlaneBot = centerClipSpace.y * viewPlaneHalfSize.y - viewPlaneHalfSize.y;
            viewPlaneTop = centerClipSpace.y * viewPlaneHalfSize.y + viewPlaneHalfSize.y;
            viewToViewportScaleBias = math.float4(
                viewPlaneHalfSizeInv * 0.5f,
                -centerClipSpace * 0.5f + 0.5f
            );
        }

        internal void PreSetup(ref RenderingData renderingData)
        {
            long probeStart = ClusteredDeferredCpuProbe.Begin();
            if (ShouldBuildClusters(ref renderingData.cameraData))
            {
                using var _ = new ProfilingScope(null, m_ProfilingSamplerFPSetup);

                if (!m_CullingHandle.IsCompleted)
                {
                    throw new InvalidOperationException("Forward+ jobs have not completed yet.");
                }

                if (m_TileMasks.Length != UniversalRenderPipeline.maxTileWords)
                {
                    m_ZBins.Dispose();
                    m_ZBinsBuffer.Dispose();
                    m_TileMasks.Dispose();
                    m_TileMasksBuffer.Dispose();
                    CreateForwardPlusBuffers();
                }
                else
                {
                    unsafe
                    {
                        UnsafeUtility.MemClear(m_ZBins.GetUnsafePtr(), m_ZBins.Length * sizeof(uint));
                        UnsafeUtility.MemClear(m_TileMasks.GetUnsafePtr(), m_TileMasks.Length * sizeof(uint));
                    }
                }

                ref var cameraData = ref renderingData.cameraData;
                var camera = cameraData.camera;

                var screenResolution = math.int2(cameraData.pixelWidth, cameraData.pixelHeight);
#if ENABLE_VR && ENABLE_XR_MODULE
                var viewCount = cameraData.xr.enabled && cameraData.xr.singlePassEnabled ? 2 : 1;
#else
                var viewCount = 1;
#endif
                m_ViewCount = viewCount;

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

                var visibleLights = renderingData.lightData.visibleLights.GetSubArray(lightOffset, m_LightCount);
                var reflectionProbes = renderingData.cullResults.visibleReflectionProbes;
                var reflectionProbeCount = math.min(reflectionProbes.Length, UniversalRenderPipeline.maxVisibleReflectionProbes);
                m_ReflectionProbeCount = reflectionProbeCount;
                var itemsPerTile = visibleLights.Length + reflectionProbeCount;
                m_WordsPerTile = (itemsPerTile + 31) / 32;

                m_ActualTileWidth = 8 >> 1;
                do
                {
                    m_ActualTileWidth <<= 1;
                    m_TileResolution = (screenResolution + m_ActualTileWidth - 1) / m_ActualTileWidth;
                }
                while ((m_TileResolution.x * m_TileResolution.y * m_WordsPerTile * viewCount) > UniversalRenderPipeline.maxTileWords);

                if (!camera.orthographic)
                {
                    // Use to calculate binIndex = log2(z) * zBinScale + zBinOffset
                    m_ZBinScale = (UniversalRenderPipeline.maxZBinWords / viewCount) / ((math.log2(camera.farClipPlane) - math.log2(camera.nearClipPlane)) * (2 + m_WordsPerTile));
                    m_ZBinOffset = -math.log2(camera.nearClipPlane) * m_ZBinScale;
                    m_BinCount = (int)(math.log2(camera.farClipPlane) * m_ZBinScale + m_ZBinOffset);
                }
                else
                {
                    // Use to calculate binIndex = z * zBinScale + zBinOffset
                    m_ZBinScale = (UniversalRenderPipeline.maxZBinWords / viewCount) / ((camera.farClipPlane - camera.nearClipPlane) * (2 + m_WordsPerTile));
                    m_ZBinOffset = -camera.nearClipPlane * m_ZBinScale;
                    m_BinCount = (int)(camera.farClipPlane * m_ZBinScale + m_ZBinOffset);
                }

                // Necessary to avoid negative bin count when the farClipPlane is set to Infinity in the editor.
                m_BinCount = Math.Max(m_BinCount, 0);

                var worldToViews = new Fixed2<float4x4>(cameraData.GetViewMatrix(0), cameraData.GetViewMatrix(math.min(1, viewCount - 1)));
                var viewToClips = new Fixed2<float4x4>(cameraData.GetProjectionMatrix(0), cameraData.GetProjectionMatrix(math.min(1, viewCount - 1)));

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

                // The GPU builder consumes the projection parameters without the jobs, so they are
                // computed before the branch.
                GetViewParams(camera, viewToClips[0], out float viewPlaneBottom0, out float viewPlaneTop0, out float4 viewToViewportScaleBias0);
                GetViewParams(camera, viewToClips[1], out float viewPlaneBottom1, out float viewPlaneTop1, out float4 viewToViewportScaleBias1);

                m_GpuClusterList = ShouldBuildClusterListOnGpu(viewCount);
                if (m_GpuClusterList)
                {
                    m_ClusterWorldToView = worldToViews[0];
                    m_ClusterViewToViewportScaleBias = viewToViewportScaleBias0;
                    m_ClusterTileScale = (float2)screenResolution / m_ActualTileWidth;
                    m_ClusterNear = camera.nearClipPlane;
                    m_ClusterOrthographic = camera.orthographic;
                    m_CullingHandle = default;
                }
                else
                {
                    var minMaxZs = new NativeArray<float2>(itemsPerTile * viewCount, Allocator.TempJob);

                    var lightMinMaxZJob = new LightMinMaxZJob
                    {
                        worldToViews = worldToViews,
                        lights = visibleLights,
                        minMaxZs = minMaxZs.GetSubArray(0, m_LightCount * viewCount)
                    };
                    // Innerloop batch count of 32 is not special, just a handwavy amount to not have too much scheduling overhead nor too little parallelism.
                    var lightMinMaxZHandle = lightMinMaxZJob.ScheduleParallel(m_LightCount * viewCount, 32, new JobHandle());

                    var reflectionProbeMinMaxZJob = new ReflectionProbeMinMaxZJob
                    {
                        worldToViews = worldToViews,
                        reflectionProbes = reflectionProbes,
                        minMaxZs = minMaxZs.GetSubArray(m_LightCount * viewCount, reflectionProbeCount * viewCount)
                    };
                    var reflectionProbeMinMaxZHandle = reflectionProbeMinMaxZJob.ScheduleParallel(reflectionProbeCount * viewCount, 32, lightMinMaxZHandle);

                    var zBinningBatchCount = (m_BinCount + ZBinningJob.batchSize - 1) / ZBinningJob.batchSize;
                    var zBinningJob = new ZBinningJob
                    {
                        bins = m_ZBins,
                        minMaxZs = minMaxZs,
                        zBinScale = m_ZBinScale,
                        zBinOffset = m_ZBinOffset,
                        binCount = m_BinCount,
                        wordsPerTile = m_WordsPerTile,
                        lightCount = m_LightCount,
                        reflectionProbeCount = reflectionProbeCount,
                        batchCount = zBinningBatchCount,
                        viewCount = viewCount,
                        isOrthographic = camera.orthographic
                    };
                    var zBinningHandle = zBinningJob.ScheduleParallel(zBinningBatchCount * viewCount, 1, reflectionProbeMinMaxZHandle);

                    reflectionProbeMinMaxZHandle.Complete();

                    // Each light needs 1 range for Y, and a range per row. Align to 128-bytes to avoid false sharing.
                    var rangesPerItem = AlignByteCount((1 + m_TileResolution.y) * UnsafeUtility.SizeOf<InclusiveRange>(), 128) / UnsafeUtility.SizeOf<InclusiveRange>();
                    var tileRanges = new NativeArray<InclusiveRange>(rangesPerItem * itemsPerTile * viewCount, Allocator.TempJob);
                    var tilingJob = new TilingJob
                    {
                        lights = visibleLights,
                        reflectionProbes = reflectionProbes,
                        tileRanges = tileRanges,
                        itemsPerTile = itemsPerTile,
                        rangesPerItem = rangesPerItem,
                        worldToViews = worldToViews,
                        tileScale = (float2)screenResolution / m_ActualTileWidth,
                        tileScaleInv = m_ActualTileWidth / (float2)screenResolution,
                        viewPlaneBottoms = new Fixed2<float>(viewPlaneBottom0, viewPlaneBottom1),
                        viewPlaneTops = new Fixed2<float>(viewPlaneTop0, viewPlaneTop1),
                        viewToViewportScaleBiases = new Fixed2<float4>(viewToViewportScaleBias0, viewToViewportScaleBias1),
                        tileCount = m_TileResolution,
                        near = camera.nearClipPlane,
                        isOrthographic = camera.orthographic
                    };

                    var tileRangeHandle = tilingJob.ScheduleParallel(itemsPerTile * viewCount, 1, reflectionProbeMinMaxZHandle);

                    var expansionJob = new TileRangeExpansionJob
                    {
                        tileRanges = tileRanges,
                        tileMasks = m_TileMasks,
                        rangesPerItem = rangesPerItem,
                        itemsPerTile = itemsPerTile,
                        wordsPerTile = m_WordsPerTile,
                        tileResolution = m_TileResolution,
                    };

                    var tilingHandle = expansionJob.ScheduleParallel(m_TileResolution.y * viewCount, 1, tileRangeHandle);
                    m_CullingHandle = JobHandle.CombineDependencies(
                        minMaxZs.Dispose(zBinningHandle),
                        tileRanges.Dispose(tilingHandle));

                    JobHandle.ScheduleBatchedJobs();
                }
            }
            ClusteredDeferredCpuProbe.End(probeStart, ref ClusteredDeferredCpuProbe.preSetup);
        }

        bool ShouldBuildClusterListOnGpu(int viewCount)
        {
            // Same device gate the renderer applied before it enabled clustered deferred; the only
            // extra requirement is a single view, because the item stream and params are per camera.
            return ClusterListGpuBuilder.enabled &&
                   m_ClusteredDeferred && !m_UseForwardPlus &&
                   viewCount == 1 &&
                   m_ClusterBuilder != null && m_ClusterBuilder.isCreated;
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
                if (m_UseForwardPlus)
                {
                    m_ReflectionProbeManager.UpdateGpuData(cmd, ref renderingData);
                }
                else if (m_PublishProbeAtlas)
                {
                    // The feature toggle can be enabled after the renderer was created; allocate
                    // lazily instead of requiring a renderer rebuild.
                    if (!m_ReflectionProbeManager.isCreated)
                        m_ReflectionProbeManager = ReflectionProbeManager.Create();

                    m_ReflectionProbeManager.UpdateGpuData(cmd, ref renderingData);
                }

                if (ShouldBuildClusters(ref renderingData.cameraData))
                {
                    var fp0 = math.float4(m_ZBinScale, m_ZBinOffset, m_LightCount, m_DirectionalLightCount);
                    var fp1 = math.float4(renderingData.cameraData.pixelRect.size / m_ActualTileWidth, m_TileResolution.x, m_WordsPerTile);
                    var fp2 = math.float4(m_BinCount, m_TileResolution.x * m_TileResolution.y, 0, 0);
                    cmd.SetGlobalVector("_FPParams0", fp0);
                    cmd.SetGlobalVector("_FPParams1", fp1);
                    cmd.SetGlobalVector("_FPParams2", fp2);

                    if (m_GpuClusterList)
                    {
                        using (new ProfilingScope(null, m_ProfilingSamplerFPGpuBuild))
                        {
                            var items = ClusterItemStream.Build(ref renderingData, m_LightOffset, m_LightCount, m_ReflectionProbeCount);
                            m_ClusterBuilder.Dispatch(cmd, new ClusterListGpuParams
                            {
                                items = items,
                                itemCount = items.Length,
                                lightCount = m_LightCount,
                                binCount = m_BinCount,
                                wordsPerTile = m_WordsPerTile,
                                zBinWordCount = m_ZBins.Length,
                                tileWordCount = m_TileMasks.Length,
                                tileCount = m_TileResolution,
                                tileScale = m_ClusterTileScale,
                                viewToViewportScaleBias = (Vector4)m_ClusterViewToViewportScaleBias,
                                worldToView = m_ClusterWorldToView,
                                near = m_ClusterNear,
                                orthographic = m_ClusterOrthographic,
                            }, m_ZBinsBuffer, m_TileMasksBuffer);
                        }

                        cmd.SetGlobalBuffer("URP_ZBins", m_ZBinsBuffer);
                        cmd.SetGlobalBuffer("urp_Tiles", m_TileMasksBuffer);
                    }
                    else
                    {
                        using (new ProfilingScope(null, m_ProfilingSamplerFPComplete))
                        {
                            long probeStart = ClusteredDeferredCpuProbe.Begin();
                            m_CullingHandle.Complete();
                            ClusteredDeferredCpuProbe.End(probeStart, ref ClusteredDeferredCpuProbe.clusterJobsComplete);
                        }

                        using (new ProfilingScope(null, m_ProfilingSamplerFPUpload))
                        {
                            long probeStart = ClusteredDeferredCpuProbe.Begin();
                            if (m_UseForwardPlus)
                            {
                                m_ZBinsBuffer.SetData(m_ZBins.Reinterpret<float4>(UnsafeUtility.SizeOf<uint>()));
                                m_TileMasksBuffer.SetData(m_TileMasks.Reinterpret<float4>(UnsafeUtility.SizeOf<uint>()));
                                cmd.SetGlobalConstantBuffer(m_ZBinsBuffer, "URP_ZBinBuffer", 0, UniversalRenderPipeline.maxZBinWords * 4);
                                cmd.SetGlobalConstantBuffer(m_TileMasksBuffer, "urp_TileBuffer", 0, UniversalRenderPipeline.maxTileWords * 4);
                            }
                            else
                            {
                                // Clustered fallback writes the same words into the structured buffers
                                // the kernel reads, so only this renderer's active mode changes.
                                m_ZBinsBuffer.SetData(m_ZBins);
                                m_TileMasksBuffer.SetData(m_TileMasks);
                                cmd.SetGlobalBuffer("URP_ZBins", m_ZBinsBuffer);
                                cmd.SetGlobalBuffer("urp_Tiles", m_TileMasksBuffer);
                            }
                            ClusteredDeferredCpuProbe.End(probeStart, ref ClusteredDeferredCpuProbe.clusterUpload);
                        }
                    }

                    if (ClusterListParityCapture.enabled)
                    {
                        var snapshot = CreateClusterSnapshot(ref renderingData, fp0, fp1, fp2);
                        if (m_GpuClusterList)
                        {
                            // Ordered after the dispatch on the same command buffer.
                            ClusterListParityCapture.RequestGpuCapture(cmd, snapshot, m_ZBinsBuffer, m_TileMasksBuffer);
                        }
                        else
                        {
                            snapshot.CopyBitmapsFrom(m_ZBins, m_TileMasks);
                            ClusterListParityCapture.Capture(snapshot);
                        }
                    }
                }

                long constantsStart = ClusteredDeferredCpuProbe.Begin();
                SetupShaderLightConstants(cmd, ref renderingData);
                ClusteredDeferredCpuProbe.End(constantsStart, ref ClusteredDeferredCpuProbe.lightConstants);

                bool lightCountCheck = (renderingData.cameraData.renderer.stripAdditionalLightOffVariants && renderingData.lightData.supportsAdditionalLights) || additionalLightsCount > 0;
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.AdditionalLightsVertex,
                    lightCountCheck && additionalLightsPerVertex && !m_UseForwardPlus);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.AdditionalLightsPixel,
                    lightCountCheck && !additionalLightsPerVertex && !m_UseForwardPlus);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.ForwardPlus,
                    m_UseForwardPlus);

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

        // Everything the parity snapshot needs except the bitmaps themselves: CreateClusterSnapshot
        // runs for both writers, then the CPU path copies its NativeArrays and the GPU path waits for
        // the ordered readback.
        ClusterListSnapshot CreateClusterSnapshot(ref RenderingData renderingData, float4 fp0, float4 fp1, float4 fp2)
        {
            var snapshot = new ClusterListSnapshot();
            snapshot.zBinScale = m_ZBinScale;
            snapshot.zBinOffset = m_ZBinOffset;
            snapshot.wordsPerTile = m_WordsPerTile;
            snapshot.binCount = m_BinCount;
            snapshot.lightCount = m_LightCount;
            snapshot.directionalLightCount = m_DirectionalLightCount;
            snapshot.viewCount = m_ViewCount;
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
                m_CullingHandle.Complete();
                if (m_ZBins.IsCreated)
                    m_ZBins.Dispose();
                m_ZBins = default;
                if (m_TileMasks.IsCreated)
                    m_TileMasks.Dispose();
                m_TileMasks = default;
                m_ZBinsBuffer?.Dispose();
                m_ZBinsBuffer = null;
                m_TileMasksBuffer?.Dispose();
                m_TileMasksBuffer = null;
                m_ClusterBuilder?.Dispose();
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
            int additionalLightsCount = SetupPerObjectLightIndices(cullResults, ref lightData);
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

        int SetupPerObjectLightIndices(CullingResults cullResults, ref LightData lightData)
        {
            if (lightData.additionalLightsCount == 0 || m_UseForwardPlus)
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
