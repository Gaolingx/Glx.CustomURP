using System;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Order in which the fallbacks are used when a screen space global illumination ray misses.
    /// Mirrors HDRP's RayMarchingFallbackHierarchy (same flag values).
    /// </summary>
    public enum RayMarchingFallbackHierarchy
    {
        /// <summary>Falls back on reflection probes (if any), then on the sky.</summary>
        [InspectorName("Reflection Probes and Sky")]
        ReflectionProbesAndSky = 0x03,
        /// <summary>Falls back on reflection probes (if any).</summary>
        [InspectorName("Reflection Probes")]
        ReflectionProbes = 0x02,
        /// <summary>Falls back on the sky.</summary>
        [InspectorName("Sky")]
        Sky = 0x01,
        /// <summary>Returns black (the material keeps its baked GI).</summary>
        [InspectorName("None")]
        None = 0x00,
    }

    /// <summary>
    /// A <see cref="VolumeParameter"/> that holds a <see cref="RayMarchingFallbackHierarchy"/> value.
    /// </summary>
    [Serializable]
    public sealed class RayMarchingFallbackHierarchyParameter : VolumeParameter<RayMarchingFallbackHierarchy>
    {
        /// <summary>Creates a new parameter.</summary>
        public RayMarchingFallbackHierarchyParameter(RayMarchingFallbackHierarchy value, bool overrideState = false)
            : base(value, overrideState) { }
    }

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

        /// <summary>
        /// Controls the fallback hierarchy used to light a pixel whose ray missed.
        /// </summary>
        [Tooltip("Controls the fallback hierarchy for indirect diffuse when a ray misses. Reflection Probes and Sky replaces the baked GI with the probe / sky ambient, like HDRP; None keeps the baked GI.")]
        public RayMarchingFallbackHierarchyParameter rayMiss = new RayMarchingFallbackHierarchyParameter(RayMarchingFallbackHierarchy.ReflectionProbesAndSky);

        /// <summary>
        /// Evaluates the effect at full camera resolution instead of half resolution plus a bilateral upsample.
        /// </summary>
        [Tooltip("Evaluates the effect at full camera resolution instead of half resolution plus a bilateral upsample.")]
        public BoolParameter fullResolution = new BoolParameter(true);

        /// <summary>
        /// Number of ray marching steps taken along each screen-space ray.
        /// </summary>
        [Tooltip("Number of ray marching steps taken along each screen-space ray.")]
        public ClampedIntParameter maxRaySteps = new ClampedIntParameter(32, 1, 256);

        /// <summary>
        /// Thickness of the depth buffer used to accept an intersection, as a fraction of the linear depth.
        /// </summary>
        [Tooltip("Thickness of the depth buffer used to accept an intersection, as a fraction of the linear depth.")]
        public ClampedFloatParameter depthBufferThickness = new ClampedFloatParameter(0.1f, 0.0f, 0.5f);

        /// <summary>
        /// Multiplies the screen-space contribution. Zero leaves the image unchanged.
        /// </summary>
        [Tooltip("Intensity of the screen-space contribution. Zero leaves the image unchanged.")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(1.0f, 0.0f, 4.0f);

        /// <summary>
        /// Accumulates the radiance over multiple frames using motion vectors.
        /// </summary>
        [Tooltip("Accumulates the radiance over multiple frames using motion vectors.")]
        public BoolParameter temporalAccumulation = new BoolParameter(true);

        /// <summary>
        /// Applies the bilateral diffuse denoiser after the temporal accumulation.
        /// </summary>
        [Tooltip("Applies the bilateral diffuse denoiser after the temporal accumulation.")]
        public BoolParameter denoise = new BoolParameter(true);

        /// <summary>
        /// Radius of the diffuse denoiser, relative to the pixel footprint.
        /// </summary>
        [Tooltip("Radius of the diffuse denoiser, relative to the pixel footprint.")]
        public ClampedFloatParameter denoiserRadius = new ClampedFloatParameter(0.6f, 0.001f, 1.0f);

        /// <summary>
        /// Runs a second temporal and spatial denoise pass with a low frequency history.
        /// </summary>
        [Tooltip("Runs a second denoise pass with a low frequency history. Sharper result at a higher cost.")]
        public BoolParameter secondDenoiserPass = new BoolParameter(true);

        /// <summary>
        /// Uses four denoiser samples per pixel instead of sixteen. Only meaningful when the effect runs at half resolution.
        /// </summary>
        [Tooltip("Uses four denoiser samples per pixel instead of sixteen.")]
        public BoolParameter halfResolutionDenoiser = new BoolParameter(false);

        /// <summary>
        /// Upsamples the half resolution result with a bilateral filter instead of a box filter.
        /// </summary>
        [Tooltip("Upsamples the half resolution result with a bilateral filter.")]
        public BoolParameter bilateralUpsample = new BoolParameter(true);
    }
}