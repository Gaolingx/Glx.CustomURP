# 12-GpuLightList：GPU 光表与异步两段式（计划）

> 状态：**计划已冻结（D-G1–D-G4）；C1 已实施并验收；G1–G3 待执行**。
> 建立：2026-11-22；前置轮次：[`PLAN/Done/11-LightCulling`](../../Done/11-LightCulling/HANDOFF.md)（Clustered Deferred v1，已归档）。
> 善后清单：[clean.md](clean.md)（C1 已落地；C2–C8 仍为规划）。
> 基线：C1 提交后的 HEAD（见 `git log`）；C1 证据在 `validation/C1/`。
> 本轮范围：只做“光列表怎么建”和“怎么派发”，不改 BRDF/GBuffer/阴影/cookie/layers/AO 语义。

---

## 0. 一页结论

11 轮已经证明：tile/zbin 列表可以被 compute 内核消费，且 24 灯以上比 stencil deferred 便宜。C1 进一步去掉了 clustered 模式下无人消费的 stencil 灯数组预计算。**本轮要回答的问题只有一个：把列表构建从 CPU Burst jobs 搬到 GPU（并允许与 GBuffer 重叠）是否值得。**

判定顺序固定为“先对、再快”：

1. **G1 正确性优先**：GPU 构建出的 `URP_ZBinBuffer`/`urp_TileBuffer` 必须与 CPU jobs 的输出等价；kernel、HLSL 迭代器、`_FPParams*`、缓冲布局一行不改。T4/T5 矩阵直接当回归。
2. **G2 只在 G1 通过后做精剪**：FPTL 式 tile 深度 min/max 细剪，输出仍是同一张位图（或同布局的紧凑列表），再次过 T4/T5。
3. **G3 最后做派发层**：空 tile indirect dispatch、variant/分类列表；这一步允许改 kernel，但必须在 G1/G2 的性能数据之上。

**Go/No-Go 闸门**：C1 的 CPU 分解（`validation/C1/cpu/CdCpuBreakdown.json`）给出列表构建相关 CPU 的真实占比。若 64 灯下 `PreSetup + cluster jobs complete` 合计 **< 0.15 ms/帧**，G1 不以“省 CPU”为理由启动，只在它能解锁异步重叠或 256+ 灯扩展性时做；若 ≥ 0.3 ms，G1 直接立项。

---

## 1. 冻结决策（D-G1–D-G4）

| ID | 决策 | 冻结值 | 理由 | 复核条件 |
|---|---|---|---|---|
| **D-G1** | 设备门槛 | 与 `ClusteredDeferredPass.IsSupportedDevice` 完全一致：compute + **常量灯数组**（本 fork `RenderingUtils.useStructuredBuffer == false`）；D3D11 / D3D12 / Vulkan / Metal。GLES/WebGL、`useStructuredBuffer=true`、XR/foveated 一律排除；Preview/Overlay 相机继续回退 stencil deferred。 | GPU 构图只是把同一份常量灯数组读进 compute；再引入第二套设备判定会让回退矩阵分叉。 | 若 Vulkan/Metal 实测不可用，或产品要求 GLES，重开本决策并要求最小 Forward 回退，而不是 stencil。 |
| **D-G2** | 候选灯来源 | **保留 CPU `CullingResults.visibleLights`** 作为候选集（引擎已按 cullingMask/距离/阴影剔除），在 `ForwardLights` 里上传一份构图用的紧凑灯记录（位置、半径/range、spot 内外角与朝向、类型、层掩码）。**GPU 灯注册表不在本轮**。 | 可见性剔除仍是正确性来源；把注册表也搬 GPU 会同时重写阴影 atlas、层语义与烘焙状态，是 clean.md C8 的独立工作量。 | 若可见灯上传本身（`SetupShaderLightConstants` 的常量数组）成为 CPU 瓶颈，先量化再决定是否做结构化缓冲/C8。 |
| **D-G3** | depth prepass / 重叠 | **本轮不新增 depth prepass**。G1 只按灯的包围体做 z 范围与屏幕 tile 覆盖，与现有 CPU 算法等价，因此可以在 GBuffer 之前构建，或用 async compute 与 GBuffer 重叠；FPTL 细剪（G2）若需要 tile 深度，优先复用 GBuffer 后的 `_CameraDepthTexture`（代价是不能重叠），只有实测收益大于一次全屏 prepass 成本时才新增 prepass，并在此重开决策。 | deferred 已经有深度拷贝；prepass 会改变 pass 序、深度绑定与 A/B 基线，收益未证前不引入。 | G2 的细剪收益 < 全屏 prepass 成本时，永久放弃 prepass，接受“构建在 GBuffer 之后”。 |
| **D-G4** | 透明/forward-only | **本轮不接管**：`_FORWARD_PLUS` 保持关闭，`_AdditionalLightsIndices` 与逐物体灯索引保留；透明与 forward-only 继续走 forward 灯路径。 | 零回归优先；透明 cluster 化会同时改材质变体、逐物体索引与 shader stripping，是 clean.md C2 的独立一轮。 | 透明成为主要灯成本时另立计划；在此之前不回退删除 per-object 索引。 |

补充冻结项：

- **布局不变契约（G1）**：`URP_ZBinBuffer`（uint words）、`urp_TileBuffer`、`_FPParams0/1/2`、header 0/1 语义、`URP_FP_*` 宏、tile 宽度 8/16/32/64 自适应选择规则全部保持不变。G1 只替换“谁写这些缓冲”。
- **异步契约**：G1 的两段式（逐灯粗筛 → tile/zbin 展开）在支持异步队列的平台用 `CommandBufferExecutionFlags.AsyncCompute` + fence 与 GBuffer 重叠；否则退回同一队列、GBuffer 之前。两种模式必须产生逐位相同的缓冲，异步只是调度差异。
- **证据契约**：每步都要“缓冲 parity + T4/T5 A/B + CPU/GPU 时间 + 1 个 `.rdc`（≤20 MiB）”。不满足 parity 不进入性能对比。

---

## 2. 起点：C1（已实施）

### 2.1 改动

- `DeferredLights.SetupLights` 新增 `precomputeStencilLights` 参数；`DeferredLights.ShouldPrecomputeStencilLights` 用 `ClusteredDeferredPass.IsEligibleCamera` 判定。clustered base game 相机跳过 `PrecomputeLights`（stencil 灯数组）并把旧句柄置 default；Preview/Overlay/XR 与 stencil 路径照常构建。
- 关键字/常量设置（`_GBUFFER_NORMALS_OCT`、mixed lighting 关键字、`SetupShaderLightConstants`、light layers）保持原样，不受开关影响。
- 新增 EditMode 断言：只有 eligible clustered 相机跳过，回退相机必须构建。
- 新增 CPU 分解探针 `ClusteredDeferredCpuProbe`（默认关闭，arena runner `--cd-cpu` 打开），切分 PreSetup / cluster job 完成 / 上传 / 灯常量 / `DeferredLights.SetupLights` / `PrecomputeLights` / 其他。

### 2.2 证据

| 项 | 结果 | 证据 |
|---|---|---|
| EditMode 全量 | 485/485（新增 C1 用例通过） | `validation/C1/tests/C1EditMode.xml` |
| PlayMode 全量 | 36/36 | `validation/C1/tests/C1PlayMode.xml` |
| T4 特性矩阵（D3D11/D3D12） | 各 7/7 `passed=true` | `validation/C1/T4-shadows/player-{d3d11,d3d12}/T4FeatureMatrix.json` |
| T5 A/B 与交叉点（D3D11/D3D12） | A/B pass（mean 0.803/0.804、max 9） | `validation/C1/T5-runtime/player-{d3d11,d3d12}/CdPerf.json` |
| CPU 分解（64 灯） | 见 §2.3 | `validation/C1/cpu/CdCpuBreakdown.json`、`cpu/d3d12/` |

### 2.3 结果

**T5 A/B（D3D11，1280×720，48 灯）**：stencil vs clustered mean **0.803/255**、max **9/255**、>8 差 **13/921600 px**；阈值 mean ≤ 2、max ≤ 24 → `passed=true`。重复运行 mean 0.817、max 9、>8 差 2 px，同样通过。GPU 交叉点仍在 16–32 灯之间：64 灯 clustered **0.347 / 0.309 ms**（两次运行）vs stencil **0.715 / 0.689 ms**；8 灯 stencil 仍略低（0.229 vs 0.248）。**GPU 时间存在约 10% 的运行间波动**（冷/热与后台负载），不作为 C1 的收益或回归依据；C1 只改 CPU 侧且未改 kernel/缓冲输入。

**CPU 分解（D3D11，64 灯，120 帧均值）**：

| slice | StencilDeferred | ClusteredDeferred |
|---|---:|---:|
| frame CPU（FrameTimingManager） | 1.730 ms | 1.237 ms |
| `ForwardLights.PreSetup`（job 调度） | 0.000 ms | 0.051 ms |
| `SetupLights` 总计 | 0.128 ms | 0.148 ms |
| ├ 灯常量上传（`SetupShaderLightConstants`） | 0.094 ms | 0.103 ms |
| ├ cluster jobs complete（含 `UniversalRenderer.Setup` 内等待） | 0 ms | 0.110 ms |
| ├ cluster 缓冲上传（两张位图 `SetData`） | 0 ms | 0.012 ms |
| └ `DeferredLights.SetupLights` | 0.010 ms | 0.007 ms |
| └└ **`PrecomputeLights`（C1 跳过项）** | **0.0035 ms** | **0.0001 ms** |
| `ResolveMixedLightingMode` | 0.013 ms | 0.019 ms |
| 其他（剔除、阴影、GBuffer/提交等） | 1.601 ms | 1.039 ms |

重复运行（同一 player、同一场景）与上表偏差 < 5%（`PrecomputeLights` 0.00348 vs 0.00349 ms；clustered `PreSetup` 0.053 vs 0.051、job 完成 0.105 vs 0.110、灯常量 0.106 vs 0.103；帧 CPU 1.278 vs 1.237 ms），分解结论稳定。

结论（用于 §0 的 Go/No-Go 闸门）：

1. **C1 的收益是确定的但很小**：64 灯下 `PrecomputeLights` 只占 0.0035 ms/帧（≈0.2% CPU），跳过它不会改变像素。它的价值在于去掉“无人消费的每帧 Temp 分配与两遍灯遍历”，并让回退规则显式化（Preview/Overlay/XR 仍构建）。
2. **列表构建的 CPU 总量**：`PreSetup` 0.051 + job 完成 0.110 + 上传 0.012 ≈ **0.173 ms/帧**，约占 1.24 ms CPU 帧的 **14%**；灯常量上传另有 0.103 ms（GPU 光表不消除这一项，除非改结构化缓冲/数据布局）。
3. **GPU 侧没有可回收的构图成本**（构图在 CPU）；clustered 光照 pass 仅 0.35 ms。因此 G1 若只以“省 CPU”为目标，上限约 0.17 ms/帧（14%）；真正有价值的是 **把构图挪到 GBuffer 之前/异步重叠**，以及 256+ 灯的扩展性。这与 §0 的闸门一致：G1 可以立项，但验收必须同时报告“CPU 下降”和“端到端帧时间不升”。
4. 64 灯并非 CPU 帧的主要来源（stencil 与 clustered 的差异 0.49 ms 主要来自逐灯 stencil pass 的提交），不要把整帧 CPU 全部归因于光列表。

**D3D12 复核**（同一 player 序列）：A/B mean **0.804/255**、max **9**、>8 差 9 px（pass）；64 灯 GPU stencil / clustered = **1.127 / 0.424 ms**；CPU 帧 1.849 / 1.161 ms；`PrecomputeLights` 0.0030 / 0.00008 ms；`PreSetup` + job 完成 + 上传 = 0 / (0.050 + 0.112 + 0.011) ms。与 D3D11 的切片占比一致。

**T4 特性矩阵**：D3D11 七项 `passed=true`（主光 cascade / screen shadow / 附加光 atlas / `_RECEIVE_SHADOWS_OFF` / cookie / layers / GTAO 直接光 AO；stencil vs clustered on 的 mean 0.020–0.974、max 3–10）；D3D12 七项同样 `passed=true`（mean 0.020–0.971、max 3–9）。阈值 mean ≤ 2、max ≤ 24。证据：`validation/C1/T4-shadows/player-d3d11/T4FeatureMatrix.json`、`player-d3d12/T4FeatureMatrix.json`（720p PNG 已本地重跑验证，未重复入库；像素参考在 `Done/11-LightCulling/validation/T4-shadows/`）。


---

## 3. G1：GPU 光表（布局不变）+ 异步两段式

### 3.1 数据流

```
CullingResults.visibleLights（CPU，D-G2）
  ↓ 上传紧凑灯记录（GraphicsBuffer，一次/帧；与常量灯数组同一份数据）
Stage A：逐灯粗筛（compute，1 thread/light）
  位置/range（spot 用锥体包围球）→ 世界 z 区间 → zbin 区间；
  世界包围球 → 屏幕 tile AABB（复用 CPU 的视图平面/缩放参数）
  ↓ 每灯一条记录（zbin start/count、tile min/max）
Stage B：tile/zbin 展开（compute）
  与现有 CPU 算法同语义：zbin scatter + 透视/正交分支 + tile range expansion
  ↓ 写 URP_ZBinBuffer / urp_TileBuffer（uint words，布局不变）
  ↓ 写 _FPParams0/1/2（值仍由 CPU 计算，保持 tile 宽度选择与模板一致）
ClusteredDeferred.compute（不改一行）
```

- Stage A 只依赖相机与灯，**不依赖 GBuffer**，因此可放在 GBuffer 之前；同步模式下顺序执行为 `PreSetup → GBuffer → Stage A/B → ClusteredDeferred`，异步模式下 Stage A/B 与 GBuffer 并行（D-G3）。
- Stage B 的原子/合并策略必须与 CPU 的位图语义一致：同一 light 对同一 word 的重复写入是幂等的（置位），不依赖写入顺序。
- `_FPParams*` 继续由 CPU 在 `ForwardLights.Setup` 设置：tile 宽度、zbin scale/offset、方向光数量与灯数都沿用现有推导，GPU 只负责填充两张位图。

### 3.2 退出条件

1. 同场景/相机/灯集下，GPU 与 CPU jobs 的 `URP_ZBinBuffer`/`urp_TileBuffer` **逐位相等**；若 z 量化存在浮点差，必须证明它不改变任何置位（parity 测试按位比较，不做容差）。
2. `_FPParams*` 与 tile 宽度（8/16/32/64）不变；`ClusteredDeferred.hlsl` 与 kernel 无 diff。
3. T4 七项特性 + T5 A/B 全部 `passed=true`，阈值沿用 mean ≤ 2/255、max ≤ 24/255。
4. CPU 侧 `PreSetup` 归零（或只剩参数计算）；GPU 新增两次 dispatch 的时间被记录，且重叠模式下端到端帧时间不升。
5. 1 个 D3D12 或 D3D11 `.rdc`（≤20 MiB）证明 dispatch 次序/尺寸与缓冲绑定；异步路径另有 fence 证据。

### 3.3 证据

`validation/G1/`：`G1BufferParity.json`（逐位）、`T4-shadows/`、`T5-runtime/`、`CdCpuBreakdown.json`、`.rdc` + RenderDoc 摘要、EditMode/PlayMode XML。

---

## 4. G2：FPTL 精剪

- 构建时读 tile 深度 min/max（复用 `_CameraDepthTexture`，或在 D-G3 复核后新增 prepass），把与 tile 深度区间不相交的 zbin 位清掉；可选每 tile 灯数上限与按类别（点/聚光/方向/探针）排序。
- **输出必须仍是 kernel 能读的布局**：优先只清位（位图语义不变）；若改紧凑列表，必须在同一提交里改 kernel + HLSL + 契约文档，并重取全部 A/B 基线。
- 退出条件：同一场景灯数下平均每像素灯循环次数下降且 A/B 不变；`_FPParams*` 语义与 tile 宽度不变；T4/T5 全绿。
- 证据：每像素灯数分布（屏幕空间热图 + 直方图）、GPU 时间、A/B。

## 5. G3：variant / 间接派发

- 空 tile 跳过：由构建阶段产出非空 tile 计数/列表，`DispatchComputeIndirect`；或按 tile mask 打包。
- 分类/feature flags：按灯类型或材质需求裁剪每个 tile 的循环；需要 kernel variant 与 stripping 预算同步。
- 退出条件：空 tile 比例高的场景 GPU 时间下降；变体数变化有记录且不放宽 stripping；T4/T5 全绿。
- 证据：`G3VariantCount.json`、间接 dispatch 参数、`.rdc` 的 dispatch 实际覆盖、A/B。

---

## 6. 验证矩阵

沿用 11 轮的测试资产（`Assets/Tests/ClusteredDeferred`），新增一项 buffer parity：

| 测试 | 类型 | 覆盖 |
|---|---|---|
| `ClusteredDeferredGpuListParityTests` | EditMode + player | CPU jobs vs GPU 两段式，`URP_ZBinBuffer`/`urp_TileBuffer` 逐位比较；tile 8/16/32/64；正交/透视；0/1/32/64/256 灯 |
| T4 特性矩阵 | player | 主光 cascade / screen shadow / 附加光 atlas / `_RECEIVE_SHADOWS_OFF` / cookie / layers / GTAO 直接光 AO |
| T5 A/B + 交叉点 | player | stencil vs clustered，0/8/16/32/48/64 灯；A/B 阈值 mean ≤ 2/255、max ≤ 24/255 |
| CPU 分解 | player | `--cd-cpu`：PreSetup 归零、Stage A/B 时间、其他 slice 不回归 |
| 异步 / 同步一致性 | player | 两模式缓冲逐位相等；帧序证据 |
| EditMode/PlayMode 全量 | editor | 既有 485/36 基线不下降 |

**能力边界（本轮仍不覆盖）**：透明/forward-only 灯路径（D-G4）、XR/foveated、GLES、MSAA、native render pass、`useStructuredBuffer=true`、GPU 灯注册表（C8）、mixed lighting-shadowmask 的 baked A/B（11 轮遗留 PARTIAL）。

---

## 7. 风险与回滚

| 风险 | 缓解 |
|---|---|
| GPU 构图在 64 灯下不划算（dispatch/带宽 > CPU jobs） | C1 profile 先量化；G1 只在占比足够或异步有净收益时启动；保留 CPU 路径一个 tag，逐场景数据决定默认值。 |
| 位图与 CPU 输出出现 off-by-one | 先做 parity 测试再谈性能；`_FPParams*` 与 tile 宽度仍由 CPU 计算，减少变量。 |
| 异步 compute 在旧驱动/部分 API 不可用 | 平台 gate + 同队列回退；回退路径必须同样逐位一致。 |
| Stage B 原子写竞争导致非确定 | 只做置位（幂等），禁止依赖顺序的计数；parity 测试对比随机灯序。 |
| 改 kernel（G2/G3）破坏 11 轮的 A/B 基线 | 每步单独提交；布局一变就重建 golden/A-B 基线并在 KnownDifferences 标明 SUPERSEDED。 |
| 透明灯路径被误删 | D-G4 冻结：本轮不碰 `_AdditionalLightsIndices`；删除必须等 C2。 |
| 异步与 GBuffer 的深度/附件依赖 | G1 的 Stage A/B 不读 GBuffer；G2 若读深度，先复核 D-G3。 |

回滚：每步独立 commit；G1 保留 CPU 路径开关（`ForwardLights` 内部 bool，默认新路径），失败可逐帧/逐资产回退到 CPU jobs，不改 shader。

---

## 8. 工作量与下一步

| 步骤 | 粗估 |
|---|---|
| G1 两段式 + parity + 异步门控 | 3–5 天 |
| G2 FPTL 细剪 | 3–5 天 |
| G3 indirect/variant | 2–4 天 |
| 回归/证据（T4/T5、双 API、RDC） | 2–3 天 |

**立即下一步**：

1. 用 C1 的 `CdCpuBreakdown.json` 填 §2.3，并对照 §0 的 Go/No-Go 闸门决定 G1 是否以“省 CPU”立项。
2. 冻结 G1 的灯记录布局（与 `_AdditionalLights*` 常量数组同源，避免两份数据漂移）。
3. 先写 parity 测试骨架（CPU 输出落盘 → GPU 输出比较），再写 compute；不先调性能。
