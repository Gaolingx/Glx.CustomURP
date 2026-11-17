using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class GroundTruthAmbientOcclusionHistoryTests
    {
        GroundTruthAmbientOcclusionHistory m_History;

        // Batch mode never creates a render pipeline, so the default RTHandle system is not
        // initialized and every allocation returns without a camera-sized budget. URP does this in
        // its pipeline constructor; the allocation tests need the same setup.
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            RTHandles.Initialize(1280, 720);
        }

        [SetUp]
        public void SetUp()
        {
            m_History = new GroundTruthAmbientOcclusionHistory();
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
            Assert.IsNull(m_History.GetHistory(m_History.readSlot));
        }

        [Test]
        public void TheFirstFrameCopiesIntoTheSlotThatIsNotWritten()
        {
            m_History.BeginFrame();

            // Temporal pass on the first frame reads GetHistory(writeSlot ^ 1) and writes writeSlot.
            Assert.AreNotEqual(m_History.writeSlot, m_History.writeSlot ^ 1);
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
        public void EffectFrameCounterSurvivesResets()
        {
            m_History.AdvanceEffectFrame();
            m_History.AdvanceEffectFrame();
            m_History.Reset(320, 180, 2);

            Assert.AreEqual(2, m_History.effectFrameCount);
        }

        [Test]
        public void SameSizeKeepsResourcesAndSizeChangeReallocates()
        {
            m_History.EnsureResources(320, 180, false, 1, TextureDimension.Tex2D);
            int generation = m_History.resourceGeneration;
            var packedData = m_History.packedData;

            m_History.EnsureResources(320, 180, false, 1, TextureDimension.Tex2D);

            Assert.AreEqual(generation, m_History.resourceGeneration);
            Assert.AreSame(packedData, m_History.packedData);

            // The depth mip chain belongs to the shared ScreenSpaceDepthPyramidSystem, so only the
            // AO buffers reallocate when the viewport size changes.
            m_History.EnsureResources(640, 360, false, 1, TextureDimension.Tex2D);

            Assert.AreEqual(generation + 1, m_History.resourceGeneration);
            Assert.AreNotSame(packedData, m_History.packedData);
            Assert.AreEqual(m_History.aoWidth, m_History.packedData.rt.width);
            Assert.AreEqual(m_History.aoHeight, m_History.packedData.rt.height);
        }

        [Test]
        public void HalfResolutionAllocatesCeiledAoBufferAndPrivatePlatformLayout()
        {
            m_History.EnsureResources(321, 181, false, 2, TextureDimension.Tex2DArray);

            Assert.IsTrue(m_History.resourcesAllocated);
            Assert.AreEqual(161, m_History.aoWidth);
            Assert.AreEqual(91, m_History.aoHeight);
            Assert.AreEqual(2, m_History.slices);
            Assert.AreEqual(TextureDimension.Tex2DArray, m_History.dimension);
            // The depth mip chain is shared with Screen Space Lighting and no longer owned here.
            Assert.AreEqual(2, m_History.packedData.rt.volumeDepth);
            Assert.AreEqual(2, m_History.occlusionTexture.rt.volumeDepth);
            Assert.AreEqual(2, m_History.historyTextureCount);
            Assert.AreEqual(GraphicsFormat.R8G8B8A8_UNorm, m_History.GetHistory(0).rt.graphicsFormat);
        }

        [Test]
        public void FullResolutionAllocatesTheCameraSize()
        {
            m_History.EnsureResources(320, 180, true, 1, TextureDimension.Tex2D);

            Assert.AreEqual(320, m_History.aoWidth);
            Assert.AreEqual(180, m_History.aoHeight);
            Assert.AreEqual(320, m_History.occlusionTexture.rt.width);
            Assert.AreEqual(180, m_History.occlusionTexture.rt.height);
        }

        [Test]
        public void TogglingResolutionReallocatesTheAoResources()
        {
            m_History.EnsureResources(320, 180, false, 1, TextureDimension.Tex2D);
            int generation = m_History.resourceGeneration;

            m_History.EnsureResources(320, 180, true, 1, TextureDimension.Tex2D);

            Assert.AreEqual(generation + 1, m_History.resourceGeneration);
            Assert.AreEqual(320, m_History.aoWidth);
        }

        [Test]
        public void ResetAndDisposeClearTheEditorMotionPose()
        {
            var pose = Matrix4x4.TRS(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 15f, 0f), Vector3.one);
            m_History.RecordEditorMotionViewProjection(pose);
            Assert.IsTrue(m_History.hasEditorMotionViewProjection);

            m_History.Reset(320, 180, 2);

            Assert.IsFalse(m_History.hasEditorMotionViewProjection);
            Assert.AreEqual(Matrix4x4.identity, m_History.editorMotionViewProjection,
                "A reset must not reproject from the pose of a previous camera session.");

            m_History.RecordEditorMotionViewProjection(pose);
            m_History.Dispose();

            Assert.IsFalse(m_History.hasEditorMotionViewProjection);
            Assert.AreEqual(Matrix4x4.identity, m_History.editorMotionViewProjection);
        }

        [Test]
        public void DisposeReleasesAllocatedResources()
        {
            m_History.EnsureResources(320, 180, false, 1, TextureDimension.Tex2D);

            m_History.Dispose();

            Assert.IsFalse(m_History.resourcesAllocated);
            Assert.IsNull(m_History.depthPyramid);
            Assert.IsNull(m_History.packedData);
            Assert.IsNull(m_History.packedBlurred);
            Assert.IsNull(m_History.packedTemporalOutput);
            Assert.IsNull(m_History.occlusionTexture);
            Assert.IsNull(m_History.GetHistory(0));
            Assert.IsNull(m_History.GetHistory(1));
            Assert.IsNull(m_History.depthPyramidOffsets);
        }
    }
}