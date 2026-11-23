using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.Universal;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public class ContactShadowFeatureImportTests
{
    const string Key = "ContactShadowFeatureImportTests.";
    const string Prefix = "Assets/T1ContactShadow_";
    const string Utils = "Packages/com.unity.render-pipelines.universal/Shaders/Utils/";
    const string EvidenceFlag = "-contactShadowT1Evidence";
    const string FeatureMenu = "Screen Space Contact Shadows";
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly string Domain = Guid.NewGuid().ToString("N");
    static string Root => Prefix + SessionState.GetString(Key + "run", "");

    [UnityTest]
    public IEnumerator InspectorImportsSurviveDomainReload()
    {
        Assert.That(GetType().Assembly.GetName().Name, Is.EqualTo("Assembly-CSharp-Editor"));
        string run = Guid.NewGuid().ToString("N");
        SessionState.SetString(Key + "run", run);
        Assert.That(AssetDatabase.CreateFolder("Assets", "T1ContactShadow_" + run), Is.Not.Empty);
        CreateFixture();
        SessionState.SetString(Key + "before", JsonUtility.ToJson(Inspect()));
        SessionState.SetString(Key + "domain", Domain);

        EditorUtility.RequestScriptReload();
        yield return new WaitForDomainReload();

        // UTF restores the iterator's program counter, not its locals or managed asset wrappers.
        Assert.That(Domain, Is.Not.EqualTo(SessionState.GetString(Key + "domain", "")));
        foreach (string file in new[] { "Renderer.asset", "Pipeline.asset", "Profile.asset", "Light.prefab" })
            Reimport(file);
        var restored = Inspect();
        Assert.That(JsonUtility.ToJson(restored), Is.EqualTo(SessionState.GetString(Key + "before", "")),
            "Asset GUIDs, local file IDs, settings and reference chains must survive save/reload.");
        RemoveRestoredFeature();
        var args = Environment.GetCommandLineArgs();
        int evidence = Array.IndexOf(args, EvidenceFlag);
        if (evidence >= 0)
        {
            var dir = args[evidence + 1];
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "FeatureImport.json"), JsonUtility.ToJson(restored, true));
        }
    }

    [TearDown]
    public void DeleteFixture()
    {
        string run = SessionState.GetString(Key + "run", "");
        if (Guid.TryParseExact(run, "N", out _) && AssetDatabase.IsValidFolder(Root))
            Assert.That(AssetDatabase.DeleteAsset(Root), Is.True, "Could not delete " + Root);
        foreach (string suffix in new[] { "run", "domain", "before" })
            SessionState.EraseString(Key + suffix);
    }

    static void CreateFixture()
    {
        var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
        AssetDatabase.CreateAsset(renderer, Root + "/Renderer.asset");
        AssetDatabase.CreateAsset(UniversalRenderPipelineAsset.Create(renderer), Root + "/Pipeline.asset");
        WithRendererPicker(renderer, (provider, editor) =>
        {
            var entry = FeatureEntry(provider);
            Assert.That(entry, Is.Not.Null, "Feature missing from Add Renderer Feature");
            Assert.That(provider.GoToChild(entry, true), Is.True);
            Assert.That(FeatureEntry(provider), Is.Null, "Picker still offers a duplicate");
        });
        var feature = (ScreenSpaceContactShadowsRendererFeature)renderer.rendererFeatures.Single();
        using (var so = new SerializedObject(feature))
        {
            var inspector = typeof(ScriptableRendererDataEditor).Assembly.GetType(
                "UnityEditor.Rendering.Universal.ScreenSpaceContactShadowsEditor", true);
            inspector.GetMethod("AssignDefaults", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(SerializedObject) }, null).Invoke(null, new object[] { so });
            so.Update();
            CheckDefault<ComputeShader>(so, "depthPyramidCompute", "ScreenSpaceCommon/DepthPyramid.compute");
            CheckDefault<ComputeShader>(so, "contactShadowsCompute", "ContactShadows/ContactShadows.compute",
                "ScreenSpaceCommon/BilateralUpsample.compute");
            CheckDefault<Shader>(so, "debugShader", "ContactShadows/ContactShadowDebug.shader", "Blit.shader");
            var mode = so.FindProperty("m_Settings.debugMode");
            int slotMode = Array.IndexOf(mode.enumNames, "Slot");
            Assert.That(slotMode, Is.GreaterThan(0));
            mode.enumValueIndex = slotMode;
            so.FindProperty("m_Settings.debugSlot").intValue = 17;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        AssetDatabase.SaveAssetIfDirty(feature);
        AssetDatabase.SaveAssetIfDirty(renderer);

        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(profile, Root + "/Profile.asset");
        var menu = typeof(ContactShadowsVolume).GetCustomAttribute<VolumeComponentMenuForRenderPipeline>();
        Assert.That(menu?.menu, Is.EqualTo("Shadowing/Contact Shadows"));
        Assert.That(menu.pipelineTypes, Does.Contain(typeof(UniversalRenderPipeline)));
        var profileEditor = Editor.CreateEditor(profile);
        var list = new VolumeComponentListEditor(profileEditor);
        try
        {
            list.Init(profile, profileEditor.serializedObject);
            typeof(VolumeComponentListEditor).GetMethod("AddComponent", PrivateInstance, null,
                new[] { typeof(Type) }, null).Invoke(list, new object[] { typeof(ContactShadowsVolume) });
        }
        finally
        {
            list.Clear();
            Object.DestroyImmediate(profileEditor);
        }
        Assert.That(profile.TryGet(out ContactShadowsVolume volume), Is.True);
        volume.enabled.Override(true);
        EditorUtility.SetDirty(volume);
        AssetDatabase.SaveAssetIfDirty(volume);
        CreateLightPrefab(profile);
    }

    // Later rounds supply the contact compute/debug assets. Verify Assign Defaults against what
    // actually exists; when absent, use a named serialization probe, never claim it as a default.
    static void CheckDefault<T>(SerializedObject so, string field, string path, string probe = null) where T : Object
    {
        var property = so.FindProperty("m_Resources." + field);
        var expected = AssetDatabase.LoadAssetAtPath<T>(Utils + path);
        Assert.That(property.objectReferenceValue, Is.EqualTo(expected), "Assign Defaults: " + field);
        if (!expected && probe != null && string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(Utils + path)))
            property.objectReferenceValue = AssetDatabase.LoadAssetAtPath<T>(Utils + probe);
        Assert.That(property.objectReferenceValue, Is.InstanceOf<T>(), field);
    }

    static void CreateLightPrefab(VolumeProfile profile)
    {
        var scene = EditorSceneManager.NewPreviewScene();
        var selection = Selection.objects;
        try
        {
            var go = ObjectFactory.CreateGameObject(scene, HideFlags.None, "Contact Shadow Light", typeof(Light));
            var menu = typeof(ContactShadowLight).GetCustomAttribute<AddComponentMenu>();
            Assert.That(menu?.componentMenu, Is.EqualTo("Rendering/Contact Shadow Light"));
            Selection.activeGameObject = go;
            Assert.That(EditorApplication.ExecuteMenuItem("Component/" + menu.componentMenu), Is.True);
            var marker = go.GetComponent<ContactShadowLight>();
            Assert.That(marker, Is.Not.Null);
            marker.castContactShadows = false;
            go.AddComponent<Volume>().sharedProfile = profile;
            Assert.That(PrefabUtility.SaveAsPrefabAsset(go, Root + "/Light.prefab"), Is.Not.Null);
        }
        finally
        {
            Selection.objects = selection;
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    static void WithRendererPicker(UniversalRendererData data, Action<FilterWindow.IProvider, Editor> action)
    {
        var type = typeof(ScriptableRendererDataEditor);
        var editor = Editor.CreateEditor(data, type); // Use the base inspector's own OnEnable.
        try
        {
            var providerType = type.Assembly.GetType("UnityEditor.Rendering.Universal.ScriptableRendererFeatureProvider", true);
            action((FilterWindow.IProvider)Activator.CreateInstance(providerType, new object[] { editor }), editor);
        }
        finally { Object.DestroyImmediate(editor); }
    }

    static FilterWindow.Element FeatureEntry(FilterWindow.IProvider provider)
    {
        var tree = new List<FilterWindow.Element>();
        provider.CreateComponentTree(tree);
        return tree.SingleOrDefault(e => e.level == 1 && e.name == FeatureMenu);
    }

    static void RemoveRestoredFeature()
    {
        var renderer = Load<UniversalRendererData>("Renderer.asset");
        WithRendererPicker(renderer, (provider, editor) =>
        {
            typeof(ScriptableRendererDataEditor).GetMethod("RemoveComponent", PrivateInstance)
                .Invoke(editor, new object[] { 0 }); // Same action as the inspector context menu.
            Assert.That(FeatureEntry(provider), Is.Not.Null);
        });
        AssetDatabase.SaveAssetIfDirty(renderer);
        Reimport("Renderer.asset");
        using var so = new SerializedObject(Load<UniversalRendererData>("Renderer.asset"));
        Assert.That(so.FindProperty("m_RendererFeatures").arraySize, Is.Zero);
        Assert.That(so.FindProperty("m_RendererFeatureMap").arraySize, Is.Zero);
        Assert.That(AssetDatabase.LoadAllAssetsAtPath(Root + "/Renderer.asset").OfType<ScreenSpaceContactShadowsRendererFeature>(), Is.Empty);
    }

    [Serializable]
    class Snapshot
    {
        // References are asset path # GUID : local file ID. Linked fields come from their owners.
        public string pipeline, defaultRenderer, rendererFeature, debugMode;
        public long rendererFeatureMap0;
        public int debugSlot;
        public string contactShadowsCompute, depthPyramidCompute, debugShader;
        public string prefab, light, contactShadowLight, sharedProfile, profileComponent;
        public bool castContactShadows, volumeEnabled, volumeEnabledOverride;
    }

    static Snapshot Inspect()
    {
        var pipeline = Load<UniversalRenderPipelineAsset>("Pipeline.asset");
        using var p = new SerializedObject(pipeline);
        var renderer = Load<UniversalRendererData>("Renderer.asset");
        var list = p.FindProperty("m_RendererDataList");
        var linked = list.GetArrayElementAtIndex(p.FindProperty("m_DefaultRendererIndex").intValue).objectReferenceValue;
        Assert.That(linked, Is.EqualTo(renderer));
        using var r = new SerializedObject(renderer);
        var features = r.FindProperty("m_RendererFeatures");
        var map = r.FindProperty("m_RendererFeatureMap");
        Assert.That(features.arraySize, Is.EqualTo(1));
        Assert.That(map.arraySize, Is.EqualTo(1));
        var feature = features.GetArrayElementAtIndex(0).objectReferenceValue;
        Assert.That(feature, Is.TypeOf<ScreenSpaceContactShadowsRendererFeature>());
        Assert.That(AssetDatabase.IsSubAsset(feature), Is.True);
        Assert.That(AssetDatabase.GetAssetPath(feature), Is.EqualTo(AssetDatabase.GetAssetPath(renderer)));
        string featureRef = Reference(feature, out long localId);
        Assert.That(map.GetArrayElementAtIndex(0).longValue, Is.EqualTo(localId));
        using var f = new SerializedObject(feature);
        var mode = f.FindProperty("m_Settings.debugMode");
        var prefab = Load<GameObject>("Light.prefab");
        var light = prefab.GetComponent<Light>();
        var marker = light.GetComponent<ContactShadowLight>();
        var profile = prefab.GetComponent<Volume>().sharedProfile;
        Assert.That(profile, Is.EqualTo(Load<VolumeProfile>("Profile.asset")));
        var volume = (ContactShadowsVolume)profile.components.Single();
        Reference(profile, out long profileId);
        Reference(volume, out long volumeId);
        Assert.That(volumeId, Is.Not.EqualTo(profileId));
        Assert.That(AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GetAssetPath(volume)), Is.EqualTo(profile));
        Assert.That(AssetDatabase.GetAssetPath(volume), Is.EqualTo(AssetDatabase.GetAssetPath(profile)));
        var snapshot = new Snapshot
        {
            pipeline = Reference(pipeline), defaultRenderer = Reference(linked), rendererFeature = featureRef,
            rendererFeatureMap0 = map.GetArrayElementAtIndex(0).longValue,
            debugMode = mode.enumNames[mode.enumValueIndex], debugSlot = f.FindProperty("m_Settings.debugSlot").intValue,
            contactShadowsCompute = Reference(f.FindProperty("m_Resources.contactShadowsCompute").objectReferenceValue),
            depthPyramidCompute = Reference(f.FindProperty("m_Resources.depthPyramidCompute").objectReferenceValue),
            debugShader = Reference(f.FindProperty("m_Resources.debugShader").objectReferenceValue),
            prefab = Reference(prefab), light = Reference(light), contactShadowLight = Reference(marker),
            sharedProfile = Reference(profile), profileComponent = Reference(volume), castContactShadows = marker.castContactShadows,
            volumeEnabled = volume.enabled.value, volumeEnabledOverride = volume.enabled.overrideState
        };
        Assert.That(snapshot.debugMode, Is.EqualTo("Slot"));
        Assert.That(snapshot.debugSlot, Is.EqualTo(17));
        Assert.That(snapshot.castContactShadows, Is.False);
        Assert.That(snapshot.volumeEnabled && snapshot.volumeEnabledOverride, Is.True);
        return snapshot;
    }

    static string Reference(Object obj) => Reference(obj, out _);

    static string Reference(Object obj, out long localId)
    {
        Assert.That(obj, Is.Not.Null);
        Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out localId), Is.True, obj.name);
        Assert.That(localId, Is.Not.Zero);
        return AssetDatabase.GetAssetPath(obj) + "#" + guid + ":" + localId;
    }

    static T Load<T>(string file) where T : Object => AssetDatabase.LoadAssetAtPath<T>(Root + "/" + file);
    static void Reimport(string file) => AssetDatabase.ImportAsset(Root + "/" + file,
        ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
}
