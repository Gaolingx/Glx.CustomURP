using System.Collections.Generic;

namespace UnityEngine.Rendering.Universal
{
    // Shares one packed depth mip chain between Screen Space Lighting and Ground Truth Ambient
    // Occlusion. The frame's first consumer enqueues ScreenSpaceDepthPyramidPass; every later
    // consumer binds the same attachment, mirroring HDRP where the prepass builds the chain once
    // and AO, SSGI and SSR receive the same texture handle.
    internal static class ScreenSpaceDepthPyramidSystem
    {
        const int k_MaxViews = 2;

        static CameraHistoryStore<ScreenSpaceDepthPyramid> s_Store;
        static ScreenSpaceDepthPyramidPass s_Pass;
        static readonly HashSet<ScriptableRendererFeature> s_Owners = new HashSet<ScriptableRendererFeature>();

        internal static ScreenSpaceDepthPyramidPass sharedPass => s_Pass;

        // Features call this from Create so the shared store lives exactly as long as at least one
        // consumer exists; Create can run several times, so registration is idempotent.
        internal static void RegisterOwner(ScriptableRendererFeature owner)
        {
            if (owner == null)
                return;

            // Disabled domain reload can leave destroyed feature instances registered across play
            // mode transitions; dropping them here keeps the owner count honest.
            s_Owners.RemoveWhere(candidate => candidate == null);
            s_Owners.Add(owner);
        }

        internal static void UnregisterOwner(ScriptableRendererFeature owner)
        {
            if (owner == null || !s_Owners.Remove(owner) || s_Owners.Count > 0)
                return;

            Dispose();
        }

        internal static ScreenSpaceDepthPyramid Acquire(
            ScriptableRenderer renderer,
            in RenderingData renderingData,
            ComputeShader compute,
            int maxUnusedFrames,
            int maxCachedCameras)
        {
            if (renderer == null || compute == null)
                return null;

            var cameraData = renderingData.cameraData;
            int frameIndex = Time.frameCount;

            if (s_Store == null)
            {
                s_Store = new CameraHistoryStore<ScreenSpaceDepthPyramid>();
                s_Pass = new ScreenSpaceDepthPyramidPass();
            }

            s_Store.PruneDestroyedCameras();
            s_Store.PruneUnused(frameIndex, maxUnusedFrames);
            s_Store.TrimToCount(maxCachedCameras);

            int viewCount = cameraData.xr.enabled ? Mathf.Clamp(cameraData.xr.viewCount, 1, k_MaxViews) : 1;
            var dimension = viewCount > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;
            int xrEye = cameraData.xr.enabled ? cameraData.xr.multipassId : 0;
            var descriptor = cameraData.cameraTargetDescriptor;

            var pyramid = s_Store.GetOrCreate(
                cameraData.camera,
                xrEye,
                descriptor.width,
                descriptor.height,
                frameIndex,
                0,
                out _);

            pyramid.Prepare(descriptor.width, descriptor.height, viewCount, dimension);

            if (pyramid.ShouldBuild(frameIndex))
            {
                s_Pass.Setup(pyramid, compute);
                renderer.EnqueuePass(s_Pass);
            }

            return pyramid;
        }

        static void Dispose()
        {
            s_Store?.Dispose();
            s_Store = null;
            s_Pass = null;
        }
    }
}