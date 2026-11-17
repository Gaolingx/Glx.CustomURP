# P6 regression evidence (2026-11-17)

Scope: P6 regression subset after the P5 documentation pass — full EditMode, full PlayMode, and the closed-effect byte baseline. T12 replay, performance, the 10× RT-count loop and XR single-pass smoke remain pending.

## Environment

- Unity 2022.3.62f1, batchmode, Windows 10 (10.0.20348), D3D11 editor.
- Unity logged `No ULF license found` / `Access token unavailable` in both runs; the tests still executed. The post-fix runs exited 0.

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

After restoration `git status` contains only the PlayMode test fix and this `P6/` directory.

## Still pending (not claimed)

| Item | Status |
|---|---|
| T12 combination matrix replay 34/34/35 | Pending; historical T12 evidence remains, Forward+ absolute values are not comparable after P4-3 |
| 1280×720 GPU time delta ≤10% | Pending; no paired measurement in `validation/` |
| RT count after 10× resolution/camera cycles | Pending; needs a dedicated resource-count instrumentation (the PlayMode camera-lifetime test only proves history entry pruning) |
| T4–T7 historical menu harnesses | Deferred to P6-2: 39 references to removed history fields (`validFrameCount`, `resourceGeneration`, `depthPyramidMipCount`) remain in four Editor harnesses. They compile (string reflection) and are outside the test suite, but running them yields -1; replace or delete them with the P6-2 cleanup. |
| XR single-pass smoke | Accepted environment limit; T13/T14 Mock HMD evidence remains |
| Build warning attribution | Done: 4001 shader warnings = 3998 plugin `Load` truncations + 3 URP core; 0 C# warnings, same count as P4-2 |
