using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    // Screen Space Lighting and GTAO consume one packed depth mip chain: whichever feature runs
    // first enqueues the shared build pass and the other binds the same attachment.
    [TestFixture]
    class ScreenSpaceDepthPyramidSharingTests
    {
        Camera m_Camera;
        UniversalRendererData m_RendererData;
        ScreenSpaceTestUtils.TestRenderer m_Renderer;
        GroundTruthAmbientOcclusionRendererFeature m_Gtao;
        ScreenSpaceLightingRendererFeature m_Ssl;
        VolumeStack m_Stack;
        VolumeStack m_PreviousStack;

        [SetUp]
        public void SetUp()
        {
            m_Camera = ScreenSpaceTestUtils.CreateCamera("ScreenSpaceDepthPyramidCamera");
            m_RendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            m_RendererData.debugShaders = new ScriptableRendererData.DebugShaderResources();
            m_Renderer = new ScreenSpaceTestUtils.TestRenderer(m_RendererData);
            m_Gtao = ScriptableObject.CreateInstance<GroundTruthAmbientOcclusionRendererFeature>();
            m_Ssl = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
            m_Stack = VolumeManager.instance.CreateStack();
            m_PreviousStack = VolumeManager.instance.stack;
            VolumeManager.instance.stack = m_Stack;
        }

        [TearDown]
        public void TearDown()
        {
            VolumeManager.instance.stack = m_PreviousStack;
            m_Stack?.Dispose();

            DisposeFeature(m_Gtao);
            DisposeFeature(m_Ssl);

            m_Renderer?.Dispose();
            if (m_RendererData != null)
                Object.DestroyImmediate(m_RendererData);
            ScreenSpaceTestUtils.DestroyCamera(m_Camera);
        }

        static void DisposeFeature(ScriptableRendererFeature feature)
        {
            if (feature == null)
                return;

            feature.Dispose();
            Object.DestroyImmediate(feature);
        }

        void AddBothFeatures(bool gtaoFirst)
        {
            var renderingData = ScreenSpaceTestUtils.CreateRenderingData(ScreenSpaceTestUtils.CreateCameraData(m_Camera));
            if (gtaoFirst)
            {
                m_Gtao.AddRenderPasses(m_Renderer, ref renderingData);
                m_Ssl.AddRenderPasses(m_Renderer, ref renderingData);
            }
            else
            {
                m_Ssl.AddRenderPasses(m_Renderer, ref renderingData);
                m_Gtao.AddRenderPasses(m_Renderer, ref renderingData);
            }
        }

        int PyramidEnqueues()
        {
            return m_Renderer.queuedPasses.FindAll(pass => pass == ScreenSpaceDepthPyramidSystem.sharedPass).Count;
        }

        [TestCase(true)]
        [TestCase(false)]
        public void BothFeaturesEnqueueThePyramidOnce(bool gtaoFirst)
        {
            m_Stack.GetComponent<ScreenSpaceGlobalIllumination>().enabled.Override(true);

            AddBothFeatures(gtaoFirst);

            Assert.AreEqual(1, PyramidEnqueues(), "The depth pyramid must be built once when both features are active.");
            Assert.Contains(m_Gtao.pass, m_Renderer.queuedPasses);
            Assert.Contains(m_Ssl.ssgiPass, m_Renderer.queuedPasses);
        }

        [Test]
        public void BothFeaturesBindTheSameAttachment()
        {
            m_Stack.GetComponent<ScreenSpaceGlobalIllumination>().enabled.Override(true);

            AddBothFeatures(gtaoFirst: true);

            Assert.IsTrue(m_Gtao.historyStore.TryGet(m_Camera, 0, out var gtaoHistory));
            Assert.IsTrue(m_Ssl.historyStore.TryGet(m_Camera, 0, out var sslHistory));
            Assert.AreSame(gtaoHistory.depthPyramidSource, sslHistory.depthPyramidSource,
                "Both features must consume the same packed depth mip chain.");
        }

        [Test]
        public void IdleSslDoesNotAffectTheGtaoPyramid()
        {
            AddBothFeatures(gtaoFirst: true);

            Assert.AreEqual(1, PyramidEnqueues());
            Assert.AreEqual(0, m_Ssl.historyStore.entryCount);
        }

        [Test]
        public void SslStillBuildsThePyramidWithoutGtao()
        {
            m_Stack.GetComponent<ScreenSpaceGlobalIllumination>().enabled.Override(true);
            m_Gtao.settings.intensity = 0f;

            AddBothFeatures(gtaoFirst: true);

            Assert.AreEqual(1, PyramidEnqueues());
            Assert.AreEqual(0, m_Gtao.historyStore.entryCount);
        }
    }
}