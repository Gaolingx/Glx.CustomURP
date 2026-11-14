using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    // T11: the refraction bindings run after the color pyramid and the transparent prepass, and
    // before transparent SSR and the URP transparent draw. The volume defaults mirror HDRP and the
    // effect is opt-in, so a missing override never changes an existing camera.
    [TestFixture]
    class ScreenSpaceLightingRefractionTests
    {
        [Test]
        public void VolumeDefaultsMatchHDRPAndAreOptOut()
        {
            var refraction = ScriptableObject.CreateInstance<ScreenSpaceRefraction>();
            try
            {
                Assert.IsFalse(refraction.enabled.value, "Screen-space refraction must be opt-in by default.");
                Assert.AreEqual(0.1f, refraction.screenFadeDistance.value, 1e-6f);
                Assert.AreEqual(1f, refraction.intensity.value, 1e-6f);
            }
            finally
            {
                Object.DestroyImmediate(refraction);
            }
        }

        [Test]
        public void RefractionBindingsRunBeforeTransparentDraw()
        {
            int bindings = (int)ScreenSpaceLightingRefractionPass.k_RenderPassEvent;
            Assert.AreEqual(448, bindings);
            Assert.Greater(bindings, (int)ScreenSpaceLightingTransparentDepthNormalsPass.k_RenderPassEvent);
            Assert.Less(bindings, (int)ScreenSpaceLightingTransparentSsrPass.k_RenderPassEvent);
            Assert.Less(bindings, (int)RenderPassEvent.BeforeRenderingTransparents);
            Assert.Greater(bindings, (int)RenderPassEvent.AfterRenderingSkybox);
        }

        [Test]
        public void KeywordStringsStayStable()
        {
            Assert.AreEqual("_SCREEN_SPACE_REFRACTION", ShaderKeywordStrings.RefractionKeyword);
            Assert.AreEqual("_REFRACTION_PLANE", ShaderKeywordStrings.RefractionPlane);
            Assert.AreEqual("_REFRACTION_SPHERE", ShaderKeywordStrings.RefractionSphere);
            Assert.AreEqual("_REFRACTION_THIN", ShaderKeywordStrings.RefractionThin);
        }

        [Test]
        public void FeatureOwnsTheRefractionPass()
        {
            var feature = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
            try
            {
                feature.Create();
                Assert.IsNotNull(feature.refractionPass, "The Screen Space Lighting feature must create the refraction binding pass.");
            }
            finally
            {
                Object.DestroyImmediate(feature);
            }
        }
    }
}