# 09-DDGI_improve 主流方案调研报告

> 状态：调研更新（2026-11-19），供 [HANDOFF.md](HANDOFF.md) 决策使用
> 基线：调研起草于 `0a73b2b`；实施基线按 `720bedd` 重测，见 HANDOFF §2.2
> 范围：桌面 URP 的 DDGI；参考实现为 `E:\workspace\GitHub\RTXGI-DDGI-URP`
> 对照：NVIDIA RTXGI 1.x/2.x、RTXDI（ReSTIR）、NRD、UE5 Lumen、HDRP APV + SSGI/RTGI、本仓库 Screen Space Lighting
> 本报告只做调研与选型论证；模块边界、任务与验收以 HANDOFF 为准。

---

## 0. 结论先行

1. **本项目要做的是“探针式 DDGI”，不是“ReSTIR + SVGF 路径追踪”。** 参考仓库 `RTXGI-DDGI-URP` 只实现了 DDGI（probe 射线 + 八面体辐照度/距离矩 + 时序 hysteresis + relocation/classification），**没有 ReSTIR、没有 reservoir/sample pool、没有 SVGF/à-trous**。把三者混成一句话是常见误解。
2. **NVIDIA 的三条产品线要分开看**：
   - **RTXGI 1.x = DDGI**（probe 网格）；**RTXGI 2.x 已转向 NRC + SHaRC 辐射缓存**，不再是 DDGI。
   - **RTXDI = ReSTIR DI/GI/PT**（直接光/路径的样本复用），不是 GI 缓存。
   - **NRD = RELAX / ReBLUR / SIGMA**（生产降噪器）；**SVGF 是 2017 年论文，RELAX 是它的工业化演进**；RTXGI/RTXDI SDK 里没有 SVGF。
3. **Lumen 走的是“表面缓存 + 世界辐射缓存”路线**，不是探针 DDGI，也不是 ReSTIR；每帧 final gather 只算约一跳，多跳靠 surface cache radiosity + radiance cache 跨帧反馈。UE 5.5–5.7 的 MegaLights 是**随机直接光**，不是 Lumen GI 的 ReSTIR。
4. **“DDGI 为 RTGI 提供 multi-bounce”只在旧 RTXDI 1.2 集成示例里成立**；现代 RTXDI GI/PT 不消费 DDGI。DDGI 自己的多跳是“命中点采样上一帧探针辐照度”的跨帧能量反馈。
5. **本项目选型**：桌面 D3D12 + DXR；单一静态 volume；第一版只覆盖内置不透明材质；DDGI 与 SSGI 互斥；只借鉴参考仓库的数据面/采样/更新数学，**重做调度、多跳反馈、灯光收集、材质接入和能量处理**。理由与边界见 §7 与 HANDOFF。
6. **本仓库现有 SSGI/SSR 的输入、降噪、移动端与“不依赖 G-buffer”的结论见 §5.7**；DDGI 是对屏幕空间路线的补充（屏幕外 + 多跳），不是替代。

---

## 1. DDGI 的通用骨架

DDGI 在世界空间维护一个 probe 网格，每个 probe 用一张八面体辐照度图 + 一张距离矩图描述周围环境，逐帧用射线更新。它与屏幕空间 GI 的根本区别是：**结果存在 probe 里，不依赖当前相机可见性**。

标准每帧流程：

```
1. Trace       每 probe 发 N 条球面 Fibonacci 方向光线（每帧随机旋转）
2. Irradiance  8×8 tile（6×6 内容 + 1px border），cosine 加权卷积
3. Distance    16×16 tile（14×14 + border），存 (μ, E[d²]) 两阶矩
4. Relocate    把卡进几何的 probe 推出来
5. Classify    禁用空区域 probe
6. Border Fix  八面体接缝处复制 border texel，保证双线性连续
```

着色时取像素周围 8 个 probe，做三线性插值 × 法线朝向权重 × **Chebyshev 可见性测试**（用深度矩算遮挡），得到间接漫反射。

关键性质：

- 成本 ∝ **probe 数 × 每 probe 射线数**，与屏幕像素数无关；探针数固定后，分辨率/相机移动不增加成本。
- 输出天然低通：probe 间距（默认 2–3 m）决定空间频率上限；细节需要屏幕空间 AO/SSGI 补充。
- 不需要逐像素降噪器：hysteresis + 三线性插值 + Chebyshev 已经是多帧/空间积分；残余噪声靠 hysteresis 和 probe variability 处理。

---

## 2. NVIDIA 官方栈的真实分工

### 2.1 RTXGI 1.x / 2.x

| 版本 | 内容 | 说明 |
|---|---|---|
| RTXGI 1.x（DDGI） | probe 网格、八面体辐照度 + 距离矩、hysteresis、relocation、classification、probe variability | 最后版本约 1.3.7（2023-05）；参考仓库对应的就是这条线 |
| RTXGI 2.x | **NRC + SHaRC** 世界空间辐射缓存，不再提供 DDGI | 2.x README 明确把老仓库标为 “older DDGI version”；不存在 “DDGI 2.0” |

DDGI 1.x 的运行参数（官方默认）：probe 间距 2–3 m，每 probe 256 rays（前 32 条固定射线用于 relocation/classification），irradiance 8×8（6×6 内容），distance 16×16（14×14 内容），hysteresis 0.97；probe 内存约 3.3 KB/probe（含 ray data）。官方 2019 论文用 32×8×32 ≈ 8.2k probes × 64 rays 在 2080 Ti 上约 7 ms，**所有 probe 每帧更新**。

多跳：probe 射线命中点着色时，直接光之后再加一次 **上一帧 DDGI 辐照度采样**（recursive irradiance）。一帧一跳，时序累积收敛到“无限跳”，代价是延迟。

### 2.2 RTXDI / ReSTIR

RTXDI 是 ReSTIR 采样库，解决的是“百万级动态光源的直接光采样太贵”：

- 每像素/每 tile 一个 reservoir：`{sample, w_sum, M, W}`，`W = w_sum/(M·p̂(y))`。
- 初始候选（RTXDI 会把灯光预采样成 light sample pool）→ 时序复用（motion vector + 深度/法线拒绝 + pairwise MIS）→ 空间复用（K 邻居 + MIS）→ visibility reuse（有偏，但省 shadow ray）。
- ReSTIR GI（2021）把 reservoir 换成“次级顶点 + 出射辐射”，用 shift mapping 复用路径；GRIS（2022）给出带 Jacobian 的广义 RIS，才有 ReSTIR PT；2024 的 MCMC mutation 解决 reservoir 相关性；2026 的 ReSTIR PT Enhanced 合并 DI+GI reservoir。
- **“sample pool”不是算法名**：论文里指送入加权 reservoir 的候选集合；实现里也常指 light-tile 预采样池/上一帧 reservoir 缓冲。
- ReSTIR 不替代降噪：它把 1–2 spp 的方差压到可接受，出货管线仍要 NRD/神经降噪。

### 2.3 降噪：SVGF / RELAX / ReBLUR

- **SVGF**（2017）：时序重投影 + 亮度一阶/二阶矩估计方差 + 5 次 5×5 à-trous（深度/法线/亮度 edge-stopping）+ firefly clamp。
- **NRD RELAX**：à-trous，SVGF 血统，专为 RTXDI 信号设计，漫反射/高光分开。
- **NRD ReBLUR**：recurrent blur，**不是 SVGF**；没有独立时序累积 pass，历史在 blur 内稳定；官方约比 SVGF 快 50%、比 RELAX 快 15%。
- **NRD SIGMA**：逐光的 shadow-only 降噪。
- **结论**：RTXGI/RTXDI SDK 不含 SVGF；DDGI 本身不需要降噪器。

### 2.4 出货管线

常见顺序：**RTXDI DI → ReSTIR GI/PT → NRD（diffuse/specular 分开，shadow 可加 SIGMA）→ TAA/DLSS RR**。

| 作品 | 复用 | 降噪 | 降噪对象 |
|---|---|---|---|
| Portal RTX | RTXDI（DI/GI） | NRD → DLSS 3.5 RR | 路径追踪辐射，漫反射/高光分开 |
| Cyberpunk 2077 RT Overdrive | RTXDI + ReSTIR GI + radiance cache | NRD → RR | 完整路径追踪合成 |
| Alan Wake 2 | 未公开确认 ReSTIR | NRD indirect + RR | 漫反射/高光间接，再合成 |
| Half-Life 2 RTX | RTXDI + NRD | DLSS 4 RR + NRC | 合成 + 缓存间接 |
| RTX Remix | RTXDI | NRD + RR | 合成 |

---

## 3. Lumen：表面缓存 + 辐射缓存

Lumen 不是探针 DDGI，也不是 ReSTIR。它的核心是 **surface cache + world radiance cache + screen probe final gather**。

### 3.1 Surface Cache

- 离线为 mesh 生成 **mesh cards**（默认每 mesh 12 张），运行时把 albedo/normal/emissive/depth 渲进 atlas。
- 每帧只重捕获一部分 texel（capture atlas 默认是 SC atlas 面积的 1/64），光照也按 `UpdateFactor` 增量更新。
- 它是“命中点材质/辐射”的来源：SDF 只给距离/命中位置，材质必须回 surface cache 取。

### 3.2 追踪

- 每根射线先做 **screen trace**（HZB 深度追踪，直接采样 Scene Depth/Color）。
- 软件路径：Global Distance Field / Detail SDF；命中后查 surface cache。
- 硬件路径：DXR/Vulkan BVH；Ray Lighting Mode 选 *Surface Cache*（默认、便宜）或 *Hit Lighting*（在命中点实算材质和直接光）。
- Far Field 扩展到约 10 km。

### 3.3 World Radiance Cache

- 4 层 clipmap，每 probe 存 32×32 八面体辐射图 + hit distance/depth/normal。
- 按预算刷新（每帧 N probes × 16–32 rays）；未刷新的 probe 是旧光照，快速移动时会出现 pops。
- 采样 8 个邻居 probe，带球面视差重投影和可见性权重。

### 3.4 Final Gather 与降噪

- 屏幕探针（16–32 px 间隔），每探针 8×8 方向，每根射线按 **screen → SDF/BVH → radiance cache → sky** 回退。
- 双边上采样到像素；Lumen 自己的时序重投影（velocity + 深度/法线 + history clamp）+ 探针空间滤波；最后交给 TSR。

### 3.5 多跳与延迟

- 每帧 final gather 只算约 **一跳**漫反射。
- 多跳靠 **surface cache radiosity 反馈**（每帧约 1/64 缓存被重新点亮）+ radiance cache 跨帧累积。
- 官方文档的 “infinite bounces” 是这种累积；全局光照变化传播要数秒，快速移动会看到 surface cache/radiance cache 的滞后。

### 3.6 UE 5.5–5.7（2025–2026）

- **MegaLights**（5.5 实验 → 5.7 Beta）：随机直接光，tile 级可见灯光列表 + 随机双线性取样 + 时序降噪；Epic 在 SIGGRAPH 2025 明确说 ReSTIR 不适合这个问题。**它只管直接光，GI 仍是 Lumen**。
- **Lumen 里仍然没有 ReSTIR/reservoir**（5.7 的 `StochasticInterpolation` 只是邻居探针随机复用）。
- HWRT 到 5.6 已能追平软件追踪的帧预算；主机上出现 60 Hz 的 Lumen 作品。
- 移动端 Lumen 是**高端 Android 实验特性**（Vulkan SM5），iOS 不支持。

---

## 4. HDRP：APV + SSGI/RTGI（没有 DDGI）

### 4.1 证据

对 HDRP 14.0.12 和 core 包全量搜索 `DDGI`、`irradiance atlas`、`depth atlas`、`raytraced probes`，没有实现命中。与 probe 相关的 `.compute` 只有编辑器烘焙用的 `ProbeVolumeCellDilation.compute` 和运行时 `ProbeVolumeBlendStates.compute`（仅 lerp 两套烘焙 SH）。

### 4.2 APV 是“高质量但静态”的 probe SH

- Brick = 3×3×3 cell ⇒ 4×4×4 = 64 probes。
- 每 probe 存 **SH L0/L1/L2**，不是方向图；7 张 Texture3D 池 + 2 张索引 buffer。
- 格式：L0/L1x `RGBA16F`，L1/L2 `RGBA8`/`BC7`，validity `R8`。
- 存储是烘焙产物（`SetAdditionalBakedProbes`），运行时没有重追；动态部分只有 cell streaming、scenario SH 混合、probe dilation（烘焙期）。

### 4.3 采样与动态 GI

- 逐像素采样在 `ProbeVolume.hlsl`：world → cell → chunk → brick，硬件三线性，validity mask + leak reduction。
- HDRP 的动态 GI 是另外两条路径：
  - **SSGI**：屏幕空间 Hi-Z + 上一帧 color pyramid + 时域重投影，miss 用 APV 兜底。
  - **RTGI**：逐像素 DXR，miss 用 APV 兜底。
- **关键判断**：APV 静态、SSGI/RTGI 逐像素且离开屏幕失效；DDGI 正好卡在中间（probe 级缓存 + 每帧更新）。HDRP 没有这条中间路线。

### 4.4 对本项目的启示

我们不接 HDRP，但 HDRP 的接入点证明了一件事：**间接漫反射必须在材质/GBuffer 写入前就可用**。URP 的等价接入点是 `GlobalIllumination.hlsl` / `Lighting.hlsl`，DDGI 采样应放在那里，而不是后处理全屏合成。

---

## 5. RTXGI-DDGI-URP：完整可读的参考实现

仓库：Unity **2022.3.62f1**（README 写的 17f1c1 已过时）+ URP 14.0.9，**依赖 DXR/DX12**。它没有改 URP 包，全部逻辑在 `Assets/Features`，通过 `ScriptableRendererFeature` + 全局纹理 + 自定义 Lit shader 接入。

### 5.1 帧序：一个 pass，每相机每帧全量跑

`DDGIFeature.cs:292` 把 pass 放在 `BeforeRenderingOpaques`，`AddRenderPasses` 无条件入队（`:1238`）。`Execute`（`:451`）顺序：

| 顺序 | 内容 | 代码 |
|---|---|---|
| 1 | 清历史（首帧/重置） | `:459` |
| 2 | CPU 收集场景灯光 → 上传 StructuredBuffer | `:462` |
| 3 | 推送 CBUFFER（每帧新随机轴/角） | `:466` |
| 4 | `BuildRayTracingAccelerationStructure` + `DispatchRays` | `:474-486` |
| 5 | Irradiance compute | `:490-502` |
| 6 | Distance compute | `:504-515` |
| 7 | Relocation | `:517-545` |
| 8 | Classification | `:547-575` |
| 9 | Variability 归约 + `AsyncGPUReadback` | `:577-630` |
| 10 | `CopyTexture` irradiance/distance → history | `:639-640` |

**没有 round-robin 预算。** Sponza 资产实际是 **22×22×23 = 10,648 probes，256 rays/probe**，全量派发约 **273 万条主光线/帧**；ray buffer 按 `probes × 512 × 16 B` 分配，约 **87.2 MB**。这是它最大的性能特征。

### 5.2 资源布局

尺寸约定：**Y 轴朝上 ⇒ arraySize = probeCount.y**，宽/高分别是 x/z（`DDGIFeature.cs:1022-1076`）。默认 22³；Sponza 资产 z=23。

| 资源 | 格式 | 默认 22³ | Sponza 22×22×23 |
|---|---|---|---|
| `RayBuffer` | `StructuredBuffer<float4>` | probes × maxRays（实现按 512 分配），xyz=radiance，w=distance | 10,648 × 512 × 16 B ≈ 87.2 MB |
| `_ProbeIrradiance` | `RGBA16F` Tex2DArray | 176×176×22 | 176×184×22 |
| `_ProbeDistance` | `RG16F` Tex2DArray | 352×352×22 | 352×368×22 |
| `_ProbeData` | `RGBA16F` Tex2DArray | 22×22×22（1 texel/probe） | 22×22×23 |
| `_ProbeIrradianceHistory` / `_ProbeDistanceHistory` | 同上 | 双缓冲 | 双缓冲 |

八面体编码与 texel 定位在 `DDGIProbeIndexing.hlsl:107-168`：probe base texel = `coords * (interior+2)`，内容从 `+1,+1` 开始；采样 UV 由 `EncodeNormalOctahedron(direction)` 映射到 tile 中心。

### 5.3 更新 kernel

**Irradiance**（`DDGIUpdateIrradiance.compute`）：

- 一个 thread group = 一个 probe，6×6 线程 = 6×6 texel（`:32`）。
- 每个 texel 对应方向 `DecodeNormalOctahedron(...)`（`:57`）。
- 对每条 ray 权重 `saturate(dot(probeDir, rayDir))`，累加 `(weight*radiance, weight)`（`:86-93`）。
- 归一化 `rgb /= max(2*Σw, eps)`（`:108`）。
- 时域混合 hysteresis 0.98；历史为黑时置 0；亮度突变时降 hysteresis、clamp delta（`:110-139`）。
- 可选 variability：逐 texel `σ/μ`，供归约判断收敛（`:143-157`）。
- Border 复制：预烘焙 `BORDER_OFFSETS` 表（`:166-171`）。

**Distance**（`DDGIUpdateDistance.compute`）：

- 14×14 线程（`:33`）。
- 深度先 `min(abs(d), 1.5*|probeSize|)` 截断（`:87`）。
- 权重 `pow(saturate(dot), 64)`（`:92`）。
- 累加 `(d, d²) * w`，归一化后存两阶矩（`:95`、`:111`）。
- 同样的时域混合 + border（`:113-130`）。

**Relocation / Classification**：每 32 probes 一组，直接读写 `_ProbeData`。

- Relocation：固定 32 条 ray 统计背面/正面命中，把 probe 沿最近背面方向推出，或朝最远正面方向拉出；offset `|offset/probeSize|² < 0.45²` 截断。
- Classification：背面比例超阈值或 cell 内无几何命中 → `INACTIVE`；非活跃 probe 每帧只追 32 条固定 ray。
- 固定 32 条 ray 不参与随机旋转，也不参与 irradiance/distance 卷积，保证时域稳定。

### 5.4 光追

硬件 DXR，不是 cubemap 光栅化：

- TLAS 自动管理所有 Renderer（`:309-311`），每帧重建（收敛后跳过）。
- Raygen：`DDGIRayTracing.raytrace:13`，`DispatchRaysIndex().x = rayIdx`，`.y = probeIdx`。
- 命中材质信息来自材质 shader 的 `DDGIRayTracing` pass（`LitRayTracing.shader`），所以场景所有材质必须换成这个 shader。
- miss → 天空 cubemap / 渐变 / 纯色，距离写 `1e27`。
- 背面 → 距离写负值 `-0.2*d`，供 relocation/classification 判断。
- 非固定 ray 命中后：对每盏方向光/点光各追一条 shadow ray，加自发光，再加一次上一帧 DDGI 反馈：`albedo*Lambert()*SampleDDGIIrradiance(...)`。

最后一条就是多跳来源：**跨帧能量反馈**，不是同帧迭代。

### 5.5 最终着色

没有全屏 pass。它把 URP Lit 的 `bakedGI` 置 0，然后：

```hlsl
float3 indirectRadiance = SampleDDGIIrradiance(positionWS, normalWS, -viewDir);
float3 indirectLighting = albedo * Lambert() * indirectRadiance * ao.indirectAmbientOcclusion;
color.rgb += indirectLighting;
```

采样函数在 `DDGIFuncs.hlsl:327-433`：

1. 位置加法线/视线 bias。
2. `DDGIGetVolumeBlendWeight` 做体积边缘淡出。
3. 取 base probe，遍历 8 个相邻 probe，跳过 INACTIVE。
4. 三线性权重 × 背面权重 `wrapShading²+0.2`。
5. Chebyshev：`(μ, E[d²])` → `variance = |μ²-E[d²]|`，`cheb = var/(var+(μ-d)²)`，三次方后 clamp 到 0.05。
6. 低于 0.2 的权重 crush，再乘三线性权重。
7. 采样 `_ProbeIrradianceHistory`，`pow(x, 2.5)` 解码（抵消写入端 `pow(x,1/5)`）。
8. 归一化后 **再平方一次**、乘 `2π`、乘 `_IndirectIntensity`。

第 8 步的额外平方**不是 RTXGI 原版操作**，属于这个仓库自己的亮度处理，也让能量不守恒；移植时必须删掉或改成明确的 exposure 参数。

### 5.6 与生产实现的差距

1. **没有无限滚动**：`DDGIGetScrollingProbeIndex` 是 TODO，只支持单个静态 volume。
2. **全量更新，无预算调度**：不能按帧分摊；probe 数/光线数一调，显存和耗时线性涨。
3. **收敛后完全停更**：`mIsConverged` 时连 TLAS 都不重建；动态物体/自发光变化会冻结（README 明确列了这个 bug）。
4. **Relocation 在 irradiance/distance 更新之后执行**，比原版晚一帧生效。
5. **`FindObjectsOfType<Light>()` 每帧 CPU 收集灯光**，生产环境应换成 URP 可见光列表。
6. **只有用 `Custom/LitRayTracing` 的材质有 GI**，URP Lit、地形、透明物都不行；APV/lightmap 被强制关掉。
7. 无空间降噪，只有 hysteresis；能量守恒参数 `_EnergyPreservation` / `_NormalBias` 声明了但没被任何 shader 读取。

### 5.7 对照：本仓库 SSGI/SSR 与 UnitySSGIURP（上文结论固化）

本节把此前对话中关于**本仓库屏幕空间路线**的结论固化，作为 DDGI 的对照基线，避免后续只记录 DDGI 而丢失“为什么不直接用现成 SSGI”的依据。

#### 5.7.1 本仓库 SSGI/SSR 的输入与接入点

现有 `ScreenSpaceLightingRendererFeature` 的 SSGI/SSR 是 HDRP 14 的移植，**纯屏幕空间 + 时序**，不维护世界空间缓存：

| 项 | 事实 | 证据 |
|---|---|---|
| SSGI 事件 | `AfterRenderingPrePasses + 5`（205），早于 GBuffer 210 / Forward opaque 250 | `SsgiPass.cs:9`、`:16` |
| SSR 事件 | `AfterRenderingPrePasses + 6`（206） | `SsrPass.cs:17-21` |
| SSGI 输入 | 相机深度、法线（depth-normals prepass，alpha 带 smoothness）、motion vectors、上一帧颜色金字塔、上一帧深度/法线、蓝噪声/探针 SH | `ScreenSpaceGlobalIllumination.compute:27-38` |
| SSR 输入 | 深度、法线 + smoothness、motion、上一帧颜色金字塔 | `ScreenSpaceReflections.compute:46-59` |
| 结果消费 | 材质侧 `lerp(bakedGI, tracedRadiance, weight)`；不写全屏合成 pass | `ScreenSpaceLightingBlend.hlsl:16-59` |
| Deferred 接入 | `LitGBufferPass` 在 GBuffer 写入前调用 `GlobalIllumination`，GI 进 GBuffer3 | `LitGBufferPass.hlsl:211-223`、`GlobalIllumination.hlsl:354-384` |

**“为什么不依赖 G-buffer”的准确表述**：它不依赖 deferred G-buffer 的材质数据（albedo/metallic/emission/lighting），只依赖 depth-normals prepass 产出的法线（+alpha smoothness）。在 Deferred 下 `cameraNormalsTextureHandle` 会直接返回 **GBuffer2 这个 allocation**（`UniversalRenderer.cs:145-159`），而 depth-normals prepass 会把法线 prime 进去（`UniversalRenderer.cs:1016-1020`、`:1054-1072`）；因此 RenderDoc 里看到 `_SsgNormalTexture → _GBuffer2` 并不表示它读了材质 G-buffer。

原因是硬性的：

1. SSGI/SSR 在 GBuffer（210）/ opaque（250）**之前**运行，结果要作为 opaque 着色的输入；如果读 albedo 等 G-buffer，就会形成循环依赖，只能退化为后处理叠加。
2. SSGI 算的是**入射辐照度**，与接收面材质无关；albedo 由材质乘一次。SSR 输出 un-premultiplied radiance + weight，BRDF/FGD 也由材质应用一次。
3. Forward / Forward+ / Deferred 共用同一实现；Forward 没有 G-buffer，只有 `_CameraNormalsTexture`。
4. Deferred 下为了在 GBuffer 前拿到完整法线，depth-normals prepass 不能只画 forward-only 物体（`UniversalRenderer.cs:1068-1071`），几何提交会多一遍；这是该路线的已知代价，与 GTAO 共享这份法线。

#### 5.7.2 降噪对照

| 环节 | 本仓库 SSGI | UnitySSGIURP |
|---|---|---|
| 时序验证 | 独立 `ValidateHistory`：位置半径、法线阈值、重投影边界、上一帧深度/法线 | trace 内用上一帧深度 + 最大半径算 `canBeReprojected`，把结果编码进 alpha 符号；不验证法线 |
| 历史失效 | 采样数清零、α=0 | `RAY_COUNT=max(4,..)` + 降低步进，用更多射线补 |
| 邻域保护 | **没有 color clamp**，只靠 validation | Aggressive 下 3×3 AABB clamp |
| 时序惯性 | 采样数上限 8，α=0.93；记忆约 14 帧 | 8 帧后 α≈0.94，且 Stabilization 再混一次，等效约 0.996 |
| 空间滤波 | 16-tap 蓝噪声盘分布（64 点、每 4 帧旋转），投影 + 深度容差，depth/normal/plane 权重；HF + LF 两级 | 8-tap A-Trous（随机 2–5 texel 膨胀）+ 8-tap ReBLUR-style Poisson ×2；0.5 分辨率 |
| 上采样 | 2×2 双边（深度权重 + 空间高斯） | 4 邻域“最近深度”选择，不做滤波 |
| 证据 | `TemporalFilter.compute:70-203`、`DiffuseDenoiser.compute:82-127`、`BilateralFilter.hlsl:52-69`、`BilateralUpsample.compute:31-71`、`SsgiPass.cs:604-657` | `ScreenSpaceGlobalIllumination.shader:195-355`、`:497-575`、`:900-1050`、`ScreenSpaceGlobalIlluminationURP.cs:699-777` |

结论：**质量上本仓库更好**（遮挡恢复、命中面运动、细节保留、半分辨率上采样），**性价比上 UnitySSGIURP 更好**（0.5 分辨率、8-tap、简单时序）。本仓库的主要缺陷是没有邻域 clamp（光照突变会残留约 10 多帧），且默认全分辨率 9 个 dispatch、32 个空间 tap/像素。

#### 5.7.3 移动端结论

| 维度 | 本仓库 SSGI/SSR | UnitySSGIURP |
|---|---|---|
| API 门槛 | compute + R32F/RGBA16F/RG16F/R16F UAV（`ScreenSpaceCapability.cs:84-107`） | SM3.5 / GLES3.0 fragment（`#pragma target 3.5`） |
| 默认 trace | **全分辨率**、1 ray/pixel、≤32 步（`ScreenSpaceGlobalIllumination.cs:59`、`:65`） | **0.5 缩放**、Low=1 ray/pixel、24 步（`ScreenSpaceGlobalIlluminationVolume.cs:81`、`:87`、`:94`、`:100`、`:106`） |
| 默认降噪 | temporal validate/accumulate ×2 + 16 tap ×2，全分辨率（`ScreenSpaceGlobalIllumination.cs:83`、`:89`、`:101`） | 8-tap A-Trous + 2× Poisson + stabilization，0.5 分辨率 |
| 每帧额外全屏 pass | 无（材质内 blend） | intermediate copy + combine + history color copy（3 个） |
| 固定开销 | Depth Pyramid 12 dispatch、GTAO 4、两张 Gaussian 颜色金字塔 25+27 事件（RenderDoc，1366×768 Deferred，2026-11-19） | 无 depth pyramid；但有 combine/copy |
| 历史格式 | 全 RGBA16F；color pyramid 带 mip；prev normal RGBA16F；prev depth R32F | 中间色 B10G11R11；GI 0.5 分辨率 16F；history depth R32F |
| 结论 | **默认配置不适合移动端**；compute 门槛 + 全分辨率 + 高带宽 | 低端/GLES3.0 更合适；中端默认更便宜 |

补充事实：

- 本仓库的 per-camera history 上限是 8（`ScreenSpaceLightingSettings.cs:12`），多相机移动端内存压力大。
- UnitySSGIURP 的 `_GBuffer2` 强依赖（`SSGIInput.hlsl:24`）：Deferred 下直接读；Forward 下要额外画一遍 3-MRT Forward GBuffer（`ScreenSpaceGlobalIlluminationURP.cs:500`、`:1722+`），所以两条路线在不同 renderer 上各自多付一遍几何。
- 本仓库可以在移动/中端上通过 `fullResolution=false`、`halfResolutionDenoiser=true`、`secondDenoiserPass=false` 把成本压下来，但仍有 compute 门槛和颜色金字塔的固定开销。

#### 5.7.4 对 DDGI 决策的含义

1. 现有 SSGI/SSR 是**屏幕空间、1 跳、依赖相机可见性**；DDGI 是**世界空间、屏幕外可用、跨帧多跳**。两者不是替代关系。
2. v1 选 **DDGI 与 SSGI 互斥**：两者都输出间接漫反射，同时开会双重计能量；先保证正确性，再考虑未来的“DDGI 底光 + SSGI 屏幕空间修正”权重契约。
3. DDGI 的体积外/未覆盖路径回退到 baked GI / APV，正好复用现有 `bakedGI` 管线；不需要改 SSGI 的算法。
4. DDGI 是 **desktop-only**（DXR），与上文移动端结论一致：移动端继续走现有 SSL/APV 或 UnitySSGIURP 风格的低成本方案。

---

## 6. 方案对比

| | DDGI（RTXGI 1.x / 参考仓库） | ReSTIR + NRD（RTXDI 出货栈） | Lumen | HDRP APV + SSGI/RTGI | 本仓库 Screen Space Lighting（§5.7） |
|---|---|---|---|---|---|
| 世界空间存储 | probe 网格：八面体 irradiance + 距离矩 | reservoir/路径样本，无辐射缓存（Cyberpunk 另加 radiance cache） | surface cache + 4 层 radiance cache probes | 烘焙 probe SH（APV） | 无 |
| 每帧追踪 | 每 probe 固定 rays（与像素无关） | 每像素/每 tile 1–2 个候选 + 复用 | 屏幕探针 + 反射射线 + 缓存刷新预算 | 逐像素屏幕/RT | 每像素 1 ray（可半分辨率） |
| 直接光 | probe 命中点逐灯 shadow ray | RTXDI ReSTIR DI | 普通 deferred/forward；MegaLights 可选 | 光栅化 | 光栅化 |
| 间接光 | probe 辐照度（低通） | ReSTIR GI/PT 路径 | final gather + radiance cache | SSGI/RTGI（动态）+ APV（静态） | SSGI，1 跳 + 时序 |
| 多跳 | 上一帧 probe 反馈，一帧一跳 | 路径复用（PT 可多跳） | surface cache radiosity + cache 反馈，一帧一跳 | 无（APV 烘焙/SSGI 单跳） | 上一帧颜色金字塔含上帧 GI，弱时序多跳 |
| 降噪 | 不需要（hysteresis + 插值） | NRD RELAX/ReBLUR/SIGMA | 内部时序 + 空间 + TSR | HDRP 自带时域 | HDRP 时序验证 + 16-tap 双边 ×2 |
| 延迟 | 中（hysteresis） | 低（1–2 spp） | 高（缓存 amortize，数秒传播） | 低 | 中 |
| 硬件门槛 | DXR/Vulkan RT | DXR/Vulkan RT | DXR 或 SDF；移动仅高端 Android 实验 | DXR（RTGI） | compute（GLES3.1+/Vulkan/Metal） |
| 工程复杂度 | 低–中 | 高 | 极高 | 高（官方管线内） | 中 |

**结论**：四种方案解决的是不同层级的问题。DDGI 是“便宜的、屏幕外的、多跳的间接光底噪”，ReSTIR 是“海量动态光源的直接光采样”，Lumen 是“用缓存把路径追踪 amortize 到可实时”，本仓库 SSL 是“屏幕内的高质量补充”。它们可以组合，但不应互相替代。

---

## 7. 对本项目的落地建议（供 HANDOFF 冻结）

1. **后端**：桌面 **D3D12 + DXR**（`SystemInfo.graphicsDeviceType == Direct3D12` 且 `SystemInfo.supportsRayTracing`）。D3D11/Vulkan 不提供软件 DDGI 回退：feature 自动禁用并保留 baked GI/APV，只打一次 warning。这与参考仓库的 DXR 假设一致，也避免在 D3D11 上维护第二条追踪后端。
2. **范围**：**单一静态 volume**；不做多 volume、不做无限滚动、不做透明物贡献。第一版只覆盖**内置不透明材质**（Lit / ComplexLit / SimpleLit / Terrain / SpeedTree / Particles Lit）；ShaderGraph 明确不在 v1，后续单独评估。
3. **调度**：必须改成**分帧预算**。默认 22³ probes、256 rays、每帧更新 1/8 probe（warmup 8 帧全量）；固定 32 rays 保留给 relocation/classification；variability 只降低更新率，**永不彻底停更**。默认值在 T0 Contract 冻结，允许在 T0 按目标 GPU 下调（如 16³/128）后再冻结。
4. **接入**：DDGI 采样放在 `GlobalIllumination.hlsl` / `Lighting.hlsl`，与现有 SSGI 同一位置；这样 forward / forward+ / deferred（GBuffer 写入）都覆盖，不需要全屏合成 pass。DDGI 与 SSGI **互斥**（DDGI 优先，SSGI 跳过），避免双重计能量。
5. **多跳与能量**：保留“命中点采样上一帧 probe”的一帧一跳反馈；删除参考实现采样末尾的额外平方；用 `2π`、Lambert、albedo 做明确的能量归一化，并写 A/B 证据。
6. **灯光**：用 URP `renderingData.lightData.visibleLights`，不要 `FindObjectsOfType`；第一版支持方向光 + 点光/聚光，shadow 用 RT shadow ray；天空支持 cubemap / 渐变 / 纯色。
7. **不做 ReSTIR / SVGF / NRD / Lumen 式 surface cache**：它们解决的是本任务之外的问题；引入会显著扩大范围和风险。

---

## 8. 风险与未决问题

| 风险 | 说明 | 处理 |
|---|---|---|
| DXR 可用性 | 只有 D3D12 + RT 硬件能跑；D3D11/Vulkan 无回退 | 能力门控 + 明确 warning + baked GI/APV 保留 |
| TLAS 每帧重建 | 大场景 CPU/GPU 成本高 | 只在场景/实例变化（scene-change token）时重建；probe 调度不触发重建；记录耗时 |
| 内置材质 pass 覆盖 | 每个内置 shader 都要加 `DDGIRayTracing` pass；D3D11 编译不得受影响 | 共享 include + 契约测试 + D3D11 构建门禁 |
| ShaderGraph 不覆盖 | 使用 ShaderGraph 的物体不贡献 GI | v1 明示非目标；debug 视图标记未覆盖材质；后续任务 |
| probe 漏光/穿墙 | 探针间距、Chebyshev、relocation 都有边界情况 | 参考实现参数 + 测试场景边界用例；记录 KnownDifferences |
| 多相机/编辑器 | DDGI 是世界空间数据，不应按相机复制 | 单份 history；只允许 base/Game 相机更新；Preview/Reflection 跳过 |
| 收敛停更 | 参考实现冻结 GI | 改为降频，不停更；动态光/自发光恢复测试 |
| 证据预算 | 仓库 `.rdc` 已用 1000.1 MiB / 1024 MiB | 默认不新增 `.rdc`；必要时 ≤20 MiB 并需所有者批准 |
| 变体剥离 | player 构建可能剥离 DDGI shader/pass/keyword | T11/T12 显式构建检查（参考 06 F-12 教训） |
| 性能 | 全量 22³×256 = 273 万 rays 不现实 | 分帧预算 + 默认 1/8；性能门禁见 HANDOFF AC-11 |

---

## 9. 勘误与文档一致性

本次更新修正/澄清以下内容（与旧版报告或参考仓库 README 的差异）：

| 项 | 旧说法 | 实际 |
|---|---|---|
| 参考仓库 Unity 版本 | 2022.3.17f1c1（README） | **2022.3.62f1**（`ProjectSettings/ProjectVersion.txt`） |
| Sponza 射线数 | 144 rays/probe，约 153 万 rays/frame | **256 rays/probe，约 273 万 rays/frame**（`Sponza-PPV.asset`） |
| Sponza probe 数 | 22³ | **22×22×23**（z=23） |
| “RTXGI 2.0 = DDGI 2.0” | — | RTXGI 2.x 是 NRC + SHaRC，**不是 DDGI**；DDGI 1.x 最后约 1.3.7 |
| “RTXGI 用 SVGF” | — | RTXGI/RTXDI 不含 SVGF；生产降噪是 NRD（RELAX/ReBLUR/SIGMA），RELAX 是 SVGF 演进 |
| “DDGI 给 RTXDI GI 提供 multi-bounce” | — | 仅旧 RTXDI 1.2 集成示例；现代 RTXDI GI/PT **不消费 DDGI** |
| “ReSTIR 样本池 + SVGF 让游戏实时跑 PT” | — | 是 ReSTIR（样本复用）+ NRD/RR（降噪）的组合；ReSTIR 偏置、仍需降噪；DDGI 是另一条 GI 路线 |
| “Lumen 用 ReSTIR” | — | 截至 UE 5.7，Lumen GI **没有 ReSTIR**；MegaLights 是随机直接光，不是 GI ReSTIR |
| “RTGI” | 像是一个模块 | 不是 NVIDIA 模块名；间接光可能是 ReSTIR GI/PT、DDGI 探针或 RTXGI 2.x 缓存 |

---

## 10. 参考索引

### 外部资料

- DDGI 论文（JCGT 2019）：https://jcgt.org/published/0008/02/01/
- DDGI 扩展（JCGT 2021）：https://jcgt.org/published/0010/02/01/
- NVIDIA RTXGI-DDGI（1.x）：https://github.com/NVIDIAGameWorks/RTXGI-DDGI
- NVIDIA RTXGI（2.x）：https://github.com/NVIDIA-RTX/RTXGI
- NVIDIA RTXDI：https://github.com/NVIDIA-RTX/RTXDI
- NVIDIA NRD：https://github.com/NVIDIA-RTX/NRD
- SVGF（HPG 2017）：https://research.nvidia.com/publication/2017-07_Spatiotemporal-Variance-Guided-Filtering
- Lumen Technical Details：https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-technical-details-in-unreal-engine
- Lumen Performance Guide：https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-performance-guide-for-unreal-engine
- UE 5.5 / 5.7 Release Notes：https://dev.epicgames.com/documentation/en-us/unreal-engine/unreal-engine-5-5-release-notes ，https://dev.epicgames.com/documentation/en-us/unreal-engine/unreal-engine-5-7-release-notes
- MegaLights：https://dev.epicgames.com/documentation/en-us/unreal-engine/megalights-in-unreal-engine
- Lumen on Mobile：https://dev.epicgames.com/documentation/en-us/unreal-engine/using-lumen-global-illumination-on-mobile-in-unreal-engine

### 参考仓库

- 宿主：[DDGIFeature.cs](file:///E:/workspace/GitHub/RTXGI-DDGI-URP/Assets/Features/Runtime/DDGIFeature.cs)、参数 [Overrides/DDGI.cs](file:///E:/workspace/GitHub/RTXGI-DDGI-URP/Assets/Features/Runtime/Overrides/DDGI.cs)
- 更新：[DDGIUpdateIrradiance.compute](file:///E:/workspace/GitHub/RTXGI-DDGI-URP/Assets/Features/Resources/Shaders/DDGIUpdateIrradiance.compute)、[DDGIUpdateDistance.compute](file:///E:/workspace/GitHub/RTXGI-DDGI-URP/Assets/Features/Resources/Shaders/DDGIUpdateDistance.compute)
- 光追：[DDGIRayTracing.raytrace](file:///E:/workspace/GitHub/RTXGI-DDGI-URP/Assets/Features/Resources/Shaders/DDGIRayTracing.raytrace)、[LitRayTracing.shader](file:///E:/workspace/GitHub/RTXGI-DDGI-URP/Assets/Features/Resources/Shaders/LitRayTracing.shader)
- 采样/索引：[DDGIFuncs.hlsl](file:///E:/workspace/GitHub/RTXGI-DDGI-URP/Assets/Features/Resources/Shaders/Lib/DDGIFuncs.hlsl)、[DDGIProbeIndexing.hlsl](file:///E:/workspace/GitHub/RTXGI-DDGI-URP/Assets/Features/Resources/Shaders/Lib/DDGIProbeIndexing.hlsl)

### HDRP 对照

- APV：[ProbeVolume.hlsl](file:///E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.core@14.0.12/Runtime/Lighting/ProbeVolume/ProbeVolume.hlsl)、[ProbeBrickPool.cs](file:///E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.core@14.0.12/Runtime/Lighting/ProbeVolume/ProbeBrickPool.cs)
- 动态 GI：[ScreenSpaceGlobalIllumination.compute](file:///E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12/Runtime/Lighting/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute)、[RayTracingIndirectDiffuse.hlsl](file:///E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12/Runtime/RenderPipeline/Raytracing/Shaders/IndirectDiffuse/RayTracingIndirectDiffuse.hlsl)

一句话总结：**RTXGI/RTXDI 是“探针 GI + ReSTIR 直接光 + NRD 降噪”的组合，Lumen 是“表面缓存 + 辐射缓存 + final gather”，本仓库要做的 DDGI 属于第一条路线中最轻的一档。参考实现的数据面和采样可以直接借鉴，但调度、多跳、材质接入、能量和收敛停更必须重做。**