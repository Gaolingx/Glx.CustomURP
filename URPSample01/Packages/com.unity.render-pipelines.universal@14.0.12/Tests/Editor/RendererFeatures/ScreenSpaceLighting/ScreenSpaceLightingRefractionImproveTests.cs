using System.IO;
using NUnit.Framework;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    // 05: HDRP alignment contract for the screen-space refraction improvement round.
    [TestFixture]
    class ScreenSpaceLightingRefractionImproveTests
    {
        const string RelativeRoot = "Packages/com.unity.render-pipelines.universal@14.0.12/";

        static string ReadPackageFile(string relativePath)
        {
            var path = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", RelativeRoot, relativePath));
            return File.ReadAllText(path);
        }

        [Test]
        public void ProxyModeValuesMatchShaderContract()
        {
            Assert.AreEqual(0, (int)ScreenSpaceLightingRefractionPass.ProxyMode.None);
            Assert.AreEqual(1, (int)ScreenSpaceLightingRefractionPass.ProxyMode.Box);
            Assert.AreEqual(2, (int)ScreenSpaceLightingRefractionPass.ProxyMode.Infinite);

            string refraction = ReadPackageFile("ShaderLibrary/ScreenSpaceLighting/ScreenSpaceRefraction.hlsl");
            StringAssert.Contains("#define SSL_REFRACTION_PROXY_MODE_NONE 0", refraction);
            StringAssert.Contains("#define SSL_REFRACTION_PROXY_MODE_BOX 1", refraction);
            StringAssert.Contains("#define SSL_REFRACTION_PROXY_MODE_INFINITE 2", refraction);
        }

        [Test]
        public void KeywordLifetimeDoesNotDependOnABoxProjectedProbe()
        {
            string pass = ReadPackageFile("Runtime/RendererFeatures/ScreenSpaceLighting/Passes/RefractionPass.cs");
            // P0-3: no proxy is a valid state (infinite/sky fallback); only missing inputs keep the
            // legacy path. Intensity zero must keep the disabled output byte identical.
            StringAssert.DoesNotContain("if (!TryGetProxy", pass);
            StringAssert.Contains("SelectProxy(", pass);
            StringAssert.Contains("ProxyMode.Infinite", pass);
            StringAssert.Contains("settings.intensity.value > 0f", pass);
        }

        [Test]
        public void ScreenMissContinuesWithProbeFallback()
        {
            string refraction = ReadPackageFile("ShaderLibrary/ScreenSpaceLighting/ScreenSpaceRefraction.hlsl");
            StringAssert.Contains("ScreenSpaceRefractionTransmission", refraction);
            StringAssert.Contains("half remainingWeight = saturate(1.0h - result.weight);", refraction);
            StringAssert.Contains("GlossyEnvironmentReflection(", refraction);
            StringAssert.Contains("probeRadiance * remainingWeight", refraction);
            StringAssert.Contains("result.weight = screenWeight;", refraction);
        }

        [Test]
        public void OpacityIsConsumedAsTransmittanceMask()
        {
            string lit = ReadPackageFile("Shaders/LitForwardPass.hlsl");
            StringAssert.Contains("UniversalFragmentPBRSplit", lit);
            StringAssert.Contains("saturate(1.0h - surfaceData.alpha)", lit);
            StringAssert.Contains("half3 surfaceColor = MixFog(diffuseColor + specularColor, inputData.fogCoord);", lit);
            StringAssert.Contains("half3 foggedDiffuse = MixFog(diffuseColor, inputData.fogCoord);", lit);
            StringAssert.Contains("color.rgb = surfaceColor + transmittanceMask * (transmitted - foggedDiffuse);", lit);
            StringAssert.Contains("color.a = refraction.valid ? 1.0h : surfaceData.alpha;", lit);

            string graph = ReadPackageFile("Editor/ShaderGraph/Includes/PBRForwardPass.hlsl");
            StringAssert.Contains("UniversalFragmentPBRSplit", graph);
            StringAssert.Contains("saturate(1.0h - surface.alpha)", graph);
            StringAssert.Contains("half3 surfaceColor = MixFog(diffuseColor + specularColor, inputData.fogCoord);", graph);
            StringAssert.Contains("half3 foggedDiffuse = MixFog(diffuseColor, inputData.fogCoord);", graph);
            StringAssert.Contains("color.rgb = surfaceColor + transmittanceMask * (transmitted - foggedDiffuse);", graph);
            StringAssert.Contains("color.a = refraction.valid ? 1.0h : surface.alpha;", graph);

            string lighting = ReadPackageFile("ShaderLibrary/Lighting.hlsl");
            StringAssert.Contains("void UniversalFragmentPBRSplit(", lighting);
            StringAssert.Contains("void LightingPhysicallyBasedSplit(", lighting);
            StringAssert.Contains("surfaceData.emission", lighting);
        }

        [Test]
        public void IorFeedsFresnel0OnlyWhileTheEffectIsActive()
        {
            string brdf = ReadPackageFile("ShaderLibrary/BRDF.hlsl");
            StringAssert.Contains("IorToFresnel0(max(_Ior, 1.0))", brdf);
            StringAssert.Contains("&& defined(_SCREEN_SPACE_REFRACTION)", brdf);
            StringAssert.Contains("reflectivity = dot(brdfSpecular, half3(0.2126, 0.7152, 0.0722));", brdf);

            // Same analytic values HDRP documents for IorToFresnel0.
            Assert.AreEqual(0f, IorToFresnel0(1f), 1e-4f);
            Assert.AreEqual(0.04f, IorToFresnel0(1.5f), 1e-4f);
            Assert.AreEqual(0.1837f, IorToFresnel0(2.5f), 1e-4f);
        }

        static float IorToFresnel0(float ior)
        {
            float ratio = (ior - 1f) / (ior + 1f);
            return ratio * ratio;
        }

        [Test]
        public void ColorPyramidMipCountMatchesHdrpLodCount()
        {
            string pass = ReadPackageFile("Runtime/RendererFeatures/ScreenSpaceLighting/Passes/RefractionPass.cs");
            // HDRP passes _ColorPyramidLodCount (the mip count) and the shader derives the last mip
            // index with max(count - 1, 0). Subtract in both places would lose the coarsest mip.
            StringAssert.Contains("Mathf.Max(0, m_History.colorPyramidMipCount)", pass);
            StringAssert.DoesNotContain("m_History.colorPyramidMipCount - 1", pass);

            string refraction = ReadPackageFile("ShaderLibrary/ScreenSpaceLighting/ScreenSpaceRefraction.hlsl");
            StringAssert.Contains("int pyramidMipCount = (int)_SslRefractionParams.x;", refraction);
            StringAssert.Contains("PositivePow(input.perceptualRoughness, 1.3) * (float)max(pyramidMipCount - 1, 0)", refraction);
        }

        [Test]
        public void ThinModelUsesHdrpAbsorptionDistance()
        {
            string refraction = ReadPackageFile("ShaderLibrary/ScreenSpaceLighting/ScreenSpaceRefraction.hlsl");
            StringAssert.Contains("float absorptionDistance = 0.005;", refraction);
            StringAssert.Contains("thickness = 0.005;", refraction);
        }
    }
}
