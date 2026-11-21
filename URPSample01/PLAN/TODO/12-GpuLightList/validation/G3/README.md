# G3 证据索引（按 (tile, zbin) 的紧凑列表）

结论：**NO-GO**（详见 [HANDOFF §5.3](../../HANDOFF.md)）。紧凑列表在每个灯数档都多 ~0.08–0.13 ms；固定成本是一次全屏深度扫描，而位图迭代器在 1–2 word 下本来就很便宜。开关保留为 A/B，默认关闭。

## 最终证据（当前 HEAD 的代码）

| 内容 | 路径 |
|---|---|
| EditMode 490/490 | `tests/G3EditMode.xml`、`logs/G3EditMode.log` |
| PlayMode 40/40（含 `CompactListsKeepPixelsIdentical`） | `tests/G3PlayMode.xml`、`logs/G3PlayMode.log` |
| 编译（player） | `logs/build-t5-d3d11-g3.log` |
| D3D11 T5：compact off（基线） | `T5-runtime/player-d3d11-g3-compact-off/` |
| D3D11 T5：compact on（带 cell 统计） | `T5-runtime/player-d3d11-g3-compact-on/` |
| D3D11 T5：同配置重跑（进程间噪声对照） | `T5-runtime/player-d3d11-g3-compact-off-rerun/` |
| D3D11 T5：G2 prune on（同 exe 对照） | `T5-runtime/player-d3d11-g3-prune-control/` |
| D3D11 T4 特性矩阵 7/7 | `T4-shadows/player-d3d11/T4FeatureMatrix.json`、`logs/build-t4-d3d11-g3.log`（720p PNG 已在本地重跑验证，未重复入库；像素参考在 `validation/G1/T4-shadows/`） |
| D3D12 T5：默认路径（compact off） / compact on | `T5-runtime/player-d3d12-g3-default/`、`T5-runtime/player-d3d12-g3-compact-on/`、`logs/build-t5-d3d12-g3.log` |
| 像素差（48 灯截图） | `T5-runtime/pixel-diff.txt` |
| 变体记录（G3 不新增 keyword/variant） | `G3VariantCount.json` |

复跑开关：`--cd-compact-on` / `--cd-compact-off` / `--cd-compact-stats`（`CdPerf.json` 的 `clusterListCompact` 与每档 `compactCells/compactEntries/compactOverflowCells/compactOverflowTiles/compactScannedCells` 记录实际生效值与规模）。`compactLists` 与 `pruneByDepth` 同时打开时，G2 的独立细剪 dispatch 会被跳过（紧凑构建自带深度切片）。

## 关键数字（1280×720，D3D11，同一 player）

| 灯数 | 0 | 8 | 16 | 32 | 48 | 64 |
|---|---:|---:|---:|---:|---:|---:|
| clustered GPU，compact off (ms) | 0.171 | 0.215 | 0.235 | 0.289 | 0.328 | 0.375 |
| clustered GPU，compact on (ms) | 0.258 | 0.301 | 0.355 | 0.421 | 0.407 | 0.458 |
| Δ (ms) | +0.087 | +0.086 | +0.120 | +0.132 | +0.079 | +0.083 |

- 64 灯：21,692 个非空 cell、433,570 个索引（≈20/非空 cell）、108 overflow cell、264 overflow tile（3,600 tile，K=32）。
- 48–64 灯：36,743 个被扫描 cell / 3,600 tile（≈10.2 cell/tile，全部有几何）→ 没有空 tile 可跳过，因此没有做 `DispatchComputeIndirect`。
- A/B（48 灯，stencil vs clustered）：off mean 0.806 / max 9；on 0.820 / 9；prune 0.818 / 9，全部 `passed=true`。
- T4（D3D11，7 项特性）：全部 `passed=true`（stencil vs clustered on：mean 0.020–0.961、max 3–10）。
- D3D12：默认路径 0.280/0.297/0.313/0.366/0.402/0.488 ms；compact on 0.331/0.337/0.365/0.437/0.405/0.455 ms（48/64 灯落在运行间噪声内），A/B 均 `passed=true`。

## 复跑命令

```bash
U="/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe"
P="E:/GitHub/Glx.CustomURP/URPSample01"
OUT="$P/PLAN/TODO/12-GpuLightList/validation/G3"

"$U" -batchmode -projectPath "$P" -runTests -testPlatform EditMode \
  -testResults "$OUT/tests/G3EditMode.xml" -logFile "$OUT/logs/G3EditMode.log"
"$U" -batchmode -projectPath "$P" -runTests -testPlatform PlayMode \
  -testResults "$OUT/tests/G3PlayMode.xml" -logFile "$OUT/logs/G3PlayMode.log"
"$U" -batchmode -projectPath "$P" -executeMethod ClusteredDeferredArenaBuild.BuildD3D11AndExit \
  -logFile "$OUT/logs/build-t5-d3d11-g3.log"

EXE="$P/Builds/ClusteredDeferredArena/Direct3D11/CdArena.exe"
"$EXE" --cd-output "$OUT/T5-runtime/player-d3d11-g3-compact-off" --cd-compact-off
"$EXE" --cd-output "$OUT/T5-runtime/player-d3d11-g3-compact-on" --cd-compact-on --cd-compact-stats
"$EXE" --cd-output "$OUT/T5-runtime/player-d3d11-g3-prune-control" --cd-prune-on --cd-prune-stats
```

> batchmode/player 会把 `ProjectSettings.asset`、测试场景与 renderer 资产按本机 Unity 重新序列化（对象顺序、材质 GUID、Windows 图形 API 列表）。验证结束后已还原为基线，提交中不含这些改动（同 G1 §A.5）。
