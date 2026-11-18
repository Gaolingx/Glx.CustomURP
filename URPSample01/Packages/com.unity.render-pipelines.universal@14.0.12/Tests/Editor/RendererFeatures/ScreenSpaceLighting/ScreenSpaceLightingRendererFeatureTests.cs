using System.IO;
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
        public void EditorMotionPassIsEnqueuedWhenSsgiOrSsrNeedsIt()
        {
            m_Stack.GetComponent<ScreenSpaceReflection>().enabled.Override(true);

            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.Contains(m_Feature.editorMotionPass, m_Renderer.queuedPasses);
            Assert.AreEqual(203, (int)m_Feature.editorMotionPass.renderPassEvent);
            Assert.Less(
                (int)m_Feature.editorMotionPass.renderPassEvent,
                (int)ScreenSpaceLightingSsgiPass.k_RenderPassEvent,
                "The supplement must run before SSGI samples the motion texture.");
            Assert.Less(
                (int)m_Feature.editorMotionPass.renderPassEvent,
                (int)ScreenSpaceLightingSsrPass.k_RenderPassEvent,
                "The supplement must run before SSR samples the motion texture.");
        }

        [Test]
        public void EditorMotionPassIsNotEnqueuedForRefractionOnly()
        {
            m_Stack.GetComponent<ScreenSpaceRefraction>().enabled.Override(true);

            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.IsFalse(m_Renderer.queuedPasses.Contains(m_Feature.editorMotionPass),
                "Refraction alone does not consume temporal history.");
        }

        [Test]
        public void EditorMotionPassSkipsCamerasThatUrpAlreadyHandles()
        {
            m_Camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            m_Stack.GetComponent<ScreenSpaceGlobalIllumination>().enabled.Override(true);

            InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

            Assert.IsFalse(m_Renderer.queuedPasses.Contains(m_Feature.editorMotionPass));
            Assert.Contains(m_Feature.setupPass, m_Renderer.queuedPasses);
        }

        [Test]
        public void EditorMotionPassCanBeSuppressedForBaselineRuns()
        {
            m_Stack.GetComponent<ScreenSpaceReflection>().enabled.Override(true);
            ScreenSpaceEditorCameraMotionPass.s_EditorSuppressEditorMotionPass = true;
            try
            {
                InvokeAddRenderPasses(ScreenSpaceTestUtils.CreateCameraData(m_Camera));

                Assert.IsFalse(m_Renderer.queuedPasses.Contains(m_Feature.editorMotionPass));
                Assert.Contains(m_Feature.setupPass, m_Renderer.queuedPasses);
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

        // X1: a camera that never runs the feature (preview / reflection / overlay / missing
        // resources) must not inherit the previous camera's result textures either. The player
        // evidence is in validation/P4-2; this guards the per-camera clear wiring.
        [Test]
        public void PerCameraClearResetsTheMaterialBindings()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var path = Path.Combine(
                projectRoot, "Packages", "com.unity.render-pipelines.universal@14.0.12",
                "Runtime", "ScriptableRenderer.cs");
            Assert.IsTrue(File.Exists(path), $"URP renderer source not found at {path}.");

            var source = File.ReadAllText(path);
            int start = source.IndexOf("static void ClearRenderingState(CommandBuffer cmd)", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, "ClearRenderingState is missing.");
            int end = source.IndexOf("internal void Clear(CameraRenderType cameraType)", start, System.StringComparison.Ordinal);
            Assert.Greater(end, start, "ClearRenderingState end marker is missing.");
            var body = source.Substring(start, end - start);

            StringAssert.Contains("cmd.DisableShaderKeyword(ShaderKeywordStrings.ScreenSpaceGI);", body);
            StringAssert.Contains("cmd.DisableShaderKeyword(ShaderKeywordStrings.ScreenSpaceSSR);", body);
            StringAssert.Contains("cmd.DisableShaderKeyword(ShaderKeywordStrings.ScreenSpaceTransparentSSR);", body);
            StringAssert.Contains("cmd.DisableShaderKeyword(ShaderKeywordStrings.ScreenSpaceOcclusion);", body);
            StringAssert.Contains("s_SsgiTextureID", body);
            StringAssert.Contains("s_SsgiParamsID", body);
            StringAssert.Contains("s_SsrTextureID", body);
            StringAssert.Contains("s_SsrParamsID", body);
            StringAssert.Contains("s_TransparentSsrTextureID", body);
            StringAssert.Contains("s_TransparentSsrParamsID", body);
            StringAssert.Contains("s_ScreenSpaceOcclusionTextureID", body);

            StringAssert.Contains("Shader.PropertyToID(\"_ScreenSpaceGiTexture\")", source);
            StringAssert.Contains("Shader.PropertyToID(\"_ScreenSpaceGiParams\")", source);
            StringAssert.Contains("Shader.PropertyToID(\"_ScreenSpaceSsrTexture\")", source);
            StringAssert.Contains("Shader.PropertyToID(\"_ScreenSpaceSsrParams\")", source);
            StringAssert.Contains("Shader.PropertyToID(\"_ScreenSpaceTransparentSsrTexture\")", source);
            StringAssert.Contains("Shader.PropertyToID(\"_ScreenSpaceTransparentSsrParams\")", source);
            StringAssert.Contains("Shader.PropertyToID(\"_ScreenSpaceOcclusionTexture\")", source);
        }
    }
}
