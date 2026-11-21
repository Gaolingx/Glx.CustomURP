# G1a 验证记录：灯记录布局冻结 + parity 骨架

> 轮次：12-GpuLightList / G1 的第一步（HANDOFF §3.4）。
> 环境：Unity 2022.3.62f1（Editor，D3D11）、batchmode test runner。
> 范围：**只冻结 GPU 构建器要读的记录与比较规则**；不新增 compute，不改 kernel、位图布局或 `_FPParams*`。

## 1. 改动

- 新增 `Runtime/ClusteredDeferred/ClusterItemRecord.cs`：48 字节 item 记录（`positionRange` / `directionExtent` / `itemType` / `lightType` / `layerMask` / `sourceIndex`）+ `ClusterItemStream.Build`（顺序与 CPU job 的 item 下标空间一致）。
- 新增 `Runtime/ClusteredDeferred/ClusterListParity.cs`：`ClusterListSnapshot`（两张位图 + `_FPParams*` + 布局参数 + item 流，二进制 `CDLP` v1 落盘）、`ClusterListDiff`（按位比较、只统计已用前缀、报告前 8 个差异 word）、`ClusterListParityCapture`（默认关闭的捕获开关）。
- `ForwardLights`：`PreSetup` 记录 `m_LightOffset` / `m_ViewCount` / `m_ReflectionProbeCount`；`Setup` 把 `_FPParams*` 提成局部变量（值不变），开关打开时调用 `CaptureClusterList`。关闭时每帧只多一次 bool 检查。
- `AssemblyInfo.cs`：`InternalsVisibleTo("Unity.ClusteredDeferred.Tests.PlayMode")`，让 PlayMode rig 能读取捕获结果。
- 测试：包内 EditMode `ClusteredDeferredGpuListParityTests`（5 个用例）；PlayMode `ClusterListSnapshotIsDeterministicAndDescribesTheItemStream`（1 个用例）。

## 2. 命令

```bash
U="/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe"
P="E:/GitHub/Glx.CustomURP/URPSample01"
OUT="$P/PLAN/TODO/12-GpuLightList/validation/G1"

"$U" -batchmode -projectPath "$P" -runTests -testPlatform EditMode \
  -testResults "$OUT/tests/G1EditMode.xml" -logFile "$OUT/logs/G1EditMode.log"
"$U" -batchmode -projectPath "$P" -runTests -testPlatform PlayMode \
  -testResults "$OUT/tests/G1PlayMode.xml" -logFile "$OUT/logs/G1PlayMode.log"
```

## 3. 结果

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

## 4. 未证明（留给 G1b）

- GPU Stage A/B 与 CPU jobs 的逐位 parity —— compute 尚未编写，本步只保证比较器与输入契约可用。
- player/arena 运行与 `G1BufferParity.json` 产出；异步/同步两种调度的一致性。
- CPU `PreSetup` 归零、GPU dispatch 时间、端到端帧时间。本步不改变任何渲染结果，未重跑 T4/T5。

## 5. 工程噪声

batchmode 运行会把 `ProjectSettings.asset` / `GraphicsSettings.asset` / `QualitySettings.asset` 迁移到本机 Unity 的序列化版本（`runInBackground`、`serializedVersion` 等）。验证结束后已还原为基线，提交中不含这些改动。
