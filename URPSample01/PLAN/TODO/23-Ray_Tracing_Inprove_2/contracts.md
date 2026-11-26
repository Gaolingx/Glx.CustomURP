# 23 冻结契约（P0）

**冻结时间：** 2026-11-26
**基线提交：** `350d18c`
**修订：** 2026-11-27 新增 C2.7（P1，additive：新缓冲 + 新版本位）；同日 P1 传输修订：ray payload 24 → 64 字节（closest-hit 不得写 UAV，表面经 payload 回传），发布结果仍为 24 字节
**适用范围：** 本轮 P1–P3 的全部改动。任何一条契约被打破，都必须先改本文件、改 tripwire 测试、重录基线，再进实现。

## C1. Pass 顺序与资源生命周期

| 项 | 冻结值 |
|---|---|
| SSR 注入点 | `AfterRenderingPrePasses + 6` = 206（[SsrPass.cs:31](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs#L31)） |
| GBuffer pass | 210 |
| ClusteredDeferred 光照 | 230（[UniversalRenderer.cs:367](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/UniversalRenderer.cs#L367)） |
| 输出消费者 | `ClusteredDeferred.compute` / `GlobalIllumination.hlsl` 的 GI 槽；全局资源名不变 |
| 透明 SSR | 独立 pass，Approximation + 前帧颜色，**本轮不动** |

- **P3 允许**：把不透明 SSR 移到 210 之后、230 之前；移动后仍必须在该帧 ClusteredDeferred 读 GI 槽之前完成。
- **P3 禁止**：把 SSR 移到 ClusteredDeferred 之后；修改 GI 槽的名字或采样语义；改变透明 SSR 的注入点。
- **守护**：`SsrP3ContractTests` 通过反射断言 `k_RenderPassEvent == 206`；P3 切换时该断言与本文一起更新。

## C2. 命中记录 ABI

| 项 | 冻结值 |
|---|---|
| 发布结果 | 24 字节：`float3 radiance @0`、`float hitT @12`、`uint flags @16`、`uint mode @20`（`GlxPublishedHit`） |
| ray payload | 64 字节：发布结果 @0–23 + 反射表面传输 @24–63（roughness @24、metallic @28、八面体法线 @32、baseColor @40、emissive @52；仅反射模式写，见 C2.7） |
| 硬上限 | 64 字节 |
| 递归深度 | 2 |
| 每个受支持 shader 的命名 hit pass | ≤ 1 |
| shadow / probe 模式 | 语义与字段保持不变 |

- **P1 允许**：为反射模式**新增**一份 hit 表面记录（GB0/GB1/GB2/GB3 + distance），并用独立版本位/缓冲区分；不得复用现有 24B 字段表达新语义。
- **P1 禁止**：把 radiance/hitT/flags/mode 任一项改义；让 shadow/probe 模式写新记录。
- **P1 约束**：closest-hit **不得写 UAV**（Unity 光追管线不支持材质 hit pass 写 UAV）。反射表面只能经 ray payload 的 @24–63 回传，由 raygen 组装并写入 C2.7 记录；shadow/probe 模式不填这 40 字节。
- **守护**：`SsrP3ContractTests` 读取 `GlxBasicPayload.hlsl`、`GlxPublishedHit.hlsl` 与 `SsrRayRequest.hlsl` 源码，断言字段顺序与数量。

### C2.7 hit 表面记录（P1 新增，24B payload 不变）

定义：[SsrHitSurface.hlsl](../../../Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/RayTracing/SsrHitSurface.hlsl)，stride 80，独立 `RWStructuredBuffer<SsrHitSurface>`。
索引 = R1 请求序号（`DispatchRaysIndex().x`）；消费者用 `_R1Queue[i].slot` 映射到像素槽。写入者 = SSR raygen：closest-hit 只把表面填进 ray payload @24–63，raygen 调 `SsrBuildHitSurface` 写记录。无效/未命中/unused 槽写全 0（version 0），因此不需要逐帧清空这个 80B 缓冲。

| 字段 | 通道 |
|---|---|
| `gb0` | xyz 线性 baseColor；w specular occlusion（未知时 1） |
| `gb1` | xyz 世界空间着色法线；w perceptual roughness |
| `gb2` | xyz fresnel0；w coat mask（URP 无 clear coat，恒 0） |
| `gb3` | xyz emissive（含 baked）；w 保留 0 |
| `misc` | x distance（`RayTCurrent()`，>0）；y surfaceType；z version；w 来源位 |

- surfaceType：`0` Lit、`1` SimpleLit、`2` Unlit。P1 只写 `Lit`（测试用 `GlxBasicHit` 按 Lit 近似）；`1/2` 在 P2 材质矩阵落地前不得写入。
- version：`SsrHitSurfaceVersion = 1`；`0` 表示空槽/无效。消费者必须校验，非当前版本按 miss 处理。
- 来源位：`misc.w & 1`，`0` = RT 命中，`1` = SS 命中（P3 写入）；其余位保留，未经版本升级不得占用。
- 写入范围：只有 `GlxReflectionMode` 的 closest-hit 填 payload 表面字段，raygen 也只在该模式的结果上组装记录；shadow / probe / ProbeGeo 模式一律不写。
- 传输精度：payload 里的法线是八面体 `float2`、金属度标量（raygen 合成 fresnel0），其余为 float；记录通道语义冻结，8bit vs 16F 的打包（P2 A/B）只能改编解码，不得改字段顺序、数量或 stride。

**材质能力边界**

hit 表面记录只承载标准 PBR 字段：baseColor、specular occlusion、normal、perceptual roughness、fresnel0、coat mask、emissive+baked、distance，以及 surfaceType（Lit/SimpleLit/Unlit）。

本管线**不提供** SSS、transmission、anisotropy：GBuffer 无对应通道，命中核无对应分支，契约**不为其保留字段、标志位或代码路径**。

只有能提供上述标准字段的材质才允许注册进反射用 RTAS；无法提供者视为不支持，在注册期报错（每材质一次）并跳过。**不得以第二条着色路径（含 closest-hit 近似）承接此类材质。**

## C3. 反射信号语义

- 输出 `rgb` = **非预乘**反射辐射；`a` = 覆盖率/权重。
- 材质端只做一次 `lerp(environmentSpecular, radiance, a)`（[ScreenSpaceLightingBlend.hlsl:66](../../../Packages/com.unity.render-pipelines.universal@14.0.12/ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingBlend.hlsl#L66)）；BRDF 只求值一次。
- `a == 0` 表示"保持 probe/sky"，不得解释为黑色反射。
- 权重构成：smoothness fade × screen-edge fade × RT 覆盖率，乘积夹在 [0,1]。
- **P1–P3 禁止**：改为预乘、把权重塞进 alpha 以外通道、在材质端二次乘 BRDF。

## C4. Denoiser 契约

| 项 | 冻结值 |
|---|---|
| 输入/输出分辨率 | trace 分辨率（半分辨率时 = 屏幕/2） |
| 颜色 | 非预乘 radiance（rgb） |
| 权重 | 当前帧覆盖率（a），时域/双边只混合 rgb 并原样透传 a |
| 历史 | `(radiance, sampleCount)` + 前帧 depth/normal；接收器空间复投影 |
| 平滑面 | AABB clip，feedback `lerp(0.96, 0.91, w²)` |
| 粗糙面 | 指数累积，样本数饱和 8，factor 0.93 |
| 历史拒绝 | 法线阈值 0.75 |

- **P3 禁止**：把 denoiser 当作"着色缝修复器"（如按 SS/RT 来源分块滤波、按边界 clamp 辐亮度）；改变输入布局。
- **允许**：P3 之后另行排期的噪声修复（coverage/mip）可调滤波参数，但必须单独验收。

## C5. History 捕获契约

- 颜色金字塔、前帧 depth/normal 在透明之后捕获；ping-pong 读写槽与 `hasValidHistory` 语义不变。
- 冷启动/算法切换/denoise 开关/分辨率变化触发重置。
- **P3 必须保持**：即使 SSR 注入点后移，历史捕获仍然发生在"本帧着色完成后、下一帧 SSR 之前"，且不得采到半更新的 GBuffer。

## C6. Fixture 契约

| 项 | 冻结值 |
|---|---|
| 场景 | `Assets/Tests/RayTracing/SSR/Fixture/SsrP3Baseline.unity`（由基线场景复制，移除性能 runner） |
| Volume | `SsrP3BaselineProfile.asset`：SSR on、PBR Accumulation、**半分辨率**（DXR 资格）、denoise 开、rayLength 50、bias 0.5、minSmoothness 0.9、smoothnessFadeStart 0.9、depthBufferThickness 0.01、screenFadeDistance 0.1、rayMaxIterations 64、reflectSky 开、intensity 1、denoiserRadius 8、affectSmoothSurfaces 关、singleReflectionBounce 关；SSGI/Refraction 不启用 |
| 渲染器 | `SsrP3DeferredRenderer.asset`：Deferred + ScreenSpaceLighting feature（`rayTracingReflections = 1`、`reflectionRayBudget = 32768`） |
| 相机 | 基线场景作者相机，位置/旋转/55° FOV 原样；`renderPostProcessing = false`、AA None、HDR on |
| 分辨率/格式 | 1920×1080，RGBA16F 线性，无 MSAA，无动态分辨率 |
| 时钟 | `Time.captureFramerate = 60`，预热 120 次相机渲染，第 121 次捕获 |
| API | Direct3D12（DXR 资格由 `SystemInfo.supportsRayTracing` 记录，不作为 dispatch 证据） |
| 随机 | R1 seed 20261125（默认）；不使用 `UnityEngine.Random` |
| 变体 | `base`、`nodenoise`、`ssroff`，各自独立进程 |

- **禁止**：在两次对比运行之间重新生成场景/材质/Volume 参数；改相机；改分辨率。
- 场景/资产 blob 哈希写入 `validation/P0/Freeze.json`。

## C7. 证据契约

- 目录：`validation/<ID>/<platform>-<renderer>/<variant>/run-XX/`。
- 每个 run：`Player.json`、`frame.raw`（线性 RGBA16F）、`frame.png`（tonemap 审查用）、`Summary.json`（含 SHA256、diff 统计）。
- 同一 API、同一构建的两个全新进程必须逐位一致；不一致必须记录差异像素数与最大通道差并判 BLOCKED。
- 未完成的 gate 明确标注 OPEN，不以"通过"代替。

## C8. 布局 tripwire（测试强制）

| 项 | 值 | 来源 |
|---|---|---|
| `R1Sample` | 80 字节 / 16 字段 | `SsrRayRequest.hlsl` |
| `GlxPublishedHit` | 24 字节 / 4 字段 | `GlxPublishedHit.hlsl` |
| `GlxBasicPayload` | 64 字节 / 9 字段（前 4 个为发布头） | `GlxBasicPayload.hlsl` |
| `SsrHitSurface` | 80 字节 / 5 字段 / version 1 | `SsrHitSurface.hlsl`（C2.7） |
| payload 硬上限 | 64 字节 | 20 号 B0 Freeze |
| tile | 8×8 | `SsrRequestQueue.hlsl` / `SSR_TILE_SIZE` |
| `_R1Counts` | 11 × uint | `SsrRequestQueue.cs` |

- 测试文件：`Assets/Tests/RayTracing/SSR/SsrP3ContractTests.cs`。
- 任何布局变化必须先改本节与测试；否则 CI/本地 EditMode 直接失败。
