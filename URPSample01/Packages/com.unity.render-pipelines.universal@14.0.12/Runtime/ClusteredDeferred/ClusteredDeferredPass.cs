using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;

namespace UnityEngine.Rendering.Universal.Internal
{
    // Clustered deferred lighting for the Universal Renderer.
    //
    // Three steps in one pass event (BeforeRenderingDeferredLights, after GBuffer + CopyDepth):
    //   1. material-type mask: two stencil-tested fullscreen draws -> R8_UInt (0 skip, 1 Lit, 2 SimpleLit)
    //   2. pixel-parallel compute: depth/GBuffer/mask -> RGBA16F direct lighting UAV
    //   3. resolve: fullscreen Blend One One adds the UAV into the lighting attachment (GBuffer3)
    // Compute dispatches have no fixed-function blending, hence the UAV + resolve; see HANDOFF D8/D16.
    internal class ClusteredDeferredPass : ScriptableRenderPass
    {
        // C2: forward-drawn objects (transparent, forward-only) read the same structured cluster
        // bitmaps as the kernel instead of the per-object light indices. Static so the arena can
        // reproduce the per-object path in the same build.
        internal static bool forwardClusterLights = true;

        static class ShaderIDs
        {
            public static readonly int MaterialTypeMask = Shader.PropertyToID("_MaterialTypeMask");
            public static readonly int LightingTexture = Shader.PropertyToID("_CdLightingTexture");
            public static readonly int LightingTextureRW = Shader.PropertyToID("_CdLightingTextureRW");
            public static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
            public static readonly int GBuffer0 = Shader.PropertyToID("_GBuffer0");
            public static readonly int GBuffer1 = Shader.PropertyToID("_GBuffer1");
            public static readonly int GBuffer2 = Shader.PropertyToID("_GBuffer2");
            public static readonly int ShadowMaskTexture = Shader.PropertyToID("_CdShadowMaskTexture");
            public static readonly int LightLayersTexture = Shader.PropertyToID("_CdLightLayersTexture");
            public static readonly int BakedShadowMixMode = Shader.PropertyToID("_CdBakedShadowMixMode");
            public static readonly int ScreenSize = Shader.PropertyToID("_ScreenSize");
            public static readonly int CameraPositionWS = Shader.PropertyToID("_CdCameraPositionWS");
            public static readonly int ViewForward = Shader.PropertyToID("_CdViewForward");
            public static readonly int CameraParams = Shader.PropertyToID("_CdCameraParams");
        }

        const int k_ThreadGroupSize = 16;

        readonly DeferredLights m_DeferredLights;
        // G1c: the cluster list may still be running on the async compute queue when this pass
        // starts; the scheduler owns the fence and inserts the wait here, after GBuffer.
        readonly ForwardLights m_ForwardLights;
        readonly ComputeShader m_Compute;
        readonly int m_Kernel;
        readonly Material m_MaskMaterial;
        readonly Material m_ResolveMaterial;
        readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler("Clustered Deferred");

        RenderTexture m_MaskTexture;
        RenderTexture m_LightingTexture;

        public ClusteredDeferredPass(RenderPassEvent evt, DeferredLights deferredLights, ForwardLights forwardLights,
            ComputeShader compute, Shader maskShader, Shader resolveShader)
        {
            base.profilingSampler = new ProfilingSampler(nameof(ClusteredDeferredPass));
            base.renderPassEvent = evt;
            m_DeferredLights = deferredLights;
            m_ForwardLights = forwardLights;
            m_Compute = compute;
            m_Kernel = compute != null ? compute.FindKernel("ClusteredDeferred") : -1;
            m_MaskMaterial = maskShader != null ? CoreUtils.CreateEngineMaterial(maskShader) : null;
            m_ResolveMaterial = resolveShader != null ? CoreUtils.CreateEngineMaterial(resolveShader) : null;

            if (m_Compute == null || m_Kernel < 0 || m_MaskMaterial == null || m_ResolveMaterial == null)
                Debug.LogError("Clustered deferred is enabled but its compute/mask/resolve resources are missing.");
        }

        // v1 requires compute plus readable GBuffer/depth textures; GLES/WebGL have no compute
        // path and the structured-buffer light layout is not what the kernel reads.
        internal static bool IsSupportedDevice
        {
            get
            {
                if (!SystemInfo.supportsComputeShaders || RenderingUtils.useStructuredBuffer)
                    return false;

                // GLES has compute on 3.1+ but the deferred GBuffer path is not supported there;
                // WebGL never reports compute support, so the check above already excludes it.
                var api = SystemInfo.graphicsDeviceType;
                return api != GraphicsDeviceType.OpenGLES2 &&
                       api != GraphicsDeviceType.OpenGLES3;
            }
        }

        // XR/foveated are out of v1 scope; preview and overlay cameras keep stencil deferred.
        internal static bool IsEligibleCamera(ref CameraData cameraData)
        {
            return cameraData.cameraType == CameraType.Game &&
                   cameraData.renderType == CameraRenderType.Base &&
                   !cameraData.isPreviewCamera &&
                   !cameraData.xr.enabled;
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            var lightingAttachment = m_DeferredLights.GbufferAttachments[m_DeferredLights.GBufferLightingIndex];
            var depthAttachment = m_DeferredLights.DepthAttachmentHandle;
            ConfigureTarget(lightingAttachment, depthAttachment);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (m_Compute == null || m_Kernel < 0 || m_MaskMaterial == null || m_ResolveMaterial == null)
                return;

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                ref var cameraData = ref renderingData.cameraData;
                var width = cameraData.cameraTargetDescriptor.width;
                var height = cameraData.cameraTargetDescriptor.height;
                if (width <= 0 || height <= 0)
                    return;

                EnsureTargets(width, height);
                if (m_MaskTexture == null || m_LightingTexture == null)
                    return;

                var lightingAttachment = m_DeferredLights.GbufferAttachments[m_DeferredLights.GBufferLightingIndex];
                var depthAttachment = m_DeferredLights.DepthAttachmentHandle;
                var lightingTarget = (RenderTargetIdentifier)lightingAttachment;
                var depthTarget = (RenderTargetIdentifier)depthAttachment;

                // 1) Material type mask.
                cmd.SetRenderTarget(new RenderTargetIdentifier(m_MaskTexture), depthTarget);
                cmd.ClearRenderTarget(false, true, Color.clear);
                cmd.DrawProcedural(Matrix4x4.identity, m_MaskMaterial, 0, MeshTopology.Triangles, 3);
                cmd.DrawProcedural(Matrix4x4.identity, m_MaskMaterial, 1, MeshTopology.Triangles, 3);

                // 2) Direct lighting compute. Cluster buffers, _FPParams*, light constants and
                // unity_MatrixInvVP are global state published by ForwardLights/UniversalRenderer.
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.MaterialTypeMask, new RenderTargetIdentifier(m_MaskTexture));
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.LightingTextureRW, new RenderTargetIdentifier(m_LightingTexture));
                var depthCopy = m_DeferredLights.DepthCopyTexture ?? m_DeferredLights.DepthAttachmentHandle;
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.CameraDepthTexture, (RenderTargetIdentifier)depthCopy);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.GBuffer0, (RenderTargetIdentifier)m_DeferredLights.GbufferAttachments[0]);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.GBuffer1, (RenderTargetIdentifier)m_DeferredLights.GbufferAttachments[1]);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.GBuffer2, (RenderTargetIdentifier)m_DeferredLights.GbufferAttachments[2]);

                // Optional GBuffer slots. The kernel samples them under the same keywords the
                // deferred path uses (_DEFERRED_MIXED_LIGHTING / _LIGHT_LAYERS); those globals are
                // enabled by DeferredLights.Setup before this pass, except the mixed-lighting
                // keyword, which the stencil path sets around ExecuteDeferredPass.
                var shadowMaskIndex = m_DeferredLights.GBufferShadowMask;
                if (shadowMaskIndex >= 0)
                    cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.ShadowMaskTexture,
                        (RenderTargetIdentifier)m_DeferredLights.GbufferAttachments[shadowMaskIndex]);
                var lightLayersIndex = m_DeferredLights.GBufferRenderingLayers;
                if (lightLayersIndex >= 0)
                    cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.LightLayersTexture,
                        (RenderTargetIdentifier)m_DeferredLights.GbufferAttachments[lightLayersIndex]);

                // Shadows.hlsl picks min-lerp for subtractive and "shadowmask always" mode and a
                // plain fade lerp for distance shadowmask; the kernel has no per-material keyword,
                // so the mode is a uniform.
                var mixedSetup = m_DeferredLights.MixedLightingSetup;
                var mixMode = mixedSetup == MixedLightingSetup.Subtractive ||
                    (mixedSetup == MixedLightingSetup.ShadowMask &&
                     QualitySettings.shadowmaskMode == ShadowmaskMode.Shadowmask)
                    ? 1.0f
                    : 0.0f;
                cmd.SetComputeFloatParam(m_Compute, ShaderIDs.BakedShadowMixMode, mixMode);

                var camera = cameraData.camera;
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.ScreenSize,
                    new Vector4(width, height, 1.0f / width, 1.0f / height));
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.CameraPositionWS, camera.transform.position);
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.ViewForward, camera.transform.forward);
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.CameraParams,
                    new Vector4(camera.orthographic ? 0.0f : 1.0f, 0.0f, 0.0f, 0.0f));

                // The stencil path enables this around ExecuteDeferredPass (DeferredLights.cs:581);
                // the clustered path replaces that pass, so it must set the same keyword itself.
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings._DEFERRED_MIXED_LIGHTING, m_DeferredLights.UseShadowMask);
                // The G1c async build runs after GBuffer; waiting at the compute stage here lets the
                // mask draws above overlap whatever is left of it instead of stalling all GPU work.
                m_ForwardLights.WaitForClusterList(cmd);

                // G2: narrow the conservative tile bits against each tile's visible depth slice.
                // _ZBufferParams is derived from the same near/far pair the zbin scale uses.
                var zBufferParams = ComputeZBufferParams(camera.nearClipPlane, camera.farClipPlane);
                m_ForwardLights.PruneClusterList(cmd, (RenderTargetIdentifier)depthCopy, zBufferParams);

                // G3: compact per-(tile, zbin) lists. Skipped while the feature is off or on the CPU
                // fallback; the kernel then keeps reading the bitmaps.
                m_ForwardLights.BuildClusterCells(cmd, (RenderTargetIdentifier)depthCopy, zBufferParams);

                cmd.DispatchCompute(m_Compute, m_Kernel,
                    (width + k_ThreadGroupSize - 1) / k_ThreadGroupSize,
                    (height + k_ThreadGroupSize - 1) / k_ThreadGroupSize,
                    1);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings._DEFERRED_MIXED_LIGHTING, false);

                // 3) Add the direct lighting into GBuffer3.
                cmd.SetGlobalTexture(ShaderIDs.LightingTexture, m_LightingTexture);
                cmd.SetRenderTarget(lightingTarget, depthTarget);
                cmd.DrawProcedural(Matrix4x4.identity, m_ResolveMaterial, 0, MeshTopology.Triangles, 3);
                if (RenderSettings.fog && !camera.orthographic)
                {
                    cmd.SetGlobalTexture(ShaderIDs.CameraDepthTexture, (RenderTargetIdentifier)depthCopy);
                    cmd.DrawProcedural(Matrix4x4.identity, m_ResolveMaterial, 1, MeshTopology.Triangles, 3);
                }
            }
        }

        // Unity's linear-depth parameters for the platform's depth convention, matching the global
        // _ZBufferParams the shaders get. Kept local because core runtime sources may not reference
        // the screen-space lighting plugin utilities (PluginIsolationTests).
        static Vector4 ComputeZBufferParams(float nearPlane, float farPlane)
        {
            nearPlane = Mathf.Max(1e-5f, nearPlane);
            farPlane = Mathf.Max(nearPlane + 1e-4f, farPlane);

            if (SystemInfo.usesReversedZBuffer)
            {
                float x = -1f + farPlane / nearPlane;
                return new Vector4(x, 1f, x / farPlane, 1f / farPlane);
            }

            float xn = 1f - farPlane / nearPlane;
            float yn = farPlane / nearPlane;
            return new Vector4(xn, yn, xn / farPlane, 1f / nearPlane);
        }

        void EnsureTargets(int width, int height)
        {
            if (m_MaskTexture != null && m_LightingTexture != null &&
                m_MaskTexture.width == width && m_MaskTexture.height == height)
            {
                return;
            }

            ReleaseTargets();

            var maskDescriptor = new RenderTextureDescriptor(width, height, GraphicsFormat.R8_UInt, 0)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = false,
            };
            m_MaskTexture = new RenderTexture(maskDescriptor)
            {
                name = "Clustered Deferred Material Mask",
                hideFlags = HideFlags.HideAndDontSave,
            };
            m_MaskTexture.Create();

            var lightingDescriptor = new RenderTextureDescriptor(width, height, GraphicsFormat.R16G16B16A16_SFloat, 0)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = true,
            };
            m_LightingTexture = new RenderTexture(lightingDescriptor)
            {
                name = "Clustered Deferred Lighting",
                hideFlags = HideFlags.HideAndDontSave,
            };
            m_LightingTexture.Create();
        }

        void ReleaseTargets()
        {
            if (m_MaskTexture != null)
            {
                m_MaskTexture.Release();
                CoreUtils.Destroy(m_MaskTexture);
                m_MaskTexture = null;
            }

            if (m_LightingTexture != null)
            {
                m_LightingTexture.Release();
                CoreUtils.Destroy(m_LightingTexture);
                m_LightingTexture = null;
            }
        }

        public void Dispose()
        {
            ReleaseTargets();
            CoreUtils.Destroy(m_MaskMaterial);
            CoreUtils.Destroy(m_ResolveMaterial);
        }
    }
}