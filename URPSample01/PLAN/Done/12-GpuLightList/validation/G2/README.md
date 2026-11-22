# G2 证据索引（FPTL tile 深度细剪）

结论：**NO-GO**（详见 [HANDOFF §4.2](../../HANDOFF.md)）。位图清位数 ~65%，像素逐位不变，但端到端 GPU 时间没有下降；细剪 dispatch 本身在 0 灯时就要 ~0.07 ms。开关保留为 A/B，默认关闭。

## 最终证据（当前 HEAD 的代码）

| 内容 | 路径 |
|---|---|
| EditMode 490/490 | `tests/G2EditMode.xml`、`logs/G2EditMode.log` |
| PlayMode 39/39（含 `DepthPruneKeepsPixelsIdentical`） | `tests/G2PlayMode.xml`、`logs/G2PlayMode.log` |
| 编译（clean） | `logs/compile5.log` |
| D3D11 T5：prune on（stats）/ off，同一 exe | `T5-runtime/player-d3d11-g2-prune/`、`T5-runtime/player-d3d11-g2-noprune/` |
| D3D11 T5：仅深度（关闭侧平面判定） | `T5-runtime/player-d3d11-g2-zonly/` |
| D3D12 T5：默认路径（prune off） | `T5-runtime/player-d3d12-g2-default/` |
| 构建日志 | `logs/build-t5-d3d11-g2-prune.log`、`logs/build-t5-d3d12-g2-default.log` |

复跑开关：`--cd-prune-on` / `--cd-prune-off` / `--cd-prune-zonly` / `--cd-prune-stats`（`CdPerf.json` 的 `clusterListPrune` 与 `pruneKeptBits/pruneClearedBits` 记录实际生效值）。

## 迭代证据（已删除，结论保留在下文/ HANDOFF）

- 中间版本（单 `ClusterTileDepth` kernel + 逐 tile 深度/清位回读、invVP 线性化、空 tile-only、分组 kernel 等）的 player 目录、构建日志和 `TileDepth*/ItemDepth*.csv` 已从仓库删除，避免把 ~65 MB 的迭代产物混进验收；其中关键结论写在 [HANDOFF §4.1/§4.2](../../HANDOFF.md)。
- `tile-depth-grid.png`：按 tile 深度可视化的诊断图（红 = 无几何 tile，最终 kernel 会把它们的位全部清掉）。
- `prune-compare.png`、`prune-crops.png`：定位 48 灯 84 px 过剪的诊断截图（tile 深度与像素 kernel 深度空间不一致，已用 `_ZBufferParams` 线性化修复）。
