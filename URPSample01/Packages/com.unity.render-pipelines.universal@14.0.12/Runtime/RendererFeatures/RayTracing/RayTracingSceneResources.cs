using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering.Universal
{
    // Main-thread service owned by the renderer/pipeline, never by a camera. No global shader
    // binding. The owner calls SetRequests even on the transition to None, and Dispose on teardown.
    internal sealed class RayTracingSceneResources : IDisposable
    {
        readonly Func<int, IDisposable> m_Create;
        IDisposable m_Resource;
        Scene m_Scene;
        int m_LayerMask, m_Revision;
        bool m_Disposed, m_AllocationFailed;
        internal RayTracingRequests requests { get; private set; }
        internal int liveResources => m_Resource == null ? 0 : 1;
        internal int subscribers => ((requests & RayTracingRequests.GI) != 0 ? 1 : 0) +
            ((requests & RayTracingRequests.Reflections) != 0 ? 1 : 0);

        internal RayTracingSceneResources() : this(CreateManual) { }
        internal RayTracingSceneResources(Func<int, IDisposable> create) => m_Create = create ?? throw new ArgumentNullException(nameof(create));

        static IDisposable CreateManual(int layers) => new RayTracingAccelerationStructure(
            new RayTracingAccelerationStructure.RASSettings(RayTracingAccelerationStructure.ManagementMode.Manual,
                RayTracingAccelerationStructure.RayTracingModeMask.Everything, layers));

        internal void SetRequests(RayTracingRequests value)
        {
            if (m_Disposed) return;
            // Unknown bits must never activate or retain an allocation.
            if ((value & ~(RayTracingRequests.GI | RayTracingRequests.Reflections)) != 0) value = 0;
            if (requests == value) return;
            if (requests == 0 && value != 0)
            {
                RenderPipelineManager.activeRenderPipelineDisposed += Dispose;
                SceneManager.sceneUnloaded += SceneUnloaded;
#if UNITY_EDITOR
                UnityEditor.SceneManagement.EditorSceneManager.sceneClosed += SceneUnloaded;
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Dispose;
#endif
            }
            requests = value;
            if (value == 0)
            {
                RenderPipelineManager.activeRenderPipelineDisposed -= Dispose;
                SceneManager.sceneUnloaded -= SceneUnloaded;
#if UNITY_EDITOR
                UnityEditor.SceneManagement.EditorSceneManager.sceneClosed -= SceneUnloaded;
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
#endif
                Release();
            }
        }

        // Only this return value applies to this camera invocation. An allocated RTAS is not a
        // built or dispatchable TLAS. T1 has no build/publication state or shader-visible handles.
        internal RayTracingDecision Prepare(in CameraData camera, bool enabled, bool highQuality,
            RayTracingShader shader, Material hitMaterial, int revision, int layers, RayTracingRequests cameraRequests)
        {
            // Demand for this view can be empty while other cameras still borrow the scene.
            if ((requests & cameraRequests) == 0 || (cameraRequests & ~requests) != 0) return RayTracingDecision.Off;
            var gate = RayTracingSceneGate.Capture(camera, enabled, highQuality, shader, hitMaterial);
            return Prepare(gate, camera.camera != null ? camera.camera.gameObject.scene : default, revision, layers);
        }

        internal RayTracingDecision Prepare(in RayTracingSceneGate gate, Scene scene, int revision, int layers)
        {
            var decision = gate.Evaluate(requests);
            if (decision != RayTracingDecision.Accepted)
            {
                // Camera-only rejections do not evict the scene used by other base cameras.
                if (decision != RayTracingDecision.CameraUnsupported && decision != RayTracingDecision.XrUnsupported)
                    Release();
                return decision;
            }
            if (!scene.IsValid() || !scene.isLoaded || (m_Resource != null && scene != m_Scene))
                return RayTracingDecision.IncompatibleScene;
            // World origin is fixed at zero in v1. Never lend a stale revision/filter to a caller.
            if (scene == m_Scene && revision == m_Revision && layers == m_LayerMask)
            {
                if (m_AllocationFailed) return RayTracingDecision.AllocationFailed;
                if (m_Resource != null) return RayTracingDecision.Accepted;
            }
            Release();
            m_Scene = scene;
            m_Revision = revision;
            m_LayerMask = layers;
            try
            {
                m_Resource = m_Create(layers);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"RT scene allocation failed: {exception.Message}");
            }
            m_AllocationFailed = m_Resource == null;
            return m_AllocationFailed ? RayTracingDecision.AllocationFailed : RayTracingDecision.Accepted;
        }

        internal void SceneUnloaded(Scene scene)
        {
            if (m_Scene == scene) Release();
        }

        void Release()
        {
            var resource = m_Resource;
            m_Resource = null;
            m_Scene = default;
            m_AllocationFailed = false;
            resource?.Dispose();
        }

        public void Dispose()
        {
            SetRequests(RayTracingRequests.None);
            m_Disposed = true;
        }
    }
}
