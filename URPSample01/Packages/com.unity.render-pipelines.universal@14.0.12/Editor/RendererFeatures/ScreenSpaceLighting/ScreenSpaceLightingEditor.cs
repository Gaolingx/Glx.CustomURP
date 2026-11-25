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
                "Enable SSGI, SSR and refraction per volume under Add Override > Lighting. Transparent " +
                "reflections also need Transparent on the Reflection override and Receive Transparent SSR " +
                "on the material.",
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

            serializedObject.Update();
            var settingsProperty = serializedObject.FindProperty("m_Settings");

            if (ScreenSpaceFeatureInspector.Foldout(feature, "Settings", true))
            {
                EditorGUI.indentLevel++;

                if (ScreenSpaceFeatureInspector.Foldout(feature, "Reflections"))
                {
                    EditorGUI.indentLevel++;
                    ScreenSpaceFeatureInspector.DrawProperties(settingsProperty,
                        "publishProbeAtlas", "rayTracingReflections", "reflectionRayBudget", "rayTracingProbeFallback");
                    EditorGUI.indentLevel--;
                }

                if (ScreenSpaceFeatureInspector.Foldout(feature, "Probes"))
                {
                    EditorGUI.indentLevel++;
                    ScreenSpaceFeatureInspector.DrawProperties(settingsProperty,
                        "probeGridOrigin", "probeGridSpacing", "probeGridCounts",
                        "probeBatchSize", "probeRaysPerProbe",
                        "probeFeedback", "probeRelocation", "probeClassification", "probeCoverage");
                    EditorGUI.indentLevel--;
                }

                ScreenSpaceFeatureInspector.HistoryFoldout(feature, settingsProperty);

                if (ScreenSpaceFeatureInspector.Foldout(feature, "Debug"))
                {
                    EditorGUI.indentLevel++;
                    ScreenSpaceFeatureInspector.DrawProperties(settingsProperty, "debugMode", "debugMip");
                    EditorGUI.indentLevel--;
                }

                EditorGUI.indentLevel--;
            }

            if (ScreenSpaceFeatureInspector.Foldout(feature, "Resources", true))
            {
                EditorGUI.indentLevel++;
                ScreenSpaceFeatureInspector.DrawProperties(
                    settingsProperty.FindPropertyRelative("resources"),
                    "depthPyramidCompute", "colorPyramidShader", "debugShader",
                    "ssgiCompute", "ssgiTemporalFilterCompute", "ssgiDiffuseDenoiserCompute", "bilateralUpsampleCompute",
                    "ssrCompute", "ssrReflectionDenoiserCompute",
                    "rayTracingSsr", "rayTracingGi", "giProbeUpdateCompute", "transparentDepthCopyShader");
                EditorGUI.indentLevel--;
            }

            if (serializedObject.ApplyModifiedProperties())
                EditorUtility.SetDirty(feature);
        }
    }
}
