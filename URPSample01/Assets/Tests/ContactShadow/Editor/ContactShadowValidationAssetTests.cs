using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public class ContactShadowValidationAssetTests
{
    const string Root = "Assets/Tests/ContactShadow/";
    const string Pipelines = Root + "Pipelines/";
    const string Utils = "Packages/com.unity.render-pipelines.universal/Shaders/Utils/";

    [TestCase("Forward", RenderingMode.Forward, 1)]
    [TestCase("ForwardPlus", RenderingMode.ForwardPlus, 1)]
    [TestCase("Deferred", RenderingMode.Deferred, 1)]
    [TestCase("ForwardMSAA4x", RenderingMode.Forward, 4)]
    public void PipelineLoadsExpectedRendererAndContactResources(string name, RenderingMode mode, int samples)
    {
        var pipeline = Load<UniversalRenderPipelineAsset>(Pipelines + name + ".asset");
        var renderer = Load<UniversalRendererData>(Pipelines + mode + "Renderer.asset");
        using var p = new SerializedObject(pipeline);
        var renderers = p.FindProperty("m_RendererDataList");
        Assert.That(renderers.arraySize, Is.EqualTo(1));
        Assert.That(p.FindProperty("m_DefaultRendererIndex").intValue, Is.Zero);
        Assert.That(renderers.GetArrayElementAtIndex(0).objectReferenceValue, Is.EqualTo(renderer));
        Assert.That(renderer.renderingMode, Is.EqualTo(mode));
        Assert.That(renderer.copyDepthMode, Is.EqualTo(CopyDepthMode.ForcePrepass));
        Assert.That(pipeline.supportsCameraDepthTexture, Is.True);
        Assert.That(pipeline.msaaSampleCount, Is.EqualTo(samples));
        Assert.That(pipeline.renderScale, Is.EqualTo(1f));
        Assert.That(pipeline.mainLightRenderingMode, Is.EqualTo(LightRenderingMode.PerPixel));
        Assert.That(pipeline.additionalLightsRenderingMode, Is.EqualTo(LightRenderingMode.PerPixel));
        Assert.That(pipeline.supportsAdditionalLightShadows, Is.True);

        Assert.That(renderer.rendererFeatures, Has.Count.EqualTo(1));
        var feature = renderer.rendererFeatures[0];
        Assert.That(feature, Is.TypeOf<ScreenSpaceContactShadowsRendererFeature>());
        Assert.That(feature.isActive, Is.True);
        Assert.That(AssetDatabase.IsSubAsset(feature), Is.True);
        Assert.That(AssetDatabase.GetAssetPath(feature), Is.EqualTo(AssetDatabase.GetAssetPath(renderer)));
        Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long id), Is.True);
        using var r = new SerializedObject(renderer);
        var map = r.FindProperty("m_RendererFeatureMap");
        Assert.That(map.arraySize, Is.EqualTo(1));
        Assert.That(map.GetArrayElementAtIndex(0).longValue, Is.EqualTo(id));
        using var f = new SerializedObject(feature);
        Assert.That(f.FindProperty("m_Resources.depthPyramidCompute").objectReferenceValue,
            Is.EqualTo(Load<ComputeShader>(Utils + "ScreenSpaceCommon/DepthPyramid.compute")));
        Assert.That(f.FindProperty("m_Resources.contactShadowsCompute").objectReferenceValue,
            Is.EqualTo(Load<ComputeShader>(Utils + "ContactShadows/ContactShadows.compute")));
    }

    [Test]
    public void ClusteredModesReferenceTheirLightingKernels()
    {
        foreach (string name in new[] { "ForwardPlus", "Deferred" })
        {
            using var renderer = new SerializedObject(Load<UniversalRendererData>(Pipelines + name + "Renderer.asset"));
            Assert.That(renderer.FindProperty("shaders.clusterBuilderCS").objectReferenceValue,
                Is.EqualTo(Load<ComputeShader>(Utils + "ClusterBuilder.compute")));
            if (name == "Deferred")
                Assert.That(renderer.FindProperty("shaders.clusteredDeferredCS").objectReferenceValue,
                    Is.EqualTo(Load<ComputeShader>(Utils + "ClusteredDeferred.compute")));
        }
    }

    [TestCase("ContactShadowBaseline")]
    [TestCase("ContactShadowStress")]
    public void SceneLoadsWithContactCameraVolumeAndLights(string name)
    {
        string path = Root + "Scenes/" + name + ".unity";
        Load<SceneAsset>(path);
        var scene = SceneManager.GetSceneByPath(path);
        bool alreadyLoaded = scene.isLoaded;
        if (!alreadyLoaded)
            scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
        try
        {
            Assert.That(scene.isLoaded, Is.True);
            var roots = scene.GetRootGameObjects();
            var cameras = roots.SelectMany(root => root.GetComponentsInChildren<Camera>(true));
            Assert.That(cameras.Any(camera => camera.name == "ContactCamera"), Is.True, path);
            var volumes = roots.SelectMany(root => root.GetComponentsInChildren<Volume>(true));
            var volume = volumes.Single(v => v.name == "ContactVolume");
            Assert.That(volume.sharedProfile, Is.Not.Null);
            Assert.That(volume.sharedProfile.TryGet(out ContactShadowsVolume contact), Is.True);
            Assert.That(contact.active && contact.enabled.overrideState && contact.enabled.value, Is.True);

            var lights = roots.SelectMany(root => root.GetComponentsInChildren<ContactShadowLight>(true))
                .Where(marker => marker.castContactShadows && marker.enabled)
                .Select(marker => marker.GetComponent<Light>()).ToArray();
            Assert.That(lights, Has.None.Null);
            if (name == "ContactShadowBaseline")
            {
                // MSAA needs both a pipeline sample count and camera opt-in. Hardware dynamic
                // resolution is a separate camera opt-in; renderScale is not a substitute.
                Assert.That(cameras.Any(c => c.allowMSAA && !c.allowDynamicResolution), Is.True, "4x MSAA camera");
                using var dynamicCamera = new SerializedObject(cameras.Single(c => c.name == "ContactCameraDynamic"));
                Assert.That(dynamicCamera.FindProperty("m_AllowDynamicResolution").boolValue, Is.True);
                Assert.That(dynamicCamera.FindProperty("m_AllowMSAA").boolValue, Is.False);
                CollectionAssert.IsSubsetOf(new[] { LightType.Directional, LightType.Point, LightType.Spot },
                    lights.Select(light => light.type).ToArray());
                Assert.That(lights.Any(light => light.enabled && light.shadows != LightShadows.None &&
                    (light.type == LightType.Point || light.type == LightType.Spot)), Is.True,
                    "D10 needs a shadow-casting additional light, not just the pipeline shadow flag.");
            }
            else
                Assert.That(lights.Length, Is.GreaterThanOrEqualTo(24), "The stress scene must exercise packed light slots.");
        }
        finally
        {
            if (!alreadyLoaded)
                EditorSceneManager.CloseScene(scene, true);
        }
    }

    static T Load<T>(string path) where T : Object
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        Assert.That(asset, Is.Not.Null, path);
        return asset;
    }
}
