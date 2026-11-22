using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using Unity.Collections;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal.Internal
{
    // Customization per platform.
    static class DeferredConfig
    {
        internal static bool IsOpenGL { get; set; }

        // DX10 uses SM 4.0. However URP shaders requires SM 4.5 or will use fallback to SM 2.0 shaders otherwise.
        // We will consider deferred renderer is not available when SM 2.0 shaders run.
        internal static bool IsDX10 { get; set; }
    }

    // Manages deferred lights.
    internal class DeferredLights
    {
        internal static class ShaderConstants
        {
            public static int _MainLightPosition = Shader.PropertyToID("_MainLightPosition");   // ForwardLights.LightConstantBuffer also refers to the same ShaderPropertyID - TODO: move this definition to a common location shared by other UniversalRP classes
            public static int _MainLightColor = Shader.PropertyToID("_MainLightColor");         // ForwardLights.LightConstantBuffer also refers to the same ShaderPropertyID - TODO: move this definition to a common location shared by other UniversalRP classes
            public static int _MainLightLayerMask = Shader.PropertyToID("_MainLightLayerMask"); // ForwardLights.LightConstantBuffer also refers to the same ShaderPropertyID - TODO: move this definition to a common location shared by other UniversalRP classes
        }

        internal static readonly string[] k_GBufferNames = new string[]
        {
            "_GBuffer0",
            "_GBuffer1",
            "_GBuffer2",
            "_GBuffer3",
            "_GBuffer4",
            "_GBuffer5",
            "_GBuffer6"
        };

        static readonly string k_SetupLights = "SetupLights";
        static readonly string k_SetupLightConstants = "Setup Light Constants";
        private static readonly ProfilingSampler m_ProfilingSetupLightConstants = new ProfilingSampler(k_SetupLightConstants);

        internal int GBufferAlbedoIndex { get { return 0; } }
        internal int GBufferSpecularMetallicIndex { get { return 1; } }
        internal int GBufferNormalSmoothnessIndex { get { return 2; } }
        internal int GBufferLightingIndex { get { return 3; } }
        internal int GbufferDepthIndex { get { return UseRenderPass ? GBufferLightingIndex + 1 : -1; } }
        internal int GBufferRenderingLayers { get { return UseRenderingLayers ? GBufferLightingIndex + (UseRenderPass ? 1 : 0) + 1 : -1; } }
        // Shadow Mask can change at runtime. Because of this it needs to come after the non-changing buffers.
        internal int GBufferShadowMask { get { return UseShadowMask ? GBufferLightingIndex + (UseRenderPass ? 1 : 0) + (UseRenderingLayers ? 1 : 0) + 1 : -1; } }
        // Color buffer count (not including dephStencil).
        internal int GBufferSliceCount { get { return 4 + (UseRenderPass ? 1 : 0) + (UseShadowMask ? 1 : 0) + (UseRenderingLayers ? 1 : 0); } }

        internal GraphicsFormat GetGBufferFormat(int index)
        {
            if (index == GBufferAlbedoIndex) // sRGB albedo, materialFlags
                return QualitySettings.activeColorSpace == ColorSpace.Linear ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
            else if (index == GBufferSpecularMetallicIndex) // sRGB specular, [unused]
                return GraphicsFormat.R8G8B8A8_UNorm;
            else if (index == GBufferNormalSmoothnessIndex)
                return AccurateGbufferNormals ? GraphicsFormat.R8G8B8A8_UNorm : DepthNormalOnlyPass.GetGraphicsFormat(); // normal normal normal packedSmoothness
            else if (index == GBufferLightingIndex) // Emissive+baked: Most likely B10G11R11_UFloatPack32 or R16G16B16A16_SFloat
                return GraphicsFormat.None;
            else if (index == GbufferDepthIndex) // Render-pass on mobiles: reading back real depth-buffer is either inefficient (Arm Vulkan) or impossible (Metal).
                return GraphicsFormat.R32_SFloat;
            else if (index == GBufferShadowMask) // Optional: shadow mask is outputed in mixed lighting subtractive mode for non-static meshes only
                return GraphicsFormat.B8G8R8A8_UNorm;
            else if (index == GBufferRenderingLayers) // Optional: rendering layers is outputed when light layers are enabled (subset of rendering layers)
                return RenderingLayerUtils.GetFormat(RenderingLayerMaskSize);
            else
                return GraphicsFormat.None;
        }

        // This may return different values depending on what lights are rendered for a given frame.
        internal bool UseShadowMask { get { return this.MixedLightingSetup != MixedLightingSetup.None; } }
        //
        internal bool UseRenderingLayers { get { return UseLightLayers || UseDecalLayers; } }
        //
        internal RenderingLayerUtils.MaskSize RenderingLayerMaskSize { get; set; }
        //
        internal bool UseDecalLayers { get; set; }
        //
        internal bool UseLightLayers { get { return UniversalRenderPipeline.asset.useRenderingLayers; } }
        //
        internal bool UseRenderPass { get; set; }
        //
        internal bool HasDepthPrepass { get; set; }
        //
        internal bool HasNormalPrepass { get; set; }
        //
        internal bool HasRenderingLayerPrepass { get; set; }
        // This is an overlay camera being rendered.
        internal bool IsOverlay { get; set; }

        internal bool AccurateGbufferNormals { get; set; }

        // We browse all visible lights and found the mixed lighting setup every frame.
        internal MixedLightingSetup MixedLightingSetup { get; set; }

        // Output lighting result.
        internal RTHandle[] GbufferAttachments { get; set; }
        private RTHandle[] GbufferRTHandles;
        internal TextureHandle[] GbufferTextureHandles { get; set; }
        internal RTHandle[] DeferredInputAttachments { get; set; }
        internal bool[] DeferredInputIsTransient { get; set; }
        // Input depth texture, also bound as read-only RT
        internal RTHandle DepthAttachment { get; set; }
        //
        internal RTHandle DepthCopyTexture { get; set; }

        internal GraphicsFormat[] GbufferFormats { get; set; }
        internal RTHandle DepthAttachmentHandle { get; set; }

        internal DeferredLights()
        {
            // Cache result for GL platform here. SystemInfo properties are in C++ land so repeated access will be unecessary penalized.
            // They can also only be called from main thread!
            DeferredConfig.IsOpenGL = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES2
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3;

            // Cache result for DX10 platform too. Same reasons as above.
            DeferredConfig.IsDX10 = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11 && SystemInfo.graphicsShaderLevel <= 40;

            this.AccurateGbufferNormals = true;
        }

        internal void SetupLights(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            Profiler.BeginSample(k_SetupLights);

            long probeStart = ClusteredDeferredCpuProbe.Begin();
            ClusteredDeferredCpuProbe.CountFrame();

            Camera camera = renderingData.cameraData.camera;

            var cmd = renderingData.commandBuffer;
            using (new ProfilingScope(cmd, m_ProfilingSetupLightConstants))
            {
                // Shared uniform constants for all lights.
                SetupShaderLightConstants(cmd, ref renderingData);

#if UNITY_EDITOR
                // This flag is used to strip mixed lighting shader variants when a player is built.
                // All shader variants are available in the editor.
                bool supportsMixedLighting = true;
#else
                bool supportsMixedLighting = renderingData.lightData.supportsMixedLighting;
#endif

                // Setup global keywords.
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings._GBUFFER_NORMALS_OCT, this.AccurateGbufferNormals);
                bool isShadowMask = supportsMixedLighting && this.MixedLightingSetup == MixedLightingSetup.ShadowMask;
                bool isShadowMaskAlways = isShadowMask && QualitySettings.shadowmaskMode == ShadowmaskMode.Shadowmask;
                bool isSubtractive = supportsMixedLighting && this.MixedLightingSetup == MixedLightingSetup.Subtractive;
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.LightmapShadowMixing, isSubtractive || isShadowMaskAlways);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.ShadowsShadowMask, isShadowMask);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.MixedLightingSubtractive, isSubtractive); // Backward compatibility
                // This should be moved to a more global scope when framebuffer fetch is introduced to more passes
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.RenderPassEnabled, this.UseRenderPass && renderingData.cameraData.cameraType == CameraType.Game);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.LightLayers, UseLightLayers && !CoreUtils.IsSceneLightingDisabled(camera));

                RenderingLayerUtils.SetupProperties(cmd, RenderingLayerMaskSize);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            ClusteredDeferredCpuProbe.End(probeStart, ref ClusteredDeferredCpuProbe.deferredSetupLights);
            Profiler.EndSample();
        }

        internal void ResolveMixedLightingMode(ref RenderingData renderingData)
        {
            // Find the mixed lighting mode. This is the same logic as ForwardLights.
            this.MixedLightingSetup = MixedLightingSetup.None;

#if !UNITY_EDITOR
            // This flag is used to strip mixed lighting shader variants when a player is built.
            // All shader variants are available in the editor.
            if (renderingData.lightData.supportsMixedLighting)
#endif
            {
                NativeArray<VisibleLight> visibleLights = renderingData.lightData.visibleLights;
                for (int lightIndex = 0; lightIndex < renderingData.lightData.visibleLights.Length && this.MixedLightingSetup == MixedLightingSetup.None; ++lightIndex)
                {
                    Light light = visibleLights.UnsafeElementAtMutable(lightIndex).light;

                    if (light != null
                        && light.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed
                        && light.shadows != LightShadows.None)
                    {
                        switch (light.bakingOutput.mixedLightingMode)
                        {
                            case MixedLightingMode.Subtractive:
                                this.MixedLightingSetup = MixedLightingSetup.Subtractive;
                                break;
                            case MixedLightingMode.Shadowmask:
                                this.MixedLightingSetup = MixedLightingSetup.ShadowMask;
                                break;
                        }
                    }
                }
            }
            // Once the mixed lighting mode has been discovered, we know how many MRTs we need for the gbuffer.
            // Subtractive mixed lighting requires shadowMask output, which is actually used to store unity_ProbesOcclusion values.

            CreateGbufferResources();
        }

        // In cases when custom pass is injected between GBuffer and Deferred passes we need to fallback
        // To non-renderpass path in the middle of setup, which means recreating the gbuffer attachments as well due to GBuffer4 used for RenderPass
        internal void DisableFramebufferFetchInput()
        {
            this.UseRenderPass = false;
            CreateGbufferResources();
        }

        internal void ReleaseGbufferResources()
        {
            if (this.GbufferRTHandles != null)
            {
                // Release the old handles before creating the new one
                for (int i = 0; i < this.GbufferRTHandles.Length; ++i)
                {
                    RTHandles.Release(this.GbufferRTHandles[i]);
                }
            }
        }

        internal void ReAllocateGBufferIfNeeded(RenderTextureDescriptor gbufferSlice, int gbufferIndex)
        {
            if (this.GbufferRTHandles != null)
            {
                // In case DeferredLight does not own the RTHandle, we can skip realloc.
                if (this.GbufferRTHandles[gbufferIndex].GetInstanceID() != this.GbufferAttachments[gbufferIndex].GetInstanceID())
                    return;

                gbufferSlice.depthBufferBits = 0; // make sure no depth surface is actually created
                gbufferSlice.stencilFormat = GraphicsFormat.None;
                gbufferSlice.graphicsFormat = this.GetGBufferFormat(gbufferIndex);
                RenderingUtils.ReAllocateIfNeeded(ref this.GbufferRTHandles[gbufferIndex], gbufferSlice, FilterMode.Point, TextureWrapMode.Clamp, name: DeferredLights.k_GBufferNames[gbufferIndex]);
                this.GbufferAttachments[gbufferIndex] = this.GbufferRTHandles[gbufferIndex];
            }
        }

        internal void CreateGbufferResources()
        {
            int gbufferSliceCount = this.GBufferSliceCount;
            if (this.GbufferRTHandles == null || this.GbufferRTHandles.Length != gbufferSliceCount)
            {
                ReleaseGbufferResources();

                this.GbufferAttachments = new RTHandle[gbufferSliceCount];
                this.GbufferRTHandles = new RTHandle[gbufferSliceCount];
                this.GbufferFormats = new GraphicsFormat[gbufferSliceCount];
                this.GbufferTextureHandles = new TextureHandle[gbufferSliceCount];
                for (int i = 0; i < gbufferSliceCount; ++i)
                {
                    this.GbufferRTHandles[i] = RTHandles.Alloc(k_GBufferNames[i], name: k_GBufferNames[i]);
                    this.GbufferAttachments[i] = this.GbufferRTHandles[i];
                    this.GbufferFormats[i] = this.GetGBufferFormat(i);
                }
            }
        }

        internal void UpdateDeferredInputAttachments()
        {
            this.DeferredInputAttachments[0] = this.GbufferAttachments[0];
            this.DeferredInputAttachments[1] = this.GbufferAttachments[1];
            this.DeferredInputAttachments[2] = this.GbufferAttachments[2];
            this.DeferredInputAttachments[3] = this.GbufferAttachments[4];
            if (UseShadowMask)
                this.DeferredInputAttachments[4] = this.GbufferAttachments[GBufferShadowMask];
        }

        internal bool IsRuntimeSupportedThisFrame()
        {
            // GBuffer slice count can change depending actual geometry/light being rendered.
            // For instance, we only bind shadowMask RT if the scene supports mix lighting and at least one visible light has subtractive mixed ligting mode.
            return this.GBufferSliceCount <= SystemInfo.supportedRenderTargetCount && !DeferredConfig.IsOpenGL && !DeferredConfig.IsDX10;
        }

        public void Setup(ref RenderingData renderingData,
            bool hasDepthPrepass,
            bool hasNormalPrepass,
            bool hasRenderingLayerPrepass,
            RTHandle depthCopyTexture,
            RTHandle depthAttachment,
            RTHandle colorAttachment)
        {
            this.HasDepthPrepass = hasDepthPrepass;
            this.HasNormalPrepass = hasNormalPrepass;
            this.HasRenderingLayerPrepass = hasRenderingLayerPrepass;

            this.DepthCopyTexture = depthCopyTexture;

            this.GbufferAttachments[this.GBufferLightingIndex] = colorAttachment;
            this.DepthAttachment = depthAttachment;

            var inputCount = 4 + (UseShadowMask ?  1 : 0);
            if (this.DeferredInputAttachments == null && this.UseRenderPass && this.GbufferAttachments.Length >= 3 ||
                (this.DeferredInputAttachments != null && inputCount != this.DeferredInputAttachments.Length))
            {
                this.DeferredInputAttachments = new RTHandle[inputCount];
                this.DeferredInputIsTransient = new bool[inputCount];
                int i, j = 0;
                for (i = 0; i < inputCount; i++, j++)
                {
                    if (j == GBufferLightingIndex)
                        j++;
                    DeferredInputAttachments[i] = GbufferAttachments[j];
                    DeferredInputIsTransient[i] = j != GbufferDepthIndex;
                }
            }
            this.DepthAttachmentHandle = this.DepthAttachment;
        }

        public void OnCameraCleanup(CommandBuffer cmd)
        {
            // Disable any global keywords setup in SetupLights().
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings._GBUFFER_NORMALS_OCT, false);
        }

        internal static StencilState OverwriteStencil(StencilState s, int stencilWriteMask)
        {
            if (!s.enabled)
            {
                return new StencilState(
                    true,
                    0, (byte)stencilWriteMask,
                    CompareFunction.Always, StencilOp.Replace, StencilOp.Keep, StencilOp.Keep,
                    CompareFunction.Always, StencilOp.Replace, StencilOp.Keep, StencilOp.Keep
                );
            }

            CompareFunction funcFront = s.compareFunctionFront != CompareFunction.Disabled ? s.compareFunctionFront : CompareFunction.Always;
            CompareFunction funcBack = s.compareFunctionBack != CompareFunction.Disabled ? s.compareFunctionBack : CompareFunction.Always;
            StencilOp passFront = s.passOperationFront;
            StencilOp failFront = s.failOperationFront;
            StencilOp zfailFront = s.zFailOperationFront;
            StencilOp passBack = s.passOperationBack;
            StencilOp failBack = s.failOperationBack;
            StencilOp zfailBack = s.zFailOperationBack;

            return new StencilState(
                true,
                (byte)(s.readMask & 0x0F), (byte)(s.writeMask | stencilWriteMask),
                funcFront, passFront, failFront, zfailFront,
                funcBack, passBack, failBack, zfailBack
            );
        }

        internal static RenderStateBlock OverwriteStencil(RenderStateBlock block, int stencilWriteMask, int stencilRef)
        {
            if (!block.stencilState.enabled)
            {
                block.stencilState = new StencilState(
                    true,
                    0, (byte)stencilWriteMask,
                    CompareFunction.Always, StencilOp.Replace, StencilOp.Keep, StencilOp.Keep,
                    CompareFunction.Always, StencilOp.Replace, StencilOp.Keep, StencilOp.Keep
                );
            }
            else
            {
                StencilState s = block.stencilState;
                CompareFunction funcFront = s.compareFunctionFront != CompareFunction.Disabled ? s.compareFunctionFront : CompareFunction.Always;
                CompareFunction funcBack = s.compareFunctionBack != CompareFunction.Disabled ? s.compareFunctionBack : CompareFunction.Always;
                StencilOp passFront = s.passOperationFront;
                StencilOp failFront = s.failOperationFront;
                StencilOp zfailFront = s.zFailOperationFront;
                StencilOp passBack = s.passOperationBack;
                StencilOp failBack = s.failOperationBack;
                StencilOp zfailBack = s.zFailOperationBack;

                block.stencilState = new StencilState(
                    true,
                    (byte)(s.readMask & 0x0F), (byte)(s.writeMask | stencilWriteMask),
                    funcFront, passFront, failFront, zfailFront,
                    funcBack, passBack, failBack, zfailBack
                );
            }

            block.mask |= RenderStateMask.Stencil;
            block.stencilReference = (block.stencilReference & (int)StencilUsage.UserMask) | stencilRef;

            return block;
        }

        // adapted from ForwardLights.SetupShaderLightConstants
        void SetupShaderLightConstants(CommandBuffer cmd, ref RenderingData renderingData)
        {
            // Main light has an optimized shader path for main light. This will benefit games that only care about a single light.
            // Universal Forward pipeline only supports a single shadow light, if available it will be the main light.
            SetupMainLightConstants(cmd, ref renderingData.lightData);
        }

        // adapted from ForwardLights.SetupShaderLightConstants
        void SetupMainLightConstants(CommandBuffer cmd, ref LightData lightData)
        {
            if (lightData.mainLightIndex < 0)
                return;

            Vector4 lightPos, lightColor, lightAttenuation, lightSpotDir, lightOcclusionChannel;
            UniversalRenderPipeline.InitializeLightConstants_Common(lightData.visibleLights, lightData.mainLightIndex, out lightPos, out lightColor, out lightAttenuation, out lightSpotDir, out lightOcclusionChannel);

            var additionalLightData = lightData.visibleLights[lightData.mainLightIndex].light.GetUniversalAdditionalLightData();
            uint lightLayerMask = RenderingLayerUtils.ToValidRenderingLayers(additionalLightData.renderingLayers);

            cmd.SetGlobalVector(ShaderConstants._MainLightPosition, lightPos);
            cmd.SetGlobalVector(ShaderConstants._MainLightColor, lightColor);
            cmd.SetGlobalInt(ShaderConstants._MainLightLayerMask, (int)lightLayerMask);
        }
    }
}
