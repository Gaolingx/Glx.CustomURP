using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Transparent Screen Space Reflections.
    //
    // Trace rays start on the transparent surface written by the 447 prepass and march the opaque
    // Hi-Z, so a transparent object reflects the scene behind it instead of reusing the opaque
    // background pixel. Radiance comes from the previous frame color pyramid because the current
    // frame color pyramid is generated before transparents (400) and would not contain them.
    //
    // Transparent SSR always uses the Approximation algorithm, independent of the volume algorithm:
    // the reprojection has no temporal accumulation history of its own, and the PBR accumulation
    // belongs to the opaque trace. The result is bound with its own texture and keyword for the
    // translucent draw (450); opaque materials never see it.
    internal sealed class ScreenSpaceLightingTransparentSsrPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingTransparents - 1);
        internal const string k_ProfilingSamplerName = "Screen Space Reflections Transparent";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

        const int k_TileSize = 8;

        ScreenSpaceLightingHistory m_History;
        ScreenSpaceLightingResources m_Resources;
        ScreenSpaceLightingTransparentDepthNormalsPass m_Prepass;
        RTHandle m_OutputTexture;
        bool m_ProducedOutput;
        bool m_LoggedMissingResources;
        readonly ScreenSpaceLightingXrViewConstants[] m_XrViews = new ScreenSpaceLightingXrViewConstants[ScreenSpaceLightingXr.k_MaxViews];
        readonly Matrix4x4[] m_XrViewProjScratch = new Matrix4x4[ScreenSpaceLightingXr.k_MaxViews];
        readonly Matrix4x4[] m_XrInvViewProjScratch = new Matrix4x4[ScreenSpaceLightingXr.k_MaxViews];
        readonly Matrix4x4[] m_XrPrevViewProjScratch = new Matrix4x4[ScreenSpaceLightingXr.k_MaxViews];
        readonly Matrix4x4[] m_XrPrevInvViewProjScratch = new Matrix4x4[ScreenSpaceLightingXr.k_MaxViews];
        readonly Vector4[] m_XrCameraPositionScratch = new Vector4[ScreenSpaceLightingXr.k_MaxViews];

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
        internal static int s_EditorTraceDispatchCount;
        internal static int s_EditorReprojectDispatchCount;
        internal static int s_EditorAccumulationDispatchCount;

        // Verification hook: resets the dispatch evidence between configuration sweeps.
        internal static void ResetEditorEvidenceCounters()
        {
            s_EditorExecuteCount = 0;
            s_EditorTraceDispatchCount = 0;
            s_EditorReprojectDispatchCount = 0;
            s_EditorAccumulationDispatchCount = 0;
        }
#endif

        internal bool producedOutput => m_ProducedOutput;
        internal bool usingPbrAccumulation => false;
        internal RTHandle outputTexture => m_ProducedOutput ? m_OutputTexture : null;

        internal ScreenSpaceLightingTransparentSsrPass()
        {
            renderPassEvent = k_RenderPassEvent;
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Motion);
        }

        internal void Setup(
            ScreenSpaceLightingHistory history,
            ScreenSpaceLightingResources resources,
            ScreenSpaceLightingTransparentDepthNormalsPass prepass)
        {
            m_History = history;
            m_Resources = resources;
            m_Prepass = prepass;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            s_EditorExecuteCount++;
#endif
            m_ProducedOutput = false;
            m_OutputTexture = null;

            var cmd = renderingData.commandBuffer;
            var stack = VolumeManager.instance.stack;
            var settings = stack != null ? stack.GetComponent<ScreenSpaceReflection>() : null;
            bool active = settings != null && settings.enabled.value && settings.enabledTransparent.value;

            // The prepass serial guard rejects a misordered or missing prepass instead of tracing
            // from the previous frame's private depth and normals.
            if (!active || m_History == null || m_Resources == null || m_Prepass == null ||
                !m_Prepass.producedOutput || m_Prepass.producedFrameSerial != m_History.frameSerial ||
                !m_History.transparentResourcesAllocated)
            {
                DisableMaterialBinding(cmd);
                return;
            }

            if (!m_Resources.hasTransparentSsrResources)
            {
                LogMissingResourcesOnce();
                DisableMaterialBinding(cmd);
                return;
            }

            // Both trace and reproject sample the previous frame color pyramid. Without it the only
            // correct output is none, which keeps the reflection probe on the transparent material.
            if (m_History.readColorPyramid == null)
            {
                DisableMaterialBinding(cmd);
                return;
            }

            ref var cameraData = ref renderingData.cameraData;
            var descriptor = cameraData.cameraTargetDescriptor;
            int viewCount = ScreenSpaceLightingXr.GetViewCount(cameraData);
            bool useTextureArray = ScreenSpaceLightingXr.IsTextureArray(cameraData);
            if (cameraData.xr.enabled)
                ScreenSpaceLightingXr.FillViewConstants(cameraData, viewCount, m_XrViews, m_History);
            int screenWidth = Mathf.Max(1, descriptor.width);
            int screenHeight = Mathf.Max(1, descriptor.height);

            var motionTexture = ScreenSpaceLightingRenderUtils.GetMotionTexture(cameraData.renderer);
            if (motionTexture == null)
            {
                DisableMaterialBinding(cmd);
                return;
            }

            var compute = m_Resources.ssrCompute;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                SetKeyword(compute, ScreenSpaceLightingShaderIDs.XrArrayKeyword, useTextureArray);
                // The private normal target uses the forward DepthNormals encoding, never the
                // deferred octahedral GBuffer layout.
                SetKeyword(compute, ScreenSpaceLightingShaderIDs.SsrNormalsOctKeyword, false);
                SetKeyword(compute, ScreenSpaceLightingShaderIDs.SsrApproxKeyword, true);

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

                var cb = CreateShaderVariables(ref cameraData, settings, screenWidth, screenHeight);
                PushShaderVariables(cmd, compute, cb);

                var traceKernel = compute.FindKernel("ScreenSpaceReflectionsTracing");
                cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrDepthTexture, m_History.transparentDepth);
                cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrNormalTexture, m_History.transparentNormals);
                cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiDepthPyramid, m_History.depthPyramid);
                cmd.SetComputeBufferParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiDepthPyramidMipLevelOffsets, m_History.depthPyramidOffsets);
                cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrHitPointTextureRW, m_History.ssrTransparentHitPoint);
                cmd.DispatchCompute(compute, traceKernel, groupsX, groupsY, viewCount);
#if UNITY_EDITOR
                s_EditorTraceDispatchCount++;
#endif

                var reprojectKernel = compute.FindKernel("ScreenSpaceReflectionsReprojection");
                cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrHitPointTextureRW, m_History.ssrTransparentHitPoint);
                cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrNormalTexture, m_History.transparentNormals);
                cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrMotionTexture, motionTexture);
                cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrColorPyramid, m_History.readColorPyramid);
                cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrDepthTexture, m_History.transparentDepth);
                cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrOutputTextureRW, m_History.ssrTransparentOutput);
                cmd.DispatchCompute(compute, reprojectKernel, groupsX, groupsY, viewCount);
#if UNITY_EDITOR
                s_EditorReprojectDispatchCount++;
#endif

                cmd.SetGlobalTexture(ScreenSpaceLightingShaderIDs.ScreenSpaceTransparentReflectionTexture, m_History.ssrTransparentOutput);
                cmd.SetGlobalVector(
                    ScreenSpaceLightingShaderIDs.ScreenSpaceTransparentReflectionParams,
                    new Vector4(settings.intensity.value, 0f, 0f, 0f));
                cmd.EnableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceTransparentReflectionKeyword);
            }

            m_OutputTexture = m_History.ssrTransparentOutput;
            m_History.MarkSsrTransparentOutputProduced();
            m_ProducedOutput = true;
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            // The keyword is only valid while transparents are drawn. Never leave it enabled for the
            // next camera or for passes after transparents.
            DisableMaterialBinding(cmd);
        }

        static void DisableMaterialBinding(CommandBuffer cmd)
        {
            if (cmd == null)
                return;

            cmd.SetGlobalTexture(ScreenSpaceLightingShaderIDs.ScreenSpaceTransparentReflectionTexture, Texture2D.blackTexture);
            cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.ScreenSpaceTransparentReflectionParams, Vector4.zero);
            cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceTransparentReflectionKeyword);
        }

        ScreenSpaceLightingSsrShaderVariables CreateShaderVariables(
            ref CameraData cameraData,
            ScreenSpaceReflection settings,
            int screenWidth,
            int screenHeight)
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
            // HDRP disables sky reflection for transparents: a sky hit has no depth and would appear
            // heavily distorted. The sky still comes from the reflection probe on the material.
            cb._SsrRayMarchingParams = new Vector4(
                Mathf.Max(1, settings.rayMaxIterations.value),
                thicknessScale,
                thicknessBias,
                0f);
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
            cb._SsrColorPyramidScaleAndLimitPrev = new Vector4(1f, 1f, 1f, 1f);
            cb._SsrCameraPositionWS = cameraData.xr.enabled
                ? m_XrViews[0].cameraPositionWS
                : cameraData.camera.transform.position;
            cb._SsrPbrParams = Vector4.zero;
            cb._SsrPbrFlags = Vector4.zero;
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
                "Transparent Screen Space Reflections are enabled but the SSR compute shader or the depth copy shader is missing. " +
                "Assign the package resources in the renderer feature inspector.");
        }
    }
}