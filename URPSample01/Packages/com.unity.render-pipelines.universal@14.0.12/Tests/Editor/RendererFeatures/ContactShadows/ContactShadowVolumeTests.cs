using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class ContactShadowVolumeTests
    {
        [Test]
        public void DefaultsAndParameterBoundsMatchContract()
        {
            var volume = ScriptableObject.CreateInstance<ContactShadowsVolume>();
            try
            {
                Assert.IsFalse(volume.enabled.value);
                Assert.AreEqual(0.15f, volume.length.value);
                Assert.AreEqual(1f, volume.opacity.value);
                Assert.AreEqual(0.5f, volume.distanceScaleFactor.value);
                Assert.AreEqual(0.2f, volume.rayBias.value);
                Assert.AreEqual(0.15f, volume.thicknessScale.value);
                Assert.AreEqual(10, volume.sampleCount.value);
                Assert.AreEqual(50f, volume.maxDistance.value);
                Assert.AreEqual(0f, volume.minDistance.value);
                Assert.AreEqual(5f, volume.fadeDistance.value);
                Assert.AreEqual(0f, volume.fadeInDistance.value);
                foreach (var p in new[] { volume.length, volume.opacity, volume.distanceScaleFactor, volume.rayBias, volume.thicknessScale })
                {
                    p.value = -1f;
                    Assert.AreEqual(p == volume.thicknessScale ? 0.02f : 0f, p.value);
                    p.value = 2f;
                    Assert.AreEqual(1f, p.value);
                }
                foreach (var p in new[] { volume.minDistance, volume.maxDistance, volume.fadeDistance, volume.fadeInDistance })
                {
                    p.value = -1f;
                    Assert.AreEqual(0f, p.value);
                }
                volume.sampleCount.value = 0;
                Assert.AreEqual(4, volume.sampleCount.value);
                volume.sampleCount.value = 100;
                Assert.AreEqual(64, volume.sampleCount.value);
            }
            finally { Object.DestroyImmediate(volume); }
        }

        [Test]
        public void DisabledStackDoesNotEnqueueAndMissingComputeWarnsOnce()
        {
            var manager = VolumeManager.instance;
            var original = manager.stack;
            var stack = manager.CreateStack();
            var feature = ScriptableObject.CreateInstance<ScreenSpaceContactShadowsRendererFeature>();
            var id = ScreenSpaceContactShadowsShaderIDs.opacity;
            var originalOpacity = Shader.GetGlobalFloat(id);
            try
            {
                manager.stack = stack;
                var volume = stack.GetComponent<ContactShadowsVolume>();
                var rendering = default(RenderingData);
                Shader.SetGlobalFloat(id, 0.375f);
                feature.AddRenderPasses(null, ref rendering);
                Assert.AreEqual(0.375f, Shader.GetGlobalFloat(id));
                LogAssert.NoUnexpectedReceived();
                if (ScreenSpaceContactShadowsCapability.SupportsApi(SystemInfo.graphicsDeviceType))
                {
                    volume.enabled.value = true;
                    LogAssert.Expect(LogType.Warning, "Screen Space Contact Shadows requires both a depth pyramid compute shader and a contact shadows compute shader.");
                    feature.AddRenderPasses(null, ref rendering);
                    feature.AddRenderPasses(null, ref rendering);
                    LogAssert.NoUnexpectedReceived();
                }
            }
            finally
            {
                Shader.SetGlobalFloat(id, originalOpacity);
                manager.stack = original;
                manager.DestroyStack(stack);
                Object.DestroyImmediate(feature);
            }
        }
    }
}
