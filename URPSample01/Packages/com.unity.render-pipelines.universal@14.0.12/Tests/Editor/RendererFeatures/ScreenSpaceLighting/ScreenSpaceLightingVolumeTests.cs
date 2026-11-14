using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class ScreenSpaceLightingVolumeTests
    {
        VolumeStack m_Stack;

        [SetUp]
        public void SetUp()
        {
            m_Stack = VolumeManager.instance.CreateStack();
        }

        [TearDown]
        public void TearDown()
        {
            m_Stack?.Dispose();
            m_Stack = null;
        }

        static void AssertMenu<T>(string expectedMenu) where T : VolumeComponent
        {
            var attribute = (VolumeComponentMenuForRenderPipeline)Attribute.GetCustomAttribute(
                typeof(T), typeof(VolumeComponentMenuForRenderPipeline));

            Assert.IsNotNull(attribute);
            Assert.AreEqual(expectedMenu, attribute.menu);
            CollectionAssert.Contains(attribute.pipelineTypes, typeof(UniversalRenderPipeline));
        }

        [Test]
        public void VolumeComponentsUseLightingMenu()
        {
            AssertMenu<ScreenSpaceGlobalIllumination>("Lighting/Screen Space Global Illumination");
            AssertMenu<ScreenSpaceReflection>("Lighting/Screen Space Reflection");
            AssertMenu<ScreenSpaceRefraction>("Lighting/Screen Space Refraction");
        }

        [Test]
        public void VolumeComponentsDefaultToDisabled()
        {
            var ssgi = ScriptableObject.CreateInstance<ScreenSpaceGlobalIllumination>();
            var ssr = ScriptableObject.CreateInstance<ScreenSpaceReflection>();
            var refraction = ScriptableObject.CreateInstance<ScreenSpaceRefraction>();
            try
            {
                Assert.IsFalse(ssgi.enabled.value);
                Assert.IsFalse(ssr.enabled.value);
                Assert.IsFalse(refraction.enabled.value);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(ssgi);
                UnityEngine.Object.DestroyImmediate(ssr);
                UnityEngine.Object.DestroyImmediate(refraction);
            }
        }

        [Test]
        public void VolumeComponentsAreNotPostProcessComponents()
        {
            Assert.IsFalse(typeof(IPostProcessComponent).IsAssignableFrom(typeof(ScreenSpaceGlobalIllumination)));
            Assert.IsFalse(typeof(IPostProcessComponent).IsAssignableFrom(typeof(ScreenSpaceReflection)));
            Assert.IsFalse(typeof(IPostProcessComponent).IsAssignableFrom(typeof(ScreenSpaceRefraction)));
        }

        [Test]
        public void FeatureIsDisabledWhenNoVolumeComponentIsEnabled()
        {
            Assert.IsFalse(ScreenSpaceLightingRendererFeature.HasAnyEffectEnabled(null));
            Assert.IsFalse(ScreenSpaceLightingRendererFeature.HasAnyEffectEnabled(m_Stack));
        }

        [Test]
        public void EachVolumeComponentCanEnableTheFeature()
        {
            var ssgi = m_Stack.GetComponent<ScreenSpaceGlobalIllumination>();
            var ssr = m_Stack.GetComponent<ScreenSpaceReflection>();
            var refraction = m_Stack.GetComponent<ScreenSpaceRefraction>();
            Assert.IsNotNull(ssgi);
            Assert.IsNotNull(ssr);
            Assert.IsNotNull(refraction);

            ssgi.enabled.Override(true);
            Assert.IsTrue(ScreenSpaceLightingRendererFeature.HasAnyEffectEnabled(m_Stack));
            ssgi.enabled.Override(false);

            ssr.enabled.Override(true);
            Assert.IsTrue(ScreenSpaceLightingRendererFeature.HasAnyEffectEnabled(m_Stack));
            ssr.enabled.Override(false);

            refraction.enabled.Override(true);
            Assert.IsTrue(ScreenSpaceLightingRendererFeature.HasAnyEffectEnabled(m_Stack));
        }
    }
}
