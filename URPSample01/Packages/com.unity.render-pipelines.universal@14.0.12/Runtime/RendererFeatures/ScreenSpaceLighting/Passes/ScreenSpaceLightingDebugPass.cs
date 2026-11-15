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
        ScreenSpaceLightingSsgiPass m_SsgiPass;
        ScreenSpaceLightingSsrPass m_SsrPass;
        ScreenSpaceLightingTransparentDepthNormalsPass m_TransparentDepthNormalsPass;
        ScreenSpaceLightingTransparentSsrPass m_TransparentSsrPass;
        readonly MaterialPropertyBlock m_PropertyBlock = new MaterialPropertyBlock();

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
#endif

        internal ScreenSpaceLightingDebugPass()
        {
            renderPassEvent = k_RenderPassEvent;
        }

        internal void Setup(
            ScreenSpaceLightingHistory history,
            ScreenSpaceLightingSettings settings,
            ScreenSpaceLightingSsgiPass ssgiPass,
            ScreenSpaceLightingSsrPass ssrPass,
            ScreenSpaceLightingTransparentDepthNormalsPass transparentDepthNormalsPass,
            ScreenSpaceLightingTransparentSsrPass transparentSsrPass)
        {
            m_History = history;
            m_Settings = settings;
            m_SsgiPass = ssgiPass;
            m_SsrPass = ssrPass;
            m_TransparentDepthNormalsPass = transparentDepthNormalsPass;
            m_TransparentSsrPass = transparentSsrPass;
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
            bool foveationDisabled = ScreenSpaceLightingXr.DisableFoveatedRendering(cmd, renderingData.cameraData);
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                CoreUtils.SetRenderTarget(cmd, colorTarget, ClearFlag.None, 0, CubemapFace.Unknown, -1);
                cmd.SetViewport(new Rect(0f, 0f, m_History.width, m_History.height));
                cmd.DrawProcedural(Matrix4x4.identity, material, materialPass, MeshTopology.Triangles, 3, 1, m_PropertyBlock);
            }

            ScreenSpaceLightingXr.RestoreFoveatedRendering(cmd, foveationDisabled);
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
                case ScreenSpaceLightingDebugMode.SsgiWeight:
                {
                    if (m_SsgiPass == null || !m_SsgiPass.producedOutput || m_SsgiPass.finalTexture == null || m_SsgiPass.finalTexture.rt == null)
                        return false;
                    outputMode = 3;
                    texture = m_SsgiPass.finalTexture.rt;
                    return true;
                }
                case ScreenSpaceLightingDebugMode.CameraDepth:
                    // Drawn by pass 1 with the URP global depth texture.
                    return true;
                case ScreenSpaceLightingDebugMode.SsgiRaw:
                {
                    if (m_SsgiPass == null || !m_SsgiPass.producedOutput || m_SsgiPass.rawTexture == null || m_SsgiPass.rawTexture.rt == null)
                        return false;
                    outputMode = 1;
                    texture = m_SsgiPass.rawTexture.rt;
                    return true;
                }
                case ScreenSpaceLightingDebugMode.SsgiDenoised:
                {
                    if (m_SsgiPass == null || !m_SsgiPass.producedOutput || m_SsgiPass.finalTexture == null || m_SsgiPass.finalTexture.rt == null)
                        return false;
                    outputMode = 1;
                    texture = m_SsgiPass.finalTexture.rt;
                    return true;
                }
                case ScreenSpaceLightingDebugMode.SsgiHistory:
                {
                    var source = m_SsgiPass != null ? (m_SsgiPass.historyTexture ?? m_SsgiPass.accumulationTexture) : null;
                    if (m_SsgiPass == null || !m_SsgiPass.producedOutput || source == null || source.rt == null)
                        return false;
                    outputMode = 1;
                    texture = source.rt;
                    return true;
                }
                // The PBR speed rejection no longer exists, so the deprecated mode shows SsrRaw.
                case ScreenSpaceLightingDebugMode.SsrRaw:
                case ScreenSpaceLightingDebugMode.SsrAccumulationSpeed:
                {
                    if (m_SsrPass == null || !m_SsrPass.producedOutput || m_SsrPass.outputTexture == null || m_SsrPass.outputTexture.rt == null)
                        return false;
                    outputMode = 1;
                    texture = m_SsrPass.outputTexture.rt;
                    return true;
                }
                case ScreenSpaceLightingDebugMode.SsrWeight:
                {
                    if (m_SsrPass == null || !m_SsrPass.producedOutput || m_SsrPass.outputTexture == null || m_SsrPass.outputTexture.rt == null)
                        return false;
                    outputMode = 3;
                    texture = m_SsrPass.outputTexture.rt;
                    return true;
                }
                case ScreenSpaceLightingDebugMode.SsrPbrParams:
                {
                    if (m_SsrPass == null || !m_SsrPass.producedOutput ||
                        m_SsrPass.outputTexture == null || m_SsrPass.outputTexture.rt == null)
                        return false;
                    outputMode = 1;
                    texture = m_SsrPass.outputTexture.rt;
                    return true;
                }
                case ScreenSpaceLightingDebugMode.TransparentDepth:
                {
                    if (m_TransparentDepthNormalsPass == null || !m_TransparentDepthNormalsPass.producedOutput ||
                        m_History.transparentDepth == null || m_History.transparentDepth.rt == null)
                        return false;
                    outputMode = 0;
                    texture = m_History.transparentDepth.rt;
                    return true;
                }
                case ScreenSpaceLightingDebugMode.SsrTransparentRaw:
                case ScreenSpaceLightingDebugMode.SsrTransparentWeight:
                {
                    if (m_TransparentSsrPass == null || !m_TransparentSsrPass.producedOutput ||
                        m_TransparentSsrPass.outputTexture == null || m_TransparentSsrPass.outputTexture.rt == null)
                        return false;
                    outputMode = m_Settings.debugMode == ScreenSpaceLightingDebugMode.SsrTransparentWeight ? 3 : 1;
                    texture = m_TransparentSsrPass.outputTexture.rt;
                    return true;
                }
                default:
                    return false;
            }
        }
    }
}