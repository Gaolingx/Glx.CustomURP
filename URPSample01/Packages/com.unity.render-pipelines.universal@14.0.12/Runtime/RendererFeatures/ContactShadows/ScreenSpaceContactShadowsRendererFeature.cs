namespace UnityEngine.Rendering.Universal
{
    [DisallowMultipleRendererFeature("Screen Space Contact Shadows")]
    public sealed class ScreenSpaceContactShadowsRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] ScreenSpaceContactShadowsSettings m_Settings = new ScreenSpaceContactShadowsSettings();
        [SerializeField] ScreenSpaceContactShadowsResources m_Resources = new ScreenSpaceContactShadowsResources();
        CameraHistoryStore<ScreenSpaceContactShadowsHistory> m_Histories;
        ScreenSpaceContactShadowLights m_Lights;
        ScreenSpaceContactShadowsPass m_Pass;
        ScreenSpaceContactShadowsCleanupPass m_Cleanup;
        ScreenSpaceContactShadowsDebugPass m_Debug;
        bool m_LoggedMissingResources, m_LoggedUnsupported;
        ScreenSpaceCapabilityResult m_Platform;

        public override void Create()
        {
            ScreenSpaceDepthPyramidSystem.RegisterOwner(this);
            m_Histories ??= new CameraHistoryStore<ScreenSpaceContactShadowsHistory>();
            m_Lights ??= new ScreenSpaceContactShadowLights();
            m_Pass ??= new ScreenSpaceContactShadowsPass(m_Lights);
            m_Cleanup ??= new ScreenSpaceContactShadowsCleanupPass(m_Lights.additionalMasks.Length);
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
            var camera = renderingData.cameraData;
            if (!ScreenSpaceCapability.EvaluateCamera(camera).supported)
                return;
            m_Lights.Build(renderingData.lightData);
            if (m_Lights.count == 0)
                return;

            // Event sorting places cleanup after opaque lighting. Queue it before any active-path
            // early return, even when there is no packed target or dispatch for this camera.
            renderer.EnqueuePass(m_Cleanup);
            if (m_Resources == null || !m_Resources.hasComputeResources ||
                !m_Resources.depthPyramidCompute.HasKernel("KDepthPyramidCopy") ||
                !m_Resources.depthPyramidCompute.HasKernel("KDepthPyramidDownsample") ||
                !m_Resources.contactShadowsCompute.HasKernel("KContactShadow"))
            {
                if (!m_LoggedMissingResources)
                {
                    Debug.LogWarning("Screen Space Contact Shadows requires both a depth pyramid compute shader and a contact shadows compute shader.", this);
                    m_LoggedMissingResources = true;
                }
                return;
            }

            m_LoggedMissingResources = false;
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
            m_Pass.Setup(volume, history, m_Resources.contactShadowsCompute);
            renderer.EnqueuePass(m_Pass);
            if (m_Settings.debugMode != ContactShadowDebugMode.Off && m_Resources.debugShader != null && m_Resources.debugShader.isSupported)
            {
                m_Debug ??= new ScreenSpaceContactShadowsDebugPass(this);
                m_Debug.Setup(m_Resources.debugShader, m_Settings.debugMode, m_Settings.debugSlot);
                renderer.EnqueuePass(m_Debug);
            }
        }

        protected override void Dispose(bool disposing)
        {
            m_Debug?.Dispose();
            m_Debug = null;
            m_Histories?.Dispose();
            m_Histories = null;
            m_Pass?.Dispose();
            m_Pass = null;
            m_Lights = null;
            m_Cleanup?.Dispose();
            m_Cleanup = null;
            ScreenSpaceDepthPyramidSystem.UnregisterOwner(this);
        }
    }
}
