# 23 P3 B6：统一复测（唯一测量批）

**状态：** B6 已执行（2026-11-27）。24 组抓帧、双 API、两进程、三变体 × {P1 回滚, P3 after-GBuffer}；GPU 管线顺序与 225 执行已被 RenderDoc 证实；**门禁 1（缝）与门禁 3（静止方差字面值）NOT MET**，门禁 4/6 PASS，门禁 5 为 proxy（GPU 计时器噪声，真 GPU 门禁仍 OPEN），门禁 2 PARTIAL。

> **B7/B8 之后（2026-11-27）：** 本目录的 `p3` 路径证据是在 B7/B8 之前采集的。B7 把 SSR 注入点从 220 移到 **221**、绑定移到 **222**，并改了 reason 6 覆盖与 225 环境源（新增可选 A2/A3，默认 A1）；B8 又给 SS 命中核补了阴影变体、让 P3 强制运行 RT 命中核。因此 `Win64-*/p3/*` 的哈希与门禁 2/3 结论只对旧 HEAD 有效，需在 B6.1 重测；`Win64-*/p1/*` 回滚路径不受影响，继续作为回归基线（`b1e0a889` / `7fb3e01b` 等冻结值应保持不变）。A2 需要一份开启探针兜底的夹具才能验证。
**前置：** [P3-PLAN.md](../../../P3-PLAN.md) §6；[HANDOFF.md](../../../HANDOFF.md)；P0/P1 证据在 [P0](../P0/README.md) / [P1](../P1/README.md)。

## 运行批次

```
Win64-<D3D12|D3D11>/<p1|p3>/<base|nodenoise|ssroff>/<run-01|run-02>/Player.json + frame.png (+frame.raw, 本机)
```

- `p1` 路径带 `-p3-ssr-rollback`（206 注入、GBuffer 保留镜面）；`p3` 路径带 `-p3-ssr-after-gbuffer`（采集时为 220/221/225/230；B7 起为 221/222/225/230）。
- 每变体两个全新进程；`firstUnityFrame=1`、`unityFrameAtCapture=121`。
- 静态序列：`Static/<api>/<p1|p3>/base`，`-p3-static 300`（先 300 帧纯计时，再 300 帧逐帧 ROI 回读）。
- RenderDoc：`RenderDoc/`（`.rdc` 与导出 bin 按 P0/P1 政策留本机；分析见 [CaptureAnalysis.json](RenderDoc/CaptureAnalysis.json)）。
- 对比脚本：[compare_p3.py](compare_p3.py) → [Comparison.json](Comparison.json)；缝指标复用 [P1/seam_metric.py](../P1/seam_metric.py) → [seam/](seam)。

## 确定性（门禁 6）

12/12 组 run-01 == run-02 逐位一致（`Comparison.json.determinism`）。

## 回滚回归（门禁 4 的哈希部分）

| 配置 | 冻结值 | 本次 | 结果 |
|---|---|---|---|
| D3D12 p1 base | `b1e0a889…`（P1 B6） | `b1e0a889…` | 一致 |
| D3D11 p1 base | `7fb3e01b…` | `7fb3e01b…` | 一致 |
| D3D11 p1 nodenoise | `1bfd98e9…` | `1bfd98e9…` | 一致 |
| D3D11 p1 ssroff | `8ab8e569…` | `8ab8e569…` | 一致 |

`ssrAfterGbuffer` 打开但 SSR 关闭（`ssroff`）时，P3 与 P1 帧在双 API 上**逐位相同**（D3D12 `1295029d…`、D3D11 `8ab8e569…`）：接收者重路由在“只保留环境镜面”的情况下没有改变任何一个像素，验证了 225 的环境项单次计数。

其余 P1→P3 差异（`Comparison.json.comparisons`，absolute channel delta）：

| API | 变体 | 整帧 meanAbs | ROI meanAbs | ROI max |
|---|---|---|---|---|
| D3D12 | base | 2.14e-4 | 5.95e-4 | 0.317 |
| D3D12 | nodenoise | 4.07e-4 | 1.14e-3 | 10.5（萤火虫） |
| D3D11 | base | 6.33e-5 | 1.74e-4 | 0.214 |
| D3D11 | nodenoise | 9.38e-5 | 2.61e-4 | 4.67 |

无 NaN/Inf：24/24 `nonFiniteSamples=0`。

## GPU 顺序（RenderDoc）

D3D12 P3（nodenoise，`p3-d3d12-nodenoise_frame2442.rdc`）：

```
SSR Trace 1487 → R1 Original 1498 → … → DXR Trace and Shade 1534
→ Hit Shading (RT) 1549 [dispatch 1560]
→ Hit Shading (SS) 1562 [dispatch 1571]
→ DXR Temporal 1573 → Reconstruction 1585
→ Material Binding 1608（marker-only）
→ Indirect Specular 1610 [dispatch 1618]
→ Clustered Deferred 1620 [dispatch 1630]
```

D3D11 P3（`p3-d3d11-base_frame4846.rdc`）：无 DXR/命中核 marker；`SSR Indirect Specular` 797 [dispatch 815] 在 `Clustered Deferred` 820 之前——无 RT 时环境镜面仍由 225 产生。
`_R2Hits` 128 位 flags：`2 = GlxHit`（RT）、`10 = GlxHit|SS`（SS 取自当期 GBuffer）。

## 门禁结果

| # | 门禁 | 结果 | 证据 |
|---|---|---|---|
| 1 | 缝：nodenoise SS↔RT 相邻块 median ≤ P0 的 50%，mean 不上升 | **NOT MET** | median 0.002364→0.002149（−9.1%），mean 0.012981→0.015324（+18.1%）；洋红对照 mean +7.6%、`ssroff` 对照 median 更低 → 指标被内容主导（与 P1 结论一致）。三份 `_R1Original` 副本给出相同结论（median −8%~−10%）。 |
| 2 | SS/RT 同一反射一致性 | **PARTIAL** | 边界 block median 0.00215 luma（ROI 均值 0.1298 的 0.17%）；区域均值 SS 0.1124 / RT 0.1421 属位置差。缺“配对反射”口径，未定义阈值。 |
| 3 | 静止 300 帧 ROI 方差 ≤ 基线 | **NOT MET（字面）** | D3D12 3.49e-12→1.46e-11（4.2×），D3D11 2.56e-12→3.47e-11（13.6×）；绝对标准差 1.9e-6→3.8e-6 / 1.6e-6→5.9e-6 luma，300 帧均值跨度 ≤1.6e-5，无漂移/ghost。 |
| 4 | 无 NaN/Inf；D3D11 回退与 P0 逐位一致 | **PASS** | 24/24 无 NaN；D3D11 三变体及 D3D12 base 冻结哈希一致；D3D11 P3 基线与 P1 差异 meanAbs 6.3e-5（1 次 ULP 级离群由 nodenoise 萤火虫贡献）。 |
| 5 | 整帧 ≤ +0.5 ms、SSR 链 ≤ +0.4 ms（双 API GPU 时间） | **PARTIAL / OPEN** | Player FrameTimingManager 中位数（剔除 >100 ms 离群）：D3D12 CPU +0.154 ms、GPU +0.205 ms；D3D11 CPU +0.008 ms、GPU +0.002 ms——均在 +0.5 ms 内。但 GPU 计时器给出 923 ms 与 1.8e12 ms 的无效样本（有效样本 267–296/300），**真 GPU 门禁与逐 pass SSR 链耗时仍 OPEN**。 |
| 6 | 证据：双 API、两进程、原始帧+哈希+计数器+设置恢复 | **PASS** | 24 组 `Player.json`（哈希、R1 计数、TLAS、dispatch 计数）+ PNG + 本机 raw；`SettingsRestore.json` 已在上一次构建记录 `apisRestored=true`。 |

## D5 决策量化

- **#4 一帧跳变（base，P1↔P3）**：D3D12 整帧 meanAbs 2.14e-4（阈值 1e-3），最差 32×32 block 均值跳变 0.0338（阈值 0.05），p99 block 0.0041 → **PASS**；D3D11 6.3e-5 / 0.0240 → **PASS**。
- **#5 历史捕获时机 / 300 帧静止**：无 ghost 漂移，但 P3 方差高于 P1（见门禁 3）；记录为已知代价，不表述为“已修复”。

## 已知限制

1. FrameTimingManager 的 `gpuFrameTime` 在本机不稳定的（有效样本 <100%，离群到 1e12 ms）；门禁 5 只给出中位数 proxy，逐 pass GPU counter 抓帧未做。
2. `_R1Original` 在帧末有三份 41.47 MB 副本（8167/8157/8162 SS 槽位）；RenderDoc 绑定信息不含资源 ID，无法唯一判定消费者用的是哪份。缝指标对三份都跑了，结论一致；副本差异 ≤10 槽位。
3. 缝指标 median 由内容主导（SSR 关闭对照反而更低）；洋红对照只在 mean 上体现（+7.6%）。因此门禁 1 的 50% median 阈值在该夹具上不可达，这是度量形状问题，不是回归。

## B6.1：归因矩阵 + 覆盖率连续化

**状态：代码就绪，像素待运行。** 计划与门禁口径见 [P3-PLAN.md §B6.1](../../../P3-PLAN.md)；脚本 [run_b61.ps1](run_b61.ps1)。

- 用例：`base / thick(0.05) / k-low(8192) / fullres / nodenoise / p1`，双 API、两进程，输出 `B61/<api>/<case>/run-0X`。
- 抓帧器新增参数（请求值与生效值都写 `Player.json`）：`-p3-thickness`、`-p3-fullres`、`-p3-budget`、`-p3-denoiser-radius`。
- 指标：`seam_metric.py` 新增 `ssRtBoundaryNormalized`（边界台阶 ÷ 边界外侧本地梯度），替代被内容主导的原始 median 门禁。
- 代码（P3-only；P1 回滚不读）：合并核同表面邻居借用（alpha ≤ 0.5）、剪影置信度（命中像素深度边缘 → 交出覆盖率份额）、R1 按 wanted 密度分配配额。
- 全分辨率与 denoiser 参数只作 A/B 变量，不改 C4；`base` 仍为 fixture 值（半分辨率、radius 8、feedback 0.91–0.96）。

| 项 | 结果 |
|---|---|
| 契约 + 单测（`SsrRequestTests`、`SsrP3ContractTests`） | **PASS**：31/31、1 skip（RT payload 编译需 DXR 编辑器），[EditMode.xml](B61/tests/EditMode.xml) |
| 归因矩阵 12 组 | OPEN（待运行，需重建 P0 Player） |
| `ssRtBoundaryNormalized` median/p90 base→改进用例下降 | OPEN |
| P1 回滚冻结哈希（`b1e0a889` / `7fb3e01b` / `1bfd98e9` / `8ab8e569`） | 待复核 |
| 探针边界不新增 > 0.02 台阶（相对 `ssroff`） | OPEN |

## 复现

```powershell
$U = "D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe"
$P = "E:\GitHub\Glx.CustomURP\URPSample01"
$V = "$P\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation"
$exe = "$V\P0\Player\SsrP3Baseline.exe"

# 测量批次（每格两进程）
foreach ($api in "d3d12","d3d11") {
  $flag = "-force-$api"
  $dir  = "Win64-D3D" + ($(if ($api -eq "d3d12") {"12"} else {"11"}))
  foreach ($path in "p1","p3") {
    $pflag = if ($path -eq "p1") { "-p3-ssr-rollback" } else { "-p3-ssr-after-gbuffer" }
    foreach ($v in "base","nodenoise","ssroff") {
      foreach ($r in "run-01","run-02") {
        & $exe $flag -p3-output "$V\P3\$dir\$path\$v\$r" -p3-variant $v $pflag -p3-run $r
      }
    }
  }
}

# 静态 300 帧（计时 + ROI 方差）
& $exe -force-d3d12 -p3-output "$V\P3\Static\d3d12\p3\base" -p3-variant base -p3-ssr-after-gbuffer -p3-run static-300 -p3-static 300

python "$V\P3\compare_p3.py"
```
