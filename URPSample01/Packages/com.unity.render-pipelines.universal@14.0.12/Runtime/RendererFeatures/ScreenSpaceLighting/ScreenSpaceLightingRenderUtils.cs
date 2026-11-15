using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    internal static class ScreenSpaceLightingRenderUtils
    {
        // Scene-linear HDR history. The camera target can be R11G11B10 or LDR; a fixed floating
        // point format keeps the Gaussian chain and the temporal history comparable.
        internal static readonly GraphicsFormat k_ColorFormat = GraphicsFormat.R16G16B16A16_SFloat;
        static readonly int s_CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");

        internal static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        internal static RTHandle GetNormalTexture(ScriptableRenderer renderer)
        {
            if (renderer is UniversalRenderer universalRenderer)
                return universalRenderer.cameraNormalsTextureHandle;

            return null;
        }

        internal static RTHandle GetMotionTexture(ScriptableRenderer renderer)
        {
            if (renderer is UniversalRenderer universalRenderer)
                return universalRenderer.m_MotionVectorColor;

            return null;
        }

        internal static void RenderColorGaussianPyramid(
            CommandBuffer cmd,
            Material material,
            MaterialPropertyBlock propertyBlock,
            RTHandle source,
            RTHandle destination,
            RTHandle tempDownsample,
            RTHandle tempBlur,
            int width,
            int height,
            int mipCount)
        {
            if (material == null || source == null || destination == null || tempDownsample == null || tempBlur == null)
                return;
            if (source.rt == null || destination.rt == null || tempDownsample.rt == null || tempBlur.rt == null)
                return;

            // Copy the top mip. The source can be the camera color target or an already scaled RTHandle.
            propertyBlock.SetTexture(ScreenSpaceLightingShaderIDs.Source, source);
            propertyBlock.SetVector(ScreenSpaceLightingShaderIDs.SrcScaleBias, new Vector4(width / (float)source.rt.width, height / (float)source.rt.height, 0f, 0f));
            propertyBlock.SetFloat(ScreenSpaceLightingShaderIDs.SourceMip, 0f);
            CoreUtils.SetRenderTarget(cmd, destination, ClearFlag.None, 0, CubemapFace.Unknown, -1);
            cmd.SetViewport(new Rect(0f, 0f, width, height));
            cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, propertyBlock);

            int srcWidth = width;
            int srcHeight = height;
            int tempWidth = Mathf.Max(1, tempDownsample.rt.width);
            int tempHeight = Mathf.Max(1, tempDownsample.rt.height);
            int blurWidth = Mathf.Max(1, tempBlur.rt.width);
            int blurHeight = Mathf.Max(1, tempBlur.rt.height);

            for (int mipLevel = 1; mipLevel < mipCount; mipLevel++)
            {
                int dstWidth = Mathf.Max(1, srcWidth >> 1);
                int dstHeight = Mathf.Max(1, srcHeight >> 1);

                // Downsample the previous level into the half-resolution temporary target.
                propertyBlock.SetTexture(ScreenSpaceLightingShaderIDs.Source, destination);
                propertyBlock.SetVector(ScreenSpaceLightingShaderIDs.SrcScaleBias, new Vector4(srcWidth / (float)width, srcHeight / (float)height, 0f, 0f));
                propertyBlock.SetFloat(ScreenSpaceLightingShaderIDs.SourceMip, mipLevel - 1);
                CoreUtils.SetRenderTarget(cmd, tempDownsample, ClearFlag.None, 0, CubemapFace.Unknown, -1);
                cmd.SetViewport(new Rect(0f, 0f, dstWidth, dstHeight));
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, propertyBlock);

                // Horizontal Gaussian.
                propertyBlock.SetTexture(ScreenSpaceLightingShaderIDs.Source, tempDownsample);
                propertyBlock.SetVector(ScreenSpaceLightingShaderIDs.SrcScaleBias, new Vector4(dstWidth / (float)tempWidth, dstHeight / (float)tempHeight, 0f, 0f));
                propertyBlock.SetVector(ScreenSpaceLightingShaderIDs.SrcUvLimits,
                    new Vector4((dstWidth - 0.5f) / tempWidth, (dstHeight - 0.5f) / tempHeight, 1f / tempWidth, 0f));
                propertyBlock.SetFloat(ScreenSpaceLightingShaderIDs.SourceMip, 0f);
                CoreUtils.SetRenderTarget(cmd, tempBlur, ClearFlag.None, 0, CubemapFace.Unknown, -1);
                cmd.SetViewport(new Rect(0f, 0f, dstWidth, dstHeight));
                cmd.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 3, 1, propertyBlock);

                // Vertical Gaussian into the destination mip.
                propertyBlock.SetTexture(ScreenSpaceLightingShaderIDs.Source, tempBlur);
                propertyBlock.SetVector(ScreenSpaceLightingShaderIDs.SrcScaleBias, new Vector4(dstWidth / (float)blurWidth, dstHeight / (float)blurHeight, 0f, 0f));
                propertyBlock.SetVector(ScreenSpaceLightingShaderIDs.SrcUvLimits,
                    new Vector4((dstWidth - 0.5f) / blurWidth, (dstHeight - 0.5f) / blurHeight, 0f, 1f / blurHeight));
                propertyBlock.SetFloat(ScreenSpaceLightingShaderIDs.SourceMip, 0f);
                CoreUtils.SetRenderTarget(cmd, destination, ClearFlag.None, mipLevel, CubemapFace.Unknown, -1);
                cmd.SetViewport(new Rect(0f, 0f, dstWidth, dstHeight));
                cmd.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 3, 1, propertyBlock);

                srcWidth = dstWidth;
                srcHeight = dstHeight;
            }
        }

        // Captures the current depth into a full-resolution previous-depth slot with the same packed
        // copy kernel used for mip 0 of the pyramid. The destination offset is zero.
        internal static void CopyDepthToPrevious(
            CommandBuffer cmd,
            ComputeShader compute,
            RTHandle depthTexture,
            RTHandle destination,
            int width,
            int height,
            int viewCount,
            bool useTextureArray,
            int[] srcOffsetAndLimit,
            int[] dstOffset)
        {
            if (compute == null || depthTexture == null || destination == null)
                return;

            if (useTextureArray)
                compute.EnableKeyword(ScreenSpaceLightingShaderIDs.XrArrayKeyword);
            else
                compute.DisableKeyword(ScreenSpaceLightingShaderIDs.XrArrayKeyword);

            int kernel = compute.FindKernel("KDepthPyramidCopy");
            int limitX = Mathf.Max(0, width - 1);
            int limitY = Mathf.Max(0, height - 1);
            srcOffsetAndLimit[0] = 0;
            srcOffsetAndLimit[1] = 0;
            srcOffsetAndLimit[2] = limitX;
            srcOffsetAndLimit[3] = limitY;
            dstOffset[0] = 0;
            dstOffset[1] = 0;
            dstOffset[2] = 0;
            dstOffset[3] = 0;

            cmd.SetComputeTextureParam(compute, kernel, s_CameraDepthTexture, depthTexture);
            cmd.SetComputeTextureParam(compute, kernel, ScreenSpaceLightingShaderIDs.DepthMipChain, destination);
            cmd.SetComputeIntParams(compute, ScreenSpaceLightingShaderIDs.SrcOffsetAndLimit, srcOffsetAndLimit);
            cmd.SetComputeIntParams(compute, ScreenSpaceLightingShaderIDs.DstOffset, dstOffset);
            cmd.DispatchCompute(compute, kernel, DivideRoundUp(width, 8), DivideRoundUp(height, 8), viewCount);
        }

        // Captures the current camera normals into the next history slot, encoding untouched. The
        // temporal filter decodes it with the same keyword the trace uses (signed or octahedron).
        internal static void CopyNormalsToPrevious(
            CommandBuffer cmd,
            ComputeShader compute,
            RTHandle normalTexture,
            RTHandle destination,
            int width,
            int height,
            int viewCount,
            bool useTextureArray)
        {
            if (compute == null || normalTexture == null || destination == null)
                return;

            if (useTextureArray)
                compute.EnableKeyword(ScreenSpaceLightingShaderIDs.XrArrayKeyword);
            else
                compute.DisableKeyword(ScreenSpaceLightingShaderIDs.XrArrayKeyword);

            int kernel = compute.FindKernel("CopyHistoryNormals");
            int[] copySize = { Mathf.Max(0, width), Mathf.Max(0, height), 0, 0 };

            cmd.SetComputeTextureParam(compute, kernel, ScreenSpaceLightingShaderIDs.SsgiSourceNormalTexture, normalTexture);
            cmd.SetComputeTextureParam(compute, kernel, ScreenSpaceLightingShaderIDs.SsgiOutputHistoryNormalTextureRW, destination);
            cmd.SetComputeIntParams(compute, ScreenSpaceLightingShaderIDs.SsgiHistoryCopySize, copySize);
            cmd.DispatchCompute(compute, kernel, DivideRoundUp(width, 8), DivideRoundUp(height, 8), viewCount);
        }
    }
}