using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    /// <summary>
    /// Shared lifecycle and drawing for the screen-space renderer feature inspectors. Null package
    /// references are restored through <see cref="ResourceReloader"/>, the same mechanism URP uses for
    /// its own serialized resources, and written back so player builds keep the references.
    /// </summary>
    internal abstract class ScreenSpaceFeatureEditor : ScriptableRendererFeatureEditor
    {
        internal static readonly string[] k_HistoryFields = { "maxCachedCameras", "maxUnusedFrames" };

        // URP persists inspector foldout state in EditorPrefs through SavedBool. Keying by feature type
        // instead of instance keeps the key stable across asset reloads, like the 2D inspectors do.
        static readonly Dictionary<string, SavedBool> s_Foldouts = new Dictionary<string, SavedBool>();

        /// <summary>Serialized container whose null <c>[Reload]</c> fields are restored on enable.</summary>
        protected abstract object ReloadTarget { get; }

        protected virtual void OnEnable()
        {
            ReloadResources();
        }

        void ReloadResources()
        {
            if (this == null || target == null)
                return;

            var container = ReloadTarget;
            if (container == null)
                return;

            var (changed, assetDatabaseNotReady) = ResourceReloader.TryReloadAllNullIn(
                container, UniversalRenderPipelineAsset.packagePath);

            if (assetDatabaseNotReady)
            {
                // Same retry as HDRP: the AssetDatabase is still importing, so try again next tick.
                EditorApplication.delayCall += ReloadResources;
                return;
            }

            if (!changed)
                return;

            // URP only marks the asset dirty here; the references are persisted because the resources
            // live in the renderer asset and a player build cannot resolve them from the package.
            EditorUtility.SetDirty(target);
            if (AssetDatabase.Contains(target))
                AssetDatabase.SaveAssetIfDirty(target);
        }

        protected void DrawSection(SerializedProperty parent, string key, GUIContent title, string[] fields, bool defaultOpen = false)
        {
            if (!Foldout(key, title, defaultOpen))
                return;

            EditorGUI.indentLevel++;
            DrawProperties(parent, fields);
            EditorGUI.indentLevel--;
        }

        protected void DrawResources(SerializedProperty resources, GUIContent title, string[] fields)
        {
            DrawSection(resources, "Resources", title, fields, true);
        }

        protected void DrawHistory(SerializedProperty settings, GUIContent title)
        {
            if (!Foldout("History", title))
                return;

            EditorGUI.indentLevel++;
            // The clamps mirror the [Range] attributes on the three feature settings, so the shared
            // panel does not read them back through reflection.
            IntField(settings, "maxCachedCameras", 1, 64);
            IntField(settings, "maxUnusedFrames", 0, 10000);
            EditorGUI.indentLevel--;
        }

        static void IntField(SerializedProperty settings, string name, int min, int max)
        {
            var property = settings.FindPropertyRelative(name);
            if (property == null)
            {
                Debug.LogError($"Screen-space inspector: settings has no serialized field '{name}'.");
                return;
            }

            EditorGUI.BeginChangeCheck();
            int value = EditorGUILayout.IntField(property.displayName, property.intValue);
            if (EditorGUI.EndChangeCheck())
                property.intValue = Mathf.Clamp(value, min, max);
        }

        void DrawProperties(SerializedProperty parent, string[] names)
        {
            foreach (string name in names)
            {
                var property = parent?.FindPropertyRelative(name);
                if (property == null)
                {
                    Debug.LogError($"Screen-space inspector: '{target.GetType().Name}' has no serialized field '{name}'.");
                    continue;
                }

                EditorGUILayout.PropertyField(property);
            }
        }

        protected bool Foldout(string key, GUIContent title, bool defaultOpen = false)
        {
            string prefsKey = $"{target.GetType().Name}.ScreenSpace.{key}";
            if (!s_Foldouts.TryGetValue(prefsKey, out var foldout))
            {
                foldout = new SavedBool(prefsKey, defaultOpen);
                s_Foldouts.Add(prefsKey, foldout);
            }

            bool open = EditorGUILayout.Foldout(foldout.value, title, true);
            foldout.value = open;
            return open;
        }
    }
}
