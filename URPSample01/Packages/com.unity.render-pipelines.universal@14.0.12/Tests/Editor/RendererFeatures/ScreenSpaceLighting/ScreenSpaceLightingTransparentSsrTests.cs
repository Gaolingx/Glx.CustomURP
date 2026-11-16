using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    // T10: the transparent prepass runs before the transparent trace, the trace runs before URP
    // draws transparents, and the transparent resources never alias the opaque output.
    [TestFixture]
    class ScreenSpaceLightingTransparentSsrTests
    {
        ScreenSpaceLightingHistory m_History;

        static string PackageRoot => Path.Combine(
            Path.GetDirectoryName(Application.dataPath),
            "Packages",
            "com.unity.render-pipelines.universal@14.0.12");

        static string ReadPackageFile(string relativePath)
        {
            string path = Path.Combine(PackageRoot, relativePath);
            Assert.IsTrue(File.Exists(path), $"Expected package file at {path}.");
            return File.ReadAllText(path);
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
        public void TransparentPassesRunBetweenColorPyramidAndTransparents()
        {
            Assert.AreEqual(447, (int)ScreenSpaceLightingTransparentDepthNormalsPass.k_RenderPassEvent);
            Assert.AreEqual(449, (int)ScreenSpaceLightingTransparentSsrPass.k_RenderPassEvent);
            Assert.Less(
                (int)ScreenSpaceLightingTransparentDepthNormalsPass.k_RenderPassEvent,
                (int)ScreenSpaceLightingTransparentSsrPass.k_RenderPassEvent);
            Assert.Less(
                (int)ScreenSpaceLightingTransparentSsrPass.k_RenderPassEvent,
                (int)RenderPassEvent.BeforeRenderingTransparents);
            Assert.Greater(
                (int)ScreenSpaceLightingTransparentDepthNormalsPass.k_RenderPassEvent,
                (int)RenderPassEvent.AfterRenderingSkybox);
        }

        [Test]
        public void VolumeExposesTransparentOptIn()
        {
            var ssr = ScriptableObject.CreateInstance<ScreenSpaceReflection>();
            try
            {
                Assert.IsFalse(ssr.enabled.value);
                Assert.IsFalse(ssr.enabledTransparent.value, "Transparent SSR must be opt-in by default.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(ssr);
            }
        }

        [Test]
        public void TransparentResourcesAllocateIndependentlyFromTheOpaqueOutput()
        {
            m_History.BeginFrame();
            m_History.EnsureSsrResources(320, 180, 1, TextureDimension.Tex2D);
            m_History.EnsureTransparentSsrResources(320, 180, 1, TextureDimension.Tex2D);

            Assert.IsTrue(m_History.transparentResourcesAllocated);
            Assert.IsNotNull(m_History.transparentDepth);
            Assert.IsNotNull(m_History.transparentNormals);
            Assert.IsNotNull(m_History.ssrTransparentHitPoint);
            Assert.IsNotNull(m_History.ssrTransparentOutput);
            Assert.AreNotSame(m_History.ssrOutput, m_History.ssrTransparentOutput);
            Assert.AreNotSame(m_History.ssrHitPoint, m_History.ssrTransparentHitPoint);
            Assert.IsFalse(m_History.ssrTransparentOutputProduced);
        }

        [Test]
        public void TransparentResourcesFollowTheCameraResolution()
        {
            m_History.EnsureTransparentSsrResources(320, 180, 1, TextureDimension.Tex2D);
            int generation = m_History.transparentResourceGeneration;

            m_History.EnsureTransparentSsrResources(320, 180, 1, TextureDimension.Tex2D);
            Assert.AreEqual(generation, m_History.transparentResourceGeneration, "an unchanged size must not reallocate");

            m_History.EnsureTransparentSsrResources(640, 360, 1, TextureDimension.Tex2D);
            Assert.AreEqual(generation + 1, m_History.transparentResourceGeneration);
            Assert.AreEqual(640, m_History.transparentWidth);
            Assert.AreEqual(360, m_History.transparentHeight);
        }

        [Test]
        public void TransparentOutputIsClearedEveryFrame()
        {
            m_History.EnsureTransparentSsrResources(320, 180, 1, TextureDimension.Tex2D);
            m_History.BeginFrame();
            m_History.MarkSsrTransparentOutputProduced();
            Assert.IsTrue(m_History.ssrTransparentOutputProduced);

            m_History.BeginFrame();
            Assert.IsFalse(m_History.ssrTransparentOutputProduced, "the previous transparent result must not be reused");
        }

        [Test]
        public void TransparentResultNeverUsesPbrAccumulation()
        {
            var pass = new ScreenSpaceLightingTransparentSsrPass();
            Assert.IsFalse(pass.usingPbrAccumulation);
            Assert.IsNull(pass.outputTexture, "no result is exposed before the pass produced one.");
        }

        [Test]
        public void TransparentPrepassAndTraceShareTheRenderSerial()
        {
            var prepass = new ScreenSpaceLightingTransparentDepthNormalsPass();
            Assert.AreEqual(-1, prepass.producedFrameSerial);
            Assert.IsFalse(prepass.producedOutput);

            m_History.BeginFrame();
            int serial = m_History.frameSerial;
            m_History.BeginFrame();
            Assert.AreEqual(serial + 1, m_History.frameSerial, "each camera render advances the serial once.");
        }

        [Test]
        public void DepthSeedAppliesTheCopyDepthYFlipRule()
        {
            string pass = ReadPackageFile(
                "Runtime/RendererFeatures/ScreenSpaceLighting/Passes/TransparentDepthNormalRenderPass.cs");

            // CopyDepthPass flips when source and destination handles live in different y
            // orientations; the seed copy must follow the same rule instead of a fixed scale bias.
            StringAssert.Contains("cameraData.IsHandleYFlipped(depthSource)", pass);
            StringAssert.Contains("cameraData.IsHandleYFlipped(m_History.transparentDepth)", pass);
            StringAssert.Contains("yflip ? -1f : 1f", pass);
            StringAssert.Contains("yflip ? 1f : 0f", pass);
        }

        [Test]
        public void TransparentTraceOwnsEverySharedKeyword()
        {
            string pass = ReadPackageFile(
                "Runtime/RendererFeatures/ScreenSpaceLighting/Passes/TransparentSsrPass.cs");

            // The SSR compute shader is shared with the opaque pass; the transparent pass must set
            // its own half resolution / blue-noise / normals state instead of inheriting it.
            StringAssert.Contains("ScreenSpaceLightingShaderIDs.SsrHalfResKeyword, false", pass);
            StringAssert.Contains("ScreenSpaceLightingShaderIDs.SsgiBlueNoiseKeyword, m_Resources.hasSsrBlueNoise", pass);
            StringAssert.Contains("ScreenSpaceLightingShaderIDs.SsrNormalsOctKeyword, false", pass);
            StringAssert.Contains("BindSsrBlueNoise(cmd, compute, traceKernel)", pass);
        }

        [Test]
        public void TransparentPassUsesAnIsolatedComputeInstance()
        {
            var guids = AssetDatabase.FindAssets("ScreenSpaceReflections t:ComputeShader");
            Assert.Greater(guids.Length, 0, "the SSR compute shader must be importable");
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(AssetDatabase.GUIDToAssetPath(guids[0]));
            Assert.IsNotNull(shader);

            // The opaque and transparent passes need different variants of the same kernels in one
            // frame. Compute keywords are asset state, so two objects are required.
            var instance = UnityEngine.Object.Instantiate(shader);
            try
            {
                Assert.IsNotNull(instance);
                Assert.AreNotSame(shader, instance);
                shader.EnableKeyword("SSR_APPROX");
                Assert.IsTrue(shader.IsKeywordEnabled("SSR_APPROX"));
                Assert.IsFalse(instance.IsKeywordEnabled("SSR_APPROX"));
            }
            finally
            {
                if (instance != null)
                    UnityEngine.Object.DestroyImmediate(instance);
                shader.DisableKeyword("SSR_APPROX");
            }

            string pass = ReadPackageFile(
                "Runtime/RendererFeatures/ScreenSpaceLighting/Passes/TransparentSsrPass.cs");
            StringAssert.Contains("m_Resources.ssrTransparentCompute", pass);
        }

        [Test]
        public void KeywordConstantsMatchTheShaderContract()
        {
            Assert.AreEqual("_SCREEN_SPACE_TRANSPARENT_SSR", ShaderKeywordStrings.ScreenSpaceTransparentSSR);
            Assert.AreEqual("_TRANSPARENT_SSR_RECEIVE", ShaderKeywordStrings.TransparentSSRReceive);
            Assert.AreEqual(ShaderKeywordStrings.ScreenSpaceTransparentSSR, ScreenSpaceLightingShaderIDs.ScreenSpaceTransparentReflectionKeyword);
        }

        [Test]
        public void FeatureQueuesTransparentPrepassBeforeTransparentTrace()
        {
            var camera = ScreenSpaceTestUtils.CreateCamera("TransparentSsrCamera");
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
                var ssr = stack.GetComponent<ScreenSpaceReflection>();
                ssr.enabled.Override(true);
                ssr.enabledTransparent.Override(true);
                var renderingData = ScreenSpaceTestUtils.CreateRenderingData(ScreenSpaceTestUtils.CreateCameraData(camera));
                feature.AddRenderPasses(renderer, ref renderingData);

                Assert.Contains(feature.transparentDepthNormalsPass, renderer.queuedPasses);
                Assert.Contains(feature.transparentSsrPass, renderer.queuedPasses);
                Assert.Less(
                    (int)feature.transparentDepthNormalsPass.renderPassEvent,
                    (int)feature.transparentSsrPass.renderPassEvent);
                Assert.Less(
                    (int)feature.transparentSsrPass.renderPassEvent,
                    (int)RenderPassEvent.BeforeRenderingTransparents);
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