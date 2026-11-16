using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace UnityEditor.Rendering.Universal.Tests
{
    // F-01 regression: GTAO.compute and GTAOTemporalDenoise.compute select the full resolution path
    // with the FULL_RES multi_compile keyword. The shared depth helpers tested an undefined
    // GTAO_FULL_RES guard, so full resolution always compiled the half resolution branch and read
    // outside mip 1. These assertions pin the guard name and the removal of the old CPU flag lane.
    [TestFixture]
    class GroundTruthAmbientOcclusionFullResolutionTests
    {
        const string k_PackageRoot = "Packages/com.unity.render-pipelines.universal@14.0.12";
        const string k_GtaoCommonPath = k_PackageRoot + "/Shaders/Utils/GroundTruthAmbientOcclusion/GTAOCommon.hlsl";
        const string k_GtaoComputePath = k_PackageRoot + "/Shaders/Utils/GroundTruthAmbientOcclusion/GTAO.compute";
        const string k_PassPath = k_PackageRoot +
            "/Runtime/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionPass.cs";

        static string ReadSource(string relativePath)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string path = Path.Combine(projectRoot, relativePath);
            Assert.IsTrue(File.Exists(path), $"Source file not found: {path}");
            return File.ReadAllText(path);
        }

        [Test]
        public void DepthHelpersUseTheKeywordThatGtaoComputeDefines()
        {
            string common = ReadSource(k_GtaoCommonPath);
            string compute = ReadSource(k_GtaoComputePath);

            StringAssert.DoesNotContain("GTAO_FULL_RES", common);
            StringAssert.Contains("#ifdef FULL_RES", common);
            StringAssert.Contains("multi_compile HALF_RES FULL_RES", compute);
        }

        [Test]
        public void ConstantBufferUploadHasNoFullResolutionFlag()
        {
            string pass = ReadSource(k_PassPath);

            StringAssert.DoesNotContain("fullResolution ? 0f : 1f", pass);
            StringAssert.DoesNotContain("fullResolution ? 0 : 1", pass);
        }
    }
}