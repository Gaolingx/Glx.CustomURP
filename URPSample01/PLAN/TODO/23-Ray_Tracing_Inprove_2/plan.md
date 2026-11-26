# 23 实施计划：SS 命中 / RT 命中统一着色（P3 = HDRP Mixed 语义）

**状态：** P0 完成（基线 `2a38d40`，契约已冻结）；P1 需先确认 §2 决策
**日期：** 2026-11-26
**目标提交：** `350d18c` 之后
**契约：** [contracts.md](contracts.md)（P0 冻结，P1–P3 按版本位放宽）

## 0. 目标与非目标

### 目标

1. 消除 A 类缝：同一条反射在 SS 命中、RT 命中、probe 兜底三条路径上得到一致的光照结果。
2. 最终形态（P3）：SSR 在 GBuffer 之后、ClusteredDeferred 之前；SS 命中与 RT 命中**共用同一个 hit 表面记录和同一个光照核**；denoiser 与权重只处理噪声与覆盖率。
3. 全过程保持非预乘 radiance + 覆盖率 alpha 的材质契约不变，材质端只做一次 BRDF。

### 非目标（本轮及后续阶段都不做）

- 二值可见性毛刺（coverage 软化、射线锥 mip、命中点时间累积）——单独排期。
- multi-bounce、quality 模式、N spp、async compute、APV。
- decal、light layer、SSS/transmission、光追阴影进入反射射线。
- 修改 Approx 算法与透明 SSR 的现有语义。

## 1. 阶段划分

### P0：基线 + 契约冻结（本轮实施）

**原则：** 不改任何渲染行为，只冻结"切换前"的夹具、输出、阈值与 ABI。

| # | 交付 | 位置 | 完成判据 |
|---|---|---|---|
| P0-1 | 独立 fixture 资产：场景、Volume、Deferred 渲染器、Pipeline | `Assets/Tests/RayTracing/SSR/Fixture/` | 场景可从干净工程重建；`ScreenSpaceLightingBaseline` 原场景不被修改 |
| P0-2 | 捕获器：固定相机、120 帧预热、第 121 次渲染取 RGBA16F 线性帧 + 调试 PNG + JSON | `Assets/Tests/RayTracing/SSR/`（runtime + editor） | 两个全新 Player 进程同 API 逐位一致；记录 API/GPU/supportsRayTracing/feature 配置/dispatch 计数 |
| P0-3 | 契约文档 + 布局 tripwire 测试 | [contracts.md](contracts.md)、`SsrP3ContractTests.cs` | 测试覆盖 R1Sample 80B、R2 24B/64B cap、8×8 tile、206 注入点；改布局必须改测试 |
| P0-4 | 基线证据 | `validation/P0/Win64-D3D12/` | base / nodenoise / ssroff 三个变体各两进程；SHA 一致；ROI 差异记录 |
| P0-5 | Freeze 清单 | `validation/P0/Freeze.json` | 提交、场景/资产 blob、相机、分辨率、API、种子、阈值全部冻结 |

**P0 变体（同一场景、同一相机、不同进程）：**

- `base`：PBR Accumulation、半分辨率（DXR 资格要求）、denoise 开、rayLength 50、bias 0.5、min smoothness 0.9。
- `nodenoise`：同上，denoise 关。用于区分"着色缝"与"采样噪声"。
- `ssroff`：SSR 关（纯 probe/sky）。用于给出"SSR 贡献 = base − ssroff"的参考。

### P1：RT 命中写虚拟 GBuffer + hit 延迟着色（半根治）

| 内容 | 说明 |
|---|---|
| hit 记录 | closest-hit 除现有 24B payload 外，写 `SsrHitSurface`（GB0/GB1/GB2/GB3 + distance），版本位标识；shadow/probe 模式不写 |
| 着色核 | 新增 `SsrHitDeferred.compute`：解码 hit 表面 → `CdShade` 同一套 BRDF/主光/附加光/阴影 |
| 灯与阴影 | 过渡方案：现有 64 灯表全量遍历（去掉 top-4 截断）+ shadow atlas 采样；不做世界 cluster |
| 环境光 | diffuse + specular probe/sky（补 `GlxProbeSampling`） |
| 回退 | 无 GBuffer 支持的材质回退到现 closest-hit 着色；D3D11 完整回退现路径 |

**验收：** 同场景 base 帧的 SS/RT 边界亮度差（ROI）显著下降；无 NaN/全黑；D3D11 不回归；性能增量 ≤ 0.3ms（RT 单元未饱和，见 [SSR光追非瓶颈实测结论]）。

### P2：世界空间 light cluster + 材质矩阵（对齐 HDRP 口径）

| 内容 | 说明 |
|---|---|
| 世界 cluster | 64×64×32 等价物，按 hit 世界坐标查询；与 ClusteredDeferred 的屏幕 tile cluster 并存 |
| 材质矩阵 | Lit/Layered/Terrain/Unlit 支持；SSS/transmission/anisotropy 显式回退 |
| 计数器 | Player 可见的 R1 requested/served/overflow/hit/miss + 每命中灯数/阴影射线条数 |
| 精度 | 8bit vs 16F GBuffer 的带宽/质量 A/B |

**验收：** hit 着色与光栅同表面逐点对照（同灯、同阴影、同 probe）误差 ≤ 阈值；计数器闭环。

### P3：SSR 挪到 GBuffer 之后，SS 命中读当前帧 GBuffer（根治）

| 内容 | 说明 |
|---|---|
| 注入点 | `206` → `AfterRenderingGbuffer`（>210 且 <230）；transparent 路径不动 |
| SS 命中 | 读命中像素的当前帧 GBuffer，取表面数据，走 P1 的同一 hit 光照核；不再采前帧颜色金字塔 |
| 合成 | `ssShare/hwShare` 只表示覆盖率与 smoothness/edge fade，不表示"两条着色路径的折中" |
| Denoiser | 输入语义不变（radiance + weight），只保留噪声/历史职责 |
| 回滚开关 | Volume/feature 级开关切回 P0 路径（前帧颜色采样 + 现 closest-hit），用于 A/B 与故障隔离 |

**验收（全部为阻断项）：**

1. 同一 fixture、同 API：base 帧 ROI 的"SS/RT 边界亮度跳变"≤ P0 基线的 50%，且 probe 边界无新增跳变。
2. `nodenoise` 帧：SS 块与 RT 块的均值/色温差 ≤ 阈值；不得用 denoiser 掩盖。
3. 时间稳定性：静止相机 300 帧内 ROI 亮度方差 ≤ 基线；运动相机无新 ghost。
4. 无 NaN/Inf；D3D11 回退路径与 P0 逐位一致（或仅噪声级）。
5. 性能：整帧增量 ≤ +0.5ms，SSR 链增量 ≤ +0.4ms；GPU 时间与 P0 对比记录。
6. 证据：双 API、两进程、原始线性帧 + 哈希 + 计数器 + 设置恢复。

## 2. 关键决策点（owner 确认后才能进 P1/P3）

1. 阴影口径：接受 HDRP 同款 `SHADOW_LOW`（快、无屏幕空间 dither），还是要求全质量（成本显著上升）？
2. 材质范围：SSS/transmission/anisotropy 是否统一回退到现 closest-hit 着色？
3. GBuffer 精度：沿用 8bit（HDRP 同款）还是 16F？
4. P3 是否接受"运动场景一帧亮度跳变"这一已知代价（SS 命中从时间延迟变为当前帧）？
5. 是否允许 P3 改变 SSR 的历史捕获时机（会影响透明 SSR 与 Editor 运动验证）？

## 3. 证据与命名

统一遵循 [20 号证据布局](../../Done/20-Ray_tracing/validation/README.md)：

```
PLAN/TODO/23-Ray_Tracing_Inprove_2/validation/<ID>/<platform>-<renderer>/<variant>/run-XX/
```

- 每个 run 至少包含：`Player.json`、`frame.raw`（线性 RGBA16F）、`frame.png`（审查用 tonemap）、`Summary.json`。
- 另外在 round 根保留 `Freeze.json`、`README.md`、各阶段 `HANDOFF.md`。
- 失败、阻塞、未复跑照常入库；不得只留通过记录。

## 4. 回滚

- P1/P2：`SsrHitDeferred` 作为独立 kernel，开关关闭时完全走 P0 路径。
- P3：保留 P0 路径为 feature 级 fallback；切换只改注入点与命中着色核，不改 R1/R2 调度与输出契约。
