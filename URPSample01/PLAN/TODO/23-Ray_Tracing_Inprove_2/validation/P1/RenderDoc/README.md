# P1 RenderDoc 抓帧分析（D3D12）

**结论：** P1 的命中着色从未执行，不是 UAV 时序、也不是 raygen 覆盖，而是 **Unity 丢弃了这次 compute dispatch**：`SsrHitDeferred.compute` 经 `GlxProbeSampling.hlsl` 无条件声明了 GI 探针图集，而 SSR-only fixture 不跑探针生产者、这些全局资源未绑定；Unity 在命令缓冲执行到该 dispatch 时以第一条未绑定资源报
`Compute shader (SsrHitDeferred): Property (_GlxProbeIrrSnapshot) at kernel index (0) is not set`
并**不向 GPU 提交派发**。

修复前 `Player.log` 里那条"未绑定探针贴图"的告警就是根因，不是无害噪声；B5-fix 的"固定颜色二分"因此失效——kernel 从未运行，debug 0/1/2 自然逐位相同。

## 抓帧与分析方法

- 工具：RenderDoc 1.47 + `renderdoc-mcp`（`capture_frame` 注入、`open_capture`、`list_events`、`get_bindings`、`goto_event`、`export_buffer`）。
- 目标：`validation/P0/Player/SsrP3Baseline.exe`，`-force-d3d12 -p3-variant base -p3-hit-debug 1`（修复前/后各一帧）。
- 命中表面记录：`_R2Surfaces`（80 B/请求）；发布结果：`_R2Hits`（24 B、960×540 = 12,441,600 B）。把 `_R2Hits` 导出后按 24 B 步长扫描：`radiance == (2,0,2) && hitT == 1 && flags == 2` 即 debug=1 的洋红写入。

## 修复前（`hitdebug1b_frame1990.rdc`）

| 项 | 值 |
|---|---|
| 标记 | `SSR DXR Trace and Shade` 1434 → DispatchRay 1447；**`SSR Hit Shading` 1449 → pop 1450，区间内没有 Dispatch**；`SSR DXR Temporal` 1451 → 1462 |
| 本帧 dispatch 数 | 33（含 hit kernel 应为 34） |
| `_R2Hits` 扫描 | 4 个候选缓冲全部 **洋红 0 条**；flags 直方图 `{0: 506447, 1: 11539, 2: 405, 6: 9}`，即只有 raygen 写过 |
| 日志 | `Property (_GlxProbeIrrSnapshot) ... is not set` |
| 帧 SHA | `96b9c197…`（与 P0 相同） |

## 运行时诊断（新增字段）

给 `ScreenSpaceLightingSsrPass` 加了派发诊断，`SsrP3BaselineCapture` 写进 `Player.json`：

```
hitDeferredCalls = 120, hitDeferredDispatches = 120, skipsSettings = 0, skipsCompute = 0,
hitDeferredKernel = 0, hitDeferredGroups = 512, settings = true, computeBound = true
```

即 **C# 每帧都调用了 `DispatchCompute`**，问题只可能出在执行侧。

## 绑定二分

1. 只补 `_GlxProbeIrrSnapshot`（黑 1×1）：告警变成 `_GlxProbeDistSnapshot`，debug=1 帧仍 `96b9c197…` → dispatch 仍被丢弃。
2. 再补 `_GlxProbeDistSnapshot` 与两个探针 StructuredBuffer：告警消失，debug=0 帧 `2f713f17…`、debug=1 帧 `7cbbe4a5…`。

修复落在 `SsrPass.DispatchHitDeferred`：当 `rayTracingProbeFallback` 关闭时，为探针图集绑占位资源（黑 1×1 + 队列缓冲），它们不会被采样（`_GlxHitLowFrequencySource == 0`）；回退开启时沿用 GI 生产者发布的全局图集。

## 修复后（`hitdebug1-fixed-late_frame2070.rdc`）

| 项 | 值 |
|---|---|
| 标记 | `SSR Hit Shading` 1405 → **Dispatch 1416** → pop 1417 |
| 绑定（CS） | 4 纹理 / 5 个只读 StructuredBuffer（`_R1Queue`、`_R1Counts`、`_R2Surfaces`、两个探针快照）/ **1 UAV（`_R2Hits`）** |
| `_R2Hits` 扫描 | 4 个候选缓冲分别 **洋红 11,950 / 11,949 / 11,951 / 11,950 条**；flags 只剩 `{0, 2}` → 服务槽位被 kernel 全部覆写 |

## 回归

| 项 | 结果 |
|---|---|
| D3D12 base | `2f713f17…`，两次运行一致，无 NaN |
| D3D11 base | `7fb3e01b…`，与 P0 逐位一致（`hitDeferredDispatches = 0`） |
| P0 → 修复帧，地面 ROI | meanAbs `1.36e-6`、p99 `0`、max `6.7e-3`、>1e-2 比例 `0` |

## 复现

```powershell
$exe = "E:\GitHub\Glx.CustomURP\URPSample01\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation\P0\Player\SsrP3Baseline.exe"
$wd  = Split-Path $exe
# 用 renderdoc-mcp 的 capture_frame 注入，exePath=$exe, workingDir=$wd,
# cmdLine = "-force-d3d12 -p3-variant base -p3-hit-debug 1"，约 2~4 秒后触发
# 或 renderdoccmd capture -w -c <模板> -d $wd $exe -force-d3d12 -p3-variant base -p3-hit-debug 1
```

- 两张 `.rdc`（114 MB + 108 MB）只保留在本地，未提交（仓库 `.rdc` 总量已接近 1 GiB 预算）。文件名、尺寸、SHA256 见 [CaptureAnalysis.json](CaptureAnalysis.json)。
- 抓帧时的 `frame.raw` 与临时输出在 `%TEMP%\ssr-p3-rdoc\`，未入库。

## 仍未完成

1. P1 验收的 SS/RT 边界亮度差尚未计算；服务掩码可从洋红抓帧恢复（11,950 个 trace 槽位）。
2. hit kernel 目前没有环境光与阴影：`_GlxHitAmbientIrradiance` 未设置（0），且 compute shader 的 `_MAIN_LIGHT_SHADOWS` / `_ADDITIONAL_LIGHT_SHADOWS` 关键字从未启用（走默认变体）。A 决策要求的是单 tap `SHADOW_LOW`，需要在下一轮补关键字与 ambient 输入。
3. 探针回退开启时，P2 需要绑定真实图集并把 `_GlxHitLowFrequencySource` 置 1。
