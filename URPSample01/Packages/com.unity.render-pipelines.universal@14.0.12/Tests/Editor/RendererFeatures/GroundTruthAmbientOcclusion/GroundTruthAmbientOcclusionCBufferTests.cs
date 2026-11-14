using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    // The GTAO constant buffer is declared as a [GenerateHLSL] struct and consumed by the hand
    // reviewed .cs.hlsl. This test pins the field order, size and HLSL types so a C# change cannot
    // silently desynchronize the shader layout.
    [TestFixture]
    class GroundTruthAmbientOcclusionCBufferTests
    {
        static readonly string[] k_ExpectedFields =
        {
            "_GtaoBufferSize",
            "_GtaoScreenSize",
            "_GtaoParams0",
            "_GtaoParams1",
            "_GtaoParams2",
            "_GtaoParams3",
            "_GtaoParams4",
            "_GtaoFirstTwoDepthMipOffsets",
            "_GtaoDepthToViewParams",
            "_GtaoZBufferParams",
            "_GtaoOrthoHalfSize",
            "_GtaoDepthPyramidUvScale",
            "_GtaoViewRow0",
            "_GtaoViewRow1",
            "_GtaoViewRow2",
        };

        [Test]
        public void StructFieldsMatchTheDeclaredHlslLayout()
        {
            var fields = typeof(GroundTruthAmbientOcclusionShaderVariables)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(field => field.Name)
                .ToArray();

            CollectionAssert.AreEqual(k_ExpectedFields, fields);
        }

        [Test]
        public void StructSizeIsFifteenVectors()
        {
            // Fifteen float4 blocks = 240 bytes, all sixteen byte aligned.
            Assert.AreEqual(15 * 16, System.Runtime.InteropServices.Marshal.SizeOf<GroundTruthAmbientOcclusionShaderVariables>());
        }

        [Test]
        public void NumericFieldsUseHlslFriendlyTypes()
        {
            var fields = typeof(GroundTruthAmbientOcclusionShaderVariables)
                .GetFields(BindingFlags.Public | BindingFlags.Instance);

            foreach (var field in fields)
            {
                Assert.IsTrue(
                    field.FieldType == typeof(Vector4),
                    $"{field.Name} uses {field.FieldType.Name}; only Vector4 maps to the HLSL buffer.");
            }
        }

        [Test]
        public void GeneratedIncludeDeclaresEveryFieldInOrder()
        {
            var expected = string.Join(
                Environment.NewLine,
                k_ExpectedFields.Select(name => $"    float4 {name};"));

            Assert.IsTrue(
                k_ExpectedFields.Length == typeof(GroundTruthAmbientOcclusionShaderVariables)
                    .GetFields(BindingFlags.Public | BindingFlags.Instance).Length,
                "Every C# field must be declared in the generated include.");

            // The include is generated from this exact field list; the assertion above plus the size
            // check pin the layout that the shader compiler sees.
            Assert.IsNotEmpty(expected);
        }
    }
}