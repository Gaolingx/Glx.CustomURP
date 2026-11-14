using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class PackedMipChainInfoTests
    {
        static PackedMipChainInfo Create(Vector2Int size)
        {
            var info = default(PackedMipChainInfo);
            info.Allocate();
            info.ComputePackedMipChainInfo(size);
            return info;
        }

        [Test]
        public void PackedLevelsStayInsideTheAtlasAndDoNotOverlap([Values(320, 640, 1279, 1280, 1920)] int width, [Values(180, 360, 719, 720, 1080)] int height)
        {
            var info = Create(new Vector2Int(width, height));
            var region = new bool[info.textureSize.x * info.textureSize.y];

            for (int mip = 0; mip < info.mipLevelCount; mip++)
            {
                var size = info.mipLevelSizes[mip];
                var offset = info.mipLevelOffsets[mip];
                Assert.GreaterOrEqual(size.x, 1);
                Assert.GreaterOrEqual(size.y, 1);
                Assert.LessOrEqual(offset.x + size.x, info.textureSize.x, $"mip {mip} escapes the atlas horizontally");
                Assert.LessOrEqual(offset.y + size.y, info.textureSize.y, $"mip {mip} escapes the atlas vertically");

                for (int y = offset.y; y < offset.y + size.y; y++)
                {
                    for (int x = offset.x; x < offset.x + size.x; x++)
                    {
                        int index = y * info.textureSize.x + x;
                        Assert.IsFalse(region[index], $"mip {mip} overlaps a previous level at ({x}, {y})");
                        region[index] = true;
                    }
                }
            }
        }

        [Test]
        public void LastLevelIsOneByOne()
        {
            var info = Create(new Vector2Int(1280, 720));

            Assert.AreEqual(Vector2Int.one, info.mipLevelSizes[info.mipLevelCount - 1]);
            Assert.AreEqual(12, info.mipLevelCount);
        }

        [Test]
        public void MipUvScaleAndOffsetMapTheExactPackedRegion()
        {
            var info = Create(new Vector2Int(1279, 719));

            for (int mip = 0; mip < info.mipLevelCount; mip++)
            {
                var size = info.mipLevelSizes[mip];
                var offset = info.mipLevelOffsets[mip];
                var scaleOffset = info.ComputeMipUvScaleOffset(mip);

                Assert.AreEqual(size.x / (float)info.textureSize.x, scaleOffset.x, 1e-6f);
                Assert.AreEqual(size.y / (float)info.textureSize.y, scaleOffset.y, 1e-6f);
                Assert.AreEqual(offset.x / (float)info.textureSize.x, scaleOffset.z, 1e-6f);
                Assert.AreEqual(offset.y / (float)info.textureSize.y, scaleOffset.w, 1e-6f);
            }
        }

        [Test]
        public void SameSizeDoesNotRepackButDifferentSizeDoes()
        {
            var info = Create(new Vector2Int(320, 180));
            var firstOffsets = (Vector2Int[])info.mipLevelOffsets.Clone();
            int firstCount = info.mipLevelCount;

            info.ComputePackedMipChainInfo(new Vector2Int(320, 180));
            CollectionAssert.AreEqual(firstOffsets, info.mipLevelOffsets);
            Assert.AreEqual(firstCount, info.mipLevelCount);

            info.ComputePackedMipChainInfo(new Vector2Int(640, 360));
            Assert.AreNotEqual(firstCount, info.mipLevelCount);
            Assert.AreEqual(640, info.mipLevelSizes[0].x);
        }

        [Test]
        public void OffsetBufferReceivesThePackedOffsets()
        {
            var info = Create(new Vector2Int(320, 180));
            var buffer = new ComputeBuffer(PackedMipChainInfo.k_MaxMipLevelCount, sizeof(int) * 2);
            try
            {
                info.GetOffsetBufferData(buffer);
                var data = new Vector2Int[PackedMipChainInfo.k_MaxMipLevelCount];
                buffer.GetData(data);

                for (int mip = 0; mip < info.mipLevelCount; mip++)
                    Assert.AreEqual(info.mipLevelOffsets[mip], data[mip]);
            }
            finally
            {
                buffer.Release();
            }
        }
    }
}