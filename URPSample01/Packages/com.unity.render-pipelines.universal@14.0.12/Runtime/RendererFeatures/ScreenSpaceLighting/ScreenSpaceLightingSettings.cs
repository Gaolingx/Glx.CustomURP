using System;
using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    // P3 environment source for the deferred indirect-specular composite (D1). A1 samples the
    // per-camera _GlossyEnvironmentCubeMap (the reflection/sky fallback URP binds every camera); A2
    // reuses the published GI probe atlas when the probe fallback runs; A3 uses the single ambient
    // irradiance. See P3-PLAN.md §2 (D1) for the B0 selection record.
    internal enum SsrIndirectSource
    {
        A1 = 0,
        A2 = 1,
        A3 = 2
    }

    // How the hardware traced reflection path resolves its rays. RayTracedOnly matches HDRP's
    // RayTracing mode; ScreenSpaceFirst mirrors HDRP's Mixed tracing: the screen space march runs
    // first and only the pixels it cannot resolve trace hardware rays.
    internal enum SsrTracingMode
    {
        RayTracedOnly = 0,
        ScreenSpaceFirst = 1
    }

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
        [Tooltip("Publishes the reflection probe atlas in Forward and Deferred so the SSGI miss fallback can sample local reflection probes. Ignored in Forward+, which always publishes the atlas. Enabling it allocates and updates the atlas, and the sky part of Reflection Probes and Sky becomes the ambient probe.")]
        internal bool publishProbeAtlas = false;

        [SerializeField, Tooltip("Trace opaque reflections against registered scene meshes instead of the screen space march, at full or half resolution. Requires hardware ray tracing support. When enabled, rayTracingSamples rays are traced per pixel per frame and the volume algorithm only selects the screen-space fallback used when the device cannot trace.")]
        internal bool rayTracingReflections = false;

        [SerializeField, Range(1, 32), Tooltip("Ray-traced reflection samples per pixel per frame. Every trace pixel is ray traced each frame; the reflection denoiser accumulates the samples across frames.")]
        internal int rayTracingSamples = 1;

        [SerializeField, Tooltip("RayTracedOnly traces every pixel in hardware. ScreenSpaceFirst marches the previous frame color pyramid first and only traces hardware rays for the pixels the march cannot resolve (mirrors HDRP's Mixed tracing). ScreenSpaceFirst needs the previous color pyramid and motion vectors; on a frame without them it falls back to RayTracedOnly.")]
        internal SsrTracingMode ssrTracingMode = SsrTracingMode.RayTracedOnly;

        [SerializeField, Tooltip("Environment source mixed with SSR in the deferred indirect-specular composite. A1 samples _GlossyEnvironmentCubeMap (default); A2 uses the published GI probe atlas when the probe fallback runs (directional irradiance, no roughness mips; probe coverage blends it over the cubemap); A3 uses the ambient irradiance stand-in.")]
        internal SsrIndirectSource ssrIndirectSource = SsrIndirectSource.A1;

        [SerializeField, Tooltip("Update the scene probe cache from the shared RT scene as the first SSGI miss tier (DDGI, before reflection probes and sky). Requires hardware ray tracing support.")]
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

        [SerializeField, Range(0f, 1f), Tooltip("Probe-volume coverage c, applied by exactly one owner: the SSGI DDGI miss tier when SSGI output is bound, otherwise the material lerp(bakedGI, E/pi, c). Test hook for the G3 weight sweep.")]
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
            if (rayTracingReflections && resources.rayTracingSsr == null)
                resources.rayTracingSsr = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Experimental.Rendering.RayTracingShader>(
                    "Packages/com.unity.render-pipelines.universal/Shaders/Utils/ScreenSpaceLighting/RayTracingSsr.raytrace");
            if (rayTracingProbeFallback && resources.rayTracingGi == null)
                resources.rayTracingGi = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Experimental.Rendering.RayTracingShader>(
                    "Packages/com.unity.render-pipelines.universal/Shaders/RayTracing/GlxProbeTrace.raytrace");
#endif
            if (!resources.isComplete)
                ScreenSpaceLightingResourceLocator.AssignDefaults(resources);
        }
    }
}