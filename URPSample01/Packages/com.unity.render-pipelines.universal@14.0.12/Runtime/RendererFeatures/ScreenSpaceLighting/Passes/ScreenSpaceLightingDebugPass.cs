using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    // Writes one private Screen Space Lighting resource to the camera color target for verification.
    // The pass is only enqueued while a debug mode is selected and never touches material lighting.
    internal sealed class ScreenSpaceLightingDebugPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingPostProcessing + 1);
        internal const string k_ProfilingSamplerName = "SSL Debug View";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

        ScreenSpaceLightingHistory m_History;
        ScreenSpaceLightingSettings m_Settings;
        readonly MaterialPropertyBlock m_PropertyBlock = new MaterialPropertyBlock();

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
#endif

        internal ScreenSpaceLightingDebugPass()
        {
            renderPassEvent = k_RenderPassEvent;
        }

        internal void Setup(ScreenSpaceLightingHistory history, ScreenSpaceLightingSettings settings)
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
            if (material == null || colorTarget == null)
                return;

            int materialPass = m_Settings.debugMode == ScreenSpaceLightingDebugMode.CameraDepth ? 1 : 0;
            if (materialPass == 0)
            {
                if (!TryGetDebugSource(out var texture, out var uvScaleOffset, out var mipLevel, out var outputMode))
                    return;

                m_PropertyBlock.SetTexture(ScreenSpaceLightingShaderIDs.DebugTexture, texture);
                m_PropertyBlock.SetVector(ScreenSpaceLightingShaderIDs.DebugUvScaleOffset, uvScaleOffset);
                m_PropertyBlock.SetFloat(ScreenSpaceLightingShaderIDs.DebugMip, mipLevel);
                m_PropertyBlock.SetInt(ScreenSpaceLightingShaderIDs.DebugOutput, outputMode);
            }

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                CoreUtils.SetRenderTarget(cmd, colorTarget, ClearFlag.None, 0, CubemapFace.Unknown, -1);
                cmd.SetViewport(new Rect(0f, 0f, m_History.width, m_History.height));
                cmd.DrawProcedural(Matrix4x4.identity, material, materialPass, MeshTopology.Triangles, 3, 1, m_PropertyBlock);
            }
        }

        bool TryGetDebugSource(out Texture texture, out Vector4 uvScaleOffset, out float mipLevel, out int outputMode)
        {
            texture = null;
            uvScaleOffset = new Vector4(1f, 1f, 0f, 0f);
            mipLevel = 0f;
            outputMode = 0;

            switch (m_Settings.debugMode)
            {
                case ScreenSpaceLightingDebugMode.DepthPyramid:
                case ScreenSpaceLightingDebugMode.DepthPyramidUv:
                {
                    var info = m_History.depthPyramidInfo;
                    if (!info.IsValid() || m_History.depthPyramid == null)
                        return false;

                    int mip = Mathf.Clamp(m_Settings.debugMip, 0, info.mipLevelCount - 1);
                    uvScaleOffset = info.ComputeMipUvScaleOffset(mip);
                    // The packed atlas has no mip chain; the level is selected through the uv remap.
                    mipLevel = 0f;
                    outputMode = m_Settings.debugMode == ScreenSpaceLightingDebugMode.DepthPyramidUv ? 2 : 0;
                    texture = m_History.depthPyramid.rt;
                    return true;
                }
                case ScreenSpaceLightingDebugMode.CurrentColorPyramid:
                {
                    if (m_History.currentColorPyramid == null)
                        return false;
                    mipLevel = Mathf.Clamp(m_Settings.debugMip, 0, Mathf.Max(0, m_History.colorPyramidMipCount - 1));
                    outputMode = 1;
                    texture = m_History.currentColorPyramid.rt;
                    return true;
                }
                case ScreenSpaceLightingDebugMode.PreviousColor:
                {
                    if (m_History.readColorPyramid == null)
                        return false;
                    mipLevel = Mathf.Clamp(m_Settings.debugMip, 0, Mathf.Max(0, m_History.colorPyramidMipCount - 1));
                    outputMode = 1;
                    texture = m_History.readColorPyramid.rt;
                    return true;
                }
                case ScreenSpaceLightingDebugMode.PreviousDepth:
                {
                    if (m_History.readPreviousDepth == null)
                        return false;
                    outputMode = 0;
                    texture = m_History.readPreviousDepth.rt;
                    return true;
                }
                case ScreenSpaceLightingDebugMode.CameraDepth:
                    // Drawn by pass 1 with the URP global depth texture.
                    return true;
                default:
                    return false;
            }
        }
    }
}