using IDs = UnityEngine.Rendering.Universal.ScreenSpaceContactShadowsShaderIDs;

namespace UnityEngine.Rendering.Universal
{
    internal sealed class ScreenSpaceContactShadowsDebugPass : ScriptableRenderPass
    {
        readonly ScreenSpaceContactShadowsRendererFeature m_Feature;
        readonly MaterialPropertyBlock m_Properties = new MaterialPropertyBlock();
        Material m_Material;

        internal ScreenSpaceContactShadowsDebugPass(ScreenSpaceContactShadowsRendererFeature feature)
        {
            m_Feature = feature;
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            profilingSampler = new ProfilingSampler("Contact Shadows Debug");
        }

        internal void Setup(Shader shader, ContactShadowDebugMode mode, int slot)
        {
            if (m_Material == null || m_Material.shader != shader)
            {
                CoreUtils.Destroy(m_Material);
                m_Material = CoreUtils.CreateEngineMaterial(shader);
            }
            m_Properties.SetInteger(IDs.debugMode, (int)mode);
            m_Properties.SetInteger(IDs.debugSlot, Mathf.Clamp(slot, 0, 23));
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData;
            int eye = camera.xr.enabled ? camera.xr.multipassId : 0;
            if (!m_Feature.TryGetHistory(camera.camera, eye, out var history) || !history.packedReady)
                return;
            var packed = history.packedTexture?.rt;
            if (packed == null || !packed.IsCreated())
                return;

            // Override the neutral lighting binding for this draw only. URP binds camera color.
            m_Properties.SetTexture(IDs.texture, packed);
            m_Properties.SetVector(IDs.screenSize, new Vector4(packed.width, packed.height, 0, 0));
            renderingData.commandBuffer.DrawProcedural(Matrix4x4.identity, m_Material, 0, MeshTopology.Triangles, 3, 1, m_Properties);
            m_Properties.Clear();
        }

        internal void Dispose() => CoreUtils.Destroy(m_Material);
    }
}
