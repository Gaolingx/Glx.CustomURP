using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace UnityEditor.Rendering.Universal.Tests
{
    // F-02 regression: ReflectionDenoiser.compute maps _PixelSpreadAngleTangent to the shared
    // _SsgiDenoiserParams.x uniform. A standalone float write lands on no declared uniform, so the
    // reflection denoiser always read zero unless SSGI happened to upload the vector first. The SSR
    // pass must upload the vector itself, and the dead temporal-filter float writes stay removed.
    [TestFixture]
    class ScreenSpaceLightingSsrDenoiserParamsTests
    {
        static string PackageRoot => Path.Combine(
            Path.GetDirectoryName(Application.dataPath),
            "Packages",
            "com.unity.render-pipelines.universal@14.0.12");

        static string ReadPackageFile(string relativePath)
        {
            string path = Path.Combine(PackageRoot, relativePath);
            Assert.IsTrue(File.Exists(path), $"Expected package file at {path}.");
            return File.ReadAllText(path);
        }

        [Test]
        public void DenoiserShaderAliasesTheSpreadAngleToTheSharedVector()
        {
            string denoiser = ReadPackageFile("Shaders/Utils/Denoising/ReflectionDenoiser.compute");
            StringAssert.Contains("#define _PixelSpreadAngleTangent _SsgiDenoiserParams.x", denoiser);
        }

        [Test]
        public void SsrPassUploadsTheSharedVectorAndDropsTheStandaloneFloat()
        {
            string pass = ReadPackageFile(
                "Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs");

            StringAssert.Contains("ScreenSpaceLightingShaderIDs.SsgiDenoiserParams", pass);
            StringAssert.DoesNotContain("ScreenSpaceLightingShaderIDs.PixelSpreadAngleTangent", pass);
        }

        [Test]
        public void StandaloneSpreadAngleFloatWritesAreRemoved()
        {
            string temporal = ReadPackageFile("Shaders/Utils/Denoising/TemporalFilter.compute");
            StringAssert.Contains("#define _PixelSpreadAngleTangent _SsgiDenoiserParams.x", temporal);

            // No kernel declares a standalone _PixelSpreadAngleTangent uniform, so every float write
            // to it is shadowed by the macro and never reaches a shader.
            string ssgi = ReadPackageFile(
                "Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsgiPass.cs");
            Assert.AreEqual(
                0,
                CountOccurrences(ssgi, "PixelSpreadAngleTangent"),
                "The dead standalone float write must stay removed.");
        }

        static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }
    }
}