using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    internal static class GlxBasicHitTrace
    {
        internal static readonly ProfilingSampler sampler = new ProfilingSampler("Glx Basic Hit Trace");
        static readonly int s_Scene = Shader.PropertyToID("_GlxScene");
        static readonly int s_Rays = Shader.PropertyToID("_GlxRays");
        static readonly int s_Results = Shader.PropertyToID("_GlxResults");
        static readonly int s_ToLight = Shader.PropertyToID("_GlxToLight");
        static readonly int s_LightRgb = Shader.PropertyToID("_GlxLightRgb");

        internal static void Dispatch(RayTracingShader shader, RayTracingAccelerationStructure scene,
            ComputeBuffer rays, ComputeBuffer results, int count, Vector3 toLight, Vector3 lightRgb, bool shadows)
        {
            if (count == 0) return;
            var cmd = CommandBufferPool.Get();
            try
            {
                using (new ProfilingScope(cmd, sampler))
                {
                    cmd.SetRayTracingShaderPass(shader, "GlxDXR");
                    cmd.SetRayTracingAccelerationStructure(shader, s_Scene, scene);
                    cmd.SetRayTracingBufferParam(shader, s_Rays, rays);
                    cmd.SetRayTracingBufferParam(shader, s_Results, results);
                    cmd.SetRayTracingVectorParam(shader, s_ToLight, new Vector4(toLight.x, toLight.y, toLight.z, shadows ? 1 : 0));
                    cmd.SetRayTracingVectorParam(shader, s_LightRgb, lightRgb);
                    cmd.DispatchRays(shader, "GlxBasicTrace", (uint)count, 1, 1, null);
                }
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally { CommandBufferPool.Release(cmd); }
        }
    }
}
