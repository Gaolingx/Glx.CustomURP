# T14 support matrix

| Renderer | Result |
|---|---|
| Forward | Passed |
| ForwardPlus | Passed |
| Deferred | Passed |
| DeferredFallbackForward | Passed |

| XR | Result |
|---|---|
| MultiPass / Direct3D11 | Passed |
| MultiPass / Direct3D12 | Passed |
| MultiPass / Vulkan | Passed |
| SinglePassInstanced / Direct3D11 | Passed |
| SinglePassInstanced / Direct3D12 | Passed |
| SinglePassInstanced / Vulkan | Passed |
| Foveation / DX11 | NotApplicable (SystemInfo.foveatedRenderingCaps=None) |
| Foveation / DX12 | GuardsVerified (caps=FoveationImage, passes disable foveation for full-screen raster) |
| Foveation / Vulkan | GuardsVerified (caps=FoveationImage, passes disable foveation for full-screen raster) |
| Foveation / note | Mock HMD foveation simulation was not compared image-by-image; only the guard path and stereo correctness were verified. |
| RealHMDs | NotTested (no physical HMD in this environment; Mock HMD only) |

| Platform | Result |
|---|---|
| Windows DX11 | Tested |
| Windows DX12 | Tested |
| Windows Vulkan | Tested |
| Metal | NotTested (Windows host, no Metal device) |
| OpenGLES/Android | NotTested |
| macOS/iOS | NotTested |

| Release player | Result |
|---|---|
| StandaloneWindows64 | Passed (T14 release build + XR player runs) |

| Resolution | Result |
|---|---|
| StaticRenderScale | Passed (T12 RenderScale75; T13 full-scale XR runs) |
| HardwareDynamicResolution | UnsupportedByUrp14 |

Not tested results are not counted as supported.

## 06-ValidationAllRDC status (2026-11-17)

This section records 06 GPU validation coverage; rows marked Pending are exit-criteria gaps, not support claims.

| Area | Result |
|---|---|
| Forward / ForwardPlus / Deferred | Passed (GTAO, SSGI, SSR, transparent SSR and refraction dispatch, binding probe and image cases) |
| Orthographic GTAO | Passed (P1-2; URP addition, no HDRP counterpart) |
| Static render scale 0.5 / 0.75 / 1 / 2 | Passed (P4-3; dispatch dimensions 640×360 / 960×540 / 1280×720 / 2560×1440, 0 non-finite pixels) |
| MSAA 1× / 4× | Passed on Forward and ForwardPlus; Deferred compatibility path ignores MSAA (P4-3) |
| A→B camera keyword/binding reset | Passed (P4-1/P4-2; injected leaks 60/60 before fix → 0 after; 44/44 rejected-camera bindings clean) |
| XR single-pass | NotRevalidatedAfter06 (T13/T14 Mock HMD evidence remains; a dedicated smoke is deferred) |
| T12 combination matrix replay | Pending (historical 34/34/35 from T12; 06 replay deferred) |
| EditMode full suite | Passed (455/455, P6) |
| PlayMode full suite | Passed (6/6, P6; 3 renderers × 2 tests) |
| Performance (1280×720 GPU delta) | Pending (no 06 measurement; not claimed) |
| Resource-count leak loop | Pending (camera-lifetime history pruning passes; 10× RT-count cycling not yet instrumented) |
| Release/validation player build | Passed (0 errors; 4001 shader warnings: 3998 plugin `Load` truncations + 3 URP core shader warnings, same count as P4-2) |

ForwardPlus absolute values from P4-3 onward are not directly comparable with earlier rounds because the validation build now collects Forward+ variants for all three validation pipelines; relative metrics and pass/fail outcomes remain comparable.
