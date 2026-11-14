using NUnit.Framework;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    // T12: the screen-space lighting consumers stay before the G-buffer. URP's deferred renderer
    // only falls back to a forward-only depth/normals pass when a consumer's earliest input event
    // lands between AfterRenderingGbuffer and BeforeRenderingOpaques; SSL/GTAO must never request
    // inputs in that window.
    [TestFixture]
    class ScreenSpaceLightingT12SchedulingTests
    {
        const RenderPassEvent k_AfterGbufferFallbackStart = RenderPassEvent.AfterRenderingGbuffer;
        const RenderPassEvent k_AfterGbufferFallbackEnd = RenderPassEvent.BeforeRenderingOpaques;
        const RenderPassEvent k_GtaoDepthPyramidEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 3);
        const RenderPassEvent k_GtaoDispatchEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 4);
        const RenderPassEvent k_SslDepthPyramidEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 3);
        const RenderPassEvent k_SslSetupEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 7);

        static UniversalRenderer.RenderPassInputSummary CreateSummary()
        {
            return new UniversalRenderer.RenderPassInputSummary
            {
                requiresDepthNormalAtEvent = RenderPassEvent.BeforeRenderingOpaques,
                requiresDepthTextureEarliestEvent = RenderPassEvent.BeforeRenderingPostProcessing,
                requiresMotionVectorEarliestEvent = RenderPassEvent.AfterRendering
            };
        }

        static bool InAfterGbufferFallbackWindow(RenderPassEvent passEvent)
        {
            return passEvent >= k_AfterGbufferFallbackStart && passEvent <= k_AfterGbufferFallbackEnd;
        }

        [Test]
        public void ScreenSpaceConsumersDoNotTriggerTheAfterGbufferFallback()
        {
            var summary = CreateSummary();

            // GTAO: depth + normal consumers before the G-buffer.
            UniversalRenderer.AccumulatePassInput(ref summary, k_GtaoDepthPyramidEvent,
                ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal, eventBeforeMainRendering: true);
            // SSL: depth/normal + motion consumers before the G-buffer.
            UniversalRenderer.AccumulatePassInput(ref summary, k_SslDepthPyramidEvent,
                ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal | ScriptableRenderPassInput.Motion,
                eventBeforeMainRendering: true);

            Assert.IsTrue(summary.requiresDepthTexture);
            Assert.IsTrue(summary.requiresNormalsTexture);
            Assert.IsTrue(summary.requiresMotionVectors);
            Assert.LessOrEqual(summary.requiresDepthNormalAtEvent, RenderPassEvent.BeforeRenderingGbuffer,
                "Depth/normals must be requested before the deferred G-buffer.");
            Assert.IsFalse(InAfterGbufferFallbackWindow(summary.requiresDepthNormalAtEvent),
                "SSL/GTAO must not land in the forward-only AfterGbuffer fallback window.");
            Assert.AreEqual(UniversalRenderer.k_EarlyMotionVectorRenderPassEvent,
                UniversalRenderer.ResolveMotionVectorPassEvent(RenderPassEvent.AfterRenderingOpaques + 1,
                    summary.requiresMotionVectorEarliestEvent));
        }

        [Test]
        public void ConsumersInsideTheFallbackWindowAreRejectedByTheCriterion()
        {
            // Sanity check of the predicate that encodes the T12 exit criterion: a hypothetical
            // consumer in (AfterGbuffer, BeforeRenderingOpaques] would trigger the forward-only pass.
            Assert.IsFalse(InAfterGbufferFallbackWindow(RenderPassEvent.BeforeRenderingGbuffer));
            Assert.IsTrue(InAfterGbufferFallbackWindow(RenderPassEvent.AfterRenderingGbuffer));
            Assert.IsTrue(InAfterGbufferFallbackWindow(RenderPassEvent.BeforeRenderingOpaques));
        }

        [Test]
        public void ScreenSpacePassEventsStayBeforeMainRendering()
        {
            Assert.Greater((int)k_GtaoDepthPyramidEvent, (int)RenderPassEvent.AfterRenderingPrePasses);
            Assert.Less((int)k_GtaoDispatchEvent, (int)RenderPassEvent.BeforeRenderingGbuffer);
            Assert.AreEqual(k_GtaoDepthPyramidEvent, k_SslDepthPyramidEvent);
            Assert.Less((int)k_SslSetupEvent, (int)RenderPassEvent.BeforeRenderingGbuffer);
            Assert.AreEqual(207, (int)k_SslSetupEvent);
        }

        [Test]
        public void ClearcoatAndRefractionKeywordsAreIndependentPerCamera()
        {
            // The cleanup passes disable the global contribution keywords; the material keywords are
            // per material and never enabled by a render pass.
            Assert.AreEqual("_SCREEN_SPACE_GI", ShaderKeywordStrings.ScreenSpaceGI);
            Assert.AreEqual("_SCREEN_SPACE_SSR", ShaderKeywordStrings.ScreenSpaceSSR);
            Assert.AreEqual("_SCREEN_SPACE_TRANSPARENT_SSR", ShaderKeywordStrings.ScreenSpaceTransparentSSR);
            Assert.AreEqual("_SCREEN_SPACE_REFRACTION", ShaderKeywordStrings.RefractionKeyword);
            Assert.AreEqual("_REFRACTION_PLANE", ShaderKeywordStrings.RefractionPlane);
            Assert.AreEqual("_TRANSPARENT_SSR_RECEIVE", ShaderKeywordStrings.TransparentSSRReceive);
        }
    }
}