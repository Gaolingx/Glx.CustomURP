using System.Runtime.InteropServices;
using IDs = UnityEngine.Rendering.Universal.ScreenSpaceContactShadowsShaderIDs;

namespace UnityEngine.Rendering.Universal
{
    internal sealed class ScreenSpaceContactShadowsPass : ScriptableRenderPass
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LightRecord
        {
            public Vector3 position;
            public uint type;
            public Vector3 direction;
            public uint mask;
        }

        readonly ProfilingSampler m_Sampler = new ProfilingSampler("Contact Shadows");
        readonly LightRecord[] m_MainLight = new LightRecord[1];
        readonly Matrix4x4[] m_View = new Matrix4x4[2], m_Projection = new Matrix4x4[2],
            m_ViewProjection = new Matrix4x4[2], m_InvViewProjection = new Matrix4x4[2];
        readonly Vector4[] m_CameraPosition = new Vector4[2];
        ScreenSpaceContactShadowsHistory m_History;
        ComputeShader m_Compute;
        ComputeBuffer m_Lights;
        int m_Kernel;
        Vector4 m_TraceParams, m_DistanceParams;
        int m_SampleCount;
#if UNITY_EDITOR
        internal static int editorDispatchCount { get; private set; }
#endif
        internal ScreenSpaceContactShadowsPass() => renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 8;

        internal void Setup(ContactShadowsVolume volume, ScreenSpaceContactShadowsHistory history, ComputeShader compute, Vector3 direction)
        {
            m_History = history;
            if (m_Compute != compute)
            {
                m_Compute = compute;
                m_Kernel = compute.FindKernel("KContactShadow");
            }
            m_MainLight[0] = new LightRecord { direction = direction, mask = 1u };
            m_TraceParams = new Vector4(volume.length.value, volume.distanceScaleFactor.value, volume.rayBias.value, volume.thicknessScale.value);
            m_DistanceParams = new Vector4(volume.minDistance.value, volume.maxDistance.value, volume.fadeInDistance.value, volume.fadeDistance.value);
            m_SampleCount = volume.sampleCount.value;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData;
            if (ScreenSpaceRenderUtils.GetCameraDepthTexture(camera.renderer) == null || !m_History.EnsurePackedTexture())
                return;
            var output = m_History.packedTexture;
            int width = output.rt.width, height = output.rt.height;
            var atlas = m_History.depthPyramidSource;
            for (int eye = 0; eye < atlas.viewCount; ++eye)
            {
                m_View[eye] = camera.GetViewMatrix(eye);
                m_Projection[eye] = camera.GetGPUProjectionMatrix(eye);
                m_ViewProjection[eye] = m_Projection[eye] * m_View[eye];
                m_InvViewProjection[eye] = m_ViewProjection[eye].inverse;
                m_CameraPosition[eye] = m_View[eye].inverse.GetColumn(3);
            }

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, m_Sampler))
            {
                m_Lights ??= new ComputeBuffer(1, Marshal.SizeOf<LightRecord>());
                cmd.SetBufferData(m_Lights, m_MainLight);
                CoreUtils.SetKeyword(m_Compute, ScreenSpaceShaderIDs.XrArrayKeyword, atlas.dimension == TextureDimension.Tex2DArray);
                cmd.SetComputeBufferParam(m_Compute, m_Kernel, IDs.lightBuffer, m_Lights);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, IDs.depthPyramid, atlas.texture);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, IDs.output, output);
                cmd.SetComputeVectorParam(m_Compute, IDs.mip0, MipRect(m_History.confirmMip));
                cmd.SetComputeVectorParam(m_Compute, IDs.mip1, MipRect(m_History.traceMip));
                cmd.SetComputeVectorParam(m_Compute, IDs.screenSize, new Vector4(width, height, 1f / width, 1f / height));
                cmd.SetComputeVectorParam(m_Compute, IDs.traceParams, m_TraceParams);
                cmd.SetComputeVectorParam(m_Compute, IDs.distanceParams, m_DistanceParams);
                cmd.SetComputeIntParam(m_Compute, IDs.lightCount, 1);
                cmd.SetComputeIntParam(m_Compute, IDs.sampleCount, m_SampleCount);
                cmd.SetComputeIntParam(m_Compute, IDs.frameIndex, Time.frameCount);
                cmd.SetComputeIntParam(m_Compute, IDs.taaEnabled, camera.postProcessEnabled && camera.antialiasing == AntialiasingMode.TemporalAntiAliasing ? 1 : 0);
                cmd.SetComputeMatrixArrayParam(m_Compute, IDs.view, m_View);
                cmd.SetComputeMatrixArrayParam(m_Compute, IDs.projection, m_Projection);
                cmd.SetComputeMatrixArrayParam(m_Compute, IDs.viewProjection, m_ViewProjection);
                cmd.SetComputeMatrixArrayParam(m_Compute, IDs.invViewProjection, m_InvViewProjection);
                cmd.SetComputeVectorArrayParam(m_Compute, IDs.cameraPosWS, m_CameraPosition);
                cmd.DispatchCompute(m_Compute, m_Kernel, (width + 7) / 8, (height + 7) / 8, atlas.viewCount);
#if UNITY_EDITOR
                ++editorDispatchCount;
#endif
            }
        }

        static Vector4 MipRect(RectInt r) => new Vector4(r.x, r.y, r.width, r.height);

        internal void Dispose()
        {
            m_Lights?.Release();
            m_Lights = null;
        }
    }
}
