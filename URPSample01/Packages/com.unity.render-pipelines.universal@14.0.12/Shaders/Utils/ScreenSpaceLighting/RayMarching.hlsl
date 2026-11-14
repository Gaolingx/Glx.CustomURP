#ifndef UNIVERSAL_SCREEN_SPACE_LIGHTING_RAYMARCHING_INCLUDED
#define UNIVERSAL_SCREEN_SPACE_LIGHTING_RAYMARCHING_INCLUDED

// Screen-space Hi-Z ray marching, ported from HDRP 14.0.12
// Runtime/Lighting/ScreenSpaceLighting/RayMarching.hlsl. The packed depth atlas and its mip offset
// buffer are owned by ScreenSpaceLightingRendererFeature; the shader only reads them.

#include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceLightingCommon.hlsl"

// Private packed depth pyramid of this feature, built by ScreenSpaceLightingDepthPyramidPass.
SSGI_TEXTURE2D_FLOAT(_SsgiDepthPyramid);
StructuredBuffer<int2> _SsgiDepthPyramidMipLevelOffsets;

#define SSGI_RAY_TRACE_EPS 0.00024414

#define _SsgiRayMarchingSteps (int)_SsgiRayMarchingParams.x
#define _SsgiRayMarchingThicknessScale _SsgiRayMarchingParams.y
#define _SsgiRayMarchingThicknessBias _SsgiRayMarchingParams.z
#define _SsgiRayMarchingReflectsSky (int)_SsgiRayMarchingParams.w

bool SsgiRayMarch(float3 positionWS, float3 sampleDir, float3 normalWS, float2 positionSS, float deviceDepth, bool killRay, out float3 rayPos)
{
    rayPos = float3(-1.0, -1.0, -1.0);
    bool status = false;

    // The ray starts at the centre of the current pixel and is extended to the far plane.
    float3 rayOrigin = float3(positionSS + 0.5, deviceDepth);

    float3 reflPosWS = positionWS + sampleDir;
    float3 reflPosNDC = ComputeNormalizedDeviceCoordinatesWithZ(reflPosWS, unity_MatrixVP);
    float3 reflPosSS = float3(reflPosNDC.xy * _SsgiScreenSize.xy, reflPosNDC.z);
    float3 rayDir = reflPosSS - rayOrigin;
    float3 rcpRayDir = rcp(rayDir);
    int2 rayStep = int2(rcpRayDir.x >= 0 ? 1 : 0,
                        rcpRayDir.y >= 0 ? 1 : 0);
    float3 raySign = float3(rcpRayDir.x >= 0 ? 1 : -1,
                            rcpRayDir.y >= 0 ? 1 : -1,
                            rcpRayDir.z >= 0 ? 1 : -1);
    bool rayTowardsEye = rcpRayDir.z >= 0;

    killRay = killRay || (reflPosSS.z <= 0);

    if (killRay)
        return false;

    // Clip the ray to the frustum by shrinking it half a texel.
    float tMax;
    {
        const float halfTexel = 0.5;
        float3 bounds;
        bounds.x = (rcpRayDir.x >= 0) ? _SsgiScreenSize.x - halfTexel : halfTexel;
        bounds.y = (rcpRayDir.y >= 0) ? _SsgiScreenSize.y - halfTexel : halfTexel;
        float maxDepth = (_SsgiRayMarchingReflectsSky != 0) ? -0.00000024 : 0.00000024;
        bounds.z = (rcpRayDir.z >= 0) ? 1 : maxDepth;

        float3 dist = bounds * rcpRayDir - (rayOrigin * rcpRayDir);
        tMax = Min3(dist.x, dist.y, dist.z);
    }

    // Advance to the next texel to avoid self intersections.
    float t;
    {
        float2 dist = abs(0.5 * rcpRayDir.xy);
        t = min(dist.x, dist.y);
    }

    int mipLevel = 0;
    int2 mipOffset = _SsgiDepthPyramidMipLevelOffsets[mipLevel];
    int iterCount = 0;
    bool hit = false;
    bool miss = false;
    bool belowMip0 = false;

    while (!(hit || miss) && (t <= tMax) && (iterCount < _SsgiRayMarchingSteps))
    {
        rayPos = rayOrigin + t * rayDir;

        float2 sgnEdgeDist = round(rayPos.xy) - rayPos.xy;
        float2 satEdgeDist = clamp(raySign.xy * sgnEdgeDist + SSGI_RAY_TRACE_EPS, 0, SSGI_RAY_TRACE_EPS);
        rayPos.xy += raySign.xy * satEdgeDist;

        int2 mipCoord = (int2)rayPos.xy >> mipLevel;
        float4 bounds;
        bounds.z = SSGI_LOAD(_SsgiDepthPyramid, mipOffset + mipCoord);
        bounds.xy = (mipCoord + rayStep) << mipLevel;
        bounds.w = bounds.z * _SsgiRayMarchingThicknessScale + _SsgiRayMarchingThicknessBias;

        float4 dist = bounds * rcpRayDir.xyzz - (rayOrigin.xyzz * rcpRayDir.xyzz);
        float distWall = min(dist.x, dist.y);
        float distFloor = dist.z;
        float distBase = dist.w;

        bool belowFloor = rayPos.z < bounds.z;
        bool aboveBase = rayPos.z >= bounds.w;

        bool insideFloor = belowFloor && aboveBase;
        bool hitFloor = (t <= distFloor) && (distFloor <= distWall);

        miss = belowMip0 && insideFloor;
        hit = (mipLevel == 0) && (hitFloor || insideFloor);
        belowMip0 = (mipLevel == 0) && belowFloor;

        t = hitFloor ? distFloor : (((mipLevel != 0) && belowFloor) ? t : distWall);
        rayPos.z = bounds.z;

        mipLevel += (hitFloor || belowFloor || rayTowardsEye) ? -1 : 1;
        mipLevel = clamp(mipLevel, 0, 6);
        mipOffset = _SsgiDepthPyramidMipLevelOffsets[mipLevel];
        iterCount++;
    }

    miss = miss || ((_SsgiRayMarchingReflectsSky == 0) && (rayPos.z == 0));
    status = hit && !miss;
    return status;
}

#endif