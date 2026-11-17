# P4-3 分辨率 / MSAA / 渲染器矩阵（含 F-12，2026-11-17）

> 任务：P4-3。目标：X2——render scale 0.5/0.75/1/2 × MSAA 开/关 × 三渲染器，dispatch 维度正确、无 NaN、无越界；并定位 P1-4 记录的 F-12（Forward+ 下 TerrainLit 只收到环境光）。
> 结论：**20/20 用例通过**。GTAO/SSGI/SSR 的 dispatch 维度随 render scale 精确变化（640×360 / 960×540 / 1280×720 / 2560×1440），MSAA 4× 不产生非有限像素。F-12 的根因是**验证 player 构建期未收集 Forward+ 变体**，不是 Terrain/URP 运行时缺陷；修正构建后 Forward+ 地形像素与 Forward 完全一致。全量 EditMode **455/455**。

## 1. 产物与复现

| 项 | 值 |
|---|---|
| Manifest | [P4Manifest.json](P4Manifest.json)（20 个用例） |
| Player 报告 | [Player/Report.json](Player/Report.json) + 20 张 1280×720 截图 |
| 指标 | [Metrics.json](Metrics.json)（[analyze.py](analyze.py) 重算，`allPassed=true`） |
| F-12 像素 | [TerrainPixels.json](TerrainPixels.json)；抓帧 [TerrainBefore/](TerrainBefore/)、[TerrainAfter/](TerrainAfter/) |
| 构建 | [BuildSummary.json](BuildSummary.json)（Succeeded，0 error；日志本地 `BuildPlayer.log` 不入库） |
| 回归 | [EditModeFull.xml](EditModeFull.xml)（455/455） |

```bash
# 构建（三套 validation pipeline 会在构建期停放到 quality levels）
"/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode -quit \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingValidationPlayerSetup.BuildPlayer \
  -logFile "<abs>/validation/P4-3/BuildPlayer.log"

# 矩阵
"Builds/SslValidationPlayer/SslValidationPlayer.exe" \
  -ssl-validation-manifest "<abs>/validation/P4-3/P4Manifest.json" \
  -ssl-validation-out "<abs>/validation/P4-3/Player" \
  -force-d3d11 -screen-width 1280 -screen-height 720 -screen-fullscreen 0
python analyze.py

# F-12 抓帧（Forward / Forward+，gtaoCoverage + 方向光）
"/c/Program Files/RenderDoc/renderdoccmd.exe" capture -w --opt-hook-children \
  --opt-disallow-fullscreen --opt-disallow-vsync \
  "Builds/SslValidationPlayer/SslValidationPlayer.exe" \
  -ssl-validation-manifest "<abs>/validation/P4-3/DiagManifest.json" \
  -ssl-validation-out "<abs>/validation/P4-3/TerrainAfter" \
  -force-d3d11 -screen-width 1280 -screen-height 720 -screen-fullscreen 0
```

`TerrainBefore/` 由未修正构建期变体收集的同版本 player 产生（P1-4 之后的构建），`TerrainAfter/` 由修正后的 player 产生；两次使用同一 `DiagManifest.json` 与同一相机。

## 2. F-12：Forward+ 变体在构建期被剥离

**复核方法。** 用 RenderDoc 抓取同一 manifest 的 Forward 与 Forward+ 帧，对同一像素 `pick_pixel`。Unity 剥离 shader 名字，disassembly/reflection 无法直接确认变体（本轮 `search_shaders("urp_TileBuffer")` 在修复前后都是 0），因此以像素为判据：

| 抓帧 | 地形像素 (1134,463) | Lit 鳍片 (153,438) |
|---|---|---|
| Forward（修复前） | (0.545, 0.529, 0.514) | (0.549, 0.565, 0.580) |
| **Forward+（修复前）** | **(0.184, 0.235, 0.306)** | (0.549, 0.565, 0.580) |
| Forward（修复后） | (0.545, 0.529, 0.514) | (0.549, 0.565, 0.580) |
| **Forward+（修复后）** | **(0.545, 0.529, 0.514)** | (0.549, 0.565, 0.580) |

Forward+ 的 TerrainLit 在修复前与 P1-4 记录的环境光基线一致；修复后与 Forward 逐通道相同。URP 在 Forward+ 下不请求 `PerObjectData.LightData`（`GetPerObjectLightFlags`），非 Forward+ 变体把 `unity_LightData.z` 当作主光距离衰减，TerrainLit 的内部绘制因此丢掉主光；Lit 鳍片的环境项占比高，数值上看不出差别。两条证据合起来指向“Forward+ 帧跑的是非 FP 变体”。

**根因。** `ShaderBuildPreprocessor` 只收集**构建目标 quality levels 引用的** URP pipeline 资产的 shader feature。项目三个 quality level 原本都指向默认 Forward 资产；`BuildPlayer` 之前只把当前 level 临时指向 Deferred，因此收集到 Forward + Deferred，Forward+ 的 `_FORWARD_PLUS` 变体被剥离。

**修复**（[ScreenSpaceLightingValidationPlayerSetup.BuildPlayer](../../../../../Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingValidationPlayerSetup.cs)）：构建前把 Deferred / Forward / Forward+ 三套 validation pipeline 分别停放到三个 quality level，构建后逐级恢复原值（`QualitySettings.renderPipeline` 是当前 level 的属性，必须切级读写）。player 体积 119.3 MB → 121.7 MB，构建时间从 ~30 s 增至 ~15 min（全量变体编译）。

**影响范围。** P1-4 §6 的“Forward+ 地形缺主光”不是 Terrain 缺陷；P1-4 的 Forward+ 明暗对比结论已在原文加更正注记。P0–P3 各轮 Forward+ 用例的**相对指标**（开/关比值、dispatch 计数、history 行为）不受影响，但任何依赖直接光照绝对值的 Forward+ 数值不应跨轮比较。

## 3. X2：分辨率和 MSAA 矩阵

`matrix_*` 用例同时开启 GTAO（full-res、temporal）、SSGI（full-res、temporal、denoise）与 SSR（Approximation）；`pbr_*` 用例只开 SSR PBR（关闭 SSGI，保证像素展开角只能来自 SSR pass）。

| 用例 | 渲染器 | Scale | MSAA | 目标 | GTAO AO | SSGI trace | SSR trace | NaN |
|---|---|---|---|---|---|---|---|---|
| matrix_forward_scale50 | Forward | 0.5 | 1 | 640×360 | 640×360 | 640×360 | 640×360 | 0 |
| matrix_forward_scale100 | Forward | 1 | 1 | 1280×720 | 1280×720 | 1280×720 | 1280×720 | 0 |
| matrix_forward_scale200 | Forward | 2 | 1 | 2560×1440 | 2560×1440 | 2560×1440 | 2560×1440 | 0 |
| matrix_forward_scale75 | Forward | 0.75 | 1 | 960×540 | 960×540 | 960×540 | 960×540 | 0 |
| matrix_forwardplus_scale50/100/200 | Forward+ | 0.5/1/2 | 1 | 640×360 / 1280×720 / 2560×1440 | 同步 | 同步 | 同步 | 0 |
| matrix_deferred_scale50/100/200 | Deferred | 0.5/1/2 | 1 | 640×360 / 1280×720 / 2560×1440 | 同步 | 同步 | 同步 | 0 |
| matrix_forward_scale100_msaa4 | Forward | 1 | 4 | 1280×720 | 1280×720 | 1280×720 | 1280×720 | 0 |
| matrix_forwardplus_scale100_msaa4 | Forward+ | 1 | 4 | 1280×720 | 1280×720 | 1280×720 | 1280×720 | 0 |
| matrix_deferred_scale100_msaa4 | Deferred | 1 | 4 | 1280×720 | 1280×720 | 1280×720 | 1280×720 | 0 |
| matrix_forward_scale50_msaa4 | Forward | 0.5 | 4 | 640×360 | 640×360 | 640×360 | 640×360 | 0 |
| pbr_forward_scale50/200 | Forward | 0.5/2 | 1 | 640×360 / 2560×1440 | —（关） | —（关） | 640×360 / 2560×1440 | 0 |
| pbr_forwardplus_scale50/200 | Forward+ | 0.5/2 | 1 | 640×360 / 2560×1440 | — | — | 同步 | 0 |
| pbr_deferred_scale50/200 | Deferred | 0.5/2 | 1 | 640×360 / 2560×1440 | — | — | 同步 | 0 |

每个用例的断言（Player 内完成，`Report.json` 逐条记录）：

- 效果 pass 的 `screenWidth/screenHeight` 等于 `Screen size × renderScale`（新增 `CheckTargetSize`；`Camera.pixelWidth` 不含 render scale，不能用于该断言）。
- GTAO AO 尺寸、组数；SSGI trace/全屏尺寸与各组数；SSR trace/全屏尺寸、组数、dispatch 数；PBR 用例的 denoiser 像素展开角分母来自缩放后的 trace。
- 截图 `nonFinitePixels == 0`。

**MSAA 说明。** 每个 MSAA 用例把 pipeline `msaaSampleCount` 设为 4，并打开 `Camera.allowMSAA`（验证相机默认关闭）。计算 pass 仍写入非 MSAA 纹理，深度在采样前由 URP 解析；dispatch 维度与非 MSAA 完全一致，无 NaN。

## 4. 透明 Y 翻转“源/目标翻转态不同”分支

`CameraData.IsHandleYFlipped` 在 D3D11（`graphicsUVStartsAtTop == true`）下：SceneView/Preview 为 true，backbuffer 为 false，其余任何 RT 为 true。验证器的透明深度源（`_CameraDepthTexture` RTHandle）与目标（history 透明深度 RT）都是非 backbuffer → 两者同为 true，`yflip=false`，与 P3-3 的抓帧一致。

要让两者翻转态不同，必须有且仅有一个句柄是相机目标（例如相机直接渲染到 backbuffer 且深度也指向 backbuffer）；validation pipeline 始终分配中间 RT，因此在 D3D11 桌面上该分支不可达。本轮维持 P3-3 的结论：**规则层由 EditMode 断言覆盖，运行时“翻转态不同”分支不在 D3D11 验证范围**；真正的触发路径是 OpenGL/GLES 与部分 XR 目标，未纳入本轮。

## 5. 动态分辨率

URP 14.0.12 不为 Screen Space Lighting 驱动 `DynamicResolutionHandler`，本轮按计划 §1.2 继续标记**硬件动态分辨率不支持**，不做运行时验证；render scale 矩阵已覆盖 URP 自身的缩放路径。

## 6. 覆盖边界

- 矩阵用例均为全分辨率效果；半分辨率 trace（SSGI/SSR）已在 P2-4/P3-4 覆盖，未在缩放矩阵中重复。
- MSAA 只取 4×（2×/8× 未跑）；MSAA 与半分辨率效果的组合未覆盖。
- PBR 用例刻意关闭 SSGI，沿用 P3-1 的归属约束（`_SsgiDenoiserParams.x` 的像素展开角只能由 SSR 写入）。
- 越界读取以“所有 dispatch 组数与纹理尺寸精确匹配 + 无 NaN/Inf 像素”间接判定；D3D11 未启用 GPU 验证层，不做逐访问断言。
