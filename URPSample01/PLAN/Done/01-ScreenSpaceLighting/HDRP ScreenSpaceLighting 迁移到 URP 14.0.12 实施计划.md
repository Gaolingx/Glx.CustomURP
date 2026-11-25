# HDRP ScreenSpaceLighting 迁移到 URP 14.0.12 实施计划

> 状态：**T0 决策已冻结；T1 已按 R3 协议退出，并在 T2 改动后由 Run15/Run16 复验（2026-11-14，CPU p95 + 图像/显存/Pass 顺序可重复，GPU p95 为参考数据）；T2–T12 均已退出（T12 与 T13 于 2026-11-15 退出）；2026-11-14 按用户要求重新打开 T0，移除 URP 内置 SSAO（像素着色器实现），GTAO 成为唯一 AO 生产者；T14 未开始**。本文记录架构理由、范围与验收契约；后续执行入口只维护在 `PLAN/Done/01-ScreenSpaceLighting/HANDOFF.md`。GTAO、SSGI、Opaque SSR Approximation/PBR Accumulation、Transparent SSR 与 Refraction 均已实现，全组合矩阵已通过；T1 结论以 `PLAN/Done/01-ScreenSpaceLighting/validation/T1/T1Report.json` 为准，T2 结论以 `PLAN/Done/01-ScreenSpaceLighting/validation/T2/T2Verification.json` 和 `PLAN/Done/01-ScreenSpaceLighting/validation/T2/FrameDebugger/FrameDebuggerReport.json` 为准，T3 结论以 `PLAN/Done/01-ScreenSpaceLighting/validation/T3/T3Report.json` 为准，T4 结论以 `PLAN/Done/01-ScreenSpaceLighting/validation/T4/T4Report.json` 为准，T5 结论以 `PLAN/Done/01-ScreenSpaceLighting/validation/T5/T5Report.json` 为准，T7 结论以 `PLAN/Done/01-ScreenSpaceLighting/validation/T7/T7Report.json` 为准，T8 结论以 `PLAN/Done/01-ScreenSpaceLighting/validation/T8/T8Report.json` 为准，T9 结论以 `PLAN/Done/01-ScreenSpaceLighting/validation/T9/T9Report.json` 为准，T10 结论以 `PLAN/Done/01-ScreenSpaceLighting/validation/T10/T10Report.json` 为准，T11 结论以 `PLAN/Done/01-ScreenSpaceLighting/validation/T11/T11Report.json` 为准，T12 结论以 `PLAN/Done/01-ScreenSpaceLighting/validation/T12/T12Report.json` 为准，T13 结论以 `PLAN/Done/01-ScreenSpaceLighting/validation/T13/T13Report.json` 为准。
>
> 基线：Unity `2022.3.62f1`，URP/HDRP `14.0.12`。
>
> URP 目标包：`E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12`（项目与包已从 `E:/workspace/GitHub/URPSample01` 迁入当前仓库）。
>
> HDRP 源包：`E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12`。
>
> RenderDoc 源包：`C:\Program Files\RenderDoc`。
>
> 路径约定：下文 `URP/`、`HDRP/` 分别代表上述两个包根目录；Shader include 使用 Unity canonical 包名 `Packages/com.unity.render-pipelines.universal/...`，不包含物理目录中的 `@14.0.12`。

## 1. 目标、范围与交付形态

### 1.1 功能目标

在 URP 14.0.12 中迁移 HDRP 的非 DXR 屏幕空间光照能力：

- **独立 GTAO RendererFeature**：全/半分辨率、空间降噪、时域降噪、双边上采样；设置方式参考 URP 内置 SSAO。
- **Screen Space Lighting RendererFeature**：统一管理 SSGI、SSR、Refraction 所需 Pass 和资源。
- SSGI：Hi-Z tracing、上一帧颜色重投影、时域/空间降噪、双边上采样、历史失效处理。
- SSR：不透明 Approximation 与 PBR Accumulation、速度拒绝、粗糙度/屏幕边缘淡出；随后完成透明 SSR 与 clear coat。
- Screen Space Refraction：透明 Lit 中的 proxy raycast、当前帧颜色金字塔采样、深度拒绝、透射率与屏幕边缘淡出。
- Forward、Forward+、Deferred，以及 Deferred 中的 ForwardOnly 几何。
- 共享内置 Motion、深度/颜色金字塔、逐相机/逐眼历史、调试视图、能力检测和构建保留规则。

### 1.2 两个开发部分

1. **URP Core Support**：修改 URP 内置 C# 调度、Lit/ShaderGraph 材质和 DepthNormals，为屏幕空间效果提供通用输入、时序与消费契约。
2. **RendererFeature Plugin**：尽量将全部 compute、降噪、历史、Volume 参数、Feature 设置、资源和 Pass 编排放在新增目录中，通过 RendererFeature 接入。

首版插件代码仍位于 `Unity.RenderPipelines.Universal` 程序集内，因为需要使用 URP 14 的 internal renderer 生命周期与构建设施；“插件”指**目录、依赖、资源和运行生命周期边界**，不是首版就额外建立独立 asmdef/package。Core 不得引用具体插件 Feature、Module 或 Volume 类型；移除插件源码后，保留的 Core 必须能编译并走原始 GI/AO 路径。

### 1.3 非目标

首版明确不迁移：

- RTAO、RTGI、RTR、Mixed ray-traced 模式、DXR RTAS、RT light cluster、path tracing。
- HDRP pre-exposure、HDRP GBuffer 布局、HDRP stencil 位定义、HDRP RenderGraph/HDCamera 框架。
- Async compute 作为默认能力；先保证同步执行正确，再单独立项优化。
- 让所有自定义 Shader 自动获得 SSGI/SSR/Refraction。未适配材质必须回退到原有 URP GI/probe，不采样无效数据。
- 首版建立独立插件程序集或把 HDRP C# partial class 原样搬入 URP。

## 2. T0：架构边界与最终决策

> **冻结基线（2026-11-14）**：第 1.2–1.3、2、5.2、6、7、9 节规定的交付边界、依赖方向、资源所有权、Pass 顺序和数据契约为 T1–T14 的实施基线。后续任务不得静默改回单一总 Feature、插件私有 Motion、UACD 插件历史、跨 Feature 全局 Hi-Z 或 Deferred GBuffer 后叠加；确需变更时必须重新打开 T0，记录原因、兼容性影响、测试影响和替代方案，并同步更新本文与 `PLAN/Done/01-ScreenSpaceLighting/HANDOFF.md` 后再实施。测试资产、性能数值和验证证据不属于架构冻结内容，可按实际结果补充。

### 2.1 依赖方向与模块责任

```text
URP Core Support
├─ UniversalRenderer：按最早消费事件调度内置 Depth/DepthNormals/Motion
├─ 内置材质：DepthNormals 有效 smoothness、Forward/GBuffer GI 消费
├─ 透明材质 prepass、SSR/Refraction 消费所需的材质数据与 Shader Pass
└─ 通用 keyword 清理、材质端输入声明与 neutral fallback
        ↑ 只允许单向依赖
RendererFeature Plugin（新增代码，仍在 URP 程序集）
├─ ScreenSpaceCommon：算法工具、packed mip 工具、历史存储工具；不是 Feature
├─ GroundTruthAmbientOcclusionRendererFeature：独立 GTAO
└─ ScreenSpaceLightingRendererFeature：SSGI + SSR + Refraction
   ├─ SSGI / SSR / Refraction VolumeComponent
   ├─ SSL 私有 Hi-Z、Color Pyramid、Previous Color/Depth、历史
   └─ Trace / Reproject / Denoise / Upsample / Debug Pass
```

| 责任 | Core | GTAO Feature | ScreenSpaceLighting Feature |
|---|---|---|---|
| DepthNormals、Depth、内置 Motion 的生成与时序 | 负责 | 声明输入需求 | 声明输入需求 |
| AO compute、AO 历史、AO 参数与 SSAO 冲突处理 | 只提供 AO 消费语义 | 负责 | 不负责 |
| SSGI/SSR Hi-Z | 不负责 | 使用自己的私有 Hi-Z | 负责，SSGI/SSR 共用一份 |
| Color Pyramid、上一帧颜色/深度 | 不负责 | 不负责 | 负责 |
| 插件历史所有权与释放 | 不持有插件字段 | 自己持有 AO 历史 | 自己持有 SSGI/SSR/颜色历史 |
| GI 混合、材质 fallback、DepthNormals 编码 | 负责 | 不写材质 GI | 提供有效纹理和权重 |
| Volume 参数 | 不定义具体类型 | 无 Volume | 定义并读取 SSGI/SSR/Refraction |
| 构建时的效果 variant 精简 | 不错误删除新 keyword | 插件 Editor 预处理器按 Feature 存在性处理 | 同左 |

**T0 已否决的旧方案：**不再设置“包含 GTAO 的总 Feature”；不在 `UniversalAdditionalCameraData` 增加插件专用历史字段；不复制 `_SslMotionVectorTexture` 和第二套 object motion；不允许两个 Feature 通过静态全局资源互相隐式供应数据。

### 2.2 两个 Feature 与 Volume 的配置契约

| 项目 | GroundTruthAmbientOcclusion | ScreenSpaceLighting |
|---|---|---|
| 添加位置 | `UniversalRendererData.rendererFeatures` | 同左 |
| Feature 设置 | 分辨率、强度、半径、direct/indirect、降噪、时域、质量、debug；Inspector 类似 SSAO | Compute/纹理资源引用、debug、能力与资源上限；效果参数由 Volume 提供 |
| Volume | 无 | `Lighting/Screen Space Global Illumination`、`Lighting/Screen Space Reflection`、`Lighting/Screen Space Refraction` |
| 启用条件 | Feature active、设置有效、无 SSAO 冲突、平台支持 | Feature active、对应 Volume active 且 `enabled=true`、参数有效、平台支持 |
| 参数优先级 | 单个 Feature 序列化设置 | Volume stack 的标准 override/混合结果；Feature 不另设一套同名效果参数 |

Volume **不直接添加 Pass**。Feature 在每相机 `AddRenderPasses` 中从 `renderingData.cameraData.volumeStack` 读取最终组件值，再决定各 Pass 是否入队。它们不是 `IPostProcessComponent`，不受相机 Post Processing 开关控制；关闭后处理不应顺带关闭 SSGI/SSR。

模块开关的运行时变化不得触发构建期误裁剪：只要任一使用中的 Renderer Data 含对应 Feature，就保留其可能运行的材质变体；Volume 当前是否勾选 `enabled` 不能作为 strip 依据。

### 2.3 跨 Feature 资源与冲突策略

- **内置 Motion 共用**：Core 将现有 `MotionVectorRenderPass` 提前到最早消费者之前，TAA/Motion Blur 继续读取 `_MotionVectorTexture`。
- **Hi-Z 代码共用、资源隔离**：SSGI/SSR 共用 SSL 私有金字塔；GTAO 自己拥有另一份金字塔。两个 Feature 同时开启时，首版允许重复的 Hi-Z GPU/显存成本。
- 这是有意的首版权衡：换取 GTAO 可独立使用、Feature 列表顺序无关、禁用任一 Feature 不影响另一个。后续若要合并，必须先设计通用 per-renderer/per-camera 资源 broker 和引用计数，另立优化任务，不在算法交付中隐式跨 Feature 查找。
- **AO 单生产者（2026-11-14 修订）**：按用户要求已移除 URP 内置 `ScreenSpaceAmbientOcclusion`（像素着色器实现）、其 Editor、Shader、SSAO 专用 keyword 与 BlueNoise256 资源。GTAO 成为唯一 AO 生产者，原先的“内置 SSAO 优先、GTAO 警告并禁用”冲突规则随实现一并退役（`ScriptableRenderer.GetRendererFeature<T>()` 已删除）。`_SCREEN_SPACE_OCCLUSION`、`_ScreenSpaceOcclusionTexture`、`_AmbientOcclusionParam` 与 `AmbientOcclusion.hlsl` 继续作为材质侧契约保留，由 GTAO 写入。构建期 AO keyword 预过滤固定为 `Select`（保留 ON/OFF 两个变体），插件级 strip 优化留到 T14。
- Core 可增加通用 `ScriptableRenderer.GetRendererFeature<T>()` 供冲突检测使用，但不得增加 `GetGtaoFeature()` 等专用 API。
- GTAO、SSL 的中间纹理、CB、history 和 debug ID 必须带各自前缀；只有本文明确规定的最终材质/AO 输入可以共享全局名称。

### 2.4 历史所有权与生命周期

每个 Feature 实例拥有自己的 history store，以相机和 XR eye 为键，不修改 `UniversalAdditionalCameraData`：

- `Camera + passId/eye` 定位历史；single-pass 使用对应 array slice，multipass 不共用历史。
- 每次相机渲染先读取 previous，trace/denoise 全部完成后写 next，帧末再交换索引。
- 读取 `UniversalAdditionalCameraData.resetHistory` 作为统一相机重置信号，同时检查尺寸、render scale、算法、分辨率、denoise 设置和颜色历史 generation。
- Feature 每次活跃渲染时清理已销毁相机的 entry；`Dispose()` 释放该 Feature 的全部相机历史、RTHandle、ComputeBuffer 和 Material。
- 相机长期不再渲染但对象仍存活时，按可配置的最大缓存相机数/未使用帧数回收；相机 cut、销毁、Domain Reload、Renderer Feature 删除均有自动测试。
- Preview、reflection、offscreen-depth、Overlay 等按能力表禁用或隔离，禁止递归捕获和跨相机污染。

| 历史 | 所有者 | 主要失效条件 |
|---|---|---|
| AO 双份 | GTAO Feature | 尺寸、scale、相机 cut、算法/分辨率切换 |
| SSL depth pyramid | SSL Feature，帧内资源 | 每帧重建，不跨帧复用 |
| GTAO depth pyramid | GTAO Feature，帧内资源 | 每帧重建，不跨帧复用 |
| SSR accumulation 双份 | SSL Feature | 首帧、颜色历史无效、算法切换、尺寸变化 |
| SSGI HF/LF | SSL Feature | denoise 设置、分辨率、相机/历史深度失效 |
| Color pyramid 双份 | SSL Feature | 尺寸/render scale/XR 布局变化 |
| Previous depth/normal | SSL Feature | 与颜色历史同步，逐眼保存 |

### 2.5 用户配置入口与验收步骤

“从 Pipeline Asset 导入 Feature”在 URP 14 的准确操作是：

1. 在 Project 窗口选择 URP Pipeline Asset，定位其 **Renderer List**，打开对应 `UniversalRendererData`。
2. 在 Renderer Data 的 **Renderer Features** 列表点击 **Add Renderer Feature**。
3. 添加 `Screen Space Lighting`；需要 GTAO 时再添加 `Ground Truth Ambient Occlusion`。Forward、Forward+、Deferred 使用不同 Renderer Data 时分别配置。
4. 创建/选择 Global Volume 和其 Profile，点击 **Add Override > Lighting**，添加 SSGI、SSR、Refraction 组件并勾选对应 `enabled`。
5. 确认相机使用目标 Renderer、Volume Mask 能命中该 Global Volume，且相机不处于不支持类型。
6. GTAO 只在其 Feature Inspector 中调参；内置 SSAO 已于 2026-11-14 移除，不再存在两个 AO Feature 的取舍。

T3 必须按以上真实 UI 路径做一次手工验收并保留截图；只通过脚本创建对象不算完成该验收。

### 2.6 T0 修订：移除内置 SSAO（2026-11-14）

**决策**：在 T5 验证 GTAO 于 Forward/Forward+/Deferred 正常工作后，按用户要求删除 URP 内置 SSAO（像素着色器实现）。GTAO 成为唯一 AO 生产者。

**删除范围**：`Runtime/RendererFeatures/ScreenSpaceAmbientOcclusion.cs`、`Editor/RendererFeatures/ScreenSpaceAmbientOcclusionEditor.cs`、`Shaders/Utils/ScreenSpaceAmbientOcclusion.shader`、`ShaderLibrary/SSAO.hlsl`、`Textures/BlueNoise256/`，以及 Shader Build Preprocessor / Shader Stripper / Asset Prefiltering 中全部 SSAO 专用设置、keyword 和 `ShaderFeatures.ScreenSpaceOcclusionAfterOpaque` 分支。

**保留契约**：`_SCREEN_SPACE_OCCLUSION`、`_ScreenSpaceOcclusionTexture`、`_AmbientOcclusionParam`、`AmbientOcclusion.hlsl`、Deferred 的 AO-only pass（重命名为 `AmbientOcclusionOnly`）继续存在，由 GTAO 写入；材质端无需改动。

**影响**：原 §2.3 的“内置 SSAO 优先、GTAO 警告并禁用”规则退役，`ScriptableRenderer.GetRendererFeature<T>()` 删除；已序列化 SSAO Feature 的旧 Renderer Data 会出现 Missing Script，需要手动删除并改用 GTAO。构建期 AO keyword 预过滤固定为 `Select`（ON/OFF 都保留）；插件级 strip 优化留到 T14。T5 验证器、EditMode 测试和证据已同步更新并重新生成。

旧草稿基于 Unity 6000.5/URP 17，以下结论不能直接用于当前 14.0.12：

| 旧假设 | 14.0.12 实际情况 | 本计划处理 |
|---|---|---|
| 使用 RenderGraph、`AddComputePass`、`TextureHandle` | 当前项目主路径为 Compatibility `ScriptableRenderPass.Execute` | 用 `CommandBuffer.DispatchCompute`、`ConfigureTarget`、`RTHandle` |
| 使用 `UniversalCameraHistory` | 当前版本没有该通用历史系统 | Feature 自管逐相机、逐 XR eye 历史 |
| 已有 `_SCREEN_SPACE_IRRADIANCE` 注入缝 | 当前 `GlobalIllumination.hlsl` 没有 SSGI/SSR 消费 | Core 新增统一输入/混合头和 keyword |
| Deferred 在 GBuffer 后追加 SSR/SSGI 即可 | GBuffer3 已是 `emission + GI` | 在 GBuffer 前生成并在 GBuffer 材质阶段消费 |
| DepthNormals alpha 可直接当 smoothness | 当前固定为 0，且 smoothness=0 也是合法值 | 使用“有效标记 + smoothness”编码，不能裸写 0–1 |
| 请求 Motion 输入便可在光照前读取 | 内置 MotionVectors 默认在 depth copy 后 | Core 同时提前内置 Motion 与所需 depth copy |
| `_CameraOpaqueTexture` 可替代颜色金字塔 | 单张当前帧拷贝，不是历史 Gaussian mip chain | SSL 自管 ping-pong 颜色历史与 mip |
| URP TAA 历史可直接复用 | TAA accumulation 是后处理专用 | 独立管理效果历史及 reset generation |
| 透明 SSR 可复用 opaque normal/depth | HDRP 在透明 SSR 前执行 transparent depth/normal prepass | Core 新增透明材质 prepass，SSL 使用独立透明输入 |

URP 生命周期依据：`URP/Runtime/ScriptableRendererFeature.cs:20-54`、`:92-105`，`URP/Runtime/Passes/ScriptableRenderPass.cs:370-379`。Render target 初始化后通过 `SetupRenderPasses` 获取，不能在 `AddRenderPasses` 阶段假定 `cameraColorTargetHandle` 已有效。

## 4. HDRP 14.0.12 源码盘点

### 4.1 目录内模块

`HDRP/Runtime/Lighting/ScreenSpaceLighting/` 共 30 个实现源码文件、35 个 compute kernel，不含 `.meta`。

| 模块 | 文件与 kernel | 移植判定 |
|---|---|---|
| GTAO | `GTAO.compute:GTAOMain`；`GTAOSpatialDenoise.compute:SpatialDenoise`；`GTAOTemporalDenoise.compute`；`GTAOCopyHistory.compute:GTAODenoise_CopyHistory`；`GTAOBlurAndUpsample.compute` 5 kernels | 保留算法，替换 include、normal/depth/motion 解码、CB、历史和输出语义 |
| GTAO 共用 | `GTAOCommon.hlsl`、`ScreenSpaceAmbientOcclusion.cs`、`ShaderVariablesAmbientOcclusion.cs(.cs.hlsl)` | 参数/CB 可参考，C# 编排重写；删除 RTAO 设置 |
| SSGI | `ScreenSpaceGlobalIllumination.compute` 的 `TraceGlobalIllumination[Half]`、`ReprojectGlobalIllumination[Half]` | 保留 trace/reproject 主体；重写坐标、历史、fallback 和材质消费 |
| SSGI 共用 | `RayMarching.hlsl`、`RayMarchingFallbackHierarchy.cs(.cs.hlsl)`、`ScreenSpaceGlobalIllumination.cs(.cs.hlsl)` | Hi-Z 算法可保留；枚举仅保留 ScreenSpace/Off 语义 |
| Upsample | `BilateralUpsample.compute` 2 kernels、`BilateralUpsample.hlsl`、`BilateralUpsampleDef.cs(.cs.hlsl)` | 保留数学过程，替换 HDRP 工具头与全局纹理约定 |
| SSR | `ScreenSpaceReflections.compute`：Tracing、Reprojection、9 个普通 Accumulate + 9 个 Debug kernel | 保留 Trace/Reproject；Accumulation 按设置保留组合，不删除速度拒绝能力 |
| SSR 设置/CB | `ScreenSpaceReflection.cs(.cs.hlsl)`、`ShaderVariablesScreenSpaceReflection.cs(.cs.hlsl)` | 删除 RTR 字段；Approx/PBR 参数与 CB 布局重新生成 |
| 共用/折射 | `ScreenSpaceLighting.hlsl`、`ShaderVariablesScreenSpaceLighting.hlsl`、`ScreenSpaceRefraction.cs`、`ScreenSpaceTracing.hlsl` | 保留 edge fade 和 proxy 数学；折射是透明材质逻辑，不是独立 compute |

关键源码位置：

- GTAO 总顺序：`HDRP/Runtime/Lighting/ScreenSpaceLighting/HDRenderPipeline.AmbientOcclusion.cs:147-189`。
- SSGI trace/reproject：`HDRP/Runtime/Lighting/ScreenSpaceLighting/HDRenderPipeline.ScreenSpaceGlobalIllumination.cs:171-300`；denoise：`:385-453`。
- SSR 主 C# 编排：`HDRP/Runtime/RenderPipeline/HDRenderPipeline.LightLoop.cs:1181-1513`。
- 透明 SSR 前置条件：同文件 `:1214-1227`、`:1267-1269`、`:1352-1355`；透明 depth/normal prepass 位于 `HDRenderPipeline.RenderGraph.cs:1161-1185`、`:1373-1379`。
- SSR 材质消费：`HDRP/Runtime/Material/Lit/Lit.hlsl:1823-1887`；RGB 是 radiance，A 是 hierarchy/混合权重。
- SSGI 材质消费：`HDRP/Runtime/Lighting/LightLoop/LightLoop.hlsl:455-480`。
- 折射消费：`HDRP/Runtime/Material/Lit/Lit.hlsl:1890-1974`。

### 4.2 必须一起移植的目录外非 DXR 依赖

| 依赖 | 源文件 | 用途 |
|---|---|---|
| 深度金字塔 | `Runtime/RenderPipeline/RenderPass/DepthPyramid.compute`、`MipGenerator.cs`、`DepthPyramidConstants.cs(.cs.hlsl)`、`HDUtils.PackedMipChainInfo` | Hi-Z packed atlas、mip offsets、reverse-Z 最近深度 |
| 颜色金字塔 | `Runtime/RenderPipeline/RenderPass/MipGenerator.cs` | Gaussian downsample、横纵滤波、XR/DRS 处理参考 |
| SSGI 时域降噪 | `Runtime/RenderPipeline/Raytracing/HDTemporalFilter.cs`、`Shaders/Denoising/TemporalFilter.compute` | history validation、accumulation、copy |
| SSGI 空间降噪 | `Runtime/RenderPipeline/Raytracing/HDDiffuseDenoiser.cs`、`Shaders/Denoising/DiffuseDenoiser.compute` | bilateral/gather；裁掉 DXR 无关支路 |
| 采样工具 | `Runtime/RenderPipeline/Raytracing/Shaders/RayTracingCommon.hlsl`、`RaytracingSampling.hlsl`、蓝噪声工具 | cosine/GGX sampling、低差异序列、时域采样 |

### 4.3 已记录的有意差异

- `RayMarching.hlsl:124-129` 是单最近深度而非 min-max，朝相机/物体背面追踪有额外成本，mip 被钳制到 6。
- HDRP 14.0.12 只有半分辨率 SSGI 路径把 denoised 结果赋回 colorBuffer。URP 版全/半分辨率都消费 denoised 结果，这是**有意修复的差异**，必须保留 raw/denoised A/B 图，不宣称逐像素等同 HDRP。
- HDRP 历史颜色带 pre-exposure；URP 使用场景线性 HDR 历史，删除 current/previous exposure 换算。
- HDRP AO 是 inverse AO、黑色 neutral；URP AO 是白色 neutral 的正向可见度因子，输出前转换。
- 首版 GTAO 与 SSL 的 Hi-Z 不跨 Feature 合并；透明折射金字塔和 decal 输入差异见第 6、7 节。

## 5. URP 接入依据与 Deferred 决策

### 5.1 三条路径的关键事实

- Forward/Forward+ 的 Lit 最终进入 `UniversalFragmentPBR`，再调用完整参数 `GlobalIllumination`：`URP/ShaderLibrary/Lighting.hlsl:257-292`，`URP/ShaderLibrary/GlobalIllumination.hlsl:353-383`。
- Forward+ probe atlas/cluster 路径位于 `GlobalIllumination.hlsl:175-209`；普通 Forward 双 `unity_SpecCube` 路径位于 `:210-268`。fallback 留在材质端可保留两条路径差异。
- Deferred Lit 在 `URP/Shaders/LitGBufferPass.hlsl:211-222` 已计算 GI，并把 `surfaceData.emission + color` 写入 GBuffer3。
- GBuffer3 布局见 `URP/ShaderLibrary/UnityGBuffer.hlsl:206-210`；`SurfaceDataFromGbuffer` 无法分离 emission：`:152-170`。
- AfterGbuffer 自定义 Pass 会影响 framebuffer fetch：`URP/Runtime/UniversalRenderer.cs:643-653`、`URP/Runtime/DeferredLights.cs:362-368`。
- Normal 的最早消费事件在 GBuffer 前时，Deferred DepthNormals prepass 覆盖全部 `DepthNormals/DepthNormalsOnly` 几何：`URP/Runtime/UniversalRenderer.cs:1005-1036`。
- 内置 Motion Pass 构造在 depth copy 之后：`URP/Runtime/UniversalRenderer.cs:286-299`；现有输入汇总只有布尔 Motion，没有最早 Motion 事件：`:1437-1495`。

### 5.2 Deferred 首版固定为 Pre-GBuffer 消费

GBuffer 后直接叠加会重复环境 GI；要替换又必须从 `emission + indirect diffuse + indirect specular` 中分离各项，现有 GBuffer3 信息不足。因此首版统一为：

```text
DepthNormals(encoded normal + smoothness) + Built-in Early Motion + Hi-Z + Previous History
    -> GTAO / SSGI / SSR
    -> Deferred GBuffer 材质调用与 Forward 相同的 GI 混合
    -> DeferredLights 只追加直接光，并按现有规则应用屏幕空间 AO
```

收益是不分解 GBuffer3、不重复 emission/GI，Forward、ForwardOnly、Deferred 共用 GI 混合，并避开 AfterGbuffer framebuffer-fetch 回退。代价是 Deferred 多一次 DepthNormals prepass，且 tracing 使用 prepass 材质数据。GBuffer 后独立 indirect lighting pass 仅作为后续优化，不在首版范围。

## 6. 帧内 Pass 顺序与资源所有权

### 6.1 不透明 GTAO/SSGI/SSR

事件值来自 `URP/Runtime/Passes/ScriptableRenderPass.cs:51-148`。实现可微调偏移，但必须保持依赖关系，不得依赖 Feature 列表排列顺序。

| 事件建议 | Pass | 所有者与说明 |
|---|---|---|
| 150 | URP DepthNormals | Core；扩展 normal alpha 编码；Deferred 覆盖 GBuffer 与 ForwardOnly 几何 |
| 200–201 | depth priming copy/resolve、DBuffer、所需 CopyDepth | Core；Motion 消费提前时，CopyDepth 也必须提前，Deferred 从 prepass depth 复制 |
| 202 | URP MotionVectors | Core；复用 `_MotionVectorTexture`，生成 camera/object motion |
| 203 | GTAO Depth Pyramid | GTAO；仅 GTAO 使用 |
| 203 | SSL Depth Pyramid | SSL；SSGI/SSR 共用，和 GTAO 资源不同名 |
| 204 | GTAO dispatch 组 | GTAO；Main -> Spatial -> Temporal -> Upsample |
| 205 | SSGI dispatch 组 | SSL；Trace -> Reproject -> Temporal/Diffuse Denoise -> Upsample |
| 206 | Opaque SSR dispatch 组 | SSL；Trace -> Reproject -> Accumulate |
| 207 | SSL material bindings/debug | SSL；无有效输出时绑定 neutral 且权重为 0 |
| **210** | **Deferred GBuffer** | Core 材质消费 SSGI/SSR；GTAO 仍由现有 DeferredLights 规则消费 |
| **250** | **Forward/Forward+ Opaque、Deferred ForwardOnly** | Core 材质调用同一 GI 混合 |

Core 输入汇总需新增最早 Motion 消费事件，并把内置 Motion 移到该事件之前，同时更新 depth 的最早事件。没有提前消费者时保持原有时序；若消费者早于可生成的 DepthNormals/Depth，能力检测必须拒绝该插件 Pass，不能静默读取旧深度。提前后的 Motion 同时供后续 TAA/Motion Blur 使用，不再生成第二份。

### 6.2 透明、折射与历史颜色

| 事件建议 | Pass | 数据口径 |
|---|---|---|
| 400–402 | Current Color Pyramid（折射） | SSL；opaque + sky 后、transparent 前；首版不包含 pre-refraction transparents |
| 447 | Transparent Depth/Normal Prepass | SSL 编排、Core 材质 Pass；写独立 transparent depth/normal，不覆盖 opaque Hi-Z，不误用其背后 opaque 表面 |
| 449 | Transparent SSR | SSL；ray origin 来自 transparent depth/normal，marching 仍用 opaque Hi-Z，颜色来自上一帧 color pyramid；只使用 Approximation |
| 450 | URP Transparent | Core 透明 Lit 消费独立 SSR 纹理；折射材质执行 proxy raycast 并采样当前帧 color pyramid |
| 501 | Previous Color History Capture + Pyramid | SSL；捕获 transparent 后、post 前的场景线性 HDR，供下一帧 SSR/SSGI |
| 550 后 | Debug | 仅开启调试时写入，不改变正常输出 |

透明 prepass 使用新的 `UniversalTransparentDepthNormals` LightMode，仅对显式支持 Transparent SSR 的材质启用。独立 depth target 从当前 opaque depth 初始化，避免改变主透明 Pass 的深度测试语义。透明 object motion 若首版未单独生成，必须作为已知限制验证；不得把 opaque motion 宣称为透明物体运动。

当前帧折射金字塔不包含 pre-refraction transparents 是首版差异。若要求 HDRP 顺序等价，后续需拆分 URP transparent queue：pre-refraction transparents -> color pyramid -> refractive transparents。

## 7. 数据与 Shader 契约

### 7.1 Normal、smoothness 与有效材质标记

仅在 **DepthNormals 输出**中使用以下编码，不能把 smoothness=0 与不支持材质的 alpha=0 混为一谈：

```hlsl
encodedAlpha = supported ? 0.5 + 0.5 * smoothness : 0.0;
valid = encodedAlpha > 0.0;
smoothness = saturate(encodedAlpha * 2.0 - 1.0);
```

- 修改 Lit/ComplexLit 共用的 `LitDepthNormalsPass.hlsl`，并按支持矩阵处理 SimpleLit、Particles Lit、Terrain Lit、ShaderGraph DepthNormals。
- Unlit、BakedLit、未适配自定义 Shader 保持 alpha=0；SSR 对这些像素 weight=0，GTAO 仍可在只有 normal/depth 时工作。
- Deferred prepass 的编码值会被正常 GBuffer Pass 重写；最终 GBuffer2 alpha 仍是原有 0–1 smoothness，DeferredLights 不得读取 prepass 编码语义。
- 首版 tracing 使用 base normal/base roughness；clear coat 需要独立 mask/roughness，不从 base alpha 猜测。
- Forward DBuffer 在 201，DepthNormals 在 150；Deferred GBuffer Decal 在 220。首版 tracing 使用 **decal 前** normal/roughness，列入限制和测试。

### 7.2 Built-in Early Motion

- 参考 `URP/Runtime/Passes/MotionVectorRenderPass.cs:68-120` 的逐眼矩阵和 camera/object 绘制，以及 `:154-207` 的深度重建/foveation。
- 使用 `_MotionVectorTexture`，不再新增 `_SslMotionVectorTexture`；内置 TAA/Motion Blur 回归必须证明提前后结果不变。
- 保持 `R16G16_SFloat` 和 NDC 符号约定，对静止相机、动态物体、XR 和 camera cut 写测试。
- Core 修改的是通用输入调度，不出现 GTAO/SSGI/SSR 类型判断。

### 7.3 Depth/Color Pyramid

- 保留 HDRP packed atlas + mip offsets，减少 `RayMarching.hlsl` 改动；reverse-Z 用 max reduction，非 reverse-Z 用 min。T4 已按 HDRP 单通道布局固定为 `R32_SFloat` 原始设备深度，并在 compute 中显式绑定 `UniversalRenderer.m_DepthTexture`（command-buffer 全局在 CPU 侧不可读）。
- GTAO 与 SSL 分别分配 pyramid 和 offset ComputeBuffer；Pass 用显式 compute 参数绑定，不通过未定义所有权的全局纹理互相读取。
- 颜色金字塔保留 Gaussian downsample 和横/纵滤波；mip 限制、UV scale/limit 随 viewport/render scale 更新。
- RTHandle 使用实际 viewport 尺寸、MSAA=1；历史颜色为线性 HDR，不经过 post exposure、tonemapping 或 TAA 输出。
- UAV 格式启动时检查 `FormatUsage.LoadStore`，不支持时仅关闭对应模块并给出原因。

### 7.4 GI 混合与 AO

Core 新增材质端输入/混合头，Plugin 只负责提供有效纹理和权重：

```hlsl
// 伪代码：权重包含有效历史、hit confidence、roughness fade 和 edge fade。
indirectDiffuse  = lerp(bakedGI, ssgi.rgb, ssgiWeight);
indirectSpecular = lerp(environmentSpecular, ssr.rgb, ssr.a * ssrWeight);
color = EnvironmentBRDF(brdfData, indirectDiffuse, indirectSpecular, fresnelTerm);
// clear coat 单独处理；AO 只在现有返回路径乘一次。
```

- SSGI miss/history invalid 时 weight=0，保留 `bakedGI`、SH/APV/lightmap。
- SSR miss 时 `ssr.a=0`，不能把黑色 radiance 当有效反射压黑环境。
- 不在 compute 中绑定 HDRP `g_vLightListTile`、`EnvLightData`；Forward+/Forward 的 probe 差异由原材质函数处理。
- GTAO 输出白色 neutral 的 `_ScreenSpaceOcclusionTexture`；Forward 与材质 AO 取 min，Deferred 保留 `StencilDeferred.hlsl` 的 direct/indirect 修正。
- Deferred 在 `LitGBufferPass.hlsl` 调用带 `positionWS + normalizedScreenSpaceUV` 的完整 GI 重载。
- Core 的 `ClearRenderingState` 每相机禁用新增全局 keyword；SSL 在无输出和 `OnCameraCleanup` 时恢复 neutral/black 与 weight=0，避免污染后续相机。
- 折射使用 URP 现有 `unity_SpecCube0_BoxMin/BoxMax/ProbePosition/HDR` 等数据构造 box proxy；URP 没有 HDRP `EnvLightData`，必须在支持矩阵中说明 proxy 模型差异。

### 7.5 Refraction 材质契约

URP 14 Lit 没有 HDRP 的完整折射参数，不能只添加一个采样函数就宣称完成。Core 需明确新增并序列化：折射开关/模型、IOR、厚度、transmittance、roughness mip 和 Transparent SSR receive 开关；材质 GUI、keyword、ShaderGraph 对应字段和旧材质默认值必须保持原画面。

Refraction 只在显式支持的透明 Lit/ShaderGraph Lit 中启用。无有效 proxy、深度拒绝或屏幕边缘 weight=0 时走原 URP probe/透明路径；不读取本帧尚未写入的颜色。

## 8. 模块实施要点

### 8.1 独立 GTAO

1. 移植 Main、Spatial、Temporal、CopyHistory、必要 Blur/Upsample kernels。
2. 使用 GTAO 私有 Hi-Z、URP normal/depth 和内置 Motion；AO 历史由 Feature 自管。
3. CBuffer 用 `[GenerateHLSL]` 生成并校验 C#/HLSL 布局。
4. 无 Motion/历史无效时使用空间结果并清历史；输出转换为 URP AO 语义。
5. GTAO 是唯一 AO 生产者（2026-11-14 起内置 SSAO 已移除）；实现白色 neutral 清理与禁用恢复基线。
6. 验证三路径、ForwardOnly、全/半分辨率、动态物体、相机切换和与 SSL 的任意 Feature 顺序。

### 8.2 SSGI

1. 移植 full/half Trace 与 Reproject，输出 hit point、radiance 和有效权重。
2. 接入上一帧 color pyramid、previous depth 和内置 Motion，去除 pre-exposure。
3. 移植 TemporalFilter validation/accumulation/copy 与 DiffuseDenoiser 必要支路。
4. 增加 HF/LF 历史、history validity、首帧清屏和相机 reset。
5. 半分辨率 BilateralUpsample；全分辨率也返回 denoised 结果。
6. miss 由原 bakedGI fallback；APV 继续走 URP 原路径。
7. 验证屏幕外/disocclusion/高速物体、miss 不压黑、emission 不重复。

### 8.3 Opaque SSR 与 clear coat

1. 先完成 Approximation：Trace -> Reproject，RGBA16F radiance + weight。
2. 保留 roughness/edge fade、off-screen reject、非有限颜色拒绝、天空/探针回退。
3. 再完成 PBR Accumulation：双历史、3 类 speed rejection、surface/hit/both 组合；公开设置必须真实选择对应 kernel。
4. 接入 base layer `EnvironmentBRDF`，确认 FGD/BRDF 只乘一次。
5. clear coat 增加独立 mask/roughness 数据，按 hierarchy weight 处理 base/coat，避免双重 probe。

### 8.4 Transparent SSR 与 Refraction

1. Core 增加透明 depth/normal Shader Pass；SSL 在 447 渲染独立透明输入，不能复用 opaque 像素。
2. Transparent SSR 只用 Approximation，使用上一帧颜色金字塔、透明 ray-origin depth/normal 和 opaque Hi-Z。
3. 不透明/透明 SSR 使用独立输出和 keyword；不支持材质回退 probe。
4. 移植 proxy raycast，映射到 URP probe box 数据；完成深度拒绝、roughness mip、transmittance 和 edge fade。
5. 记录透明 motion、pre-refraction transparent 顺序和 probe proxy 模型差异，不用“与 HDRP 完全一致”掩盖。

## 9. 文件级改动清单

### 9.1 Core：必改现有文件

| 文件 | 责任与改动 |
|---|---|
| `URP/Runtime/UniversalRenderer.cs` | 汇总最早 Motion 事件；同步提前 Motion/CopyDepth；保持无插件时原时序；不在 AfterGbuffer 插入新 Pass |
| `URP/Runtime/ScriptableRenderer.cs` | 通用 Feature 查询（如需要）；每相机清理新增全局 keyword；不包含具体插件类型 |
| `URP/ShaderLibrary/GlobalIllumination.hlsl` | 完整参数重载中混合 SSGI/SSR，保留 Forward/Forward+ fallback 与 AO 语义 |
| `URP/Shaders/LitGBufferPass.hlsl` | 传 positionWS/screen UV 到完整 GI 重载，在 GBuffer 阶段消费 |
| `URP/Shaders/LitDepthNormalsPass.hlsl` | DepthNormals alpha 写有效 smoothness 编码，两个 normal 分支一致 |
| `URP/Shaders/SimpleLitDepthNormalsPass.hlsl`、`Particles/ParticlesDepthNormalsPass.hlsl`、`Terrain/TerrainLitDepthNormalsPass.hlsl` | 仅对支持的 PBR 材质写编码；其余明确无效 |
| `URP/Shaders/Lit.shader`、`ComplexLit.shader`、`SimpleLit.shader`、支持透明效果的 Shader | GI keyword/variant；透明 depth/normal Pass；折射/SSR receive 材质属性 |
| `URP/Shaders/LitForwardPass.hlsl`、`LitInput.hlsl`、`SurfaceData.hlsl` 及 ComplexLit 对应文件 | 透明 SSR/Refraction 消费和材质数据；base/coat 分离 |
| `URP/Editor/ShaderGUI/Shaders/LitShader.cs`、`ShaderGUI/ShadingModels/LitGUI.cs`、相关 GUI | 新属性的 Inspector、keyword、默认/迁移行为 |
| `URP/Editor/ShaderGraph/Includes/PBRForwardPass.hlsl`、`PBRGBufferPass.hlsl`、`DepthNormalsOnlyPass.hlsl` | ShaderGraph GI 参数、透明 prepass 输入、smoothness 编码 |
| `URP/Editor/ShaderGraph/Targets/UniversalLitSubTarget.cs`、`UniversalTarget.cs` | keyword、pass/variant、透明 prepass 和材质字段支持 |

`URP/Runtime/UniversalAdditionalCameraData.cs`、`URP/ShaderLibrary/UnityGBuffer.hlsl`、`URP/Runtime/DeferredLights.cs` 首版**不新增插件字段、不改变布局、不做 GBuffer3 增量猜测**。若实施中发现必须修改，必须回到 T0 记录原因和兼容性影响，不能临时扩大边界。

### 9.2 Core：新增材质端支持文件

```text
URP/ShaderLibrary/ScreenSpaceLighting/
├─ ScreenSpaceLightingInput.hlsl       # Core 所有的全局输入声明与 neutral 约定
├─ ScreenSpaceLightingBlend.hlsl       # SSGI/SSR 材质混合，Plugin 提供数据
├─ ScreenSpaceLightingNormal.hlsl      # DepthNormals 编码/解码
└─ ScreenSpaceRefraction.hlsl          # 透明材质 proxy/深度拒绝/fallback

URP/Shaders/Utils/
└─ TransparentDepthNormalsPass.hlsl    # 新 LightMode 的共享实现
```

这些文件属于 Core，保证插件源码被移除后内置 Shader 仍有完整 include 和禁用路径。compute/降噪算法不得放入 Core 材质头。

### 9.3 Plugin：新增文件

```text
URP/Runtime/RendererFeatures/ScreenSpaceCommon/
├─ PackedMipChainInfo.cs
├─ DepthPyramidPass.cs                 # 每个 Feature 实例私有资源
├─ CameraHistoryStore.cs               # 通用生命周期，不存具体效果参数
├─ ScreenSpaceCapability.cs
└─ ScreenSpaceResourceUtils.cs

URP/Runtime/RendererFeatures/GroundTruthAmbientOcclusion/
├─ GroundTruthAmbientOcclusionRendererFeature.cs
├─ GroundTruthAmbientOcclusionSettings.cs
├─ GroundTruthAmbientOcclusionResources.cs
├─ GroundTruthAmbientOcclusionShaderIDs.cs
├─ GroundTruthAmbientOcclusionShaderVariables.cs(+.cs.hlsl)
├─ GroundTruthAmbientOcclusionHistory.cs
├─ GroundTruthAmbientOcclusionDepthPyramidPass.cs
├─ GroundTruthAmbientOcclusionPass.cs
├─ GroundTruthAmbientOcclusionDebugPass.cs
└─ GroundTruthAmbientOcclusionDebugMode.cs

URP/Runtime/RendererFeatures/ScreenSpaceLighting/
├─ ScreenSpaceLightingRendererFeature.cs
├─ ScreenSpaceLightingSettings.cs
├─ ScreenSpaceLightingResources.cs
├─ ScreenSpaceLightingShaderIDs.cs
├─ ScreenSpaceLightingHistory.cs
├─ ScreenSpaceLightingVolumeSettings.cs # 可拆为 SSGI/SSR/Refraction 三个文件
├─ Modules/{SsgiModule,SsrModule,RefractionModule}.cs
└─ Passes/
   ├─ ColorPyramidPass.cs
   ├─ SsgiPass.cs
   ├─ SsrPass.cs
   ├─ TransparentDepthNormalRenderPass.cs
   ├─ ColorHistoryCapturePass.cs
   └─ ScreenSpaceLightingDebugPass.cs

URP/Runtime/RenderPipeline/ScreenSpaceLighting/
├─ TemporalFilter.cs
└─ DiffuseDenoiser.cs

URP/Shaders/Utils/ScreenSpaceCommon/
├─ DepthPyramid.compute
├─ BilateralUpsample.compute
└─ BilateralUpsample.hlsl

URP/Shaders/Utils/GroundTruthAmbientOcclusion/
├─ GTAO.compute
├─ GTAOSpatialDenoise.compute
├─ GTAOTemporalDenoise.compute
├─ GTAOCopyHistory.compute
├─ GTAOBlurAndUpsample.compute
├─ GTAOCommon.hlsl
└─ GTAODebug.shader

URP/Shaders/Utils/ScreenSpaceLighting/
├─ ColorPyramid.compute
├─ ScreenSpaceGlobalIllumination.compute
├─ ScreenSpaceReflections.compute
├─ TemporalFilter.compute
├─ DiffuseDenoiser.compute
├─ RayMarching.hlsl
├─ ScreenSpaceTracing.hlsl
├─ Sampling.hlsl
└─ ShaderVariables*.cs.hlsl

URP/Editor/RendererFeatures/
├─ GroundTruthAmbientOcclusion/
│  ├─ GroundTruthAmbientOcclusionEditor.cs
│  └─ GroundTruthAmbientOcclusionShaderPreprocessor.cs
└─ ScreenSpaceLighting/
   ├─ ScreenSpaceLightingEditor.cs
   ├─ ScreenSpaceLightingResourceEditor.cs
   └─ ScreenSpaceLightingShaderPreprocessor.cs
```

文件可按复杂度合并，但 Feature 设置、资源所有权、历史、pyramid、trace/denoise 和材质消费不得混成一个不可测试的大 Pass。所有新 Unity 源码、ComputeShader、测试资产和 Profile 都必须保留对应 `.meta`。

## 10. T0–T14 任务列表、依赖与退出条件

优先级：**P0** 是基础与 opaque 可交付链路，**P1** 是完整迁移所需扩展；若 P1 未完成，只能在支持矩阵中宣称对应子集。每个任务都必须同步通过相关测试并清理自身临时资源，不能把全部验证推迟到 T14。

| ID | 优先级 / 依赖 | 工作与交付物 | 退出与验收 |
|---|---|---|---|
| **T0** | P0 / 无 | 确立 Core/Plugin 边界、两个 Feature、Volume、历史/Motion/Hi-Z 所有权、冲突策略和文件清单 | **2026-11-14 已冻结**；无“GTAO 子模块”“Volume 直接加 Pass”“UACD 持插件历史”等旧决策；变更必须重新打开 T0 并同步两份文档 |
| **T1** | P0 / T0 | 建立三 Renderer 测试资产、固定场景/相机/随机序列、原始截图、Frame Debugger 时序、GPU/显存基线；确定目标 API 和可量化性能预算 | **已退出（2026-11-14，R3）**：资产、构建、图像、显存、Frame Debugger 和设置恢复通过；Run13/Run14 在固定协议下三路径 PNG 逐字节一致、graphics-driver 显存一致、CPU p95 波动 2.19%/2.28%/2.30%（≤10%）。T2 Core 改动后由 Run15/Run16 复验，CPU p95 波动 0.28%/0.01%/0.04%。GPU p95 因本机 DVFS 且 GeForce/WDDM 不执行 `nvidia-smi -lgc` 锁频，按 R3 仅作参考数据；10% 容差、场景与预算数值未变，Run1–Run16 及无效运行全部保留 |
| **T2** | P0 / T1 | Core DepthNormals 编码、最早 Motion/Depth 调度、通用 keyword 清理与调度测试 | **已退出（2026-11-14）**：Lit/ComplexLit DepthNormals alpha 使用 `0.5+0.5*smoothness`、0 表示不支持；最早 Motion 消费者把共享 Motion 提前到 202，并同步提前 Depth/CopyDepth；Deferred 提前 Motion 时强制完整 DepthNormals prepass，GBuffer2 alpha 仍为原始 smoothness；无插件时三路径事件数据与 T1 基线逐事件一致（`eventDataHash` 相同）。202 前 Motion/Depth 有效（动态 98 px、静止 0）；TAA/Motion Blur 早/晚 Motion 输出逐字节一致；smoothness=0（406 px）与不支持 alpha=0（42 px）可区分，无 0–0.5 保留区像素。证据：`PLAN/Done/01-ScreenSpaceLighting/validation/T2/` |
| **T3** | P0 / T1 | 两个空 Feature、资源序列化、Volume 组件、能力检测、history store、neutral 输出和 Editor UI | **已退出（2026-11-14）**：按 2.5 的真实 UI 路径配置成功并保留 13 张截图（`PLAN/Done/01-ScreenSpaceLighting/validation/T3/UI/`）；三路径 Volume 控制 Pass 入队、Feature 关闭不执行；空效果与 T1 基线逐像素一致；history 计数 1→2→1；无插件三路径 `eventDataHash` 与 T1 相同；EditMode 48 passed / 0 failed；`.meta` 完整。`T3Report.json` 的 `t3ExitCriteriaMet=true` |
| **T4** | P0 / T2、T3 | SSL 私有 Hi-Z、颜色金字塔、previous color/depth、双缓冲、reset generation 和中间调试视图 | **已退出（2026-11-14）**：packed Hi-Z（12 mip，offset/UV scale 数值验证）、Gaussian 颜色金字塔（9 mip）、双缓冲 previous color/depth 和 7 个调试视图落地；三路径 debug-off 与 T1 基线逐字节一致，首帧/相机 cut/尺寸/render scale 均触发 reset，读写槽逐帧分离，双相机独立且销毁后回收，SSL-only Renderer 全部通过；Frame Debugger 顺序 203 → 400 → 501。证据：`PLAN/Done/01-ScreenSpaceLighting/validation/T4/`（`t4ExitCriteriaMet=true`，EditMode 85 passed） |
| **T5** | P0 / T2、T3 | 独立 GTAO 全 kernel、私有 Hi-Z、AO 历史、SSAO 式设置 | **已退出（2026-11-14）**：Main/Spatial/Temporal/CopyHistory/Blur+Upsample、私有 packed Hi-Z（12 mip）、双缓冲 AO 历史、raw/final/global AO 与深度金字塔调试视图落地；三路径 AO 可见、half/full 与 temporal/spatial 真实选择对应 kernel；`intensity=0` 与 Feature 关闭恢复基线逐字节一致；SSL+GTAO 两种 Feature 顺序图像一致；重复 Hi-Z 成本 11,059,200 字节已记录。**同日按用户要求移除 URP 内置 SSAO，GTAO 成为唯一 AO 生产者**；证据：`PLAN/Done/01-ScreenSpaceLighting/validation/T5/`（`t5ExitCriteriaMet=true`，EditMode 103 passed） |
| **T6** | P0 / T4 | SSGI Trace/Reproject、Temporal/Diffuse Denoise、HF/LF、Bilateral Upsample、材质注入 | **已退出（2026-11-14）**：Trace/Reproject、history validation/accumulation、DiffuseDenoiser 必要支路、半分辨率 Bilateral Upsample 与 HF/LF 历史落地；Core `_SCREEN_SPACE_GI` 材质混合在 Forward/Forward+/Deferred 统一消费；三路径 `passed=true`，零权重像素与 baked GI 逐像素一致（最大差 1.19e-7），首帧/intensity=0 逐字节恢复基线，emission 亮度比 0.991，调试图与消费纹理一致（19/921,600 像素、最大差 0.0012），Frame Debugger 顺序 203→205→207→opaque。证据：`PLAN/Done/01-ScreenSpaceLighting/validation/T6/`（`t6ExitCriteriaMet=true`，EditMode 111 passed） |
| **T7** | P0 / T4 | Opaque SSR Approximation、radiance+weight、roughness/edge fade 和 probe/sky fallback | **已退出（2026-11-14）**：Trace/Reproject Approximation、RGBA16F 非预乘 radiance + weight、roughness/edge fade、屏外/非有限/负值拒绝和 probe/sky 回退落地；Core `_SCREEN_SPACE_SSR` 在 `EnvironmentBRDF` 前统一混合；三路径 `passed=true`，miss 的 670,152 像素与 probe 基线逐像素一致（最大差 0）、intensity=0 与首帧逐字节恢复基线、金属粗糙度权重 0.444/0.350/0.198/0 且低于 minSmoothness 为 0、非金属光滑球 0.410、干净首帧 intensity 响应比 2.000（误差 0）、调试图 921,600 像素 0 差异、无 NaN/Inf/负值；Frame Debugger 顺序 Depth Pyramid → SSR → opaque。证据：`PLAN/Done/01-ScreenSpaceLighting/validation/T7/`（`t7ExitCriteriaMet=true`，EditMode 118 passed） |
| **T8** | P1 / T7 | SSR PBR Accumulation、双历史、全部速度拒绝组合、算法切换 reset | **已退出（2026-11-14）**：GGX VNDF trace、3×3 cross reproject、双 RGBA16F 累积历史落地；9 个公开组合各自只命中独立 kernel（每组 3 次派发），surface/target 在动态场景差异 226,681 像素、speed 参数 0↔1 差异 232,951 像素，硬拒绝调试 100% 红、接受调试 100% 绿；算法切换 reset 2 次、相机 cut reset 2 次；PBR 的 miss 670,222 像素与 probe 基线一致、首帧/intensity=0 恢复基线、CB 回读 `(0.0263,0.5,0.5,1)`、无 NaN/Inf/负值。证据：`PLAN/Done/01-ScreenSpaceLighting/validation/T8/`（`t8ExitCriteriaMet=true`，EditMode 126 passed） |
| **T9** | P1 / T6、T8 | Lit、ComplexLit、ShaderGraph Lit 完整覆盖；clear coat 独立数据；SimpleLit/Particles/Terrain 支持或明确回退 | **已退出（2026-11-14）**：Lit/ComplexLit/ShaderGraph Lit（Forward + GBuffer）均消费 `_SCREEN_SPACE_GI/_SCREEN_SPACE_SSR`；ComplexLit 的 DepthNormals 在 coat mask>0 时写 coat SSR smoothness（alpha 1.0），材质端 base/coat 使用独立 mask/roughness 与 hierarchy weight，base smoothness 0.3 的 coat 球 trace weight 仍为 1.0；SimpleLit/Particles/Terrain Lit 保持 alpha 0、weight 0；572,746 个零权重像素与 probe 图像逐像素一致、intensity=0 逐字节恢复；alpha test、LOD、lightmap + mixed lighting 回归通过。APV 因 URP 14.0.12 无 ProbeVolume 集成标为不支持。证据：`PLAN/Done/01-ScreenSpaceLighting/validation/T9/`（`t9ExitCriteriaMet=true`，EditMode 132 passed） |
| **T10** | P1 / T7、T9 | Core 透明 depth/normal Pass，SSL 透明 prepass、独立 Transparent SSR Approx 和材质消费 | **已退出（2026-11-15）**：Core 新增 `UniversalTransparentDepthNormals` 共享实现并接入 Lit/ComplexLit；材质通过 `_TransparentSsrReceive`/`_TRANSPARENT_SSR_RECEIVE` 显式 opt-in，未 opt-in 片段在 prepass 中 `clip`。SSL 在 447 把 opaque depth 复制进私有 depth 后绘制透明 depth/normal，449 以 `_SCREEN_SPACE_TRANSPARENT_SSR` 和独立 hit point/output 执行 Approximation-only Trace/Reproject。三路径 `T10Verification.json` 全部 `passed=true`：intensity=0/关闭后逐字节恢复、opaque weight 与 camera depth diff=0、receiver 785 px/weight 1.0、opt-out 与 SimpleLit 0 px/weight 0、ComplexLit clear coat 透明 1,266 px/weight 1.0、透明 depth 0.006142 > opaque 0.003819（9,560 px 不同）、双层最近面胜出、PBR 下 transparent 4/4 dispatch 且 accumulation=0、移动透明体 ray origin 跟随且无 NaN。EditMode 142 passed / 0 failed。证据：`PLAN/Done/01-ScreenSpaceLighting/validation/T10/`（`t10ExitCriteriaMet=true`） |
| **T11** | P1 / T4、T9 | 折射材质属性/GUI/ShaderGraph、当前帧 Color Pyramid、proxy raycast、深度拒绝、transmittance、edge fade | **已退出（2026-11-15）**：Lit/ComplexLit/ShaderGraph Lit 新增 `_RefractionModel`（None 默认）/`_Ior`/`_Thickness`/`_TransmittanceColor`/`_TransmittanceDistance` 与 `_REFRACTION_PLANE/SPHERE/THIN`；Volume 新增 `screenFadeDistance`/`intensity`。SSL 在 448 绑定 400 生成的当前帧 color pyramid、opaque depth 与最近 box-projected probe 的 AABB proxy；材质端移植 HDRP proxy raycast、深度拒绝、absorption、edge fade 与 (1-FGD) 透射，折射开启时 alpha 按权重强制为 1。三路径 `T11Verification.json` 全部 `passed=true`：旧材质/`ModelsNone`/`intensity=0`/关闭相位逐像素恢复（容差 1e-6）、legacy 透明 0 像素变化、box/complex/sphere/SG 分别改变 5,915/2,996/1,233/3,092 像素、SG 透明 prepass 改变 543 像素、红色吸收使红通道比降至 0.363、背景由橙变蓝时玻璃均值从 (1.502,0.867,0.319) 变为 (0.274,0.438,1.577)；透明面板开关时玻璃圆内 0 像素变化、color pyramid 0 像素变化（面板自身 5,931 像素），证明当前 pyramid 不含 pre-refraction transparents。EditMode 149 passed / 0 failed。证据：`PLAN/Done/01-ScreenSpaceLighting/validation/T11/`（`t11ExitCriteriaMet=true`） |
| **T12** | P1 / T5、T6、T8–T11 | 全组合 Deferred/Forward 加固：ForwardOnly、decal、Depth Priming、Opaque Texture、TAA、Motion Blur、HDR/MSAA、camera stack | **已退出（2026-11-15）**：Forward/Forward+/Deferred 各 34 个组合用例（Deferred 35，含 `GL.wireframe` 触发的实际 Forward 回退）全部通过；GTAO/SSL 顺序不变（0 px）、Volume 缺失/override/mask/双 Volume、render scale/尺寸重置/camera cut/算法切换 reset、Depth Priming、Opaque Texture、TAA、Motion Blur、DBuffer/ScreenSpace Decal、MSAA、相机栈、Preview/Reflection 与 SimpleLit 回退均有结果。Frame Debugger 94/94/105 事件，`MotionVectors → SSL Depth Pyramid → GTAO Depth Pyramid → SSGI/SSR → opaque/GBuffer`，Pre-GBuffer 未触发 AfterGbuffer 回退；全局 keyword 清理通过；1280×720 base-level 私有资源 SSL 98,611,832 B、GTAO 9,216,120 B、packed Hi-Z 5,529,600 B（12 mip），每帧 1 次透明 prepass。Deferred 兼容路径忽略 MSAA、相机栈/PP/Opaque Texture 改变中间色彩精度均记录为 URP 侧限制。EditMode 153 passed / 0 failed。证据：`PLAN/Done/01-ScreenSpaceLighting/validation/T12/`（`t12ExitCriteriaMet=true`） |
| **T13** | P1 / T12 | XR multipass/single-pass/foveation、render scale/动态分辨率、DX11/DX12/Vulkan/Metal 能力与性能 | **已退出（2026-11-15）**：SSL/GTAO compute 逐眼矩阵与 slice、每眼相机位置/上一帧逆 VP 与 history 分键落地；修复 `ScreenSpaceLightingCommon.hlsl` 的 XR 宏守卫（`SSGI_XR_ARRAY`→`SSL_XR_ARRAY`）后 single-pass compute 实际执行。Mock HMD 播放器 DX11/DX12/Vulkan 双模式全部 `passed=true`：两眼镜像差 924,469–1,060,399 px、关闭恢复参考、SSGI/SSR/折射/GTAO 两眼均可见、single-pass 1 entry×2 slices 且两 slice 上一帧颜色金字塔哈希不同、multipass 2 entries×1 slice 且两眼下级 prev 矩阵已记录。DX12/Vulkan `FoveationImage` 下全屏 raster/prepass 已加 foveation 守卫（DX11 caps=None）。URP 14.0.12 不驱动 `DynamicResolutionHandler`，`ScalableBufferManager` 不能缩放 RTHandle 相机目标（DX11 `IntermediateTargetsNotRescaled`、DX12/Vulkan readback 无效），按引擎限制记录为不支持；静态 render scale 由 T12 覆盖。Metal/mobile/OpenGLES/真实 HMD 标 NotTested。T12 回归 34/34/35、EditMode 153 passed / 0 failed。证据：`PLAN/Done/01-ScreenSpaceLighting/validation/T13/`（`t13ExitCriteriaMet=true`） |
| **T14** | P0 / 所有本次宣称交付的 T | 最终 Edit/Play/图形/Player/stripping 回归，支持矩阵、已知差异、性能报告、产物归档和清理 | **已退出（2026-11-15）**：EditMode 153/153、PlayMode 6/6、T12 组合矩阵回放 34/34/35 且 103 张图像哈希与 T12 记录逐字节一致、Frame Debugger 94/94/105 事件顺序不变（`Motion(7/7/9) → SSL Depth Pyramid(9/9/10) → GTAO(20/20/21) → SSGI(36/36/37) → SSR(43/43/44) → opaque/GBuffer(45/45/46)`）。Release Player（`BuildOptions.None`）构建 0 errors、17 个必需 ComputeShader/Shader 均在包内；DX11/DX12/Vulkan 双 XR 模式播放器全部通过。`T1..T13` 证据未改动（T13 requiredFiles 哈希逐项一致）；支持矩阵/已知差异/性能显存/归档清单落地，T1 与 T14 临时 Player、`Assets/Settings` 的 SSAO 残留已清理。证据：`PLAN/Done/01-ScreenSpaceLighting/validation/T14/`（`t14ExitCriteriaMet=true`） |

不允许把 T5/T6/T7 的缺陷留到 T12 才首次验证；T12 是交叉组合回归，不替代模块自己的退出条件。

## 11. 测试改造与验收证据

### 11.1 测试文件与资产

新增测试代码：

```text
URP/Tests/Editor/UniversalRendererPassInputTests.cs # T2 Core：Motion/Depth 输入汇总与调度（已实现）

URP/Tests/Editor/RendererFeatures/GroundTruthAmbientOcclusion/
├─ GroundTruthAmbientOcclusionRendererFeatureTests.cs # 调度事件、输入、调试、能力
├─ GroundTruthAmbientOcclusionHistoryTests.cs          # reset、ping-pong、重分配、半/全分辨率
└─ GroundTruthAmbientOcclusionCBufferTests.cs          # GenerateHLSL 布局与字段顺序

URP/Tests/Editor/RendererFeatures/ScreenSpaceLighting/
├─ ScreenSpaceLightingFeatureTests.cs       # RendererData/Volume/设置优先级/能力
├─ ScreenSpaceLightingHistoryTests.cs       # generation、重分配、相机回收
├─ ScreenSpaceLightingSchedulingTests.cs    # Motion/Depth/DepthNormals 时序
├─ ScreenSpaceLightingShaderPreprocessorTests.cs
└─ ScreenSpaceLightingCBufferTests.cs

URP/Tests/Runtime/RendererFeatures/GroundTruthAmbientOcclusion/
├─ GroundTruthAmbientOcclusionLifecycleTests.cs
└─ GroundTruthAmbientOcclusionRenderTests.cs

URP/Tests/Runtime/RendererFeatures/ScreenSpaceLighting/
├─ ScreenSpaceLightingLifecycleTests.cs
├─ ScreenSpaceLightingRenderTests.cs
└─ ScreenSpaceLightingHistoryRenderTests.cs
```

现有 `URP/Tests/Editor/ShaderBuildPreprocessorTests.cs` 只增加“无新 Feature 时原 stripping 结果不变”的回归，不把具体插件类型塞入 Core 预处理器。新插件自己的 Editor 预处理器负责按 Renderer Data 中的 Feature 存在性 strip；Volume 运行时开关不得 strip。

项目内保留可重复图形测试资产：`Assets/Tests/ScreenSpaceLighting/{Scenes,Renderers,Materials,Profiles,GoldenImages}`。测试不得直接覆盖用户现有 `Assets/Settings` 或 `SampleScene`；Setup/TearDown 必须恢复 GraphicsSettings、QualitySettings、Volume、关键字和相机状态。

T1 固定基线配置：

- `ScreenSpaceLightingBaselineSetup.cs` 生成同一固定 Scene、三份无 RendererFeature 的 Renderer Data 和对应 Pipeline Asset；随机种子 `14012`，相机、几何、材质和灯光不随模块变化。
- 主基线为 StandaloneWindows64 / DX11、1280×720、render scale 1、HDR、MSAA 关闭、后处理关闭；T1 使用 Development Player 获取帧时间和 graphics-driver 显存，T14 另做 Release 回归。
- **GPU 计时协议 R3（2026-11-14）**：R2 的锁频方案在本机不可执行——`nvidia-smi -lgc 300/600/1800` 均返回 0，但 SM 时钟仍停留在 36–224 MHz，persistence mode 为 `[N/A]`，即 GeForce/WDDM 驱动不执行应用时钟锁（证据：`Diagnostics/clock-lock-verify.txt`）。NVML 576.80 以 50 ms 间隔采样的独立诊断（`PLAN/Done/01-ScreenSpaceLighting/validation/T1/Diagnostics/Dvfs-2026-11-14/`）进一步证明，固定场景每帧仅约 0.1 ms GPU 工作，未锁频时 SM 时钟在 91 s 内于 63–1515 MHz 间切换 180 次（P3/P5/P8），GPU p95 跟随 DVFS 而非渲染成本。因此 R3 将 720p T1 的 GPU p95 降级为逐轮记录、逐轮报告的参考数据，不再作为 T1 或模块的通过判定；T1 可重复性门槛保留 CPU p95 的 10% 对称容差，并要求两轮截图逐字节一致、graphics-driver 显存一致、Pass 顺序（Frame Debugger）不变。GPU 时间仍必须成功采集（缺失即协议无效），`RawFrameTimes.csv` 的 `UtcTicks` 列与 `GpuClocks.csv` 继续留存以便与 NVML 对齐。p95 统计量、10% 容差、场景、分辨率、帧率上限、采样帧数均不变，Run1–Run13 全部保留，包括所有失败与无效运行。模块级增量 GPU 预算数值不变，但必须用独立 GPU profiler（RenderDoc/Nsight）或可锁频环境验证，不得用该 720p harness 单独宣称达标。
- 每路径预热 60 帧、采样 1800 帧，记录 CPU/GPU mean、median、p95、显存和同步 PNG；同一 Player 连续运行两次，不把缺失时间戳或截图的退出码 0 当作通过。最初 180 帧的 Run1–Run6 全部保留；六轮证据显示亚毫秒 p95 尾部波动无法稳定满足 10%，因此只增加样本量，10% 容差和性能预算不变。1800 帧的 Run7–Run8 仍有 p95 超差，完整保留；Run9–Run10 复用 `WaitForEndOfFrame` 并输出 `RawFrameTimes.csv` 后，CPU p95 全部进入容差，原始数据则显示无帧率上限的 GPU 尾部仍不稳定且大多不与 CPU 峰值重合。Run11–Run12 固定 60 FPS、vSync 关闭后，Forward 的 CPU/GPU p95 通过，但 Forward+ 与 Deferred 的 GPU p95 仍分别为 94.71% 和 51.58%，T1 未退出；p95 指标、10% 容差和性能预算不变，所有失败运行与 `RawFrameTimes.csv` 均保留。2026-11-14 已完成独立工具核实：NVML 证据见 `PLAN/Done/01-ScreenSpaceLighting/validation/T1/Diagnostics/Dvfs-2026-11-14/`，确认尾部来自 GPU 降频（DVFS）而非渲染负载；随后 R2 锁频被证明不被本机驱动执行，改用 R3 参考数据协议，Run11–Run12 作为失败证据保留，Run13 作为 R2 下的完整运行保留，Run14 与其组成 R3 的有效运行对。
- 量化预算唯一来源为 `PLAN/Done/01-ScreenSpaceLighting/validation/T1/PerformanceBudget.json`，硬件与驱动为 `Environment.json`，构建、运行和 Frame Debugger 证据均保存在 `PLAN/Done/01-ScreenSpaceLighting/validation/T1/`。
- 生成器与采集器只操作独立测试资产和独立进程内 Pipeline；结束后核对原 `GraphicsSettings.asset`、`QualitySettings.asset`、`ProjectSettings.asset` 未变化。

### 11.2 分层测试要求

| 层级 | 必测内容 | 证据 |
|---|---|---|
| EditMode | Feature 创建/重复添加、Volume 菜单与 stack override、设置优先级、CBuffer 布局、格式回退、history reset/realloc/prune、strip 规则 | NUnit XML；失败日志 |
| PlayMode | 三 Renderer 入队、运行中开关、相机 cut/创建销毁、Domain Reload、Base/Overlay、资源释放、无 Feature 回退 | NUnit XML；Pass 时序；内存计数 |
| 图形 | 固定相机/时间/随机种子的 raw hit、reproject、denoise、weight、最终消费 golden images | PNG、容差、GPU/API/分辨率元数据 |
| 手工 UI | 2.5 的 Renderer Data + Volume 真实操作；GTAO Inspector；冲突警告；debug 视图 | 配置截图、Volume 截图、Frame Debugger 截图 |
| 构建 | 三种 Renderer、Feature 有/无、各模块组合、Player stripping、ComputeShader 引用、Domain Reload 后资源 | Player 构建日志、缺失 variant 检查 |
| 性能 | DepthNormals、Motion、两套 Hi-Z、Trace、Denoise、Transparent、历史内存，相对 T1 基线增量 | Profiler/计时 CSV、显存表、场景与硬件信息 |

图形测试按 API/平台设容差，不用跨 GPU 的逐像素相等。HDRP 对照图用于形态和能量检查；URP 自身禁用 Feature 的基线用于回归。发现差异时保留 raw/weight/history 中间图，不能只比较最终平均亮度。

### 11.3 正确性验收

- 禁用 Feature 或单模块时与原 URP 基线一致，不残留 keyword、texture、stencil 或历史副作用。
- GTAO neutral 正确，direct/indirect 与材质 AO 不重复；Deferred 不重复遮蔽 emission/GI。
- SSGI/SSR miss 权重为 0；命中时 BRDF 只乘一次；emission 不重复、不丢失。
- 无读取尚未生成的 Motion、当前帧 opaque color 或尚未写入的透明颜色。
- 无 NaN/Inf、未初始化历史、previous/current 槽混用、XR 双眼串历史。
- Frame Debugger 证明 Pass 顺序；RenderDoc 证明同一 subresource 未同时作为未解绑读写资源。
- 两个 Feature 以不同列表顺序安装、分别禁用和同时启用时，除已记录的重复 Hi-Z 成本外结果一致。

### 11.4 T2 实施与证据记录（2026-11-14）

**Core 调度**（`URP/Runtime/UniversalRenderer.cs`）

- 默认 Motion 事件保留在 `m_DefaultMotionVectorPassEvent`（CopyDepth 事件 + 1）。新增 `k_EarlyMotionVectorRenderPassEvent = AfterRenderingPrePasses + 2`（数值 202），仅当队列中存在早于默认事件的 Motion 消费者时使用；没有消费者或只有 TAA/Motion Blur 时保持原时序。
- 输入汇总抽取为 `AccumulatePassInput`、`ResolveMotionVectorPassEvent`、`ApplyMotionVectorDependencies` 三个 internal 静态函数，`RenderPassInputSummary` 改为 internal，供 EditMode 直接验证调度规则，不依赖真实 Renderer。
- 提前 Motion 会同步把 Depth 最早事件与 DepthNormals 事件拉到 Motion 之前：Forward/Forward+ 由提前的 depth（或 depth-normal）prepass 提供深度，不再追加 CopyDepth；Deferred 在 201 用 prepass 深度生成提前 CopyDepth（`requiresDepthTextureEarliestEvent - 1`），并强制 `requiresNormalsTexture`（DeferredLights 读取该标志前必须已确定，避免 GBuffer 附件二次分配）。
- Deferred 的 GBuffer2 alpha 仍是原始 smoothness，`DeferredLights` 不读取 prepass 编码；混合路径仍以 GBuffer 数据为准。
- 兼容路径在分配 Motion 纹理后、入队 Motion Pass 前显式 `SetGlobalTexture("_MotionVectorTexture", ...)`；Render Graph 路径原本已绑定，两路径行为一致。

**DepthNormals 编码与材质**

- `LitDepthNormalsPass.hlsl` 两个 normal 分支统一写 `0.5 + 0.5 * saturate(smoothness)`；0 保留给“不支持”，因此 smoothness=0 编码为 0.5。`_METALLICSPECGLOSSMAP`、`_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A` 会按需启用 uv 插值器，缺少 uv 时 smoothness helper 使用常量。
- `Lit.shader`、`ComplexLit.shader` 的 DepthNormals Pass 增加 `_METALLICSPECGLOSSMAP`、`_SPECULAR_SETUP` fragment keyword；移除 ComplexLit DepthOnly Pass 中未使用的 `_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A` 声明。
- 未适配材质（Unlit 等）继续写 alpha 0，与 URP 既有约定一致；SimpleLit/Particles/Terrain/ShaderGraph 的编码或明确回退留到 T9，透明 prepass 留到 T10。

**证据**

| 项目 | 结果 | 入口 |
|---|---|---|
| EditMode 调度测试 | 13 passed / 0 failed，设置恢复 | `PLAN/Done/01-ScreenSpaceLighting/validation/T2/UnityTests.log` |
| 三路径输入有效性 | 动态 Motion 峰值 0.01128（98 px）、静止 0；深度/法线有效；alpha 0/0.5/高值分别为 42/406/35915，0–0.5 保留区 0 px，无洋红 | `PLAN/Done/01-ScreenSpaceLighting/validation/T2/T2Verification.json`、`T2ProbeDepth_*.png`、`T2ProbeRawNormal_*.png` |
| TAA/Motion Blur 回归 | 早/默认 Motion 输出哈希逐字节一致（三路径均 0 差异） | 同上 |
| Pass 顺序（带探针） | Forward/Forward+ 11 事件，Deferred 22 事件：DepthNormalPrepass(150) → [Deferred: CopyDepth(201)] → MotionVectors(202) → 探针(203) → GBuffer(210)/Opaque(250) → 天空盒/最终 Blit | `PLAN/Done/01-ScreenSpaceLighting/validation/T2/FrameDebugger/FrameDebuggerReport.json` |
| 无插件不变性 | 无探针调度 6/6/16 事件，与 T1 基线逐事件 `eventDataHash` 相同（仅 Renderer 资产名不同） | `PLAN/Done/01-ScreenSpaceLighting/validation/T2/FrameDebugger/WithoutProbe/`、`PLAN/Done/01-ScreenSpaceLighting/validation/T1/FrameDebugger/FrameDebuggerReport.json` |
| T1 复验 | Run15/Run16 通过：三路径 PNG 逐字节一致、graphics-driver 显存一致、CPU p95 波动 0.28% / 0.01% / 0.04% | `PLAN/Done/01-ScreenSpaceLighting/validation/T1/Reproducibility.json` |

**已知限制**：T2 只验证 Core 输入与调度，不包含任何 GTAO/SSGI/SSR 算法、透明 prepass 或历史资源；探针 Feature、探针材质与 `SslT2*` Renderer/Pipeline 均为 `Assets/Tests/ScreenSpaceLighting` 下的测试资产，不属于 Core 或后续插件 API。

### 11.5 T4 实施与证据记录（2026-11-14）

**私有资源与所有权**（`ScreenSpaceLightingHistory`，仍按相机/XR eye 存在 `CameraHistoryStore`）

- packed 深度金字塔：`DepthPyramid.compute` 的 copy + 8x8 closest-depth downsample，`PackedMipChainInfo` 生成 atlas 尺寸、mip offset 和 `ComputeMipUvScaleOffset`；offset `ComputeBuffer` 与 RTHandle 均由 history entry 持有并在尺寸/布局变化时释放重建。
- 当前帧颜色金字塔：`ColorPyramid.shader` 的 bilinear downsample + 9-tap 可分离 Gaussian；400 生成，mip 数按 HDRP 规则在 8x8 以下停止。
- previous color/depth：501 写入 `writeSlot`，`readSlot = bufferIndex`、`writeSlot = bufferIndex ^ 1`，捕获后交换；当帧读槽与写槽恒不相同。颜色/深度各两份，按相机与 XR eye 独立。
- reset：store 的尺寸变化、`resetHistoryFrames` token 变化和首帧都会调用 `Reset`；`Reset` 清空 valid 标记和 ping-pong 位置，资源在下次 `EnsureResources` 时按新尺寸重建。

**URP 接入**

- 深度金字塔在 `AfterRenderingPrePasses + 3`（203）请求 `Depth | Motion`，复用 T2 的内置 Motion/Depth 提前调度；Deferred 由提前 Motion 强制完整 DepthNormals prepass，金字塔在 GBuffer（210）前完成。
- 颜色金字塔在 400（sky 后）请求 `Color`，确保相机颜色是中间纹理；历史捕获在 501（transparent 后）并同时复制深度。调试视图在 551，只写相机颜色，不改变材质光照。
- 深度源直接绑定 `UniversalRenderer.m_DepthTexture`：`CommandBuffer.SetGlobalTexture` 写入的全局纹理在 CPU 侧不可读，且 compute 只声明全局名也读不到有效数据；绑定 RTHandle 后三路径才得到正确深度。
- 深度金字塔格式收敛为 `R32_SFloat`，与 HDRP 单通道深度一致；T3 能力探测同步收紧。
- 调试视图：DepthPyramid（可选 mip）、CurrentColorPyramid、PreviousColor、PreviousDepth、CameraDepth，以及输出 atlas UV 的 DepthPyramidUv；UV 视图的 min/max 与 `PackedMipChainInfo` 的 offset/scale 在 0.02 容差内一致。
- 仍只支持 Compatibility `ScriptableRenderPass.Execute`；RenderGraph 路径未注册这些 Pass，属于已记录限制。

**证据**

| 项目 | 结果 | 入口 |
|---|---|---|
| T4 结论 | `t4ExitCriteriaMet=true` | `PLAN/Done/01-ScreenSpaceLighting/validation/T4/T4Report.json` |
| 三路径渲染/生命周期 | 三路径 `passed=true`；debug-off 与 T1 基线逐字节一致 | `PLAN/Done/01-ScreenSpaceLighting/validation/T4/T4Verification.json` |
| Frame Debugger | Forward/Forward+ 67 事件，Deferred 78；SSL Depth Pyramid → Current Color Pyramid → CopyColor → Color History Capture → FinalBlit | `PLAN/Done/01-ScreenSpaceLighting/validation/T4/FrameDebugger/FrameDebuggerReport.json` |
| EditMode | 85 passed / 0 failed | `PLAN/Done/01-ScreenSpaceLighting/validation/T4/UnityTests.log` |
| 调试图与基线 | 33 张 PNG（含 mip/UV/尺寸变化/首帧 debug-off） | `PLAN/Done/01-ScreenSpaceLighting/validation/T4/T4_*.png` |

**已知差异**：多一次 400 颜色金字塔和 501 颜色/深度捕获；`CopyColor` 因 `ConfigureInput(Color)` 在 SSL 开启时额外执行；`resetHistoryFrames` 的上升/下降边沿可能连续失效两代；GTAO 独立 Hi-Z 成本仍按 T0 保留，T4 不合并。

### 11.6 T5 实施与证据记录（2026-11-14）

**独立 GTAO**（`URP/Runtime/RendererFeatures/GroundTruthAmbientOcclusion/`）

- 全 kernel 移植：`GTAO.compute`（horizon search）、`GTAOSpatialDenoise.compute`、`GTAOTemporalDenoise.compute`、`GTAOCopyHistory.compute`、`GTAOBlurAndUpsample.compute`（BlurUpsample / BilateralUpsampling / BoxUpsampling）；删除未使用的 `Blur`/`Blur_FullRes` kernel。
- 资源：`GroundTruthAmbientOcclusionHistory` 持有私有 packed depth pyramid（`R32_SFloat`、12 mip、独立 offset buffer）、`packedData`/`packedBlurred`/`packedTemporal`、双缓冲 `R8G8B8A8_UNorm` AO 历史与全分辨率 `R8_UNorm`（回退 `R32_SFloat`）可见度输出；与 SSL 实例完全隔离。
- CBuffer：`GroundTruthAmbientOcclusionShaderVariables` 使用 `[GenerateHLSL(generateCBuffer = true)]` 并配备 `.cs.hlsl`；视图矩阵改为 3 个行向量（`_GtaoViewRow0..2`），避免 compute 矩阵绑定差异；EditMode 测试固定字段顺序与 240 字节布局。
- 语义：`GTAOOutputFinalAO` 输出 URP 可见度（`pow(integral, intensity)`）；禁用时白色 neutral，`OnCameraCleanup` 关闭 `_SCREEN_SPACE_OCCLUSION` 并复位 `_AmbientOcclusionParam`。
- 调度：深度金字塔 203（Depth|Motion）、GTAO 主 pass 204（Depth|Normal|Motion），Forward/Forward+ 在 opaque 前、Deferred 在 GBuffer/Deferred Shading 前完成；Deferred 法线通过新增的 `UniversalRenderer.cameraNormalsTextureHandle`（GBuffer2 附件）读取。
- 冲突：内置 SSAO 于 2026-11-14 移除；GTAO 是唯一 AO 生产者，材质侧继续消费 `_SCREEN_SPACE_OCCLUSION`/`_ScreenSpaceOcclusionTexture`/`_AmbientOcclusionParam`。
- 验证器：`Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT5Verification.cs` 生成 6 个 T5 Renderer/Pipeline（正常/反向 Feature 顺序）并覆盖 half/full、temporal/spatial、intensity=0、Feature 关闭、相机 cut、尺寸变化、双相机隔离和顺序无关；场景渲染前调用 `DynamicGI.UpdateEnvironment()` 保证间接光有效。

**证据**

| 项目 | 结果 | 入口 |
|---|---|---|
| T5 结论 | `t5ExitCriteriaMet=true` | `PLAN/Done/01-ScreenSpaceLighting/validation/T5/T5Report.json` |
| 三路径渲染/生命周期 | 三路径 `passed=true`；临时相机条目回收、尺寸重分配、历史 reset 全部通过 | `PLAN/Done/01-ScreenSpaceLighting/validation/T5/T5Verification.json` |
| Frame Debugger | Forward/Forward+ 82 事件，Deferred 93；GTAO Depth Pyramid → GTAO dispatch → opaque/GBuffer；Deferred 在 Deferred Shading 前 | `PLAN/Done/01-ScreenSpaceLighting/validation/T5/FrameDebugger/FrameDebuggerReport.json` |
| EditMode | 103 passed / 0 failed | `PLAN/Done/01-ScreenSpaceLighting/validation/T5/UnityTests.log` |
| 调试图与对比图 | 33 张 PNG（raw/final/global AO、深度金字塔、half/full、spatial-only、顺序组合、尺寸变化） | `PLAN/Done/01-ScreenSpaceLighting/validation/T5/T5_*.png` |
| 重复 Hi-Z 成本 | `duplicateHiZBytes=11,059,200`（1280×720 私有 pyramid 两份） | `T5Verification.json` 的各 mode 字段 |

**已知限制**：URP 内置 SSAO 已按用户要求在 T5 后移除，现有引用该 Feature 的 Renderer Data 会出现 Missing Script，需要手动删除后改用 GTAO；AO keyword 的构建期精简固定为保留 ON/OFF，插件级 strip 优化留到 T14；XR single-pass 复用单视图矩阵（T13）；仅 Compatibility 路径。

### 11.7 T6 实施与证据记录（2026-11-14）

- 新增 Compute：`Shaders/Utils/ScreenSpaceLighting/{ScreenSpaceGlobalIllumination,TemporalFilter,DiffuseDenoiser}.compute`、`Shaders/Utils/ScreenSpaceCommon/BilateralUpsample.compute`，共享 `ScreenSpaceLightingCommon.hlsl`、`RayMarching.hlsl`、`Sampling.hlsl`、`Denoising.hlsl`。
- 新增 Runtime：`Passes/SsgiPass.cs`（trace/reproject/validation/accumulation/denoise/upsample 编排与资源绑定）、`ScreenSpaceLightingShaderVariables.cs(+.cs.hlsl)`；扩展 `ScreenSpaceGlobalIllumination` Volume（full/half、ray steps、thickness、intensity、temporal、denoise、second pass、half-res denoiser、bilateral upsample）；`ScreenSpaceLightingHistory` 增加 SSGI 私有资源与 HF/LF 双缓冲。
- Core 材质契约：新增 `ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingInput.hlsl` 与 `ScreenSpaceLightingBlend.hlsl`；`GlobalIllumination.hlsl` 完整重载与 `LitGBufferPass.hlsl` 混入 `lerp(bakedGI, ssgiRadiance, weight)`；`Lit/ComplexLit` 增加 `_SCREEN_SPACE_GI`；`ScriptableRenderer.ClearRenderingState` 每相机禁用。
- 退出证据：`T6Verification.json`（三路径 `passed=true`）、`FrameDebugger/FrameDebuggerReport.json`（Depth Pyramid → SSGI → opaque）、`UnityTests.log`（111 passed / 0 failed）、`T6Report.json`（`t6ExitCriteriaMet=true`）。调试图覆盖 raw/denoised/history/weight 与 half-res。
- 有意差异与限制：miss 保留 baked GI（不使用 HDRP probe/sky fallback）；无上一帧法线和 object-motion 拒绝；采样使用 IGN + R2 而非 blue-noise set（轻微 stipple）；仅 Lit/ComplexLit 材质；透明不接收 SSGI；仅 Compatibility Execute 路径，XR 逐眼矩阵留到 T13。

### 11.8 T7 实施与证据记录（2026-11-14）

- 新增 Compute：`Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute`（`ScreenSpaceReflectionsTracing` / `ScreenSpaceReflectionsReprojection`，Approximation）。`RayMarching.hlsl` 将循环抽为参数化 `SslRayMarch`，SSGI 保持原 `SsgiRayMarch` 包装；SSR 与 SSGI 共用 SSL 私有 Hi-Z，但各自绑定与 CB 独立。
- 新增 Runtime：`Passes/SsrPass.cs`（事件 206，Trace → Reproject）；`ScreenSpaceLightingHistory` 增加逐相机 RGBA16F 输出与 R16G16_SFloat hit point；`ScreenSpaceLightingShaderVariables` 增加 `ScreenSpaceLightingSsrShaderVariables`（6 个 Vector4）；`ScreenSpaceReflection` Volume 增加 minSmoothness、smoothnessFadeStart、depthBufferThickness、screenFadeDistance、rayMaxIterations、reflectSky、intensity。
- Core 材质契约：`ScreenSpaceLightingInput.hlsl` 增加 `_ScreenSpaceSsrTexture/_ScreenSpaceSsrParams`；`ScreenSpaceLightingBlend.hlsl` 增加 `ScreenSpaceLightingApplyIndirectSpecular`；`GlobalIllumination.hlsl` 在 `EnvironmentBRDF` 之前混合 indirect specular；`Lit/ComplexLit` 增加 `_SCREEN_SPACE_SSR`；`ScriptableRenderer.ClearRenderingState` 每相机禁用；Setup/Cleanup 同时管理 SSGI/SSR keyword。
- Deferred 特殊处理：SSR 在 GBuffer 之前执行，`cameraNormalsTextureHandle` 返回的 GBuffer2 仍是 DepthNormals prepass 的 `0.5 + 0.5 * smoothness` 编码，因此三路径统一按编码值解码，不能使用 GBuffer 覆盖后的 raw smoothness；修正后 Deferred 与 Forward 的权重统计逐项一致。
- 退出证据：`T7Verification.json`（三路径 `passed=true`）、`FrameDebugger/FrameDebuggerReport.json`（Depth Pyramid → SSR → opaque）、`UnityTests.log`（118 passed / 0 failed）、`T7Report.json`（`t7ExitCriteriaMet=true`）。调试图覆盖 SsrRaw/SsrWeight，并与消费纹理逐像素一致。
- 有意差异与限制：仅 Approximation；radiance 不预乘（HDRP 预乘，URP 用 `lerp` 避免除以 weight）；颜色历史包含上一帧 SSR 形成有界反馈，intensity 线性只在 reset 后首个干净 SSR 帧验证；仅 Lit/ComplexLit base layer（coat 留 T9）；仅 Compatibility Execute 路径，XR 逐眼矩阵留 T13。

### 11.9 T8 实施与证据记录（2026-11-14）

- Compute：`ScreenSpaceReflections.compute` 扩展为 21 个 kernel：Trace/Reproject（`SSR_APPROX` 分支）、`ClearBuffer2D`、`DebugSsrParams`、9 个 Accumulate 与 9 个 Debug Accumulate；GGX VNDF 使用 core `SampleGGXVisibleNormal`，采样序列复用 IGN+R2。
- Runtime：`ScreenSpaceLightingHistory` 增加 PBR original + 双累积历史、逐相机算法记忆与 reset；`SsrPass` 按 `usedAlgorithm` 选择输出，按 world/smooth/surface/target 选择真实 kernel，首帧/算法切换清空累积；`ScreenSpaceReflection` Volume 增加算法、accumulation/bias/speed 参数；调试模式增加 `SsrAccumulationSpeed` 与 `SsrPbrParams`。
- 结果契约：累积非预乘 radiance + weight，材质端与 T7 完全一致；HDRP 累积预乘 radiance，属有意差异。
- 退出证据：`T8Verification.json`（三路径 `passed=true`）、`UnityTests.log`（126 passed / 0 failed）、`T8Report.json`（`t8ExitCriteriaMet=true`）。9 组合 kernel 计数、动态场景 surface/target 与 speed 0↔1 差异、红/绿调试图、算法切换 reset、CB 回读全部记录在报告。
- 已知限制：world speed 重建沿用 HDRP 公式，静态场景基线会饱和归一化速度，因此接受/拒绝行为测试使用屏幕空间 kernel；18 个 Accumulate kernel 在 Frame Debugger 中同名，kernel 选择改由逐组合计数器证明；仅 Compatibility Execute，XR 逐眼矩阵留到 T13。

### 11.10 T9 实施与证据记录（2026-11-14）

**Core 材质契约**

- `ScreenSpaceLightingBlend.hlsl`：新增 `ScreenSpaceLightingApplyIndirectSpecularClearCoat` / `ScreenSpaceLightingEvaluateIndirectSpecularClearCoat`。clear coat 用一条共享 trace 和两个独立 reflection hierarchy：
  - coat 层使用 `weight` 与材质自己的 `clearCoatMask` / coat smoothness，采样 `lerp(coatEnv, radiance, weight)`；
  - base 层使用 `weight * lerp(1, 0, saturate((basePerceptualRoughness - CLEAR_COAT_PERCEPTUAL_ROUGHNESS) / 0.2))`（HDRP 的 coat-on-bottom-layer 规则），rough base 保留 probe；
  - `coatMask <= 0` 时退化为 base 单层，与 Lit 完全相同；miss（weight=0）时两层分别保留自己的 probe，不产生黑色反射或二次 probe/BRDF。
- `GlobalIllumination.hlsl`：clear coat 分支改用上述函数，base/coat 各自在 `EnvironmentBRDF` / `EnvironmentBRDFClearCoat` 中只乘一次 BRDF；SSR keyword 关闭时该函数直接返回原 probe 值，因此非 SSR 图像不变。
- `LitDepthNormalsPass.hlsl`：ComplexLit 的 `_CLEARCOAT` / `_CLEARCOATMAP` 变体在 DepthNormals alpha 写入 `0.5 + 0.5 * (1 - CLEAR_COAT_SSR_PERCEPTUAL_ROUGHNESS)`（即 1.0），并在 `clearCoatMask > 0` 时才覆盖；mask=0 保留 base smoothness。`REQUIRES_UV_INTERPOLATOR` 增加 `_CLEARCOATMAP`。`ComplexLit.shader` 的 `DepthNormalsOnly` pass 增加 coat 关键字变体，使 SSR 与 HDRP 一样按 coat roughness 追踪。
- ShaderGraph：`UniversalTarget` 增加 `_SCREEN_SPACE_GI` / `_SCREEN_SPACE_SSR` 关键字描述符，`UniversalLitSubTarget` 的 Forward/GBuffer 关键字集合加入两者；`PBRGBufferPass.hlsl` 改用带 `positionWS + normalizedScreenSpaceUV` 的完整 GI 重载；`DepthNormalsOnlyPass.hlsl` 写 smoothness 编码（`0.5 + 0.5 * smoothness`），complex lit 时按 `CoatMask` 写 coat SSR smoothness；`FragmentDepthNormals` 端口掩码增加 Smoothness/CoatMask/CoatSmoothness，`DepthNormalOnly` pass 在 complex lit 时定义 `_CLEARCOAT`。
- 不支持材质：SimpleLit、Particles Lit、Terrain Lit 的 DepthNormals 继续写 alpha 0，且不声明 `_SCREEN_SPACE_GI/_SCREEN_SPACE_SSR`；Terrain 的 forward 路径仍走不带 screen UV 的 legacy GI 重载。它们因此不可能获得非零 SSR/SSGI 权重。

**验证器**

- `Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT9Verification.cs` 生成三份 T9 Renderer/Pipeline（各含 SSL + 读深度法线的探针 Feature）、两个 ShaderGraph（Lit 与 Complex Lit，后者 CoatMask=1），运行时搭建 clear coat 球、alpha test 棋盘面、LODGroup、ShaderGraph 球、SimpleLit/Particles/Terrain Lit 球，以及镜像地面。
- SSR 权重通过 `SsrWeight` 调试视图读取（屏幕空间栅格化，避免直接读取 compute 纹理时的坐标约定差异）；深度法线 alpha 由 203 处的探针 Feature 读取。
- 额外烘焙一个小场景（Mixed + Subtractive，1 张 lightmap）验证 SSGI/SSR 的 miss 与 intensity=0 不破坏 lightmap。

**退出证据**

| 项目 | 结果 | 入口 |
|---|---|---|
| T9 结论 | `t9ExitCriteriaMet=true` | `PLAN/Done/01-ScreenSpaceLighting/validation/T9/T9Report.json` |
| 三路径渲染/生命周期 | 三路径 `passed=true`；`CoatIntensity0` 逐字节恢复 probe 基线（三路径同一 SHA） | `PLAN/Done/01-ScreenSpaceLighting/validation/T9/T9Verification.json` |
| Clear coat 独立数据 | base smoothness 0.3 + coat mask 1 的球 trace weight 1.0（无 coat 编码时为 0）；coat smoothness 0.2↔1 改变 270,919 像素（Deferred 270,957），coat mask 1↔0 改变 271,300 像素（Deferred 271,339） | 同上 |
| 深度法线编码 | coat 球 alpha 1.000、coat mask 0 球 0.654、SimpleLit/Particles/Terrain 0.000、SG Lit 0.748、SG Complex Lit 1.000 | 同上 |
| 不支持材质 | SimpleLit/Particles/Terrain 的 SSR weight 全为 0；Terrain Lit 用其 shader + 平面隔离材质契约 | 同上 |
| 无二次 probe/BRDF | SSR on 时 572,746 个零权重像素与 probe 图像逐像素一致（最大通道差 0）；intensity=0 逐字节恢复 | 同上 |
| Alpha test | 棋盘面 974 个实体像素 weight 1.0、19,713 个裁剪像素 weight 0 | 同上 |
| LOD | ForceLOD 0/1 均为 weight 1.0，两级图像差 1,299 像素 | 同上 |
| ShaderGraph | SG Lit 的 SSR/SSGI 均改变图像，SSR weight 1.0；SG Complex Lit 在 intensity=0 时与基线逐字节一致 | 同上 |
| Lightmap/Mixed | 1 张 lightmap、Subtractive；三路径 intensity=0 恢复 lightmap 基线、效果开启后图像改变、无 NaN/洋红 | 同上 |
| 支持矩阵 | Lit/ComplexLit/ShaderGraph Lit 支持；SimpleLit/Particles/Terrain Lit 明确回退（weight 0）；APV 在 URP 14.0.12 无 ProbeVolume 集成，标为不支持 | `T9Report.json` |
| EditMode | 132 passed / 0 failed（新增 `ScreenSpaceLightingMaterialTests` 6 项） | `PLAN/Done/01-ScreenSpaceLighting/validation/T9/UnityTests.log` |

**已知限制**

- SSR 只追踪 base 法线；clear coat 仅把 trace roughness 提升到 coat SSR roughness（0），不写几何法线，也不追踪独立的 coat 法线；coat 层与 base 层共用同一 radiance，仅权重/mask/probe 独立。
- `minSmoothness=1` 的“全 miss”配置仍会让 coat roughness 0 的像素通过（`>` 判定），因此 miss 回归以 572,746 个实际零权重像素与 probe 图像逐点比较，而不是整图相等。
- 测试场景中的 Terrain 使用 Terrain Lit shader + 平面，原因是用手动 `Camera.Render` 循环时运行时创建的 Terrain 对象不被绘制；Terrain 的材质契约（DepthNormals alpha 0、无 SSL 关键字、legacy GI 重载）由 EditMode 源测试与图形权重共同固定。
- SSR 权重读取改用 `SsrWeight` 调试视图；直接 blit compute 输出的 RTHandle 会因坐标约定导致纵向偏移，不作为证据来源。
- APV：URP 14.0.12 Runtime 没有 ProbeVolume 集成，无法在本包内验收；lightmap/SH/mixed lighting 回归通过。

### 11.11 T10 实施与证据记录（2026-11-15）

**Core 材质契约**

- 新增 `Shaders/Utils/TransparentDepthNormalsPass.hlsl`：复用 `LitDepthNormalsPass.hlsl` 的顶点/片元与 smoothness 编码（`0.5 + 0.5 * smoothness`，0 表示不支持），仅在 `_TRANSPARENT_SSR_RECEIVE` 定义时写入；否则 `clip`，防止未 opt-in 的透明表面成为其他透明材质的 ray origin。
- `Lit.shader`、`ComplexLit.shader`：新增 `[ToggleUI] _TransparentSSRReceive`、Forward 的 `_TRANSPARENT_SSR_RECEIVE` shader_feature 与 `_SCREEN_SPACE_TRANSPARENT_SSR` multi_compile、新的 `TransparentDepthNormals` LightMode pass（`ZWrite On`、`Cull[_Cull]`、alpha test / normal / detail / metallic-gloss / smoothness channel；ComplexLit 含 `_CLEARCOAT/_CLEARCOATMAP`）。该 pass 不声明 `_WRITE_RENDERING_LAYERS` 与 `_GBUFFER_NORMALS_OCT`，私有正常目标始终使用 Forward/SNorm 编码。
- `ScreenSpaceLightingInput.hlsl` / `ScreenSpaceLightingBlend.hlsl`：新增 `_ScreenSpaceTransparentSsrTexture/_Params`；`ScreenSpaceLightingApplyIndirectSpecular` 与 clear coat 包装在 `_SCREEN_SPACE_SSR` 之后增加 `_TRANSPARENT_SSR_RECEIVE && _SCREEN_SPACE_TRANSPARENT_SSR` 支路，仍使用同一 `lerp(probe, radiance, weight)` 契约与独立 BRDF 只乘一次。
- `ShaderKeywordStrings.ScreenSpaceTransparentSSR/TransparentSSRReceive`；`ScriptableRenderer.ClearRenderingState` 每相机禁用透明关键字；`LitGUI.SetMaterialKeywords` 仅在透明材质且开关开启时启用材质关键字，`LitShader.DrawSurfaceOptions` 在透明表面类型下显示开关。
- SimpleLit/Particles/Terrain/Unlit 无该 pass 与关键字，继续走 probe；ShaderGraph 透明 prepass/receive 未实现，归 T11 与折射的 ShaderGraph 改造一起处理。

**Plugin**

- `ScreenSpaceLightingHistory` 新增逐相机私有 `transparentDepth`（Depth32）、`transparentNormals`（`DepthNormalOnlyPass.GetGraphicsFormat()`）、`ssrTransparentHitPoint`（R16G16_SFloat）与 `ssrTransparentOutput`（RGBA16F）；不透明与透明输出/hit point 完全分离。新增 `frameSerial`，每次 `BeginFrame` 递增。
- `ScreenSpaceLightingTransparentDepthNormalsPass`（447）：复用 URP `CopyDepth` 材质和 `_OUTPUT_DEPTH` 把 `_CameraDepthTexture` 写入私有 depth，清除 `_DEPTH_MSAA_*` 避免继承 CopyDepthPass 的全局状态；随后 `CommonTransparent` 排序、`ZWrite On` 绘制 `UniversalTransparentDepthNormals` 材质。主相机 depth/color 从未绑定。记录 `producedFrameSerial`。
- `ScreenSpaceLightingTransparentSsrPass`（449）：仅当 `prepass.producedFrameSerial == history.frameSerial` 时执行；绑定私有 depth/normal 为 ray origin、opaque packed Hi-Z 为 marching、上一帧 color pyramid 为 radiance；强制 `SSR_APPROX`、关闭 `SSR_NORMALS_OCT`、`_SsrReflectsSky=0`、PBR 参数全 0。输出使用独立纹理和 `_SCREEN_SPACE_TRANSPARENT_SSR` 关键字，`OnCameraCleanup` 恢复 black/0/off。无 accumulation 历史。
- Volume `ScreenSpaceReflection.enabledTransparent`（默认 false）；Feature 创建/入队两个新 Pass；Debug 模式新增 `TransparentDepth`、`SsrTransparentRaw`、`SsrTransparentWeight`；资源新增 `transparentDepthCopyShader`（`Hidden/Universal Render Pipeline/CopyDepth`），旧 Renderer 在 Inspector 打开时会通过 `ResourceReloader` 补齐空引用。
- `ScreenSpaceLightingEditor` 帮助文本更新；EditMode 新增 `ScreenSpaceLightingTransparentSsrTests`（事件顺序、串行号、资源隔离/重建/逐帧清空、Approximation-only、关键字契约）与材质 Source 契约测试。

**验证器与证据**

- `Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingT10Verification.cs` 生成三份 T10 Renderer/Pipeline 与四个持久化材质（Receiver / NoReceiver / SimpleLit / ComplexLit clear coat），运行时搭建镜像地面、发光背景、opt-in/opt-out/不支持透明球、双层透明球和 ComplexLit coat 透明球。
- 每个模式比较：Reference、OpaqueOnly、TransparentOn、TransparentIntensity0、TransparentOffAgain；调试视图 SsrWeight、SsrTransparentRaw/Weight、TransparentDepth、CameraDepth（有无 prepass）。
- 三路径 `T10Verification.json` 全部 `passed=true`；`T10Report.json` 的 `t10ExitCriteriaMet=true`；EditMode 142 passed / 0 failed。

| 项目 | 结果 | 入口 |
|---|---|---|
| 三路径结论 | `passed=true` | `validation/T10/T10Verification.json` |
| ray origin | receiver 透明 depth 0.006142 > opaque 0.003819，9,560 像素不同；双层 near-only=both=0.006007 > far 0.004822 | 同上 |
| 主透明深度 | prepass 前后 `CameraDepth` diff=0 | 同上 |
| 输出隔离 | transparent on/off 的 opaque `SsrWeight` diff=0；TransparentOn 仅改变 2,314 像素 | 同上 |
| 无历史误用 | PBR 下 transparent trace/reproject=4/4、accumulation=0、opaque accumulation=4 | 同上 |
| opt-in 契约 | receiver 785 px / weight 1.0；ComplexLit coat 1,266 px / weight 1.0；opt-out 与 SimpleLit 0 px / weight 0 | 同上 |
| 无二次 probe/BRDF | intensity=0 与关闭后逐字节恢复 Reference/opaque-only | 同上 |
| 透明 motion | 旧位置回落 0.003819、新位置 0.006146、0 non-finite、weight∈[0,1] | 同上 |
| EditMode | 142 passed / 0 failed | `validation/T10/UnityTests.log` |

**已知差异与限制**

- URP 不生成透明 object motion；ray origin 每帧刷新，但 reprojection 使用 hit pixel/camera motion，移动透明体反射可能滞后。
- Transparent SSR 只使用 Approximation 和上一帧 color pyramid；上一帧 pyramid 含上一帧透明贡献，形成有界反馈。
- 透明反射关闭 sky 命中（与 HDRP 一致），sky 回退到 reflection probe；首版不包含 pre-refraction transparent 顺序（折射属 T11）。
- ShaderGraph Lit 透明 pass/receive 未实现，继续回退 probe。
- 顺序由 447/449 事件与 `frameSerial` 运行时守卫证明（PBR 4 帧 4/4 dispatch）；本轮未采 Frame Debugger，T12 组合回归可补。
- 透明 prepass 使用 Renderer Data 的 `transparentLayerMask`（核心 `UniversalRenderer.transparentLayerMask` 只读访问器），与内置透明 pass 的过滤一致；私有 depth/normal 目标恒为单采样，MSAA 相机组合由 T12 验证。
- 仅 Compatibility `ScriptableRenderPass.Execute`；XR 单视图矩阵留到 T13。私有透明资源 1280×720 约 20 MB。

### 11.12 T13 实施与证据记录（2026-11-15）

**XR 运行时与逐眼数据**

- `ScreenSpaceLightingXr`（Runtime/RendererFeatures/ScreenSpaceLighting）统一视图数量/资源维度/逐眼矩阵计算；shader 中 `SSL_VIEW_PROJ`、`SSL_INV_VIEW_PROJ`、`SSL_PREV_INV_VIEW_PROJ`、`SSGI/SSR 相机位置` 在 `SSL_XR_ARRAY` 下按 dispatch slice 选择，单视图路径仍使用 `unity_MatrixVP` 与 SSGI CB。
- `ScreenSpaceLightingHistory` 新增逐眼上一帧 VP（`RecordViewProj`/`TryGetPreviousViewProj`），在深度金字塔 pass（203）记录；non-XR 继续使用 `motionVectorsPersistentData.previousViewProjection`。`EnsureResources` 重分配时清空本帧读指针，避免绑定已释放的 RTHandle。
- `CameraHistoryStore` 继续按 (camera, multipassId) 分键；Feature 检测到 viewCount 布局变化（multipass ↔ single-pass）时清空 store，防止另一只眼的旧 entry 残留。
- GTAO 增加 `_GtaoXrViewRow0/1/2[2]` 并按 `GTAO_SLICE` 选择，修复 single-pass 右眼使用左眼 view 旋转的问题。
- **关键缺陷修复**：`ScreenSpaceLightingCommon.hlsl` 的数组纹理分支原先用 `SSGI_XR_ARRAY` 守卫，而实际 keyword 是 `SSL_XR_ARRAY`，导致 single-pass 下 compute 仍绑定 2D 资源、dispatch 被 Unity 校验丢弃（hit point 全 0）。修正后三 API 双模式全部通过。
- Foveation：颜色金字塔、颜色历史捕获、透明 depth/normal prepass 与调试视图在 `xr.supportsFoveatedRendering` 时 `SetFoveatedRenderingMode(Disabled)`，结束后恢复。

**测试环境**

- 新增测试依赖 `com.unity.xr.management@4.5.3`、`com.unity.xr.mock-hmd@1.5.0-exp.3`、`com.unity.inputsystem@1.7.0`（其余传递依赖自动解析）。`Assets/XR` 的 Mock HMD loader 关闭 “Initialize XR on Startup”，只有 T13 播放器显式启动显示。
- Mock HMD 仅在独立播放器创建 render pass（编辑器 edit/play mode 的 `GetRenderPassCount()` 为 0），因此 XR 证据由 `SslT13XrPlayer.exe` 生成；播放器构建包含 DX11/DX12/Vulkan 三 API。

**证据**

| 项目 | 结果 | 入口 |
|---|---|---|
| T13 结论 | `t13ExitCriteriaMet=true` | `PLAN/Done/01-ScreenSpaceLighting/validation/T13/T13Report.json` |
| XR 播放器（DX11/DX12/Vulkan） | 双模式 `passed=true`；参考恢复、两眼镜像、SSGI/SSR/折射/GTAO 两眼可见、无 NaN | `Player/Run{DX11,DX12,Vulkan}/T13XrPlayerVerification.json`、14 PNG/轮 |
| 历史隔离 | single-pass 1 entry×2 slices、深度金字塔 2 slices、上一帧颜色金字塔两 slice 哈希不同；multipass 2 entries×1 slice、两个 prev 矩阵 | 同上各 mode 字段 |
| 平台能力 | DX11/DX12/Vulkan compute+UAV 格式清单；DX12/Vulkan `FoveationImage`，DX11 `None` | `T13EditorVerification.{DX11,DX12,Vulkan}.json` |
| 动态分辨率 | 记录为引擎限制：DX11 `IntermediateTargetsNotRescaled`、DX12/Vulkan `InvalidOutput`；静态 render scale 由 T12 覆盖 | 同上 `dynamicResolution` |
| T12 回归 | Forward/Forward+/Deferred 34/34/35 用例通过 | `T12RegressionVerification.json`、`UnityT12Regression.Run1.log` |
| EditMode | 153 passed / 0 failed | `PLAN/Done/01-ScreenSpaceLighting/validation/T13/UnityTests.log` |

**已知限制**：真实 HMD、Metal、mobile/OpenGLES 未测试；foveation 只验证守卫与立体正确性，未比较图像级 foveation 质量；动态分辨率在本 URP 版本不可用（记录为不支持）；Mock HMD 分辨率固定 1512×1680，与 T1 的 1280×720 预算不可直接比较，T13 不新增 GPU 预算结论。

### 11.13 T14 实施与证据记录（2026-11-15）

**回归入口**

- `PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14EditModeTests.ps1`与 `Invoke-T14PlayModeTests.ps1`：都通过 Unity CLI `-runTests -testPlatform {EditMode,PlayMode} -testResults <xml> -testFilter ...` 驱动。EditMode 沿用 `ScreenSpaceLightingTestRunner` 的 18 个测试类过滤；PlayMode 运行新增的 `Unity.ScreenSpaceLighting.Tests.PlayMode` 程序集。原因：`TestRunnerApi` + `-executeMethod` + `-quit` 在本机出现“下一次编辑器 tick 才启动运行、quit 先到”的空跑（T14 第一次尝试即如此，日志保留为 `UnityTests.Run1ExecuteMethodAborted.log`）。
- `Invoke-T1Unity.ps1` 的 `-ExecuteMethod` 改为可选，供 `-runTests` 自行决定退出；既有调用方式不变。
- `Invoke-T14GraphicsRegression.ps1`：复用 `ScreenSpaceLightingT12Verification.RunT12Verification`，通过新增的 `-ssl-t12-output <path>` 参数把报告与 PNG 写进 `T14/Graphics`，不覆盖 T12 证据。
- `Invoke-T14FrameDebugger.ps1`：交互式编辑器采集；`T14/FrameDebugger/FrameDebuggerReport.json`。
- `ScreenSpaceLightingT14PlayerSetup.BuildReleasePlayer`：`BuildOptions.None`（无 Development、无 IncludeTestAssemblies）构建两个场景——T13 XR 播放器场景（启动场景，运行时验证）+ 自动生成的 `ScreenSpaceLightingT14Stripping.unity`（引用 ComplexLit/SimpleLit/ShaderGraph 材质，并为两个 T9 ShaderGraph 生成 `SslT9*T14.mat`）。通过 `BuildReport.packedAssets` 审计 17 个必需 ComputeShader/Shader 是否进包。
- `Analyze-T14Evidence.py`：汇总 XML/log/JSON/PNG，输出 `T14Report.json`、`T14SupportMatrix.md`、`T14KnownDifferences.md`、`T14Performance.json`、`T14ArchiveManifest.json`，并核对 T13 requiredFiles 哈希与 `T1..T13` 证据目录无未提交改动。

**PlayMode 套件**（`Assets/Tests/ScreenSpaceLighting/Tests/PlayMode`）

- `AllEffectsDispatchAndRestoreReference(Forward|ForwardPlus|Deferred)`：真实播放循环中三路径的深度/颜色金字塔、SSGI、SSR、透明 prepass/SSR、GTAO 每帧派发，setup 绑定计数、history entry/frameSerial/validFrameCount、全局 keyword 清理、关闭后逐像素恢复参考图。
- `RuntimeControlsAndCameraLifetime(...)`：运行中 `SetActive(false)` 后不再派发且恢复参考、移除 Volume 组件后不派发、第二相机独立 entry、销毁后回收。
- 插件内部（history store、pass 计数器、GTAO settings）通过反射读取：`PluginIsolationTests` 禁止 Core 源码点名插件类型，因此未添加 `InternalsVisibleTo`。

**证据**

| 项目 | 结果 | 入口 |
|---|---|---|
| T14 结论 | `t14ExitCriteriaMet=true`（九项 criteria 全 true） | `PLAN/Done/01-ScreenSpaceLighting/validation/T14/T14Report.json` |
| EditMode | 153 passed / 0 failed | `UnityEditModeTests.xml`、`UnityEditModeTests.Run2.log` |
| PlayMode | 6 passed / 0 failed | `UnityPlayModeTests.xml`、`UnityPlayModeTests.Run5.log` |
| 图形回归 | 34/34/35 用例通过；103 张图像哈希与 T12 记录完全一致 | `Graphics/T12Verification.json`、103 PNG |
| Frame Debugger | 94/94/105 事件；Motion(7/7/9) → SSL Hi-Z(9/9/10) → GTAO Hi-Z(20/20/21) → SSGI(36/36/37) → SSR(43/43/44) → opaque/GBuffer(45/45/46) | `FrameDebugger/FrameDebuggerReport.json` |
| Release Player | Succeeded、0 errors、163,703,618 B、2093 packed assets、17/17 必需资源在包内 | `T14PlayerBuildSummary.json` |
| 播放器运行 | DX11/DX12/Vulkan × MultiPass/SinglePassInstanced 全部 `passed=true` | `Runs/{DX11,DX12,Vulkan}/T13XrPlayerVerification.json` |
| 支持矩阵/已知差异/性能/归档 | 已生成 | `T14SupportMatrix.md`、`T14KnownDifferences.md`、`T14Performance.json`、`T14ArchiveManifest.json` |

**已知限制与清理**：Vulkan release player 写完证据后不自行退出（T13 同样如此），由工具终止，不计退出码；插件级 build strip 未在 T14 引入（四条 `_SCREEN_SPACE_*` 变体在每次构建中保留，列入已知差异）；`Assets/Settings/URP-{HighFidelity,Balanced}-Renderer.asset` 中 T5 删除 SSAO 后残留的 SSAO Feature 已移除（此前构建日志中 `missing RendererFeatures` 报错的根因）；删除 `PLAN/Done/01-ScreenSpaceLighting/validation/T1/Player/` 与 `PLAN/Done/01-ScreenSpaceLighting/validation/T14/Player/`。

## 12. 验证矩阵

| 维度 | 用例 |
|---|---|
| Renderer | Forward、Forward+、Deferred、Deferred 实际回退 Forward、Deferred ForwardOnly |
| Feature 安装 | 仅 GTAO、仅 SSL、两者不同顺序、无 Feature |
| 模块组合 | 单独 GTAO/SSGI/SSR、GTAO+SSGI、GTAO+SSR、SSGI+SSR、全部开启、运行中切换 |
| Volume | 组件缺失、active 开关、enabled 开关、overrideState、两个 Volume 混合、相机 Volume Mask、Post Processing 开关 |
| 分辨率 | 全/半分辨率、render scale 改变、窗口改变、历史重新分配 |
| 历史 | 首帧、camera cut、相机切换、同帧重复渲染、Domain Reload、算法切换 |
| 几何 | 静态/动态、disocclusion、alpha test、薄物体、屏幕边缘、天空、近/远裁剪 |
| 材质 | metallic/specular、粗糙度 0 与渐变、clear coat、emission、lightmap、SH/APV、reflection probes、不支持自定义 Shader |
| 透明 | 多层透明、透明在 opaque 前/后投影、无 prepass 材质、折射深度拒绝、transmittance、屏幕边缘 |
| URP 组合 | DBuffer/ScreenSpace Decal、Depth Priming、Opaque Texture、TAA、Motion Blur、HDR/MSAA |
| 相机 | Base/Overlay、Scene/Game、Preview、reflection、offscreen-depth |
| XR/平台 | multipass、single-pass、foveation；各目标 API 的 compute/UAV 格式 |

每次完整回归必须标明“通过 / 失败 / 不支持 / 未测试”；“未测试”不能计入通过，平台未实测不能宣称支持。

## 13. 产物留存与清理

### 13.1 必须留存

- 所有 Core/Plugin 源码、序列化资源引用、ComputeShader、材质端 include、`.meta` 和必要的生成 `.cs.hlsl`。
- 第 11 节测试源码、测试 Scene、三种 Renderer Data、Volume Profile、材质和 golden images。
- `PLAN/Done/01-ScreenSpaceLighting/validation/` 下的最终证据：原始/最终基准图、配置与 Frame Debugger 截图、测试 XML、性能 CSV、显存表、支持矩阵和已知差异说明。
- 对仍需追踪的问题保留最小复现场景和对应 RenderDoc capture；正常每次迭代的 capture 不全部归档。
- 性能报告必须包含 Unity/API/GPU、场景 commit/版本、分辨率、设置、测试日期和相对基线增量，孤立毫秒数无效。

### 13.2 运行时与代码清理

- `Dispose()`、history prune 和尺寸重分配释放旧 RTHandle/ComputeBuffer/Material；禁止每帧 `new Material`、未释放 `CommandBuffer` 或静态相机引用。
- 每相机结束清理全局 keyword/texture；禁用时 AO 回白色 neutral，SSGI/SSR 回 black+weight 0；debug 不改变正常输出。
- 移除临时 Console spam、硬编码本机绝对资源路径、试验性 shader 分支、无效 UI 参数和未使用 kernel；保留一次性、可诊断的能力/冲突警告。
- 最终 Player 不包含仅 Editor 的测试场景和调试资源；必要 ComputeShader/变体不能因清理被误 strip。

### 13.3 临时文件清理边界

- `Library/`、`Temp/`、`Logs/`、默认 TestResults、本地临时截图和中间 capture 可在最终证据复制到 `PLAN/Done/01-ScreenSpaceLighting/validation/` 后由开发者按项目流程清理。
- `PLAN/Done/01-ScreenSpaceLighting/validation/T1/Player/` 仅是 T1 临时 Development Player；两次有效运行、图像检查和设置恢复核对完成后删除，保留生成/构建工具、BuildSummary、日志、PNG、CSV 与报告，不把 Player 本体作为长期证据。**T14 已于 2026-11-15 删除该目录**。
- `PLAN/Done/01-ScreenSpaceLighting/validation/T14/Player/` 是 T14 release 回归的临时 Player（约 157 MB），归档后删除，由 `ScreenSpaceLightingT14PlayerSetup.BuildReleasePlayer` 重新生成；两处 Player 目录均已在 `.gitignore` 中忽略。
- **不得为了清理而直接删除用户已有目录。**T14 先列出生成物清单，只删除本任务创建且已确认无留存价值的文件；用户原有场景、设置、PLAN 聊天原稿和其他改动不动。
- 原始聊天文件 `PLAN/Done/01-ScreenSpaceLighting/chat/HDRP 屏幕空间光照 迁移 到 URP 可行性.md` 作为调研历史保留，不用最终计划覆盖。

## 14. 风险、冲突与一致性检查

| 风险或旧矛盾 | 等级 | 本计划的解决方式 |
|---|---|---|
| 一个总 Feature 包含 GTAO，违背独立设置要求 | 高 | 两个 Feature；GTAO 无 Volume，SSGI/SSR/Refraction 由 SSL + Volume 管理 |
| Core 直接持有插件历史，形成反向依赖 | 高 | Feature 自管 history store；不改 UACD；Core 无插件类型引用 |
| 独立 Early Motion 与内置 Motion 重复且深度口径不同 | 高 | Core 复用并提前内置 Motion，同步提前 CopyDepth，回归 TAA/Motion Blur |
| GTAO/SSL 共享全局 Hi-Z 导致顺序与释放竞争 | 高 | 首版资源隔离；允许重复成本；跨 Feature broker 另立优化任务 |
| smoothness=0 与未适配 Shader alpha=0 无法区分 | 高 | DepthNormals alpha 使用 0.5–1 有效编码，0 表示无效 |
| Deferred GBuffer 后合成导致 emission/GI 重复 | 高 | 固定 Pre-GBuffer 消费，不改 GBuffer3 布局 |
| 透明 SSR 错用 opaque normal/depth | 高 | 新增透明 depth/normal prepass 和独立输入；T10 单独验收 |
| Feature 被误写成直接加到 Pipeline Asset | 中 | 明确 Pipeline Asset -> Renderer List -> Renderer Data -> Add Renderer Feature |
| Volume 被误写成直接添加 Pass | 中 | Volume 只提供 stack 参数，Feature 决定入队；非 PostProcessComponent |
| Prepass 与 decal 后材质不一致 | 中 | 首版明确 decal 前输入并测试；需要时另做 material prepass |
| SSGI 全分辨率与 HDRP 当前返回行为不同 | 中 | 有意使用 denoised 输出，保留 raw/denoised A/B 证据 |
| 折射被误当成独立 compute 或 URP 已有材质参数 | 中 | 明确透明材质逻辑；新增属性/GUI/ShaderGraph 和 fallback |
| Compute/UAV、XR、动态分辨率差异 | 中 | 能力检测、逐眼/逐平台测试，未实测标不支持 |
| 额外 DepthNormals 和双 Hi-Z 成本 | 中 | T1 预算、T5/T12/T13 独立 profiling；正确性后再优化 |
| 透明折射顺序和透明 motion 不完全等同 HDRP | 中 | 明确限制、图像测试；完整等价需拆分 transparent queue/补透明 motion |
| 测试只在最终收尾执行 | 中 | 每个 T 有退出条件；T12 做交叉组合，T14 做发布回归 |
| 清理误删用户资产或测试证据 | 中 | 先归档并列清单，只清本任务临时产物；保留原聊天和测试资产 |

## 15. 完成定义（Definition of Done）

只有满足以下条件，才可宣称“HDRP ScreenSpaceLighting 非 DXR 一套已迁移并支持 Forward/Forward+/Deferred”：

1. T0–T14 中所有宣称交付的任务均有退出证据；GTAO、SSGI、Opaque/Transparent SSR、Refraction、clear coat 的状态分别列出，不用 opaque 版本代替全套完成。
2. 两个 Feature 都能通过 Renderer Data 配置；GTAO 使用独立 Inspector 设置；SSGI/SSR/Refraction 能通过 Volume Add Override 配置并真实控制 Pass。
3. Forward、Forward+、Deferred 和 ForwardOnly 通过功能、历史、材质、构建测试；公开设置均真实生效，不存在只有 UI 的无效参数。
4. 无重复 GI/AO/emission、错误 pre-exposure、无效 motion/roughness、首帧历史污染、透明输入错用或禁用后画面污染。
5. Core 不依赖具体插件类型；无 Feature 时原画面、时序和 stripping 回归通过；移除插件源码后 Core 可编译。
6. 所有有意差异均记录：URP probe fallback、decal 前材质、SSGI 全分辨率 denoise、独立 Hi-Z 成本、透明 motion、折射 transparent 顺序和平台/XR 限制。
7. 性能、显存、Player 构建、shader stripping 和目标平台达到 T1 约定的发布标准；未测试平台不得宣称支持。
8. 测试、基准图、性能报告和支持矩阵已归档，临时资源已清理，文档状态与实际证据一致。

若仅完成 P0 的 GTAO/SSGI/Opaque SSR，必须标为“部分迁移”，T10/T11 等保持未完成，不得使用上述全套完成表述。

**T14 归档状态（2026-11-15）**：上述 1–8 项均由 `PLAN/Done/01-ScreenSpaceLighting/validation/T14/T14Report.json` 的九项 criteria 与既有 T0–T13 证据覆盖；可按“已迁移（非 DXR）并支持 Forward/Forward+/Deferred”交付。以下子集按证据分别声明：GTAO、SSGI、Opaque SSR（Approximation + PBR Accumulation）、Transparent SSR（Approximation-only）、Refraction、ComplexLit clear coat 全部通过；APV、硬件动态分辨率、Metal/mobile/OpenGLES/真实 HMD 明确不宣称支持；插件级 build strip 与 blue-noise 采样列为后续优化，不属本次宣称。已知差异清单见 `PLAN/Done/01-ScreenSpaceLighting/validation/T14/T14KnownDifferences.md`，支持矩阵见 `PLAN/Done/01-ScreenSpaceLighting/validation/T14/T14SupportMatrix.md`。

## 16. 主要源码证据索引

- HDRP 帧顺序：`HDRP/Runtime/RenderPipeline/HDRenderPipeline.RenderGraph.cs:101-228`。
- HDRP 透明 SSR/prepass：同文件 `:1161-1185`、`:1373-1410`。
- HDRP prepass/depth pyramid：`HDRP/Runtime/RenderPipeline/HDRenderPipeline.Prepass.cs:178-298`。
- HDRP SSR C#：`HDRP/Runtime/RenderPipeline/HDRenderPipeline.LightLoop.cs:1181-1513`。
- HDRP SSGI C#：`HDRP/Runtime/Lighting/ScreenSpaceLighting/HDRenderPipeline.ScreenSpaceGlobalIllumination.cs:455-477`。
- HDRP Hi-Z：`HDRP/Runtime/Lighting/ScreenSpaceLighting/RayMarching.hlsl:5-140`。
- URP pass 事件：`URP/Runtime/Passes/ScriptableRenderPass.cs:51-148`。
- URP 输入需求：`URP/Runtime/UniversalRenderer.cs:1437-1495`。
- URP DepthNormals 分配/Deferred tags：`URP/Runtime/UniversalRenderer.cs:965-1036`。
- URP CopyDepth/CopyColor/Motion：`URP/Runtime/UniversalRenderer.cs:1148-1191`。
- URP Motion 构造时序：`URP/Runtime/UniversalRenderer.cs:286-299`。
- URP Forward GI：`URP/ShaderLibrary/GlobalIllumination.hlsl:353-400`。
- URP Deferred GBuffer GI：`URP/Shaders/LitGBufferPass.hlsl:211-222`。
- URP Deferred AO：`URP/Shaders/Utils/StencilDeferred.hlsl:286-318`。
- URP AO 语义：`URP/ShaderLibrary/AmbientOcclusion.hlsl:17-65`。
- URP DepthNormals alpha：`URP/Shaders/LitDepthNormalsPass.hlsl:92-149`。
- URP Motion 实现：`URP/Runtime/Passes/MotionVectorRenderPass.cs:17-120`。
- URP Volume 菜单示例：`URP/Runtime/Overrides/MotionBlur.cs:45-75`。
- URP Feature 添加入口：`URP/Editor/ScriptableRendererDataEditor.cs:71-107`。
- URP 构建测试模式：`URP/Tests/Editor/ShaderBuildPreprocessorTests.cs:17-110`。
