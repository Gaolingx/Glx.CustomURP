using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    // Writes one private GTAO resource to the camera color target for verification. The pass is only
    // enqueued while a debug mode is selected and never changes material lighting.
    internal sealed class GroundTruthAmbientOcclusionDebugPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingPostProcessing + 2);
        internal const string k_ProfilingSamplerName = "GTAO Debug View";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

        GroundTruthAmbientOcclusionHistory m_History;
        GroundTruthAmbientOcclusionSettings m_Settings;
        readonly MaterialPropertyBlock m_PropertyBlock = new MaterialPropertyBlock();

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
#endif

        internal GroundTruthAmbientOcclusionDebugPass()
        {
            renderPassEvent = k_RenderPassEvent;
        }

        internal void Setup(GroundTruthAmbientOcclusionHistory history, GroundTruthAmbientOcclusionSettings settings)
        {
            m_History = history;
            m_Settings = settings;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            s_EditorExecuteCount++;
#endif
            if (m_History == null || m_Settings == null || !m_Settings.debugEnabled)
                return;

            var material = m_Settings.resources != null ? m_Settings.resources.debugMaterial : null;
            var colorTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
            if (material == null || colorTarget == null || !m_History.resourcesAllocated)
                return;

            if (!TryGetDebugSource(out var texture, out var uvScaleOffset, out var unpack, out var useGlobal))
                return;

            m_PropertyBlock.SetTexture(GroundTruthAmbientOcclusionShaderIDs.DebugTexture, texture);
            m_PropertyBlock.SetVector(GroundTruthAmbientOcclusionShaderIDs.DebugUvScaleOffset, uvScaleOffset);
            m_PropertyBlock.SetInt(GroundTruthAmbientOcclusionShaderIDs.DebugUnpack, unpack ? 1 : 0);
            m_PropertyBlock.SetInt(GroundTruthAmbientOcclusionShaderIDs.DebugUseGlobal, useGlobal ? 1 : 0);

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                CoreUtils.SetRenderTarget(cmd, colorTarget, ClearFlag.None, 0, CubemapFace.Unknown, -1);
                cmd.SetViewport(new Rect(0f, 0f, m_History.width, m_History.height));
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, m_PropertyBlock);
            }
        }

        bool TryGetDebugSource(out Texture texture, out Vector4 uvScaleOffset, out bool unpack, out bool useGlobal)
        {
            texture = null;
            uvScaleOffset = new Vector4(1f, 1f, 0f, 0f);
            unpack = false;
            useGlobal = false;

            switch (m_Settings.debugMode)
            {
                case GroundTruthAmbientOcclusionDebugMode.AmbientOcclusion:
                    if (m_History.occlusionTexture == null)
                        return false;
                    texture = m_History.occlusionTexture.rt;
                    return true;

                case GroundTruthAmbientOcclusionDebugMode.RawAmbientOcclusion:
                    if (m_History.packedData == null)
                        return false;
                    texture = m_History.packedData.rt;
                    unpack = true;
                    return true;

                case GroundTruthAmbientOcclusionDebugMode.GlobalAmbientOcclusion:
                    // Samples the material facing global instead of the private handle.
                    useGlobal = true;
                    texture = m_History.occlusionTexture.rt;
                    return true;

                case GroundTruthAmbientOcclusionDebugMode.DepthPyramid:
                {
                    var info = m_History.depthPyramidInfo;
                    if (!info.IsValid() || m_History.depthPyramid == null)
                        return false;

                    int mip = Mathf.Clamp(m_Settings.debugMip, 0, info.mipLevelCount - 1);
                    uvScaleOffset = info.ComputeMipUvScaleOffset(mip);
                    texture = m_History.depthPyramid.rt;
                    return true;
                }

                default:
                    return false;
            }
        }
    }
}