using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    [CustomEditor(typeof(ScreenSpaceLightingRendererFeature))]
    internal sealed class ScreenSpaceLightingEditor : ScreenSpaceFeatureEditor
    {
        internal static readonly string[] k_ReflectionFields =
        {
            "publishProbeAtlas", "rayTracingReflections", "reflectionRayBudget", "hitDeferredShading",
            "ssrAfterGbuffer", "ssrIndirectSource", "rayTracingProbeFallback"
        };

        internal static readonly string[] k_ProbeFields =
        {
            "probeGridOrigin", "probeGridSpacing", "probeGridCounts",
            "probeBatchSize", "probeRaysPerProbe",
            "probeFeedback", "probeRelocation", "probeClassification", "probeCoverage"
        };

        internal static readonly string[] k_DebugFields = { "debugMode", "debugMip" };

        internal static readonly string[] k_ResourceFields =
        {
            "depthPyramidCompute", "colorPyramidShader", "debugShader",
            "ssgiCompute", "ssgiTemporalFilterCompute", "ssgiDiffuseDenoiserCompute", "bilateralUpsampleCompute",
            "ssrCompute", "ssrReflectionDenoiserCompute", "ssrHitDeferredCompute",
#if UNITY_STANDALONE_WIN
            "rayTracingSsr", "rayTracingGi",
#endif
            "giProbeUpdateCompute", "transparentDepthCopyShader"
        };

        private struct Styles
        {
            public static readonly GUIContent settings = EditorGUIUtility.TrTextContent("Settings");
            public static readonly GUIContent reflections = EditorGUIUtility.TrTextContent("Reflections");
            public static readonly GUIContent probes = EditorGUIUtility.TrTextContent("Probes");
            public static readonly GUIContent history = EditorGUIUtility.TrTextContent("History", "Camera history budget.");
            public static readonly GUIContent debug = EditorGUIUtility.TrTextContent("Debug");
            public static readonly GUIContent resources = EditorGUIUtility.TrTextContent(
                "Resources", "Shaders used by this feature. Empty references are restored from the URP package.");
        }

        private SerializedProperty m_Settings;
        private SerializedProperty m_Resources;

        protected override object ReloadTarget => ((ScreenSpaceLightingRendererFeature)target).settings;

        protected override void OnEnable()
        {
            base.OnEnable();
            m_Settings = serializedObject.FindProperty("m_Settings");
            m_Resources = m_Settings.FindPropertyRelative("resources");
        }

        public override void OnInspectorGUI()
        {
            var feature = (ScreenSpaceLightingRendererFeature)target;
            var settings = feature.settings;

            EditorGUILayout.HelpBox(
                "Enable SSGI, SSR and refraction per volume under Add Override > Lighting. Transparent " +
                "reflections also need Transparent on the Reflection override and Receive Transparent SSR " +
                "on the material.",
                MessageType.Info);

            if (settings == null || settings.resources == null || !settings.resources.isComplete)
            {
                EditorGUILayout.HelpBox(
                    "The Screen Space Lighting compute and debug shaders could not be resolved from the URP " +
                    "package. Empty references are restored when this inspector opens; the feature stays " +
                    "disabled until the shaders are available.",
                    MessageType.Warning);
            }

            serializedObject.Update();

            if (Foldout("Settings", Styles.settings, true))
            {
                EditorGUI.indentLevel++;
                DrawSection(m_Settings, "Reflections", Styles.reflections, k_ReflectionFields);
                DrawSection(m_Settings, "Probes", Styles.probes, k_ProbeFields);
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
