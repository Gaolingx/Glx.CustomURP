using System;
using UnityEngine;

namespace UnityEditor.Rendering.Universal.Tests
{
    // Test-only, linear CPU storage. Tiles are row-major with 8 probes per row.
    internal sealed class ProbeHalfAtlas
    {
        internal readonly int Interior, Channels;
        internal int Pitch => Interior + 2;
        internal readonly ushort[] Words;

        internal ProbeHalfAtlas(int interior, int channels)
        {
            Interior = interior;
            Channels = channels;
            Words = new ushort[256 * Pitch * Pitch * channels];
        }

        // x,y are interior-relative; -1 and Interior are the one-texel gutters.
        internal int Address(int probe, int x, int y) => Channels *
            (((probe / 8 * Pitch + y + 1) * 8 + probe % 8) * Pitch + x + 1);

        internal void Store(int probe, int x, int y, Vector4 value)
        {
            int address = Address(probe, x, y);
            for (int c = 0; c < Channels; ++c)
            {
                ushort bits = Mathf.FloatToHalf(value[c]);
                Words[address + c] = (bits & 0x7c00) == 0x7c00 ? (ushort)0 : bits;
            }
        }

        internal Vector4 Read(int probe, int x, int y)
        {
            var result = Vector4.zero;
            int address = Address(probe, x, y);
            for (int c = 0; c < Channels; ++c)
                result[c] = Mathf.HalfToFloat(Words[address + c]);
            return result;
        }

        internal void FillGutter(int probe)
        {
            int n = Interior;
            for (int y = -1; y <= n; ++y)
            for (int x = -1; x <= n; ++x)
            {
                bool outsideX = x < 0 || x >= n, outsideY = y < 0 || y >= n;
                if (!outsideX && !outsideY) continue;
                int sx = Mathf.Clamp(x, 0, n - 1), sy = Mathf.Clamp(y, 0, n - 1);
                if (outsideY) sx = n - 1 - sx;
                if (outsideX) sy = n - 1 - sy;
                Array.Copy(Words, Address(probe, sx, sy), Words, Address(probe, x, y), Channels);
            }
        }

        internal static Vector2 Oct(Vector3 dir)
        {
            dir /= Mathf.Abs(dir.x) + Mathf.Abs(dir.y) + Mathf.Abs(dir.z);
            return dir.z >= 0 ? new Vector2(dir.x, dir.y) : new Vector2(
                Mathf.Sign(dir.x) * (1 - Mathf.Abs(dir.y)), Mathf.Sign(dir.y) * (1 - Mathf.Abs(dir.x)));
        }

        internal static Vector3 Normal(int n, int x, int y)
        {
            float u = (2f * x + 1) / n - 1, v = (2f * y + 1) / n - 1;
            float z = 1 - Mathf.Abs(u) - Mathf.Abs(v);
            return new Vector3(u - Mathf.Sign(u) * Mathf.Max(0, -z),
                v - Mathf.Sign(v) * Mathf.Max(0, -z), z).normalized;
        }
    }

    internal static class ProbeMathReference
    {
        internal enum Kind { Hit, Sky, Blocked, Truncated, Missing, Unserved }
        internal struct Ray
        {
            internal Vector3 Direction, Radiance;
            internal float Distance;
            internal Kind Category;
        }

        internal sealed class TraceLog
        {
            internal readonly int[] Counts = new int[6];
            internal int SkyReads;
            internal string Mask;
        }

        // Normalized cosine quadrature. Unknown rays carry no measurement. If no ray
        // supports the texel, leave its packed contents untouched and return invalid.
        internal static bool Convolve(ProbeHalfAtlas atlas, int probe, int x, int y,
            Ray[] rays, Func<Ray, Vector3> sky, out TraceLog log)
        {
            log = new TraceLog();
            var mask = new char[rays.Length];
            var n = ProbeHalfAtlas.Normal(atlas.Interior, x, y);
            Vector3 sum = Vector3.zero;
            float denominator = 0;
            for (int i = 0; i < rays.Length; ++i)
            {
                if (i < 32) { mask[i] = 'P'; continue; }
                Ray r = rays[i];
                int k = (int)r.Category;
                ++log.Counts[k];
                mask[i] = "HSBTMU"[k];
                if (k > (int)Kind.Blocked) continue;
                float w = Mathf.Max(0, Vector3.Dot(n, r.Direction));
                if (w == 0) continue;
                denominator += w;
                if (atlas.Channels == 2)
                    sum += w * new Vector3(r.Distance, r.Distance * r.Distance, 0);
                else if (r.Category == Kind.Sky)
                {
                    ++log.SkyReads;
                    sum += w * sky(r);
                }
                else if (r.Category == Kind.Hit) sum += w * r.Radiance;
            }
            log.Mask = new string(mask);
            if (denominator == 0) return false;
            sum *= (atlas.Channels == 4 ? Mathf.PI : 1) / denominator;
            atlas.Store(probe, x, y, new Vector4(sum.x, sum.y, sum.z, 1));
            return true;
        }

        internal static Vector3 Feedback(Vector3 albedo, Vector3 previousE) =>
            Vector3.Scale(Vector3.Min(albedo, Vector3.one * .9f), previousE) / Mathf.PI;

        internal struct Data { internal Vector3 Offset; internal bool Active, Published; }
        internal static int Index(Vector3Int p) => p.x + 8 * (p.y + 4 * p.z);

        // Synthetic binary radial visibility fixture (not a production moment filter).
        internal static bool Query(ProbeHalfAtlas atlas, Data[] data, Vector3Int cell,
            Vector3 fraction, float visibleRadius, out Vector3 color)
        {
            color = Vector3.zero;
            float total = 0;
            for (int corner = 0; corner < 8; ++corner)
            {
                var step = new Vector3Int(corner & 1, corner >> 1 & 1, corner >> 2);
                Vector3Int coord = cell + step;
                if ((uint)coord.x >= 8 || (uint)coord.y >= 4 || (uint)coord.z >= 8) continue;
                int i = Index(coord);
                var p = data[i];
                if (!p.Active || !p.Published || ((Vector3)step + p.Offset - fraction).magnitude > visibleRadius) continue;
                float w = (step.x == 0 ? 1 - fraction.x : fraction.x) *
                    (step.y == 0 ? 1 - fraction.y : fraction.y) * (step.z == 0 ? 1 - fraction.z : fraction.z);
                color += w * (Vector3)atlas.Read(i, 3, 3);
                total += w;
            }
            if (total == 0) return false;
            color /= total * Mathf.PI;
            return true;
        }
    }
}
