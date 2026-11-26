using System;
using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// GPU programs owned by a Screen Space Lighting feature. The references are serialized so a player
    /// build keeps the compute shader and materials; the feature editor fills package defaults when empty.
    /// </summary>
    [Serializable, ReloadGroup]
    internal sealed class ScreenSpaceLightingResources
    {
        [SerializeField, Reload("Shaders/Utils/ScreenSpaceCommon/DepthPyramid.compute")]
        internal ComputeShader depthPyramidCompute;

        [SerializeField, Reload("Shaders/Utils/ScreenSpaceLighting/ColorPyramid.shader")]
        internal Shader colorPyramidShader;

        [SerializeField, Reload("Shaders/Utils/ScreenSpaceLighting/ScreenSpaceLightingDebug.shader")]
        internal Shader debugShader;

        [SerializeField, Reload("Shaders/Utils/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute")]
        internal ComputeShader ssgiCompute;

        [SerializeField, Reload("Shaders/Utils/Denoising/TemporalFilter.compute")]
        internal ComputeShader ssgiTemporalFilterCompute;

        [SerializeField, Reload("Shaders/Utils/Denoising/DiffuseDenoiser.compute")]
        internal ComputeShader ssgiDiffuseDenoiserCompute;

        [SerializeField, Reload("Shaders/Utils/ScreenSpaceCommon/BilateralUpsample.compute")]
        internal ComputeShader bilateralUpsampleCompute;

        [SerializeField, Reload("Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute")]
        internal ComputeShader ssrCompute;

        [SerializeField, Reload("Shaders/Utils/ScreenSpaceLighting/SsrHitDeferred.compute")]
        internal ComputeShader ssrHitDeferredCompute;

        [SerializeField, Reload("Shaders/Utils/Denoising/ReflectionDenoiser.compute")]
        internal ComputeShader ssrReflectionDenoiserCompute;

        // Not in Resources, and no serialized reference in non-Windows players.
#if UNITY_STANDALONE_WIN && (UNITY_EDITOR || UNITY_64)
        [SerializeField] internal UnityEngine.Experimental.Rendering.RayTracingShader rayTracingSsr;
        [SerializeField] internal UnityEngine.Experimental.Rendering.RayTracingShader rayTracingGi;
#else
        internal UnityEngine.Experimental.Rendering.RayTracingShader rayTracingSsr => null;
        internal UnityEngine.Experimental.Rendering.RayTracingShader rayTracingGi => null;
#endif

        [SerializeField, Reload("Shaders/RayTracing/GlxProbeUpdate.compute")]
        internal ComputeShader giProbeUpdateCompute;

        // Seeds the private transparent depth target from the opaque camera depth. The URP
        // CopyDepth shader is reused so the _OUTPUT_DEPTH (SV_Depth) path is identical.
        [SerializeField, Reload("Shaders/Utils/CopyDepth.shader")]
        internal Shader transparentDepthCopyShader;

        [NonSerialized] Material m_ColorPyramidMaterial;
        [NonSerialized] Material m_DebugMaterial;
        [NonSerialized] Material m_TransparentDepthCopyMaterial;
        [NonSerialized] ComputeShader m_SsrTransparentCompute;
        [NonSerialized] bool m_SsgiBlueNoiseResolved;
        [NonSerialized] Texture2D m_SsgiOwenScrambledTexture;
        [NonSerialized] Texture2D m_SsgiScramblingTile;
        [NonSerialized] Texture2D m_SsgiRankingTile;
        [NonSerialized] bool m_SsrBlueNoiseResolved;
        [NonSerialized] Texture2D m_SsrScramblingTile;
        [NonSerialized] Texture2D m_SsrRankingTile;
        [NonSerialized] bool m_SsrFilterMappingResolved;
        [NonSerialized] Texture2D m_SsrReflectionFilterMapping;

        // The three LUTs of HDRP's blue-noise distributed Owen scrambled Sobol sequence (BND). They live
        // in a Resources folder of the package so every player build has them without having to
        // re-serialize the renderer asset; the kernels fall back to IGN + R2 when they are missing.
        internal Texture2D ssgiOwenScrambledTexture
        {
            get { ResolveSsgiBlueNoise(); return m_SsgiOwenScrambledTexture; }
        }

        internal Texture2D ssgiScramblingTile
        {
            get { ResolveSsgiBlueNoise(); return m_SsgiScramblingTile; }
        }

        internal Texture2D ssgiRankingTile
        {
            get { ResolveSsgiBlueNoise(); return m_SsgiRankingTile; }
        }

        internal bool hasSsgiBlueNoise =>
            ssgiOwenScrambledTexture != null && ssgiScramblingTile != null && ssgiRankingTile != null;

        // HDRP binds the 1SPP tiles to the screen space SSR trace and the 8SPP tiles to SSGI; the
        // Owen scrambled 256 texture is shared. Keeping both sets means each effect matches the
        // stratification HDRP uses for the same number of samples per pixel.
        internal Texture2D ssrScramblingTile
        {
            get { ResolveSsrBlueNoise(); return m_SsrScramblingTile; }
        }

        internal Texture2D ssrRankingTile
        {
            get { ResolveSsrBlueNoise(); return m_SsrRankingTile; }
        }

        internal bool hasSsrBlueNoise =>
            ssgiOwenScrambledTexture != null && ssrScramblingTile != null && ssrRankingTile != null;

        void ResolveSsrBlueNoise()
        {
            if (m_SsrBlueNoiseResolved)
                return;

            m_SsrBlueNoiseResolved = true;
            m_SsrScramblingTile = Resources.Load<Texture2D>("SsgiBlueNoise/ScramblingTile1SPP");
            m_SsrRankingTile = Resources.Load<Texture2D>("SsgiBlueNoise/RankingTile1SPP");
        }

        void ResolveSsgiBlueNoise()
        {
            if (m_SsgiBlueNoiseResolved)
                return;

            m_SsgiBlueNoiseResolved = true;
            m_SsgiOwenScrambledTexture = Resources.Load<Texture2D>("SsgiBlueNoise/OwenScrambledNoise256");
            m_SsgiScramblingTile = Resources.Load<Texture2D>("SsgiBlueNoise/ScramblingTile8SPP");
            m_SsgiRankingTile = Resources.Load<Texture2D>("SsgiBlueNoise/RankingTile8SPP");
        }

        // HDRP's ReflectionKernelMapping LUT scales the reflection filter radius by (NoV, roughness). The
        // asset is copied with HDRP's import settings (sRGB, mipmaps) because that is how HDRP samples
        // it, so the radius scale matches. It is resolved from the package Resources folder so a
        // player build keeps it; if the asset is stripped the kernel receives a white texture, which
        // collapses the radius scale to 1 and therefore to a fixed radius filter.
        internal Texture2D ssrReflectionFilterMapping
        {
            get
            {
                if (!m_SsrFilterMappingResolved)
                {
                    m_SsrFilterMappingResolved = true;
                    m_SsrReflectionFilterMapping = Resources.Load<Texture2D>("SsrDenoise/ReflectionKernelMapping");
                }

                return m_SsrReflectionFilterMapping != null ? m_SsrReflectionFilterMapping : Texture2D.whiteTexture;
            }
        }

        // The depth pyramid and color pyramid are the T4 contract; SSGI resources are optional so a
        // renderer that only uses the color history keeps working.
        internal bool hasRuntimeResources => depthPyramidCompute != null && colorPyramidShader != null;

        internal bool hasSsgiResources =>
            ssgiCompute != null &&
            ssgiTemporalFilterCompute != null &&
            ssgiDiffuseDenoiserCompute != null &&
            bilateralUpsampleCompute != null;

        internal bool hasSsrResources => ssrCompute != null && ssrReflectionDenoiserCompute != null;

        // Transparent SSR additionally needs the depth copy shader to seed the private depth target.
        internal bool hasTransparentSsrResources => ssrCompute != null && transparentDepthCopyShader != null;

        // ComputeShader keywords are asset state resolved when the queued dispatch executes, not
        // per command. The opaque and transparent SSR passes need different variants (PBR vs
        // Approximation, full vs half resolution) in the same frame, so the transparent pass owns a
        // separate instance; sharing the asset would let whichever pass sets keywords last decide
        // the variant for both.
        internal ComputeShader ssrTransparentCompute
        {
            get
            {
                if (m_SsrTransparentCompute == null && ssrCompute != null)
                {
                    m_SsrTransparentCompute = UnityEngine.Object.Instantiate(ssrCompute);
                    m_SsrTransparentCompute.name = ssrCompute.name + " (Transparent SSR)";
                }

                return m_SsrTransparentCompute;
            }
        }

        // The debug view is optional at runtime; the feature editor and tests require the full set.
        internal bool isComplete =>
            hasRuntimeResources && hasSsgiResources && hasSsrResources &&
            transparentDepthCopyShader != null && debugShader != null;

        internal Material colorPyramidMaterial
        {
            get
            {
                if (m_ColorPyramidMaterial == null && colorPyramidShader != null)
                    m_ColorPyramidMaterial = CoreUtils.CreateEngineMaterial(colorPyramidShader);
                return m_ColorPyramidMaterial;
            }
        }

        internal Material debugMaterial
        {
            get
            {
                if (m_DebugMaterial == null && debugShader != null)
                    m_DebugMaterial = CoreUtils.CreateEngineMaterial(debugShader);
                return m_DebugMaterial;
            }
        }

        internal Material transparentDepthCopyMaterial
        {
            get
            {
                if (m_TransparentDepthCopyMaterial == null && transparentDepthCopyShader != null)
                    m_TransparentDepthCopyMaterial = CoreUtils.CreateEngineMaterial(transparentDepthCopyShader);
                return m_TransparentDepthCopyMaterial;
            }
        }

        internal void DisposeMaterials()
        {
            CoreUtils.Destroy(m_ColorPyramidMaterial);
            m_ColorPyramidMaterial = null;
            CoreUtils.Destroy(m_DebugMaterial);
            m_DebugMaterial = null;
            CoreUtils.Destroy(m_TransparentDepthCopyMaterial);
            m_TransparentDepthCopyMaterial = null;
            CoreUtils.Destroy(m_SsrTransparentCompute);
            m_SsrTransparentCompute = null;
        }
    }

    internal static class ScreenSpaceLightingResourceLocator
    {
        internal static void AssignDefaults(ScreenSpaceLightingResources resources)
        {
#if UNITY_EDITOR
            if (resources != null)
                ResourceReloader.TryReloadAllNullIn(resources, UniversalRenderPipelineAsset.packagePath);
#endif
        }
    }
}