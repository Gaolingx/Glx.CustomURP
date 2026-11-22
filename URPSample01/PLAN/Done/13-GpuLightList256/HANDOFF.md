# 13-GpuLightList256：256+ 灯扩展性实测（决策探针）

> 建立：2026-11-22；前置：[`PLAN/Done/12-GpuLightList`](../12-GpuLightList/HANDOFF.md)（默认同步 GPU 列表 + 位图；G2/G3 为 NO-GO）。
> 性质：半天内的决策探针，不是完整轮次。只回答“GPU 光表在 256 灯下是否还站得住”，据此决定继续、改 word 预算还是归档这条线。
> 状态：**测量完成（2026-11-22）。结论：GPU 列表在 256 灯成立，保持默认同步路径；不需要改 word 预算；G2/G3 仍 NO-GO。下一步是透明/forward-only 接管（12 轮 D-G4 / clean.md C2）。**
> 范围：不改默认路径；G2/G3 开关保持默认关闭，只作为对照。

## 0. 问题

12 轮把列表构建搬到 GPU 并设为默认（同步），但所有证据都在 ≤64 灯。256 灯时：

- `m_WordsPerTile = 8`，`maxZBinWords = 4096` → zbin 预算 `4096 / (2 + 8) ≈ 409` 档（待实测；用户此前担心只有 ~30 档，需要核实）。
- `maxTileWords = 16,384` → 720p 下 tile 宽度会从 16/8 升到 32。
- 每像素要扫 8 个 tile word，位图迭代器的成本形态与 ≤64 灯（1–2 word）不同；这也决定 G3 紧凑列表是否翻盘。

需要同时确认：

1. parity 是否仍成立（zbin 逐位相等、tile CPU ⊆ GPU、无丢失位）。
2. 列表构建 CPU、clustered 光照 GPU、stencil 回退 GPU 在 256 灯的成本。
3. A/B 是否仍 `passed=true`，有无 magenta/溢出。
4. G3 紧凑列表在 8 word 下是否反而有利（可选对照）。

## 1. 方法

同一 D3D11 player、1280×720、arena 场景；新增 CLI 覆盖（`ClusteredDeferredArenaRunner`）：

- `--cd-total-lights 256`
- `--cd-light-counts 0,32,64,128,256`
- `--cd-screenshot-lights 256`
- `--cd-samples <n>` / `--cd-warmup <n>`（缩短或加长采样）

四次运行：

| 运行 | 开关 | 产出 |
|---|---|---|
| parity | `--cd-parity --cd-total-lights 256` | `G1BufferParity.json`：zbin/tile parity、`binCount`、`wordsPerTile`、`tileWidth`、tile 分辨率、item 覆盖 |
| cpu | `--cd-cpu --cd-total-lights 256` | `CdCpuBreakdown.json`：Stencil vs Clustered 的 PreSetup/job 完成/上传/灯常量 |
| sweep | 默认 | `CdPerf.json`：0/32/64/128/256 灯的 CPU/GPU、A/B、magenta |
| compact（可选） | `--cd-compact-on --cd-compact-stats` | `CdPerf.json` 的 compact cell/entry/overflow 与 GPU 时间 |

## 2. 退出判定

- **正确性**：256 灯 zbin parity 逐位、tile 无漏位、A/B `passed=true`、无 magenta。
- **成本**：列表构建 CPU、clustered GPU 与 stencil GPU 在 256 灯均记录；与 64 灯基线对比。
- **决策三选一**：保持 GPU 列表默认（扩展性成立）／调整 `maxZBinWords` 或 tile 预算后复测／归档该线（256 灯下不划算或不可用）。

## 3. 风险与边界

- arena 灯场在 >64 灯时每 64 盏位置复用（`depth = (i / 16) % 4`）：256 灯是“每个位置 4 盏”的最坏重叠场。结论只代表该压力场景，不代表空间均匀分布；若结果处于边界，再补一个展开深度的场。
- 灯场类型按 `i % 3` 交替，位置复用但类型不同（Point/Spot）。
- player 构建会临时重序列化场景与项目设置；验证后还原，流程与 12 轮一致。

## 4. 结果

完整数据、命令与像素对比见 [`validation/README.md`](validation/README.md)。

### 4.1 关键数字（D3D11，1280×720）

| 项 | 64 灯（对照） | 256 灯 |
|---|---:|---:|
| `binCount` / `wordsPerTile` / `tileWidth` | 1024 / 2 / 16 | **409 / 8 / 32** |
| zbin parity（CPU vs GPU） | 逐位相等 | **逐位相等**（4090 word 用满） |
| tile parity | CPU ⊆ GPU，0 漏位 | **CPU ⊆ GPU，0 漏位**（CPU 73,743 / GPU 166,854 bit） |
| Stencil / Clustered GPU | 0.661 / 0.416 ms | **1.763 / 1.270 ms** |
| Stencil / Clustered CPU | 1.485 / 1.048 ms | **3.218 / 1.811 ms** |
| GPU 列表 CPU | ~0.061 ms | **~0.155 ms**（灯常量 0.407 ms 另计） |

- zbin 深度分辨率：9.64 octave / 409 bin ≈ **0.024 octave/bin**（约 1.6% 相对深度）；用户此前预期的“~30 档”没有出现。
- 256 灯下 clustered 仍比 stencil 省约 28% GPU、44% CPU；交叉点仍在 32 灯以下。
- 像素：clustered vs Forward+ mean **0.290** / 622 px >8；stencil vs clustered mean **4.080** / max 29 失败，stencil vs Forward+ mean 3.864。

### 4.2 256 灯 A/B 失败的根因

不是列表丢灯，而是 **stencil 参照路径的累加精度**：

- arena pipeline 的 HDR 缓冲是 `R11G11B10f`（`m_HDRColorBufferPrecision: 0`）。stencil deferred 对每盏灯做一次 additive blend，256 灯产生 256 次舍入；clustered 与 Forward+ 都在单次着色内求和、写一次。
- 差异的通道最大值是 (12,12,29)，蓝色占优，正好对应 R11G11B10f 蓝色分量精度最低；48 灯时同一 A/B 仍是 mean 0.803 / max 9，随灯数增长而越过阈值。
- 因此 256 灯的正确性参照应换成 Forward+（或 64-bit HDR 缓冲），12 轮的 stencil A/B 门槛不外推到 256。

### 4.3 G2/G3 复核

| 运行 | GPU (ms) | 结果 |
|---|---:|---|
| 默认（compact off） | 1.206 | — |
| `--cd-prune-on` | 1.284 | 清 63.1% 位，无收益；像素与同序列 off 有 mean 0.233 差异（默认关闭，未继续归因） |
| `--cd-compact-on` | 1.309 | 非空 cell 平均 ≈379 项，6,475 cell overflow（K=32），仍更慢 |

G2/G3 在 256 灯仍未翻盘；紧凑列表要赢需要“每 cell 项数远小于 word 扫描”的分布，本灯场（高重叠）没有出现。

### 4.4 决策

1. **保持 GPU 列表默认**：同步两段式 + 位图在 256 灯下 parity 成立、像素与 Forward+ 一致、性能与 CPU 仍优于 stencil。
2. **不改 `maxZBinWords` / tile 预算**：409 档与 32 px tile 已用满预算且工作正常；没有 depth 分辨率崩塌。
3. **G2/G3 保持默认关闭**：256 灯下仍无收益。
4. **关闭“256+ 灯扩展性”这条待证项**；下一个实际能力缺口是透明/forward-only 接管（D-G4 / clean.md C2），需另立一轮。

### 4.5 边界

- 256 灯场是每 64 盏复用位置的最坏重叠场（96/256 item 整屏）；不代表空间均匀分布。若需要代表性数字，应展开深度轴后复测。
- 只测 D3D11 720p 单卡；未覆盖 D3D12、异步、透明/forward-only、XR、GLES。
- 256 灯跨运行像素噪声约 mean 0.11 / 5,750 px >8（同序列 compact on/off 逐位一致），因此所有结论取同一次运行内的对比。
