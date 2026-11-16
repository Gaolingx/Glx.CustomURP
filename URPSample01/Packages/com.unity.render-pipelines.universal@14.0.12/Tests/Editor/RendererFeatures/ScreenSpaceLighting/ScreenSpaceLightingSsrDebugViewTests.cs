using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    // F-04 regression: the SsrPbrParams debug kernel used to write into m_History.ssrOutput, which is
    // the texture the setup pass binds to the opaque GBuffer / forward shading. The diagnostics now
    // own a private target, and the PBR chain keeps running while the view is selected.
    [TestFixture]
    class ScreenSpaceLightingSsrDebugViewTests
    {
        ScreenSpaceLightingHistory m_History;

        static string PackageRoot => Path.Combine(
            Path.GetDirectoryName(Application.dataPath),
            "Packages",
            "com.unity.render-pipelines.universal@14.0.12");

        static string ReadPackageFile(string relativePath)
        {
            string path = Path.Combine(PackageRoot, relativePath);
            Assert.IsTrue(File.Exists(path), $"Expected package file at {path}.");
            return File.ReadAllText(path);
        }

        [SetUp]
        public void SetUp()
        {
            m_History = new ScreenSpaceLightingHistory();
            m_History.Reset(320, 180, 1);
        }

        [TearDown]
        public void TearDown()
        {
            m_History?.Dispose();
        }

        [Test]
        public void DebugTargetIsAllocatedSeparatelyFromTheMaterialOutput()
        {
            m_History.EnsureSsrResources(320, 180, 1, TextureDimension.Tex2D);
            m_History.EnsureSsrPbrResources(320, 180, 320, 180, 1, TextureDimension.Tex2D);
            m_History.EnsureSsrDebugResources(320, 180, 1, TextureDimension.Tex2D);

            Assert.IsNotNull(m_History.ssrDebugParams);
            Assert.AreEqual("SslSsrDebugParams", m_History.ssrDebugParams.name);
            Assert.AreNotSame(m_History.ssrOutput, m_History.ssrDebugParams);
            Assert.AreNotSame(m_History.ssrPbrOriginal, m_History.ssrDebugParams);
        }

        [Test]
        public void DebugTargetFollowsTheCameraResolution()
        {
            m_History.EnsureSsrDebugResources(320, 180, 1, TextureDimension.Tex2D);
            var first = m_History.ssrDebugParams;

            m_History.EnsureSsrDebugResources(320, 180, 1, TextureDimension.Tex2D);
            Assert.AreSame(first, m_History.ssrDebugParams, "an unchanged size must not reallocate");

            m_History.EnsureSsrDebugResources(640, 360, 1, TextureDimension.Tex2D);
            Assert.AreNotSame(first, m_History.ssrDebugParams);
        }

        [Test]
        public void DisposingTheSsrResourcesReleasesTheDebugTarget()
        {
            m_History.EnsureSsrResources(320, 180, 1, TextureDimension.Tex2D);
            m_History.EnsureSsrDebugResources(320, 180, 1, TextureDimension.Tex2D);
            Assert.IsNotNull(m_History.ssrDebugParams);

            m_History.EnsureSsrResources(640, 360, 1, TextureDimension.Tex2D);
            Assert.IsNull(m_History.ssrDebugParams, "a resolution change must release the old debug target");
        }

        [Test]
        public void DebugKernelWritesThePrivateTargetAndDoesNotReturn()
        {
            string pass = ReadPackageFile(
                "Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs");

            StringAssert.Contains("m_History.ssrDebugParams", pass);
            StringAssert.Contains("EnsureSsrDebugResources", pass);
            Assert.IsFalse(
                pass.Contains("m_History.ssrOutput);\n                cmd.DispatchCompute(compute, debugKernel"),
                "the debug kernel must not write the material-facing output.");
            StringAssert.DoesNotContain("pbr && !debugParams", pass);
            StringAssert.DoesNotContain("!halfResolution && !debugParams", pass);
        }

        [Test]
        public void DebugPassReadsThePrivateDiagnosticTexture()
        {
            string pass = ReadPackageFile(
                "Runtime/RendererFeatures/ScreenSpaceLighting/Passes/ScreenSpaceLightingDebugPass.cs");

            StringAssert.Contains("m_SsrPass.debugTexture", pass);
        }

        [Test]
        public void DebugTargetIsSampleableByTheDebugMaterial()
        {
            m_History.EnsureSsrDebugResources(320, 180, 1, TextureDimension.Tex2D);
            var descriptor = m_History.ssrDebugParams.rt.descriptor;

            Assert.AreEqual(GraphicsFormat.R16G16B16A16_SFloat, descriptor.graphicsFormat);
            Assert.IsTrue(descriptor.enableRandomWrite);
            Assert.AreEqual(320, descriptor.width);
            Assert.AreEqual(180, descriptor.height);
        }
    }
}
