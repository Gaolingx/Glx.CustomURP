using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class ScreenSpaceLightingSsrPbrTests
    {
        ScreenSpaceLightingHistory m_History;
        ScreenSpaceReflection m_Settings;

        [SetUp]
        public void SetUp()
        {
            m_History = new ScreenSpaceLightingHistory();
            m_History.Reset(320, 180, 1);
            m_Settings = ScriptableObject.CreateInstance<ScreenSpaceReflection>();
        }

        [TearDown]
        public void TearDown()
        {
            m_History?.Dispose();
            if (m_Settings != null)
                UnityEngine.Object.DestroyImmediate(m_Settings);
        }

        [Test]
        public void VolumeExposesPbrAccumulationControls()
        {
            Assert.AreEqual(ScreenSpaceReflectionAlgorithm.Approximation, m_Settings.usedAlgorithm.value);
            Assert.AreEqual(0.75f, m_Settings.accumulationFactor.value, 1e-5f);
            Assert.AreEqual(0.5f, m_Settings.biasFactor.value, 1e-5f);
            Assert.AreEqual(0.5f, m_Settings.speedRejectionParam.value, 1e-5f);
            Assert.AreEqual(0.2f, m_Settings.speedRejectionScalerFactor.value, 1e-5f);
            Assert.IsFalse(m_Settings.speedSmoothReject.value);
            Assert.IsTrue(m_Settings.speedSurfaceOnly.value);
            Assert.IsTrue(m_Settings.speedTargetOnly.value);
            Assert.IsFalse(m_Settings.enableWorldSpeedRejection.value);
        }

        [Test]
        public void EveryPublicCombinationMapsToItsOwnKernel()
        {
            m_Settings.speedSurfaceOnly.Override(true);
            m_Settings.speedTargetOnly.Override(true);

            m_Settings.enableWorldSpeedRejection.Override(false);
            Assert.AreEqual(
                "ScreenSpaceReflectionsAccumulateNoWorldSpeedRejectionBoth",
                ScreenSpaceLightingSsrPass.GetAccumulationKernelName(m_Settings, false));

            m_Settings.speedSurfaceOnly.Override(true);
            m_Settings.speedTargetOnly.Override(false);
            Assert.AreEqual(
                "ScreenSpaceReflectionsAccumulateNoWorldSpeedRejectionSourceOnly",
                ScreenSpaceLightingSsrPass.GetAccumulationKernelName(m_Settings, false));

            m_Settings.speedSurfaceOnly.Override(false);
            m_Settings.speedTargetOnly.Override(true);
            Assert.AreEqual(
                "ScreenSpaceReflectionsAccumulateNoWorldSpeedRejectionTargetOnly",
                ScreenSpaceLightingSsrPass.GetAccumulationKernelName(m_Settings, false));

            m_Settings.enableWorldSpeedRejection.Override(true);
            m_Settings.speedSmoothReject.Override(false);

            m_Settings.speedSurfaceOnly.Override(true);
            m_Settings.speedTargetOnly.Override(true);
            Assert.AreEqual(
                "ScreenSpaceReflectionsAccumulateHardThresholdSpeedRejectionBoth",
                ScreenSpaceLightingSsrPass.GetAccumulationKernelName(m_Settings, false));

            m_Settings.speedSurfaceOnly.Override(true);
            m_Settings.speedTargetOnly.Override(false);
            Assert.AreEqual(
                "ScreenSpaceReflectionsAccumulateHardThresholdSpeedRejectionSourceOnly",
                ScreenSpaceLightingSsrPass.GetAccumulationKernelName(m_Settings, false));

            m_Settings.speedSurfaceOnly.Override(false);
            m_Settings.speedTargetOnly.Override(true);
            Assert.AreEqual(
                "ScreenSpaceReflectionsAccumulateHardThresholdSpeedRejectionTargetOnly",
                ScreenSpaceLightingSsrPass.GetAccumulationKernelName(m_Settings, false));

            m_Settings.speedSmoothReject.Override(true);

            m_Settings.speedSurfaceOnly.Override(true);
            m_Settings.speedTargetOnly.Override(true);
            Assert.AreEqual(
                "ScreenSpaceReflectionsAccumulateSmoothSpeedRejectionBoth",
                ScreenSpaceLightingSsrPass.GetAccumulationKernelName(m_Settings, false));

            m_Settings.speedSurfaceOnly.Override(true);
            m_Settings.speedTargetOnly.Override(false);
            Assert.AreEqual(
                "ScreenSpaceReflectionsAccumulateSmoothSpeedRejectionSourceOnly",
                ScreenSpaceLightingSsrPass.GetAccumulationKernelName(m_Settings, false));

            m_Settings.speedSurfaceOnly.Override(false);
            m_Settings.speedTargetOnly.Override(true);
            Assert.AreEqual(
                "ScreenSpaceReflectionsAccumulateSmoothSpeedRejectionTargetOnly",
                ScreenSpaceLightingSsrPass.GetAccumulationKernelName(m_Settings, false));
        }

        [Test]
        public void DebugSpeedUsesTheDebugKernelVariants()
        {
            m_Settings.enableWorldSpeedRejection.Override(true);
            m_Settings.speedSmoothReject.Override(false);
            Assert.AreEqual(
                "ScreenSpaceReflectionsAccumulateHardThresholdSpeedRejectionBothDebug",
                ScreenSpaceLightingSsrPass.GetAccumulationKernelName(m_Settings, true));

            m_Settings.speedSmoothReject.Override(true);
            Assert.AreEqual(
                "ScreenSpaceReflectionsAccumulateSmoothSpeedRejectionBothDebug",
                ScreenSpaceLightingSsrPass.GetAccumulationKernelName(m_Settings, true));
        }

        [Test]
        public void DisablingBothMotionSourcesFallsBackToBoth()
        {
            m_Settings.speedSurfaceOnly.Override(false);
            m_Settings.speedTargetOnly.Override(false);
            Assert.AreEqual(
                "ScreenSpaceReflectionsAccumulateNoWorldSpeedRejectionBoth",
                ScreenSpaceLightingSsrPass.GetAccumulationKernelName(m_Settings, false));
        }

        [Test]
        public void AlgorithmSwitchResetsTheAccumulationHistory()
        {
            m_History.EnsureSsrPbrResources(320, 180, 1, TextureDimension.Tex2D);
            int baselineResets = m_History.ssrPbrResetCount;
            m_History.SyncSsrPbrAlgorithm(ScreenSpaceReflectionAlgorithm.PBRAccumulation);
            Assert.AreEqual(baselineResets, m_History.ssrPbrResetCount, "the first selection initializes without a reset");

            m_History.BeginSsrPbrFrame();
            Assert.IsFalse(m_History.ssrPbrHasValidHistory);
            Assert.AreEqual(-1, m_History.ssrPbrReadSlot);
            m_History.SwapSsrPbrBuffers();
            Assert.IsTrue(m_History.ssrPbrHasValidHistory);

            m_History.SyncSsrPbrAlgorithm(ScreenSpaceReflectionAlgorithm.PBRAccumulation);
            Assert.AreEqual(baselineResets, m_History.ssrPbrResetCount, "an unchanged algorithm must not reset");

            m_History.SyncSsrPbrAlgorithm(ScreenSpaceReflectionAlgorithm.Approximation);
            Assert.AreEqual(baselineResets + 1, m_History.ssrPbrResetCount);
            Assert.IsFalse(m_History.ssrPbrHasValidHistory);

            m_History.SyncSsrPbrAlgorithm(ScreenSpaceReflectionAlgorithm.PBRAccumulation);
            Assert.AreEqual(baselineResets + 2, m_History.ssrPbrResetCount);
            Assert.IsFalse(m_History.ssrPbrHasValidHistory);
        }

        [Test]
        public void PbrSlotsAlternateAndNeverOverlapInTheSameFrame()
        {
            m_History.EnsureSsrPbrResources(320, 180, 1, TextureDimension.Tex2D);
            for (int frame = 0; frame < 6; frame++)
            {
                m_History.BeginSsrPbrFrame();
                Assert.AreNotEqual(m_History.ssrPbrReadSlot, m_History.ssrPbrWriteSlot, $"frame {frame} reads the slot it is about to write");
                m_History.SwapSsrPbrBuffers();
            }

            Assert.IsTrue(m_History.ssrPbrHasValidHistory);
        }

        [Test]
        public void PbrResourcesFollowTheCameraResolution()
        {
            m_History.EnsureSsrPbrResources(320, 180, 1, TextureDimension.Tex2D);
            Assert.IsTrue(m_History.ssrPbrResourcesAllocated);
            int generation = m_History.ssrPbrResourceGeneration;

            m_History.EnsureSsrPbrResources(320, 180, 1, TextureDimension.Tex2D);
            Assert.AreEqual(generation, m_History.ssrPbrResourceGeneration);

            m_History.EnsureSsrPbrResources(640, 360, 1, TextureDimension.Tex2D);
            Assert.AreEqual(generation + 1, m_History.ssrPbrResourceGeneration);
        }

        [Test]
        public void CameraResetInvalidatesThePbrAccumulation()
        {
            m_History.EnsureSsrPbrResources(320, 180, 1, TextureDimension.Tex2D);
            m_History.BeginSsrPbrFrame();
            m_History.SwapSsrPbrBuffers();
            Assert.IsTrue(m_History.ssrPbrHasValidHistory);

            int resets = m_History.ssrPbrResetCount;
            m_History.Reset(320, 180, 2);
            Assert.IsFalse(m_History.ssrPbrHasValidHistory);
            Assert.AreEqual(resets + 1, m_History.ssrPbrResetCount);
        }
    }
}