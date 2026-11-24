# HDRP 14.0.12：RTGI / RTR 如何在屏幕和世界空间之间接力

2026-11-24｜本地源码研究，不是性能实测。复查包：`E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12`。下表 `RT/` 表示包内 `Runtime/RenderPipeline/Raytracing/`；`U/` 表示本项目 `URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/`。行号针对该快照，升级以符号重查。

## 对照：相同的“先屏幕”，不同的命中数据

| 路径 | 命中拿到什么 | 局限与借鉴 |
|---|---|---|
| 当前 URP SSGI / opaque SSR | 本帧 Hi-Z 求 hit UV，以命中处 motion 读上一帧颜色；GI 有命中点深度拒绝，SSR 的历史取色不等于 GI 的检查 | 205/206 早于 GBuffer，三种 renderer 都能走。历史颜色含相机着色，不是纯漫反射或世界空间材质；出屏和历史拒绝是两个不同原因 |
| HDRP Performance + Mixed | 有效屏幕交点从**本帧相机 GBuffer**解出材质，标记该方向已解决；未解决方向走 RTAS，写同构命中 GBuffer；随后统一延迟着色 | `mixedTracing` 依赖 Deferred，不是把 SSGI/RTR 颜色 alpha 混合。屏幕路线能看到部分只在光栅侧表达的材质/形变，但不能恢复屏外表面 |
| HDRP Quality | 从 raygen 进入 `IndirectDXR`；closest-hit 求材质和光照，可按设置增加采样或反弹 | 是另一种成本/质量选择，不能用“屏幕步数更多”替代；不作为 URP v1 目标 |
| 本轮 URP 选择 | 屏内保留现有颜色路径；反射离屏命中单独着色后返辐亮度；GI 离屏由低频动态 probe 估计 | 不要求四张 hit GBuffer、完整 HDRP LightLoop 或 Surface Cache。SS/HW 光照来源不同，必须验证亮度接缝；probe 是接收点估计，不是假装精确的射线交点 |

## 版本化源码索引

| 文件与行（包内相对位置） | 已核实行为 |
|---|---|
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

## 移植决策与风险

1. **沿用**追踪/着色分离、半分辨率可选、效果各自的历史与上采样、ray mask，先验证正确性再评估方向分桶。**不搬** HDRP Deferred GBuffer 数据结构、Quality 的递归和依赖 HDRP 灯光集群的 kernel；也不把 UE 的压缩/间接 dispatch 当作 Unity 2022.3 能力。
2. 用已知静态自发光几何、锁曝光和相机轨迹隔离“几何没追到”与“追到但着色不一致”。屏幕假命中不会触发 DXR，需单测 hit rejection；未解决不得自动升级为天空。低预算、TMax 截断、未入库实例、近层透明物和采样稀疏造成的局限须显式标记。
3. [TLAS](TLAS.md) 与共用着色先做已知射线 smoke，再分别开发 [GI](../gi/HANDOFF.md) 与 [反射](../reflection/HANDOFF.md)。HDRP 的 RTGI **不是**本项目动态更新 probe 的出处；探针数据与能量约定另见 [RTXGI 对照](../gi/report.md)。
