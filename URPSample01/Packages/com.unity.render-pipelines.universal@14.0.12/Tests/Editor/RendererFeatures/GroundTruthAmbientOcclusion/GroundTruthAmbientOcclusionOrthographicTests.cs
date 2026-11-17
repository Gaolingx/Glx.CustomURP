using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    // F-03 regression: the perspective _ZBufferParams mapping is not valid for an orthographic
    // window depth, and the view direction is constant instead of point-to-camera. These assertions
    // pin the shared linear-depth helper, the constant orthographic view direction and the CPU side
    // scale/bias upload.
    [TestFixture]
    class GroundTruthAmbientOcclusionOrthographicTests
    {
        const string k_PackageRoot = "Packages/com.unity.render-pipelines.universal@14.0.12";
        const string k_GtaoCommonPath = k_PackageRoot + "/Shaders/Utils/GroundTruthAmbientOcclusion/GTAOCommon.hlsl";
        const string k_GtaoComputePath = k_PackageRoot + "/Shaders/Utils/GroundTruthAmbientOcclusion/GTAO.compute";
        const string k_SpatialDenoisePath = k_PackageRoot + "/Shaders/Utils/GroundTruthAmbientOcclusion/GTAOSpatialDenoise.compute";
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
        public void PositionReconstructionUsesTheOrthographicDepthMapping()
        {
            string common = ReadSource(k_GtaoCommonPath);

            StringAssert.Contains("float GTAOLinearDepth(float rawDepth)", common);
            StringAssert.Contains("_GtaoOrthoDepthParams.x", common);
            StringAssert.Contains("LinearEyeDepth(rawDepth, _GtaoZBufferParams)", common);
            StringAssert.Contains("float linearDepth = GTAOLinearDepth(rawDepth);", common);
        }

        [Test]
        public void SpatialDenoiseLinearisesDepthThroughTheSharedHelper()
        {
            string spatial = ReadSource(k_SpatialDenoisePath);

            StringAssert.Contains("#pragma multi_compile _ GTAO_ORTHO", spatial);
            StringAssert.Contains("GTAOLinearDepth(sampleDepth)", spatial);
            StringAssert.Contains("GTAOLinearDepth(centralDepth)", spatial);
            StringAssert.DoesNotContain("LinearEyeDepth", spatial);
        }

        [Test]
        public void ViewDirectionIsConstantUnderOrthographicProjection()
        {
            string compute = ReadSource(k_GtaoComputePath);

            StringAssert.Contains("float3 V = float3(0.0, 0.0, -1.0);", compute);
            // The constant must live in the GTAO_ORTHO branch; the perspective path still derives V
            // from the reconstructed position.
            int ortho = compute.IndexOf("#ifdef GTAO_ORTHO", System.StringComparison.Ordinal);
            int constant = compute.IndexOf("float3 V = float3(0.0, 0.0, -1.0);", System.StringComparison.Ordinal);
            int perspective = compute.IndexOf("float3 V = normalize(-positionVS);", System.StringComparison.Ordinal);
            Assert.IsTrue(ortho >= 0 && ortho < constant && constant < perspective,
                "The constant view direction must be the GTAO_ORTHO branch of the V selection.");
        }

        [Test]
        public void OrthographicDepthParamsMapNearAndFar()
        {
            var reversed = GroundTruthAmbientOcclusionPass.ComputeOrthoDepthParams(0.1f, 100f, true);
            Assert.AreEqual(0.1f, Apply(reversed, 1f), 1e-4f, "Reversed-Z near plane must map to the near distance.");
            Assert.AreEqual(100f, Apply(reversed, 0f), 1e-3f, "Reversed-Z far plane must map to the far distance.");

            var forward = GroundTruthAmbientOcclusionPass.ComputeOrthoDepthParams(0.1f, 100f, false);
            Assert.AreEqual(0.1f, Apply(forward, 0f), 1e-4f, "Non-reversed near plane must map to the near distance.");
            Assert.AreEqual(100f, Apply(forward, 1f), 1e-3f, "Non-reversed far plane must map to the far distance.");
        }

        [Test]
        public void PassUploadsTheOrthographicDepthParamsAndKeyword()
        {
            string pass = ReadSource(k_PassPath);

            StringAssert.Contains("_GtaoOrthoDepthParams = ComputeOrthoDepthParams(", pass);
            StringAssert.Contains("GroundTruthAmbientOcclusionShaderIDs.OrthoDepthParams", pass);
            StringAssert.Contains("SetKeyword(spatial, GroundTruthAmbientOcclusionShaderIDs.OrthographicKeyword", pass);
        }

        static float Apply(Vector4 depthParams, float rawDepth)
        {
            return rawDepth * depthParams.x + depthParams.y;
        }
    }
}
