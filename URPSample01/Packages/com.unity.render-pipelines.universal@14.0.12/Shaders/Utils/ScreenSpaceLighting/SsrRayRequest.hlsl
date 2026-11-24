#ifndef GLX_SSR_RAY_REQUEST_INCLUDED
#define GLX_SSR_RAY_REQUEST_INCLUDED
// Same 80-byte record in classification, compaction and raygeneration.
struct R1Sample
{
    uint slot, sequenceFrame, reason, sourcePixel;
    float3 position; float roughness;
    float3 normal; float tMin;
    float3 direction; float tMax;
    float ssShare, requestShare; uint materialId, instanceId;
};
#endif
