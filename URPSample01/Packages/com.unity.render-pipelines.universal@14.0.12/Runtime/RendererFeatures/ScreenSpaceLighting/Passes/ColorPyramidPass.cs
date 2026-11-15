using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    // Builds this frame's pre-transparent color pyramid. Refraction samples it at render time, so it
    // is regenerated every frame and is not part of the temporal history.
    internal sealed class ScreenSpaceLightingCurrentColorPyramidPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = RenderPassEvent.AfterRenderingSkybox;
        internal const string k_ProfilingSamplerName = "SSL Current Color Pyramid";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

        ScreenSpaceLightingHistory m_History;
        ScreenSpaceLightingResources m_Resources;
        readonly MaterialPropertyBlock m_PropertyBlock = new MaterialPropertyBlock();

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
#endif

        internal ScreenSpaceLightingCurrentColorPyramidPass()
        {
            renderPassEvent = k_RenderPassEvent;
            // Forces an intermediate color target so the camera color can be sampled as a texture.
            ConfigureInput(ScriptableRenderPassInput.Color);
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
            if (m_History == null || m_Resources == null)
                return;

            EnsureHistoryResources(ref renderingData.cameraData);

            var material = m_Resources.colorPyramidMaterial;
            var destination = m_History.currentColorPyramid;
            var source = renderingData.cameraData.renderer.cameraColorTargetHandle;
            if (material == null || source == null || destination == null)
                return;

            var cmd = renderingData.commandBuffer;
            // The Gaussian chain is a screen-space pass: foveated shading would write the private
            // history at a lower rate, so it is disabled for this draw only.
            bool foveationDisabled = ScreenSpaceLightingXr.DisableFoveatedRendering(cmd, renderingData.cameraData);
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                ScreenSpaceLightingRenderUtils.RenderColorGaussianPyramid(
                    cmd,
                    material,
                    m_PropertyBlock,
                    source,
                    destination,
                    m_History.tempDownsample,
                    m_History.tempBlur,
                    m_History.width,
                    m_History.height,
                    m_History.colorPyramidMipCount);
            }

            ScreenSpaceLightingXr.RestoreFoveatedRendering(cmd, foveationDisabled);
        }

        void EnsureHistoryResources(ref CameraData cameraData)
        {
            var descriptor = cameraData.cameraTargetDescriptor;
            m_History.EnsureResources(
                descriptor.width,
                descriptor.height,
                ScreenSpaceLightingXr.GetViewCount(cameraData),
                ScreenSpaceLightingXr.GetResourceDimension(cameraData),
                ScreenSpaceLightingRenderUtils.k_ColorFormat);
        }
    }

    // Captures the post-transparent scene color and depth into the next history slot. The slot that
    // this frame's tracing reads is never written, so the previous history cannot be overwritten
    // while it is still in use.
    internal sealed class ScreenSpaceLightingColorHistoryCapturePass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingTransparents + 1);
        internal const string k_ProfilingSamplerName = "SSL Color History Capture";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

        ScreenSpaceLightingHistory m_History;
        ScreenSpaceLightingResources m_Resources;
        readonly MaterialPropertyBlock m_PropertyBlock = new MaterialPropertyBlock();
        readonly int[] m_SrcOffsetAndLimit = new int[4];
        readonly int[] m_DstOffset = new int[4];

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
#endif

        internal ScreenSpaceLightingColorHistoryCapturePass()
        {
            renderPassEvent = k_RenderPassEvent;
            ConfigureInput(ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Depth);
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
            if (m_History == null || m_Resources == null)
                return;

            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            m_History.EnsureResources(
                descriptor.width,
                descriptor.height,
                ScreenSpaceLightingXr.GetViewCount(renderingData.cameraData),
                ScreenSpaceLightingXr.GetResourceDimension(renderingData.cameraData),
                ScreenSpaceLightingRenderUtils.k_ColorFormat);

            var material = m_Resources.colorPyramidMaterial;
            var destination = m_History.GetHistoryColorPyramid(m_History.writeSlot);
            var source = renderingData.cameraData.renderer.cameraColorTargetHandle;
            if (material == null || source == null || destination == null)
                return;

            var cmd = renderingData.commandBuffer;
            bool foveationDisabled = ScreenSpaceLightingXr.DisableFoveatedRendering(cmd, renderingData.cameraData);
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                ScreenSpaceLightingRenderUtils.RenderColorGaussianPyramid(
                    cmd,
                    material,
                    m_PropertyBlock,
                    source,
                    destination,
                    m_History.tempDownsample,
                    m_History.tempBlur,
                    m_History.width,
                    m_History.height,
                    m_History.colorPyramidMipCount);

                var previousDepth = m_History.GetPreviousDepth(m_History.writeSlot);
                var depthTexture = ScreenSpaceRenderUtils.GetCameraDepthTexture(renderingData.cameraData.renderer);
                if (previousDepth != null && depthTexture != null)
                {
                    ScreenSpaceLightingRenderUtils.CopyDepthToPrevious(
                        cmd,
                        m_Resources.depthPyramidCompute,
                        depthTexture,
                        previousDepth,
                        m_History.width,
                        m_History.height,
                        m_History.slices,
                        m_History.dimension == TextureDimension.Tex2DArray,
                        m_SrcOffsetAndLimit,
                        m_DstOffset);
                }

                // Previous normals feed the temporal filter's normal consistency rejection.
                var previousNormals = m_History.GetPreviousNormal(m_History.writeSlot);
                var normalTexture = ScreenSpaceLightingRenderUtils.GetNormalTexture(renderingData.cameraData.renderer);
                if (previousNormals != null && normalTexture != null)
                {
                    ScreenSpaceLightingRenderUtils.CopyNormalsToPrevious(
                        cmd,
                        m_Resources.ssgiTemporalFilterCompute,
                        normalTexture,
                        previousNormals,
                        m_History.width,
                        m_History.height,
                        m_History.slices,
                        m_History.dimension == TextureDimension.Tex2DArray);
                }

                m_History.SwapBuffers();
            }

            ScreenSpaceLightingXr.RestoreFoveatedRendering(cmd, foveationDisabled);
        }
    }
}