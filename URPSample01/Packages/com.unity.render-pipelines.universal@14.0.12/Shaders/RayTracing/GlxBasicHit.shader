Shader "Hidden/Universal Render Pipeline/GlxBasicHit"
{
    Properties
    {
        _GlxAlbedo ("Linear Albedo", Vector) = (1, 1, 1, 0)
        _GlxEmission ("Linear Emission", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        // Keep a conventional first pass for Unity material pass discovery.
        // URP does not schedule this LightMode for camera rendering.
        Pass
        {
            Name "RasterPlaceholder"
            Tags { "LightMode" = "GlxRasterPlaceholder" }
            ColorMask 0
            ZWrite Off
        }
        Pass
        {
            Name "GlxDXR"
            Tags { "LightMode" = "RayTracing" }
            HLSLPROGRAM
            #pragma raytracing GlxBasicHit
            #include "UnityRayTracingMeshUtils.cginc"
            #include "GlxBasicPayload.hlsl"

            cbuffer UnityPerMaterial
            {
                float4 _GlxAlbedo;
                float4 _GlxEmission;
            };

            float3 GlxGeometricNormalWS()
            {
                uint3 tri = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
                float3 a = UnityRayTracingFetchVertexAttribute3(tri.x, kVertexAttributePosition);
                float3 b = UnityRayTracingFetchVertexAttribute3(tri.y, kVertexAttributePosition);
                float3 c = UnityRayTracingFetchVertexAttribute3(tri.z, kVertexAttributePosition);
                float3x3 objectToWorld = (float3x3)ObjectToWorld3x4();
                float3 n = normalize(cross(mul(objectToWorld, b - a), mul(objectToWorld, c - a)));
                return dot(n, WorldRayDirection()) > 0.0 ? -n : n;
            }

            [shader("closesthit")]
            void GlxBasicClosestHit(inout GlxBasicPayload payload : SV_RayPayload,
                                    BuiltInTriangleIntersectionAttributes attributes : SV_IntersectionAttributes)
            {
                if (payload.mode == GlxShadowMode)
                {
                    payload.flags = GlxHit;
                    return;
                }
                float3 toLight = normalize(float3(f16tof32(payload.flags & 0xffffu), f16tof32(payload.flags >> 16), payload.hitT));
                float3 lightRgb = payload.radiance;
                bool traceShadow = payload.mode == GlxPrimaryWithShadow;
                payload.mode = GlxPrimaryMode;
                payload.flags = GlxHit;
                payload.hitT = RayTCurrent();
                payload.radiance = _GlxEmission.rgb;
                float3 normalWS = GlxGeometricNormalWS();
                float nDotL = saturate(dot(normalWS, toLight));
                if (nDotL <= 0.0)
                    return;

                if (traceShadow)
                {
                    float3 hitWS = WorldRayOrigin() + payload.hitT * WorldRayDirection();
                    RayDesc shadow;
                    shadow.Origin = hitWS + normalWS * 0.002;
                    shadow.Direction = toLight;
                    shadow.TMin = 0.0;
                    shadow.TMax = 3.402823466e+38F;
                    GlxBasicPayload visibility = GlxEmptyPayload(GlxShadowMode);
                    TraceRay(_GlxScene, RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH,
                             0xff, 0, 1, 0, shadow, visibility);
                    if (visibility.flags == GlxHit)
                    {
                        payload.flags |= GlxShadowed;
                        return; // Emission survives occlusion.
                    }
                }
                const float invPi = 0.31830988618;
                payload.radiance += (_GlxAlbedo.rgb * invPi) * lightRgb * nDotL;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
