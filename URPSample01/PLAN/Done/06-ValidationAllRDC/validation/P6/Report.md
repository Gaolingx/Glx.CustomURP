# P6 regression, T12 replay and performance evidence (2026-11-17)

Scope: P6 closes the 06 round — full EditMode, full PlayMode, the closed-effect byte baseline, the T12 combination-matrix replay, the 1280×720 performance pass, and the stale history-field migration in the T4–T7/T12 menu harnesses.

## Environment

- Unity 2022.3.62f1, batchmode, Windows 10 (10.0.20348), D3D11.
- NVIDIA GeForce RTX 5060 Ti, driver 32.0.15.7680.
- Unity logged `No ULF license found` / `Access token unavailable` in every run; the runs still executed and the post-fix runs exited 0.
- GPU clocks cannot be locked on this machine (GeForce/WDDM rejects application clock locks; SM clocks swing 63–1519 MHz). GPU timings are reference-only, matching the T1/T14 protocol decision in [T14Performance.json](../../../01-ScreenSpaceLighting/validation/T14/T14Performance.json).

## EditMode

| Run | Result | Duration | Evidence |
|---|---|---|---|
| Full, unfiltered | **455/455 passed, 0 failed, 0 skipped** | 58.7 s | [UnityEditModeFull.xml](EditMode/UnityEditModeFull.xml), [log](EditMode/UnityEditModeFull.log) |
| Filtered class list (diagnostic) | 160/160 passed | 56.7 s | [UnityEditModeTests.xml](EditMode/UnityEditModeTests.xml) |

The filtered run is kept only to show the T14 class filter still passes; the exit criterion uses the unfiltered 455 count, matching P4-3/P4-4.

Command:

```bash
"/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -runTests -testPlatform EditMode \
  -testResults "<abs>/validation/P6/EditMode/UnityEditModeFull.xml" \
  -logFile "<abs>/validation/P6/EditMode/UnityEditModeFull.log"
```

## PlayMode

### Pre-fix failure

The first run exposed a P4-4 cleanup regression: the PlayMode suite still reflected on `ScreenSpaceLightingHistory.validFrameCount`, which P4-4 deleted. The field read as `-1` on all three renderers.

| Case | Result | Message |
|---|---|---|
| `AllEffectsDispatchAndRestoreReference("Forward")` | Failed | `the SSL history has no accumulated frames. Expected: greater than 1 But was: -1` (`ScreenSpaceLightingPlayModeTests.cs:96`) |
| `AllEffectsDispatchAndRestoreReference("ForwardPlus")` | Failed | same |
| `AllEffectsDispatchAndRestoreReference("Deferred")` | Failed | same |
| `RuntimeControlsAndCameraLifetime(...)` × 3 | Passed | — |

Totals: 6 run, 3 passed, 3 failed, 42.4 s. Transcribed in [PlayMode/BeforeFix.json](PlayMode/BeforeFix.json); the original XML/log were overwritten by the post-fix rerun.

Fix: replace the stale assertion with `frameSerial > 1`, which still proves accumulation past the first rendered frame; keep `hasValidHistory` and `resetCount`.

### Post-fix result

| Run | Result | Duration | Evidence |
|---|---|---|---|
| Full PlayMode, 3 renderers × 2 tests | **6/6 passed, 0 failed** | 2.5 s | [UnityPlayModeTests.xml](PlayMode/UnityPlayModeTests.xml), [log](PlayMode/UnityPlayModeTests.log) |

Closed-baseline coverage in the passing suite:

- `AllEffectsDispatchAndRestoreReference`: effects off → on changes the image; all-on has 0 non-finite pixels; every effect counter advances; one history entry with `frameSerial > 1`; `hasValidHistory`; `resetCount > 0`; all four global keywords are off after camera cleanup; **effects off again is byte-identical to the reference**; SSGI/SSR/depth-pyramid counters do not advance while disabled.
- `RuntimeControlsAndCameraLifetime`: **disabling the SSL feature at runtime restores the reference byte-identically**; a second camera gets its own history entry and the entry is pruned when the camera is destroyed; **removing the SSGI/SSR/refraction Volume components restores the reference byte-identically** and stops SSGI dispatch.

The `intensity=0` baselines are covered by the earlier per-effect evidence: GTAO `intensity=0` vs feature disabled is 0 pixels ([P1-GTAO](../P1-GTAO/Report.md)); SSR intensity 0 is in [P3-5](../P3-5/Report.md). The PlayMode suite covers effect-off, feature-off and Volume-component removal across Forward, Forward+ and Deferred.

Command:

```bash
"/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -runTests -testPlatform PlayMode \
  -testResults "<abs>/validation/P6/PlayMode/UnityPlayModeTests.xml" \
  -testFilter "Unity.ScreenSpaceLighting.Tests.PlayMode" \
  -logFile "<abs>/validation/P6/PlayMode/UnityPlayModeTests.log"
```

## ProjectSettings restoration

Before each run the four batchmode-sensitive settings were snapshotted; after each run they were restored byte-for-byte and verified with SHA-256:

| File | SHA-256 |
|---|---|
| `ProjectSettings/GraphicsSettings.asset` | `1965d795622315852c0e3dced3f15db7b16b805958387add74fe54f10d684e71` |
| `ProjectSettings/QualitySettings.asset` | `f7698a6c2c236296dc0f4ff054c925bf44a80a57c09b2ad2b643105e8a389249` |
| `ProjectSettings/ProjectSettings.asset` | `ecba8e50b77feb0ceb8ec4dee2fc85faa0512fb2058a6c5568e88451b42b0a93` |
| `ProjectSettings/EditorSettings.asset` | `2702f317d35952146794d4b795d5a202d5139ee2a5a4540ac5df015ff4d89a86` |

Before and after hashes are recorded in [settings-before.sha256](T12/settings-before.sha256) and [settings-after.sha256](T12/settings-after.sha256); they are identical. The T1 baseline build rewrote its renderers/pipelines/materials/scene; those were restored from snapshots taken before the build. The only asset change kept is the T12 renderers' reserialization (`publishProbeAtlas`, `ssrReflectionDenoiserCompute`), which Unity adds on load and which makes future T12 runs deterministic.

## T12 combination-matrix replay

The historical T12 assets were reused; the output was redirected so the archived T12 evidence is untouched.

Command:

```bash
"/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingT12Verification.RunT12Verification \
  -ssl-t12-output "<abs>/validation/P6/T12" \
  -logFile "<abs>/validation/P6/T12/UnityT12.log" -quit
```

| Renderer | Cases | Failed | Issues |
|---|---|---|---|
| Forward | 34 | 0 | 0 |
| ForwardPlus | 34 | 0 | 0 |
| Deferred | 35 | 0 | 0 |

**34/34/35 is confirmed by the replay.** The earlier P6 note of "23 per renderer" was a miscount of report fields, not cases.

| Metric | Forward | ForwardPlus | Deferred |
|---|---|---|---|
| Render-scale reset count | 2 | 2 | 2 |
| Target-resize reset count | 2 | 2 | 2 |
| Camera-cut reset count | 2 | 2 | 2 |
| SSR algorithm-switch reset count | 3 | 3 | 3 |
| Global keywords clean after cameras | yes | yes | yes |
| Camera stack / preview / reflection cameras | pass | pass | pass |
| SSL private bytes (720p) | 107,827,712 | 107,827,712 | 107,827,712 |
| GTAO private bytes | 3,686,400 | 3,686,400 | 3,686,400 |
| Depth-pyramid bytes / mips | 5,529,600 / 12 | 5,529,600 / 12 | 5,529,600 / 12 |
| Color-pyramid mips | 9 | 9 | 9 |
| Transparent prepass executions | 1 | 1 | 1 |

Evidence: [T12Verification.json](T12/T12Verification.json), [log](T12/UnityT12.log), 103 PNGs. All effect cases (reference/all-on/all-off/SSGI-only/SSR-only/refraction, Volume states, MSAA, decals, depth priming, motion blur, TAA, camera types) passed with zero non-finite and zero magenta pixels.

Known instrumentation gap: `depthPyramidDispatches` reads **0 on all three renderers** even though the pyramid is present (12 mips, 5.5 MB) and the Forward mode's pass-execution assertions pass. The DEV copy/downsample counters are not incremented for the harness's second render of the frame. `costQuantified` does not assert this field, so it does not affect the replay result; it is recorded here rather than silently ignored.

## 1280×720 performance pass

The T1 baseline player (60 warmup frames, 1800 samples per mode, 60 fps target, 1280×720, D3D11 Development build) was rebuilt from the current code and run with `--ssl-baseline-output`.

| Mode | CPU p95 (ms) | Baseline CPU p95 (ms) | Δ | GPU p95 (ms, reference) | Baseline GPU p95 (reference) | Graphics driver bytes | Δ |
|---|---|---|---|---|---|---|---|
| Forward | 16.8596 | 16.7000 | **+0.96%** | 1.3901 | 2.9932 | 42,664,133 | +0.088% |
| ForwardPlus | 17.0596 | 16.6939 | **+2.19%** | 2.5654 | 2.5426 | 42,746,057 | +0.088% |
| Deferred | 16.8650 | 16.6922 | **+1.04%** | 2.6706 | 2.5800 | 57,410,759 | +0.065% |

- Initial and final graphics-driver memory are recorded in [Run/BaselineReport.json](Perf/Run/BaselineReport.json); no growth between modes.
- All screenshots captured with 0 magenta pixels; 1800 CPU samples per mode, 1798–1800 GPU samples.
- **CPU p95 is within the 10% regression gate** on all three renderers.
- **GPU p95 is reference-only**: DVFS cannot be locked on this machine ([T1 DVFS diagnostics](../../../01-ScreenSpaceLighting/validation/T1/Diagnostics/Dvfs-2026-11-14/)), and the T14 protocol explicitly forbids using this harness as a GPU pass/fail gate. The archived GPU numbers (2.99/2.54/2.58 ms) and the new ones (1.39/2.57/2.67 ms) are both dominated by clock state, not by the code change.
- Scope limit: the T1 baseline renderers intentionally contain no SSL feature, so this run measures **core-path regression plus memory**, not the incremental cost of SSL. SSL's incremental GPU cost remains covered by the T14 budget table, which is itself reference-only.

Evidence: [PerfComparison.json](Perf/PerfComparison.json), [BuildSummary.json](Perf/BuildSummary.json), [Build log](Perf/UnityBaselineBuild.log), [Run/BaselineReport.json](Perf/Run/BaselineReport.json), [FrameTimes.csv](Perf/Run/FrameTimes.csv), [RawFrameTimes.csv](Perf/Run/RawFrameTimes.csv), three PNGs.

## Stale history-field migration (T4–T7, T12)

P4-4 deleted `ScreenSpaceLightingHistory.validFrameCount`, `ScreenSpaceLightingHistory.resourceGeneration` and both histories' `depthPyramidMipCount`. Five Editor harnesses still read them by name; `ReadInt` returns 0 for a missing member, so their history assertions had silently degraded.

| Harness | Change |
|---|---|
| T4 | `validFrameCount` → `frameSerial` (post-cut first frame = `serialBefore + 1`; per-frame accumulation = `+1`); `resourceGeneration` check replaced by the 640×360 color-pyramid mip count (8); depth mips read from `depthPyramidInfo.mipLevelCount` |
| T6, T7 | snapshot field `validFrameCount` → `frameSerial` |
| T5 | GTAO `validFrameCount`/`resourceGeneration` kept (still present); depth mips read from `depthPyramidInfo.mipLevelCount` |
| T12 | depth mips read from `depthPyramidInfo.mipLevelCount` (the same value now feeds `sslDepthPyramidMips` = 12) |

Runtime verification: T4 was executed in batchmode ([UnityT4.log](T4Harness/UnityT4.log), [T4Verification.json](T4Harness/T4Verification.json)). The migrated values are correct on all three renderers — `AfterWarmup` serial 12, post-cut frame 1 serial 13, temporary camera serial 1 → settled 4, 640×360 color mips 8, depth mips 12.

T4 itself still exits 1 for two reasons unrelated to this migration and pre-dating it:

1. Its reference frame is a live capture with the SSL feature present; since P1–P3 implemented GTAO/SSGI/SSR, the effects now change pixels relative to that reference (max channel delta 1.066).
2. The depth-pyramid DEV execute/copy counters read 0 in Forward+/Deferred (same counter gap as the T12 note above), so the historical "pass executed" assertions fail there.

T4–T7 remain outside the EditMode/PlayMode suites; the stale-field defect itself is fixed and verified. Refreshing those historical harnesses' references/counters is not part of P6.

## Accepted items and known deviations

| Item | Status | Reason |
|---|---|---|
| GPU p95 delta ≤10% | **Reference only** | DVFS cannot be locked on this GeForce/WDDM machine; T14's protocol already demoted GPU p95 to reference data. CPU p95 is the gate and passes. |
| RT count after 10× resolution/camera cycles | **Accepted** | No dedicated resource counter exists. Covered instead by T12's per-renderer private-byte snapshot (107.8 MB SSL + 3.7 MB GTAO), T12's render-scale/target-resize/camera-cut reset counts, and PlayMode's history-entry pruning. A 10× allocation loop would need new instrumentation, which is out of P6 scope. |
| XR single-pass smoke | **Accepted (environment limit)** | Not re-run in 06; T13/T14 Mock HMD evidence remains valid. `TEXTURE2D_X`/`blackTexture` binding needs a separate smoke. |
| T4–T7 harness pass/fail | **Out of suite** | Historical harnesses; stale-field reads fixed and verified, but their live-reference and counter checks are not maintained. |
| Build warning baseline | **Accepted (existing noise)** | 4001 shader warnings: 3998 plugin `Load` truncations (SSR 3171, SSGI 746, GTAO 80, Leak Probe 1) + 3 URP core; 0 C# warnings. The T1 player build reported 3 warnings, same class. |

## Exit criteria

| Criterion | Status |
|---|---|
| F-01..F-11 Fixed or Accepted; no High Accepted | Met |
| 0 build errors; no new warnings | Met |
| EditMode full, PlayMode full | Met (455/455, 6/6) |
| T12 combination matrix 34/34/35 | **Met (replay)** |
| Closed-effect byte baseline | Met (P6 PlayMode + P1-GTAO/P3-5) |
| GPU evidence per effect × renderer | Met (P0–P4 `.rdc`, census, probe, pixels) |
| 7.2 numeric thresholds | Met |
| Performance ≤10% regression | Met on CPU p95; GPU recorded as reference per protocol |
| Memory back to baseline; 10× loop | Driver memory Δ ≤0.09%; 10× loop **Accepted** (no counter) |
| Docs consistent | Met after this update |
| Archive + clean `git status` | Done in the archive commit |
| Public-reviewable commits | Met |
