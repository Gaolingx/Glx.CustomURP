using System;

namespace UnityEngine.Rendering.Universal
{
    [Serializable, ReloadGroup]
    internal sealed class ScreenSpaceContactShadowsResources
    {
        [SerializeField, Reload("Shaders/Utils/ScreenSpaceCommon/DepthPyramid.compute")]
        internal ComputeShader depthPyramidCompute;

        [SerializeField, Reload("Shaders/Utils/ContactShadows/ContactShadows.compute")]
        internal ComputeShader contactShadowsCompute;

        [SerializeField, Reload("Shaders/Utils/ContactShadows/ContactShadowDebug.shader")]
        internal Shader debugShader;

        internal bool hasComputeResources => depthPyramidCompute != null && contactShadowsCompute != null;
    }
}
