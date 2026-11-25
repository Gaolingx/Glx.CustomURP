using System;
using System.IO;
using NUnit.Framework;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class ContactShadowPyramidRenderTests
    {
        const int Width = 96, Height = 64, Layer = 30;
        const float Near = 1f, Far = 13f, FrontDepth = 4f;
        const string EvidenceFlag = "-contactShadowT2Evidence";

        GameObject m_Rig, m_Environment, m_Cube;
        Camera m_Camera;
        Light m_PreviousSun;
        VolumeProfile m_Profile;
        RenderTexture m_Target, m_PreviousActive;
        RenderPipelineAsset m_PreviousQuality;
        UniversalRendererData m_Renderer;
        UniversalRenderPipelineAsset m_Pipeline;
        bool m_PreviousAsyncCompilation;

        [SetUp]
        public void SetUp()
        {
            m_PreviousSun = RenderSettings.sun;
            m_PreviousActive = RenderTexture.active;
            m_PreviousQuality = QualitySettings.renderPipeline;
            m_PreviousAsyncCompilation = ShaderUtil.allowAsyncCompilation;
        }

        [TearDown]
        public void TearDown()
        {
            RenderSettings.sun = m_PreviousSun;
            QualitySettings.renderPipeline = m_PreviousQuality;
            ShaderUtil.allowAsyncCompilation = m_PreviousAsyncCompilation;
            RenderTexture.active = m_PreviousActive;
            if (m_Camera) m_Camera.targetTexture = null;
            Object.DestroyImmediate(m_Rig);
            Object.DestroyImmediate(m_Environment);
            Object.DestroyImmediate(m_Cube);
            Object.DestroyImmediate(m_Target);
            Object.DestroyImmediate(m_Pipeline); // Disposes the constructed renderer before its data/features.
            if (m_Renderer)
            {
                foreach (var feature in m_Renderer.rendererFeatures)
                {
                    feature.Dispose(); // Also covers fixture failures before the renderer was constructed.
                    Object.DestroyImmediate(feature);
                }
                Object.DestroyImmediate(m_Renderer);
            }
            if (m_Profile)
            {
                foreach (var component in m_Profile.components) Object.DestroyImmediate(component);
                Object.DestroyImmediate(m_Profile);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CameraRenderBuildsOneDepthAtlas(bool withSslAndGtao)
        {
            if (!ScreenSpaceContactShadowsCapability.EvaluatePlatform().supported ||
                !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("T2 render evidence requires a supported compute GPU, R32_UInt UAV and GPU readback.");

            ShaderUtil.allowAsyncCompilation = false;
            CreateScene(withSslAndGtao);
            m_Renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            ResourceReloader.ReloadAllNullIn(m_Renderer, UniversalRenderPipelineAsset.packagePath);
            var ssl = withSslAndGtao ? AddFeature<ScreenSpaceLightingRendererFeature>() : null;
            var gtao = withSslAndGtao ? AddFeature<GroundTruthAmbientOcclusionRendererFeature>() : null;
            var contact = AddFeature<ScreenSpaceContactShadowsRendererFeature>();
            using (var so = new SerializedObject(contact))
            {
                ScreenSpaceContactShadowsEditor.AssignDefaults(so);
                Assert.IsNotNull(so.FindProperty("m_Resources.contactShadowsCompute").objectReferenceValue);
            }
            m_Pipeline = UniversalRenderPipelineAsset.Create(m_Renderer);
            m_Pipeline.msaaSampleCount = 1;
            m_Pipeline.renderScale = 1f;
            QualitySettings.renderPipeline = m_Pipeline; // Overrides GraphicsSettings without editing the default asset.

            var marker = m_Environment.GetComponent<ContactShadowLight>();
            marker.castContactShadows = false;
            int inactiveCount = ScreenSpaceDepthPyramidPass.s_EditorExecuteCount;
            m_Camera.Render();
            Assert.IsFalse(contact.TryGetHistory(m_Camera, 0, out _));
            if (!withSslAndGtao)
                Assert.AreEqual(inactiveCount, ScreenSpaceDepthPyramidPass.s_EditorExecuteCount);
            marker.castContactShadows = true;
            m_Rig.transform.position += Vector3.right;
            m_Cube.transform.position += Vector3.right;
            int execute = ScreenSpaceDepthPyramidPass.s_EditorExecuteCount;
            int copy = ScreenSpaceDepthPyramidPass.s_EditorCopyDispatchCount;
            int downsample = ScreenSpaceDepthPyramidPass.s_EditorDownsampleDispatchCount;
            m_Camera.Render(); // No yields or other cameras between the counter snapshots.
            Assert.AreEqual(1, ScreenSpaceDepthPyramidPass.s_EditorExecuteCount - execute, "Depth Pyramid executions");
            Assert.AreEqual(1, ScreenSpaceDepthPyramidPass.s_EditorCopyDispatchCount - copy, "Mip 0 copy dispatches");
            Assert.AreEqual(203, (int)ScreenSpaceDepthPyramidSystem.sharedPass.renderPassEvent);
            Assert.IsTrue(contact.TryGetHistory(m_Camera, 0, out var history), "No contact history for the rendered camera");
            var pyramid = history.depthPyramidSource;
            Assert.IsNotNull(pyramid);
            Assert.IsTrue(pyramid.ready);
            Assert.AreEqual(pyramid.mipCount - 1, ScreenSpaceDepthPyramidPass.s_EditorDownsampleDispatchCount - downsample);
            Assert.IsNotNull(history.packedTexture);
            var packed = history.packedTexture.rt;
            Assert.IsTrue(packed && packed.IsCreated() && packed.enableRandomWrite);
            Assert.AreEqual(GraphicsFormat.R32_UInt, packed.graphicsFormat);
            Assert.AreEqual(new Vector2Int(Width, Height), new Vector2Int(packed.width, packed.height));
            Assert.AreEqual(TextureDimension.Tex2D, packed.dimension);
            if (withSslAndGtao)
            {
                Assert.IsTrue(ssl.historyStore.TryGet(m_Camera, 0, out var sslHistory));
                Assert.IsTrue(gtao.historyStore.TryGet(m_Camera, 0, out var gtaoHistory));
                Assert.AreSame(pyramid, sslHistory.depthPyramidSource);
                Assert.AreSame(pyramid, gtaoHistory.depthPyramidSource);
            }
            string configuration = withSslAndGtao ? "ContactSslGtao" : "ContactOnly";
            CheckAtlas(pyramid, configuration);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), EvidenceFlag) >= 0)
                ProbeFrameDebugger(configuration);
        }

        void ProbeFrameDebugger(string configuration)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var utility = typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility", true);
            bool local = (bool)utility.GetProperty("locallySupported", flags).GetValue(null);
            int events = 0;
            if (local)
            {
                var toggle = utility.GetMethod("SetEnabled", flags);
                int profiler = UnityEditorInternal.ProfilerDriver.connectedProfiler;
                try
                {
                    toggle.Invoke(null, new object[] { true, profiler });
                    m_Rig.transform.position += Vector3.right;
                    m_Camera.Render();
                    events = (int)utility.GetProperty("count", flags).GetValue(null);
                    var name = utility.GetMethod("GetFrameEventInfoName", flags);
                    for (int i = 0; i < events; ++i)
                        TestContext.WriteLine($"Frame Debugger event {i}: {name.Invoke(null, new object[] { i })}");
                }
                finally { toggle.Invoke(null, new object[] { false, profiler }); }
            }
            TestContext.WriteLine($"{configuration} Frame Debugger: locallySupported={local}, eventCount={events}");
        }

        T AddFeature<T>() where T : ScriptableRendererFeature
        {
            var feature = ScriptableObject.CreateInstance<T>();
            m_Renderer.rendererFeatures.Add(feature);
            return feature;
        }

        void CreateScene(bool withSslAndGtao)
        {
            m_Rig = new GameObject("T2 Camera", typeof(Camera)) { hideFlags = HideFlags.DontSave };
            m_Camera = m_Rig.GetComponent<Camera>();
            m_Camera.enabled = false;
            m_Camera.cameraType = CameraType.Game;
            m_Camera.cullingMask = 1 << Layer;
            m_Camera.clearFlags = CameraClearFlags.SolidColor;
            m_Camera.nearClipPlane = Near;
            m_Camera.farClipPlane = Far;
            m_Camera.fieldOfView = 60f;
            var cameraData = m_Camera.GetUniversalAdditionalCameraData();
            cameraData.volumeLayerMask = 1 << Layer;
            cameraData.allowXRRendering = false;
            m_Target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            Assert.IsTrue(m_Target.Create());
            m_Camera.targetTexture = m_Target;

            m_Profile = ScriptableObject.CreateInstance<VolumeProfile>();
            m_Profile.Add<ContactShadowsVolume>().enabled.Override(true);
            if (withSslAndGtao) m_Profile.Add<ScreenSpaceGlobalIllumination>().enabled.Override(true);
            var environment = m_Environment = new GameObject("T2 Volume and Light") { hideFlags = HideFlags.DontSave };
            environment.layer = Layer; // Set before Volume.OnEnable registers its layer.
            var volume = environment.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = m_Profile;
            var sun = environment.AddComponent<Light>();
            sun.type = LightType.Directional;
            RenderSettings.sun = sun;
            environment.AddComponent<ContactShadowLight>();

            var cube = m_Cube = new GameObject("T2 Depth Target", typeof(MeshFilter), typeof(MeshRenderer)) { hideFlags = HideFlags.DontSave };
            cube.layer = Layer;
            cube.transform.position = Vector3.forward * (FrontDepth + 0.5f);
            cube.transform.localScale = new Vector3(2.15f, 1.6f, 1f);
            cube.GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            cube.GetComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                UniversalRenderPipelineAsset.packagePath + "/Runtime/Materials/Lit.mat");
        }

        static void CheckAtlas(ScreenSpaceDepthPyramid pyramid, string configuration)
        {
            var atlas = pyramid.texture.rt;
            Assert.AreEqual(GraphicsFormat.R32_SFloat, atlas.graphicsFormat);
            var info = pyramid.info;
            Assert.GreaterOrEqual(info.mipLevelCount, 2);
            Assert.AreEqual(new Vector2Int(atlas.width, atlas.height), info.textureSize);
            Assert.AreEqual(Vector2Int.zero, info.mipLevelOffsets[0]);
            Assert.AreEqual(new Vector2Int(Width, Height), info.mipLevelSizes[0]);
            Assert.AreEqual(new Vector2Int(0, Height), info.mipLevelOffsets[1]);
            Assert.AreEqual(new Vector2Int(Width / 2, Height / 2), info.mipLevelSizes[1]);

            var request = AsyncGPUReadback.Request(atlas, 0);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, "Could not read back the shared R32_SFloat atlas");
            var data = request.GetData<float>();
            Assert.AreEqual(atlas.width * atlas.height, data.Length);
            float Depth(int mip, int x, int y) => data[(info.mipLevelOffsets[mip].y + y) * atlas.width + info.mipLevelOffsets[mip].x + x];

            bool reversed = SystemInfo.usesReversedZBuffer;
            // Perspective device depth, not linear eye depth. The front face is parallel to the image plane.
            float nearWeight = Near * (Far - FrontDepth) / (FrontDepth * (Far - Near));
            float front = reversed ? nearWeight : 1f - nearWeight;
            float clear = reversed ? 0f : 1f;
            for (int mip = 0; mip <= 1; ++mip)
            {
                var size = info.mipLevelSizes[mip];
                Assert.AreEqual(front, Depth(mip, size.x / 2, size.y / 2), 1e-4f,
                    $"{configuration} mip {mip}: center must contain the front face at eye depth {FrontDepth}");
                Assert.AreEqual(clear, Depth(mip, 1, 1), 1e-6f, $"Mip {mip}: corner must be clear depth");
                for (int y = 0; y < size.y; ++y)
                for (int x = 0; x < size.x; ++x)
                {
                    float d = Depth(mip, x, y);
                    if (!(Mathf.Abs(d - front) <= 1e-4f || Mathf.Abs(d - clear) <= 1e-6f))
                        Assert.Fail($"Mip {mip} ({x},{y}): {d} is neither cube nor clear depth");
                }
            }
            float Closest(float a, float b) => reversed ? Mathf.Max(a, b) : Mathf.Min(a, b);
            for (int y = 0; y < Height / 2; ++y)
            for (int x = 0; x < Width / 2; ++x)
            {
                int sx = 2 * x, sy = 2 * y;
                float a = Depth(0, sx, sy), b = Depth(0, sx + 1, sy);
                float c = Depth(0, sx, sy + 1), d = Depth(0, sx + 1, sy + 1);
                Assert.AreEqual(Closest(Closest(a, b), Closest(c, d)), Depth(1, x, y), 1e-6f, $"Mip 1 reduction at ({x},{y})");
            }

            TestContext.WriteLine($"{configuration}, {SystemInfo.graphicsDeviceType}: pass 203 execute/copy = 1/1; " +
                $"mip 0 center/corner = {Depth(0, Width / 2, Height / 2):G8}/{Depth(0, 1, 1):G8}; " +
                $"mip 1 center/corner = {Depth(1, Width / 4, Height / 4):G8}/{Depth(1, 1, 1):G8}");
            var args = Environment.GetCommandLineArgs();
            int flag = Array.IndexOf(args, EvidenceFlag);
            if (flag < 0) return;
            Assert.IsTrue(flag + 1 < args.Length && !string.IsNullOrWhiteSpace(args[flag + 1]) && !args[flag + 1].StartsWith("-"),
                EvidenceFlag + " requires an output directory");
            string directory = Path.GetFullPath(args[flag + 1]);
            Directory.CreateDirectory(directory);
            var image = new Texture2D(atlas.width, atlas.height, TextureFormat.RGB24, false, true);
            try
            {
                // Leave unused atlas padding black. Scale *measured* device depth for legibility.
                var pixels = new Color32[data.Length];
                for (int mip = 0; mip < info.mipLevelCount; ++mip)
                for (int y = 0; y < info.mipLevelSizes[mip].y; ++y)
                for (int x = 0; x < info.mipLevelSizes[mip].x; ++x)
                {
                    float raw = Depth(mip, x, y);
                    byte gray = (byte)Mathf.RoundToInt(255 * Mathf.Clamp01((reversed ? raw : 1f - raw) / nearWeight));
                    var origin = info.mipLevelOffsets[mip];
                    pixels[(origin.y + y) * atlas.width + origin.x + x] = new Color32(gray, gray, gray, 255);
                }
                image.SetPixels32(pixels);
                image.Apply();
                File.WriteAllBytes(Path.Combine(directory, configuration + "DepthAtlas.png"), image.EncodeToPNG());
            }
            finally { Object.DestroyImmediate(image); }
        }
    }
}
