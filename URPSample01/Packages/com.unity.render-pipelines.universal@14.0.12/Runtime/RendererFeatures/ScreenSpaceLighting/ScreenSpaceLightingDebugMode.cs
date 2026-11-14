namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Intermediate view of the Screen Space Lighting private resources. Debug rendering only runs
    /// while a mode other than <see cref="Off"/> is selected and never feeds the material lighting path.
    /// </summary>
    public enum ScreenSpaceLightingDebugMode
    {
        /// <summary>No debug pass is enqueued.</summary>
        Off = 0,
        /// <summary>Private packed depth pyramid, sampled at the mip selected by debugMip.</summary>
        DepthPyramid = 1,
        /// <summary>Color pyramid generated before transparents this frame.</summary>
        CurrentColorPyramid = 2,
        /// <summary>Color pyramid captured after transparents for the next frame.</summary>
        PreviousColor = 3,
        /// <summary>Depth captured together with the previous color history.</summary>
        PreviousDepth = 4,
        /// <summary>Atlas UV of the selected packed depth pyramid mip, used to verify mip scale and offset.</summary>
        DepthPyramidUv = 5,
        /// <summary>The camera depth texture exactly as the pyramid pass reads it; diagnostic for source binding.</summary>
        CameraDepth = 6,
        /// <summary>Radiance reprojected from the previous color pyramid before any denoising.</summary>
        SsgiRaw = 7,
        /// <summary>The final denoised SSGI radiance that materials consume.</summary>
        SsgiDenoised = 8,
        /// <summary>This frame's accumulated SSGI history (radiance, alpha is the validity weight).</summary>
        SsgiHistory = 9,
        /// <summary>Validity weight of the final SSGI result. Black means the material keeps baked GI.</summary>
        SsgiWeight = 10,
        /// <summary>Reflection radiance reprojected from the previous color pyramid (opaque SSR).</summary>
        SsrRaw = 11,
        /// <summary>Blend weight of the opaque SSR result. Black means the material keeps the reflection probe.</summary>
        SsrWeight = 12,
    }
}