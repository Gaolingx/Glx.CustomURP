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

        [NonSerialized] Material m_ColorPyramidMaterial;
        [NonSerialized] Material m_DebugMaterial;

        // The depth pyramid and color pyramid are the T4 contract; SSGI resources are optional so a
        // renderer that only uses the color history keeps working.
        internal bool hasRuntimeResources => depthPyramidCompute != null && colorPyramidShader != null;

        internal bool hasSsgiResources =>
            ssgiCompute != null &&
            ssgiTemporalFilterCompute != null &&
            ssgiDiffuseDenoiserCompute != null &&
            bilateralUpsampleCompute != null;

        internal bool hasSsrResources => ssrCompute != null;

        // The debug view is optional at runtime; the feature editor and tests require the full set.
        internal bool isComplete => hasRuntimeResources && hasSsgiResources && hasSsrResources && debugShader != null;

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

        internal void DisposeMaterials()
        {
            CoreUtils.Destroy(m_ColorPyramidMaterial);
            m_ColorPyramidMaterial = null;
            CoreUtils.Destroy(m_DebugMaterial);
            m_DebugMaterial = null;
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
                    "/ScreenSpaceLighting/TemporalFilter.compute");
            }

            if (resources.ssgiDiffuseDenoiserCompute == null)
            {
                resources.ssgiDiffuseDenoiserCompute = FindPackageAsset<ComputeShader>(
                    "DiffuseDenoiser t:ComputeShader",
                    "/ScreenSpaceLighting/DiffuseDenoiser.compute");
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