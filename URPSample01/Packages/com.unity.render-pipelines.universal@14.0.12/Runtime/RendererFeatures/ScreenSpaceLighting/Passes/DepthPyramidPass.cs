using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Builds the private packed depth pyramid consumed by SSGI and SSR. The pass runs after the
    // built-in depth and motion passes (202) so the camera depth texture is valid for this frame.
    internal sealed class ScreenSpaceLightingDepthPyramidPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 3);
        internal const string k_ProfilingSamplerName = "SSL Depth Pyramid";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);
        static readonly int s_CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");

        ScreenSpaceLightingHistory m_History;
        ScreenSpaceLightingResources m_Resources;
        readonly int[] m_SrcOffsetAndLimit = new int[4];
        readonly int[] m_DstOffset = new int[4];
        readonly int[] m_DstSize = new int[2];

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
        internal static int s_EditorCopyDispatchCount;
#endif

        internal ScreenSpaceLightingDepthPyramidPass()
        {
            renderPassEvent = k_RenderPassEvent;
            // Motion is required by SSGI/SSR reprojection and is also what forces URP to produce a
            // depth texture before the GBuffer pass on the deferred path.
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Motion);
        }

        internal void Setup(ScreenSpaceLightingHistory history, ScreenSpaceLightingResources resources)
        {
            m_History = history;
            m_Resources = resources;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            s_EditorExecuteCount++;
#endif
            if (m_History == null || m_Resources == null || m_Resources.depthPyramidCompute == null)
                return;

            ref var cameraData = ref renderingData.cameraData;
            var descriptor = cameraData.cameraTargetDescriptor;
            int viewCount = cameraData.xr.enabled ? Mathf.Max(1, cameraData.xr.viewCount) : 1;
            TextureDimension dimension = viewCount > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;

            m_History.EnsureResources(
                descriptor.width,
                descriptor.height,
                viewCount,
                dimension,
                ScreenSpaceLightingRenderUtils.k_ColorFormat);

            var compute = m_Resources.depthPyramidCompute;
            var info = m_History.depthPyramidInfo;
            var depthTexture = GetCameraDepthTexture(cameraData.renderer);
            if (depthTexture == null || !info.IsValid() || m_History.depthPyramid == null)
                return;

            var cmd = renderingData.commandBuffer;
            SetXrKeyword(compute, dimension == TextureDimension.Tex2DArray);
            int copyKernel = compute.FindKernel("KDepthPyramidCopy");
            int downsampleKernel = compute.FindKernel("KDepthPyramidDownsample");

            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                cmd.SetComputeTextureParam(compute, copyKernel, s_CameraDepthTexture, depthTexture);
                cmd.SetComputeTextureParam(compute, copyKernel, ScreenSpaceLightingShaderIDs.DepthMipChain, m_History.depthPyramid);
                cmd.SetComputeTextureParam(compute, downsampleKernel, ScreenSpaceLightingShaderIDs.DepthMipChain, m_History.depthPyramid);

                var mip0Size = info.mipLevelSizes[0];
                var mip0Offset = info.mipLevelOffsets[0];
                SetOffsets(cmd, compute, mip0Offset, mip0Size, mip0Offset, mip0Size);
                cmd.DispatchCompute(compute, copyKernel,
                    ScreenSpaceLightingRenderUtils.DivideRoundUp(mip0Size.x, 8),
                    ScreenSpaceLightingRenderUtils.DivideRoundUp(mip0Size.y, 8),
                    viewCount);
#if UNITY_EDITOR
                s_EditorCopyDispatchCount++;
#endif

                for (int mipLevel = 1; mipLevel < info.mipLevelCount; mipLevel++)
                {
                    var srcSize = info.mipLevelSizes[mipLevel - 1];
                    var srcOffset = info.mipLevelOffsets[mipLevel - 1];
                    var dstSize = info.mipLevelSizes[mipLevel];
                    var dstOffset = info.mipLevelOffsets[mipLevel];
                    SetOffsets(cmd, compute, srcOffset, srcSize, dstOffset, dstSize);
                    cmd.DispatchCompute(compute, downsampleKernel,
                        ScreenSpaceLightingRenderUtils.DivideRoundUp(dstSize.x, 8),
                        ScreenSpaceLightingRenderUtils.DivideRoundUp(dstSize.y, 8),
                        viewCount);
                }
            }
        }

        internal static RTHandle GetCameraDepthTexture(ScriptableRenderer renderer)
        {
            if (renderer is UniversalRenderer universalRenderer && universalRenderer.m_DepthTexture != null)
                return universalRenderer.m_DepthTexture;

            return renderer != null ? renderer.cameraDepthTargetHandle : null;
        }

        void SetOffsets(CommandBuffer cmd, ComputeShader compute, Vector2Int srcOffset, Vector2Int srcSize, Vector2Int dstOffset, Vector2Int dstSize)
        {
            Vector2Int srcLimit = srcOffset + srcSize - Vector2Int.one;
            m_SrcOffsetAndLimit[0] = srcOffset.x;
            m_SrcOffsetAndLimit[1] = srcOffset.y;
            m_SrcOffsetAndLimit[2] = srcLimit.x;
            m_SrcOffsetAndLimit[3] = srcLimit.y;
            m_DstOffset[0] = dstOffset.x;
            m_DstOffset[1] = dstOffset.y;
            m_DstOffset[2] = 0;
            m_DstOffset[3] = 0;

            m_DstSize[0] = dstSize.x;
            m_DstSize[1] = dstSize.y;

            cmd.SetComputeIntParams(compute, ScreenSpaceLightingShaderIDs.SrcOffsetAndLimit, m_SrcOffsetAndLimit);
            cmd.SetComputeIntParams(compute, ScreenSpaceLightingShaderIDs.DstOffset, m_DstOffset);
            cmd.SetComputeIntParams(compute, ScreenSpaceLightingShaderIDs.DstSize, m_DstSize);
        }

        static void SetXrKeyword(ComputeShader compute, bool enabled)
        {
            if (enabled)
                compute.EnableKeyword(ScreenSpaceLightingShaderIDs.XrArrayKeyword);
            else
                compute.DisableKeyword(ScreenSpaceLightingShaderIDs.XrArrayKeyword);
        }
    }
}