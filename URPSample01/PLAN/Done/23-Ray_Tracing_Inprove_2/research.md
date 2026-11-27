# 23 调研：SS 命中与 RT 命中统一着色（HDRP Mixed 语义）

**状态：** 调研完成，P0 开工
**日期：** 2026-11-26
**本轮基线：** `350d18c`（工作树干净）
**HDRP 基线：** `com.unity.render-pipelines.high-definition@14.0.12`，源码在 `HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12`

## 0. TL;DR

1. 上一轮（[22 号调研](../22-Ray_Tracing_Inprove/research.md)）把"RT 命中改走 deferred GBuffer 统一着色"列为"明确不做"。本轮要做的正是它，并且比它更进一步：**P3 把 SSR 从 206 挪到 GBuffer(210) 之后、ClusteredDeferred(230) 之前，SS 命中读当前帧 GBuffer 重新着色**，即 HDRP 的 Mixed 语义。
2. HDRP 无缝的原因不是权重公式，也不是某个降噪参数，而是**SS 命中和 RT 命中写进同一个虚拟 GBuffer，由同一个 deferred 光照核着色**。SSR 的"颜色混合"在 HDRP 里根本不存在。
3. 只把 RT 命中改成"写 GBuffer + 延迟着色"是**半根治**：SS 命中仍采前帧颜色金字塔，SS/RT 边界还剩"时间差 + 采样方式"残差。要根治必须让 SS 命中也走 hit GBuffer。
4. 统一着色**不解决**可见性毛刺（二值 hit、1spp 噪声、轮廓锯齿）。那些仍要靠 coverage 软化、射线锥 mip、命中点时间累积单独处理。
5. 我方已有大半基础设施：Deferred 渲染器 + 自研 `ClusteredDeferred.compute`（compute 里可调用 `CdShade`）、URP GBuffer 打包 helpers、RT closest-hit 已取到 albedo/normal/metallic/smoothness/emission/顶点色。缺口集中在：hit GBuffer 输出、hit 光照核、按 hit 位置取灯、shadow map 采样、specular probe、pass 挪位。

## 1. 问题分层

截图里圈的波浪带和上一轮讨论的"梳齿"不是同一类缺陷，必须分开验收：

| 层 | 表现 | 根因 | 统一着色能否解决 |
|---|---|---|---|
| A. 着色口径缝 | SS 块与 RT 块、RT 块与 probe 块之间亮度/色温突变 | 两条路径用不同光照模型（SS 采前帧最终色；RT 用 ≤4 灯/2 条二值阴影射线；probe 是另一套） | **能**，前提是 SS 与 RT 共用同一套 hit 表面数据 + 同一光照核 |
| B. 可见性毛刺 | 反射轮廓处的须状/梳齿、逐像素跳变 | Hi-Z 二值命中 + 掠射角厚度判定 + 1spp 随机 GGX | 不能 |
| C. 材质范围差 | decal、light layer、SSS/transmission、AO 在 RT 命中上缺失 | GBuffer 不携带这些通道 | 只能按支持矩阵回退 |

本轮 P3 只负责 A。B 按[上一轮结论](../22-Ray_Tracing_Inprove/research.md)的 coverage/mip/命中点累积路线单独排期。

## 2. HDRP 14.0.12 的实现

### 2.1 帧内顺序（RayTracing + Performance）

| 步骤 | 内容 | 关键文件 |
|---|---|---|
| 1 | DirGen：每像素生成反射方向，`w = 1/pdf`，mirror 用 `w = 1` | `Runtime/RenderPipeline/Raytracing/Shaders/Reflections/RaytracingReflections.compute` |
| 2 | 可选屏幕空间预 march（Mixed）：命中的像素**直接把表面数据写进同一个 GBuffer**，方向 `w = 0` 跳过 DXR | `Shaders/RayMarching.compute:145-164` |
| 3 | RayBinning：16×16 tile、八面体 16×16 方向分桶，一维压缩 | `Shaders/Common/RayBinning.compute` |
| 4 | DispatchRays：closest-hit 跑材质 shader，写虚拟 GBuffer + Distance | `Shaders/Deferred/RaytracingGBuffer.raytrace`、`ShaderPassRaytracingGBuffer.hlsl:5-51` |
| 5 | RaytracingDeferred：逐 hit 像素解码 GBuffer，用 RT 光照核着色 | `Shaders/Deferred/RaytracingDeferred.compute:57-228` |
| 6 | AdjustWeight（仅 smoothness fade）→ ReflectionDenoiser → Upscale | `Shaders/RaytracingReflectionFilter.compute:37-120`、`HDReflectionDenoiser.cs` |
| 7 | 结果作为 `_SsrLightingTexture` 由反射像素的 BSDF 消费，余量回 fallback hierarchy | `Runtime/Material/Lit/Lit.hlsl:1823-1888` |

### 2.2 虚拟 GBuffer 内容

- GB0：baseColor(sRGB) + specularOcclusion
- GB1：八面体法线 + perceptualRoughness
- GB2：fresnel0(sRGB) + `PackFloatInt8bit(coatMask, GBUFFER_LIT_STANDARD, 8)`
- GB3：emissive + baked
- Distance：`RayTCurrent()`；unlit 为 −1，sky 为 0（`RayTracingCommon.hlsl:70-86`）
- **不写** motion、tangent/bi-tangent、light layer、stencil、decal 数据

### 2.3 光照与阴影

- 用的是 ray tracing 版 `LightLoop`（`Shaders/RaytracingLightLoop.hlsl`），灯来自 **世界空间光线追踪 light cluster**（64×64×32 cell，`Shaders/RayTracingLightCluster.hlsl`），按 **hit 位置**查询（`RaytracingLightLoop.hlsl:78/177/244`），不是屏幕 FPTL tile。
- 阴影是**常规 shadow map**（cascade atlas + punctual/area atlas），强制 `SHADOW_LOW`（`RaytracingDeferred.compute:11-18`：高/超高过滤需要屏幕位置，RT 上下文拿不到），不使用 contact shadow，也不在反射射线里追光追阴影。
- 环境光走 reflection hierarchy（sky/probe），按权重混合。

### 2.4 支持范围与回退

- 支持：Lit 家族（含 clear coat 近似）、LayeredLit、LitTessellation、TerrainLit、Unlit；ShaderGraph 经 `FitToStandardLit` 压平。
- 不支持：SSS、transmission、anisotropy、iridescence、decal、light layer、stencil。
- **没有 per-material 回退**：shader 没有 `GBufferDXR` pass 时，命中按 miss 处理，直接回 miss hierarchy（probe/sky）。

### 2.5 与 SSR 的关系（关键）

- `RenderSSR` 在 `tracing != RayMarching` 时只调 `RenderRayTracedReflections`，**根本不跑 SSR compute**。
- Mixed 模式不是颜色混合：屏幕空间 march 命中的像素写 GBuffer 并 `w = 0`；RT 只追剩余射线；两条路径的命中都由同一个 `RaytracingDeferred` 核着色。
- 只有 smoothness fade 作为权重（`ComputeWeightValue`），probe 补 `1 - alpha` 的余量。
- Mixed 要求 lit 模式为 Deferred（`RaytracingReflection.cs:359`）。

### 2.6 已声明的限制

- 虚拟 GBuffer 是 8bit（GB0 sRGB8、GB1/2 UNorm8）。
- `posInput` 的 tile/linearDepth 对光线起点未初始化，命中着色不能依赖。
- 无 decal、light layer、stencil 数据。
- half-res 时 source pixel 直接 `halfResCoord * 2`，无重投影/棋盘校验。

## 3. 我方现状（`350d18c`）

### 3.1 Pass 顺序

- SSL 的 SSR pass 在 `AfterRenderingPrePasses + 6` = **206**（[SsrPass.cs:31](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs#L31)），只读 depth/normals prepass + **前帧**颜色金字塔。
- GBuffer pass = **210**，ClusteredDeferred 光照 = **230**（[UniversalRenderer.cs:367](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/UniversalRenderer.cs#L367)）。
- 所以当前 SSR 运行时**当前帧 GBuffer 还不存在**，这正是 P3 要挪位置的原因。

### 3.2 两条命中路径的着色口径

- SS 命中：在 `SsrReadOriginalColor` 里对**前帧颜色金字塔**按 roughness 取 mip（[ScreenSpaceReflections.compute:216](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute#L216)），得到的是"上一帧已经着色完的最终色"，含直射、间接、probe、自发光。
- RT 命中：closest-hit 里 `InitializeBRDFData(albedo, metallic, 0, smoothness)` + `GlxShadeLights`（[GlxLitHit.hlsl:138](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/RayTracing/GlxLitHit.hlsl#L138)）。`GlxShadeLights` 从 64 项灯表里按亮度**只取前 4 盏**，最多追 **2 条二值阴影射线**（[GlxHitLights.hlsl:88](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/RayTracing/GlxHitLights.hlsl#L88)）；diffuse 环境用低频 irradiance，**specular 环境缺失**。
- 因此同一张桌子上的反射，SS 块与 RT 块的亮度/色温天然不同；R1/R2 的权重公式再准也只是把两个不同的量线性混合，这就是"缝"。

### 3.3 混合与降噪

- R1 分类 → 固定 K 预算轮转 → DXR → `R2Temporal`（历史复用）→ 复投影 → `SsrPbrHwMerge` → ReflectionDenoiser → 上采样。
- 合成权重：`hw = min(1 - ss, requestShare, history.a)`，`a = s + hw`，输出**非预乘** radiance + 覆盖率 alpha（[ScreenSpaceReflections.compute:414](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute#L414)）。
- Denoiser 只做时域累积/AABB clip/双边滤波（[ReflectionDenoiser.compute:112](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/Denoising/ReflectionDenoiser.compute#L112)），它现在实际上在替两条着色路径"缝合"。
- RT 资格要求 `!ssr.fullResolution`（[SsrRayTracingCameras.cs:39](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/SsrRayTracingCameras.cs#L39)），所以 P0 fixture 必须用半分辨率才能覆盖 DXR 路径。

### 3.4 ABI 与布局（本轮要冻结）

- `R1Sample`：80 字节，16 个 32bit 标量/float3 字段（[SsrRayRequest.hlsl](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/SsrRayRequest.hlsl)）。
- `R2` 命中：24 字节 payload（radiance float3 @0、hitT @12、flags @16、mode @20），硬上限 64 字节；`_R1Counts` 11×4 字节。
- tile：8×8；预算 K 上限 32768。

## 4. 目标方案与我方缺口

### 4.1 P3 目标（HDRP Mixed 语义）

1. SSR pass 注入点从 206 移到 **GBuffer(210) 之后、ClusteredDeferred(230) 之前**。
2. SS 命中不再采前帧颜色，而是**读当前帧 GBuffer 的命中像素**，取出表面数据（albedo/normal/roughness/metallic/emissive），走与 RT 命中相同的 hit 光照核重新着色。
3. RT 命中改为写虚拟 hit GBuffer（不再在 closest-hit 里直接算辐射），由同一个核着色。
4. denoiser 与权重只负责噪声与覆盖率，不承担"缝合"。
5. probe/sky 只是覆盖率余量的 fallback，不再是"另一条着色路径"。

### 4.2 差距清单

| 缺口 | 现状 | P3 需要 | 可复用 |
|---|---|---|---|
| hit GBuffer 输出 | payload 只有 radiance | closest-hit 写 GB0/1/2/3 + distance，或在 206 之后新增一个 GBuffer 输出缓冲 | RT 材质 shader 已取全部表面数据 |
| hit 光照核 | closest-hit 内联简化光照 | compute 读 hit 表面 → BRDF → 光照，与 ClusteredDeferred 同一套 | `ClusteredDeferred.compute` + `CdShade` 可 compute 调用 |
| 按 hit 位置取灯 | 64 项灯表 top-4 | 世界空间 cluster（HDRP 式）或全灯表（P1 过渡） | 自研 cluster builder 已有 GPU 位图/紧凑表，但**按屏幕 tile 查**（`ClusteredDeferred.hlsl:651`），不能按 hit 世界坐标查 |
| 阴影 | 2 条二值 DXR 阴影 | 采样 shadow atlas（SHADOW_LOW 口径） | URP shadow 参数已全局绑定 |
| 环境光 | 仅 diffuse | diffuse + specular probe/sky | `GlxProbeSampling.hlsl`、probe atlas |
| SS 命中的表面数据 | 前帧颜色 | 当前帧 GBuffer（pass 挪位后才存在） | — |
| 材质范围 | Lit 全支持（简化光照） | 定义支持矩阵 + 不支持材质的回退（现 closest-hit 着色或 probe） | — |

### 4.3 主要风险

1. **时间语义**：SSR 挪到 GBuffer 之后，SS 命中变成"当前帧着色"，而 denoiser 历史是按接收器复投影的；历史里存的是上一帧的已着色辐射。静止场景无差；运动场景可能出现一帧的亮度跳变。P3 必须重录并给出接受阈值。
2. **GBuffer 可用性**：URP Deferred 的 GBuffer 在 210 写入、230 才被消费；SSR 插在中间是可行的，但必须保证不破坏 GBuffer 内容（只读、不绑定为 UAV）。
3. **光照一致性范围**：世界空间 cluster 是新的 GPU 生产者，必须与按像素的可见性/阴影同源，否则只是把缝换了位置。P3 验收必须包含"同表面光栅 vs hit 着色"的逐点对照。
4. **精度**：8bit GBuffer 是 HDRP 的折衷；我方可以考虑 16F，但变体与带宽要实测。
5. **变体与回退**：D3D11 无 DXR，必须完整回退到现有路径；材质不支持时回退路径要显式。
6. **透明 SSR**：transparent 路径仍用 Approximation + 前帧颜色；P3 不应改变透明语义。

## 5. 参考文件清单

- HDRP：`RaytracingReflection.cs`、`RaytracingDeferredLightLoop.cs`、`RaytracingDeferred.compute`、`RaytracingGBuffer.raytrace`、`RaytracingLightLoop.hlsl`、`RayTracingLightCluster.hlsl`、`RaytracingReflectionFilter.compute`、`ShaderPassRaytracingGBuffer.hlsl`、`Lit.hlsl`。
- 我方：`SsrPass.cs`、`ScreenSpaceReflections.compute`、`SsrRequestQueue.hlsl/.cs`、`SsrRayRequest.hlsl`、`GlxLitHit.hlsl`、`GlxHitLights.hlsl`、`ClusteredDeferred.hlsl/.compute`、`UniversalRenderer.cs`、`ReflectionDenoiser.compute`。
