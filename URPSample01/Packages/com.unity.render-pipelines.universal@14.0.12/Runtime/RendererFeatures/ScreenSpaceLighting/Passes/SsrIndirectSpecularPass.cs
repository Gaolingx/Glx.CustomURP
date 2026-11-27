using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // P3 (B2, D1-A): deferred indirect-specular composite. Runs after the opaque SSR pass (221) and
    // the GBuffer pass, before ClusteredDeferred (230), and produces the indirect-specular term the
    // deferred GBuffer no longer writes. The deferred light kernel adds the result to the baked colour.
    //
    // The pass is inert unless ssrAfterGbuffer is on, so the rollback path never pays for it and the
    // P1 frame stays bit-identical. Its availability is also published to the GBuffer pass: the
    // receiver only drops its indirect specular when this pass can actually composite it (see
    // CanExecute), so a missing kernel or target can never make reflections vanish silently.
    internal sealed class ScreenSpaceLightingSsrIndirectSpecularPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingGbuffer + 5);
        internal const string k_ProfilingSamplerName = "SSR Indirect Specular";
        // A2 declaration gate: the probe atlas resources must not be declared unless the Setup pass
        // bound a published generation this frame (an unbound declared resource drops the dispatch).
        internal const string k_ProbeEnvKeyword = "_GLX_SSR_PROBE_ENV";

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
            // Global consumed by the deferred light kernel (installed in B2d).
            public static readonly int SpecularGlobal = Shader.PropertyToID("_GlxSsrSpecular");
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
            if (m_Settings == null || !m_Settings.ssrAfterGbuffer)
                return false;
            if (m_Compute == null || m_Kernel < 0 || m_History == null || m_History.ssrIndirectSpecular == null)
                return false;
            if (!(renderingData.cameraData.renderer is UniversalRenderer renderer))
                return false;
            var deferred = renderer.deferredLights;
            if (deferred == null || deferred.GbufferAttachments == null || deferred.GbufferAttachments.Length < 3)
                return false;
            if (ScreenSpaceRenderUtils.GetCameraDepthTexture(renderer) == null)
                return false;
            // A stale target would composite the wrong size, so fall back to the P1 path instead.
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
            // A2 only when the atlas is really bound; A1/A3 never declare the probe resources.
            bool probeEnv = m_Settings.ssrIndirectSource == SsrIndirectSource.A2 && m_ProbeEnvBound;
            SetKeyword(m_Compute, k_ProbeEnvKeyword, probeEnv);
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

            // Publish the composite as a global and switch the deferred kernel to the variant that
            // adds it. The keyword is enabled only here, after the texture is bound, so the rollback
            // path never declares an unbound texture; the cleanup pass disables it after opaques.
            cmd.SetGlobalTexture(ShaderIDs.SpecularGlobal, m_History.ssrIndirectSpecular);
            cmd.EnableShaderKeyword(ScreenSpaceLightingShaderIDs.GlxSsrSpecularKeyword);
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
