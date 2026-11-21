# G1 验证记录：GPU 光表（G1a 布局/骨架 + G1b Stage A/B）

> 轮次：12-GpuLightList / G1（HANDOFF §3.4、§3.5）。
> 环境：Unity 2022.3.62f1（Editor + D3D11 standalone Development player）、RTX 5060 Ti、1280×720。
> 范围：只改“谁写光列表”；word 布局、header、tile 宽度与 `_FPParams*` 不变。

## G1a：灯记录布局 + parity 骨架

### A.1 改动

- 新增 `Runtime/ClusteredDeferred/ClusterItemRecord.cs`：48 字节 item 记录（`positionRange` / `directionExtent` / `itemType` / `lightType` / `layerMask` / `sourceIndex`）+ `ClusterItemStream.Build`（顺序与 CPU job 的 item 下标空间一致）。
- 新增 `Runtime/ClusteredDeferred/ClusterListParity.cs`：`ClusterListSnapshot`（两张位图 + `_FPParams*` + 布局参数 + item 流，二进制 `CDLP` v1 落盘）、`ClusterListDiff`（按位比较、只统计已用前缀、报告前 8 个差异 word）、`ClusterListParityCapture`（默认关闭的捕获开关）。
- `ForwardLights`：`PreSetup` 记录 `m_LightOffset` / `m_ViewCount` / `m_ReflectionProbeCount`；`Setup` 把 `_FPParams*` 提成局部变量（值不变），开关打开时调用 `CaptureClusterList`。关闭时每帧只多一次 bool 检查。
- `AssemblyInfo.cs`：`InternalsVisibleTo("Unity.ClusteredDeferred.Tests.PlayMode")`，让 PlayMode rig 能读取捕获结果。
- 测试：包内 EditMode `ClusteredDeferredGpuListParityTests`（5 个用例）；PlayMode `ClusterListSnapshotIsDeterministicAndDescribesTheItemStream`（1 个用例）。

### A.2 命令

```bash
U="/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe"
P="E:/GitHub/Glx.CustomURP/URPSample01"
OUT="$P/PLAN/TODO/12-GpuLightList/validation/G1"

"$U" -batchmode -projectPath "$P" -runTests -testPlatform EditMode \
  -testResults "$OUT/tests/G1EditMode.xml" -logFile "$OUT/logs/G1EditMode.log"
"$U" -batchmode -projectPath "$P" -runTests -testPlatform PlayMode \
  -testResults "$OUT/tests/G1PlayMode.xml" -logFile "$OUT/logs/G1PlayMode.log"
```

### A.3 结果

| 项 | 结果 | 证据 |
|---|---|---|
| EditMode 全量 | **490/490**（485 基线 + 5 新用例） | `tests/G1EditMode.xml` |
| PlayMode 全量 | **37/37**（36 基线 + 1 新用例） | `tests/G1PlayMode.xml` |
| 记录布局 | size 48、offset 0/16/32/36/40/44 固定 | `ClusterItemRecordLayoutIsFrozen` |
| 落盘往返 | 位图/参数/item 逐位一致 | `SnapshotRoundTripsThroughDisk` |
| 差异定位 | 植入 1 bit → 1 word / 1 bit，下标正确 | `DiffLocalizesAPlantedBitFlip` |
| 前缀边界 | 已用前缀外的 word 写入只计数、不失败 | `DiffIgnoresTheTailBeyondTheUsedRegion` |
| 参数严格性 | `tileWidth` 与 `_FPParams[0]` 1 ulp 漂移均判失败 | `DiffRejectsParameterDrift` |
| 真实相机 | 640×360、24 盏点光 + 1 方向光：连续两帧位图逐位相等；item 数 = lightCount + 探针；无方向光进流；`_FPParams*` 与快照一致 | PlayMode 用例 |

### A.4 未证明

- GPU Stage A/B 与 CPU jobs 的逐位 parity —— compute 尚未编写，本步只保证比较器与输入契约可用。
- player/arena 运行与 `G1BufferParity.json` 产出；异步/同步两种调度的一致性。
- CPU `PreSetup` 归零、GPU dispatch 时间、端到端帧时间。本步不改变任何渲染结果，未重跑 T4/T5。

### A.5 工程噪声

batchmode 运行会把 `ProjectSettings.asset` / `GraphicsSettings.asset` / `QualitySettings.asset` 迁移到本机 Unity 的序列化版本（`runInBackground`、`serializedVersion` 等）。验证结束后已还原为基线，提交中不含这些改动。

## G1b：Stage A/B（同队列）

### B.1 改动

- `Shaders/Utils/ClusterBuilder.compute`：`ClusterItemBounds`（z 区间 + 保守 tile AABB）、`ClusterZBinClear`、`ClusterZBinScatter`（原子置位 + 双 header min/max）、`ClusterZBinPack`、`ClusterTileExpand`（row×tile 独占写）。
- `Runtime/ClusteredDeferred/ClusterListGpuBuilder.cs`：临时缓冲、参数上传、5 次 dispatch；`enabled` 为回退开关。
- `ForwardLights`：GPU 路径跳过四个 CPU job；`Setup` 记录 `_FPParams*` 后 dispatch 并绑定 structured 缓冲；CPU 回退写同一 structured 缓冲；`Cleanup` 释放 builder。
- `ClusteredDeferred.hlsl`：两张位图由 constant buffer 改为 `StructuredBuffer<uint>`（3 处读取点，word 布局不变）；删除 `CdSelect4` 与 `CD_MAX_*_VEC4S`。
- `UniversalRendererData.ShaderResources.clusterBuilderCS` + `UniversalRenderer` 注入；`ClusterListParityCapture` 增加同队列 `RequestAsyncReadback`。
- 测试：PlayMode `GpuClusterListCoversTheCpuList`（CPU ⊆ GPU、zbin 逐位、像素逐位一致）。

### B.2 命令

```bash
U="/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe"
P="E:/GitHub/Glx.CustomURP/URPSample01"
OUT="$P/PLAN/TODO/12-GpuLightList/validation/G1"

"$U" -batchmode -projectPath "$P" -runTests -testPlatform EditMode -testResults "$OUT/tests/G1EditMode.xml" -logFile "$OUT/logs/G1EditMode.log"
"$U" -batchmode -projectPath "$P" -runTests -testPlatform PlayMode -testResults "$OUT/tests/G1PlayMode.xml" -logFile "$OUT/logs/G1PlayMode.log"

"$U" -batchmode -projectPath "$P" -executeMethod ClusteredDeferredArenaBuild.BuildD3D11AndExit -logFile "$OUT/logs/build-t5-d3d11.log"
"$P/Builds/ClusteredDeferredArena/Direct3D11/CdArena.exe" --cd-output "$OUT/T5-runtime/player-d3d11"
"$P/Builds/ClusteredDeferredArena/Direct3D11/CdArena.exe" --cd-output "$OUT/cpu" --cd-cpu

"$U" -batchmode -projectPath "$P" -executeMethod ClusteredDeferredArenaBuild.BuildT4D3D11AndExit -logFile "$OUT/logs/build-t4-d3d11.log"
"$P/Builds/ClusteredDeferredArenaT4/Direct3D11/CdArenaT4.exe" --cd-output "$OUT/T4-shadows/player-d3d11" --cd-t4
```

### B.3 结果

| 项 | 结果 | 证据 |
|---|---|---|
| zbin 位图 | **逐位相等**（720p/48 灯：0 word / 0 bit） | `GpuClusterListCoversTheCpuList` 日志、`tests/G1PlayMode.xml` |
| tile 位图 | CPU ⊆ GPU；640×360/24 灯 CPU 75,590 → GPU 86,400 bit（**114.3%**），0 漏位 | 同上（`Tile bits: ...` 日志行） |
| 像素 | GPU 列表帧 vs CPU 列表帧 **max diff 0** | 同上 |
| T5 A/B（48 灯） | mean **0.7989/255**、max **9**、>8 差 11 px，`passed=true` | `T5-runtime/player-d3d11/CdPerf.json` |
| T4 特性矩阵 | **7/7 passed** | `T4-shadows/player-d3d11/T4FeatureMatrix.json` |
| D3D12 T5 A/B（48 灯） | mean **0.8256/255**、max **9**、>8 差 11 px，`passed=true`；64 灯 stencil 2.011/1.217 ms、clustered 1.133/0.549 ms | `T5-runtime/player-d3d12/CdPerf.json` |
| CPU 分解（64 灯） | `preSetup` 0.051→**0.015**、jobs 0.110→**0.000**、上传 0.012→**0.000**、`setupLights` 0.148→**0.232** | `cpu/CdCpuBreakdown.json` |
| EditMode / PlayMode | **490/490**、**38/38** | `tests/G1EditMode.xml`、`tests/G1PlayMode.xml` |

T5 64 灯（D3D11）：stencil CPU 1.665 / GPU 0.765 ms；clustered CPU 1.175 / GPU 0.524 ms。交叉点仍与 11 轮一致（8 灯 stencil 略低，16–32 灯附近反转）。

### B.4 未证明

- D3D12 的 T4 特性矩阵；parity 用例只在编辑器内跑（player 内的 CPU/GPU 位图对拍尚未接入 runner）。
- 异步 compute 与 GBuffer 重叠（G1c）及其 fence 证据。
- 1 个新的 `.rdc`：绑定已从 constant 改为 structured，旧抓帧不再是当前结构参考。
- 端到端帧时间不升的正式 A/B（同一 player 内 GPU 列表 vs CPU 列表的帧时间对比）；当前只有 CPU 分解与 GPU 时间。
- CPU 侧 item 构建 + dispatch 提交约 0.084 ms，尚未优化。

### B.5 工程噪声

arena 构建会经 `ClusteredDeferredTestAssetSetup.CreateAll()` 重写场景、管线与 feature renderer 资产（fileID/序列化漂移），并让 ProjectSettings 迁移。验证后已把非语义改动还原为基线；提交只保留 renderer 资产中新增 `clusterBuilderCS` 引用的增量。T4/T5 的 720p PNG 未入库（与 C1 同口径），像素参考仍是 `Done/11-LightCulling/validation/`。
