using UnityEditor;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    [CustomEditor(typeof(GroundTruthAmbientOcclusionRendererFeature))]
    internal sealed class GroundTruthAmbientOcclusionEditor : ScriptableRendererFeatureEditor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "GTAO consumes the URP DepthNormals prepass and motion vectors. " +
                "Preview, reflection and overlay cameras are skipped. Configure it once per renderer data.",
                MessageType.Info);

            base.OnInspectorGUI();
        }
    }
}
