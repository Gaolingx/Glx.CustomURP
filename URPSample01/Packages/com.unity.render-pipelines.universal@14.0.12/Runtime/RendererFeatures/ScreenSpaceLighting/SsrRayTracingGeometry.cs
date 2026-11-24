using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>Explicit registration of a static or rigid mesh for world-space reflections.</summary>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(MeshRenderer))]
    [AddComponentMenu("Rendering/SSR Ray Tracing Geometry")]
    public sealed class SsrRayTracingGeometry : MonoBehaviour
    {
        [SerializeField, Range(0, 0xffffff), Tooltip("Stable DXR instance ID. Must be unique within this scene.")]
        int m_InstanceId;
        [SerializeField] bool m_EnableTriangleCulling = true;

        static readonly List<SsrRayTracingGeometry> s_Active = new List<SsrRayTracingGeometry>();
        static int s_Revision;
        /// <summary>Assign a stable, scene-unique 24-bit ID to a spawned renderer before rendering.</summary>
        public int instanceId
        {
            get => m_InstanceId;
            set => m_InstanceId = Mathf.Clamp(value, 0, 0xffffff);
        }

        MeshRenderer m_Renderer;
        MeshFilter m_Filter;
        Mesh m_Mesh;
        Matrix4x4 m_Matrix;
        int m_Layer, m_PreviousId, m_SceneHandle;
        bool m_Visible, m_Cull;
        readonly List<Material> m_Current = new List<Material>();
        readonly List<Material> m_Previous = new List<Material>();
        readonly List<RayTracingSubMeshFlags> m_Flags = new List<RayTracingSubMeshFlags>();

        void OnEnable()
        {
            m_Renderer = GetComponent<MeshRenderer>();
            if (!s_Active.Contains(this)) s_Active.Add(this);
            ++s_Revision;
        }

        void OnDisable() { s_Active.Remove(this); ++s_Revision; }

        static bool s_ReportedDuplicate;
        internal static Material Collect(Scene scene, List<RayTracingMember> members, HashSet<uint> ids, out int revision)
        {
            members.Clear();
            ids.Clear();
            Material witness = null;
            foreach (var entry in s_Active)
            {
                if (!entry || !entry.m_Renderer) continue;
                var memberScene = entry.gameObject.scene;
                if (entry.m_SceneHandle != memberScene.handle) { entry.m_SceneHandle = memberScene.handle; ++s_Revision; }
                if (memberScene != scene) continue;
                var representative = entry.Refresh();
                if (!representative) continue;
                uint id = (uint)Mathf.Clamp(entry.m_InstanceId, 0, 0xffffff);
                if (!ids.Add(id))
                {
                    if (!s_ReportedDuplicate) Debug.LogWarning($"Duplicate SSR ray tracing instance ID {id}; using screen-space reflections.", entry);
                    s_ReportedDuplicate = true;
                    members.Clear();
                    revision = s_Revision;
                    return null;
                }
                members.Add(new RayTracingMember(entry.m_Renderer, id, 0xff, entry.m_EnableTriangleCulling));
                witness = representative;
            }
            s_ReportedDuplicate = false;
            revision = s_Revision;
            return witness;
        }

        Material Refresh()
        {
            if (!m_Filter) TryGetComponent(out m_Filter);
            var mesh = m_Filter ? m_Filter.sharedMesh : null;
            var r = m_Renderer;
            bool visible = r.enabled && !r.forceRenderingOff && mesh &&
                (r.rayTracingMode == RayTracingMode.Static || r.rayTracingMode == RayTracingMode.DynamicTransform);
            var matrix = r.localToWorldMatrix;
            bool dirty = m_Mesh != mesh || !m_Matrix.Equals(matrix) || m_Visible != visible || m_Layer != gameObject.layer ||
                         m_PreviousId != m_InstanceId || m_Cull != m_EnableTriangleCulling;
            m_Mesh = mesh; m_Matrix = matrix; m_Visible = visible; m_Layer = gameObject.layer;
            m_PreviousId = m_InstanceId; m_Cull = m_EnableTriangleCulling;
            r.GetSharedMaterials(m_Current);
            Material witness = null;
            int count = visible ? Mathf.Min(mesh.subMeshCount, m_Current.Count) : 0;
            if (count != m_Flags.Count)
            {
                dirty = true;
                m_Flags.Clear(); m_Previous.Clear();
                for (int i = 0; i < count; ++i) { m_Flags.Add(RayTracingSubMeshFlags.Disabled); m_Previous.Add(null); }
            }
            for (int i = 0; i < count; ++i)
            {
                var material = m_Current[i];
                var flags = mesh.GetTopology(i) == MeshTopology.Triangles ? RayTracingSceneInstances.Classify(material) : RayTracingSubMeshFlags.Disabled;
                dirty |= flags != m_Flags[i] || material != m_Previous[i];
                m_Flags[i] = flags; m_Previous[i] = material;
                if (flags != RayTracingSubMeshFlags.Disabled) witness = material;
            }
            if (dirty) ++s_Revision;
            return witness;
        }
    }
}
