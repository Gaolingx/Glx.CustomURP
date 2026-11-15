using System;
using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// GPU programs owned by a Screen Space Lighting feature. The references are serialized so a player
    /// build keeps the compute shader and materials; the feature editor fills package defaults when empty.
    /// </summary>
    [Serializable]
    internal sealed class ScreenSpaceLightingResources
    {
        [SerializeField] internal ComputeShader depthPyramidCompute;
        [SerializeField] internal Shader colorPyramidShader;
        [SerializeField] internal Shader debugShader;

        [SerializeField] internal ComputeShader ssgiCompute;
        [SerializeField] internal ComputeShader ssgiTemporalFilterCompute;
        [SerializeField] internal ComputeShader ssgiDiffuseDenoiserCompute;
        [SerializeField] internal ComputeShader bilateralUpsampleCompute;
        [SerializeField] internal ComputeShader ssrCompute;
        [SerializeField] internal ComputeShader ssrReflectionDenoiserCompute;

        // Seeds the private transparent depth target from the opaque camera depth. The URP
        // CopyDepth shader is reused so the _OUTPUT_DEPTH (SV_Depth) path is identical.
        [SerializeField] internal Shader transparentDepthCopyShader;

        [NonSerialized] Material m_ColorPyramidMaterial;
        [NonSerialized] Material m_DebugMaterial;
        [NonSerialized] Material m_TransparentDepthCopyMaterial;
        [NonSerialized] bool m_SsgiBlueNoiseResolved;
        [NonSerialized] Texture2D m_SsgiOwenScrambledTexture;
        [NonSerialized] Texture2D m_SsgiScramblingTile;
        [NonSerialized] Texture2D m_SsgiRankingTile;
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

        void ResolveSsgiBlueNoise()
        {
            if (m_SsgiBlueNoiseResolved)
                return;

            m_SsgiBlueNoiseResolved = true;
            m_SsgiOwenScrambledTexture = Resources.Load<Texture2D>("SsgiBlueNoise/OwenScrambledNoise256");
            m_SsgiScramblingTile = Resources.Load<Texture2D>("SsgiBlueNoise/ScramblingTile8SPP");
            m_SsgiRankingTile = Resources.Load<Texture2D>("SsgiBlueNoise/RankingTile8SPP");
        }

        // HDRP's ReflectionKernelMapping LUT scales the reflection filter radius by (NoV, roughness).
        // It is resolved from the package Resources folder so a player build keeps it; until the asset
        // is present the kernel receives a white texture, which collapses the radius scale to 1 and
        // therefore to a fixed radius filter.
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
        }
    }

    internal static class ScreenSpaceLightingResourceLocator
    {
        internal static void AssignDefaults(ScreenSpaceLightingResources resources)
        {
            if (resources == null)
                return;

#if UNITY_EDITOR
            if (resources.depthPyramidCompute == null)
            {
                resources.depthPyramidCompute = FindPackageAsset<ComputeShader>(
                    "DepthPyramid t:ComputeShader",
                    "/ScreenSpaceCommon/DepthPyramid.compute");
            }

            if (resources.colorPyramidShader == null)
            {
                resources.colorPyramidShader = FindPackageAsset<Shader>(
                    "ColorPyramid t:Shader",
                    "/ScreenSpaceLighting/ColorPyramid.shader");
            }

            if (resources.debugShader == null)
            {
                resources.debugShader = FindPackageAsset<Shader>(
                    "ScreenSpaceLightingDebug t:Shader",
                    "/ScreenSpaceLighting/ScreenSpaceLightingDebug.shader");
            }

            if (resources.ssgiCompute == null)
            {
                resources.ssgiCompute = FindPackageAsset<ComputeShader>(
                    "ScreenSpaceGlobalIllumination t:ComputeShader",
                    "/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute");
            }

            if (resources.ssgiTemporalFilterCompute == null)
            {
                resources.ssgiTemporalFilterCompute = FindPackageAsset<ComputeShader>(
                    "TemporalFilter t:ComputeShader",
                    "/Denoising/TemporalFilter.compute");
            }

            if (resources.ssgiDiffuseDenoiserCompute == null)
            {
                resources.ssgiDiffuseDenoiserCompute = FindPackageAsset<ComputeShader>(
                    "DiffuseDenoiser t:ComputeShader",
                    "/Denoising/DiffuseDenoiser.compute");
            }

            if (resources.bilateralUpsampleCompute == null)
            {
                resources.bilateralUpsampleCompute = FindPackageAsset<ComputeShader>(
                    "BilateralUpsample t:ComputeShader",
                    "/ScreenSpaceCommon/BilateralUpsample.compute");
            }

            if (resources.ssrCompute == null)
            {
                resources.ssrCompute = FindPackageAsset<ComputeShader>(
                    "ScreenSpaceReflections t:ComputeShader",
                    "/ScreenSpaceLighting/ScreenSpaceReflections.compute");
            }

            if (resources.ssrReflectionDenoiserCompute == null)
            {
                resources.ssrReflectionDenoiserCompute = FindPackageAsset<ComputeShader>(
                    "ReflectionDenoiser t:ComputeShader",
                    "/Denoising/ReflectionDenoiser.compute");
            }

            if (resources.transparentDepthCopyShader == null)
            {
                resources.transparentDepthCopyShader = FindPackageAsset<Shader>(
                    "CopyDepth t:Shader",
                    "/Shaders/Utils/CopyDepth.shader");
            }
#endif
        }

#if UNITY_EDITOR
        static T FindPackageAsset<T>(string filter, string pathSuffix) where T : UnityEngine.Object
        {
            foreach (var guid in UnityEditor.AssetDatabase.FindAssets(filter, new[] { "Packages" }))
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (path.Replace('\\', '/').EndsWith(pathSuffix, StringComparison.Ordinal))
                    return UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);
            }

            return null;
        }
#endif
    }
}