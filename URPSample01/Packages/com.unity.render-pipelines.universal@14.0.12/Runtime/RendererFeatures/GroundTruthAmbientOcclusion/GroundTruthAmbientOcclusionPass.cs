using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    // GTAO dispatch group: horizon search, spatial denoise, temporal accumulation and upsample.
    // Runs after the shared depth pyramid (203) and before Deferred GBuffer (210) or Forward opaque
    // geometry (250) so the AO is available when materials and DeferredLights consume it.
    internal sealed class GroundTruthAmbientOcclusionPass : ScriptableRenderPass
    {
        internal const RenderPassEvent k_RenderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 4);
        internal const string k_ProfilingSamplerName = "Ground Truth Ambient Occlusion";

        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler(k_ProfilingSamplerName);

        GroundTruthAmbientOcclusionHistory m_History;
        GroundTruthAmbientOcclusionSettings m_Settings;
        GroundTruthAmbientOcclusionResources m_Resources;
        bool m_Orthographic;
        bool m_LoggedMissingNormals;
        readonly Vector4[] m_XrViewRow0 = new Vector4[ScreenSpaceLightingXr.k_MaxViews];
        readonly Vector4[] m_XrViewRow1 = new Vector4[ScreenSpaceLightingXr.k_MaxViews];
        readonly Vector4[] m_XrViewRow2 = new Vector4[ScreenSpaceLightingXr.k_MaxViews];

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // DEV-only snapshot of the last executed frame. The validation player reads it to assert the
        // dispatch chain, the resolution keywords and whether the material facing state was published.
        internal sealed class GtaoDispatchStats
        {
            public int frameIndex = -1;
            public int screenWidth;
            public int screenHeight;
            public int aoWidth;
            public int aoHeight;
            public int groupsX;
            public int groupsY;
            public int groupsZ = 1;
            public bool fullResolution;
            public bool temporal;
            public bool orthographic;
            public bool spatialRuns;
            public bool historyReady;
            public bool produced;
            public int historyReadSlot = -1;
            public int historyWriteSlot = -1;
            public int mainDispatches;
            public int spatialDispatches;
            public int copyHistoryDispatches;
            public int temporalDispatches;
            public int blurUpsampleDispatches;
            public int upsampleDispatches;

            public void Reset(int frame)
            {
                frameIndex = frame;
                screenWidth = 0;
                screenHeight = 0;
                aoWidth = 0;
                aoHeight = 0;
                groupsX = 0;
                groupsY = 0;
                groupsZ = 1;
                fullResolution = false;
                temporal = false;
                orthographic = false;
                spatialRuns = false;
                historyReady = false;
                produced = false;
                historyReadSlot = -1;
                historyWriteSlot = -1;
                mainDispatches = 0;
                spatialDispatches = 0;
                copyHistoryDispatches = 0;
                temporalDispatches = 0;
                blurUpsampleDispatches = 0;
                upsampleDispatches = 0;
            }
        }

        internal static readonly GtaoDispatchStats s_LastDispatchStats = new GtaoDispatchStats();
#endif

#if UNITY_EDITOR
        internal static int s_EditorExecuteCount;
        internal static int s_EditorMainDispatchCount;
        internal static int s_EditorSpatialDispatchCount;
        internal static int s_EditorTemporalDispatchCount;
        internal static int s_EditorUpsampleDispatchCount;
        internal static int s_EditorCopyHistoryDispatchCount;
#endif

        internal GroundTruthAmbientOcclusionPass()
        {
            renderPassEvent = k_RenderPassEvent;
            // Normal supplies the world-space normal and encoded smoothness, Depth the pyramid input
            // and Motion the temporal reprojection. Requesting Motion also forces URP to build depth
            // before the GBuffer pass on the deferred path (T2 scheduling).
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal | ScriptableRenderPassInput.Motion);
        }

        internal void Setup(
            GroundTruthAmbientOcclusionHistory history,
            GroundTruthAmbientOcclusionSettings settings,
            GroundTruthAmbientOcclusionResources resources)
        {
            m_History = history;
            m_Settings = settings;
            m_Resources = resources;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            s_EditorExecuteCount++;
#endif
            if (m_History == null || m_Settings == null || m_Resources == null)
                return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchStats.Reset(Time.frameCount);
#endif

            ref var cameraData = ref renderingData.cameraData;
            var descriptor = cameraData.cameraTargetDescriptor;
            int viewCount = cameraData.xr.enabled ? Mathf.Max(1, cameraData.xr.viewCount) : 1;
            TextureDimension dimension = viewCount > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;

            m_History.EnsureResources(
                descriptor.width,
                descriptor.height,
                m_Settings.fullResolution,
                viewCount,
                dimension);

            var normalTexture = GetNormalTexture(cameraData.renderer);
            var motionTexture = GetMotionTexture(cameraData.renderer);
            if (normalTexture == null)
            {
                if (!m_LoggedMissingNormals)
                {
                    m_LoggedMissingNormals = true;
                    Debug.LogWarning(
                        "Ground Truth Ambient Occlusion requires the camera normals texture. Use a UniversalRenderer " +
                        "and make sure opaque shaders provide a DepthNormals pass.");
                }
                return;
            }

            m_LoggedMissingNormals = false;

            if (!m_History.resourcesAllocated)
                return;

            ComputeShaderVariables(ref cameraData, out var cb);
            bool temporal = m_Settings.temporalAccumulation && motionTexture != null;
            bool octNormals = UseOctEncodedNormals(cameraData.renderer);
            bool useTextureArray = dimension == TextureDimension.Tex2DArray;
            if (useTextureArray)
                FillXrViewRows(ref cameraData, viewCount);

            var cmd = renderingData.commandBuffer;
            bool producedOutput;
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                producedOutput = Dispatch(cmd, cb, temporal, octNormals, motionTexture, normalTexture, viewCount, useTextureArray);
            }

            if (!producedOutput)
            {
                // F-10: the material facing state must never advertise AO that this frame did not
                // write, otherwise the opaque passes sample last frame's texture (or an undefined one
                // on the first frame after a reset).
                cmd.DisableShaderKeyword(ShaderKeywordStrings.ScreenSpaceOcclusion);
                cmd.SetGlobalVector(
                    GroundTruthAmbientOcclusionShaderIDs.AmbientOcclusionParam,
                    new Vector4(1f, 0f, 0f, 0f));
                return;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchStats.produced = true;
#endif

            m_History.AdvanceEffectFrame();
            if (temporal)
                m_History.SwapBuffers();

            cmd.SetGlobalTexture(GroundTruthAmbientOcclusionShaderIDs.ScreenSpaceOcclusionTexture, m_History.occlusionTexture);
            cmd.EnableShaderKeyword(ShaderKeywordStrings.ScreenSpaceOcclusion);
            cmd.SetGlobalVector(
                GroundTruthAmbientOcclusionShaderIDs.AmbientOcclusionParam,
                new Vector4(1f, 0f, 0f, m_Settings.directLightingStrength));
        }

        bool Dispatch(
            CommandBuffer cmd,
            in GroundTruthAmbientOcclusionShaderVariables cb,
            bool temporal,
            bool octNormals,
            RTHandle motionTexture,
            RTHandle normalTexture,
            int viewCount,
            bool useTextureArray)
        {
            var gtao = m_Resources.gtaoCompute;
            var spatial = m_Resources.spatialDenoiseCompute;
            var temporalCompute = m_Resources.temporalDenoiseCompute;
            var copyHistory = m_Resources.copyHistoryCompute;
            var upsample = m_Resources.blurAndUpsampleCompute;

            bool fullResolution = m_Settings.fullResolution;
            // Matches HDRP: the spatial bilateral denoise runs for the temporal path and for full
            // resolution. Half resolution without temporal accumulation uses the smart blur fused
            // into the BlurUpsample kernel instead.
            bool spatialRuns = temporal || fullResolution;
            bool packSpatialOutput = temporal;
            int aoWidth = m_History.aoWidth;
            int aoHeight = m_History.aoHeight;
            int groupCountX = DivideRoundUp(aoWidth, 8);
            int groupCountY = DivideRoundUp(aoHeight, 8);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var stats = s_LastDispatchStats;
            stats.screenWidth = m_History.width;
            stats.screenHeight = m_History.height;
            stats.aoWidth = aoWidth;
            stats.aoHeight = aoHeight;
            stats.groupsX = groupCountX;
            stats.groupsY = groupCountY;
            stats.groupsZ = viewCount;
            stats.fullResolution = fullResolution;
            stats.temporal = temporal;
            stats.orthographic = m_Orthographic;
            stats.spatialRuns = spatialRuns;
            stats.historyReadSlot = m_History.readSlot;
            stats.historyWriteSlot = m_History.writeSlot;
#endif

            SetKeyword(gtao, GroundTruthAmbientOcclusionShaderIDs.FullResKeyword, fullResolution);
            SetKeyword(gtao, GroundTruthAmbientOcclusionShaderIDs.HalfResKeyword, !fullResolution);
            SetKeyword(gtao, GroundTruthAmbientOcclusionShaderIDs.TemporalKeyword, temporal);
            SetKeyword(gtao, GroundTruthAmbientOcclusionShaderIDs.XrArrayKeyword, useTextureArray);
            SetKeyword(gtao, GroundTruthAmbientOcclusionShaderIDs.NormalsOctKeyword, octNormals);
            SetKeyword(gtao, GroundTruthAmbientOcclusionShaderIDs.OrthographicKeyword, m_Orthographic);

            int gtaoKernel = gtao.FindKernel("GTAOMain");
            PushShaderVariables(cmd, gtao, cb);
            if (useTextureArray)
            {
                cmd.SetComputeVectorArrayParam(gtao, GroundTruthAmbientOcclusionShaderIDs.XrViewRow0, m_XrViewRow0);
                cmd.SetComputeVectorArrayParam(gtao, GroundTruthAmbientOcclusionShaderIDs.XrViewRow1, m_XrViewRow1);
                cmd.SetComputeVectorArrayParam(gtao, GroundTruthAmbientOcclusionShaderIDs.XrViewRow2, m_XrViewRow2);
            }
            cmd.SetComputeTextureParam(gtao, gtaoKernel, GroundTruthAmbientOcclusionShaderIDs.NormalTexture, normalTexture);
            cmd.SetComputeTextureParam(gtao, gtaoKernel, GroundTruthAmbientOcclusionShaderIDs.DepthPyramid, m_History.depthPyramid);
            cmd.SetComputeTextureParam(gtao, gtaoKernel, GroundTruthAmbientOcclusionShaderIDs.PackedData, m_History.packedData);
            cmd.DispatchCompute(gtao, gtaoKernel, groupCountX, groupCountY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchStats.mainDispatches++;
#endif
#if UNITY_EDITOR
            s_EditorMainDispatchCount++;
#endif

            if (spatialRuns)
            {
                SetKeyword(spatial, GroundTruthAmbientOcclusionShaderIDs.ToTemporalKeyword, packSpatialOutput);
                SetKeyword(spatial, GroundTruthAmbientOcclusionShaderIDs.XrArrayKeyword, useTextureArray);
                SetKeyword(spatial, GroundTruthAmbientOcclusionShaderIDs.OrthographicKeyword, m_Orthographic);
                int spatialKernel = spatial.FindKernel("SpatialDenoise");
                PushShaderVariables(cmd, spatial, cb);
                cmd.SetComputeTextureParam(spatial, spatialKernel, GroundTruthAmbientOcclusionShaderIDs.PackedData, m_History.packedData);
                cmd.SetComputeTextureParam(
                    spatial,
                    spatialKernel,
                    GroundTruthAmbientOcclusionShaderIDs.OcclusionTexture,
                    packSpatialOutput ? m_History.packedBlurred : m_History.occlusionTexture);
                cmd.DispatchCompute(spatial, spatialKernel, groupCountX, groupCountY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_LastDispatchStats.spatialDispatches++;
#endif
#if UNITY_EDITOR
                s_EditorSpatialDispatchCount++;
#endif
            }

            if (!temporal)
            {
                if (!fullResolution)
                {
                    // Non temporal half resolution: smart blur and upsample the raw packed buffer in
                    // one pass, like HDRP's BlurUpsample kernel.
                    DispatchBlurUpsample(cmd, upsample, cb, m_History.packedData, viewCount, useTextureArray, groupCountX, groupCountY);
                }

                return true;
            }

            // Temporal accumulation. The first frame after a reset has no readable history, so the
            // current blurred result is copied into the slot that will be read next frame.
            bool historyReady = m_History.readSlot >= 0 && m_History.GetHistory(m_History.readSlot) != null;
            RTHandle historyInput = historyReady
                ? m_History.GetHistory(m_History.readSlot)
                : m_History.GetHistory(m_History.writeSlot ^ 1);
            RTHandle historyOutput = m_History.GetHistory(m_History.writeSlot);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchStats.historyReady = historyReady;
#endif
            if (historyInput == null || historyOutput == null)
                return false;

            if (!historyReady)
            {
                SetKeyword(copyHistory, GroundTruthAmbientOcclusionShaderIDs.XrArrayKeyword, useTextureArray);
                int copyKernel = copyHistory.FindKernel("GTAODenoise_CopyHistory");
                cmd.SetComputeTextureParam(copyHistory, copyKernel, GroundTruthAmbientOcclusionShaderIDs.InputTexture, m_History.packedBlurred);
                cmd.SetComputeTextureParam(copyHistory, copyKernel, GroundTruthAmbientOcclusionShaderIDs.OutputTexture, historyInput);
                cmd.DispatchCompute(copyHistory, copyKernel, groupCountX, groupCountY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                s_LastDispatchStats.copyHistoryDispatches++;
#endif
#if UNITY_EDITOR
                s_EditorCopyHistoryDispatchCount++;
#endif
            }

            SetKeyword(temporalCompute, GroundTruthAmbientOcclusionShaderIDs.FullResKeyword, fullResolution);
            SetKeyword(temporalCompute, GroundTruthAmbientOcclusionShaderIDs.HalfResKeyword, !fullResolution);
            SetKeyword(temporalCompute, GroundTruthAmbientOcclusionShaderIDs.XrArrayKeyword, useTextureArray);
            var temporalOutput = fullResolution ? m_History.occlusionTexture : m_History.packedTemporalOutput;
            int temporalKernel = temporalCompute.FindKernel("TemporalDenoise");
            PushShaderVariables(cmd, temporalCompute, cb);
            cmd.SetComputeTextureParam(temporalCompute, temporalKernel, GroundTruthAmbientOcclusionShaderIDs.PackedBlurred, m_History.packedBlurred);
            cmd.SetComputeTextureParam(temporalCompute, temporalKernel, GroundTruthAmbientOcclusionShaderIDs.PackedHistory, historyInput);
            cmd.SetComputeTextureParam(temporalCompute, temporalKernel, GroundTruthAmbientOcclusionShaderIDs.OutputHistory, historyOutput);
            cmd.SetComputeTextureParam(temporalCompute, temporalKernel, GroundTruthAmbientOcclusionShaderIDs.MotionTexture, motionTexture);
            cmd.SetComputeTextureParam(temporalCompute, temporalKernel, GroundTruthAmbientOcclusionShaderIDs.OcclusionTexture, temporalOutput);
            cmd.DispatchCompute(temporalCompute, temporalKernel, groupCountX, groupCountY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchStats.temporalDispatches++;
#endif
#if UNITY_EDITOR
            s_EditorTemporalDispatchCount++;
#endif

            if (fullResolution)
                return true;

            DispatchUpsample(cmd, upsample, cb, temporalOutput, viewCount, useTextureArray, groupCountX, groupCountY);
            return true;
        }

        void DispatchBlurUpsample(
            CommandBuffer cmd,
            ComputeShader upsample,
            in GroundTruthAmbientOcclusionShaderVariables cb,
            RTHandle packedInput,
            int viewCount,
            bool useTextureArray,
            int groupCountX,
            int groupCountY)
        {
            SetKeyword(upsample, GroundTruthAmbientOcclusionShaderIDs.XrArrayKeyword, useTextureArray);
            int kernel = upsample.FindKernel("BlurUpsample");
            PushShaderVariables(cmd, upsample, cb);
            cmd.SetComputeTextureParam(upsample, kernel, GroundTruthAmbientOcclusionShaderIDs.PackedData, packedInput);
            cmd.SetComputeTextureParam(upsample, kernel, GroundTruthAmbientOcclusionShaderIDs.OcclusionTexture, m_History.occlusionTexture);
            cmd.SetComputeTextureParam(upsample, kernel, GroundTruthAmbientOcclusionShaderIDs.DepthPyramid, m_History.depthPyramid);
            cmd.DispatchCompute(upsample, kernel, groupCountX, groupCountY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchStats.blurUpsampleDispatches++;
#endif
#if UNITY_EDITOR
            s_EditorUpsampleDispatchCount++;
#endif
        }

        void DispatchUpsample(
            CommandBuffer cmd,
            ComputeShader upsample,
            in GroundTruthAmbientOcclusionShaderVariables cb,
            RTHandle packedInput,
            int viewCount,
            bool useTextureArray,
            int groupCountX,
            int groupCountY)
        {
            SetKeyword(upsample, GroundTruthAmbientOcclusionShaderIDs.XrArrayKeyword, useTextureArray);
            int upsampleKernel = upsample.FindKernel(m_Settings.bilateralUpsample ? "BilateralUpsampling" : "BoxUpsampling");
            PushShaderVariables(cmd, upsample, cb);
            cmd.SetComputeTextureParam(upsample, upsampleKernel, GroundTruthAmbientOcclusionShaderIDs.PackedData, packedInput);
            cmd.SetComputeTextureParam(upsample, upsampleKernel, GroundTruthAmbientOcclusionShaderIDs.OcclusionTexture, m_History.occlusionTexture);
            cmd.SetComputeTextureParam(upsample, upsampleKernel, GroundTruthAmbientOcclusionShaderIDs.DepthPyramid, m_History.depthPyramid);
            cmd.DispatchCompute(upsample, upsampleKernel, groupCountX, groupCountY, viewCount);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_LastDispatchStats.upsampleDispatches++;
#endif
#if UNITY_EDITOR
            s_EditorUpsampleDispatchCount++;
#endif
        }

        void ComputeShaderVariables(ref CameraData cameraData, out GroundTruthAmbientOcclusionShaderVariables cb)
        {
            var camera = cameraData.camera;
            int aoWidth = Mathf.Max(1, m_History.aoWidth);
            int aoHeight = Mathf.Max(1, m_History.aoHeight);
            int screenWidth = Mathf.Max(1, m_History.width);
            int screenHeight = Mathf.Max(1, m_History.height);
            bool fullResolution = m_Settings.fullResolution;
            m_Orthographic = camera.orthographic;

            float invHalfTanFOV = Mathf.Abs(camera.projectionMatrix.m11);
            if (invHalfTanFOV < 1e-4f)
                invHalfTanFOV = 1f;
            float aspect = aoHeight / (float)aoWidth;

            float radius = Mathf.Max(0.01f, m_Settings.radius);
            float stepCount = Mathf.Max(1, m_Settings.stepCount);
            int frame = m_History.effectFrameCount;
            float scaleFactor = (aoWidth * aoHeight) / (540f * 960f);
            float radInPixels = Mathf.Max(16f, m_Settings.maximumRadiusInPixels * Mathf.Sqrt(scaleFactor));

            float stepSize = fullResolution ? 1f : 0.5f;
            float blurTolerance = 1f - Mathf.Clamp01(m_Settings.blurSharpness);
            const float maxBlurTolerance = 0.25f;
            const float minBlurTolerance = -2.5f;
            blurTolerance = minBlurTolerance + blurTolerance * (maxBlurTolerance - minBlurTolerance);
            float bTolerance = 1f - Mathf.Pow(10f, blurTolerance) * stepSize;
            bTolerance *= bTolerance;
            const float upsampleTolerance = -7.0f;
            float uTolerance = Mathf.Pow(10f, upsampleTolerance);
            float noiseFilterWeight = 1f / (1f + uTolerance);

            float upperNudgeFactor = 1f - Mathf.Clamp01(m_Settings.ghostingReduction);
            const float maxUpperNudgeLimit = 5.0f;
            const float minUpperNudgeLimit = 0.25f;
            upperNudgeFactor = minUpperNudgeLimit + upperNudgeFactor * (maxUpperNudgeLimit - minUpperNudgeLimit);

            var info = m_History.depthPyramidInfo;
            bool hasMip1 = info.IsValid() && info.mipLevelOffsets != null && info.mipLevelCount > 1;
            bool hasMip2 = info.IsValid() && info.mipLevelOffsets != null && info.mipLevelCount > 2;
            Vector2Int mip1 = hasMip1 ? info.mipLevelOffsets[1] : Vector2Int.zero;
            Vector2Int mip2 = hasMip2 ? info.mipLevelOffsets[2] : Vector2Int.zero;

            cb = default;
            cb._GtaoBufferSize = new Vector4(aoWidth, aoHeight, 1f / aoWidth, 1f / aoHeight);
            cb._GtaoScreenSize = new Vector4(screenWidth, screenHeight, 0f, 0f);
            cb._GtaoParams0 = new Vector4(
                // Full resolution is a compile-time FULL_RES keyword, so no resolution lane is uploaded.
                0f,
                aoHeight * invHalfTanFOV * 0.25f,
                radius,
                stepCount);
            cb._GtaoParams1 = new Vector4(
                m_Settings.intensity,
                1f / (radius * radius),
                (frame / 6) % 4,
                frame % 6);
            cb._GtaoParams2 = new Vector4(
                aoWidth,
                aoHeight,
                1f / (stepCount + 1f),
                radInPixels);
            cb._GtaoParams3 = new Vector4(bTolerance, uTolerance, noiseFilterWeight, stepSize);
            cb._GtaoParams4 = new Vector4(
                Mathf.Clamp(m_Settings.directionCount, 1, 6),
                upperNudgeFactor,
                minUpperNudgeLimit,
                m_Settings.spatialBilateralAggressiveness * 15f);
            cb._GtaoFirstTwoDepthMipOffsets = new Vector4(mip1.x, mip1.y, mip2.x, mip2.y);
            cb._GtaoDepthToViewParams = new Vector4(
                2f / (invHalfTanFOV * aspect * aoWidth),
                2f / (invHalfTanFOV * aoHeight),
                1f / (invHalfTanFOV * aspect),
                1f / invHalfTanFOV);
            cb._GtaoZBufferParams = ComputeZBufferParams(camera.nearClipPlane, camera.farClipPlane);
            cb._GtaoOrthoDepthParams = ComputeOrthoDepthParams(
                camera.nearClipPlane, camera.farClipPlane, SystemInfo.usesReversedZBuffer);
            float halfHeight = camera.orthographicSize;
            cb._GtaoOrthoHalfSize = new Vector4(halfHeight * camera.aspect, halfHeight, 0f, 0f);
            var worldToView = camera.worldToCameraMatrix;
            cb._GtaoViewRow0 = worldToView.GetRow(0);
            cb._GtaoViewRow1 = worldToView.GetRow(1);
            cb._GtaoViewRow2 = worldToView.GetRow(2);
        }

        bool UseOctEncodedNormals(ScriptableRenderer renderer)
        {
            // URP only writes octahedron-encoded normals for the deferred GBuffer when the renderer
            // enables accurate GBuffer normals; forward DepthNormals stores signed world normals.
            if (!(renderer is UniversalRenderer universalRenderer))
                return false;

            return universalRenderer.renderingModeActual == RenderingMode.Deferred && universalRenderer.accurateGbufferNormals;
        }

        void FillXrViewRows(ref CameraData cameraData, int viewCount)
        {
            int count = Mathf.Clamp(viewCount, 1, ScreenSpaceLightingXr.k_MaxViews);
            for (int viewIndex = 0; viewIndex < count; viewIndex++)
            {
                // Same convention as camera.worldToCameraMatrix: the rotation rows include Unity's
                // Z handedness flip, which keeps the view-space horizon search consistent.
                var worldToView = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * cameraData.GetViewMatrix(viewIndex);
                m_XrViewRow0[viewIndex] = worldToView.GetRow(0);
                m_XrViewRow1[viewIndex] = worldToView.GetRow(1);
                m_XrViewRow2[viewIndex] = worldToView.GetRow(2);
            }
        }

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

        // Orthographic window depth is linear in the eye distance, so it needs a scale/bias pair
        // instead of the perspective _ZBufferParams. Reversed-Z puts the near plane at 1, everything
        // else at 0.
        internal static Vector4 ComputeOrthoDepthParams(float nearPlane, float farPlane, bool reversedZ)
        {
            nearPlane = Mathf.Max(1e-5f, nearPlane);
            farPlane = Mathf.Max(nearPlane + 1e-4f, farPlane);
            float range = farPlane - nearPlane;
            return reversedZ
                ? new Vector4(-range, farPlane, 0f, 0f)
                : new Vector4(range, nearPlane, 0f, 0f);
        }

        static RTHandle GetNormalTexture(ScriptableRenderer renderer)
        {
            if (renderer is UniversalRenderer universalRenderer)
                return universalRenderer.cameraNormalsTextureHandle;

            return null;
        }

        internal static RTHandle GetMotionTexture(ScriptableRenderer renderer)
        {
            if (renderer is UniversalRenderer universalRenderer)
                return universalRenderer.m_MotionVectorColor;

            return null;
        }

        internal static void PushShaderVariables(
            CommandBuffer cmd,
            ComputeShader compute,
            in GroundTruthAmbientOcclusionShaderVariables cb)
        {
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.BufferSize, cb._GtaoBufferSize);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.ScreenSize, cb._GtaoScreenSize);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.Params0, cb._GtaoParams0);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.Params1, cb._GtaoParams1);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.Params2, cb._GtaoParams2);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.Params3, cb._GtaoParams3);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.Params4, cb._GtaoParams4);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.FirstTwoDepthMipOffsets, cb._GtaoFirstTwoDepthMipOffsets);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.DepthToViewParams, cb._GtaoDepthToViewParams);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.ZBufferParams, cb._GtaoZBufferParams);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.OrthoHalfSize, cb._GtaoOrthoHalfSize);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.OrthoDepthParams, cb._GtaoOrthoDepthParams);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.ViewRow0, cb._GtaoViewRow0);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.ViewRow1, cb._GtaoViewRow1);
            cmd.SetComputeVectorParam(compute, GroundTruthAmbientOcclusionShaderIDs.ViewRow2, cb._GtaoViewRow2);
        }

        static void SetKeyword(ComputeShader compute, string keyword, bool enabled)
        {
            if (enabled)
                compute.EnableKeyword(keyword);
            else
                compute.DisableKeyword(keyword);
        }

        static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            if (cmd == null)
                return;

            cmd.DisableShaderKeyword(ShaderKeywordStrings.ScreenSpaceOcclusion);
            cmd.SetGlobalVector(
                GroundTruthAmbientOcclusionShaderIDs.AmbientOcclusionParam,
                new Vector4(1f, 0f, 0f, 0f));
        }
    }
}