# P0 冻结契约：非 Forward+ 探针 atlas 开关

> 冻结日期：2026-11-16
> 结论：采用方案 A（复用探针 atlas + 暴力遍历），以下语义作为 P1–P5 的实现与验收依据。

## 1. 开关

| 项 | 结论 |
|---|---|
| 字段 | `ScreenSpaceLightingSettings.publishProbeAtlas` |
| 默认值 | `false`（opt-in） |
| 作用域 | 仅非 Forward+（Forward / Deferred） |
| Forward+ | 始终发布 atlas 与 cluster 列表，开关不生效 |
| 运行时切换 | 支持，下一帧生效；关闭时停止更新但保留已分配 atlas，避免反复分配抖动 |
| 关闭时成本 | 不创建 `ReflectionProbeManager`，无 atlas 显存、无每帧上传、行为与改动前逐字节一致 |

## 2. `rayMiss` 回退语义（开关开启后的非 Forward+）

| `rayMiss` | 改动前（非 Forward+） | 开关开启后 |
|---|---|---|
| `None` | 无回退，材质保留 baked GI | 不变 |
| `Sky` | ambient SH | 不变（ambient SH） |
| `ReflectionProbes` | 默认 cubemap，weight = 1 | 可见探针列表；无覆盖时 weight = 0，材质保留 baked GI（**行为变化**） |
| `ReflectionProbesAndSky` | 默认 cubemap，weight = 1 | 探针列表，权重不足 1 的余量补 ambient SH（**行为变化**：天空从方向性 cubemap 变为 SH） |

## 3. 验收边界

- 开关关闭：Forward / Deferred 输出与改动前逐字节一致；`reflectionProbeManager == null`；atlas 未创建。
- 开关开启：`urp_ReflProbes_Count > 0` 时 SSGI miss 可命中局部探针；0 探针时按上表回退。
- Forward+：开关 off/on 输出逐字节一致，cluster 路径无回归。
- 不修改非 Forward+ 的 `cullingParameters.reflectionProbeSortingCriteria`（暴力遍历不依赖顺序，避免改变引擎逐对象探针选择）。
