# P4 validation evidence

> Run 2026-11-16 on Unity 2022.3.62f1 / URP 14.0.12, RTX 5060 Ti, Direct3D11.

## Reproduction

```bash
# Functional + stress + performance capture (writes this directory)
"D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingSsgiProbeFallbackVerification.RunSsgiProbeFallbackVerification \
  -quit -logFile "<abs>/validation/P4/UnitySsgiProbeFallback.log"

# Full EditMode suite
"D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" -runTests -testPlatform EditMode \
  -testResults "<abs>/validation/P4/UnityEditModeTests.xml" \
  -logFile "<abs>/validation/P4/UnityEditMode.log"
```

The harness loads `SslT8{Forward,ForwardPlus,Deferred}` renderer/pipeline fixtures, copies
`ScreenSpaceLightingT3Verification.unity` to a temporary scene with a serialized custom reflection
probe (linear RGB 0.15/3.0/0.15), disables temporal accumulation and the denoiser, and reads the SSGI
final RTHandle at 320x180. The `.log` files are gitignored (`*.log`); the XML, JSON and PNGs are
committed.

## Functional results (`SsgiProbeFallbackVerification.json`)

`rayMiss=ReflectionProbes`; covered pixels are SSGI output pixels with weight > 0.01. The green tint is
`mean(G) - max(mean(R), mean(B))` over covered pixels.

| Renderer | Capture | coverage | green tint | covered mean weight |
|---|---|---|---|---|
| Forward | toggle off | 38.28% | -0.007 | 1.000 |
| Forward | toggle on | 30.64% | 2.564 | 0.9997 |
| Forward | ProbesAndSky | 38.28% | 2.021 | 1.000 |
| ForwardPlus | toggle off | 30.64% | 2.592 | 0.9997 |
| ForwardPlus | toggle on | 30.64% | 2.592 | 0.9997 |
| Deferred | toggle off | 38.28% | -0.007 | 1.000 |
| Deferred | toggle on | 30.64% | 2.564 | 0.9997 |

- `ForwardPlus_Off.png` and `ForwardPlus_On.png` are byte-identical: Forward+ ignores the toggle.
- Toggle off keeps the default cubemap (neutral/blue), toggle on switches the miss pixels inside the
  probe box to the green probe and narrows the weight to the box, so the uncovered pixels keep the
  baked GI for `rayMiss=ReflectionProbes`.
- `ProbesAndSky` restores 38.28% coverage (box weight + ambient SH remainder); its premultiplied mean
  differs from pure `Sky` (0.3273 vs 0.0957), proving the probe contribution is present.
- No NaN/Inf pixels in any capture.

## Stress results (`stress` in the JSON)

| Probes created | Probes culled | coverage | green tint | covered mean weight |
|---|---|---|---|---|
| 0 | 0 | 3.14% (screen-space hits only) | -0.049 | 1.000 |
| 1 | 1 | 30.64% | 2.564 | 0.9997 |
| 4 | 4 | 30.64% | 2.564 | 0.9997 |
| 32 | 32 | 30.64% | 2.564 | 0.9997 |

Each count is serialized into its own temporary scene because Unity does not rebuild the culling
list when probes are enabled/disabled at runtime. All probes share the same green cubemap and probe 0
covers the visible half, so the 1/4/32 captures saturate on the first covering probe; the count is
proven by the culling list and by the zero-probe capture, which loses the green tint and keeps only
the screen-space hit radiance. Images: `Stress_00/01/04/32.png`.

## Performance (`performance` in the JSON)

1280x720, 32 visible probes, `rayMiss=ReflectionProbes`, temporal/denoise off. The toggle-off and
toggle-on states alternate per battery; one battery is 30 `camera.Render()` calls followed by a
blocking 1x1 `ReadPixels`, timed with `Stopwatch`. The reported delta is the median of the eight
paired per-frame differences.

| Metric | Value |
|---|---|
| off median | 1.613 ms/frame |
| on median | 2.002 ms/frame |
| median paired delta | **0.153 ms** (budget 0.2 ms) |
| max paired delta | 0.746 ms (scheduling noise) |

The SSGI dispatch count is unchanged (the probe list runs inside the existing reproject kernel); the
delta includes the per-frame atlas upload. This is a whole-frame wall-clock measurement, not a GPU
timestamp of the reproject dispatch alone, so the max pair is noisy; the median is the budget check.
Frame Debugger texture bindings were not captured (optional in the plan).

## EditMode suite

`UnityEditModeTests.xml`: **426/426 passed, 0 failed** with the probe atlas toggle in place. The
probe-atlas contract tests are in `ScreenSpaceLightingProbeAtlasTests.cs`; the plugin isolation rule
(`PluginIsolationTests.CoreRuntimeSourcesDoNotReferencePluginTypes`) passes because the feature
pushes `SetProbeAtlasPublishing` from `AddRenderPasses` instead of core URP looking the feature up.
