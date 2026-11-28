using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
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

        public static UniversalRendererData ForwardPlusRenderer =>
            (UniversalRendererData)Load<UniversalRenderPipelineAsset>("CdForwardPlusPipeline.asset").scriptableRendererData;
        public static UniversalRendererData ClusteredRenderer => Load<UniversalRendererData>("CdClusteredDeferredRenderer.asset");
        public static UniversalRenderPipelineAsset ClusteredPipeline => Load<UniversalRenderPipelineAsset>("CdClusteredDeferredPipeline.asset");

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
        public void ClusteredDeferredKernelCompiles()
        {
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
            Assert.IsNotNull(compute, "Missing " + ComputePath);
            Assert.IsTrue(compute.HasKernel("ClusteredDeferred"), "The compute shader has no ClusteredDeferred kernel.");
            Assert.AreEqual(0, ShaderUtil.GetComputeShaderMessageCount(compute), "Compute shader reported compile messages.");

            compute.GetKernelThreadGroupSizes(compute.FindKernel("ClusteredDeferred"), out uint x, out uint y, out uint z);
            CollectionAssert.AreEqual(new uint[] { 16, 16, 1 }, new[] { x, y, z }, "Dispatch dimensions must cover one 16x16 pixel group.");
        }

        // ShaderUtil under-reported broken compute variants in the T4 shadow round, so the player
        // build logs remain the authority on compilation. This pins the declared matrix instead:
        // 2 * 4 * 2 * 5 * 2 * 2 * 2 * 2 = 1280, then contact shadows adds one 2x axis, for 2560
        // combinations. The SSR indirect-specular composite is a runtime binding, not an axis.
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

            CollectionAssert.AreEqual(new[] { 2, 4, 2, 5, 2, 2, 2, 2, 2 }, groups,
                "The clustered deferred keyword groups changed; update the variant budget and evidence.");
            Assert.AreEqual(2560, groups.Aggregate(1, (total, options) => total * options));
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
        public void LinearCameraTargetRequiresSampleableStencilAndWritableColor()
        {
            const GraphicsFormat color = GraphicsFormat.R16G16B16A16_SFloat;
            var target = new RenderTextureDescriptor(64, 64, color, 0) { msaaSamples = 1 };
            var api = SystemInfo.graphicsDeviceType;
            bool expected = (api == GraphicsDeviceType.Direct3D11 || api == GraphicsDeviceType.Direct3D12 ||
                             api == GraphicsDeviceType.Metal || api == GraphicsDeviceType.Vulkan) &&
                            SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat_S8_UInt, FormatUsage.Render) &&
                            SystemInfo.IsFormatSupported(color, FormatUsage.Render) &&
                            SystemInfo.IsFormatSupported(color, FormatUsage.LoadStore);
            Assert.AreEqual(expected, UniversalRenderer.SupportsDeferredAttachments(target));
            target.msaaSamples = 4;
            Assert.IsFalse(UniversalRenderer.SupportsDeferredAttachments(target));
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
        public void OracleAndClusteredFixturesUseComparableRendererSettings()
        {
            var oracle = ClusteredDeferredAssets.ForwardPlusRenderer;
            var clustered = ClusteredDeferredAssets.ClusteredRenderer;

            Assert.AreEqual(RenderingMode.ForwardPlus, oracle.renderingMode);
            Assert.AreEqual(RenderingMode.Deferred, clustered.renderingMode);
            Assert.AreEqual(oracle.useNativeRenderPass, clustered.useNativeRenderPass);
            Assert.AreEqual(oracle.opaqueLayerMask, clustered.opaqueLayerMask);
            Assert.AreEqual(oracle.transparentLayerMask, clustered.transparentLayerMask);
            Assert.AreEqual(oracle.depthPrimingMode, clustered.depthPrimingMode);
            Assert.AreEqual(oracle.accurateGbufferNormals, clustered.accurateGbufferNormals);

            Assert.IsNotNull(oracle.shaders.clusterBuilderCS, "Forward+ renderer has no list builder.");
            Assert.IsNotNull(clustered.shaders.clusterBuilderCS, "Clustered renderer has no list builder.");
            Assert.IsNotNull(clustered.shaders.clusteredDeferredCS, "Clustered renderer has no compute shader reference.");
        }
    }

    [TestFixture]
    class ClusteredDeferredRendererTests
    {
        UnityEngine.Camera m_Camera;
        RenderPipelineAsset m_PreviousDefaultPipeline;
        RenderPipelineAsset m_PreviousQualityPipeline;

        [SetUp]
        public void SetUp()
        {
            m_Camera = new GameObject("Cluster culling fixture").AddComponent<UnityEngine.Camera>();
            m_Camera.enabled = false;
            m_PreviousDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
            m_PreviousQualityPipeline = QualitySettings.renderPipeline;
            ClusteredDeferredAssets.UsePipeline(ClusteredDeferredAssets.ClusteredPipeline);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(m_Camera.gameObject);
            GraphicsSettings.defaultRenderPipeline = m_PreviousDefaultPipeline;
            QualitySettings.renderPipeline = m_PreviousQualityPipeline;
        }

        [TestCase(RenderingMode.Forward)]
        [TestCase(RenderingMode.ForwardPlus)]
        public void NonDeferredRenderersUseDeviceAppropriateCulling(RenderingMode mode)
        {
            var copied = UnityEngine.Object.Instantiate(ClusteredDeferredAssets.ClusteredRenderer);
            try
            {
                copied.renderingMode = mode;
                using (var renderer = new UniversalRenderer(copied))
                {
                    var game = Camera(CameraType.Game);
                    var expected = mode == RenderingMode.ForwardPlus && !ClusterListGpuBuilder.IsSupportedDevice
                        ? RenderingMode.Forward : mode;
                    AssertCulling(renderer, game, expected);
                    Assert.IsFalse(renderer.clusteredDeferredEnabled);
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

        CameraData Camera(CameraType type, CameraRenderType renderType = CameraRenderType.Base) => new CameraData
        {
            camera = m_Camera,
            cameraType = type,
            renderType = renderType,
            xr = XRSystem.emptyPass,
            maxShadowDistance = 25f,
            cameraTargetDescriptor = new RenderTextureDescriptor(64, 64, GraphicsFormat.R16G16B16A16_SFloat, 0) { msaaSamples = 1 },
        };

        static void AssertCulling(UniversalRenderer renderer, CameraData camera, RenderingMode expected, bool renderGraph = false)
        {
            renderer.UpdateCameraRenderingMode(ref camera, renderGraph);
            Assert.AreEqual(expected, renderer.renderingModeActual, $"{camera.cameraType}/{camera.renderType}");
            var culling = new ScriptableCullingParameters { reflectionProbeSortingCriteria = ReflectionProbeSortingCriteria.ImportanceThenSize };
            renderer.SetupCullingParameters(ref culling, ref camera);
            bool clustered = expected == RenderingMode.Deferred || expected == RenderingMode.ForwardPlus;
            Assert.AreEqual(UniversalRenderPipeline.maxVisibleAdditionalLights + (clustered ? 0 : 1), culling.maximumVisibleLights);
            Assert.AreEqual(clustered ? ReflectionProbeSortingCriteria.None : ReflectionProbeSortingCriteria.ImportanceThenSize,
                culling.reflectionProbeSortingCriteria);
            Assert.AreEqual(expected == RenderingMode.ForwardPlus ? CullingOptions.DisablePerObjectCulling : (CullingOptions)0,
                culling.cullingOptions & CullingOptions.DisablePerObjectCulling);
        }

        [Test]
        public void CullingUsesCurrentCameraModeNotPreviousCameraMode()
        {
            using (var renderer = new UniversalRenderer(ClusteredDeferredAssets.ClusteredRenderer))
            {
                var game = Camera(CameraType.Game);
                if (!ClusterListGpuBuilder.IsSupportedDevice || !ClusteredDeferredPass.IsSupportedDevice ||
                    !UniversalRenderer.SupportsDeferredAttachments(game.cameraTargetDescriptor))
                    Assert.Ignore("Mode transitions require a device supporting the linear deferred fixture target.");
                const RenderingMode gameMode = RenderingMode.Deferred;
                AssertCulling(renderer, game, gameMode);
                AssertCulling(renderer, Camera(CameraType.Game, CameraRenderType.Overlay), RenderingMode.Forward);
                AssertCulling(renderer, game, gameMode);
                AssertCulling(renderer, Camera(CameraType.Preview), RenderingMode.Forward);
                AssertCulling(renderer, Camera(CameraType.SceneView), RenderingMode.Forward);
                AssertCulling(renderer, game, gameMode);
                AssertCulling(renderer, game, RenderingMode.Forward, renderGraph: true);
                var multisampled = game;
                multisampled.cameraTargetDescriptor.msaaSamples = 4;
                AssertCulling(renderer, multisampled, RenderingMode.Forward);
                AssertCulling(renderer, game, gameMode);
            }
        }

        [Test]
        public void MissingComputeResourceForcesForwardEvenForBaseGame()
        {
            var copied = UnityEngine.Object.Instantiate(ClusteredDeferredAssets.ClusteredRenderer);
            try
            {
                copied.shaders = JsonUtility.FromJson<UniversalRendererData.ShaderResources>(JsonUtility.ToJson(copied.shaders));
                var game = Camera(CameraType.Game);
                if (!ClusterListGpuBuilder.IsSupportedDevice || !ClusteredDeferredPass.IsSupportedDevice ||
                    !UniversalRenderer.SupportsDeferredAttachments(game.cameraTargetDescriptor))
                    Assert.Ignore("The missing-resource test needs an otherwise eligible deferred camera.");
                using (var control = new UniversalRenderer(copied))
                    AssertCulling(control, game, RenderingMode.Deferred);
                copied.shaders.clusteredDeferredCS = null;
                using (var renderer = new UniversalRenderer(copied))
                    AssertCulling(renderer, game, RenderingMode.Forward);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copied);
            }
        }

        [Test]
        public void XRBaseGameUsesForwardWithoutAHeadset()
        {
            // The core package keeps XRView/AddView internal. Supply one default view; only its
            // presence is consumed by the gate. Never mutate XRSystem.emptyPass or a pooled pass.
            var xr = new UnityEngine.Experimental.Rendering.XRPass();
            var addView = xr.GetType().GetMethod("AddView", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(addView);
            addView.Invoke(xr, new[] { Activator.CreateInstance(addView.GetParameters()[0].ParameterType) });
            if (!xr.enabled)
                Assert.Ignore("This build of the Core XR module compiles XRPass.enabled to false.");
            var game = Camera(CameraType.Game);
            game.xr = xr;
            Assert.IsFalse(ClusteredDeferredPass.IsEligibleCamera(ref game));
            using (var renderer = new UniversalRenderer(ClusteredDeferredAssets.ClusteredRenderer))
                AssertCulling(renderer, game, RenderingMode.Forward);
        }
    }
}
