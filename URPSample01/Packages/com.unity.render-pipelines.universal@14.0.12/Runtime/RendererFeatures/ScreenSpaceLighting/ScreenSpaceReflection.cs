using System;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Screen space reflection algorithm.
    /// </summary>
    public enum ScreenSpaceReflectionAlgorithm
    {
        /// <summary>Single frame reflection that samples the previous frame color pyramid.</summary>
        Approximation = 0,
        /// <summary>Physically based trace with temporal accumulation over multiple frames.</summary>
        PBRAccumulation = 1,
    }

    /// <summary>
    /// A volume parameter that selects the screen space reflection algorithm.
    /// </summary>
    [Serializable]
    public sealed class ScreenSpaceReflectionAlgorithmParameter : VolumeParameter<ScreenSpaceReflectionAlgorithm>
    {
        /// <summary>
        /// Creates a new algorithm parameter.
        /// </summary>
        /// <param name="value">Initial algorithm.</param>
        /// <param name="overrideState">Initial override state.</param>
        public ScreenSpaceReflectionAlgorithmParameter(ScreenSpaceReflectionAlgorithm value, bool overrideState = false)
            : base(value, overrideState) { }
    }

    /// <summary>
    /// A volume component that controls screen-space reflections.
    /// The effect also requires a <c>ScreenSpaceLightingRendererFeature</c> on the active renderer.
    /// </summary>
    /// <remarks>
    /// The defaults and the ray marching parameters mirror HDRP 14.0.12. Ray tracing modes are not
    /// migrated; the algorithm choice is limited to Approximation and PBRAccumulation.
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
        /// Allows transparent materials that opt in (Receive Transparent SSR) to trace reflections.
        /// Transparent reflections always use the Approximation algorithm and the previous frame
        /// color pyramid; the opaque algorithm setting is not applied to them.
        /// </summary>
        [Tooltip("Lets transparent materials that enable Receive Transparent SSR reflect the scene. Always uses the Approximation algorithm.")]
        public BoolParameter enabledTransparent = new BoolParameter(false);

        /// <summary>
        /// Selects the reflection algorithm. Switching resets the accumulation history.
        /// </summary>
        [Tooltip("Reflection algorithm. Switching the algorithm resets the accumulation history.")]
        public ScreenSpaceReflectionAlgorithmParameter usedAlgorithm =
            new ScreenSpaceReflectionAlgorithmParameter(ScreenSpaceReflectionAlgorithm.Approximation);

        /// <summary>
        /// Traces reflections at full resolution. Disabling it traces at half resolution and
        /// bilaterally upsamples the result. Applies to both Approximation and PBR Accumulation.
        /// </summary>
        [Tooltip("Trace reflections at full resolution. Disable for half resolution with an upsample. Applies to both algorithms.")]
        public BoolParameter fullResolution = new BoolParameter(true);

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
        /// Maximum length of a hardware traced reflection ray in world units. Clamped to the camera
        /// far plane, because a longer ray would only repeat the miss.
        /// </summary>
        [Tooltip("Maximum hardware traced reflection ray length in world units. Clamped to the camera far plane.")]
        public MinFloatParameter rayLength = new MinFloatParameter(50f, 0.01f);

        /// <summary>
        /// Multiplies the screen-space contribution. Zero leaves the image unchanged, which keeps
        /// the regression against the reflection probe path testable.
        /// </summary>
        [Tooltip("Intensity of the screen-space reflection. Zero leaves the image unchanged.")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(1.0f, 0.0f, 4.0f);

        /// <summary>
        /// Biases the GGX visible normal samples towards the normal. Only used by the PBR algorithm.
        /// </summary>
        [Tooltip("Biases the GGX reflection samples. PBR algorithm only.")]
        public ClampedFloatParameter biasFactor = new ClampedFloatParameter(0.5f, 0.0f, 1.0f);

        /// <summary>
        /// Runs the reflection denoiser: the temporal accumulation and the bilateral filter. Disable
        /// it to inspect the raw per-frame trace/reproject/merge signal. Only used by the PBR
        /// algorithm.
        /// </summary>
        [Tooltip("Runs the reflection denoiser. Disable to inspect the raw per-frame signal. PBR algorithm only.")]
        public BoolParameter denoise = new BoolParameter(true);

        /// <summary>
        /// Radius in pixels of the reflection denoiser bilateral filter. The half resolution path
        /// uses half of this value.
        /// </summary>
        [Tooltip("Radius in pixels of the reflection denoiser. Halved at half resolution. PBR algorithm only.")]
        public ClampedIntParameter denoiserRadius = new ClampedIntParameter(8, 1, 32);

        /// <summary>
        /// Lets the denoiser modify perfectly smooth surfaces. When disabled, surfaces above 0.99
        /// smoothness bypass the temporal accumulation.
        /// </summary>
        [Tooltip("Lets the reflection denoiser affect perfectly smooth surfaces. PBR algorithm only.")]
        public BoolParameter affectSmoothSurfaces = new BoolParameter(false);

        /// <summary>
        /// SSR traces a single reflection bounce by construction. The flag enables the roughness
        /// based denoiser radius scaling that HDRP couples to a single bounce.
        /// </summary>
        [Tooltip("Single reflection bounce. SSR traces one bounce; this enables the roughness based denoiser radius. PBR algorithm only.")]
        public BoolParameter singleReflectionBounce = new BoolParameter(true);
    }
}