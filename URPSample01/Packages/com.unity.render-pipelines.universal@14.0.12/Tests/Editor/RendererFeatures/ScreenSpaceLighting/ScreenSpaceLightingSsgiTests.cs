using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class ScreenSpaceLightingSsgiTests
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
        public void SsgiPassOrderIsTraceThenBindThenCleanup()
        {
            Assert.AreEqual(205, (int)ScreenSpaceLightingSsgiPass.k_RenderPassEvent);
            Assert.AreEqual(207, (int)ScreenSpaceLightingSetupPass.k_RenderPassEvent);
            Assert.AreEqual(300, (int)ScreenSpaceLightingCleanupPass.k_RenderPassEvent);
            Assert.Less((int)ScreenSpaceLightingSsgiPass.k_RenderPassEvent, (int)RenderPassEvent.BeforeRenderingGbuffer);
            Assert.Greater((int)ScreenSpaceLightingCleanupPass.k_RenderPassEvent, (int)RenderPassEvent.BeforeRenderingOpaques);
        }

        [Test]
        public void VolumeComponentExposesSsgiQualityControls()
        {
            var ssgi = ScriptableObject.CreateInstance<ScreenSpaceGlobalIllumination>();
            try
            {
                Assert.IsFalse(ssgi.enabled.value);
                Assert.IsTrue(ssgi.fullResolution.value);
                Assert.AreEqual(32, ssgi.maxRaySteps.value);
                Assert.AreEqual(0.1f, ssgi.depthBufferThickness.value, 1e-5f);
                Assert.AreEqual(1f, ssgi.intensity.value, 1e-5f);
                Assert.IsTrue(ssgi.temporalAccumulation.value);
                Assert.IsTrue(ssgi.denoise.value);
                Assert.IsTrue(ssgi.secondDenoiserPass.value);
                Assert.IsFalse(ssgi.halfResolutionDenoiser.value);
                Assert.IsTrue(ssgi.bilateralUpsample.value);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(ssgi);
            }
        }

        [Test]
        public void FirstSsgiFrameHasNoReadableHistoryButStillHasAWriteSlot()
        {
            m_History.BeginSsgiFrame();

            Assert.IsFalse(m_History.ssgiHasValidHistory);
            Assert.AreEqual(-1, m_History.ssgiReadSlot);
            Assert.AreEqual(1, m_History.ssgiWriteSlot);
            Assert.IsNull(m_History.GetSsgiHistoryHF(m_History.ssgiReadSlot));
        }

        [Test]
        public void SsgiSlotsAlternateAndNeverOverlapInTheSameFrame()
        {
            for (int frame = 0; frame < 6; frame++)
            {
                m_History.BeginSsgiFrame();
                Assert.AreNotEqual(m_History.ssgiReadSlot, m_History.ssgiWriteSlot, $"frame {frame} reads the slot it is about to write");
                m_History.SwapSsgiBuffers();
            }

            Assert.IsTrue(m_History.ssgiHasValidHistory);
        }

        [Test]
        public void ResetInvalidatesSsgiHistory()
        {
            m_History.BeginSsgiFrame();
            m_History.SwapSsgiBuffers();

            m_History.Reset(320, 180, 2);

            Assert.IsFalse(m_History.ssgiHasValidHistory);
            Assert.AreEqual(0, m_History.ssgiBufferIndex);
            Assert.AreEqual(-1, m_History.ssgiReadSlot);
        }

        [Test]
        public void SsgiResourcesFollowTheTraceResolution()
        {
            m_History.EnsureSsgiResources(320, 180, 160, 90, 1, TextureDimension.Tex2D);

            Assert.IsTrue(m_History.ssgiResourcesAllocated);
            Assert.AreEqual(160, m_History.ssgiTraceWidth);
            Assert.AreEqual(90, m_History.ssgiTraceHeight);

            int generation = m_History.ssgiResourceGeneration;
            m_History.EnsureSsgiResources(320, 180, 160, 90, 1, TextureDimension.Tex2D);
            Assert.AreEqual(generation, m_History.ssgiResourceGeneration, "an unchanged size must not reallocate");

            m_History.EnsureSsgiResources(320, 180, 320, 180, 1, TextureDimension.Tex2D);
            Assert.AreEqual(generation + 1, m_History.ssgiResourceGeneration);
            Assert.AreEqual(320, m_History.ssgiTraceWidth);
            Assert.AreEqual(180, m_History.ssgiTraceHeight);
        }

        [Test]
        public void SsgiConstantBufferLayoutMatchesTheGeneratedInclude()
        {
            var expectedFields = new[]
            {
                "_SsgiScreenSize",
                "_SsgiTraceSize",
                "_SsgiRayMarchingParams",
                "_SsgiRayMarchingParams2",
                "_SsgiZBufferParams",
                "_SsgiColorPyramidScaleAndLimitPrev",
                "_SsgiTemporalParams",
                "_SsgiDenoiserParams",
                "_SsgiCameraPositionWS",
                "_SsgiPrevInvViewProj",
            };

            var fields = typeof(ScreenSpaceLightingSsgiShaderVariables)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(field => field.Name)
                .ToArray();

            CollectionAssert.AreEqual(expectedFields, fields);

            foreach (var field in typeof(ScreenSpaceLightingSsgiShaderVariables).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.IsTrue(
                    field.FieldType == typeof(Vector4) || field.FieldType == typeof(Matrix4x4),
                    $"{field.Name} uses {field.FieldType.Name}; only Vector4 and Matrix4x4 map to the HLSL buffer.");
            }

            // Nine Vector4 blocks and one matrix = 144 + 64 bytes.
            Assert.AreEqual(9 * 16 + 1 * 64, System.Runtime.InteropServices.Marshal.SizeOf<ScreenSpaceLightingSsgiShaderVariables>());
        }

        [Test]
        public void FeatureEnqueuesSsgiPassForEveryVolumeToggle()
        {
            var camera = ScreenSpaceTestUtils.CreateCamera("SsgiCamera");
            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            rendererData.debugShaders = new ScriptableRendererData.DebugShaderResources();
            var renderer = new ScreenSpaceTestUtils.TestRenderer(rendererData);
            var feature = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
            var stack = VolumeManager.instance.CreateStack();
            var previousStack = VolumeManager.instance.stack;
            VolumeManager.instance.stack = stack;
            try
            {
                feature.Create();
                stack.GetComponent<ScreenSpaceGlobalIllumination>().enabled.Override(true);
                var renderingData = ScreenSpaceTestUtils.CreateRenderingData(ScreenSpaceTestUtils.CreateCameraData(camera));
                feature.AddRenderPasses(renderer, ref renderingData);

                Assert.Contains(feature.ssgiPass, renderer.queuedPasses);
                Assert.Contains(feature.cleanupPass, renderer.queuedPasses);
                // Scheduling is by event, not enqueue order: the SSGI dispatch must run before the
                // material binding pass.
                Assert.Less(
                    (int)feature.ssgiPass.renderPassEvent,
                    (int)feature.setupPass.renderPassEvent);
            }
            finally
            {
                VolumeManager.instance.stack = previousStack;
                stack.Dispose();
                feature.Dispose();
                UnityEngine.Object.DestroyImmediate(feature);
                renderer.Dispose();
                UnityEngine.Object.DestroyImmediate(rendererData);
                ScreenSpaceTestUtils.DestroyCamera(camera);
            }
        }
    }
}