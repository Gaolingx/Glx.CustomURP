using System;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// A volume component that controls screen-space reflections.
    /// The effect also requires a <c>ScreenSpaceLightingRendererFeature</c> on the active renderer.
    /// </summary>
    [Serializable, VolumeComponentMenuForRenderPipeline("Lighting/Screen Space Reflection", typeof(UniversalRenderPipeline))]
    public sealed class ScreenSpaceReflection : VolumeComponent
    {
        /// <summary>
        /// Enables screen-space reflections for this volume.
        /// </summary>
        [Tooltip("Enables screen-space reflections for this volume.")]
        public BoolParameter enabled = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);
    }
}
