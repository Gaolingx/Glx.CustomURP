using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class ScreenSpaceLightingRendererFeatureTests
    {
        Camera m_Camera;
        UniversalRendererData m_RendererData;
        ScreenSpaceLightingRendererFeature m_Feature;
        ScreenSpaceTestUtils.TestRenderer m_Renderer;
        VolumeStack m_Stack;
        VolumeStack m_PreviousStack;

        [SetUp]
        public void SetUp()
        {
            m_Camera = ScreenSpaceTestUtils.CreateCamera("ScreenSpaceLightingCamera");
            m_RendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            m_RendererData.debugShaders = new ScriptableRendererData.DebugShaderResources();
            m_Renderer = new ScreenSpaceTestUtils.TestRenderer(m_RendererData);
            m_Feature = ScriptableObject.CreateInstance<ScreenSpaceLightingRendererFeature>();
            m_Stack = VolumeManager.instance.CreateStack();
            m_PreviousStack = VolumeManager.instance.stack;
            VolumeManager.instance.stack = m_Stack;
        }

        [TearDown]
        public void TearDown()
        {
            VolumeManager.instance.stack = m_PreviousStack;
            m_Stack?.Dispose();
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
        public void SetupPassUsesReservedMaterialBindingEvent()
        {
            Assert.AreEqual(207, (int)ScreenSpaceLightingSetupPass.k_RenderPassEvent);
            Assert.AreEqual(207, (int)m_Feature.setupPass.renderPassEvent);
            Assert.AreEqual(ScriptableRenderPassInput.None, m_Feature.setupPass.input,
                "The empty T3 pass must not change built-in URP input scheduling.");
        }

        [Test]
        public void PassIsNotEnqueuedWhileEveryVolumeComponentIsDisabled()
        {
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.IsFalse(m_Renderer.queuedPasses.Contains(m_Feature.setupPass));
            Assert.AreEqual(0, m_Feature.historyStore.entryCount);
        }

        [Test]
        public void EnabledVolumeComponentEnqueuesPassAndCreatesHistory()
        {
            m_Stack.GetComponent<ScreenSpaceRefraction>().enabled.Override(true);

            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.Contains(m_Feature.setupPass, m_Renderer.queuedPasses);
            Assert.AreEqual(1, m_Feature.historyStore.entryCount);
        }

        [Test]
        public void SameCameraKeepsOneHistoryEntry()
        {
            m_Stack.GetComponent<ScreenSpaceGlobalIllumination>().enabled.Override(true);

            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.AreEqual(1, m_Feature.historyStore.entryCount);
        }

        [Test]
        public void PreviewAndOverlayCamerasDoNotEnqueue()
        {
            m_Stack.GetComponent<ScreenSpaceReflection>().enabled.Override(true);

            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera, CameraType.Preview));
            Assert.IsFalse(m_Renderer.queuedPasses.Contains(m_Feature.setupPass));

            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera, CameraType.Game, CameraRenderType.Overlay));
            Assert.IsFalse(m_Renderer.queuedPasses.Contains(m_Feature.setupPass));
            Assert.AreEqual(0, m_Feature.historyStore.entryCount);
        }

        [Test]
        public void CreateRebuildsPassAndHistoryStore()
        {
            var previousPass = m_Feature.setupPass;
            var previousStore = m_Feature.historyStore;

            m_Feature.Create();

            Assert.IsNotNull(m_Feature.setupPass);
            Assert.IsNotNull(m_Feature.historyStore);
            Assert.AreNotSame(previousPass, m_Feature.setupPass);
            Assert.AreNotSame(previousStore, m_Feature.historyStore);
            Assert.AreEqual(ScreenSpaceLightingSetupPass.k_RenderPassEvent, m_Feature.setupPass.renderPassEvent);
        }

        [Test]
        public void DisposeReleasesHistoryStore()
        {
            m_Stack.GetComponent<ScreenSpaceGlobalIllumination>().enabled.Override(true);
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            m_Feature.Dispose();

            Assert.IsNull(m_Feature.historyStore);
        }
    }
}
