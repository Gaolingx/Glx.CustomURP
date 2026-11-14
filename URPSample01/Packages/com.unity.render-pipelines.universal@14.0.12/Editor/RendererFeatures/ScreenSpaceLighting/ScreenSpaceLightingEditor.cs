using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    [CustomEditor(typeof(ScreenSpaceLightingRendererFeature))]
    internal sealed class ScreenSpaceLightingEditor : ScriptableRendererFeatureEditor
    {
        public override void OnInspectorGUI()
        {
            var feature = (ScreenSpaceLightingRendererFeature)target;
            var settings = feature.settings;

            EditorGUILayout.HelpBox(
                "SSGI, screen-space reflections (opaque and transparent) and refraction are enabled per volume through " +
                "Add Override > Lighting > Screen Space Global Illumination / Reflection / Refraction. " +
                "Transparent reflections additionally require the Transparent option on the Reflection override and " +
                "Receive Transparent SSR on the transparent material. " +
                "The intermediate debug view only changes the image while a mode other than Off is selected.",
                MessageType.Info);

            if (settings != null && !settings.resources.isComplete)
            {
                EditorGUILayout.HelpBox(
                    "The Screen Space Lighting compute and debug shaders are not assigned. " +
                    "Use Assign Package Defaults to reference the shaders shipped with URP.",
                    MessageType.Warning);
                if (GUILayout.Button("Assign Package Defaults"))
                {
                    ScreenSpaceLightingResourceLocator.AssignDefaults(settings.resources);
                    EditorUtility.SetDirty(feature);
                    AssetDatabase.SaveAssets();
                }
            }

            EditorGUI.BeginChangeCheck();
            base.OnInspectorGUI();
            if (EditorGUI.EndChangeCheck())
                EditorUtility.SetDirty(feature);
        }
    }
}