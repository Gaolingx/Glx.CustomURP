using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Bind the opaque SSR result for the material-side blend once it exists.
    //
    // The SSR pass runs at 221, after the GBuffer and the Decal GBuffer pass (220), so the deferred
    // receiver no longer reads the material blend (the 225 composite does) but forward opaque
    // receivers (250) still do. This pass rebinds the texture and keyword between the SSR pass (221)
    // and the forward opaques, leaving the Setup pass's SSGI/probe binding at 207 untouched.
    internal sealed class ScreenSpaceLightingSsrBindPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingGbuffer + 2);
        internal const string k_ProfilingSamplerName = "SSR Material Binding";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

        ScreenSpaceLightingSsrPass m_SsrPass;

        internal ScreenSpaceLightingSsrBindPass()
        {
            renderPassEvent = k_RenderPassEvent;
            ConfigureInput(ScriptableRenderPassInput.None);
        }

        internal void Setup(ScreenSpaceLightingSsrPass ssrPass)
        {
            m_SsrPass = ssrPass;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                var stack = VolumeManager.instance != null ? VolumeManager.instance.stack : null;
                var ssrSettings = stack != null ? stack.GetComponent<ScreenSpaceReflection>() : null;
                bool ssrActive = ssrSettings != null && ssrSettings.enabled.value;
                bool bindSsr = ssrActive &&
                               m_SsrPass != null &&
                               m_SsrPass.producedOutput &&
                               m_SsrPass.outputTexture != null;

                if (bindSsr)
                {
                    cmd.SetGlobalTexture(ScreenSpaceLightingShaderIDs.ScreenSpaceReflectionTexture, m_SsrPass.outputTexture);
                    cmd.SetGlobalVector(
                        ScreenSpaceLightingShaderIDs.ScreenSpaceReflectionParams,
                        new Vector4(ssrSettings.intensity.value, 0f, 0f, 0f));
                    cmd.EnableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceReflectionKeyword);
                }
                else
                {
                    cmd.SetGlobalTexture(ScreenSpaceLightingShaderIDs.ScreenSpaceReflectionTexture, Texture2D.blackTexture);
                    cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.ScreenSpaceReflectionParams, Vector4.zero);
                    cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceReflectionKeyword);
                }
            }
        }
    }
}
