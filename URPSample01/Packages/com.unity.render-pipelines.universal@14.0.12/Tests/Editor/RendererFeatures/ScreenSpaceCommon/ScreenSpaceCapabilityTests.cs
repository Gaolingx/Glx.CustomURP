using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class ScreenSpaceCapabilityTests
    {
        Camera m_Camera;

        [SetUp]
        public void SetUp()
        {
            m_Camera = ScreenSpaceTestUtils.CreateCamera("CapabilityCamera");
        }

        [TearDown]
        public void TearDown()
        {
            ScreenSpaceTestUtils.DestroyCamera(m_Camera);
        }

        [Test]
        public void GameCameraIsSupported()
        {
            var cameraData = ScreenSpaceTestUtils.CreateCameraData(m_Camera);

            var result = ScreenSpaceCapability.EvaluateCamera(cameraData);

            Assert.IsTrue(result.supported);
            Assert.AreEqual(ScreenSpaceCapabilityStatus.Supported, result.status);
        }

        [Test]
        public void PreviewCameraIsRejected()
        {
            var cameraData = ScreenSpaceTestUtils.CreateCameraData(m_Camera, CameraType.Preview);

            var result = ScreenSpaceCapability.EvaluateCamera(cameraData);

            Assert.IsFalse(result.supported);
            Assert.AreEqual(ScreenSpaceCapabilityStatus.CameraUnsupported, result.status);
            Assert.IsNotEmpty(result.message);
        }

        [Test]
        public void ReflectionCameraIsRejected()
        {
            var cameraData = ScreenSpaceTestUtils.CreateCameraData(m_Camera, CameraType.Reflection);

            Assert.IsFalse(ScreenSpaceCapability.EvaluateCamera(cameraData).supported);
        }

        [Test]
        public void OverlayCameraIsRejected()
        {
            var cameraData = ScreenSpaceTestUtils.CreateCameraData(m_Camera, CameraType.Game, CameraRenderType.Overlay);

            Assert.IsFalse(ScreenSpaceCapability.EvaluateCamera(cameraData).supported);
        }

        [Test]
        public void MissingCameraIsRejected()
        {
            var cameraData = ScreenSpaceTestUtils.CreateCameraData(null);

            Assert.IsFalse(ScreenSpaceCapability.EvaluateCamera(cameraData).supported);
        }

        [Test]
        public void EmptyTargetSizeIsRejected()
        {
            var cameraData = ScreenSpaceTestUtils.CreateCameraData(m_Camera, width: 0, height: 0);

            Assert.IsFalse(ScreenSpaceCapability.EvaluateCamera(cameraData).supported);
        }
    }
}
