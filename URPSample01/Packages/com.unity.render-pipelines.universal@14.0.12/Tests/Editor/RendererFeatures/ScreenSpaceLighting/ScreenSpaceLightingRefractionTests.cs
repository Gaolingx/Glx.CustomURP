using System.IO;
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

        // F-05: a camera that never runs the binding pass (preview / reflection / overlay / rejected)
        // must not inherit the previous camera's refraction keyword or material-facing bindings. The
        // player evidence is in validation/P4-1; this guards the wiring that makes it possible.
        [Test]
        public void PerCameraClearResetsTheRefractionMaterialBinding()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var path = Path.Combine(
                projectRoot, "Packages", "com.unity.render-pipelines.universal@14.0.12",
                "Runtime", "ScriptableRenderer.cs");
            Assert.IsTrue(File.Exists(path), $"URP renderer source not found at {path}.");

            var source = File.ReadAllText(path);
            int start = source.IndexOf("static void ClearRenderingState(CommandBuffer cmd)", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, "ClearRenderingState is missing.");
            int end = source.IndexOf("internal void Clear(CameraRenderType cameraType)", start, System.StringComparison.Ordinal);
            Assert.Greater(end, start, "ClearRenderingState end marker is missing.");
            var body = source.Substring(start, end - start);

            StringAssert.Contains("cmd.DisableShaderKeyword(ShaderKeywordStrings.RefractionKeyword);", body);
            StringAssert.Contains("s_RefractionParamsID", body);
            StringAssert.Contains("s_RefractionProbeCountID", body);
            StringAssert.Contains("s_RefractionColorPyramidID", body);
            StringAssert.Contains("s_RefractionDepthTextureID", body);

            StringAssert.Contains("Shader.PropertyToID(\"_SslRefractionParams\")", source);
            StringAssert.Contains("Shader.PropertyToID(\"_SslRefractionProbeCount\")", source);
            StringAssert.Contains("Shader.PropertyToID(\"_SslRefractionColorPyramid\")", source);
            StringAssert.Contains("Shader.PropertyToID(\"_SslRefractionDepthTexture\")", source);
        }
    }
}