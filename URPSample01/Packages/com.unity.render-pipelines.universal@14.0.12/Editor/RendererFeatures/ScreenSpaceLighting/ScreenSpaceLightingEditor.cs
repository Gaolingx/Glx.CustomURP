using UnityEditor;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    [CustomEditor(typeof(ScreenSpaceLightingRendererFeature))]
    internal sealed class ScreenSpaceLightingEditor : ScriptableRendererFeatureEditor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "SSGI, screen-space reflections and refraction are enabled per volume through " +
                "Add Override > Lighting > Screen Space Global Illumination / Reflection / Refraction. " +
                "The feature alone does not change the image.",
                MessageType.Info);

            base.OnInspectorGUI();
        }
    }
}
