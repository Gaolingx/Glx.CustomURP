#if UNITY_EDITOR
using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace ContactShadowTests.PlayMode
{
    // Measure the exact RGBA8 camera output, not an intermediate mask or a post-cleanup shader global.
    internal sealed class ContactShadowImagePair
    {
        [Serializable]
        internal struct Region
        {
            public string name;
            public int x, y, width, height, changedRGBA, darkened, brightened;
            public float meanOffLuma, meanOnLuma;
        }
        [Serializable]
        sealed class Report
        {
            public string api, gpu, unity, comparison;
            public Region[] regions;
        }
        readonly Color32[] off, on;
        readonly int width, height;
        readonly string label;
        internal ContactShadowImagePair(string label, Color32[] off, Color32[] on, int width, int height)
        {
            this.label = label;
            this.off = off;
            this.on = on;
            this.width = width;
            this.height = height;
        }
        // Byte luminance. A one-code-value change is counted for exactness but cannot alone prove shadowing.
        static float Luma(Color32 c) => .2126f * c.r + .7152f * c.g + .0722f * c.b;
        internal Region Measure(string name, RectInt roi)
        {
            var r = new Region { name = name, x = roi.x, y = roi.y, width = roi.width, height = roi.height };
            foreach (var p in roi.allPositionsWithin)
            {
                int i = p.x + p.y * width;
                var a = off[i]; var b = on[i];
                float la = Luma(a), lb = Luma(b);
                if (a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a) r.changedRGBA++;
                if (la - lb >= 2) r.darkened++;
                if (lb - la >= 2) r.brightened++;
                r.meanOffLuma += la;
                r.meanOnLuma += lb;
            }
            r.meanOffLuma /= roi.width * roi.height;
            r.meanOnLuma /= roi.width * roi.height;
            return r;
        }
        internal void RequireDark(Region r)
        {
            Assert.That(r.darkened, Is.GreaterThanOrEqualTo(Mathf.CeilToInt(r.width * r.height * .08f)),
                $"{label}/{r.name}: only {r.darkened} pixels darkened by >=2 luma codes.");
            Assert.That(r.meanOffLuma - r.meanOnLuma, Is.GreaterThan(.5f), $"{label}/{r.name}: mean drop in byte luminance");
        }
        internal void RequireEqual(Region r) => Assert.That(r.changedRGBA, Is.Zero, $"{label}/{r.name}: changed RGBA8 pixels");

        internal void Record(bool images, params Region[] regions)
        {
            string json = JsonUtility.ToJson(new Report { api = SystemInfo.graphicsDeviceType.ToString(),
                gpu = SystemInfo.graphicsDeviceName, unity = Application.unityVersion, comparison = label, regions = regions }, true);
            TestContext.WriteLine(json);
            var args = Environment.GetCommandLineArgs();
            int flag = Array.IndexOf(args, "-contactShadowEvidence");
            if (flag < 0) return;
            Assert.That(flag + 1 < args.Length && !args[flag + 1].StartsWith("-"), "Missing -contactShadowEvidence directory");
            string prefix = Path.Combine(Path.GetFullPath(args[flag + 1]), SystemInfo.graphicsDeviceType.ToString(), label);
            Directory.CreateDirectory(Path.GetDirectoryName(prefix));
            File.WriteAllText(prefix + ".json", json);
            if (!images) return;
            var diff = new Color32[on.Length];
            for (int i = 0; i < diff.Length; ++i)
            {
                Color32 a = off[i], b = on[i];
                diff[i] = new Color32((byte)Math.Abs(a.r - b.r), (byte)Math.Abs(a.g - b.g), (byte)Math.Abs(a.b - b.b), 255);
            }
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                void Write(string suffix, Color32[] pixels)
                {
                    tex.SetPixels32(pixels);
                    tex.Apply(false);
                    File.WriteAllBytes(prefix + suffix, tex.EncodeToPNG());
                }
                Write("-off.png", off); Write("-on.png", on); Write("-diff.png", diff);
            }
            finally { UnityEngine.Object.Destroy(tex); }
        }
    }
}
#endif
