using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal.Internal;

namespace UnityEngine.Rendering.Universal
{
    // Deferred indirect-specular composite. Runs after the opaque SSR pass (221) and the GBuffer
    // pass, before ClusteredDeferred (230), and produces the indirect-specular term the deferred
    // GBuffer no longer writes. The deferred light kernel adds the result to the baked colour.
    //
    // The pass is inert when it has nothing to composite. Its availability is also published to the
    // GBuffer pass: the receiver only drops its indirect specular when this pass can actually
    // composite it (see CanExecute), so a missing kernel or target can never make reflections
    // vanish silently.
    internal sealed class ScreenSpaceLightingSsrIndirectSpecularPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingGbuffer + 5);
        internal const string k_ProfilingSamplerName = "SSR Indirect Specular";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

        static class ShaderIDs
        {
            public static readonly int GBuffer0 = Shader.PropertyToID("_GBuffer0");
            public static readonly int GBuffer1 = Shader.PropertyToID("_GBuffer1");
            public static readonly int GBuffer2 = Shader.PropertyToID("_GBuffer2");
            public static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
            public static readonly int SsrTexture = Shader.PropertyToID("_ScreenSpaceSsrTexture");
            public static readonly int OutputRW = Shader.PropertyToID("_GlxSsrSpecularRW");
            public static readonly int ScreenSize = Shader.PropertyToID("_SsrIndirectScreenSize");
            public static readonly int CameraPos = Shader.PropertyToID("_SsrIndirectCameraPos");
            public static readonly int Params = Shader.PropertyToID("_SsrIndirectParams");
        }

        const string k_NormalsOctKeyword = "_GBUFFER_NORMALS_OCT";

        ScreenSpaceLightingHistory m_History;
        ScreenSpaceLightingResources m_Resources;
        ScreenSpaceLightingSettings m_Settings;
        ScreenSpaceLightingSsrPass m_SsrPass;
        ComputeShader m_Compute;
        int m_Kernel = -1;
        // Published probe generation bound by the Setup pass (207) for this camera/frame.
        bool m_ProbeEnvBound;
        // The Setup pass's per-frame decision that the deferred GBuffer drops its indirect specular.
        // Execute follows the same decision so a mid-frame reallocation can never double-count.
        bool m_ReceiverRouted;

        internal ScreenSpaceLightingSsrIndirectSpecularPass()
        {
            renderPassEvent = k_RenderPassEvent;
            ConfigureInput(ScriptableRenderPassInput.None);
        }

        internal void Setup(ScreenSpaceLightingHistory history, ScreenSpaceLightingResources resources,
            ScreenSpaceLightingSettings settings, ScreenSpaceLightingSsrPass ssrPass)
        {
            m_History = history;
            m_Resources = resources;
            m_Settings = settings;
            m_SsrPass = ssrPass;
            m_Compute = resources != null ? resources.ssrIndirectSpecularCompute : null;
            m_Kernel = m_Compute != null && m_Compute.HasKernel("SsrIndirectSpecular")
                ? m_Compute.FindKernel("SsrIndirectSpecular")
                : -1;
        }

        // Called by the Setup pass after it binds the probe atlas, so the A2 variant is only selected
        // when the published generation really is bound.
        internal void SetProbeEnvBound(bool bound) => m_ProbeEnvBound = bound;

        // The Setup pass publishes its routing decision here; Execute must use the same value.
        internal void SetReceiverRouted(bool routed) => m_ReceiverRouted = routed;

        // Everything this pass needs to produce a complete receiver term. The Setup pass publishes
        // _GlxSsrAfterGbuffer from this result so the GBuffer only drops its specular when the
        // composite will run. The target is allocated by the SSR pass (221), so before its first
        // allocation (or after a resolution change) this returns false and the frame keeps the P1
        // GBuffer specular; the next frame routes normally. A camera whose SSR volume stays off never
        // allocates targets and therefore keeps the P1 environment term, which is the correct
        // (probe-aware) fallback rather than an empty composite.
        internal bool CanExecute(in RenderingData renderingData)
        {
            if (m_Settings == null)
                return false;
            if (m_Compute == null || m_Kernel < 0 || m_History == null || m_History.ssrIndirectSpecular == null)
                return false;
            if (!(renderingData.cameraData.renderer is UniversalRenderer renderer))
                return false;
            // A Deferred-configured renderer can fall back to Forward per camera, and the GBuffer it
            // would composite was never drawn this frame.
            if (renderer.renderingModeActual != RenderingMode.Deferred)
                return false;
            var deferred = renderer.deferredLights;
            if (deferred == null || deferred.GbufferAttachments == null || deferred.GbufferAttachments.Length < 3)
                return false;
            if (ScreenSpaceRenderUtils.GetCameraDepthTexture(renderer) == null)
                return false;
            // A stale target would composite the wrong size, so keep the GBuffer's own specular.
            int width = renderingData.cameraData.cameraTargetDescriptor.width;
            int height = renderingData.cameraData.cameraTargetDescriptor.height;
            return width > 0 && height > 0 && m_History.ssrWidth == width && m_History.ssrHeight == height;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // Runs whenever the deferred receiver no longer writes indirect specular, even if the SSR
            // volume is off or the SSR pass produced nothing this frame: the environment term (A1/A2)
            // must survive, and the SSR texture falls back to black (weight 0) below.
            if (!m_ReceiverRouted)
                return;

            var renderer = (UniversalRenderer)renderingData.cameraData.renderer;
            var deferred = renderer.deferredLights;
            ref var cameraData = ref renderingData.cameraData;
            var depth = ScreenSpaceRenderUtils.GetCameraDepthTexture(renderer);

            int width = cameraData.cameraTargetDescriptor.width;
            int height = cameraData.cameraTargetDescriptor.height;
            if (width <= 0 || height <= 0)
                return;

            var stack = VolumeManager.instance != null ? VolumeManager.instance.stack : null;
            var ssrSettings = stack != null ? stack.GetComponent<ScreenSpaceReflection>() : null;
            float intensity = ssrSettings != null ? ssrSettings.intensity.value : 1f;

            bool octNormals = ScreenSpaceLightingSsgiPass.UseOctEncodedNormals(renderer);
            SetKeyword(m_Compute, k_NormalsOctKeyword, octNormals);
            // A2 blends the published probe atlas over the cubemap. The kernel always declares the
            // query; the Setup pass keeps the binding valid (real or invalid dummy), so this only
            // selects the runtime source and its weight.
            bool probeEnv = m_Settings.ssrIndirectSource == SsrIndirectSource.A2 && m_ProbeEnvBound;
            float probeWeight = probeEnv ? Mathf.Clamp01(m_Settings.probeCoverage) : 0f;

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.GBuffer0, deferred.GbufferAttachments[0]);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.GBuffer1, deferred.GbufferAttachments[1]);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.GBuffer2, deferred.GbufferAttachments[2]);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.CameraDepthTexture, depth);
                if (m_SsrPass != null && m_SsrPass.producedOutput && m_SsrPass.outputTexture != null)
                    cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.SsrTexture, m_SsrPass.outputTexture);
                else
                    cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.SsrTexture, Texture2D.blackTexture);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.OutputRW, m_History.ssrIndirectSpecular);
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.ScreenSize,
                    new Vector4(width, height, 1f / width, 1f / height));
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.CameraPos, cameraData.camera.transform.position);
                // x intensity, y probe coverage, z SsrIndirectSource (A1/A2/A3).
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.Params,
                    new Vector4(intensity, probeWeight, (float)m_Settings.ssrIndirectSource, 0f));
                cmd.DispatchCompute(m_Compute, m_Kernel, (width + 15) / 16, (height + 15) / 16, 1);
            }

            // Hand the composite to the deferred kernel (230). It binds the texture per dispatch and
            // falls back to black when no receiver was routed, so the kernel needs no variant.
            ClusteredDeferredPass.PublishSpecularComposite(m_History.ssrIndirectSpecular);
        }

        static void SetKeyword(ComputeShader compute, string keyword, bool enabled)
        {
            if (enabled)
                compute.EnableKeyword(keyword);
            else
                compute.DisableKeyword(keyword);
        }
    }
}
