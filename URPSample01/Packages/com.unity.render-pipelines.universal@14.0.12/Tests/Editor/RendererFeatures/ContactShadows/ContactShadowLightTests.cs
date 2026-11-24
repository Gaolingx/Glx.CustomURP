using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;

namespace UnityEngine.Rendering.Universal
{
    public class ContactShadowLightTests
    {
        readonly List<GameObject> m_Objects = new List<GameObject>();
        string m_AssetFolder;

        GameObject Track(GameObject obj)
        {
            m_Objects.Add(obj);
            return obj;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = m_Objects.Count - 1; i >= 0; --i)
                if (m_Objects[i] != null)
                    Object.DestroyImmediate(m_Objects[i]);
            m_Objects.Clear();
            if (m_AssetFolder != null)
            {
                Assert.That(AssetDatabase.DeleteAsset(m_AssetFolder), Is.True);
                m_AssetFolder = null;
            }
        }

        [TestCase(LightType.Directional, true)]
        [TestCase(LightType.Point, true)]
        [TestCase(LightType.Spot, true)]
        [TestCase(LightType.Rectangle, false)]
        public void IsMarked_FiltersByLightType(LightType type, bool expected)
        {
            var obj = Track(new GameObject("Light"));
            var light = obj.AddComponent<Light>();
            light.type = type;
            obj.AddComponent<ContactShadowLight>();
            Assert.That(ContactShadowLight.IsMarked(light), Is.EqualTo(expected));
        }

        [Test]
        public void IsMarked_NullIsFalse()
        {
            Assert.That(ContactShadowLight.IsMarked(null), Is.False);
        }

        [Test]
        public void IsMarked_OnlyUsesMarkerOnSameGameObject()
        {
            var target = Track(new GameObject("Target")).AddComponent<Light>();
            var parent = Track(new GameObject("Parent", typeof(Light), typeof(ContactShadowLight)));
            var child = Track(new GameObject("Child", typeof(Light), typeof(ContactShadowLight)));
            Track(new GameObject("Unrelated", typeof(Light), typeof(ContactShadowLight)));
            target.transform.SetParent(parent.transform);
            child.transform.SetParent(target.transform);

            Assert.That(ContactShadowLight.IsMarked(target), Is.False);
            target.gameObject.AddComponent<ContactShadowLight>();
            Assert.That(ContactShadowLight.IsMarked(target), Is.True);
        }

        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        public void IsMarked_RequiresEnabledLightEnabledMarkerAndCastFlag(bool lightEnabled, bool markerEnabled, bool cast)
        {
            var obj = Track(new GameObject("Light"));
            var light = obj.AddComponent<Light>();
            var marker = obj.AddComponent<ContactShadowLight>();
            light.enabled = lightEnabled;
            marker.enabled = markerEnabled;
            marker.castContactShadows = cast;
            Assert.That(ContactShadowLight.IsMarked(light), Is.False);
        }

        [Test]
        public void InvalidMarkers_WarnOnceEachOnEnableInEditMode()
        {
            const string warning = "Contact Shadow Light requires a Light on the same GameObject.";
            int count = 0;
            Application.LogCallback capture = (message, stack, type) =>
            {
                if (type == LogType.Warning && message == warning)
                    ++count;
            };
            Assert.That(Application.isPlaying, Is.False);
            Application.logMessageReceived += capture;
            try
            {
                for (int i = 0; i < 2; ++i)
                {
                    var obj = Track(new GameObject("Invalid marker"));
                    obj.SetActive(false);
                    var marker = obj.AddComponent<ContactShadowLight>();
                    Assert.That(count, Is.EqualTo(i), "Inactive marker must not warn before enable.");
                    obj.SetActive(true);
                    Assert.That(count, Is.EqualTo(i + 1), "Each component must emit its own warning.");
                    marker.enabled = false;
                    marker.enabled = true;
                    Assert.That(count, Is.EqualTo(i + 1), "Re-enabling must not repeat the warning.");
                }
            }
            finally
            {
                Application.logMessageReceived -= capture;
            }
        }

        GameObject InstantiatePrefab(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(asset, Is.Not.Null);
            return Track((GameObject)PrefabUtility.InstantiatePrefab(asset));
        }

        [Test]
        public void Prefab_CastOverrideSurvivesReimportWithoutChangingSource()
        {
            string guid = AssetDatabase.CreateFolder("Assets", "ContactShadowLightTests_" + Guid.NewGuid().ToString("N"));
            Assert.That(guid, Is.Not.Null.And.Not.Empty);
            m_AssetFolder = AssetDatabase.GUIDToAssetPath(guid);
            string sourcePath = m_AssetFolder + "/Light.prefab";
            string variantPath = m_AssetFolder + "/Override.prefab";
            var original = Track(new GameObject("Light", typeof(Light), typeof(ContactShadowLight)));
            Assert.That(PrefabUtility.SaveAsPrefabAsset(original, sourcePath), Is.Not.Null);

            var instance = InstantiatePrefab(sourcePath);
            var light = instance.GetComponent<Light>();
            Assert.That(ContactShadowLight.IsMarked(light), Is.True);
            var marker = instance.GetComponent<ContactShadowLight>();
            marker.castContactShadows = false;
            PrefabUtility.RecordPrefabInstancePropertyModifications(marker);
            Assert.That(ContactShadowLight.IsMarked(light), Is.False);
            Assert.That(PrefabUtility.SaveAsPrefabAsset(instance, variantPath), Is.Not.Null);

            const ImportAssetOptions options = ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport;
            AssetDatabase.ImportAsset(sourcePath, options);
            AssetDatabase.ImportAsset(variantPath, options);
            var variant = InstantiatePrefab(variantPath);
            Assert.That(PrefabUtility.GetPrefabAssetType(variant), Is.EqualTo(PrefabAssetType.Variant));
            var variantMarker = variant.GetComponent<ContactShadowLight>();
            Assert.That(variantMarker, Is.Not.Null);
            Assert.That(variantMarker.castContactShadows, Is.False);
            Assert.That(ContactShadowLight.IsMarked(variant.GetComponent<Light>()), Is.False);
            Assert.That(ContactShadowLight.IsMarked(InstantiatePrefab(sourcePath).GetComponent<Light>()), Is.True);
        }
    }
}
