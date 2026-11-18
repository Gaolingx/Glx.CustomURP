using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
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
        public void PassesUseTheReservedDispatchEvents()
        {
            Assert.AreEqual(203, (int)ScreenSpaceDepthPyramidPass.k_RenderPassEvent);
            Assert.AreEqual(204, (int)GroundTruthAmbientOcclusionPass.k_RenderPassEvent);
            Assert.AreEqual((int)RenderPassEvent.BeforeRenderingPostProcessing + 2, (int)GroundTruthAmbientOcclusionDebugPass.k_RenderPassEvent);

            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.AreEqual(ScreenSpaceDepthPyramidPass.k_RenderPassEvent, ScreenSpaceDepthPyramidSystem.sharedPass.renderPassEvent);
            Assert.AreEqual(GroundTruthAmbientOcclusionPass.k_RenderPassEvent, m_Feature.pass.renderPassEvent);
        }

        [Test]
        public void PassesDeclareTheirDepthNormalAndMotionInputs()
        {
            const ScriptableRenderPassInput expected = ScriptableRenderPassInput.Depth |
                                                       ScriptableRenderPassInput.Normal |
                                                       ScriptableRenderPassInput.Motion;
            Assert.AreEqual(expected, m_Feature.pass.input,
                "GTAO must request normals, depth and the shared early motion vectors.");

            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.AreEqual(
                ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Motion,
                ScreenSpaceDepthPyramidSystem.sharedPass.input,
                "The shared depth pyramid only needs depth and the early motion schedule.");
        }

        [Test]
        public void GameCameraEnqueuesBothPassesAndCreatesHistory()
        {
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.Contains(ScreenSpaceDepthPyramidSystem.sharedPass, m_Renderer.queuedPasses);
            Assert.Contains(m_Feature.pass, m_Renderer.queuedPasses);
            Assert.AreEqual(1, m_Feature.historyStore.entryCount);
        }

        [Test]
        public void SameCameraFrameEnqueuesTheSharedPyramidOnce()
        {
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            int pyramidEnqueues = m_Renderer.queuedPasses.FindAll(pass => pass == ScreenSpaceDepthPyramidSystem.sharedPass).Count;
            Assert.AreEqual(1, pyramidEnqueues, "The depth pyramid must be built once per camera and frame.");
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

            Assert.IsEmpty(m_Renderer.queuedPasses);
            Assert.AreEqual(0, m_Feature.historyStore.entryCount);
        }

        [Test]
        public void ZeroIntensityDoesNotEnqueue()
        {
            m_Feature.settings.intensity = 0f;

            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.IsEmpty(m_Renderer.queuedPasses);
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
        public void CreateRebuildsPassesAndHistoryStore()
        {
            var previousPass = m_Feature.pass;
            var previousStore = m_Feature.historyStore;

            m_Feature.Create();

            Assert.AreNotSame(previousPass, m_Feature.pass);
            Assert.AreNotSame(previousStore, m_Feature.historyStore);
            Assert.AreEqual(GroundTruthAmbientOcclusionPass.k_RenderPassEvent, m_Feature.pass.renderPassEvent);
        }

        [Test]
        public void EditorMotionPassIsEnqueuedForCamerasWithoutAdditionalCameraData()
        {
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.Contains(m_Feature.editorMotionPass, m_Renderer.queuedPasses);
            Assert.AreEqual(203, (int)m_Feature.editorMotionPass.renderPassEvent);
            Assert.Less(
                (int)RenderPassEvent.AfterRenderingPrePasses + 2,
                (int)m_Feature.editorMotionPass.renderPassEvent,
                "The supplement must run after URP clears the motion target.");
            Assert.Less(
                (int)m_Feature.editorMotionPass.renderPassEvent,
                (int)GroundTruthAmbientOcclusionPass.k_RenderPassEvent,
                "The supplement must run before GTAO samples the motion texture.");
        }

        [Test]
        public void EditorMotionPassSkipsCamerasThatUrpAlreadyHandles()
        {
            m_Camera.gameObject.AddComponent<UniversalAdditionalCameraData>();

            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.IsFalse(m_Renderer.queuedPasses.Contains(m_Feature.editorMotionPass));
            Assert.Contains(m_Feature.pass, m_Renderer.queuedPasses);
        }

        [Test]
        public void EditorMotionPassCanBeSuppressedForBaselineRuns()
        {
            ScreenSpaceEditorCameraMotionPass.s_EditorSuppressEditorMotionPass = true;
            try
            {
                InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

                Assert.IsFalse(m_Renderer.queuedPasses.Contains(m_Feature.editorMotionPass));
                Assert.Contains(m_Feature.pass, m_Renderer.queuedPasses);
            }
            finally
            {
                ScreenSpaceEditorCameraMotionPass.s_EditorSuppressEditorMotionPass = false;
            }
        }

        [Test]
        public void CreateRebuildsTheEditorMotionPass()
        {
            var previousPass = m_Feature.editorMotionPass;

            m_Feature.Create();

            Assert.AreNotSame(previousPass, m_Feature.editorMotionPass);
            Assert.AreEqual(203, (int)m_Feature.editorMotionPass.renderPassEvent);
        }

        [Test]
        public void DebugPassIsOnlyEnqueuedWhileADebugViewIsSelected()
        {
            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));
            Assert.IsFalse(m_Renderer.queuedPasses.Contains(m_Feature.debugPass));

            m_Feature.settings.debugMode = GroundTruthAmbientOcclusionDebugMode.AmbientOcclusion;
            m_Renderer.queuedPasses.Clear();

            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));
            Assert.Contains(m_Feature.debugPass, m_Renderer.queuedPasses);
        }
    }
}
