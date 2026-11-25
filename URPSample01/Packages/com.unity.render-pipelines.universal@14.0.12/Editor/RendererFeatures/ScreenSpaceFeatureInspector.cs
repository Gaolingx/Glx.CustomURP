using UnityEditor;
using UnityEngine;

namespace UnityEditor.Rendering.Universal
{
    /// <summary>
    /// Shared inspector drawing for the screen-space renderer features so their settings blocks read
    /// the same way across the Screen Space Lighting, GTAO and contact shadow inspectors.
    /// </summary>
    internal static class ScreenSpaceFeatureInspector
    {
        internal static bool Foldout(Object target, string title, bool defaultOpen = false)
        {
            string key = "Glx.ScreenSpace.v1." + target.GetType().Name + "." + target.GetInstanceID() + "." + title;
            bool open = EditorGUILayout.Foldout(SessionState.GetBool(key, defaultOpen), title, true);
            SessionState.SetBool(key, open);
            return open;
        }

        internal static void DrawProperties(SerializedProperty parent, params string[] names)
        {
            foreach (string name in names)
            {
                var property = parent.FindPropertyRelative(name);
                if (property != null)
                    EditorGUILayout.PropertyField(property);
            }
        }

        // The history budget is edited as plain integers so the same two fields do not appear as
        // sliders in one feature inspector and as number fields in another.
        internal static void HistoryFoldout(Object target, SerializedProperty settings)
        {
            if (!Foldout(target, "History"))
                return;

            EditorGUI.indentLevel++;
            IntField(settings.FindPropertyRelative("maxCachedCameras"), 1, 64);
            IntField(settings.FindPropertyRelative("maxUnusedFrames"), 0, 10000);
            EditorGUI.indentLevel--;
        }

        static void IntField(SerializedProperty property, int min, int max)
        {
            EditorGUI.BeginChangeCheck();
            int value = EditorGUILayout.IntField(property.displayName, property.intValue);
            if (EditorGUI.EndChangeCheck())
                property.intValue = Mathf.Clamp(value, min, max);
        }
    }
}
