# 20-Ray_tracing 调研报告：Hybrid GI/Reflection、Surface Cache 与无缓存 Recipe

**状态：调研与设计，未实现、未做 GPU 测量。** 源码快照：Unity `2022.3.62f1`、内嵌 URP `14.0.12`、HDRP `14.0.12`、参考仓库 `E:/workspace/GitHub/RTXGI-DDGI-URP`。行号只对当前快照有效，升级后按符号重查。命中着色方法见 [HitShading](HitShading.md)。

本报告把可行性核查、HDRP/URP 源码研究、Lumen/SHaRC 对照、无 Surface Cache recipe、探针回退与验收口径集中到一处；模块级实施细节仍以 [HANDOFF](../HANDOFF.md)、[TLAS](TLAS.md)、[SSR-DXR](SSR-DXR.md)、[GI HANDOFF](../gi/HANDOFF.md)、[GI 研究](../gi/report.md) 为准。

**引用等级**：**A** = 本机源码或 Unity 安装包逐行核对；**B** = Unity/Epic/NVIDIA 官方文档或发布说明；**C** = 会议演讲或第三方复述，可能缺少厂商确认。源码行号默认 A；外部链接默认 B；标为 talk-attributed 的按 C 处理，不当作文档事实。

## 0. 结论先行

1. **RT 是可选高端扩展**：默认资产不启用；Windows x64 D3D12 DXR 首发验证；PS5/Xbox 按 SKU 单独验收。移动/Web/D3D11/低档位不出队、不分配 RT 资源，原 SSGI/SSR/baked GI 不变。
2. **Unity 2022.3 可做，但只有一种模型**：`RayTracingAccelerationStructure` + `RayTracingShader` + `CommandBuffer.BuildRayTracingAccelerationStructure/DispatchRays`；没有 inline RayQuery、没有通用 bindless、没有 GPU 间接 RT dispatch。inline RT 到 2023.1 才进**计算着色器**，2023.2 扩到光栅化；Unity 6 增加 RTAS build flags 与异步 build；Unity 6.7 LTS 在 URP 提供 Surface Cache GI + UnifiedRayTracing（可在 compute-only 平台运行），是升级后的首选方向，不在 2022.3 预支。
3. **v1 采用无 Surface Cache recipe**：屏幕 SS 先行 → 固定 K/B×R 的 DXR 全材质 Fat CHS（完整 SurfaceData + AHS cutout + 灯表/逐灯阴影）→ 动态 probe 辐照度（GI，含 1 跳反馈 + relocation/classification）→ 显式 Skylight/Reflection Probe 兜底。不做 Surface Cache/mesh cards/atlas、不做递归多跳、不做逐像素 GI DXR。
4. **Surface Cache 不是“老显卡专用”**：Lumen 在 HWRT 下也把 Surface Cache 当默认/快速命中着色路径；HWRT 替换的是几何求交（SDF→BVH），不是 Surface Cache。我们不做它，是因为材质覆盖小、验证成本高，不是因为技术过时。
5. **HDRP Mixed 不能直接搬**：`mixedTracing` 只在 Deferred 且 `tracing==Mixed` 时成立；Performance 不跑屏幕步进；Quality 是独立 raygen/递归路径。HDRP 的四张命中 GBuffer、LightLoop、LightCluster、递归都不进 URP v1。
6. **HDRP 命中 pass 不叫 `LightMode="RayTracing"`**：实际是 `IndirectDXR` / `GBufferDXR` / `ForwardDXR` / `VisibilityDXR` 等，`SetRayTracingShaderPass` 按这个名字选 pass。URP Lit 没有任何 DXR pass；我们需要为受支持材质新增一个命名 pass，不是给一个通用 tag。
7. **命中着色分三级**：H-A（v1，Fat CHS + per-material pass：完整 SurfaceData + AHS + 灯表/阴影）、H-B（v1.5 候选，Thin CHS + 共享材质表）、H-C（v2 候选，完整 Surface Cache）。v1 只承诺 H-A；全材质不需要 bindless/顶点池/atlas，但每材质 pass/variant 仍在。
8. **probe 是辐照度缓存，不是 Surface Cache**：它只补 SSGI 的屏幕外低频漫反射；SSR 的光滑反射仍需 hit lighting。GI 的 G1–G5 不受 SSR 的 H-A/H-B/H-C 选择影响；反馈只读上一帧 atlas 一次，不递归追射线。
9. **压缩/TMin 续传是 v2 性能实验**：Unity 2022.3 没有 RT 间接派发，固定 K + GPU 早退是 v1；压缩、上一帧 N 估计 K、TMin 续传只在 R2b 里用隐藏遮挡物验证，不阻塞 v1。
10. **性能与主机未验证**：尚未做 GPU 测量；B0 先冻结光线/毫秒/显存/灯数/阴影/payload/variant 预算，T3/T3b/T4 先证真实 Build/bind/DispatchRays、材质 pass/AHS 与灯表，再谈 I2 通过。

## 1. 平台、API 与项目边界

### 1.1 已核实的运行环境

| 事实 | 证据 |
|---|---|
| Unity `2022.3.62f1`；URP 为仓库内嵌 `Packages/com.unity.render-pipelines.universal@14.0.12` | `URPSample01/ProjectSettings/ProjectVersion.txt`；`URPSample01/Packages/` |
| 项目 Windows 默认图形 API 为 D3D12（`m_APIs: 12000000` = `Direct3D12`） | `URPSample01/ProjectSettings/ProjectSettings.asset:531–533` |
| 质量档：Performant / Balanced / High Fidelity | `URPSample01/ProjectSettings/QualitySettings.asset` |
| P0 回归基线是 `720bedd` 的 **D3D11** 记录（EditMode 184、PlayMode 6、T12 34/34/35） | [validation/P0/Baseline.json](../validation/P0/Baseline.json) |
| 本机已有 D3D12 构建/捕获脚本，可复用到 RT Player 验证 | `URPSample01/Assets/Tests/ClusteredDeferred/Editor/*D3D12*.cs` |

### 1.2 Unity 2022.3 公开 RT API

证据：`D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Data/Managed/UnityEngine/UnityEngine.CoreModule.xml`。

| API | 行 | 事实 |
|---|---|---|
| `RayTracingAccelerationStructure` | 11325 | `UnityEngine.Experimental.Rendering`；引擎级，不是 URP 公共 API |
| `AddInstance(Renderer, …)` | 11330–11500 | 支持 submesh 选择、transparent 标记、triangle culling、**8 位 instance mask**、instance id |
| `RASSettings(ManagementMode, RayTracingModeMask, int layerMask)` | 11598 | `layerMask` 是 **32 位** GameObject 层过滤；`RayTracingModeMask` 过滤 Renderer 的 `RayTracingMode` |
| `RayTracingMode.Static / DynamicTransform / DynamicGeometry / Off` | 11944 | Static 永不更新；DynamicTransform 只更新 transform；DynamicGeometry 更新 Mesh+transform |
| `UpdateInstanceTransform/Mask/ID/PropertyBlock` | 11667–11700 | 逐实例更新；mask 8 位，与 `TraceRay` 的 mask 对齐 |
| `RemoveInstance / ClearInstances / Dispose` | 11519,11545,11641 | 生命周期由调用方负责 |
| `Update(relativeOrigin)` | 11661 | 更新全部实例 transform |
| `CullInstances(RayTracingInstanceCullingConfig&)` | 11536 | 按 layer/ShadowCastingMode/Material/LOD/plane/sphere 配置过滤；结果含 `transformsChanged`/`materialsCRC`（11819–11824） |
| `CommandBuffer.BuildRayTracingAccelerationStructure` | 35216 | 本项目选用的构建提交入口（渲染图内排队） |
| `CommandBuffer.DispatchRays(RayTracingShader, string, uint, uint, uint, Camera)` | 35509 | 宽度/高度 CPU 指定；**没有 args-buffer 间接重载** |
| `CommandBuffer.SetRayTracingShaderPass(RayTracingShader, string)` | 38147 | 按材质 pass 名选择 CHS/AHS；HDRP 用 `IndirectDXR`/`GBufferDXR`/`PathTracingDXR`，不是 `LightMode="RayTracing"` tag |
| `RayTracingShader`（raygen/closest-hit/any-hit/miss） | 11969 | 材质命中由 per-material pass（SBT）提供，不是全局 CHS；AnyHit 可做 alpha test |
| `RayTracingShader.SetAccelerationStructure` | 11989 | 绑定 RTAS；配合 `CommandBuffer.SetRayTracingShaderPass` 选材质命中 pass |
| `SystemInfo.supportsRayTracing` | 49424 | 运行时能力门控 |

**关键边界**：

- 2022.3 没有 inline RayQuery：CoreModule XML 中查不到 `RayQuery` / `supportsInlineRayTracing` / `inlineraytracing` 符号，也没有 `ComputeShader.SetRayTracingAccelerationStructure`，因此 sandwich 配方不能直接实现。Unity 2023.1 先给 DXR1.1 平台的**计算着色器**加入 inline RT（`SystemInfo.supportsInlineRayTracing`、`#pragma require inlineraytracing`、`ComputeShader.SetRayTracingAccelerationStructure`），2023.2 再扩到光栅化着色器；Unity 6 追加 RTAS build flags 与 compute queue 异步构建。
- 2022.3 没有通用 bindless；方案 A 的“Bindless 顶点/纹理数组”只能退化为材质 buffer/atlas + 每材质 pass。
- 2022.3 没有 RT 间接派发；压缩后仍要固定 K 的 `DispatchRays` + raygen 早退，尾部 wave 会空转。
- 构建入口不只一个：命令缓冲 `CommandBuffer.BuildRayTracingAccelerationStructure`（35216）与对象方法 `RayTracingAccelerationStructure.Build()` / `Build(Vector3)`（11507、11513）。v1 统一走命令缓冲，保证与渲染队列和帧序一致；不要写成“唯一公开入口”。
- RTAS 的 Build/refit 由驱动/Unity 内部决定；公开 API 只记录“提交 Build 次数”，不要声称显式 refit 或 BLAS/TLAS 分离计数。

### 1.3 材质命中入口

Unity 的 closest-hit 来自**材质 shader 中与 `SetRayTracingShaderPass` 同名的 pass**，不是全局一个 CHS。HDRP 的 Lit 有 `IndirectDXR`、`GBufferDXR`、`ForwardDXR`、`VisibilityDXR`、`SubSurfaceDXR`、`DebugDXR`、`PathTracingDXR` 等 pass（`Runtime/Material/Lit/Lit.shader:1027–1035,1081–1082,1130–1131,1168–1169,1197–1198,1231–1232,1255–1256`），每个带 `#pragma raytracing surface_shader` 与 `#pragma only_renderers d3d11 xboxseries ps5`；`SetRayTracingShaderPass` 在 `HDRenderPipeline.RaytracingDeferredLightLoop.cs:306`、`HDRenderPipeline.RaytracingIndirectDiffuse.cs:390`、`HDRenderPipeline.RaytracingReflection.cs:521` 分别选 `GBufferDXR` / `IndirectDXR` / `IndirectDXR`。

URP 14 Lit/ComplexLit 只有 `UniversalForward`、`UniversalGBuffer`、`ShadowCaster`、`DepthOnly`、`DepthNormals`、`UniversalTransparentDepthNormals`、`Meta`、`Universal2D`，**没有任何 DXR pass**。因此 v1 必须新增一个命名 pass（例如 `GlxDXR`）到受支持材质；名称是自由的，但要与 C# 的 `SetRayTracingShaderPass` 一致。不要写成 `LightMode="RayTracing"`；那只是早期讨论里的错误简称。

v1 的 `GlxDXR` 采用 **Fat CHS**：CHS 内用 `UnityRayTracingFetchVertexAttribute3/4` 取 BLAS 顶点、per-material 贴图解 SurfaceData，AHS 用 `_ALPHATEST_ON` + `IgnoreHit()` 做 cutout；不需要 bindless/顶点池/atlas。HDRP 的 `PathTracingDXR` pass（`Lit.shader:1255–1261`）与 `PathTracingIntegrator.hlsl:339–385`/`:387–417` 是同构参考；HDRP 是 **Fat CHS + 瘦 raygen 循环**，不是 Thin CHS。详细 recipe、覆盖与易错点见 [HitShading.md](HitShading.md)。

## 2. 现有 URP SSGI/SSR 合同（已核实）

### 2.1 Pass 顺序

`RenderPassEvent` 常量在 `Runtime/Passes/ScriptableRenderPass.cs:76,82,87,107,112`：`AfterRenderingPrePasses=200`、`BeforeRenderingGbuffer=210`、`BeforeRenderingOpaques=250`、`AfterRenderingOpaques=300`。

| 顺序 | 内容 | 证据 |
|---|---|---|
| 201 | DBuffer decal；非 Deferred 的 ScreenSpaceShadows（对应 pass 入队时） | `Runtime/Decal/DBuffer/DBufferRenderPass.cs:37`；`Runtime/RendererFeatures/ScreenSpaceShadows.cs:66-70` |
| 202 | 内置 motion vectors：SSGI/opaque SSR 入队并请求 Motion 时，早于默认消费者时序 | `Runtime/UniversalRenderer.cs:163,1557-1559,1598`；`SsgiPass.cs:182`、`SsrPass.cs:199` |
| 203 | 共享深度金字塔（`+3`） | `Runtime/RendererFeatures/ScreenSpaceCommon/ScreenSpaceDepthPyramidPass.cs:10`；由第一个消费者入队 `ScreenSpaceDepthPyramidSystem.cs:85` |
| 203 | Editor Scene View 相机 motion（`+3`，仅 Editor） | `ScreenSpaceCommon/ScreenSpaceEditorCameraMotionPass.cs:37`；`ScreenSpaceLightingRendererFeature.cs:191` |
| 204 | GTAO（`+4`） | `RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionPass.cs:10` |
| 205 | SSGI（`+5`） | `RendererFeatures/ScreenSpaceLighting/Passes/SsgiPass.cs:16` |
| 206 | opaque SSR（`+6`） | `Passes/SsrPass.cs:22` |
| 207 | 材质绑定（`+7`） | `Passes/ScreenSpaceLightingSetupPass.cs:9`；GI 绑定 `:54,58`，SSR 绑定 `:79,83`；失败时黑+keyword off `:65-67,90-92` |
| 210 | Deferred GBuffer | `Runtime/UniversalRenderer.cs:349` |
| 250 | Forward opaque | `Runtime/UniversalRenderer.cs:373` |
| 300 | 清理/keyword off | `ScreenSpaceLightingSetupPass.cs:103,126-129,133-146` |

**TLAS 拟放 201，不要求独占**：同槽按入队顺序执行，构建不读取 DBuffer 或屏幕空间阴影的输出，两者也不消费 TLAS。B0 记录现有 pass 的实际顺序；接入后再用 GPU 记录确认 Build 先于首个使用该快照的 DispatchRays。

### 2.2 SSGI

| 事实 | 证据 |
|---|---|
| 两个 kernel：Trace/Reproject；8×8 thread group | `Shaders/Utils/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute:7-8` |
| Trace 只写命中点 NDC；miss 写 `(99,0)` | 同上 `:88` |
| Reproject：invalid = `hitUv.x > 1`；命中写 `float4(SsgiClampRadiance(color), 1.0)` | 同上 `:120-214`（`:141`、`:166`） |
| Fallback 顺序：Reflection Probes → Sky；**没有 APV 步骤** | 同上 `:46-49,171-209`；`ScreenSpaceGlobalIllumination.cs:9-23,53` |
| `rayMiss=None` 时写 `(0,0,0,0)`，weight 0 | 同上 `:208-213` |
| 输出是 **unpremultiplied radiance + weight**；半分辨率 bilateral upsample 不除 alpha | `Shaders/Utils/ScreenSpaceCommon/BilateralUpsample.compute:39-65` |
| 时序历史保留 `(radiance, weight)`，sample count 在独立纹理 | `Shaders/Utils/Denoising/TemporalFilter.compute:5-7` |

**对 GI 回退的含义**：现有 SSGI 可以在 miss 时写旧 reflection/sky，并把 weight 设为 1。启用动态 probe 回退时，必须把 `rayMiss` 设为 `None`，让 miss 写 weight 0，由材质端 `base = lerp(bakedGI, Pgi, c)` 填充；否则旧 fallback 会和 probe 叠加。

### 2.3 SSR

| 事实 | 证据 |
|---|---|
| Miss sentinel `99.0`；trace 只写 hit UV | `Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute:41,229,285` |
| `SsrSamplePreviousColor`：mip=roughness；opacity = 屏幕边缘淡出 × 粗糙度淡出；invalid 为 0 | 同上 `:186-215` |
| Approximation reproject 做邻域填洞，输出 normalized `radiance = outputs/weightSum` | 同上 `:306-375,380-419`（`:372`） |
| PBR reproject 用 3×3 GGX/pdf 权重，`radiance = outputs/weightSum`，**alpha 是中心像素自己的 `centerOpacity`** | 同上 `:421-489`（`:483,486`） |
| 输出目标：Approximation → `_SsrOutputTextureRW`；PBR → `_SsrReprojectTextureRW` | 同上 `:83-87,492` |
| 无上一帧颜色金字塔 → 整个 pass 不输出（含 trace） | `SsrPass.cs:237-238` |
| Approximation 半分辨率 upsample；PBR denoise + half-res upsample | `SsrPass.cs:438-457` |
| History reset：算法切换、尺寸/reset token | `ScreenSpaceLightingHistory.cs:653-667,642-649`；`CameraHistoryStore.cs:16-23,69` |

**关键坑**：PBR 的 radiance 来自 3×3 cross，而 alpha 是中心像素自己的 opacity；两者不是同一采样。DXR 复合时不能把 alpha 当“覆盖份额”反推原始 miss，也不能把 PBR history 的 alpha 当累积覆盖率。

**首帧 HW 的结构约束**：现有 pass 在 `readColorPyramid == null` 时直接返回，trace 也不会执行。若按 [反射 HANDOFF](../reflection/HANDOFF.md) 允许首帧用当前深度/法线独立生成 HW 请求，就必须把分类/派发移到该早退之前，并保证此时 SS 份额为 0；否则首帧不会产生任何请求。

### 2.4 材质合成

`ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingBlend.hlsl`：

- GI：`radiance = max(ssgi.rgb,0)*intensity; return lerp(bakedGI, radiance, saturate(ssgi.a))`（`:24-25`）。
- SSR：`weight = saturate(a * intensity); return lerp(environmentSpecular, radiance, weight)`（`:40-42`），**point clamp**（`:51,54,106,111`）。
- Clear coat：coat lobe 用 SSR weight，base lobe 乘 roughness hierarchy（`:69-94`；coat `:87`，base `:85-86`）。
- 调用点：`GlobalIllumination.hlsl:364,373-380,384`；`Lighting.hlsl:412,419-424,428-429`。
- **强度语义不对称**：GI 的 `intensity` 乘 radiance（`:24`），SSR 的 `intensity` 乘 weight（`:40,79`）。

### 2.5 Forward / Deferred 消费与法线来源

- Deferred 在 GBuffer 写入前调用完整 `GlobalIllumination`：`Shaders/LitGBufferPass.hlsl:220-226`；法线是顶点/切线 shading normal（`:81-87`），`normalizedScreenSpaceUV` 来自 `positionCS`（`:112`）。
- Forward 经 `UniversalFragmentPBR` → `Lighting.hlsl`，同样只消费一次。
- Compute 侧法线来自 `universalRenderer.cameraNormalsTextureHandle`（`ScreenSpaceLightingRenderUtils.cs:62-65`）；只有 Deferred + `accurateGbufferNormals` 才走 octahedral decode（`SsgiPass.cs:976-982`；`ScreenSpaceReflections.compute:112-119`）。
- SSR 材质有效性依赖 normal-alpha smoothness 编码；0 ⇒ weight 0（`ScreenSpaceReflections.compute:122-131`）。

## 3. HDRP 14 对照：SSGI / RTGI / RTR

包根：`E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12`（下称 `HDRP/`）。

### 3.1 SSGI

| 事实 | 证据 |
|---|---|
| 4 个 kernel：full/half trace、full/half reproject；8×8 thread group | `HDRP/Runtime/Lighting/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute:26-29,34,71-73` |
| 命中缓冲只有 `float2` NDC（R16G16_SFloat）；输出是 B10G11R11 的 `float3` | `HDRP/Runtime/Lighting/ScreenSpaceLighting/HDRenderPipeline.ScreenSpaceGlobalIllumination.cs:232-233,236-237` |
| 半分辨率是 opt-in（`fullResolutionSS` 默认 true），bilateral upsample 在 denoise 之后 | `HDRP/Runtime/Lighting/GlobalIllumination.cs:55`；`HDRP/Runtime/Lighting/ScreenSpaceLighting/HDRenderPipeline.ScreenSpaceGlobalIllumination.cs:177-189,332-351,458-468` |
| Fallback：APV（`_EnableProbeVolumes` + `PROBE_VOLUMES_L1/L2`）→ reflection probes → sky → black | `ScreenSpaceGlobalIllumination.compute:280-313`；`RayMarchingFallbackHierarchy.cs:7-27` |
| History validity：NDC 越界、上一帧 UV 越界、hit depth 与 `_HistoryDepthTexture` 差值 | `ScreenSpaceGlobalIllumination.compute:252-277` |
| 输出是 exposure-corrected、HSV clamp（`SSGI_CLAMP_VALUE 7.0`）、mip offset 1 的 radiance；**没有写 weight** | 同上 `:37,39,284,321-327` |

**与 URP 的差异**：URP fork 的 SSGI 输出是 `(radiance, weight)`，HDRP 的 SSGI buffer 只存 radiance，HDRP 的合成/掩码在别处。不要把 HDRP 的“无 weight”直接套到 URP；也不要把 URP 的 weight 契约说成 HDRP 的。

### 3.2 RTGI

| 模式 | 事实 | 证据 |
|---|---|---|
| Performance / Mixed | 共用一条 render 路径；`mixedTracing = tracing==Mixed && litShaderMode==Deferred`；**Performance 不跑屏幕步进** | `HDRP/Runtime/RenderPipeline/Raytracing/HDRenderPipeline.RaytracingIndirectDiffuse.cs:561-570,61`；`...RaytracingDeferredLightLoop.cs:238-251,291-294` |
| Quality | 独立 raygen 走 `IndirectDXR`，有采样/反弹参数与递归上限 | `RayTracingMode.cs:8-18`；`...RaytracingIndirectDiffuse.cs:552-562,389-390,434` |
| Miss | settings 驱动的 hierarchy + APV-as-miss + ambient-probe-as-sky；RTR 关 APV、关 ambient-probe-sky | `...RaytracingIndirectDiffuse.cs:88-97`；`...RaytracingReflection.cs:386-395` |
| 调度 | 可选屏幕 raymarch → 16×16 binning → `DispatchRays`（1D binned 或 2D）→ 8×8 deferred lighting | `...RaytracingDeferredLightLoop.cs:22,145,296-299,306,360-366,392-397` |
| Binning | 固定 16×16 group、256 个八面体方向 bin + `InterlockedAdd`，invalid 进 sink bin 256；**始终派发 `bufferSizeX*bufferSizeY`，不是压缩后的间接派发** | `HDRP/Runtime/RenderPipeline/Raytracing/Shaders/Common/RayBinning.compute:13-14,41-63,88-99`；`...DeferredLightLoop.cs:360-362` |

**结论**：HDRP 的 ray binning 是相干性优化，不是 GPU 间接派发；不要把它当 Lumen 的 compaction/indirect。

### 3.3 RTR

| 事实 | 证据 |
|---|---|
| opaque/transparent 两个 call site；transparent 有独立无 history 路径 | `HDRP/Runtime/RenderPipeline/HDRenderPipeline.RenderGraph.cs:180,1379`；`HDRenderPipeline.LightLoop.cs:1187-1210` |
| Reflection denoise：temporal accumulate → copy history → H/V bilateral，8×8；transparent 跳过；有 half-res kernel | `HDRP/Runtime/RenderPipeline/Raytracing/HDReflectionDenoiser.cs:87-108,126-176`；`...RaytracingReflection.cs:594-598,605-616` |
| RTR 半分辨率门控跟动态分辨率走，不是普通 toggle | `HDRP/Runtime/RenderPipeline/Raytracing/HDRaytracingManager.cs:787-790` |

### 3.4 材质命中 pass 与 RTAS

- HDRP 的命中 pass 是 `IndirectDXR` / `GBufferDXR` / `ForwardDXR` / `VisibilityDXR` / `SubSurfaceDXR` / `DebugDXR` / `PathTracingDXR`，每个带 `#pragma raytracing surface_shader` 与 `#pragma only_renderers d3d11 xboxseries ps5`（`HDRP/Runtime/Material/Lit/Lit.shader:1027-1035,1081-1082,1130-1131,1168-1169,1197-1198,1231-1232,1255-1256`）。
- `SetRayTracingShaderPass` 按 pass 名选择：`GBufferDXR`（`...DeferredLightLoop.cs:306`）、`IndirectDXR`（`...IndirectDiffuse.cs:390`、`...Reflection.cs:521`）。
- 全材质参考：`PathTracingDXR` 的 CHS 在 `PathTracingIntegrator.hlsl:339–385` 解 SurfaceData/直接光/BSDF，AHS 在 `:387–417` 用 `_ALPHATEST_ON` + `IgnoreHit()`；顶点属性来自 BLAS（`RaytracingIntersection.hlsl:97–140`），payload 见 `PathTracingPayload.hlsl:22–56`。HDRP 是 **Fat CHS + 瘦 raygen**（`PathTracingMain.raytrace:189–229`），不是 Thin CHS。
- HDRP 的 RTAS 在 `Automatic` 模式下**每帧每相机重建**（`HDRaytracingManager.cs:538-599`；`HDRenderPipeline.cs:2282-2289`；`RayTracingSettings.cs:9-15`）。**不要**再说“HDRP 只在脏帧 Build”；我们自己的 dirty 策略是项目设计，不是 HDRP 行为。
- HDRP RTAS culling：per-effect `RayTracingInstanceCullingTest`、layer mask、`RayTracingRendererFlag` instance mask、HDRP shader tag、LOD culling、忽略 reflection probe（`HDRTASManager.cs:69-82,121-126,280-334,201-263`）。

### 3.5 可借鉴 / 不搬

**借鉴**：

1. trace/shade 分离 + 紧凑方向/validity 缓冲，invalid 便宜丢弃。
2. 显式 miss hierarchy（bitmask），以及单独的 last-bounce hierarchy。
3. history 三次便宜拒绝（边界、深度、motion/stencil），temporal → spatial，不把空间结果回灌。
4. half-res + depth-guided bilateral upsample，放在 denoise 之后；动态分辨率安全门控。
5. 1D tiled ray dispatch 让 invalid 便宜（思想可借，256-bin 实现不搬）。
6. 按路径 gate denoise：透明不降噪、不 clamp。

**不搬**：四张命中 GBuffer + `MINIMAL_GBUFFER` + 完整 HDRP LightLoop；LightCluster；Quality 的递归/多跳/`NO_RAY_RECURSION`；HDRP 的 per-effect pass 与 HDRP `SurfaceData/BSDFData/LightList` 实现；APV-as-ray-miss；HDRP 的 color-pyramid mip-offset 技巧。**借** `PathTracingDXR` 的 CHS/AHS/顶点属性/payload 结构（见 [HitShading](HitShading.md)），不借它的 HDRP 材质体系。

## 4. Lumen：SWRT/HWRT、Surface Cache 与 Hit Lighting

### 4.1 两级管线

| 管线 | 流程 | 代价/约束 |
|---|---|---|
| Surface-Cache（快速路径） | Screen tracing → Trace + Surface Cache → Skylight（miss） | 一个共享 CHS 只取几何法线 + surface-cache 参数化；raygen 做 normal-weighted surface-cache evaluation；强制 opaque、无 Any-Hit；需要 mesh cards/atlas |
| Hit-Lighting（高质量） | Screen tracing → Trace + Surface Cache → Sort → Retrace + Hit Lighting → Skylight | 第一遍 surface cache 做前提，Sort 按材质/几何分桶，Retrace 只对选中的 ray 做动态材质/光照；更贵，但支持没有 surface cache 参数化的 mesh |

**来源与证据等级**：

- “Screen traces first, then a more reliable method”；Surface Cache 默认用于反射命中，Hit Lighting 可选且更贵；HWRT 下间接漫反射仍用 Surface Cache：Epic Lumen Technical Details / Lumen GI and Reflections 文档。
- “single CHS / no any-hit / forced opaque / Sort + Retrace”属于 SIGGRAPH 2022 Lumen talk 的实现细节，不在 Epic 公开文档里；本报告把它们标为 **talk-attributed**，引用课程页与 slides，不当作 Epic 文档事实。
- Surface Cache 不是“老显卡专用”：SWRT（SDF）和 HWRT（BVH）都以它为默认命中着色；HWRT 替换的是几何求交。我们不做它，是范围选择。

### 4.2 Screen tracing、compaction、TMin 与 Skylight

Lumen 的 screen probe/reflection tracing 主干可以概括为：

0. **为什么还留屏幕追踪**：HZB 步进比 BVH 便宜，直接用当前帧已着色颜色（透明/贴花等 RT 命中拿不到的效果也在内），并顺带得到安全距离；但屏幕外、被遮挡和背面它解决不了，而且屏幕命中只是“颜色+深度”的近似，必须用厚度/深度容差验证，否则假命中会提前终止、把离屏洞伪装成有效反射。
1. **Screen tracing**：HZB 步进；有效命中（Z 差 < Thickness）→ 直接读已着色颜色，标记 `hit + distance`；miss/不确定遮挡 → 记录 `!hit + 最后安全距离`。
2. **Compaction**：扫描状态，`bHit==false` 的射线用 wave prefix sum/atomic 压进连续队列，统计 N。
3. **HWRT**：`DispatchIndirect` 只启动 N 个线程；从 `TMin = max(screenSafeDistance - pullbackBias, defaultTMin)` 继续追 BVH，而不是从 0 重追。
4. **Skylight**：HWRT 后仍 `!hit` 的射线才按方向采 sky/SH/cubemap。

**对我们的约束**：Unity 2022.3 没有 inline RayQuery、没有 RT 间接派发；Compaction 可以在 compute 做，但只能固定 K 派发 + 早退；TMin 续传需要改 trace 输出并做隐藏遮挡物验证。详细差异见 [SSR-DXR](SSR-DXR.md) 与 R2b。

## 5. Hybrid recipe：Surface Cache、Bindless Hit、SHaRC

### 5.1 无 Surface Cache 的三明治

```
[SS]  SSGI/SSR screen trace（现有 Hi-Z）
      -> resolved：沿用现有 SS 取色/历史规则
      -> needsHW：分类时 append 到请求列表
      -> invalid：回环境/反射探针

[HW]  固定 K/B×R 的 DispatchRays + GPU 早退
      -> GlxDXR CHS：SurfaceData(albedo/normal/metallic/smoothness/emission)
         + 灯表（方向/点/聚光）+ 逐灯阴影
      -> AHS：alpha test（_ALPHATEST_ON + IgnoreHit）；阴影命中即遮挡
      -> probe 模式：+ 上一帧 atlas 反馈（1 次 8 邻居查询）
         reflection 模式：命中点完整 PBR，不做反馈
      -> 不乘接收面 Fresnel，不重复接收面 BRDF

[合成] unpremultiplied (radiance, weight) 写回原 slot
       材质端 SSR: lerp(env, radiance, weight)；GI: lerp(base, Lss, w)
       BRDF 只应用一次

[GI]  动态 probe：B probes × R rays/帧（前 32 条固定）-> irradiance atlas
      relocation/classification 默认开；inactive 只追 32 条
      材质端按接收点 P/N 查询并应用 offset；SS 高频 + probe 低频

[Miss] 仅可信的远处 miss 采 Skylight；其余保持 Reflection Probe / Sky
```

### 5.2 方案 A：Bindless hit + 直接光 + probe 二次反弹

**优点**：不建 Surface Cache；命中材质是真值；动态材质/光照立即生效；存储小；emission + 太阳 + 一条阴影射线成本可预测；probe 二次反弹便宜；在有 inline RT + bindless 的引擎里可全在 compute 完成。

**缺点**：bindless 顶点/纹理数组是引擎能力，Unity 2022.3 没有；每命中纹理采样/材质分支仍会分歧，cache miss/mip/aniso 都是成本；“20 行”是示意；强制 opaque 排除 alpha test/透明；probe 反弹只解决漫反射，且会漏光/滞后；hit point 出 probe 体积只能回退。

**我们的对应**：H-A 是它的受约束版本（per-material Fat CHS：完整材质 + 灯表/阴影 + GI 1 跳反馈；无 bindless、无 Surface Cache）；H-B 是 Thin CHS + 共享材质表，是 v1.5 候选。

### 5.3 方案 B：Spatial Hash Radiance Cache（SHaRC 一类）

**优点**：着色成本与射线数量解耦；少量更新射线 + 大量查表；世界空间覆盖屏幕外；支持多跳；NVIDIA SHaRC 已在 Portal RTX、Cyberpunk path tracing 等生产管线使用；不依赖神经网络，任何 DXR/Vulkan RT GPU 可用。

**缺点**：不是“十分之一代码”，而是完整缓存系统：哈希表、cell size、法线桶、插入/更新/淘汰、时序累积、bias、冲突、漏光；预热/滞后；动态光照/物体/disocclusion 要处理；方向分辨率有限，光滑反射不够；Unity 2022.3 的更新射线仍要走 CHS；对“一像素一条 SSR + probe GI”的我们，收益不一定覆盖复杂度。

**我们的对应**：v2 候选。只有 profiling 显示 hit shading/ray budget 是主瓶颈时才立项；不是 drop-in。

### 5.4 对比

| | A：bindless hit + probe | B：SHaRC | 我们 v1（H-A） |
|---|---|---|---|
| 核心 | 命中点取材质 + 直接光 + probe 反弹 | 世界空间哈希辐射缓存 | per-material Fat CHS（全材质+灯表）+ 接收点 probe（含反馈） |
| 材质覆盖 | 广（依赖 bindless） | 广（缓存） | 窄（登记 Lit/cutout/自发光；每材质 pass） |
| 多跳 | 1 跳（probe） | 多跳 | 反馈跨帧迭代；无同帧递归 |
| Unity 2022.3 | 只能 H-B 近似 | 可行但重 | 直接可行 |
| 主要风险 | 分歧、漏光、无 bindless | 缓存复杂度、滞后、漏光 | 覆盖窄、无多跳 |

**结论**：v1 保持 H-A；H-B 只在 R4 证明 pass/variant 或 hit shading 成本成为瓶颈后做；B 放 v2，先量 hit shading/ray budget，不要先写缓存。

## 6. 动态 probe：只作 SSGI 漫反射回退

### 6.1 数据面与更新

- 单一静态有界网格，位置固定；固定预算 `B probes × R rays`（R ≥ 32），轮转更新；前 32 条固定射线供 relocation/classification；多相机同一场景帧不重复；整批完成后发布一致快照；未更新 tile 保留上一次有效值；冷启动不做超预算预热。
- 命中点算方向光/点光/聚光 + 逐灯阴影 + 自发光，再读上一帧 atlas 做一次反馈；**不做递归多跳**。relocation/classification 默认开；多 volume、滚动、独立 DDGI、GI 逐像素 DXR 仍不做。
- 存储：irradiance `RGBA16F` 8×8 tile（6×6 内容 + 1 texel border + 4 个重复角）；distance `RG16F` 16×16（14×14 内容 + 1 texel border + 4 个重复角）；probe data（offset + state）；上一帧已发布快照；有界灯表；本批 ray scratch。
- 采样：8 邻居三线性 × 法线 wrap shading × Chebyshev；probe 世界位置 = 网格 + offset；inactive/无效 probe 权重归零后重新归一化；薄墙用距离矩拒绝。

### 6.2 参考实现的数学（`E:/workspace/GitHub/RTXGI-DDGI-URP`）

| 事实 | 证据 |
|---|---|
| Irradiance 权重 `saturate(dot(probeDir,rayDir))`，归一化 `×1/max(2Σw,eps)` | `Assets/Features/Resources/Shaders/DDGIUpdateIrradiance.compute:90-92,102-108` |
| 时域：历史黑→hysteresis 0；`Max(prev-cur)>0.2`→hysteresis−0.75；`Lum(delta)>1`→delta×0.25；最小 lerp 步长 1/1024 | 同上 `:110-139` |
| 在 encode 空间（`pow(x,1/5)`）做 hysteresis；采样端 `pow(x,2.5)` 解码 | 同上 `:114`；`Lib/DDGIFuncs.hlsl:419` |
| Distance：`min(abs(d),1.5·abs(probeSize))`，权重 `pow(saturate(dot),64)`，累积 `(d,d²)·w`，`×1/(2Σw)` | `DDGIUpdateDistance.compute:87,91-95,104-116` |
| 8 邻居采样 + wrap shading + Chebyshev + crush + 1e-6 floor + 重归一化 | `Lib/DDGIFuncs.hlsl:348-430` |
| 方向 = 球面 Fibonacci + 逐帧随机旋转；32 条固定射线不旋转且不参与卷积 | `Lib/DDGIFuncs.hlsl:108-141`；`DDGIInputs.hlsl:29` |
| 命中光照 = 方向光 + 点/聚光全部 + 逐灯阴影 + 自发光；每命中灯数无上限 | `Shaders/DDGIRayTracing.raytrace:71-114` |
| 反馈 = 命中点读 `_ProbeIrradianceHistory` 8 邻居 × `min(albedo,0.9)·Lambert()`；跨帧迭代 | 同上 `:115`；`Lib/DDGIFuncs.hlsl:348-430` |
| 灯表 = 每帧 `FindObjectsOfType<Light>()` 并重建 buffer；不用相机 visibleLights | `Runtime/DDGIFeature.cs:752-809` |
| Relocation = 32 条固定射线；背向比例/近正面时偏移；offset 限 0.45×probe 间距 | `Shaders/DDGIRelocateProbe.compute:20-130`；`Runtime/Overrides/DDGI.cs:48-56` |
| Classification = 背向比例高或 32 条射线都超出体素 → inactive；inactive 只追 32 条，消费者跳过 | `Shaders/DDGIProbeClassification.compute:11-100`；`DDGIRayTracing.raytrace:20-23`；`Lib/DDGIFuncs.hlsl:366` |
| 参考限制 = 无 AnyHit/cutout、`_EmissionMap` 未接、灯光无预算上限、TLAS 每帧重建 | `LitRayTracing.shader:154-191`；`DDGIFeature.cs:472-486,752` |
| 消费者读 `_ProbeIrradianceHistory` / `_ProbeDistanceHistory`，天然晚一帧 | `Lib/DDGIFuncs.hlsl:394,418` |
| 代码默认 22³ probes、**144 rays/probe**（范围 32–256），ray buffer 按 `MaxNumRays=512` 固定 stride | `Runtime/Overrides/DDGI.cs:91-96`；`Runtime/DDGIFeature.cs:333,340` |
| 默认 variability OFF ⇒ `mIsConverged` 强制 false，参考实现默认不会停更 | `Runtime/DDGIFeature.cs:634,983-984`；`Overrides/DDGI.cs:60` |
| 收敛门也包住 RTAS Build + DispatchRays；`DDGIFeature.cs:652` 是 `Release()`，不是收敛跳过 | `Runtime/DDGIFeature.cs:472-487,652` |
| 命中材质来自 `Custom/LitRayTracing` 的 `DDGIRayTracing` pass；emission 只读 `_EmissionColor`，`_EmissionMap` 未用 | `Shaders/LitRayTracing.shader:124-194`（`:190`） |
| `_EnergyPreservation` / `_NormalBias` 被 push 但 shader 未读，是死参数 | `Runtime/DDGIFeature.cs:702,704` |

### 6.3 能量契约

我们的设计约定是：

- `Eprobe` = 探针 irradiance（余弦半球积分量）= 命中点直接光 + 自发光 + 一次 `Eprev` 反馈；反馈在射线 radiance 上加 `min(albedo,cap)·Lambert()·Eprev(P,N)`，经积分后对 E 的贡献是 `min(albedo,cap)·Eprev`，只在 producer 加一次；
- `Pgi = Eprobe / π` = 接收点入射漫反射辐亮度，与 URP `bakedGI` 同量纲；
- `c` = 体积覆盖与有效位，只乘一次；
- `base = lerp(bakedGI, Pgi, c)`；
- 材质 `lerp(base, Lss, w)`，即 `(1−w)·base + w·Lss`。

**必须标注**：`Pgi=Eprobe/π` 是我们的设计约定，不是参考实现的能量链。参考实现的链是 `1/(2Σw)`（atlas，存 `E/(2π)`）→ `pow(x,1/5)` 编码 → `pow(x,2.5)` 解码 → `×Σ⁻¹` → `×itself` → `×2π` → 材质 `×albedo/π`。取常数场 `L` 走一遍：atlas 存 `L/2`，采样端平均 `√(L/2)` 再平方回 `L/2`，`×2π` 得 `πL=E`，材质端 `×albedo/π` 得 `albedo·L`——**没有额外的 0.5**；非均匀场“对 √E 均值再平方”才是真正的方差偏差。G1 必须先把 atlas 单位钉死，否则后续 G3 的 ROI 对不上；按 `0.5·albedo·E/π` 设参考基线会把正确实现判成亮一倍。

**反馈不是同帧 1 跳**：probe 分批轮转，反馈读上一帧已发布快照，跨帧迭代；G5 按阶跃/衰减判定，不能只测一帧，也不能把反馈当递归多跳。反馈读的是上一帧 irradiance atlas，不是正在写的 atlas；inactive/无效邻居为 0。

## 7. TLAS 与场景服务

- RTAS 是场景级快照，不是相机资源；两个消费者在同一 revision/过滤配置/原点上共享。帧序上放在 **201**（`AfterRenderingPrePasses+1`，共槽条件见 §2.1），在第一个 RT 消费者之前、且与相机无关地构建/复用。首帧无有效快照回原效果；卸载、管线重建、设备丢失、渲染器销毁时 Dispose。
- 成员登记：明确登记可贡献的 Renderer；不逐相机全场查找；**v1 不用 plane/frustum 裁剪**（HDRP 的 extended frustum 不搬），屏外几何仍可能被 GI/反射命中。`CullInstances` 可做 layer/material/LOD 过滤，并用 `transformsChanged`/`materialsCRC` 做 dirty 判定。
- 区分 32 位 layer 过滤（`RASSettings`）、8 位 instance mask（`AddInstance`/`TraceRay`）、submesh enable/alpha 策略。
- dirty/Build：静态成员不重复 Build；动态成员按 revision 只提交一次；`RayTracingMode.Static` 不因 transform 自动更新；刚体走 `UpdateInstanceTransform` + Build；灯光/发光参数影响着色或 probe 失效，不应无条件重建几何。记录“提交 Build 次数”，不声称显式 refit。
- `TMin` 只做与尺度相符的表面偏移；Hi-Z 走过的距离不是世界空间安全距离；有限 TMax 的截断不等于天空可见。
- 灯表：场景级注册表保存全部合格 Directional/Point/Spot；GPU 灯表容量 `Lmax`、每命中灯数 `LmaxHit`、阴影射线 `Smax` 在 B0 冻结；不使用相机 `visibleLights`；灯光变化只更新 buffer/着色输入，不重建几何；不应用 light layer mask/cookie/面光源。
- 共同 ABI：世界空间 origin、归一化 direction、TMin/TMax、实例 mask；命中材质由 `GlxDXR` CHS/AHS 提供（BLAS 顶点属性 + per-material 贴图 + cutout）；payload 含 hitT/valid/flags；输出区分几何命中（距离、材质有效性、沿返回方向的线性 HDR 辐亮度）、范围内无交点、无效材质/未服务；不支持材质/背面约定显式计数。

## 8. 验收矩阵与独立证据

每个 ID 是可单独重跑的工作单元；先冻结输入，再提交实现、测试、数据摘要和已知差异。fixture 不能代替 Player 集成签收。

| ID / 前置 | 交付 | 通过条件 / 证据 |
|---|---|---|
| B0 / 无 | 冻结设备、版本、场景、灯光、相机轨迹、阈值、光线/毫秒/显存/`Lmax/LmaxHit/Smax`/payload/variant 上限 | 同 HEAD 的 D3D11 和 D3D12 各跑两遍 RT-off；项目默认 D3D12，需显式切 D3D11；关闭扩展与原路径同种子、同历史长度、同帧逐像素一致；记录筛选器/计数、`supportsRayTracing`、目标 GPU |
| T1–T3 / B0 | 后端门控、RTAS、实例、基础命中着色 | 关 SSGI/SSR 也能用已知射线验证；T1 开关后资源净增长 0；T2 离屏/禁用/mask/移动/LOD 与 CPU 解析交点一致；T3 Player 证明确有 Build/bind/DispatchRays，emit/direct/shadow 与解析参考一致，非有限值 0 |
| T3b / T2+T3 | `GlxDXR` 全材质 pass：CHS/AHS、BLAS 顶点、贴图/法线/cutout、payload | 贴图/法线/cutout 与光栅一致到 B0；payload 读回符合布局；supported/unsupported 显式计数；记录 payload 字节与 variant 数 |
| T4 / T3b | 灯表与逐灯阴影 | CPU 登记全部合格 Directional/Point/Spot；GPU 表最多上传 `Lmax` 盏，每命中选灯和阴影各受 `LmaxHit`、`Smax` 限制，分别记录三层溢出；移动/删除/禁用更新；阴影开关正确；不依赖 `visibleLights`；记录灯数/每命中灯数/阴影数/overflow |
| G1–G3 / B0；G2 真实更新依赖 G1+T3b+T4，G3 真实集成依赖 G2 | 探针数学、分帧更新、SSGI 合成 | G1 常量场/两色场/常距平面/非法/relocated/inactive/反馈项，先钉死 atlas 单位；G2 预算/轮转/发布/动态恢复/灯与阴影计数；G3 先 feedback/relocation/classification 全关跑基线，再扫权重/强度/首帧/切镜/薄墙/体积外/双相机，关 SSR |
| G4 / G2 | relocation + classification（默认开） | offset 限幅、inactive 只追 32 且消费者跳过、动态几何重激活、无抖动/串索引；记录 active/inactive/relocated |
| G5 / G2+T3b+T4 | probe 1 跳反馈 | 灯开/关阶跃、feedback on/off、无效/relocated/inactive 邻居、薄墙；与高采样参考 ROI；旧光在 B0 时间窗内衰减；不递归 |
| R1–R3 / B0；真实补追依赖 T3 | 分类/预算、离屏求交、重建 | R1 计数守恒/无越界/无历史可分类；R2 命中正确实例/距离/颜色，未服务不误标 sky；R3 ROI 轮廓召回/错误率/源切换亮度/拖尾/GPU ms，透明控制组不变 |
| R4 / R2+T3b | H-A 材质覆盖 | 只有登记子集参与；不支持材质显式计数；无 Surface Cache/mesh card 资源；不重复接收面 Fresnel/BRDF；H-A 开关不改变 R1–R3 |
| R5 / R2+T3b+T4 | 反射全材质与灯光预算 | 命中点 PBR/cutout/灯表/阴影与参考一致；灯数/阴影/payload/overflow 计数；不做 probe 反馈；超预算回退并计数 |
| R2b / R2（v2 候选） | 压缩、上一帧 N 估计 K、TMin 续传 | 隐藏遮挡物不漏遮挡；结果确定；收益超过压缩/重追成本才进 v2；失败只记 v2 NO-GO，不影响 R1–R5 |
| I1 / T、G、R | 开关组合、相机/资源隔离 | 都关/GI/SSR/双开 × Forward/Forward+/Deferred；再扫 feedback/relocation/classification 与 T3b/T4；双开共用同一 TLAS/灯表版本；单开无另一模块 dispatch；切镜/分辨率/动态物体/相机堆叠/运行时关闭无残影/泄漏 |
| I2 / I1 | Player 画质/性能与平台签收 | Windows DXR 实派发 + 同机 D3D12 off + D3D11 off + 非目标构建剥离；主机按目标单独构建/真机测量/批准；PC 成绩不默认开启主机 |

每个证据包记录：基线/实现 SHA、设备/SKU/驱动/API/质量档、分辨率、场景版本、种子、曝光、帧号、预热帧、计时窗口、输入/输出路径、实际值/门槛、pass/fail/blocked；另记 TLAS Build/实例数、probe scheduled/updated/published、active/inactive/relocated、SSR requested/served/overflow/hit/miss、dispatch marker、payload 字节、pass/variant、灯数/阴影数、hit shading ms、R2b N/K/wave/TMin A/B。证据路径：`validation/<ID>/<platform>-<renderer>/`，不覆盖 P0。

## 9. 风险与未决问题

| 风险 | 说明 | 处理 |
|---|---|---|
| 引擎能力 | 2022.3 无 inline RayQuery / 通用 bindless / RT 间接派发 | v1 用 RayTracingShader + 固定 K + 早退；H-B/H-C 与 R2b 明确为候选 |
| 材质覆盖 | URP Lit 无 DXR pass；每材质要新增命名 pass；全材质靠 per-material pass + BLAS 顶点属性，不需要 bindless；ShaderGraph/透明/蒙皮仍不支持 | v1 登记 Lit/cutout/自发光；R4/T3b 统计 supported/unsupported 与 variant；不支持不静默当黑/sky |
| 命中成本 | 全灯 + 逐灯阴影 + 8 邻居反馈 + 材质解包会让命中成本远高于最小着色；payload/寄存器/SBT 分歧未测 | B0 冻结 `Lmax/LmaxHit/Smax/payload/variant`；T3b/T4/R5 分项计时；超预算收窄覆盖 |
| 反馈稳定性 | 跨帧迭代可能过冲、残留、漏光；关灯后旧能量可能滞留 | G5 阶跃/衰减；不额外乘 albedo；inactive/无效为 0；不把反馈当递归多跳 |
| Relocation/classification | offset 抖动、错误 inactive、动态几何重激活延迟 | G4 固定射线/阈值；调试可视化；动态进出用例；B0 帧数内重激活 |
| 能量/单位 | 我们的 `Pgi=E/π` 是设计约定；参考实现能量链不同 | G1 先钉 atlas 单位；G3 用线性 ROI/亮度跳变；不要直接抄参考的 `×2π/平方` |
| 探针可见性 | 薄墙漏光、体积外、probe 卡几何、动态光滞后 | 距离矩 Chebyshev + 法线；薄墙单测；人工布点/禁用；不靠更重的时域滤波掩盖 |
| TMin 续传 | Hi-Z 距离不是世界空间安全距离，可能漏屏外遮挡 | 不进 v1；R2b 隐藏遮挡物 A/B；失败只记 v2 NO-GO |
| SSR 复合 | PBR 的 radiance 来自 3×3 cross，alpha 是中心 opacity；不能当覆盖份额 | 保留原始样本状态；HW 只补未解决份额；unpremultiplied radiance + weight |
| 性能 | TLAS Build、probe 更新、hit shading（材质/灯/阴影/反馈）、relocation/classification、denoise、显存都未测 | B0 冻结预算；I2 分项计时；未定预算不报通过 |
| 主机 | PS5/Xbox 的 Unity RT 支持、SKU/质量档/性能无本地证据 | 单独构建/真机测量；PC 通过不默认开启 |
| 维护 | 内嵌 URP fork 差异；每材质 pass/variant；DXR pass 的 keyword 组合；文档行号漂移 | 提交说明列包内改动；research 行号版本化；R4/T3b 记录 variant；优先共享 include/材质表 |
| 未来引擎 | 2023.1 计算 inline RT、2023.2 光栅化 inline RT；Unity 6 RTAS build flags/异步 build；Unity 6.7 LTS 的 URP Surface Cache GI + UnifiedRayTracing（含 compute-only 平台） | 不在 2022.3 预支；升级时优先评估官方 Surface Cache GI 而不是自建 H-C，再重评 H-B/B；不把未来能力写进 v1 |

## 10. 易错点与证据

| 易错说法 | 实际 | 证据 |
|---|---|---|
| HDRP 材质用 `LightMode="RayTracing"` pass | 实际 pass 名是 `IndirectDXR`/`GBufferDXR`/`ForwardDXR`/…；`SetRayTracingShaderPass` 按名字选 | `Lit.shader:1027-1035`；`DeferredLightLoop.cs:306` 等 |
| 混淆内置与 Editor 相机 motion | 内置 early motion 在 202；Editor 相机 motion 在 203，与深度金字塔共槽 | `UniversalRenderer.cs:163,1557-1559`；`ScreenSpaceEditorCameraMotionPass.cs:37` |
| URP SSGI fallback 有 APV 步骤 | URP 只有 Reflection Probes → Sky；APV 是 HDRP 的 | `ScreenSpaceGlobalIllumination.compute:171-209` |
| HDRP SSGI 写 `(radiance, weight)` | HDRP SSGI buffer 只写 radiance；URP fork 才写 `(radiance, weight)` | HDRP compute `:37,39,284,321-327`；URP blend `:24-25` |
| HDRP 只在脏帧 Build RTAS | `Automatic` 下每帧每相机重建 | `HDRaytracingManager.cs:538-599`；`HDRenderPipeline.cs:2282-2289` |
| 参考实现默认 256 rays/probe | 代码默认 144（范围 32–256）；ray buffer 按 `MaxNumRays=512` 固定 stride；256 是 asset 值 | `Overrides/DDGI.cs:91-96`；`DDGIFeature.cs:333,340` |
| `DDGIFeature.cs:652` 是收敛跳过 | 那是 `Release()`；收敛门在 472–487/492–501/506–514，判据 983–984 | `DDGIFeature.cs` |
| distance tile 是 2px border | 1 texel/side + 4 个重复角（16=14+2） | `DDGIUpdateDistance.compute:14-31` |
| `Pgi=E/π` 是参考实现结论 | 这是我们的设计约定；参考链能量不同 | `DDGIUpdateIrradiance.compute:102-108`；`DDGIFuncs.hlsl:419,427-430` |
| Surface Cache 只服务老显卡/SDF | Lumen HWRT 也默认用它；HWRT 只替换几何求交 | Epic Lumen 文档 |
| Lumen single CHS/no AHS/Sort+Retrace 是 Epic 文档 | 属 SIGGRAPH 2022 talk 细节 | SIGGRAPH 2022 slides |
| 2077 直接等于 bindless G-buffer + DDGI | 2077 path tracing 更典型是 ReSTIR + NRD + SHaRC/NRC；具体归因部分未官方确认 | NVIDIA GDC 2024 session；RTG 章节 |
| 参考实现能量链约 `0.5·albedo·E/π` | 常数场往返得到 `albedo·L = albedo·E/π`，无额外 0.5；0.5 来自把 atlas 的 `1/(2Σw)` 与采样端 `×2π` 拆开看 | `DDGIUpdateIrradiance.compute:102–108`；`DDGIFuncs.hlsl:416–430` |
| `CommandBuffer.BuildRayTracingAccelerationStructure` 是唯一公开构建入口 | 对象方法 `RayTracingAccelerationStructure.Build()` / `Build(Vector3)` 同样公开；v1 选命令缓冲是为了对齐渲染队列 | CoreModule XML：11507、11513、35216 |
| HDRP 是 Thin CHS + Fat RayGen | 实际是 **Fat CHS + 瘦 raygen 循环**：CHS 解材质/直接光/BSDF，raygen 只循环 `TraceRay`/累加 | `PathTracingIntegrator.hlsl:78,162-164,339-385`；`PathTracingMain.raytrace:189-229` |
| HDRP payload 48–64B | HDRP `PathPayload` 字段更多（PT/AOV/continuation），约百字节量级；48–64B 是我们自己的目标 | `PathTracingPayload.hlsl:22-56` |
| URP ShaderGraph 原生兼容 RT | 只有 HDRP 有 RT target 且有限制；URP 没有 DXR target/pass | HDRP `Ray-Tracing-Getting-Started.md:288-293`；URP Lit pass 列表 |
| SkinnedMesh 无条件兼容 | 有条件；RTAS 可含 SMR，但 vertex animation 是文档限制 | `HDRaytracingManager.cs:377`；HDRP 文档 `:273` |
| SBT 只慢 10–20% | 无出处；hit-group 分歧、variant、payload 寄存器压力都可能反转 | 需实测 |
| 全材质必须 bindless/顶点池/atlas | 不需要；per-material SBT + `UnityRayTracingFetchVertexAttribute3/4` + 材质自有贴图即可，代价是 pass/variant 覆盖 | `UnityRayTracingMeshUtils.cginc:220,274`；HDRP `Lit.shader:1255`；参考实现 `LitRayTracing.shader:154-191` |
| 参考实现“只做直接光” | 它还做上一帧 atlas 反馈（1 次读取）、relocation/classification、全方向/点/聚光与逐灯阴影 | `DDGIRayTracing.raytrace:71-115`；`DDGIProbeClassification.compute`；`DDGIRelocateProbe.compute` |

## 11. 参考索引

### 本地源码（项目）

- URP 包：`URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/`
  - SSGI：[ScreenSpaceGlobalIllumination.compute](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute)、[SsgiPass.cs](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsgiPass.cs)
  - SSR：[ScreenSpaceReflections.compute](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute)、[SsrPass.cs](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs)
  - 合成：[ScreenSpaceLightingBlend.hlsl](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingBlend.hlsl)
  - Pass 顺序：[ScreenSpaceDepthPyramidPass.cs](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceCommon/ScreenSpaceDepthPyramidPass.cs)、[ScreenSpaceLightingSetupPass.cs](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/ScreenSpaceLightingSetupPass.cs)
- Unity API：`D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Data/Managed/UnityEngine/UnityEngine.CoreModule.xml`（11325、11330–11536、11598、11944、11969、35216、35509、38147、49424）
- Unity 内置 RT 顶点属性：`D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Data/CGIncludes/UnityRayTracingMeshUtils.cginc:48–52,220,274`
- 项目设置：`URPSample01/ProjectSettings/ProjectVersion.txt`、`ProjectSettings.asset:531–533`、`QualitySettings.asset`
- 本计划：[HANDOFF](../HANDOFF.md)、[TLAS](TLAS.md)、[SSR-DXR](SSR-DXR.md)、[HitShading](HitShading.md)、[GI HANDOFF](../gi/HANDOFF.md)、[GI 研究](../gi/report.md)、[反射 HANDOFF](../reflection/HANDOFF.md)

### 本地源码（HDRP）

包根：`E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12/`

- SSGI：`Runtime/Lighting/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute`、`Runtime/Lighting/ScreenSpaceLighting/HDRenderPipeline.ScreenSpaceGlobalIllumination.cs`、`Runtime/Lighting/GlobalIllumination.cs`
- RTGI/RTR：`Runtime/RenderPipeline/Raytracing/HDRenderPipeline.RaytracingIndirectDiffuse.cs`、`...RaytracingReflection.cs`、`...RaytracingDeferredLightLoop.cs`、`...HDReflectionDenoiser.cs`
- RTAS：`Runtime/RenderPipeline/Raytracing/HDRaytracingManager.cs`、`...HDRTASManager.cs`、`Runtime/Lighting/RayTracingSettings.cs`
- 材质 pass：`Runtime/Material/Lit/Lit.shader`

### 本地源码（探针参考）

`E:/workspace/GitHub/RTXGI-DDGI-URP/`

- `Assets/Features/Runtime/DDGIFeature.cs`、`Assets/Features/Runtime/Overrides/DDGI.cs`
- `Assets/Features/Resources/Shaders/DDGIUpdateIrradiance.compute`、`DDGIUpdateDistance.compute`、`DDGIRayTracing.raytrace`、`LitRayTracing.shader`
- `Assets/Features/Resources/Shaders/Lib/DDGIFuncs.hlsl`、`DDGIProbeIndexing.hlsl`、`DDGIInputs.hlsl`

### 外部资料

- Epic Lumen Technical Details：https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-technical-details-in-unreal-engine
- Epic Lumen GI and Reflections：https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-global-illumination-and-reflections-in-unreal-engine
- SIGGRAPH 2022 Advances in Real-Time Rendering（课程页）：https://advances.realtimerendering.com/s2022/index.html
- SIGGRAPH 2022 Lumen slides：https://advances.realtimerendering.com/s2022/SIGGRAPH2022-Advances-Lumen-Wright%20et%20al.pdf
- NVIDIA NRC/SHaRC 介绍：https://developer.nvidia.com/blog/generative-ai-for-digital-humans-and-new-ai-powered-nvidia-rtx-lighting/
- NVIDIA RTX Kit：https://developer.nvidia.com/rtx-kit
- NVIDIA RTXGI：https://developer.nvidia.com/rtx/ray-tracing/rtxgi
- NVIDIA GDC 2024 Cyberpunk path tracing：https://www.nvidia.com/en-us/on-demand/session/gdc24-gdc1002/
- Unity 2023.1 Tech Stream（inline RT）：https://unity.com/blog/engine-platform/2023-1-tech-stream-now-available
- Unity 2023.1 手册 New in 2023.1（`supportsInlineRayTracing`、`#pragma require inlineraytracing` 仅计算着色器）：https://docs.unity.cn/cn/2023.1/Manual/WhatsNew20231.html
- Unity 2023.2 Tech Stream（inline RT 扩到光栅化+计算）：https://unity.com/blog/engine-platform/2023-2-tech-stream-now-available
- Unity 6 DirectX 12（RTAS build flags）：https://unity.com/blog/directx-12-improvements-in-unity-6
- Unity 7 / single render pipeline（UnifiedRayTracing）：https://unity.com/topics/the-path-to-a-single-render-pipeline-in-unity-7
- Unite Seoul 2026 主题演讲（URP Surface Cache GI 预览于 6.7 alpha，随 Unity 6.7 LTS 推出）：https://unity.com/blog/unite-seoul-keynote-2026-recap
- Unity 6 What's New（RTAS build flags、inline RT、异步构建）：https://docs.unity.com/en-us/engine/6000.6/manual/whats-new/unity6
- HDRP 14 Ray Tracing Getting Started：https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@14.0/manual/Ray-Tracing-Getting-Started.html
- Microsoft DXR 1.1 / RayQuery：https://microsoft.github.io/DirectX-Specs/d3d/Raytracing.html

## 12. 一句话总结

**我们做的是一条“屏幕优先 + 全材质 Fat CHS（灯表/逐灯阴影/AHS cutout）+ 世界空间 probe 低频（1 跳反馈 + relocation/classification）+ 显式天空/环境兜底”的无 Surface Cache 混合 RT 管线；HDRP 借 Fat CHS/调度/历史/降噪思想，Lumen 借“先屏幕后世界”的分层，SHaRC 留作 v2；Unity 2022.3 的能力边界决定了 v1 只能用 RayTracingShader + per-material pass + 固定 K/B×R，而不是 inline RayQuery + bindless + 压缩 + 间接派发。**


