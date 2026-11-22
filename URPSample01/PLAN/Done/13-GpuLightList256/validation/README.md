# 13-GpuLightList256 验证记录

> 环境：Unity 2022.3.62f1、D3D11 standalone player、RTX 5060 Ti、1280×720。
> 代码：`ClusteredDeferredArenaRunner` 新增 CLI 覆盖（`--cd-total-lights`、`--cd-light-counts`、`--cd-screenshot-lights`、`--cd-samples`、`--cd-warmup`），并在 parity 报告中记录 `binCount/wordsPerTile/tileWidth/tileResolution`。
> 灯场：arena 默认布局在 >64 灯时每 64 盏复用位置（`depth = (i/16) % 4`），256 灯即“每个位置 4 盏”的最坏重叠场；96/256 个 item 整屏。

## 结论

- **GPU 列表在 256 灯站得住**：zbin parity 逐位相等、tile CPU ⊆ GPU 无漏位；像素与 Forward+ 一致（mean 0.290），性能仍优于 stencil（GPU 1.27 vs 1.76 ms，CPU 2.10 vs 4.27 ms）。
- **zbin 不是 ~30 档**：`binCount = 409`、`wordsPerTile = 8`、`tileWidth = 32`（40×23）；9.64 octave 深度范围约 0.024 octave/bin（≈1.6% 相对深度），`maxZBinWords = 4096` 用满 4090。无需改 word 预算。
- **256 灯 A/B 失效的是 stencil 参照，不是列表**：stencil vs clustered mean 4.080 / max 29；clustered vs Forward+ mean 0.290；stencil vs Forward+ mean 3.864。通道最大值 **(12,12,29)** 的蓝色占优对应 `R11G11B10f`：stencil 每盏灯一次加法混合（256 次舍入），clustered 与 Forward+ 都是一次着色内求和。48 灯基线 stencil vs clustered 仍是 mean 0.803 / max 9。
- **G2/G3 在 256 灯仍 NO-GO**：prune 清掉 63.1% 的位但 1.284 vs 1.206 ms；compact on 1.309 ms（+0.10），非空 cell 平均约 379 项、6,475 个 cell overflow（K=32）。

## Parity（同一 player）

| 项 | 64 灯（对照） | 256 灯 |
|---|---:|---:|
| `binCount` / `wordsPerTile` | 1024 / 2 | **409 / 8** |
| `tileWidth` / 分辨率 | 16 / 80×45 | **32 / 40×23** |
| zbin 已用 word / 总量 | 4096 / 4096 | 4090 / 4096 |
| tile 已用 word / 总量 | 7,200 / 16,384 | 7,360 / 16,384 |
| zbin CPU vs GPU | 0 word / 0 bit 差异 | **0 word / 0 bit 差异** |
| tile 漏位 / 多位 | 0 / 91,350 | **0 / 93,111** |
| tile bit：CPU → GPU | 70,624 → 161,974（229%） | 73,743 → 166,854（226%） |
| item / 整屏 item / 中位覆盖 tile | 64 / 22 / 2,430 | 256 / 96 / 621（共 920） |

`passed=true`；64 灯数字与 G1c 完全一致，说明新 player 未改变默认路径。

## 性能（D3D11，1280×720，120–150 帧均值；GPU 有 ~10% 运行间波动）

| 灯数 | Stencil GPU | Clustered GPU | Forward+ GPU | Stencil CPU | Clustered CPU |
|---:|---:|---:|---:|---:|---:|
| 0 | 0.161 | 0.220 | 0.315 | 0.793 | 0.870 |
| 32 | 0.436 | 0.368 | 0.381 | 1.100 | 0.866 |
| 64 | 0.661 | 0.416 | 0.466 | 1.485 | 1.048 |
| 128 | 1.127 | 0.704 | 0.706 | 2.169 | 1.371 |
| 256 | **1.763** | **1.270** | 1.246 | **3.218** | **1.811** |

256 灯 p95：stencil 2.364、clustered 2.193、Forward+ 2.410 ms。clustered 在 256 灯仍比 stencil 省约 28% GPU、44% CPU；交叉点仍低于 32 灯。

## CPU 分解（256 灯，120 帧）

| slice (ms) | StencilDeferred | ClusteredDeferred |
|---|---:|---:|
| frame CPU | 4.267 | 2.095 |
| `preSetup` | 0.0002 | 0.0104 |
| `SetupLights` 总计 | 0.396 | 0.569 |
| ├ 灯常量 | 0.329 | 0.407 |
| ├ cluster jobs complete | 0 | 0.0001 |
| ├ cluster 上传 | 0 | 0 |
| └ `DeferredLights.SetupLights` | 0.013 | 0.0074 |
| `PrecomputeLights` | 0.0065 | 0.0001 |
| 其他（含逐灯 stencil 提交） | 3.871 | 1.515 |

GPU 列表的 CPU 成本 ≈ `setupLights - 灯常量 - deferredSetup` = **~0.155 ms/帧**（64 灯约 0.061），构成是 item 构建 + 5 次 dispatch + 一次上传；灯常量（0.407 ms）仍由 CPU 承担，GPU 列表不消除它。

## 像素与 A/B（同一次 sweep，256 灯）

| 对比 | mean | max (R,G,B) | >8 像素 |
|---|---:|---|---:|
| Stencil vs Clustered | **4.080** | (12,12,29) | 387,697 |
| Clustered vs Forward+ | **0.290** | (86,56,13) | 622 |
| Stencil vs Forward+ | 3.864 | (86,56,29) | 387,193 |

- 48 灯基线：Stencil vs Clustered mean 0.803 / max 9 / 13 px；Clustered vs Forward+ mean 0.209 / max ~81 / 453 px（同一组管线在低灯数下 Forward+ 与 clustered 的固定差异）。
- arena pipeline 的 `m_HDRColorBufferPrecision: 0` = `R11G11B10f`。stencil deferred 对每盏灯做一次 additive blend，256 灯累计 256 次舍入；clustered/Forward+ 在单次着色内求和后写一次。蓝色通道精度最低，与 max 分布的 (12,12,29) 一致。因此 256 灯的 A/B 不能再用 stencil 作参照；需要参照时用 Forward+ 或 64-bit HDR 缓冲。
- 跨运行像素噪声（256 灯、同为 compact off 的两次运行）：mean 0.114、max 102、5,750 px >8；同序列的 compact on/off 逐位一致（max 1）。所有结论均取同一次运行内的 stencil/clustered/Forward+ 对比。

## G2/G3 在 256 灯（可选对照）

| 运行 | GPU (ms) | 说明 |
|---|---:|---|
| 默认（compact off） | 1.206 | 64→256 序列 |
| `--cd-prune-on` | 1.284 | kept 61,545 / cleared 105,309（63.1%），无收益 |
| `--cd-compact-on` | 1.309（重跑同值） | 非空 cell 585、索引 221,645（≈379/非空 cell）、overflow cell 6,475、overflow tile 19、扫描 7,874 cell |
| 同序列 compact off 对照 | 1.358 | 用于 prune/compact 的像素对照 |

- 首次 `compact/player-d3d11-256-on`（64→256）的 L64 GPU 均值出现一次 FrameTimingManager 异常值（1.37e10 ms；p95 1.058 ms 正常）；该次只采用 L256，1.309 ms 来自 L256-only 重跑。
- 紧凑列表在 8 word 下仍更长（每 (tile,zbin) 平均数百项），且绝大多数 cell 超过 K=32 回退位图；没有翻盘。
- prune 在 256 灯仍只增加固定成本；其像素与同序列 off 对照有 mean 0.233 / max 51 / 9,302 px 的差异。由于该开关默认关闭、且本轮不改 G2 结论，未继续归因；若将来要在高灯数启用 prune，需要单独验证。

## 复跑命令

```bash
U="/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe"
P="E:/GitHub/Glx.CustomURP/URPSample01"
OUT="$P/PLAN/Done/13-GpuLightList256/validation/256"

"$U" -batchmode -projectPath "$P" -executeMethod ClusteredDeferredArenaBuild.BuildD3D11AndExit \
  -logFile "$OUT/logs/build-d3d11-256.log"

EXE="$P/Builds/ClusteredDeferredArena/Direct3D11/CdArena.exe"
"$EXE" --cd-output "$OUT/parity/player-d3d11-64"  --cd-parity --cd-total-lights 64
"$EXE" --cd-output "$OUT/parity/player-d3d11-256" --cd-parity --cd-total-lights 256
"$EXE" --cd-output "$OUT/cpu/player-d3d11-256"    --cd-cpu --cd-total-lights 256
"$EXE" --cd-output "$OUT/sweep/player-d3d11-256"  --cd-total-lights 256 \
  --cd-light-counts 0,32,64,128,256 --cd-screenshot-lights 256
"$EXE" --cd-output "$OUT/compact/player-d3d11-256-on-rerun" --cd-total-lights 256 \
  --cd-light-counts 256 --cd-screenshot-lights 256 --cd-compact-on --cd-compact-stats
"$EXE" --cd-output "$OUT/prune/player-d3d11-256-on" --cd-total-lights 256 \
  --cd-light-counts 256 --cd-screenshot-lights 256 --cd-prune-on --cd-prune-stats
"$EXE" --cd-output "$OUT/prune/player-d3d11-256-off-control" --cd-total-lights 256 \
  --cd-light-counts 256 --cd-screenshot-lights 256 --cd-prune-off
```

> player 构建会重序列化场景、renderer 资产和 ProjectSettings；验证后已还原，提交只包含 CLI 覆盖与 parity 字段。`Builds/` 与 `.rdc` 之外的运行产物均在 `validation/256/`。

## 边界

- 256 灯场是位置复用的最坏重叠压力场，不代表空间均匀分布；若要代表性的 256 灯数字，需要展开深度或换成更宽的灯场后复测。
- 本轮只测 D3D11 720p 单卡；未覆盖 D3D12、异步、透明/forward-only、XR、GLES。
