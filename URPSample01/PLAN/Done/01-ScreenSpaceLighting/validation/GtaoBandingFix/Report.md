# GTAO vertical banding fix (2026-11-15)

## Symptom

Enabling Ground Truth Ambient Occlusion draws full-height vertical dark bands over large,
grazing-angle surfaces (the T3 verification ground plane). The bands survive every quality setting
(half/full resolution, 2..32 steps, 1..6 directions, temporal on/off) and disappear only when the
AO intensity reaches zero.

## Reproduction (before the fix)

`validation/T5/T5_Deferred_AmbientOcclusion.png` and
`validation/T5/T5_ForwardPlus_GtaoSpatialOnly.png` already contain the bands: the defect shipped
with the original GTAO port (T5). Dumping the intermediate buffers at 1280x720 and 838x421 showed:

| Stage | Result |
|---|---|
| `GtaoPackedData` (GTAO main horizon search) | correct AO |
| `GtaoPackedBlurred` (spatial bilateral denoise) | **constant along Y**: every row identical |
| `GtaoPackedTemporal` / history | inherits the spatial result |
| `GtaoOcclusion` (after the bilateral upsample) | same band pattern at full resolution |

Both denoisers fetched neighbours through HDRP's `GatherRed` with a bilinear-clamped UV. In this
project that path returns a degenerate result: only the X coordinate of the fetch survives, so every
output row samples the same input row and the AO of one row is smeared vertically. Bound as-is in the
URP compatibility compute path, the sampler state HDRP relies on is not actually bound to the compute
kernels, so the gather is undefined.

## Fix

- `GTAOSpatialDenoise.compute`: the bilateral blur fetches its 3x3 neighbourhood with explicit
  `Load` calls on integer pixel coordinates instead of `GatherRed` + UV, keeping the same depth
  weights and the `TO_TEMPORAL` output contract.
- `GTAOBlurAndUpsample.compute`: the bilateral/box upsample fetches the 2x2 low resolution AO block
  and the 2x2 high resolution depth block with integers loads as well. The group-shared smart blur
  is dropped because the spatial denoise already filters the buffer, which removes the fragile
  groupshared pass instead of porting it twice.
- `GroundTruthAmbientOcclusionPass.cs`: the spatial denoise now always runs and always writes the
  packed buffer when a half resolution pass is used, so the non-temporal path also upsamples a
  denoised buffer; the upsample dispatch (re)binds the private depth pyramid.
- `GTAOCommon.hlsl`: `GatherRed`, its sampler and the UV-based gather helper are removed; the
  file header documents the deviation from HDRP.
- `ScreenSpaceLightingT12Verification.cs`: the `UnsupportedMaterial` case now holds GTAO disabled in
  both captures. It verifies that SimpleLit keeps the SSL weight at 0, and SimpleLit legitimately
  receives GTAO once the AO buffer is correct.

## Investigation: why HDRP behaves differently

The kernels are a line-by-line port of HDRP 14.0.12, so the first question is whether the divergence is
a URP defect or a porting problem. A standalone compute probe (`GatherProbe.txt`) ran the same
`GatherRed` on a texture whose value depends only on Y and reported whether the row ramp survives:

| Variant | Row ramp preserved |
|---|---|
| `SamplerState s_gtao_point_clamp_sampler` (the port's renamed sampler) | yes |
| `SamplerState s_point_clamp_sampler` (HDRP's original name) | yes |
| built-in `sampler_PointClamp` | yes |
| corner UV (`pixel * invSize`, the HDRP convention, no half texel) | yes |
| source written by a compute dispatch, read by another dispatch in the same command buffer | yes |

So `GatherRed`, the sampler naming and the UAV->SRV ordering are all fine in this project: it is not a
URP compute bug and the T0 `_Gtao` rename of the sampler is not the cause. Two further probe runs on
the real spatial kernel narrowed it down:

- original UVs (`GTAOClampAndScaleUVForBilinear((pixel + offset) * _GtaoBufferSize.zw)`) with the
  fetch replaced by `Load` at the same UV: **still banded**;
- the identical fetch with the clamp (`min`) removed: **no banding**.

That points at the UV/clamp composition inside the denoise kernels rather than at `GatherRed` itself.
The fetch collapses the V coordinate for most of the image, so every output row ends up with the AO of
a single input row, which is exactly the column pattern seen on screen. The fix removes that whole
path (integer loads on the same 2x2 block), so the artifact cannot come back through it.

Open point: the exact codegen-level reason the clamped UV degenerates (wrong uniform value, or a
folding/min optimisation in the compiled kernel) is not proven. A RenderDoc capture of the failing
spatial dispatch would show the bound uniform and the sampler/UV read; the behavioural evidence above
is what the fix was validated against.

## Validation

| Check | Result | Evidence |
|---|---|---|
| EditMode | 153 passed / 0 failed | `EditModeAfterFix.xml`, `UnityEditModeTests.log` |
| PlayMode (three renderers) | 6 passed / 0 failed | `PlayModeAfterFix.xml`, `UnityPlayModeTests.log` |
| T12 combination matrix replay | Forward/Forward+/Deferred 34/34/35 passed | `T12VerificationAfterFix.json`, `UnityGraphicsRegression.Run2.log` |
| GTAO-off frames | pixel-identical to the archived T12 images | `After_*_Reference.png` compare with `validation/T12/T12_*_Reference.png` |
| GTAO-on frames | changed by design (that is the fix) | `After_*_GtaoOnly.png`, `After_*_AllOn.png` |
| AO buffer | localized contact AO, no column structure | `After_1280x720_AO.png`, `After_1280x720_Final.png`, `After_838x421_Final.png` |

`T12VerificationAfterFix.json` keeps the per-case `sha256` of the linear samples. Every case whose
`sha256` differs from `validation/T12/T12Report.json` is a GTAO-enabled case; the all-off baselines
have identical PNGs (sub-LSB float noise only, which the 8-bit PNG rounds away).

## Remaining notes

- The AO texture still leaves the last column/row unwritten for odd screen sizes (the 2x2 upsample
  writes `2x-1..2x`), which shows as a dotted edge; it predates this fix and does not affect the
  reported artifact.
- XR single-pass, Vulkan and DX12 stereo runs were not re-executed for this fix; the change only
  swaps `GatherRed` for `Load` on the same coordinates, and `GTAO_LOAD` already carries the XR slice
  indexing, but a player re-run is the remaining validation step if a release build must be claimed.