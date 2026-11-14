using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class CameraHistoryStoreTests
    {
        sealed class TestHistory : IScreenSpaceHistoryEntry
        {
            public int width;
            public int height;
            public int resetCount;
            public int lastResetFrame;
            public int bufferIndex;
            public bool disposed;

            public void Reset(int width, int height, int frameIndex)
            {
                this.width = width;
                this.height = height;
                lastResetFrame = frameIndex;
                resetCount++;
                bufferIndex = 0;
            }

            public void SwapBuffers()
            {
                bufferIndex ^= 1;
            }

            public void Dispose()
            {
                disposed = true;
            }
        }

        CameraHistoryStore<TestHistory> m_Store;
        Camera m_Camera;

        [SetUp]
        public void SetUp()
        {
            m_Store = new CameraHistoryStore<TestHistory>();
            m_Camera = ScreenSpaceTestUtils.CreateCamera("HistoryStoreCamera");
        }

        [TearDown]
        public void TearDown()
        {
            m_Store?.Dispose();
            ScreenSpaceTestUtils.DestroyCamera(m_Camera);
        }

        [Test]
        public void FirstRequestCreatesAndResetsEntry()
        {
            var history = m_Store.GetOrCreate(m_Camera, 0, 320, 180, 10, 0, out bool needsReset);

            Assert.IsTrue(needsReset);
            Assert.AreEqual(1, history.resetCount);
            Assert.AreEqual(10, history.lastResetFrame);
            Assert.AreEqual(320, history.width);
            Assert.AreEqual(180, history.height);
            Assert.AreEqual(1, m_Store.entryCount);
        }

        [Test]
        public void SameCameraReusesEntryWithoutReset()
        {
            var first = m_Store.GetOrCreate(m_Camera, 0, 320, 180, 10, 0, out _);
            var second = m_Store.GetOrCreate(m_Camera, 0, 320, 180, 11, 0, out bool needsReset);

            Assert.AreSame(first, second);
            Assert.IsFalse(needsReset);
            Assert.AreEqual(1, second.resetCount);
            Assert.AreEqual(1, m_Store.entryCount);
        }

        [Test]
        public void SizeChangeResetsEntry()
        {
            var first = m_Store.GetOrCreate(m_Camera, 0, 320, 180, 10, 0, out _);
            var second = m_Store.GetOrCreate(m_Camera, 0, 640, 360, 11, 0, out bool needsReset);

            Assert.AreSame(first, second);
            Assert.IsTrue(needsReset);
            Assert.AreEqual(2, second.resetCount);
            Assert.AreEqual(640, second.width);
            Assert.AreEqual(360, second.height);
        }

        [Test]
        public void ResetTokenResetsOnlyWhenItChanges()
        {
            var history = m_Store.GetOrCreate(m_Camera, 0, 320, 180, 10, 0, out _);

            m_Store.GetOrCreate(m_Camera, 0, 320, 180, 11, 1, out bool firstReset);
            m_Store.GetOrCreate(m_Camera, 0, 320, 180, 12, 1, out bool repeatReset);
            m_Store.GetOrCreate(m_Camera, 0, 320, 180, 13, 2, out bool secondReset);

            Assert.IsTrue(firstReset);
            Assert.IsFalse(repeatReset);
            Assert.IsTrue(secondReset);
            Assert.AreEqual(3, history.resetCount);
        }

        [Test]
        public void NegativeResetTokenHoldsResetEveryFrame()
        {
            var history = m_Store.GetOrCreate(m_Camera, 0, 320, 180, 10, -1, out _);
            m_Store.GetOrCreate(m_Camera, 0, 320, 180, 11, -1, out bool needsReset);

            Assert.IsTrue(needsReset);
            Assert.AreEqual(2, history.resetCount);
        }

        [Test]
        public void XrEyesOwnIndependentEntries()
        {
            var left = m_Store.GetOrCreate(m_Camera, 0, 320, 180, 10, 0, out _);
            var right = m_Store.GetOrCreate(m_Camera, 1, 320, 180, 10, 0, out _);

            Assert.AreNotSame(left, right);
            Assert.AreEqual(2, m_Store.entryCount);
        }

        [Test]
        public void DestroyedCameraEntryIsPruned()
        {
            m_Store.GetOrCreate(m_Camera, 0, 320, 180, 10, 0, out _);
            ScreenSpaceTestUtils.DestroyCamera(m_Camera);

            m_Store.PruneDestroyedCameras();

            Assert.AreEqual(0, m_Store.entryCount);
        }

        [Test]
        public void UnusedEntryAgesOutAfterConfiguredFrames()
        {
            m_Store.GetOrCreate(m_Camera, 0, 320, 180, 100, 0, out _);

            m_Store.PruneUnused(105, 5);
            Assert.AreEqual(1, m_Store.entryCount, "An entry exactly at the limit must survive.");

            m_Store.PruneUnused(106, 5);
            Assert.AreEqual(0, m_Store.entryCount);
        }

        [Test]
        public void TrimToCountReleasesLeastRecentlyUsedEntry()
        {
            var cameraA = ScreenSpaceTestUtils.CreateCamera("HistoryStoreCameraA");
            var cameraB = ScreenSpaceTestUtils.CreateCamera("HistoryStoreCameraB");
            try
            {
                var historyA = m_Store.GetOrCreate(cameraA, 0, 320, 180, 1, 0, out _);
                var historyB = m_Store.GetOrCreate(cameraB, 0, 320, 180, 2, 0, out _);
                m_Store.GetOrCreate(m_Camera, 0, 320, 180, 3, 0, out _);

                m_Store.TrimToCount(2);

                Assert.AreEqual(2, m_Store.entryCount);
                Assert.IsTrue(historyA.disposed);
                Assert.IsFalse(historyB.disposed);
            }
            finally
            {
                ScreenSpaceTestUtils.DestroyCamera(cameraA);
                ScreenSpaceTestUtils.DestroyCamera(cameraB);
            }
        }

        [Test]
        public void DisposeReleasesEntriesAndRejectsFurtherUse()
        {
            var history = m_Store.GetOrCreate(m_Camera, 0, 320, 180, 10, 0, out _);

            m_Store.Dispose();

            Assert.IsTrue(history.disposed);
            Assert.AreEqual(0, m_Store.entryCount);
            Assert.Throws<ObjectDisposedException>(() => m_Store.GetOrCreate(m_Camera, 0, 320, 180, 11, 0, out _));
        }
    }
}
