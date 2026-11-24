using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    internal class ContactShadowCapabilityTests
    {
        [TestCase(GraphicsDeviceType.Direct3D11)]
        [TestCase(GraphicsDeviceType.Direct3D12)]
        [TestCase(GraphicsDeviceType.Vulkan)]
        [TestCase(GraphicsDeviceType.Metal)]
        public void SupportedApiRequiresPackedStore(GraphicsDeviceType api)
        {
            var shared = ScreenSpaceCapabilityResult.Supported();
            var supported = ScreenSpaceContactShadowsCapability.Evaluate(api, shared, true);
            Assert.IsTrue(supported.supported);
            Assert.AreEqual(ScreenSpaceCapabilityStatus.Supported, supported.status);

            var missingFormat = ScreenSpaceContactShadowsCapability.Evaluate(api, shared, false);
            Assert.IsFalse(missingFormat.supported);
            Assert.AreEqual(ScreenSpaceCapabilityStatus.RequiredFormatUnsupported, missingFormat.status);
        }

        [TestCase(GraphicsDeviceType.OpenGLCore)]
        [TestCase(GraphicsDeviceType.OpenGLES3)]
        [TestCase(GraphicsDeviceType.Null)]
        public void UnsupportedApiRejectsEvenWithPackedStore(GraphicsDeviceType api)
        {
            var result = ScreenSpaceContactShadowsCapability.Evaluate(api, ScreenSpaceCapabilityResult.Supported(), true);
            Assert.IsFalse(result.supported);
            Assert.AreNotEqual(ScreenSpaceCapabilityStatus.Supported, result.status);
        }

        [TestCase(GraphicsDeviceType.OpenGLCore, true)]
        [TestCase(GraphicsDeviceType.Direct3D11, false)]
        public void UnsupportedFeatureWarnsOnceAndDoesNotQueue(GraphicsDeviceType api, bool packedStore)
        {
            var manager = VolumeManager.instance;
            var previous = manager.stack;
            var stack = manager.CreateStack();
            var camera = ScreenSpaceTestUtils.CreateCamera("Unsupported Contact Shadows");
            var data = ScriptableObject.CreateInstance<UniversalRendererData>();
            var feature = ScriptableObject.CreateInstance<ScreenSpaceContactShadowsRendererFeature>();
            try
            {
                ResourceReloader.ReloadAllNullIn(data, UniversalRenderPipelineAsset.packagePath);
                using var renderer = new ScreenSpaceTestUtils.TestRenderer(data);
                var result = ScreenSpaceContactShadowsCapability.Evaluate(api, ScreenSpaceCapabilityResult.Supported(), packedStore);
                typeof(ScreenSpaceContactShadowsRendererFeature).GetField("m_Platform", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(feature, result);
                manager.stack = stack;
                stack.GetComponent<ContactShadowsVolume>().enabled.value = true;
                var rendering = ScreenSpaceTestUtils.CreateRenderingData(ScreenSpaceTestUtils.CreateCameraData(camera));
                LogAssert.Expect(LogType.Warning, result.message);
                feature.AddRenderPasses(renderer, ref rendering);
                feature.AddRenderPasses(renderer, ref rendering);
                Assert.IsEmpty(renderer.queuedPasses);
                Assert.IsFalse(feature.TryGetHistory(camera, 0, out _));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                manager.stack = previous;
                manager.DestroyStack(stack);
                feature.Dispose();
                Object.DestroyImmediate(feature);
                Object.DestroyImmediate(data);
                ScreenSpaceTestUtils.DestroyCamera(camera);
            }
        }

        [TestCase(ScreenSpaceCapabilityStatus.ComputeShadersUnsupported)]
        [TestCase(ScreenSpaceCapabilityStatus.DepthPyramidFormatUnsupported)]
        [TestCase(ScreenSpaceCapabilityStatus.RequiredFormatUnsupported)]
        [TestCase(ScreenSpaceCapabilityStatus.CameraUnsupported)]
        [TestCase(ScreenSpaceCapabilityStatus.XrLayoutUnsupported)]
        public void SharedFailurePreservesStatusAndDiagnostic(ScreenSpaceCapabilityStatus status)
        {
            var shared = ScreenSpaceCapabilityResult.Unsupported(status, "Diagnostic from the shared capability check");
            var result = ScreenSpaceContactShadowsCapability.Evaluate(GraphicsDeviceType.Direct3D11, shared, false);
            Assert.IsFalse(result.supported);
            Assert.AreEqual(shared.status, result.status);
            Assert.AreEqual(shared.message, result.message);
        }
    }
}
