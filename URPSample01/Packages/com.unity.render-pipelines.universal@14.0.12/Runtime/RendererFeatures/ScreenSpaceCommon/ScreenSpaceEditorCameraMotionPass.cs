#if UNITY_EDITOR
using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    // Per-camera previous pose for the editor camera motion supplement. Only a matrix is stored, so
    // the entry owns no GPU resource and the history store never has to release one.
    internal sealed class ScreenSpaceEditorCameraMotionHistory : IScreenSpaceHistoryEntry
    {
        internal bool hasViewProjection { get; private set; }
        internal Matrix4x4 viewProjection { get; private set; }

        internal void Record(Matrix4x4 value)
        {
            viewProjection = value;
            hasViewProjection = true;
        }

        public void Reset(int width, int height, int frameIndex)
        {
            viewProjection = Matrix4x4.identity;
            hasViewProjection = false;
        }

        public void Dispose()
        {
            Reset(0, 0, -1);
        }
    }

    // URP fills the motion vector target only for cameras with a UniversalAdditionalCameraData.
    // Scene View and other editor-created cameras therefore sample a cleared (black) texture, so
    // GTAO and screen space lighting blend their history in place instead of reprojecting it. This
    // pass writes camera-only motion for those cameras, mirroring URP's CameraMotionVectors.shader.
    internal sealed class ScreenSpaceEditorCameraMotionPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 3);
        internal const string k_ProfilingSamplerName = "Editor Camera Motion Vectors";
        internal const string k_ShaderName = "Hidden/Universal Render Pipeline/ScreenSpaceCommon/EditorCameraMotion";
        internal const int k_ShaderPassIndex = 0;

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);
        static readonly int s_PrevViewProjMatrix = Shader.PropertyToID("_PrevViewProjMatrix");
        static readonly int s_NonJitteredViewProjMatrix = Shader.PropertyToID("_NonJitteredViewProjMatrix");

        // Set by the verification harness to reproduce the no-motion baseline.
        internal static bool s_EditorSuppressEditorMotionPass;

        internal static int s_EditorExecuteCount;
        internal static int s_EditorDrawCount;
        internal static int s_EditorFirstFrameSkipCount;

        static Material s_Material;
        static bool s_LoggedMissingShader;

        readonly CameraHistoryStore<ScreenSpaceEditorCameraMotionHistory> m_HistoryStore =
            new CameraHistoryStore<ScreenSpaceEditorCameraMotionHistory>();

        internal CameraHistoryStore<ScreenSpaceEditorCameraMotionHistory> historyStore => m_HistoryStore;

        internal ScreenSpaceEditorCameraMotionPass()
        {
            renderPassEvent = k_RenderPassEvent;
            // The pass reprojects _CameraDepthTexture. GTAO already requests depth, but a
            // Screen Space Lighting-only enqueue must not depend on the other feature.
            ConfigureInput(ScriptableRenderPassInput.Depth);
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
            if (s_EditorSuppressEditorMotionPass)
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

            var motionTarget = renderer.m_MotionVectorColor;
            var material = GetMaterial();
            if (motionTarget == null || material == null)
                return;

            Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(
                cameraData.GetProjectionMatrixNoJitter(0), true) * cameraData.GetViewMatrix(0);

            var descriptor = cameraData.cameraTargetDescriptor;
            var history = m_HistoryStore.GetOrCreate(
                cameraData.camera,
                0,
                descriptor.width,
                descriptor.height,
                Time.frameCount,
                cameraData.taaSettings.resetHistoryFrames,
                out _);

            // The first pose after a history reset has no previous view to reproject from. Both
            // consumers copy their spatial result into history on that frame, so a cleared target is fine.
            if (!history.hasViewProjection)
            {
                history.Record(viewProjection);
                s_EditorFirstFrameSkipCount++;
                return;
            }

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                // Same globals and conventions as MotionVectorRenderPass so the result matches URP's
                // native camera motion vectors frame by frame.
                cmd.SetGlobalMatrix(s_PrevViewProjMatrix, history.viewProjection);
                cmd.SetGlobalMatrix(s_NonJitteredViewProjMatrix, viewProjection);
                CoreUtils.SetRenderTarget(cmd, motionTarget);
                cmd.DrawProcedural(Matrix4x4.identity, material, k_ShaderPassIndex, MeshTopology.Triangles, 3, 1);
            }

            // Record per render, not per Time.frameCount: editor repaints can render the same camera
            // several times in one frame, and each pose must reproject from the pose drawn before it.
            history.Record(viewProjection);
            s_EditorDrawCount++;
        }

        static Material GetMaterial()
        {
            if (s_Material == null)
            {
                var shader = Shader.Find(k_ShaderName);
                if (shader != null)
                {
                    s_Material = CoreUtils.CreateEngineMaterial(shader);
                }
                else if (!s_LoggedMissingShader)
                {
                    s_LoggedMissingShader = true;
                    Debug.LogWarning(
                        $"The editor camera motion shader '{k_ShaderName}' is missing. Scene View motion " +
                        "vectors stay cleared for cameras without a UniversalAdditionalCameraData.");
                }
            }

            return s_Material;
        }
    }
}
#endif
