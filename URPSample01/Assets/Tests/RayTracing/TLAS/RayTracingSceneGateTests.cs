using System;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class RayTracingSceneGateTests
    {
        static RayTracingSceneGate Eligible => new RayTracingSceneGate
        {
            assetEnabled = true, highQuality = true, windows64 = true, backend = GraphicsDeviceType.Direct3D12,
            supportsRayTracing = true, compute = true, formats = true, validCamera = true,
            cameraType = CameraType.Game, renderType = CameraRenderType.Base, shaderAndPass = true
        };

        sealed class Allocation : IDisposable
        {
            public int releases;
            public void Dispose() => Assert.AreEqual(1, ++releases, "double release");
        }

        [TestCase("Off")]
        [TestCase("Quality")]
        [TestCase("Target")]
        [TestCase("Backend")]
        [TestCase("RayTracingUnsupported")]
        [TestCase("ComputeUnsupported")]
        [TestCase("FormatUnsupported")]
        [TestCase("CameraUnsupported")]
        [TestCase("XrUnsupported")]
        [TestCase("MissingShaderOrPass")]
        public void EachMissingRequirementDeniesWithoutAllocating(string reason)
        {
            var gate = Eligible;
            switch (reason)
            {
                case "Off": gate.assetEnabled = false; break;
                case "Quality": gate.highQuality = false; break;
                case "Target": gate.windows64 = false; break;
                case "Backend": gate.backend = GraphicsDeviceType.Direct3D11; break;
                case "RayTracingUnsupported": gate.supportsRayTracing = false; break;
                case "ComputeUnsupported": gate.compute = false; break;
                case "FormatUnsupported": gate.formats = false; break;
                case "CameraUnsupported": gate.validCamera = false; break;
                case "XrUnsupported": gate.xr = true; break;
                case "MissingShaderOrPass": gate.shaderAndPass = false; break;
                default: Assert.Fail(reason); break;
            }
            int attempts = 0;
            using (var service = new RayTracingSceneResources(_ => { attempts++; return new Allocation(); }))
            {
                service.SetRequests(RayTracingRequests.Reflections);
                Assert.AreEqual(reason, service.Prepare(gate, SceneManager.GetActiveScene(), 0, -1).ToString());
                Assert.AreEqual(0, attempts);
                Assert.AreEqual(0, service.liveResources);
            }
        }

        [Test]
        public void RequestsShareAcrossCameraEvaluationsAndReleaseOnLastSubscriber()
        {
            var scene = SceneManager.GetActiveScene();
            var allocations = new System.Collections.Generic.List<Allocation>();
            using (var service = new RayTracingSceneResources(layers =>
            {
                Assert.AreEqual(unchecked((int)0x80000000), layers);
                var handle = new Allocation(); allocations.Add(handle); return handle;
            }))
            {
                RayTracingDecision Prepare(RayTracingSceneGate gate) => service.Prepare(gate, scene, 7, int.MinValue);
                for (int i = 0; i != 2; ++i)
                {
                    Assert.AreEqual(RayTracingDecision.Off, Prepare(Eligible));
                    Assert.AreEqual(0, service.subscribers);
                    service.SetRequests(RayTracingRequests.GI);
                    Assert.AreEqual(1, service.subscribers);
                    Assert.AreEqual(0, service.liveResources);
                    Assert.AreEqual(RayTracingDecision.Accepted, Prepare(Eligible));
                    service.SetRequests(RayTracingRequests.GI | RayTracingRequests.Reflections);
                    Assert.AreEqual(2, service.subscribers);
                    var second = Eligible;
                    foreach (var type in new[] { CameraType.Reflection, CameraType.SceneView, CameraType.Preview })
                    {
                        second.cameraType = type;
                        Assert.AreEqual(RayTracingDecision.CameraUnsupported, Prepare(second));
                    }
                    second.cameraType = CameraType.Game;
                    second.renderType = CameraRenderType.Overlay;
                    Assert.AreEqual(RayTracingDecision.CameraUnsupported, Prepare(second));
                    second.renderType = CameraRenderType.Base;
                    Assert.AreEqual(RayTracingDecision.Accepted, Prepare(second));
                    Assert.AreEqual(i + 1, allocations.Count);
                    Assert.AreEqual(1, service.liveResources);
                    service.SetRequests(0);
                    Assert.AreEqual(1, allocations[i].releases);
                    Assert.AreEqual(0, service.liveResources);
                }
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ReplacementFailureCannotRetainPreviousResource(bool throws)
        {
            var first = new Allocation();
            int attempts = 0;
            using (var service = new RayTracingSceneResources(_ => ++attempts == 1 ? first :
                throws ? throw new InvalidOperationException("injected failure") : null))
            {
                var scene = SceneManager.GetActiveScene();
                service.SetRequests(RayTracingRequests.GI);
                Assert.AreEqual(RayTracingDecision.Accepted, service.Prepare(Eligible, scene, 0, -1));
                for (int i = 0; i != 2; ++i)
                    Assert.AreEqual(RayTracingDecision.AllocationFailed, service.Prepare(Eligible, scene, 1, -1));
                Assert.AreEqual(1, first.releases);
                Assert.AreEqual(0, service.liveResources);
                Assert.AreEqual(2, attempts);
            }
        }

        [Test]
        public void SceneCloseFilterChangeAndOwnerDisposalReleaseExactlyOnce()
        {
            var other = SceneManager.GetActiveScene();
            const string fixturePath = "Assets/Tests/ContactShadow/Scenes/ContactShadowBaseline.unity";
            Assert.IsFalse(SceneManager.GetSceneByPath(fixturePath).isLoaded, "Scene fixture must not already be open");
            var scene = EditorSceneManager.OpenScene(fixturePath, OpenSceneMode.Additive);
            var allocations = new System.Collections.Generic.List<Allocation>();
            using (var service = new RayTracingSceneResources(_ =>
            {
                var allocation = new Allocation(); allocations.Add(allocation); return allocation;
            }))
            try
            {
                service.SetRequests(RayTracingRequests.Reflections);
                Assert.AreEqual(RayTracingDecision.Accepted, service.Prepare(Eligible, scene, 0, -1));
                Assert.AreEqual(RayTracingDecision.IncompatibleScene, service.Prepare(Eligible, other, 0, -1));
                Assert.AreEqual(RayTracingDecision.Accepted, service.Prepare(Eligible, scene, 0, 1));
                Assert.AreEqual(1, allocations[0].releases);
                Assert.IsTrue(EditorSceneManager.CloseScene(scene, true));
                Assert.AreEqual(1, allocations[1].releases);
                Assert.AreEqual(0, service.liveResources);
                Assert.AreEqual(RayTracingDecision.Accepted, service.Prepare(Eligible, other, 0, 1));
                service.Dispose(); // Same callback as activeRenderPipelineDisposed; do not replace the test runner's pipeline.
                service.SetRequests(RayTracingRequests.GI);
                Assert.AreEqual(RayTracingDecision.Off, service.Prepare(Eligible, other, 0, 1));
                Assert.AreEqual(1, allocations[2].releases);
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void ActualEditorDeviceAndCameraDecisionWithoutClaimingShaderReadiness()
        {
            var go = new GameObject("T1 capability camera");
            try
            {
                var camera = go.AddComponent<Camera>();
                var data = new CameraData { camera = camera, cameraType = camera.cameraType,
                    renderType = CameraRenderType.Base, cameraTargetDescriptor = new RenderTextureDescriptor(32, 32) };
                var gate = RayTracingSceneGate.Capture(data, true, true, null, null);
                var decision = gate.Evaluate(RayTracingRequests.GI);
                Assert.AreEqual(SystemInfo.graphicsDeviceType, gate.backend);
                Assert.AreEqual(SystemInfo.supportsRayTracing, gate.supportsRayTracing);
                Assert.AreEqual(SystemInfo.supportsComputeShaders, gate.compute);
                Assert.IsTrue(gate.validCamera);
                Assert.IsFalse(gate.shaderAndPass);
                Assert.AreNotEqual(RayTracingDecision.Accepted, decision);
                TestContext.WriteLine($"Editor {SystemInfo.graphicsDeviceName}: {gate.backend}, Win64 target={gate.windows64}, " +
                    $"RT={gate.supportsRayTracing}, compute={gate.compute}, UAV={gate.formats}, decision={decision}");
                using (var service = new RayTracingSceneResources())
                {
                    service.SetRequests(RayTracingRequests.GI);
                    Assert.AreEqual(RayTracingDecision.Off, service.Prepare(data, true, true, null, null, 0, -1, RayTracingRequests.None));
                    Assert.AreEqual(RayTracingDecision.Off, service.Prepare(data, true, true, null, null, 0, -1, RayTracingRequests.Reflections));
                    Assert.AreEqual(decision, service.Prepare(data, true, true, null, null, 0, -1, RayTracingRequests.GI));
                    Assert.AreEqual(0, service.liveResources);
                    TestContext.Out.WriteLine($"original gate={decision}, liveRTAS={service.liveResources}");
                    if (decision == RayTracingDecision.MissingShaderOrPass)
                    {
                        // Isolate the real constructor/teardown on a qualifying device. Shader readiness
                        // is injected here only; no shader is bound and no build/dispatch is claimed.
                        gate.shaderAndPass = true;
                        Assert.AreEqual(RayTracingDecision.Accepted, service.Prepare(gate, go.scene, 0, -1));
                        int allocated = service.liveResources;
                        Assert.AreEqual(1, allocated);
                        service.SetRequests(0);
                        Assert.AreEqual(0, service.liveResources);
                        TestContext.Out.WriteLine($"injected shader readiness only; native RTAS live={allocated}, after unsubscribe={service.liveResources}");
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
