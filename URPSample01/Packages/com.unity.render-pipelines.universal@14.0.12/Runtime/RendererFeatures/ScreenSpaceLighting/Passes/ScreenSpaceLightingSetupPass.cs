using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    internal sealed class ScreenSpaceLightingSetupPass : ScriptableRenderPass
    {
        // Material binding/debug slot from the pass order table, after SSGI (205) and opaque SSR
        // (206) and before Deferred GBuffer (210) or Forward opaque geometry (250).
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 7);

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler("Screen Space Lighting Setup");

        ScreenSpaceLightingSsgiPass m_SsgiPass;
        ScreenSpaceLightingSsrPass m_SsrPass;
        ScreenSpaceLightingSsrIndirectSpecularPass m_SsrIndirectPass;
        RayTracingProbeFallback m_ProbeFallback;
        float m_ProbeCoverage;
        bool m_SsrAfterGbuffer;
        bool m_WarnedReceiverUnavailable;

#if UNITY_EDITOR
        // Editor-only evidence hook: verification code reads this counter to prove the pass ran and
        // that the material keyword was enabled with a valid SSGI texture.
        internal static int s_EditorExecuteCount;
        internal static int s_EditorSsgiBoundCount;
        internal static int s_EditorSsrBoundCount;
#endif

        internal ScreenSpaceLightingSetupPass()
        {
            renderPassEvent = k_RenderPassEvent;
            ConfigureInput(ScriptableRenderPassInput.None);
        }

        internal void Setup(ScreenSpaceLightingSsgiPass ssgiPass, ScreenSpaceLightingSsrPass ssrPass,
            ScreenSpaceLightingSsrIndirectSpecularPass ssrIndirectPass, bool ssrAfterGbuffer)
        {
            m_SsgiPass = ssgiPass;
            m_SsrPass = ssrPass;
            m_SsrIndirectPass = ssrIndirectPass;
            m_SsrAfterGbuffer = ssrAfterGbuffer;
        }

        internal void SetProbeFallback(RayTracingProbeFallback probe, float coverage)
        {
            m_ProbeFallback = probe;
            m_ProbeCoverage = Mathf.Clamp01(coverage);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            s_EditorExecuteCount++;
#endif
            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                // P3: publish the receiver switch before the GBuffer pass so the deferred Lit
                // fragment keeps only indirect diffuse. 0 on the rollback path keeps GBuffer3 as P1.
                // The switch is also 0 whenever the 225 composite cannot run, so a missing kernel or
                // target degrades to the P1 GBuffer path instead of dropping every reflection.
                bool routeReceiver = m_SsrAfterGbuffer &&
                                     m_SsrIndirectPass != null &&
                                     m_SsrIndirectPass.CanExecute(renderingData);
                // The composite follows the same decision, so a later reallocation cannot double-add.
                m_SsrIndirectPass?.SetReceiverRouted(routeReceiver);
                if (m_SsrAfterGbuffer && !routeReceiver && !m_WarnedReceiverUnavailable)
                {
                    m_WarnedReceiverUnavailable = true;
                    Debug.LogWarning("[ScreenSpaceLighting] ssrAfterGbuffer is on but the 225 indirect-specular " +
                                     "composite cannot run (missing compute/kernel, target or deferred GBuffer); " +
                                     "the deferred GBuffer keeps its indirect specular and the P3 receiver reroute " +
                                     "stays off until it can.");
                }
                cmd.SetGlobalFloat(ScreenSpaceLightingShaderIDs.GlxSsrAfterGbuffer, routeReceiver ? 1f : 0f);

                var stack = VolumeManager.instance.stack;
                var settings = stack != null ? stack.GetComponent<ScreenSpaceGlobalIllumination>() : null;
                bool ssgiActive = settings != null && settings.enabled.value;
                bool bindSsgi = ssgiActive &&
                                m_SsgiPass != null &&
                                m_SsgiPass.producedOutput &&
                                m_SsgiPass.finalTexture != null;

                if (bindSsgi)
                {
                    cmd.SetGlobalTexture(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationTexture, m_SsgiPass.finalTexture);
                    cmd.SetGlobalVector(
                        ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationParams,
                        new Vector4(settings.intensity.value, 0f, 0f, 0f));
                    cmd.EnableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationKeyword);
#if UNITY_EDITOR
                    s_EditorSsgiBoundCount++;
#endif
                }
                else
                {
                    cmd.SetGlobalTexture(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationTexture, Texture2D.blackTexture);
                    cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationParams, Vector4.zero);
                    cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationKeyword);
                }

                // DDGI ownership is exclusive. When this frame's SSGI dispatch bound the published
                // generation, the SSGI texture already carries the DDGI tier, so the material must
                // not blend it again (the URP equivalent of HDRP's "SSGI replaces bakeDiffuseLighting"
                // branch). The material-side pre-blend remains the owner for cameras without SSGI
                // output, or when SSGI intensity is zero and the shader returns the base unchanged.
                bool probeReady = m_ProbeFallback != null && m_ProbeFallback.BindPublishedGlobals(cmd);
                bool ssgiOwnsProbe = bindSsgi &&
                                     Mathf.Max(0f, settings.intensity.value) > 0f &&
                                     m_SsgiPass.ddgiBoundThisFrame;
                bool bindProbe = probeReady && !ssgiOwnsProbe;
                // A2 needs to know whether the published atlas is bound before it selects its variant.
                m_SsrIndirectPass?.SetProbeEnvBound(probeReady);
                if (bindProbe)
                {
                    cmd.SetGlobalVector(
                        ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationProbeParams,
                        new Vector4(m_ProbeCoverage, 0f, 0f, 0f));
                    cmd.EnableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationProbeKeyword);
                }
                else
                {
                    cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationProbeParams, Vector4.zero);
                    cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationProbeKeyword);
                }

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
#if UNITY_EDITOR
                    s_EditorSsrBoundCount++;
#endif
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

    // SSGI and opaque SSR only feed opaque materials. Transparent forward objects run in the same
    // material shader, so the keywords are disabled right after the opaques event to keep the baked
    // GI and reflection probes on transparents and to avoid leaking the textures into a later camera.
    internal sealed class ScreenSpaceLightingCleanupPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        internal const string k_ProfilingSamplerName = "SSL Cleanup";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
#endif

        internal ScreenSpaceLightingCleanupPass()
        {
            renderPassEvent = k_RenderPassEvent;
            ConfigureInput(ScriptableRenderPassInput.None);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            s_EditorExecuteCount++;
#endif
            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationKeyword);
                cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationParams, Vector4.zero);
                cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationProbeKeyword);
                cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationProbeParams, Vector4.zero);
                cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceReflectionKeyword);
                cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.ScreenSpaceReflectionParams, Vector4.zero);
                // P3: the deferred kernel's specular variant is per-frame; never let it leak past the
                // opaque phase (the composite texture is not refreshed for transparents).
                cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.GlxSsrSpecularKeyword);
            }
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            if (cmd == null)
                return;

            cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationKeyword);
            cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationParams, Vector4.zero);
            cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationProbeKeyword);
            cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.ScreenSpaceGlobalIlluminationProbeParams, Vector4.zero);
            cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceReflectionKeyword);
            cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.ScreenSpaceReflectionParams, Vector4.zero);
            cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.GlxSsrSpecularKeyword);
            // Refraction is enabled later (448) and consumed by transparents only; this is the safety
            // net for a camera that never reaches the transparent phase.
            cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceRefractionKeyword);
            cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.RefractionParams, Vector4.zero);
        }
    }
}