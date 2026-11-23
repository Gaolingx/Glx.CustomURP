namespace UnityEngine.Rendering.Universal
{
    [DisallowMultipleRendererFeature("Screen Space Contact Shadows")]
    public sealed class ScreenSpaceContactShadowsRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] ScreenSpaceContactShadowsSettings m_Settings = new ScreenSpaceContactShadowsSettings();
        [SerializeField] ScreenSpaceContactShadowsResources m_Resources = new ScreenSpaceContactShadowsResources();
        ScreenSpaceContactShadowsPass m_Pass;
        bool m_LoggedMissingResources;

        public override void Create() => m_Pass = new ScreenSpaceContactShadowsPass();

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var volume = VolumeManager.instance.stack.GetComponent<ContactShadowsVolume>();
            if (volume == null || !volume.active || !volume.enabled.value ||
                !ScreenSpaceContactShadowsCapability.SupportsApi(SystemInfo.graphicsDeviceType))
                return;

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
            m_Pass.Setup(volume);
        }
    }
}
