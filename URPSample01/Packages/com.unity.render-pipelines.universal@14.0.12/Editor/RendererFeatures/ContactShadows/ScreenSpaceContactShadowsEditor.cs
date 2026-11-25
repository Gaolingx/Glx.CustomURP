using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    [CustomEditor(typeof(ScreenSpaceContactShadowsRendererFeature))]
    internal sealed class ScreenSpaceContactShadowsEditor : ScreenSpaceFeatureEditor
    {
        internal static readonly string[] k_ResourceFields =
        {
            "depthPyramidCompute", "contactShadowsCompute", "debugShader"
        };

        internal static readonly string[] k_SettingsFields =
        {
            "maxCachedCameras", "maxUnusedFrames", "debugMode", "debugSlot"
        };

        static readonly string[] k_DebugFields = { "debugMode", "debugSlot" };

        private struct Styles
        {
            public static readonly GUIContent settings = EditorGUIUtility.TrTextContent("Settings");
            public static readonly GUIContent history = EditorGUIUtility.TrTextContent("History", "Camera history budget.");
            public static readonly GUIContent debug = EditorGUIUtility.TrTextContent("Debug");
            public static readonly GUIContent resources = EditorGUIUtility.TrTextContent(
                "Resources", "Shaders used by this feature. Empty references are restored from the URP package.");
        }

        private SerializedProperty m_Resources;
        private SerializedProperty m_Settings;

        protected override object ReloadTarget => target;

        protected override void OnEnable()
        {
            base.OnEnable();
            m_Settings = serializedObject.FindProperty("m_Settings");
            m_Resources = serializedObject.FindProperty("m_Resources");
        }

        public override void OnInspectorGUI()
        {
            var feature = (ScreenSpaceContactShadowsRendererFeature)target;

            EditorGUILayout.HelpBox(
                "Add Shadowing/Contact Shadows to the Volume Profile and Contact Shadow Light to each " +
                "participating light. Sample Count is set directly on the Volume override.",
                MessageType.Info);

            serializedObject.Update();

            if (!HasCompleteResources())
            {
                EditorGUILayout.HelpBox(
                    "The contact shadow compute shaders could not be resolved from the URP package. Empty " +
                    "references are restored when this inspector opens; the feature stays disabled until " +
                    "the shaders are available.",
                    MessageType.Warning);
            }

            if (Foldout("Settings", Styles.settings, true))
            {
                EditorGUI.indentLevel++;
                DrawHistory(m_Settings, Styles.history);
                DrawSection(m_Settings, "Debug", Styles.debug, k_DebugFields);
                EditorGUI.indentLevel--;
            }

            DrawResources(m_Resources, Styles.resources, k_ResourceFields);

            if (serializedObject.ApplyModifiedProperties())
                EditorUtility.SetDirty(feature);

            var depthShader = ResourceValue<ComputeShader>("depthPyramidCompute");
            var contactShader = ResourceValue<ComputeShader>("contactShadowsCompute");
            if ((depthShader && (!depthShader.HasKernel("KDepthPyramidCopy") || !depthShader.HasKernel("KDepthPyramidDownsample"))) ||
                (contactShader && !contactShader.HasKernel("KContactShadow")))
            {
                EditorGUILayout.HelpBox(
                    "An assigned compute shader lacks a required kernel. Replace the incorrect reference " +
                    "with one that provides the expected kernel.",
                    MessageType.Warning);
            }
        }

        // Kept for the render-pass tests that fill the feature through a SerializedObject.
        internal static void AssignDefaults(SerializedObject feature)
        {
            feature.Update();
            ResourceReloader.ReloadAllNullIn(feature.targetObject, UniversalRenderPipelineAsset.packagePath);
            feature.Update();
        }

        bool HasCompleteResources()
        {
            return ResourceValue<ComputeShader>("depthPyramidCompute") != null &&
                ResourceValue<ComputeShader>("contactShadowsCompute") != null &&
                ResourceValue<Object>("debugShader") != null;
        }

        T ResourceValue<T>(string name) where T : Object
        {
            return m_Resources?.FindPropertyRelative(name)?.objectReferenceValue as T;
        }
    }
}
