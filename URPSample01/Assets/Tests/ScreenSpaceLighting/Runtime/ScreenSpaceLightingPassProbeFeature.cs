using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Test-only consumer of the Core early motion/depth schedule. It declares Depth+Normal+Motion inputs
// and samples them at event 203, which is before any GTAO/SSGI/SSR pass exists.
public sealed class ScreenSpaceLightingPassProbeFeature : ScriptableRendererFeature
{
    public RenderPassEvent probeEvent = (RenderPassEvent)203;
    public Material probeMaterial;

    public static RenderTexture probeTarget;
    public static int executeCount;

    ProbePass m_Pass;

    public override void Create()
    {
        m_Pass = new ProbePass();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (probeMaterial == null || probeTarget == null)
            return;
        m_Pass.renderPassEvent = probeEvent;
        m_Pass.material = probeMaterial;
        m_Pass.target = probeTarget;
        renderer.EnqueuePass(m_Pass);
    }

    sealed class ProbePass : ScriptableRenderPass
    {
        public Material material;
        public RenderTexture target;

        public ProbePass()
        {
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal | ScriptableRenderPassInput.Motion);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (material == null || target == null)
                return;
            var cmd = CommandBufferPool.Get("ScreenSpaceLighting Pass Probe");
            cmd.SetRenderTarget(target);
            cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            executeCount++;
        }
    }
}
