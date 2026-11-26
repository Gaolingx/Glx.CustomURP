using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // P3 (B2, D1-A): deferred indirect-specular composite. Runs after the opaque SSR pass and the
    // GBuffer pass (event 225, >220 and <230) and produces the indirect-specular term the deferred
    // GBuffer no longer writes. The deferred light kernel adds the result to the baked colour.
    //
    // The pass is inert unless ssrAfterGbuffer is on, so the rollback path never pays for it and the
    // P1 frame stays bit-identical.
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
            // Global consumed by the deferred light kernel (installed in B2d).
            public static readonly int SpecularGlobal = Shader.PropertyToID("_GlxSsrSpecular");
            public static readonly int SpecularEnabled = Shader.PropertyToID("_GlxSsrSpecularEnabled");
        }

        const string k_NormalsOctKeyword = "_GBUFFER_NORMALS_OCT";

        ScreenSpaceLightingHistory m_History;
        ScreenSpaceLightingResources m_Resources;
        ScreenSpaceLightingSettings m_Settings;
        ScreenSpaceLightingSsrPass m_SsrPass;
        ComputeShader m_Compute;
        int m_Kernel = -1;

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

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (m_Settings == null || !m_Settings.ssrAfterGbuffer)
                return;
            if (m_Compute == null || m_Kernel < 0 || m_History == null || m_History.ssrIndirectSpecular == null)
                return;
            if (m_SsrPass == null || !m_SsrPass.producedOutput || m_SsrPass.outputTexture == null)
                return;
            if (!(renderingData.cameraData.renderer is UniversalRenderer renderer))
                return;

            var deferred = renderer.deferredLights;
            if (deferred == null || deferred.GbufferAttachments == null || deferred.GbufferAttachments.Length < 3)
                return;

            ref var cameraData = ref renderingData.cameraData;
            var depth = ScreenSpaceRenderUtils.GetCameraDepthTexture(renderer);
            if (depth == null)
                return;

            int width = cameraData.cameraTargetDescriptor.width;
            int height = cameraData.cameraTargetDescriptor.height;
            if (width <= 0 || height <= 0)
                return;

            var stack = VolumeManager.instance != null ? VolumeManager.instance.stack : null;
            var ssrSettings = stack != null ? stack.GetComponent<ScreenSpaceReflection>() : null;
            float intensity = ssrSettings != null ? ssrSettings.intensity.value : 1f;

            bool octNormals = ScreenSpaceLightingSsgiPass.UseOctEncodedNormals(renderer);
            SetKeyword(m_Compute, k_NormalsOctKeyword, octNormals);

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.GBuffer0, deferred.GbufferAttachments[0]);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.GBuffer1, deferred.GbufferAttachments[1]);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.GBuffer2, deferred.GbufferAttachments[2]);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.CameraDepthTexture, depth);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.SsrTexture, m_SsrPass.outputTexture);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.OutputRW, m_History.ssrIndirectSpecular);
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.ScreenSize,
                    new Vector4(width, height, 1f / width, 1f / height));
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.CameraPos, cameraData.camera.transform.position);
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.Params, new Vector4(intensity, 0f, 0f, 0f));
                cmd.DispatchCompute(m_Compute, m_Kernel, (width + 15) / 16, (height + 15) / 16, 1);
            }

            // Publish for the deferred light kernel (B2d). The plugin owns this global; the feature
            // installs a persistent default so the kernel never declares an unbound texture.
            cmd.SetGlobalTexture(ShaderIDs.SpecularGlobal, m_History.ssrIndirectSpecular);
            cmd.SetGlobalFloat(ShaderIDs.SpecularEnabled, 1f);
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
