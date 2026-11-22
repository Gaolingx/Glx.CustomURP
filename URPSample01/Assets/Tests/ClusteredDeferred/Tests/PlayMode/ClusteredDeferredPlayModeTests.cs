using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Unity.ClusteredDeferred.Tests.PlayMode
{
    // T5/T7 runtime regression for the clustered deferred lighting path.
    //
    // The suite drives real cameras through clustered deferred and Forward+ against a rig with one of
    // every material type that matters to the mask pass (Lit, SimpleLit, Unlit/forward-only,
    // ComplexLit/skipped), so the A/B covers lighting, the material mask and the forward-only
    // fallback at once. The player-only arena runner keeps the large light-count sweep; this suite
    // is the in-editor gate that runs with the normal EditMode/PlayMode batch.
    public class ClusteredDeferredPlayModeTests
    {
        const string RendererRoot = "Assets/Tests/ClusteredDeferred/Renderers";
        const string ClusteredPipelinePath = RendererRoot + "/CdClusteredDeferredPipeline.asset";
        const string ForwardPlusPipelinePath = RendererRoot + "/CdForwardPlusPipeline.asset";

        const int WarmupFrames = 3;
        const float kMeanThreshold = 2.0f;
        const float kMaxThreshold = 24.0f;

        public struct TileCase
        {
            public string name;
            public int width;
            public int height;
            public int lights;
            public int expectedTileWidth;
            public override string ToString() => name;
        }

        // maxTileWords = 16384 on desktop; the CPU doubles the tile width from 8 until the tile
        // grid fits. These cases hit 8/16/32/64 exactly (see ForwardLights.cs:265-271).
        public static readonly TileCase[] TileCases =
        {
            new TileCase { name = "tile8_640x360_8lights", width = 640, height = 360, lights = 8, expectedTileWidth = 8 },
            new TileCase { name = "tile16_1280x720_48lights", width = 1280, height = 720, lights = 48, expectedTileWidth = 16 },
            new TileCase { name = "tile32_1920x1080_65lights", width = 1920, height = 1080, lights = 65, expectedTileWidth = 32 },
            new TileCase { name = "tile64_3840x2160_65lights", width = 3840, height = 2160, lights = 65, expectedTileWidth = 64 },
        };

        struct DiffStats
        {
            public float mean;
            public float max;
            public int over8;
            public int nonFinite;
        }

        RenderPipelineAsset m_PreviousPipeline;
        RenderPipelineAsset m_PreviousQualityPipeline;
        UniversalRenderPipelineAsset m_Pipeline;
        GameObject m_Root;
        GameObject m_CameraObject;
        Camera m_Camera;
        RenderTexture m_Target;
        readonly List<Light> m_Lights = new List<Light>();
        readonly List<Material> m_Materials = new List<Material>();

        [UnityTest]
        public IEnumerator ClusteredMatchesForwardPlusAcrossMaterialTypes()
        {
            yield return SetUpRig(ClusteredPipelinePath, 640, 360, 24);
            try
            {
                yield return RenderFrames(WarmupFrames);
                Assert.AreEqual(2, CountClusterTargets());
                var clustered = ReadPixels();
                AssertImageUsable(clustered, "clustered");
                var receivers = m_Root.GetComponentsInChildren<Renderer>()
                    .Where(r => r.name == "Cd Back Wall" || r.name.EndsWith("Sphere")).ToArray();
                Assert.AreEqual(4, receivers.Length);
                var interiors = CaptureRegions(receivers);

                SwitchPipeline(ForwardPlusPipelinePath);
                yield return null;
                yield return RenderFrames(WarmupFrames);
                var forwardPlus = ReadPixels();
                AssertImageUsable(forwardPlus, "Forward+");
                var forwardInteriors = CaptureRegions(receivers);
                for (int i = 0; i < receivers.Length; ++i)
                {
                    var interior = Compare(interiors[i], forwardInteriors[i]);
                    Assert.LessOrEqual(interior.mean, kMeanThreshold, receivers[i].name);
                    Assert.LessOrEqual(interior.max, kMaxThreshold, receivers[i].name);
                }

                var output = System.Environment.GetEnvironmentVariable("C3_MATERIAL_OUTPUT");
                if (!string.IsNullOrEmpty(output))
                {
                    var directory = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(output, SystemInfo.graphicsDeviceType.ToString()));
                    foreach (var frame in new[] { ("clustered", clustered), ("forwardPlus", forwardPlus) })
                    {
                        using var writer = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(directory.FullName, frame.Item1 + ".rgba32f")));
                        foreach (Color pixel in frame.Item2)
                        {
                            writer.Write(pixel.r);
                            writer.Write(pixel.g);
                            writer.Write(pixel.b);
                            writer.Write(pixel.a);
                        }
                    }
                }
                var diff = Compare(forwardPlus, clustered);
                Assert.AreEqual(0, diff.nonFinite, "A/B image contains non-finite pixels.");
                Assert.LessOrEqual(diff.mean, kMeanThreshold,
                    $"mean channel difference {diff.mean:0.###}/255 exceeds {kMeanThreshold}");
                // C0 control measured isolated Forward+/GBuffer peaks up to 86/255. Allow those
                // outliers only in the whole frame; keep the tight interior, mean and area gates.
                Assert.LessOrEqual(diff.max, 96f, "Whole-frame Forward+ peak (in 1/255 units).");
                Assert.LessOrEqual(diff.over8, clustered.Length / 1000,
                    $"{diff.over8} pixels differ by more than 8/255; expected sparse raster/shading differences.");
            }
            finally
            {
                TearDownRig();
            }
        }

        [UnityTest]
        public IEnumerator PerspectiveFogMatchesForwardPlus() => CompareFogReceivers(false);

        [UnityTest]
        public IEnumerator OrthographicFogSkipsOnlyTheDeferredReceiver() => CompareFogReceivers(true);

        IEnumerator CompareFogReceivers(bool orthographic)
        {
            yield return SetUpRig(ForwardPlusPipelinePath, 640, 360, 1);
            bool fog = RenderSettings.fog;
            var mode = RenderSettings.fogMode;
            var color = RenderSettings.fogColor;
            float start = RenderSettings.fogStartDistance, end = RenderSettings.fogEndDistance, density = RenderSettings.fogDensity;
            try
            {
                foreach (var renderer in m_Root.GetComponentsInChildren<Renderer>())
                    renderer.enabled = false;
                m_Camera.orthographic = orthographic;
                m_Camera.orthographicSize = 5f;
                var lit = CreateMaterial("Universal Render Pipeline/Lit", "Deferred", Color.white, 0f, 0f);
                var forward = CreateMaterial("Universal Render Pipeline/Complex Lit", "Forward-only", Color.white, 0f, 0f);
                // Complex Lit has a GBuffer filler as well as its real forward shading pass.
                // Exclude the filler so a fullscreen depth fog cannot masquerade as forward fog.
                forward.SetShaderPassEnabled("UniversalGBuffer", false);
                Assert.IsTrue(Enumerable.Range(0, forward.passCount).Any(i =>
                    forward.shader.FindPassTagValue(i, new ShaderTagId("LightMode")).name == "UniversalForwardOnly"));
                var transparent = Resources.Load<Material>("CdTransparent");
                Assert.IsNotNull(transparent);
                Assert.Greater(transparent.renderQueue, (int)RenderQueue.GeometryLast);
                Assert.IsTrue(transparent.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"));
                Assert.AreEqual((int)BlendMode.SrcAlpha, transparent.GetInt("_SrcBlend"));
                Assert.AreEqual((int)BlendMode.OneMinusSrcAlpha, transparent.GetInt("_DstBlend"));
                Assert.That(transparent.GetColor("_BaseColor").a, Is.GreaterThan(0f).And.LessThan(1f));

                // Keep the partially transparent receiver distinguishable from clear even in a scene
                // with no ambient light. Only the rig's unshadowed directional light is needed.
                m_Root.GetComponentInChildren<Light>().intensity = 2f;
                var materials = new[] { lit, forward, transparent };
                var receivers = new Renderer[materials.Length];
                for (int i = 0; i < materials.Length; i++)
                {
                    var position = m_Camera.ViewportToWorldPoint(new Vector3(0.2f + 0.3f * i, 0.5f, 10f + i * 2f));
                    receivers[i] = AddBox(materials[i], materials[i].name, position, new Vector3(2f, 2f, 0.2f)).GetComponent<Renderer>();
                }

                RenderSettings.fogColor = new Color(1f, 0.02f, 0.04f);
                RenderSettings.fogStartDistance = 2f;
                RenderSettings.fogEndDistance = 20f;
                RenderSettings.fogDensity = 0.075f;
                FogMode?[] modes = { null, FogMode.Linear, FogMode.Exponential, FogMode.ExponentialSquared };
                var reference = new Color[modes.Length][][];
                foreach (bool clustered in new[] { false, true })
                {
                    if (clustered)
                    {
                        SwitchPipeline(ClusteredPipelinePath);
                        yield return null;
                    }
                    RenderSettings.fog = false;
                    foreach (var receiver in receivers)
                        receiver.enabled = false;
                    yield return RenderFrames(WarmupFrames);
                    var empty = CaptureRegions(receivers);
                    foreach (var receiver in receivers)
                        receiver.enabled = true;

                    Color[][] off = null;
                    for (int m = 0; m < modes.Length; m++)
                    {
                        RenderSettings.fogMode = modes[m] ?? FogMode.Linear;
                        RenderSettings.fog = modes[m].HasValue;
                        yield return RenderFrames(WarmupFrames);
                        var regions = CaptureRegions(receivers);
                        if (m == 0)
                        {
                            off = regions;
                            if (clustered)
                                Assert.AreEqual(2, CountClusterTargets(), "Expected the eligible deferred camera to allocate both cluster targets.");
                        }
                        if (!clustered)
                            reference[m] = regions;
                        for (int i = 0; i < receivers.Length; i++)
                        {
                            string label = $"{(clustered ? "clustered" : "Forward+")} {modes[m]?.ToString() ?? "off"} {receivers[i].name}";
                            var change = Compare(m == 0 ? empty[i] : off[i], regions[i]);
                            Assert.AreEqual(0, change.nonFinite, label);
                            if (clustered && orthographic && i == 0 && m != 0)
                            {
                                // Legacy fullscreen deferred fog intentionally skips orthographic cameras.
                                // Forward+ shades this same surface in its forward pass and does not skip it.
                                Assert.LessOrEqual(change.max, 1f, label + " unexpected orthographic deferred fog");
                                continue;
                            }
                            Assert.Greater(change.mean, 4f, label + (m == 0 ? " not visible against empty frame" : " missing fog response"));
                            if (clustered)
                            {
                                var parity = Compare(reference[m][i], regions[i]);
                                Assert.LessOrEqual(parity.mean, kMeanThreshold, label + " mean difference vs Forward+");
                                Assert.LessOrEqual(parity.max, kMaxThreshold, label + " max difference vs Forward+");
                            }
                        }
                    }
                }
            }
            finally
            {
                RenderSettings.fogMode = mode;
                RenderSettings.fogColor = color;
                RenderSettings.fogStartDistance = start;
                RenderSettings.fogEndDistance = end;
                RenderSettings.fogDensity = density;
                RenderSettings.fog = fog;
                TearDownRig();
            }
        }

        Color[][] CaptureRegions(Renderer[] receivers)
        {
            var pixels = ReadPixels(); // All three interior patches come from the same frame.
            const int side = 12;
            var regions = new Color[receivers.Length][];
            for (int i = 0; i < receivers.Length; i++)
            {
                var center = m_Camera.WorldToViewportPoint(receivers[i].transform.position);
                int x = Mathf.RoundToInt(center.x * m_Target.width) - side / 2;
                int y = Mathf.RoundToInt(center.y * m_Target.height) - side / 2;
                regions[i] = new Color[side * side];
                for (int row = 0; row < side; row++)
                    System.Array.Copy(pixels, (y + row) * m_Target.width + x, regions[i], row * side, side);
            }
            return regions;
        }

        // C2: forward-drawn transparent objects read the same cluster list as the deferred kernel.
        // Before the forward cluster variant they consumed the per-object indices (four lights by
        // default) while the surface behind them consumed the cluster bitmap. Forward+ is the
        // cluster-semantics reference; the same comparison with the switch off measures the gap the
        // variant is supposed to close, so the fixed deferred/forward differences cancel out.
        [UnityTest]
        public IEnumerator ForwardTransparentObjectsReadTheClusterList()
        {
            return CompareForwardReceiver(false);
        }

        [UnityTest]
        public IEnumerator OpaqueForwardOnlyShaderGraphReadsTheClusterList()
        {
            return CompareForwardReceiver(true);
        }

        IEnumerator CompareForwardReceiver(bool opaque)
        {
            yield return SetUpRig(ClusteredPipelinePath, 640, 360, 48, transparent: !opaque);
            bool originalSwitch = ClusteredDeferredPass.forwardClusterLights;
            try
            {
#if UNITY_EDITOR
                if (opaque)
                {
                    var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/Tests/ScreenSpaceLighting/Materials/SslT9ComplexLitT14.mat");
                    Assert.IsNotNull(material);
                    Assert.LessOrEqual(material.renderQueue, (int)RenderQueue.GeometryLast);
                    var lightMode = new ShaderTagId("LightMode");
                    Assert.IsTrue(Enumerable.Range(0, material.passCount).Any(i =>
                        material.shader.FindPassTagValue(i, lightMode).name == "UniversalForwardOnly"));
                    foreach (var renderer in m_Root.GetComponentsInChildren<Renderer>())
                        renderer.enabled = false;
                    AddSphere(material, "Cd Opaque ShaderGraph Receiver", new Vector3(0f, 1.4f, -4f), 4.5f);
                }
#endif
                ClusteredDeferredPass.forwardClusterLights = true;
                yield return RenderFrames(WarmupFrames);
                Assert.IsTrue(Shader.IsKeywordEnabled("_CLUSTERED_DEFERRED_LIGHTS"),
                    "The clustered deferred renderer did not enable the forward cluster keyword.");
                var clusteredShared = ReadPixels();
                AssertImageUsable(clusteredShared, "clustered (cluster lights)");

                SwitchPipeline(ForwardPlusPipelinePath);
                yield return null;
                yield return RenderFrames(WarmupFrames);
                var forwardPlus = ReadPixels();
                AssertImageUsable(forwardPlus, "forward+");

                var shared = Compare(clusteredShared, forwardPlus);

                ClusteredDeferredPass.forwardClusterLights = false;
                SwitchPipeline(ClusteredPipelinePath);
                yield return null;
                yield return RenderFrames(WarmupFrames);
                Assert.IsFalse(Shader.IsKeywordEnabled("_CLUSTERED_DEFERRED_LIGHTS"),
                    "Turning the switch off did not restore the per-object path.");
                var clusteredPerObject = ReadPixels();
                AssertImageUsable(clusteredPerObject, "clustered (per-object lights)");

                var perObject = Compare(clusteredPerObject, forwardPlus);

                Assert.AreEqual(0, shared.nonFinite, "A/B image contains non-finite pixels.");
                Assert.LessOrEqual(shared.mean, kMeanThreshold,
                    $"mean channel difference {shared.mean:0.###}/255 exceeds {kMeanThreshold}; the forward receiver is not reading the cluster list.");
                // The fixed deferred-vs-forward difference keeps a small residue; the per-object path
                // has to stay visibly worse or the forward variant is not taking effect.
                Assert.Less(shared.over8 * 3, perObject.over8,
                    $"cluster path leaves {shared.over8} px >8 while the per-object path leaves {perObject.over8}; " +
                    "the forward receiver is not picking up the cluster list.");
                Debug.Log($"[CD] {(opaque ? "opaque forward-only ShaderGraph" : "transparent")} A/B vs Forward+: cluster over8={shared.over8} mean={shared.mean:0.###}; " +
                          $"per-object over8={perObject.over8} mean={perObject.mean:0.###} ({clusteredShared.Length} px)");
            }
            finally
            {
                ClusteredDeferredPass.forwardClusterLights = originalSwitch;
                TearDownRig();
            }
        }

        // Observe production ordering at the first renderer-feature callback, not just after
        // rendering (where a decision made too late for culling would look correct).
        sealed class CameraRoutingProbe : ScriptableRendererFeature
        {
            public Camera primary, overlay;
            public readonly List<Camera> cameras = new List<Camera>();
            public readonly List<RenderingMode> preCullModes = new List<RenderingMode>();
            public readonly List<PerObjectData> lightFlags = new List<PerObjectData>();

            public override void Create() { }
            public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData data)
            {
                if (data.camera != primary && data.camera != overlay) return;
                cameras.Add(data.camera);
                preCullModes.Add(((UniversalRenderer)renderer).renderingModeActual);
            }

            public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data)
            {
                if (data.cameraData.camera == primary || data.cameraData.camera == overlay)
                    lightFlags.Add(data.perObjectData);
            }
        }

        [UnityTest]
        public IEnumerator CameraStackSelectsModeBeforeCullAndPreservesPerObjectLights()
        {
            yield return SetUpRig(ClusteredPipelinePath, 640, 360, 3);
            const int overlayLayer = 29;
            var pipeline = Object.Instantiate(m_Pipeline);
            var rendererData = Object.Instantiate(m_Pipeline.scriptableRendererData);
            var probe = ScriptableObject.CreateInstance<CameraRoutingProbe>();
            try
            {
                rendererData.rendererFeatures.Add(probe);
                pipeline.m_RendererDataList = new[] { rendererData };
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                yield return null;
                var overlayObject = new GameObject("Cd Forward Overlay");
                overlayObject.transform.SetParent(m_Camera.transform, false);
                var overlay = overlayObject.AddComponent<Camera>();
                overlay.CopyFrom(m_Camera);
                overlay.cullingMask = 1 << overlayLayer;
                var overlayData = overlay.GetUniversalAdditionalCameraData();
                overlayData.renderType = CameraRenderType.Overlay;
                m_Camera.cullingMask &= ~overlay.cullingMask;
                var stack = m_Camera.GetUniversalAdditionalCameraData().cameraStack;

                // The two front faces have disjoint light ranges. A missing index map (or a
                // repeated index zero) cannot give both objects the correct response to this swap.
                var white = CreateMaterial("Universal Render Pipeline/Lit", "Cd Overlay Receiver", Color.white, 0f, 0f);
                var receivers = new Renderer[2];
                for (int i = 0; i < receivers.Length; i++)
                {
                    var pos = m_Camera.ViewportToWorldPoint(new Vector3(i == 0 ? 0.30f : 0.70f, 0.5f, 9f));
                    var box = AddBox(white, "Overlay receiver " + i, pos, new Vector3(1.5f, 1.5f, 0.2f));
                    box.layer = overlayLayer;
                    receivers[i] = box.GetComponent<Renderer>();
                    var light = m_Lights[i];
                    light.gameObject.layer = overlayLayer;
                    light.cullingMask = overlay.cullingMask;
                    light.transform.position = pos - m_Camera.transform.forward * 1.3f;
                    light.range = 2.5f;
                    light.intensity = 3f;
                    light.color = i == 0 ? Color.red : Color.blue;
                }

                yield return RenderFrames(WarmupFrames);
                probe.primary = m_Camera;
                probe.overlay = overlay;
                m_Camera.Render();
                var baseOnly = ReadPixels();
                m_Camera.cameraType = CameraType.Preview;
                m_Camera.Render();
                m_Camera.cameraType = CameraType.Game;
                stack.Add(overlay);
                m_Camera.Render();
                var first = CaptureRegions(receivers);
                m_Lights[0].color = Color.blue;
                m_Lights[1].color = Color.red;
                m_Camera.Render();
                var swapped = CaptureRegions(receivers);
                for (int i = 0; i < receivers.Length; i++)
                {
                    var delta = Compare(first[i], swapped[i]);
                    Assert.AreEqual(0, delta.nonFinite);
                    var red = i == 0 ? first[i] : swapped[i];
                    var blue = i == 0 ? swapped[i] : first[i];
                    Assert.Greater(red.Average(c => c.r) - blue.Average(c => c.r), 8f / 255f, receivers[i].name);
                    Assert.Greater(blue.Average(c => c.b) - red.Average(c => c.b), 8f / 255f, receivers[i].name);
                }
                stack.Remove(overlay);
                m_Lights[0].color = Color.red;
                m_Lights[1].color = Color.blue;
                m_Camera.Render();
                CollectionAssert.AreEqual(new[] { m_Camera, m_Camera, m_Camera, overlay, m_Camera, overlay, m_Camera }, probe.cameras);
                CollectionAssert.AreEqual(new[] { RenderingMode.Deferred, RenderingMode.Forward, RenderingMode.Deferred,
                    RenderingMode.Forward, RenderingMode.Deferred, RenderingMode.Forward, RenderingMode.Deferred }, probe.preCullModes);
                Assert.AreEqual(probe.cameras.Count, probe.lightFlags.Count);
                const PerObjectData required = PerObjectData.LightData | PerObjectData.LightIndices;
                for (int i = 0; i < probe.lightFlags.Count; ++i)
                    Assert.AreEqual(required, probe.lightFlags[i] & required, $"Per-object light data missing at camera {i}");
                var restored = Compare(baseOnly, ReadPixels());
                Assert.AreEqual(0, restored.nonFinite);
                Assert.LessOrEqual(restored.mean, 0.01f, "Overlay state leaked into the next base render.");
                Assert.LessOrEqual(restored.max, 1f);
            }
            finally
            {
                TearDownRig();
                Object.Destroy(pipeline);
                Object.Destroy(rendererData);
                Object.Destroy(probe);
            }
        }

        [UnityTest]
        public IEnumerator TemporaryTargetsAreReleasedWhenThePathIsRemoved()
        {
            yield return SetUpRig(ClusteredPipelinePath, 640, 360, 8);
            try
            {
                yield return RenderFrames(2);
                Assert.AreEqual(2, CountClusterTargets(), "The clustered pass did not allocate its mask/lighting targets.");

                SwitchPipeline(ForwardPlusPipelinePath);
                yield return null;
                yield return null;
                Assert.AreEqual(0, CountClusterTargets(), "Clustered deferred render targets survived the renderer disposal.");
            }
            finally
            {
                TearDownRig();
            }
        }

        [UnityTest]
        public IEnumerator TileWidthFollowsResolutionAndLightCount([ValueSource(nameof(TileCases))] TileCase tileCase)
        {
            yield return SetUpRig(ClusteredPipelinePath, tileCase.width, tileCase.height, tileCase.lights);
            try
            {
                yield return RenderFrames(2);
                var parameters = Shader.GetGlobalVector(Shader.PropertyToID("_FPParams1"));
                Assert.Greater(parameters.x, 0.0f, "The clustered path did not publish _FPParams1.");
                int tileWidth = Mathf.RoundToInt(tileCase.width / parameters.x);
                Assert.AreEqual(tileCase.expectedTileWidth, tileWidth,
                    $"{tileCase.name}: expected tile width {tileCase.expectedTileWidth}, got {tileWidth} ({parameters}).");
            }
            finally
            {
                TearDownRig();
            }
        }

        // The CPU list is the parity reference for the GPU builder, so it must be stable frame to
        // frame and its item stream must describe exactly what the bits index. Pinned to the CPU
        // writer: the GPU path has its own test below.
        [UnityTest]
        public IEnumerator ClusterListSnapshotIsDeterministicAndDescribesTheItemStream()
        {
            yield return SetUpRig(ClusteredPipelinePath, 640, 360, 24);
            ClusterListGpuBuilder.enabled = false;
            ClusterListParityCapture.enabled = true;
            try
            {
                yield return RenderFrames(WarmupFrames);
                var first = ClusterListParityCapture.last;
                yield return RenderFrames(2);
                var second = ClusterListParityCapture.last;

                Assert.IsNotNull(first.zBins, "The parity capture never ran; no eligible clustered camera reached Setup.");
                Assert.IsNotNull(second.zBins);

                var diff = ClusterListSnapshot.Diff(first, second);
                Assert.IsTrue(diff.Exact,
                    "Consecutive frames disagree, so the CPU list cannot serve as the parity reference. " + diff);

                Assert.Greater(CountSetBits(first.tileMasks, first.TileUsedWords), 0,
                    "The tile bitmap is empty; parity would pass trivially.");
                Assert.Greater(CountSetBits(first.zBins, first.ZBinUsedWords), 0,
                    "The zbin bitmap is empty; parity would pass trivially.");

                Assert.AreEqual(m_Lights.Count, first.lightCount, "Every rig point light should reach the item stream.");
                Assert.AreEqual(first.lightCount, first.items.Length - CountProbeItems(first),
                    "The light items must be the leading block; probes trail them.");
                for (int i = 0; i < first.lightCount; i++)
                {
                    Assert.AreEqual(ClusterItemType.Light, first.items[i].itemType, "Item " + i);
                    Assert.AreNotEqual(LightType.Directional, first.items[i].lightType,
                        "Directional lights are shaded outside the cluster list and must not be items.");
                }

                var fp0 = Shader.GetGlobalVector("_FPParams0");
                var fp1 = Shader.GetGlobalVector("_FPParams1");
                Assert.AreEqual(fp0.x, first.fpParams[0], 0f, "zBinScale drifted from _FPParams0.");
                Assert.AreEqual(fp1.x, first.fpParams[4], 0f, "tileScale drifted from _FPParams1.");
                Assert.AreEqual(fp1.z, first.fpParams[6], 0f, "wordsPerTile drifted from _FPParams1.");
                Assert.AreEqual(Mathf.RoundToInt(640f / fp1.x), first.tileWidth,
                    "The captured tile width disagrees with the published _FPParams1.");
            }
            finally
            {
                ClusterListGpuBuilder.enabled = true;
                ClusterListParityCapture.Reset();
                TearDownRig();
            }
        }

        // G1b gate: the GPU builder must cover every bit the CPU jobs set (target/zbin bits are
        // compared), with the conservative tile AABB allowed to add candidates the CPU rejects.
        [UnityTest]
        public IEnumerator GpuClusterListCoversTheCpuList()
        {
            yield return SetUpRig(ClusteredPipelinePath, 640, 360, 24);
            ClusterListParityCapture.enabled = true;
            ClusterListGpuBuilder.enabled = true;
            try
            {
                yield return RenderFrames(WarmupFrames);
                yield return WaitForGpuSnapshot();
                var gpu = ClusterListParityCapture.lastGpu;
                Assert.IsNotNull(gpu.zBins, "The GPU builder never published a snapshot (readback failed or the CPU path ran).");
                Assert.IsNotNull(gpu.tileMasks);
                var gpuImage = ReadPixels();

                ClusterListGpuBuilder.enabled = false;
                ClusterListParityCapture.lastGpu = default;
                yield return null;
                yield return RenderFrames(2);
                var cpu = ClusterListParityCapture.last;
                Assert.IsNotNull(cpu.zBins, "The CPU fallback did not capture its bitmap.");
                var cpuImage = ReadPixels();

                var diff = ClusterListSnapshot.Diff(cpu, gpu);
                int cpuBits = CountSetBits(cpu.tileMasks, cpu.TileUsedWords);
                int gpuBits = CountSetBits(gpu.tileMasks, gpu.TileUsedWords);
                Debug.Log($"Tile bits: cpu {cpuBits}, gpu {gpuBits}, extra {diff.tileMaskExtraBits} " +
                          $"({100f * gpuBits / Mathf.Max(1, cpuBits):0.#}% of CPU). " + diff);
                Assert.IsTrue(diff.CoversCpuBits, "The GPU list drops bits the CPU sets: " + diff);
                Assert.AreEqual(0, diff.zBinMismatchBits,
                    "The zbin path is the exact one and must match the CPU word for word: " + diff);
                Assert.LessOrEqual(gpuBits, cpuBits * 4,
                    "The conservative tile AABB is far looser than the hand-checked bound.");

                // Extra candidates are only useful if they cost nothing visually: the lights they add
                // are attenuated to zero, so the image must be bit-identical.
                var imageDiff = Compare(cpuImage, gpuImage);
                Assert.AreEqual(0f, imageDiff.max,
                    $"The GPU list changed pixels (mean {imageDiff.mean:0.####}, max {imageDiff.max:0.####}).");
            }
            finally
            {
                ClusterListGpuBuilder.enabled = true;
                ClusterListParityCapture.Reset();
                TearDownRig();
            }
        }

        static IEnumerator WaitForGpuSnapshot()
        {
            for (int i = 0; i < 10 && ClusterListParityCapture.lastGpu.zBins == null; i++)
                yield return null;
        }

        // G2: the pruned bitmap may only drop candidates that are attenuated to zero, so the frame
        // has to be bit-identical to the unpruned list. The stats readback proves the prune pass
        // actually ran on this rig (kept + cleared covers the conservative tile bits).
        [UnityTest]
        public IEnumerator DepthPruneKeepsPixelsIdentical()
        {
            yield return SetUpRig(ClusteredPipelinePath, 640, 360, 24);
            ClusterListGpuBuilder.enabled = true;
            ClusterListGpuBuilder.pruneByDepth = true;
            ClusterListGpuBuilder.pruneStatsEnabled = true;
            try
            {
                ClusterListGpuBuilder.lastPruneKeptBits = 0;
                ClusterListGpuBuilder.lastPruneClearedBits = 0;
                yield return RenderFrames(WarmupFrames);
                // The stats arrive through an async readback; give it a few frames instead of
                // guessing how deep the graphics queue is at this point in the suite.
                for (int i = 0; i < 30 &&
                     ClusterListGpuBuilder.lastPruneKeptBits + ClusterListGpuBuilder.lastPruneClearedBits == 0; i++)
                    yield return RenderFrames(1);
                int kept = ClusterListGpuBuilder.lastPruneKeptBits;
                int cleared = ClusterListGpuBuilder.lastPruneClearedBits;
                var pruned = ReadPixels();
                Debug.Log($"Depth prune bits: kept {kept}, cleared {cleared}.");

                ClusterListGpuBuilder.pruneByDepth = false;
                yield return RenderFrames(2);
                var unpruned = ReadPixels();

                var diff = Compare(pruned, unpruned);
                Assert.AreEqual(0f, diff.max,
                    $"The depth prune changed pixels (mean {diff.mean:0.####}, max {diff.max:0.####}).");
                Assert.Greater(kept + cleared, 0,
                    "The prune readback never arrived; the pass did not run or the capture is disabled.");
            }
            finally
            {
                ClusterListGpuBuilder.pruneByDepth = false;
                ClusterListGpuBuilder.pruneStatsEnabled = false;
                ClusterListGpuBuilder.lastPruneKeptBits = 0;
                ClusterListGpuBuilder.lastPruneClearedBits = 0;
                TearDownRig();
            }
        }

        // G3: the compact list is the same (tile, zbin) intersection the bitmap iterator computes,
        // so the frame has to be bit-identical to the bitmap path. Overflow cells fall back to the
        // bitmap inside the kernel, which is covered by the same comparison.
        [UnityTest]
        public IEnumerator CompactListsKeepPixelsIdentical()
        {
            yield return SetUpRig(ClusteredPipelinePath, 640, 360, 24);
            ClusterListGpuBuilder.enabled = true;
            ClusterListGpuBuilder.compactLists = true;
            ClusterListGpuBuilder.compactStatsEnabled = true;
            try
            {
                ResetCompactStats();
                yield return RenderFrames(WarmupFrames);
                for (int i = 0; i < 30 && ClusterListGpuBuilder.lastCompactEntries <= 0; i++)
                    yield return RenderFrames(1);
                int entries = ClusterListGpuBuilder.lastCompactEntries;
                int cells = ClusterListGpuBuilder.lastCompactCells;
                int overflowCells = ClusterListGpuBuilder.lastCompactOverflowCells;
                int overflowTiles = ClusterListGpuBuilder.lastCompactOverflowTiles;
                int scannedCells = ClusterListGpuBuilder.lastCompactScannedCells;
                var compact = ReadPixels();
                Debug.Log($"Compact cells: cells {cells}, entries {entries}, scanned {scannedCells}, " +
                          $"overflow cells {overflowCells}, overflow tiles {overflowTiles}.");

                ClusterListGpuBuilder.compactLists = false;
                yield return RenderFrames(2);
                var bitmap = ReadPixels();

                var diff = Compare(bitmap, compact);
                Assert.AreEqual(0f, diff.max,
                    $"The compact list changed pixels (mean {diff.mean:0.####}, max {diff.max:0.####}).");
                Assert.Greater(entries, 0,
                    "The compact stats never arrived; the cell build did not run or the readback failed.");
                Assert.Greater(scannedCells, 0, "The cell build scanned no (tile, zbin) cells.");
            }
            finally
            {
                ClusterListGpuBuilder.compactLists = false;
                ClusterListGpuBuilder.compactStatsEnabled = false;
                ResetCompactStats();
                TearDownRig();
            }
        }

        static void ResetCompactStats()
        {
            ClusterListGpuBuilder.lastCompactEntries = 0;
            ClusterListGpuBuilder.lastCompactCells = 0;
            ClusterListGpuBuilder.lastCompactOverflowCells = 0;
            ClusterListGpuBuilder.lastCompactOverflowTiles = 0;
            ClusterListGpuBuilder.lastCompactScannedCells = 0;
        }

        static int CountProbeItems(ClusterListSnapshot snapshot)
        {
            int probes = 0;
            for (int i = snapshot.lightCount; i < snapshot.items.Length; i++)
            {
                Assert.AreEqual(ClusterItemType.Probe, snapshot.items[i].itemType, "Item " + i);
                probes++;
            }
            return probes;
        }

        static int CountSetBits(uint[] words, int count)
        {
            int bits = 0;
            for (int i = 0; i < count; i++)
            {
                for (uint word = words[i]; word != 0; word &= word - 1)
                    bits++;
            }
            return bits;
        }

        // ---------------------------------------------------------------------------------------------
        // Harness
        // ---------------------------------------------------------------------------------------------

        IEnumerator SetUpRig(string pipelinePath, int width, int height, int additionalLights, bool transparent = false)
        {
            m_PreviousPipeline = GraphicsSettings.defaultRenderPipeline;
            m_PreviousQualityPipeline = QualitySettings.renderPipeline;

#if UNITY_EDITOR
            m_Pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
            Assert.IsNotNull(m_Pipeline, "Missing pipeline asset " + pipelinePath);
#else
            Assert.Ignore("The PlayMode regression loads its pipeline assets through the AssetDatabase.");
#endif

            GraphicsSettings.defaultRenderPipeline = m_Pipeline;
            QualitySettings.renderPipeline = m_Pipeline;
            yield return null;

            m_Root = new GameObject("Clustered Deferred PlayMode Rig");
            var lit = CreateMaterial("Universal Render Pipeline/Lit", "Cd Lit", new Color(0.55f, 0.57f, 0.6f), 0f, 0.6f);
            var simpleLit = CreateMaterial("Universal Render Pipeline/Simple Lit", "Cd SimpleLit", new Color(0.8f, 0.45f, 0.3f), 0f, 0.4f);
            var complexLit = CreateMaterial("Universal Render Pipeline/Complex Lit", "Cd ComplexLit", new Color(0.3f, 0.7f, 0.4f), 0f, 0.5f);
            var unlit = CreateMaterial("Universal Render Pipeline/Unlit", "Cd Unlit", new Color(0.9f, 0.9f, 0.2f), 0f, 0f);

            AddBox(lit, "Cd Floor", new Vector3(0f, 0f, 0f), new Vector3(30f, 0.1f, 24f));
            AddBox(lit, "Cd Back Wall", new Vector3(0f, 4f, 8f), new Vector3(24f, 8f, 0.2f));
            AddSphere(simpleLit, "Cd SimpleLit Sphere", new Vector3(-2.2f, 1f, 0.5f), 1.4f);
            AddSphere(complexLit, "Cd ComplexLit Sphere", new Vector3(0f, 1f, 1.2f), 1.4f);
            AddSphere(unlit, "Cd Unlit Sphere", new Vector3(2.2f, 1f, 0.2f), 1.4f);

            if (transparent)
            {
                var transparentMaterial = Resources.Load<Material>("CdTransparent");
                Assert.IsNotNull(transparentMaterial, "Missing Resources/CdTransparent.mat.");
                AddSphere(transparentMaterial, "Cd Transparent Sphere", new Vector3(0f, 1.4f, -4f), 3f);
            }

            var mainLightObject = new GameObject("Cd Main Light");
            mainLightObject.transform.SetParent(m_Root.transform, false);
            var mainLight = mainLightObject.AddComponent<Light>();
            mainLight.type = LightType.Directional;
            mainLight.color = new Color(1f, 0.96f, 0.9f);
            mainLight.intensity = 0.6f;
            mainLight.shadows = LightShadows.None;
            mainLightObject.transform.rotation = Quaternion.Euler(45f, -30f, 0f);

            SpawnLights(additionalLights);

            m_CameraObject = new GameObject("Cd Camera");
            m_Camera = m_CameraObject.AddComponent<Camera>();
            m_Camera.clearFlags = CameraClearFlags.SolidColor;
            m_Camera.backgroundColor = new Color(0.02f, 0.03f, 0.05f, 1f);
            m_Camera.transform.position = new Vector3(0f, 2.2f, -11f);
            m_Camera.transform.LookAt(new Vector3(0f, 1.2f, 0f));
            m_Camera.fieldOfView = 55f;
            m_Camera.nearClipPlane = 0.1f;
            m_Camera.farClipPlane = 60f;
            var cameraData = m_Camera.GetUniversalAdditionalCameraData();
            cameraData.renderPostProcessing = false;
            cameraData.renderShadows = false;
            cameraData.antialiasing = AntialiasingMode.None;

            m_Target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "Clustered Deferred PlayMode Target",
            };
            m_Target.Create();
            m_Camera.targetTexture = m_Target;

            DynamicGI.UpdateEnvironment();
            yield return null;
        }

        void TearDownRig()
        {
            if (m_CameraObject != null)
                Object.Destroy(m_CameraObject);
            if (m_Root != null)
                Object.Destroy(m_Root);
            if (m_Target != null)
            {
                m_Target.Release();
                Object.Destroy(m_Target);
            }

            foreach (var material in m_Materials)
            {
                if (material != null)
                    Object.Destroy(material);
            }

            m_Materials.Clear();
            m_Lights.Clear();

            GraphicsSettings.defaultRenderPipeline = m_PreviousPipeline;
            QualitySettings.renderPipeline = m_PreviousQualityPipeline;

            m_CameraObject = null;
            m_Camera = null;
            m_Root = null;
            m_Target = null;
            m_Pipeline = null;
        }

        void SwitchPipeline(string pipelinePath)
        {
#if UNITY_EDITOR
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
            Assert.IsNotNull(pipeline, "Missing pipeline asset " + pipelinePath);
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
#else
            Assert.Ignore("The PlayMode regression loads its pipeline assets through the AssetDatabase.");
#endif
        }

        void SpawnLights(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var lightObject = new GameObject("Cd Light " + i);
                lightObject.transform.SetParent(m_Root.transform, false);
                int x = i % 8;
                int y = (i / 8) % 4;
                int z = i / 32;
                lightObject.transform.position = new Vector3(-3.5f + x, 0.8f + y * 0.8f, -6.5f + z * 2.2f + x * 0.25f);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 5f;
                light.intensity = 1.2f;
                light.shadows = LightShadows.None;
                light.color = Color.HSVToRGB((i * 0.173f) % 1f, 0.55f, 1f);
                m_Lights.Add(light);
            }
        }

        Material CreateMaterial(string shaderName, string name, Color color, float metallic, float smoothness)
        {
            var shader = Shader.Find(shaderName);
            Assert.IsNotNull(shader, "Missing shader " + shaderName);
            var material = new Material(shader) { name = name };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            m_Materials.Add(material);
            return material;
        }

        GameObject AddBox(Material material, string name, Vector3 position, Vector3 size)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(m_Root.transform, false);
            box.transform.position = position;
            box.transform.localScale = size;
            box.GetComponent<Renderer>().sharedMaterial = material;
            return box;
        }

        GameObject AddSphere(Material material, string name, Vector3 position, float scale)
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = name;
            sphere.transform.SetParent(m_Root.transform, false);
            sphere.transform.position = position;
            sphere.transform.localScale = Vector3.one * scale;
            sphere.GetComponent<Renderer>().sharedMaterial = material;
            return sphere;
        }

        IEnumerator RenderFrames(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                m_Camera.Render();
                yield return null;
            }
        }

        Color[] ReadPixels()
        {
            var texture = new Texture2D(m_Target.width, m_Target.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                var previous = RenderTexture.active;
                RenderTexture.active = m_Target;
                texture.ReadPixels(new Rect(0, 0, m_Target.width, m_Target.height), 0, 0, false);
                texture.Apply(false, false);
                RenderTexture.active = previous;
                return texture.GetPixels();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        static int CountClusterTargets()
        {
            return Resources.FindObjectsOfTypeAll<RenderTexture>()
                .Count(rt => rt != null &&
                             (rt.name == "Clustered Deferred Material Mask" || rt.name == "Clustered Deferred Lighting"));
        }

        static DiffStats Compare(Color[] a, Color[] b)
        {
            Assert.AreEqual(a.Length, b.Length);
            var stats = new DiffStats();
            double total = 0;
            for (int i = 0; i < a.Length; i++)
            {
                float dr = Mathf.Abs(a[i].r - b[i].r) * 255f;
                float dg = Mathf.Abs(a[i].g - b[i].g) * 255f;
                float db = Mathf.Abs(a[i].b - b[i].b) * 255f;
                if (!IsFinite(dr) || !IsFinite(dg) || !IsFinite(db) ||
                    !IsFinite(a[i].r) || !IsFinite(a[i].g) || !IsFinite(a[i].b) ||
                    !IsFinite(b[i].r) || !IsFinite(b[i].g) || !IsFinite(b[i].b))
                {
                    stats.nonFinite++;
                    continue;
                }

                total += dr + dg + db;
                float channelMax = Mathf.Max(dr, Mathf.Max(dg, db));
                if (channelMax > stats.max)
                    stats.max = channelMax;
                if (channelMax > 8f)
                    stats.over8++;
            }

            stats.mean = (float)(total / (a.Length * 3.0));
            return stats;
        }

        static void AssertImageUsable(Color[] pixels, string label)
        {
            int lit = 0;
            foreach (var pixel in pixels)
            {
                Assert.IsTrue(IsFinite(pixel.r) && IsFinite(pixel.g) && IsFinite(pixel.b), label + " image contains non-finite pixels.");
                if (pixel.r + pixel.g + pixel.b > 0.05f)
                    lit++;
            }

            Assert.Greater(lit, pixels.Length / 10, label + " image is almost black; the rig did not light.");
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
