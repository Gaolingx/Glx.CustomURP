#ifndef GLX_PUBLISHED_HIT_INCLUDED
#define GLX_PUBLISHED_HIT_INCLUDED

// C2 published result head. Reflection mode carries more surface data through the ray
// payload (C2.7), but every result buffer and every consumer keeps this frozen layout.
// stride 24
struct GlxPublishedHit
{
    float3 radiance;
    float hitT;
    uint flags;
    uint mode;
};

#endif
