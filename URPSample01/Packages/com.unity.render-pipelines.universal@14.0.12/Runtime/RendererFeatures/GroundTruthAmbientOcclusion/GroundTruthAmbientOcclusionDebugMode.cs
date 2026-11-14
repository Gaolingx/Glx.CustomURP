namespace UnityEngine.Rendering.Universal
{
    // Intermediate views written by the debug pass. Off leaves the final image untouched.
    internal enum GroundTruthAmbientOcclusionDebugMode
    {
        Off = 0,
        AmbientOcclusion = 1,
        DepthPyramid = 2,
        RawAmbientOcclusion = 3,
        GlobalAmbientOcclusion = 4,
    }
}