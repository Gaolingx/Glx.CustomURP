using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Adds screen-space global illumination, reflections and refraction.
    /// The individual effects are enabled through the matching volume overrides under <c>Lighting</c>.
    /// </summary>
    [DisallowMultipleRendererFeature("Screen Space Lighting")]
    [Tooltip("Screen-space global illumination, reflections and refraction. Each effect is enabled by its own volume override.")]
    public sealed class ScreenSpaceLightingRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] ScreenSpaceLightingSettings m_Settings = new ScreenSpaceLightingSettings();

        ScreenSpaceLightingSetupPass m_SetupPass;
        CameraHistoryStore<ScreenSpaceLightingHistory> m_HistoryStore;

        internal ScreenSpaceLightingSettings settings => m_Settings;
        internal ScreenSpaceLightingSetupPass setupPass => m_SetupPass;
        internal CameraHistoryStore<ScreenSpaceLightingHistory> historyStore => m_HistoryStore;

        internal static bool HasAnyEffectEnabled(VolumeStack stack)
        {
            if (stack == null)
                return false;

            var ssgi = stack.GetComponent<ScreenSpaceGlobalIllumination>();
            if (ssgi != null && ssgi.enabled.value)
                return true;

            var ssr = stack.GetComponent<ScreenSpaceReflection>();
            if (ssr != null && ssr.enabled.value)
                return true;

            var refraction = stack.GetComponent<ScreenSpaceRefraction>();
            return refraction != null && refraction.enabled.value;
        }

        /// <inheritdoc/>
        public override void Create()
        {
            if (m_Settings == null)
                m_Settings = new ScreenSpaceLightingSettings();

            m_HistoryStore?.Dispose();
            m_HistoryStore = new CameraHistoryStore<ScreenSpaceLightingHistory>();
            m_SetupPass = new ScreenSpaceLightingSetupPass();
        }

        /// <inheritdoc/>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var cameraData = renderingData.cameraData;
            int frameIndex = Time.frameCount;

            m_HistoryStore.PruneDestroyedCameras();
            m_HistoryStore.PruneUnused(frameIndex, m_Settings.maxUnusedFrames);
            m_HistoryStore.TrimToCount(m_Settings.maxCachedCameras);

            if (!ScreenSpaceCapability.EvaluatePlatform().supported)
                return;

            if (!ScreenSpaceCapability.EvaluateCamera(cameraData).supported)
                return;

            if (!HasAnyEffectEnabled(VolumeManager.instance.stack))
                return;

            int xrEye = cameraData.xr.enabled ? cameraData.xr.multipassId : 0;
            var descriptor = cameraData.cameraTargetDescriptor;
            m_HistoryStore.GetOrCreate(cameraData.camera, xrEye, descriptor.width, descriptor.height, frameIndex, cameraData.taaSettings.resetHistoryFrames, out _);

            renderer.EnqueuePass(m_SetupPass);
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                m_HistoryStore?.Dispose();
                m_HistoryStore = null;
            }

            base.Dispose(disposing);
        }
    }
}
