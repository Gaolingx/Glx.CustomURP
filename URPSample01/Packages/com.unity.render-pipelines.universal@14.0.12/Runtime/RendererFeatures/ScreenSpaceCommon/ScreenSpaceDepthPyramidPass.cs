using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Builds the packed depth mip chain shared by every screen-space consumer of the camera view.
    // The chain is produced once per camera and frame: ScreenSpaceDepthPyramidSystem enqueues this
    // pass only for the first consumer, the later ones bind the same attachment.
    internal sealed class ScreenSpaceDepthPyramidPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 3);
        internal const string k_ProfilingSamplerName = "Depth Pyramid";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);
        static readonly int s_DstSize = Shader.PropertyToID("_SslDstSize");

        ScreenSpaceDepthPyramid m_Pyramid;
        ComputeShader m_Compute;
        readonly int[] m_SrcOffsetAndLimit = new int[4];
        readonly int[] m_DstOffset = new int[4];
        readonly int[] m_DstSize = new int[2];

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
        internal static int s_EditorCopyDispatchCount;
        internal static int s_EditorDownsampleDispatchCount;
#endif

        internal ScreenSpaceDepthPyramidPass()
        {
            renderPassEvent = k_RenderPassEvent;
            // Motion is required by the SSGI/SSR reprojection and is also what forces URP to produce
            // a depth texture before the GBuffer pass on the deferred path.
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Motion);
        }

        internal void Setup(ScreenSpaceDepthPyramid pyramid, ComputeShader compute)
        {
            m_Pyramid = pyramid;
            m_Compute = compute;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            s_EditorExecuteCount++;
#endif
            if (m_Pyramid == null || m_Compute == null)
                return;

            m_Pyramid.EnsureResources();
            if (!m_Pyramid.ready)
                return;

            var depthTexture = ScreenSpaceRenderUtils.GetCameraDepthTexture(renderingData.cameraData.renderer);
            if (depthTexture == null)
                return;

            var compute = m_Compute;
            var info = m_Pyramid.info;
            int viewCount = m_Pyramid.viewCount;
            bool useTextureArray = m_Pyramid.dimension == TextureDimension.Tex2DArray;
            SetKeyword(compute, ScreenSpaceShaderIDs.XrArrayKeyword, useTextureArray);

            int copyKernel = compute.FindKernel("KDepthPyramidCopy");
            int downsampleKernel = compute.FindKernel("KDepthPyramidDownsample");

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                cmd.SetComputeTextureParam(compute, copyKernel, ScreenSpaceShaderIDs.CameraDepthTexture, depthTexture);
                cmd.SetComputeTextureParam(compute, copyKernel, ScreenSpaceShaderIDs.DepthMipChain, m_Pyramid.texture);
                cmd.SetComputeTextureParam(compute, downsampleKernel, ScreenSpaceShaderIDs.DepthMipChain, m_Pyramid.texture);

                var mip0Size = info.mipLevelSizes[0];
                var mip0Offset = info.mipLevelOffsets[0];
                SetOffsets(cmd, compute, mip0Offset, mip0Size, mip0Offset, mip0Size);
                cmd.DispatchCompute(compute, copyKernel,
                    DivideRoundUp(mip0Size.x, 8),
                    DivideRoundUp(mip0Size.y, 8),
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
                        DivideRoundUp(dstSize.x, 8),
                        DivideRoundUp(dstSize.y, 8),
                        viewCount);
                }
#if UNITY_EDITOR
                s_EditorDownsampleDispatchCount += info.mipLevelCount - 1;
#endif
            }
        }

        void SetOffsets(
            CommandBuffer cmd,
            ComputeShader compute,
            Vector2Int srcOffset,
            Vector2Int srcSize,
            Vector2Int dstOffset,
            Vector2Int dstSize)
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

            cmd.SetComputeIntParams(compute, ScreenSpaceShaderIDs.SrcOffsetAndLimit, m_SrcOffsetAndLimit);
            cmd.SetComputeIntParams(compute, ScreenSpaceShaderIDs.DstOffset, m_DstOffset);
            cmd.SetComputeIntParams(compute, s_DstSize, m_DstSize);
        }

        static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        static void SetKeyword(ComputeShader compute, string keyword, bool enabled)
        {
            if (enabled)
                compute.EnableKeyword(keyword);
            else
                compute.DisableKeyword(keyword);
        }
    }
}