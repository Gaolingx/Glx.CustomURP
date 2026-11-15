using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    // Per-view camera data for XR. URP only publishes the first view through the legacy
    // unity_MatrixVP globals, so the Screen Space Lighting compute kernels receive explicit per-view
    // matrices and read the current view by dispatch slice. Mirrors HDRP's ShaderVariablesXR.
    internal struct ScreenSpaceLightingXrViewConstants
    {
        public Matrix4x4 viewProj;
        public Matrix4x4 invViewProj;
        public Vector3 cameraPositionWS;
        public Matrix4x4 prevViewProj;
        public Matrix4x4 prevInvViewProj;
        public bool hasPreviousViewProj;
    }

    internal static class ScreenSpaceLightingXr
    {
        internal const int k_MaxViews = 2;

        internal static int GetViewCount(in CameraData cameraData)
        {
            if (!cameraData.xr.enabled)
                return 1;

            return Mathf.Clamp(cameraData.xr.viewCount, 1, k_MaxViews);
        }

        // Single-pass instanced renders both eyes in one pass, so private resources become texture
        // arrays. Multipass renders one eye per camera pass and keeps plain textures.
        internal static bool IsTextureArray(in CameraData cameraData)
        {
            return cameraData.xr.enabled && GetViewCount(cameraData) > 1;
        }

        internal static TextureDimension GetResourceDimension(in CameraData cameraData)
        {
            return IsTextureArray(cameraData) ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;
        }

        internal static Matrix4x4 GetViewProjection(in CameraData cameraData, int viewIndex)
        {
            // Matches the legacy unity_MatrixVP that URP sets for the first view: GPU projection
            // (platform Y flip and reverse Z) times the jittered view matrix.
            return cameraData.GetGPUProjectionMatrix(viewIndex) * cameraData.GetViewMatrix(viewIndex);
        }

        internal static Vector3 GetCameraPositionWS(in CameraData cameraData, int viewIndex)
        {
            // The eye centre. Multipass also uses this so the ray origin is the rendered eye, not the
            // mono camera centre that URP exposes through camera.transform.position.
            return cameraData.GetViewMatrix(viewIndex).inverse.GetColumn(3);
        }

        internal static void FillViewConstants(
            in CameraData cameraData,
            int viewCount,
            ScreenSpaceLightingXrViewConstants[] views,
            ScreenSpaceLightingHistory history)
        {
            for (int viewIndex = 0; viewIndex < viewCount; viewIndex++)
            {
                ref var view = ref views[viewIndex];
                view.viewProj = GetViewProjection(cameraData, viewIndex);
                view.invViewProj = Matrix4x4.Inverse(view.viewProj);
                view.cameraPositionWS = GetCameraPositionWS(cameraData, viewIndex);
                view.hasPreviousViewProj = history != null &&
                                           history.TryGetPreviousViewProj(viewIndex, out view.prevViewProj);
                if (!view.hasPreviousViewProj)
                    view.prevViewProj = view.viewProj;
                view.prevInvViewProj = Matrix4x4.Inverse(view.prevViewProj);
            }
        }

        // Pushes the per-view matrices to a compute shader. The kernels select the view with the
        // dispatch slice (SSL_XR_ARRAY), exactly like HDRP selects _XRInvViewProjMatrix with
        // unity_StereoEyeIndex.
        internal static void BindComputeViewConstants(
            CommandBuffer cmd,
            ScreenSpaceLightingXrViewConstants[] views,
            int viewCount,
            Matrix4x4[] viewProjScratch,
            Matrix4x4[] invViewProjScratch,
            Matrix4x4[] prevViewProjScratch,
            Matrix4x4[] prevInvViewProjScratch,
            Vector4[] cameraPositionScratch)
        {
            if (cmd == null || viewCount <= 1)
                return;

            viewCount = Mathf.Clamp(viewCount, 1, k_MaxViews);
            for (int viewIndex = 0; viewIndex < viewCount; viewIndex++)
            {
                viewProjScratch[viewIndex] = views[viewIndex].viewProj;
                invViewProjScratch[viewIndex] = views[viewIndex].invViewProj;
                prevViewProjScratch[viewIndex] = views[viewIndex].prevViewProj;
                prevInvViewProjScratch[viewIndex] = views[viewIndex].prevInvViewProj;
                var position = views[viewIndex].cameraPositionWS;
                cameraPositionScratch[viewIndex] = new Vector4(position.x, position.y, position.z, 1f);
            }

            // Global uniforms are visible to every compute that includes ScreenSpaceLightingCommon.hlsl and
            // keeps the same binding path HDRP uses for its XR matrices.
            cmd.SetGlobalMatrixArray(ScreenSpaceLightingShaderIDs.SslXrViewProj, viewProjScratch);
            cmd.SetGlobalMatrixArray(ScreenSpaceLightingShaderIDs.SslXrInvViewProj, invViewProjScratch);
            cmd.SetGlobalMatrixArray(ScreenSpaceLightingShaderIDs.SslXrPrevViewProj, prevViewProjScratch);
            cmd.SetGlobalMatrixArray(ScreenSpaceLightingShaderIDs.SslXrPrevInvViewProj, prevInvViewProjScratch);
            cmd.SetGlobalVectorArray(ScreenSpaceLightingShaderIDs.SslXrCameraPositionWS, cameraPositionScratch);
        }

        // Foveated rendering can lower the raster shading rate. Every full-screen pass that writes a
        // private screen-space resource disables it while it draws and restores the camera state
        // afterwards, matching URP's own screen-space passes. Compute dispatches are not rasterized
        // and need no guard.
        internal static bool DisableFoveatedRendering(CommandBuffer cmd, in CameraData cameraData)
        {
#if ENABLE_VR && ENABLE_XR_MODULE
            if (cmd != null && cameraData.xr.enabled && cameraData.xr.supportsFoveatedRendering)
            {
                cmd.SetFoveatedRenderingMode(FoveatedRenderingMode.Disabled);
                return true;
            }
#endif
            return false;
        }

        internal static void RestoreFoveatedRendering(CommandBuffer cmd, bool disabled)
        {
#if ENABLE_VR && ENABLE_XR_MODULE
            if (disabled && cmd != null)
                cmd.SetFoveatedRenderingMode(FoveatedRenderingMode.Enabled);
#endif
        }
    }
}