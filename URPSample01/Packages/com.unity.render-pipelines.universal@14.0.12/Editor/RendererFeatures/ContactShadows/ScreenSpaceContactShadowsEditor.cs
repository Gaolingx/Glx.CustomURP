using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    [CustomEditor(typeof(ScreenSpaceContactShadowsRendererFeature))]
    internal sealed class ScreenSpaceContactShadowsEditor : ScriptableRendererFeatureEditor
    {
        const string k_ShaderRoot = "Packages/com.unity.render-pipelines.universal/Shaders/Utils/";

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Add Shadowing/Contact Shadows to the Volume Profile and Contact Shadow Light to each participating light. Sample Count is set directly on the Volume override.", MessageType.Info);
            serializedObject.Update();
            var resources = serializedObject.FindProperty("m_Resources");
            if (!resources.FindPropertyRelative("depthPyramidCompute").objectReferenceValue ||
                !resources.FindPropertyRelative("contactShadowsCompute").objectReferenceValue ||
                !resources.FindPropertyRelative("debugShader").objectReferenceValue)
            {
                EditorGUILayout.HelpBox("One or more shader references are missing. Assign Defaults fills empty references from the URP package.", MessageType.Warning);
                if (GUILayout.Button("Assign Defaults"))
                    AssignDefaults(serializedObject);
            }
            base.OnInspectorGUI();
        }

        internal static void AssignDefaults(SerializedObject feature)
        {
            feature.Update();
            var resources = feature.FindProperty("m_Resources");
            Assign<ComputeShader>(resources, "depthPyramidCompute", "ScreenSpaceCommon/DepthPyramid.compute");
            Assign<ComputeShader>(resources, "contactShadowsCompute", "ContactShadows/ContactShadows.compute");
            Assign<Shader>(resources, "debugShader", "ContactShadows/ContactShadowDebug.shader");
            feature.ApplyModifiedProperties();
        }

        static void Assign<T>(SerializedProperty resources, string field, string path) where T : Object
        {
            var property = resources.FindPropertyRelative(field);
            if (property.objectReferenceValue == null)
                property.objectReferenceValue = AssetDatabase.LoadAssetAtPath<T>(k_ShaderRoot + path);
        }
    }
}
