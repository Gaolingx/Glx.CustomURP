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

        static string Relative(string absolutePath)
        {
            return absolutePath
                .Substring(PackageRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }
    }
}