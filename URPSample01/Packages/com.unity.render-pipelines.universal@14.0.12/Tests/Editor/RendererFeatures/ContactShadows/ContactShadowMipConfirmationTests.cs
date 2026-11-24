using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using IDs = UnityEngine.Rendering.Universal.ScreenSpaceContactShadowsShaderIDs;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class ContactShadowMipConfirmationTests
    {
        const int Width = 64, Height = 32, ProbeX = 32, ProbeY = 16;
        static readonly string ShaderPath = UniversalRenderPipelineAsset.packagePath + "/Shaders/Utils/ContactShadows/ContactShadows.compute";

        [StructLayout(LayoutKind.Sequential)]
        struct LightRecord
        {
            public Vector3 position;
            public uint type;
            public Vector3 direction;
            public uint mask;
        }

        [Test]
        public void ContactKernelImportsWithoutShaderMessages()
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            Assert.IsNotNull(shader);
            Assert.IsTrue(shader.HasKernel("KContactShadow"));
            Assert.AreEqual(0, ShaderUtil.GetComputeShaderMessageCount(shader));
        }

        [TestCase(true, false, 0, TestName = "Mip1CandidateWithoutFineHitIsUnoccluded")]
        [TestCase(false, true, 0, TestName = "FineHitWithoutMip1CandidateIsUnoccluded")]
        [TestCase(true, true, 7, TestName = "FineHitAtSameStepSetsEachLightBit")]
        [TestCase(true, true, 7, true, TestName = "ArraySliceDoesNotReadTheOtherEyesBlocker")]
        public void ConfirmAtSameStep(bool coarseHit, bool fineHit, int expectedMask, bool array = false)
        {
            if (!ScreenSpaceContactShadowsCapability.SupportsApi(SystemInfo.graphicsDeviceType) ||
                !SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, FormatUsage.Sample) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_UInt, FormatUsage.LoadStore))
                Assert.Ignore("Requires contact shadow compute, RFloat sampling, R32_UInt UAV and GPU readback.");

            var asset = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            Assert.IsNotNull(asset, ShaderPath);
            var shader = Object.Instantiate(asset);
            Texture atlas = null;
            RenderTexture output = null;
            ComputeBuffer lights = null;
            try
            {
                CoreUtils.SetKeyword(shader, "SSL_XR_ARRAY", array);
                int kernel = shader.FindKernel("KContactShadow");
                var projection = GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-1, 1, -0.5f, 0.5f, 1, 3), true);
                float Depth(float eyeZ) => projection.MultiplyPoint(new Vector3(0, 0, -eyeZ)).z;
                float clear = SystemInfo.usesReversedZBuffer ? 0f : 1f;
                float receiver = Depth(2f), blocker = Depth(1.99f);

                // One four-pixel step spans the blocker: advancing t before confirmation skips it.
                var pixels = new float[Width * (Height + Height / 2)];
                for (int i = 0; i < pixels.Length; ++i) pixels[i] = clear;
                for (int y = 0; y < Height; ++y)
                {
                    pixels[y * Width + ProbeX] = receiver;
                    for (int x = 40; x < 44; ++x)
                    {
                        if (fineHit) pixels[y * Width + x] = blocker;
                        if (coarseHit) pixels[(Height + y / 2) * Width + x / 2] = blocker;
                    }
                }
                if (array)
                {
                    var stereo = new Texture2DArray(Width, Height + Height / 2, 2, TextureFormat.RFloat, false, true);
                    atlas = stereo;
                    stereo.SetPixelData(pixels, 0, 0);
                    for (int i = 0; i < pixels.Length; ++i) pixels[i] = clear;
                    stereo.SetPixelData(pixels, 0, 1);
                    stereo.Apply(false, false);
                }
                else
                {
                    var mono = new Texture2D(Width, Height + Height / 2, TextureFormat.RFloat, false, true);
                    atlas = mono;
                    mono.SetPixelData(pixels, 0);
                    mono.Apply(false, false);
                }
                output = new RenderTexture(Width, Height, 0, GraphicsFormat.R32_UInt)
                {
                    dimension = array ? TextureDimension.Tex2DArray : TextureDimension.Tex2D,
                    volumeDepth = array ? 2 : 1, enableRandomWrite = true
                };
                Assert.IsTrue(output.Create());

                // The unused source points left: choosing position vs direction incorrectly loses a bit.
                var records = new[]
                {
                    new LightRecord { type = 0, direction = Vector3.right, position = new Vector3(-100, 0, -2), mask = 1 },
                    new LightRecord { type = 1, direction = Vector3.left, position = new Vector3(100, 0, -2), mask = 2 },
                    new LightRecord { type = 2, direction = Vector3.left, position = new Vector3(100, 0, -2), mask = 4 }
                };
                lights = new ComputeBuffer(records.Length, 32);
                lights.SetData(records);
                shader.SetBuffer(kernel, IDs.lightBuffer, lights);
                shader.SetInt(IDs.lightCount, records.Length);
                shader.SetTexture(kernel, IDs.depthPyramid, atlas);
                shader.SetTexture(kernel, IDs.output, output);
                shader.SetVector(IDs.mip0, new Vector4(0, 0, Width, Height));
                shader.SetVector(IDs.mip1, new Vector4(0, Height, Width / 2, Height / 2));
                shader.SetVector(IDs.screenSize, new Vector4(Width, Height, 1f / Width, 1f / Height));
                shader.SetVector(IDs.traceParams, new Vector4(1, 0, 0, 0.15f));
                shader.SetVector(IDs.distanceParams, new Vector4(0, 10, 0, 1));
                shader.SetInt(IDs.sampleCount, 4);
                shader.SetInt(IDs.frameIndex, 0);
                shader.SetInt(IDs.taaEnabled, 0);
                var projections = new[] { projection, projection };
                shader.SetMatrixArray(IDs.view, new[] { Matrix4x4.identity, Matrix4x4.identity });
                shader.SetMatrixArray(IDs.projection, projections);
                shader.SetMatrixArray(IDs.viewProjection, projections);
                shader.SetMatrixArray(IDs.invViewProjection, new[] { projection.inverse, projection.inverse });
                shader.SetVectorArray(IDs.cameraPosWS, new[] { Vector4.zero, Vector4.zero });
                shader.Dispatch(kernel, Width / 8, Height / 8, output.volumeDepth);

                var readback = AsyncGPUReadback.Request(output, 0);
                readback.WaitForCompletion();
                Assert.IsFalse(readback.hasError, "Failed to read the packed contact shadow texture");
                uint packed = readback.GetData<uint>()[ProbeY * Width + ProbeX];
                Assert.AreEqual((uint)expectedMask, packed & 0x00ffffffu, $"Receiver packed value: 0x{packed:X8}");
                if (array) Assert.Zero(readback.GetData<uint>(1)[ProbeY * Width + ProbeX]);
                TestContext.WriteLine($"{SystemInfo.graphicsDeviceType}: coarse={coarseHit}, fine={fineHit}, packed=0x{packed:X8}");
            }
            finally
            {
                lights?.Release();
                Object.DestroyImmediate(output);
                Object.DestroyImmediate(atlas);
                Object.DestroyImmediate(shader);
            }
        }
    }
}
