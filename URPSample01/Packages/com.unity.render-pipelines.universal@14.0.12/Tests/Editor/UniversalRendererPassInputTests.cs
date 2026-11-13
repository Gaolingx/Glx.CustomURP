using NUnit.Framework;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class UniversalRendererPassInputTests
    {
        // Mirrors the default schedule: motion vectors one event after the copy-depth event.
        const RenderPassEvent k_DefaultMotionEvent = RenderPassEvent.AfterRenderingOpaques + 1;
        // Screen-space lighting consumes motion here; see the pass table in the migration plan.
        const RenderPassEvent k_FirstSslConsumerEvent = (RenderPassEvent)203;

        static UniversalRenderer.RenderPassInputSummary CreateSummary()
        {
            return new UniversalRenderer.RenderPassInputSummary
            {
                requiresDepthNormalAtEvent = RenderPassEvent.BeforeRenderingOpaques,
                requiresDepthTextureEarliestEvent = RenderPassEvent.BeforeRenderingPostProcessing,
                requiresMotionVectorEarliestEvent = RenderPassEvent.AfterRendering
            };
        }

        [Test]
        public void EarlyMotionEventFitsBetweenDepthCopyAndGbuffer()
        {
            Assert.Greater(UniversalRenderer.k_EarlyMotionVectorRenderPassEvent, RenderPassEvent.AfterRenderingPrePasses,
                "Early motion must run after the prepass depth copy.");
            Assert.Less(UniversalRenderer.k_EarlyMotionVectorRenderPassEvent, RenderPassEvent.BeforeRenderingGbuffer,
                "Early motion must run before the deferred gbuffer and the opaque forward pass.");
            Assert.AreEqual((RenderPassEvent)202, UniversalRenderer.k_EarlyMotionVectorRenderPassEvent);
        }

        [Test]
        public void NoInputKeepsDefaultMotionSchedule()
        {
            var summary = CreateSummary();

            Assert.IsFalse(summary.requiresMotionVectors);
            Assert.AreEqual(RenderPassEvent.AfterRendering, summary.requiresMotionVectorEarliestEvent);
            Assert.AreEqual(k_DefaultMotionEvent,
                UniversalRenderer.ResolveMotionVectorPassEvent(k_DefaultMotionEvent, summary.requiresMotionVectorEarliestEvent));
        }

        [Test]
        public void MotionConsumerBeforeDefaultMovesMotionEarlier()
        {
            var summary = CreateSummary();
            UniversalRenderer.AccumulatePassInput(ref summary, k_FirstSslConsumerEvent, ScriptableRenderPassInput.Motion, false);

            Assert.IsTrue(summary.requiresMotionVectors);
            Assert.AreEqual(k_FirstSslConsumerEvent, summary.requiresMotionVectorEarliestEvent);
            Assert.AreEqual(UniversalRenderer.k_EarlyMotionVectorRenderPassEvent,
                UniversalRenderer.ResolveMotionVectorPassEvent(k_DefaultMotionEvent, summary.requiresMotionVectorEarliestEvent));
        }

        [Test]
        public void MotionConsumerAtDefaultEventKeepsDefaultSchedule()
        {
            var summary = CreateSummary();
            UniversalRenderer.AccumulatePassInput(ref summary, k_DefaultMotionEvent, ScriptableRenderPassInput.Motion, false);

            Assert.AreEqual(k_DefaultMotionEvent,
                UniversalRenderer.ResolveMotionVectorPassEvent(k_DefaultMotionEvent, summary.requiresMotionVectorEarliestEvent));
        }

        [Test]
        public void MotionConsumerAfterDefaultKeepsDefaultSchedule()
        {
            var summary = CreateSummary();
            UniversalRenderer.AccumulatePassInput(ref summary, RenderPassEvent.BeforeRenderingPostProcessing, ScriptableRenderPassInput.Motion, false);

            Assert.IsTrue(summary.requiresMotionVectors);
            Assert.AreEqual(RenderPassEvent.BeforeRenderingPostProcessing, summary.requiresMotionVectorEarliestEvent);
            Assert.AreEqual(k_DefaultMotionEvent,
                UniversalRenderer.ResolveMotionVectorPassEvent(k_DefaultMotionEvent, summary.requiresMotionVectorEarliestEvent));
        }

        [Test]
        public void EarliestMotionConsumerWins()
        {
            var summary = CreateSummary();
            UniversalRenderer.AccumulatePassInput(ref summary, (RenderPassEvent)206, ScriptableRenderPassInput.Motion, false);
            UniversalRenderer.AccumulatePassInput(ref summary, (RenderPassEvent)204, ScriptableRenderPassInput.Motion, false);
            UniversalRenderer.AccumulatePassInput(ref summary, (RenderPassEvent)207, ScriptableRenderPassInput.Motion, false);

            Assert.AreEqual((RenderPassEvent)204, summary.requiresMotionVectorEarliestEvent);
        }

        [Test]
        public void TemporalAaAloneDoesNotRequestEarlyMotion()
        {
            // Temporal AA sets requiresMotionVectors without injecting a consumer event.
            var summary = CreateSummary();
            summary.requiresMotionVectors = true;

            Assert.AreEqual(k_DefaultMotionEvent,
                UniversalRenderer.ResolveMotionVectorPassEvent(k_DefaultMotionEvent, summary.requiresMotionVectorEarliestEvent));
        }

        [Test]
        public void DepthConsumerBeforeMainRenderingRequestsPrepass()
        {
            var summary = CreateSummary();
            UniversalRenderer.AccumulatePassInput(ref summary, k_FirstSslConsumerEvent, ScriptableRenderPassInput.Depth, true);

            Assert.IsTrue(summary.requiresDepthTexture);
            Assert.IsTrue(summary.requiresDepthPrepass);
            Assert.IsFalse(summary.requiresNormalsTexture);
            Assert.AreEqual(k_FirstSslConsumerEvent, summary.requiresDepthTextureEarliestEvent);
            Assert.AreEqual(k_FirstSslConsumerEvent, summary.requiresDepthNormalAtEvent);
        }

        [Test]
        public void DepthConsumerAfterMainRenderingOnlyCopiesDepth()
        {
            var summary = CreateSummary();
            UniversalRenderer.AccumulatePassInput(ref summary, RenderPassEvent.BeforeRenderingPostProcessing, ScriptableRenderPassInput.Depth, false);

            Assert.IsTrue(summary.requiresDepthTexture);
            Assert.IsFalse(summary.requiresDepthPrepass);
            Assert.AreEqual(RenderPassEvent.BeforeRenderingPostProcessing, summary.requiresDepthTextureEarliestEvent);
        }

        [Test]
        public void NormalConsumerAlwaysRequestsDepthNormalPrepass()
        {
            var summary = CreateSummary();
            UniversalRenderer.AccumulatePassInput(ref summary, RenderPassEvent.BeforeRenderingOpaques, ScriptableRenderPassInput.Normal, false);

            Assert.IsTrue(summary.requiresNormalsTexture);
            Assert.IsTrue(summary.requiresDepthPrepass);
            Assert.AreEqual(RenderPassEvent.BeforeRenderingOpaques, summary.requiresDepthNormalAtEvent);
        }

        [Test]
        public void LaterNormalConsumerKeepsDefaultDepthNormalEvent()
        {
            var summary = CreateSummary();
            UniversalRenderer.AccumulatePassInput(ref summary, (RenderPassEvent)400, ScriptableRenderPassInput.Normal, false);

            Assert.IsTrue(summary.requiresNormalsTexture);
            Assert.AreEqual(RenderPassEvent.BeforeRenderingOpaques, summary.requiresDepthNormalAtEvent);
        }

        [Test]
        public void MotionDependenciesPullDepthBeforeTheMotionPass()
        {
            var summary = CreateSummary();
            UniversalRenderer.AccumulatePassInput(ref summary, RenderPassEvent.BeforeRenderingPostProcessing, ScriptableRenderPassInput.Depth, false);
            summary.requiresMotionVectors = true;

            var motionEvent = UniversalRenderer.ResolveMotionVectorPassEvent(k_DefaultMotionEvent, k_FirstSslConsumerEvent);
            Assert.AreEqual(UniversalRenderer.k_EarlyMotionVectorRenderPassEvent, motionEvent);

            UniversalRenderer.ApplyMotionVectorDependencies(ref summary, motionEvent);

            Assert.IsTrue(summary.requiresDepthTexture);
            Assert.AreEqual(UniversalRenderer.k_EarlyMotionVectorRenderPassEvent, summary.requiresDepthTextureEarliestEvent,
                "Depth must be available before camera motion vectors reproject it.");
            Assert.AreEqual(UniversalRenderer.k_EarlyMotionVectorRenderPassEvent, summary.requiresDepthNormalAtEvent);
        }

        [Test]
        public void DepthConsumerAfterMotionKeepsItsOwnEarliestEvent()
        {
            var summary = CreateSummary();
            UniversalRenderer.AccumulatePassInput(ref summary, RenderPassEvent.BeforeRenderingTransparents, ScriptableRenderPassInput.Depth, false);
            UniversalRenderer.ApplyMotionVectorDependencies(ref summary, k_DefaultMotionEvent);

            Assert.AreEqual(k_DefaultMotionEvent, summary.requiresDepthTextureEarliestEvent);
        }
    }
}
