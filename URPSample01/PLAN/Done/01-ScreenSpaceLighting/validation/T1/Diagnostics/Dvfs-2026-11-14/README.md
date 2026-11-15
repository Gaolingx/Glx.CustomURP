# T1 GPU 计时诊断：DVFS（2026-11-14）

本目录记录 T1 GPU p95 重复性失败的独立诊断，不属于正式的 Run13/Run14 基线。

## 目的

HANDOFF 要求在修改计时方法前，用独立 GPU 工具确认 Run11/Run12 的 GPU p95 尾部尖峰来源。候选来源有三类：真实 GPU 工作、DX11 `FrameTimingManager` 计时口径、共享显示/驱动环境。

## 工具与环境

- `nvidia-smi` / NVML 576.80，驱动 576.80，GPU 为 NVIDIA GeForce RTX 5060 Ti（Max SM Clock 3090 MHz，Power Limit 180 W）。
- 工具在 Player 运行期间以 `-lms 50` 采样 `timestamp,clocks.sm,clocks.mem,pstate,power.draw,utilization.gpu,temperature.gpu`，原始数据见 `GpuClocks.csv`。
- 运行前/后的完整 NVML 快照见 `nvidia-smi-q-before.txt`、`nvidia-smi-q-after.txt`。
- Player 为当前仓库（`E:\GitHub\Glx.CustomURP\URPSample01`）重建的同一 Development Player，命令与 T1 固定协议一致（DX11、1280×720、render scale 1、60 FPS、vSync 关闭、种子 14012、预热 60、采样 1800）。
- 三张截图与 `Assets/Tests/ScreenSpaceLighting/GoldenImages` 的 SHA-256 完全一致，说明迁移路径后构建产物未改变画面。

## 结果

`DvfsAnalysis.json` 为机器可读汇总，关键数字：

- Player 运行的 91 s 窗口内，SM 时钟在 **63–1515 MHz** 之间切换 **180 次**，P-state 分布 P5 1184 / P8 387 / P3 229；GPU 最大时钟为 3090 MHz。
- 各路径 SM 时钟中位数仅 92–600 MHz，低于 300 MHz 的采样占比 49%–80%。
- 同一窗口 GPU 利用率均值 6.9%–9.0%，按 16.67 ms 帧距折算约 **1.1–1.5 ms/帧** 的 GPU 忙时。
- 同一构建下，本次运行（低时钟）GPU 中位数 0.15–0.20 ms、p95 0.94–2.35 ms；Run11/Run12（当时时钟状态不同）中位数为 0.075–0.13 ms。相同内容的帧时间随 GPU 频率状态大幅漂移。

## 结论

T1 固定场景每帧只有约 0.1 ms GPU 工作，GPU 在 60 FPS 下基本空闲，驱动持续在 P3/P5/P8 间降频与升频。`FrameTimingManager` 的 GPU p95 因此跟随 DVFS 状态而不是确定性的渲染成本变化，10% 容差在未锁频条件下不可达。

因此曾把 T1 计时协议修订为 R2：测量期间把 SM 时钟锁定到固定值（`nvidia-smi -lgc 1800`），结束后立即 `nvidia-smi -rgc` 恢复，并保留时钟采样证据。p95 指标、10% 容差、场景、分辨率、帧率上限和采样帧数保持不变；Run1–Run12 全部保留。

## 后续：R2 锁频不可执行，R3 采用参考数据

R2 实测失败：`nvidia-smi -lgc 300`、`-lgc 600`、`-lgc 1800` 均返回 0，但 SM 时钟仍停留在 36–224 MHz，persistence mode 为 `[N/A]`，即 GeForce/WDDM 驱动不执行应用时钟锁。证据见 `../clock-lock-verify.txt`。因此 R2 下的 Run13 虽完整跑完，其时钟仍在 P3/P5 间波动，锁频门槛永远无法满足。

R3 据此把 720p T1 的 GPU p95 降级为逐轮记录的参考数据，不作为 T1 或模块的通过条件；T1 可重复性改由 CPU p95（10% 对称容差）、截图逐字节一致、graphics-driver 显存一致和 Pass 顺序不变共同判定。p95 指标、10% 容差、场景、分辨率、帧率上限和采样帧数均未改变，Run1–Run13 全部保留。模块级增量 GPU 预算必须改用独立 GPU profiler 或可锁频环境验证。

## 证据文件

| 文件 | 内容 |
|---|---|
| `GpuClocks.csv` | 50 ms 间隔的 NVML 原始采样（含运行窗口外尾部） |
| `DvfsAnalysis.json` | 窗口、P-state、时钟分布与逐路径统计 |
| `nvidia-smi-q-before.txt` / `nvidia-smi-q-after.txt` | 运行前后 NVML 完整快照 |
| `PlayerRun.log` | 本次诊断 Player 日志 |
| `Run/` | 本次诊断的三路径报告、原始帧时间与截图（未锁频） |
