using System;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// A volume component that controls screen-space refraction.
    /// The effect also requires a <c>ScreenSpaceLightingRendererFeature</c> on the active renderer and
    /// a transparent material that selects a refraction model.
    /// </summary>
    /// <remarks>
    /// The defaults mirror HDRP 14.0.12. The proxy is the active box-projected reflection probe, the
    /// only scene proxy available in URP; per-material proxy volumes are not migrated.
    /// </remarks>
    [Serializable, VolumeComponentMenuForRenderPipeline("Lighting/Screen Space Refraction", typeof(UniversalRenderPipeline))]
    public sealed class ScreenSpaceRefraction : VolumeComponent
    {
        /// <summary>
        /// Enables screen-space refraction for this volume.
        /// </summary>
        [Tooltip("Enables screen-space refraction for this volume.")]
        public BoolParameter enabled = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);

        /// <summary>
        /// Controls the distance at which screen-space refraction fades out near the edge of the
        /// screen. A value near 0 fades only close to the edge; a value near 1 fades from the center.
        /// </summary>
        [Tooltip("Distance from the screen edge where refraction fades out.")]
        public ClampedFloatParameter screenFadeDistance = new ClampedFloatParameter(0.1f, 0.001f, 1.0f);

        /// <summary>
        /// Multiplies the screen-space refraction contribution. Zero leaves the image unchanged,
        /// which keeps the fallback to the reflection probe path testable.
        /// </summary>
        [Tooltip("Intensity of the screen-space refraction. Zero leaves the image unchanged.")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(1.0f, 0.0f, 4.0f);
    }
}