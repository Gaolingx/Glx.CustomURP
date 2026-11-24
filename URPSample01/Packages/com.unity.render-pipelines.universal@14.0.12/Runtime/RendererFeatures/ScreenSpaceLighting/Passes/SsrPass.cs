using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Opaque Screen Space Reflections: Approximation and PBR Accumulation.
    //
    // Approximation: Trace writes the mirror hit, Reproject samples the previous color pyramid and
    // writes radiance + weight. At full resolution that result is the material-facing texture; at
    // half resolution it is bilaterally upsampled into the full resolution output.
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
        // Internal injection only. R1 does not opt any renderer into unfinished RT.
        internal SsrRequestQueue requestQueue;
        bool WantsRequests(ref CameraData camera, ScreenSpaceReflection settings) =>
            requestQueue != null && requestQueue.budget > 0 && !settings.fullResolution.value &&
            !camera.xr.enabled && camera.renderType == CameraRenderType.Base && camera.cameraType == CameraType.Game;
        RTHandle m_OutputTexture;
        bool m_ProducedOutput;
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
        // Pixel spread angle the reflection denoiser receives, with the trace size it was derived
        // from. The denoiser reads the shared _SsgiDenoiserParams.x uniform, so a standalone float
        // would be shadowed by the macro in ReflectionDenoiser.compute.
        internal static float s_LastPixelSpreadAngleTangent;
        internal static int s_LastPixelSpreadTraceWidth;
        internal static int s_LastPixelSpreadTraceHeight;
        internal static int s_LastPixelSpreadFrame = -1;
        // Debug view routing evidence: the parameter kernel must target its own texture and the
        // material-facing output must stay on the real SSR result.
        internal static int s_LastDebugParamsFrame = -1;
        internal static string s_LastDebugParamsTarget = string.Empty;
        internal static string s_LastMaterialOutputTarget = string.Empty;

        // Verification snapshot: the player cannot see the editor-only counters above, so every
        // dispatched frame publishes the algorithm, sizes, groups, keywords and history slots it
        // actually used. One snapshot per camera/frame is enough; the player reads it after warmup.
        internal sealed class SsrDispatchStats
        {
            public int frameIndex = -1;
            public int screenWidth;
            public int screenHeight;
            public int traceWidth;
            public int traceHeight;
            public int groupsZ = 1;
            public bool pbr;
            public bool halfResolution;
            public bool approxKeyword;
            public bool halfResKeyword;
            public bool normalsOctKeyword;
            public bool blueNoiseKeyword;
            public bool xrArrayKeyword;

            public int traceDispatches;
            public int reprojectDispatches;
            public int temporalDispatches;
            public int copyHistoryDispatches;
            public int bilateralHDispatches;
            public int bilateralVDispatches;
            public int upsampleDispatches;
            public int debugDispatches;

            public int traceGroupsX;
            public int traceGroupsY;
            public int screenGroupsX;
            public int screenGroupsY;

            public bool historyValid;
            public int historyReadSlot = -1;
            public int historyWriteSlot = -1;
            public int historyResetCount;

            // Oldest first. Eight frames are enough to show the ping-pong in one report.
            public readonly int[] historyReadSlots = new int[8];
            public readonly int[] historyWriteSlots = new int[8];

            public void BeginFrame(
                int frame, int width, int height, int traceW, int traceH, int viewCount,
                bool isPbr, bool halfRes, bool octNormals, bool blueNoise, bool xrArray,
                int traceGX, int traceGY, int screenGX, int screenGY)
            {
                frameIndex = frame;
                screenWidth = width;
                screenHeight = height;
                traceWidth = traceW;
                traceHeight = traceH;
                groupsZ = viewCount;
                pbr = isPbr;
                halfResolution = halfRes;
                approxKeyword = !isPbr;
                halfResKeyword = halfRes;
                normalsOctKeyword = octNormals;
                blueNoiseKeyword = blueNoise;
                xrArrayKeyword = xrArray;
                traceGroupsX = traceGX;
                traceGroupsY = traceGY;
                screenGroupsX = screenGX;
                screenGroupsY = screenGY;
                traceDispatches = 0;
                reprojectDispatches = 0;
                temporalDispatches = 0;
                copyHistoryDispatches = 0;
                bilateralHDispatches = 0;
                bilateralVDispatches = 0;
                upsampleDispatches = 0;
                debugDispatches = 0;
                historyValid = false;
                historyReadSlot = -1;
                historyWriteSlot = -1;
                for (int i = 1; i < historyReadSlots.Length; i++)
                {
                    historyReadSlots[i - 1] = historyReadSlots[i];
                    historyWriteSlots[i - 1] = historyWriteSlots[i];
                }

                int last = historyReadSlots.Length - 1;
                historyReadSlots[last] = -1;
                historyWriteSlots[last] = -1;
            }

            public void RecordHistory(int readSlot, int writeSlot, bool valid, int resetCount)
            {
                historyValid = valid;
                historyReadSlot = readSlot;
                historyWriteSlot = writeSlot;
                historyResetCount = resetCount;
                int last = historyReadSlots.Length - 1;
                historyReadSlots[last] = readSlot;
                historyWriteSlots[last] = writeSlot;
            }
        }

        internal static readonly SsrDispatchStats s_LastDispatchStats = new SsrDispatchStats();
#endif

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
        internal static int s_EditorTraceDispatchCount;
        internal static int s_EditorReprojectDispatchCount;
        internal static int s_EditorTemporalDispatchCount;
        internal static int s_EditorCopyHistoryDispatchCount;
        internal static int s_EditorBilateralH_DispatchCount;
        internal static int s_EditorBilateralV_DispatchCount;
        internal static int s_EditorUpsampleDispatchCount;
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
            s_EditorUpsampleDispatchCount = 0;
            s_EditorLastDenoiseKernel = string.Empty;
            s_EditorLastDenoiseParams = Vector4.zero;
        }
#endif

        internal bool producedOutput => m_ProducedOutput;
        internal RTHandle outputTexture => m_ProducedOutput ? m_OutputTexture : null;
        internal RTHandle debugTexture => m_History != null ? m_History.ssrDebugParams : null;

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
            ref var cameraData = ref renderingData.cameraData;
            if (m_History.readColorPyramid == null)
            {
                if (WantsRequests(ref cameraData, settings))
                {
                    var depth = ScreenSpaceRenderUtils.GetCameraDepthTexture(cameraData.renderer);
                    var normals = ScreenSpaceLightingRenderUtils.GetNormalTexture(cameraData.renderer);
                    if (depth != null && normals != null)
                    {
                        var cs = m_Resources.ssrCompute;
                        var buffer = renderingData.commandBuffer;
                        int w = Mathf.Max(1,cameraData.cameraTargetDescriptor.width), h = Mathf.Max(1,cameraData.cameraTargetDescriptor.height);
                        int tw = Mathf.Max(1,w >> 1), th = Mathf.Max(1,h >> 1);
                        SetKeyword(cs, ScreenSpaceShaderIDs.XrArrayKeyword, false);
                        SetKeyword(cs, ScreenSpaceLightingShaderIDs.SsrNormalsOctKeyword, ScreenSpaceLightingSsgiPass.UseOctEncodedNormals(cameraData.renderer));
                        SetKeyword(cs, ScreenSpaceLightingShaderIDs.SsrApproxKeyword, settings.usedAlgorithm.value != ScreenSpaceReflectionAlgorithm.PBRAccumulation);
                        SetKeyword(cs, ScreenSpaceLightingShaderIDs.SsrHalfResKeyword, true);
                        SetKeyword(cs, ScreenSpaceLightingShaderIDs.SsgiBlueNoiseKeyword, m_Resources.hasSsrBlueNoise);
                        PushShaderVariables(buffer,cs,CreateShaderVariables(ref cameraData,settings,w,h,tw,th,false));
                        RecordOriginals(buffer,cs,depth,normals,null,null,tw,th);
                    }
                }
                return;
            }

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
            m_History.SyncSsrPbrAlgorithm(settings.usedAlgorithm.value);

            // Approximation and PBR share the volume's resolution switch. A half resolution trace
            // writes a trace sized color target and is bilaterally upsampled into the full resolution
            // material-facing output.
            bool halfResolution = !settings.fullResolution.value;
            int traceWidth = halfResolution ? Mathf.Max(1, screenWidth >> 1) : screenWidth;
            int traceHeight = halfResolution ? Mathf.Max(1, screenHeight >> 1) : screenHeight;

            m_History.EnsureSsrResources(screenWidth, screenHeight, traceWidth, traceHeight, viewCount, dimension);
            if (!m_History.ssrResourcesAllocated)
                return;

            if (pbr)
            {
                m_History.EnsureSsrPbrResources(screenWidth, screenHeight, traceWidth, traceHeight, viewCount, dimension);
                if (!m_History.ssrPbrResourcesAllocated)
                    return;

                m_History.BeginSsrPbrFrame();
            }

            bool useTextureArray = dimension == TextureDimension.Tex2DArray;
            bool octNormals = ScreenSpaceLightingSsgiPass.UseOctEncodedNormals(cameraData.renderer);
            bool debugParams = pbr && m_Settings != null && m_Settings.debugEnabled &&
                               m_Settings.debugMode == ScreenSpaceLightingDebugMode.SsrPbrParams;
            if (debugParams)
                m_History.EnsureSsrDebugResources(screenWidth, screenHeight, viewCount, dimension);
            if (cameraData.xr.enabled)
                ScreenSpaceLightingXr.FillViewConstants(cameraData, viewCount, m_XrViews, m_History);

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                Dispatch(cmd, ref cameraData, settings, depthTexture, normalTexture, motionTexture,
                    screenWidth, screenHeight, traceWidth, traceHeight, viewCount, useTextureArray, octNormals,
                    pbr, halfResolution, debugParams);
            }

            if (pbr)
                m_History.SwapSsrPbrBuffers();

            // Full resolution PBR writes the filtered result into its trace sized original texture.
            // Every half resolution path, and the full resolution Approximation path, leaves the
            // material-facing result in ssrOutput.
            m_OutputTexture = pbr && !halfResolution
                ? m_History.ssrPbrOriginal
                : m_History.ssrOutput;
            m_ProducedOutput = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastMaterialOutputTarget = m_OutputTexture != null ? m_OutputTexture.name : string.Empty;
            if (debugParams)
            {
                s_LastDebugParamsFrame = Time.frameCount;
                s_LastDebugParamsTarget = m_History.ssrDebugParams != null
                    ? m_History.ssrDebugParams.name
                    : string.Empty;
            }
#endif
        }

        void RecordOriginals(CommandBuffer cmd, ComputeShader cs, RTHandle depth, RTHandle normals,
            RTHandle motion, RTHandle hit, int w, int h)
        {
            int kernel = cs.FindKernel(hit == null ? "R1Cold" : "R1Original");
            BindSsrBlueNoise(cmd, cs, kernel);
            cmd.SetComputeTextureParam(cs,kernel,ScreenSpaceLightingShaderIDs.SsrDepthTexture,depth);
            cmd.SetComputeTextureParam(cs,kernel,ScreenSpaceLightingShaderIDs.SsrNormalTexture,normals);
            if (hit != null)
            {
                cmd.SetComputeTextureParam(cs,kernel,ScreenSpaceLightingShaderIDs.SsrHitPointTextureRW,hit);
                cmd.SetComputeTextureParam(cs,kernel,ScreenSpaceLightingShaderIDs.SsrMotionTexture,motion);
                cmd.SetComputeTextureParam(cs,kernel,ScreenSpaceLightingShaderIDs.SsrColorPyramid,m_History.readColorPyramid);
            }
            requestQueue.Record(cmd,cs,kernel,w,h,Time.frameCount);
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
            int traceWidth,
            int traceHeight,
            int viewCount,
            bool useTextureArray,
            bool octNormals,
            bool pbr,
            bool halfResolution,
            bool debugParams)
        {
            var compute = m_Resources.ssrCompute;
            var denoiser = m_Resources.ssrReflectionDenoiserCompute;
            SetKeyword(compute, ScreenSpaceShaderIDs.XrArrayKeyword, useTextureArray);
            SetKeyword(compute, ScreenSpaceLightingShaderIDs.SsrNormalsOctKeyword, octNormals);
            SetKeyword(compute, ScreenSpaceLightingShaderIDs.SsrApproxKeyword, !pbr);
            SetKeyword(compute, ScreenSpaceLightingShaderIDs.SsrHalfResKeyword, halfResolution);
            SetKeyword(compute, ScreenSpaceLightingShaderIDs.SsgiBlueNoiseKeyword, m_Resources.hasSsrBlueNoise);
            SetKeyword(denoiser, ScreenSpaceShaderIDs.XrArrayKeyword, useTextureArray);
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

            int traceGroupsX = ScreenSpaceLightingRenderUtils.DivideRoundUp(traceWidth, k_TileSize);
            int traceGroupsY = ScreenSpaceLightingRenderUtils.DivideRoundUp(traceHeight, k_TileSize);
            int screenGroupsX = ScreenSpaceLightingRenderUtils.DivideRoundUp(screenWidth, k_TileSize);
            int screenGroupsY = ScreenSpaceLightingRenderUtils.DivideRoundUp(screenHeight, k_TileSize);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var dispatchStats = s_LastDispatchStats;
            dispatchStats.BeginFrame(
                Time.frameCount, screenWidth, screenHeight, traceWidth, traceHeight, viewCount,
                pbr, halfResolution, octNormals, m_Resources.hasSsrBlueNoise, useTextureArray,
                traceGroupsX, traceGroupsY, screenGroupsX, screenGroupsY);
            if (pbr)
            {
                // BeginSsrPbrFrame already picked this frame's slots.
                dispatchStats.RecordHistory(
                    m_History.ssrPbrReadSlot, m_History.ssrPbrWriteSlot,
                    m_History.ssrPbrHasValidHistory, m_History.ssrPbrResetCount);
            }
#endif

            bool pbrHistoryValid = pbr && m_History.ssrPbrHasValidHistory;
            var cb = CreateShaderVariables(ref cameraData, settings, screenWidth, screenHeight, traceWidth, traceHeight, pbrHistoryValid);
            PushShaderVariables(cmd, compute, cb);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchViewCount = viewCount;
            s_LastDispatchTextureArray = useTextureArray ? 1 : 0;
#endif

            var traceKernel = compute.FindKernel("ScreenSpaceReflectionsTracing");
            BindSsrBlueNoise(cmd, compute, traceKernel);
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiDepthPyramid, m_History.depthPyramid);
            cmd.SetComputeBufferParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiDepthPyramidMipLevelOffsets, m_History.depthPyramidOffsets);
            // Both algorithms trace into a hit point that follows the trace resolution.
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrHitPointTextureRW,
                pbr ? m_History.ssrPbrHitPoint : m_History.ssrHitPoint);
            cmd.DispatchCompute(compute, traceKernel, traceGroupsX, traceGroupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_TraceDispatchCount++;
            s_LastDispatchStats.traceDispatches++;
#endif
#if UNITY_EDITOR
            s_EditorTraceDispatchCount++;
#endif

            if (WantsRequests(ref cameraData, settings))
                RecordOriginals(cmd, compute, depthTexture, normalTexture, motionTexture,
                    pbr ? m_History.ssrPbrHitPoint : m_History.ssrHitPoint, traceWidth, traceHeight);

            var reprojectKernel = compute.FindKernel("ScreenSpaceReflectionsReprojection");
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrHitPointTextureRW,
                pbr ? m_History.ssrPbrHitPoint : m_History.ssrHitPoint);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrMotionTexture, motionTexture);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrColorPyramid, m_History.readColorPyramid);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrDepthTexture, depthTexture);
            if (pbr)
                cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrReprojectTextureRW, m_History.ssrPbrOriginal);
            else
                cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrOutputTextureRW,
                    halfResolution && m_History.ssrApproxOutput != null ? m_History.ssrApproxOutput : m_History.ssrOutput);
            cmd.DispatchCompute(compute, reprojectKernel, traceGroupsX, traceGroupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_ReprojectDispatchCount++;
            s_LastDispatchStats.reprojectDispatches++;
#endif
#if UNITY_EDITOR
            s_EditorReprojectDispatchCount++;
#endif

            if (debugParams)
            {
                // Diagnostics have their own target. Writing them into m_History.ssrOutput replaced
                // the material-facing texture, because the setup pass binds that same handle to the
                // opaque GBuffer / forward shading.
                int debugKernel = compute.FindKernel("DebugSsrParams");
                cmd.SetComputeTextureParam(compute, debugKernel, ScreenSpaceLightingShaderIDs.SsrOutputTextureRW,
                    m_History.ssrDebugParams);
                cmd.DispatchCompute(compute, debugKernel, screenGroupsX, screenGroupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_LastDispatchStats.debugDispatches++;
#endif
            }

            if (pbr)
            {
                PushDenoiserShaderVariables(cmd, ref cameraData, denoiser, screenWidth, screenHeight, traceWidth, traceHeight, pbrHistoryValid);
                DispatchReflectionDenoise(cmd, denoiser, settings, depthTexture, normalTexture, motionTexture,
                    traceGroupsX, traceGroupsY, viewCount, pbrHistoryValid, halfResolution);

                if (halfResolution)
                {
                    DispatchUpsample(cmd, depthTexture, m_History.ssrPbrOriginal, m_History.ssrOutput,
                        screenWidth, screenHeight, traceWidth, traceHeight, screenGroupsX, screenGroupsY,
                        viewCount, useTextureArray);
                }
            }
            else if (halfResolution && m_History.ssrApproxOutput != null)
            {
                // The half resolution Approximation reproject wrote radiance + weight into the trace
                // sized target; upsample it into the full resolution material-facing output.
                DispatchUpsample(cmd, depthTexture, m_History.ssrApproxOutput, m_History.ssrOutput,
                    screenWidth, screenHeight, traceWidth, traceHeight, screenGroupsX, screenGroupsY,
                    viewCount, useTextureArray);
            }
        }

        // The reflection denoiser reuses the geometry helpers of the SSGI temporal filter, so it reads
        // the same Screen Space Lighting constant set. The effect resolution follows the trace size,
        // which is half the screen for the half resolution PBR path.
        void PushDenoiserShaderVariables(
            CommandBuffer cmd,
            ref CameraData cameraData,
            ComputeShader denoiser,
            int screenWidth,
            int screenHeight,
            int traceWidth,
            int traceHeight,
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
            var traceSize = new Vector4(traceWidth, traceHeight, 1f / traceWidth, 1f / traceHeight);
            cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiScreenSize, screenSize);
            cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiTraceSize, traceSize);
            cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiZBufferParams,
                ScreenSpaceLightingRenderUtils.ComputeZBufferParams(near, far));
            cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiCameraPositionWS, cameraPosition);
            cmd.SetComputeMatrixParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiPrevInvViewProj, previousInvViewProj);
            cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiRayMarchingParams2,
                new Vector4(1f, 0f, historyValid ? 1f : 0f, 0f));

            // ReflectionDenoiser.compute maps _PixelSpreadAngleTangent to _SsgiDenoiserParams.x, and
            // this pass is the only writer for its kernels. A standalone float write lands on an
            // undeclared uniform and leaves the reprojection radius at its 0.1 world unit floor.
            float pixelSpreadTangent = ScreenSpaceLightingRenderUtils.ComputePixelSpreadTangent(
                cameraData.camera.fieldOfView, traceWidth, traceHeight);
            cmd.SetComputeVectorParam(denoiser, ScreenSpaceLightingShaderIDs.SsgiDenoiserParams,
                new Vector4(pixelSpreadTangent, -1f, 0f, 0f));
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastPixelSpreadAngleTangent = pixelSpreadTangent;
            s_LastPixelSpreadTraceWidth = traceWidth;
            s_LastPixelSpreadTraceHeight = traceHeight;
            s_LastPixelSpreadFrame = Time.frameCount;
#endif
        }

        void DispatchReflectionDenoise(
            CommandBuffer cmd,
            ComputeShader denoiser,
            ScreenSpaceReflection settings,
            RTHandle depthTexture,
            RTHandle normalTexture,
            RTHandle motionTexture,
            int groupsX,
            int groupsY,
            int viewCount,
            bool historyValid,
            bool halfResolution)
        {
            // A cleared (never written) slot is bound while the history is invalid; the temporal
            // kernel returns before sampling it because _HistoryValidity is zero.
            int readSlot = historyValid ? m_History.ssrPbrReadSlot : (m_History.ssrPbrWriteSlot ^ 1);
            int writeSlot = m_History.ssrPbrWriteSlot;

            cmd.SetComputeIntParam(denoiser, ScreenSpaceLightingShaderIDs.AffectSmoothSurfaces,
                settings.affectSmoothSurfaces.value ? 1 : 0);
            cmd.SetComputeIntParam(denoiser, ScreenSpaceLightingShaderIDs.SingleReflectionBounce,
                settings.singleReflectionBounce.value ? 1 : 0);
            cmd.SetComputeFloatParam(denoiser, ScreenSpaceLightingShaderIDs.RoughnessBasedDenoising,
                settings.singleReflectionBounce.value ? 1f : 0f);
            // HDRP halves the kernel size for the half resolution denoiser.
            int denoiserRadius = halfResolution ? settings.denoiserRadius.value / 2 : settings.denoiserRadius.value;
            cmd.SetComputeIntParam(denoiser, ScreenSpaceLightingShaderIDs.DenoiserFilterRadius, denoiserRadius);
            cmd.SetComputeFloatParam(denoiser, ScreenSpaceLightingShaderIDs.RaytracingReflectionMinSmoothness,
                settings.minSmoothness.value);

            string temporalKernelName = halfResolution ? "TemporalAccumulationHalfRes" : "TemporalAccumulationFullRes";
            var temporalKernel = denoiser.FindKernel(temporalKernelName);
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchStats.temporalDispatches++;
#endif
#if UNITY_EDITOR
            s_EditorTemporalDispatchCount++;
            s_EditorLastDenoiseKernel = temporalKernelName;
            s_EditorLastDenoiseParams = new Vector4(
                denoiserRadius,
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchStats.copyHistoryDispatches++;
#endif
#if UNITY_EDITOR
            s_EditorCopyHistoryDispatchCount++;
            s_EditorLastDenoiseKernel = "CopyHistory";
#endif

            var mapping = m_Resources.ssrReflectionFilterMapping;
            string filterHKernelName = halfResolution ? "BilateralFilterH_HR" : "BilateralFilterH_FR";
            var filterHKernel = denoiser.FindKernel(filterHKernelName);
            cmd.SetComputeTextureParam(denoiser, filterHKernel, ScreenSpaceLightingShaderIDs.DenoiseInputTexture, m_History.ssrDenoiseIntermediate0);
            cmd.SetComputeTextureParam(denoiser, filterHKernel, ScreenSpaceLightingShaderIDs.DenoiseDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(denoiser, filterHKernel, ScreenSpaceLightingShaderIDs.DenoiseNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(denoiser, filterHKernel, ScreenSpaceLightingShaderIDs.DenoiseOutputTextureRW, m_History.ssrDenoiseIntermediate1);
            cmd.SetComputeTextureParam(denoiser, filterHKernel, ScreenSpaceLightingShaderIDs.ReflectionFilterMapping, mapping);
            cmd.DispatchCompute(denoiser, filterHKernel, groupsX, groupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchStats.bilateralHDispatches++;
#endif
#if UNITY_EDITOR
            s_EditorBilateralH_DispatchCount++;
            s_EditorLastDenoiseKernel = filterHKernelName;
#endif

            string filterVKernelName = halfResolution ? "BilateralFilterV_HR" : "BilateralFilterV_FR";
            var filterVKernel = denoiser.FindKernel(filterVKernelName);
            cmd.SetComputeTextureParam(denoiser, filterVKernel, ScreenSpaceLightingShaderIDs.DenoiseInputTexture, m_History.ssrDenoiseIntermediate1);
            cmd.SetComputeTextureParam(denoiser, filterVKernel, ScreenSpaceLightingShaderIDs.DenoiseDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(denoiser, filterVKernel, ScreenSpaceLightingShaderIDs.DenoiseNormalTexture, normalTexture);
            // The original texture is free after the temporal pass consumed it; HDRP also writes the
            // filtered result back into the noisy input. The half resolution path upsamples it.
            cmd.SetComputeTextureParam(denoiser, filterVKernel, ScreenSpaceLightingShaderIDs.DenoiseOutputTextureRW, m_History.ssrPbrOriginal);
            cmd.SetComputeTextureParam(denoiser, filterVKernel, ScreenSpaceLightingShaderIDs.ReflectionFilterMapping, mapping);
            cmd.DispatchCompute(denoiser, filterVKernel, groupsX, groupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchStats.bilateralVDispatches++;
#endif
#if UNITY_EDITOR
            s_EditorBilateralV_DispatchCount++;
            s_EditorLastDenoiseKernel = filterVKernelName;
#endif
        }

        // Half resolution SSR result -> full resolution material-facing output. Reuses the SSGI
        // bilateral upsample, which is depth weighted so a half resolution texel never borrows
        // radiance across a silhouette. The alpha (blend weight) is filtered with the same weights.
        void DispatchUpsample(
            CommandBuffer cmd,
            RTHandle depthTexture,
            RTHandle lowResolution,
            RTHandle output,
            int screenWidth,
            int screenHeight,
            int traceWidth,
            int traceHeight,
            int groupsX,
            int groupsY,
            int viewCount,
            bool useTextureArray)
        {
            var upsample = m_Resources.bilateralUpsampleCompute;
            SetKeyword(upsample, ScreenSpaceShaderIDs.XrArrayKeyword, useTextureArray);

            var screenSize = new Vector4(screenWidth, screenHeight, 1f / screenWidth, 1f / screenHeight);
            var traceSize = new Vector4(traceWidth, traceHeight, 1f / traceWidth, 1f / traceHeight);
            float lowResPercentage = traceWidth / (float)screenWidth;
            cmd.SetComputeVectorParam(upsample, ScreenSpaceLightingShaderIDs.SsgiScreenSize, screenSize);
            cmd.SetComputeVectorParam(upsample, ScreenSpaceLightingShaderIDs.SsgiTraceSize, traceSize);
            cmd.SetComputeVectorParam(upsample, ScreenSpaceLightingShaderIDs.SsgiTemporalParams,
                new Vector4(lowResPercentage, 1f / lowResPercentage, 0f, 0f));

            var kernel = upsample.FindKernel("BilateralUpSampleColor");
            cmd.SetComputeTextureParam(upsample, kernel, ScreenSpaceLightingShaderIDs.SsgiDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(upsample, kernel, ScreenSpaceLightingShaderIDs.SsgiLowResolutionTexture, lowResolution);
            cmd.SetComputeTextureParam(upsample, kernel, ScreenSpaceLightingShaderIDs.SsgiUpsampledTextureRW, output);
            cmd.DispatchCompute(upsample, kernel, groupsX, groupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchStats.upsampleDispatches++;
#endif
#if UNITY_EDITOR
            s_EditorUpsampleDispatchCount++;
#endif
        }

        ScreenSpaceLightingSsrShaderVariables CreateShaderVariables(
            ref CameraData cameraData,
            ScreenSpaceReflection settings,
            int screenWidth,
            int screenHeight,
            int traceWidth,
            int traceHeight,
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
            cb._SsrTraceSize = new Vector4(traceWidth, traceHeight, 1f / traceWidth, 1f / traceHeight);
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
            // The trace reads the packed depth chain up to its actual last mip, capped like HDRP's
            // _SsrDepthPyramidMaxMip. SSGI keeps the shared helper's mip 6 clamp.
            cb._SsrPyramidParams = new Vector4(
                Mathf.Max(0, m_History.colorPyramidMipCount - 1),
                ScreenSpaceLightingRenderUtils.ComputeDepthPyramidMaxMip(m_History.depthPyramidInfo.mipLevelCount),
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
            cb._SsrPbrFlags = new Vector4(Time.frameCount, historyValid ? 1f : 0f, 0f, 0f);
            return cb;
        }

        static void PushShaderVariables(CommandBuffer cmd, ComputeShader compute, in ScreenSpaceLightingSsrShaderVariables cb)
        {
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrScreenSize, cb._SsrScreenSize);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrTraceSize, cb._SsrTraceSize);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrRayMarchingParams, cb._SsrRayMarchingParams);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrRoughnessFadeParams, cb._SsrRoughnessFadeParams);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrPyramidParams, cb._SsrPyramidParams);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrColorPyramidScaleAndLimitPrev, cb._SsrColorPyramidScaleAndLimitPrev);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrCameraPositionWS, cb._SsrCameraPositionWS);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrPbrParams, cb._SsrPbrParams);
            cmd.SetComputeVectorParam(compute, ScreenSpaceLightingShaderIDs.SsrPbrFlags, cb._SsrPbrFlags);
        }

        // The trace uses the same BND sequence as HDRP, but with the 1SPP tiles because it takes one
        // sample per pixel. The Owen scrambled texture is shared with SSGI, which binds 8SPP tiles.
        void BindSsrBlueNoise(CommandBuffer cmd, ComputeShader compute, int kernel)
        {
            if (!m_Resources.hasSsrBlueNoise)
                return;

            cmd.SetComputeTextureParam(compute, kernel, ScreenSpaceLightingShaderIDs.SsgiOwenScrambledTexture,
                m_Resources.ssgiOwenScrambledTexture);
            cmd.SetComputeTextureParam(compute, kernel, ScreenSpaceLightingShaderIDs.SsgiScramblingTileXSPP,
                m_Resources.ssrScramblingTile);
            cmd.SetComputeTextureParam(compute, kernel, ScreenSpaceLightingShaderIDs.SsgiRankingTileXSPP,
                m_Resources.ssrRankingTile);
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