using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace UnityEditor.Rendering.Universal.Tests
{
    // Round 08: the deferred depth-normal prepass shares GBuffer2 with the GBuffer pass. Every opaque
    // DepthNormals pass must therefore declare the octahedron keyword, otherwise the prepass writes
    // signed normals into a UNorm GBuffer2 while the deferred lighting and the screen-space effects
    // decode octahedron encoding (flat surfaces then decode to tilted normals and the AO/GI noise).
    [TestFixture]
    class AccurateGbufferNormalsTests
    {
        static string PackageRoot => Path.Combine(
            Path.GetDirectoryName(Application.dataPath),
            "Packages",
            "com.unity.render-pipelines.universal@14.0.12");

        static string ShaderRoot => Path.Combine(PackageRoot, "Shaders");

        static string[] DepthNormalsShaderFiles()
        {
            return Directory
                .GetFiles(ShaderRoot, "*.shader", SearchOption.AllDirectories)
                .OrderBy(path => path)
                .ToArray();
        }

        [Test]
        public void OpaqueDepthNormalsPassesDeclareTheOctahedronKeyword()
        {
            var missing = DepthNormalsShaderFiles()
                .SelectMany(path => Regex
                    .Split(File.ReadAllText(path), @"\n\s*Pass\s*\n")
                    .Skip(1)
                    .Select(block => new
                    {
                        Path = path,
                        Name = Regex.Match(block, "Name\\s+\"([^\"]+)\"").Groups[1].Value,
                        Block = block
                    })
                    .Where(pass => pass.Name == "DepthNormals" || pass.Name == "DepthNormalsOnly")
                    .Where(pass => !pass.Block.Contains("_GBUFFER_NORMALS_OCT"))
                    .Select(pass => $"{Relative(pass.Path)} ({pass.Name})"))
                .ToArray();

            Assert.IsEmpty(
                missing,
                "DepthNormals passes must declare '#pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT' " +
                "so the deferred prepass matches the GBuffer2 encoding: " + string.Join(", ", missing));
        }

        [Test]
        public void ScreenSpaceEffectDecodersKeepTheOctahedronBranch()
        {
            AssertDecoder(
                "Shaders/Utils/GroundTruthAmbientOcclusion/GTAOCommon.hlsl",
                "GTAO_NORMALS_OCT");
            AssertDecoder(
                "Shaders/Utils/ScreenSpaceLighting/ScreenSpaceLightingCommon.hlsl",
                "SSGI_NORMALS_OCT");
            AssertDecoder(
                "Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute",
                "SSR_NORMALS_OCT");
        }

        static void AssertDecoder(string relativePath, string keyword)
        {
            string path = Path.Combine(PackageRoot, relativePath);
            Assert.IsTrue(File.Exists(path), $"Expected package file at {path}.");

            string source = File.ReadAllText(path);
            Assert.IsTrue(source.Contains($"#if defined({keyword})"), $"{relativePath} must branch on {keyword}.");
            Assert.IsTrue(source.Contains("Unpack888ToFloat2"), $"{relativePath} must unpack the 888 packed normal.");
            Assert.IsTrue(source.Contains("UnpackNormalOctQuadEncode"), $"{relativePath} must decode the octahedron normal.");
        }

        [Test]
        public void RayTracedReflectionRaygenDecodesTheOctahedronNormal()
        {
            // The ray tracing compiler drops the SRP core prelude, so the raygen carries its own 888
            // unpack instead of core Unpack888ToFloat2. It must mirror the core 12-bit layout (r/g
            // low bytes, both high nibbles in b, rescaled by 4095) or every octahedron decodes to a
            // direction that fails the raygen's facing gate and the whole reflection is dropped.
            const string relativePath = "Shaders/Utils/ScreenSpaceLighting/RayTracingSsr.raytrace";
            string path = Path.Combine(PackageRoot, relativePath);
            Assert.IsTrue(File.Exists(path), $"Expected package file at {path}.");

            string source = File.ReadAllText(path);
            Assert.IsTrue(source.Contains("_SsrNormalsOct"),
                $"{relativePath} must select the octahedron decode from the reflected flag.");
            Assert.IsTrue(source.Contains("i.z >> 4") && source.Contains("& 15u") && source.Contains("<< 8"),
                $"{relativePath} must rebuild the 4-bit highs from b like core Unpack888UIntToFloat2.");
            Assert.IsTrue(source.Contains("4095.0"),
                $"{relativePath} must rescale the 12-bit octahedron components by 4095.");
        }

        [Test]
        public void ReflectionRoughnessDecodersHonorTheDeferredGbufferSmoothness()
        {
            // The opaque SSR runs after the GBuffer pass and reads GBuffer2, whose alpha is the raw
            // smoothness, not the depth-normals prepass 0.5 + 0.5 * smoothness. Decoding the raw value
            // as the prepass encoding inflates every roughness by up to 2x, which pushes materials at
            // the volume's minSmoothness cut out of the reflection window (and silently skips the
            // reflection bilateral filter). Every decoder and the pass that uploads the flag must
            // keep the _SsrGbufferSmoothness branch.
            AssertSmoothnessSourceDecoder(
                "Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute");
            AssertSmoothnessSourceDecoder(
                "Shaders/Utils/ScreenSpaceLighting/RayTracingSsr.raytrace");
            AssertSmoothnessSourceDecoder(
                "Shaders/Utils/Denoising/BilateralFilter.hlsl");
            AssertSmoothnessSourceDecoder(
                "Shaders/Utils/Denoising/ReflectionDenoiser.compute");
            AssertSmoothnessSourceDecoder(
                "Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs");
        }

        static void AssertSmoothnessSourceDecoder(string relativePath)
        {
            string path = Path.Combine(PackageRoot, relativePath);
            Assert.IsTrue(File.Exists(path), $"Expected package file at {path}.");

            string source = File.ReadAllText(path);
            Assert.IsTrue(source.Contains("_SsrGbufferSmoothness"),
                $"{relativePath} must select the smoothness encoding from _SsrGbufferSmoothness.");
        }

        static string Relative(string absolutePath)
        {
            return absolutePath
                .Substring(PackageRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }
    }
}