using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering.Universal
{
    // Renderer owns the scene; cameras borrow its built snapshot but never share request slots.
    // Only subscribe while an eligible consumer exists (SetActive(false) skips AddRenderPasses).
    internal sealed class SsrRayTracingCameras : IDisposable
    {
        sealed class Entry
        {
            internal SsrRequestQueue queue;
            internal int frame;
        }
        readonly ScreenSpaceLightingRendererFeature m_Owner;
        readonly Dictionary<Camera, Entry> m_Cameras = new Dictionary<Camera, Entry>();
        readonly List<Camera> m_Release = new List<Camera>();
        readonly List<RayTracingMember> m_Members = new List<RayTracingMember>();
        readonly HashSet<uint> m_Ids = new HashSet<uint>();
        readonly RayTracingSceneResources m_Scene;
        bool m_Subscribed;
        internal void RegisterLight(Light light) => m_Scene.RegisterLight(light);
        internal void UnregisterLight(Light light) => m_Scene.UnregisterLight(light);
        internal SsrRayTracingCameras(ScreenSpaceLightingRendererFeature owner, RayTracingSceneResources scene)
        {
            m_Owner = owner;
            m_Scene = scene;
        }

        internal static bool Eligible(in CameraData camera, ScreenSpaceLightingSettings settings, ScreenSpaceReflection ssr)
        {
            // Both algorithms consume the fill: Approximation point-expands it after the upsample,
            // PBR folds it into the reprojection before its denoiser. The trace-sized targets exist
            // at both resolutions; the fixed-K budget is simply spread over 4x more tiles at full
            // resolution, so the requested budget must scale up with the trace size.
            if (!settings.rayTracingReflections || settings.reflectionRayBudget <= 0 || ssr == null || !ssr.enabled.value ||
                settings.resources?.rayTracingSsr == null || !settings.resources.hasSsrResources ||
                (!ssr.fullResolution.value && settings.resources.bilateralUpsampleCompute == null)) return false;
            return camera.camera &&
                camera.cameraType == CameraType.Game && camera.renderType == CameraRenderType.Base &&
                (camera.xr == null || !camera.xr.enabled) && camera.cameraTargetDescriptor.dimension == TextureDimension.Tex2D &&
                SystemInfo.supportsRayTracing;
        }

        internal void Prepare(in CameraData camera, ScreenSpaceLightingSettings settings, ScreenSpaceLightingSsrPass pass)
        {
            var witness = SsrRayTracingGeometry.Collect(camera.camera.gameObject.scene, m_Members, m_Ids, out int revision);
            if (witness)
            {
                m_Scene.AddRequests(RayTracingRequests.Reflections);
                if (m_Scene.TryBuild(camera, true, settings.resources.rayTracingSsr, witness, revision, ~0,
                    RayTracingRequests.Reflections, m_Members, out var snapshot) && m_Scene.instanceCount > 0)
                {
                    if (!m_Cameras.TryGetValue(camera.camera, out var entry))
                        m_Cameras[camera.camera] = entry = new Entry();
                    int k = Mathf.Clamp(settings.reflectionRayBudget, 0, 131072);
                    if (entry.queue == null || entry.queue.budget != k)
                    {
                        entry.queue?.Dispose();
                        entry.queue = new SsrRequestQueue(k);
                    }
                    entry.frame = Time.frameCount;
                    pass.SetHybrid(entry.queue, m_Scene, snapshot);
                    if (!m_Subscribed)
                    {
                        RenderPipelineManager.endContextRendering += EndContext;
                        RenderPipelineManager.activeRenderPipelineDisposed += Clear;
                        SceneManager.sceneUnloaded += SceneUnloaded;
#if UNITY_EDITOR
                        UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Clear;
                        UnityEditor.SceneManagement.EditorSceneManager.sceneClosed += SceneUnloaded;
#endif
                        m_Subscribed = true;
                    }
                    return;
                }
            }
            Forget(camera.camera);
        }

        internal void Forget(Camera camera)
        {
            if (!ReferenceEquals(camera, null) && m_Cameras.TryGetValue(camera, out var entry))
            {
                entry.queue.Dispose();
                m_Cameras.Remove(camera);
            }
            if (m_Cameras.Count == 0) Clear();
        }

        void SceneUnloaded(Scene scene)
        {
            m_Release.Clear();
            foreach (var c in m_Cameras.Keys)
                if (!c || c.gameObject.scene == scene) m_Release.Add(c);
            foreach (var c in m_Release) Forget(c);
        }

        void EndContext(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (!m_Owner || !m_Owner.isActive || !m_Owner.settings.rayTracingReflections || m_Owner.settings.reflectionRayBudget <= 0)
            { Clear(); return; }
            m_Release.Clear();
            foreach (var pair in m_Cameras)
                if (!pair.Key || !pair.Key.isActiveAndEnabled || Time.frameCount - pair.Value.frame > 1)
                    m_Release.Add(pair.Key);
            foreach (var c in m_Release) Forget(c);
        }

        internal void Clear()
        {
            if (m_Subscribed)
            {
                RenderPipelineManager.endContextRendering -= EndContext;
                RenderPipelineManager.activeRenderPipelineDisposed -= Clear;
                SceneManager.sceneUnloaded -= SceneUnloaded;
#if UNITY_EDITOR
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Clear;
                UnityEditor.SceneManagement.EditorSceneManager.sceneClosed -= SceneUnloaded;
#endif
                m_Subscribed = false;
            }
            foreach (var entry in m_Cameras.Values) entry.queue.Dispose();
            m_Cameras.Clear();
            m_Members.Clear();
            // The scene is shared with the GI probe producer; drop only this consumer's bit.
            m_Scene.RemoveRequests(RayTracingRequests.Reflections);
        }

        public void Dispose() { Clear(); }
    }
}
