using System;
using NUnit.Framework;
using UnityEngine;
using Ray = UnityEditor.Rendering.Universal.Tests.ProbeMathReference.Ray;
using static UnityEditor.Rendering.Universal.Tests.ProbeMathReference;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    public class RayTracingProbeMathTests
    {
        static readonly Vector3 A = new Vector3(2, .375f, 1.25f), B = new Vector3(.25f, 3, .5f);
        const int Probe = 18;

        // At (n/2,n/2) the oct texel normal is normalize(1,1,n-2).
        // 24 copies of four unit directions; dot products 1, 1/2, 1/4, -1.
        // Thus normalized positive weights are 4:2:1, independent of convolution code.
        static Ray[] Rays(int n, Func<int, Kind> classify)
        {
            Vector3 normal = new Vector3(1, 1, n - 2).normalized;
            Vector3 tangent = new Vector3(1, -1, 0).normalized;
            var rays = new Ray[128];
            float[] cos = { 1, .5f, .25f, -1 };
            for (int i = 0; i < rays.Length; ++i)
            {
                int j = i % 4;
                rays[i] = new Ray {
                    Direction = i < 32 ? normal : cos[j] * normal + Mathf.Sqrt(1 - cos[j] * cos[j]) * tangent,
                    Category = i < 32 ? Kind.Hit : classify(j),
                    Radiance = i < 32 ? Vector3.one * 500 : j == 0 ? A : B,
                    Distance = i < 32 ? 29 : 3.7f
                };
            }
            return rays;
        }

        static void Near(float actual, double expected)
        {
            Assert.That(float.IsNaN(actual) || float.IsInfinity(actual), Is.False);
            Assert.That(Math.Abs(actual - expected) / Math.Max(Math.Abs(expected), .01), Is.LessThanOrEqualTo(.005));
        }

        static void Near(Vector3 actual, Vector3 expected)
        {
            for (int c = 0; c < 3; ++c) Near(actual[c], expected[c]);
        }

        static Vector4 Readback(ProbeHalfAtlas atlas, int p, int x, int y)
        {
            int address = atlas.Address(p, x, y);
            string[] words = new string[atlas.Channels];
            for (int c = 0; c < words.Length; ++c) words[c] = atlas.Words[address + c].ToString("X4");
            Vector4 value = atlas.Read(p, x, y);
            TestContext.Out.WriteLine($"probe={p} xy={x},{y} wordAddress={address} fp16={string.Join("/", words)} decoded={value.ToString("F6")}");
            return value;
        }

        static Vector4 Run(ProbeHalfAtlas atlas, Ray[] rays, bool valid, int[] counts, int skyReads, Func<Ray, Vector3> sky = null)
        {
            int t = atlas.Interior / 2, calls = 0;
            bool result = Convolve(atlas, Probe, t, t, rays, r => { ++calls; return sky == null ? A : sky(r); }, out var log);
            TestContext.Out.WriteLine($"n={atlas.Interior} cos(32..127)=[1,.5,.25,-1]x24; P=maintenance; mask={log.Mask}; HSBTMU={string.Join(",", log.Counts)}; skyReads={calls}; valid={result}");
            var value = Readback(atlas, Probe, t, t);
            CollectionAssert.AreEqual(counts, log.Counts);
            Assert.That(calls, Is.EqualTo(skyReads));
            Assert.That(log.SkyReads, Is.EqualTo(calls));
            Assert.That(result, Is.EqualTo(valid));
            return value;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SurfaceField(bool twoColor)
        {
            var rays = Rays(6, j => Kind.Hit);
            if (!twoColor) for (int i = 32; i < 128; ++i) rays[i].Radiance = A;
            TestContext.Out.WriteLine($"L0={A} L1={(twoColor ? B : A)} prefix L=500");
            Vector3 e = Run(new ProbeHalfAtlas(6, 4), rays, true, new[] {96,0,0,0,0,0}, 0);
            Near(e / Mathf.PI, twoColor ? (4 * A + 3 * B) / 7 : A);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DistanceMoments(bool varied)
        {
            var rays = Rays(14, j => Kind.Hit);
            if (varied) for (int i = 32; i < 128; i += 4) rays[i].Distance = 1.5f;
            TestContext.Out.WriteLine($"distance=[{(varied ? 1.5 : 3.7)},3.7,3.7,3.7]; prefix d=29");
            Vector4 m = Run(new ProbeHalfAtlas(14, 2), rays, true, new[] {96,0,0,0,0,0}, 0);
            Near(m.x, varied ? (4 * 1.5 + 3 * 3.7) / 7 : 3.7);
            Near(m.y, varied ? (4 * 2.25 + 3 * 13.69) / 7 : 13.69);
        }

        [Test]
        public void SkyIsAMeasuredSampleForDistanceMoments()
        {
            // Class cos .5 is sky at the moment cap; the other classes stay hits at 3.7.
            var rays = Rays(14, j => j == 1 ? Kind.Sky : Kind.Hit);
            for (int i = 32; i < 128; ++i)
                if (rays[i].Category == Kind.Sky) rays[i].Distance = 1.5f;
            Vector4 m = Run(new ProbeHalfAtlas(14, 2), rays, true, new[] {72,24,0,0,0,0}, 0);
            Near(m.x, (4*3.7f + 2*1.5f + 3.7f) / 7);
            Near(m.y, (4*13.69f + 2*2.25f + 13.69f) / 7);
        }

        [Test]
        public void FeedbackAddsCappedPreviousIrradianceOnce()
        {
            var rays = Rays(6, j => Kind.Hit);
            var term = Feedback(new Vector3(1, .25f, 0), new Vector3(4, 6, 2));
            for (int i = 32; i < 128; ++i) rays[i].Radiance = A + term;
            TestContext.Out.WriteLine($"direct={A} albedo=(1,.25,0) cap=.9 Eprev=(4,6,2)");
            Vector3 e = Run(new ProbeHalfAtlas(6, 4), rays, true, new[] {96,0,0,0,0,0}, 0);
            Near(e, Mathf.PI * A + new Vector3(3.6f, 1.5f, 0));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void KnownSky(bool window)
        {
            var rays = Rays(6, j => !window || j == 1 ? Kind.Sky : Kind.Blocked);
            TestContext.Out.WriteLine($"sky L={A}; blocked L=0; window={window}");
            Vector3 e = Run(new ProbeHalfAtlas(6, 4), rays, true,
                window ? new[] {0,24,72,0,0,0} : new[] {0,96,0,0,0,0}, window ? 24 : 72);
            Near(e / Mathf.PI, window ? 2 * A / 7 : A);
        }

        [Test]
        public void UnresolvedIsNeitherSkyNorMeasuredBlack()
        {
            var atlas = new ProbeHalfAtlas(6, 4);
            atlas.Store(Probe, 3, 3, new Vector4(5, 4, 3, 1));
            var unknown = Rays(6, j => j == 0 ? Kind.Truncated : j == 1 ? Kind.Missing : Kind.Unserved);
            Vector4 kept = Run(atlas, unknown, false, new[] {0,0,0,24,24,48}, 0);
            Assert.That(kept, Is.EqualTo(new Vector4(5, 4, 3, 1)));
            for (int i = 32; i < 128; i += 4) unknown[i].Category = Kind.Sky;
            Near((Vector3)Run(atlas, unknown, true, new[] {0,24,0,0,24,48}, 24) / Mathf.PI, A);
            Vector4 black = Run(atlas, Rays(6, j => Kind.Sky), true, new[] {0,96,0,0,0,0}, 72, r => Vector3.zero);
            Assert.That(black, Is.EqualTo(new Vector4(0, 0, 0, 1)));
        }

        [TestCase(6, 4)]
        [TestCase(14, 2)]
        public void OctahedralInteriorAndSeamGutters(int n, int channels)
        {
            var atlas = new ProbeHalfAtlas(n, channels);
            // Probe 9 is the second tile on the second atlas row. Seed different values
            // at every interior texel and channel, exactly representable in binary16.
            for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
            {
                float v = y * n + x + 1;
                atlas.Store(9, x, y, new Vector4(v, -v, 2*v, .5f*v));
                Vector2 uv = ProbeHalfAtlas.Oct(ProbeHalfAtlas.Normal(n, x, y));
                Assert.That((uv.x + 1) * n / 2, Is.EqualTo(x + .5f).Within(.0001));
                Assert.That((uv.y + 1) * n / 2, Is.EqualTo(y + .5f).Within(.0001));
            }
            atlas.FillGutter(9);
            int pitch = n + 2, width = pitch * 8;
            void Check(int tx, int ty, int label)
            {
                int addr = ((pitch + ty) * width + pitch + tx) * channels;
                float[] expected = {label, -label, 2*label, .5f*label};
                for (int c = 0; c < channels; ++c)
                    Assert.That(Mathf.HalfToFloat(atlas.Words[addr+c]), Is.EqualTo(expected[c]));
            }
            var gutter = ProbeHalfAtlas.Gutter(n);
            Assert.That(gutter.Length, Is.EqualTo(4*n + 4));
            var dests = new System.Collections.Generic.HashSet<int>();
            foreach (var e in gutter)
            {
                Assert.That(e.dx == 0 || e.dy == 0 || e.dx == n + 1 || e.dy == n + 1, Is.True,
                    $"dest {e.dx},{e.dy} is not a border texel");
                Assert.That(dests.Add(e.dy * (n + 2) + e.dx), Is.True, $"dest {e.dx},{e.dy} filled twice");
                Check(e.dx, e.dy, (e.sy - 1) * n + (e.sx - 1) + 1);
            }
            // The seam is not a tile-edge mirror: the south-west corner takes the north-east
            // interior texel, and the west-south border takes north-west.
            Check(0, 0, n*n);
            Check(0, 1, n*(n-1) + 1);
            Readback(atlas, 9, -1, -1); Readback(atlas, 9, n, n/2);
            Assert.That(ProbeHalfAtlas.Oct(new Vector3(-1, 2, -1)), Is.EqualTo(new Vector2(-.5f, .75f)));
        }

        [Test]
        public void NonfinitePackedChannelsBecomeZero()
        {
            var atlas = new ProbeHalfAtlas(6, 4);
            atlas.Store(0, 0, 0, new Vector4(float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1));
            Assert.That(Readback(atlas, 0, 0, 0), Is.EqualTo(new Vector4(0, 0, 0, 1)));
        }

        [Test]
        public void GridEdgeDoesNotWrapIntoNextRow()
        {
            var atlas = new ProbeHalfAtlas(6, 4);
            var data = new Data[256];
            foreach (int i in new[] {7, 8})
            {
                data[i] = new Data { Published = true, Active = true };
                atlas.Store(i, 3, 3, Vector4.one * Mathf.PI * (i == 7 ? 2 : 50));
            }
            Assert.That(Query(atlas, data, new Vector3Int(7,0,0), new Vector3(.25f,0,0), 1.1f, out var value), Is.True);
            Near(value, Vector3.one * 2);
        }

        [Test]
        public void EightNeighborValidityAndRelocation()
        {
            var atlas = new ProbeHalfAtlas(6, 4);
            var data = new Data[256];
            var cell = new Vector3Int(2, 1, 3);
            var fraction = new Vector3(.25f, .5f, .5f);
            int[] indices = {106,107,114,115,138,139,146,147};
            // All eight get data; only 106 (lit), 107 (black), 114 (relocated) survive.
            for (int k = 0; k < 8; ++k)
            {
                int i = indices[k];
                data[i] = new Data { Published = k != 3, Active = k < 4 };
                atlas.Store(i, 3, 3, Vector4.one * Mathf.PI * (k == 0 ? 2 : k == 1 ? 0 : k == 2 ? 6 : 50));
            }
            // Radius 1.1 includes every unrelocated corner. Move 114 beyond it.
            void Check(bool expectedValid, float expected)
            {
                bool valid = Query(atlas, data, cell, fraction, 1.1f, out var value);
                TestContext.Out.WriteLine($"cell={cell} f={fraction} offset114={data[114].Offset} valid={valid} E/pi={value.ToString("F6")}");
                Assert.That(valid, Is.EqualTo(expectedValid));
                Near(value, Vector3.one * expected);
            }
            foreach (int i in indices) Readback(atlas, i, 3, 3);
            Check(true, 24f/7); // weights 3:1:3
            data[114].Offset = new Vector3(0, .45f, 0);
            Check(true, 1.5f); // weights 3:1, not lit-only 2
            data[106].Active = false;
            Check(true, 0);
            data[107].Published = false;
            Check(false, 0);
        }
    }
}
