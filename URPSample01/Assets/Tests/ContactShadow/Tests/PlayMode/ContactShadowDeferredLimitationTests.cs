#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace ContactShadowTests.PlayMode
{
    public sealed class ContactShadowDeferredLimitationTests : ContactShadowRenderFixture
    {
        [UnityTest]
        public IEnumerator AdditionalShadowsDisabledOnPipeline()
        {
            yield return Open(RenderingMode.Deferred, additionalShadows: false);
            // A dead shader hook or a disabled feature must not satisfy the negative solely by returning black.
            Isolate(0, LightShadows.None);
            yield return CompareVolume("D10-asset-off-main", p => Check(p, true));
            Isolate(1, LightShadows.Hard);
            yield return CompareVolume("D10-asset-off-point", p => Check(p, false));
        }

        [UnityTest]
        public IEnumerator ShadowlessPointCannotBorrowSpotsShadowVariant()
        {
            yield return Open(RenderingMode.Deferred);
            Lights[0].enabled = false;
            Lights[1].shadows = Lights[2].shadows = LightShadows.Hard;
            MarkOnly(1);
            yield return CompareVolume("D10-point-casts", p => Check(p, true));
            Lights[1].shadows = LightShadows.None;
            yield return CompareVolume("D10-point-none-spot-casts", p => Check(p, false));
            MarkOnly(2);
            yield return CompareVolume("D10-spot-casts-point-none", p => Check(p, true));
            Lights[2].shadows = LightShadows.None;
            yield return CompareVolume("D10-all-none", p => Check(p, false));
        }

        static void Check(ContactShadowImagePair pair, bool expectDark)
        {
            var contact = pair.Measure("ground-behind-blocker", Contact);
            var full = pair.Measure("whole-frame", Full);
            pair.Record(false, contact, full);
            if (expectDark) pair.RequireDark(contact); else pair.RequireEqual(full);
        }
    }
}
#endif
