#ifndef GLX_SSR_RAY_REQUEST_INCLUDED
#define GLX_SSR_RAY_REQUEST_INCLUDED
// Same 64-byte record in classification, compaction, raygeneration and hit shading. The four
// fields that had no consumer (sequence key, source pixel, material/instance id) were dropped.
struct R1Sample
{
    uint slot, reason;
    float3 position; float roughness;
    float3 normal; float tMin;
    float3 direction; float tMax;
    float ssShare, requestShare;
};
// B6.2 early screen: the temporal, the P3 reproject, the PBR merge and the composite read this
// 12-byte record first and only fetch the 64-byte R1Sample once the slot is in the hybrid domain.
// ssShare/requestShare stay fp32 so the coverage math is bit-identical to the record path.
struct R1Coverage
{
    float ssShare;
    float requestShare;
    uint reason;
};
#endif
