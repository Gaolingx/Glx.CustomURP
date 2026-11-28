using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Opaque Screen Space Reflections: Approximation and PBR Accumulation, plus a ray traced path.
    //
    // With ray tracing enabled (and the device capable), every trace pixel traces
    // settings.rayTracingSamples rays against the scene each frame and the hits are shaded
    // immediately; the reflection denoiser then temporally accumulates the result. The volume
    // algorithm only selects the fallback used when the device cannot trace. This mirrors HDRP's
    // RayTracing mode: no screen space march and no per-pixel mixing of the two sources.
    //
    // SsrTracingMode.ScreenSpaceFirst opts into HDRP's Mixed tracing instead: the screen space march
    // runs first and the raygen resolves its hits from the previous color pyramid, tracing hardware
    // rays only for the pixels the march missed. RayTracedOnly stays the default.
    //
    // Screen-space Approximation: Trace writes the mirror hit, Reproject samples the previous color
    // pyramid and writes radiance + weight. At full resolution that result is the material-facing
    // texture; at half resolution it is bilaterally upsampled into the full resolution output.
    //
    // Screen-space PBR Accumulation: Trace samples the GGX visible normal distribution, Reproject
    // gathers the reflected surface and writes the un-accumulated signal, then the reflection
    // denoiser temporally accumulates it against the history and runs a separable bilateral filter.
    // The material-facing contract is un-premultiplied radiance with the blend weight in alpha, so a
    // miss keeps the reflection probe.
    //
    // Runs at 221, after the Decal GBuffer pass (220), and the material binding moves to 222, so the
    // deferred receiver takes the 225 composite instead of the material blend while Forward opaque
    // geometry (250) still consumes the material binding.
    internal sealed class ScreenSpaceLightingSsrPass : ScriptableRenderPass
    {
        // Opaque SSR runs after the GBuffer pass so screen-space hits can read the current frame's
        // GBuffer. This sits at +1 because the Decal GBuffer pass also injects at
        // AfterRenderingGbuffer (220): a distinct event makes "SSR after decals" deterministic
        // instead of depending on the renderer-feature injection order.
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingGbuffer + 1);
        internal const string k_ProfilingSamplerName = "Screen Space Reflections";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

        const int k_TileSize = 8;

        ScreenSpaceLightingHistory m_History;
        ScreenSpaceLightingResources m_Resources;
        ScreenSpaceLightingSettings m_Settings;
        RayTracingSceneResources m_Scene;
        RayTracingAccelerationStructure m_Snapshot;
        static readonly int s_CdCameraPositionWS = Shader.PropertyToID("_CdCameraPositionWS");
        static readonly int s_Scene = Shader.PropertyToID("_GlxScene");
        static readonly int s_SsrInvViewProj = Shader.PropertyToID("_SsrInvViewProj");
        static readonly int s_GlxProbeIrrSnapshot = Shader.PropertyToID("_GlxProbeIrrSnapshot");
        static readonly int s_GlxProbeDistSnapshot = Shader.PropertyToID("_GlxProbeDistSnapshot");
        static readonly int s_GlxProbeStateSnapshot = Shader.PropertyToID("_GlxProbeStateSnapshot");
        static readonly int s_GlxProbePublishedSnapshot = Shader.PropertyToID("_GlxProbePublishedSnapshot");
        static readonly ProfilingSampler s_RayTrace = new ProfilingSampler("SSR Ray Traced");
        int m_AdditionalLightCount;
        Vector3 m_CameraPositionWS;
        // P1 calibration knob for the omnidirectional ambient stand-in (Player harness
        // -p3-hit-ambient). 1 = the physical SH average; 0 reproduces the pre-ambient frame.
        internal static float s_HitAmbientScale = 1f;
        static readonly int s_HitAmbientIrradiance = Shader.PropertyToID("_GlxHitAmbientIrradiance");
        static readonly Vector3[] s_AmbientDirections =
        {
            Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back
        };
        static readonly Color[] s_AmbientColors = new Color[6];
        // The screen-space march used to run inside the pass sampler with no marker of its own, so
        // GPU captures attributed its time to nothing.
        static readonly ProfilingSampler s_Trace = new ProfilingSampler("SSR Trace");
        // I2 timing hook: reproject/upsample/composite after the hardware trace, plus the pass
        // sampler that contains both trace and reconstruction.
        static readonly ProfilingSampler s_Reconstruction = new ProfilingSampler("SSR Reconstruction");
        // Per-dispatch markers for the reconstruction chain: without them the Frame Debugger falls
        // back to the compute asset name ("Compute ReflectionDenoiser") and the four denoiser kernels
        // collapse into anonymous, indistinguishable rows.
        static readonly ProfilingSampler s_Reproject = new ProfilingSampler("SSR Reproject");
        static readonly ProfilingSampler s_DebugParams = new ProfilingSampler("SSR Debug Params");
        static readonly ProfilingSampler s_DenoiseTemporal = new ProfilingSampler("SSR Denoise Temporal");
        static readonly ProfilingSampler s_DenoiseCopyHistory = new ProfilingSampler("SSR Denoise Copy History");
        static readonly ProfilingSampler s_DenoiseBilateralH = new ProfilingSampler("SSR Denoise Bilateral H");
        static readonly ProfilingSampler s_DenoiseBilateralV = new ProfilingSampler("SSR Denoise Bilateral V");
        static readonly ProfilingSampler s_Upsample = new ProfilingSampler("SSR Upsample");
        internal static ProfilingSampler passSampler => s_ProfilingSampler;
        internal static ProfilingSampler reconstructionSampler => s_Reconstruction;

        internal void SetRayTracing(RayTracingSceneResources scene, RayTracingAccelerationStructure snapshot)
        {
            m_Scene = scene;
            m_Snapshot = snapshot;
        }

        bool RayTracingActive =>
            m_Scene != null && m_Snapshot != null && m_Resources != null &&
            m_Resources.rayTracingSsr != null && m_Settings != null && m_Settings.rayTracingSamples > 0;

        // The hit shading reads one irradiance value, so publish the SH average as irradiance
        // (SH * PI) once per camera. Only the ray traced path consumes it.
        void PushHitAmbient(CommandBuffer cmd)
        {
            if (!RayTracingActive)
                return;
            if (s_HitAmbientScale <= 0f)
            {
                cmd.SetGlobalVector(s_HitAmbientIrradiance, Vector4.zero);
                return;
            }
            RenderSettings.ambientProbe.Evaluate(s_AmbientDirections, s_AmbientColors);
            var sum = Vector3.zero;
            for (var i = 0; i < s_AmbientColors.Length; i++)
                sum += new Vector3(s_AmbientColors[i].r, s_AmbientColors[i].g, s_AmbientColors[i].b);
            var average = sum / s_AmbientColors.Length;
            cmd.SetGlobalVector(s_HitAmbientIrradiance,
                new Vector4(average.x, average.y, average.z, 1f) * (Mathf.PI * s_HitAmbientScale));
        }
        RTHandle m_OutputTexture;
        bool m_ProducedOutput;
        bool m_LoggedMissingResources;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // ScreenSpaceFirst silently degrades to ray traced only on a frame without the previous
        // color pyramid, so publish the resolved mode once per change instead of leaving a switch
        // that appears to do nothing.
        int m_LoggedTracingMode = -1;
#endif
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
            public int hwMergeDispatches;
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
                hwMergeDispatches = 0;
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
            m_AdditionalLightCount = renderingData.lightData.additionalLightsCount;
            m_CameraPositionWS = renderingData.cameraData.camera.transform.position;

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

            ref var cameraData = ref renderingData.cameraData;
            bool rayTraced = RayTracingActive;
            bool cold = m_History.readColorPyramid == null;
            // The ray traced path does not sample the previous color pyramid, so it can render the
            // first frame; the screen space paths need it.
            if (cold && !rayTraced) return;

            var descriptor = cameraData.cameraTargetDescriptor;
            int viewCount = ScreenSpaceLightingXr.GetViewCount(cameraData);
            TextureDimension dimension = ScreenSpaceLightingXr.GetResourceDimension(cameraData);

            var depthTexture = ScreenSpaceRenderUtils.GetCameraDepthTexture(cameraData.renderer);
            var normalTexture = ScreenSpaceLightingRenderUtils.GetNormalTexture(cameraData.renderer);
            var motionTexture = ScreenSpaceLightingRenderUtils.GetMotionTexture(cameraData.renderer);
            cold |= motionTexture == null;
            if (depthTexture == null || normalTexture == null || (cold && !rayTraced))
                return;

            int screenWidth = Mathf.Max(1, descriptor.width);
            int screenHeight = Mathf.Max(1, descriptor.height);
            // The ray traced path reuses the PBR accumulation resources: both produce an
            // un-premultiplied radiance + weight signal and share the reflection denoiser. The volume
            // algorithm only selects the screen space fallback when the device cannot trace.
            bool pbr = settings.usedAlgorithm.value == ScreenSpaceReflectionAlgorithm.PBRAccumulation || rayTraced;
            m_History.SyncSsrPbrAlgorithm(pbr ? ScreenSpaceReflectionAlgorithm.PBRAccumulation : ScreenSpaceReflectionAlgorithm.Approximation);

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
            // From the GBuffer pass onward the deferred normal source is GBuffer2, whose alpha holds
            // the raw material smoothness instead of the depth-normals prepass 0.5 + 0.5 encoding.
            bool gbufferNormals = ScreenSpaceLightingSsgiPass.UsesGbufferNormals(cameraData.renderer);
            bool debugParams = pbr && m_Settings != null && m_Settings.debugEnabled &&
                               m_Settings.debugMode == ScreenSpaceLightingDebugMode.SsrPbrParams;
            if (debugParams)
                m_History.EnsureSsrDebugResources(screenWidth, screenHeight, viewCount, dimension);
            if (cameraData.xr.enabled)
                ScreenSpaceLightingXr.FillViewConstants(cameraData, viewCount, m_XrViews, m_History);

            // Without a motion texture the denoiser cannot reproject, so this frame writes no
            // history and the accumulation restarts next frame.
            bool denoiseFrame = pbr && settings.denoise.value && motionTexture != null;

            var cmd = renderingData.commandBuffer;
            // P1 ambient: GlxHitLowFrequencyIrradiance reads _GlxHitAmbientIrradiance, which nothing
            // published, so reflection hits were shaded without the ambient the raster path has.
            // SampleSH returns the clamped-cosine convolution already divided by PI; the hit kernel
            // divides by PI again, so feed it the irradiance (SH * PI). One omnidirectional value is
            // the P1 low-frequency stand-in; P2 replaces it with the probe/sky hierarchy.
            PushHitAmbient(cmd);
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                if (rayTraced)
                {
                    bool screenSpaceFirst = ResolveScreenSpaceFirst(motionTexture);
                    if (screenSpaceFirst)
                        DispatchScreenSpaceFirstTrace(cmd, ref cameraData, settings, depthTexture,
                            normalTexture, screenWidth, screenHeight, traceWidth, traceHeight, viewCount,
                            useTextureArray, octNormals, gbufferNormals, halfResolution);
                    DispatchRayTraced(cmd, ref cameraData, settings, depthTexture, normalTexture,
                        screenWidth, screenHeight, traceWidth, traceHeight, octNormals, gbufferNormals,
                        screenSpaceFirst, m_History.readColorPyramid, motionTexture);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    LogTracingMode(screenSpaceFirst);
#endif

                    if (denoiseFrame)
                    {
                        var denoiser = m_Resources.ssrReflectionDenoiserCompute;
                        // The raygen selects its octahedron branch with a reflected integer flag, but
                        // the denoiser is a compute kernel and still uses the keyword, like the screen
                        // space Dispatch path.
                        SetKeyword(denoiser, ScreenSpaceLightingShaderIDs.SsgiNormalsOctKeyword, octNormals);
                        cmd.SetComputeIntParam(denoiser, ScreenSpaceLightingShaderIDs.SsrGbufferSmoothnessFlag,
                            gbufferNormals ? 1 : 0);
                        bool pbrHistoryValid = m_History.ssrPbrHasValidHistory;
                        PushDenoiserShaderVariables(cmd, ref cameraData, denoiser, screenWidth, screenHeight,
                            traceWidth, traceHeight, pbrHistoryValid);
                        DispatchReflectionDenoise(cmd, denoiser, settings, depthTexture, normalTexture,
                            motionTexture, ScreenSpaceLightingRenderUtils.DivideRoundUp(traceWidth, k_TileSize),
                            ScreenSpaceLightingRenderUtils.DivideRoundUp(traceHeight, k_TileSize), viewCount,
                            pbrHistoryValid, halfResolution);
                    }

                    if (halfResolution)
                    {
                        DispatchUpsample(cmd, depthTexture, m_History.ssrPbrOriginal, m_History.ssrOutput,
                            screenWidth, screenHeight, traceWidth, traceHeight,
                            ScreenSpaceLightingRenderUtils.DivideRoundUp(screenWidth, k_TileSize),
                            ScreenSpaceLightingRenderUtils.DivideRoundUp(screenHeight, k_TileSize),
                            viewCount, useTextureArray);
                    }
                }
                else
                {
                    Dispatch(cmd, ref cameraData, settings, depthTexture, normalTexture, motionTexture,
                        screenWidth, screenHeight, traceWidth, traceHeight, viewCount, useTextureArray,
                        octNormals, gbufferNormals, pbr, halfResolution, debugParams);
                }
            }

            // Skipping the denoiser means nothing writes the PBR history this frame, so a later
            // re-enable must not sample the stale accumulation: drop it once, on the transition.
            if (denoiseFrame)
                m_History.SwapSsrPbrBuffers();
            else if (pbr && m_History.ssrPbrHasValidHistory)
                m_History.ResetSsrPbrHistory();

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
            bool gbufferNormals,
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
            cmd.SetComputeIntParam(compute, ScreenSpaceLightingShaderIDs.SsrGbufferSmoothnessFlag, gbufferNormals ? 1 : 0);
            cmd.SetComputeIntParam(denoiser, ScreenSpaceLightingShaderIDs.SsrGbufferSmoothnessFlag, gbufferNormals ? 1 : 0);

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
            PushSsrRayLength(cmd, compute, ref cameraData, settings);

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
            using (new ProfilingScope(cmd, s_Trace))
                cmd.DispatchCompute(compute, traceKernel, traceGroupsX, traceGroupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_TraceDispatchCount++;
            s_LastDispatchStats.traceDispatches++;
#endif
#if UNITY_EDITOR
            s_EditorTraceDispatchCount++;
#endif

            using (new ProfilingScope(cmd, s_Reconstruction))
            {
            var reprojectKernel = compute.FindKernel("ScreenSpaceReflectionsReprojection");
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrHitPointTextureRW,
                pbr ? m_History.ssrPbrHitPoint : m_History.ssrHitPoint);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrMotionTexture, motionTexture);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrColorPyramid, m_History.readColorPyramid);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrDepthTexture, depthTexture);
            if (pbr)
                cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrReprojectTextureRW,
                    m_History.ssrPbrOriginal);
            else
                cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrOutputTextureRW,
                    halfResolution && m_History.ssrApproxOutput != null ? m_History.ssrApproxOutput : m_History.ssrOutput);
            using (new ProfilingScope(cmd, s_Reproject))
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
                using (new ProfilingScope(cmd, s_DebugParams))
                    cmd.DispatchCompute(compute, debugKernel, screenGroupsX, screenGroupsY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_LastDispatchStats.debugDispatches++;
#endif
            }

            if (pbr)
            {
                // Without the denoiser, ssrPbrOriginal keeps the raw reproject/merge signal and the
                // upsample below reads it unchanged.
                if (settings.denoise.value)
                {
                    PushDenoiserShaderVariables(cmd, ref cameraData, denoiser, screenWidth, screenHeight, traceWidth, traceHeight, pbrHistoryValid);
                    DispatchReflectionDenoise(cmd, denoiser, settings, depthTexture, normalTexture, motionTexture,
                        traceGroupsX, traceGroupsY, viewCount, pbrHistoryValid, halfResolution);
                }

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
        }

        // ScreenSpaceFirst mirrors HDRP's Mixed tracing: the screen space march runs first and the
        // raygen resolves its hit pixels from the previous color pyramid, tracing hardware rays only
        // where the march missed. It consumes the march's hit texture and reprojects with motion
        // vectors, so it is only active when both plus the shared depth pyramid exist; a cold start
        // or a camera without motion vectors keeps the ray traced only behavior.
        bool ResolveScreenSpaceFirst(RTHandle motionTexture)
        {
            return m_Settings.ssrTracingMode == SsrTracingMode.ScreenSpaceFirst &&
                m_Resources.ssrCompute != null &&
                m_History.readColorPyramid != null && motionTexture != null &&
                m_History.depthPyramid != null && m_History.depthPyramidOffsets != null &&
                m_History.ssrPbrHitPoint != null;
        }

        // The hybrid march reuses the screen space fallback's trace kernel in PBR mode: every trace
        // pixel samples one GGX direction, marches the packed depth pyramid and writes either the hit
        // UV or the miss sentinel into the PBR hit point texture the raygen reads.
        void DispatchScreenSpaceFirstTrace(
            CommandBuffer cmd,
            ref CameraData cameraData,
            ScreenSpaceReflection settings,
            RTHandle depthTexture,
            RTHandle normalTexture,
            int screenWidth,
            int screenHeight,
            int traceWidth,
            int traceHeight,
            int viewCount,
            bool useTextureArray,
            bool octNormals,
            bool gbufferNormals,
            bool halfResolution)
        {
            var compute = m_Resources.ssrCompute;
            SetKeyword(compute, ScreenSpaceShaderIDs.XrArrayKeyword, useTextureArray);
            SetKeyword(compute, ScreenSpaceLightingShaderIDs.SsrNormalsOctKeyword, octNormals);
            cmd.SetComputeIntParam(compute, ScreenSpaceLightingShaderIDs.SsrGbufferSmoothnessFlag, gbufferNormals ? 1 : 0);
            // The raygen traces the GGX distribution, so the march must sample the same direction
            // instead of the Approximation mirror direction.
            SetKeyword(compute, ScreenSpaceLightingShaderIDs.SsrApproxKeyword, false);
            SetKeyword(compute, ScreenSpaceLightingShaderIDs.SsrHalfResKeyword, halfResolution);
            SetKeyword(compute, ScreenSpaceLightingShaderIDs.SsgiBlueNoiseKeyword, m_Resources.hasSsrBlueNoise);

            var cb = CreateShaderVariables(ref cameraData, settings, screenWidth, screenHeight,
                traceWidth, traceHeight, m_History.ssrPbrHasValidHistory);
            PushShaderVariables(cmd, compute, cb);
            PushSsrRayLength(cmd, compute, ref cameraData, settings);

            var traceKernel = compute.FindKernel("ScreenSpaceReflectionsTracing");
            BindSsrBlueNoise(cmd, compute, traceKernel);
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiDepthPyramid, m_History.depthPyramid);
            cmd.SetComputeBufferParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiDepthPyramidMipLevelOffsets, m_History.depthPyramidOffsets);
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrHitPointTextureRW, m_History.ssrPbrHitPoint);
            using (new ProfilingScope(cmd, s_Trace))
                cmd.DispatchCompute(compute, traceKernel,
                    ScreenSpaceLightingRenderUtils.DivideRoundUp(traceWidth, k_TileSize),
                    ScreenSpaceLightingRenderUtils.DivideRoundUp(traceHeight, k_TileSize), viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_TraceDispatchCount++;
#endif
#if UNITY_EDITOR
            s_EditorTraceDispatchCount++;
#endif
        }

        // Full-screen ray traced reflections: one thread per trace pixel traces
        // settings.rayTracingSamples rays, shades every hit with the shared single-point kernel and
        // writes un-premultiplied radiance plus the resolved share into ssrPbrOriginal. The reflection
        // denoiser then accumulates the samples across frames, exactly like the screen-space PBR
        // signal. Misses publish weight 0 so the material keeps its probe/sky term.
        //
        // With screenSpaceFirst the raygen first resolves the march's hit pixels from the previous
        // color pyramid and only traces rays for the rest, matching HDRP's Mixed tracing.
        void DispatchRayTraced(
            CommandBuffer cmd,
            ref CameraData cameraData,
            ScreenSpaceReflection settings,
            RTHandle depthTexture,
            RTHandle normalTexture,
            int screenWidth,
            int screenHeight,
            int traceWidth,
            int traceHeight,
            bool octNormals,
            bool gbufferNormals,
            bool screenSpaceFirst,
            RTHandle colorPyramid,
            RTHandle motionTexture)
        {
            var shader = m_Resources.rayTracingSsr;
            float near = Mathf.Max(1e-5f, cameraData.camera.nearClipPlane);
            float far = Mathf.Max(near + 1e-4f, cameraData.camera.farClipPlane);

            float roughnessFadeEnd = 1f - settings.minSmoothness.value;
            float roughnessFadeStart = 1f - settings.smoothnessFadeStart.value;
            float roughnessFadeLength = roughnessFadeEnd - roughnessFadeStart;
            float fadeRcpLength = roughnessFadeLength != 0f ? 1f / roughnessFadeLength : 0f;
            float fadeEndTimesRcpLength = roughnessFadeLength != 0f ? roughnessFadeEnd * fadeRcpLength : 1f;
            // The screen-space-first branch fades its reprojected pyramid sample at the screen edge,
            // so it needs the same edge fade slope the compute reprojection receives.
            float edgeFadeRcpLength = Mathf.Min(1f / Mathf.Max(1e-4f, settings.screenFadeDistance.value), float.MaxValue);
            int samples = Mathf.Clamp(m_Settings.rayTracingSamples, 1, 32);

            cmd.SetRayTracingShaderPass(shader, "GlxDXR");
            cmd.SetRayTracingAccelerationStructure(shader, s_Scene, m_Snapshot);
            m_Scene.BindLights(cmd, shader);

            cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsrDepthTexture, depthTexture);
            cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsrNormalTexture, normalTexture);
            cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsrRtRadianceRW, m_History.ssrPbrOriginal);

            // The raygen declares the screen-space-first inputs unconditionally and Unity drops a
            // dispatch at the first unbound declared texture, so bind the march output plus
            // placeholders for frames where the march did not run.
            cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsrSsHitPointTexture, m_History.ssrPbrHitPoint);
            if (screenSpaceFirst)
            {
                cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsrColorPyramid, colorPyramid);
                cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsrMotionTexture, motionTexture);
            }
            else
            {
                cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsrColorPyramid, Texture2D.blackTexture);
                cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsrMotionTexture, Texture2D.blackTexture);
            }
            cmd.SetRayTracingIntParam(shader, ScreenSpaceLightingShaderIDs.SsrScreenSpaceFirst, screenSpaceFirst ? 1 : 0);
            // The raygen samples the previous color pyramid with the roughness mip, so it needs the
            // same top mip and reprojection limits the compute reprojection uses.
            cmd.SetRayTracingVectorParam(shader, ScreenSpaceLightingShaderIDs.SsrPyramidParams,
                new Vector4(Mathf.Max(0, m_History.colorPyramidMipCount - 1), 0f, 0f, 0f));
            cmd.SetRayTracingVectorParam(shader, ScreenSpaceLightingShaderIDs.SsrColorPyramidScaleAndLimitPrev,
                new Vector4(1f, 1f, 1f, 1f));

            cmd.SetRayTracingVectorParam(shader, ScreenSpaceLightingShaderIDs.SsrScreenSize,
                new Vector4(screenWidth, screenHeight, 1f / screenWidth, 1f / screenHeight));
            cmd.SetRayTracingVectorParam(shader, ScreenSpaceLightingShaderIDs.SsrTraceSize,
                new Vector4(traceWidth, traceHeight, 1f / traceWidth, 1f / traceHeight));
            cmd.SetRayTracingVectorParam(shader, ScreenSpaceLightingShaderIDs.SsrRoughnessFadeParams,
                new Vector4(fadeRcpLength, fadeEndTimesRcpLength, roughnessFadeEnd, edgeFadeRcpLength));
            cmd.SetRayTracingVectorParam(shader, ScreenSpaceLightingShaderIDs.SsrRayTraceParams,
                new Vector4(Mathf.Min(settings.rayLength.value, far), Time.frameCount, samples, settings.biasFactor.value));
            cmd.SetRayTracingVectorParam(shader, ScreenSpaceLightingShaderIDs.SsrCameraPositionWS, m_CameraPositionWS);
            // Same value ScriptableRenderer publishes as unity_MatrixInvVP; ray tracing shaders do not
            // receive the built-in camera constants, so bind it explicitly.
            Matrix4x4 invViewProj = Matrix4x4.Inverse(cameraData.GetViewMatrix()) *
                Matrix4x4.Inverse(cameraData.GetGPUProjectionMatrix());
            cmd.SetRayTracingMatrixParam(shader, s_SsrInvViewProj, invViewProj);
            // ClusteredDeferred reads the camera from its own uniform. The raster pass only sets it on
            // the ClusteredDeferred compute shader, so bind it here instead of relying on the global.
            cmd.SetRayTracingVectorParam(shader, s_CdCameraPositionWS, m_CameraPositionWS);
            cmd.SetRayTracingIntParam(shader, ScreenSpaceLightingShaderIDs.SsrNormalsOctFlag, octNormals ? 1 : 0);
            cmd.SetRayTracingIntParam(shader, ScreenSpaceLightingShaderIDs.SsrGbufferSmoothnessFlag,
                gbufferNormals ? 1 : 0);
            cmd.SetRayTracingIntParam(shader, ScreenSpaceLightingShaderIDs.SsrHitLightCount, m_AdditionalLightCount);
            cmd.SetRayTracingIntParam(shader, ScreenSpaceLightingShaderIDs.SsrBlueNoiseValid,
                m_Resources.hasSsrBlueNoise ? 1 : 0);
            if (m_Resources.hasSsrBlueNoise)
            {
                cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsgiOwenScrambledTexture,
                    m_Resources.ssgiOwenScrambledTexture);
                cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsgiScramblingTileXSPP,
                    m_Resources.ssrScramblingTile);
                cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsgiRankingTileXSPP,
                    m_Resources.ssrRankingTile);
            }
            else
            {
                // The sequence falls back to IGN + R2, but a declared texture must still be bound or
                // Unity drops the dispatch.
                cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsgiOwenScrambledTexture,
                    Texture2D.whiteTexture);
                cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsgiScramblingTileXSPP,
                    Texture2D.whiteTexture);
                cmd.SetRayTracingTextureParam(shader, ScreenSpaceLightingShaderIDs.SsgiRankingTileXSPP,
                    Texture2D.whiteTexture);
            }

            // GlxProbeQuery declares the published-probe atlas unconditionally and Unity drops a
            // dispatch at the first unbound declared resource. Without the GI probe fallback nothing
            // publishes those globals, so bind placeholders that are never sampled
            // (_GlxHitLowFrequencySource is 0 and the ambient comes from _GlxHitAmbientIrradiance).
            if (m_Settings == null || !m_Settings.rayTracingProbeFallback)
            {
                cmd.SetRayTracingTextureParam(shader, s_GlxProbeIrrSnapshot, Texture2D.blackTexture);
                cmd.SetRayTracingTextureParam(shader, s_GlxProbeDistSnapshot, Texture2D.blackTexture);
                var placeholder = m_Scene.lightCounters;
                if (placeholder != null)
                {
                    cmd.SetRayTracingBufferParam(shader, s_GlxProbeStateSnapshot, placeholder);
                    cmd.SetRayTracingBufferParam(shader, s_GlxProbePublishedSnapshot, placeholder);
                }
            }

            using (new ProfilingScope(cmd, s_RayTrace))
                cmd.DispatchRays(shader, "TraceSsrFullScreen", (uint)traceWidth, (uint)traceHeight, 1, null);
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
            // The ray traced path can run on a frame that has no color history yet; the temporal
            // kernel never samples these when _HistoryValidity is zero, but they must be bound.
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.HistoryDepthTexture,
                m_History.readPreviousDepth != null ? m_History.readPreviousDepth : depthTexture);
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.HistoryNormalTexture,
                m_History.readPreviousNormals != null ? m_History.readPreviousNormals : normalTexture);
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.HistoryBuffer, m_History.GetSsrPbrHistory(readSlot));
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.HistorySampleCountTexture, m_History.GetSsrPbrSampleCount(readSlot));
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.DenoiseOutputTextureRW, m_History.ssrDenoiseIntermediate0);
            cmd.SetComputeTextureParam(denoiser, temporalKernel, ScreenSpaceLightingShaderIDs.DenoiseSampleCountRW, m_History.GetSsrPbrSampleCount(writeSlot));
            using (new ProfilingScope(cmd, s_DenoiseTemporal))
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
            using (new ProfilingScope(cmd, s_DenoiseCopyHistory))
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
            using (new ProfilingScope(cmd, s_DenoiseBilateralH))
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
            using (new ProfilingScope(cmd, s_DenoiseBilateralV))
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
            using (new ProfilingScope(cmd, s_Upsample))
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

        // Standalone uniform like _R1K/_R1Phase, not a constant buffer member: only R1 reads it and
        // the SSR layout is pinned to nine Vector4 slots. A ray longer than the far plane cannot hit
        // anything, so the volume value is clamped instead of silently tracing past the depth buffer.
        static void PushSsrRayLength(CommandBuffer cmd, ComputeShader compute, ref CameraData cameraData, ScreenSpaceReflection settings)
        {
            float near = Mathf.Max(1e-5f, cameraData.camera.nearClipPlane);
            float far = Mathf.Max(near + 1e-4f, cameraData.camera.farClipPlane);
            cmd.SetComputeFloatParam(compute, ScreenSpaceLightingShaderIDs.SsrRayLength,
                Mathf.Min(settings.rayLength.value, far));
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

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        void LogTracingMode(bool screenSpaceFirst)
        {
            int mode = screenSpaceFirst ? 1 : 0;
            if (m_LoggedTracingMode == mode)
                return;

            m_LoggedTracingMode = mode;
            Debug.Log(screenSpaceFirst
                ? "[SSL][SSR] tracing mode: screen-space first (the march resolves, hardware rays fill the misses)"
                : "[SSL][SSR] tracing mode: ray traced only (the march is disabled or unavailable this frame)");
        }
#endif

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