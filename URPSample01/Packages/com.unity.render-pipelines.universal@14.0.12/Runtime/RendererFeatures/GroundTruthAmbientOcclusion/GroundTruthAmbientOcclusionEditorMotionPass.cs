#if UNITY_EDITOR
using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    // URP fills the motion vector target only for cameras with a UniversalAdditionalCameraData.
    // Scene View and other editor-created cameras therefore sample a cleared (black) texture, so
    // GTAO's temporal filter blends the previous AO in place instead of reprojecting it. This pass
    // writes camera-only motion for those cameras, mirroring URP's CameraMotionVectors.shader.
    internal sealed class GroundTruthAmbientOcclusionEditorMotionPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 3);
        internal const string k_ProfilingSamplerName = "GTAO Editor Camera Motion Vectors";
        internal const int k_ShaderPassIndex = 1;

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);
        static readonly int s_PrevViewProjMatrix = Shader.PropertyToID("_PrevViewProjMatrix");
        static readonly int s_NonJitteredViewProjMatrix = Shader.PropertyToID("_NonJitteredViewProjMatrix");

        internal static int s_EditorExecuteCount;
        internal static int s_EditorDrawCount;
        internal static int s_EditorFirstFrameSkipCount;

        GroundTruthAmbientOcclusionHistory m_History;
        GroundTruthAmbientOcclusionResources m_Resources;

        internal GroundTruthAmbientOcclusionEditorMotionPass()
        {
            renderPassEvent = k_RenderPassEvent;
            // The pass reprojects _CameraDepthTexture; motion is already requested by GTAO itself.
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        internal void Setup(GroundTruthAmbientOcclusionHistory history, GroundTruthAmbientOcclusionResources resources)
        {
            m_History = history;
            m_Resources = resources;
        }

        internal static void ResetEditorCounters()
        {
            s_EditorExecuteCount = 0;
            s_EditorDrawCount = 0;
            s_EditorFirstFrameSkipCount = 0;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            s_EditorExecuteCount++;
            if (m_History == null || m_Resources == null)
                return;

            ref var cameraData = ref renderingData.cameraData;
            if (!(cameraData.renderer is UniversalRenderer renderer))
                return;

            if (cameraData.xr.enabled && cameraData.xr.viewCount > 1)
                return;

            // Cameras with the component already receive URP's camera and object motion; overwriting
            // the target would discard the object motion URP just rendered.
            if (cameraData.camera.TryGetComponent<UniversalAdditionalCameraData>(out _))
                return;

            var motionTarget = GroundTruthAmbientOcclusionPass.GetMotionTexture(renderer);
            var material = m_Resources.debugMaterial;
            if (motionTarget == null || material == null)
                return;

            Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(
                cameraData.GetProjectionMatrixNoJitter(0), true) * cameraData.GetViewMatrix(0);

            // The first pose after a history reset has no previous view to reproject from; GTAO only
            // copies its spatial result into history on that frame, so a cleared motion target is fine.
            if (!m_History.hasEditorMotionViewProjection)
            {
                m_History.RecordEditorMotionViewProjection(viewProjection);
                s_EditorFirstFrameSkipCount++;
                return;
            }

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                // Same globals and conventions as MotionVectorRenderPass so the result matches URP's
                // native camera motion vectors frame by frame.
                cmd.SetGlobalMatrix(s_PrevViewProjMatrix, m_History.editorMotionViewProjection);
                cmd.SetGlobalMatrix(s_NonJitteredViewProjMatrix, viewProjection);
                CoreUtils.SetRenderTarget(cmd, motionTarget);
                cmd.DrawProcedural(Matrix4x4.identity, material, k_ShaderPassIndex, MeshTopology.Triangles, 3, 1);
            }

            // Record per render, not per Time.frameCount: editor repaints can render the same camera
            // several times in one frame, and each pose must reproject from the pose drawn before it.
            m_History.RecordEditorMotionViewProjection(viewProjection);
            s_EditorDrawCount++;
        }
    }
}
#endif