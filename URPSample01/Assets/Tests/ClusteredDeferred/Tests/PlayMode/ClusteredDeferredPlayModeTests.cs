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
    // The suite drives real cameras through both deferred renderers against a rig with one of
    // every material type that matters to the mask pass (Lit, SimpleLit, Unlit/forward-only,
    // ComplexLit/skipped), so the A/B covers lighting, the material mask and the forward-only
    // fallback at once. The player-only arena runner keeps the large light-count sweep; this suite
    // is the in-editor gate that runs with the normal EditMode/PlayMode batch.
    public class ClusteredDeferredPlayModeTests
    {
        const string RendererRoot = "Assets/Tests/ClusteredDeferred/Renderers";
        const string StencilPipelinePath = RendererRoot + "/CdStencilDeferredPipeline.asset";
        const string ClusteredPipelinePath = RendererRoot + "/CdClusteredDeferredPipeline.asset";
        const string ClusteredRendererPath = RendererRoot + "/CdClusteredDeferredRenderer.asset";

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
        UniversalRendererData m_ClusteredRendererData;
        GameObject m_Root;
        GameObject m_CameraObject;
        Camera m_Camera;
        RenderTexture m_Target;
        readonly List<Light> m_Lights = new List<Light>();
        readonly List<Material> m_Materials = new List<Material>();

        [UnityTest]
        public IEnumerator StencilMatchesClusteredAcrossMaterialTypes()
        {
            yield return SetUpRig(ClusteredPipelinePath, 640, 360, 24);
            try
            {
                yield return RenderFrames(WarmupFrames);
                var clustered = ReadPixels();
                AssertImageUsable(clustered, "clustered");

                SwitchPipeline(StencilPipelinePath);
                yield return null;
                yield return RenderFrames(WarmupFrames);
                var stencil = ReadPixels();
                AssertImageUsable(stencil, "stencil");

                var diff = Compare(stencil, clustered);
                Assert.AreEqual(0, diff.nonFinite, "A/B image contains non-finite pixels.");
                Assert.LessOrEqual(diff.mean, kMeanThreshold,
                    $"mean channel difference {diff.mean:0.###}/255 exceeds {kMeanThreshold}");
                Assert.LessOrEqual(diff.max, kMaxThreshold,
                    $"max channel difference {diff.max:0.###}/255 exceeds {kMaxThreshold}");
                Assert.LessOrEqual(diff.over8, clustered.Length / 1000,
                    $"{diff.over8} pixels differ by more than 8/255; expected a sparse light-volume-edge residue.");
            }
            finally
            {
                TearDownRig();
            }
        }

        [UnityTest]
        public IEnumerator RuntimeSwitchFallsBackAndReenables()
        {
            yield return SetUpRig(ClusteredPipelinePath, 640, 360, 24);
#if !UNITY_EDITOR
            Assert.Ignore("The runtime switch loads its renderer asset through the AssetDatabase.");
            yield break;
#else
            bool original = m_ClusteredRendererData.clusteredDeferredLighting;
            try
            {
                yield return RenderFrames(WarmupFrames);
                var clusteredOn = ReadPixels();

                SwitchPipeline(StencilPipelinePath);
                yield return null;
                yield return RenderFrames(WarmupFrames);
                var stencil = ReadPixels();

                SwitchPipeline(ClusteredPipelinePath);
                yield return null;
                yield return RenderFrames(WarmupFrames);

                m_ClusteredRendererData.clusteredDeferredLighting = false;
                yield return null;
                yield return RenderFrames(WarmupFrames);
                Assert.IsFalse(m_ClusteredRendererData.clusteredDeferredLighting);
                var toggledOff = ReadPixels();
                var offDiff = Compare(stencil, toggledOff);
                Assert.LessOrEqual(offDiff.mean, kMeanThreshold,
                    "Turning the switch off at runtime did not return to stencil deferred.");
                Assert.LessOrEqual(offDiff.max, kMaxThreshold,
                    $"Runtime off-image max difference {offDiff.max:0.###}/255.");

                m_ClusteredRendererData.clusteredDeferredLighting = true;
                yield return null;
                yield return RenderFrames(WarmupFrames);
                var toggledOn = ReadPixels();
                var onDiff = Compare(clusteredOn, toggledOn);
                Assert.LessOrEqual(onDiff.mean, 0.01f, "Re-enabling at runtime changed the clustered image.");
                Assert.LessOrEqual(onDiff.max, 1.0f, "Re-enabling at runtime changed the clustered image.");
            }
            finally
            {
                m_ClusteredRendererData.clusteredDeferredLighting = original;
                EditorUtility.SetDirty(m_ClusteredRendererData);
                AssetDatabase.SaveAssets();
                TearDownRig();
            }
#endif
        }

        [UnityTest]
        public IEnumerator TemporaryTargetsAreReleasedWhenThePathIsRemoved()
        {
            yield return SetUpRig(ClusteredPipelinePath, 640, 360, 8);
            try
            {
                yield return RenderFrames(2);
                Assert.AreEqual(2, CountClusterTargets(), "The clustered pass did not allocate its mask/lighting targets.");

                SwitchPipeline(StencilPipelinePath);
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

        // G1's parity gate compares the GPU builder against these two bitmaps, so the CPU list has to
        // be stable frame to frame and the item stream has to describe exactly what the bits index.
        [UnityTest]
        public IEnumerator ClusterListSnapshotIsDeterministicAndDescribesTheItemStream()
        {
            yield return SetUpRig(ClusteredPipelinePath, 640, 360, 24);
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
                ClusterListParityCapture.Reset();
                TearDownRig();
            }
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

        IEnumerator SetUpRig(string pipelinePath, int width, int height, int additionalLights)
        {
            m_PreviousPipeline = GraphicsSettings.defaultRenderPipeline;
            m_PreviousQualityPipeline = QualitySettings.renderPipeline;

#if UNITY_EDITOR
            m_Pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
            m_ClusteredRendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(ClusteredRendererPath);
            Assert.IsNotNull(m_Pipeline, "Missing pipeline asset " + pipelinePath);
            Assert.IsNotNull(m_ClusteredRendererData, "Missing clustered renderer asset " + ClusteredRendererPath);
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
            m_ClusteredRendererData = null;
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
