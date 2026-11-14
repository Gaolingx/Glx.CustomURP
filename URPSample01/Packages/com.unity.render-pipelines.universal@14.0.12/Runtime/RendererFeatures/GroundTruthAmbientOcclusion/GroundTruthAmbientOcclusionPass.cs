namespace UnityEngine.Rendering.Universal
{
    internal sealed class GroundTruthAmbientOcclusionPass : ScriptableRenderPass
    {
        // GTAO dispatch group slot from the pass order table: after the depth/motion prepass and
        // before Deferred GBuffer or Forward opaque geometry.
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 4);

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler("Ground Truth Ambient Occlusion");

#if UNITY_EDITOR
        // Editor-only evidence hook: the skeleton dispatches no GPU work, so Frame Debugger cannot
        // show the pass. Verification code reads this counter to prove the pass really executed.
        internal static int s_EditorExecuteCount;
#endif

        internal GroundTruthAmbientOcclusionPass()
        {
            renderPassEvent = k_RenderPassEvent;
            // The AO kernels are added by T5. Requesting Depth/Normal/Motion before they exist would
            // change the built-in URP scheduling and the baseline frame, so the skeleton stays empty.
            ConfigureInput(ScriptableRenderPassInput.None);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            s_EditorExecuteCount++;
#endif
            // Only a profiling marker until T5 adds the AO dispatch group.
            using (new ProfilingScope(renderingData.commandBuffer, s_ProfilingSampler))
            {
            }
        }
    }
}
