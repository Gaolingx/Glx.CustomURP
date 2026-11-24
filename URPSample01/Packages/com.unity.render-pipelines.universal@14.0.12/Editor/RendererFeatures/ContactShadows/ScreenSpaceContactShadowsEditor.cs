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
            var settings = serializedObject.FindProperty("m_Settings");
            var mode = settings.FindPropertyRelative("debugMode");
            EditorGUILayout.PropertyField(mode);
            if (mode.intValue == (int)ContactShadowDebugMode.Slot)
                EditorGUILayout.PropertyField(settings.FindPropertyRelative("debugSlot"));
            if (mode.intValue != (int)ContactShadowDebugMode.Off)
                EditorGUILayout.HelpBox("Mask shows any occluded light; Fade shows the fade byte; Slot isolates one light bit. Raw maps the three mask bytes to RGB (lowest first) and fade to alpha, before post-processing.", MessageType.Info);
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("maxCachedCameras"));
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("maxUnusedFrames"));

            EditorGUILayout.LabelField("Resources", EditorStyles.boldLabel);
            var depth = resources.FindPropertyRelative("depthPyramidCompute");
            var contact = resources.FindPropertyRelative("contactShadowsCompute");
            var debug = resources.FindPropertyRelative("debugShader");
            EditorGUILayout.PropertyField(depth);
            EditorGUILayout.PropertyField(contact);
            EditorGUILayout.PropertyField(debug);
            serializedObject.ApplyModifiedProperties();
            var depthShader = depth.objectReferenceValue as ComputeShader;
            var contactShader = contact.objectReferenceValue as ComputeShader;
            if (!depthShader || !contactShader || !debug.objectReferenceValue)
                EditorGUILayout.HelpBox("Assign Defaults fills empty references from the URP package. Missing compute resources disable contact shadows; a missing debug shader disables only the debug view.", MessageType.Warning);
            if ((depthShader && (!depthShader.HasKernel("KDepthPyramidCopy") || !depthShader.HasKernel("KDepthPyramidDownsample"))) ||
                (contactShader && !contactShader.HasKernel("KContactShadow")))
                EditorGUILayout.HelpBox("An assigned compute shader lacks a required kernel. Clear the incorrect reference and use Assign Defaults.", MessageType.Warning);
            if (GUILayout.Button("Assign Defaults"))
                AssignDefaults(serializedObject);
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
