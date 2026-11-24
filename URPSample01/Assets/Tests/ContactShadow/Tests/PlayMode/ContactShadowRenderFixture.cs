#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ContactShadowTests.PlayMode
{
    // Editor-hosted PlayMode: load by asset path, without installing scenes in Build Settings.
    public abstract class ContactShadowRenderFixture
    {
        protected const int W = 256, H = 192;
        const string Root = "Assets/Tests/ContactShadow/";
        const string ScenePath = Root + "Scenes/ContactShadowBaseline.unity";
        protected static readonly RectInt Contact = new RectInt(115, 123, 28, 14);
        protected static readonly RectInt Facing = new RectInt(157, 123, 28, 14);
        protected static readonly RectInt Full = new RectInt(0, 0, W, H);
        protected static readonly string[] Panels = { "ReceiverOff", "Transparent", "BakedLit", "Custom" };
        // Inset at the left of each panel's little blocker, not its silhouette or the floor.
        protected static RectInt Panel(int i) => new RectInt(17 + 60 * i, 54, 24, 10);

        protected Camera View;
        protected Light[] Lights;
        protected ContactShadowLight[] Markers;
        protected ContactShadowsVolume Settings;
        protected ScreenSpaceContactShadowsRendererFeature Feature;
        protected RenderingMode Mode;
        protected RenderTexture Target;
        RenderPipelineAsset previousQuality;
        UniversalRenderPipelineAsset pipelineCopy;
        Scene scene, previousScene;
        bool installed, featureWasActive, asyncCompilation;
        Texture2D readback;
        VolumeProfile profileInstance;
        static readonly PropertyInfo Actual = typeof(UniversalRenderer).GetProperty("renderingModeActual",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        protected IEnumerator Open(RenderingMode mode, bool additionalShadows = true)
        {
            Mode = mode;
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>($"{Root}Pipelines/{mode}.asset");
            Assert.That(pipeline, Is.Not.Null, "Missing T7 pipeline: " + mode);
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath), Is.Not.Null, "Generate/import the T7 baseline scene first.");
            Assert.That(SceneManager.GetSceneByPath(ScenePath).isLoaded, Is.False, "Start from the Test Runner's empty scene.");
            Assert.That(pipeline.supportsAdditionalLightShadows, Is.True);
            if (!additionalShadows)
            {
                pipelineCopy = Object.Instantiate(pipeline);
                using var serialized = new SerializedObject(pipelineCopy);
                serialized.FindProperty("m_AdditionalLightShadowsSupported").boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                pipeline = pipelineCopy;
                Assert.That(pipeline.supportsAdditionalLightShadows, Is.False);
            }
            previousQuality = QualitySettings.renderPipeline;
            previousScene = SceneManager.GetActiveScene();
            asyncCompilation = ShaderUtil.allowAsyncCompilation;
            installed = true;
            ShaderUtil.allowAsyncCompilation = false;
            QualitySettings.renderPipeline = pipeline;
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            Assert.That(SceneManager.SetActiveScene(scene), Is.True);
            Assert.That(GraphicsSettings.currentRenderPipeline, Is.EqualTo(pipeline));
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>($"{Root}Pipelines/{mode}Renderer.asset");
            Feature = data.rendererFeatures.OfType<ScreenSpaceContactShadowsRendererFeature>().Single();
            featureWasActive = Feature.isActive;
            Assert.That(featureWasActive, Is.True, "Fixture feature must start active.");
            View = Named<Camera>("ContactCamera");
            View.enabled = false;
            View.aspect = (float)W / H;
            View.allowDynamicResolution = View.allowMSAA = false;
            View.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.EveryFrame);
            View.GetUniversalAdditionalCameraData().antialiasing = AntialiasingMode.None;
            Lights = new[] { Named<Light>("Main"), Named<Light>("Point"), Named<Light>("Spot") };
            Markers = Lights.Select(l => l.GetComponent<ContactShadowLight>()).ToArray();
            Assert.That(Markers, Has.None.Null);
            var volume = Named<Volume>("ContactVolume");
            // Accessing .profile instantiates each component; never change the serialized profile.
            profileInstance = volume.profile;
            Assert.That(profileInstance.TryGet(out Settings), Is.True);
            Assert.That(Settings.active && Settings.enabled.overrideState && Settings.enabled.value, Is.True);
            Target = new RenderTexture(new RenderTextureDescriptor(W, H, GraphicsFormat.R8G8B8A8_UNorm, 24) { msaaSamples = 1 });
            Assert.That(Target.Create(), Is.True, "Linear, single-sample camera attachment required.");
            View.targetTexture = Target;
            readback = new Texture2D(W, H, TextureFormat.RGBA32, false, true);
            yield return null;
            Capture(); // Initialize pipelines/resources before either side of a comparison.
        }

        protected T Named<T>(string name) where T : Component => scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<T>(true)).Single(x => x.name == name);

        protected Color32[] Capture(Camera camera = null)
        {
            camera = camera ? camera : View;
            var previous = RenderTexture.active;
            try
            {
                camera.Render();
                // This fork silently falls back to Forward for unsupported targets. Do not label that Deferred.
                Assert.That(Actual, Is.Not.Null, "Update actual camera-path probe for this URP version.");
                Assert.That(Actual.GetValue(camera.GetUniversalAdditionalCameraData().scriptableRenderer), Is.EqualTo(Mode));
                RenderTexture.active = camera.targetTexture;
                readback.ReadPixels(new Rect(0, 0, W, H), 0, 0, false);
                return readback.GetPixels32();
            }
            finally { RenderTexture.active = previous; }
        }

        protected void MarkOnly(int index)
        {
            for (int i = 0; i < Markers.Length; i++) Markers[i].castContactShadows = i == index;
        }

        // Isolate the physical light as well as its marker: the wrong light's bit cannot satisfy a positive ROI.
        protected void Isolate(int index, LightShadows shadows)
        {
            MarkOnly(index);
            for (int i = 0; i < Lights.Length; i++) Lights[i].enabled = i == index;
            Lights[index].shadows = shadows;
            // Keep the actual shadow caster and shadow variant but leave direct energy for contact to attenuate.
            if (shadows != LightShadows.None) Lights[index].shadowStrength = .7f;
        }

        internal ContactShadowImagePair Pair(string label, Color32[] off, Color32[] on) =>
            new ContactShadowImagePair(Mode + "/" + label, off, on, W, H);

        internal IEnumerator CompareVolume(string label, Action<ContactShadowImagePair> check)
        {
            Settings.enabled.Override(false);
            yield return null;
            var off = Capture();
            Settings.enabled.Override(true);
            yield return null;
            check(Pair(label, off, Capture()));
        }

        [UnityTearDown]
        public IEnumerator Close()
        {
            if (Feature) Feature.SetActive(featureWasActive);
            if (View) View.targetTexture = null;
            if (installed)
            {
                ShaderUtil.allowAsyncCompilation = asyncCompilation;
                QualitySettings.renderPipeline = previousQuality;
                if (previousScene.isLoaded) SceneManager.SetActiveScene(previousScene);
            }
            if (scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
            if (profileInstance)
            {
                foreach (var component in profileInstance.components) Object.Destroy(component);
                Object.Destroy(profileInstance);
            }
            if (readback) Object.Destroy(readback);
            if (Target) { Target.Release(); Object.Destroy(Target); }
            if (pipelineCopy) Object.Destroy(pipelineCopy);
            installed = false;
            yield return null;
        }
    }
}
#endif
