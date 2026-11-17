# P3-3 透明 SSR 深度翻转与关键字隔离修复（F-06/F-08）证据（2026-11-17）

> 任务：P3-3 / F-06、F-08（Medium）。目标：透明深度种子遵循 `CopyDepthPass` 的 Y 翻转规则；透明 SSR 不依赖不透明 pass 的 ComputeShader keyword 副作用。
> 结论：**两项修复完成**。深度/法线方向在相机深度视图与透明深度视图之间逐像素一致（面板与水印以外 826,164 像素最大差 0）；透明 trace 与不透明 PBR trace 以不同编译变体同帧共存，首次运行时的 `_SsrOutputTextureRW` 未绑定错误消失。

## 1. 缺陷与修复

### F-06 透明深度种子忽略 Y 翻转

旧实现固定 `Blitter.BlitTexture(cmd, depthSource, new Vector4(1, 1, 0, 0), material, 0)`，而 URP `CopyDepthPass`（`CopyDepthPass.cs:180`）在源的 Y 方向与目标不同时翻转。Y 翻转目标（部分 RT/XR）上，种子深度与后续 `_SsrDepthTexture` 读取方向相反，透明 SSR 命中位置上下颠倒。

修复：复用 `CopyDepthPass` 判定（`TransparentDepthNormalRenderPass.cs:120-128`）：

```csharp
bool yflip = cameraData.IsHandleYFlipped(depthSource) != cameraData.IsHandleYFlipped(m_History.transparentDepth);
Blitter.BlitTexture(cmd, depthSource, new Vector4(1f, yflip ? -1f : 1f, 0f, yflip ? 1f : 0f), material, 0);
```

DEV 统计记录帧号、`yflip`、源/目标各自的翻转状态。

### F-08 共享 ComputeShader keyword（实际危害强于原记录）

原记录为“透明 pass 未显式设置 `SSR_HALF_RES` 与 `SSGI_BLUE_NOISE`，靠调用顺序正确”。首次 GPU 运行暴露了更强的故障：`ComputeShader` 的 keyword 是**资产状态**，在排队的 dispatch 真正执行时才解析（`ScriptableRenderer` 按 pass 逐个提交命令缓冲）。透明 pass 在共享 `ssrCompute` 上设置 `SSR_APPROX` / `SSR_HALF_RES` 后，不透明 pass 已排队的 PBR reproject 在执行时按 Approximation 变体运行，player 报错：

```text
Compute shader (ScreenSpaceReflections): Property (_SsrOutputTextureRW) at kernel index (1) is not set
```

半分辨率 PBR 输出因此缺失反射辐射。仅补全关键字无法解决同帧两个变体共存的问题。

修复：

- `ScreenSpaceLightingResources` 拥有独立实例 `ssrTransparentCompute = Object.Instantiate(ssrCompute)`（`ScreenSpaceLightingResources.cs:139-149`，`DisposeMaterials` 中销毁），透明 pass 的 keyword 不再影响不透明资产。
- 透明 pass 入口显式设置它依赖的全部关键字：`SSR_HALF_RES=false`、`SSR_NORMALS_OCT=false`、`SSR_APPROX=true`、`SSGI_BLUE_NOISE=资源存在`、`SSL_XR_ARRAY`（`TransparentSsrPass.cs:146-200`），并绑定蓝色噪声 LUT。
- DEV 统计记录 keyword 帧号、四个关键字的实际状态、LUT 是否绑定、`s_LastIsolatedCompute`（`!ReferenceEquals(compute, m_Resources.ssrCompute)`）。

新增 EditMode 回归（`ScreenSpaceLightingTransparentSsrTests`）：

- `DepthSeedAppliesTheCopyDepthYFlipRule`：源码必须使用 `IsHandleYFlipped` 差值。
- `TransparentTraceOwnsEverySharedKeyword`：透明 pass 必须显式写四个关键字。
- `TransparentPassUsesAnIsolatedComputeInstance`：`Object.Instantiate` 得到的实例关键字与源资产互不影响。

## 2. 产物与复现

| 项 | 值 |
|---|---|
| Manifest | [P3Manifest.json](P3Manifest.json) |
| 用例 | `forward_ssr_pbr_half_transparent`（透明 opt-in、half PBR，抓帧）、`forward_camera_depth_view`（debugMode 6）、`forward_transparent_depth_view`（debugMode 15） |
| Player | `Builds/SslValidationPlayer/SslValidationPlayer.exe`（D3D11、Development、1280×720） |
| 抓帧 | `renderdoccmd capture -w --opt-hook-children … -ssl-validation-manifest …P3-3/P3Manifest.json -ssl-validation-out …P3-3/Player` |

`Player/Report.json`：3/3 通过，`nonFinitePixels=0`。最终全量 EditMode 套件 187/187 通过。

## 3. 数值证据

三个用例的 `transparentSsr`（DEV 静态字段反射）：

| 字段 | 值 |
|---|---|
| keywordFrameIndex / depthSeedFrameIndex | 19 / 34 / 47（与用例帧一致，无陈旧） |
| `SSR_HALF_RES` | **false** |
| `SSR_NORMALS_OCT` | **false** |
| `SSR_APPROX` | **true** |
| `SSGI_BLUE_NOISE` | true，且 `blueNoiseTexturesBound=true`（两者一致） |
| isolatedCompute | **true** |
| depthSeed sourceFlipped / destinationFlipped | true / true |
| depthSeed yflip / expected | **false / false** |
| passed | **true** |

深度视图一致性（`forward_camera_depth_view.png` vs `forward_transparent_depth_view.png`，1280×720）：

| 区域 | 像素数 | 中位差 | p95 | p99 | 最大差 | 非零像素 |
|---|---|---|---|---|---|---|
| 全图 | 921,600 | 0 | 0 | 14 | 28 | 40,992 |
| 排除透明面板（x 500–790, y 295–490）与水印（y ≥ 690） | **826,164** | **0** | **0** | **0** | **0** | **0** |

差异全部集中在透明立方体面板与水印内；其余像素逐字节一致，说明种子深度与相机深度方向相同。半分辨率 PBR 的像素展开角为 `0.0032075015`（640×360），与 P3-1 的公式一致。

## 4. GPU 证据

抓帧 `forward_ssr_pbr_half_transparent_frame160.rdc`，`P3Census.json` 记录全部事件。

### 4.1 不透明 half PBR 链（marker 324–468，7 dispatch）

| order | event | kernel | CS shader |
|---|---|---|---|
| 0 | 345 | trace | `ResourceId::9844` |
| 1 | 367 | reproject | `ResourceId::9847` |
| 2 | 394 | temporalAccumulationHalfRes | `ResourceId::9850` |
| 3 | 408 | copyHistory | `ResourceId::9853` |
| 4 | 429 | bilateralFilterH_HR | `ResourceId::9855` |
| 5 | 450 | bilateralFilterV_HR | `ResourceId::9858` |
| 6 | 465 | upsample | `ResourceId::9859` |

不透明 trace（345）为 PBR 变体：cbuffer1 144 字节 / 9 个变量，7 个只读绑定（深度、法线、深度金字塔、偏移 buffer 与 3 张 LUT），1 个 UAV。reproject（367）有 2 个 UAV（命中点 + `SsrPbrOriginal`）。

### 4.2 F-06 深度种子（marker 1021）

- 1035：blit 以 `_CameraDepthAttachment`（`ResourceId::9566`）为源，写入 `SslTransparentDepth`（`ResourceId::9741`）。
- 9741 后续用途：1054/1072 DepthStencilTarget（透明物体写入私有深度/法线），**1101/1123 CS_Resource**（透明 trace / reproject 读取）。种子确实是透明 SSR 的深度来源。

### 4.3 F-08 透明变体同帧共存（marker 1083–1126）

| order | event | kernel | CS shader | cbuffer1 | 只读纹理 | UAV |
|---|---|---|---|---|---|---|
| 0 | 1101 | trace（Approximation） | `ResourceId::9865` | 112 B / 7 变量 | 3 + 偏移 buffer | 1 |
| 1 | 1123 | reproject | `ResourceId::9868` | 112 B / 7 变量 | 4 | 2 |

对比不透明 trace 的 `9844`（144 B / 9 变量、3 张 LUT）与透明 trace 的 `9865`（112 B / 7 变量、无 LUT）：两者是**同帧内不同编译变体**。透明 reproject（1123）写 `SslSsrTransparentOutput`（`ResourceId::9757`，CS_RWResource），随后由 PS draw 1200 读取（PS_Resource），透明材质拿到真实结果且无未绑定属性错误。

LUT 使用说明：Approximation trace 不采样蓝噪声（`SsgiGetSequenceSample` 仅在 PBR 分支调用，`ScreenSpaceReflections.compute:252`），因此 `OwenScrambledNoise256` / `ScramblingTile1SPP` / `RankingTile1SPP` 的 GPU 读取只出现在不透明 PBR trace（event 345）。透明 pass 侧“绑定意图”由 DEV 统计 `blueNoiseTexturesBound=true` 记录。

## 5. 验收映射

| 标准 | 结果 |
|---|---|
| R4 透明 Y 翻转（F-06） | ✅ 采用 `CopyDepthPass` 同款规则；player 记录源/目标翻转状态与判定结果一致；深度视图在面板外逐像素一致 |
| R4 透明 keyword 生命周期 | ✅ 透明 pass 内启用、`OnCameraCleanup` 关闭；逐事件 keyword 生效范围不在本任务 |
| R7 透明 pass 不依赖不透明副作用（F-08） | ✅ 独立实例 + 四个关键字显式设置；GPU 证明同帧 PBR/Approx 两个变体共存 |
| R1 dispatch 数 | ✅ half PBR 7 + 透明 2 |
| 首次运行错误消失 | ✅ 无 `_SsrOutputTextureRW ... not set`；`nonFinitePixels=0` |
| R5 半分辨率输出 | ✅ 截图中地面反射正常，半分辨率 PBR 像素展开角正确（P3-1 已量化） |

## 6. 已知边界

- 独立实例依赖 `UnityEngine.Object.Instantiate(ComputeShader)`：已验证实例的 keyword 与源资产相互独立，但会额外持有一份 compute 程序；资源生命周期随 feature 释放（EditMode 覆盖）。
- 桌面 D3D11 的 RT 用例中源与目标句柄同为 Y 翻转态（`sourceFlipped=true, destinationFlipped=true`），因此本帧 `yflip=false`。真正需要翻转的路径（二者不同）由规则源码断言覆盖，GPU 举证留给 P4 的分辨率/XR 矩阵。
- `.rdc` 已入库：

| 抓帧 | 字节 | SHA-256 |
|---|---|---|
| `forward_ssr_pbr_half_transparent_frame160.rdc` | 18,059,585 | `731b5bcecd6009bc58b250364cf4f3b7d47cdd2a7aa4d9f48ff0ad9c40997b9a` |

## 7. 文件

```text
validation/P3-3/
  P3Manifest.json
  P3Census.json
  Report.md
  Player/
    Report.json
    forward_ssr_pbr_half_transparent.png
    forward_camera_depth_view.png
    forward_transparent_depth_view.png
    *.rdc ×1（已入库）
```
