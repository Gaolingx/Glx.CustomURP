using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Binds the material-facing inputs for screen-space refraction.
    //
    // The pass runs after the opaque phase and before transparents. The color pyramid was generated
    // at 400 (opaque + sky) so a refractive material never samples colors that the current frame has
    // not written yet; transparent objects are intentionally absent from it (HDRP's pre-refraction
    // transparent ordering is not migrated, see the support matrix).
    //
    // The opaque camera depth is bound as well: the material rejects the refracted offset when the
    // sampled pixel belongs to geometry in front of the refracting surface, which is HDRP's
    // `hitLinearDepth > posInput.linearDepth` rule.
    internal sealed class ScreenSpaceLightingRefractionPass : ScriptableRenderPass
    {
        // Must match the SSL_REFRACTION_PROXY_MODE_* defines in ScreenSpaceRefraction.hlsl.
        internal enum ProxyMode
        {
            None = 0,
            Box = 1,
            Infinite = 2,
        }

        // 448: after the transparent depth/normal prepass (447), before transparent SSR (449) and
        // the URP transparent draw (450).
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingTransparents - 2);
        internal const string k_ProfilingSamplerName = "SSL Refraction Bindings";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

        ScreenSpaceLightingHistory m_History;

        // Scene scans are expensive; a reflection probe rarely changes, so refresh at a low rate.
        const int k_SceneProbeRefreshFrames = 30;
        ReflectionProbe[] m_SceneProbes;
        int m_SceneProbeRefreshFrame = -9999;

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
        internal static int s_EditorBoundCount;
        internal static int s_EditorNoProxyCount;
        internal static int s_EditorVisibleProbeCount;
        internal static int s_EditorSceneProbeCount;
        internal static int s_EditorBoxProbeCount;

        internal static void ResetEditorEvidenceCounters()
        {
            s_EditorExecuteCount = 0;
            s_EditorBoundCount = 0;
            s_EditorNoProxyCount = 0;
            s_EditorVisibleProbeCount = 0;
            s_EditorSceneProbeCount = 0;
            s_EditorBoxProbeCount = 0;
        }
#endif

        internal ScreenSpaceLightingRefractionPass()
        {
            renderPassEvent = k_RenderPassEvent;
            // The color pyramid is produced earlier; this pass only needs the opaque depth.
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        internal void Setup(ScreenSpaceLightingHistory history)
        {
            m_History = history;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            s_EditorExecuteCount++;
#endif
            var cmd = renderingData.commandBuffer;
            var stack = VolumeManager.instance.stack;
            var settings = stack != null ? stack.GetComponent<ScreenSpaceRefraction>() : null;
            // Intensity zero must keep the legacy transparent output byte for byte, so it gates the
            // whole effect instead of only scaling the screen-space weight.
            bool active = settings != null && settings.enabled.value && settings.intensity.value > 0f;

            if (!active || m_History == null || !m_History.resourcesAllocated || m_History.currentColorPyramid == null)
            {
                DisableMaterialBinding(cmd);
                return;
            }

            var depthTexture = ScreenSpaceRenderUtils.GetCameraDepthTexture(renderingData.cameraData.renderer);
            if (depthTexture == null)
            {
                DisableMaterialBinding(cmd);
                return;
            }

            var cameraPosition = renderingData.cameraData.camera.transform.position;
            var proxyMode = SelectProxy(cameraPosition, renderingData.cullResults, out var proxyBoxMin, out var proxyBoxMax);
#if UNITY_EDITOR
            if (proxyMode != ProxyMode.Box)
                s_EditorNoProxyCount++;
#endif

            int width = Mathf.Max(1, m_History.width);
            int height = Mathf.Max(1, m_History.height);
            // Same convention as HDRP's ComputeViewportScaleAndLimit: the clamp keeps the sample
            // inside the pixel centre of the full resolution color pyramid.
            var scaleAndLimit = new Vector4(1f, 1f, (width - 0.5f) / width, (height - 0.5f) / height);
            float fadeRcpLength = 1f / Mathf.Max(0.001f, settings.screenFadeDistance.value);

            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                cmd.SetGlobalTexture(ScreenSpaceLightingShaderIDs.RefractionColorPyramid, m_History.currentColorPyramid);
                cmd.SetGlobalTexture(ScreenSpaceLightingShaderIDs.RefractionDepthTexture, depthTexture);
                cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.RefractionColorPyramidScaleAndLimit, scaleAndLimit);
                cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.RefractionProxyBoxMin, proxyBoxMin);
                cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.RefractionProxyBoxMax, proxyBoxMax);
                cmd.SetGlobalVector(
                    ScreenSpaceLightingShaderIDs.RefractionParams,
                    new Vector4(
                        // HDRP's _ColorPyramidLodCount is the mip count; the shader derives the last
                        // mip index from it. Passing the last index here would lose the coarsest mip.
                        Mathf.Max(0, m_History.colorPyramidMipCount),
                        fadeRcpLength,
                        settings.intensity.value,
                        0f));
                cmd.EnableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceRefractionKeyword);
#if UNITY_EDITOR
                s_EditorBoundCount++;
#endif
            }
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            // The keyword is only valid while transparents are drawn. Never leave it enabled for the
            // next camera.
            DisableMaterialBinding(cmd);
        }

        static void DisableMaterialBinding(CommandBuffer cmd)
        {
            if (cmd == null)
                return;

            cmd.DisableShaderKeyword(ScreenSpaceLightingShaderIDs.ScreenSpaceRefractionKeyword);
            cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.RefractionParams, Vector4.zero);
            cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.RefractionProxyBoxMin, Vector4.zero);
            cmd.SetGlobalVector(ScreenSpaceLightingShaderIDs.RefractionProxyBoxMax, Vector4.zero);
            cmd.SetGlobalTexture(ScreenSpaceLightingShaderIDs.RefractionColorPyramid, Texture2D.blackTexture);
            cmd.SetGlobalTexture(ScreenSpaceLightingShaderIDs.RefractionDepthTexture, Texture2D.blackTexture);
        }

        // Picks the scene proxy for the refracted ray:
        //  1. the box-projected reflection probe that best approximates the scene around the camera;
        //  2. otherwise any active reflection probe bounds (box projection only affects reflections);
        //  3. otherwise a far-projection proxy. HDRP's no-env-light default is a camera-centred
        //     renderer-bounds sphere whose projection usually clamps to 0; this branch is a
        //     deliberate URP extension that keeps probe-less scenes refracting.
        // URP exposes no per-material proxy volume and no sphere influence, so the world AABB is the
        // approximation. HDRP's default probe also uses its influence volume as the proxy; only an
        // explicit infinite proxy volume is not representable here. The probe rotation is ignored
        // (the AABB of a rotated probe is conservative).
        //
        // CullingResults normally contains the probes, but it does not include runtime probes in
        // every renderer path; a cached scene scan is the fallback.
        ProxyMode SelectProxy(Vector3 cameraPosition, CullingResults cullResults, out Vector4 boxMin, out Vector4 boxMax)
        {
            boxMin = Vector4.zero;
            boxMax = Vector4.zero;

            RefreshSceneProbesIfNeeded();

            if (TryFindProbe(cameraPosition, cullResults, requireBoxProjection: true, out var bounds))
            {
                boxMin = new Vector4(bounds.min.x, bounds.min.y, bounds.min.z, (float)ProxyMode.Box);
                boxMax = new Vector4(bounds.max.x, bounds.max.y, bounds.max.z, 0f);
                return ProxyMode.Box;
            }

            // No box-projected probe. Any probe volume is still a valid refraction proxy: the box
            // projection flag only changes how reflections are sampled, not where the volume is.
            if (TryFindProbe(cameraPosition, cullResults, requireBoxProjection: false, out bounds))
            {
                boxMin = new Vector4(bounds.min.x, bounds.min.y, bounds.min.z, (float)ProxyMode.Box);
                boxMax = new Vector4(bounds.max.x, bounds.max.y, bounds.max.z, 0f);
                return ProxyMode.Box;
            }

            // Far-projection proxy (URP extension); see ScreenSpaceRefraction.hlsl.
            boxMin = new Vector4(0f, 0f, 0f, (float)ProxyMode.Infinite);
            return ProxyMode.Infinite;
        }

        bool TryFindProbe(Vector3 cameraPosition, CullingResults cullResults, bool requireBoxProjection, out Bounds bestBounds)
        {
            bool found = false;
            float bestScore = float.MaxValue;
            int bestImportance = int.MinValue;
            float bestVolume = float.MaxValue;
            Bounds foundBounds = default;

            void Consider(Bounds bounds, int importance)
            {
                if (bounds.size.x <= 0f || bounds.size.y <= 0f || bounds.size.z <= 0f)
                    return;

                // 0 when the camera is inside the probe; otherwise the squared distance to it.
                float score = bounds.SqrDistance(cameraPosition);
                float volume = bounds.size.sqrMagnitude;
                bool better = !found || score < bestScore - 1e-4f ||
                              (Mathf.Abs(score - bestScore) <= 1e-4f &&
                               (importance > bestImportance ||
                                (importance == bestImportance && volume < bestVolume)));
                if (better)
                {
                    found = true;
                    bestScore = score;
                    bestImportance = importance;
                    bestVolume = volume;
                    foundBounds = bounds;
                }
            }

            var visibleProbes = cullResults.visibleReflectionProbes;
#if UNITY_EDITOR
            s_EditorVisibleProbeCount = visibleProbes.IsCreated ? visibleProbes.Length : 0;
#endif
            if (visibleProbes.IsCreated)
            {
                for (int i = 0; i < visibleProbes.Length; i++)
                {
                    var probe = visibleProbes[i];
                    if (requireBoxProjection && !probe.isBoxProjection)
                        continue;
                    Consider(probe.bounds, probe.importance);
                }
            }

            int sceneProbeCount = m_SceneProbes?.Length ?? 0;
#if UNITY_EDITOR
            s_EditorSceneProbeCount = sceneProbeCount;
            if (requireBoxProjection)
                s_EditorBoxProbeCount = 0;
#endif
            for (int i = 0; i < sceneProbeCount; i++)
            {
                var probe = m_SceneProbes[i];
                if (probe == null || !probe.isActiveAndEnabled)
                    continue;
                if (requireBoxProjection && !probe.boxProjection)
                    continue;
#if UNITY_EDITOR
                if (requireBoxProjection)
                    s_EditorBoxProbeCount++;
#endif
                Consider(probe.bounds, probe.importance);
            }

            bestBounds = foundBounds;
            return found;
        }

        void RefreshSceneProbesIfNeeded()
        {
            int frame = Time.frameCount;
            if (m_SceneProbes != null && frame - m_SceneProbeRefreshFrame < k_SceneProbeRefreshFrames)
                return;

            m_SceneProbes = Object.FindObjectsOfType<ReflectionProbe>();
            m_SceneProbeRefreshFrame = frame;
        }
    }
}