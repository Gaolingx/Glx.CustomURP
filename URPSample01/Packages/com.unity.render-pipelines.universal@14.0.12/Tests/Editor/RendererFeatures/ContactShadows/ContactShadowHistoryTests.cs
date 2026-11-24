using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    internal class ContactShadowHistoryTests
    {
        ScreenSpaceContactShadowsHistory m_History;
        ScreenSpaceDepthPyramid m_Pyramid;

        [SetUp]
        public void SetUp()
        {
            m_History = new ScreenSpaceContactShadowsHistory();
            m_Pyramid = new ScreenSpaceDepthPyramid();
        }

        [TearDown]
        public void TearDown()
        {
            m_History.Dispose();
            m_Pyramid.Dispose();
        }

        void PreparePyramid(int width, int height, int views = 1, TextureDimension dimension = TextureDimension.Tex2D)
        {
            if (!SystemInfo.supportsComputeShaders ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, FormatUsage.LoadStore) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_UInt, FormatUsage.LoadStore) ||
                (dimension == TextureDimension.Tex2DArray && !SystemInfo.supports2DArrayTextures))
                Assert.Ignore("Requires load/store depth and packed targets with the requested layout.");

            m_Pyramid.Prepare(width, height, views, dimension);
            m_Pyramid.EnsureResources();
            Assert.IsTrue(m_Pyramid.ready);
        }

        static RTHandle EnsurePacked(ScreenSpaceContactShadowsHistory history, int width, int height,
            TextureDimension dimension = TextureDimension.Tex2D, int views = 1)
        {
            Assert.IsTrue(history.EnsurePackedTexture());
            var handle = history.packedTexture;
            Assert.IsNotNull(handle);
            var rt = handle.rt;
            Assert.IsNotNull(rt);
            Assert.AreEqual(width, rt.width);
            Assert.AreEqual(height, rt.height);
            Assert.AreEqual(dimension, rt.dimension);
            Assert.AreEqual(views, rt.volumeDepth);
            Assert.AreEqual(GraphicsFormat.R32_UInt, rt.graphicsFormat);
            Assert.IsTrue(rt.enableRandomWrite);
            return handle;
        }

        [Test]
        public void MissingOrUnbuiltPyramidDoesNotAllocate()
        {
            m_History.Reset(65, 33, 1);
            Assert.IsFalse(m_History.EnsurePackedTexture());
            Assert.IsNull(m_History.packedTexture);

            m_Pyramid.Prepare(65, 33, 1, TextureDimension.Tex2D);
            m_History.depthPyramidSource = m_Pyramid;
            Assert.IsFalse(m_History.EnsurePackedTexture());
            Assert.IsNull(m_History.packedTexture);
        }

        [Test]
        public void OddViewportUsesMipOneForTraceAndMipZeroForConfirmation()
        {
            m_History.Reset(65, 33, 1);
            PreparePyramid(65, 33);
            m_History.depthPyramidSource = m_Pyramid;
            var first = EnsurePacked(m_History, 65, 33);
            Assert.AreEqual(new RectInt(0, 33, 33, 17), m_History.traceMip);
            Assert.AreEqual(new RectInt(0, 0, 65, 33), m_History.confirmMip);
            Assert.AreSame(first, EnsurePacked(m_History, 65, 33));

            m_History.Reset(49, 25, 2);
            PreparePyramid(49, 25);
            m_History.depthPyramidSource = m_Pyramid;
            EnsurePacked(m_History, 49, 25);
            Assert.IsNull(first.rt, "Resize must release the old allocation.");
            Assert.AreEqual(new RectInt(0, 25, 25, 13), m_History.traceMip);
            Assert.AreEqual(new RectInt(0, 0, 49, 25), m_History.confirmMip);
        }

        [Test]
        public void SameViewportTracksPyramidDimensionAndSliceCountIndependently()
        {
            m_History.Reset(32, 16, 1);
            PreparePyramid(32, 16);
            m_History.depthPyramidSource = m_Pyramid;
            var mono = EnsurePacked(m_History, 32, 16);

            PreparePyramid(32, 16, 1, TextureDimension.Tex2DArray);
            var array = EnsurePacked(m_History, 32, 16, TextureDimension.Tex2DArray, 1);
            Assert.IsNull(mono.rt);

            PreparePyramid(32, 16, 2, TextureDimension.Tex2DArray);
            EnsurePacked(m_History, 32, 16, TextureDimension.Tex2DArray, 2);
            Assert.IsNull(array.rt);
        }

        [Test]
        public void ExistingPackedTextureCannotMakeAnUnavailablePyramidReady()
        {
            m_History.Reset(32, 16, 1);
            PreparePyramid(32, 16);
            m_History.depthPyramidSource = m_Pyramid;
            EnsurePacked(m_History, 32, 16);

            m_Pyramid.Dispose();
            Assert.IsFalse(m_History.EnsurePackedTexture());
            PreparePyramid(32, 16);
            m_History.depthPyramidSource = m_Pyramid;
            EnsurePacked(m_History, 32, 16);
        }

        [Test]
        public void CameraAndEyeOwnDistinctTargetsUntilPrunedOrDisposed()
        {
            PreparePyramid(32, 16);
            var a = ScreenSpaceTestUtils.CreateCamera("Contact A");
            var b = ScreenSpaceTestUtils.CreateCamera("Contact B");
            try
            {
                using (var store = new CameraHistoryStore<ScreenSpaceContactShadowsHistory>())
                {
                    var left = store.GetOrCreate(a, 0, 32, 16, 1, 0, out _);
                    var right = store.GetOrCreate(a, 1, 32, 16, 1, 0, out _);
                    var other = store.GetOrCreate(b, 0, 32, 16, 1, 0, out _);
                    left.depthPyramidSource = right.depthPyramidSource = other.depthPyramidSource = m_Pyramid;
                    var leftRT = EnsurePacked(left, 32, 16);
                    var rightRT = EnsurePacked(right, 32, 16);
                    var otherRT = EnsurePacked(other, 32, 16);
                    Assert.AreNotSame(leftRT, rightRT);
                    Assert.AreNotSame(leftRT, otherRT);
                    Assert.AreNotSame(rightRT, otherRT);

                    Assert.AreSame(left, store.GetOrCreate(a, 0, 32, 16, 2, 0, out var reset));
                    Assert.IsFalse(reset);
                    store.PruneUnused(2, 0);
                    Assert.IsNull(rightRT.rt);
                    Assert.IsNull(otherRT.rt);
                    Assert.AreSame(leftRT, EnsurePacked(left, 32, 16));

                    store.Dispose();
                    Assert.IsNull(leftRT.rt);
                    Assert.IsNull(left.packedTexture);
                    Assert.IsTrue(m_Pyramid.ready, "Disposing a consumer must not release the shared pyramid.");
                }
            }
            finally
            {
                ScreenSpaceTestUtils.DestroyCamera(a);
                ScreenSpaceTestUtils.DestroyCamera(b);
            }
        }
    }
}
