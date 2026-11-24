using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering.Universal
{
    // Main-thread service owned by the renderer/pipeline, never by a camera. The owner calls
    // SetRequests even on the transition to None, and Dispose on teardown.
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

        // Prepare alone never publishes a built structure. T2 is opt-in via TryBuild below.
        RayTracingSceneInstances m_Instances;
        RayTracingSceneLights m_Lights;
        internal int registeredLights => m_Lights?.registered ?? 0;
        internal int uploadedLights => m_Lights?.uploaded ?? 0;
        internal int tableOverflow => registeredLights - uploadedLights;
        internal int lightUploadCount => m_Lights?.uploadCount ?? 0;
        internal ComputeBuffer lightCounters => m_Lights?.counters;

        // Pre-existing components (including disabled ones) are discovered at first Bind.
        // Spawners register subsequent additions. Destruction, disable, hierarchy reorder and
        // property changes are observed at Bind without invalidating the geometry revision.
        internal void RegisterLight(Light light) => m_Lights?.Register(light);
        internal void UnregisterLight(Light light) => m_Lights?.Unregister(light);

        // Record immediately before a table-mode dispatch. Submit in recording order; capture
        // counters before the next BindLights, which starts a fresh per-dispatch measurement.
        internal void BindLights(CommandBuffer cmd, RayTracingShader shader)
        {
            if (!m_Built) throw new InvalidOperationException("BindLights requires a built RT scene");
            if (cmd == null || !shader) throw new ArgumentNullException(cmd == null ? nameof(cmd) : nameof(shader));
            if (m_Lights == null) m_Lights = new RayTracingSceneLights(m_Scene);
            m_Lights.Bind(cmd, shader);
        }
        internal int instanceCount => m_Instances?.count ?? 0;
        internal int disabledSubmeshes => m_Instances?.disabledSubmeshes ?? 0;
        bool m_Built;
        int m_BuiltRevision;
        internal int buildCount { get; private set; }

        // The owner maintains a complete explicit registration set and advances revision for
        // geometry/membership/filter changes, not camera or lighting changes. No scene searches.
        internal bool TryBuild(in CameraData camera, bool enabled, bool highQuality, RayTracingShader shader,
            Material hitMaterial, int revision, int layers, RayTracingRequests demand,
            System.Collections.Generic.IReadOnlyList<RayTracingMember> members, out RayTracingAccelerationStructure snapshot)
        {
            snapshot = null;
            if (Prepare(camera, enabled, highQuality, shader, hitMaterial, revision, layers, demand) != RayTracingDecision.Accepted)
                return false;
            var native = (RayTracingAccelerationStructure)m_Resource;
            try
            {
                if (!m_Built || revision != m_BuiltRevision)
                {
                    if (m_Instances == null) m_Instances = new RayTracingSceneInstances();
                    if (m_Instances.Sync(native, m_Scene, layers, members) || !m_Built)
                    {
                        var cmd = CommandBufferPool.Get("T2 Build scene RTAS");
                        try
                        {
                            cmd.BuildRayTracingAccelerationStructure(native);
                            Graphics.ExecuteCommandBuffer(cmd);
                            ++buildCount;
                        }
                        finally { CommandBufferPool.Release(cmd); }
                    }
                    m_Built = true;
                    m_BuiltRevision = revision;
                }
                snapshot = native;
                return true;
            }
            catch (Exception ex)
            {
                var scene = m_Scene;
                Release();
                m_Scene = scene;
                m_AllocationFailed = true;
                Debug.LogWarning($"RT scene build failed: {ex.Message}");
                return false;
            }
        }
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
            if (scene == m_Scene && layers == m_LayerMask)
            {
                if (m_AllocationFailed && revision == m_Revision) return RayTracingDecision.AllocationFailed;
                m_Revision = revision;
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
            m_Built = false;
            m_Instances = null;
            m_Lights?.Dispose();
            m_Lights = null;
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
