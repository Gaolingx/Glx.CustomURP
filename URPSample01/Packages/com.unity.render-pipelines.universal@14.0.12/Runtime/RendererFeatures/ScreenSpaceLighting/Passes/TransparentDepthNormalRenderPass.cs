using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Draws the transparent ray origin for Transparent SSR.
    //
    // A private depth/normal pair is seeded with a copy of the opaque camera depth, then transparent
    // materials with the UniversalTransparentDepthNormals light mode render into it with the same
    // depth encoding as the opaque DepthNormals prepass (0.5 + 0.5 * smoothness, 0 = unsupported).
    // The main transparent pass is not touched: it keeps its own blend, depth test and depth write
    // state, and this pass never binds the camera depth target.
    //
    // Runs before the transparent SSR trace (449) and before URP draws transparents (450).
    internal sealed class ScreenSpaceLightingTransparentDepthNormalsPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingTransparents - 3);
        internal const string k_ProfilingSamplerName = "SSL Transparent Depth Normals";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);
        static readonly ShaderTagId k_LightModeTag = new ShaderTagId("UniversalTransparentDepthNormals");

        ScreenSpaceLightingHistory m_History;
        ScreenSpaceLightingResources m_Resources;
        bool m_ProducedOutput;
        int m_ProducedFrameSerial = -1;
        bool m_LoggedMissingResources;

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
#endif

        internal bool producedOutput => m_ProducedOutput;
        internal int producedFrameSerial => m_ProducedFrameSerial;

        internal ScreenSpaceLightingTransparentDepthNormalsPass()
        {
            renderPassEvent = k_RenderPassEvent;
            ConfigureInput(ScriptableRenderPassInput.Depth);
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
            m_ProducedFrameSerial = -1;

            if (m_History == null || m_Resources == null)
                return;

            var stack = VolumeManager.instance.stack;
            var settings = stack != null ? stack.GetComponent<ScreenSpaceReflection>() : null;
            if (settings == null || !settings.enabled.value || !settings.enabledTransparent.value)
            {
                m_LoggedMissingResources = false;
                return;
            }

            if (!m_Resources.hasTransparentSsrResources)
            {
                LogMissingResourcesOnce();
                return;
            }

            var material = m_Resources.transparentDepthCopyMaterial;
            if (material == null)
                return;

            ref var cameraData = ref renderingData.cameraData;
            var camera = cameraData.camera;
            var depthSource = ScreenSpaceRenderUtils.GetCameraDepthTexture(cameraData.renderer);
            if (depthSource == null)
                return;

            var descriptor = cameraData.cameraTargetDescriptor;
            int viewCount = ScreenSpaceLightingXr.GetViewCount(cameraData);
            TextureDimension dimension = ScreenSpaceLightingXr.GetResourceDimension(cameraData);
            int width = Mathf.Max(1, descriptor.width);
            int height = Mathf.Max(1, descriptor.height);

            m_History.EnsureTransparentSsrResources(width, height, viewCount, dimension);
            if (!m_History.transparentResourcesAllocated)
                return;

            var cmd = renderingData.commandBuffer;
            // The prepass feeds full-resolution screen-space tracing, so a reduced raster shading
            // rate would lower the normal quality for every reflected pixel.
            bool foveationDisabled = ScreenSpaceLightingXr.DisableFoveatedRendering(cmd, cameraData);
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                // 1) Seed the private depth with the opaque camera depth. The fullscreen CopyDepth
                // shader writes SV_Depth, so a depth-only target is initialized in one draw without
                // clearing the main depth attachment.
                CoreUtils.SetRenderTarget(cmd, m_History.transparentDepth, ClearFlag.None);
                cmd.SetViewport(new Rect(0f, 0f, width, height));
                cmd.SetGlobalTexture(ScreenSpaceLightingShaderIDs.CameraDepthAttachment, depthSource.nameID);
                cmd.DisableShaderKeyword("_DEPTH_MSAA_2");
                cmd.DisableShaderKeyword("_DEPTH_MSAA_4");
                cmd.DisableShaderKeyword("_DEPTH_MSAA_8");
                cmd.EnableShaderKeyword(ScreenSpaceLightingShaderIDs.OutputDepthKeyword);
                Blitter.BlitTexture(cmd, depthSource, new Vector4(1f, 1f, 0f, 0f), material, 0);
                cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.OutputDepthKeyword);

                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                // 2) Draw the opted-in transparent surfaces into the normal target and the seeded
                // depth. CommonTransparent order draws far to near; ZWrite On keeps the closest
                // transparent surface, matching HDRP's transparent depth prepass.
                CoreUtils.SetRenderTarget(cmd, m_History.transparentNormals, m_History.transparentDepth, ClearFlag.Color, Color.black);
                cmd.SetViewport(new Rect(0f, 0f, width, height));
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                var sortingSettings = new SortingSettings(camera) { criteria = SortingCriteria.CommonTransparent };
                var drawSettings = new DrawingSettings(k_LightModeTag, sortingSettings)
                {
                    perObjectData = PerObjectData.None,
                    enableDynamicBatching = renderingData.supportsDynamicBatching,
                    enableInstancing = camera.cameraType != CameraType.Preview,
                };
                var filteringSettings = new FilteringSettings(RenderQueueRange.transparent, ResolveLayerMask(cameraData.renderer, camera));
                context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filteringSettings);
            }

            ScreenSpaceLightingXr.RestoreFoveatedRendering(cmd, foveationDisabled);

            m_ProducedOutput = true;
            m_ProducedFrameSerial = m_History.frameSerial;
        }

        // The built-in transparent pass filters by the renderer's transparent layer mask, not the
        // camera culling mask; use the same value so the prepass and the visible transparents agree.
        static int ResolveLayerMask(ScriptableRenderer renderer, Camera camera)
        {
            if (renderer is UniversalRenderer universalRenderer)
                return universalRenderer.transparentLayerMask;

            return camera.cullingMask;
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