# ScreenSpaceLighting 交接

> 更新：2026-11-14。架构理由、边界和完整任务依赖见 [实施计划](HDRP%20ScreenSpaceLighting%20迁移到%20URP%2014.0.12%20实施计划.md)；本文件只维护下一步。

## 当前位置

- **T0 已冻结**。任何边界变更都必须先重新打开 T0，并同步 PLAN 与本文件。
- **T1 已按 R3 协议退出（2026-11-14）**，并在 T2 Core 改动后由 **Run15 / Run16** 复验：三路径 PNG 逐字节一致、graphics-driver 显存一致、CPU p95 波动 **0.28% / 0.01% / 0.04%**，构建、Frame Debugger 与设置恢复全部通过。`T1Report.json` 当前指向 Run15/Run16；Run13/Run14 作为 T2 前的有效运行对保留。
- **T2 已退出（2026-11-14）**。Core 已提供 DepthNormals smoothness 编码、最早 Motion/Depth 调度和调度单元测试；无插件时三路径 Frame Debugger 事件数据与 T1 基线逐事件一致，探针在 203 时 Motion/Depth 已有效，TAA/Motion Blur 早/晚 Motion 输出逐字节一致。
- **T3–T14 未开始**。当前没有任何 GTAO、SSGI、SSR 或 Refraction 渲染效果实现完成。
- **GPU p95 为参考数据**。本机 DVFS 与 GeForce/WDDM 不执行 `nvidia-smi -lgc` 导致 720p GPU p95 无法重复；证据与理由见 [Environment.json](validation/T1/Environment.json) 和 `Diagnostics/Dvfs-2026-11-14/`。10% 容差、场景、预算数值均未变，Run1–Run16 与无效运行全部保留。
- 临时 Player 已清理；原始运行目录、日志、CSV、PNG 和报告均保留。四项 ProjectSettings 已恢复原始 SHA-256。

唯一事实入口：[T1Report.json](validation/T1/T1Report.json)（`t1ExitCriteriaMet=true`）与 [T2Verification.json](validation/T2/T2Verification.json)（`passed=true`）。

## 下一步：T3 空框架

只实现空框架，不提前实现 GTAO/SSGI/SSR 算法：

1. 建立两个空 Renderer Feature：独立 GTAO Feature 与共享 SSL Feature；资源序列化、remove/disable/Domain Reload 后可重建。
2. 增加 Volume 组件（GTAO 与 SSL 各一套）、设置优先级、能力检测和 neutral 输出；未配置或禁用效果时不得改变基线画面。
3. 建立每个 Feature 私有的 history store 骨架（双缓冲、reset generation、尺寸变化/相机销毁回收），但暂不接算法。
4. 接入 Renderer Data 与 Volume 的 Editor UI 真实路径；Core 不得引用插件类型。
5. 验收：按 PLAN 2.5 的真实 UI 路径配置成功；Volume 控制 Pass 入队；空效果不改变画面；删除/禁用 Feature、Domain Reload、相机销毁无泄漏；Core 不引用 GTAO/SSL 具体类型。
6. T3 退出前重复 T1 无插件回归（下一次使用 **Run17 / Run18**），要求 Frame Debugger 和画面仍与基线一致。

不得跳过 T3 直接实现效果。

## T2 证据与复现

- 结论：`validation/T2/T2Verification.json`（三路径 `passed=true`）
- Frame Debugger（带探针）：`validation/T2/FrameDebugger/FrameDebuggerReport.json`；Forward/Forward+ 11 事件，Deferred 22 事件，顺序为 `DepthNormalPrepass → [Deferred: CopyDepth] → MotionVectors(202) → 探针(203) → GBuffer/Opaque → 天空盒/Blit`
- 无插件不变性：`validation/T2/FrameDebugger/WithoutProbe/`（6/6/16 事件，与 `validation/T1/FrameDebugger/FrameDebuggerReport.json` 的 `eventDataHash` 逐路径相同）
- EditMode 调度测试：`validation/T2/UnityTests.log`（13 passed / 0 failed）
- 采集迭代保留：`FrameDebugger/{DiagnosticRun,ProbeNotEnqueued,WithoutProbe}/`、`FrameDebuggerError.BatchmodeDefaultT1.txt` 及各次 Unity 日志

复现注意：

- Frame Debugger 采集必须使用**交互式编辑器**（`BatchMode: 0`）且 Unity 窗口位于前台、未被遮挡；窗口被遮挡时编辑器 tick 会停，采集超时。
- 探针 Feature 只有在材质与 target 均存在时才入队；`ScreenSpaceLightingFrameDebuggerCapture` 会自行加载 `SslProbe.mat`、创建 `SslT2Probe` 并启用探针。
- `Invoke-T1Unity.ps1` 现在只等待编辑器进程退出，不再被 `Unity.Licensing.Client`、Hub helper 等孤儿进程阻塞；退出码与设置恢复报告不变。

### 重建命令

在项目根目录执行；包装器会等待 Unity 退出并恢复四项项目设置。下次 T3 复验请使用递增 Run 名，不得覆盖已有运行：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "PALN/validation/T1/Invoke-T1Unity.ps1" \
  -ExecuteMethod ScreenSpaceLightingBaselineSetup.BuildBaselinePlayer \
  -LogPath "PALN/validation/T1/UnityBuild.Run17-Run18.log" \
  -ReportPath "PALN/validation/T1/BuildSettingsRestoration.Run17-Run18.json" \
  -Quit
```

### 采集与分析命令

Player 重建后依次执行下列命令；两轮不得并发，Run 名必须递增：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "PALN/validation/T1/Invoke-T1Run.ps1" -RunName Run17

powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "PALN/validation/T1/Invoke-T1Run.ps1" -RunName Run18

python "PALN/validation/T1/Analyze-T1Evidence.py" --runs Run17 Run18
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

- 最新 T1 结论：[T1Report.json](validation/T1/T1Report.json)
- 两轮比较：[Reproducibility.json](validation/T1/Reproducibility.json)
- 原始计时：`validation/T1/Run15/RawFrameTimes.csv`、`validation/T1/Run16/RawFrameTimes.csv`
- GPU 参考数据：`validation/T1/Run15/GpuClocks.csv`、`validation/T1/Run16/GpuClocks.csv`
- T2 结论：[T2Verification.json](validation/T2/T2Verification.json)
- T2 Frame Debugger：[FrameDebuggerReport.json](validation/T2/FrameDebugger/FrameDebuggerReport.json)
- DVFS 诊断：[Diagnostics/Dvfs-2026-11-14/README.md](validation/T1/Diagnostics/Dvfs-2026-11-14/README.md)
- T1 Frame Debugger：[FrameDebuggerReport.json](validation/T1/FrameDebugger/FrameDebuggerReport.json)
- 构建设置恢复：[BuildSettingsRestoration.json](validation/T1/BuildSettingsRestoration.json)
- Frame Debugger 设置恢复：[FrameDebuggerSettingsRestoration.json](validation/T1/FrameDebuggerSettingsRestoration.json)
- 量化预算：[PerformanceBudget.json](validation/T1/PerformanceBudget.json)
