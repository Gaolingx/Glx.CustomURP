using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class PluginIsolationTests
    {
        static readonly string[] k_PluginTypeNames =
        {
            "GroundTruthAmbientOcclusion",
            "ScreenSpaceLighting",
            "ScreenSpaceGlobalIllumination",
            "ScreenSpaceReflection",
            "ScreenSpaceRefraction",
            "CameraHistoryStore",
            "PackedMipChainInfo",
            "ScreenSpaceCapability",
            "ScreenSpaceLightingResources",
            "ScreenSpaceLightingShaderIDs",
            "ScreenSpaceLightingRenderUtils",
            "ScreenSpaceRenderUtils",
            "ScreenSpaceDepthPyramid",
            "ScreenSpaceDepthPyramidPass",
            "ScreenSpaceDepthPyramidSystem",
            "ScreenSpaceLightingCurrentColorPyramidPass",
            "ScreenSpaceLightingColorHistoryCapturePass",
            "ScreenSpaceLightingDebugPass",
            "ScreenSpaceLightingDebugMode",
        };

        // T0 dependency direction: URP core runtime must not know about the renderer-feature plugin types.
        [Test]
        public void CoreRuntimeSourcesDoNotReferencePluginTypes()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string runtimeRoot = Path.Combine(projectRoot, "Packages", "com.unity.render-pipelines.universal@14.0.12", "Runtime");
            Assert.IsTrue(Directory.Exists(runtimeRoot), $"URP runtime package not found at {runtimeRoot}.");

            var coreFiles = new List<string>();
            foreach (var file in Directory.GetFiles(runtimeRoot, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains("/RendererFeatures/"))
                    continue;

                coreFiles.Add(file);
            }

            Assert.IsNotEmpty(coreFiles);

            var violations = new List<string>();
            foreach (var file in coreFiles)
            {
                string source = File.ReadAllText(file);
                foreach (var typeName in k_PluginTypeNames)
                {
                    if (source.Contains(typeName))
                        violations.Add($"{file} references {typeName}");
                }
            }

            Assert.IsEmpty(violations, string.Join("\n", violations));
        }
    }
}
