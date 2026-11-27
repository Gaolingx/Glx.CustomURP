# 23 HANDOFF：SS/RT 统一着色（P3 前置）

**更新时间：** 2026-11-27
**基线上限：** `350d18c`（P0 的 fixture 与证据均为其后新增文件）
**当前阶段：** P3 B7/B8 审计修正已落地（2026-11-27，代码/静态；像素待 B6.1）——SSR 221 / 绑定 222（严格晚于 Decal 220）、225 可用性门控、reason 6 保留硬件补集、225 环境源接 A2/A3；B8 又补齐 SS 命中核的阴影变体并让 P3 强制运行 RT 命中核。**B6 的 `p3` 证据因此失效**（`p1` 回滚哈希不变），门禁 1/2/3/5 需重测；`ssrAfterGbuffer` 仍默认关闭。B6 原始结论见下文；证据在 [validation/P3](validation/P3/README.md)。

## 已完成

| 交付 | 位置 | 状态 |
|---|---|---|
| 调研 | [research.md](research.md) | 完成 |
| 计划（P0–P3） | [plan.md](plan.md) | 完成 |
| 契约冻结（C1–C8） | [contracts.md](contracts.md) | 完成 |
| 布局 tripwire | [SsrP3ContractTests.cs](../../../Assets/Tests/RayTracing/SSR/SsrP3ContractTests.cs) | 完成（4/4 通过，[ContractTests.xml](validation/P0/tests/ContractTests.xml)） |
| Fixture 构建器 | [SsrP3BaselineSetup.cs](../../../Assets/Tests/ScreenSpaceLighting/Editor/SsrP3BaselineSetup.cs) | 完成 |
| Player 捕获器 | [SsrP3BaselineCapture.cs](../../../Assets/Tests/ScreenSpaceLighting/Runtime/SsrP3BaselineCapture.cs) | 完成 |
| Fixture 资产 | `Assets/Tests/RayTracing/SSR/Fixture/` | 已生成 |
| D3D12 基线 | [validation/P0](validation/P0/README.md) | 已捕获，三变体两进程逐位一致 |
| A 决策锁定 | [plan.md](plan.md) §2 | 完成（2026-11-27） |
| P1 契约（C2.7 记录） | [contracts.md](contracts.md) / `Shaders/RayTracing/SsrHitSurface.hlsl` | 完成（tripwire 5/5，[ContractTests.xml](validation/P1/tests/ContractTests.xml)） |
| P1 命中表面传输 | `GlxBasicPayload` 64B / `RayTracingSsr.raytrace` 写 `_R2Surfaces` | 完成（7/7 @D3D12，payload 实测 64B） |
| P1 命中延迟着色 | `SsrHitDeferred.compute` + `SsrPass.DispatchHitDeferred` | 完成（默认关，待 B4 开关 / B5 构建验证） |
| P1 开关与回退 | `hitDeferredShading` / `RayTracingSceneInstances.ReportUnsupportedSurface` / 构建器资源解析 | 完成（默认关；不支持材质注册期报错并跳过） |
| B5 构建 + 证据 | `validation/P1/Win64-D3D12` 与 `Win64-D3D11` | 完成（构建 0 错误；双 API 各变体两进程 SHA 一致） |

## 已锁定决策（2026-11-27）

1. **阴影**：接受 HDRP 同款 `SHADOW_LOW`。hit 着色单 tap 阴影图比较，不做 PCF/Poisson/dither；与光栅的质量差计入 P2 容差，不作为缺陷追。
2. **材质**：只支持标准 PBR 字段；SSS/transmission/anisotropy 不提供、不设 per-material 回退，注册期报错并跳过，命中按 miss（probe/sky）。禁止第二条着色路径（含 closest-hit 近似）。写入 contracts C2.7（B1 落地）。
3. 未定项时间点：GBuffer 8bit/16F → P2 A/B；P3 一帧跳变、历史捕获时机 → P3 开工前。

## 关键结论（P0 实测）

- 工程的 Windows API 是 Automatic（实际只编 D3D11）。Player 若用 `-force-d3d12` 启动而构建时没带 D3D12，计算着色器 kernel 会缺失：`Clustered deferred fell back to Forward`、`Kernel 'KDepthPyramidCopy' not found`。构建器现在显式设置 D3D11+D3D12，并在 `finally` 中先还原显式列表、再还原 Automatic 标志（`SettingsRestore.json` 的 `apisRestored = true`），工程文件逐字节回到提交值。
- 工程默认管线是 Forward。Player 构建若不在构建期把 fixture 的 Deferred 管线设为默认/所有质量档，URP 会剔除 Deferred GBuffer 变体，画面变成"天空盒 + 全黑几何"。构建器已在 `try/finally` 中处理并恢复。
- 原始基线场景没有任何 `SsrRayTracingGeometry`，DXR 混合会静默不启用（`hwMergeDispatches = 0`）。fixture 已给 14 个网格注册稳定实例 ID。
- 共享 `Ground.mat` 的 smoothness 是 0.25，本来不产生 SSR；fixture 使用独立的 0.95 smoothness 地面材质，不修改共享资产。
- 构建器修正 API 后已重建 Player 并重录六份证据（三变体 × 两进程），`frame.raw` SHA256 与冻结值逐位一致，说明修正只影响 kernel 可用性、不改变画面。
- 基线（121 次渲染，`firstUnityFrame = 1`）：
  - `base` `96b9c197…`，`nodenoise` `693c54de…`，`ssroff` `1295029d…`；同变体两进程 SHA 完全一致。
  - `hwMergeDispatches = 1`（base/nodenoise），`traceDispatches = 1`；`ssroff` 全 0。
  - `base−ssroff` 地面 ROI：meanAbs 8.4e-4、p99 0.030、2.5% 像素 > 0.01。
  - `base−nodenoise` 地面 ROI：meanAbs 4.0e-4、p99 0.003、max 5.93（少量高光/萤火虫）。
  - 无 NaN/Inf。

## B2 关键结论（实现期发现）

- **Unity 光追管线不允许材质 `RayTracing` pass 的 closest-hit 写 UAV**（6000.3 修复说明 UUM-141971：not supported，编译器报错；2022.3 上属"可能崩"）。HDRP 14 同样不这么做：`RayIntersectionGBuffer` 经 payload 回传，由 raygen 写 `_GBufferTexture*RW`。
- 因此反射表面只能经 ray payload 回传：`GlxBasicPayload` 24 → **64 字节**（发布结果仍在 @0–23，`_R2Hits`/`_GlxResults`/`_GlxProbeResults` 全部保持 24B），raygen 调 `SsrBuildHitSurface` 写 80B 记录。shadow/probe 模式不填 @24–63。
- 传输布局避开了对齐歧义：`float2` 八面体法线放在 32（4B/8B 对齐都一致），两个 `float3` 跟随发布头证明的 4B 打包。
- `SsrHitSurface` 按请求序号索引（`DispatchRaysIndex().x`），消费者用 `_R1Queue[i].slot` 映射像素槽；未命中/无效槽写 version 0，无需逐帧清空 80B 缓冲。

## B5-fix2 关键结论（RenderDoc）

- B5-fix 的固定颜色二分失效：kernel 从未运行，debug 0/1/2 自然逐位相同。
- 抓帧事实（D3D12，`validation/P1/RenderDoc/`）：`SSR Hit Shading` 标记区间没有 dispatch（本帧 33 个 dispatch，少的就是 hit kernel）；`_R2Hits` 洋红 0 条，只有 raygen 的 legacy 值。
- pass 诊断：120/120 次调用 `DispatchCompute`（kernel 0、512 组、settings/compute 正常）→ 丢弃发生在执行侧。
- 根因：`SsrHitDeferred.compute` 经 `GlxProbeSampling.hlsl` 无条件声明 GI 探针图集；SSR-only fixture 不跑探针生产者、全局资源未绑定，Unity 报 `Property (_GlxProbeIrrSnapshot) ... is not set` 并**不提交派发**。B5 误把该告警当成"已派发"的证据。
- 修复：`DispatchHitDeferred` 在 `rayTracingProbeFallback` 关闭时绑占位资源（黑 1×1 + 队列缓冲，`_GlxHitLowFrequencySource == 0` 时不会被采样）；回退开启时沿用生产者的全局图集。
- 修复后：D3D12 base `96b9c197` → `2f713f17`（两次一致）；debug=1 `7cbbe4a5`；抓帧里 `SSR Hit Shading` 出现 dispatch（1416），`_R2Hits` 洋红 11,950 条；D3D11 `7fb3e01b` 不变；无 NaN、无 "not set" 告警。
- 仍 OPEN：缝指标与性能（B6）；hit kernel 的环境光 `_GlxHitAmbientIrradiance` 与阴影关键字尚未接上。

## B6 关键结论（验收度量）

- 阴影并非缺失：CS 反汇编确认 comparison sampler + main/additional shadowmap 已绑定（关键字由阴影 pass 的全局状态继承）。
- 环境光确实缺失：`_GlxHitAmbientIrradiance` 原无写入点。现按 `RenderSettings.ambientProbe` 六方向平均 ×π 写入，仅 hybrid 队列存在时写（D3D11 保持 `7fb3e01b`）。本 fixture 环境光≈0，故在此场景惰性。
- 指标（`validation/P1/seam_metric.py`）：`_R1Original.reason==2` 为 SS 区（8,146 槽位），`reason==4/6` 为 RT 域（179,079），ROI 内 SS↔RT 相邻 560 对，2×2 块亮度差。
  - base：0.011384（P0）→ 0.011382（P1），median 不变；洋红对照 0.012774（指标敏感）。
  - nodenoise：0.012394 → 0.013947（+12.5%），median +0.6%，与局部梯度对照（≈0.0137）齐平。
  - 整帧 ROI P0→P1 meanAbs 1.4e-6。
- 判读：旧 closest-hit 本就用真实阴影射线 + top-4 灯 + 自有 BRDF，与单 tap 阴影图 + 全量灯 + CdShade 在本场景高度一致；残余缝由历史/内容/denoiser 主导。`nodenoise` 的 +12.5% 属 A 决策接受的阴影口径差。
- 回归：D3D12 base `b1e0a889`（两次一致，无 NaN）；D3D11 `7fb3e01b`（重建后首次运行出现过一次 ULP 离群 `484aafb6`，随后 4/4 复现基线）。
- 结论：**P1 缝验收 NOT MET**。要继续要一份能区分两条路径的测例（金属/粗糙命中面、多附加灯、阴影中的命中）或由 owner 按"执行+一致性"签收。性能 ≤0.3 ms 仍 OPEN。

## P3 B6 关键结论（统一复测，2026-11-27）

- **构建**：首个 Player 构建暴露 225 核 D3D11 编译失败（compute include 下 `unity_MatrixInvVP` 未声明），修复后 0 错误、4 变体全编（commit `19e819a`）。静态序列/计时能力随后加入捕获器（`-p3-static N`：先 N 帧纯计时、再 N 帧逐帧 ROI 回读）。
- **管线顺序（RenderDoc，D3D12 nodenoise）**：`Hit Shading (RT)` 1549→1560、`(SS)` 1562→1571、`Indirect Specular` 1610→1618、`Clustered Deferred` 1620→1630；225 真正 dispatch 且先于 230。D3D11：无 DXR marker，225 仍 dispatch（815）先于 230（820）。`_R2Hits` flags `2=RT`、`10=SS`（8,107–8,167 槽）。
- **确定性**：12/12 组 run-01=run-02 逐位一致；D3D12 rollback base 仍为 `b1e0a889`，D3D11 三变体仍为 `7fb3e01b/1bfd98e9/8ab8e569`。
- **ssroff 等价比**：P3 与 P1 在 SSR 关闭时双 API 逐位相同——225 的环境项单次计数无漂移。
- **门禁**：1 缝 NOT MET（median −9.1%，mean +18.1%；洋红/关闭对照证明 median 被内容主导）；2 PARTIAL（边界 median 0.00215 luma）；3 静止方差 NOT MET 字面（4.2×/13.6× 基线，绝对 std ≤5.9e-6，无漂移）；4 PASS（无 NaN、回退哈希一致）；5 PARTIAL/OPEN（中位数 proxy 在 +0.5 ms 内，GPU 计时器存在 923 ms / 1.8e12 ms 无效样本，逐 pass 耗时未测）；6 PASS。
- **D5 #4**：一帧跳变整帧 meanAbs 2.14e-4、最差 block 0.0338（阈值 1e-3 / 0.05）→ PASS；D3D11 6.3e-5 / 0.0240 → PASS。
- **D5 #5**：300 帧无 ghost 漂移；但 P3 ROI 方差高于 P1，按字面记 NOT MET，不称“已修复”。

## P3 B7 审计修正（2026-11-27，B6 之后）

B6 审计发现四个缺口，已按“代码先行、像素待 B6.1”落地：

- **Decal 顺序**：Decal GBuffer 也注入 220，若 SSR 同在 220，先后取决于 feature 注入顺序（fixture 无 decal，证据不覆盖）。SSR 移到 **221**、材质绑定 **222**，严格晚于 decal；225 不变。
- **静默丢镜面**：`_GlxSsrAfterGbuffer` 原只按开关发布，225 若因缺 kernel/目标 RT 提前返回，GBuffer 又已去镜面 → 整帧反射消失。现由 `ScreenSpaceLightingSsrIndirectSpecularPass.CanExecute`（settings/compute/kernel/目标 RT/延迟 GBuffer/深度）决定是否发 1，并在首次降级时告警一次。
- **reason 6 补集**：SS 命中覆盖同槽 RT 后，`R2Temporal` 原把硬件历史清零，导致屏幕边缘 `requestShare` 补集被丢弃、覆盖率掉到 `edge×fade`。现 SS 仍不进硬件历史，但 `requestShare > 0` 时复投影硬件历史，合并核 `s·SS + hw·HW` 归一化；已知限制：当帧新服务的 RT 辐射仍被 SS 覆盖。
- **环境源 A2/A3**：225 原来只采 `_GlossyEnvironmentCubeMap`（= `ReflectionProbe.defaultTexture`），而 P1 材质侧用 `unity_SpecCube0` 或探针混合 → 探针场景会换源且未被测。现按 `ssrIndirectSource` 选 A1/A2/A3；A2 仅在 Setup 成功绑定已发布图集时启用 `_GLX_SSR_PROBE_ENV`，沿反射方向取 `GlxProbeQuery` 辐照度 ×1/π，按 `probeCoverage` 与天空混合（无 roughness mip/盒投影）。默认仍 A1；A2 需探针夹具验证。

影响：`Win64-*/p3/*` 的 B6 证据对 B7 HEAD 失效，门禁 1/2/3/5 需在 B6.1 重测；`p1` 回滚哈希应保持不变。契约测试新增事件/门控/补集/A2 断言与两个命中核的编译 smoke test，结果落 [validation/P3/tests/ContractTests-B7.xml](validation/P3/tests/ContractTests-B7.xml)。

## P3 B8 审核补充（2026-11-27，B7 之后）

- **SS 命中核缺阴影变体**：`SsrHitSs.compute` 只声明了 `_GBUFFER_NORMALS_OCT`，共享着色在缺 `_MAIN_LIGHT_SHADOWS(_CASCADE)`/`_ADDITIONAL_LIGHT_SHADOWS` 时把阴影项编译成常量 1（`ClusteredDeferred.hlsl` 的 `!defined(CD_MAIN_LIGHT_CALCULATE_SHADOWS)` 分支），于是 SS 命中无阴影、RT 命中单 tap 阴影——直接违反 D3 的 SHADOW_LOW 同口径，也是门禁 2 未解释差的候选来源（B6 的 RenderDoc 反汇编只检查了 RT 核）。现补齐同一组变体，tripwire 对两个核同时断言。
- **P3 只统一了一半**：`ssrAfterGbuffer` 不依赖 `hitDeferredShading`，开关关闭时 SS 侧走核、RT 侧保留 closest-hit；fixture 靠显式打开才规避。现 `DispatchHitDeferred` 使用 `hitDeferredShading || ssrAfterGbuffer`，P3 下两个命中核无条件运行，`Player.json` 的 `hitDeferredSettings` 记录生效值。
- 影响：P3 路径的阴影中反射会变化，B6 的 `p3` 证据继续失效；D3D11 无 SS 核、行为不变。门禁 2/3 应受益，仍需 B6.1 重测。

## 未完成 / 门禁

| Gate | 状态 | 说明 |
|---|---|---|
| P0 fixture 生成 | CLOSED | `SsrP3BaselineSetup.CreateFixtureMenu` |
| P0 Player 构建 | CLOSED | `BuildPlayerMenu`；显式 D3D11+D3D12，D3D12 运行，构建后工程设置精确还原 |
| P0 两进程复现 | CLOSED（D3D12） | 三个变体均逐位一致 |
| P0 契约测试 | CLOSED | `ContractTests.xml`：4/4 通过（payload 24B、R1Sample 80B、队列 stride、Pass 206） |
| R1 计数器 | CLOSED | 捕获帧内异步回读：187225 / 179076 / 11952 / 167124 |
| DXR dispatch 直接证据 | CLOSED | TLAS 14 实例、0 禁用子网格、121 次构建（`Player.json`） |
| D3D11 回退基线 | CLOSED | 首次录制，三变体两进程逐位一致；tlas 0、R1 全 0 |
| P1 验收（SS/RT 缝下降） | **NOT MET** | 0.011384 → 0.011382（base）；nodenoise +12.5%；洋红对照证明指标敏感。见 [P1 README](validation/P1/README.md#b6环境光阴影补齐--ssrt-边界度量) |
| P1 性能（≤0.3 ms） | **OPEN** | 未测；需 Player FrameTimingManager 或带 GPU counter 的抓帧 |
| P3 实施计划 | CLOSED（2026-11-27） | [P3-PLAN.md](P3-PLAN.md)：接收者重路由（GBuffer3 不再写间接镜面）、220→221 注入点、SS 读当前 GBuffer、契约/tripwire、B0–B7；复测统一在 B6/B6.1 |
| P3 B0 选型 + harness 开关 | CLOSED（2026-11-27） | D1=A1（`_GlossyEnvironmentCubeMap`，不碰 `unity_SpecCube0`）；feature 增 `ssrAfterGbuffer`/`ssrIndirectSource`；捕获器增 `-p3-ssr-after-gbuffer`/`-p3-ssr-rollback`/`-p3-ssr-indirect`；`Player.json` 记 requested 与 feature 回读 |
| P3 B1 契约先行 | CLOSED（红，2026-11-27） | contracts.md C1/C2/C2.7/C8 更新；`SsrP3ContractTests` 新增 4 条 P3 断言 + C2.7 gbuffer 源（预期红，B2–B4 转绿）；legacy 206 断言保持绿；未改渲染行为 |
| P3 B2 接收者重路由 | CLOSED（build/静态，2026-11-27；像素在 B6） | B2a SSR 事件 206↔220（B7 改为 221）；B2b GBuffer3 去间接镜面（`GlobalIlluminationDeferredDiffuse` + `_GlxSsrAfterGbuffer`）；B2c 225 `SsrIndirectSpecular` + specular RT + 资源/builder；B2d `_GLX_SSR_SPECULAR` 变体在 230 相加（避免未绑定声明丢 dispatch）；B2e 材质绑定（B7 改为 222）。`ssrAfterGbuffer` 关时不 enqueue 绑定/225、不启用变体，P1 路径不变 |
| P3 B4 公共着色函数 | CLOSED（build/静态，2026-11-27） | `SsrShadeSurface.hlsl`（`SsrShadedSample` + `SsrShadeSurface`）由 RT 命中核与 B3 SS 命中核共用；closest-hit 因 per-draw 头无法包含 compute 头，仅作 P1 回退（契约 C8 已修订） |
| P3 B3 SS 读当前 GBuffer | CLOSED（build/静态，2026-11-27；像素在 B6） | 新增 `SsrHitSs.compute`（trace 全屏读当期 GBuffer 构面，`source=gbuffer`，调 `SsrShadeSurface`，写 `_R2Hits` 带 SS 位）；`R2Temporal` 跳过 SS；`SSR_REPROJECT` 经 `_GLX_SSR_SHADE_SS` 关键字读 `_R2Hits` 与 `ssShare` 覆盖率替代前帧色；D3D11 无队列时 keyword 关、回落前帧色 SS |
| P3 B5 编排与清理 | CLOSED（build/静态，2026-11-27） | marker 统一 `SSR Hit Shading (RT)` / `(SS)`；tripwire 增 `SsrP3_PassOrderIsMonotonic`（B7 起为 221<222<225<230）与 `SsrP3_SwitchDefaultsOffAndKeywordsDeclared`（`ssrAfterGbuffer=false`、两处关键字、marker 名）；回滚 206 路径保留为现役参考 |
| P3 B7 事件与门控 | CLOSED（代码/静态，2026-11-27） | SSR 221 / 绑定 222（严格晚于 Decal 220）；`CanExecute` 门控 `_GlxSsrAfterGbuffer`，225 不可用时 GBuffer 保持 P1 写镜面并首次告警 |
| P3 B7 覆盖率与环境源 | CLOSED（代码/静态，2026-11-27） | `R2Temporal` 对 SS 槽复投影硬件历史以保留 reason 6 补集；225 按 `ssrIndirectSource` 支持 A1/A2/A3，A2 受 `_GLX_SSR_PROBE_ENV` 与已发布图集约束 |
| P3 B8 阴影与强制命中核 | CLOSED（代码/静态，2026-11-27） | `SsrHitSs.compute` 补齐 `_MAIN_LIGHT_SHADOWS(_CASCADE)`/`_ADDITIONAL_LIGHT_SHADOWS`；`DispatchHitDeferred` 在 `ssrAfterGbuffer` 下无条件运行 |
| P3 B6.1 重测（P3 路径） | **OPEN** | B6 的 `p3` 证据对 B7/B8 HEAD 失效；门禁 1/2/3/5 需重跑，A2 需探针夹具；`p1` 回滚哈希用于回归 |
| P3 B6 构建（shader 变体真编） | CLOSED（2026-11-27） | 首次构建 14 个 D3D11 错误（225 核 `unity_MatrixInvVP` 未声明）；修复后 0 错误、`SsrIndirectSpecular` 4 变体全编（`19e819a`） |
| P3 B6 执行/顺序（RenderDoc） | CLOSED（2026-11-27，B7 之前的 HEAD） | D3D12：RT/SS 命中核各 1 dispatch，225 dispatch 先于 230；D3D11：无 DXR，225 仍先于 230；`_R2Hits` flags 2/10 计数见 [CaptureAnalysis](validation/P3/RenderDoc/CaptureAnalysis.json)。B7 改动事件号后需重抓 |
| P3 B6 门禁 1（缝） | **NOT MET** | median 0.002364→0.002149（−9.1%）、mean +18.1%；threshold 50%；洋红 mean +7.6%、关闭对照 median 更低 → 内容主导 |
| P3 B6 门禁 2（SS/RT 一致） | **PARTIAL** | 边界 median 0.00215 luma（ROI 均值 0.17%）；缺配对反射口径与阈值 |
| P3 B6 门禁 3（静止方差） | **NOT MET（字面）** | 4.2×/13.6× 基线；绝对 std ≤5.9e-6 luma，无漂移 |
| P3 B6 门禁 4（NaN/回退） | **PASS** | 24/24 无 NaN；D3D11 三变体与 D3D12 base 冻结哈希一致 |
| P3 B6 门禁 5（性能） | **PARTIAL / OPEN** | 中位数 proxy 在 +0.5 ms 内；FrameTimingManager GPU 样本有 923 ms / 1.8e12 ms 无效值，逐 pass 与真 GPU 门禁仍 OPEN |
| P3 B6 门禁 6（证据） | **PASS** | 双 API×两进程×三变体×两路径 24 组；哈希/PNG/计数器/设置恢复齐备（raw 与 .rdc 按政策留本机） |
| P1 命中着色执行 | CLOSED | 根因：未绑定探针图集导致 Unity 丢弃 dispatch；已修复并双 API 回归（见 [P1 README](validation/P1/README.md#b5-fix2renderdoc-定位根因已修复)） |
| P1 契约（C2.7 + `SsrHitSurface`） | CLOSED | 80B / 5 字段 / version 1 已冻结，tripwire 5/5 |
| P1 hit GBuffer + 延迟着色核 | CLOSED | B3 核 + B4 开关/回退完成；B5 构建验证 + 证据 |

## 如何跑

```powershell
$U = "D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe"
$P = "E:\GitHub\Glx.CustomURP\URPSample01"

# 生成 fixture（快）
& $U -batchmode -quit -projectPath $P -executeMethod SsrP3BaselineSetup.CreateFixtureMenu -logFile "$env:TEMP\ssr-p3-fixture.log"

# 构建 Player（会临时把 fixture 管线设为默认 + 所有质量档，然后恢复）
& $U -batchmode -quit -projectPath $P -executeMethod SsrP3BaselineSetup.BuildPlayerMenu -logFile "$env:TEMP\ssr-p3-build.log"

# 捕获（每个变体两个全新进程）
$exe = "$P\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation\P0\Player\SsrP3Baseline.exe"
foreach ($v in "base","nodenoise","ssroff") {
  foreach ($r in "run-01","run-02") {
    $out = "$P\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation\P0\Win64-D3D12\$v\$r"
    & $exe -force-d3d12 -p3-output $out -p3-variant $v -p3-run $r
  }
}

# 对比
python "$P\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation\P0\compare.py"

# RenderDoc 抓帧（可选 -p3-warmup N 拉长渲染窗口，便于中段触发；默认 120）
& renderdoccmd capture -w -c "$out\capture" -d "$P\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation\P0\Player" `
  "$exe" -force-d3d12 -p3-output "$out" -p3-variant base -p3-hit-debug 1 -p3-warmup 900

# P3 路径抓帧：-p3-ssr-after-gbuffer 打开接收者重路由（SSR 221 / 绑定 222 / 合成 225 / 230 相加）。
# 现役 marker： "SSR Hit Shading (RT)"、"SSR Hit Shading (SS)"、"SSR Indirect Specular"、"SSR Material Binding"。
# 回滚对照用 -p3-ssr-rollback。上一行 legacy 抓帧里的 "SSR Hit Shading" 已更名为 "SSR Hit Shading (RT)"。
& renderdoccmd capture -w -c "$out\capture-p3" -d "$P\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation\P0\Player" `
  "$exe" -force-d3d12 -p3-output "$out" -p3-variant base -p3-ssr-after-gbuffer -p3-warmup 900

# B6 测量批次（双 API × {p1,p3} × 三变体 × 两进程）与静态 300 帧（D5 #5 方差 + 门禁 5 计时）。
# 证据落 validation/P3/<Win64-API>/<p1|p3>/<variant>/<run>；静态落在 validation/P3/Static。
$V = "$P\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation"
& $exe -force-d3d12 -p3-output "$V\P3\Static\d3d12\p3\base" -p3-variant base -p3-ssr-after-gbuffer -p3-run static-300 -p3-static 300
python "$V\P3\compare_p3.py"

# 契约测试（-runTests 不能带 -quit，否则导入后直接退出、不产出 XML）
& $U -batchmode -nographics -projectPath $P -runTests -testPlatform EditMode `
  -testFilter SsrP3ContractTests `
  -testResults "$P\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation\P1\tests\ContractTests.xml" `
  -logFile "$env:TEMP\ssr-p3-b1-tests.log"
```

## 下一阶段（P3 收口）入口

1. 门禁 1/3 已按字面记 NOT MET：若要继续，先换一个有判别力的测例（金属/粗糙命中面、多附加灯、阴影中的命中）并冻结新基线，别在同一夹具上调阈值。
2. 门禁 5 需真 GPU 时间：给捕获器加逐 pass GPU counter（或改用带 counter 的 RenderDoc/DXGI 方案），当前 FrameTimingManager 的 gpuFrameTime 在本机不可信。
3. `ssrAfterGbuffer` 在门禁全绿前保持默认关闭；回滚 206 路径继续作为现役像素参考，不删除。
