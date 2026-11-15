using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Adds ground truth ambient occlusion (GTAO) to the active renderer.
    /// GTAO owns its AO history and packed buffers; the depth mip chain it traces against is shared
    /// with <c>ScreenSpaceLightingRendererFeature</c> through <c>ScreenSpaceDepthPyramidSystem</c>.
    /// </summary>
    [DisallowMultipleRendererFeature("Ground Truth Ambient Occlusion")]
    [Tooltip("Ground truth ambient occlusion darkens creases and contact regions using a screen-space horizon search.")]
    public sealed class GroundTruthAmbientOcclusionRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] GroundTruthAmbientOcclusionSettings m_Settings = new GroundTruthAmbientOcclusionSettings();

        GroundTruthAmbientOcclusionPass m_Pass;
        GroundTruthAmbientOcclusionDebugPass m_DebugPass;
        CameraHistoryStore<GroundTruthAmbientOcclusionHistory> m_HistoryStore;
        bool m_LoggedMissingResources;

        internal GroundTruthAmbientOcclusionSettings settings => m_Settings;
        internal GroundTruthAmbientOcclusionPass pass => m_Pass;
        internal GroundTruthAmbientOcclusionDebugPass debugPass => m_DebugPass;
        internal CameraHistoryStore<GroundTruthAmbientOcclusionHistory> historyStore => m_HistoryStore;
        internal bool loggedMissingResources => m_LoggedMissingResources;

        /// <inheritdoc/>
        public override void Create()
        {
            if (m_Settings == null)
                m_Settings = new GroundTruthAmbientOcclusionSettings();

            m_Settings.EnsureResources();

            m_HistoryStore?.Dispose();
            m_HistoryStore = new CameraHistoryStore<GroundTruthAmbientOcclusionHistory>();
            m_Pass = new GroundTruthAmbientOcclusionPass();
            m_DebugPass = new GroundTruthAmbientOcclusionDebugPass();

            ScreenSpaceDepthPyramidSystem.RegisterOwner(this);
        }

        /// <inheritdoc/>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var cameraData = renderingData.cameraData;
            int frameIndex = Time.frameCount;

            m_HistoryStore.PruneDestroyedCameras();
            m_HistoryStore.PruneUnused(frameIndex, m_Settings.maxUnusedFrames);
            m_HistoryStore.TrimToCount(m_Settings.maxCachedCameras);

            if (!m_Settings.effectEnabled && !m_Settings.debugEnabled)
            {
                m_LoggedMissingResources = false;
                return;
            }

            if (!ScreenSpaceCapability.EvaluatePlatform().supported)
                return;

            if (!ScreenSpaceCapability.EvaluateCamera(cameraData).supported)
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

            var depthPyramid = ScreenSpaceDepthPyramidSystem.Acquire(
                renderer,
                in renderingData,
                m_Settings.resources.depthPyramidCompute,
                m_Settings.maxUnusedFrames,
                m_Settings.maxCachedCameras);
            if (depthPyramid == null)
                return;

            history.depthPyramidSource = depthPyramid;
            m_Pass.Setup(history, m_Settings, m_Settings.resources);
            m_DebugPass.Setup(history, m_Settings);

            renderer.EnqueuePass(m_Pass);

            if (m_Settings.debugEnabled && m_Settings.resources.debugShader != null)
                renderer.EnqueuePass(m_DebugPass);
        }

        void LogMissingResourcesOnce()
        {
            if (m_LoggedMissingResources)
                return;

            m_LoggedMissingResources = true;
            Debug.LogWarning(
                "Ground Truth Ambient Occlusion is enabled but its compute shaders are missing. " +
                "Assign the package resources in the renderer feature inspector.");
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ScreenSpaceDepthPyramidSystem.UnregisterOwner(this);
                m_HistoryStore?.Dispose();
                m_HistoryStore = null;
                m_Settings?.resources?.DisposeMaterials();
            }

            base.Dispose(disposing);
        }
    }
}