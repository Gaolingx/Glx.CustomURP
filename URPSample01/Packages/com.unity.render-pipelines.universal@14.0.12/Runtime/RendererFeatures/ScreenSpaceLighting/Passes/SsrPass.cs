using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Opaque Screen Space Reflections (Approximation). Trace writes the hit point, reproject samples
    // the previous frame color pyramid and writes radiance + weight. Runs after the SSGI dispatch
    // (205) and before the material binding pass (207), so Deferred GBuffer (210) and Forward opaque
    // geometry (250) consume the same texture.
    //
    // The result is never premultiplied by the weight. The Core material blend keeps the reflection
    // probe where weight is 0, which is what stops a miss from darkening the image.
    internal sealed class ScreenSpaceLightingSsrPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 6);
        internal const string k_ProfilingSamplerName = "Screen Space Reflections";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

        const int k_TileSize = 8;

        ScreenSpaceLightingHistory m_History;
        ScreenSpaceLightingResources m_Resources;
        RTHandle m_OutputTexture;
        bool m_ProducedOutput;
        bool m_LoggedMissingResources;

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
        internal static int s_EditorTraceDispatchCount;
        internal static int s_EditorReprojectDispatchCount;
#endif

        internal bool producedOutput => m_ProducedOutput;
        internal RTHandle outputTexture => m_ProducedOutput ? m_OutputTexture : null;

        internal ScreenSpaceLightingSsrPass()
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

            // The approximation samples the previous frame color pyramid. Without it (first frame,
            // camera cut, renderer creation) the only correct output is none, which keeps the
            // reflection probe / sky the material already computed.
            if (m_History.readColorPyramid == null)
                return;

            ref var cameraData = ref renderingData.cameraData;
            var descriptor = cameraData.cameraTargetDescriptor;
            int viewCount = cameraData.xr.enabled ? Mathf.Max(1, cameraData.xr.viewCount) : 1;
            TextureDimension dimension = viewCount > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;

            var depthTexture = ScreenSpaceLightingDepthPyramidPass.GetCameraDepthTexture(cameraData.renderer);
            var normalTexture = ScreenSpaceLightingRenderUtils.GetNormalTexture(cameraData.renderer);
            var motionTexture = ScreenSpaceLightingRenderUtils.GetMotionTexture(cameraData.renderer);
            if (depthTexture == null || normalTexture == null || motionTexture == null)
                return;

            int screenWidth = Mathf.Max(1, descriptor.width);
            int screenHeight = Mathf.Max(1, descriptor.height);

            m_History.EnsureSsrResources(screenWidth, screenHeight, viewCount, dimension);
            if (!m_History.ssrResourcesAllocated)
                return;

            bool useTextureArray = dimension == TextureDimension.Tex2DArray;
            bool octNormals = ScreenSpaceLightingSsgiPass.UseOctEncodedNormals(cameraData.renderer);

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                Dispatch(cmd, ref cameraData, settings, depthTexture, normalTexture, motionTexture,
                    screenWidth, screenHeight, viewCount, useTextureArray, octNormals);
            }

            m_History.MarkSsrOutputProduced();
            m_OutputTexture = m_History.ssrOutput;
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
            bool octNormals)
        {
            var compute = m_Resources.ssrCompute;
            SetKeyword(compute, ScreenSpaceLightingShaderIDs.XrArrayKeyword, useTextureArray);
            SetKeyword(compute, ScreenSpaceLightingShaderIDs.SsrNormalsOctKeyword, octNormals);

            int groupsX = ScreenSpaceLightingRenderUtils.DivideRoundUp(screenWidth, k_TileSize);
            int groupsY = ScreenSpaceLightingRenderUtils.DivideRoundUp(screenHeight, k_TileSize);

            var cb = CreateShaderVariables(ref cameraData, settings, screenWidth, screenHeight);
            PushShaderVariables(cmd, compute, cb);

            var traceKernel = compute.FindKernel("ScreenSpaceReflectionsTracing");
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiDepthPyramid, m_History.depthPyramid);
            cmd.SetComputeBufferParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsgiDepthPyramidMipLevelOffsets, m_History.depthPyramidOffsets);
            cmd.SetComputeTextureParam(compute, traceKernel, ScreenSpaceLightingShaderIDs.SsrHitPointTextureRW, m_History.ssrHitPoint);
            cmd.DispatchCompute(compute, traceKernel, groupsX, groupsY, viewCount);
#if UNITY_EDITOR
            s_EditorTraceDispatchCount++;
#endif

            var reprojectKernel = compute.FindKernel("ScreenSpaceReflectionsReprojection");
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrHitPointTextureRW, m_History.ssrHitPoint);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrMotionTexture, motionTexture);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrColorPyramid, m_History.readColorPyramid);
            cmd.SetComputeTextureParam(compute, reprojectKernel, ScreenSpaceLightingShaderIDs.SsrOutputTextureRW, m_History.ssrOutput);
            cmd.DispatchCompute(compute, reprojectKernel, groupsX, groupsY, viewCount);
#if UNITY_EDITOR
            s_EditorReprojectDispatchCount++;
#endif
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
            cb._SsrCameraPositionWS = cameraData.camera.transform.position;
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