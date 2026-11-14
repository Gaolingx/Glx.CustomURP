using System;
using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Describes where every level of a packed depth pyramid lives inside a single texture allocation.
    /// The layout keeps all mip levels inside the top level so the chain does not need power-of-two dimensions.
    /// </summary>
    internal struct PackedMipChainInfo
    {
        internal const int k_MaxMipLevelCount = 15;

        public Vector2Int textureSize;
        public int mipLevelCount;
        public Vector2Int[] mipLevelSizes;
        public Vector2Int[] mipLevelOffsets;

        Vector2 cachedTextureScale;
        Vector2Int cachedHardwareTextureSize;
        bool m_OffsetBufferWillNeedUpdate;

        public void Allocate()
        {
            mipLevelSizes = new Vector2Int[k_MaxMipLevelCount];
            mipLevelOffsets = new Vector2Int[k_MaxMipLevelCount];
            m_OffsetBufferWillNeedUpdate = true;
        }

        public void ComputePackedMipChainInfo(Vector2Int viewportSize)
        {
            bool hardwareDynamicResolution = DynamicResolutionHandler.instance.HardwareDynamicResIsEnabled();
            Vector2Int hardwareTextureSize = hardwareDynamicResolution
                ? DynamicResolutionHandler.instance.ApplyScalesOnSize(viewportSize)
                : viewportSize;
            Vector2 textureScale = hardwareDynamicResolution
                ? new Vector2((float)viewportSize.x / hardwareTextureSize.x, (float)viewportSize.y / hardwareTextureSize.y)
                : Vector2.one;

            if (cachedHardwareTextureSize == hardwareTextureSize && cachedTextureScale == textureScale)
                return;

            cachedHardwareTextureSize = hardwareTextureSize;
            cachedTextureScale = textureScale;

            if (mipLevelSizes == null || mipLevelOffsets == null)
                Allocate();

            mipLevelSizes[0] = hardwareTextureSize;
            mipLevelOffsets[0] = Vector2Int.zero;

            int mipLevel = 0;
            Vector2Int mipSize = hardwareTextureSize;

            do
            {
                mipLevel++;

                mipSize.x = Math.Max(1, (mipSize.x + 1) >> 1);
                mipSize.y = Math.Max(1, (mipSize.y + 1) >> 1);
                mipLevelSizes[mipLevel] = mipSize;

                Vector2Int previousMipBegin = mipLevelOffsets[mipLevel - 1];
                Vector2Int previousMipEnd = previousMipBegin + mipLevelSizes[mipLevel - 1];
                Vector2Int mipBegin = (mipLevel & 1) != 0
                    ? new Vector2Int(previousMipBegin.x, previousMipEnd.y)
                    : new Vector2Int(previousMipEnd.x, previousMipBegin.y);

                mipLevelOffsets[mipLevel] = mipBegin;
                hardwareTextureSize.x = Math.Max(hardwareTextureSize.x, mipBegin.x + mipSize.x);
                hardwareTextureSize.y = Math.Max(hardwareTextureSize.y, mipBegin.y + mipSize.y);
            }
            while (mipSize.x > 1 || mipSize.y > 1);

            textureSize = new Vector2Int(
                Mathf.CeilToInt(hardwareTextureSize.x * textureScale.x),
                Mathf.CeilToInt(hardwareTextureSize.y * textureScale.y));

            mipLevelCount = mipLevel + 1;
            m_OffsetBufferWillNeedUpdate = true;
        }

        public ComputeBuffer GetOffsetBufferData(ComputeBuffer mipLevelOffsetsBuffer)
        {
            if (m_OffsetBufferWillNeedUpdate)
            {
                mipLevelOffsetsBuffer.SetData(mipLevelOffsets);
                m_OffsetBufferWillNeedUpdate = false;
            }

            return mipLevelOffsetsBuffer;
        }

        internal bool IsValid()
        {
            return mipLevelCount > 0 && mipLevelSizes != null && mipLevelOffsets != null;
        }

        // Maps [0, 1] over one packed level to the atlas: xy scales the sample uv, zw moves it to the
        // level origin. Tracing and the debug views use the same mapping so a level can never leak
        // into a neighbouring packed region.
        internal Vector4 ComputeMipUvScaleOffset(int mipLevel)
        {
            if (!IsValid())
                return new Vector4(1f, 1f, 0f, 0f);

            mipLevel = Mathf.Clamp(mipLevel, 0, mipLevelCount - 1);
            var size = mipLevelSizes[mipLevel];
            var offset = mipLevelOffsets[mipLevel];
            float rcpWidth = 1f / Mathf.Max(1, textureSize.x);
            float rcpHeight = 1f / Mathf.Max(1, textureSize.y);

            return new Vector4(
                size.x * rcpWidth,
                size.y * rcpHeight,
                offset.x * rcpWidth,
                offset.y * rcpHeight);
        }
    }
}
