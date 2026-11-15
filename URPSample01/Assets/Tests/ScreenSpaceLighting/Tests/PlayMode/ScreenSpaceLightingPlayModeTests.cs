using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Unity.ScreenSpaceLighting.Tests.PlayMode
{
    // T14 release regression: the same effects the EditMode suites inspect in isolation have to run
    // through the real player loop for every renderer. These tests drive a runtime camera against the
    // T12 pipeline assets, so they exercise volume updates, renderer construction, history allocation
    // and camera lifetime instead of the editor-only scratches used during module implementation.
    public class ScreenSpaceLightingPlayModeTests
    {
        const string RendererRoot = "Assets/Tests/ScreenSpaceLighting/Renderers";
        const string MaterialRoot = "Assets/Tests/ScreenSpaceLighting/Materials";
        const int ImageWidth = 640;
        const int ImageHeight = 360;
        const int WarmupFrames = 10;
        const float k_IdenticalThreshold = 1e-6f;
        static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static readonly string[] Modes = { "Forward", "ForwardPlus", "Deferred" };

        UniversalRenderPipelineAsset m_Pipeline;
        RenderPipelineAsset m_PreviousPipeline;
        RenderPipelineAsset m_PreviousQualityPipeline;
        UniversalRendererData m_RendererData;
        ScreenSpaceLightingRendererFeature m_Ssl;
        GroundTruthAmbientOcclusionRendererFeature m_Gtao;
        VolumeProfile m_Profile;
        ScreenSpaceGlobalIllumination m_Ssgi;
        ScreenSpaceReflection m_Ssr;
        ScreenSpaceRefraction m_Refraction;
        GameObject m_Root;
        GameObject m_CameraObject;
        Camera m_Camera;
        UniversalAdditionalCameraData m_CameraData;
        RenderTexture m_Target;
        readonly List<Material> m_TrackedMaterials = new List<Material>();

        struct Counters
        {
            public int depthPyramid;
            public int colorPyramid;
            public int ssgiTrace;
            public int ssrTrace;
            public int transparentPrepass;
            public int transparentTrace;
            public int gtaoMain;
            public int setupBound;
        }

        [UnityTest]
        public IEnumerator AllEffectsDispatchAndRestoreReference([ValueSource(nameof(Modes))] string mode)
        {
            yield return SetUpRig(mode);
            try
            {
                SetEffects(false, false, false, false);
                m_CameraData.resetHistory = true;
                yield return RenderFrames(WarmupFrames);
                var reference = ReadPixels();

                var before = ReadCounters();
                SetEffects(true, true, true, true);
                m_CameraData.resetHistory = true;
                yield return RenderFrames(WarmupFrames);
                var allOn = ReadPixels();
                var after = ReadCounters();

                int changed = CountDifferences(reference, allOn);
                Assert.Greater(changed, 0, mode + ": enabling every screen-space effect did not change the image.");
                Assert.AreEqual(0, Count(allOn, IsNonFinite), mode + ": the all-on image contains non-finite pixels.");

                Assert.GreaterOrEqual(after.depthPyramid - before.depthPyramid, WarmupFrames, mode + ": the SSL depth pyramid did not execute every frame.");
                Assert.GreaterOrEqual(after.colorPyramid - before.colorPyramid, WarmupFrames, mode + ": the SSL color pyramid did not execute every frame.");
                Assert.GreaterOrEqual(after.ssgiTrace - before.ssgiTrace, WarmupFrames, mode + ": SSGI trace did not dispatch every frame.");
                Assert.GreaterOrEqual(after.ssrTrace - before.ssrTrace, WarmupFrames, mode + ": SSR trace did not dispatch every frame.");
                Assert.GreaterOrEqual(after.transparentPrepass - before.transparentPrepass, WarmupFrames, mode + ": the transparent depth/normal prepass did not run every frame.");
                Assert.GreaterOrEqual(after.transparentTrace - before.transparentTrace, WarmupFrames, mode + ": transparent SSR did not dispatch every frame.");
                Assert.GreaterOrEqual(after.gtaoMain - before.gtaoMain, WarmupFrames, mode + ": GTAO did not dispatch every frame.");
                Assert.GreaterOrEqual(after.setupBound - before.setupBound, WarmupFrames, mode + ": the SSL setup pass did not bind its globals every frame.");

                Assert.AreEqual(1, HistoryEntryCount(), mode + ": the feature did not keep exactly one history entry for the camera.");
                Assert.IsTrue(TryGetHistory(m_Camera, out var history), mode + ": the feature has no history for the camera.");
                Assert.Greater(ReadInt(history, "frameSerial"), 0, mode + ": the SSL history never advanced a frame.");
                Assert.Greater(ReadInt(history, "validFrameCount"), 1, mode + ": the SSL history has no accumulated frames.");
                Assert.IsTrue((bool)ReadMember(history, "hasValidHistory"), mode + ": the SSL history is not valid after a warmup.");
                Assert.Greater(ReadInt(history, "resetCount"), 0, mode + ": the SSL history was never allocated/reset.");

                // Cleanup disables the shared keywords at the end of every camera.
                foreach (var keyword in new[] { "_SCREEN_SPACE_GI", "_SCREEN_SPACE_SSR", "_SCREEN_SPACE_TRANSPARENT_SSR", "_SCREEN_SPACE_REFRACTION" })
                    Assert.IsFalse(Shader.IsKeywordEnabled(keyword), mode + ": global keyword " + keyword + " leaked past the camera cleanup.");

                SetEffects(false, false, false, false);
                m_CameraData.resetHistory = true;
                var beforeOff = ReadCounters();
                yield return RenderFrames(WarmupFrames);
                var restored = ReadPixels();
                var afterOff = ReadCounters();

                Assert.AreEqual(0, CountDifferences(reference, restored), mode + ": disabling the effects did not restore the reference image.");
                Assert.AreEqual(beforeOff.ssgiTrace, afterOff.ssgiTrace, mode + ": SSGI kept dispatching while disabled.");
                Assert.AreEqual(beforeOff.ssrTrace, afterOff.ssrTrace, mode + ": SSR kept dispatching while disabled.");
                Assert.AreEqual(beforeOff.depthPyramid, afterOff.depthPyramid, mode + ": the SSL depth pyramid kept executing while every effect was disabled.");
            }
            finally
            {
                TearDownRig();
            }
        }

        [UnityTest]
        public IEnumerator RuntimeControlsAndCameraLifetime([ValueSource(nameof(Modes))] string mode)
        {
            yield return SetUpRig(mode);
            GameObject secondCameraObject = null;
            try
            {
                SetEffects(false, false, false, false);
                m_CameraData.resetHistory = true;
                yield return RenderFrames(WarmupFrames);
                var reference = ReadPixels();

                // Feature disabled at runtime: no passes are enqueued and the image is the reference.
                SetEffects(true, true, true, true);
                m_CameraData.resetHistory = true;
                yield return RenderFrames(WarmupFrames);
                m_Ssl.SetActive(false);
                SetEffects(false, false, false, false);
                m_CameraData.resetHistory = true;
                var beforeDisabled = ReadCounters();
                yield return RenderFrames(WarmupFrames);
                var featureOff = ReadPixels();
                Assert.AreEqual(0, CountDifferences(reference, featureOff), mode + ": disabling the SSL feature at runtime did not restore the reference image.");
                Assert.AreEqual(beforeDisabled.ssgiTrace, ReadCounters().ssgiTrace, mode + ": a disabled SSL feature still dispatched SSGI.");
                m_Ssl.SetActive(true);

                // A second camera gets its own history entry; destroying it releases that entry.
                SetEffects(true, true, true, true);
                m_CameraData.resetHistory = true;
                yield return RenderFrames(4);
                secondCameraObject = new GameObject("T14 PlayMode Second Camera");
                var secondCamera = secondCameraObject.AddComponent<Camera>();
                secondCamera.CopyFrom(m_Camera);
                secondCamera.targetTexture = m_Target;
                secondCamera.depth = m_Camera.depth - 1f;
                secondCamera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
                secondCamera.Render();
                yield return null;
                Assert.AreEqual(2, HistoryEntryCount(), mode + ": a second camera did not get its own SSL history entry.");

                Object.Destroy(secondCameraObject);
                secondCameraObject = null;
                m_Camera.Render();
                yield return null;
                Assert.AreEqual(1, HistoryEntryCount(), mode + ": destroying the second camera did not prune its SSL history entry.");

                // Volume components removed: the effect cannot be requested at all. This runs last
                // because removing the components unbinds the parameters used by SetEffects.
                SetEffects(true, true, true, true);
                m_CameraData.resetHistory = true;
                yield return RenderFrames(WarmupFrames);
                m_Profile.Remove<ScreenSpaceGlobalIllumination>();
                m_Profile.Remove<ScreenSpaceReflection>();
                m_Profile.Remove<ScreenSpaceRefraction>();
                m_Gtao.SetActive(false);
                m_CameraData.resetHistory = true;
                var beforeNoComponents = ReadCounters();
                yield return RenderFrames(WarmupFrames);
                var noComponents = ReadPixels();
                Assert.AreEqual(0, CountDifferences(reference, noComponents), mode + ": a profile without SSL components changed the reference image.");
                Assert.AreEqual(beforeNoComponents.ssgiTrace, ReadCounters().ssgiTrace, mode + ": SSGI still dispatched without a volume component.");
            }
            finally
            {
                if (secondCameraObject != null)
                    Object.Destroy(secondCameraObject);
                TearDownRig();
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Harness
        // ---------------------------------------------------------------------------------------------

        IEnumerator SetUpRig(string mode)
        {
            m_PreviousPipeline = GraphicsSettings.defaultRenderPipeline;
            m_PreviousQualityPipeline = QualitySettings.renderPipeline;

#if UNITY_EDITOR
            m_Pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(
                RendererRoot + "/SslT12" + mode + "Pipeline.asset");
            m_RendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(
                RendererRoot + "/SslT12" + mode + "Renderer.asset");
#else
            Assert.Ignore("The PlayMode regression loads the T12 pipeline assets through the AssetDatabase.");
#endif
            Assert.IsNotNull(m_Pipeline, mode + ": missing pipeline asset.");
            Assert.IsNotNull(m_RendererData, mode + ": missing renderer asset.");
            m_Ssl = m_RendererData.rendererFeatures.OfType<ScreenSpaceLightingRendererFeature>().FirstOrDefault();
            m_Gtao = m_RendererData.rendererFeatures.OfType<GroundTruthAmbientOcclusionRendererFeature>().FirstOrDefault();
            Assert.IsNotNull(m_Ssl, mode + ": the renderer has no Screen Space Lighting feature.");
            Assert.IsNotNull(m_Gtao, mode + ": the renderer has no GTAO feature.");

            GraphicsSettings.defaultRenderPipeline = m_Pipeline;
            QualitySettings.renderPipeline = m_Pipeline;
            yield return null;

            m_Pipeline.renderScale = 1f;
            m_Pipeline.msaaSampleCount = 1;
            m_Pipeline.supportsHDR = true;
            m_Pipeline.supportsCameraOpaqueTexture = false;

            m_Root = new GameObject("T14 PlayMode Rig");
            m_Profile = ScriptableObject.CreateInstance<VolumeProfile>();
            m_Profile.name = "T14 PlayMode Profile";
            m_Ssgi = m_Profile.Add<ScreenSpaceGlobalIllumination>(true);
            m_Ssr = m_Profile.Add<ScreenSpaceReflection>(true);
            m_Refraction = m_Profile.Add<ScreenSpaceRefraction>(true);

            var volumeObject = new GameObject("T14 PlayMode Volume");
            volumeObject.transform.SetParent(m_Root.transform, false);
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 0f;
            volume.sharedProfile = m_Profile;

            var cameraPosition = new Vector3(0f, 1.6f, -9f);
            var center = new Vector3(0f, 1.4f, 0f);
            var litShader = Shader.Find("Universal Render Pipeline/Lit");
            var floorMaterial = Track(CreateLitMaterial(litShader, "T14 Floor", new Color(0.5f, 0.52f, 0.55f), 0f, 0.6f));
            AddBox(m_Root, floorMaterial, "T14 Floor", new Vector3(0f, 0f, 0f), new Vector3(30f, 0.1f, 20f));
            var backdrop = Track(CreateLitMaterial(litShader, "T14 Backdrop", new Color(0.02f, 0.02f, 0.02f), 0f, 0.2f));
            backdrop.EnableKeyword("_EMISSION");
            backdrop.SetColor("_EmissionColor", new Color(2f, 1.1f, 0.3f));
            AddBox(m_Root, backdrop, "T14 Backdrop", new Vector3(0f, 3f, 9f), new Vector3(26f, 10f, 0.2f));
            var metal = Track(CreateLitMaterial(litShader, "T14 Metal", new Color(0.9f, 0.85f, 0.7f), 1f, 0.95f));
            AddSphere(m_Root, metal, "T14 Metal Sphere", center + new Vector3(2.4f, 0f, 0f), 1.4f);
            AddSphere(m_Root, Track(LoadMaterial(MaterialRoot + "/T11RefractionBox.mat")), "T14 Glass", center + new Vector3(-2.4f, 0f, 0f), 1.2f);
            AddSphere(m_Root, Track(LoadMaterial(MaterialRoot + "/T10TransparentReceiver.mat")), "T14 Receiver", center + new Vector3(0f, 0f, 1.6f), 1.1f);

            m_CameraObject = new GameObject("T14 PlayMode Camera");
            m_Camera = m_CameraObject.AddComponent<Camera>();
            m_Camera.clearFlags = CameraClearFlags.Skybox;
            m_Camera.transform.position = cameraPosition;
            m_Camera.transform.LookAt(center);
            m_Camera.fieldOfView = 55f;
            m_Camera.nearClipPlane = 0.1f;
            m_Camera.farClipPlane = 60f;
            m_CameraData = m_Camera.GetUniversalAdditionalCameraData();
            m_CameraData.renderPostProcessing = false;
            m_CameraData.antialiasing = AntialiasingMode.None;
            m_CameraData.volumeLayerMask = ~0;
            m_CameraData.resetHistory = true;

            m_Target = new RenderTexture(ImageWidth, ImageHeight, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "T14 PlayMode Target",
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

            if (m_Profile != null)
                Object.Destroy(m_Profile);
            foreach (var material in m_TrackedMaterials)
            {
                if (material != null)
                    Object.Destroy(material);
            }

            m_TrackedMaterials.Clear();

            GraphicsSettings.defaultRenderPipeline = m_PreviousPipeline;
            QualitySettings.renderPipeline = m_PreviousQualityPipeline;

            m_CameraObject = null;
            m_Camera = null;
            m_Root = null;
            m_Target = null;
            m_Profile = null;
            m_Ssl = null;
            m_Gtao = null;
        }

        Material Track(Material material)
        {
            m_TrackedMaterials.Add(material);
            return material;
        }

        void SetEffects(bool ssgi, bool ssr, bool refraction, bool gtao)
        {
            m_Ssgi.enabled.Override(ssgi);
            m_Ssgi.intensity.Override(1f);
            m_Ssr.enabled.Override(ssr);
            m_Ssr.enabledTransparent.Override(ssr);
            m_Ssr.intensity.Override(1f);
            m_Ssr.minSmoothness.Override(0.4f);
            m_Ssr.smoothnessFadeStart.Override(0.4f);
            m_Ssr.usedAlgorithm.Override(ScreenSpaceReflectionAlgorithm.Approximation);
            m_Refraction.enabled.Override(refraction);
            m_Refraction.intensity.Override(1f);
            var gtaoSettings = ReadMember(m_Gtao, "settings");
            var intensityField = gtaoSettings?.GetType().GetField("intensity", InstanceFlags);
            intensityField?.SetValue(gtaoSettings, gtao ? 1f : 0f);
        }

        IEnumerator RenderFrames(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                m_Camera.Render();
                yield return null;
            }
        }

        static Counters ReadCounters()
        {
            return new Counters
            {
                depthPyramid = ReadStaticInt("ScreenSpaceLightingDepthPyramidPass", "s_EditorExecuteCount"),
                colorPyramid = ReadStaticInt("ScreenSpaceLightingCurrentColorPyramidPass", "s_EditorExecuteCount"),
                ssgiTrace = ReadStaticInt("ScreenSpaceLightingSsgiPass", "s_EditorTraceDispatchCount"),
                ssrTrace = ReadStaticInt("ScreenSpaceLightingSsrPass", "s_EditorTraceDispatchCount"),
                transparentPrepass = ReadStaticInt("ScreenSpaceLightingTransparentDepthNormalsPass", "s_EditorExecuteCount"),
                transparentTrace = ReadStaticInt("ScreenSpaceLightingTransparentSsrPass", "s_EditorTraceDispatchCount"),
                gtaoMain = ReadStaticInt("GroundTruthAmbientOcclusionPass", "s_EditorMainDispatchCount"),
                setupBound = ReadStaticInt("ScreenSpaceLightingSetupPass", "s_EditorSsgiBoundCount") +
                             ReadStaticInt("ScreenSpaceLightingSetupPass", "s_EditorSsrBoundCount"),
            };
        }

        // The runtime assembly does not expose its internals to test assemblies: PluginIsolationTests
        // forbids Core sources from naming plugin types, so the PlayMode suite inspects the plugin
        // through reflection exactly like the editor verification harnesses do.
        static object ReadMember(object target, string name)
        {
            var property = target.GetType().GetProperty(name, InstanceFlags);
            if (property != null)
                return property.GetValue(target);
            var field = target.GetType().GetField(name, InstanceFlags);
            return field?.GetValue(target);
        }

        static int ReadInt(object target, string name)
        {
            var value = ReadMember(target, name);
            return value is int integer ? integer : -1;
        }

        static int ReadStaticInt(string typeName, string fieldName)
        {
            var type = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal." + typeName);
            var field = type?.GetField(fieldName, StaticFlags);
            var value = field?.GetValue(null);
            return value is int integer ? integer : -1;
        }

        object HistoryStore()
        {
            return ReadMember(m_Ssl, "historyStore");
        }

        int HistoryEntryCount()
        {
            var store = HistoryStore();
            return store != null ? (int)ReadMember(store, "entryCount") : -1;
        }

        bool TryGetHistory(Camera camera, out object history)
        {
            history = null;
            var store = HistoryStore();
            var method = store?.GetType().GetMethod("TryGet", InstanceFlags);
            if (method == null)
                return false;
            var arguments = new object[] { camera, 0, null };
            bool found = (bool)method.Invoke(store, arguments);
            history = arguments[2];
            return found;
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

        static int CountDifferences(Color[] a, Color[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return -1;
            int count = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (MaxChannelDelta(a[i], b[i]) > k_IdenticalThreshold)
                    count++;
            }

            return count;
        }

        static int Count(Color[] samples, System.Func<Color, bool> predicate)
        {
            int count = 0;
            foreach (var sample in samples)
            {
                if (predicate(sample))
                    count++;
            }

            return count;
        }

        static bool IsNonFinite(Color color)
        {
            return !IsFinite(color.r) || !IsFinite(color.g) || !IsFinite(color.b);
        }

        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        static float MaxChannelDelta(Color a, Color b)
        {
            return Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
        }

        static Material CreateLitMaterial(Shader shader, string name, Color color, float metallic, float smoothness)
        {
            var material = new Material(shader) { name = name };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            return material;
        }

        static Material LoadMaterial(string path)
        {
#if UNITY_EDITOR
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
                throw new System.IO.FileNotFoundException("Missing T14 material asset.", path);
            return new Material(material) { name = material.name + " (T14 Runtime)" };
#else
            throw new System.InvalidOperationException("The PlayMode regression loads its materials through the AssetDatabase.");
#endif
        }

        static GameObject AddBox(GameObject parent, Material material, string name, Vector3 position, Vector3 size)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent.transform, false);
            box.transform.position = position;
            box.transform.localScale = size;
            box.GetComponent<Renderer>().sharedMaterial = material;
            return box;
        }

        static GameObject AddSphere(GameObject parent, Material material, string name, Vector3 position, float scale)
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = name;
            sphere.transform.SetParent(parent.transform, false);
            sphere.transform.position = position;
            sphere.transform.localScale = Vector3.one * scale;
            sphere.GetComponent<Renderer>().sharedMaterial = material;
            return sphere;
        }
    }
}