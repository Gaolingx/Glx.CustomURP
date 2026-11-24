namespace UnityEngine.Rendering.Universal
{
    internal static class ScreenSpaceContactShadowsShaderIDs
    {
        internal const string deferredKeyword = "_CONTACT_SHADOWS_DEFERRED";
        internal static readonly int texture = Shader.PropertyToID("_ContactShadowTexture");
        internal static readonly int output = Shader.PropertyToID("_ContactShadowOutput");
        internal static readonly int depthPyramid = Shader.PropertyToID("_ContactShadowDepthPyramid");
        internal static readonly int mip0 = Shader.PropertyToID("_ContactShadowMip0");
        internal static readonly int mip1 = Shader.PropertyToID("_ContactShadowMip1");
        internal static readonly int lightBuffer = Shader.PropertyToID("_ContactShadowLightBuffer");
        internal static readonly int lightCount = Shader.PropertyToID("_ContactShadowLightCount");
        internal static readonly int mainLightMask = Shader.PropertyToID("_MainLightContactShadowMask");
        internal static readonly int additionalLightMasks = Shader.PropertyToID("_AdditionalLightsContactShadowMasks");
        internal static readonly int opacity = Shader.PropertyToID("_ContactShadowOpacity");
        internal static readonly int screenSize = Shader.PropertyToID("_ContactShadowScreenSize");
        internal static readonly int traceParams = Shader.PropertyToID("_ContactShadowTraceParams");
        internal static readonly int distanceParams = Shader.PropertyToID("_ContactShadowDistanceParams");
        internal static readonly int sampleCount = Shader.PropertyToID("_ContactShadowSampleCount");
        internal static readonly int frameIndex = Shader.PropertyToID("_ContactShadowFrameIndex");
        internal static readonly int taaEnabled = Shader.PropertyToID("_ContactShadowTaaEnabled");
        internal static readonly int view = Shader.PropertyToID("_ContactShadowView");
        internal static readonly int viewProjection = Shader.PropertyToID("_ContactShadowViewProjection");
        internal static readonly int invViewProjection = Shader.PropertyToID("_ContactShadowInvViewProjection");
        internal static readonly int projection = Shader.PropertyToID("_ContactShadowProjection");
        internal static readonly int cameraPosWS = Shader.PropertyToID("_ContactShadowCameraPosWS");
        internal static readonly int debugMode = Shader.PropertyToID("_ContactShadowDebugMode");
        internal static readonly int debugSlot = Shader.PropertyToID("_ContactShadowDebugSlot");
    }
}
