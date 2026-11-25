using System;
using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    [Serializable]
    internal sealed class ScreenSpaceLightingSettings
    {
        [SerializeField]
        [Range(1, 64)]
        [Tooltip("Maximum number of cameras whose SSGI/SSR history is kept before the least recently used entry is released.")]
        internal int maxCachedCameras = 8;

        [SerializeField]
        [Range(0, 10000)]
        [Tooltip("Number of frames an idle camera may keep its SSGI/SSR history before it is released. Zero releases idle cameras immediately.")]
        internal int maxUnusedFrames = 120;

        [SerializeField]
        [Tooltip("Publishes the reflection probe atlas in Forward and Deferred so the SSGI miss fallback can sample local reflection probes. Ignored in Forward+, which always publishes the atlas.")]
        internal bool publishProbeAtlas = false;

        [SerializeField, Tooltip("High Fidelity / Win64 DXR only. Resolve smooth opaque half-resolution Approximation SSR against registered scene meshes.")]
        internal bool rayTracingReflections = false;

        [SerializeField, Range(0, 32768), Tooltip("Fixed ray dispatch width per eligible camera. Zero disables hardware reflection requests.")]
        internal int reflectionRayBudget = 32768;

        [SerializeField, Tooltip("High Fidelity / Win64 DXR only. Update the scene probe cache from the shared RT scene as the SSGI diffuse fallback.")]
        internal bool rayTracingProbeFallback = false;

        // Manually placed bounded probe volume (v1 has no rolling volumes). The batch, directions and
        // activation rules stay at the B0-frozen values; only the placement is authored here.
        [SerializeField] internal Vector3 probeGridOrigin = new Vector3(-3.5f, 0.5f, -3.5f);
        [SerializeField, Min(0.01f)] internal float probeGridSpacing = 1f;
        [SerializeField] internal Vector3Int probeGridCounts = new Vector3Int(8, 4, 8);
        [SerializeField, Range(1, 256)] internal int probeBatchSize = 16;
        [SerializeField, Range(32, 512)] internal int probeRaysPerProbe = 128;
        [SerializeField] internal bool probeFeedback = true;
        [SerializeField] internal bool probeRelocation = true;
        [SerializeField] internal bool probeClassification = true;

        [SerializeField, Range(0f, 1f), Tooltip("Probe-volume coverage c applied once to lerp(bakedGI, E/pi, c). Test hook for the G3 weight sweep.")]
        internal float probeCoverage = 1f;

        [SerializeField]
        [Tooltip("Intermediate resource view. Off leaves the final image unchanged.")]
        internal ScreenSpaceLightingDebugMode debugMode = ScreenSpaceLightingDebugMode.Off;

        [SerializeField]
        [Range(0, PackedMipChainInfo.k_MaxMipLevelCount - 1)]
        [Tooltip("Mip level shown by the depth pyramid debug views. Ignored by the other debug modes.")]
        internal int debugMip;

        [SerializeField]
        [Tooltip("Compute shader and shader references used by the Screen Space Lighting passes.")]
        internal ScreenSpaceLightingResources resources = new ScreenSpaceLightingResources();

        internal bool debugEnabled => debugMode != ScreenSpaceLightingDebugMode.Off;

        internal void EnsureResources()
        {
            if (resources == null)
                resources = new ScreenSpaceLightingResources();

#if UNITY_EDITOR_WIN && UNITY_STANDALONE_WIN
            if (rayTracingReflections && resources.rayTracingSsr == null &&
                UnityEditor.EditorUserBuildSettings.activeBuildTarget == UnityEditor.BuildTarget.StandaloneWindows64)
                resources.rayTracingSsr = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Experimental.Rendering.RayTracingShader>(
                    "Packages/com.unity.render-pipelines.universal/Shaders/Utils/ScreenSpaceLighting/RayTracingSsr.raytrace");
            if (rayTracingProbeFallback && resources.rayTracingGi == null &&
                UnityEditor.EditorUserBuildSettings.activeBuildTarget == UnityEditor.BuildTarget.StandaloneWindows64)
                resources.rayTracingGi = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Experimental.Rendering.RayTracingShader>(
                    "Packages/com.unity.render-pipelines.universal/Shaders/RayTracing/GlxProbeTrace.raytrace");
#endif
            if (!resources.isComplete)
                ScreenSpaceLightingResourceLocator.AssignDefaults(resources);
        }
    }
}