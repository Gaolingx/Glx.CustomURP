namespace UnityEngine.Rendering.Universal
{
    internal sealed class ScreenSpaceContactShadowsPass : ScriptableRenderPass
    {
        internal Vector4 traceParams { get; private set; }
        internal Vector4 distanceParams { get; private set; }
        internal float opacity { get; private set; }
        internal int sampleCount { get; private set; }

        internal ScreenSpaceContactShadowsPass() => renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 8;

        internal void Setup(ContactShadowsVolume volume)
        {
            traceParams = new Vector4(volume.length.value, volume.distanceScaleFactor.value, volume.rayBias.value, volume.thicknessScale.value);
            distanceParams = new Vector4(volume.minDistance.value, volume.maxDistance.value, volume.fadeInDistance.value, volume.fadeDistance.value);
            opacity = volume.opacity.value;
            sampleCount = volume.sampleCount.value;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) { }
    }
}
