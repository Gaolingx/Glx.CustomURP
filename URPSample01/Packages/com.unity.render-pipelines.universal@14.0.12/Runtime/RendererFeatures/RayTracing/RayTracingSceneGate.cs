using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    [System.Flags]
    internal enum RayTracingRequests { None = 0, GI = 1, Reflections = 2 }

    internal enum RayTracingDecision
    {
        Off, RayTracingUnsupported, ComputeUnsupported, FormatUnsupported,
        CameraUnsupported, XrUnsupported, MissingShaderOrPass, IncompatibleScene, AllocationFailed, Accepted
    }

    // A value snapshot, not a cached last-camera flag. Policy and resource availability are
    // supplied by the requesting renderer; no serialized switches or volume dependencies in T1.
    internal struct RayTracingSceneGate
    {
        internal bool assetEnabled, supportsRayTracing, compute, formats;
        internal bool validCamera, xr, shaderAndPass;
        internal CameraType cameraType;
        internal CameraRenderType renderType;

        internal RayTracingDecision Evaluate(RayTracingRequests requests)
        {
            if (!assetEnabled || requests == 0 || (requests & ~(RayTracingRequests.GI | RayTracingRequests.Reflections)) != 0)
                return RayTracingDecision.Off;
            if (!supportsRayTracing) return RayTracingDecision.RayTracingUnsupported;
            if (!compute) return RayTracingDecision.ComputeUnsupported;
            if (!formats) return RayTracingDecision.FormatUnsupported;
            if (!validCamera || cameraType != CameraType.Game || renderType != CameraRenderType.Base)
                return RayTracingDecision.CameraUnsupported;
            if (xr) return RayTracingDecision.XrUnsupported;
            return shaderAndPass ? RayTracingDecision.Accepted : RayTracingDecision.MissingShaderOrPass;
        }

        internal static RayTracingSceneGate Capture(in CameraData data, bool assetEnabled,
            RayTracingShader shader, Material hitMaterial)
        {
            var platform = ScreenSpaceCapability.EvaluatePlatform();
            return new RayTracingSceneGate
            {
                assetEnabled = assetEnabled, supportsRayTracing = SystemInfo.supportsRayTracing,
                compute = SystemInfo.supportsComputeShaders, formats = platform.supported,
                validCamera = data.camera != null && data.cameraTargetDescriptor.width > 0 && data.cameraTargetDescriptor.height > 0,
                cameraType = data.cameraType, renderType = data.renderType, xr = data.xr != null && data.xr.enabled,
                shaderAndPass = shader != null && hitMaterial != null && hitMaterial.FindPass("GlxDXR") >= 0
            };
        }
    }
}
