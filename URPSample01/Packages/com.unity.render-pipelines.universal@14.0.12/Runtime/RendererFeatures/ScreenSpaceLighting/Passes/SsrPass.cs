using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Opaque Screen Space Reflections: Approximation and PBR Accumulation.
    //
    // Approximation: Trace writes the mirror hit, Reproject samples the previous color pyramid and
    // writes radiance + weight straight to the material-facing texture.
    //
    // PBR Accumulation: Trace samples the GGX visible normal distribution, Reproject gathers the
    // reflected surface and writes the un-accumulated signal, then the reflection denoiser
    // temporally accumulates it against the history and runs a separable bilateral filter. The
    // material-facing contract is un-premultiplied radiance with the blend weight in alpha, so a miss
    // keeps the reflection probe.
    //
    // Runs after the SSGI dispatch (205) and before the material binding pass (207), so Deferred
    // GBuffer (210) and Forward opaque geometry (250) consume the same texture.
    internal sealed class ScreenSpaceLightingSsrPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 6);
        internal const string k_ProfilingSamplerName = "Screen Space Reflections";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

        const int k_TileSize = 8;

        ScreenSpaceLightingHistory m_History;
        ScreenSpaceLightingResources m_Resources;
        ScreenSpaceLightingSettings m_Settings;
        RTHandle m_OutputTexture;
        bool m_ProducedOutput;
        bool m_UsingPbrAccumulation;
        bool m_LoggedMissingResources;
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
#endif

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
        internal static int s_EditorTraceDispatchCount;
        internal static int s_EditorReprojectDispatchCount;
        internal static int s_EditorTemporalDispatchCount;
        internal static int s_EditorCopyHistoryDispatchCount;
        internal static int s_EditorBilateralH_DispatchCount;
        internal static int s_EditorBilateralV_DispatchCount;
        internal static string s_EditorLastDenoiseKernel = string.Empty;
        // (denoiser radius, roughness based denoising, min smoothness, history validity).
        internal static Vector4 s_EditorLastDenoiseParams;

        // Verification hook: resets the dispatch evidence between configuration sweeps.
        internal static void ResetEditorEvidenceCounters()
        {
            s_EditorExecuteCount = 0;
            s_EditorTraceDispatchCount = 0;
            s_EditorReprojectDispatchCount = 0;
            s_EditorTemporalDispatchCount = 0;
            s_EditorCopyHistoryDispatchCount = 0;
            s_EditorBilateralH_DispatchCount = 0;
            s_EditorBilateralV_DispatchCount = 0;
            s_EditorLastDenoiseKernel = string.Empty;
            s_EditorLastDenoiseParams = Vector4.zero;
        }
#endif

        internal bool producedOutput => m_ProducedOutput;
        internal RTHandle outputTexture => m_ProducedOutput ? m_OutputTexture : null;
        internal bool usingPbrAccumulation => m_UsingPbrAccumulation;

        internal ScreenSpaceLightingSsrPass()
        {
            renderPassEvent = k_RenderPassEvent;
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal | ScriptableRenderPassInput.Motion);
        }

        internal void Setup(ScreenSpaceLightingHistory history, ScreenSpaceLightingResources resources, ScreenSpaceLightingSettings settings)
        {
            m_History = history;
            m_Resources = resources;
            m_Settings = settings;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            s_EditorExecuteCount++;
#endif
            m_ProducedOutput = false;
            m_OutputTexture = null;
            m_UsingPbrAccumulation = false;

            if (m_History == null || m_Resources == null)
                return;

            var stack = VolumeManager.instance.stack;
            var settings = stack != null ? stack.GetComponent<ScreenSpaceReflection>() : null;
            if (settings == null || !settings.enabled.value)
            {
                m_LoggedMissingResources = false;
                return;
            }

            if (!m_Resources.hasSsrResources)
            {
                LogMissingResourcesOnce();
                return;
            }

            // Both algorithms sample the previous frame color pyramid. Without it (first frame,
            // camera cut, renderer creation) the only correct output is none, which keeps the
            // reflection probe / sky the material already computed.
            if (m_History.readColorPyramid == null)
                return;

            ref var cameraData = ref renderingData.cameraData;
            var descriptor = cameraData.cameraTargetDescriptor;
            int viewCount = ScreenSpaceLightingXr.GetViewCount(cameraData);
            TextureDimension dimension = ScreenSpaceLightingXr.GetResourceDimension(cameraData);

            var depthTexture = ScreenSpaceRenderUtils.GetCameraDepthTexture(cameraData.renderer);
            var normalTexture = ScreenSpaceLightingRenderUtils.GetNormalTexture(cameraData.renderer);
            var motionTexture = ScreenSpaceLightingRenderUtils.GetMotionTexture(cameraData.renderer);
            if (depthTexture == null || normalTexture == null || motionTexture == null)
                return;

            int screenWidth = Mathf.Max(1, descriptor.width);
            int screenHeight = Mathf.Max(1, descriptor.height);
            bool pbr = settings.usedAlgorithm.value == ScreenSpaceReflectionAlgorithm.PBRAccumulation;
            m_UsingPbrAccumulation = pbr;
            m_History.SyncSsrPbrAlgorithm(settings.usedAlgorithm.value);

            m_History.EnsureSsrResources(screenWidth, screenHeight, viewCount, dimension);
            if (!m_History.ssrResourcesAllocated)
                return;

            if (pbr)
            {
                m_History.EnsureSsrPbrResources(screenWidth, screenHeight, viewCount, dimension);
                if (!m_History.ssrPbrResourcesAllocated)
                    return;

                m_History.BeginSsrPbrFrame();
            }

            bool useTextureArray = dimension == TextureDimension.Tex2DArray;
            bool octNormals = ScreenSpaceLightingSsgiPass.UseOctEncodedNormals(cameraData.renderer);
            bool debugParams = pbr && m_Settings != null && m_Settings.debugEnabled &&
                               m_Settings.debugMode == ScreenSpaceLightingDebugMode.SsrPbrParams;
            if (cameraData.xr.enabled)
                ScreenSpaceLightingXr.FillViewConstants(cameraData, viewCount, m_XrViews, m_History);

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                Dispatch(cmd, ref cameraData, settings, depthTexture, normalTexture, motionTexture,
                    screenWidth, screenHeight, viewCount, useTextureArray, octNormals, pbr, debugParams);
            }

            if (pbr && !debugParams)
                m_History.SwapSsrPbrBuffers();

            m_OutputTexture = m_History.ssrOutput;
            m_History.MarkSsrOutputProduced();
            m_ProducedOutput = true;
        }

        void Dispatch(
            CommandBuffer cmd,
            ref CameraData cameraData,
            ScreenSpaceReflection settings,
            RTHandle depthTexture,
            RTHandle normalTexture,
            RTHandle motionTexture,
            int screenWidth,
            int screenHeight,
            int viewCount,
            bool useTextureArray,
            bool octNormals,
            bool pbr,
            bool debugParams)
        {
            var compute = m_Resources.ssrCompute;
            var denoiser = m_Resources.ssrReflectionDenoiserCompute;
            SetKeyword(compute, ScreenSpaceLightingShaderIDs.XrArrayKeyword, useTextureArray);
            SetKeyword(compute, ScreenSpaceLightingShaderIDs.SsrNormalsOctKeyword, octNormals);
            SetKeyword(compute, ScreenSpaceLightingShaderIDs.SsrApproxKeyword, !pbr);
            SetKeyword(denoiser, ScreenSpaceLightingShaderIDs.XrArrayKeyword, useTextureArray);
            SetKeyword(denoiser, ScreenSpaceLightingShaderIDs.SsgiNormalsOctKeyword, octNormals);

            if (useTextureArray)
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

            int groupsX = ScreenSpaceLightingRenderUtils.DivideRoundUp(screenWidth, k_TileSize);
            int groupsY = ScreenSpaceLightingRenderUtils.DivideRoundUp(screenHeight, k_TileSize);

            bool pbrHistoryValid = pbr && m_History.ssrPbrHasValidHistory;
            var cb = CreateShaderVariables(ref cameraData, settings, screenWidth, screenHeight, pbrHistoryValid);
            PushShaderVariables(cmd, compute, cb);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchViewCount = viewCount;
            s_LastDispatchTextureArray = useTextureArray ? 1 : 0;
#endif

            var traceKernel = compute.FindKernel("ScreenSpaceReflectionsTracing");
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiDepthPyramid, m_History.depthPyramid);
            cmd.SetComputeBufferParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiDepthPyramidMipLevelOffsets, m_History.depthPyramidOffsets);
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrHitPointTextureRW, m_History.ssrHitPoint);
            cmd.DispatchCompute(compute, traceKernel, groupsX, groupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_TraceDispatchCount++;
#endif
#if UNITY_EDITOR
            s_EditorTraceDispatchCount++;
#endif

            var reprojectKernel = compute.FindKernel("ScreenSpaceReflectionsReprojection");
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrHitPointTextureRW, m_History.ssrHitPoint);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrMotionTexture, motionTexture);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrColorPyramid, m_History.readColorPyramid);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrDepthTexture, depthTexture);
            if (pbr)
                cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrReprojectTextureRW, m_History.ssrPbrOriginal);
            else
                cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrOutputTextureRW, m_History.ssrOutput);
            cmd.DispatchCompute(compute, reprojectKernel, groupsX, groupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_ReprojectDispatchCount++;
#endif
#if UNITY_EDITOR
            s_EditorReprojectDispatchCount++;
#endif

            if (debugParams)
            {
                int debugKernel = compute.FindKernel("DebugSsrParams");
                cmd.SetComputeTextureParam(compute, debugKernel, ScreenSpaceLightingShaderIDs.SsrOutputTextureRW, m_History.ssrOutput);
                cmd.DispatchCompute(compute, debugKernel, groupsX, groupsY, viewCount);
                return;
            }

            if (pbr)
            {
                PushDenoiserShaderVariables(cmd, ref cameraData, denoiser, screenWidth, screenHeight, pbrHistoryValid);
                DispatchReflectionDenoise(cmd, denoiser, settings, depthTexture, normalTexture, motionTexture,
                    cameraData.camera.fieldOfView, screenWidth, screenHeight, groupsX, groupsY, viewCount, pbrHistoryValid);
            }
        }

        // The reflection denoiser reuses the geometry helpers of the SSGI temporal filter, so it reads
        // the same Screen Space Lighting constant set. The SSR trace remains full resolution until the
        // half resolution denoiser lands, which makes the effect and screen sizes identical.
        void PushDenoiserShaderVariables(
            CommandBuffer cmd,
            ref CameraData cameraData,
            ComputeShader denoiser,
            int screenWidth,
            int screenHeight,
            bool historyValid)
        {
            float near = Mathf.Max(1e-5f, cameraData.camera.nearClipPlane);
            float far = Mathf.Max(near + 1e-4f, cameraData.camera.farClipPlane);
            var cameraPosition = cameraData.camera.transform.position;
            var previousInvViewProj = ScreenSpaceLightingRenderUtils.GetPreviousViewProjection(cameraData).inverse;
            if (cameraData.xr.enabled)
            {
                // Multipass renders one eye per pass; single-pass kernels read the per-view globals.
                cameraPosition = m_XrViews[0].cameraPositionWS;
                previousInvViewProj = m_XrViews[0].hasPreviousViewProj
                    ? m_XrViews[0].prevInvViewProj
                    : Matrix4x4.Inverse(m_XrViews[0].viewProj);
            }

            var screenSize = new Vector4(screenWidth, screenHeight, 1f / screenWidth, 1f / screenHeight);
            cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiScreenSize, screenSize);
            cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiTraceSize, screenSize);
            cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiZBufferParams,
                ScreenSpaceLightingRenderUtils.ComputeZBufferParams(near, far));
            cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiCameraPositionWS, cameraPosition);
            cmd.SetComputeMatrixParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiPrevInvViewProj, previousInvViewProj);
            cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiRayMarchingParams2,
                new Vector4(1f, 0f, historyValid ? 1f : 0f, 0f));
        }

        void DispatchReflectionDenoise(
            CommandBuffer cmd,
            ComputeShader denoiser,
            ScreenSpaceReflection settings,
            RTHandle depthTexture,
            RTHandle normalTexture,
            RTHandle motionTexture,
            float fieldOfView,
            int screenWidth,
            int screenHeight,
            int groupsX,
            int groupsY,
            int viewCount,
            bool historyValid)
        {
            // A cleared (never written) slot is bound while the history is invalid; the temporal
            // kernel returns before sampling it because _HistoryValidity is zero.
            int readSlot = historyValid ? m_History.ssrPbrReadSlot : (m_History.ssrPbrWriteSlot ^ 1);
            int writeSlot = m_History.ssrPbrWriteSlot;

            cmd.SetComputeFloatParam(denoiser, ScreenSpaceLightingShaderIDs.PixelSpreadAngleTangent,
                ScreenSpaceLightingRenderUtils.ComputePixelSpreadTangent(fieldOfView, screenWidth, screenHeight));
            cmd.SetComputeIntParam(denoiser, ScreenSpaceLightingShaderIDs.AffectSmoothSurfaces,
                settings.affectSmoothSurfaces.value ? 1 : 0);
            cmd.SetComputeIntParam(denoiser, ScreenSpaceLightingShaderIDs.SingleReflectionBounce,
                settings.singleReflectionBounce.value ? 1 : 0);
            cmd.SetComputeFloatParam(denoiser, ScreenSpaceLightingShaderIDs.RoughnessBasedDenoising,
                settings.singleReflectionBounce.value ? 1f : 0f);
            cmd.SetComputeIntParam(denoiser, ScreenSpaceLightingShaderIDs.DenoiserFilterRadius, settings.denoiserRadius.value);
            cmd.SetComputeFloatParam(denoiser, ScreenSpaceLightingShaderIDs.RaytracingReflectionMinSmoothness,
                settings.minSmoothness.value);

            var temporalKernel = denoiser.FindKernel("TemporalAccumulationFullRes");
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.DenoiseInputTexture, m_History.ssrPbrOriginal);
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.DenoiseDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.DenoiseNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.DenoiseMotionTexture, motionTexture);
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.HistoryDepthTexture, m_History.readPreviousDepth);
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.HistoryNormalTexture, m_History.readPreviousNormals);
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.HistoryBuffer, m_History.GetSsrPbrHistory(readSlot));
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.HistorySampleCountTexture, m_History.GetSsrPbrSampleCount(readSlot));
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.DenoiseOutputTextureRW, m_History.ssrDenoiseIntermediate0);
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.DenoiseSampleCountRW, m_History.GetSsrPbrSampleCount(writeSlot));
            cmd.DispatchCompute(denoiser, temporalKernel, groupsX, groupsY, viewCount);
#if UNITY_EDITOR
            s_EditorTemporalDispatchCount++;
            s_EditorLastDenoiseKernel = "TemporalAccumulationFullRes";
            s_EditorLastDenoiseParams = new Vector4(
                settings.denoiserRadius.value,
                settings.singleReflectionBounce.value ? 1f : 0f,
                settings.minSmoothness.value,
                historyValid ? 1f : 0f);
#endif

            // The accumulated signal becomes next frame's history. The sample count already lives in
            // the write slot, written by the temporal dispatch.
            var copyKernel = denoiser.FindKernel("CopyHistory");
            cmd.SetComputeTextureParam(denoiser, copyKernel, ScreenSpaceLightingShaderIDs.DenoiseInputTexture, m_History.ssrDenoiseIntermediate0);
            cmd.SetComputeTextureParam(denoiser, copyKernel, ScreenSpaceLightingShaderIDs.DenoiseOutputTextureRW, m_History.GetSsrPbrHistory(writeSlot));
            cmd.DispatchCompute(denoiser, copyKernel, groupsX, groupsY, viewCount);
#if UNITY_EDITOR
            s_EditorCopyHistoryDispatchCount++;
            s_EditorLastDenoiseKernel = "CopyHistory";
#endif

            var mapping = m_Resources.ssrReflectionFilterMapping;
            var filterHKernel = denoiser.FindKernel("BilateralFilterH_FR");
            cmd.SetComputeTextureParam(denoiser, filterHKernel, ScreenSpaceLightingShaderIDs.DenoiseInputTexture, m_History.ssrDenoiseIntermediate0);
            cmd.SetComputeTextureParam(denoiser, filterHKernel, ScreenSpaceLightingShaderIDs.DenoiseDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(denoiser, filterHKernel, ScreenSpaceLightingShaderIDs.DenoiseNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(denoiser, filterHKernel, ScreenSpaceLightingShaderIDs.DenoiseOutputTextureRW, m_History.ssrDenoiseIntermediate1);
            cmd.SetComputeTextureParam(denoiser, filterHKernel, ScreenSpaceLightingShaderIDs.ReflectionFilterMapping, mapping);
            cmd.DispatchCompute(denoiser, filterHKernel, groupsX, groupsY, viewCount);
#if UNITY_EDITOR
            s_EditorBilateralH_DispatchCount++;
            s_EditorLastDenoiseKernel = "BilateralFilterH_FR";
#endif

            var filterVKernel = denoiser.FindKernel("BilateralFilterV_FR");
            cmd.SetComputeTextureParam(denoiser, filterVKernel, ScreenSpaceLightingShaderIDs.DenoiseInputTexture, m_History.ssrDenoiseIntermediate1);
            cmd.SetComputeTextureParam(denoiser, filterVKernel, ScreenSpaceLightingShaderIDs.DenoiseDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(denoiser, filterVKernel, ScreenSpaceLightingShaderIDs.DenoiseNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(denoiser, filterVKernel, ScreenSpaceLightingShaderIDs.DenoiseOutputTextureRW, m_History.ssrOutput);
            cmd.SetComputeTextureParam(denoiser, filterVKernel, ScreenSpaceLightingShaderIDs.ReflectionFilterMapping, mapping);
            cmd.DispatchCompute(denoiser, filterVKernel, groupsX, groupsY, viewCount);
#if UNITY_EDITOR
            s_EditorBilateralV_DispatchCount++;
            s_EditorLastDenoiseKernel = "BilateralFilterV_FR";
#endif
        }

        ScreenSpaceLightingSsrShaderVariables CreateShaderVariables(
            ref CameraData cameraData,
            ScreenSpaceReflection settings,
            int screenWidth,
            int screenHeight,
            bool historyValid)
        {
            float near = Mathf.Max(1e-5f, cameraData.camera.nearClipPlane);
            float far = Mathf.Max(near + 1e-4f, cameraData.camera.farClipPlane);
            float thickness = Mathf.Max(0f, settings.depthBufferThickness.value);
            float thicknessScale = 1f / (1f + thickness);
            float thicknessBias = -near / (far - near) * (thickness * thicknessScale);

            float roughnessFadeEnd = 1f - settings.minSmoothness.value;
            float roughnessFadeStart = 1f - settings.smoothnessFadeStart.value;
            float roughnessFadeLength = roughnessFadeEnd - roughnessFadeStart;
            float fadeRcpLength = roughnessFadeLength != 0f ? 1f / roughnessFadeLength : 0f;
            float fadeEndTimesRcpLength = roughnessFadeLength != 0f ? roughnessFadeEnd * fadeRcpLength : 1f;
            float edgeFadeRcpLength = Mathf.Min(1f / Mathf.Max(1e-4f, settings.screenFadeDistance.value), float.MaxValue);

            var cb = default(ScreenSpaceLightingSsrShaderVariables);
            cb._SsrScreenSize = new Vector4(screenWidth, screenHeight, 1f / screenWidth, 1f / screenHeight);
            cb._SsrRayMarchingParams = new Vector4(
                Mathf.Max(1, settings.rayMaxIterations.value),
                thicknessScale,
                thicknessBias,
                settings.reflectSky.value ? 1f : 0f);
            cb._SsrRoughnessFadeParams = new Vector4(
                fadeRcpLength,
                fadeEndTimesRcpLength,
                roughnessFadeEnd,
                edgeFadeRcpLength);
            cb._SsrPyramidParams = new Vector4(
                Mathf.Max(0, m_History.colorPyramidMipCount - 1),
                Mathf.Max(0, m_History.depthPyramidMipCount - 1),
                0f,
                0f);
            // No render scale or dynamic resolution offset is applied to the SSL history in this
            // version, so the previous frame pyramid uses the full 0..1 range.
            cb._SsrColorPyramidScaleAndLimitPrev = new Vector4(1f, 1f, 1f, 1f);
            // Multipass uses the rendered eye centre; single-pass kernels read the per-view globals.
            cb._SsrCameraPositionWS = cameraData.xr.enabled
                ? m_XrViews[0].cameraPositionWS
                : cameraData.camera.transform.position;
            cb._SsrPbrParams = new Vector4(0f, settings.biasFactor.value, 0f, 0f);
            cb._SsrPbrFlags = new Vector4(Time.frameCount % 64, historyValid ? 1f : 0f, 0f, 0f);
            return cb;
        }

        static void PushShaderVariables(CommandBuffer cmd, ComputeShader compute, in ScreenSpaceLightingSsrShaderVariables cb)
        {
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrScreenSize, cb._SsrScreenSize);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrRayMarchingParams, cb._SsrRayMarchingParams);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrRoughnessFadeParams, cb._SsrRoughnessFadeParams);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrPyramidParams, cb._SsrPyramidParams);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrColorPyramidScaleAndLimitPrev, cb._SsrColorPyramidScaleAndLimitPrev);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrCameraPositionWS, cb._SsrCameraPositionWS);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrPbrParams, cb._SsrPbrParams);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrPbrFlags, cb._SsrPbrFlags);
        }

        static void SetKeyword(ComputeShader compute, string keyword, bool enabled)
        {
            if (enabled)
                compute.EnableKeyword(keyword);
            else
                compute.DisableKeyword(keyword);
        }

        void LogMissingResourcesOnce()
        {
            if (m_LoggedMissingResources)
                return;

            m_LoggedMissingResources = true;
            Debug.LogWarning(
                "Screen Space Reflections are enabled but the SSR compute shader is missing. " +
                "Assign the package resources in the renderer feature inspector.");
        }
    }
}