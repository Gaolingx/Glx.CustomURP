using System.IO;
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
    class ScreenSpaceLightingSsrTests
    {
        ScreenSpaceLightingHistory m_History;

        static string ReadPackageFile(string relativePath)
        {
            string packageRoot = Path.Combine(
                Path.GetDirectoryName(Application.dataPath),
                "Packages",
                "com.unity.render-pipelines.universal@14.0.12");
            string path = Path.Combine(packageRoot, relativePath);
            Assert.IsTrue(File.Exists(path), $"Expected package file at {path}.");
            return File.ReadAllText(path);
        }

        [Test]
        public void SsrDepthPyramidMaxMipTracksTheBuiltChain()
        {
            Assert.AreEqual(0, ScreenSpaceLightingRenderUtils.ComputeDepthPyramidMaxMip(0));
            Assert.AreEqual(0, ScreenSpaceLightingRenderUtils.ComputeDepthPyramidMaxMip(1));
            Assert.AreEqual(10, ScreenSpaceLightingRenderUtils.ComputeDepthPyramidMaxMip(11));
            Assert.AreEqual(14, ScreenSpaceLightingRenderUtils.ComputeDepthPyramidMaxMip(15));
            Assert.AreEqual(14, ScreenSpaceLightingRenderUtils.ComputeDepthPyramidMaxMip(16));
        }

        [Test]
        public void SsrTraceUsesTheUploadedDepthPyramidMaxMip()
        {
            string source = ReadPackageFile("Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute");
            StringAssert.Contains("#define _SsrDepthPyramidMaxMip (int)_SsrPyramidParams.y", source);
            StringAssert.DoesNotContain("SSR_MIP_LEVEL_CLAMP", source);
        }

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
        public void SsrPassRunsAfterSsgiAndBeforeMaterialBinding()
        {
            Assert.AreEqual((int)RenderPassEvent.AfterRenderingGbuffer + 1, (int)ScreenSpaceLightingSsrPass.k_RenderPassEvent);
            Assert.Less((int)ScreenSpaceLightingSsgiPass.k_RenderPassEvent, (int)ScreenSpaceLightingSsrPass.k_RenderPassEvent);
            Assert.Less((int)ScreenSpaceLightingSsrPass.k_RenderPassEvent, (int)ScreenSpaceLightingSsrBindPass.k_RenderPassEvent);
            Assert.Less((int)ScreenSpaceLightingSsrBindPass.k_RenderPassEvent,
                (int)ScreenSpaceLightingSsrIndirectSpecularPass.k_RenderPassEvent);
        }

        [Test]
        public void VolumeComponentExposesApproximationControls()
        {
            var ssr = ScriptableObject.CreateInstance<ScreenSpaceReflection>();
            try
            {
                Assert.IsFalse(ssr.enabled.value);
                Assert.AreEqual(0.9f, ssr.minSmoothness.value, 1e-5f);
                Assert.AreEqual(0.9f, ssr.smoothnessFadeStart.value, 1e-5f);
                Assert.AreEqual(0.01f, ssr.depthBufferThickness.value, 1e-5f);
                Assert.AreEqual(0.1f, ssr.screenFadeDistance.value, 1e-5f);
                Assert.AreEqual(64, ssr.rayMaxIterations.value);
                Assert.IsTrue(ssr.reflectSky.value);
                Assert.AreEqual(50f, ssr.rayLength.value, 1e-5f);
                Assert.AreEqual(0.01f, ssr.rayLength.min, 1e-5f);
                Assert.AreEqual(1f, ssr.intensity.value, 1e-5f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(ssr);
            }
        }

        [Test]
        public void SsrTracingModeDefaultsToRayTracedOnly()
        {
            var settings = new ScreenSpaceLightingSettings();
            Assert.AreEqual(SsrTracingMode.RayTracedOnly, settings.ssrTracingMode);
            // Existing feature assets predate the field, so its serialized default must stay index 0
            // or they would silently switch to the mixed path.
            Assert.AreEqual(0, (int)SsrTracingMode.RayTracedOnly);
            Assert.AreEqual(1, (int)SsrTracingMode.ScreenSpaceFirst);
        }

        [Test]
        public void FirstFrameHasNoPreviousColorButStillAllocatesTheOutput()
        {
            m_History.BeginFrame();
            m_History.EnsureSsrResources(320, 180, 1, TextureDimension.Tex2D);

            Assert.IsNull(m_History.readColorPyramid);
            Assert.IsTrue(m_History.ssrResourcesAllocated);
            Assert.IsNotNull(m_History.ssrOutput);
            Assert.IsNotNull(m_History.ssrHitPoint);
        }

        [Test]
        public void SsrResourcesFollowTheCameraResolution()
        {
            m_History.EnsureSsrResources(320, 180, 1, TextureDimension.Tex2D);
            int generation = m_History.ssrResourceGeneration;

            m_History.EnsureSsrResources(320, 180, 1, TextureDimension.Tex2D);
            Assert.AreEqual(generation, m_History.ssrResourceGeneration, "an unchanged size must not reallocate");

            m_History.EnsureSsrResources(640, 360, 1, TextureDimension.Tex2D);
            Assert.AreEqual(generation + 1, m_History.ssrResourceGeneration);
            Assert.AreEqual(640, m_History.ssrWidth);
            Assert.AreEqual(360, m_History.ssrHeight);
        }

        [Test]
        public void SsrConstantBufferLayoutMatchesTheGeneratedInclude()
        {
            var expectedFields = new[]
            {
                "_SsrScreenSize",
                "_SsrTraceSize",
                "_SsrRayMarchingParams",
                "_SsrRoughnessFadeParams",
                "_SsrPyramidParams",
                "_SsrColorPyramidScaleAndLimitPrev",
                "_SsrCameraPositionWS",
                "_SsrPbrParams",
                "_SsrPbrFlags",
            };

            var fields = typeof(ScreenSpaceLightingSsrShaderVariables)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(field => field.Name)
                .ToArray();

            CollectionAssert.AreEqual(expectedFields, fields);

            foreach (var field in typeof(ScreenSpaceLightingSsrShaderVariables).GetFields(BindingFlags.Public | BindingFlags.Instance))
                Assert.AreEqual(typeof(Vector4), field.FieldType, $"{field.Name} does not map to a Vector4 constant buffer slot.");

            Assert.AreEqual(9 * 16, System.Runtime.InteropServices.Marshal.SizeOf<ScreenSpaceLightingSsrShaderVariables>());
        }

        [Test]
        public void FeatureEnqueuesSsrPassWhenOnlyTheReflectionVolumeIsEnabled()
        {
            var camera = ScreenSpaceTestUtils.CreateCamera("SsrCamera");
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
                stack.GetComponent<ScreenSpaceGlobalIllumination>().enabled.Override(false);
                stack.GetComponent<ScreenSpaceReflection>().enabled.Override(true);
                var renderingData = ScreenSpaceTestUtils.CreateRenderingData(ScreenSpaceTestUtils.CreateCameraData(camera));
                feature.AddRenderPasses(renderer, ref renderingData);

                Assert.Contains(feature.ssrPass, renderer.queuedPasses);
                Assert.Contains(feature.setupPass, renderer.queuedPasses);
                Assert.IsFalse(feature.ssgiPass.producedOutput);
                Assert.Less(
                    (int)feature.setupPass.renderPassEvent,
                    (int)feature.ssrPass.renderPassEvent);
                Assert.Contains(feature.ssrBindPass, renderer.queuedPasses);
                Assert.Less(
                    (int)feature.ssrPass.renderPassEvent,
                    (int)feature.ssrBindPass.renderPassEvent);
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