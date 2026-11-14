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
        /// Selects the reflection algorithm. Switching resets the accumulation history.
        /// </summary>
        [Tooltip("Reflection algorithm. Switching the algorithm resets the accumulation history.")]
        public ScreenSpaceReflectionAlgorithmParameter usedAlgorithm =
            new ScreenSpaceReflectionAlgorithmParameter(ScreenSpaceReflectionAlgorithm.Approximation);

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

        /// <summary>
        /// Controls how much of the previous accumulation is kept. Only used by the PBR algorithm.
        /// </summary>
        [Tooltip("Amount of temporal accumulation. Higher values keep more history. PBR algorithm only.")]
        public ClampedFloatParameter accumulationFactor = new ClampedFloatParameter(0.75f, 0.0f, 1.0f);

        /// <summary>
        /// Biases the GGX visible normal samples towards the normal. Only used by the PBR algorithm.
        /// </summary>
        [Tooltip("Biases the GGX reflection samples. PBR algorithm only.")]
        public ClampedFloatParameter biasFactor = new ClampedFloatParameter(0.5f, 0.0f, 1.0f);

        /// <summary>
        /// Fraction of the speed range used to reject the accumulation history.
        /// </summary>
        [Tooltip("Fraction of the speed range that rejects the accumulation history.")]
        public ClampedFloatParameter speedRejectionParam = new ClampedFloatParameter(0.5f, 0.0f, 1.0f);

        /// <summary>
        /// Upper bound of the world/screen speed used by the rejection.
        /// </summary>
        [Tooltip("Upper speed range used by the rejection. Increase it when objects move faster.")]
        public ClampedFloatParameter speedRejectionScalerFactor = new ClampedFloatParameter(0.2f, 0.001f, 1f);

        /// <summary>
        /// Blends the rejection smoothly instead of using a hard threshold.
        /// </summary>
        [Tooltip("Rejects the history smoothly instead of using a hard threshold.")]
        public BoolParameter speedSmoothReject = new BoolParameter(false);

        /// <summary>
        /// Uses the motion of the reflecting surface for the speed rejection.
        /// </summary>
        [Tooltip("Uses the motion of the reflecting surface for the speed rejection.")]
        public BoolParameter speedSurfaceOnly = new BoolParameter(true);

        /// <summary>
        /// Uses the motion of the reflected hit surface for the speed rejection.
        /// </summary>
        [Tooltip("Uses the motion of the reflected hit surface for the speed rejection.")]
        public BoolParameter speedTargetOnly = new BoolParameter(true);

        /// <summary>
        /// Reconstructs the motion in world space instead of using the screen-space length.
        /// </summary>
        [Tooltip("Uses world-space motion for the rejection instead of screen-space motion.")]
        public BoolParameter enableWorldSpeedRejection = new BoolParameter(false);
    }
}