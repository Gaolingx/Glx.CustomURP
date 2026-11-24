namespace UnityEngine.Rendering.Universal
{
    [DisallowMultipleRendererFeature("Screen Space Contact Shadows")]
    public sealed class ScreenSpaceContactShadowsRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] ScreenSpaceContactShadowsSettings m_Settings = new ScreenSpaceContactShadowsSettings();
        [SerializeField] ScreenSpaceContactShadowsResources m_Resources = new ScreenSpaceContactShadowsResources();
        CameraHistoryStore<ScreenSpaceContactShadowsHistory> m_Histories;
        ScreenSpaceContactShadowsPass m_Pass;
        bool m_LoggedMissingResources, m_LoggedUnsupported;
        ScreenSpaceCapabilityResult m_Platform;

        public override void Create()
        {
            ScreenSpaceDepthPyramidSystem.RegisterOwner(this);
            m_Histories ??= new CameraHistoryStore<ScreenSpaceContactShadowsHistory>();
            m_Pass ??= new ScreenSpaceContactShadowsPass();
            m_Platform = ScreenSpaceContactShadowsCapability.EvaluatePlatform();
        }

        internal bool TryGetHistory(Camera camera, int eye, out ScreenSpaceContactShadowsHistory history)
        {
            history = null;
            return m_Histories != null && m_Histories.TryGet(camera, eye, out history);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            int frame = Time.frameCount;
            m_Histories.PruneDestroyedCameras();
            m_Histories.PruneUnused(frame, m_Settings.maxUnusedFrames);
            var volume = VolumeManager.instance.stack.GetComponent<ContactShadowsVolume>();
            if (volume == null || !volume.active || !volume.enabled.value)
                return;
            if (!m_Platform.supported)
            {
                if (!m_LoggedUnsupported)
                    Debug.LogWarning(m_Platform.message, this);
                m_LoggedUnsupported = true;
                return;
            }
            if (!m_Resources.hasComputeResources)
            {
                if (!m_LoggedMissingResources)
                {
                    Debug.LogWarning("Screen Space Contact Shadows requires both a depth pyramid compute shader and a contact shadows compute shader.", this);
                    m_LoggedMissingResources = true;
                }
                return;
            }

            m_LoggedMissingResources = false;
            var camera = renderingData.cameraData;
            if (!ScreenSpaceCapability.EvaluateCamera(camera).supported || !HasContactLight(renderingData.lightData))
                return;
            var pyramid = ScreenSpaceDepthPyramidSystem.Acquire(renderer, renderingData, m_Resources.depthPyramidCompute,
                m_Settings.maxUnusedFrames, m_Settings.maxCachedCameras);
            if (pyramid == null)
                return;

            int eye = camera.xr.enabled ? camera.xr.multipassId : 0;
            if (!m_Histories.TryGet(camera.camera, eye, out _))
                m_Histories.TrimToCount(Mathf.Max(1, m_Settings.maxCachedCameras) - 1);
            var descriptor = camera.cameraTargetDescriptor;
            var history = m_Histories.GetOrCreate(camera.camera, eye, descriptor.width, descriptor.height, frame, 0, out _);
            history.depthPyramidSource = pyramid;
            m_Pass.Setup(volume, history);
            renderer.EnqueuePass(m_Pass);
        }

        static bool HasContactLight(in LightData data)
        {
            foreach (var visible in data.visibleLights)
            {
                if (visible.lightType != LightType.Directional && visible.lightType != LightType.Point && visible.lightType != LightType.Spot)
                    continue;
                var light = visible.light;
                if (light != null && light.TryGetComponent<ContactShadowLight>(out var marker) && marker.isActiveAndEnabled && marker.castContactShadows)
                    return true;
            }
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            m_Histories?.Dispose();
            m_Histories = null;
            m_Pass = null;
            ScreenSpaceDepthPyramidSystem.UnregisterOwner(this);
        }
    }
}
