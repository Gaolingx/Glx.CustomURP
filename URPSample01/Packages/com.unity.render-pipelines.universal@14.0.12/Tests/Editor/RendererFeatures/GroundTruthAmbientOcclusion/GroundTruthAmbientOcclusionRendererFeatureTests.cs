using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class GroundTruthAmbientOcclusionRendererFeatureTests
    {
        Camera m_Camera;
        UniversalRendererData m_RendererData;
        GroundTruthAmbientOcclusionRendererFeature m_Feature;
        ScreenSpaceTestUtils.TestRenderer m_Renderer;

        [SetUp]
        public void SetUp()
        {
            m_Camera = ScreenSpaceTestUtils.CreateCamera("GtaoCamera");
            m_RendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            m_RendererData.debugShaders = new ScriptableRendererData.DebugShaderResources();
            m_Renderer = new ScreenSpaceTestUtils.TestRenderer(m_RendererData);
            m_Feature = ScriptableObject.CreateInstance<GroundTruthAmbientOcclusionRendererFeature>();
        }

        [TearDown]
        public void TearDown()
        {
            m_Feature?.Dispose();
            if (m_Feature != null)
                Object.DestroyImmediate(m_Feature);
            m_Renderer?.Dispose();
            if (m_RendererData != null)
                Object.DestroyImmediate(m_RendererData);
            ScreenSpaceTestUtils.DestroyCamera(m_Camera);
        }

        void InvokeAddRenderPasses(CameraData cameraData)
        {
            var renderingData = ScreenSpaceTestUtils.CreateRenderingData(cameraData);
            m_Feature.AddRenderPasses(m_Renderer, ref renderingData);
        }

        [Test]
        public void PassUsesReservedDispatchEvent()
        {
            Assert.AreEqual(204, (int)GroundTruthAmbientOcclusionPass.k_RenderPassEvent);
            Assert.AreEqual(204, (int)m_Feature.pass.renderPassEvent);
            Assert.AreEqual(ScriptableRenderPassInput.None, m_Feature.pass.input,
                "The empty T3 pass must not change built-in URP input scheduling.");
        }

        [Test]
        public void GameCameraEnqueuesPassAndCreatesHistory()
        {
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.Contains(m_Feature.pass, m_Renderer.queuedPasses);
            Assert.AreEqual(1, m_Feature.historyStore.entryCount);
        }

        [Test]
        public void SameCameraKeepsOneHistoryEntry()
        {
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.AreEqual(1, m_Feature.historyStore.entryCount);
        }

        [Test]
        public void UnsupportedCamerasDoNotEnqueueOrAllocateHistory()
        {
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera, CameraType.Reflection));
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera, CameraType.Preview));
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera, CameraType.Game, CameraRenderType.Overlay));

            Assert.IsFalse(m_Renderer.queuedPasses.Contains(m_Feature.pass));
            Assert.AreEqual(0, m_Feature.historyStore.entryCount);
        }

        [Test]
        public void DestroyedCameraHistoryIsPrunedOnNextFrame()
        {
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));
            Assert.AreEqual(1, m_Feature.historyStore.entryCount);

            ScreenSpaceTestUtils.DestroyCamera(m_Camera);
            m_Camera = ScreenSpaceTestUtils.CreateCamera("GtaoCameraNextFrame");

            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.AreEqual(1, m_Feature.historyStore.entryCount);
        }

        [Test]
        public void CreateRebuildsPassAndHistoryStore()
        {
            var previousPass = m_Feature.pass;
            var previousStore = m_Feature.historyStore;

            m_Feature.Create();

            Assert.AreNotSame(previousPass, m_Feature.pass);
            Assert.AreNotSame(previousStore, m_Feature.historyStore);
            Assert.AreEqual(GroundTruthAmbientOcclusionPass.k_RenderPassEvent, m_Feature.pass.renderPassEvent);
        }
    }
}
