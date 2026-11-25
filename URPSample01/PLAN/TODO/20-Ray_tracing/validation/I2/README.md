# I2 Windows 画质/性能/平台（HEAD 复跑）

复跑入口：[run.py](run.py)（`--strip-only` 只做构建与剥离审计）、[verify.py](verify.py)。证据在 `evidence/`，首轮失败记录在 `evidence-pre-fix/`，P0-1 的 scoped 剥离审计在 `evidence/strip-audit/`。

## P0-2 计量口径（本轮决定）

1. **全帧 GPU**：`FrameTimingManager` 在本机 D3D12 只有近零/部分样本（`gpuFrameSamples` 38–120），因此 `gpuFrameTimingUsable=false`；`gpuFrameP95` 仍记录但仅供参照，**没有任何门槛使用它**。RT 帧增量继续用五个不重叠 GPU scope 求和（TLAS build、probe trace/update、relocate/classify、SSR trace、SSR reconstruction；命中着色嵌套在 trace scope 内），它是 RT 帧增量的**下界**，由报告字段 `rtGpuAccounting` 记录。B0 的 4 ms 门槛按该口径通过。
2. **显存**：用图形驱动分配内存的峰值增量 `76.7 MiB`（B0 256 MiB）。该增量包含加速结构的分配，但 Unity 不暴露 per-AS 尺寸；字段 `rtMemoryAccounting` 记录。补充测量尝试：RenderDoc 1.47 对 D3D12 DXR Player 抓帧未成功（CLI 未能完成捕获，进程已清理），未取得 AS 精确字节数，按上述代理口径记录。
3. **冷启动**：新增 `cold` 阶段——进程启动后的前 60 帧、无预热、RT（GI+SSR）开启；D3D11 同阶段作为后端控制。记录 p50/p95/max、RT scope 求和、首个 TLAS build / probe update / probe publish 帧、驱动内存起始/峰值，并汇总到报告 `coldStart` 字段。

## 结果（HEAD `aceb6ca` 之后的 fixture，GUID `91057fe5…`，剥离 `9ca1c0ad…`）

| 项 | D3D12 | D3D11 |
|---|---|---|
| 冷启动 CPU p50 / p95 / max | `4.13 / 25.87 / 389.1 ms` | `1.81 / 21.76 / 214.4 ms` |
| 热 `both` CPU p95 | `4.05 ms` | — |
| 冷启动 RT scope 求和 p95 / 热 | `0.652 / 0.391 ms` | 0（Backend） |
| 首个 build / update / publish 帧 | `0 / 0 / 0` | — / — / — |
| 冷启动驱动内存 起始→峰值 | `19.2 → 563.3 MiB` | `43.0 → 532.1 MiB` |
| RT GPU 增量（热，B0 4 ms） | `0.396–0.412 ms` | — |
| 驱动峰值增量（B0 256 MiB） | `76.7 MiB` | — |
| 画质（1080p sanity） | 变更 `1.857M` 像素、镜面 `0.0068→0.0095`（1.40×）、亮度 +29%、非有限 0 | — |
| 剥离审计 | 16 个 GlxDXR + 4160 个 probe keyword set | 0 / 0，0 条 probe 编译与查询库警告 |

冷启动尾部由首帧 PSO/着色器准备主导（max 389 ms），中位数已接近热态；首个采样帧即完成 TLAS 构建与 probe 发布，因此 RT 没有额外的稳定期延迟。

## 未覆盖

主机（PS5/XSX/XSS）构建/真机/批准；PIX/RenderDoc 级的 AS 精确尺寸与全帧 GPU 实测；R3/G3 的两条图像缝（另见 P0-4）。
