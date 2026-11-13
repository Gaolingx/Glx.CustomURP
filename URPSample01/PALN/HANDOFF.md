# ScreenSpaceLighting 交接

> 更新：2026-11-14。架构理由、边界和完整任务依赖见 [实施计划](HDRP%20ScreenSpaceLighting%20迁移到%20URP%2014.0.12%20实施计划.md)；本文件只维护下一步。

## 当前位置

- **T0 已冻结**。任何边界变更都必须先重新打开 T0，并同步 PLAN 与本文件。
- **T1 尚未退出**：测试资产、Player 构建、三路径图像、显存、Frame Debugger 和设置恢复已通过；GPU p95 重复性未通过。
- 最新有效运行为 **Run11 / Run12**：Forward 通过；Forward+ GPU p95 波动 **94.71%**，Deferred **51.58%**，均超过 10% 容差。
- **T2–T14 未开始**。当前没有任何 GTAO、SSGI、SSR 或 Refraction 渲染效果实现完成。
- 临时 Player 已清理；原始 Run1–Run12、无效运行、日志、CSV、PNG 和报告均保留。四项 ProjectSettings 已恢复原始 SHA-256。

唯一事实入口：[T1Report.json](validation/T1/T1Report.json)。当前 `t1ExitCriteriaMet=false`，不得开始 T2。

## 下一步：解除 T1 GPU 计时阻塞

1. 检查 [Reproducibility.json](validation/T1/Reproducibility.json) 及 Run11/Run12 的 `RawFrameTimes.csv`。使用 PIX、NVIDIA Nsight 或其他独立 GPU 工具捕获同一场景，确认尾部峰值来自真实 GPU 工作、DX11 `FrameTimingManager` 计时口径，还是共享显示/驱动环境；保存工具版本和捕获证据。
2. 60 FPS 下 `cpuFrameTime` 包含帧率等待，约为 16.67 ms；不得把它与此前无上限运行的 CPU 渲染耗时直接比较。
3. 若需修正计时方法，先更新 PLAN 和 `PerformanceBudget.json` 的 `protocolRevision`，再采集新运行。**不得降低 10% 容差、删除失败运行或把 p95 换成更宽松的指标。**没有测量方法或环境依据时，不要直接重跑。
4. 修正后重新构建并采集 Run13/Run14；仅当最终报告 `t1ExitCriteriaMet=true` 时，才能把 T1 标为完成并进入 T2。

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

### 两轮采集命令

仅在第 1–3 步完成后执行，两轮不得并发：

```bash
"PALN/validation/T1/Player/SslBaselinePlayer.exe" \
  -force-d3d11 -screen-fullscreen 0 -screen-width 1280 -screen-height 720 \
  -logFile "E:\workspace\GitHub\URPSample01\PALN\validation\T1\PlayerRun13.log" \
  --ssl-baseline-output "E:\workspace\GitHub\URPSample01\PALN\validation\T1\Run13"

"PALN/validation/T1/Player/SslBaselinePlayer.exe" \
  -force-d3d11 -screen-fullscreen 0 -screen-width 1280 -screen-height 720 \
  -logFile "E:\workspace\GitHub\URPSample01\PALN\validation\T1\PlayerRun14.log" \
  --ssl-baseline-output "E:\workspace\GitHub\URPSample01\PALN\validation\T1\Run14"

python "PALN/validation/T1/Analyze-T1Evidence.py" --runs Run13 Run14
```

当前固定协议：Unity `2022.3.62f1`、URP `14.0.12`、StandaloneWindows64 Development Player、DX11、1280×720、种子 `14012`、每路径预热 60 帧、采样 1800 帧、60 FPS、vSync 关闭。

### T1 验收门槛

- 构建 `Succeeded`、0 errors，Unity 退出码 0；构建和 Frame Debugger 的设置恢复报告均通过。
- 两轮报告均满足固定协议，三路径截图有效、无洋红，GPU 时间和 graphics-driver 显存计数有效。
- 三路径 PNG 一致、显存一致，所有 CPU/GPU p95 波动均不超过 10%。
- `GoldenImages` 三张 1280×720 PNG 及 `.meta` 齐全。
- `T1Report.json` 最终为 `t1ExitCriteriaMet=true`；否则保留证据并继续阻塞，不能开始 T2。
- 证据核对完成后删除 `PALN/validation/T1/Player/`，保留工具、日志、运行目录和报告。

## T1 解除后的下一任务：T2

只实施 Core 通用支持，不提前实现效果算法：

1. 修改 `UniversalRenderer.cs`，汇总最早 Motion 消费事件并同步提前所需 Depth/CopyDepth；无提前消费者时保持原时序。
2. 修改 `LitDepthNormalsPass.hlsl` 及计划列出的对应 DepthNormals Pass，使用 `0.5 + 0.5 * smoothness` 有效编码，0 表示不支持。
3. 增加通用 keyword 清理与调度测试；Core 不引用具体 GTAO/SSL Feature、Module 或 Volume 类型。
4. 验收无插件时画面和 Frame Debugger 不变，202 前 Motion/Depth 有效，三路径静止/动态向量、TAA/Motion Blur 回归通过，smoothness=0 与无效材质可区分。

## 证据入口

- 最新结论：[T1Report.json](validation/T1/T1Report.json)
- 两轮比较：[Reproducibility.json](validation/T1/Reproducibility.json)
- 原始计时：`validation/T1/Run11/RawFrameTimes.csv`、`validation/T1/Run12/RawFrameTimes.csv`
- Frame Debugger：[FrameDebuggerReport.json](validation/T1/FrameDebugger/FrameDebuggerReport.json)
- 构建设置恢复：[BuildSettingsRestoration.json](validation/T1/BuildSettingsRestoration.json)
- Frame Debugger 设置恢复：[FrameDebuggerSettingsRestoration.json](validation/T1/FrameDebuggerSettingsRestoration.json)
- 量化预算：[PerformanceBudget.json](validation/T1/PerformanceBudget.json)
