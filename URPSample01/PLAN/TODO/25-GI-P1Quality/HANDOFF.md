# GI 探针 P1：距离矩窄瓣、Chebyshev 稳健化、灯光突变时序阻尼

**状态：** 实现完成并提交（`PASS*`：Editor 级门禁通过；设备 A/B 与 GPU ms 仍未取，见 §二）。
**依赖：** P0 轮（`ac1defc`：天空输入、距离矩方向、八面体接缝）已在同一 HEAD 上。
**范围：** 借鉴清单 P1 的 4、5、7；不含 bias/体积淡出（P1-6）、可视化（P2-8/9）、收敛冻结（P2-10）。

## 一、改动

### P1-4 距离矩窄瓣加权

`GlxProbeUpdate.compute` `ProbeDistance`：权重由 `saturate(dot)` 改为 `pow(saturate(dot), 64)`。

- 依据：RTXGI 的 `DDGIUpdateDistance.compute` 同款，余弦权重会把矩图糊到无法区分近/远命中，削弱查询端的 Chebyshev。
- **有意分歧**：保留本项目的 `(Σw·d, Σw·d²)/Σw` 归一化，不搬 RTXGI 的 `/2Σw`（那是它 `pow` 权重与 `2π` 解码约定的配对项）。
- 影响：距离 atlas 的数值会整体变化（同一天花板 `1.5×spacing`），A/B 必须按 atlas 原始字节比，不能拿新旧绝对值直接比。

### P1-5 Chebyshev 稳健化

`GlxProbeQuery.hlsl`：`chebyshev = max(chebyshev³, 0)`；权重先 `wrap × max(cheb, 0.05)`、`max(w, 1e-6)`，再按 RTXGI 的 `<0.2` crush（`w *= w³/0.2²`），最后乘 trilinear。

- 依据：RTXGI `DDGIFuncs.hlsl` 的查询路径。此前是裸 Chebyshev，弱光 probe 能从遮挡物后漏过来。
- **有意分歧**：保留“始终测试”而不采用 RTXGI 的 `distance > mean` 条件（那会额外改变背面/近距语义，留给单独一轮）；0.05 下限保留，避免完全被遮挡的方向被归零后重新归一化放大。
- 影响：遮挡边缘更硬、漏光减少；接收面与 probe 距离小于均值处的权重会因三次方略微变暗，A/B 要看整体亮度是否系统性下降（若下降明显，说明 floor 需要单列评估，而不是调 crush）。

### P1-7 灯光突变时序阻尼

`GlxProbeUpdate.compute` `ProbeIrradiance`：时序混合改为 RTXGI 的“gamma-5 存储域 + 变化检测”。

- 编码域混合：`enc = pow(max(E,0), 0.2)` 混合后 `pow(·, 5)` 还原。理由：RTXGI 的绝对阈值（0.2 / 1.0 / 1/1024）是在 gamma 空间标定的，本项目存线性 E，直接套会在高亮面上持续误触发。
- 规则照搬：`max(prevEnc - curEnc) > 0.2` → `hysteresis = max(0, hysteresis - 0.75)`；`Luminance(delta) > 1` → `delta *= 0.25`；变暗时 `lerpDelta` 夹在 `[1/1024, |delta|]`；基准 `hysteresis = _GlxProbePreviousWeight`（0.75）不变，便于与旧证据对照。
- 影响：**混合域从线性变为 gamma-5**，G2/G5 的阶跃/衰减数字不能沿用旧值，必须重测；固定点（常量场）不变。

## 二、门禁

| 门禁 | 结果 |
|---|---|
| D3D11 编辑器导入（`GlxProbeUpdate` / `GlxProbeQuery` 被 Lit/ComplexLit/GBuffer 变体引用） | **PASS 0 shader error** |
| G1 CPU EditMode（`RayTracingProbeMathTests`） | **PASS 14/14** |
| 整个 `Unity.RayTracing.Tests.Editor` | **PASS* 26/27**；唯一失败 `T2.ManualSceneKnownVectors`，隔离运行（暂存 P1）同样失败，判为既有问题 |
| 设备 A/B（见 §三） | **待取** |
| GPU ms | 未测 |

证据：[Tests-Assembly.xml](validation/G1/Win64-Editor/Tests-Assembly.xml)、[T2 隔离](validation/Isolation/T2-preexisting.xml)、[说明](validation/README.md)。实现期记录：FXC 无单参数 `max`，新增 `GlxMax3`（RTXGI 也自带 `Max` helper）。

**P1-4 的样本密度（解析估计，不是测量）：** `cos^64` 的有效支持约 ±25°（0.59 sr），默认 96 条 radiance 射线时每个 texel 每次更新约 4.5 条有效射线（RTXGI 112 条时约 5.3）。本项目每 probe 每 16 批才更新一次，所以矩图刷新比 RTXGI 慢一个数量级；`denominator > 0` 才写入的保护会让稀疏 texel 保留上一次有效值（不会被清零），但收敛更慢。若设备 A/B 看到距离图噪点，调 32/16 次方或提高 `probeRaysPerProbe` 都行，**不要**改回 `/2Σw` 归一化。

## 三、设备 A/B（用现有 SaveAtlas/ReadProbe）

同一场景、同一构建之外的上一 HEAD（P0）与新 HEAD 各一次；场景：常量天空 + 开放场 + 6 cm 薄墙 + 一盏可开/关的点光。

1. **P1-4**：`SaveAtlas` 距离图，取薄墙方向的 texel：新版的 `mean` 应更接近真实墙距（旧版被余弦糊低）；开放方向仍为 cap。用 `ReadProbe` 读同一 probe 的 moments 对比。
2. **P1-5**：薄墙背面接收点 `E` 与墙正面；旧版背面漏光更亮。再测“完全遮挡的 probe 角落”是否出现黑斑（floor 0.05 应阻止）。
3. **P1-7**：点光开/关阶跃，逐批 `ReadProbe` 记录 E：观察 0.2/1.0 阈值是否按预期降权、变暗时是否被 1/1024 下限拖住；关闭时间窗按旧 G5 的 256 批记录残差，但注明混合域已改，不与旧值做逐位比较。
4. 计数：`ReadCounters` 的 sky/hits/misses 不变；批次预算 ≤16×128 不变。

所有证据写入 `PLAN/TODO/25-GI-P1Quality/validation/<G2|G3|G5>/<platform>-<renderer>/`。
