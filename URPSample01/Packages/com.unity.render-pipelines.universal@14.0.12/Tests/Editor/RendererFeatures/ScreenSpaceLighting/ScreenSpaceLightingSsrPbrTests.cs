using NUnit.Framework;
using UnityEditor;
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
        public void VolumeExposesReflectionDenoiserControls()
        {
            Assert.AreEqual(ScreenSpaceReflectionAlgorithm.Approximation, m_Settings.usedAlgorithm.value);
            Assert.IsTrue(m_Settings.fullResolution.value);
            Assert.AreEqual(8, m_Settings.denoiserRadius.value);
            Assert.AreEqual(1, m_Settings.denoiserRadius.min);
            Assert.AreEqual(32, m_Settings.denoiserRadius.max);
            Assert.IsFalse(m_Settings.affectSmoothSurfaces.value);
            Assert.IsTrue(m_Settings.singleReflectionBounce.value);
            Assert.AreEqual(0.5f, m_Settings.biasFactor.value, 1e-5f);
        }

        [Test]
        public void ReflectionFilterMappingIsPackagedWithTheHdrpImportSettings()
        {
            var resources = new ScreenSpaceLightingResources();
            var mapping = resources.ssrReflectionFilterMapping;

            Assert.IsNotNull(mapping);
            Assert.IsFalse(ReferenceEquals(mapping, Texture2D.whiteTexture), "the packaged LUT must replace the white fallback");
            Assert.AreEqual(128, mapping.width);
            Assert.AreEqual(128, mapping.height);

            var path = AssetDatabase.GetAssetPath(mapping);
            Assert.IsFalse(string.IsNullOrEmpty(path), "the LUT must be resolved through a Resources folder");
            Assert.IsTrue(path.EndsWith("SsrDenoise/ReflectionKernelMapping.png"), path);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            Assert.IsNotNull(importer);
            Assert.IsTrue(importer.sRGBTexture, "HDRP decodes the LUT through the sRGB sampler");
            Assert.IsTrue(importer.mipmapEnabled, "HDRP samples the LUT with mipmaps enabled");
        }

        [Test]
        public void PbrResourcesCarryTheDenoiseIntermediatesAndSampleCounts()
        {
            Assert.IsFalse(m_History.ssrPbrResourcesAllocated);
            m_History.EnsureSsrPbrResources(320, 180, 320, 180, 1, TextureDimension.Tex2D);

            Assert.IsTrue(m_History.ssrPbrResourcesAllocated);
            Assert.IsNotNull(m_History.ssrPbrOriginal);
            Assert.IsNotNull(m_History.ssrDenoiseIntermediate0);
            Assert.IsNotNull(m_History.ssrDenoiseIntermediate1);
            Assert.AreNotSame(m_History.ssrDenoiseIntermediate0, m_History.ssrDenoiseIntermediate1);
        }

        [Test]
        public void EachHistorySlotOwnsItsSampleCountSlot()
        {
            m_History.EnsureSsrPbrResources(320, 180, 320, 180, 1, TextureDimension.Tex2D);

            Assert.IsNotNull(m_History.GetSsrPbrSampleCount(0));
            Assert.IsNotNull(m_History.GetSsrPbrSampleCount(1));
            Assert.AreNotSame(m_History.GetSsrPbrSampleCount(0), m_History.GetSsrPbrSampleCount(1));
            Assert.AreNotSame(m_History.GetSsrPbrHistory(0), m_History.GetSsrPbrHistory(1));
            Assert.IsNull(m_History.GetSsrPbrSampleCount(-1));
            Assert.IsNull(m_History.GetSsrPbrSampleCount(ScreenSpaceLightingHistory.k_HistoryBufferCount));
        }

        [Test]
        public void PbrSampleCountsSurviveAHistorySwap()
        {
            m_History.EnsureSsrPbrResources(320, 180, 320, 180, 1, TextureDimension.Tex2D);
            var slotZero = m_History.GetSsrPbrSampleCount(0);
            var slotOne = m_History.GetSsrPbrSampleCount(1);

            m_History.BeginSsrPbrFrame();
            Assert.AreEqual(slotOne, m_History.GetSsrPbrSampleCount(m_History.ssrPbrWriteSlot));
            m_History.SwapSsrPbrBuffers();
            m_History.BeginSsrPbrFrame();
            Assert.AreEqual(slotZero, m_History.GetSsrPbrSampleCount(m_History.ssrPbrWriteSlot));
            Assert.AreEqual(slotOne, m_History.GetSsrPbrSampleCount(m_History.ssrPbrReadSlot));
        }

        [Test]
        public void AlgorithmSwitchResetsTheAccumulationHistory()
        {
            m_History.EnsureSsrPbrResources(320, 180, 320, 180, 1, TextureDimension.Tex2D);
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
            m_History.EnsureSsrPbrResources(320, 180, 320, 180, 1, TextureDimension.Tex2D);
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
            m_History.EnsureSsrPbrResources(320, 180, 320, 180, 1, TextureDimension.Tex2D);
            Assert.IsTrue(m_History.ssrPbrResourcesAllocated);
            int generation = m_History.ssrPbrResourceGeneration;

            m_History.EnsureSsrPbrResources(320, 180, 320, 180, 1, TextureDimension.Tex2D);
            Assert.AreEqual(generation, m_History.ssrPbrResourceGeneration);

            m_History.EnsureSsrPbrResources(640, 360, 640, 360, 1, TextureDimension.Tex2D);
            Assert.AreEqual(generation + 1, m_History.ssrPbrResourceGeneration);
        }

        [Test]
        public void HalfResolutionPbrResourcesFollowTheTraceSize()
        {
            m_History.EnsureSsrPbrResources(320, 180, 160, 90, 1, TextureDimension.Tex2D);
            Assert.IsTrue(m_History.ssrPbrResourcesAllocated);
            Assert.IsNotNull(m_History.ssrPbrHitPoint);
            Assert.AreEqual(160, m_History.ssrPbrWidth);
            Assert.AreEqual(90, m_History.ssrPbrHeight);
            int generation = m_History.ssrPbrResourceGeneration;

            m_History.EnsureSsrPbrResources(320, 180, 160, 90, 1, TextureDimension.Tex2D);
            Assert.AreEqual(generation, m_History.ssrPbrResourceGeneration, "an unchanged trace size must not reallocate");

            m_History.EnsureSsrPbrResources(320, 180, 320, 180, 1, TextureDimension.Tex2D);
            Assert.AreEqual(generation + 1, m_History.ssrPbrResourceGeneration, "switching the trace size reallocates");
            Assert.AreEqual(320, m_History.ssrPbrWidth);
        }

        [Test]
        public void CameraResetInvalidatesThePbrAccumulation()
        {
            m_History.EnsureSsrPbrResources(320, 180, 320, 180, 1, TextureDimension.Tex2D);
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