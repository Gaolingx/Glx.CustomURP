# HDRP 14.0.12：SSGI / RTGI / RTR 如何在屏幕和世界空间之间接力

2026-11-24｜本地源码研究，不是性能实测。复查包：`E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12`。`RT/` 表示包内 `Runtime/RenderPipeline/Raytracing/`；`SSL/` 表示 `Runtime/Lighting/ScreenSpaceLighting/`；`U/` 表示本项目 `URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/`。行号针对该快照，升级后按符号重查。

## 对照：相同的“先屏幕”，不同的命中数据

| 路径 | 命中拿到什么 | 局限与借鉴 |
|---|---|---|
| 当前 URP SSGI / opaque SSR | 本帧 Hi-Z 求 hit UV，以命中处 motion 读上一帧颜色；GI 有命中点深度拒绝，SSR 的历史取色不等于 GI 的检查 | 205/206 早于 GBuffer，三种 renderer 都能走。历史颜色含相机着色，不是纯漫反射或世界空间材质；出屏和历史拒绝是两个不同原因 |
| HDRP Performance + Mixed | 有效屏幕交点从**本帧相机 GBuffer**解出材质，标记该方向已解决；未解决方向走 RTAS，写同构命中 GBuffer；随后统一延迟着色 | `mixedTracing` 依赖 Deferred，不是把 SSGI/RTR 颜色 alpha 混合。屏幕路线能看到部分只在光栅侧表达的材质/形变，但不能恢复屏外表面 |
| HDRP Quality | 从 raygen 进入 `IndirectDXR`；closest-hit 求材质和光照，可按设置增加采样或反弹 | 是另一种成本/质量选择，不能用“屏幕步数更多”替代；不作为 URP v1 目标 |
| 本轮 URP 选择 | 屏内保留现有颜色路径；反射离屏命中单独着色后返辐亮度；GI 离屏由低频动态 probe 估计 | 不要求四张 hit GBuffer、完整 HDRP LightLoop 或 Surface Cache。SS/HW 光照来源不同，必须验证亮度接缝；probe 是接收点估计，不是假装精确的射线交点 |

## 先分清三条 HDRP 路线

| HDRP 路线 | 入口 | 何时适用 | 对本项目 |
|---|---|---|---|
| SSGI（纯屏幕） | `GlobalIllumination.tracing = RayMarching` | 不要求 RT 硬件；屏外无信息 | 已有等价实现，是探针回退的宿主 |
| RTGI（Performance / Mixed / Quality） | 同一 Volume 的 `tracing = RayTracing` | 需要 RTAS；Mixed 只在 Deferred | 借“未解决方向才追 RT”的调度思想，不搬 GBuffer |
| RTR（Ray Traced Reflections） | `ScreenSpaceReflection.tracing = RayTracing` | 需要 RTAS；不透明与透明分开 | 借“SS 分类后补追”的调度与降噪，不搬 HDRP 光簇 |

`Runtime/Lighting/GlobalIllumination.cs:9–31,349–351` 定义 `Off/ScreenSpace/RayTraced/Mixed` 与 `RayTracingActive`；`SSL/ScreenSpaceGlobalIllumination.cs:5–13` 定义同一组 `IndirectDiffuseMode`。HDRP 14 的 SSGI 与 RTGI 不是两个独立效果，而是同一 GI Volume 的不同 tracing 分支。

### HDRP SSGI 的数据流

| 阶段 | 行为 | 证据 |
|---|---|---|
| Trace | 半分辨率或全分辨率 Hi-Z 射线步进，输出命中点屏幕坐标；miss 写无效 | `SSL/ScreenSpaceGlobalIllumination.compute:26–29` kernels；`SSL/HDRenderPipeline.ScreenSpaceGlobalIllumination.cs:177–204` |
| Reproject | 用命中点 motion 回投上一帧 color pyramid；无效则走 fallback | `SSL/ScreenSpaceGlobalIllumination.compute:281–308` |
| Fallback | 顺序是 **APV（若启用）→ Reflection Probes → Sky**；最后写 `(radiance, weight)` | 同上 `287–307`；`SSL/RayMarchingFallbackHierarchy.cs:7–27` |
| 时域/空间 | GI 有独立 history 有效性；半分辨率时用 bilateral upsample | `SSL/HDRenderPipeline.ScreenSpaceGlobalIllumination.cs:332–371` |
| 消费 | 在延迟光照/材质阶段作为间接漫反射输入，miss 保持 baked GI | 与 URP 的 `(radiance, weight)` + `lerp(bakedGI, …)` 同构 |

关键事实：

- SSGI 的 fallback **不是**“所有 miss 都采天空”；`rayMiss` 是显式枚举，`None` 表示保留 baked GI。
- APV 只在 `_EnableProbeVolumes` 且编译了 `PROBE_VOLUMES_L1/L2` 时参与 SSGI fallback；它仍是烘焙 SH，不是动态探针。
- HDRP 的 SSGI 与 RTGI 共用 GI history 的有效性框架，但 Quality 分路会自己派发射线，不等同于“屏幕步数更多”。

### RTGI：Mixed 是 Deferred 专属的“屏幕未解决才追”

| 模式 | 命中数据 | 约束 |
|---|---|---|
| Performance | 屏幕步进 + 混合追踪；未解决方向进入 RTAS | `RT/HDRenderPipeline.RaytracingIndirectDiffuse.cs:51–96` 设置 `mixedTracing = tracing==Mixed && litShaderMode==Deferred` |
| Mixed | 屏幕命中写 GBufferDXR，未解决方向由 raygen 追 RTAS，随后统一延迟着色 | 依赖 Deferred 的 GBuffer 表示，不能直接套到 URP Forward |
| Quality | 从 raygen 直接进入 `IndirectDXR`；closest-hit 求材质/光照，可增加采样或反弹 | `RT/HDRenderPipeline.RaytracingIndirectDiffuse.cs:410–434`；递归与成本高于 v1 |

HDRP 的 16×16 ray binning 是在 tile 内按方向分桶，改善相干性；它不是“按全屏 miss 数间接启动同数量线程”的 GPU 间接派发。`RT/HDRenderPipeline.RaytracingDeferredLightLoop.cs:22,145` 可证 tile 尺寸与内核选择。

### RTR：SS 与 HW 共存的边界

- RTR 有独立射线方向、有效性调整、时域 history 与空间降噪；允许 GI/RTR 使用不同的 `rayMiss` 策略（`RT/HDRenderPipeline.RaytracingReflection.cs:349–395`）。
- 反射降噪先写时域 history，再做空间滤波；不能把最终模糊结果回灌历史。
- 不透明与透明反射是两条路径；透明接收面能发射反射，不等于透明几何能作为可靠命中目标。
- 屏幕命中采的是**相机着色后的颜色**，HW 命中着色是**命中点材质 + 最小直接光**；亮度接缝必须单独验证，不能只看“非黑像素”。

## 版本化源码索引

| 文件与行（包内相对位置） | 已核实行为 |
|---|---|
| `Runtime/Lighting/GlobalIllumination.cs:9–31,349–351` | `Off/ScreenSpace/RayTraced/Mixed` 与 `RayTracingActive` |
| `SSL/HDRenderPipeline.ScreenSpaceGlobalIllumination.cs:25–30,177–204,266–278,332–371` | SSGI kernels、半分辨率/fullRes、fallback 参数、bilateral upsample |
| `SSL/ScreenSpaceGlobalIllumination.compute:26–29,281–308` | Trace/Reproject kernels；APV → probes → sky 的 fallback 与 `(color, weight)` |
| `SSL/RayMarchingFallbackHierarchy.cs:7–27` | `ReflectionProbesAndSky / ReflectionProbes / Sky / None` |
| `RT/HDRenderPipeline.RaytracingIndirectDiffuse.cs:51–96,283–295,417–434,472–536,554–569` | RTGI Performance 方向生成、混合追踪、重建；Quality 分路。HF 时域+空间滤波，可选独立 LF history 第二遍。miss 层级来自设置，sky 可用 ambient probe |
| `RT/HDRenderPipeline.RaytracingReflection.cs:349–395,410–432,571–597,626–640` | RTR 独立方向、有效性调整、不透明降噪、必要时上采样；允许不同的 miss 策略（天空用方向 cubemap）；Quality 直接派发，不与 GI 共用历史 |
| `Runtime/Lighting/ScreenSpaceLighting/RayMarching.hlsl:75–137`；`RT/Shaders/RayMarching.compute:105–163` | 深度层级步进、厚度、距离/stencil 判定，屏幕命中写材质/距离，`direction.w=0`，不是采 SceneColor |
| `RT/HDRenderPipeline.RaytracingDeferredLightLoop.cs:238–306,337–375`；`RT/Shaders/Common/RayBinning.compute:50–94` | 可选屏幕追踪 → 可选 16×16 tile 内按方向分桶 → GBufferDXR → 延迟光照。分桶是在 tile 内改善相干性，不等于按全屏 miss 数间接启动同等数量的 DXR 线程 |
| `RT/Shaders/Deferred/RaytracingGBuffer.raytrace:59–95,114–140`；`RT/Shaders/Deferred/RaytracingDeferred.compute:87–150,174–208` | 已解决的方向退出；其余从接收面追 RTAS。由距离重建命中位置再跑光照；miss 按层级回退，不是所有 miss 必采天空。没有依赖屏幕 `TMin` 续传 |
| `Runtime/RenderPipeline/ShaderPass/ShaderPassRaytracingIndirect.hlsl:6–47,89–128`；`RT/HDReflectionDenoiser.cs:104–114,131–176` | Quality 的命中材质/光照与可选后续射线；反射降噪先写时域 history 再做空间滤波。不要把最终模糊结果再回灌 |
| `Documentation~/snippets/tracing-modes.md:5–42`；`RT/HDRaytracingManager.HDRTASManager.cs:152–166` | Mixed 依赖 Deferred 的屏幕材质表示；光栅形变、贴花、屏内粒子与 RT proxy 可不一致。透明接收面能发射与透明几何能当 GI/反射目标是两回事 |
| `U/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute:140–213`；`U/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute:184–215,229–288,393–492` | 当前 GI/SSR 屏幕命中/历史及 fallback，最终 alpha 已包含其他权重，不能反推原始 miss |
| `U/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsgiPass.cs:217–235`、`SsrPass.cs:214–249`（同目录）、`ColorPyramidPass.cs:85–90,133–188`（同目录） | 无所需历史会整 pass 跳过；用于不透明 SSR 的历史在透明物之后捕获，不等于 AfterSkybox 本帧金字塔。切镜要有独立的 fallback 路径 |
| `U/ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingBlend.hlsl:16–52,61–87`；`U/Shaders/LitGBufferPass.hlsl:215–226` | 接收面只做一次 BRDF；镜面输入非预乘、强度调权重，GI 强度调颜色；Deferred 在写 GBuffer 时消费。不能在最终颜色上再加 RT |

## 最佳实践、明确不搬与落点

可借鉴的最佳实践（按本项目需要排序）：

1. **先屏幕后世界**：只在屏幕路线真实失败的分支请求 HW；不要把整个效果改成逐像素 DXR。
2. **追踪与着色分离**：trace 只产出命中/方向/距离/样本标识，shading 在独立 pass 完成；便于预算、负样本和亮度对比。
3. **每个效果独立 history**：GI 与反射不共享屏空间历史；HW 来源切换要降低 history 权重，禁止把空间模糊结果回灌。
4. **miss 必须显式**：区分几何 miss、历史拒绝、未服务/无预算、无效材质；只有可信的远处 miss 才允许直接采天空。
5. **半分辨率是常规选项**：GI/反射都支持半分辨率 + 双边/深度法线引导上采样；先证正确性再评估 binning。
6. **命中材质需要专用入口**：HDRP Lit 等材质带有 `LightMode = "RayTracing"` 的 pass；Unity 的 closest-hit 不是凭空读材质。
7. **预算先冻结**：HDRP 的采样数、反弹数、ray length 是 Volume 参数；本项目在 B0 冻结，不用“画面看起来可以”代替预算。

明确不搬：

- HDRP Deferred 的四张命中 GBuffer 与 LightLoop；URP v1 只做最小 Hit Lighting。
- HDRP Quality 的递归/多跳、Surface Cache、APV 数据结构。
- HDRP 的 LightCluster / ray binning kernel；URP 的灯光与带宽模型不同。
- “HDRP 只在脏帧 Build TLAS”之类的结论。HDRP 的 manager 与实例管理不是 URP 公共 API；TLAS 的构建策略以 [TLAS.md](TLAS.md) 的实测接口为准。
- HDRP 的 APV 作为本项目动态 GI 出处。APV 是烘焙 SH；动态更新探针另见 [GI 研究](../gi/report.md)。
- HDRP 的 RTGI 不是本项目动态更新探针的出处；它解决的是逐像素间接漫反射，不是 probe 网格更新。
- UE 的压缩/间接 dispatch 不是 Unity 2022.3 能力；2022.3 只有 CPU 固定宽度 `DispatchRays` + GPU 早退。
- 屏幕假命中不会触发 DXR，需单测 hit rejection；未解决不得自动升级为天空。低预算、TMax 截断、未入库实例、近层透明物和采样稀疏造成的局限须显式标记。

对 URP v1 的落点：

- 先做 TLAS 与共用着色的已知射线 smoke，再分别开发 [GI](../gi/HANDOFF.md) 与 [反射](../reflection/HANDOFF.md)。
- 屏内继续用现有 Hi-Z SSGI / SSR；只在**未解决样本**上接入 HW。
- 反射 HW 命中返回 `(unpremultiplied radiance, weight)`，与现有 `lerp(probe, radiance, weight)` 契约一致；详见 [SSR-DXR.md](SSR-DXR.md)。
- GI 的屏外低频用世界空间探针，探针在材质端按接收点 `P/N` 查询；不让 HW 假装成精确交点。详见 [GI 研究](../gi/report.md)。
- 需要新增材质侧 `RayTracing` pass 的只有**进入 TLAS 且可被命中**的 Lit/自发光子集；不支持材质显式排除并计数。
- 主机（PS5/Xbox）不因 HDRP 文档或 PC 结果自动成立；按 SKU 单独验收，见 [总计划](../HANDOFF.md)。
