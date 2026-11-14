using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Rendering.Universal;
using UnityEngine.Rendering.Universal;
using ShaderPrefilteringData = UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset.ShaderPrefilteringData;
using PrefilteringMode = UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset.PrefilteringMode;
using PrefilteringModeMainLightShadows = UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset.PrefilteringModeMainLightShadows;
using PrefilteringModeAdditionalLights = UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset.PrefilteringModeAdditionalLights;

namespace ShaderStrippingAndPrefiltering
{
    class ShaderPrefilteringTests
    {
        class TestHelper
        {
            internal bool isAssetUsingforward = true;
            internal bool stripXRKeywords = true;
            internal bool stripHDRKeywords = true;
            internal bool stripDebugDisplay = true;
            internal bool stripScreenCoordOverride = true;
            internal bool stripUnusedVariants = true;
            internal ShaderPrefilteringData defaultPrefilteringData;

            public TestHelper()
            {
                defaultPrefilteringData = CreatePrefilteringSettings(ShaderFeatures.None);
            }

            internal ShaderPrefilteringData CreatePrefilteringSettings(ShaderFeatures shaderFeatures)
            {
                return ShaderBuildPreprocessor.CreatePrefilteringSettings(ref shaderFeatures, isAssetUsingforward, stripXRKeywords, stripHDRKeywords, stripDebugDisplay, stripScreenCoordOverride, stripUnusedVariants);
            }

            internal void AssertPrefilteringData(ShaderPrefilteringData expected, ShaderPrefilteringData actual)
            {
                Assert.AreEqual(expected.forwardPlusPrefilteringMode, actual.forwardPlusPrefilteringMode, "forwardPlusPrefilteringMode mismatch");
                Assert.AreEqual(expected.deferredPrefilteringMode, actual.deferredPrefilteringMode, "deferredPrefilteringMode mismatch");
                Assert.AreEqual(expected.mainLightShadowsPrefilteringMode, actual.mainLightShadowsPrefilteringMode, "mainLightShadowsPrefilteringMode mismatch");
                Assert.AreEqual(expected.additionalLightsPrefilteringMode, actual.additionalLightsPrefilteringMode, "additionalLightsPrefilteringMode mismatch");
                Assert.AreEqual(expected.additionalLightsShadowsPrefilteringMode, actual.additionalLightsShadowsPrefilteringMode, "additionalLightsShadowsPrefilteringMode mismatch");
                Assert.AreEqual(expected.screenSpaceOcclusionPrefilteringMode, actual.screenSpaceOcclusionPrefilteringMode, "screenSpaceOcclusionPrefilteringMode mismatch");

                Assert.AreEqual(expected.stripXRKeywords, actual.stripXRKeywords, "stripXRKeywords mismatch");
                Assert.AreEqual(expected.stripHDRKeywords, actual.stripHDRKeywords, "stripHDRKeywords mismatch");
                Assert.AreEqual(expected.stripDebugDisplay, actual.stripDebugDisplay, "stripDebugDisplay mismatch");
                Assert.AreEqual(expected.stripScreenCoordOverride, actual.stripScreenCoordOverride, "stripScreenCoordOverride mismatch");
                Assert.AreEqual(expected.stripWriteRenderingLayers, actual.stripWriteRenderingLayers, "stripWriteRenderingLayers mismatch");
                Assert.AreEqual(expected.stripDBufferMRT1, actual.stripDBufferMRT1, "stripDBufferMRT1 mismatch");
                Assert.AreEqual(expected.stripDBufferMRT2, actual.stripDBufferMRT2, "stripDBufferMRT2 mismatch");
                Assert.AreEqual(expected.stripDBufferMRT3, actual.stripDBufferMRT3, "stripDBufferMRT2 mismatch");
                Assert.AreEqual(expected.stripNativeRenderPass, actual.stripNativeRenderPass, "stripNativeRenderPass mismatch");

                Assert.AreEqual(expected, actual, "Some mismatch between the Prefiltering Data that is not covered in the previous tests.");
            }
        }

        [Test]
        public void TestCreatePrefilteringSettings_NoFeatures()
        {
            ShaderPrefilteringData actual;
            ShaderPrefilteringData expected;
            TestHelper helper = new();

            expected = helper.defaultPrefilteringData;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.None);
            helper.AssertPrefilteringData(expected, actual);
        }

        [Test]
        public void TestCreatePrefilteringSettings_RenderingModes()
        {
            ShaderPrefilteringData actual;
            ShaderPrefilteringData expected;
            TestHelper helper = new();

            // Forward Plus only in use
            helper.isAssetUsingforward = false;
            expected = helper.defaultPrefilteringData;
            expected.forwardPlusPrefilteringMode = PrefilteringMode.SelectOnly;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.ForwardPlus);
            helper.AssertPrefilteringData(expected, actual);

            // Deferred only in use
            helper.isAssetUsingforward = false;
            expected = helper.defaultPrefilteringData;
            expected.deferredPrefilteringMode = PrefilteringMode.SelectOnly;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.DeferredShading);
            helper.AssertPrefilteringData(expected, actual);

            // Forward & Forward+ in use
            helper.isAssetUsingforward = true;
            expected = helper.defaultPrefilteringData;
            expected.forwardPlusPrefilteringMode = PrefilteringMode.Select;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.ForwardPlus);
            helper.AssertPrefilteringData(expected, actual);

            // Forward & Deferred in use
            helper.isAssetUsingforward = true;
            expected = helper.defaultPrefilteringData;
            expected.deferredPrefilteringMode = PrefilteringMode.Select;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.DeferredShading);
            helper.AssertPrefilteringData(expected, actual);

            // Forward+ & Deferred in use
            helper.isAssetUsingforward = false;
            expected = helper.defaultPrefilteringData;
            expected.forwardPlusPrefilteringMode = PrefilteringMode.Select;
            expected.deferredPrefilteringMode = PrefilteringMode.Select;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.ForwardPlus | ShaderFeatures.DeferredShading);
            helper.AssertPrefilteringData(expected, actual);

            // Forward, Forward+ & Deferred in use
            helper.isAssetUsingforward = true;
            expected = helper.defaultPrefilteringData;
            expected.forwardPlusPrefilteringMode = PrefilteringMode.Select;
            expected.deferredPrefilteringMode = PrefilteringMode.Select;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.ForwardPlus | ShaderFeatures.DeferredShading);
            helper.AssertPrefilteringData(expected, actual);
        }

        [Test]
        public void TestCreatePrefilteringSettings_AdditionalLights()
        {
            ShaderPrefilteringData actual;
            ShaderPrefilteringData expected;
            TestHelper helper = new();

            // Vertex with and without the OFF variant
            expected = helper.defaultPrefilteringData;
            expected.additionalLightsPrefilteringMode = PrefilteringModeAdditionalLights.SelectVertex;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.AdditionalLightsVertex);
            helper.AssertPrefilteringData(expected, actual);

            expected = helper.defaultPrefilteringData;
            expected.additionalLightsPrefilteringMode = PrefilteringModeAdditionalLights.SelectVertexAndOff;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.AdditionalLightsVertex | ShaderFeatures.AdditionalLightsKeepOffVariants);
            helper.AssertPrefilteringData(expected, actual);

            // Pixel with and without the OFF variant
            expected = helper.defaultPrefilteringData;
            expected.additionalLightsPrefilteringMode = PrefilteringModeAdditionalLights.SelectPixel;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.AdditionalLightsPixel);
            helper.AssertPrefilteringData(expected, actual);

            expected = helper.defaultPrefilteringData;
            expected.additionalLightsPrefilteringMode = PrefilteringModeAdditionalLights.SelectPixelAndOff;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.AdditionalLightsPixel | ShaderFeatures.AdditionalLightsKeepOffVariants);
            helper.AssertPrefilteringData(expected, actual);
        }

        [Test]
        public void TestCreatePrefilteringSettings_Shadows()
        {
            ShaderPrefilteringData actual;
            ShaderPrefilteringData expected;
            TestHelper helper = new();

            // Main Light Shadows
            expected = helper.defaultPrefilteringData;
            expected.mainLightShadowsPrefilteringMode = PrefilteringModeMainLightShadows.SelectMainLight;
            expected.additionalLightsShadowsPrefilteringMode = PrefilteringMode.Remove;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.MainLightShadows);
            helper.AssertPrefilteringData(expected, actual);

            // Main Light Shadows & OFF
            expected = helper.defaultPrefilteringData;
            expected.mainLightShadowsPrefilteringMode = PrefilteringModeMainLightShadows.SelectMainLightAndOff;
            expected.additionalLightsShadowsPrefilteringMode = PrefilteringMode.Remove;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.MainLightShadows | ShaderFeatures.ShadowsKeepOffVariants);
            helper.AssertPrefilteringData(expected, actual);

            // Main Light Shadows & Cascade
            expected = helper.defaultPrefilteringData;
            expected.mainLightShadowsPrefilteringMode = PrefilteringModeMainLightShadows.SelectMainLightAndCascades;
            expected.additionalLightsShadowsPrefilteringMode = PrefilteringMode.Remove;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.MainLightShadows | ShaderFeatures.MainLightShadowsCascade);
            helper.AssertPrefilteringData(expected, actual);

            // Main Light Shadows & Cascade & OFF
            expected = helper.defaultPrefilteringData;
            expected.mainLightShadowsPrefilteringMode = PrefilteringModeMainLightShadows.SelectAll;
            expected.additionalLightsShadowsPrefilteringMode = PrefilteringMode.Remove;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.MainLightShadows | ShaderFeatures.MainLightShadowsCascade | ShaderFeatures.ShadowsKeepOffVariants);
            helper.AssertPrefilteringData(expected, actual);
        }

        [Test]
        public void TestCreatePrefilteringSettings_GlobalSettings()
        {
            ShaderPrefilteringData actual;
            ShaderPrefilteringData expected;
            TestHelper helper = new();

            // XR
            helper.stripXRKeywords = false;
            expected = helper.defaultPrefilteringData;
            expected.stripXRKeywords = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.None);
            helper.AssertPrefilteringData(expected, actual);

            helper.stripXRKeywords = true;
            expected = helper.defaultPrefilteringData;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.None);
            helper.AssertPrefilteringData(expected, actual);

            // HDR
            helper.stripHDRKeywords = false;
            expected = helper.defaultPrefilteringData;
            expected.stripHDRKeywords = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.None);
            helper.AssertPrefilteringData(expected, actual);

            helper.stripHDRKeywords = true;
            expected = helper.defaultPrefilteringData;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.None);
            Assert.AreEqual(true, actual.stripHDRKeywords);
            helper.AssertPrefilteringData(expected, actual);

            // Rendering Debugger
            helper.stripDebugDisplay = false;
            expected = helper.defaultPrefilteringData;
            expected.stripDebugDisplay = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.None);
            helper.AssertPrefilteringData(expected, actual);

            helper.stripDebugDisplay = true;
            expected = helper.defaultPrefilteringData;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.None);
            helper.AssertPrefilteringData(expected, actual);

            // Screen Coord Override
            helper.stripScreenCoordOverride = false;
            expected = helper.defaultPrefilteringData;
            expected.stripScreenCoordOverride = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.None);
            helper.AssertPrefilteringData(expected, actual);

            helper.stripScreenCoordOverride = true;
            expected = helper.defaultPrefilteringData;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.None);
            helper.AssertPrefilteringData(expected, actual);
        }

        [Test]
        public void TestCreatePrefilteringSettings_RenderingLayers()
        {
            ShaderPrefilteringData actual;
            ShaderPrefilteringData expected;
            TestHelper helper = new();

            expected = helper.defaultPrefilteringData;
            expected.stripWriteRenderingLayers = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.OpaqueWriteRenderingLayers);
            helper.AssertPrefilteringData(expected, actual);

            expected = helper.defaultPrefilteringData;
            expected.stripWriteRenderingLayers = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.DepthNormalPassRenderingLayers);
            helper.AssertPrefilteringData(expected, actual);

            expected = helper.defaultPrefilteringData;
            expected.stripWriteRenderingLayers = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.GBufferWriteRenderingLayers);
            helper.AssertPrefilteringData(expected, actual);
        }

        [Test]
        public void TestCreatePrefilteringSettings_Decals()
        {
            ShaderPrefilteringData actual;
            ShaderPrefilteringData expected;
            TestHelper helper = new();

            expected = helper.defaultPrefilteringData;
            expected.stripDBufferMRT1 = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.DBufferMRT1);
            helper.AssertPrefilteringData(expected, actual);

            expected = helper.defaultPrefilteringData;
            expected.stripDBufferMRT2 = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.DBufferMRT2);
            helper.AssertPrefilteringData(expected, actual);

            expected = helper.defaultPrefilteringData;
            expected.stripDBufferMRT3 = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.DBufferMRT3);
            helper.AssertPrefilteringData(expected, actual);

            expected = helper.defaultPrefilteringData;
            expected.stripDBufferMRT1 = false;
            expected.stripDBufferMRT2 = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.DBufferMRT1 | ShaderFeatures.DBufferMRT2);
            helper.AssertPrefilteringData(expected, actual);

            expected = helper.defaultPrefilteringData;
            expected.stripDBufferMRT1 = false;
            expected.stripDBufferMRT3 = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.DBufferMRT1 | ShaderFeatures.DBufferMRT3);
            helper.AssertPrefilteringData(expected, actual);

            expected = helper.defaultPrefilteringData;
            expected.stripDBufferMRT2 = false;
            expected.stripDBufferMRT3 = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.DBufferMRT2 | ShaderFeatures.DBufferMRT3);
            helper.AssertPrefilteringData(expected, actual);

            expected = helper.defaultPrefilteringData;
            expected.stripDBufferMRT1 = false;
            expected.stripDBufferMRT2 = false;
            expected.stripDBufferMRT3 = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.DBufferMRT1 | ShaderFeatures.DBufferMRT2 | ShaderFeatures.DBufferMRT3);
            helper.AssertPrefilteringData(expected, actual);
        }

        [Test]
        public void TestCreatePrefilteringSettings_NativeRenderPass()
        {
            ShaderPrefilteringData actual;
            ShaderPrefilteringData expected;
            TestHelper helper = new();

            // Enabled Render Pass
            expected = helper.defaultPrefilteringData;
            expected.stripNativeRenderPass = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.RenderPassEnabled);
            helper.AssertPrefilteringData(expected, actual);
        }

        [Test]
        public void TestCreatePrefilteringSettings_ScreenSpaceOcclusion()
        {
            // The built-in SSAO feature was removed. GTAO consumes the generic keyword, so the AO
            // prefiltering mode always keeps both variants regardless of the feature flags.
            TestHelper helper = new();

            ShaderPrefilteringData actual = helper.CreatePrefilteringSettings(ShaderFeatures.None);
            Assert.AreEqual(PrefilteringMode.Select, actual.screenSpaceOcclusionPrefilteringMode);

            helper.stripUnusedVariants = false;
            actual = helper.CreatePrefilteringSettings(ShaderFeatures.All);
            Assert.AreEqual(PrefilteringMode.Select, actual.screenSpaceOcclusionPrefilteringMode);
        }

    }
}
