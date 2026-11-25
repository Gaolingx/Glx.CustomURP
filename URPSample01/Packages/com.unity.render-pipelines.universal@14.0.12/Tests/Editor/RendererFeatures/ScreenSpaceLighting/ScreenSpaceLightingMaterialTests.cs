using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace UnityEditor.Rendering.Universal.Tests
{
    // T9 material coverage: the Core-owned material contract must expose Screen Space Lighting only
    // where the material supplies the data the plugin needs, and clear coat must use its own mask and
    // roughness instead of reusing the base layer smoothness.
    [TestFixture]
    class ScreenSpaceLightingMaterialTests
    {
        static string PackageRoot => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Packages", "com.unity.render-pipelines.universal@14.0.12");

        static string ReadPackageFile(string relativePath)
        {
            string path = Path.Combine(PackageRoot, relativePath);
            Assert.IsTrue(File.Exists(path), $"Expected package file at {path}.");
            return File.ReadAllText(path);
        }

        static void AssertContains(string source, string expected, string label)
        {
            StringAssert.Contains(expected, source, $"{label} is missing '{expected}'.");
        }

        [Test]
        public void LitDeclaresScreenSpaceLightingKeywordsForForwardAndGbuffer()
        {
            string lit = ReadPackageFile("Shaders/Lit.shader");

            int forward = lit.IndexOf("Name \"ForwardLit\"");
            int gbuffer = lit.IndexOf("Name \"GBuffer\"");
            int depthOnly = lit.IndexOf("Name \"DepthOnly\"");
            Assert.Greater(forward, -1);
            Assert.Greater(gbuffer, forward);
            Assert.Greater(depthOnly, gbuffer);

            string forwardBlock = lit.Substring(forward, gbuffer - forward);
            string gbufferBlock = lit.Substring(gbuffer, depthOnly - gbuffer);

            AssertContains(forwardBlock, "_SCREEN_SPACE_GI", "Lit ForwardLit");
            AssertContains(forwardBlock, "_SCREEN_SPACE_GI_PROBE", "Lit ForwardLit");
            AssertContains(forwardBlock, "_SCREEN_SPACE_SSR", "Lit ForwardLit");
            AssertContains(gbufferBlock, "_SCREEN_SPACE_GI", "Lit GBuffer");
            AssertContains(gbufferBlock, "_SCREEN_SPACE_GI_PROBE", "Lit GBuffer");
            AssertContains(gbufferBlock, "_SCREEN_SPACE_SSR", "Lit GBuffer");
        }

        [Test]
        public void ComplexLitDepthNormalsTraceTheCoatRoughness()
        {
            string complexLit = ReadPackageFile("Shaders/ComplexLit.shader");
            int depthNormals = complexLit.IndexOf("Name \"DepthNormalsOnly\"");
            Assert.Greater(depthNormals, -1, "ComplexLit must keep its DepthNormalsOnly pass.");
            string depthBlock = complexLit.Substring(depthNormals);
            AssertContains(depthBlock, "_CLEARCOAT", "ComplexLit DepthNormalsOnly");

            string depthNormalsPass = ReadPackageFile("Shaders/LitDepthNormalsPass.hlsl");
            AssertContains(depthNormalsPass, "#if defined(_CLEARCOAT) || defined(_CLEARCOATMAP)", "LitDepthNormalsPass");
            AssertContains(depthNormalsPass, "CLEAR_COAT_SSR_PERCEPTUAL_ROUGHNESS", "LitDepthNormalsPass");
            AssertContains(depthNormalsPass, "SampleClearCoat", "LitDepthNormalsPass");
        }

        [Test]
        public void ClearCoatBlendUsesIndependentMaskAndRoughness()
        {
            string blend = ReadPackageFile("ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingBlend.hlsl");

            AssertContains(blend, "ScreenSpaceLightingEvaluateIndirectSpecularClearCoat", "Screen Space Lighting blend");
            AssertContains(blend, "CLEAR_COAT_PERCEPTUAL_ROUGHNESS", "Screen Space Lighting blend");
            AssertContains(blend, "coatMask", "Screen Space Lighting blend");
            AssertContains(blend, "coatEnvironmentSpecular", "Screen Space Lighting blend");
            // The coat lobe must consume the trace with its own weight and the base lobe only through
            // the coat hierarchy blend; a single mixed value applied to both lobes would double count.
            AssertContains(blend, "baseHierarchyWeight", "Screen Space Lighting blend");

            string illumination = ReadPackageFile("ShaderLibrary/GlobalIllumination.hlsl");
            AssertContains(illumination, "ScreenSpaceLightingApplyIndirectSpecularClearCoat", "GlobalIllumination");
        }

        [Test]
        public void ShaderGraphLitWiresScreenSpaceLighting()
        {
            string target = ReadPackageFile("Editor/ShaderGraph/Targets/UniversalTarget.cs");
            AssertContains(target, "referenceName = \"_SCREEN_SPACE_GI\"", "ShaderGraph keyword descriptors");
            AssertContains(target, "referenceName = \"_SCREEN_SPACE_GI_PROBE\"", "ShaderGraph keyword descriptors");
            AssertContains(target, "referenceName = \"_SCREEN_SPACE_SSR\"", "ShaderGraph keyword descriptors");

            string litSubTarget = ReadPackageFile("Editor/ShaderGraph/Targets/UniversalLitSubTarget.cs");
            AssertContains(litSubTarget, "CoreKeywordDescriptors.ScreenSpaceGlobalIllumination", "ShaderGraph Lit keywords");
            AssertContains(litSubTarget, "CoreKeywordDescriptors.ScreenSpaceGlobalIlluminationProbe", "ShaderGraph Lit keywords");
            AssertContains(litSubTarget, "CoreKeywordDescriptors.ScreenSpaceReflections", "ShaderGraph Lit keywords");

            string gbufferPass = ReadPackageFile("Editor/ShaderGraph/Includes/PBRGBufferPass.hlsl");
            AssertContains(gbufferPass, "inputData.normalizedScreenSpaceUV", "ShaderGraph PBRGBufferPass");

            string depthNormalsPass = ReadPackageFile("Editor/ShaderGraph/Includes/DepthNormalsOnlyPass.hlsl");
            AssertContains(depthNormalsPass, "encodedSmoothness", "ShaderGraph DepthNormalsOnlyPass");
            AssertContains(depthNormalsPass, "surfaceDescription.CoatMask", "ShaderGraph DepthNormalsOnlyPass");
        }

        [Test]
        public void UnsupportedMaterialsKeepZeroSmoothnessEncoding()
        {
            string simpleLit = ReadPackageFile("Shaders/SimpleLitDepthNormalsPass.hlsl");
            StringAssert.Contains("half4(packedNormalWS, 0.0)", simpleLit);
            StringAssert.Contains("half4(normalWS, 0.0)", simpleLit);

            string particles = ReadPackageFile("Shaders/Particles/ParticlesDepthNormalsPass.hlsl");
            StringAssert.Contains("half4(NormalizeNormalPerPixel(normalWS), 0.0)", particles);
            StringAssert.Contains("half4(packedNormalWS, 0.0)", particles);

            string terrain = ReadPackageFile("Shaders/Terrain/TerrainLitDepthNormalsPass.hlsl");
            StringAssert.Contains("half4(normalWS, 0.0)", terrain);

            // Unsupported shaders must not declare the Screen Space Lighting keywords either.
            foreach (var path in new[]
                     {
                         "Shaders/SimpleLit.shader",
                         "Shaders/Particles/ParticlesLit.shader",
                         "Shaders/Terrain/TerrainLit.shader",
                     })
            {
                string shader = ReadPackageFile(path);
                StringAssert.DoesNotContain("_SCREEN_SPACE_GI", shader, $"{path} must not consume SSGI.");
                StringAssert.DoesNotContain("_SCREEN_SPACE_SSR", shader, $"{path} must not consume SSR.");
            }
        }

        [Test]
        public void CoatHierarchyKeepsAMissOnTheProbes()
        {
            string blend = ReadPackageFile("ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingBlend.hlsl");
            // A miss (weight 0) must leave both lobes untouched: the fallback assignments are the
            // environment reflections, never black or a second probe.
            AssertContains(blend, "baseSpecular = environmentSpecular;", "Screen Space Lighting blend");
            AssertContains(blend, "coatSpecular = coatEnvironmentSpecular;", "Screen Space Lighting blend");
        }

        [Test]
        public void TransparentSsrIsOptInAndUsesItsOwnResult()
        {
            string lit = ReadPackageFile("Shaders/Lit.shader");
            string complexLit = ReadPackageFile("Shaders/ComplexLit.shader");
            foreach (var shader in new[] { lit, complexLit })
            {
                AssertContains(shader, "\"LightMode\" = \"UniversalTransparentDepthNormals\"", "transparent depth normals light mode");
                AssertContains(shader, "_TRANSPARENT_SSR_RECEIVE", "transparent SSR material keyword");
                AssertContains(shader, "_SCREEN_SPACE_TRANSPARENT_SSR", "transparent SSR global keyword");
                AssertContains(shader, "_TransparentSSRReceive", "transparent SSR material property");
            }

            string sharedPass = ReadPackageFile("Shaders/Utils/TransparentDepthNormalsPass.hlsl");
            AssertContains(sharedPass, "#if defined(_TRANSPARENT_SSR_RECEIVE)", "shared transparent prepass");
            AssertContains(sharedPass, "clip(-1.0)", "shared transparent prepass");
            AssertContains(sharedPass, "DepthNormalsFragment", "shared transparent prepass");

            string blend = ReadPackageFile("ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingBlend.hlsl");
            AssertContains(blend, "_ScreenSpaceTransparentSsrTexture", "transparent SSR blend input");
            AssertContains(blend, "_ScreenSpaceTransparentSsrParams", "transparent SSR blend input");
            AssertContains(blend, "defined(_TRANSPARENT_SSR_RECEIVE) && defined(_SCREEN_SPACE_TRANSPARENT_SSR)", "transparent SSR blend branch");

            // Materials without the transparent pass can never contribute a ray origin.
            foreach (var path in new[]
                     {
                         "Shaders/SimpleLit.shader",
                         "Shaders/Particles/ParticlesLit.shader",
                         "Shaders/Terrain/TerrainLit.shader",
                     })
            {
                StringAssert.DoesNotContain("UniversalTransparentDepthNormals", ReadPackageFile(path), $"{path} must not be drawn by the transparent prepass.");
                StringAssert.DoesNotContain("_SCREEN_SPACE_TRANSPARENT_SSR", ReadPackageFile(path), $"{path} must not consume transparent SSR.");
            }
        }

        [Test]
        public void LitDeclaresRefractionContractWithSafeDefaults()
        {
            foreach (var shaderPath in new[] { "Shaders/Lit.shader", "Shaders/ComplexLit.shader" })
            {
                string shader = ReadPackageFile(shaderPath);
                AssertContains(shader, "_RefractionModel", shaderPath);
                AssertContains(shader, "[Enum(None, 0, Box, 1, Sphere, 2, Thin, 3)]", shaderPath);
                AssertContains(shader, "_Ior", shaderPath);
                AssertContains(shader, "_Thickness", shaderPath);
                AssertContains(shader, "_TransmittanceColor", shaderPath);
                AssertContains(shader, "_TransmittanceDistance", shaderPath);
                AssertContains(shader, "_REFRACTION_PLANE", shaderPath);
                AssertContains(shader, "_REFRACTION_SPHERE", shaderPath);
                AssertContains(shader, "_REFRACTION_THIN", shaderPath);
                AssertContains(shader, "_SCREEN_SPACE_REFRACTION", shaderPath);

                // The model must default to None so the old transparent output is preserved.
                StringAssert.Contains("_RefractionModel(\"Refraction Model\", Float) = 0.0", shader, $"{shaderPath} must default the refraction model to None.");

                string input = ReadPackageFile("Shaders/LitInput.hlsl");
                AssertContains(input, "float _Ior;", "LitInput refraction uniforms");
                AssertContains(input, "float _TransmittanceDistance;", "LitInput refraction uniforms");
            }
        }

        [Test]
        public void RefractionMaterialPathFallsBackWithoutInputs()
        {
            string refraction = ReadPackageFile("ShaderLibrary/ScreenSpaceLighting/ScreenSpaceRefraction.hlsl");
            // Missing keyword, zero intensity and an unavailable pyramid keep the legacy output; a
            // missing proxy only disables the screen trace and leaves the probe fallback active.
            AssertContains(refraction, "if (proxyMode == SSL_REFRACTION_PROXY_MODE_NONE)", "refraction proxy guard");
            AssertContains(refraction, "if (pyramidMipCount <= 0 || intensity <= 0.0)", "refraction pyramid guard");
            AssertContains(refraction, "ScreenSpaceRefractionEdgeFade", "refraction edge fade");
            AssertContains(refraction, "ScreenSpaceRefractionToAbsorption", "refraction transmittance");
            AssertContains(refraction, "RefractionModelSphere", "refraction sphere model");
            AssertContains(refraction, "RefractionModelBox", "refraction box model");
            AssertContains(refraction, "LinearEyeDepth(hitDeviceDepth, _ZBufferParams)", "refraction depth rejection");
            AssertContains(refraction, "_SslRefractionColorPyramid", "refraction color pyramid input");
            AssertContains(refraction, "(1.0h - input.specularFGD)", "refraction fresnel transmission");
            AssertContains(refraction, "SSL_REFRACTION_PROXY_MODE_INFINITE", "infinite proxy fallback");

            // P0-1: the screen miss continues with the reflection probe/sky for the remaining weight.
            AssertContains(refraction, "half3 ScreenSpaceRefractionTransmission", "refraction probe fallback");
            AssertContains(refraction, "saturate(1.0h - result.weight)", "refraction remaining weight");
            AssertContains(refraction, "GlossyEnvironmentReflection", "refraction probe fallback sampling");

            // P1-2: the Thin model uses the same 0.005 distance for absorption and thickness.
            AssertContains(refraction, "float absorptionDistance = 0.005;", "Thin absorption distance");

            string forward = ReadPackageFile("Shaders/LitForwardPass.hlsl");
            AssertContains(forward, "ScreenSpaceRefractionEvaluate", "Lit forward pass refraction");
            AssertContains(forward, "ScreenSpaceRefractionTransmission", "Lit forward pass probe fallback");
            // P0-2: HDRP's transmittance mask and opaque refractive surface.
            AssertContains(forward, "UniversalFragmentPBRSplit", "Lit forward pass lighting split");
            AssertContains(forward, "saturate(1.0h - surfaceData.alpha)", "Lit forward pass transmittance mask");
            AssertContains(forward, "color.a = refraction.valid ? 1.0h : surfaceData.alpha;", "Lit forward pass refraction alpha");
        }

        [Test]
        public void ShaderGraphLitWiresRefractionAndTransparentPrepass()
        {
            string target = ReadPackageFile("Editor/ShaderGraph/Targets/UniversalTarget.cs");
            AssertContains(target, "referenceName = \"_SCREEN_SPACE_REFRACTION\"", "ShaderGraph refraction keyword");

            string litSubTarget = ReadPackageFile("Editor/ShaderGraph/Targets/UniversalLitSubTarget.cs");
            AssertContains(litSubTarget, "CoreKeywordDescriptors.ScreenSpaceRefraction", "ShaderGraph Lit keywords");
            AssertContains(litSubTarget, "LitDefines.RefractionPlane", "ShaderGraph Lit refraction keywords");
            AssertContains(litSubTarget, "LitDefines.TransparentSSRReceive", "ShaderGraph Lit transparent SSR keyword");
            AssertContains(litSubTarget, "lightMode = \"UniversalTransparentDepthNormals\"", "ShaderGraph transparent prepass");
            AssertContains(litSubTarget, "URP_TRANSPARENT_DEPTH_NORMALS", "ShaderGraph transparent prepass define");
            AssertContains(litSubTarget, "Property.RefractionModel", "ShaderGraph refraction properties");
            AssertContains(litSubTarget, "Property.TransmittanceColor", "ShaderGraph refraction properties");

            string depthNormals = ReadPackageFile("Editor/ShaderGraph/Includes/DepthNormalsOnlyPass.hlsl");
            AssertContains(depthNormals, "defined(URP_TRANSPARENT_DEPTH_NORMALS)", "ShaderGraph transparent prepass include");
            AssertContains(depthNormals, "clip(-1.0)", "ShaderGraph transparent prepass opt-in");

            string forward = ReadPackageFile("Editor/ShaderGraph/Includes/PBRForwardPass.hlsl");
            AssertContains(forward, "ScreenSpaceRefractionEvaluate", "ShaderGraph forward refraction");
            AssertContains(forward, "ScreenSpaceRefractionTransmission", "ShaderGraph forward probe fallback");
            AssertContains(forward, "UniversalFragmentPBRSplit", "ShaderGraph forward lighting split");
            AssertContains(forward, "saturate(1.0h - surface.alpha)", "ShaderGraph forward transmittance mask");
        }
    }
}