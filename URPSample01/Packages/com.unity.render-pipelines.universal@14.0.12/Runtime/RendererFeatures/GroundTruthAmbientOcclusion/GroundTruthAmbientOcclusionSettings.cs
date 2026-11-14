using System;
using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    [Serializable]
    internal sealed class GroundTruthAmbientOcclusionSettings
    {
        [Header("Quality")]
        [SerializeField]
        [Tooltip("Runs the horizon search at full resolution. Higher quality and cost than the default half resolution.")]
        internal bool fullResolution;

        [SerializeField]
        [Range(2, 32)]
        [Tooltip("Steps taken along each horizon direction. Higher values find finer occluders at a higher cost.")]
        internal int stepCount = 6;

        [SerializeField]
        [Range(1, 6)]
        [Tooltip("Number of sampled directions when temporal accumulation is off.")]
        internal int directionCount = 2;

        [SerializeField]
        [Range(16, 256)]
        [Tooltip("Maximum horizon search radius in pixels at 1080p. Scaled with the render resolution.")]
        internal int maximumRadiusInPixels = 40;

        [Header("Appearance")]
        [SerializeField]
        [Range(0f, 4f)]
        [Tooltip("Occlusion strength. Zero disables the effect and leaves the image unchanged.")]
        internal float intensity = 1f;

        [SerializeField]
        [Range(0.25f, 5f)]
        [Tooltip("Sampling radius in world units. Larger radii reach distant occluders and lose fine detail.")]
        internal float radius = 2f;

        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("How much the ambient occlusion also darkens direct lighting.")]
        internal float directLightingStrength;

        [Header("Denoise")]
        [SerializeField]
        [Tooltip("Accumulates the AO over multiple frames using motion vectors. Falls back to the spatial result when disabled.")]
        internal bool temporalAccumulation = true;

        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("Accepts more or less history in the temporal filter. Lower values ghost less but keep more noise.")]
        internal float ghostingReduction = 0.5f;

        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("Sharpness of the spatial filter used without temporal accumulation.")]
        internal float blurSharpness = 0.1f;

        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("How aggressively neighbouring depths reject samples in the spatial bilateral filter.")]
        internal float spatialBilateralAggressiveness = 0.15f;

        [SerializeField]
        [Tooltip("Uses a bilateral filter when upsampling the half resolution result. Disable for a cheaper box filter.")]
        internal bool bilateralUpsample = true;

        [Header("History")]
        [SerializeField]
        [Range(1, 64)]
        [Tooltip("Maximum number of cameras whose AO history is kept before the least recently used entry is released.")]
        internal int maxCachedCameras = 8;

        [SerializeField]
        [Range(0, 10000)]
        [Tooltip("Number of frames an idle camera may keep its AO history before it is released. Zero releases idle cameras immediately.")]
        internal int maxUnusedFrames = 120;

        [Header("Debug")]
        [SerializeField]
        [Tooltip("Intermediate resource view. Off leaves the final image unchanged.")]
        internal GroundTruthAmbientOcclusionDebugMode debugMode = GroundTruthAmbientOcclusionDebugMode.Off;

        [SerializeField]
        [Range(0, PackedMipChainInfo.k_MaxMipLevelCount - 1)]
        [Tooltip("Mip level shown by the depth pyramid debug view.")]
        internal int debugMip;

        [SerializeField]
        [Tooltip("Compute shaders and debug shader used by the GTAO passes.")]
        internal GroundTruthAmbientOcclusionResources resources = new GroundTruthAmbientOcclusionResources();

        internal bool effectEnabled => intensity > 0f;

        internal bool debugEnabled => debugMode != GroundTruthAmbientOcclusionDebugMode.Off;

        internal void EnsureResources()
        {
            if (resources == null)
                resources = new GroundTruthAmbientOcclusionResources();

            if (!resources.isComplete)
                GroundTruthAmbientOcclusionResourceLocator.AssignDefaults(resources);
        }
    }
}