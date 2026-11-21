# 12-GpuLightList：GPU 光表与异步两段式（计划）

> 状态：**计划已冻结（D-G1–D-G4）；C1 已实施并验收；G1a/G1b/G1c 已落地（G1 完成：GPU 光表、异步调度与 parity、结构化绑定 .rdc；异步实测净亏，默认同步）；G2 已落地并验收为 NO-GO（细剪默认关闭，保留 A/B）；G3 已落地并验收为 NO-GO（按 (tile, zbin) 的紧凑列表默认关闭，保留 A/B）**。
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

> **G1b 修订（绑定方式）**：上面“kernel 不改一行”在 D3D11 上不可实现 —— constant buffer 不能同时带 `UNORDERED_ACCESS`，`Graphics.CopyBuffer` 也不允许 constant↔structured。经确认后改为：clustered 路径的两张位图以 `StructuredBuffer<uint>` 读写（word 布局、header、索引公式不变），kernel 只改 3 处读取点（`CdSelect4` 换直接下标）；forward+ 的 `Input.hlsl` 常量绑定与 CPU jobs 保持原样。

### 3.2 退出条件

1. 同场景/相机/灯集下，GPU 与 CPU jobs 的 `URP_ZBinBuffer`/`urp_TileBuffer` **逐位相等**；若 z 量化存在浮点差，必须证明它不改变任何置位（parity 测试按位比较，不做容差）。
2. `_FPParams*` 与 tile 宽度（8/16/32/64）不变；`ClusteredDeferred.hlsl` 与 kernel 无 diff。
3. T4 七项特性 + T5 A/B 全部 `passed=true`，阈值沿用 mean ≤ 2/255、max ≤ 24/255。
4. CPU 侧 `PreSetup` 归零（或只剩参数计算）；GPU 新增两次 dispatch 的时间被记录，且重叠模式下端到端帧时间不升。
5. 1 个 D3D12 或 D3D11 `.rdc`（≤20 MiB）证明 dispatch 次序/尺寸与缓冲绑定；异步路径另有 fence 证据。

> **G1b 实测修订**：第 1 条按缓冲类型拆分执行 —— zbin 逐位相等（已达成）；tile 因 Stage A 按 §3.1 使用保守 AABB，闸门改为 `CoversCpuBits`（CPU ⊆ GPU、无漏位）+ 像素逐位一致。第 2 条因绑定方式修订（§3.1 注）改为“word 布局与 `_FPParams*` 不变、kernel 只允许读取点变化”。第 5 条的 `.rdc` 与第 4 条的异步重叠由 G1c 补齐（§3.6）。

> **G1c 修订（tile AABB）**：G1b 的 `CdCornerSign` 用 `uint` 运算再隐式转 float（`0u - 1u` 得到 `+4.29e9` 而不是 `-1`），8 个角点全部被推到远平面之外，`cornerVS.z < near` 对每个 item 都成立，tile 位图在所有场景中**饱和为整屏**。zbin 不受影响（灯分支只用中心 z），探针的 z 区间会用角点、因此同样有错（测试场景无探针，G1b 未暴露）。G1c 改为 float 符号函数后，arena 64 灯只有 22 个 range 真的因包围盒穿过近平面而整屏，其余是局部范围；tile 置位从“整屏饱和”降到 161,974 bit（CPU 70,624 的 229%），CPU ⊆ GPU 仍成立。

### 3.3 证据

`validation/G1/`：`tests/G1cEditMode.xml`、`tests/G1cPlayMode.xml`（含 GPU parity 用例）、`T5-runtime/player-{d3d11,d3d12,d3d12-async,d3d12-sync}/`、`T4-shadows/player-{d3d11,d3d12,d3d12-sync,d3d12-async}/`、`parity/player-d3d12/G1BufferParity.json`、`cpu/{d3d11,d3d12,d3d12-async}/`、`rdc/cluster-list-d3d12_frame203.rdc` 与 `rdc/Report.json`、本目录 `README.md`。G1b 的 `cpu/CdCpuBreakdown.json` 保留为同步 CPU jobs 的历史基线。

### 3.4 G1a（已落地）：灯记录布局冻结 + parity 骨架

**为什么先冻结**：G1 的验收是“GPU 与 CPU jobs 逐位相等”。如果灯记录和比较规则后写，Stage B 的每个浮点分支都会变成一次语义重构；先把 GPU 要读的输入、位图比较规则、以及“已用前缀”的边界固定下来，compute 才只是实现问题。

**`ClusterItemRecord`（48 字节，`[StructLayout(Sequential)]`）**：`Runtime/ClusteredDeferred/ClusterItemRecord.cs`

| offset | 字段 | 类型 | 灯 | 反射探针 |
|---:|---|---|---|---|
| 0 | `positionRange` | float4 | `localToWorld` c3，w = range | `bounds.center`，w = 0 |
| 16 | `directionExtent` | float4 | `localToWorld` c2，w = spotAngle（度，point 为 0） | `bounds.extents`，w = 0 |
| 32 | `itemType` | uint | 0 = Light | 1 = Probe |
| 36 | `lightType` | int | 原始 `LightType`；Directional 不进流 | `Point`（占位） |
| 40 | `layerMask` | uint | `RenderingLayerUtils.ToValidRenderingLayers` | 0 |
| 44 | `sourceIndex` | uint | `visibleLights` 下标 | `visibleReflectionProbes` 下标 |

- **顺序**：跳过前导方向光后的 `visibleLights`，随后是 `PreSetup` 已按 importance/size 排好序、且落在 `maxVisibleReflectionProbes` 内的探针。与 `itemsPerTile` 的 item 下标空间一一对应。
- **存原始输入而非 `_AdditionalLights*` 打包值**：CPU job 从 `spotAngle` 现算 `cos(radians(θ/2))`、从 `range` 现算 `sqrt(range² - coneHeight²)`；GPU 必须从同一原始值出发才可能逐位相等。反向从 `attenuation.x` 反解 range 是损失性的，禁止。
- 唯一构造点 `ClusterItemStream.Build`（与 `PreSetup` 共用 `m_LightOffset`，不再重算方向光前缀）；只在 parity 捕获开启时运行。
- 未进记录：cookie/阴影索引、`innerSpotAngle`、颜色。GPU 需要时从同一 `VisibleLight` 追加，且必须同步本表、HLSL 镜像与布局测试。

**parity 协议**：`Runtime/ClusteredDeferred/ClusterListParity.cs`

- 快照 = 两张位图 + 12 个 `_FPParams*` + `wordsPerTile/binCount/tileWidth/tile 分辨率/lightCount/directionalLightCount/viewCount` + item 流。二进制带 `CDLP` 魔数与 `FormatVersion`，用于落盘 golden 与跨进程（player）比较。
- 比较顺序：先比参数（float **按位**，NaN/-0 不豁免），再逐 word 比已用前缀：
  - zbin：`binCount * (2 + wordsPerTile) * viewCount`（`ZBinningJob.headerLength = 2`）；
  - tile：`tileW * tileH * wordsPerTile * viewCount`。
- 前缀内任何 word 不等即 `Exact = false`，报告不同 word 数、XOR bit 数、前 8 个 word 下标；前缀外的差异只计数（kernel 不能索引），不判失败。
- 捕获开关 `ClusterListParityCapture.enabled` 默认关闭；`ForwardLights.Setup` 里只有一次 bool 检查，开启时在 `m_CullingHandle.Complete()` 与上传之后复制缓冲，取到的正是本帧发布的 `URP_ZBinBuffer`/`urp_TileBuffer`。

**本步验证**（证据 `validation/G1/`）：

| 项 | 结果 |
|---|---|
| EditMode 全量 | **490/490**（485 基线 + 5 个新 parity 用例） |
| PlayMode 全量 | **37/37**（36 基线 + 1 个渲染捕捉用例） |
| 新用例 | 布局 size/offset 冻结；快照落盘往返；植入 1 bit 差异能被定位到 word；前缀外差异不判失败；参数 1 ulp 漂移判失败；真实 clustered 相机连续两帧位图逐位相等且 item 流与 `_FPParams*` 自洽 |
| 未证明 | GPU 逐位 parity（Stage A/B 未写）、player/arena 运行、CPU 下降与帧时间 |

---

### 3.5 G1b（已实施）：Stage A/B + 同队列派发

**实现**

- `Shaders/Utils/ClusterBuilder.compute` 五个 kernel：
  - `ClusterItemBounds`（1 thread/item）：复刻 `LightMinMaxZJob` / `ReflectionProbeMinMaxZJob` 的 z 区间（spot 分支逐行移植，仅灯夹到 0），并算保守 tile AABB —— 世界包围盒 8 角点投影，任一角点越过近平面则整屏。
  - `ClusterZBinClear`（整缓冲清零含 header 临时槽）、`ClusterZBinScatter`（bin×item，`InterlockedOr` 置位，`InterlockedMin/Max` 维护灯/探针两个 header）、`ClusterZBinPack`（写回 `min | max<<16` 两个字）、`ClusterTileExpand`（row×tile 独占线程，无原子）。
- `ClusterListGpuBuilder` 持有 item / minMaxZ / tileRanges / binHeader 临时缓冲；`ForwardLights.PreSetup` 在 GPU 路径跳过四个 CPU job，`Setup` 按 `_FPParams* → 5 dispatch → bind` 提交。item 上传每帧一次 `SetData`（≤320×48 B）。
- 回退：`ClusterListGpuBuilder.enabled`（默认 true）+ compute 缺失 / `viewCount != 1` / forward+ 时走 CPU jobs；回退同样写 structured 缓冲，像素路径不变。
- clustered 模式两张位图为 `Target.Structured`（4 B/word）；forward+ 仍为 constant buffer，CPU jobs 原样。
- parity 读回用 `CommandBuffer.RequestAsyncReadback`（与 dispatch 同队列排序），避免读到上一帧。

**验证（D3D11 player，1280×720，RTX 5060 Ti）**

| 项 | 结果 |
|---|---|
| zbin 位图 | **逐位相等**（720p/48 灯场景 0 word / 0 bit 差异） |
| tile 位图 | CPU ⊆ GPU；640×360/24 灯：CPU 75,590 bit → GPU 86,400 bit（**114.3%**，多 10,810 bit），无漏位 |
| 像素 | GPU 列表帧与 CPU 列表帧 **逐位相同**（max diff 0） |
| T5 A/B（48 灯） | mean **0.7989/255**、max **9**、>8 差 11 px，`passed=true` |
| T4 特性矩阵 | **7/7 passed**（stencil vs clustered on：mean 0.020–0.969、max 3–10） |
| D3D12 T5 A/B | mean **0.8256/255**、max **9**、>8 差 11 px，`passed=true`；64 灯 stencil 2.011/1.217 ms、clustered **1.133/0.549 ms**（CPU/GPU） |
| CPU 分解（64 灯） | `preSetup` 0.051→**0.015**、jobs complete 0.110→**0.000**、上传 0.012→**0.000**、`setupLights` 0.148→**0.232**（新增 item 构建 + 5 次 dispatch 提交）；净省约 **0.09 ms/帧** |
| EditMode / PlayMode | **490/490**、**38/38**（新增 GPU parity 用例） |

**结论**

- 逐位 parity 的边界：zbin 只做 `log2` 量化，GPU 与 Burst 输出一致；tile 若逐行移植 `TilingJob` 也不可能逐位（`FloatMode.Fast` 与 HLSL 的三角/开方差 1 ulp 就翻位），因此采用 §3.1 本就写明的“包围球 → tile AABB”保守超集，闸门为 `CoversCpuBits` + 像素逐位一致。
- **上表 tile 的两个数字事后看是饱和值**：PlayMode rig 的 24 盏灯距相机 4–7、range 5，立方体全部穿过近平面，本来就该整屏；同时 `CdCornerSign` 的 uint→float bug（§3.2 注）让 arena 这类“并非全部近平面”的场景也整屏。G1b 的 114.3% 因此只证明了 CPU ⊆ GPU，没有验证 AABB 的收紧程度。G1c 修复后以 arena 64 灯（161,974 / 70,624 = 229%）作为新的密度参考。
- CPU 收益约 0.09 ms/帧：0.173 ms 的构图换成约 0.084 ms（item 构建 + SetData + dispatch 提交）。G1c 把提交降到 ~0.06 ms（§3.6），异步重叠在本机不划算。

---

### 3.6 G1c（已实施）：异步调度、tile AABB 修复与提交开销

**实现**

- 调度：`ClusterListGpuBuilder.asyncCompute`（默认 **false**）+ `SystemInfo.supportsAsyncCompute` 决定是否走异步。异步时 item 上传（`cmd.SetBufferData`）与 5 次 dispatch 一起录进 `CD Cluster List (Async)`，标记 `CommandBufferExecutionFlags.AsyncCompute`（`Clear()` 会清掉 flag，必须每帧重设），`context.ExecuteCommandBufferAsync(..., ComputeQueueType.Background)` 提交，末尾 `CreateGraphicsFence(AsyncQueueSynchronisation, ComputeProcessing)`。
- 同步点在 `ClusteredDeferredPass` 的**光照 dispatch 之前**（不是 pass 开头），`WaitOnAsyncGraphicsFence(..., ComputeProcessing)` 只约束 compute，两次 mask draw 可与异步构建的尾部重叠；同步队列上 `WaitForClusterList` 是空操作。
- 异步命令缓冲自包含（上传、参数、dispatch 同队列），因此没有“graphics 上传 → async 等待”的跨队列 fence，也不需要 Setup 中途 flush。
- 提交开销：item 数组跨帧复用；parity 关闭时不再逐灯 `GetUniversalAdditionalLightData` 取 layerMask（kernel 不读该字段）；GPU 路径不再 MemClear 两张 CPU NativeArray；上传由 `GraphicsBuffer.SetData` 改为 `cmd.SetBufferData`。
- 顺带修复：`CdCornerSign`（§3.2 注）；构建脚本在 `SetGraphicsAPIs` 前补 `SetUseDefaultGraphicsAPIs(false)`（否则 D3D12 player 会静默跑在 D3D11）；异步 flag 每帧重设（否则 Development Console 弹出错误浮层，污染 T4 截图）。

**验证（1280×720，RTX 5060 Ti；D3D11 同步 / D3D12 同步+异步）**

| 项 | 结果 | 证据 |
|---|---|---|
| async vs sync 位图 | **逐位相同**（zbin 0 word / 0 bit；tile 0 word / 0 bit） | `parity/player-d3d12/G1BufferParity.json` |
| CPU ⊆ GPU | zbin 逐位（0 缺 / 0 多）；tile 0 漏位、多 91,350 bit；GPU 161,974 vs CPU 70,624（**229%**），22/64 item 整屏 | 同上 |
| T5 A/B D3D11（48 灯） | mean **0.8119**、max **9**、>8 差 7 px，pass | `T5-runtime/player-d3d11/CdPerf.json` |
| T5 A/B D3D12 默认同步（48 灯） | mean **0.8156**、max **9**、>8 差 5 px，pass | `T5-runtime/player-d3d12/CdPerf.json` |
| T5 A/B D3D12 异步（48 灯） | mean **0.8281**、max **9**、>8 差 3 px，pass | `T5-runtime/player-d3d12-async/CdPerf.json` |
| T4 特性矩阵 D3D11 | **7/7** | `T4-shadows/player-d3d11/T4FeatureMatrix.json` |
| T4 特性矩阵 D3D12 同步 / 异步 | **7/7** / **7/7** | `T4-shadows/player-d3d12{,-sync}/`、`player-d3d12-async/` |
| 64 灯 GPU 时间 | D3D11 stencil 0.634 / clustered **0.362 ms**；D3D12 同步 stencil 1.106 / clustered **0.449 ms**；D3D12 异步 clustered **0.582 ms**（同步/异步取自同一轮 player） | 对应 `CdPerf.json` |
| CPU 分解（64 灯） | D3D11 `preSetup` 0.008 + `setupLights` 0.137（灯常量 0.079 + deferred 0.006）→ 列表 ≈ **0.061 ms**；D3D12 同步 ≈ **0.064 ms**；D3D12 异步 ≈ **0.079 ms** | `cpu/d3d11/`、`cpu/d3d12/`、`cpu/d3d12-async/` |
| EditMode / PlayMode | **490/490**、**38/38** | `tests/G1cEditMode.xml`、`tests/G1cPlayMode.xml` |
| `.rdc` | D3D12 frame 203（**14.4 MB**）：异步命令列表含上传 + 5 次 dispatch，GBuffer 之后光照 dispatch 经 fence 读结构化 SRV；无校验消息 | `rdc/cluster-list-d3d12_frame203.rdc`、`rdc/Report.json` |

**结论**

- **异步是正确的但在这台 GPU 上是净亏**：同一 player、同一场景，强制 async 对 sync 的 clustered GPU frame time 在 0/8/16/32/48/64 灯全档都高约 **0.11–0.13 ms**（64 灯 0.582 vs 0.449；0 灯 0.341 vs 0.249），CPU 也多约 0.02 ms（fence + 提交）。整张列表本身只有 ~0.05 ms，隐藏不了队列交接成本。因此 `asyncCompute` 默认关闭，保留为可选开关和 parity 证据；256+ 灯若让列表变重，应重新测这个开关。
- **提交开销**：64 灯列表的 CPU 从 G1b 的 0.173 ms（CPU jobs）/ 0.084 ms（GPU 提交）降到 **~0.061 ms**（D3D11）与 **~0.064 ms**（D3D12 同步），构成是 item 构建 + 5 次 dispatch + 一次上传。
- **tile AABB 修复带来的真实收益**：arena 的 tile 位图从“每个 item 整屏”降到 229% CPU，D3D11 64 灯 clustered GPU 0.524→0.362 ms（同机跨轮，GPU 时间有 ~10% 波动，只作趋势）；D3D11 T5 的 64 灯聚簇路径也随之下探到 0.36 ms。
- 仍不覆盖：透明/forward-only、XR、GLES、MSAA、`useStructuredBuffer=true`、GPU 灯注册表；异步收益只在 D3D12 单卡 720p 上量化过。

## 4. G2：FPTL 精剪（已实施，验收 NO-GO）

- 构建时读 tile 深度 min/max（复用 `_CameraDepthTexture`，或在 D-G3 复核后新增 prepass），把与 tile 深度区间不相交的 zbin 位清掉；可选每 tile 灯数上限与按类别（点/聚光/方向/探针）排序。
- **输出必须仍是 kernel 能读的布局**：优先只清位（位图语义不变）；若改紧凑列表，必须在同一提交里改 kernel + HLSL + 契约文档，并重取全部 A/B 基线。
- 退出条件：同一场景灯数下平均每像素灯循环次数下降且 A/B 不变；`_FPParams*` 语义与 tile 宽度不变；T4/T5 全绿。
- 证据：每像素灯数分布（屏幕空间热图 + 直方图）、GPU 时间、A/B。

### 4.1 实现（只清位，不改布局）

- kernel：`ClusterBuilder.compute` 的 `ClusterTilePrune`，每个 tile 一个 8×8 group。先由 group 协作把 `_CdClusterDepthTexture`（与光照 kernel 加载的同一份深度拷贝）归约为该 tile 的线性视空间 z 区间（`_CdClusterZBufferParams` 线性化，与像素 kernel 的 zbin 深度空间一致），再由 thread 0 遍历该 tile 的 item 位：
  - tile 无几何（天空/空 tile）→ 清空该 tile 全部位；
  - item 的 z 区间与 tile 的 `[minZ,maxZ]`（±1e-3·max(1,zFar) padding）不相交 → 清位；
  - `pruneSidePlanes` 时再做包围球 vs tile 视锥切片（x/z、y/z 边界由 `_CdClusterViewToViewportScaleBias` 反推；穿近平面的球保留）。
- 只原地改写 `urp_Tiles`；`URP_ZBins`、word 布局、`_FPParams*`、tile 宽度全部不变。
- 宿主：`ClusterListGpuBuilder.Prune` 在 `ClusteredDeferredPass` 的 `WaitForClusterList` 之后、光照 dispatch 之前派发一次；`ForwardLights.PruneClusterList` 在 `pruneByDepth=false`（默认）时直接返回。
- 开关：`pruneByDepth`（默认 false）、`pruneSidePlanes`（默认 true）、`pruneStatsEnabled`（回读 kept/cleared）；arena 侧 `--cd-prune-on/--cd-prune-off/--cd-prune-zonly/--cd-prune-stats`。
- pass 内用本地 `ComputeZBufferParams`（与全局 `_ZBufferParams` 同式）；核心 runtime 源码不允许引用 ScreenSpaceLighting 插件工具（`PluginIsolationTests`）。
- 迭代中修掉的问题：用 `camera.projectionMatrix` 反推的世界空间深度与像素 kernel 的 `_ZBufferParams` 深度空间不一致，48 灯时过剪 84 px；改为直接线性化深度拷贝后 A/B 通过。逐 tile 深度/清位回读属于诊断脚手架，已删除。

### 4.2 验证（1280×720，RTX 5060 Ti；同一 D3D11 player 跑 on/off）

| 项 | 结果 | 证据 |
|---|---|---|
| 清位比例（D3D11，48 灯） | 保守位 135,270 → kept 46,763 / cleared 88,507（**65.4%** 清掉）；64 灯 105,287/161,974（65.0%）、32 灯 63.3%、16 灯 61.4%、8 灯 60.7% | `T5-runtime/player-d3d11-g2-prune/CdPerf.json` |
| 深度 vs 侧平面 | 仅深度（`--cd-prune-zonly`，64 灯）kept 65,890 / cleared 96,084（59.3%），侧平面只多剪 ~6–9% 的位 | `T5-runtime/player-d3d11-g2-zonly/CdPerf.json` |
| GPU 时间（D3D11，同 exe） | 0 灯 **0.230 vs 0.157 ms**（细剪 dispatch 固定成本 ≈ **+0.07 ms**，全屏深度扫描）；48 灯 0.390 vs 0.299（+0.091）；64 灯 0.438 vs 0.343（+0.095）；光照时间没有下降 | 同目录 `CdPerf.json` |
| A/B（48 灯） | prune on：mean **0.796/255**、max **9**、>8 差 8 px；off：mean 0.795、max 9、6 px；两者 `passed=true` | 同上 |
| 像素 prune on vs off | **逐位一致**（640×360 rig，24 灯：kept 32,182 / cleared 54,218，diff max 0） | `tests/G2PlayMode.xml`（`DepthPruneKeepsPixelsIdentical`）、`logs/G2PlayMode.log` |
| T5 D3D12 默认路径（prune off） | A/B mean 0.790、max 9、>8 差 1 px，pass；64 灯 clustered **0.444 ms**（G1 同机 0.449） | `T5-runtime/player-d3d12-g2-default/CdPerf.json` |
| EditMode / PlayMode | **490/490**、**39/39**（新增细剪用例） | `tests/G2EditMode.xml`、`tests/G2PlayMode.xml` |

**结论**

- 退出条件未达成：A/B 不变，但**每像素灯循环次数与 GPU 时间都没有下降**，细剪多付 ~0.07–0.10 ms。
- 机制：按 tile 深度区间清掉的位，像素 kernel 本来就会被自己的 zbin 拒掉——tile 的 `[minZ,maxZ]` 覆盖了 tile 内所有像素的深度，item 的 z 区间与它不相交时，tile 内任何像素的 zbin 都不会包含该 item；空 tile 则根本没有 deferred 像素。侧平面判定能多清 6–9% 的位（保守 AABB 的角落错配），但量太小，抵不过一次全屏深度扫描。
- **决策**：G2 判 NO-GO。实现保留为 A/B 路径（开关默认关），因为测量可复现且是 G3 的前置数据；真正能把“清掉的位”变成收益的是 G3 的按 (tile, zbin) 紧凑列表（位图里省下的位 = 列表长度的缩短），届时用这份 tile 深度区间直接生成列表即可。
- **G3 复核（§5.3）**：这个预期在 ≤64 灯下也被证伪 —— 紧凑列表的每个 cell 平均有 8–21 项，比 1–2 word 的位图扫描更长，且仍要付一次全屏深度扫描。

## 5. G3：按 (tile, zbin) 的紧凑列表（已实施，验收 NO-GO）

- 原目标：把 G2 清掉的位变成“列表长度”，像素 kernel 不再逐 word 求交，而是一次读取 (tile, zbin) 的紧凑 item 索引表；再用空 tile 计数做间接派发。
- **与 G2 的关系**：紧凑构建本身需要 tile 深度切片来确定 zbin 范围，所以它包含 G2 的深度/侧平面裁剪；`pruneByDepth` 与 `compactLists` 同时打开时，G2 的独立细剪 dispatch 会被跳过，避免两次全屏深度扫描。

### 5.1 实现

- kernel：`ClusterBuilder.compute` 新增 `ClusterCellBuild`，每个 tile 一个 8×8 group：
  - 用与光照 kernel 同一份深度拷贝归约 tile 的线性 z 区间，按 `_FPParams0` 的 zbin 映射得到 `[binLo,binHi]`，两端各放宽 1 bin（像素 kernel 的 zbin 由另一条浮点路径算出，留一个 bin 的余量）；
  - 每 tile 固定 `_CdClusterCellMax=32` 个 cell 槽；深度切片跨度超过 32 个 zbin 的 tile 置 overflow 位；
  - 每个 cell（tile, zbin）做 `urp_Tiles & URP_ZBins` 的同一 word 交集，按 item 升序写入索引；可选与 G2 相同的“包围球 vs tile 视锥切片”测试；超过 32 项的 cell 置 overflow；
  - 只写新缓冲，不改 `URP_ZBins`/`urp_Tiles`/`_FPParams*`/tile 宽度。
- 消费端：`ClusteredDeferred.hlsl` 的 `CdClusterInit/Next` 增加 compact 模式。cell 位于 tile 深度切片内且未 overflow 时直接遍历升序索引表（0 项的 cell 立即结束）；否则回退位图迭代器（overflow tile/cell、空 tile、CPU 回退、开关关闭）。升序保证累加顺序与位图路径相同。
- 宿主：`ClusterListGpuBuilder.BindCompact/BuildCells` 持有 cell 头/索引/tile info（1280×720、K=32 约 15 MB，只在开关打开时分配）；`ClusteredDeferredPass` 在 `WaitForClusterList` 和 G2 细剪之后、光照派发之前调用 `ForwardLights.BuildClusterCells`。
- 开关：`compactLists`（默认 **false**）；arena `--cd-compact-on/--cd-compact-off/--cd-compact-stats`；`CdPerf.json` 新增 `clusterListCompact` 与每档 `compactCells/compactEntries/compactOverflowCells/compactOverflowTiles/compactScannedCells`。
- 变体：不新增关键字/variant（一个 uniform 开关 + 始终绑定的结构化缓冲）；`ClusteredDeferred.compute` 的 multi_compile 组合数与 G2 相同。

### 5.2 验证（D3D11，1280×720，同一 player；RTX 5060 Ti）

| 项 | 结果 | 证据 |
|---|---|---|
| 像素等价（PlayMode） | compact on/off **逐位一致**（640×360、24 灯；非空 cell 21,609、索引 448,056、overflow cell 0） | `tests/G3PlayMode.xml`（`CompactListsKeepPixelsIdentical`）、`logs/G3PlayMode.log` |
| 像素等价（arena 48 灯） | on/off 最大差 **1/255、2 px**；同配置重跑的控制也是 **1/255、1 px**（进程间噪声同量级） | `T5-runtime/player-d3d11-g3-compact-{off,on}/`、`...-off-rerun/` |
| cell 统计（64 灯） | 21,692 非空 cell、433,570 索引（≈20/非空 cell）、108 overflow cell、264 overflow tile（3,600 tile，K=32） | `T5-runtime/player-d3d11-g3-compact-on/CdPerf.json` |
| GPU 时间（clustered） | off **0.171/0.215/0.235/0.289/0.328/0.375 ms**；on **0.258/0.301/0.355/0.421/0.407/0.458 ms**（0/8/16/32/48/64 灯）；每档 **+0.079…+0.132 ms** | `T5-runtime/player-d3d11-g3-compact-{off,on}/CdPerf.json` |
| G2 对照（同 exe） | prune on **0.249/0.321/0.399/0.512/0.425/0.471 ms**，同样只增不减 | `T5-runtime/player-d3d11-g3-prune-control/CdPerf.json` |
| A/B（48 灯） | off mean **0.806** / max **9**；on **0.820** / **9**；prune **0.818** / **9**，全部 `passed=true` | 三个 `CdPerf.json` 的 `diffs` |
| T4 特性矩阵（D3D11） | **7/7 passed**（stencil vs clustered on：mean 0.020–0.961、max 3–10） | `T4-shadows/player-d3d11/T4FeatureMatrix.json` |
| D3D12 T5（默认 / compact on） | 默认 0.280/0.297/0.313/0.366/0.402/0.488 ms；on 0.331/0.337/0.365/0.437/0.405/0.455 ms（48/64 落在噪声内）；A/B 均 pass | `T5-runtime/player-d3d12-g3-{default,compact-on}/CdPerf.json` |
| EditMode / PlayMode | **490/490**、**40/40** | `tests/G3EditMode.xml`、`tests/G3PlayMode.xml` |

### 5.3 结论

- **NO-GO**：紧凑列表在每个灯数档都更慢。固定成本是一次全屏深度扫描（≈0.08 ms，与 G2 同源），而光照 kernel 没有可回收的时间。
- 机制：本 workload 的 `wordsPerTile` 只有 1（≤32 灯）到 2（48/64 灯），位图迭代器每像素只扫 1–2 个 word；同一 (tile, zbin) 的紧凑列表平均 8–21 项（灯光重叠严重），列表比位图扫描更长。紧凑列表要赢，必须出现“word 数远大于每 cell 项数”的分布（≥256 灯且每 cell 稀疏），本场景没有。
- 空 tile 前提也不成立：1280×720/48–64 灯时平均每 tile ~10.2 个深度 cell（扫描 36,743 cell / 3,600 tile，全部有几何），没有可跳过的空 tile，所以没有加 `DispatchComputeIndirect`。
- 决策：`compactLists` 默认关闭，保留为 A/B 路径与扩展性基础设施（cell 布局、overflow 回退、统计都已验证）；G1/G2 的默认路径与像素不变。要重开 G3，需要 256+ 灯且每 cell 列表显著短于位图 word 扫描的场景，或把 cell 构建折进已有 pass 以消掉全屏深度扫描。
- 证据索引：[`validation/G3/README.md`](validation/G3/README.md)。

---

## 6. 验证矩阵

沿用 11 轮的测试资产（`Assets/Tests/ClusteredDeferred`），新增一项 buffer parity：

| 测试 | 类型 | 覆盖 |
|---|---|---|
| `ClusteredDeferredGpuListParityTests` | EditMode + player | CPU jobs vs GPU 两段式，`URP_ZBinBuffer`/`urp_TileBuffer` 逐位比较；tile 8/16/32/64；正交/透视；0/1/32/64/256 灯 |
| T4 特性矩阵 | player | 主光 cascade / screen shadow / 附加光 atlas / `_RECEIVE_SHADOWS_OFF` / cookie / layers / GTAO 直接光 AO |
| T5 A/B + 交叉点 | player | stencil vs clustered，0/8/16/32/48/64 灯；A/B 阈值 mean ≤ 2/255、max ≤ 24/255 |
| CPU 分解 | player | `--cd-cpu`：PreSetup 归零、Stage A/B 时间、其他 slice 不回归 |
| 异步 / 同步一致性 | player | 两模式缓冲逐位相等（G1c：`parity/player-d3d12/G1BufferParity.json`，0 word / 0 bit）；帧序证据见 `rdc/Report.json` |
| EditMode/PlayMode 全量 | editor | 既有基线不下降（G1c 490/38；G2 细剪新增 1 条 PlayMode 用例后 490/490、39/39） |

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

1. ~~用 C1 的 `CdCpuBreakdown.json` 填 §2.3，并对照 §0 的 Go/No-Go 闸门决定 G1 是否以“省 CPU”立项。~~（C1 结论：0.173 ms/帧、约 14%；G1 以“异步重叠 + 扩展性”立项，验收须同时报告 CPU 下降与端到端帧时间。）
2. ~~冻结 G1 的灯记录布局（与 `_AdditionalLights*` 常量数组同源，避免两份数据漂移）。~~（§3.4，已落地。）
3. ~~先写 parity 测试骨架（CPU 输出落盘 → GPU 输出比较），再写 compute；不先调性能。~~（§3.4，已落地；golden 落盘与比较库就绪。）
4. ~~**G1b**：写 Stage A/B compute（不改 kernel 与布局），先过 parity 再谈异步；第一版可同队列串行，异步只加调度。~~（§3.5，已落地。）
5. ~~**G1c**：异步重叠 —— `CommandBufferExecutionFlags.AsyncCompute` + fence，与 GBuffer 并行；同步/异步两种模式的位图必须逐位一致。同时在 D3D12 复跑 T5/T4，并补 1 个 `.rdc`（structured 绑定后的新结构）。~~（§3.6，已落地；异步逐位一致但在本机净亏 ~0.11–0.13 ms，默认关闭。）
6. ~~**G1 性能收尾**：item 构建与 dispatch 提交合计约 0.084 ms，先量化能否用缓存的 item 缓冲与更少的 `SetCompute*` 调用压到 0.05 ms 以下；再决定 G2 是否值得做。~~（§3.6：列表 CPU 降到 ~0.061–0.064 ms；G2 的收益需另测，见下一条。）
7. ~~**G2**：FPTL 细剪。构建时读 tile 深度 min/max，优先复用 GBuffer 后的 `_CameraDepthTexture`（D-G3），只清位不改布局；先量 tile 内灯循环次数与 GPU 时间，再谈紧凑列表。~~（§4：已落地并验收为 **NO-GO**——清 65% 的位、A/B 不变，但循环次数与 GPU 时间不降，dispatch 固定成本 ~0.07 ms；默认关，保留 A/B。）
8. ~~**G3**：在 §4.2 的测量之上做 variant / 间接派发。优先把清位换成按 (tile, zbin) 的紧凑列表（位图省下的位即列表长度），再用空 tile 计数做 `DispatchComputeIndirect`；验收前先复跑本轮的 prune on/off 作为对照。~~（§5：已落地并验收为 **NO-GO** —— 每个灯数档都多 ~0.08–0.13 ms，cell 列表（8–21 项）比 1–2 word 的位图扫描更长；空 tile 比例为 0，未做间接派发。默认关，保留 A/B。）
