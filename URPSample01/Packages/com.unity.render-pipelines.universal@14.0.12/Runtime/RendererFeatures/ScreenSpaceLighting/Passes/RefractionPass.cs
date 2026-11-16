using System.Collections.Generic;
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

        // The shader table has one slot reserved for the infinite fallback, so a miss always has a
        // proxy. The remaining slots hold finite probe AABBs.
        internal const int k_MaxRefractionProxies = 8;
        const int k_MaxBoxProxies = k_MaxRefractionProxies - 1;

        struct ProbeCandidate
        {
            public Bounds bounds;
            public int importance;
            public int instanceId;
            public bool visible;
        }

        readonly Vector4[] m_ProbeMin = new Vector4[k_MaxRefractionProxies];
        readonly Vector4[] m_ProbeMax = new Vector4[k_MaxRefractionProxies];
        readonly Vector4[] m_ProbeParams = new Vector4[k_MaxRefractionProxies];
        readonly ProbeCandidate[] m_ProbeCandidates = new ProbeCandidate[k_MaxBoxProxies];
        readonly HashSet<int> m_ProbeInstanceIds = new HashSet<int>();
        int m_ProbeCount;

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

            int boxProxyCount = BuildProbeTable(renderingData.cullResults);
#if UNITY_EDITOR
            if (boxProxyCount == 0)
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
                // Upload the fixed-size arrays; the shader reads only _SslRefractionProbeCount entries.
                cmd.SetGlobalVectorArray(ScreenSpaceLightingShaderIDs.RefractionProbeMin, m_ProbeMin);
                cmd.SetGlobalVectorArray(ScreenSpaceLightingShaderIDs.RefractionProbeMax, m_ProbeMax);
                cmd.SetGlobalVectorArray(ScreenSpaceLightingShaderIDs.RefractionProbeParams, m_ProbeParams);
                cmd.SetGlobalInt(ScreenSpaceLightingShaderIDs.RefractionProbeCount, m_ProbeCount);
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
            cmd.SetGlobalInt(ScreenSpaceLightingShaderIDs.RefractionProbeCount, 0);
            cmd.SetGlobalTexture(ScreenSpaceLightingShaderIDs.RefractionColorPyramid, Texture2D.blackTexture);
            cmd.SetGlobalTexture(ScreenSpaceLightingShaderIDs.RefractionDepthTexture, Texture2D.blackTexture);
        }

        // Builds the per-object proxy table. The shader picks the entry containing the object
        // center (highest importance), otherwise the nearest AABB, otherwise the infinite fallback.
        // The last slot is always the fallback, so the table can never be empty.
        //
        // URP exposes no per-material proxy volume and no sphere influence, so a world AABB is the
        // approximation. HDRP's default probe also uses its influence volume as the proxy; only an
        // explicit infinite proxy volume is not representable. Probe rotation is ignored (the AABB
        // of a rotated probe is conservative). URP 14 does not expose per-probe rendering layer
        // masks, so light layers do not filter the proxy.
        //
        // CullingResults normally contains the probes, but it does not include runtime probes in
        // every renderer path; the cached scene scan fills the remaining slots.
        int BuildProbeTable(CullingResults cullResults)
        {
            RefreshSceneProbesIfNeeded();

            m_ProbeInstanceIds.Clear();
            int candidateCount = 0;

            void Consider(Bounds bounds, int importance, int instanceId, bool visible)
            {
                if (bounds.size.x <= 0f || bounds.size.y <= 0f || bounds.size.z <= 0f)
                    return;
                if (!m_ProbeInstanceIds.Add(instanceId))
                    return;

                var candidate = new ProbeCandidate
                {
                    bounds = bounds,
                    importance = importance,
                    instanceId = instanceId,
                    visible = visible,
                };

                int insert = 0;
                while (insert < candidateCount && !IsBetterCandidate(candidate, m_ProbeCandidates[insert]))
                    insert++;

                if (insert >= k_MaxBoxProxies)
                    return;

                int last = Mathf.Min(candidateCount, k_MaxBoxProxies - 1);
                for (int i = last; i > insert; i--)
                    m_ProbeCandidates[i] = m_ProbeCandidates[i - 1];
                m_ProbeCandidates[insert] = candidate;
                if (candidateCount < k_MaxBoxProxies)
                    candidateCount++;
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
                    var reflectionProbe = probe.reflectionProbe;
                    if (reflectionProbe == null)
                        continue;
                    Consider(probe.bounds, probe.importance, reflectionProbe.GetInstanceID(), true);
                }
            }

            int sceneProbeCount = m_SceneProbes?.Length ?? 0;
#if UNITY_EDITOR
            s_EditorSceneProbeCount = sceneProbeCount;
            s_EditorBoxProbeCount = 0;
#endif
            for (int i = 0; i < sceneProbeCount; i++)
            {
                var probe = m_SceneProbes[i];
                if (probe == null || !probe.isActiveAndEnabled)
                    continue;
#if UNITY_EDITOR
                if (probe.boxProjection)
                    s_EditorBoxProbeCount++;
#endif
                Consider(probe.bounds, probe.importance, probe.GetInstanceID(), false);
            }

            for (int i = 0; i < candidateCount; i++)
            {
                var candidate = m_ProbeCandidates[i];
                m_ProbeMin[i] = new Vector4(candidate.bounds.min.x, candidate.bounds.min.y, candidate.bounds.min.z, 0f);
                m_ProbeMax[i] = new Vector4(candidate.bounds.max.x, candidate.bounds.max.y, candidate.bounds.max.z, 0f);
                // y is reserved for a per-probe rendering layer mask; URP 14 exposes none.
                m_ProbeParams[i] = new Vector4(candidate.importance, 0f, (float)ProxyMode.Box, 0f);
            }

            m_ProbeMin[candidateCount] = Vector4.zero;
            m_ProbeMax[candidateCount] = Vector4.zero;
            m_ProbeParams[candidateCount] = new Vector4(0f, 0f, (float)ProxyMode.Infinite, 0f);
            m_ProbeCount = candidateCount + 1;
            return candidateCount;
        }

        static bool IsBetterCandidate(ProbeCandidate candidate, ProbeCandidate current)
        {
            if (candidate.importance != current.importance)
                return candidate.importance > current.importance;
            if (candidate.visible != current.visible)
                return candidate.visible;
            float candidateVolume = candidate.bounds.size.sqrMagnitude;
            float currentVolume = current.bounds.size.sqrMagnitude;
            if (!Mathf.Approximately(candidateVolume, currentVolume))
                return candidateVolume < currentVolume;
            return candidate.instanceId < current.instanceId;
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