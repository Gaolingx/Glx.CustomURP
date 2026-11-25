using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    internal class ScreenSpaceFeatureEditorTests
    {
        const string k_ContactResourcesPath = "m_Resources";
        const string k_SettingsPath = "m_Settings";
        const string k_SettingsResourcesPath = "m_Settings.resources";

        static readonly string[] k_ContactResources =
        {
            "depthPyramidCompute", "contactShadowsCompute", "debugShader"
        };

        static readonly string[] k_LightingResources =
        {
            "depthPyramidCompute", "colorPyramidShader", "debugShader",
            "ssgiCompute", "ssgiTemporalFilterCompute", "ssgiDiffuseDenoiserCompute", "bilateralUpsampleCompute",
            "ssrCompute", "ssrReflectionDenoiserCompute", "giProbeUpdateCompute", "transparentDepthCopyShader"
        };

        static readonly string[] k_OcclusionResources =
        {
            "gtaoCompute", "spatialDenoiseCompute", "temporalDenoiseCompute", "copyHistoryCompute",
            "blurAndUpsampleCompute", "depthPyramidCompute", "debugShader"
        };

        string m_AssetFolder;

        [TearDown]
        public void DeleteAssets()
        {
            if (m_AssetFolder == null)
                return;

            Assert.IsTrue(AssetDatabase.DeleteAsset(m_AssetFolder));
            m_AssetFolder = null;
        }

        [Test]
        public void ContactShadowsInspectorFillsAndSavesPackageResources()
        {
            var feature = CreateFeature<ScreenSpaceContactShadowsRendererFeature>();
            OpenInspector(feature);

            AssertResourcesSet(feature, k_ContactResourcesPath + ".", k_ContactResources);
            AssertPersisted<ScreenSpaceContactShadowsRendererFeature>(k_ContactResourcesPath + ".", k_ContactResources);
        }

        [Test]
        public void ScreenSpaceLightingInspectorFillsAndSavesPackageResources()
        {
            var feature = CreateFeature<ScreenSpaceLightingRendererFeature>();
            OpenInspector(feature);

            AssertResourcesSet(feature, k_SettingsResourcesPath + ".", k_LightingResources);
            AssertPersisted<ScreenSpaceLightingRendererFeature>(k_SettingsResourcesPath + ".", k_LightingResources);
        }

        [Test]
        public void OcclusionInspectorFillsAndSavesPackageResources()
        {
            var feature = CreateFeature<GroundTruthAmbientOcclusionRendererFeature>();
            OpenInspector(feature);

            AssertResourcesSet(feature, k_SettingsResourcesPath + ".", k_OcclusionResources);
            AssertPersisted<GroundTruthAmbientOcclusionRendererFeature>(k_SettingsResourcesPath + ".", k_OcclusionResources);
        }

        [Test]
        public void ContactShadowsAssignDefaultsFillsEmptyReferences()
        {
            var feature = CreateFeature<ScreenSpaceContactShadowsRendererFeature>();
            using var serialized = new SerializedObject(feature);
            ScreenSpaceContactShadowsEditor.AssignDefaults(serialized);
            serialized.Update();

            foreach (string field in k_ContactResources)
                Assert.IsNotNull(serialized.FindProperty(k_ContactResourcesPath + "." + field).objectReferenceValue, field);
        }

        [Test]
        public void CompleteRendererAssetIsNotRewrittenOnInspect()
        {
            var feature = CreateFeature<ScreenSpaceLightingRendererFeature>();
            OpenInspector(feature);
            AssetDatabase.SaveAssets();

            OpenInspector(feature);

            Assert.IsFalse(EditorUtility.IsDirty(feature), "Opening a complete asset must not dirty it.");
            AssertResourcesSet(feature, k_SettingsResourcesPath + ".", k_LightingResources);
        }

        [Test]
        public void ContactShadowsInspectorDrawsEverySerializedField()
        {
            var feature = CreateFeature<ScreenSpaceContactShadowsRendererFeature>();
            OpenInspector(feature);
            using var serialized = new SerializedObject(feature);

            var settings = serialized.FindProperty(k_SettingsPath);
            var resources = serialized.FindProperty(k_ContactResourcesPath);

            AssertSameFields(ScreenSpaceContactShadowsEditor.k_SettingsFields, ChildNames(settings), "Contact shadow settings");
            AssertSameFields(ScreenSpaceContactShadowsEditor.k_ResourceFields, ChildNames(resources), "Contact shadow resources");
        }

        [Test]
        public void OcclusionInspectorDrawsEverySerializedField()
        {
            var feature = CreateFeature<GroundTruthAmbientOcclusionRendererFeature>();
            OpenInspector(feature);
            using var serialized = new SerializedObject(feature);

            var settings = serialized.FindProperty(k_SettingsPath);
            var resources = settings.FindPropertyRelative("resources");
            string[] drawn = GroundTruthAmbientOcclusionEditor.k_QualityFields
                .Concat(GroundTruthAmbientOcclusionEditor.k_AppearanceFields)
                .Concat(GroundTruthAmbientOcclusionEditor.k_DenoiseFields)
                .Concat(ScreenSpaceFeatureEditor.k_HistoryFields)
                .Concat(GroundTruthAmbientOcclusionEditor.k_DebugFields)
                .ToArray();

            string[] serializedSettings = ChildNames(settings)
                .Where(name => name != "resources")
                .ToArray();

            AssertSameFields(drawn, serializedSettings, "GTAO settings");
            AssertSameFields(GroundTruthAmbientOcclusionEditor.k_ResourceFields, ChildNames(resources), "GTAO resources");
        }

        [Test]
        public void ScreenSpaceLightingInspectorDrawsEverySerializedField()
        {
            var feature = CreateFeature<ScreenSpaceLightingRendererFeature>();
            OpenInspector(feature);
            using var serialized = new SerializedObject(feature);

            var settings = serialized.FindProperty(k_SettingsPath);
            var resources = settings.FindPropertyRelative("resources");
            string[] drawn = ScreenSpaceLightingEditor.k_ReflectionFields
                .Concat(ScreenSpaceLightingEditor.k_ProbeFields)
                .Concat(ScreenSpaceFeatureEditor.k_HistoryFields)
                .Concat(ScreenSpaceLightingEditor.k_DebugFields)
                .ToArray();

            string[] serializedSettings = ChildNames(settings)
                .Where(name => name != "resources")
                .ToArray();

            AssertSameFields(drawn, serializedSettings, "Screen Space Lighting settings");
            AssertSameFields(ScreenSpaceLightingEditor.k_ResourceFields, ChildNames(resources), "Screen Space Lighting resources");
        }

        [Test]
        public void ResourceReloadPathsResolveInPackage()
        {
            AssertReloadPaths<ScreenSpaceContactShadowsResources>();
            AssertReloadPaths<GroundTruthAmbientOcclusionResources>();
            AssertReloadPaths<ScreenSpaceLightingResources>();
        }

        T CreateFeature<T>() where T : ScriptableRendererFeature
        {
            string guid = AssetDatabase.CreateFolder("Assets", "ScreenSpaceAutoFill_" + Guid.NewGuid().ToString("N"));
            m_AssetFolder = AssetDatabase.GUIDToAssetPath(guid);
            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(renderer, m_AssetFolder + "/Renderer.asset");
            var feature = ScriptableObject.CreateInstance<T>();
            feature.name = typeof(T).Name;
            AssetDatabase.AddObjectToAsset(feature, renderer);
            AssetDatabase.SaveAssets();
            return feature;
        }

        static void OpenInspector(ScriptableRendererFeature feature)
        {
            // Creating the editor executes the inherited OnEnable, which restores the empty references.
            Object.DestroyImmediate(Editor.CreateEditor(feature));
        }

        static void AssertResourcesSet(Object feature, string prefix, string[] fields)
        {
            using var serialized = new SerializedObject(feature);
            foreach (string field in fields)
            {
                var property = serialized.FindProperty(prefix + field);
                Assert.IsNotNull(property, prefix + field);
                Assert.IsNotNull(property.objectReferenceValue, prefix + field);
            }
        }

        void AssertPersisted<T>(string prefix, string[] fields) where T : ScriptableRendererFeature
        {
            string path = m_AssetFolder + "/Renderer.asset";
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var feature = AssetDatabase.LoadAllAssetsAtPath(path).OfType<T>().Single();
            AssertResourcesSet(feature, prefix, fields);
        }

        static string[] ChildNames(SerializedProperty parent)
        {
            var names = new List<string>();
            var child = parent.Copy();
            int parentDepth = parent.depth;
            if (!child.NextVisible(true))
                return names.ToArray();

            while (child.depth > parentDepth)
            {
                names.Add(child.name);
                if (!child.NextVisible(false))
                    break;
            }

            return names.ToArray();
        }

        static void AssertSameFields(string[] expected, string[] actual, string context)
        {
            string[] missing = expected.Except(actual).ToArray();
            string[] unknown = actual.Except(expected).ToArray();
            Assert.IsEmpty(missing, $"{context} does not draw: {string.Join(", ", missing)}");
            Assert.IsEmpty(unknown, $"{context} draws fields that are not listed: {string.Join(", ", unknown)}");
        }

        static void AssertReloadPaths<T>()
        {
            foreach (var field in typeof(T).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                var reload = field.GetCustomAttribute<ReloadAttribute>();
                if (reload == null)
                    continue;

                foreach (string path in reload.paths)
                {
                    string fullPath = UniversalRenderPipelineAsset.packagePath + "/" + path;
                    Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Object>(fullPath), fullPath);
                }
            }
        }
    }
}
