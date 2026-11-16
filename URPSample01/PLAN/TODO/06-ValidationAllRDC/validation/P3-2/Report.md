# P3-2 SSR 调试输出隔离修复（F-04）证据（2026-11-17）

> 任务：P3-2 / F-04（Medium）。目标：`SsrPbrParams` 调试视图只能影响调试显示，不得覆盖材质读取的 SSR 纹理；调试视图开启时 PBR 降噪链仍须执行。
> 结论：**修复完成**。调试 kernel 写入私有 `SslSsrDebugParams`；同一帧内 6 个不透明 draw 仍从 `SslSsrPbrOriginal` 读取真实反射辐射；降噪链完整执行（调试帧 7 个 dispatch，比关闭调试多且仅多出调试 kernel）。

## 1. 缺陷与修复

旧实现（`SsrPass.cs` 修复前）：`debugParams` 分支把 `DebugSsrParams` kernel 的 `_SsrOutputTextureRW` 指向 `m_History.ssrOutput`，随后直接 `return`；`Execute` 在 `debugParams` 时跳过 `SwapSsrPbrBuffers`，并把 `m_OutputTexture` 指向 `ssrOutput`。而 `ScreenSpaceLightingSetupPass` 已把同一个 `ssrOutput` 绑定为材质全局纹理，因此开启调试视图后所有不透明像素读到的是诊断常量（截图整屏绿色）。

修复：

- `ScreenSpaceLightingHistory` 新增独立调试目标 `ssrDebugParams`：R16G16B16A16_SFloat、Point 采样、可随机写、按相机宽高/viewCount 重建并随 SSR 资源释放（`ScreenSpaceLightingHistory.cs:312-346`、`:963-964`）。
- 调试 kernel 只写该私有目标，不再 `return`；`SwapSsrPbrBuffers` 与 `m_OutputTexture` 选择恢复无条件执行（`SsrPass.cs:176-210`、`:305-314`）。
- `ScreenSpaceLightingDebugPass` 的 `SsrPbrParams` 分支改读 `m_SsrPass.debugTexture`（`ScreenSpaceLightingDebugPass.cs:186-196`）。
- DEV 统计记录帧号、调试目标名与材质输出名（`s_LastDebugParamsFrame/Target`、`s_LastMaterialOutputTarget`），供验证 player 断言。

新增 EditMode 回归 `ScreenSpaceLightingSsrDebugViewTests`（6 项）：独立分配与命名、随相机分辨率重建、随资源释放、源码断言（kernel 指向 `ssrDebugParams`，且不再存在 `pbr && !debugParams` / `!halfResolution && !debugParams` 守卫）、DebugPass 读取私有纹理、目标格式可被调试材质采样。

## 2. 产物与复现

| 项 | 值 |
|---|---|
| Manifest | [P3Manifest.json](P3Manifest.json) |
| 用例 | `forward_ssr_pbr_full_debug_off`（debugMode 0）、`forward_ssr_pbr_full_debug_params`（debugMode 14 = `SsrPbrParams`） |
| Player | `Builds/SslValidationPlayer/SslValidationPlayer.exe`（D3D11、Development、1280×720） |
| 抓帧 | `renderdoccmd capture -w --opt-hook-children … -ssl-validation-manifest …P3-2/P3Manifest.json -ssl-validation-out …P3-2/Player` |

`Player/Report.json`：2/2 通过，`nonFinitePixels=0`。最终全量 EditMode 套件 187/187 通过（含本轮新增 fixture）。

## 3. 数值证据

`Report.json` 的 `ssrDebugView`（由 DEV 静态字段反射读取）：

| 字段 | debug_off | debug_params |
|---|---|---|
| debugTarget | 空（用例未请求调试） | **SslSsrDebugParams** |
| materialOutput | 空（用例未请求调试） | **SslSsrPbrOriginal** |
| debugTargetIsPrivate | — | **true** |
| materialOutputIsReal | — | **true** |
| materialOutputIsDebugTarget | — | **false** |
| denoiseChainRan | — | **true** |
| passed | — | **true** |

两个用例的 `ssrDenoiser.pixelSpreadAngleTangent` 均为 `0.0016037507`（full 1280×720），说明调试用例的降噪链确实按 full PBR 路径运行。

截图：`forward_ssr_pbr_full_debug_off.png` 为正常反射画面；`forward_ssr_pbr_full_debug_params.png` 整屏为 `(0, 0.5, 0)` 的诊断常量——这是 `SsrPbrParams` 调试视图的设计表现（最终显示被诊断可视化替换），不再表示材质纹理被覆盖。

## 4. GPU 证据

`P3Census.json` 记录两次抓帧的 SSR marker 与 dispatch 列表。

| 用例 | SSR marker | dispatch | kernel（提交顺序） |
|---|---|---|---|
| debug_off | 324–453 | 6 | trace(345), reproject(367), temporal(394), copyHistory(408), bilateralH(429), bilateralV(450) |
| debug_params | 324–466 | 7 | trace(345), reproject(367), **DebugSsrParams(380)**, temporal(407), copyHistory(421), bilateralH(442), bilateralV(463) |

调试 kernel 位于 reproject 与 temporal 之间，与代码中“写调试目标后继续降噪”一致。资源使用（`get_resource_usage`）：

| 资源 | 用途（event: usage） |
|---|---|
| `ResourceId::9839` `SslSsrDebugParams` | 380: CS_RWResource；1485: PS_Resource |
| `ResourceId::9766` `SslSsrPbrOriginal` | 367: CS_RWResource（reproject 写）；407: CS_Resource（temporal 读）；463: CS_RWResource（bilateral V 写）；507/513/521/585/619/625: PS_Resource（6 个不透明 draw 读） |

纹理统计：

| 资源 | min | max |
|---|---|---|
| `SslSsrDebugParams` | (0, 0.5, 0, 1) | (0, 0.5, 0, 1)（常量诊断值） |
| `SslSsrPbrOriginal` | (0, 0, 0, 0) | (2.0371, 1.1641, 1.1563, 1.0)（真实反射辐射） |

即：调试纹理在全帧只被调试 kernel 写、只被 `SSL Debug View`（draw 1485）读；材质/降噪链使用的纹理与调试纹理完全分离。

## 5. 验收映射

| 标准 | 结果 |
|---|---|
| F-04 调试参数写入独立 RT | ✅ 9839 仅 380(CS 写) / 1485(PS 读) |
| F-04 材质 SSR 纹理不被调试值替换 | ✅ 9766 由 SSR 计算链写、6 个不透明 draw 读，统计为真实辐射 |
| F-04 调试视图下降噪链仍运行 | ✅ 调试帧 7 dispatch，`denoiseChainRan=true` |
| R1 PBR 全分辨率 dispatch 数 | ✅ 关闭调试 6；调试帧 7（仅多调试 kernel） |
| R5 调试视图不影响最终图 | ✅ 按可实现语义：调试视图按设计替换最终显示，但材质与降噪数据不被污染；debug_off 用例与基线路径一致 |
| 无 NaN/Inf | ✅ `nonFinitePixels=0` |

## 6. 已知边界

- 交付的 player 是 Development 构建，DXBC 反射名被剥离；调度身份由 CS shader ResourceId 与资源使用/统计证明，常量值由 DEV 统计与 `Report.json` 证明。
- `.rdc` 本地保留，不入库：

| 抓帧 | 字节 | SHA-256 |
|---|---|---|
| `forward_ssr_pbr_full_debug_off_frame145.rdc` | 17,943,572 | `e05a92780a6d2a377556ecda08161d0099d3c3215dce58a22198a71e14012a68` |
| `forward_ssr_pbr_full_debug_params_frame160.rdc` | 16,813,415 | `329b0d1ddd7d61966df3c38f3ea1075d57a0d302fe5e305f72e4db204a41611f` |

## 7. 文件

```text
validation/P3-2/
  P3Manifest.json
  P3Census.json
  Report.md
  Player/
    Report.json
    forward_ssr_pbr_full_debug_off.png
    forward_ssr_pbr_full_debug_params.png
    *.rdc ×2（本地，不入库）
```
