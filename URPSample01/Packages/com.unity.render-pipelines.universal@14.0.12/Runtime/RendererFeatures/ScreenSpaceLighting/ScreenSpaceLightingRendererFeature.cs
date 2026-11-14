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
        ScreenSpaceLightingDepthPyramidPass m_DepthPyramidPass;
        ScreenSpaceLightingSsgiPass m_SsgiPass;
        ScreenSpaceLightingSsrPass m_SsrPass;
        ScreenSpaceLightingCleanupPass m_CleanupPass;
        ScreenSpaceLightingCurrentColorPyramidPass m_CurrentColorPyramidPass;
        ScreenSpaceLightingColorHistoryCapturePass m_ColorHistoryCapturePass;
        ScreenSpaceLightingDebugPass m_DebugPass;
        CameraHistoryStore<ScreenSpaceLightingHistory> m_HistoryStore;
        bool m_LoggedMissingResources;

        internal ScreenSpaceLightingSettings settings => m_Settings;
        internal ScreenSpaceLightingSetupPass setupPass => m_SetupPass;
        internal ScreenSpaceLightingDepthPyramidPass depthPyramidPass => m_DepthPyramidPass;
        internal ScreenSpaceLightingSsgiPass ssgiPass => m_SsgiPass;
        internal ScreenSpaceLightingSsrPass ssrPass => m_SsrPass;
        internal ScreenSpaceLightingCleanupPass cleanupPass => m_CleanupPass;
        internal ScreenSpaceLightingCurrentColorPyramidPass currentColorPyramidPass => m_CurrentColorPyramidPass;
        internal ScreenSpaceLightingColorHistoryCapturePass colorHistoryCapturePass => m_ColorHistoryCapturePass;
        internal ScreenSpaceLightingDebugPass debugPass => m_DebugPass;
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

            m_Settings.EnsureResources();

            m_HistoryStore?.Dispose();
            m_HistoryStore = new CameraHistoryStore<ScreenSpaceLightingHistory>();
            m_SetupPass = new ScreenSpaceLightingSetupPass();
            m_DepthPyramidPass = new ScreenSpaceLightingDepthPyramidPass();
            m_SsgiPass = new ScreenSpaceLightingSsgiPass();
            m_SsrPass = new ScreenSpaceLightingSsrPass();
            m_CleanupPass = new ScreenSpaceLightingCleanupPass();
            m_CurrentColorPyramidPass = new ScreenSpaceLightingCurrentColorPyramidPass();
            m_ColorHistoryCapturePass = new ScreenSpaceLightingColorHistoryCapturePass();
            m_DebugPass = new ScreenSpaceLightingDebugPass();
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

            bool anyEffectEnabled = HasAnyEffectEnabled(VolumeManager.instance.stack);
            if (!anyEffectEnabled && !m_Settings.debugEnabled)
                return;

            m_Settings.EnsureResources();
            if (!m_Settings.resources.hasRuntimeResources)
            {
                LogMissingResourcesOnce();
                return;
            }

            m_LoggedMissingResources = false;

            int xrEye = cameraData.xr.enabled ? cameraData.xr.multipassId : 0;
            var descriptor = cameraData.cameraTargetDescriptor;
            var history = m_HistoryStore.GetOrCreate(
                cameraData.camera,
                xrEye,
                descriptor.width,
                descriptor.height,
                frameIndex,
                cameraData.taaSettings.resetHistoryFrames,
                out _);
            history.BeginFrame();

            m_DepthPyramidPass.Setup(history, m_Settings.resources);
            m_SsgiPass.Setup(history, m_Settings.resources);
            m_SsrPass.Setup(history, m_Settings.resources);
            m_SetupPass.Setup(m_SsgiPass, m_SsrPass);
            m_CurrentColorPyramidPass.Setup(history, m_Settings.resources);
            m_ColorHistoryCapturePass.Setup(history, m_Settings.resources);
            m_DebugPass.Setup(history, m_Settings, m_SsgiPass, m_SsrPass);

            renderer.EnqueuePass(m_SetupPass);
            renderer.EnqueuePass(m_DepthPyramidPass);
            renderer.EnqueuePass(m_SsgiPass);
            renderer.EnqueuePass(m_SsrPass);
            renderer.EnqueuePass(m_CleanupPass);
            renderer.EnqueuePass(m_CurrentColorPyramidPass);
            renderer.EnqueuePass(m_ColorHistoryCapturePass);

            if (m_Settings.debugEnabled && m_Settings.resources.debugShader != null)
                renderer.EnqueuePass(m_DebugPass);
        }

        void LogMissingResourcesOnce()
        {
            if (m_LoggedMissingResources)
                return;

            m_LoggedMissingResources = true;
            Debug.LogWarning(
                "Screen Space Lighting is enabled but its depth pyramid compute or color pyramid shader is missing. " +
                "Assign the package resources in the renderer feature inspector.");
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                m_HistoryStore?.Dispose();
                m_HistoryStore = null;
                m_Settings?.resources?.DisposeMaterials();
            }

            base.Dispose(disposing);
        }
    }
}
