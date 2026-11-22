# 15-ForwardClusterLights：让透明 / forward-only 读同一张 GPU 光表（C2）

> 建立：2026-11-22；前置：[`PLAN/Done/14-TransparentLightPath`](../14-TransparentLightPath/HANDOFF.md)（探针 GO C2）、[`PLAN/Done/12-GpuLightList`](../12-GpuLightList/HANDOFF.md)（GPU 列表为默认）。
> 状态：**PASS（2026-11-22）。C2 落地，默认在 clustered deferred 下开启。** 256 灯透明 footprint 内 `ClusteredDeferred vs ForwardPlus` 的 >8 像素比例从 **87.9%** 降到 **0.18%**（探针为 89.4%）；48 灯从 15.4% 降到 0.11%。对照（无透明）A/B 与 13/14 轮一致。证据见 [validation/README.md](validation/README.md)。

## 0. 问题

第 14 轮量化了同一屏内两份灯集合的分歧：透明/forward-only 物体走 per-object 索引（每物体最多 4 盏），不透明表面走 cluster 位图。256 灯下有 89.4% 的透明像素与 Forward+（cluster 语义）通道差 >8；上限拉到 8 只降到 66.5%。

不能直接开 `_FORWARD_PLUS`：

1. Forward+ 的 `URP_ZBins`/`urp_Tiles` 是 constant buffer；clustered deferred 用 compute 构图，必须是 structured（UAV），绑定互斥（`ForwardLights.cs:187-202`）。
2. `_FORWARD_PLUS` 同时切换反射探针 atlas 与主光语义；CD 只差灯列表，不该一起换。

## 1. 冻结决策

| ID | 决策 | 值 | 理由 |
|---|---|---|---|
| **D-C2-1** | 关键字 | 新增独立 `_CLUSTERED_DEFERRED_LIGHTS` multi_compile，不与 `_FORWARD_PLUS` 共组 | 共组会让 `stripUnusedVariants`/prefiltering 把 CD 变体当作 Forward+ off 变体删掉；独立组只增加变体，不改 stripping 语义 |
| **D-C2-2** | 缓冲读取 | 复用 `URP_ZBins`/`urp_Tiles` 的 structured 绑定，wrap 成 `URPClusterWord`/`URPClusterTileWord` | 两套布局同为 `uint` word，只有取词方式不同；迭代器/header/掩码逻辑保持同一份 |
| **D-C2-3** | 开关 | 默认开；`ClusteredDeferredPass.forwardClusterLights` + `--cd-forward-cluster-off` | 同一构建内复现 per-object 路径，作为 A/B 与证据 |
| **D-C2-4** | 适用范围 | 只在 eligible CD 相机、本帧实际构图时开；stencil / forward+ / preview / overlay 一律关闭 | 回退相机没有绑定 cluster 缓冲 |
| **D-C2-5** | 变体 | Lit / SimpleLit / ComplexLit / ParticlesLit / ParticlesSimpleLit 的 ForwardLit | 透明与 forward-only 的实际覆盖面；不扩散到 terrain/其它 |
| **D-C2-6** | 清理 | 不删 `_AdditionalLightsIndices`、`maxPerObjectAdditionalLightsCount`、stencil 回退 | 回退路径仍需要；删除属于 clean.md C2 的后续条件 |

## 2. 实现

- **Shader**（`Packages/com.unity.render-pipelines.universal@14.0.12`）
  - `ShaderLibrary/Core.hlsl`：`USE_CLUSTER_LIGHTS = USE_FORWARD_PLUS || USE_CLUSTERED_DEFERRED_LIGHTS`；CD 关键字同时强制 `_ADDITIONAL_LIGHTS`。
  - `ShaderLibrary/Input.hlsl`：`_FPParams*` 移到 `USE_CLUSTER_LIGHTS`；CD 下 `URP_ZBins`/`urp_Tiles` 声明为 `StructuredBuffer<uint>`，Forward+ 仍为 cbuffer。
  - `ShaderLibrary/Clustering.hlsl`：进入条件改为 `USE_CLUSTER_LIGHTS`；新增 `URPClusterWord`/`URPClusterTileWord` 两个取词函数，迭代器逻辑不变。
  - `ShaderLibrary/RealtimeLights.hlsl`：`LIGHT_LOOP_BEGIN`、`GetAdditionalLight`、`GetAdditionalLightsCount`、subtractive 检查改用 `USE_CLUSTER_LIGHTS`（cluster 索引不再经 per-object 映射）。
  - `ShaderLibrary/Lighting.hlsl`：附加平行光的独立循环（3 处）改为 `USE_CLUSTER_LIGHTS`；cluster 列表不含前导平行光，必须单独遍历。
  - 5 个 shader 的 ForwardLit 增加 `#pragma multi_compile _ _CLUSTERED_DEFERRED_LIGHTS`。
- **Runtime**
  - `Runtime/UniversalRenderPipelineCore.cs`：`ShaderKeywordStrings.ClusteredDeferredLights`（`:1095` 附近）。
  - `Runtime/ClusteredDeferred/ClusteredDeferredPass.cs:18`：静态开关 `forwardClusterLights = true`。
  - `Runtime/ForwardLights.cs:709-710`：`clusterForwardLights = buildClusters && m_ClusteredDeferred && !m_UseForwardPlus && forwardClusterLights`；按帧 `SetKeyword`，per-vertex 灯关键字在 cluster 开启时让位给 per-pixel。
  - `Runtime/ScriptableRenderer.cs:1449`：每相机渲染前清掉该关键字，避免跨相机泄漏。
- **夹具/验证**
  - `ClusteredDeferredArenaRunner`：`--cd-forward-cluster-off/on`、报告字段 `forwardClusterLights`/`forwardClusterKeyword`。
  - PlayMode 新增 `ForwardTransparentObjectsReadTheClusterList`：同一次运行内 cluster on/off 分别与 Forward+ 比，直接量化收益。
  - EditMode 新增 `ForwardLitShadersDeclareTheClusteredDeferredVariant`：钉住 5 个 shader 的变体声明与 Clustering 的结构化取词。

## 3. 闸门

| 闸门 | 判定 | 阈值 | 依据 |
|---|---|---|---|
| **G15-1** 256 灯 footprint 内 C/F >8 | ✅ PASS | ≤1% | **0.18%**（209 / 118,630）；探针 89.4% |
| **G15-2** 48 灯 footprint 内 C/F >8 | ✅ PASS | ≤1% | **0.11%**（132 / 116,983）；探针 15.5% |
| **G15-3** 0/8 灯与基线同量级 | ✅ PASS | 同 0 灯基线 | 0.02%，与探针一致 |
| **G15-4** control A/B 不回归 | ✅ PASS | 48 灯 S/C mean ≤2、max ≤24 | 0.810 / 9（14 轮 0.806 / 9）；C/F 0.209 / 0.290 与 13/14 轮逐位同 |
| **G15-5** 开关可复现分歧 | ✅ PASS | 关闭后回到探针量级 | 48 灯 15.36%、256 灯 87.92%（探针 15.53% / 89.44%） |
| **G15-6** 成本 | ○ 记录 | — | CD 256 灯 +0.23 ms（1.277→1.510 GPU），48 灯 +0.03 ms；与 Forward+ 的逐像素 cluster 扫描同量级；收益是正确性 |
| **G15-7** 测试 | ✅ PASS | 全量 | EditMode 491/491；PlayMode 41/41（新增用例） |
| **G15-8** GPU 证据 | ✅ PASS | 透明 draw 读 structured cluster | `.rdc` EID 682：PS 声明 `t0/t1` structured(4B) 并 `ld_structured` 走 header+tile/zbin 掩码 |

## 4. 结果

透明 footprint（clustered on/off 差分 >8）随灯数 11.7%–12.9%；footprint 内 `ClusteredDeferred vs ForwardPlus` 的通道最大差：

| 灯数 | cluster on（mean / >8 / 占比） | cluster off（mean / >8 / 占比） |
|---:|---|---|
| 0 | 0.125 / 19 / 0.02% | 0.125 / 19 / 0.02% |
| 8 | 0.103 / 19 / 0.02% | 0.103 / 19 / 0.02% |
| 48 | **0.154 / 132 / 0.11%** | 1.527 / 17,971 / 15.36% |
| 256 | **0.202 / 209 / 0.18%** | 17.184 / 104,301 / 87.92% |

- 全屏 C/F（48 / 256 灯）：cluster on **0.187 / 382 px**、**0.263 / 556 px**；无透明对照 0.209 / 452 px、0.290 / 622 px。透明组带来的差值已经低于同管线在背景上的固定差。
- PlayMode 同运行 A/B（640×360、48 灯）：cluster over8 **648**、mean 0.455；per-object over8 **17,809**、mean 11.056（27×）。
- `StencilDeferred vs ClusteredDeferred` 在 cluster on 后变大（48 灯 2 px→21,321 px >8）：stencil 的透明仍走 per-object，这是**预期语义差**，不是回归；本轮语义参照统一为 Forward+。
- 变体成本：Lit ForwardLit 片元变体 3072→6144；冷构建 918 s（本轮改动了 Input/Clustering，全部 URP 变体重编），player 139.6→143.5 MB。后续构建回到缓存命中。

## 5. 证据布局

```text
PLAN/Done/15-ForwardClusterLights/validation/
├── control/            # 无透明：12 PNG + CdPerf.json/csv
├── cluster-on/         # 透明 + 默认 cluster 灯路径
├── cluster-off/        # 透明 + --cd-forward-cluster-off
├── capsrc/CdPerf.json  # 抓帧进程：forwardClusterKeyword=true、_FORWARD_PLUS=false、上限 4
├── rdc/forward-cluster-d3d11_frame305.rdc   # 12.0 MB，EID 679/682/685 透明 draw
├── rdc/ps_682.txt      # 透明 PS 反汇编（structured t0/t1 + ld_structured）
├── tests/EditMode.xml、PlayMode.xml
├── CdArenaBuildSummary-Direct3D11.json
└── README.md
```

## 6. 复跑

```bash
U="/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe"
P="E:/GitHub/Glx.CustomURP/URPSample01"
OUT="$P/PLAN/Done/15-ForwardClusterLights/validation"
EXE="$P/Builds/ClusteredDeferredArena/Direct3D11/CdArena.exe"

"$U" -batchmode -projectPath "$P" -executeMethod ClusteredDeferredArenaBuild.BuildD3D11AndExit -logFile "$OUT/logs/build-d3d11.log"
"$EXE" --cd-output "$OUT/control"     --cd-total-lights 256 --cd-light-counts 0,8,48,256 --cd-screenshot-lights 0,8,48,256
"$EXE" --cd-output "$OUT/cluster-on"  --cd-transparent --cd-total-lights 256 --cd-light-counts 0,8,48,256 --cd-screenshot-lights 0,8,48,256
"$EXE" --cd-output "$OUT/cluster-off" --cd-transparent --cd-forward-cluster-off --cd-total-lights 256 --cd-light-counts 0,8,48,256 --cd-screenshot-lights 0,8,48,256
```

footprint 统计脚本见 [validation/README.md](validation/README.md)。

## 7. 边界与回滚

- **边界**：只测 Lit 透明夹具（SimpleLit/ComplexLit/Particles 的变体已声明但无独立像素夹具）；256 灯为最坏重叠场；只测 D3D11 720p 单卡；`Forward+` 仍走 cbuffer 路径，未统一缓冲类型；Preview/Overlay/XR、GLES、`useStructuredBuffer=true` 保持 per-object 回退。
- **已知语义变化**：CD 下透明/forward-only 与 stencil 模式在透明像素上不再一致（前者对齐 cluster/Forward+，后者仍是 per-object）。这是 C2 的目的，不是 A/B 回归。
- **回滚**：`ClusteredDeferredPass.forwardClusterLights = false`（或 CLI `--cd-forward-cluster-off`）即回到 14 轮的 per-object 行为；删除关键字 pragma 与 5 处 shader guard 可完全移除该路径。
