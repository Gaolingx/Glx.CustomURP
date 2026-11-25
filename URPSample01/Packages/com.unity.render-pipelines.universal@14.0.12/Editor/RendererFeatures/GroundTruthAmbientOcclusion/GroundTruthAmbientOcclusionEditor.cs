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
                "Requires the DepthNormals prepass and motion vectors. Preview, reflection and overlay " +
                "cameras are skipped. Opaque shaders without a DepthNormals pass receive no AO, and " +
                "Deferred rendering needs Accurate G-buffer Normals.",
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

            serializedObject.Update();
            var settingsProperty = serializedObject.FindProperty("m_Settings");

            if (ScreenSpaceFeatureInspector.Foldout(feature, "Settings", true))
            {
                EditorGUI.indentLevel++;

                if (ScreenSpaceFeatureInspector.Foldout(feature, "Quality"))
                {
                    EditorGUI.indentLevel++;
                    ScreenSpaceFeatureInspector.DrawProperties(settingsProperty,
                        "fullResolution", "stepCount", "directionCount", "maximumRadiusInPixels");
                    EditorGUI.indentLevel--;
                }

                if (ScreenSpaceFeatureInspector.Foldout(feature, "Appearance"))
                {
                    EditorGUI.indentLevel++;
                    ScreenSpaceFeatureInspector.DrawProperties(settingsProperty,
                        "intensity", "radius", "directLightingStrength");
                    EditorGUI.indentLevel--;
                }

                if (ScreenSpaceFeatureInspector.Foldout(feature, "Denoise"))
                {
                    EditorGUI.indentLevel++;
                    ScreenSpaceFeatureInspector.DrawProperties(settingsProperty,
                        "temporalAccumulation", "ghostingReduction", "blurSharpness",
                        "spatialBilateralAggressiveness", "bilateralUpsample");
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
                    "gtaoCompute", "spatialDenoiseCompute", "temporalDenoiseCompute", "copyHistoryCompute",
                    "blurAndUpsampleCompute", "depthPyramidCompute", "debugShader");
                EditorGUI.indentLevel--;
            }

            if (serializedObject.ApplyModifiedProperties())
                EditorUtility.SetDirty(feature);
        }
    }
}
