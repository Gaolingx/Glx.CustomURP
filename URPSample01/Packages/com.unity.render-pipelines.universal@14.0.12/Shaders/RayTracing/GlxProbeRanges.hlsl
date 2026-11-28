#ifndef GLX_PROBE_RANGES_INCLUDED
#define GLX_PROBE_RANGES_INCLUDED

// Radiance rays run to this range so a miss proves the ray escaped every registered surface.
// Maintenance rays keep the finite near-field range; the ray preparation, the miss shader and
// the readback all use these two values.
#define GLX_PROBE_SKY_TMAX 3.402823466e+38F

#endif
