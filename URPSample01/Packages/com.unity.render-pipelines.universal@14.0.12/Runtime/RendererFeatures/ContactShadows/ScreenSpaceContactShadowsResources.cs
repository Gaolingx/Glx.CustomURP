using System;

namespace UnityEngine.Rendering.Universal
{
    [Serializable]
    internal sealed class ScreenSpaceContactShadowsResources
    {
        public ComputeShader depthPyramidCompute = null;
        public ComputeShader contactShadowsCompute = null;
        public Shader debugShader = null;
        internal bool hasComputeResources => depthPyramidCompute != null && contactShadowsCompute != null;
    }
}
