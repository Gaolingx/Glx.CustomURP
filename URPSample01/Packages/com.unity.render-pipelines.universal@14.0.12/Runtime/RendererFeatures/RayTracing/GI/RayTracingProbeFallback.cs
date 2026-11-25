using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering.Universal
{
    // Scene-owned bridge between the shared RT scene and the SSGI diffuse probe fallback. It runs
    // one producer batch per scene frame from the first eligible camera, keeps the published globals
    // for the material blend, and removes its GI request bit whenever the fallback is off or the
    // camera/scene cannot be served. Disabling the feature releases the atlas; losing a single
    // camera only drops the request so the converged cache survives.
    internal sealed class RayTracingProbeFallback : IDisposable
    {
        readonly ScreenSpaceLightingSettings m_Settings;
        readonly RayTracingSceneResources m_Scene;
        readonly List<RayTracingMember> m_Members = new List<RayTracingMember>();
        readonly HashSet<uint> m_Ids = new HashSet<uint>();
        RayTracingProbeUpdate m_Producer;
        RayTracingProbeConfig m_Config;
        int m_SceneHandle;
        bool m_Requested, m_Active, m_Disposed;

        internal RayTracingProbeFallback(ScreenSpaceLightingSettings settings, RayTracingSceneResources scene)
        {
            m_Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            m_Scene = scene ?? throw new ArgumentNullException(nameof(scene));
        }

        internal bool ready => m_Producer != null && m_Producer.ready;
        internal int updateCount => m_Producer != null ? m_Producer.updateCount : 0;
        internal int generation => m_Producer != null ? m_Producer.generation : 0;
        internal bool producerAllocated => m_Producer != null && m_Producer.allocated;
        internal long producerBytes => m_Producer != null ? m_Producer.residentBytes : 0;
        internal RayTracingProbeConfig config => m_Config;

        // Validation hooks: read the published probe data of the integrated producer (not a fixture
        // copy) so an image check can be tied to the exact state the material sampled.
        internal Vector4[] ReadProbeData() =>
            m_Producer != null ? m_Producer.ReadProbeData() : System.Array.Empty<Vector4>();

        internal void ReadProbe(int probe, Vector4[] irradiance, Vector4[] distance, out uint published,
            out uint state)
        {
            if (m_Producer != null) m_Producer.ReadProbe(probe, irradiance, distance, out published, out state);
            else { published = 0; state = 0; }
        }

        internal static bool Eligible(in CameraData camera, ScreenSpaceLightingSettings settings,
            ScreenSpaceGlobalIllumination ssgi)
        {
            if (settings == null || !settings.rayTracingProbeFallback) return false;
            if (ssgi == null || !ssgi.enabled.value) return false;
            var resources = settings.resources;
            if (resources == null || resources.rayTracingGi == null || resources.giProbeUpdateCompute == null)
                return false;
            return camera.camera &&
                camera.cameraType == CameraType.Game && camera.renderType == CameraRenderType.Base &&
                (camera.xr == null || !camera.xr.enabled) &&
                camera.cameraTargetDescriptor.dimension == TextureDimension.Tex2D &&
                SystemInfo.supportsRayTracing;
        }

        // Runs after the shared scene exists for this camera. The producer keeps its own published
        // generation; a second camera in the same scene frame reuses it without dispatching.
        internal bool Prepare(in CameraData camera, ScreenSpaceGlobalIllumination ssgi)
        {
            m_Active = false;
            if (m_Disposed) return false;
            if (!Eligible(camera, m_Settings, ssgi))
            {
                Request(false);
                return false;
            }

            var scene = camera.camera.gameObject.scene;
            if (scene.handle != m_SceneHandle)
            {
                m_SceneHandle = scene.handle;
                ReleaseProducer();
            }

            var witness = SsrRayTracingGeometry.Collect(scene, m_Members, m_Ids, out int revision);
            if (!witness)
            {
                Request(false);
                return false;
            }

            // Gate before subscribing so an unsupported backend never reports a GI subscriber.
            var gate = RayTracingSceneGate.Capture(camera, true, m_Settings.resources.rayTracingGi, witness);
            if (gate.Evaluate(RayTracingRequests.GI) != RayTracingDecision.Accepted)
            {
                Request(false);
                return false;
            }

            // Compare the requested config, not the clamped copy the producer keeps, so an
            // out-of-range setting cannot rebuild the atlas every frame.
            var requested = BuildConfig();
            if (m_Producer == null)
                m_Producer = new RayTracingProbeUpdate(requested, m_Settings.resources.rayTracingGi,
                    m_Settings.resources.giProbeUpdateCompute);
            else if (!m_Config.Matches(requested))
            {
                // An explicit toggle change must reach the producer: it captured relocation,
                // classification and feedback at construction, so a changed config is a cold restart
                // of the atlas rather than a silently ignored switch.
                ReleaseProducer();
                m_Producer = new RayTracingProbeUpdate(requested, m_Settings.resources.rayTracingGi,
                    m_Settings.resources.giProbeUpdateCompute);
            }
            m_Config = requested;

            Request(true);
            m_Producer.TryUpdate(Time.frameCount, camera, m_Scene, witness, revision, ~0, m_Members, out _);
            m_Active = m_Producer.ready;
            return m_Active;
        }

        internal bool BindPublishedGlobals(CommandBuffer cmd) =>
            m_Active && m_Producer != null && m_Producer.BindPublishedGlobals(cmd);

        void Request(bool wanted)
        {
            if (wanted == m_Requested) return;
            if (wanted) m_Scene.AddRequests(RayTracingRequests.GI);
            else m_Scene.RemoveRequests(RayTracingRequests.GI);
            m_Requested = wanted;
        }

        void ReleaseProducer()
        {
            m_Producer?.Dispose();
            m_Producer = null;
        }

        // Full reset: feature switch off, teardown, or scene unload. Releases the atlas and the
        // scene request so the off path allocates and dispatches nothing.
        internal void Reset()
        {
            m_Active = false;
            Request(false);
            ReleaseProducer();
            m_SceneHandle = 0;
            m_Members.Clear();
            m_Ids.Clear();
        }

        RayTracingProbeConfig BuildConfig()
        {
            var config = RayTracingProbeConfig.Default;
            config.origin = m_Settings.probeGridOrigin;
            config.spacing = m_Settings.probeGridSpacing;
            config.countX = m_Settings.probeGridCounts.x;
            config.countY = m_Settings.probeGridCounts.y;
            config.countZ = m_Settings.probeGridCounts.z;
            config.budget = m_Settings.probeBatchSize;
            config.raysPerProbe = m_Settings.probeRaysPerProbe;
            config.feedback = m_Settings.probeFeedback;
            config.relocation = m_Settings.probeRelocation;
            config.classification = m_Settings.probeClassification;
            return config;
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            Reset();
        }
    }
}
