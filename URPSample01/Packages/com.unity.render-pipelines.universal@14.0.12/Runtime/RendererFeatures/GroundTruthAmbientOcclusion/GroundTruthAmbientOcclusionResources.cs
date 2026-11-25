using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// GPU programs owned by a Ground Truth Ambient Occlusion feature. The references are serialized so a
    /// player build keeps the compute shaders; <see cref="ResourceReloader"/> fills empty references from
    /// the package when the renderer asset is loaded in the editor.
    /// </summary>
    [Serializable, ReloadGroup]
    internal sealed class GroundTruthAmbientOcclusionResources
    {
        [SerializeField, Reload("Shaders/Utils/GroundTruthAmbientOcclusion/GTAO.compute")]
        internal ComputeShader gtaoCompute;

        [SerializeField, Reload("Shaders/Utils/GroundTruthAmbientOcclusion/GTAOSpatialDenoise.compute")]
        internal ComputeShader spatialDenoiseCompute;

        [SerializeField, Reload("Shaders/Utils/GroundTruthAmbientOcclusion/GTAOTemporalDenoise.compute")]
        internal ComputeShader temporalDenoiseCompute;

        [SerializeField, Reload("Shaders/Utils/GroundTruthAmbientOcclusion/GTAOCopyHistory.compute")]
        internal ComputeShader copyHistoryCompute;

        [SerializeField, Reload("Shaders/Utils/GroundTruthAmbientOcclusion/GTAOBlurAndUpsample.compute")]
        internal ComputeShader blurAndUpsampleCompute;

        [SerializeField, Reload("Shaders/Utils/ScreenSpaceCommon/DepthPyramid.compute")]
        internal ComputeShader depthPyramidCompute;

        [SerializeField, Reload("Shaders/Utils/GroundTruthAmbientOcclusion/GTAODebug.shader")]
        internal Shader debugShader;

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
#if UNITY_EDITOR
            if (resources != null)
                ResourceReloader.TryReloadAllNullIn(resources, UniversalRenderPipelineAsset.packagePath);
#endif
        }
    }
}
