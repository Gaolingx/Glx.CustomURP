using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Adds ground truth ambient occlusion (GTAO) to the active renderer.
    /// GTAO is independent from <c>ScreenSpaceLightingRendererFeature</c> and owns its own depth pyramid and history.
    /// </summary>
    [DisallowMultipleRendererFeature("Ground Truth Ambient Occlusion")]
    [Tooltip("Ground truth ambient occlusion darkens creases and contact regions using a screen-space horizon search.")]
    public sealed class GroundTruthAmbientOcclusionRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] GroundTruthAmbientOcclusionSettings m_Settings = new GroundTruthAmbientOcclusionSettings();

        GroundTruthAmbientOcclusionPass m_Pass;
        CameraHistoryStore<GroundTruthAmbientOcclusionHistory> m_HistoryStore;

        internal GroundTruthAmbientOcclusionSettings settings => m_Settings;
        internal GroundTruthAmbientOcclusionPass pass => m_Pass;
        internal CameraHistoryStore<GroundTruthAmbientOcclusionHistory> historyStore => m_HistoryStore;

        /// <inheritdoc/>
        public override void Create()
        {
            if (m_Settings == null)
                m_Settings = new GroundTruthAmbientOcclusionSettings();

            m_HistoryStore?.Dispose();
            m_HistoryStore = new CameraHistoryStore<GroundTruthAmbientOcclusionHistory>();
            m_Pass = new GroundTruthAmbientOcclusionPass();
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

            int xrEye = cameraData.xr.enabled ? cameraData.xr.multipassId : 0;
            var descriptor = cameraData.cameraTargetDescriptor;
            m_HistoryStore.GetOrCreate(cameraData.camera, xrEye, descriptor.width, descriptor.height, frameIndex, cameraData.taaSettings.resetHistoryFrames, out _);

            renderer.EnqueuePass(m_Pass);
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
