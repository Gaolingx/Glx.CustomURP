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
    }
}