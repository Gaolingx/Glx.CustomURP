using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering.Universal
{
    // Identity is supplied by the scene owner, never Unity's process-local GetInstanceID.
    internal readonly struct RayTracingMember
    {
        internal readonly Renderer renderer;
        internal readonly uint id;
        internal readonly byte mask;
        internal readonly bool cull;
        internal RayTracingMember(Renderer renderer, uint id, byte mask, bool enableTriangleCulling = true)
        {
            if (id > 0xffffff) throw new ArgumentOutOfRangeException(nameof(id)); // DXR InstanceID
            this.renderer = renderer; this.id = id; this.mask = mask; cull = enableTriangleCulling;
        }
    }

    internal sealed class RayTracingSceneInstances
    {
        sealed class Entry
        {
            internal RayTracingMember member;
            internal Mesh mesh;
            internal Material[] slots;
            internal RayTracingSubMeshFlags[] flags;
            internal Matrix4x4 matrix;
        }
        readonly Dictionary<uint, Entry> entries = new Dictionary<uint, Entry>();
        internal int count => entries.Count;
        internal int disabledSubmeshes { get; private set; }

        internal static RayTracingSubMeshFlags Classify(Material mat)
        {
            if (!mat || mat.renderQueue > (int)RenderQueue.GeometryLast || mat.FindPass("GlxDXR") < 0 ||
                !mat.GetShaderPassEnabled("GlxDXR") || (mat.HasProperty("_Surface") && mat.GetFloat("_Surface") != 0))
                return RayTracingSubMeshFlags.Disabled;
            return mat.IsKeywordEnabled("_ALPHATEST_ON") ? RayTracingSubMeshFlags.Enabled :
                RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly;
        }

        // Called only for a new owner revision. Fixed zero origin; no camera/frustum input.
        internal bool Sync(RayTracingAccelerationStructure native, Scene scene, int layers, IReadOnlyList<RayTracingMember> members)
        {
            var wanted = new Dictionary<uint, Entry>();
            var ids = new HashSet<uint>();
            var renderers = new HashSet<Renderer>();
            disabledSubmeshes = 0;
            foreach (var m in members)
            {
                var r = m.renderer as MeshRenderer;
                if (!r || r.gameObject.scene != scene || !r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy ||
                    (layers & (1 << r.gameObject.layer)) == 0 || (r.rayTracingMode != RayTracingMode.Static &&
                    r.rayTracingMode != RayTracingMode.DynamicTransform) || !r.TryGetComponent<MeshFilter>(out var filter) || !filter.sharedMesh) continue;
                if (!ids.Add(m.id) || !renderers.Add(r)) throw new ArgumentException("Duplicate RT instance ID or renderer");
                var mesh = filter.sharedMesh;
                var slots = r.sharedMaterials;
                var flags = new RayTracingSubMeshFlags[mesh.subMeshCount];
                bool any = false;
                for (int i = 0; i < flags.Length; ++i)
                {
                    flags[i] = i < slots.Length && mesh.GetTopology(i) == MeshTopology.Triangles ? Classify(slots[i]) : RayTracingSubMeshFlags.Disabled;
                    if (flags[i] != RayTracingSubMeshFlags.Disabled) any = true; else ++disabledSubmeshes;
                }
                if (any) wanted.Add(m.id, new Entry { member = m, mesh = mesh, matrix = r.localToWorldMatrix, slots = slots, flags = flags });
            }
            bool dirty = false;
            // The Renderer overload returns no native handle in 2022.3. If an owner
            // destroyed one before notifying us, reset membership on the SAME RTAS.
            foreach (var old in entries.Values)
                if (!old.member.renderer) { native.ClearInstances(); dirty = true; break; }
            if (dirty) entries.Clear();
            foreach (var id in new List<uint>(entries.Keys))
            {
                var old = entries[id];
                if (wanted.TryGetValue(id, out var next) && next.member.renderer == old.member.renderer &&
                    next.mesh == old.mesh && next.member.cull == old.member.cull &&
                    Same(old.slots, next.slots) && Same(old.flags, next.flags)) continue;
                native.RemoveInstance(old.member.renderer);
                entries.Remove(id);
                dirty = true;
            }
            foreach (var pair in wanted)
            {
                var e = pair.Value;
                var m = e.member;
                if (!entries.TryGetValue(pair.Key, out var old))
                {
                    native.AddInstance(m.renderer, e.flags, m.cull, false, m.mask, m.id);
                    dirty = true;
                }
                else
                {
                    if (m.mask != old.member.mask) { native.UpdateInstanceMask(m.renderer, m.mask); dirty = true; }
                    if (!e.matrix.Equals(old.matrix)) { native.UpdateInstanceTransform(m.renderer); dirty = true; }
                }
                entries[pair.Key] = e;
            }
            return dirty;
        }

        static bool Same<T>(T[] a, T[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; ++i) if (!EqualityComparer<T>.Default.Equals(a[i], b[i])) return false;
            return true;
        }
    }
}
