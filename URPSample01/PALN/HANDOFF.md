# ScreenSpaceLighting 交接

> 更新：2026-11-14。架构理由、边界和完整任务依赖见 [实施计划](HDRP%20ScreenSpaceLighting%20迁移到%20URP%2014.0.12%20实施计划.md)；本文件只维护下一步。

## 当前位置

- **T0 已冻结**。任何边界变更都必须先重新打开 T0，并同步 PLAN 与本文件。
- **T1 已按 R3 协议退出（2026-11-14）**。有效运行对为 **Run13 / Run14**：三路径 PNG 逐字节一致、graphics-driver 显存一致、CPU p95 波动 2.19% / 2.28% / 2.30%，构建、Frame Debugger 与设置恢复全部通过。
- **GPU p95 为参考数据**。本机 DVFS 与 GeForce/WDDM 不执行 `nvidia-smi -lgc` 导致 720p GPU p95 无法重复；证据与理由见 [Environment.json](validation/T1/Environment.json) 和 `Diagnostics/Dvfs-2026-11-14/`。10% 容差、场景、预算数值均未变，Run1–Run13 与无效运行全部保留。
- **T2–T14 未开始**。当前没有任何 GTAO、SSGI、SSR 或 Refraction 渲染效果实现完成。
- 临时 Player 已清理；原始运行目录、日志、CSV、PNG 和报告均保留。四项 ProjectSettings 已恢复原始 SHA-256。

唯一事实入口：[T1Report.json](validation/T1/T1Report.json)。当前 `t1ExitCriteriaMet=true`，可以开始 T2。

## 下一步：T2 Core 通用支持

只实施 Core 通用支持，不提前实现效果算法：

1. 修改 `UniversalRenderer.cs`，汇总最早 Motion 消费事件并同步提前所需 Depth/CopyDepth；无提前消费者时保持原时序。
2. 修改 `LitDepthNormalsPass.hlsl` 及计划列出的对应 DepthNormals Pass，使用 `0.5 + 0.5 * smoothness` 有效编码，0 表示不支持。
3. 增加通用 keyword 清理与调度测试；Core 不引用具体 GTAO/SSL Feature、Module 或 Volume 类型。
4. 验收无插件时画面和 Frame Debugger 不变，202 前 Motion/Depth 有效，三路径静止/动态向量、TAA/Motion Blur 回归通过，smoothness=0 与无效材质可区分。

T2 退出后再进入 T3；不得跳过 T2 直接实现效果。

### 重建命令

在项目根目录执行；包装器会等待 Unity 退出并恢复四项项目设置：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "PALN/validation/T1/Invoke-T1Unity.ps1" \
  -ExecuteMethod ScreenSpaceLightingBaselineSetup.BuildBaselinePlayer \
  -LogPath "PALN/validation/T1/UnityBuild.log" \
  -ReportPath "PALN/validation/T1/BuildSettingsRestoration.json" \
  -Quit
```

### 采集与分析命令

若 T2 改动后需要重新核对基线，Player 重建后依次执行下列命令；两轮不得并发：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "PALN/validation/T1/Invoke-T1Run.ps1" -RunName Run15

powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "PALN/validation/T1/Invoke-T1Run.ps1" -RunName Run16

python "PALN/validation/T1/Analyze-T1Evidence.py" --runs Run15 Run16
```

`Invoke-T1Run.ps1` 会记录 NVML 时钟采样到运行目录，仅作 GPU 参考数据，不参与判定。

当前固定协议：Unity `2022.3.62f1`、URP `14.0.12`、StandaloneWindows64 Development Player、DX11、1280×720、种子 `14012`、每路径预热 60 帧、采样 1800 帧、60 FPS、vSync 关闭。

### T1 验收门槛（R3，已满足）

- 构建 `Succeeded`、0 errors，Unity 退出码 0；构建和 Frame Debugger 的设置恢复报告均通过。
- 两轮报告均满足固定协议，三路径截图有效、无洋红，CPU/GPU 时间与 graphics-driver 显存计数均已采集。
- 三路径 PNG 一致、显存一致，所有 **CPU** p95 波动不超过 10%；GPU p95 仅记录和报告，不作为通过条件（理由见 `PerformanceBudget.json` 的 `gpuTimingPolicy`）。
- `GoldenImages` 三张 1280×720 PNG 及 `.meta` 齐全。
- `T1Report.json` 最终为 `t1ExitCriteriaMet=true`。
- 证据核对完成后删除 `PALN/validation/T1/Player/`，保留工具、日志、运行目录和报告。

## 证据入口

- 最新结论：[T1Report.json](validation/T1/T1Report.json)
- 两轮比较：[Reproducibility.json](validation/T1/Reproducibility.json)
- 原始计时：`validation/T1/Run13/RawFrameTimes.csv`、`validation/T1/Run14/RawFrameTimes.csv`
- GPU 参考数据：`validation/T1/Run13/GpuClocks.csv`、`validation/T1/Run14/GpuClocks.csv`
- DVFS 诊断：[Diagnostics/Dvfs-2026-11-14/README.md](validation/T1/Diagnostics/Dvfs-2026-11-14/README.md)
- Frame Debugger：[FrameDebuggerReport.json](validation/T1/FrameDebugger/FrameDebuggerReport.json)
- 构建设置恢复：[BuildSettingsRestoration.json](validation/T1/BuildSettingsRestoration.json)
- Frame Debugger 设置恢复：[FrameDebuggerSettingsRestoration.json](validation/T1/FrameDebuggerSettingsRestoration.json)
- 量化预算：[PerformanceBudget.json](validation/T1/PerformanceBudget.json)
