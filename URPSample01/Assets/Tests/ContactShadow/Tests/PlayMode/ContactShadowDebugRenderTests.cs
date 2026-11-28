#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace ContactShadowTests.PlayMode
{
    public sealed class ContactShadowDebugRenderTests : ContactShadowRenderFixture
    {
        // Change the live T1 settings, not the serialized renderer asset. Restore even on assertion failure.
        internal sealed class DebugSettings : IDisposable
        {
            readonly object settings, originalMode, originalSlot;
            readonly FieldInfo mode, slot;
            public DebugSettings(ScreenSpaceContactShadowsRendererFeature feature)
            {
                settings = feature.GetType().GetField("m_Settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(feature);
                mode = settings.GetType().GetField("debugMode", BindingFlags.NonPublic | BindingFlags.Instance);
                slot = settings.GetType().GetField("debugSlot", BindingFlags.NonPublic | BindingFlags.Instance);
                originalMode = mode.GetValue(settings);
                originalSlot = slot.GetValue(settings);
            }
            public void Select(string name, int index = 0)
            {
                mode.SetValue(settings, Enum.Parse(mode.FieldType, name));
                slot.SetValue(settings, index);
            }
            public void Dispose()
            {
                mode.SetValue(settings, originalMode);
                slot.SetValue(settings, originalSlot);
            }
        }

        static void Save(string name, Color32[] pixels)
        {
            var args = Environment.GetCommandLineArgs();
            int flag = Array.IndexOf(args, "-contactShadowEvidence");
            string root = Path.Combine(Application.dataPath, "../Library/ContactShadowEvidence");
            if (flag >= 0)
            {
                Assert.That(flag + 1 < args.Length && !args[flag + 1].StartsWith("-"), "Missing evidence directory");
                root = args[flag + 1];
            }
            var dir = Path.Combine(Path.GetFullPath(root), SystemInfo.graphicsDeviceType.ToString(), "Debug");
            Directory.CreateDirectory(dir);
            var image = new Texture2D(W, H, TextureFormat.RGBA32, false, true);
            try
            {
                image.SetPixels32(pixels);
                image.Apply(false);
                var path = Path.Combine(dir, name + ".png");
                File.WriteAllBytes(path, image.EncodeToPNG());
                TestContext.WriteLine(path);
            }
            finally { UnityEngine.Object.Destroy(image); }
        }

        static int Count(RectInt rect, Color32[] pixels, Predicate<Color32> match)
        {
            int count = 0;
            foreach (var p in rect.allPositionsWithin)
                if (match(pixels[p.x + p.y * W])) ++count;
            return count;
        }

        static bool Black(Color32 c) => c.r == 0 && c.g == 0 && c.b == 0;
        static bool White(Color32 c) => c.r == 255 && c.g == 255 && c.b == 255;

        [UnityTest]
        public IEnumerator DebugModesDecodeTheSameContactPixels()
        {
            yield return Open(RenderingMode.Forward);
            using var debug = new DebugSettings(Feature);
            MarkOnly(0);
            // Floor is 5 m away. A 10 m fade-in gives a mid-grey fade on its hit pixels,
            // making a shader which accidentally draws Mask for Fade fail unambiguously.
            Settings.fadeInDistance.Override(10);
            yield return null;
            debug.Select("Mask");
            var mask = Capture();
            Save("Mask", mask);
            debug.Select("Fade");
            var fade = Capture();
            Save("Fade", fade);
            debug.Select("Slot", 0);
            var slot0 = Capture();
            Save("Slot0", slot0);
            debug.Select("Slot", 1);
            var empty = Capture();
            debug.Select("Raw");
            var raw = Capture();
            Save("Raw", raw);

            Assert.That(Count(Contact, mask, White), Is.GreaterThan(30), "No contact behind blocker");
            Assert.That(Count(Facing, mask, Black), Is.EqualTo(Facing.width * Facing.height));
            Assert.That(Count(Full, empty, Black), Is.EqualTo(W * H), "Unallocated bit 1 must be black");
            int errors = 0;
            foreach (var p in Full.allPositionsWithin)
            {
                int i = p.x + p.y * W;
                bool hit = White(mask[i]);
                var f = fade[i];
                var r = raw[i];
                // Linear RGBA8 target: Raw stores little-endian mask bytes and the fade byte.
                if ((!hit && !Black(mask[i])) || (hit ? !White(slot0[i]) : !Black(slot0[i])) ||
                    r.r != (hit ? 1 : 0) || r.g != 0 || r.b != 0 || r.a != f.r ||
                    f.r != f.g || f.g != f.b || (!hit && !Black(f)) ||
                    (hit && Contact.Contains(p) && Math.Abs(f.r - 127) > 2)) ++errors;
            }
            Assert.That(errors, Is.Zero, "Pixels inconsistent with slot 0, packed bytes or the 50% floor fade");

            // Put the point light on the opposite side so bit 1 is not interchangeable with bit 0.
            var blocker = Named<Renderer>("Blocker").transform.position;
            var pos = Lights[1].transform.position;
            pos.x = 2 * blocker.x - pos.x;
            Lights[1].transform.position = pos;
            MarkOnly(1);
            yield return null;
            debug.Select("Mask");
            var pointOnly = Capture();
            Markers[0].castContactShadows = true;
            yield return null;
            debug.Select("Slot", 1);
            var slot1 = Capture();
            Save("Slot1", slot1);
            Assert.That(Count(Facing, pointOnly, White), Is.GreaterThan(30), "Opposing point light must cast a distinct shadow");
            AssertSame("point-only-mask-vs-allocated-slot-1", pointOnly, slot1);
        }

        void AssertSame(string label, Color32[] reference, Color32[] actual)
        {
            var pair = Pair(label, reference, actual);
            pair.RequireEqual(pair.Measure(label, Full));
        }

        [UnityTest]
        public IEnumerator ConsecutiveCamerasUseTheirOwnDebugImage()
        {
            yield return Open(RenderingMode.Forward);
            Isolate(0, LightShadows.None);
            using var debug = new DebugSettings(Feature);
            var other = Named<Camera>("ContactCameraMSAA");
            other.CopyFrom(View);
            other.enabled = false;
            other.targetTexture = Target;
            // Translate vertically by 40 floor pixels at the fixture's 5 m receiver depth.
            const int offset = 40;
            float floorPixel = 2f * 5f * Mathf.Tan(View.fieldOfView * .5f * Mathf.Deg2Rad) / H;
            other.transform.SetPositionAndRotation(View.transform.position - View.transform.up * (offset * floorPixel), View.transform.rotation);
            other.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.EveryFrame);
            var cameraData = other.GetUniversalAdditionalCameraData();
            cameraData.volumeLayerMask = 0;
            debug.Select("Off");
            yield return null;
            var inactiveControl = Capture(other);
            cameraData.volumeLayerMask = View.GetUniversalAdditionalCameraData().volumeLayerMask;
            debug.Select("Mask");
            yield return null;
            // No yield between these renders. The translated view must move the contact
            // to the predicted band, not merely return a different or blank image.
            var a = Capture();
            var b = Capture(other);
            var again = Capture();
            cameraData.volumeLayerMask = 0;
            var inactive = Capture(other);
            Save("CameraA", a);
            Save("CameraB", b);
            Save("CameraBInactive", inactive);
            var shifted = new RectInt(Contact.x, Contact.y + offset, Contact.width, Contact.height);
            Assert.That(Count(Contact, a, White), Is.GreaterThan(30));
            Assert.That(Count(shifted, a, Black), Is.EqualTo(shifted.width * shifted.height));
            Assert.That(Count(shifted, b, White), Is.GreaterThan(30));
            Assert.That(Count(Contact, b, Black), Is.EqualTo(Contact.width * Contact.height));
            AssertSame("A-after-B", a, again);
            AssertSame("inactive-B-after-active-A", inactiveControl, inactive);
        }
    }
}
#endif
