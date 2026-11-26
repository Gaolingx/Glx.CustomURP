# HDRP SSR / RTR 对照与缺口

**状态：** 调研完成，待排期
**日期：** 2026-11-26
**我方基线：** `52a2b78`（PBR Accumulation 接入 DXR 硬件补洞；本文所有我方行号以此提交为准）
**HDRP 基线：** `com.unity.render-pipelines.high-definition@14.0.12`，源码位于 `HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12`
**范围：** 不透明 SSR 的 Approximation / PBR Accumulation 与 HDRP RayMarching / RayTracing 的映射。透明 SSR、Volume、APV、LightCluster 细节不在本文。

## 0. 结论速览

1. HDRP 的「模式」是**每相机整体二选一**（RayMarching 或 RayTracing），不在同一帧内按像素混合 SSR 与 RT；而 RayTracing 的 **Performance** 模式内部本身就是「屏幕空间 RayMarch 优先、RT 只补 miss」的混合管线（Quality 模式不预追屏幕空间）。
2. 我们的 **PBR + DXR 补洞**在功能上对应 HDRP 的 **RayTracing + Performance**，但目前只实现了补洞：HDRP 无缝的原因是 SS 命中与 RT 命中**共用同一套 GBuffer + deferred lighting**；我们 SS 取前帧颜色金字塔、RT 用 `GlxHitLights` 简化灯表，**接缝的根因在这里**，不在权重公式。
3. **Approximation + DXR 补洞没有 HDRP 对应物**（HDRP 的 Approx 既无时域也无填洞），属自研，只能参考 RTR 的降噪/上采样策略。
4. 缺口按可排期顺序见 §4，排期建议见 §5。

## 1. HDRP 与当前管线存在的差异

### 一、HDRP 的结构：两种「模式」其实是三条路径

HDRP 的 [ScreenSpaceReflection.cs:372](file:///E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12/Runtime/Lighting/ScreenSpaceLighting/ScreenSpaceReflection.cs#L372) 里 `RayTracingActive = tracing != RayMarching`，[RenderSSR](file:///E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12/Runtime/RenderPipeline/HDRenderPipeline.LightLoop.cs#L1234) 处是**整体二选一**，不是逐像素混合：

| 路径 | 组成 | 时域 |
|---|---|---|
| RayMarching + Approximation | trace → reproject（预乘），无填洞 | 无 |
| RayMarching + PBR Accumulation | trace → reproject（3×3 十字）→ **Accumulate**（速度拒绝） | 指数累积，无空间滤波 |
| RayTracing + Performance | **DirGen(半分辨率 GGX+1/pdf) → RayBinning → 屏幕空间 RayMarch → 剩余射线 DispatchRays → AdjustWeight → ReflectionDenoiser → 上采样** | denoiser（时域+双边） |
| RayTracing + Quality | 逐像素 raygen，N samples/bounce | 同一个 denoiser |

关键发现：**HDRP 的 RT 模式自己就是「SS 射线优先、RT 补未命中」的混合管线**（[RayMarching.compute:107](file:///E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12/Runtime/RenderPipeline/Raytracing/Shaders/RayMarching.compute#L107) 命中后把 `direction.w` 置 0，raygen 跳过这些像素，只对 miss 派发 DXR），而且**没有预算概念**——所有屏幕空间 miss 都会追。我们的 R2 本质上就是这条路径的「固定 K 预算 + 轮转 + history 复用」变体。

### 二、逐阶段差异

| 阶段 | HDRP | 我们的 R2 | 差异性质 |
|---|---|---|---|
| 接收者判定 | G-buffer stencil `TraceReflectionRay` + **clear coat mask 纹理覆盖粗糙度** | depth-normals alpha 的 smoothness，**无 coat mask** | 真实缺口（coat 反射用错粗糙度） |
| 前端 | DirGen 每 2×2 一个方向 + RayBinning（八面体 256 bins 排序，提升相干性） | R1 分类 + tile 配额轮转 + scatter（固定 K） | 我们缺方向排序/压缩；这正是 R2b 计划要补的 |
| 追踪 | 统一 `RayMarch()` 共享 helper；SS 命中还要通过 **GBuffer 解码校验 + `rayLength`** | 同一 helper；命中只做深度厚度判定，无 rayLength（RT 侧硬编码 `tMax = 50`） | 缺长度上限/表面校验 |
| **命中着色** | SS 命中与 RT 命中**都写标准 GBuffer，再由同一 deferred lighting 着色**（一次 BRDF、真实阴影、灯表、clear coat） | SS 命中取**前帧颜色金字塔**；RT 命中在 raygen 里用 `GlxHitLights`（≤4 灯/2 阴影）直接算 | **最大质量缺口**：两者亮度/色温天然有缝，且我们的 RT 命中没有完整光照 |
| 时域 | RTR：统一 denoiser，每像素每帧都有值，无需复用；PBR SSR：速度拒绝累积 | PBR：denoiser + `R2Temporal` history 复用；Approx：无 denoiser，`R2Composite` 点展开 | 我们的 history 复用会带来额外滞后（预算轮转的代价） |
| 降噪/上采样 | `denoise` 可关；半分辨率先 `ReflectionRescaleAndAdjustWeight`（乘 smoothness fade 权重）再 denoiser，最后专用深度感知 `ReflectionUpscale` | 复用 SSGI 双边上采样，alpha 一起上采样 | 上采样实现不同，fade 权重位置不同 |
| 回退 | miss shader 内查 reflection probe / sky（`_RayTracingRayMissFallbackHierarchy`），另有 `lastBounceFallbackHierarchy`、APV | miss 写 alpha/flags = 0 → 材质端 `lerp(probe, radiance, weight)` | 等价；缺 APV、多 bounce 回退 |
| 采样 | 1spp（performance）/ N spp + 多 bounce（quality） | 1spp + 1 bounce | 缺多采样、多 bounce |
| 分辨率 | **SSR 固定全分辨率**；只有 RTR 有半分辨率 | 两算法都可半分辨率 | 我们是加法（也是边缘缝来源之一） |
| 调度 | SSR 可 async compute | 同步 | 性能项 |

### 三、两种模式各自的缺口（对照 HDRP）

**Approximation**
- 缺：无（HDRP 也没有时域/填洞；我们的 3×3 填洞和 DXR 补洞是自研增强）。
- 偏差：HDRP 写预乘 `(rgb·a, a)`，我们写 unpremultiplied + weight（文档已注明）。

**PBR Accumulation**
- 缺：**速度拒绝整套**（`speedRejectionParam` / `speedRejectionScalerFactor` / `speedSmoothReject` / `speedSurfaceOnly` / `speedTargetOnly` / `enableWorldSpeedRejection`）；**`accumulationFactor`**（HDRP 用 `pow(2, lerp(0,-7,f))` 控制收敛速度）。我们换成 RTR 的 denoiser（AABB clip + 样本计数），收敛语义与 HDRP 不同。
- 多出：空间双边滤波（HDRP 的 SSR 不做空间滤波）。
- 参数缺口：`denoise` 开关、`rayLength`、`clampValue`、`layerMask`、`textureLodBias`、`sampleCount`、`bounceCount`、`rayMiss`、`mode`(Performance/Quality)、`tracing`(RayMarching/RayTracing) 全都没有对应物。

### 四、结论

1. 我们现在的 PBR+DXR 组合，功能上对应 HDRP 的 **RayTracing + Performance**，但只买到了「补洞」：HDRP 那条路径之所以无缝，是因为 SS 命中和 RT 命中共用同一套 GBuffer + deferred lighting；我们的 SS 取前帧颜色、RT 用简化灯表着色，**缝的根因在这里**，不是权重公式。
2. HDRP 没有「Approx 算法 + RT 补洞」这种组合；我们的 Approx+R2 属于自研，HDRP 无法直接对标，只能参考 RTR 的降噪/上采样策略。
3. 若要对齐 HDRP 的质量上限，优先级建议：① RT 命中改走本帧 lighting（至少与 SS 命中的时间/光照近似对齐）→ ② clear coat mask → ③ speed rejection 或 `accumulationFactor` 替代/补充现在的 denoiser 时域 → ④ RayBinning 式方向排序（R2b 已规划）→ ⑤ 多采样/多 bounce。

## 2. 现有框架下可以做的改善

基于当前框架（PBR：R1→DXR→R2Temporal→reproject→SsrPbrHwMerge→denoiser→upsample；Approx：…→R2Composite），下面这些都能在**不动架构**的前提下单独切出来做，每项一个提交、一套证据。

### P0（小改动、低风险、直接消除已知缺陷）

| # | 目标 | 改动点 | 风险 | 验收 |
|---|---|---|---|---|
| P0-1 | **修复既有红测试**：`R1Quota` 用 floor，测试/文档按 ceil 推期望（K=11、6 tiles 时 served 6≠11） | [SsrRequestQueue.hlsl:69](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/SsrRequestQueue.hlsl#L69) 或测试二选一对齐 | 改代码会变调度节奏（每轮多服务 1 条/tile） | 4 个 R1 用例转绿；counts 守恒；收敛周期重新记录 |
| P0-2 | **PBR 也做 3×3 同表面填洞**（复用 Approx 的 `SsrFillMissedPixel`），减少对 HW 的依赖和空洞 | [ScreenSpaceReflections.compute:588](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute#L588) PBR 分支 miss 时调用 [:473](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute#L473) 的 helper | 低（helper 已带同表面/法线/距离测试） | 空洞像素数下降；无跨轮廓借色；R1 reason 分布不变 |
| P0-3 | **配置陷阱显式化**：RR 开了但 volume 是全分辨率/SSR 关闭时，运行时一次性告警（现在静默不生效） | [SsrRayTracingCameras.cs:32](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/SsrRayTracingCameras.cs#L32) 附近的调用方 + 新增一次 LogWarning | 低 | 只在配置冲突时打一次；正常配置零日志 |
| P0-4 | **RT 射线长度参数化**：把 R1 的硬编码 `tMax = 50` 换成 volume 参数（对齐 HDRP `rayLength`），并按相机远裁裁剪 | [SsrRequestQueue.hlsl:26](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/SsrRequestQueue.hlsl#L26)、[ScreenSpaceReflection.cs](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceReflection.cs)、SsrPass 上传 | 低 | 改参数后 R1Sample.tMax 与 DXR TMax 一致（测试断言） |
| P0-5 | **`denoise` 开关**（PBR）：关掉 temporal+bilateral 直接看原始信号，便于 A/B 与定位"是采样问题还是降噪问题" | [SsrPass.cs:595](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs#L595) + Volume 参数 | 低 | 关闭后 dispatch 只剩 trace/reproject/merge/upsample（FD 计数断言） |

**P0-1 落地（2026-11-26）：保留 `R1Quota` 的 floor。** ceil 在 K 不能被 tiles 整除时会超出 `_R1K`，`R1Scatter` 的预算裁剪会丢掉轮转顺序尾部的整块 tile（1080p 半分辨率 K=32768 时每帧约 20% tile 没有服务），与“每个 tile 每帧都能看到 K”的公平性目标冲突。红测试的期望生成器按窗口顺序、每 tile 8 条建模，和 `R1Rank`（lane 顺序取前 quota 条）不一致；改为镜像实现后 4 个用例转绿，调度与收敛周期（16 帧）不变。

P0-2 和 P0-3 建议先做：前者直接减少你看到的空洞和 HW 依赖，后者堵住"开了没生效"这类误判。

**P0 完成情况（2026-11-26）：** P0-2 已在 PBR 复投影中心 miss 时复用 `SsrFillMissedPixel`（同表面/法线/距离门槛不变；新单测同时验证同表面借色和深度台阶不借色）；P0-3 对 RR 开启但 volume 关闭/缺失或全分辨率的情况，每个 Game/Base 相机 episode 只告警一次；P0-4 新增 `rayLength`（默认 50，按相机远裁裁剪），R1 分类与 DXR 共用 `tMax`；P0-5 新增 PBR `denoise` 开关，关闭后 dispatch 只剩 trace/reproject/merge/upsample，重开时从冷历史开始。

### P1（需要一个小 plan，但仍在现有管线内）

| # | 目标 | 改动点 | 风险 | 验收 |
|---|---|---|---|---|
| P1-1 | **方向排序/压缩**（R2b 正题）：把 tile 轮转顺序改为按八面体方向分桶（HDRP RayBinning 思路），固定 K 不变 | SsrRequestQueue.hlsl 的 `R1Tiles/R1Scatter` + 新增 per-bin offset buffer | 中（调度与测试都要改） | 同 K 下 DXR marker ms 下降；图像逐字节不变或仅噪声级差异 |
| P1-2 | **时域语义对齐**：在 [ReflectionDenoiser.compute:193](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/Denoising/ReflectionDenoiser.compute#L193) 的 temporal 里加运动速度阈值拒绝，或引入 HDRP `accumulationFactor` 等价参数 | denoiser + Volume | 中（改收敛手感，需要重录基准） | 快速平移场景 ghosting 指标下降；静止场景不退化 |
| P1-3 | **接缝抑制**：merge 时对 HW 辐亮度做邻域 AABB clamp（用 SS 的 3×3 范围），或对 `reason 6` 边缘带不启用 HW | [SsrPbrHwMerge:413](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute#L413) | 中（可能压暗，需要调 nudge） | 球体倒影轮廓处像素跳变下降；整体亮度 mean 变化 ≤ 阈值 |
| P1-4 | **统一两算法的 HW 合并**：Approx 也改成 trace 分辨率 merge，删除全分辨率 `R2Composite`，共用一套上采样 | SsrPass + compute | 中（Approx 边缘要重验） | 两算法同一场景 FD 结构一致；Approx 边缘无块状；删除的 kernel 无残留引用 |
| P1-5 | **HW miss 沿射线方向回退**：miss 时按射线方向取探针/天空色，而不是直接丢权重（当前 [RayTracingSsr.raytrace:32](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/RayTracingSsr.raytrace#L32) 写 `GlxMiss` 后靠材质探针） | raytrace + `ReflectionProbeFallback.hlsl` | 中 | 粗糙表面样板（球体）反射与探针结果对比；无 NaN |

### 明确不做（属于大重构，不建议现在切）

- **RT 命中改走 deferred GBuffer 统一着色**（HDRP 无接缝的根本原因）——需要我们把命中着色改成"写 GBuffer + 一次 lighting"，等于新建一条 deferred RT 通路。
- multi-bounce / quality 模式 / N spp、async compute、APV 回退。
- clear coat mask：URP Lit 的 coat mask 目前没有现成的 GBuffer 通道，先不放进 P0/P1。

建议：可以先把 **P0-2 + P0-3** 落成具体 plan（含改动清单、测试与证据格式），或者直接开做；P1-1 建议等 R2b 那篇计划定稿后再动。
