# SSR reflections missing: ray origins rebuilt with the wrong screen size (2026-11-15)

## Symptom

Enabling Screen Space Reflections produced no visible reflection: a mirror-like floor stayed matte and
the reflective spheres only showed their reflection probe. The Frame Debugger showed the whole
dispatch chain running (trace + reproject + accumulate), `_SsrHitPointTextureRW` full of hits and
`_SsrOutputTextureRW` with weight 1 over the floor, but the radiance read back from the color pyramid
was almost black. Console also printed `CommandBuffer: temporary render texture not found while
executing (SetComputeTextureParam)` twice when entering Play mode (not reproduced in batch, see
follow-ups).

## Reproduction (before the fix)

`ScreenSpaceLightingT3Verification` + `SslT7ForwardPlusRenderer`, ground temporarily forced to a
metallic mirror (`_Metallic 1`, `_Smoothness 0.95`), SSR on/off and the plugin's debug views captured
at 1280x720 (`SsrPass` debug modes). Batch-mode camera renders, mirror ground:

| Probe | Before | After |
|---|---|---|
| final image, SSR on vs off | means differ by 4e-4: reflection invisible | sphere / point-light reflections visible |
| `_SsrOutputTextureRW` rgb mean | (0.0025, 0.0007, 0.0068) - floor radiance black | (0.0019, 0.0012, 0.0016) - floor resolves geometry |
| `_SsrOutputTextureRW` alpha mean | 0.2937 (whole floor covered) | 0.0264 (only rays that stay on screen) |
| `_SsrColorPyramid` (previous frame) sky mean | (0.6149, 0.8259, 0.9684) | unchanged |

The color pyramid content is correct, so neither the depth chain nor the pyramid capture was at
fault. The Frame Debugger's SSR event lists only `_SsrColorPyramidScaleAndLimitPrev`,
`_SsrPyramidParams`, `_SsrRoughnessFadeParams` and `_SsrScreenSize` in **Vectors**: the SSGI screen
size uniform was never uploaded to `ScreenSpaceReflections.compute`.

## Root cause

`ScreenSpaceReflections.compute` called `SsgiReconstructPositionWS()` from the shared
`ScreenSpaceLightingCommon.hlsl`:

```hlsl
float3 SsgiReconstructPositionWS(float2 positionSS, float deviceDepth)
{
    float2 uv = (positionSS + 0.5) * _SsgiScreenSize.zw;
    return ComputeWorldSpacePosition(uv, deviceDepth, SSL_INV_VIEW_PROJ);
}
```

`_SsgiScreenSize` is a field of `ScreenSpaceLightingSsgiShaderVariables`. The SSR compute shader
includes the same header but only `_Ssr*` fields are uploaded for it (`SsrPass.CreateShaderVariables`
/ `PushShaderVariables`), and the SSGI pass uploads its buffer to a different `ComputeShader` asset.
The field therefore stayed 0, `uv` collapsed to (0, 0) for every pixel, and every pixel reconstructed
its world position at the bottom-left corner of the screen at its own depth:

- the view vector, the normal bias and `reflPosWS` were computed from a position that does not belong
  to the pixel, so the reflected ray direction was wrong (constant per surface orientation);
- the traces mostly re-hit the same nearby surfaces, which produced hits with black radiance on
  smooth floors and misses on the actually reflective spheres;
- after the reproject, weight 1 + black radiance made the SSR result invisible against the probe.

## Why HDRP behaves differently

HDRP's SSR kernels call their own `GetWorldSpacePosition()` which scales by HDRP's `_ScreenSize`
(`positionNDC = positionSS * _ScreenSize.zw + 0.5 * _ScreenSize.zw`). The URP port tried to share the
SSGI helper, but the shared helper's screen size lives in the SSGI constant buffer. Same math, wrong
constant buffer.

## Fix

- `Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute`
  - new `SsrReconstructPositionWS()` that scales by `_SsrScreenSize` and uses `SSL_INV_VIEW_PROJ`
    (the same expression HDRP uses);
  - trace (`positionWS`), PBR reproject (`neighborWS`, `hitWS`) switch to it.
- `ScreenSpaceReflections.compute` PBR accumulate kernel: the hit point is already texel-center NDC,
  but the code divided it by `_SsrColorPyramidScaleAndLimitPrev.zw` (1 on an unscaled history) before
  converting to a pixel, which squashed every hit into the lower half of the screen. The motion and
  depth lookups of the speed rejection now use `hitNDC * _SsrScreenSize.xy`.

## Acceptance test change

`ScreenSpaceLightingT7Verification` used the **mean** weight over each probe sphere's projected rect
as the roughness-fade evidence ("weights 0.444/0.350/0.198/0"). That mean is
`roughnessFade * on-screen hit fraction`; with a correct trace the hit fraction depends on where the
sphere sits, not on its smoothness (most mirror rays of a sphere leave the screen), so the check is no
longer monotonic. The check now reads the maximum per-pixel weight (which is exactly the fade on a
covered pixel) and asserts:

- no pixel exceeds the analytic roughness fade (minSmoothness / smoothnessFadeStart of the test
  profile);
- the maximum weight does not increase towards rougher spheres;
- rough metal below `minSmoothness` stays at 0;
- the smooth metal sphere reaches the full fade (>= 0.9).

`ScreenSpaceLightingHistoryTests` / `GroundTruthAmbientOcclusionHistoryTests` also asserted that the
histories own the depth mip chain; commit `770777a` moved it to `ScreenSpaceDepthPyramidSystem`, so
those assertions were stale (they failed in batch mode before this change). They now assert the
resources each history still owns, and the fixtures initialize `RTHandles` for batch mode.

## Verification

- `ScreenSpaceLightingT7Verification` (Forward / Forward+ / Deferred): 3/3 `passed=true`.
  Measured maximum weight vs analytic fade, identical on all three renderers:

  | Probe | Smoothness | max weight | expected fade |
  |---|---|---|---|
  | Metal Sphere 0 | 0.95 | 1.0000 | 1.0000 |
  | Metal Sphere 1 | 0.80 | 0.8301 | 0.8438 |
  | Metal Sphere 2 | 0.65 | 0.3289 | 0.3164 |
  | Metal Sphere 3 | 0.40 | 0.0000 | 0.0000 |
  | Dielectric Sphere | 0.95 | 1.0000 | 1.0000 |

- `ScreenSpaceLightingT8Verification` (PBR accumulation, 9 speed-rejection combinations): 3/3
  `passed=true` after the accumulate hit-pixel fix.
- EditMode suite: **154 passed / 0 failed** (was 150/4 in batch mode before the fixture updates).
- Visual before/after on the T7 rig (same scene, same camera):
  `Before_ForwardPlus_SsrRaw.png` shows near-black floor radiance and thin rim slivers;
  `After_ForwardPlus_SsrRaw.png` shows the reflected spheres/point lights.
  `Before_ForwardPlus_SsrWeight.png` covers the whole floor with weight 1;
  `After_ForwardPlus_SsrWeight.png` keeps weight only where a ray hits on-screen geometry.

## Not rerun / follow-ups

- T10 (transparent SSR), T12 (combination matrix) and T13 (XR player) were not rerun. The reproject
  fix applies to the transparent path as well; its acceptance numbers are relational but should be
  replayed before the next release.
- Ray marching still clamps to mip 6 (`SSR_MIP_LEVEL_CLAMP`), while HDRP uses
  `_SsrDepthPyramidMaxMip`. Behaviour is unchanged by this fix; aligning it is an optional fidelity
  follow-up.
- `CommandBuffer: temporary render texture not found (SetComputeTextureParam)` appears twice on
  Play-mode entry in the editor but never in batch-mode camera renders. It does not correlate with the
  missing reflections and is still unexplained.

## Evidence

- `Before_ForwardPlus_*` / `After_ForwardPlus_*`: T7 rig raw radiance, weight and final image; the
  before set is the archived `validation/T7` capture of the broken trace.
- `After_Deferred_SsrFullRes.png`: Deferred renderer, same rig.
- `After_Diag_*`: T3 scene + mirror ground diagnostic (final on/off, previous color pyramid, camera
  depth, SSR raw, SSR weight).
- `UnityDiagnosticBefore.log` / `UnityDiagnosticAfter.log`: batch diagnostic run, `[SsrDiag]` lines
  hold the numbers above.
- `T7VerificationAfterFix.json`, `T8VerificationAfterFix.json`, `UnityT7AfterFix.log`,
  `UnityT8AfterFix.log`, `UnityEditModeAfterFix.log`: acceptance evidence.