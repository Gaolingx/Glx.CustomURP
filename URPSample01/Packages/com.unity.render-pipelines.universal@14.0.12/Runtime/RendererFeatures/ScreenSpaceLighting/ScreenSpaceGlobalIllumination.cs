using System;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// A volume component that controls screen-space global illumination.
    /// The effect also requires a <c>ScreenSpaceLightingRendererFeature</c> on the active renderer.
    /// </summary>
    [Serializable, VolumeComponentMenuForRenderPipeline("Lighting/Screen Space Global Illumination", typeof(UniversalRenderPipeline))]
    public sealed class ScreenSpaceGlobalIllumination : VolumeComponent
    {
        /// <summary>
        /// Enables screen-space global illumination for this volume.
        /// </summary>
        [Tooltip("Enables screen-space global illumination for this volume.")]
        public BoolParameter enabled = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);
    }
}
