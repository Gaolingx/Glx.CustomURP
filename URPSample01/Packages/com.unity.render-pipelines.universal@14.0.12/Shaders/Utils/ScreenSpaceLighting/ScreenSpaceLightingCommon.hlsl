#ifndef UNIVERSAL_SCREEN_SPACE_LIGHTING_COMMON_INCLUDED
#define UNIVERSAL_SCREEN_SPACE_LIGHTING_COMMON_INCLUDED

// Screen Space Global Illumination, ported from HDRP 14.0.12
// Runtime/Lighting/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute and RayMarching.hlsl.
// Changes from the HDRP source:
// - the packed depth pyramid, full resolution depth/normal, motion texture and color pyramid are
//   bound by URP explicitly (the HDRP light list and EnvLightData are not used);
// - ray directions use the blue-noise distributed Owen scrambled Sobol sequence when the package
//   LUTs are available and fall back to an interleaved-gradient based sequence otherwise;
// - the miss fallback is DDGI probe volume (the shared GlxProbeQuery), then Forward+ reflection
//   probe atlas / default cubemap (ReflectionProbeFallback.hlsl), then the sky ambient probe;
// - the reprojection writes un-premultiplied radiance plus a validity weight; a miss with no tier
//   data writes weight 0 so the material keeps the baked GI instead of darkening the pixel;
// - all private resource names carry the _Ssgi prefix.

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/Sampling.hlsl"
#include "Packages/com.unity.render-pipelines.universal/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingShaderVariables.cs.hlsl"

// The trace passes are dispatched at full or half resolution. Every other kernel derives the scale
// from _SsgiTemporalParams.x and _SsgiTraceSize.
#if defined(SSL_XR_ARRAY)
    static uint g_SsgiViewIndex;
    #define SSGI_ASSIGN_VIEW_INDEX(idx) g_SsgiViewIndex = (idx)
    #define SSGI_SLICE g_SsgiViewIndex
    #define SSGI_TEXTURE_TYPE_FLOAT Texture2DArray<float>
    #define SSGI_TEXTURE_TYPE_FLOAT4 Texture2DArray<float4>
    #define SSGI_TEXTURE2D_FLOAT(name) Texture2DArray<float> name
    #define SSGI_TEXTURE2D_FLOAT2(name) Texture2DArray<float2> name
    #define SSGI_TEXTURE2D_FLOAT4(name) Texture2DArray<float4> name
    #define SSGI_TEXTURE2D_UINT(name) Texture2DArray<uint> name
    #define SSGI_RW_TEXTURE2D_FLOAT(name) RWTexture2DArray<float> name
    #define SSGI_RW_TEXTURE2D_FLOAT2(name) RWTexture2DArray<float2> name
    #define SSGI_RW_TEXTURE2D_FLOAT4(name) RWTexture2DArray<float4> name
    #define SSGI_RW_TEXTURE2D_UINT(name) RWTexture2DArray<uint> name
    #define SSGI_LOAD_INDEX(p) int4((int2)(p), SSGI_SLICE, 0)
    #define SSGI_LOAD(t, p) (t).Load(SSGI_LOAD_INDEX(p))
    #define SSGI_SAMPLE_LOD(t, s, uv, lod) (t).SampleLevel(s, float3((uv), SSGI_SLICE), (lod))
    #define SSGI_STORE(t, p, v) t[int3((int2)(p), SSGI_SLICE)] = (v)
#else
    #define SSGI_ASSIGN_VIEW_INDEX(idx)
    #define SSGI_SLICE 0
    #define SSGI_TEXTURE_TYPE_FLOAT Texture2D<float>
    #define SSGI_TEXTURE_TYPE_FLOAT4 Texture2D<float4>
    #define SSGI_TEXTURE2D_FLOAT(name) Texture2D<float> name
    #define SSGI_TEXTURE2D_FLOAT2(name) Texture2D<float2> name
    #define SSGI_TEXTURE2D_FLOAT4(name) Texture2D<float4> name
    #define SSGI_TEXTURE2D_UINT(name) Texture2D<uint> name
    #define SSGI_RW_TEXTURE2D_FLOAT(name) RWTexture2D<float> name
    #define SSGI_RW_TEXTURE2D_FLOAT2(name) RWTexture2D<float2> name
    #define SSGI_RW_TEXTURE2D_FLOAT4(name) RWTexture2D<float4> name
    #define SSGI_RW_TEXTURE2D_UINT(name) RWTexture2D<uint> name
    #define SSGI_LOAD_INDEX(p) int3((int2)(p), 0)
    #define SSGI_LOAD(t, p) (t).Load(SSGI_LOAD_INDEX(p))
    #define SSGI_SAMPLE_LOD(t, s, uv, lod) (t).SampleLevel(s, (uv), (lod))
    #define SSGI_STORE(t, p, v) t[(int2)(p)] = (v)
#endif

// Unity's per-camera view/projection globals. URP sets unity_MatrixInvVP to the same value its
// material shaders consume, so depth reconstruction and the ray endpoint projection use exactly the
// matrices that rendered the depth buffer. Keeping them global avoids duplicating the render target
// flip decision on the CPU side.
float4x4 unity_MatrixVP;
float4x4 unity_MatrixInvVP;

// Single-pass instanced XR renders both eyes in one pass, but URP only publishes the first eye
// through the legacy unity_MatrixVP globals. The pipeline pushes one matrix set per view and the
// kernels pick the current one with the dispatch slice, mirroring HDRP's
// _XRInvViewProjMatrix[unity_StereoEyeIndex]. Multipass keeps one eye per pass and stays on the
// legacy globals.
#if defined(SSL_XR_ARRAY)
float4x4 _SslXrViewProj[2];
float4x4 _SslXrInvViewProj[2];
float4x4 _SslXrPrevViewProj[2];
float4x4 _SslXrPrevInvViewProj[2];
float4 _SslXrCameraPositionWS[2];

#define SSL_VIEW_PROJ (_SslXrViewProj[SSGI_SLICE])
#define SSL_INV_VIEW_PROJ (_SslXrInvViewProj[SSGI_SLICE])
#define SSL_PREV_INV_VIEW_PROJ (_SslXrPrevInvViewProj[SSGI_SLICE])
#define SSL_SSGI_CAMERA_POSITION_WS (_SslXrCameraPositionWS[SSGI_SLICE].xyz)
#define SSL_SSR_CAMERA_POSITION_WS (_SslXrCameraPositionWS[SSGI_SLICE].xyz)
#else
#define SSL_VIEW_PROJ unity_MatrixVP
#define SSL_INV_VIEW_PROJ unity_MatrixInvVP
#define SSL_PREV_INV_VIEW_PROJ _SsgiPrevInvViewProj
#define SSL_SSGI_CAMERA_POSITION_WS (_SsgiCameraPositionWS.xyz)
#define SSL_SSR_CAMERA_POSITION_WS (_SsrCameraPositionWS.xyz)
#endif

// The sampler names follow Unity's inline sampler conventions; the same convention is already used
// by the GTAO computes in this package.
SamplerState s_ssgi_point_clamp_sampler;
SamplerState s_ssgi_linear_clamp_sampler;

// Color pyramid mip sampled by the reprojection. HDRP uses the same offset.
#define SSGI_MIP_OFFSET 1
#define SSGI_CLAMP_VALUE 7.0
#define SSGI_DEPTH_DIFFERENCE_THRESHOLD 0.1

float2 SsgiGetPixelUv(uint2 pixel, float2 invSize)
{
    return ((float2)pixel + 0.5) * invSize;
}

bool SsgiIsBackgroundDepth(float deviceDepth)
{
    return deviceDepth == UNITY_RAW_FAR_CLIP_VALUE;
}

// Full resolution world-space normal. Forward depth normals store a signed normal, deferred accurate
// GBuffers store the octahedron encoding. A zero normal marks a pixel outside the prepass.
float3 SsgiDecodeNormalSS(float4 normalBuffer)
{
#if defined(SSGI_NORMALS_OCT)
    float2 remappedOctNormalWS = Unpack888ToFloat2(normalBuffer.rgb);
    return UnpackNormalOctQuadEncode(remappedOctNormalWS * 2.0 - 1.0);
#else
    float3 rawNormalWS = normalBuffer.rgb;
    float lenSq = dot(rawNormalWS, rawNormalWS);
    return lenSq > 1e-8 ? rawNormalWS * rsqrt(lenSq) : float3(0.0, 0.0, 0.0);
#endif
}

float3 SsgiReconstructPositionWS(float2 positionSS, float deviceDepth)
{
    float2 uv = (positionSS + 0.5) * _SsgiScreenSize.zw;
    return ComputeWorldSpacePosition(uv, deviceDepth, SSL_INV_VIEW_PROJ);
}

// Maps a trace-resolution pixel to the full resolution pixel it represents. Mirrors HDRP's
// GetLowResCoord; kept in one place because the trace, reproject and temporal passes share it.
uint2 SsgiGetFullResCoord(uint2 traceCoord)
{
#if defined(SSGI_HALF_RES)
    uint2 fullRes = (uint2)round((float2)(traceCoord + 0.5f) * _SsgiRayMarchingParams2.x);
    return min(fullRes, (uint2)_SsgiScreenSize.xy - 1u);
#else
    return traceCoord;
#endif
}

// Maps an effect-resolution pixel to the full-resolution pixel that owns its depth, normal and
// motion. HDRP's ray traced reflection denoiser reads the top-left pixel of each 2x2 group
// (ComputeSourceCoordinates returns effectCoord * 2), so the trace and the denoiser must share this
// mapping or they sample different sub-pixels. At full resolution the scale is 1 and this is the
// identity.
uint2 SslComputeSourceCoordinates(uint2 effectCoord, float2 screenSize, float2 effectSize)
{
    uint2 source = (uint2)floor((float2)effectCoord * (screenSize / effectSize));
    return min(source, (uint2)screenSize - 1u);
}

float3 SsgiClampRadiance(float3 color)
{
    if (!IsFinite(color.r) || !IsFinite(color.g) || !IsFinite(color.b))
        return 0.0;

    color = max(color, 0.0);
    // HDRP clamps the value channel in HSV space so a single very bright bounce does not dominate
    // the temporal accumulation.
    color = RgbToHsv(color);
    color.z = min(color.z, SSGI_CLAMP_VALUE);
    return HsvToRgb(color);
}

// Maps the perceptual smoothness stored by the material to the SSR roughness fade.
float PerceptualRoughnessFade(float perceptualRoughness, float fadeRcpLength, float fadeEndTimesRcpLength)
{
    float t = Remap10(perceptualRoughness, fadeRcpLength, fadeEndTimesRcpLength);
    return Smoothstep01(t);
}

// Fades the screen-space result out near the border so a reflection never pops when its hit point
// leaves the viewport.
float EdgeOfScreenFade(float2 coordNDC, float fadeRcpLength)
{
    float2 coordCS = coordNDC * 2.0 - 1.0;
    float2 t = Remap10(abs(coordCS), fadeRcpLength, fadeRcpLength);
    return Smoothstep01(t.x) * Smoothstep01(t.y);
}

#endif