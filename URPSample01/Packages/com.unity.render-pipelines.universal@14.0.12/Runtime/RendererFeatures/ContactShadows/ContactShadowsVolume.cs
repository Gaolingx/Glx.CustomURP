using System;

namespace UnityEngine.Rendering.Universal
{
    [Serializable, VolumeComponentMenuForRenderPipeline("Shadowing/Contact Shadows", typeof(UniversalRenderPipeline))]
    public sealed class ContactShadowsVolume : VolumeComponent
    {
        public BoolParameter enabled = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);
        public ClampedFloatParameter length = new ClampedFloatParameter(0.15f, 0f, 1f);
        public ClampedFloatParameter opacity = new ClampedFloatParameter(1f, 0f, 1f);
        public ClampedFloatParameter distanceScaleFactor = new ClampedFloatParameter(0.5f, 0f, 1f);
        public MinFloatParameter maxDistance = new MinFloatParameter(50f, 0f);
        public MinFloatParameter minDistance = new MinFloatParameter(0f, 0f);
        public MinFloatParameter fadeDistance = new MinFloatParameter(5f, 0f);
        public MinFloatParameter fadeInDistance = new MinFloatParameter(0f, 0f);
        public ClampedFloatParameter rayBias = new ClampedFloatParameter(0.2f, 0f, 1f);
        public ClampedFloatParameter thicknessScale = new ClampedFloatParameter(0.15f, 0.02f, 1f);
        [Tooltip("Ray steps per light, independent of Quality Settings.")]
        public ClampedIntParameter sampleCount = new ClampedIntParameter(10, 4, 64);
    }
}
