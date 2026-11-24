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
#endif
            if (!resources.isComplete)
                ScreenSpaceLightingResourceLocator.AssignDefaults(resources);
        }
    }
}