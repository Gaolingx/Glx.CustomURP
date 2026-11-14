using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    [CustomEditor(typeof(GroundTruthAmbientOcclusionRendererFeature))]
    internal sealed class GroundTruthAmbientOcclusionEditor : ScriptableRendererFeatureEditor
    {
        public override void OnInspectorGUI()
        {
            var feature = (GroundTruthAmbientOcclusionRendererFeature)target;
            var settings = feature.settings;

            EditorGUILayout.HelpBox(
                "Ground Truth Ambient Occlusion consumes the URP DepthNormals prepass and the built-in early " +
                "motion vectors. Preview, reflection and overlay cameras are skipped. It owns a private depth " +
                "pyramid and AO history, so it runs independently from Screen Space Lighting on the same renderer. " +
                "It yields to the built-in Screen Space Ambient Occlusion feature when both are active.",
                MessageType.Info);

            if (settings != null && !settings.resources.isComplete)
            {
                EditorGUILayout.HelpBox(
                    "The GTAO compute shaders are not assigned. Use Assign Package Defaults to reference the " +
                    "shaders shipped with URP.",
                    MessageType.Warning);
                if (GUILayout.Button("Assign Package Defaults"))
                {
                    GroundTruthAmbientOcclusionResourceLocator.AssignDefaults(settings.resources);
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