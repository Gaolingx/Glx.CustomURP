using System.Runtime.InteropServices;
using UnityEngine.Experimental.Rendering;
using IDs = UnityEngine.Rendering.Universal.ScreenSpaceContactShadowsShaderIDs;

namespace UnityEngine.Rendering.Universal
{
    internal sealed class ScreenSpaceContactShadowsPass : ScriptableRenderPass
    {
        readonly ProfilingSampler m_Sampler = new ProfilingSampler("Contact Shadows");
        readonly ScreenSpaceContactShadowLights m_LightData;
        readonly float[] m_ShaderMasks;
        readonly Matrix4x4[] m_View = new Matrix4x4[2], m_Projection = new Matrix4x4[2],
            m_ViewProjection = new Matrix4x4[2], m_InvViewProjection = new Matrix4x4[2];
        readonly Vector4[] m_CameraPosition = new Vector4[2];
        ScreenSpaceContactShadowsHistory m_History;
        ComputeShader m_Compute;
        ComputeBuffer m_Lights;
        int m_Kernel;
        Vector4 m_TraceParams, m_DistanceParams;
        int m_SampleCount;
        float m_Opacity;
#if UNITY_EDITOR
        internal static int editorDispatchCount { get; private set; }
#endif
        internal ScreenSpaceContactShadowsPass(ScreenSpaceContactShadowLights lightData)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 8;
            m_LightData = lightData;
            m_ShaderMasks = new float[lightData.additionalMasks.Length];
        }

        internal void Setup(ContactShadowsVolume volume, ScreenSpaceContactShadowsHistory history, ComputeShader compute)
        {
            m_History = history;
            if (m_Compute != compute)
            {
                m_Compute = compute;
                m_Kernel = compute.FindKernel("KContactShadow");
            }
            m_Opacity = volume.opacity.value;
            m_TraceParams = new Vector4(volume.length.value, volume.distanceScaleFactor.value, volume.rayBias.value, volume.thicknessScale.value);
            m_DistanceParams = new Vector4(volume.minDistance.value, volume.maxDistance.value, volume.fadeInDistance.value, volume.fadeDistance.value);
            m_SampleCount = volume.sampleCount.value;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData;
            if (m_Compute == null || ScreenSpaceRenderUtils.GetCameraDepthTexture(camera.renderer) == null || !m_History.EnsurePackedTexture())
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
                m_Lights ??= new ComputeBuffer(m_LightData.records.Length, Marshal.SizeOf<ScreenSpaceContactShadowLights.LightRecord>());
                cmd.SetBufferData(m_Lights, m_LightData.records, 0, 0, m_LightData.count);
                CoreUtils.SetKeyword(m_Compute, ScreenSpaceShaderIDs.XrArrayKeyword, atlas.dimension == TextureDimension.Tex2DArray);
                cmd.SetComputeBufferParam(m_Compute, m_Kernel, IDs.lightBuffer, m_Lights);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, IDs.depthPyramid, atlas.texture);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, IDs.output, output);
                cmd.SetComputeVectorParam(m_Compute, IDs.mip0, MipRect(m_History.confirmMip));
                cmd.SetComputeVectorParam(m_Compute, IDs.mip1, MipRect(m_History.traceMip));
                cmd.SetComputeVectorParam(m_Compute, IDs.screenSize, new Vector4(width, height, 1f / width, 1f / height));
                cmd.SetComputeVectorParam(m_Compute, IDs.traceParams, m_TraceParams);
                cmd.SetComputeVectorParam(m_Compute, IDs.distanceParams, m_DistanceParams);
                cmd.SetComputeIntParam(m_Compute, IDs.lightCount, m_LightData.count);
                cmd.SetComputeIntParam(m_Compute, IDs.sampleCount, m_SampleCount);
                cmd.SetComputeIntParam(m_Compute, IDs.frameIndex, Time.frameCount);
                cmd.SetComputeIntParam(m_Compute, IDs.taaEnabled, camera.postProcessEnabled && camera.antialiasing == AntialiasingMode.TemporalAntiAliasing ? 1 : 0);
                cmd.SetComputeMatrixArrayParam(m_Compute, IDs.view, m_View);
                cmd.SetComputeMatrixArrayParam(m_Compute, IDs.projection, m_Projection);
                cmd.SetComputeMatrixArrayParam(m_Compute, IDs.viewProjection, m_ViewProjection);
                cmd.SetComputeMatrixArrayParam(m_Compute, IDs.invViewProjection, m_InvViewProjection);
                cmd.SetComputeVectorArrayParam(m_Compute, IDs.cameraPosWS, m_CameraPosition);
                cmd.DispatchCompute(m_Compute, m_Kernel, (width + 7) / 8, (height + 7) / 8, atlas.viewCount);

                cmd.SetGlobalTexture(IDs.texture, output);
                cmd.SetGlobalFloat(IDs.opacity, m_Opacity);
                cmd.SetGlobalInteger(IDs.mainLightMask, (int)m_LightData.mainMask);
                for (int i = 0; i < m_ShaderMasks.Length; ++i)
                    m_ShaderMasks[i] = Unity.Mathematics.math.asuint(m_LightData.additionalMasks[i]);
                cmd.SetGlobalFloatArray(IDs.additionalLightMasks, m_ShaderMasks);
                cmd.SetGlobalInteger(IDs.lightCount, m_LightData.count);
                if (camera.renderer is UniversalRenderer ur && ur.renderingModeActual == RenderingMode.Deferred)
                    cmd.EnableShaderKeyword(IDs.deferredKeyword);
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

    internal sealed class ScreenSpaceContactShadowsCleanupPass : ScriptableRenderPass
    {
        readonly float[] m_ZeroMasks;
        RenderTexture m_Neutral, m_NeutralArray;

        internal ScreenSpaceContactShadowsCleanupPass(int maskCount)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            m_ZeroMasks = new float[maskCount];
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var xr = renderingData.cameraData.xr;
            var neutral = GetNeutral(xr.enabled && xr.viewCount > 1); // Match the shared pyramid's layout.
            var cmd = renderingData.commandBuffer;
            cmd.DisableShaderKeyword(IDs.deferredKeyword);
            cmd.SetGlobalTexture(IDs.texture, neutral);
            cmd.SetGlobalFloat(IDs.opacity, 0f);
            cmd.SetGlobalInteger(IDs.mainLightMask, 0);
            cmd.SetGlobalFloatArray(IDs.additionalLightMasks, m_ZeroMasks);
            cmd.SetGlobalInteger(IDs.lightCount, 0);
        }

        RenderTexture GetNeutral(bool array)
        {
            ref var neutral = ref (array ? ref m_NeutralArray : ref m_Neutral);
            if (neutral != null && neutral.IsCreated())
                return neutral;
            if (neutral == null)
                neutral = new RenderTexture(1, 1, 0, GraphicsFormat.R32_UInt)
                {
                    name = "_ContactShadowNeutralTexture", hideFlags = HideFlags.HideAndDontSave,
                    dimension = array ? TextureDimension.Tex2DArray : TextureDimension.Tex2D,
                    volumeDepth = array ? 2 : 1, enableRandomWrite = true, filterMode = FilterMode.Point
                };
            neutral.Create();
            var previous = RenderTexture.active;
            for (int slice = 0; slice < neutral.volumeDepth; ++slice)
            {
                Graphics.SetRenderTarget(neutral, 0, CubemapFace.Unknown, slice);
                GL.Clear(false, true, Color.clear);
            }
            RenderTexture.active = previous;
            return neutral;
        }

        internal void Dispose()
        {
            CoreUtils.Destroy(m_Neutral);
            CoreUtils.Destroy(m_NeutralArray);
            m_Neutral = null;
            m_NeutralArray = null;
        }
    }
}
