using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;
using XRSystem = UnityEngine.Experimental.Rendering.XRSystem;

namespace UnityEditor.Rendering.Universal.Tests
{
    static class ClusteredDeferredAssets
    {
        const string RendererRoot = "Assets/Tests/ClusteredDeferred/Renderers/";

        // Resolve the package through the compute asset so the suite does not depend on the
        // package folder name (which carries the version suffix for embedded packages).
        public static string ComputeAssetPath
        {
            get
            {
                foreach (var guid in AssetDatabase.FindAssets("ClusteredDeferred t:ComputeShader"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.EndsWith("/ClusteredDeferred.compute", StringComparison.Ordinal))
                        return path;
                }

                Assert.Fail("Could not find ClusteredDeferred.compute in the project.");
                return null;
            }
        }

        public static T Load<T>(string fileName) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(RendererRoot + fileName);
            Assert.IsNotNull(asset, "Missing clustered deferred test asset: " + RendererRoot + fileName);
            return asset;
        }

        public static UniversalRendererData StencilRenderer => Load<UniversalRendererData>("CdStencilDeferredRenderer.asset");
        public static UniversalRendererData ClusteredRenderer => Load<UniversalRendererData>("CdClusteredDeferredRenderer.asset");
        public static UniversalRenderPipelineAsset ClusteredPipeline => Load<UniversalRenderPipelineAsset>("CdClusteredDeferredPipeline.asset");
        public static UniversalRenderPipelineAsset StencilPipeline => Load<UniversalRenderPipelineAsset>("CdStencilDeferredPipeline.asset");

        public static void UsePipeline(UniversalRenderPipelineAsset pipeline)
        {
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
        }
    }

    [TestFixture]
    class ClusteredDeferredShaderTests
    {
        static string ComputePath => ClusteredDeferredAssets.ComputeAssetPath;

        static string IncludePath
        {
            get
            {
                // <package>/Shaders/Utils/ClusteredDeferred.compute -> <package>/ShaderLibrary/ClusteredDeferred.hlsl
                var utils = ComputePath.Substring(0, ComputePath.LastIndexOf('/'));
                var shaders = utils.Substring(0, utils.LastIndexOf('/'));
                var packageRoot = shaders.Substring(0, shaders.LastIndexOf('/'));
                return packageRoot + "/ShaderLibrary/ClusteredDeferred.hlsl";
            }
        }

        [Test]
        public void ComputeKernelAndMaskResolveShadersCompile()
        {
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
            Assert.IsNotNull(compute, "Missing " + ComputePath);
            Assert.GreaterOrEqual(compute.FindKernel("ClusteredDeferred"), 0, "The compute shader has no ClusteredDeferred kernel.");
            Assert.AreEqual(0, ShaderUtil.GetComputeShaderMessageCount(compute), "Compute shader reported compile messages.");

            foreach (var shaderName in new[]
                     {
                         "Hidden/Universal/ClusteredDeferredMask",
                         "Hidden/Universal/ClusteredDeferredResolve",
                     })
            {
                var shader = Shader.Find(shaderName);
                Assert.IsNotNull(shader, "Missing shader " + shaderName);
                Assert.AreEqual(0, ShaderUtil.GetShaderMessageCount(shader), shaderName + " reported compile messages.");
            }
        }

        // ShaderUtil under-reported broken compute variants in the T4 shadow round, so the player
        // build logs remain the authority on compilation. This pins the declared matrix instead:
        // 2 * 4 * 2 * 5 * 2 * 2 * 2 * 2 = 1280 combinations.
        [Test]
        public void ComputeDeclaresTheDocumentedKeywordMatrix()
        {
            var groups = new List<int>();
            foreach (var line in File.ReadAllLines(Path.GetFullPath(ComputePath)))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("#pragma multi_compile", StringComparison.Ordinal))
                    continue;
                var options = trimmed.Substring("#pragma multi_compile".Length)
                    .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                groups.Add(options.Length);
            }

            CollectionAssert.AreEqual(new[] { 2, 4, 2, 5, 2, 2, 2, 2 }, groups,
                "The clustered deferred keyword groups changed; update the variant budget and evidence.");
            Assert.AreEqual(1280, groups.Aggregate(1, (total, options) => total * options));
        }

        // C2 keeps _FORWARD_PLUS off in clustered deferred, so the forward cluster path needs its
        // own variant in every lit shader that draws transparent or forward-only geometry.
        [Test]
        public void ForwardLitShadersDeclareTheClusteredDeferredVariant()
        {
            var packageRoot = ComputePath.Substring(0, ComputePath.IndexOf("/Shaders/Utils/"));
            foreach (var shader in new[]
                     {
                         "Lit", "SimpleLit", "ComplexLit",
                         "Particles/ParticlesLit", "Particles/ParticlesSimpleLit",
                     })
            {
                var source = File.ReadAllText(Path.GetFullPath(packageRoot + "/Shaders/" + shader + ".shader"));
                StringAssert.Contains("#pragma multi_compile _ _CLUSTERED_DEFERRED_LIGHTS", source, shader);
            }

            var clustering = File.ReadAllText(Path.GetFullPath(packageRoot + "/ShaderLibrary/Clustering.hlsl"));
            StringAssert.Contains("URPClusterWord", clustering);
            StringAssert.Contains("#if USE_CLUSTER_LIGHTS", clustering);
        }

        [Test]
        public void KernelDoesNotUseTheStructuredBufferLightPath()
        {
            foreach (var path in new[] { ComputePath, IncludePath })
            {
                var source = StripComments(File.ReadAllText(Path.GetFullPath(path)));
                StringAssert.DoesNotContain("USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA", source, path);
            }
        }

        static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return Regex.Replace(source, @"//.*", string.Empty);
        }
    }

    [TestFixture]
    class ClusteredDeferredModeGateTests
    {
        [Test]
        public void SupportedDeviceMatchesComputeAndConstantLightArray()
        {
            var api = SystemInfo.graphicsDeviceType;
            bool expected = SystemInfo.supportsComputeShaders &&
                            !RenderingUtils.useStructuredBuffer &&
                            api != GraphicsDeviceType.OpenGLES2 &&
                            api != GraphicsDeviceType.OpenGLES3;
            Assert.AreEqual(expected, ClusteredDeferredPass.IsSupportedDevice);
        }

        [Test]
        public void OnlyBaseGameCamerasAreEligible()
        {
            var gameBase = new CameraData { cameraType = CameraType.Game, renderType = CameraRenderType.Base, xr = XRSystem.emptyPass };
            Assert.IsTrue(ClusteredDeferredPass.IsEligibleCamera(ref gameBase));

            var overlay = new CameraData { cameraType = CameraType.Game, renderType = CameraRenderType.Overlay, xr = XRSystem.emptyPass };
            Assert.IsFalse(ClusteredDeferredPass.IsEligibleCamera(ref overlay));

            var preview = new CameraData { cameraType = CameraType.Preview, renderType = CameraRenderType.Base, xr = XRSystem.emptyPass };
            Assert.IsFalse(ClusteredDeferredPass.IsEligibleCamera(ref preview));

            var sceneView = new CameraData { cameraType = CameraType.SceneView, renderType = CameraRenderType.Base, xr = XRSystem.emptyPass };
            Assert.IsFalse(ClusteredDeferredPass.IsEligibleCamera(ref sceneView));
        }

        [Test]
        public void StencilLightPrecomputeIsSkippedOnlyForEligibleClusteredCameras()
        {
            var gameBase = new CameraData { cameraType = CameraType.Game, renderType = CameraRenderType.Base, xr = XRSystem.emptyPass };
            Assert.IsFalse(DeferredLights.ShouldPrecomputeStencilLights(true, ref gameBase),
                "Clustered base game cameras never rasterize stencil light volumes.");
            Assert.IsTrue(DeferredLights.ShouldPrecomputeStencilLights(false, ref gameBase),
                "The stencil path still needs the per-type light lists.");

            var overlay = new CameraData { cameraType = CameraType.Game, renderType = CameraRenderType.Overlay, xr = XRSystem.emptyPass };
            Assert.IsTrue(DeferredLights.ShouldPrecomputeStencilLights(true, ref overlay),
                "Overlay cameras fall back to stencil deferred and need the lists.");

            var preview = new CameraData { cameraType = CameraType.Preview, renderType = CameraRenderType.Base, xr = XRSystem.emptyPass };
            Assert.IsTrue(DeferredLights.ShouldPrecomputeStencilLights(true, ref preview),
                "Preview cameras fall back to stencil deferred and need the lists.");
        }

        [Test]
        public void RendererAssetsAgreeExceptForTheClusteredSwitch()
        {
            var stencil = ClusteredDeferredAssets.StencilRenderer;
            var clustered = ClusteredDeferredAssets.ClusteredRenderer;

            Assert.AreEqual(RenderingMode.Deferred, stencil.renderingMode);
            Assert.AreEqual(RenderingMode.Deferred, clustered.renderingMode);
            Assert.IsFalse(stencil.clusteredDeferredLighting);
            Assert.IsTrue(clustered.clusteredDeferredLighting);
            Assert.AreEqual(stencil.useNativeRenderPass, clustered.useNativeRenderPass);
            Assert.AreEqual(stencil.opaqueLayerMask, clustered.opaqueLayerMask);
            Assert.AreEqual(stencil.transparentLayerMask, clustered.transparentLayerMask);
            Assert.AreEqual(stencil.depthPrimingMode, clustered.depthPrimingMode);
            Assert.AreEqual(stencil.accurateGbufferNormals, clustered.accurateGbufferNormals);

            Assert.IsNotNull(clustered.shaders.clusteredDeferredCS, "Clustered renderer has no compute shader reference.");
            Assert.IsNotNull(clustered.shaders.clusteredDeferredMaskPS, "Clustered renderer has no mask shader reference.");
            Assert.IsNotNull(clustered.shaders.clusteredDeferredResolvePS, "Clustered renderer has no resolve shader reference.");
        }
    }

    [TestFixture]
    class ClusteredDeferredRendererTests
    {
        RenderPipelineAsset m_PreviousDefaultPipeline;
        RenderPipelineAsset m_PreviousQualityPipeline;

        [SetUp]
        public void SetUp()
        {
            m_PreviousDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
            m_PreviousQualityPipeline = QualitySettings.renderPipeline;
            ClusteredDeferredAssets.UsePipeline(ClusteredDeferredAssets.ClusteredPipeline);
        }

        [TearDown]
        public void TearDown()
        {
            GraphicsSettings.defaultRenderPipeline = m_PreviousDefaultPipeline;
            QualitySettings.renderPipeline = m_PreviousQualityPipeline;
        }

        [Test]
        public void ClusteredSwitchOnlyAppliesToDeferredRenderers()
        {
            var clustered = new UniversalRenderer(ClusteredDeferredAssets.ClusteredRenderer);
            try
            {
                Assert.IsTrue(clustered.clusteredDeferredEnabled);
            }
            finally
            {
                clustered.Dispose();
            }

            var copied = UnityEngine.Object.Instantiate(ClusteredDeferredAssets.ClusteredRenderer);
            try
            {
                copied.renderingMode = RenderingMode.Forward;
                var forward = new UniversalRenderer(copied);
                try
                {
                    Assert.IsFalse(forward.clusteredDeferredEnabled, "A forward renderer must not enable clustered deferred lighting.");
                }
                finally
                {
                    forward.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copied);
            }
        }

        [Test]
        public void ClusteredRendererToleratesRepeatedDispose()
        {
            var renderer = new UniversalRenderer(ClusteredDeferredAssets.ClusteredRenderer);
            renderer.Dispose();
            Assert.DoesNotThrow(() => renderer.Dispose());
        }

        [Test]
        public void ClusteredCappingRunsBeforeCulling()
        {
            var renderer = new UniversalRenderer(ClusteredDeferredAssets.ClusteredRenderer);
            try
            {
                var culling = default(ScriptableCullingParameters);
                var cameraData = new CameraData
                {
                    cameraType = CameraType.Game,
                    renderType = CameraRenderType.Base,
                    maxShadowDistance = 25.0f,
                };
                renderer.SetupCullingParameters(ref culling, ref cameraData);

                Assert.AreEqual(UniversalRenderPipeline.maxVisibleAdditionalLights, culling.maximumVisibleLights,
                    "Clustered deferred must cap visible lights before culling.");
                Assert.AreEqual(ReflectionProbeSortingCriteria.None, culling.reflectionProbeSortingCriteria,
                    "Clustered deferred must keep the probe order the GPU list expects.");
            }
            finally
            {
                renderer.Dispose();
            }
        }

        [Test]
        public void StencilDeferredKeepsLegacyCullingCaps()
        {
            var renderer = new UniversalRenderer(ClusteredDeferredAssets.StencilRenderer);
            try
            {
                var culling = default(ScriptableCullingParameters);
                culling.reflectionProbeSortingCriteria = ReflectionProbeSortingCriteria.ImportanceThenSize;
                var cameraData = new CameraData
                {
                    cameraType = CameraType.Game,
                    renderType = CameraRenderType.Base,
                    maxShadowDistance = 25.0f,
                };
                renderer.SetupCullingParameters(ref culling, ref cameraData);

                Assert.AreEqual(0xFFFF, culling.maximumVisibleLights);
                Assert.AreEqual(ReflectionProbeSortingCriteria.ImportanceThenSize, culling.reflectionProbeSortingCriteria,
                    "Stencil deferred must not change the culling probe sorting.");
            }
            finally
            {
                renderer.Dispose();
            }
        }
    }
}
