namespace UnityEngine.Rendering.Universal
{
    internal sealed class ScreenSpaceLightingSetupPass : ScriptableRenderPass
    {
        // Material binding/debug slot from the pass order table, after SSGI (205) and opaque SSR
        // (206) and before Deferred GBuffer (210) or Forward opaque geometry (250).
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 7);

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler("Screen Space Lighting Setup");

#if UNITY_EDITOR
        // Editor-only evidence hook: the skeleton dispatches no GPU work, so Frame Debugger cannot
        // show the pass. Verification code reads this counter to prove the pass really executed.
        internal static int s_EditorExecuteCount;
#endif

        internal ScreenSpaceLightingSetupPass()
        {
            renderPassEvent = k_RenderPassEvent;
            // Reserved slot for the SSGI/SSR material bindings added by T6/T7. The temporal and
            // pyramid passes request their own inputs, so this marker requests none.
            ConfigureInput(ScriptableRenderPassInput.None);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            s_EditorExecuteCount++;
#endif
            // Only a profiling marker until T4/T6/T7 bind the SSL resources.
            using (new ProfilingScope(renderingData.commandBuffer, s_ProfilingSampler))
            {
            }
        }
    }
}
