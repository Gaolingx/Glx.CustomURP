# P4-4 死代码 / 重复声明清理（F-11 + §6.6，含 P2-5）

> 日期：2026-11-17
> 结论：§6.6 清单中经核实无引用的项全部删除；仍在使用的项保留并在 §4 记录原因。**全量 EditMode 455/455、player 构建 0 error、9 例三渲染器冒烟全部通过、9 个 binding probe 0 不匹配**。

## 1. 方法

1. 对 §6.6 每一条在 `Runtime/`、`Tests/`、`Editor/`、`Assets/Tests/` 内做符号引用计数，而不是按清单直接删。
2. 只删除“除声明/自身写入外零引用”的成员；测试读取的死标记连同测试断言一起删除。
3. 对 float4 打包向量，不能删除通道，只把未读通道停止写入并修正注释（改动布局会破坏 cbuffer 契约）。
4. 编译告警作为第二道校验：允许出现未使用私有字段的 `CS0169/CS0414`，本次为 0。

## 2. 删除项

| 项 | 位置 | 说明 |
|---|---|---|
| `validFrameCount` | `ScreenSpaceLightingHistory` | 仅测试读取；`SwapBuffers` 不再累加 |
| `resourceGeneration` | `ScreenSpaceLightingHistory` | 仅测试读取；`ssgiResourceGeneration` / `ssrResourceGeneration` / `transparentResourceGeneration` 仍保留 |
| `ssrOutputProduced` / `MarkSsrOutputProduced` | `ScreenSpaceLightingHistory`、`SsrPass` | 标记只写不读，运行时消费者为 0 |
| `ssrHasValidHistory` | `ScreenSpaceLightingHistory` | 仅测试读取 |
| `historyColorPyramidCount` / `previousDepthCount` | `ScreenSpaceLightingHistory` | 仅测试读取 |
| `usingPbrAccumulation` | `ScreenSpaceLightingTransparentSsrPass` | 恒为 false 且仅测试读取 |
| `_PixelSpreadAngleTangent` 独立 float 写入 | `SsgiPass.cs`、`ScreenSpaceLightingShaderIDs` | 无 kernel 声明该独立 uniform，写入被宏遮蔽；F-02 已改用 `_SsgiDenoiserParams.x` |
| `_GtaoDebugMode` / `_GtaoDebugMip` | `GroundTruthAmbientOcclusionShaderIDs` | 无 shader / C# 引用 |
| `depthPyramidMipCount`（GTAO） | `GroundTruthAmbientOcclusionHistory` | 无引用 |
| `depthPyramidMipCount`（SSL） | `ScreenSpaceLightingHistory` | 唯一消费者是 `_SsrPyramidParams.y`，该通道无 shader 读取 |
| `ScreenSpaceDepthPyramid.SwapBuffers()` 空实现 | `ScreenSpaceCommon` | `IScreenSpaceHistoryEntry` 已移除 `SwapBuffers`（store 只调用 `Reset`/`Dispose`） |
| `UniversalRenderer.m_PublishSslProbeAtlas` | `UniversalRenderer` | 与 `ForwardLights.m_PublishProbeAtlas` 重复；属性改为转发唯一状态 |
| 重复 `_CameraDepthTexture` / `_SslDepthMipChain` / `_SslSrcOffsetAndLimit` / `_SslDstOffset` / `SSL_XR_ARRAY` | `ScreenSpaceLightingShaderIDs`、`ScreenSpaceDepthPyramidPass`、`ScreenSpaceLightingRenderUtils` | 合并到新文件 `ScreenSpaceShaderIDs` |
| `ScreenSpaceLightingDebugMode` 值 13 的空洞 | `ScreenSpaceLightingDebugMode` | `SsrPbrParams..SsrTransparentWeight` 由 14–17 重排为 13–16；场景资产与 manifest 中 `debugMode` 全为 0，无迁移 |

## 3. 停止写入的未读通道

| 向量 | 之前 | 之后 |
|---|---|---|
| `_SsgiTemporalParams` | `(lowRes, 1/lowRes, 0.2, 0)` | `(lowRes, 1/lowRes, 0, 0)`；`.z` 无读取 |
| `_SsgiDenoiserParams` | `(spread, -1, 0, 0)` | `(spread, 0, 0, 0)`；只有 `.x` 被 `ReflectionDenoiser` / `TemporalFilter` 读取 |
| `_SsrPyramidParams` | `(colorMip, depthMip, 0, 0)` | `(colorMip, 0, 0, 0)`；只有 `.x` 被读取 |
| `_SsrPbrParams` | `(0, bias, 0, 0)` | 保持 `(0, bias, 0, 0)`；调试 kernel 从 `float4(xyz, flags.y)` 改为 `float4(0, bias, 0, flags.y)`，保留原通道映射 |
| `_GtaoScreenSize` | `(w, h, 1/w, 1/h)` | `(w, h, 0, 0)`；只有 `.xy` 被读取 |

`_GtaoParams0.x`、`_GtaoOrthoHalfSize.zw`、`_SsgiRayMarchingParams2.w`、`_SslRefractionParams.w`、`_SsrPbrFlags.zw`、`_SslRefractionProbeParams.y` 早已写 0，本轮只更新了误导性注释。float4 布局与 cbuffer 大小不变，EditMode 布局测试继续通过。

## 4. 核实后保留（清单与代码不符）

| 项 | 保留原因 |
|---|---|
| `ValidationBuffer(RW)`、`AccumulationOutputTextureRW`、`PointDistribution(RW)`、`HalfResolutionFilter`、`JitterFramePeriod`、`Receiver/OccluderMotionRejection`、`EnableExposureControl`、`*ExposureMultiplier`、`DenoiseSampleCountRW` | 由 `SsgiPass` 的 temporal / denoiser 链实际绑定（`TemporalFilter.compute`、`DiffuseDenoiser.compute`、SSR PBR temporal） |
| `SslProbeListKeyword`（`SSL_PROBE_LIST`） | `SsgiPass` 设置，`ScreenSpaceGlobalIllumination.compute` 有对应分支 |
| `reflectsSky=false` | SSR Volume 可关闭 `reflectSky`，共享 `SslRayMarch` 的该分支可达；SSGI 侧 cbuffer 恒为 1 |
| `SSGI_TEXTURE_TYPE_FLOAT4` | `BilateralFilter.hlsl` 使用；`SSGI_TEXTURE_TYPE_UINT` 在当前代码中已不存在 |
| `ScreenSpaceLightingRenderUtils.ComputeZBufferParams` | 与全局 `_ZBufferParams` 等价，但 compute kernel 不保证读取每相机全局值，保留私有副本 |

## 5. 回归

| 项 | 结果 |
|---|---|
| 全量 EditMode | **455/455**（[EditModeFull.xml](EditModeFull.xml)）；比 P4-2 少 1 项，原因是删除了只验证死标记的 `SsrOutputIsMarkedProducedOnlyAfterAFrame` |
| 编译 | 0 error；0 `warning CS`（含 `CS0169/CS0414`） |
| Player 构建 | Succeeded，0 error，4001 warnings（与 P4-2 相同），**121,695,430 → 121,694,299 bytes（−1,131 B）**（[BuildSummary.json](BuildSummary.json)） |
| 三渲染器冒烟 | 9/9 通过（[Player/Report.json](Player/Report.json)，`passed=true`） |
| Binding probe | 9 个用例 0 不匹配（[Player/](Player/)） |
| 过时文档 | GTAO Inspector/注释与 T14 的“重复 Hi-Z 显存”条目改为共享金字塔 |

未在本任务内重跑：T12 组合矩阵与关闭基线逐字节回归、PlayMode。它们属于本轮退出标准（§7.3），由 P6 统一回归；本次删除不触碰任何运行时分支条件，冒烟用例已覆盖三渲染器 × GTAO/SSGI/SSR/折射的启用路径。

## 6. 复现

```bash
# 全量 EditMode
"/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -runTests -testPlatform EditMode \
  -testResults "<abs>/validation/P4-4/EditModeFull.xml" \
  -logFile "<abs>/validation/P4-4/EditModeFull.log"

# Player 构建
"/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode -quit \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingValidationPlayerSetup.BuildPlayer \
  -logFile "<abs>/validation/P4-4/BuildPlayer.log"

# 冒烟（无抓帧；capture 仅用于 RDC，不影响运行与 binding probe）
"Builds/SslValidationPlayer/SslValidationPlayer.exe" \
  -ssl-validation-manifest "<abs>/validation/P4-4/P4Manifest.json" \
  -ssl-validation-out "<abs>/validation/P4-4/Player" \
  -force-d3d11 -screen-width 1280 -screen-height 720 -screen-fullscreen 0
```

## 7. 变更文件

运行时代码：`ScreenSpaceLightingHistory.cs`、`ScreenSpaceLightingShaderIDs.cs`、`ScreenSpaceLightingShaderVariables.cs`、`ScreenSpaceLightingDebugMode.cs`、`SsgiPass.cs`、`SsrPass.cs`、`TransparentSsrPass.cs`、`RefractionPass.cs`、`ScreenSpaceLightingRenderUtils.cs`、`UniversalRenderer.cs`、`ScreenSpaceDepthPyramid.cs`、`ScreenSpaceDepthPyramidPass.cs`、`CameraHistoryStore.cs`、`GroundTruthAmbientOcclusionHistory.cs`、`GroundTruthAmbientOcclusionPass.cs`、`GroundTruthAmbientOcclusionShaderIDs.cs`、`GroundTruthAmbientOcclusionShaderVariables.cs`、新增 `ScreenSpaceShaderIDs.cs`。

Shader：`ScreenSpaceReflections.compute`（调试 kernel 输出通道）、`RayMarching.hlsl`（过时注释）。

测试：`ScreenSpaceLightingHistoryTests.cs`、`ScreenSpaceLightingSsrTests.cs`、`ScreenSpaceLightingTransparentSsrTests.cs`、`ScreenSpaceLightingSsrDenoiserParamsTests.cs`。

文档：`GroundTruthAmbientOcclusionEditor.cs`、`PLAN/Done/01-ScreenSpaceLighting/validation/T14/T14KnownDifferences.md`、本任务的 `HANDOFF.md` 状态与 §6.6 核实说明。

## 8. P6 follow-up（2026-11-17）

P4-4 删除 `validFrameCount` 时只同步了 EditMode 测试，PlayMode 的 `AllEffectsDispatchAndRestoreReference` 仍在反射读取该字段；P6 首次全量 PlayMode 因此在三渲染器全部失败（读数为 `-1`，`ScreenSpaceLightingPlayModeTests.cs:96`）。已把断言改为 `frameSerial > 1`，保留 `hasValidHistory`/`resetCount`；复跑 **PlayMode 6/6、EditMode 全量 455/455**。证据见 [P6/Report.md](../P6/Report.md)。
