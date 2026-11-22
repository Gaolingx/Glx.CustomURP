using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class ScreenSpaceLightingProbeAtlasTests
    {
        static ForwardLights.InitParams CreateInitParams(bool forwardPlus, bool publishProbeAtlas)
        {
            return new ForwardLights.InitParams
            {
                lightCookieManager = null,
                forwardPlus = forwardPlus,
                publishProbeAtlas = publishProbeAtlas
            };
        }

        [Test]
        public void ProbeAtlasIsOptInByDefault()
        {
            var settings = new ScreenSpaceLightingSettings();
            Assert.IsFalse(settings.publishProbeAtlas);
        }

        [Test]
        public void NonForwardPlusDoesNotCreateManagerWithoutTheToggle()
        {
            var lights = new ForwardLights(CreateInitParams(false, false));

            Assert.IsFalse(lights.reflectionProbeManager.isCreated,
                "A renderer without the feature toggle must not pay for the probe atlas.");

            lights.Cleanup();
        }

        [Test]
        public void NonForwardPlusDefersRequestedAtlasUntilCameraSetup()
        {
            var lights = new ForwardLights(CreateInitParams(false, true));

            Assert.IsTrue(lights.publishProbeAtlas);
            Assert.IsFalse(lights.reflectionProbeManager.isCreated);
            Assert.IsNull(lights.reflectionProbeManager.atlasRT);

            lights.Cleanup();
        }

        [Test]
        public void RuntimeToggleUpdatesThePublishedFlag()
        {
            var lights = new ForwardLights(CreateInitParams(false, false));
            Assert.IsFalse(lights.publishProbeAtlas);

            lights.SetProbeAtlasEnabled(true);
            Assert.IsTrue(lights.publishProbeAtlas);

            lights.SetProbeAtlasEnabled(false);
            Assert.IsFalse(lights.publishProbeAtlas);

            lights.Cleanup();
        }

        [Test]
        public void ForwardPlusRemembersAtlasRequestForFallbackWithoutAllocatingAtConstruction()
        {
            var lights = new ForwardLights(CreateInitParams(true, false));

            Assert.IsFalse(lights.reflectionProbeManager.isCreated);
            Assert.IsFalse(lights.publishProbeAtlas);

            lights.SetProbeAtlasEnabled(true);

            Assert.IsTrue(lights.publishProbeAtlas);
            Assert.IsFalse(lights.reflectionProbeManager.isCreated);

            lights.Cleanup();
        }

        [Test]
        public void CleanupReleasesTheManagerAndCanRunTwice()
        {
            var lights = new ForwardLights(CreateInitParams(false, true));

            lights.Cleanup();
            Assert.IsFalse(lights.reflectionProbeManager.isCreated);

            Assert.DoesNotThrow(() => lights.Cleanup());
        }

        [Test]
        public void ForwardPlusCleanupCanRunTwice()
        {
            var lights = new ForwardLights(CreateInitParams(true, false));

            lights.Cleanup();

            Assert.DoesNotThrow(() => lights.Cleanup());
        }

        [Test]
        public void RendererFeatureLookupFindsTheScreenSpaceLightingFeature()
        {
            var data = ScriptableObject.CreateInstance<UniversalRendererData>();
            var feature = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
            try
            {
                data.rendererFeatures.Add(feature);

                Assert.IsTrue(data.TryGetRendererFeature(out ScreenSpaceLightingRendererFeature found));
                Assert.AreSame(feature, found);

                found.settings.publishProbeAtlas = true;
                Assert.IsTrue(feature.settings.publishProbeAtlas);
            }
            finally
            {
                Object.DestroyImmediate(feature);
                Object.DestroyImmediate(data);
            }
        }
    }
}
