using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Screen Space Global Illumination dispatch group: trace, reproject, temporal validation and
    // accumulation, bilateral diffuse denoise and bilateral upsample. Runs after the private depth
    // pyramid (203) and before the Deferred GBuffer (210) or Forward opaque geometry (250).
    //
    // The HDRP probe/sky fallback hierarchy is replaced by an explicit weight: a miss stores weight 0
    // and the Core GlobalIllumination blend keeps the baked GI. This is what keeps emission from being
    // added twice and keeps a miss from darkening the image.
    internal sealed class ScreenSpaceLightingSsgiPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 5);
        internal const string k_ProfilingSamplerName = "Screen Space Global Illumination";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

        const int k_TileSize = 8;
        const int k_PointDistributionCount = 64;

        ScreenSpaceLightingHistory m_History;
        ScreenSpaceLightingResources m_Resources;
        RTHandle m_FinalTexture;
        bool m_ProducedOutput;
        bool m_HistoryWritten;
        int m_SettingsStamp;
        int m_PointDistributionGeneration = -1;
        bool m_LoggedMissingResources;
        ScreenSpaceLightingSsgiShaderVariables m_ShaderVariables;
        // XR per-view matrices and eye centres. The arrays stay unbound unless XR single-pass needs
        // them, so the mono path is byte-for-byte unchanged.
        readonly ScreenSpaceLightingXrViewConstants[] m_XrViews = new ScreenSpaceLightingXrViewConstants[ScreenSpaceLightingXr.k_MaxViews];
        readonly Matrix4x4[] m_XrViewProjScratch = new Matrix4x4[ScreenSpaceLightingXr.k_MaxViews];
        readonly Matrix4x4[] m_XrInvViewProjScratch = new Matrix4x4[ScreenSpaceLightingXr.k_MaxViews];
        readonly Matrix4x4[] m_XrPrevViewProjScratch = new Matrix4x4[ScreenSpaceLightingXr.k_MaxViews];
        readonly Matrix4x4[] m_XrPrevInvViewProjScratch = new Matrix4x4[ScreenSpaceLightingXr.k_MaxViews];
        readonly Vector4[] m_XrCameraPositionScratch = new Vector4[ScreenSpaceLightingXr.k_MaxViews];

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Verification counters: XR dispatch evidence needs the dispatch count and the view/slice
        // mode at runtime, which the editor-only counters cannot provide in a player.
        internal static int s_TraceDispatchCount;
        internal static int s_ReprojectDispatchCount;
        internal static int s_LastDispatchViewCount;
        internal static int s_LastDispatchTextureArray;

        // Last frame's dispatch chain in the order it was submitted. The validation player reads it
        // through reflection to compare counts, group sizes, keyword state and history slot parity
        // against the manifest configuration; the whole snapshot is compiled out of release players.
        internal sealed class SsgiDispatchStats
        {
            public int frameIndex = -1;
            public int screenWidth;
            public int screenHeight;
            public int traceWidth;
            public int traceHeight;
            public bool temporal;
            public bool denoise;
            public bool secondDenoiserPass;
            public bool halfResolutionDenoiser;
            public bool orthographic;
            public int historyReadSlot = -1;
            public int historyWriteSlot = -1;
            public bool historyValid;

            public int traceDispatches;
            public int reprojectDispatches;
            public int validateDispatches;
            public int accumulationDispatches;
            public int copyHistoryDispatches;
            public int diffuseDenoiseDispatches;
            public int gatherDispatches;
            public int upsampleDispatches;
            public int pointDistributionDispatches;

            public int traceGroupsX;
            public int traceGroupsY;
            public int validateGroupsX;
            public int validateGroupsY;
            public int accumulationGroupsX;
            public int accumulationGroupsY;
            public int copyHistoryGroupsX;
            public int copyHistoryGroupsY;
            public int diffuseDenoiseGroupsX;
            public int diffuseDenoiseGroupsY;
            public int gatherGroupsX;
            public int gatherGroupsY;
            public int upsampleGroupsX;
            public int upsampleGroupsY;
            public int pointDistributionGroupsX;
            public int pointDistributionGroupsY;
            public int groupsZ;

            public bool halfResKeyword;
            public bool blueNoiseKeyword;
            public bool probeAtlasKeyword;
            public bool probeListKeyword;
            public bool normalsOctKeyword;
            public bool xrArrayKeyword;

            // Oldest first. Eight frames are enough to show the history ping-pong in one report.
            public readonly int[] historyReadSlots = new int[8];
            public readonly int[] historyWriteSlots = new int[8];

            public void BeginFrame(int frame)
            {
                frameIndex = frame;
                traceDispatches = 0;
                reprojectDispatches = 0;
                validateDispatches = 0;
                accumulationDispatches = 0;
                copyHistoryDispatches = 0;
                diffuseDenoiseDispatches = 0;
                gatherDispatches = 0;
                upsampleDispatches = 0;
                pointDistributionDispatches = 0;
                traceGroupsX = 0;
                traceGroupsY = 0;
                validateGroupsX = 0;
                validateGroupsY = 0;
                accumulationGroupsX = 0;
                accumulationGroupsY = 0;
                copyHistoryGroupsX = 0;
                copyHistoryGroupsY = 0;
                diffuseDenoiseGroupsX = 0;
                diffuseDenoiseGroupsY = 0;
                gatherGroupsX = 0;
                gatherGroupsY = 0;
                upsampleGroupsX = 0;
                upsampleGroupsY = 0;
                pointDistributionGroupsX = 0;
                pointDistributionGroupsY = 0;
                groupsZ = 0;
            }

            public void RecordHistory(int readSlot, int writeSlot, bool valid)
            {
                historyReadSlot = readSlot;
                historyWriteSlot = writeSlot;
                historyValid = valid;
                for (int i = 0; i < historyReadSlots.Length - 1; i++)
                {
                    historyReadSlots[i] = historyReadSlots[i + 1];
                    historyWriteSlots[i] = historyWriteSlots[i + 1];
                }

                historyReadSlots[historyReadSlots.Length - 1] = readSlot;
                historyWriteSlots[historyWriteSlots.Length - 1] = writeSlot;
            }
        }

        internal static readonly SsgiDispatchStats s_LastDispatchStats = new SsgiDispatchStats();
#endif

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
        internal static int s_EditorTraceDispatchCount;
        internal static int s_EditorReprojectDispatchCount;
        internal static int s_EditorValidationDispatchCount;
        internal static int s_EditorAccumulationDispatchCount;
        internal static int s_EditorDiffuseDispatchCount;
        internal static int s_EditorGatherDispatchCount;
        internal static int s_EditorUpsampleDispatchCount;
        internal static int s_EditorPointDistributionDispatchCount;
#endif

        internal bool producedOutput => m_ProducedOutput;
        internal RTHandle finalTexture => m_ProducedOutput ? m_FinalTexture : null;
        internal RTHandle historyTexture => m_HistoryWritten ? m_History.GetSsgiHistoryHF(m_History.ssgiWriteSlot) : null;
        internal RTHandle accumulationTexture => m_History != null ? m_History.ssgiAccumulation : null;
        internal RTHandle rawTexture => m_History != null ? m_History.ssgiProjection : null;

        internal ScreenSpaceLightingSsgiPass()
        {
            renderPassEvent = k_RenderPassEvent;
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal | ScriptableRenderPassInput.Motion);
        }

        internal void Setup(ScreenSpaceLightingHistory history, ScreenSpaceLightingResources resources)
        {
            m_History = history;
            m_Resources = resources;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            s_EditorExecuteCount++;
#endif
            m_ProducedOutput = false;
            m_FinalTexture = null;
            m_HistoryWritten = false;

            if (m_History == null || m_Resources == null)
                return;

            if (!m_Resources.hasSsgiResources)
            {
                LogMissingResourcesOnce();
                return;
            }

            var stack = VolumeManager.instance.stack;
            var settings = stack != null ? stack.GetComponent<ScreenSpaceGlobalIllumination>() : null;
            if (settings == null || !settings.enabled.value)
            {
                m_LoggedMissingResources = false;
                return;
            }

            // The reprojection reads the previous frame color pyramid, depth and normals. Without them the
            // only correct fallback is the original baked GI, which the material already has.
            if (m_History.readColorPyramid == null || m_History.readPreviousDepth == null ||
                m_History.readPreviousNormals == null)
            {
                m_History.ResetSsgiHistory();
                return;
            }

            ref var cameraData = ref renderingData.cameraData;
            var descriptor = cameraData.cameraTargetDescriptor;
            int viewCount = ScreenSpaceLightingXr.GetViewCount(cameraData);
            TextureDimension dimension = ScreenSpaceLightingXr.GetResourceDimension(cameraData);

            var depthTexture = ScreenSpaceRenderUtils.GetCameraDepthTexture(cameraData.renderer);
            var normalTexture = GetNormalTexture(cameraData.renderer);
            var motionTexture = GetMotionTexture(cameraData.renderer);
            if (depthTexture == null || normalTexture == null || motionTexture == null)
                return;

            int screenWidth = Mathf.Max(1, descriptor.width);
            int screenHeight = Mathf.Max(1, descriptor.height);
            bool fullResolution = settings.fullResolution.value;
            int traceWidth = fullResolution ? screenWidth : Mathf.Max(1, screenWidth >> 1);
            int traceHeight = fullResolution ? screenHeight : Mathf.Max(1, screenHeight >> 1);

            m_History.EnsureSsgiResources(screenWidth, screenHeight, traceWidth, traceHeight, viewCount, dimension);
            if (!m_History.ssgiResourcesAllocated)
                return;

            int stamp = ComputeSettingsStamp(settings, traceWidth, traceHeight, m_History.ssgiResourceGeneration);
            if (stamp != m_SettingsStamp)
            {
                m_SettingsStamp = stamp;
                m_History.ResetSsgiHistory();
            }

            m_History.BeginSsgiFrame();

            bool temporal = settings.temporalAccumulation.value;
            bool denoise = settings.denoise.value;
            bool secondPass = denoise && settings.secondDenoiserPass.value && temporal;
            bool halfResolutionDenoise = settings.halfResolutionDenoiser.value;
            bool octNormals = UseOctEncodedNormals(cameraData.renderer);
            bool useTextureArray = dimension == TextureDimension.Tex2DArray;
            if (cameraData.xr.enabled)
                ScreenSpaceLightingXr.FillViewConstants(cameraData, viewCount, m_XrViews, m_History);

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                Dispatch(
                    cmd,
                    ref cameraData,
                    settings,
                    depthTexture,
                    normalTexture,
                    motionTexture,
                    screenWidth,
                    screenHeight,
                    traceWidth,
                    traceHeight,
                    viewCount,
                    useTextureArray,
                    temporal,
                    denoise,
                    secondPass,
                    halfResolutionDenoise,
                    octNormals);
            }

            if (temporal)
                m_History.SwapSsgiBuffers();

            m_HistoryWritten = temporal;
            m_ProducedOutput = true;
        }

        void Dispatch(
            CommandBuffer cmd,
            ref CameraData cameraData,
            ScreenSpaceGlobalIllumination settings,
            RTHandle depthTexture,
            RTHandle normalTexture,
            RTHandle motionTexture,
            int screenWidth,
            int screenHeight,
            int traceWidth,
            int traceHeight,
            int viewCount,
            bool useTextureArray,
            bool temporal,
            bool denoise,
            bool secondPass,
            bool halfResolutionDenoise,
            bool octNormals)
        {
            var ssgiCompute = m_Resources.ssgiCompute;
            var temporalCompute = m_Resources.ssgiTemporalFilterCompute;
            var denoiserCompute = m_Resources.ssgiDiffuseDenoiserCompute;
            var upsampleCompute = m_Resources.bilateralUpsampleCompute;

            bool halfRes = traceWidth < screenWidth || traceHeight < screenHeight;
            int traceGroupsX = DivideRoundUp(traceWidth, k_TileSize);
            int traceGroupsY = DivideRoundUp(traceHeight, k_TileSize);
            int screenGroupsX = DivideRoundUp(screenWidth, k_TileSize);
            int screenGroupsY = DivideRoundUp(screenHeight, k_TileSize);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var stats = s_LastDispatchStats;
            stats.BeginFrame(Time.frameCount);
            stats.screenWidth = screenWidth;
            stats.screenHeight = screenHeight;
            stats.traceWidth = traceWidth;
            stats.traceHeight = traceHeight;
            stats.temporal = temporal;
            stats.denoise = denoise;
            stats.secondDenoiserPass = secondPass;
            stats.halfResolutionDenoiser = halfResolutionDenoise;
            stats.orthographic = cameraData.camera.orthographic;
            stats.groupsZ = viewCount;
            stats.RecordHistory(m_History.ssgiReadSlot, m_History.ssgiWriteSlot, m_History.ssgiHasValidHistory);
#endif

            SetKeyword(ssgiCompute, ScreenSpaceLightingShaderIDs.SsgiHalfResKeyword, halfRes);
            SetKeyword(ssgiCompute, ScreenSpaceLightingShaderIDs.SsgiNormalsOctKeyword, octNormals);
            SetKeyword(temporalCompute, ScreenSpaceLightingShaderIDs.XrArrayKeyword, useTextureArray);
            SetKeyword(denoiserCompute, ScreenSpaceLightingShaderIDs.XrArrayKeyword, useTextureArray);
            // Normal decoding is shared by the validation and denoiser kernels.
            SetKeyword(temporalCompute, ScreenSpaceLightingShaderIDs.SsgiNormalsOctKeyword, octNormals);
            SetKeyword(denoiserCompute, ScreenSpaceLightingShaderIDs.SsgiNormalsOctKeyword, octNormals);
            SetKeyword(upsampleCompute, ScreenSpaceLightingShaderIDs.XrArrayKeyword, useTextureArray);
            SetKeyword(ssgiCompute, ScreenSpaceLightingShaderIDs.XrArrayKeyword, useTextureArray);

            // HDRP's blue-noise distributed sequence when the package LUTs are available.
            bool blueNoise = m_Resources.hasSsgiBlueNoise;
            SetKeyword(ssgiCompute, ScreenSpaceLightingShaderIDs.SsgiBlueNoiseKeyword, blueNoise);
            SetKeyword(denoiserCompute, ScreenSpaceLightingShaderIDs.SsgiBlueNoiseKeyword, blueNoise);

            // Forward+ publishes the cluster list; Forward / Deferred use the flat visible-probe list
            // when the Screen Space Lighting feature opts into the atlas.
            bool forwardPlus = cameraData.renderer is UniversalRenderer forwardPlusRenderer &&
                forwardPlusRenderer.renderingModeActual == RenderingMode.ForwardPlus;
            bool probeList = !forwardPlus &&
                cameraData.renderer is UniversalRenderer probeListRenderer &&
                probeListRenderer.publishSslProbeAtlas;
            SetKeyword(ssgiCompute, ScreenSpaceLightingShaderIDs.SslProbeAtlasKeyword, forwardPlus);
            SetKeyword(ssgiCompute, ScreenSpaceLightingShaderIDs.SslProbeListKeyword, probeList);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            stats.halfResKeyword = ssgiCompute.IsKeywordEnabled(ScreenSpaceLightingShaderIDs.SsgiHalfResKeyword);
            stats.blueNoiseKeyword = ssgiCompute.IsKeywordEnabled(ScreenSpaceLightingShaderIDs.SsgiBlueNoiseKeyword);
            stats.probeAtlasKeyword = ssgiCompute.IsKeywordEnabled(ScreenSpaceLightingShaderIDs.SslProbeAtlasKeyword);
            stats.probeListKeyword = ssgiCompute.IsKeywordEnabled(ScreenSpaceLightingShaderIDs.SslProbeListKeyword);
            stats.normalsOctKeyword = ssgiCompute.IsKeywordEnabled(ScreenSpaceLightingShaderIDs.SsgiNormalsOctKeyword);
            stats.xrArrayKeyword = ssgiCompute.IsKeywordEnabled(ScreenSpaceLightingShaderIDs.XrArrayKeyword);
#endif

            if (useTextureArray)
            {
                BindXrViewConstants(cmd, viewCount);
            }

            float lowResPercentage = traceWidth / (float)screenWidth;
            float invLowResPercentage = 1.0f / lowResPercentage;
            float thickness = Mathf.Max(0.0f, settings.depthBufferThickness.value);
            float thicknessScale = 1.0f / (1.0f + thickness);
            float near = Mathf.Max(1e-5f, cameraData.camera.nearClipPlane);
            float far = Mathf.Max(near + 1e-4f, cameraData.camera.farClipPlane);
            float thicknessBias = -near / (far - near) * (thickness * thicknessScale);
            // HDRP uses RayTracingFrameIndex(hdCamera, 16) for SSGI, i.e. frameCount % 16.
            int frameIndex = Time.frameCount % 16;
            float historyValidity = m_History.ssgiHasValidHistory ? 1.0f : 0.0f;

            // Trace + reproject.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchViewCount = viewCount;
            s_LastDispatchTextureArray = useTextureArray ? 1 : 0;
#endif

            var traceKernel = ssgiCompute.FindKernel("TraceGlobalIllumination");
            var reprojectKernel = ssgiCompute.FindKernel("ReprojectGlobalIllumination");
            BindSsgiBlueNoise(cmd, ssgiCompute, traceKernel, reprojectKernel);
            PrepareShaderVariables(
                ref cameraData,
                screenWidth,
                screenHeight,
                traceWidth,
                traceHeight,
                invLowResPercentage,
                frameIndex,
                historyValidity,
                settings,
                thicknessScale,
                thicknessBias,
                lowResPercentage,
                ScreenSpaceLightingRenderUtils.ComputePixelSpreadTangent(cameraData.camera.fieldOfView, traceWidth, traceHeight));
            PushShaderVariables(cmd, ssgiCompute, m_ShaderVariables);

            cmd.SetComputeTextureParam(ssgiCompute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(ssgiCompute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(ssgiCompute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiDepthPyramid, m_History.depthPyramid);
            cmd.SetComputeBufferParam(ssgiCompute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiDepthPyramidMipLevelOffsets, m_History.depthPyramidOffsets);
            cmd.SetComputeTextureParam(ssgiCompute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiHitPointTextureRW, m_History.ssgiHitPoint);
            DispatchCompute(cmd, ssgiCompute, traceKernel, traceGroupsX, traceGroupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_TraceDispatchCount++;
            stats.traceDispatches++;
            stats.traceGroupsX = traceGroupsX;
            stats.traceGroupsY = traceGroupsY;
#endif
#if UNITY_EDITOR
            s_EditorTraceDispatchCount++;
#endif

            cmd.SetComputeTextureParam(ssgiCompute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsgiDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(ssgiCompute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsgiNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(ssgiCompute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsgiMotionTexture, motionTexture);
            cmd.SetComputeTextureParam(ssgiCompute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsgiColorPyramid, m_History.readColorPyramid);
            cmd.SetComputeTextureParam(ssgiCompute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsgiPreviousDepthTexture, m_History.readPreviousDepth);
            cmd.SetComputeTextureParam(ssgiCompute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsgiHitPointTextureRW, m_History.ssgiHitPoint);
            cmd.SetComputeTextureParam(ssgiCompute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsgiProjectionTextureRW, m_History.ssgiProjection);

            // Miss fallback: hierarchy, default reflection cubemap and the ambient probe SH uploaded from
            // RenderSettings (compute kernels cannot read the per-draw unity_SH* constants).
            cmd.SetComputeVectorParam(ssgiCompute, ScreenSpaceLightingShaderIDs.SsgiFallbackParams,
                new Vector4((int)settings.rayMiss.value, 0f, 0f, 0f));
            // x: perspective flag for the Forward+ cluster zbin lookup.
            cmd.SetComputeVectorParam(ssgiCompute, ScreenSpaceLightingShaderIDs.SsgiProbeAtlasParams,
                new Vector4(cameraData.camera.orthographic ? 0f : 1f, 0f, 0f, 0f));
            cmd.SetComputeVectorArrayParam(ssgiCompute, ScreenSpaceLightingShaderIDs.SsgiAmbientProbeSH, BuildAmbientProbeSH());
            var reflectionProbeTexture = ReflectionProbe.defaultTexture;
            cmd.SetComputeTextureParam(ssgiCompute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsgiReflectionProbeCubemap,
                reflectionProbeTexture != null ? reflectionProbeTexture : CoreUtils.blackCubeTexture);
            cmd.SetComputeVectorParam(ssgiCompute, ScreenSpaceLightingShaderIDs.SsgiReflectionProbeHDRDecode,
                ReflectionProbe.defaultTextureHDRDecodeValues);

            DispatchCompute(cmd, ssgiCompute, reprojectKernel, traceGroupsX, traceGroupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_ReprojectDispatchCount++;
            stats.reprojectDispatches++;
#endif
#if UNITY_EDITOR
            s_EditorReprojectDispatchCount++;
#endif

            RTHandle signal = m_History.ssgiProjection;

            if (temporal)
            {
                // History validation runs at full resolution: the rejection uses the current depth,
                // normal and motion and the previous frame depth, which are all full resolution.
                PrepareShaderVariables(
                    ref cameraData,
                    screenWidth,
                    screenHeight,
                    traceWidth,
                    traceHeight,
                    invLowResPercentage,
                    frameIndex,
                    historyValidity,
                    settings,
                    thicknessScale,
                    thicknessBias,
                    lowResPercentage,
                    ScreenSpaceLightingRenderUtils.ComputePixelSpreadTangent(cameraData.camera.fieldOfView, screenWidth, screenHeight));
                PushShaderVariables(cmd, temporalCompute, m_ShaderVariables);

                var validateKernel = temporalCompute.FindKernel("ValidateHistory");
                cmd.SetComputeTextureParam(temporalCompute, validateKernel, ScreenSpaceLightingShaderIDs.DenoiseDepthTexture, depthTexture);
                cmd.SetComputeTextureParam(temporalCompute, validateKernel, ScreenSpaceLightingShaderIDs.DenoiseNormalTexture, normalTexture);
                cmd.SetComputeTextureParam(temporalCompute, validateKernel, ScreenSpaceLightingShaderIDs.DenoiseMotionTexture, motionTexture);
                cmd.SetComputeTextureParam(temporalCompute, validateKernel, ScreenSpaceLightingShaderIDs.HistoryDepthTexture, m_History.readPreviousDepth);
                cmd.SetComputeTextureParam(temporalCompute, validateKernel, ScreenSpaceLightingShaderIDs.HistoryNormalTexture, m_History.readPreviousNormals);
                cmd.SetComputeTextureParam(temporalCompute, validateKernel, ScreenSpaceLightingShaderIDs.ValidationBufferRW, m_History.ssgiValidation);
                cmd.SetComputeFloatParam(temporalCompute, ScreenSpaceLightingShaderIDs.PixelSpreadAngleTangent, m_ShaderVariables._SsgiDenoiserParams.x);
                DispatchCompute(cmd, temporalCompute, validateKernel, screenGroupsX, screenGroupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                stats.validateDispatches++;
                stats.validateGroupsX = screenGroupsX;
                stats.validateGroupsY = screenGroupsY;
#endif
#if UNITY_EDITOR
                s_EditorValidationDispatchCount++;
#endif

                // The denoiser needs the spread angle of one trace texel, not one screen texel.
                PrepareShaderVariables(
                    ref cameraData,
                    screenWidth,
                    screenHeight,
                    traceWidth,
                    traceHeight,
                    invLowResPercentage,
                    frameIndex,
                    historyValidity,
                    settings,
                    thicknessScale,
                    thicknessBias,
                    lowResPercentage,
                    ScreenSpaceLightingRenderUtils.ComputePixelSpreadTangent(cameraData.camera.fieldOfView, traceWidth, traceHeight));
                PushShaderVariables(cmd, temporalCompute, m_ShaderVariables);

                var accumulateKernel = temporalCompute.FindKernel("TemporalAccumulationColor");
                cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.DenoiseDepthTexture, depthTexture);
                cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.DenoiseMotionTexture, motionTexture);
                cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.ValidationBuffer, m_History.ssgiValidation);
                cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.DenoiseInputTexture, signal);
                cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.HistoryBuffer, m_History.GetSsgiHistoryHF(m_History.ssgiReadSlot));
                cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.HistorySampleCountTexture, m_History.GetSsgiSampleCountHF(m_History.ssgiReadSlot));
                cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.AccumulationOutputTextureRW, m_History.ssgiAccumulation);
                cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.HistorySampleCountRW, m_History.GetSsgiSampleCountHF(m_History.ssgiWriteSlot));
                // SSGI has no intersection velocity buffer. Bind a black texture like HDRP's SSGI
                // caller: the occluder rejection path stays off, but the shader property must still
                // be bound or the dispatch is rejected.
                cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.VelocityBuffer, TextureXR.GetBlackTexture());
                BindTemporalFilterScalars(cmd, temporalCompute);
                DispatchCompute(cmd, temporalCompute, accumulateKernel, traceGroupsX, traceGroupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                stats.accumulationDispatches++;
                stats.accumulationGroupsX = traceGroupsX;
                stats.accumulationGroupsY = traceGroupsY;
#endif
#if UNITY_EDITOR
                s_EditorAccumulationDispatchCount++;
#endif

                // HDRP CopyHistory: the accumulated signal becomes next frame's history.
                DispatchCopyHistory(cmd, temporalCompute, m_History.ssgiAccumulation, m_History.GetSsgiHistoryHF(m_History.ssgiWriteSlot), traceWidth, traceHeight, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                stats.copyHistoryDispatches++;
                stats.copyHistoryGroupsX = DivideRoundUp(traceWidth, k_TileSize);
                stats.copyHistoryGroupsY = DivideRoundUp(traceHeight, k_TileSize);
#endif

                signal = m_History.ssgiAccumulation;
            }

            if (denoise)
            {
                PrepareShaderVariables(
                    ref cameraData,
                    screenWidth,
                    screenHeight,
                    traceWidth,
                    traceHeight,
                    invLowResPercentage,
                    frameIndex,
                    historyValidity,
                    settings,
                    thicknessScale,
                    thicknessBias,
                    lowResPercentage,
                    ScreenSpaceLightingRenderUtils.ComputePixelSpreadTangent(cameraData.camera.fieldOfView, traceWidth, traceHeight));
                PushShaderVariables(cmd, denoiserCompute, m_ShaderVariables);

                DispatchPointDistribution(cmd, denoiserCompute);
                RTHandle denoiseOutput = halfResolutionDenoise ? m_History.ssgiDenoiseIntermediate : m_History.ssgiDenoisedHF;
                // HDRP rotates the first-pass point distribution only while the second pass is enabled
                // (jitterFilter = secondDenoiserPassSS) and leaves the second pass on the first 16
                // samples. The 64 point distribution holds four 16-sample groups, so the period stays
                // at four.
                DispatchDiffuseDenoise(cmd, denoiserCompute, depthTexture, normalTexture, signal, denoiseOutput, traceWidth, traceHeight, viewCount,
                    settings.denoiserRadius.value, secondPass ? frameIndex % 4 : -1, halfResolutionDenoise);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                stats.diffuseDenoiseDispatches++;
                stats.diffuseDenoiseGroupsX = DivideRoundUp(traceWidth, k_TileSize);
                stats.diffuseDenoiseGroupsY = DivideRoundUp(traceHeight, k_TileSize);
#endif
                if (halfResolutionDenoise)
                {
                    DispatchDiffuseGather(cmd, denoiserCompute, depthTexture, denoiseOutput, m_History.ssgiDenoisedHF, traceWidth, traceHeight, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    stats.gatherDispatches++;
                    stats.gatherGroupsX = DivideRoundUp(traceWidth, k_TileSize);
                    stats.gatherGroupsY = DivideRoundUp(traceHeight, k_TileSize);
#endif
                }
                signal = m_History.ssgiDenoisedHF;

                if (secondPass)
                {
                    // Second (low frequency) temporal and spatial pass. It accumulates the first pass
                    // result and runs a smaller radius filter, matching the HDRP two pass schedule.
                    var accumulateKernel = temporalCompute.FindKernel("TemporalAccumulationColor");
                    cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.DenoiseDepthTexture, depthTexture);
                    cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.DenoiseMotionTexture, motionTexture);
                    cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.ValidationBuffer, m_History.ssgiValidation);
                    cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.DenoiseInputTexture, signal);
                    cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.HistoryBuffer, m_History.GetSsgiHistoryLF(m_History.ssgiReadSlot));
                    cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.HistorySampleCountTexture, m_History.GetSsgiSampleCountLF(m_History.ssgiReadSlot));
                    cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.AccumulationOutputTextureRW, m_History.ssgiAccumulation);
                    cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.HistorySampleCountRW, m_History.GetSsgiSampleCountLF(m_History.ssgiWriteSlot));
                    cmd.SetComputeTextureParam(temporalCompute, accumulateKernel, ScreenSpaceLightingShaderIDs.VelocityBuffer, TextureXR.GetBlackTexture());
                    BindTemporalFilterScalars(cmd, temporalCompute);
                    DispatchCompute(cmd, temporalCompute, accumulateKernel, traceGroupsX, traceGroupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    stats.accumulationDispatches++;
                    stats.accumulationGroupsX = traceGroupsX;
                    stats.accumulationGroupsY = traceGroupsY;
#endif
#if UNITY_EDITOR
                    s_EditorAccumulationDispatchCount++;
#endif

                    DispatchCopyHistory(cmd, temporalCompute, m_History.ssgiAccumulation, m_History.GetSsgiHistoryLF(m_History.ssgiWriteSlot), traceWidth, traceHeight, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    stats.copyHistoryDispatches++;
                    stats.copyHistoryGroupsX = DivideRoundUp(traceWidth, k_TileSize);
                    stats.copyHistoryGroupsY = DivideRoundUp(traceHeight, k_TileSize);
#endif

                    // HDRP keeps the second pass on the first 16 samples (jitterFilter = false) because the first
                    // pass already rotated the distribution this frame. The low frequency result owns its
                    // own output; writing it back into the HF buffer left the material-facing LF texture
                    // uninitialized.
                    RTHandle secondDenoiseOutput = halfResolutionDenoise ? m_History.ssgiDenoiseIntermediate : m_History.ssgiDenoisedLF;
                    DispatchDiffuseDenoise(cmd, denoiserCompute, depthTexture, normalTexture, m_History.ssgiAccumulation, secondDenoiseOutput, traceWidth, traceHeight, viewCount,
                        settings.denoiserRadius.value * 0.5f, -1, halfResolutionDenoise);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    stats.diffuseDenoiseDispatches++;
                    stats.diffuseDenoiseGroupsX = DivideRoundUp(traceWidth, k_TileSize);
                    stats.diffuseDenoiseGroupsY = DivideRoundUp(traceHeight, k_TileSize);
#endif
                    if (halfResolutionDenoise)
                    {
                        DispatchDiffuseGather(cmd, denoiserCompute, depthTexture, secondDenoiseOutput, m_History.ssgiDenoisedLF, traceWidth, traceHeight, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        stats.gatherDispatches++;
                        stats.gatherGroupsX = DivideRoundUp(traceWidth, k_TileSize);
                        stats.gatherGroupsY = DivideRoundUp(traceHeight, k_TileSize);
#endif
                    }
                    signal = m_History.ssgiDenoisedLF;
                }
            }

            if (halfRes)
            {
                PrepareShaderVariables(
                    ref cameraData,
                    screenWidth,
                    screenHeight,
                    traceWidth,
                    traceHeight,
                    invLowResPercentage,
                    frameIndex,
                    historyValidity,
                    settings,
                    thicknessScale,
                    thicknessBias,
                    lowResPercentage,
                    ScreenSpaceLightingRenderUtils.ComputePixelSpreadTangent(cameraData.camera.fieldOfView, traceWidth, traceHeight));
                PushShaderVariables(cmd, upsampleCompute, m_ShaderVariables);

                var upsampleKernel = upsampleCompute.FindKernel(
                    settings.bilateralUpsample.value ? "BilateralUpSampleColor" : "BoxUpSampleColor");
                cmd.SetComputeTextureParam(upsampleCompute, upsampleKernel, ScreenSpaceLightingShaderIDs.SsgiDepthTexture, depthTexture);
                cmd.SetComputeTextureParam(upsampleCompute, upsampleKernel, ScreenSpaceLightingShaderIDs.SsgiLowResolutionTexture, signal);
                cmd.SetComputeTextureParam(upsampleCompute, upsampleKernel, ScreenSpaceLightingShaderIDs.SsgiUpsampledTextureRW, m_History.ssgiFinal);
                DispatchCompute(cmd, upsampleCompute, upsampleKernel, screenGroupsX, screenGroupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                stats.upsampleDispatches++;
                stats.upsampleGroupsX = screenGroupsX;
                stats.upsampleGroupsY = screenGroupsY;
#endif
#if UNITY_EDITOR
                s_EditorUpsampleDispatchCount++;
#endif

                m_FinalTexture = m_History.ssgiFinal;
            }
            else
            {
                m_FinalTexture = signal;
            }
        }

        void DispatchDiffuseDenoise(
            CommandBuffer cmd,
            ComputeShader denoiserCompute,
            RTHandle depthTexture,
            RTHandle normalTexture,
            RTHandle input,
            RTHandle output,
            int traceWidth,
            int traceHeight,
            int viewCount,
            float filterRadius,
            int jitterFramePeriod,
            bool halfResolutionFilter)
        {
            var kernel = denoiserCompute.FindKernel("BilateralFilterColor");
            cmd.SetComputeTextureParam(denoiserCompute, kernel, ScreenSpaceLightingShaderIDs.DenoiseDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(denoiserCompute, kernel, ScreenSpaceLightingShaderIDs.DenoiseNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(denoiserCompute, kernel, ScreenSpaceLightingShaderIDs.DenoiseInputTexture, input);
            cmd.SetComputeTextureParam(denoiserCompute, kernel, ScreenSpaceLightingShaderIDs.DenoiseOutputTextureRW, output);
            cmd.SetComputeBufferParam(denoiserCompute, kernel, ScreenSpaceLightingShaderIDs.PointDistribution, m_History.ssgiPointDistribution);
            cmd.SetComputeFloatParam(denoiserCompute, ScreenSpaceLightingShaderIDs.DenoiserFilterRadius, filterRadius);
            cmd.SetComputeFloatParam(denoiserCompute, ScreenSpaceLightingShaderIDs.PixelSpreadAngleTangent, m_ShaderVariables._SsgiDenoiserParams.x);
            cmd.SetComputeIntParam(denoiserCompute, ScreenSpaceLightingShaderIDs.JitterFramePeriod, jitterFramePeriod);
            cmd.SetComputeIntParam(denoiserCompute, ScreenSpaceLightingShaderIDs.HalfResolutionFilter, halfResolutionFilter ? 1 : 0);
            DispatchCompute(cmd, denoiserCompute, kernel, DivideRoundUp(traceWidth, k_TileSize), DivideRoundUp(traceHeight, k_TileSize), viewCount);
#if UNITY_EDITOR
            s_EditorDiffuseDispatchCount++;
#endif
        }

        // Half resolution denoiser recombination (HDRP GatherFilter).
        void DispatchDiffuseGather(
            CommandBuffer cmd,
            ComputeShader denoiserCompute,
            RTHandle depthTexture,
            RTHandle input,
            RTHandle output,
            int traceWidth,
            int traceHeight,
            int viewCount)
        {
            var kernel = denoiserCompute.FindKernel("GatherColor");
            cmd.SetComputeTextureParam(denoiserCompute, kernel, ScreenSpaceLightingShaderIDs.DenoiseDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(denoiserCompute, kernel, ScreenSpaceLightingShaderIDs.DenoiseInputTexture, input);
            cmd.SetComputeTextureParam(denoiserCompute, kernel, ScreenSpaceLightingShaderIDs.DenoiseOutputTextureRW, output);
            DispatchCompute(cmd, denoiserCompute, kernel, DivideRoundUp(traceWidth, k_TileSize), DivideRoundUp(traceHeight, k_TileSize), viewCount);
#if UNITY_EDITOR
            s_EditorGatherDispatchCount++;
#endif
        }

        // HDRP's temporal filter constants that are small per-pass scalars rather than part of the
        // exactly packed SSGI constant buffer.
        void BindTemporalFilterScalars(CommandBuffer cmd, ComputeShader temporalCompute)
        {
            cmd.SetComputeFloatParam(temporalCompute, ScreenSpaceLightingShaderIDs.PixelSpreadAngleTangent, m_ShaderVariables._SsgiDenoiserParams.x);
            // SSGI has no intersection velocity buffer and URP has no exposure stage at this point,
            // so the HDRP rejection / exposure paths stay disabled, exactly like HDRP's SSGI caller
            // (which also passes false for both rejections).
            cmd.SetComputeIntParam(temporalCompute, ScreenSpaceLightingShaderIDs.ReceiverMotionRejection, 0);
            cmd.SetComputeIntParam(temporalCompute, ScreenSpaceLightingShaderIDs.OccluderMotionRejection, 0);
            cmd.SetComputeIntParam(temporalCompute, ScreenSpaceLightingShaderIDs.EnableExposureControl, 0);
            cmd.SetComputeFloatParam(temporalCompute, ScreenSpaceLightingShaderIDs.PreviousExposureMultiplier, 1.0f);
            cmd.SetComputeFloatParam(temporalCompute, ScreenSpaceLightingShaderIDs.CurrentExposureMultiplier, 1.0f);
        }

        void DispatchCopyHistory(
            CommandBuffer cmd,
            ComputeShader temporalCompute,
            RTHandle input,
            RTHandle history,
            int traceWidth,
            int traceHeight,
            int viewCount)
        {
            var kernel = temporalCompute.FindKernel("CopyHistory");
            cmd.SetComputeTextureParam(temporalCompute, kernel, ScreenSpaceLightingShaderIDs.DenoiseInputTexture, input);
            cmd.SetComputeTextureParam(temporalCompute, kernel, ScreenSpaceLightingShaderIDs.DenoiseOutputTextureRW, history);
            DispatchCompute(cmd, temporalCompute, kernel, DivideRoundUp(traceWidth, k_TileSize), DivideRoundUp(traceHeight, k_TileSize), viewCount);
        }

        void DispatchPointDistribution(CommandBuffer cmd, ComputeShader denoiserCompute)
        {
            bool generate = m_PointDistributionGeneration != m_History.ssgiResourceGeneration;
            var filterKernel = denoiserCompute.FindKernel("BilateralFilterColor");
            cmd.SetComputeBufferParam(denoiserCompute, filterKernel, ScreenSpaceLightingShaderIDs.PointDistribution, m_History.ssgiPointDistribution);

            if (!generate)
                return;

            m_PointDistributionGeneration = m_History.ssgiResourceGeneration;
            var kernel = denoiserCompute.FindKernel("GeneratePointDistribution");
            cmd.SetComputeBufferParam(denoiserCompute, kernel, ScreenSpaceLightingShaderIDs.PointDistributionRW, m_History.ssgiPointDistribution);
            BindSsgiBlueNoise(cmd, denoiserCompute, kernel, kernel);
            DispatchCompute(cmd, denoiserCompute, kernel, k_PointDistributionCount, 1, 1);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchStats.pointDistributionDispatches++;
            s_LastDispatchStats.pointDistributionGroupsX = k_PointDistributionCount;
            s_LastDispatchStats.pointDistributionGroupsY = 1;
#endif
#if UNITY_EDITOR
            s_EditorPointDistributionDispatchCount++;
#endif
        }

        // Binds the HDRP blue-noise LUTs used by the BND sequence. A no-op when the package resources
        // are missing, in which case the kernels use the IGN + R2 fallback.
        void BindSsgiBlueNoise(CommandBuffer cmd, ComputeShader compute, int kernelA, int kernelB)
        {
            if (!m_Resources.hasSsgiBlueNoise)
                return;

            var owen = m_Resources.ssgiOwenScrambledTexture;
            var scrambling = m_Resources.ssgiScramblingTile;
            var ranking = m_Resources.ssgiRankingTile;

            cmd.SetComputeTextureParam(compute, kernelA, ScreenSpaceLightingShaderIDs.SsgiOwenScrambledTexture, owen);
            cmd.SetComputeTextureParam(compute, kernelA, ScreenSpaceLightingShaderIDs.SsgiScramblingTileXSPP, scrambling);
            cmd.SetComputeTextureParam(compute, kernelA, ScreenSpaceLightingShaderIDs.SsgiRankingTileXSPP, ranking);

            if (kernelB != kernelA)
            {
                cmd.SetComputeTextureParam(compute, kernelB, ScreenSpaceLightingShaderIDs.SsgiOwenScrambledTexture, owen);
                cmd.SetComputeTextureParam(compute, kernelB, ScreenSpaceLightingShaderIDs.SsgiScramblingTileXSPP, scrambling);
                cmd.SetComputeTextureParam(compute, kernelB, ScreenSpaceLightingShaderIDs.SsgiRankingTileXSPP, ranking);
            }
        }

        void BindXrViewConstants(CommandBuffer cmd, int viewCount)
        {
            ScreenSpaceLightingXr.BindComputeViewConstants(
                cmd,
                m_XrViews,
                viewCount,
                m_XrViewProjScratch,
                m_XrInvViewProjScratch,
                m_XrPrevViewProjScratch,
                m_XrPrevInvViewProjScratch,
                m_XrCameraPositionScratch);
        }

        void PrepareShaderVariables(
            ref CameraData cameraData,
            int screenWidth,
            int screenHeight,
            int traceWidth,
            int traceHeight,
            float invLowResPercentage,
            int frameIndex,
            float historyValidity,
            ScreenSpaceGlobalIllumination settings,
            float thicknessScale,
            float thicknessBias,
            float lowResPercentage,
            float pixelSpreadTangent)
        {
            var previousViewProj = ScreenSpaceLightingRenderUtils.GetPreviousViewProjection(cameraData);
            var previousInvViewProj = previousViewProj.inverse;
            var cameraPosition = cameraData.camera.transform.position;
            if (cameraData.xr.enabled)
            {
                // Multipass renders one eye per pass, so the eye centre and the per-eye previous
                // matrix from the history entry are used instead of the mono camera values. The
                // single-pass kernels read the per-view globals and ignore these fields.
                cameraPosition = m_XrViews[0].cameraPositionWS;
                previousInvViewProj = m_XrViews[0].hasPreviousViewProj
                    ? m_XrViews[0].prevInvViewProj
                    : Matrix4x4.Inverse(m_XrViews[0].viewProj);
            }

            m_ShaderVariables = default;
            m_ShaderVariables._SsgiScreenSize = new Vector4(screenWidth, screenHeight, 1f / screenWidth, 1f / screenHeight);
            m_ShaderVariables._SsgiTraceSize = new Vector4(traceWidth, traceHeight, 1f / traceWidth, 1f / traceHeight);
            m_ShaderVariables._SsgiRayMarchingParams = new Vector4(
                Mathf.Max(1, settings.maxRaySteps.value),
                thicknessScale,
                thicknessBias,
                1.0f);
            m_ShaderVariables._SsgiRayMarchingParams2 = new Vector4(invLowResPercentage, frameIndex, historyValidity, 0.0f);
            m_ShaderVariables._SsgiZBufferParams = ScreenSpaceLightingRenderUtils.ComputeZBufferParams(cameraData.camera.nearClipPlane, cameraData.camera.farClipPlane);
            m_ShaderVariables._SsgiColorPyramidScaleAndLimitPrev = new Vector4(1f, 1f, 1f, 1f);
            m_ShaderVariables._SsgiTemporalParams = new Vector4(lowResPercentage, 1.0f / lowResPercentage, 0.2f, 0.0f);
            m_ShaderVariables._SsgiDenoiserParams = new Vector4(pixelSpreadTangent, -1.0f, 0.0f, 0.0f);
            m_ShaderVariables._SsgiCameraPositionWS = cameraPosition;
            m_ShaderVariables._SsgiPrevInvViewProj = previousInvViewProj;
        }

        static readonly Vector4[] s_AmbientProbeSH = new Vector4[7];

        // Packs RenderSettings.ambientProbe into the unity_SHAr..unity_SHC layout that SampleSH9
        // expects. The compute kernels cannot read unity_SH* (they live in UnityPerDraw), so the
        // probe is uploaded to the SSGI constant set explicitly.
        static Vector4[] BuildAmbientProbeSH()
        {
            SphericalHarmonicsL2 probe = RenderSettings.ambientProbe;
            s_AmbientProbeSH[0] = new Vector4(probe[0, 3], probe[0, 1], probe[0, 2], probe[0, 0]);
            s_AmbientProbeSH[1] = new Vector4(probe[1, 3], probe[1, 1], probe[1, 2], probe[1, 0]);
            s_AmbientProbeSH[2] = new Vector4(probe[2, 3], probe[2, 1], probe[2, 2], probe[2, 0]);
            s_AmbientProbeSH[3] = new Vector4(probe[0, 4], probe[0, 5], probe[0, 6], probe[0, 7]);
            s_AmbientProbeSH[4] = new Vector4(probe[1, 4], probe[1, 5], probe[1, 6], probe[1, 7]);
            s_AmbientProbeSH[5] = new Vector4(probe[2, 4], probe[2, 5], probe[2, 6], probe[2, 7]);
            s_AmbientProbeSH[6] = new Vector4(probe[0, 8], probe[1, 8], probe[2, 8], 0f);
            return s_AmbientProbeSH;
        }

        static int ComputeSettingsStamp(ScreenSpaceGlobalIllumination settings, int traceWidth, int traceHeight, int resourceGeneration)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (settings.fullResolution.value ? 1 : 0);
                hash = hash * 31 + (int)settings.rayMiss.value;
                hash = hash * 31 + (settings.temporalAccumulation.value ? 1 : 0);
                hash = hash * 31 + (settings.denoise.value ? 1 : 0);
                hash = hash * 31 + (settings.secondDenoiserPass.value ? 1 : 0);
                hash = hash * 31 + (settings.halfResolutionDenoiser.value ? 1 : 0);
                hash = hash * 31 + traceWidth;
                hash = hash * 31 + traceHeight;
                hash = hash * 31 + resourceGeneration;
                return hash;
            }
        }

        static void PushShaderVariables(CommandBuffer cmd, ComputeShader compute, in ScreenSpaceLightingSsgiShaderVariables cb)
        {
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsgiScreenSize, cb._SsgiScreenSize);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsgiTraceSize, cb._SsgiTraceSize);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsgiRayMarchingParams, cb._SsgiRayMarchingParams);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsgiRayMarchingParams2, cb._SsgiRayMarchingParams2);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsgiZBufferParams, cb._SsgiZBufferParams);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsgiColorPyramidScaleAndLimitPrev, cb._SsgiColorPyramidScaleAndLimitPrev);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsgiTemporalParams, cb._SsgiTemporalParams);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsgiDenoiserParams, cb._SsgiDenoiserParams);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsgiCameraPositionWS, cb._SsgiCameraPositionWS);
            cmd.SetComputeMatrixParam(compute, ScreenSpaceLightingShaderIDs.SsgiPrevInvViewProj, cb._SsgiPrevInvViewProj);
        }

        static void DispatchCompute(CommandBuffer cmd, ComputeShader compute, int kernel, int groupsX, int groupsY, int groupsZ)
        {
            cmd.DispatchCompute(compute, kernel, groupsX, groupsY, groupsZ);
        }

        static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        static void SetKeyword(ComputeShader compute, string keyword, bool enabled)
        {
            if (enabled)
                compute.EnableKeyword(keyword);
            else
                compute.DisableKeyword(keyword);
        }

        static RTHandle GetNormalTexture(ScriptableRenderer renderer)
        {
            if (renderer is UniversalRenderer universalRenderer)
                return universalRenderer.cameraNormalsTextureHandle;

            return null;
        }

        internal static bool UseOctEncodedNormals(ScriptableRenderer renderer)
        {
            if (!(renderer is UniversalRenderer universalRenderer))
                return false;

            return universalRenderer.renderingModeActual == RenderingMode.Deferred && universalRenderer.accurateGbufferNormals;
        }

        static RTHandle GetMotionTexture(ScriptableRenderer renderer)
        {
            if (renderer is UniversalRenderer universalRenderer)
                return universalRenderer.m_MotionVectorColor;

            return null;
        }

        void LogMissingResourcesOnce()
        {
            if (m_LoggedMissingResources)
                return;

            m_LoggedMissingResources = true;
            Debug.LogWarning(
                "Screen Space Global Illumination is enabled but its compute shaders are missing. " +
                "Assign the package resources in the renderer feature inspector.");
        }
    }
}