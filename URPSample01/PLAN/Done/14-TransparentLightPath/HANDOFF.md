# 14-TransparentLightPath：透明 / forward-only 光路接管（决策探针）

> 建立：2026-11-22；前置：[`PLAN/Done/12-GpuLightList`](../12-GpuLightList/HANDOFF.md)（GPU 列表为默认）、[`PLAN/Done/13-GpuLightList256`](../13-GpuLightList256/HANDOFF.md)（256 灯成立）。
> 性质：决策探针，不改默认路径。只回答“透明/forward-only 与 deferred cluster 的差异有多大、值不值得接管”，据此决定是否进入 C2 实现轮。
> 状态：**测量完成（2026-11-22）。结论：GO C2。** 默认 4 盏上限下，256 灯的透明像素有 **89.4%** 与 Forward+ 语义差异 >8；上限拉到 8 只降到 66.5%。per-object 路径没有可分辨的帧时间增量（Forward+ 在 256 灯 +0.17 ms），夹具未污染对照（48 灯 stencil vs clustered 0.806/9，与 13 轮一致）。证据见 [validation/README.md](validation/README.md)。

## 0. 问题

CD（clustered deferred）渲染器里，透明与 forward-only 物体走 per-object forward 灯路径：

- 灯集合来自 CPU 逐物体索引 `_AdditionalLightsIndices`，每物体最多 `m_AdditionalLightsPerObjectLimit` 盏（CD 资产实测 = 4；引擎上限 8）。
- 同一像素背后的不透明表面走 cluster 位图（256 灯场里 item 覆盖更高）。
- 所以同一场景里，透明表面与它后面的不透明表面看到的是**两份不同的灯集合**；这也是 `_AdditionalLightsIndices` / per-object 索引删不掉的原因（[clean.md](../12-GpuLightList/clean.md) C2）。

不能直接打开 `_FORWARD_PLUS`：

1. Forward+ 下 `m_ZBinsBuffer`/`m_TileMasksBuffer` 被分配成 `Constant`（`ForwardLights.cs:187-193`），而 GPU 构图要求 `!m_UseForwardPlus`（`ForwardLights.cs:464-472`）。
2. `Clustering.hlsl` 读 cbuffer 版 `URP_ZBins`（`Input.hlsl:163-170`），clustered 绑定的是同名 `StructuredBuffer<uint>`（`ClusteredDeferred.hlsl:74-78`）。

真正的接管需要 forward 端新增读结构化位图的分支（新关键字/变体），属于 C2 的实现量；本轮先量化收益。

## 1. 冻结决策（仅针对探针）

| ID | 决策 | 值 | 理由 |
|---|---|---|---|
| **D-T1** | 夹具 | arena 增加一排半透明 Lit 球（7 个，`_BaseColor.a = 0.55`，z≈-4），由 `--cd-transparent` 开启；默认关闭 | 透明像素与灯场重叠，且不遮挡既有 receiver；默认关闭保证既有 A/B 不受影响 |
| **D-T2** | 参照 | 透明像素的“cluster 语义”参照 = Forward+；stencil 只作管线对照（它与 clustered 同样走 per-object） | 只有 Forward+ 的透明表面按逐像素 cluster 取光 |
| **D-T3** | 量化口径 | 全屏 diff + 透明组投影矩形内 diff；只取同一次运行内的对比 | 透明像素占比小，全屏 mean 会稀释；矩形是投影包围盒的近似，含少量背景 |
| **D-T4** | 上限对照 | `maxAdditionalLightsCount` 4（资产默认）vs 8（引擎上限），只作用于非 Forward+ 管线 | 回答“提高每物体上限能否替代接管” |
| **D-T5** | 不改默认 | 不动 `_FORWARD_PLUS`、不改 shader、不删 `_AdditionalLightsIndices` | 探针只加夹具与 CLI |

## 2. 测量矩阵

同一 D3D11 player、1280×720；每组都以 stencil / clustered / Forward+ 三模式跑 0/8/48/256 灯，并对每个灯数截图：

| 运行 | 开关 | 用途 |
|---|---|---|
| `limit4` | `--cd-transparent` | 现状（4 盏上限）下透明与 cluster 的差异 |
| `limit8` | `--cd-transparent --cd-per-object-limit 8` | 上限拉满后的差异与成本 |
| `control` | 无透明 | 透明组本身的 frame time 成本 |

新增 CLI：`--cd-transparent`、`--cd-per-object-limit <n>`、`--cd-screenshot-lights 0,8,48,256`（逗号列表）。

## 3. 闸门（决定下一轮）

| 闸门 | 判定 | 后续 |
|---|---|---|
| **G-T1 差异存在** | `limit4` 下 clustered vs Forward+ 在透明矩形内 mean 随灯数上升，256 灯 mean > 2/255 或 >1% 像素 >8 | 接管有正确性收益 |
| **G-T2 上限可解** | `limit8` 把同一矩形差异压到阈值内（mean ≤ 2、max ≤ 24） | C2 可退化为“提高上限 + 文档”，不改 shader |
| **G-T3 成本可忽略** | 透明组的帧时间增量 ≤ 噪声（约 0.02 ms）或与上限无关 | C2 是清理/一致性工程，不是性能优化 |
| **G-T4 无回归** | `control` 与既有 48 灯基线一致（stencil vs clustered mean ≤ 2、max ≤ 24） | 夹具未污染 A/B |

结论三分支：**接管**（新 forward 关键字读结构化位图）/ **只提上限**（资产 + 文档）/ **维持现状**（记录原因）。

## 4. 证据布局

```text
PLAN/Done/14-TransparentLightPath/validation/
├── logs/                     # 构建与运行日志
├── limit4/                   # CdPerf.json/csv + PNG（每灯数每模式）
├── limit8/
├── control/
├── capsrc/                   # 抓帧进程的 CdPerf.json（含全局关键字）
└── rdc/                      # 1 帧 clustered + 透明，证明透明 draw 走 _ADDITIONAL_LIGHTS
```

## 5. 复跑命令

构建段沿用 `ClusteredDeferredArenaBuild.BuildD3D11AndExit`（`ValidationRoot` 已指向本轮 `validation/`）；运行段与 footprint 统计脚本见 [validation/README.md](validation/README.md)。

## 6. 结果（2026-11-22）

透明 footprint（clustered on/off 差分 >8）约占屏幕 11.7%–13.2%。footprint 内 `ClusteredDeferred vs ForwardPlus` 的通道最大差：

| 灯数 | 默认上限 4：mean / max / >8% | 上限 8：mean / max / >8% |
|---:|---|---|
| 0 | 0.374 / 32 / 0.02% | 0.374 / 32 / 0.02% |
| 8 | 0.310 / 32 / 0.02% | 0.310 / 32 / 0.02% |
| 48 | **4.587 / 243 / 15.53%** | **1.815 / 240 / 4.24%** |
| 256 | **51.431 / 239 / 89.44%** | **33.856 / 238 / 66.46%** |

| 闸门 | 判定 | 依据 |
|---|---|---|
| G-T1 差异存在 | **PASS** | 48 灯 15.5%、256 灯 89.4% 的透明像素差 >8；0/8 灯仅 0.02% |
| G-T2 上限可解 | **FAIL** | 上限 4→8 把 48 灯压到 4.2%，256 灯仍 66.5%；残余远高于 2/255 阈值 |
| G-T3 成本 | ○ 未达“可忽略”，但不改变结论 | control→limit4：stencil/clustered 的 \|Δ\| ≤ 0.06 ms；Forward+ 256 灯 +0.17–0.18 ms（逐像素扫完整 cluster）。成本由取光方式决定，C2 的收益是正确性 |
| G-T4 无回归 | **PASS** | control 与 13 轮一致：48 灯 S/C 0.806/9、C/F 0.209；256 灯 C/F 0.290 |

- RenderDoc（`rdc/transparent-clustered-d3d11_frame228.rdc`）：透明球在 `DrawTransparentObjects` 里 3 个 draw（EID 677/680/683），PS 只读常量缓冲、无 structured buffer；cluster SRV 只出现在 `Clustered Deferred` 的光照 dispatch（EID 624）。采样帧关键字 `_ADDITIONAL_LIGHTS=1`、`_FORWARD_PLUS=0`。
- 256 灯 stencil 参照仍因 `R11G11B10f` 逐灯混合失效（13 轮结论），本轮语义参照统一用 Forward+。

## 7. 决定与下一步

**进入 C2**：在 forward 端新增读现有 structured `URP_ZBins`/`urp_Tiles` 的关键字/迭代器（Lit/SimpleLit/ComplexLit/Particles 变体），让透明与 forward-only 和 deferred 共用同一张 GPU 光表；`--cd-transparent` 夹具与 footprint 统计作为该轮的验收基线。只提高 `maxAdditionalLightsCount` 不足以免除分歧，也不足以删除 per-object 索引。

C2 的验收口径直接沿用本轮：256 灯下 footprint 内 C/F 的 >8 比例降到与 0 灯基线同量级（≤1%），且 control 的 A/B 不回归。

## 6. 回滚

- 夹具受 `--cd-transparent` 控制，默认关闭；新增一个材质资产（`CdTransparent.mat`）与 runner 的一个字段。
- 若结论为“维持现状”，保留 HANDOFF + 证据，删掉夹具与 CLI 即可；不触碰默认渲染路径。
