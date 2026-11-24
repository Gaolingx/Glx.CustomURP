using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class ContactShadowRaymarchRenderTests
    {
        const int W = 256, H = 192, Layer = 30;
        const float ReceiverZ = 5f, BlockerZ = 4.84f;
        const string EvidenceFlag = "-contactShadowT3Evidence";
        GameObject m_Root, m_Blocker, m_EdgeBlocker;
        Camera m_Camera;
        Light m_Sun, m_OldSun;
        VolumeProfile m_Profile;
        ContactShadowsVolume m_Settings;
        UniversalRendererData m_Renderer;
        ScreenSpaceContactShadowsRendererFeature m_Feature;
        UniversalRenderPipelineAsset m_Pipeline;
        RenderPipelineAsset m_OldPipeline;
        RenderTexture m_Target, m_OldActive;
        bool m_OldAsync, m_FlipRows, m_RowsKnown;
        string m_Output;

        [Serializable]
        class Metrics
        {
            public string api;
            public bool flippedReadback;
            public int dispatches, behindX, facingX, behindY, facingY, coarseOnlyHits, planeHits;
            public int diff4To10, diff10To64, diff4To64;
            public float centerFade, innerEdgeFade, outerEdgeFade, nearHalfFade, farHalfFade;
        }

        [SetUp]
        public void SetUp()
        {
            m_OldPipeline = QualitySettings.renderPipeline;
            m_OldSun = RenderSettings.sun;
            m_OldActive = RenderTexture.active;
            m_OldAsync = ShaderUtil.allowAsyncCompilation;
            m_RowsKnown = false;
            m_Output = null;
        }

        [TearDown]
        public void TearDown()
        {
            QualitySettings.renderPipeline = m_OldPipeline;
            RenderSettings.sun = m_OldSun;
            ShaderUtil.allowAsyncCompilation = m_OldAsync;
            RenderTexture.active = m_OldActive;
            if (m_Camera) m_Camera.targetTexture = null;
            Object.DestroyImmediate(m_Root);
            Object.DestroyImmediate(m_Target);
            Object.DestroyImmediate(m_Pipeline);
            if (m_Feature) m_Feature.Dispose();
            Object.DestroyImmediate(m_Feature);
            Object.DestroyImmediate(m_Renderer);
            if (m_Profile)
            {
                foreach (var component in m_Profile.components) Object.DestroyImmediate(component);
                Object.DestroyImmediate(m_Profile);
            }
        }

        [Test]
        public void MainLightRaymarchWritesDirectionalMaskAndFade()
        {
            var api = SystemInfo.graphicsDeviceType;
            if ((api != GraphicsDeviceType.Direct3D11 && api != GraphicsDeviceType.Direct3D12) ||
                !ScreenSpaceContactShadowsCapability.EvaluatePlatform().supported || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("T3 requires D3D11/12, compute, R32_UInt load/store and GPU readback.");
            var args = Environment.GetCommandLineArgs();
            int flag = Array.IndexOf(args, EvidenceFlag);
            if (flag >= 0)
            {
                Assert.IsTrue(flag + 1 < args.Length && !string.IsNullOrWhiteSpace(args[flag + 1]) && !args[flag + 1].StartsWith("-"),
                    EvidenceFlag + " requires an output root");
                m_Output = Path.GetFullPath(args[flag + 1]);
                Directory.CreateDirectory(m_Output);
            }
            ShaderUtil.allowAsyncCompilation = false;
            CreateFixture();
            var m = new Metrics { api = api.ToString() };
            m_Sun.transform.rotation = Quaternion.LookRotation(Vector3.left);
            var x10 = Capture("Horizontal10", 10, m);
            var x4 = Capture("Horizontal4", 4, m);
            var x64 = Capture("Horizontal64", 64, m);
            m.flippedReadback = m_FlipRows;
            m.diff4To10 = MaskDifference(x4, x10);
            m.diff10To64 = MaskDifference(x10, x64);
            m.diff4To64 = MaskDifference(x4, x64);
            var shadow = new RectInt(115, 123, 28, 14);
            m.behindX = Measure(x10, shadow, out m.centerFade);
            m.facingX = Measure(x10, new RectInt(157, 123, 28, 14), out _);
            // Row 120 shares a coarse texel with blocker row 121. Horizontal rays in 120
            // must miss when the candidate is confirmed against full-resolution depth.
            m.coarseOnlyHits = Measure(x64, new RectInt(115, 120, 28, 1), out _);
            Measure(x10, new RectInt(175, 179, 25, 3), out m.innerEdgeFade);
            Measure(x10, new RectInt(175, 187, 25, 3), out m.outerEdgeFade);

            m_Sun.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            var y10 = Capture("Vertical10", 10, m);
            m.behindY = Measure(y10, new RectInt(146, 94, 8, 25), out _);
            m.facingY = Measure(y10, new RectInt(146, 141, 8, 25), out _);

            m_Sun.transform.rotation = Quaternion.LookRotation(Vector3.left);
            m_Settings.minDistance.Override(ReceiverZ - 1);
            m_Settings.fadeInDistance.Override(2);
            Measure(Capture("NearFade", 10, m), shadow, out m.nearHalfFade);
            m_Settings.minDistance.Override(0);
            m_Settings.maxDistance.Override(ReceiverZ + 1);
            m_Settings.fadeDistance.Override(2);
            Measure(Capture("FarFade", 10, m), shadow, out m.farHalfFade);

            m_Settings.maxDistance.Override(50);
            m_Blocker.SetActive(false);
            m_EdgeBlocker.SetActive(false);
            // EditMode renders can share a frame index. Change VP to invalidate cached depth.
            m_Root.transform.position += Vector3.right;
            m.planeHits = Measure(Capture("PlaneOnly", 10, m), new RectInt(0, 0, W, H), out _);
            string json = JsonUtility.ToJson(m, true);
            TestContext.WriteLine(json);
            if (m_Output != null) File.WriteAllText(Path.Combine(m_Output, "Raymarch.json"), json);

            Assert.Greater(m.behindX, 30, "No horizontal contact band behind blocker");
            Assert.Zero(m.facingX, "Unexpected mask on the side facing +X");
            Assert.Greater(m.behindY, 30, "No vertical contact band below blocker");
            Assert.Zero(m.facingY, "Unexpected mask on the side facing +Y");
            Assert.Zero(m.coarseOnlyHits, "A mip 1 candidate leaked past the mip 0 silhouette");
            Assert.Zero(m.planeHits, "Self-shadow on a bare receiver at default bias");
            Assert.Greater(Mathf.Min(m.diff4To10, m.diff10To64, m.diff4To64), 0, "Sample counts produced identical masks");
            Assert.That(m.centerFade, Is.InRange(254f, 255f));
            Assert.That(m.innerEdgeFade, Is.InRange(1f, 253f));
            Assert.That(m.outerEdgeFade, Is.InRange(1f, m.innerEdgeFade - 1));
            Assert.That(m.nearHalfFade, Is.InRange(125f, 130f), "Near ramp at 50%");
            Assert.That(m.farHalfFade, Is.InRange(125f, 130f), "Far ramp at 50%");
        }

        GameObject Child(string name)
        {
            var go = new GameObject(name) { layer = Layer, hideFlags = HideFlags.DontSave };
            go.transform.SetParent(m_Root.transform, false);
            return go;
        }

        void CreateFixture()
        {
            m_Root = new GameObject("T3 raymarch fixture") { hideFlags = HideFlags.DontSave };
            m_Root.transform.position = new Vector3(13, -7, -20); // A world-origin bias would push the receiver away from this camera.
            m_Camera = Child("Camera").AddComponent<Camera>();
            m_Camera.enabled = false;
            m_Camera.cameraType = CameraType.Game;
            m_Camera.clearFlags = CameraClearFlags.SolidColor;
            m_Camera.cullingMask = 1 << Layer;
            m_Camera.fieldOfView = 50;
            m_Camera.nearClipPlane = 1;
            m_Camera.farClipPlane = 15;
            var cameraData = m_Camera.GetUniversalAdditionalCameraData();
            cameraData.volumeLayerMask = 1 << Layer;
            cameraData.allowXRRendering = false;
            cameraData.renderPostProcessing = false;
            m_Target = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            Assert.IsTrue(m_Target.Create());
            m_Camera.targetTexture = m_Target;
            m_Camera.aspect = (float)W / H;

            var env = Child("Volume and main light");
            m_Sun = env.AddComponent<Light>();
            m_Sun.type = LightType.Directional;
            env.AddComponent<ContactShadowLight>();
            m_Sun.shadows = LightShadows.None;
            RenderSettings.sun = m_Sun;
            m_Profile = ScriptableObject.CreateInstance<VolumeProfile>();
            m_Settings = m_Profile.Add<ContactShadowsVolume>(true);
            m_Settings.enabled.Override(true);
            m_Settings.length.Override(0.64f); // Long enough to span several coarse sample intervals.
            var volume = env.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1000;
            volume.sharedProfile = m_Profile;
            Slab("Receiver", new RectInt(-3, -3, W + 6, H + 6), ReceiverZ);
            m_Blocker = Slab("Thin blocker", new RectInt(145, 121, 10, 18), BlockerZ);
            m_EdgeBlocker = Slab("Edge blocker", new RectInt(203, 172, 8, 20), BlockerZ);

            m_Renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            ResourceReloader.ReloadAllNullIn(m_Renderer, UniversalRenderPipelineAsset.packagePath);
            m_Feature = ScriptableObject.CreateInstance<ScreenSpaceContactShadowsRendererFeature>();
            m_Renderer.rendererFeatures.Add(m_Feature);
            using (var so = new SerializedObject(m_Feature)) ScreenSpaceContactShadowsEditor.AssignDefaults(so);
            m_Pipeline = UniversalRenderPipelineAsset.Create(m_Renderer);
            m_Pipeline.msaaSampleCount = 1;
            m_Pipeline.renderScale = 1;
            QualitySettings.renderPipeline = m_Pipeline;
        }

        GameObject Slab(string name, RectInt rect, float frontDepth)
        {
            Vector3 World(int x, int y) => m_Camera.ViewportToWorldPoint(new Vector3((float)x / W, (float)y / H, frontDepth));
            var lo = World(rect.xMin, rect.yMin);
            var hi = World(rect.xMax, rect.yMax);
            var go = Child(name);
            go.transform.position = (lo + hi) * 0.5f + Vector3.forward * 0.002f;
            go.transform.localScale = new Vector3(hi.x - lo.x, hi.y - lo.y, 0.004f);
            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            go.AddComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                UniversalRenderPipelineAsset.packagePath + "/Runtime/Materials/Lit.mat");
            return go;
        }

        uint[] Capture(string label, int samples, Metrics metrics)
        {
            m_Settings.sampleCount.Override(samples);
            int before = ScreenSpaceContactShadowsPass.editorDispatchCount;
            m_Camera.Render();
            int delta = ScreenSpaceContactShadowsPass.editorDispatchCount - before;
            Assert.AreEqual(1, delta, label + ": contact dispatches");
            metrics.dispatches += delta;
            Assert.IsTrue(m_Feature.TryGetHistory(m_Camera, 0, out var history));
            if (!m_RowsKnown) DetectRowOrder(history.depthPyramidSource);
            Assert.IsNotNull(history.packedTexture);
            var rt = history.packedTexture.rt;
            Assert.AreEqual(GraphicsFormat.R32_UInt, rt.graphicsFormat);
            Assert.AreEqual(new Vector2Int(W, H), new Vector2Int(rt.width, rt.height));
            var request = AsyncGPUReadback.Request(rt, 0);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, label);
            var raw = request.GetData<uint>();
            var pixels = new uint[W * H]; // Viewport order (y=0 at bottom), also used for PNGs.
            Assert.AreEqual(pixels.Length, raw.Length);
            for (int y = 0; y < H; ++y)
            for (int x = 0; x < W; ++x)
            {
                uint p = raw[(m_FlipRows ? H - 1 - y : y) * W + x];
                if ((p & 0x00fffffeu) != 0 || ((p & 1u) == 0 && p != 0))
                    Assert.Fail($"{label} ({x},{y}): invalid single-light packed value 0x{p:X8}");
                pixels[y * W + x] = p;
            }
            if (m_Output != null)
            {
                WritePng(label + "Mask", pixels, false);
                WritePng(label + "Fade", pixels, true);
            }
            return pixels;
        }

        void DetectRowOrder(ScreenSpaceDepthPyramid pyramid)
        {
            // The GPU projection used to render to an RT can already include a Y flip.
            // Locate the asymmetric blocker in independent mip 0 depth, never in the mask under test.
            var rt = pyramid.texture.rt;
            var request = AsyncGPUReadback.Request(rt, 0);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, "Depth atlas orientation probe");
            var data = request.GetData<float>();
            var uv = m_Camera.WorldToViewportPoint(m_Blocker.transform.position);
            int x = Mathf.FloorToInt(uv.x * W), y = Mathf.FloorToInt(uv.y * H);
            var origin = pyramid.info.mipLevelOffsets[0];
            float At(int row) => data[(origin.y + row) * rt.width + origin.x + x];
            float difference = At(y) - At(H - 1 - y);
            Assert.Greater(Mathf.Abs(difference), 0.001f, "Depth does not distinguish blocker from mirrored receiver");
            m_FlipRows = SystemInfo.usesReversedZBuffer ? difference < 0 : difference > 0;
            m_RowsKnown = true;
        }

        static int MaskDifference(uint[] a, uint[] b)
        {
            int count = 0;
            for (int i = 0; i < a.Length; ++i) if (((a[i] ^ b[i]) & 1u) != 0) ++count;
            return count;
        }

        static int Measure(uint[] pixels, RectInt region, out float meanHitFade)
        {
            int hits = 0, sum = 0;
            for (int y = region.yMin; y < region.yMax; ++y)
            for (int x = region.xMin; x < region.xMax; ++x)
            {
                uint p = pixels[y * W + x];
                if ((p & 1u) == 0) continue;
                ++hits;
                sum += (int)(p >> 24);
            }
            meanHitFade = hits > 0 ? (float)sum / hits : 0;
            return hits;
        }

        void WritePng(string label, uint[] data, bool fade)
        {
            var image = new Texture2D(W, H, TextureFormat.RGB24, false, true);
            try
            {
                var pixels = new Color32[data.Length];
                for (int i = 0; i < data.Length; ++i)
                {
                    byte gray = fade ? (byte)(data[i] >> 24) : (byte)((data[i] & 1u) * 255);
                    pixels[i] = new Color32(gray, gray, gray, 255);
                }
                image.SetPixels32(pixels);
                image.Apply();
                File.WriteAllBytes(Path.Combine(m_Output, label + ".png"), image.EncodeToPNG());
            }
            finally { Object.DestroyImmediate(image); }
        }
    }
}
