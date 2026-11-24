#ifndef GLX_HIT_LIGHT_COUNTERS_INCLUDED
#define GLX_HIT_LIGHT_COUNTERS_INCLUDED
#include "GlxBasicPayload.hlsl"

// Raygen only. Table CHS returns integer counts in the high bits of mode; consume
// them once after primary TraceRay, before publishing the ordinary 24-byte result.
RWStructuredBuffer<uint> _GlxLightCounters;
void GlxRecordLightCounters(inout GlxBasicPayload p)
{
    uint selected = (p.mode >> 8) & 7u;
    uint rejected = (p.mode >> 11) & 63u;
    uint rays = (p.mode >> 17) & 3u;
    uint deferred = (p.mode >> 19) & 7u;
    if (selected) InterlockedAdd(_GlxLightCounters[0], selected);
    if (rejected) InterlockedAdd(_GlxLightCounters[1], rejected);
    if (rays) InterlockedAdd(_GlxLightCounters[2], rays);
    if (deferred) InterlockedAdd(_GlxLightCounters[3], deferred);
    p.mode = GlxPrimaryMode;
}
#endif
