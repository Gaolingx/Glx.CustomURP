using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// GPU programs owned by a Ground Truth Ambient Occlusion feature. The references are serialized so a
    /// player build keeps the compute shaders; the feature editor fills package defaults when empty.
    /// </summary>
    [Serializable]
    internal sealed class GroundTruthAmbientOcclusionResources
    {
        [SerializeField] internal ComputeShader gtaoCompute;
        [SerializeField] internal ComputeShader spatialDenoiseCompute;
        [SerializeField] internal ComputeShader temporalDenoiseCompute;
        [SerializeField] internal ComputeShader copyHistoryCompute;
        [SerializeField] internal ComputeShader blurAndUpsampleCompute;
        [SerializeField] internal ComputeShader depthPyramidCompute;
        [SerializeField] internal Shader debugShader;

        [NonSerialized] Material m_DebugMaterial;

        internal bool hasRuntimeResources =>
            gtaoCompute != null &&
            spatialDenoiseCompute != null &&
            temporalDenoiseCompute != null &&
            copyHistoryCompute != null &&
            blurAndUpsampleCompute != null &&
            depthPyramidCompute != null;

        // The debug view is optional at runtime; the feature editor and tests require the full set.
        internal bool isComplete => hasRuntimeResources && debugShader != null;

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
            CoreUtils.Destroy(m_DebugMaterial);
            m_DebugMaterial = null;
        }
    }

    internal static class GroundTruthAmbientOcclusionResourceLocator
    {
        internal static void AssignDefaults(GroundTruthAmbientOcclusionResources resources)
        {
            if (resources == null)
                return;

#if UNITY_EDITOR
            if (resources.gtaoCompute == null)
            {
                resources.gtaoCompute = FindPackageAsset<ComputeShader>(
                    "GTAO t:ComputeShader",
                    "/GroundTruthAmbientOcclusion/GTAO.compute");
            }

            if (resources.spatialDenoiseCompute == null)
            {
                resources.spatialDenoiseCompute = FindPackageAsset<ComputeShader>(
                    "GTAOSpatialDenoise t:ComputeShader",
                    "/GroundTruthAmbientOcclusion/GTAOSpatialDenoise.compute");
            }

            if (resources.temporalDenoiseCompute == null)
            {
                resources.temporalDenoiseCompute = FindPackageAsset<ComputeShader>(
                    "GTAOTemporalDenoise t:ComputeShader",
                    "/GroundTruthAmbientOcclusion/GTAOTemporalDenoise.compute");
            }

            if (resources.copyHistoryCompute == null)
            {
                resources.copyHistoryCompute = FindPackageAsset<ComputeShader>(
                    "GTAOCopyHistory t:ComputeShader",
                    "/GroundTruthAmbientOcclusion/GTAOCopyHistory.compute");
            }

            if (resources.blurAndUpsampleCompute == null)
            {
                resources.blurAndUpsampleCompute = FindPackageAsset<ComputeShader>(
                    "GTAOBlurAndUpsample t:ComputeShader",
                    "/GroundTruthAmbientOcclusion/GTAOBlurAndUpsample.compute");
            }

            if (resources.depthPyramidCompute == null)
            {
                resources.depthPyramidCompute = FindPackageAsset<ComputeShader>(
                    "DepthPyramid t:ComputeShader",
                    "/ScreenSpaceCommon/DepthPyramid.compute");
            }

            if (resources.debugShader == null)
            {
                resources.debugShader = FindPackageAsset<Shader>(
                    "GTAODebug t:Shader",
                    "/GroundTruthAmbientOcclusion/GTAODebug.shader");
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