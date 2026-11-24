using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering.Universal
{
    // Main thread, one instance per accepted scene. Discover authored lights once (including
    // inactive ones); runtime spawners register new components. Poll members, not the scene.
    internal sealed class RayTracingSceneLights : IDisposable
    {
        internal const int Capacity = 64;
        const int WordsPerLight = 4, WordBytes = 16;
        static readonly int s_Table = Shader.PropertyToID("GlxHitLightTable");
        internal static readonly int countersId = Shader.PropertyToID("_GlxLightCounters");
        static readonly uint[] s_Zero = new uint[4];
        readonly Scene m_Scene;
        readonly List<Entry> m_Members = new List<Entry>();
        readonly List<Entry> m_Active = new List<Entry>();
        readonly List<Component> m_Components = new List<Component>();
        readonly Vector4[] m_Words = new Vector4[1 + Capacity * WordsPerLight];
        ComputeBuffer m_Table;
        internal ComputeBuffer counters { get; private set; }
        internal int registered => m_Active.Count;
        internal int uploaded => Math.Min(registered, Capacity);
        internal int uploadCount { get; private set; }

        sealed class Entry
        {
            internal Light light;
            internal int kind, ordinal;
            internal int[] path = Array.Empty<int>();
            internal bool active;
        }

        internal RayTracingSceneLights(Scene scene)
        {
            m_Scene = scene;
            var lights = new List<Light>();
            foreach (var root in scene.GetRootGameObjects())
            {
                root.GetComponentsInChildren(true, lights);
                foreach (var light in lights) Register(light);
            }
        }

        internal void Register(Light light)
        {
            if (!light || light.gameObject.scene != m_Scene) return;
            foreach (var e in m_Members) if (e.light == light) return;
            m_Members.Add(new Entry { light = light });
        }

        internal void Unregister(Light light)
        {
            for (int i = m_Members.Count - 1; i >= 0; --i)
                if (m_Members[i].light == light) { m_Members.RemoveAt(i); m_OrderDirty = true; }
        }

        bool m_OrderDirty = true;
        static int Kind(Light l) => l.type == LightType.Directional ? 0 :
            l.type == LightType.Point ? 1 : l.type == LightType.Spot ? 2 : -1;

        static bool BakedOnly(Light light)
        {
#if UNITY_EDITOR
            return light.lightmapBakeType == LightmapBakeType.Baked;
#else
            return light.bakingOutput.lightmapBakeType == LightmapBakeType.Baked;
#endif
        }

        bool RefreshKey(Entry e)
        {
            var t = e.light.transform;
            int depth = 0;
            for (var p = t; p; p = p.parent) ++depth;
            bool changed = e.path.Length != depth;
            if (changed) e.path = new int[depth];
            for (var p = t; p; p = p.parent)
            {
                int index = p.GetSiblingIndex();
                changed |= e.path[--depth] != index;
                e.path[depth] = index;
            }
            e.light.GetComponents(m_Components);
            int ordinal = m_Components.IndexOf(e.light);
            changed |= ordinal != e.ordinal;
            e.ordinal = ordinal;
            return changed;
        }

        static int Compare(Entry a, Entry b)
        {
            int d = a.kind.CompareTo(b.kind);
            for (int i = 0; d == 0 && i < Math.Min(a.path.Length, b.path.Length); ++i)
                d = a.path[i].CompareTo(b.path[i]);
            if (d == 0) d = a.path.Length.CompareTo(b.path.Length);
            return d != 0 ? d : a.ordinal.CompareTo(b.ordinal);
        }

        bool Store(int index, Vector4 value)
        {
            if (m_Words[index].Equals(value)) return false;
            m_Words[index] = value;
            return true;
        }

        bool Pack(int slot, Entry e)
        {
            Light l = e.light;
            var t = l.transform;
            Vector3 axis = -t.forward;
            Color rgb = l.color.linear * l.intensity;
            if (GraphicsSettings.lightsUseColorTemperature && l.useColorTemperature)
                rgb *= Mathf.CorrelatedColorTemperatureToRGB(l.colorTemperature);
            Vector4 atten = Vector4.zero;
            if (e.kind != 0) UniversalRenderPipeline.GetPunctualLightDistanceAttenuation(l.range, ref atten);
            if (e.kind == 2) UniversalRenderPipeline.GetSpotAngleAttenuation(l.spotAngle, l.innerSpotAngle, ref atten);
            Vector3 origin = e.kind == 0 ? axis : t.position;
            int word = 1 + slot * WordsPerLight;
            return Store(word, new Vector4(origin.x, origin.y, origin.z, e.kind)) |
                Store(word + 1, new Vector4(rgb.r, rgb.g, rgb.b, l.shadows != LightShadows.None ? 1 : 0)) |
                Store(word + 2, e.kind == 2 ? (Vector4)axis : Vector4.zero) |
                Store(word + 3, new Vector4(atten.x, atten.z, atten.w, 0));
        }

        internal void Bind(CommandBuffer cmd, RayTracingShader shader)
        {
            for (int i = m_Members.Count - 1; i >= 0; --i)
            {
                var e = m_Members[i];
                var l = e.light;
                if (!l || l.gameObject.scene != m_Scene)
                {
                    m_Members.RemoveAt(i);
                    m_OrderDirty |= e.active;
                    continue;
                }
                int kind = Kind(l);
                bool active = kind >= 0 && l.isActiveAndEnabled && !BakedOnly(l);
                m_OrderDirty |= e.active != active || kind != e.kind;
                e.active = active;
                e.kind = kind;
                if (active) m_OrderDirty |= RefreshKey(e);
            }
            if (m_OrderDirty)
            {
                m_Active.Clear();
                foreach (var e in m_Members) if (e.active) m_Active.Add(e);
                m_Active.Sort(Compare);
                m_OrderDirty = false;
            }
            bool dirty = Store(0, new Vector4(uploaded, 0, 0, 0));
            for (int i = 0; i < uploaded; ++i) dirty |= Pack(i, m_Active[i]);
            if (m_Table == null)
            {
                m_Table = new ComputeBuffer(m_Words.Length, WordBytes, ComputeBufferType.Constant);
                dirty = true;
            }
            if (dirty)
            {
                cmd.SetBufferData(m_Table, m_Words);
                ++uploadCount;
            }
            if (counters == null) counters = new ComputeBuffer(s_Zero.Length, sizeof(uint));
            // CHS is a material shader, not the raygen library: bind its named cbuffer on
            // the command timeline, never through loose SetRayTracing* scalar parameters.
            cmd.SetGlobalConstantBuffer(m_Table, s_Table, 0, m_Words.Length * WordBytes);
            cmd.SetBufferData(counters, s_Zero);
            cmd.SetRayTracingBufferParam(shader, countersId, counters);
        }

        public void Dispose()
        {
            m_Table?.Release();
            counters?.Release();
            m_Table = null;
            counters = null;
        }
    }
}
