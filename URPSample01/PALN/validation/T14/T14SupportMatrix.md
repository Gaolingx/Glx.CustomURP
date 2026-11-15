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
