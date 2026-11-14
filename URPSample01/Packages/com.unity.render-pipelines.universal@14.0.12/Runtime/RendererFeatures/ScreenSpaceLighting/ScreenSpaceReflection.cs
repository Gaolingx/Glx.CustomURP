using System;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// A volume component that controls screen-space reflections.
    /// The effect also requires a <c>ScreenSpaceLightingRendererFeature</c> on the active renderer.
    /// </summary>
    /// <remarks>
    /// The defaults and the ray marching parameters mirror HDRP 14.0.12. Unlike HDRP this volume
    /// does not expose a raytracing or PBR accumulation algorithm yet; only the Approximation
    /// algorithm is implemented.
    /// </remarks>
    [Serializable, VolumeComponentMenuForRenderPipeline("Lighting/Screen Space Reflection", typeof(UniversalRenderPipeline))]
    public sealed class ScreenSpaceReflection : VolumeComponent
    {
        /// <summary>
        /// Enables screen-space reflections for this volume.
        /// </summary>
        [Tooltip("Enables screen-space reflections for this volume.")]
        public BoolParameter enabled = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);

        /// <summary>
        /// The smoothness at which the effect starts. Below this value the material keeps its
        /// reflection probe / sky lighting.
        /// </summary>
        [Tooltip("Smoothness at which screen-space reflections start. Rougher pixels keep the reflection probe.")]
        public ClampedFloatParameter minSmoothness = new ClampedFloatParameter(0.9f, 0.0f, 1.0f);

        /// <summary>
        /// The smoothness at which the effect reaches full intensity. The contribution fades out
        /// between <see cref="minSmoothness"/> and this value.
        /// </summary>
        [Tooltip("Smoothness at which screen-space reflections reach full intensity. Values between Min Smoothness and this value are faded.")]
        public ClampedFloatParameter smoothnessFadeStart = new ClampedFloatParameter(0.9f, 0.0f, 1.0f);

        /// <summary>
        /// Assumed thickness of the surface behind a reflected hit, as a fraction of the hit depth.
        /// </summary>
        [Tooltip("Assumed thickness of objects the reflection ray may pass behind, as a fraction of the hit depth.")]
        public ClampedFloatParameter depthBufferThickness = new ClampedFloatParameter(0.01f, 0.0f, 1.0f);

        /// <summary>
        /// Screen-space distance over which the reflection fades out at the edge of the screen.
        /// </summary>
        [Tooltip("Distance from the screen edge where the reflection fades out.")]
        public ClampedFloatParameter screenFadeDistance = new ClampedFloatParameter(0.1f, 0.0f, 1.0f);

        /// <summary>
        /// Maximum number of Hi-Z ray marching steps.
        /// </summary>
        [Tooltip("Maximum number of Hi-Z ray marching steps.")]
        public ClampedIntParameter rayMaxIterations = new ClampedIntParameter(64, 1, 256);

        /// <summary>
        /// Allows rays that exit the geometry to sample the sky from the previous color pyramid.
        /// </summary>
        [Tooltip("Allows screenspace rays that miss the scene to sample the sky.")]
        public BoolParameter reflectSky = new BoolParameter(true);

        /// <summary>
        /// Multiplies the screen-space contribution. Zero leaves the image unchanged, which keeps
        /// the regression against the reflection probe path testable.
        /// </summary>
        [Tooltip("Intensity of the screen-space reflection. Zero leaves the image unchanged.")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(1.0f, 0.0f, 4.0f);
    }
}