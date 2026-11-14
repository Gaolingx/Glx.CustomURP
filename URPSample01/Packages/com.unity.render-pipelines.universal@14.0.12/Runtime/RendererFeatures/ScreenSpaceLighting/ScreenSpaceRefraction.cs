using System;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// A volume component that controls screen-space refraction.
    /// The effect also requires a <c>ScreenSpaceLightingRendererFeature</c> on the active renderer.
    /// </summary>
    [Serializable, VolumeComponentMenuForRenderPipeline("Lighting/Screen Space Refraction", typeof(UniversalRenderPipeline))]
    public sealed class ScreenSpaceRefraction : VolumeComponent
    {
        /// <summary>
        /// Enables screen-space refraction for this volume.
        /// </summary>
        [Tooltip("Enables screen-space refraction for this volume.")]
        public BoolParameter enabled = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);
    }
}
