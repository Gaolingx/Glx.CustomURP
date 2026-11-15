# SSR hole filling for the approximation path (2026-11-16)

## Symptom

Smooth floors showed broken reflections: single pixel holes wherever the mirror ray just missed, which
reads as a ragged or "punched" reflection instead of a continuous image. The PBR accumulation path
already gathers a 3x3 neighbourhood (HDRP's reprojection gather), but the Approximation path used a
single tap and wrote weight 0 for every miss.

## Change

`ScreenSpaceReflections.compute`, Approximation branch of `ScreenSpaceReflectionsReprojection`:

- a pixel whose own ray missed is filled from the same 3x3 cross HDRP's PBR reprojection gathers;
- a neighbour is accepted only when it belongs to the same surface (world position within 1% of the
  view distance, normal dot >= 0.9), is eligible for SSR itself (roughness <= `_SsrRoughnessFadeEnd`)
  and produced a valid hit;
- the filled radiance is the opacity-weighted average of the accepted neighbours and the filled
  weight is `sum(opacity) / 4`, so a hole surrounded by four valid hits is fully covered, a lone
  neighbour only partially, and a region where every ray misses still keeps the reflection probe.

Valid single-tap pixels are untouched, so sharp reflections are not blurred. The roughness fade
contract and the "miss keeps the probe" contract both stay intact, which the T7 probes below confirm.

## Verification

`ScreenSpaceLightingT7Verification` (Forward / Forward+ / Deferred): 3/3 `passed=true`.
`ScreenSpaceLightingT8Verification` (PBR, 9 speed rejection combinations): 3/3 `passed=true`.

Weight map on the T7 rig (1280x720, `T7_ForwardPlus_SsrWeight.png`):

| Metric | Before | After |
|---|---|---|
| Pixels with weight > 0.5 | 96,040 | 98,960 |
| Pixels filled by the gather | - | 2,920 |
| Pixels lost | - | 0 |
| Mean weight, metal sphere 0.95 | 0.0671 | 0.0779 |
| Mean weight, dielectric 0.95 | 0.0346 | 0.0439 |
| Max weight per probe | 1.0000 / 0.8301 / 0.3289 / 0 | unchanged |
| Rough metal (0.40) weight | 0 | 0 |

The maximum weight per sphere still matches the analytic roughness fade exactly, so only missed
pixels changed. `Before_ForwardPlus_SsrWeight.png` is the same scene without the gather.

## Not changed

- HDRP's Approximation path does not gather; this is a deliberate extension requested for artifact
  reduction. The algorithm is identical to HDRP's PBR gather minus the GGX value/pdf weighting,
  because the approximation ray is a single mirror ray.
- Ray marching still clamps to mip 6 (`SSR_MIP_LEVEL_CLAMP`) while HDRP uses
  `_SsrDepthPyramidMaxMip`; long grazing rays are therefore still limited. Aligning it is a separate
  fidelity change.

## Evidence

`Before_ForwardPlus_SsrWeight.png`, `After_ForwardPlus_SsrWeight.png`, `After_ForwardPlus_SsrRaw.png`,
`After_ForwardPlus_SsrFullRes.png`, `T7VerificationAfterGather.json`,
`T8VerificationAfterGather.json`, `UnityT7AfterGather.log`, `UnityT8AfterGather.log`.