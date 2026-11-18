using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class ScreenSpaceEditorCameraMotionPassTests
    {
        Camera m_Camera;
        Camera m_SecondCamera;

        [SetUp]
        public void SetUp()
        {
            m_Camera = ScreenSpaceTestUtils.CreateCamera("EditorMotionCamera");
            m_SecondCamera = ScreenSpaceTestUtils.CreateCamera("EditorMotionCameraSecond");
        }

        [TearDown]
        public void TearDown()
        {
            ScreenSpaceTestUtils.DestroyCamera(m_Camera);
            ScreenSpaceTestUtils.DestroyCamera(m_SecondCamera);
        }

        [Test]
        public void PassRunsAfterUrpClearsMotionAndBeforeScreenSpaceConsumers()
        {
            var pass = new ScreenSpaceEditorCameraMotionPass();

            Assert.AreEqual(203, (int)pass.renderPassEvent);
            Assert.Less(
                (int)RenderPassEvent.AfterRenderingPrePasses + 2,
                (int)pass.renderPassEvent,
                "The supplement must run after URP clears the motion target.");
            Assert.Less(
                (int)pass.renderPassEvent,
                (int)ScreenSpaceLightingSsgiPass.k_RenderPassEvent,
                "SSGI must be able to consume the supplement.");
            Assert.Less(
                (int)pass.renderPassEvent,
                (int)ScreenSpaceLightingSsrPass.k_RenderPassEvent,
                "SSR must be able to consume the supplement.");
            Assert.AreEqual(ScriptableRenderPassInput.Depth, pass.input);
        }

        [Test]
        public void HistoryResetClearsTheRecordedPose()
        {
            var history = new ScreenSpaceEditorCameraMotionHistory();
            var pose = Matrix4x4.TRS(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 15f, 0f), Vector3.one);

            history.Record(pose);
            Assert.IsTrue(history.hasViewProjection);

            history.Reset(320, 180, 2);

            Assert.IsFalse(history.hasViewProjection);
            Assert.AreEqual(Matrix4x4.identity, history.viewProjection,
                "A reset must not reproject from the pose of a previous camera session.");

            history.Record(pose);
            history.Dispose();

            Assert.IsFalse(history.hasViewProjection);
            Assert.AreEqual(Matrix4x4.identity, history.viewProjection);
        }

        [Test]
        public void CamerasKeepIndependentEditorMotionPoses()
        {
            var pass = new ScreenSpaceEditorCameraMotionPass();
            var first = pass.historyStore.GetOrCreate(m_Camera, 0, 640, 360, 0, 0, out _);
            var second = pass.historyStore.GetOrCreate(m_SecondCamera, 0, 640, 360, 0, 0, out _);

            Assert.AreNotSame(first, second);

            var pose = Matrix4x4.TRS(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 15f, 0f), Vector3.one);
            first.Record(pose);

            Assert.IsFalse(second.hasViewProjection,
                "Recording one camera's pose must not leak into another camera's history.");
            Assert.AreEqual(pose, first.viewProjection);
        }

        [Test]
        public void ResetTokenInvalidatesThePose()
        {
            var pass = new ScreenSpaceEditorCameraMotionPass();
            var history = pass.historyStore.GetOrCreate(m_Camera, 0, 640, 360, 0, 0, out _);
            history.Record(Matrix4x4.identity);

            var same = pass.historyStore.GetOrCreate(m_Camera, 0, 640, 360, 1, 0, out _);

            Assert.AreSame(history, same);
            Assert.IsTrue(history.hasViewProjection, "The pose must survive an unchanged reset token.");

            pass.historyStore.GetOrCreate(m_Camera, 0, 640, 360, 2, 1, out _);

            Assert.IsFalse(history.hasViewProjection,
                "A reset token change must drop the previous pose.");
        }
    }
}
