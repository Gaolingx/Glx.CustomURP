namespace UnityEngine.Rendering.Universal.Internal
{
    internal class ClusteredDeferredPass : ScriptableRenderPass
    {
        // C2: forward-drawn objects (transparent, forward-only) read the same structured cluster
        // bitmaps as the kernel instead of the per-object light indices. Static so the arena can
        // reproduce the per-object path in the same build.
        internal static bool forwardClusterLights = true;

        static class ShaderIDs
        {
            public static readonly int StencilTexture = Shader.PropertyToID("_CdStencilTexture");
            public static readonly int BakedColor = Shader.PropertyToID("_CdBakedColor");
            public static readonly int FogMode = Shader.PropertyToID("_CdFogMode");
            public static readonly int FogProjection = Shader.PropertyToID("_CdFogProjection");
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
            public static readonly int SsrSpecular = Shader.PropertyToID("_GlxSsrSpecular");
        }

        const int k_ThreadGroupSize = 16;

        readonly DeferredLights m_DeferredLights;
        // G1c: the cluster list may still be running on the async compute queue when this pass
        // starts; the scheduler owns the fence and inserts the wait here, after GBuffer.
        readonly ForwardLights m_ForwardLights;
        readonly ComputeShader m_Compute;
        readonly int m_Kernel;
        readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler("Clustered Deferred");

        public ClusteredDeferredPass(RenderPassEvent evt, DeferredLights deferredLights, ForwardLights forwardLights,
            ComputeShader compute)
        {
            base.profilingSampler = new ProfilingSampler(nameof(ClusteredDeferredPass));
            base.renderPassEvent = evt;
            m_DeferredLights = deferredLights;
            m_ForwardLights = forwardLights;
            m_Compute = compute;
            int kernel = compute != null && compute.HasKernel("ClusteredDeferred") ? compute.FindKernel("ClusteredDeferred") : -1;
            m_Kernel = kernel >= 0 && compute.IsSupported(kernel) ? kernel : -1;
            if (!isAvailable)
                Debug.LogWarning("Clustered deferred lighting resources are unavailable; using Forward.");
        }

        // v1 requires compute plus readable GBuffer/depth textures; GLES/WebGL have no compute
        // path and the structured-buffer light layout is not what the kernel reads.
        internal static bool IsSupportedDevice
        {
            get
            {
                if (!SystemInfo.supportsComputeShaders || RenderingUtils.useStructuredBuffer)
                    return false;

                var api = SystemInfo.graphicsDeviceType;
                return (api == GraphicsDeviceType.Direct3D11 || api == GraphicsDeviceType.Direct3D12 ||
                        api == GraphicsDeviceType.Vulkan || api == GraphicsDeviceType.Metal) &&
                       SystemInfo.graphicsShaderLevel >= 45 && SystemInfo.supportedRenderTargetCount >= 4;
            }
        }

        internal bool isAvailable => m_Compute != null && m_Kernel >= 0;

        static RTHandle s_SpecularComposite;
        static int s_SpecularCompositeFrame = -1;

        // Published by the indirect-specular pass (225) after it composites the term the deferred
        // GBuffer no longer writes.
        internal static void PublishSpecularComposite(RTHandle texture)
        {
            s_SpecularComposite = texture;
            s_SpecularCompositeFrame = Time.frameCount;
        }

        // Consumed once per dispatch. A publish from an earlier frame is stale: the receiver may have
        // been routed for a camera whose deferred kernel never ran.
        static RTHandle ConsumeSpecularComposite()
        {
            var texture = s_SpecularCompositeFrame == Time.frameCount ? s_SpecularComposite : null;
            s_SpecularComposite = null;
            s_SpecularCompositeFrame = -1;
            return texture;
        }

        // XR/foveated, preview and overlay cameras use ordinary Forward.
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
            if (!isAvailable)
                return;

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                ref var cameraData = ref renderingData.cameraData;
                var width = cameraData.cameraTargetDescriptor.width;
                var height = cameraData.cameraTargetDescriptor.height;
                if (width <= 0 || height <= 0)
                    return;

                var renderer = (UniversalRenderer)cameraData.renderer;
                var baked = m_DeferredLights.GbufferAttachments[m_DeferredLights.GBufferLightingIndex];
                cmd.SetRenderTarget(BuiltinRenderTextureType.None);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.StencilTexture,
                    Bindable(m_DeferredLights.DepthAttachmentHandle), 0, RenderTextureSubElement.Stencil);
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.BakedColor, Bindable(baked));
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.LightingTextureRW, Bindable(renderer.deferredColorDestination));
                var depthCopy = m_DeferredLights.DepthCopyTexture ?? m_DeferredLights.DepthAttachmentHandle;
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.CameraDepthTexture, Bindable(depthCopy));
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.GBuffer0, Bindable(m_DeferredLights.GbufferAttachments[0]));
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.GBuffer1, Bindable(m_DeferredLights.GbufferAttachments[1]));
                cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.GBuffer2, Bindable(m_DeferredLights.GbufferAttachments[2]));

                // The shadow caster passes publish shared shadow textures and constants. Only
                // the optional GBuffer inputs need explicit compute bindings here.
                var shadowMaskIndex = m_DeferredLights.GBufferShadowMask;
                if (shadowMaskIndex >= 0)
                    cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.ShadowMaskTexture,
                        Bindable(m_DeferredLights.GbufferAttachments[shadowMaskIndex]));
                var lightLayersIndex = m_DeferredLights.GBufferRenderingLayers;
                if (lightLayersIndex >= 0)
                    cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.LightLayersTexture,
                        Bindable(m_DeferredLights.GbufferAttachments[lightLayersIndex]));

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
                var projection = cameraData.GetGPUProjectionMatrix();
                cmd.SetComputeIntParam(m_Compute, ShaderIDs.FogMode, RenderSettings.fog ? (int)RenderSettings.fogMode : 0);
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.FogProjection,
                    new Vector4(projection.m22, projection.m23, camera.nearClipPlane, camera.farClipPlane));
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.ScreenSize,
                    new Vector4(width, height, 1.0f / width, 1.0f / height));
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.CameraPositionWS, camera.transform.position);
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.ViewForward, camera.transform.forward);
                cmd.SetComputeVectorParam(m_Compute, ShaderIDs.CameraParams,
                    new Vector4(camera.orthographic ? 0.0f : 1.0f, 0.0f, 0.0f, 0.0f));

                // Scope the optional shadowmask variant to the lighting dispatch.
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings._DEFERRED_MIXED_LIGHTING, m_DeferredLights.UseShadowMask);

                // G2: narrow the conservative tile bits against each tile's visible depth slice.
                // _ZBufferParams is derived from the same near/far pair the zbin scale uses.
                var zBufferParams = ComputeZBufferParams(camera.nearClipPlane, camera.farClipPlane);
                m_ForwardLights.PruneClusterList(cmd, (RenderTargetIdentifier)depthCopy, zBufferParams);

                // G3: compact per-(tile, zbin) lists. Skipped while the feature is off or on the CPU
                // fallback; the kernel then keeps reading the bitmaps.
                m_ForwardLights.BuildClusterCells(cmd, (RenderTargetIdentifier)depthCopy, zBufferParams);

                // The kernel always samples the composite, so bind it on every dispatch; black keeps
                // a frame without a routed receiver from dropping the dispatch on an unbound texture.
                var specular = ConsumeSpecularComposite();
                if (specular != null)
                    cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.SsrSpecular, Bindable(specular));
                else
                    cmd.SetComputeTextureParam(m_Compute, m_Kernel, ShaderIDs.SsrSpecular, Texture2D.blackTexture);

                cmd.DispatchCompute(m_Compute, m_Kernel,
                    (width + k_ThreadGroupSize - 1) / k_ThreadGroupSize,
                    (height + k_ThreadGroupSize - 1) / k_ThreadGroupSize,
                    1);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings._DEFERRED_MIXED_LIGHTING, false);

                renderer.CompleteDeferredColorHandoff(cmd);
            }
        }

        // A handle's nameID only resolves after that texture was bound as a target in this command
        // buffer. The color destination is bound by the handoff after the dispatch, so binding by
        // name drops the first frame into an unregistered temporary; bind the concrete texture.
        static RenderTargetIdentifier Bindable(RTHandle handle) =>
            handle.rt != null ? new RenderTargetIdentifier(handle.rt) : handle.nameID;

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

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            m_DeferredLights.OnCameraCleanup(cmd);
        }

    }
}
