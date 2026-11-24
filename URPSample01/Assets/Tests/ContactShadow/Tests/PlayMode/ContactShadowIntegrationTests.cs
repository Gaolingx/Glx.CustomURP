#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace ContactShadowTests.PlayMode
{
    public sealed class ContactShadowIntegrationTests : ContactShadowRenderFixture
    {
        // Independent NUnit cases: one broken path/light still leaves evidence for all the others.
        static readonly RenderingMode[] Modes = { RenderingMode.Forward, RenderingMode.ForwardPlus, RenderingMode.Deferred };
        [UnityTest] public IEnumerator Main([ValueSource(nameof(Modes))] RenderingMode mode) => LightPair(mode, 0);
        [UnityTest] public IEnumerator Point([ValueSource(nameof(Modes))] RenderingMode mode) => LightPair(mode, 1);
        [UnityTest] public IEnumerator Spot([ValueSource(nameof(Modes))] RenderingMode mode) => LightPair(mode, 2);

        [UnityTest]
        public IEnumerator BaselineScene([ValueSource(nameof(Modes))] RenderingMode mode)
        {
            yield return Open(mode);
            // This is the persistent, all-three-lights baseline, not a collage of isolated renders.
            foreach (var marker in Markers) Assert.That(marker.isActiveAndEnabled && marker.castContactShadows, Is.True);
            yield return CompareVolume("Baseline", pair => CheckRegions(pair, true));
        }

        static void CheckRegions(ContactShadowImagePair pair, bool images)
        {
            var behind = pair.Measure("ground-behind-blocker", Contact);
            var facing = pair.Measure("ground-light-facing", Facing);
            var regions = new List<ContactShadowImagePair.Region> { behind, facing };
            for (int i = 0; i < Panels.Length; ++i) regions.Add(pair.Measure(Panels[i], Panel(i)));
            pair.Record(images, regions.ToArray());
            pair.RequireDark(behind);
            pair.RequireEqual(facing);
            for (int i = 2; i < regions.Count; ++i) pair.RequireEqual(regions[i]);
        }

        IEnumerator LightPair(RenderingMode mode, int index)
        {
            yield return Open(mode);
            Isolate(index, index != 0 && mode == RenderingMode.Deferred ? LightShadows.Hard : LightShadows.None);
            Feature.SetActive(false);
            yield return null;
            var noFeature = Capture();
            Feature.SetActive(true);
            yield return CompareVolume(Lights[index].name, pair => CheckRegions(pair, false));
            Settings.enabled.Override(false);
            yield return null;
            var inactive = Pair(Lights[index].name + "-inactive", noFeature, Capture());
            var full = inactive.Measure("feature-disabled-vs-volume-disabled-after-active", Full);
            inactive.Record(false, full);
            inactive.RequireEqual(full);
        }
    }

    public sealed class ContactShadowReceiveShadowsTests : ContactShadowRenderFixture
    {
        static readonly RenderingMode[] Modes = { RenderingMode.Forward, RenderingMode.ForwardPlus, RenderingMode.Deferred };
        [UnityTest]
        public IEnumerator MaterialSwitchControlsSameReceiver([ValueSource(nameof(Modes))] RenderingMode mode)
        {
            yield return Open(mode);
            Isolate(0, LightShadows.None);
            var receiver = Named<Renderer>("ReceiverOff");
            Assert.That(receiver.sharedMaterial.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF"), Is.True);
            // Hold geometry, camera, blocker and lights fixed. A genuinely shaded positive control on the
            // same pixels prevents an untouched patch of floor from passing as a ReceiveOff success.
            foreach (bool receive in new[] { false, true })
            {
                if (receive) receiver.sharedMaterial = Named<Renderer>("Ground").sharedMaterial;
                yield return CompareVolume("Receive-" + receive, pair =>
                {
                    var panel = pair.Measure("receiver-under-its-own-blocker", Panel(0));
                    pair.Record(false, panel);
                    if (receive) pair.RequireDark(panel); else pair.RequireEqual(panel);
                });
            }
        }
    }

    public sealed class ContactShadowRuntimeToggleTests : ContactShadowRenderFixture
    {
        [UnityTest]
        public IEnumerator MarkerVolumeFeatureAndNextCameraDoNotLeak()
        {
            yield return Open(RenderingMode.Forward);
            Isolate(0, LightShadows.None);
            // Same projection/target, second camera excludes the fixture Volume. Render it immediately
            // after an active camera as well as before, with no frame boundary between camera draws.
            var other = Named<Camera>("ContactCameraMSAA");
            other.CopyFrom(View);
            other.enabled = false;
            other.GetUniversalAdditionalCameraData().volumeLayerMask = 0;
            other.targetTexture = Target;
            Settings.enabled.Override(false);
            yield return null;
            var off = Capture();
            var otherOff = Capture(other);
            Settings.enabled.Override(true);
            yield return null;
            var active = Capture();
            var second = Pair("second-camera", otherOff, Capture(other));
            second.RequireEqual(second.Measure("inactive-camera-after-active", Full));
            var positive = Pair("runtime-enabled", off, active);
            positive.RequireDark(positive.Measure("behind", Contact));

            Markers[0].enabled = false;
            yield return null;
            CheckFrame("marker-disabled", off);
            Markers[0].enabled = true;
            yield return null;
            CheckFrame("marker-restored", active);
            Feature.SetActive(false);
            yield return null;
            CheckFrame("feature-disabled", off);
            Feature.SetActive(true);
            yield return null;
            CheckFrame("feature-restored", active);
            Settings.enabled.Override(false);
            yield return null;
            CheckFrame("volume-disabled", off);
        }

        void CheckFrame(string name, Color32[] expected)
        {
            var pair = Pair(name, expected, Capture());
            var frame = pair.Measure("whole-frame", Full);
            pair.Record(false, frame);
            pair.RequireEqual(frame);
        }
    }
}
#endif
