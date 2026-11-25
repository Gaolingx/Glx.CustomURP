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
        [Test]
        public void SupportedPlatformRequiresPackedStore()
        {
            var shared = ScreenSpaceCapabilityResult.Supported();
            var supported = ScreenSpaceContactShadowsCapability.Evaluate(shared, true);
            Assert.IsTrue(supported.supported);
            Assert.AreEqual(ScreenSpaceCapabilityStatus.Supported, supported.status);

            var missingFormat = ScreenSpaceContactShadowsCapability.Evaluate(shared, false);
            Assert.IsFalse(missingFormat.supported);
            Assert.AreEqual(ScreenSpaceCapabilityStatus.RequiredFormatUnsupported, missingFormat.status);
        }

        [Test]
        public void UnsupportedFeatureWarnsOnceAndDoesNotQueue()
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
                var result = ScreenSpaceContactShadowsCapability.Evaluate(ScreenSpaceCapabilityResult.Supported(), false);
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
            var result = ScreenSpaceContactShadowsCapability.Evaluate(shared, false);
            Assert.IsFalse(result.supported);
            Assert.AreEqual(shared.status, result.status);
            Assert.AreEqual(shared.message, result.message);
        }
    }
}
