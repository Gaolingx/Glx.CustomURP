using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    [CustomEditor(typeof(GroundTruthAmbientOcclusionRendererFeature))]
    internal sealed class GroundTruthAmbientOcclusionEditor : ScreenSpaceFeatureEditor
    {
        internal static readonly string[] k_QualityFields =
        {
            "fullResolution", "stepCount", "directionCount", "maximumRadiusInPixels"
        };

        internal static readonly string[] k_AppearanceFields =
        {
            "intensity", "radius", "directLightingStrength"
        };

        internal static readonly string[] k_DenoiseFields =
        {
            "temporalAccumulation", "ghostingReduction", "blurSharpness",
            "spatialBilateralAggressiveness", "bilateralUpsample"
        };

        internal static readonly string[] k_DebugFields = { "debugMode", "debugMip" };

        internal static readonly string[] k_ResourceFields =
        {
            "gtaoCompute", "spatialDenoiseCompute", "temporalDenoiseCompute", "copyHistoryCompute",
            "blurAndUpsampleCompute", "depthPyramidCompute", "debugShader"
        };

        private struct Styles
        {
            public static readonly GUIContent settings = EditorGUIUtility.TrTextContent("Settings");
            public static readonly GUIContent quality = EditorGUIUtility.TrTextContent("Quality");
            public static readonly GUIContent appearance = EditorGUIUtility.TrTextContent("Appearance");
            public static readonly GUIContent denoise = EditorGUIUtility.TrTextContent("Denoise");
            public static readonly GUIContent history = EditorGUIUtility.TrTextContent("History", "Camera history budget.");
            public static readonly GUIContent debug = EditorGUIUtility.TrTextContent("Debug");
            public static readonly GUIContent resources = EditorGUIUtility.TrTextContent(
                "Resources", "Shaders used by this feature. Empty references are restored from the URP package.");
        }

        private SerializedProperty m_Settings;
        private SerializedProperty m_Resources;

        protected override object ReloadTarget => ((GroundTruthAmbientOcclusionRendererFeature)target).settings;

        protected override void OnEnable()
        {
            base.OnEnable();
            m_Settings = serializedObject.FindProperty("m_Settings");
            m_Resources = m_Settings.FindPropertyRelative("resources");
        }

        public override void OnInspectorGUI()
        {
            var feature = (GroundTruthAmbientOcclusionRendererFeature)target;
            var settings = feature.settings;

            EditorGUILayout.HelpBox(
                "Requires the DepthNormals prepass and motion vectors. Preview, reflection and overlay " +
                "cameras are skipped, and the Editor supplies camera-only motion vectors to cameras " +
                "without UniversalAdditionalCameraData. Opaque shaders without a DepthNormals pass " +
                "receive no AO, and Deferred rendering needs Accurate G-buffer Normals.",
                MessageType.Info);

            if (settings == null || settings.resources == null || !settings.resources.isComplete)
            {
                EditorGUILayout.HelpBox(
                    "The GTAO compute shaders could not be resolved from the URP package. Empty references " +
                    "are restored when this inspector opens; the feature stays disabled until the shaders " +
                    "are available.",
                    MessageType.Warning);
            }

            serializedObject.Update();

            if (Foldout("Settings", Styles.settings, true))
            {
                EditorGUI.indentLevel++;
                DrawSection(m_Settings, "Quality", Styles.quality, k_QualityFields);
                DrawSection(m_Settings, "Appearance", Styles.appearance, k_AppearanceFields);
                DrawSection(m_Settings, "Denoise", Styles.denoise, k_DenoiseFields);
                DrawHistory(m_Settings, Styles.history);
                DrawSection(m_Settings, "Debug", Styles.debug, k_DebugFields);
                EditorGUI.indentLevel--;
            }

            DrawResources(m_Resources, Styles.resources, k_ResourceFields);

            if (serializedObject.ApplyModifiedProperties())
                EditorUtility.SetDirty(feature);
        }
    }
}
