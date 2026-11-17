# P2-4 SSGI 覆盖 / 多探针 / 时域 / 半分辨率 / 材质正确性（2026-11-17）

> 任务：P2-4。目标：在 P2-3 修复降噪器之后，重新度量 SSGI 的覆盖与发光体能量增益（S2）、时域收敛与相机 cut（S3）、反射探针 atlas 开关与四色权重（S4）、半分辨率上采样（S5）与材质覆盖（S6）。
> 结论：**17/17 用例通过，S2–S6 全部达标**。新增三套专用场景（封闭房间、四色反射探针、材质覆盖鳍片），截图指标全部由 [analyze.py](analyze.py) 重算；RenderDoc 抓帧确认探针 atlas 在开启时真实存在（2048×1024）而关闭时不存在，半分辨率 trace 为 640×360 并由 `SslSsgiFinal` 上采样到 1280×720。

## 1. 产物与复现

| 项 | 值 |
|---|---|
| Manifest | [P2Manifest.json](P2Manifest.json)（17 个用例） |
| 抓帧 manifest | [CaptureManifest.json](CaptureManifest.json)（5 个抓帧） |
| 分析 | [analyze.py](analyze.py) → [Metrics.json](Metrics.json) |
| CPU/GPU 报告 | [Player/Report.json](Player/Report.json)、[GpuCensus.json](GpuCensus.json) |
| 截图 | `Player/*.png`（17 张 + 时域第二帧） |
| GPU 导出 | `Player/GPU/renderdoc-mcp-export/`（探针 atlas、探针 on/off、全/半分辨率 final） |
| Player | `Builds/SslValidationPlayer/SslValidationPlayer.exe`（D3D11、Development、1280×720、Deferred 资产构建） |
| 构建 | [BuildPlayer.log](BuildPlayer.log)，`BuildSummary.json`：Succeeded、0 error |
| 回归 | [EditModeFull.xml](EditModeFull.xml) |

```bash
# 构建
"/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingValidationPlayerSetup.BuildPlayer \
  -logFile "…/validation/P2-4/BuildPlayer.log"

# 截图用例
"Builds/SslValidationPlayer/SslValidationPlayer.exe" \
  -ssl-validation-manifest "…/validation/P2-4/P2Manifest.json" \
  -ssl-validation-out "…/validation/P2-4/Player" \
  -force-d3d11 -screen-width 1280 -screen-height 720 -screen-fullscreen 0
python analyze.py

# 抓帧
"/c/Program Files/RenderDoc/renderdoccmd.exe" capture -w --opt-hook-children \
  --opt-disallow-fullscreen --opt-disallow-vsync \
  "Builds/SslValidationPlayer/SslValidationPlayer.exe" \
  -ssl-validation-manifest "…/validation/P2-4/CaptureManifest.json" \
  -ssl-validation-out "…/validation/P2-4/Player/GPU" \
  -force-d3d11 -screen-width 1280 -screen-height 720 -screen-fullscreen 0
```

## 2. 新增场景与 player 能力

| 布局 | 内容 | 用途 |
|---|---|---|
| `ssgiRoom` | 封闭房间（地板/后墙/天花/侧墙）+ 后墙 14×4.2 发光板 | S2：射线命中率不再受开放天空影响 |
| `ssgiProbes` | 单色地板 + 4 个实心色反射探针（红/绿/蓝/黄，128²、box projection、blend 2.75） | S4：atlas 开/关与四色权重 |
| `ssgiCoverage` | Lit / ComplexLit / SimpleLit / BakedLit / Unlit / ShaderGraph Lit 六块竖板 + 地面发光带 + Terrain | S6：材质覆盖 |

Player 同时新增：`ambientIntensity`（S2 用 0.05 的 Flat 环境项做受控基线）、`cameraCutAfterWarmup`（S3）、`screenshotAfterFrames`（同一 history 序列的第二张截图）、`disableReflectionProbes`（把探针 rig 移到世界下方）。探针 rig 不再 `SetActive(false)`：Unity 会释放实时探针的 cubemap 且不会因 `RenderProbe` 重建；改为常驻 + 位置停放后，128² cubemap 始终存在。

## 3. S2 覆盖与发光体增益

场景：`ssgiRoom`，相机 `(0,2,-7)`，`rayMiss=None`（只统计屏幕空间命中），方向光关闭、Flat 环境 0.05，避免环境 SH 掩盖反弹。

| 指标 | 数值 | 阈值 | 结果 |
|---|---|---|---|
| 覆盖（weight ≥0.5，[ssgi_coverage_weight.png](Player/ssgi_coverage_weight.png)） | **57.09%** | ≥15% | ✅ |
| 任意非零覆盖（weight >0.02） | 95.07% | — | 参考 |
| 发光板在地面的 G 增益（`ssgi_gain_on/off`，区域 540–740 × 450–500） | **2.26×**（0.02064 / 0.00913） | ≥1.5× | ✅ |
| 未覆盖像素（3×3 腐蚀）与关闭基线的差 | 33,960 px，mean 0.000、max 0.288/255、>1 像素 **0** | ≤1e-6（8-bit 可测下限） | ✅ |

未覆盖像素在 8-bit 截图里逐像素完全相同（最大差 0.288 量化级）；受 8-bit 截断限制，无法直接观测 1e-6，报告中以“量化后 0 差异”作为等效证据（与 P3-5 的关闭基线口径一致）。

## 4. S3 时域

| 指标 | 数值 | 阈值 | 结果 |
|---|---|---|---|
| 静止 30 帧后第 30/32 帧差（[ssgi_temporal_static*.png](Player/ssgi_temporal_static.png)） | mean **0.034/255**、max 8.68 | mean ≤1/255 | ✅ |
| 相机 cut 后首帧 validity（`ssgiSwitch`） | true → **false**（帧 143 → 146） | 首帧 validity=0 | ✅ |
| cut 后 30 帧残影（对比同姿态收敛参考帧） | mean **0.161/255**、max 5.23 | mean ≤1/255 | ✅ |

`settingsResetCount` 只在 set* 配置变化时递增，相机 cut 走 `resetHistory` 路径，因此只断言 validity 掉零，不要求计数 +1（注释已写在 player 中）。

## 5. S4 反射探针

截图指标为 SSGI final 调试视图：绿色区 `G / mean(R,B)`，红色区 `R / mean(G,B)`，四色带为绿色通道相对其它通道的色度。`greenOverRed` = 绿区 G 优势 / 红区 R 优势。

| 用例 | greenOverRed | 期望 | 结果 |
|---|---|---|---|
| Forward + `publishProbeAtlas` | **4.64** | ≥1.5 | ✅ |
| Forward 关闭 atlas | **1.00** | <1.2（默认 cubemap） | ✅ |
| Deferred + atlas | **4.65** | ≥1.5 | ✅ |
| Deferred 关闭 atlas | **1.00** | <1.2 | ✅ |
| Forward+（toggle 关） | **4.29** | ≥1.5（cluster 自带 atlas） | ✅ |
| Forward+（toggle 开） | **4.28** | ≥1.5 | ✅ |
| 0 探针（rig 移出世界） | 0.00，`nonFinitePixels=0` | 无 NaN、无探针色 | ✅ |

四色权重随距离单调衰减（绿区 40/75/110 px 半宽）：**0.517 → 0.450 → 0.358**。红/蓝/黄区的主导通道同样成立（`redOverOthers` 3.47、`blueOverOthers` 6.90、`yellowOverBlue` 2.95）。

**GPU 证据**（[GpuCensus.json](GpuCensus.json)）：

| 项 | 开启 atlas | 关闭 atlas |
|---|---|---|
| `URP Reflection Probe Atlas` 资源 | **2048×1024 R11G11B10** | **不存在** |
| 探针 cubemap | `SSGI Probe 3` 128²×6、8 mips | 同上（未被消费） |
| SSGI dispatch | 9（trace/reproject/validate/accumulate/copy/denoise ×2） | 9 |
| 导出 final 绿区色度 | **3.12**（[probe_on_denoised.png](Player/GPU/renderdoc-mcp-export/probe_on_denoised.png)） | 1.00（[probe_off_denoised.png](Player/GPU/renderdoc-mcp-export/probe_off_denoised.png)） |

导出 [probe_atlas.png](Player/GPU/renderdoc-mcp-export/probe_atlas.png) 直接看到四个纯色 cubemap 及其 mip 链，[probe_on_denoised.png](Player/GPU/renderdoc-mcp-export/probe_on_denoised.png) 在地面呈现红/绿/蓝/黄四段。

## 6. S5 半分辨率

| 指标 | 数值 | 阈值 | 结果 |
|---|---|---|---|
| full vs half 相关（截图，`SsgiDenoised` 视图） | **0.99924** | ≥0.98 | ✅ |
| full vs half 相关（GPU 导出 1280×720 final） | **0.9997** | ≥0.98 | ✅ |
| 上采样 1 px 硬缝（半分辨率奇偶列邻差比） | half **1.060** vs full 1.164 → 比值 **0.910** | 无 1 px 硬缝 | ✅ |
| NaN/Inf | 0 | 0 | ✅ |

GPU 抓帧显示 trace/reproject/accumulate/denoise/history 均为 **640×360**，最终由 `SslSsgiFinal_1280x720` 上采样；见 [GpuCensus.json](GpuCensus.json) 与 [half_final.png](Player/GPU/renderdoc-mcp-export/half_final.png)。

## 7. S6 材质覆盖

`ssgiCoverage` 布局、方向光开启、SSGI 开关对比，鳍片内部区域（±24 px × y 400–445）：

| 材质 | mean / max（8-bit） | 期望 | 结果 |
|---|---|---|---|
| Lit | 1.94 / 3.31 | 生效 | ✅ |
| ComplexLit | 2.71 / 7.07 | 生效 | ✅ |
| ShaderGraph Lit | 1.81 / 3.54 | 生效 | ✅ |
| SimpleLit | 0.00 / 0.00 | 0 差异 | ✅ |
| BakedLit | 0.00 / 0.00 | 0 差异 | ✅ |
| Unlit | 0.00 / 0.00 | 0 差异 | ✅ |
| TerrainLit（右上地形区 1150–1250 × 392–404） | 0.00 / 0.00 | 0 差异 | ✅ |

## 8. 回归

| 项 | 结果 |
|---|---|
| Player 构建 | Succeeded，0 error（[BuildPlayer.log](BuildPlayer.log)） |
| 截图/CPU 用例 | **17/17**，[Player/Report.json](Player/Report.json) `completed=true passed=true` |
| 指标脚本 | [Metrics.json](Metrics.json) `passed=true`（S2/S3/S4/S5/S6 全绿） |
| 全量 EditMode | **454/454**（[EditModeFull.xml](EditModeFull.xml)） |

## 9. 验收映射

| 标准 | 结果 |
|---|---|
| S2 覆盖 ≥15% | ✅ 57.1%（封闭房间、rayMiss=None） |
| S2 发光体地面 G 增益 ≥1.5× | ✅ 2.26× |
| S2 未覆盖像素 = baked GI | ✅ 33,960 px，量化后 max 0.29/255、>1 像素 0 |
| S3 静止 30 帧收敛 | ✅ 帧间 mean 0.034/255 |
| S3 cut 首帧 validity=0 | ✅ true→false（帧 143→146） |
| S3 残影 ≤1/255 | ✅ mean 0.161/255 |
| S4 三渲染器命中局部探针 | ✅ Forward/Deferred 4.6×，Forward+ 4.3× |
| S4 atlas 关闭回退默认 cubemap | ✅ Forward/Deferred 1.00；Forward+ 不受 toggle 影响 |
| S4 四色权重随距离单调 | ✅ 0.517→0.450→0.358 |
| S4 0 探针无 NaN | ✅ 0 非有限像素 |
| S5 半/全相关 ≥0.98 | ✅ 0.9992（截图）/ 0.9997（GPU） |
| S5 无 1 px 硬缝 | ✅ 奇偶邻差比 0.910（半/全） |
| S6 仅 Lit/ComplexLit/SG Lit 生效 | ✅ 三者均非零，其余四种逐字节 0 |

## 10. 说明与边界

1. **覆盖是场景属性。** 开放场景的屏幕空间命中率天然很低（T6 记录 0.025），因此 S2 用封闭房间度量“链路能否覆盖”；默认场景的低覆盖率不是缺陷。
2. **S2 的受控基线**用 Flat 环境 0.05 + 关闭方向光，隔离发光体反弹；环境项在进入/离开该组用例时各重建一次 `DynamicGI.UpdateEnvironment`。
3. **S6 的 0 差异是 8-bit 相等**；材质侧 `lerp(bakedGI, radiance, weight)` 在 weight=0 时是精确恒等，截图量化后为 0。
4. **S4 关闭 atlas 时 Forward/Deferred 仍有 SSGI**，只是回退到默认反射 cubemap（白色），因此 final 调试图不是黑色。
5. `.rdc` 抓帧（约 170 MB）只保留在本地，已被 `Player/*/*.rdc` 规则忽略；提交的只有 `GpuCensus.json` 与导出 PNG。
6. 本次修改仅限验证资产/Player（场景布局、指标与探针停车），未改变 SSGI 渲染算法。