using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class ScreenSpaceLightingHistoryTests
    {
        ScreenSpaceLightingHistory m_History;

        [SetUp]
        public void SetUp()
        {
            m_History = new ScreenSpaceLightingHistory();
            m_History.Reset(320, 180, 1);
        }

        [TearDown]
        public void TearDown()
        {
            m_History?.Dispose();
        }

        [Test]
        public void FirstFrameHasNoReadableHistoryButStillHasAWriteSlot()
        {
            m_History.BeginFrame();

            Assert.IsFalse(m_History.hasValidHistory);
            Assert.AreEqual(-1, m_History.readSlot);
            Assert.AreEqual(1, m_History.writeSlot);
            Assert.IsFalse(m_History.sameFrameConflict);
            Assert.IsNull(m_History.readColorPyramid);
            Assert.IsNull(m_History.readPreviousDepth);
        }

        [Test]
        public void SwappedHistoryIsReadFromTheSlotWrittenLastFrame()
        {
            m_History.BeginFrame();
            m_History.SwapBuffers();
            m_History.BeginFrame();

            Assert.IsTrue(m_History.hasValidHistory);
            Assert.AreEqual(1, m_History.readSlot);
            Assert.AreEqual(0, m_History.writeSlot);
            Assert.AreEqual(1, m_History.validFrameCount);
            Assert.AreNotEqual(m_History.readSlot, m_History.writeSlot);
        }

        [Test]
        public void SlotsAlternateEveryFrameAndNeverOverlapInTheSameFrame()
        {
            for (int frame = 0; frame < 6; frame++)
            {
                m_History.BeginFrame();
                Assert.AreNotEqual(m_History.readSlot, m_History.writeSlot, $"frame {frame} reads the slot it is about to write");
                Assert.IsFalse(m_History.sameFrameConflict);
                m_History.SwapBuffers();
            }

            Assert.AreEqual(6, m_History.validFrameCount);
        }

        [Test]
        public void ResetInvalidatesHistoryAndRestartsThePingPong()
        {
            m_History.BeginFrame();
            m_History.SwapBuffers();

            m_History.Reset(320, 180, 2);

            Assert.IsFalse(m_History.hasValidHistory);
            Assert.AreEqual(0, m_History.bufferIndex);
            Assert.AreEqual(2, m_History.resetCount);
            Assert.AreEqual(2, m_History.lastResetFrame);
            Assert.AreEqual(0, m_History.validFrameCount);
        }

        [Test]
        public void SameSizeKeepsResourcesAndSizeChangeReallocates()
        {
            m_History.EnsureResources(320, 180, 1, TextureDimension.Tex2D, ScreenSpaceLightingRenderUtils.k_ColorFormat);
            int generation = m_History.resourceGeneration;
            var depthPyramid = m_History.depthPyramid;

            m_History.EnsureResources(320, 180, 1, TextureDimension.Tex2D, ScreenSpaceLightingRenderUtils.k_ColorFormat);

            Assert.AreEqual(generation, m_History.resourceGeneration);
            Assert.AreSame(depthPyramid, m_History.depthPyramid);

            m_History.EnsureResources(640, 360, 1, TextureDimension.Tex2D, ScreenSpaceLightingRenderUtils.k_ColorFormat);

            Assert.AreEqual(generation + 1, m_History.resourceGeneration);
            Assert.AreNotSame(depthPyramid, m_History.depthPyramid);
            Assert.AreEqual(m_History.depthPyramidInfo.textureSize.x, m_History.depthPyramid.rt.width);
            Assert.AreEqual(m_History.depthPyramidInfo.textureSize.y, m_History.depthPyramid.rt.height);
        }

        [Test]
        public void AllocatedResourcesMatchTheRequestedLayout()
        {
            m_History.EnsureResources(320, 180, 2, TextureDimension.Tex2DArray, ScreenSpaceLightingRenderUtils.k_ColorFormat);

            Assert.IsTrue(m_History.resourcesAllocated);
            Assert.AreEqual(2, m_History.slices);
            Assert.AreEqual(TextureDimension.Tex2DArray, m_History.dimension);
            Assert.AreEqual(2, m_History.depthPyramid.rt.volumeDepth);
            Assert.AreEqual(2, m_History.currentColorPyramid.rt.volumeDepth);
            Assert.AreEqual(2, m_History.historyColorPyramidCount);
            Assert.AreEqual(2, m_History.previousDepthCount);
            Assert.AreEqual(ScreenSpaceLightingHistory.ComputeColorPyramidMipCount(320, 180), m_History.colorPyramidMipCount);
            Assert.AreEqual(m_History.depthPyramidInfo.mipLevelCount, m_History.depthPyramidMipCount);
        }

        [Test]
        public void DisposeReleasesAllocatedResources()
        {
            m_History.EnsureResources(320, 180, 1, TextureDimension.Tex2D, ScreenSpaceLightingRenderUtils.k_ColorFormat);

            m_History.Dispose();

            Assert.IsFalse(m_History.resourcesAllocated);
            Assert.IsNull(m_History.depthPyramid);
            Assert.IsNull(m_History.currentColorPyramid);
            Assert.IsNull(m_History.GetHistoryColorPyramid(0));
            Assert.IsNull(m_History.GetHistoryColorPyramid(1));
            Assert.IsNull(m_History.GetPreviousDepth(0));
            Assert.IsNull(m_History.GetPreviousDepth(1));
        }

        [Test]
        public void ColorPyramidMipCountStopsBelowTheGaussianBlock()
        {
            Assert.AreEqual(9, ScreenSpaceLightingHistory.ComputeColorPyramidMipCount(1280, 720));
            Assert.AreEqual(2, ScreenSpaceLightingHistory.ComputeColorPyramidMipCount(8, 8));
            Assert.AreEqual(1, ScreenSpaceLightingHistory.ComputeColorPyramidMipCount(4, 4));
        }
    }
}