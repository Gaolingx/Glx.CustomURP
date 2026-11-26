# R2b 调研与实施计划：只对未解决射线跑 RTR（压缩队列 + TMin 续传 + 自适应 K）

**状态：调研完成，待 Phase 0 测量后决策**
**日期：2026-11-26**
**关联任务：** [20-Ray_tracing/todolist.md R2b](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/Done/20-Ray_tracing/todolist.md)、[SSR-DXR.md §2.2/§4/§6](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/Done/20-Ray_tracing/research/SSR-DXR.md)
**代码基线：** HEAD `0742b8c`（`feat(reflection): temporally reuse the hardware reflection fill`）
**范围：** 仅不透明 SSR 的 DXR 补追（opaque Approximation + 半分辨率），不涉及透明 SSR、折射、GI probe 更新和 Surface Cache。

---

## 0. 结论摘要（TL;DR）

1. **方向可行**：现有 `SS trace → R1 分类 → 紧凑队列 → DispatchRays → R2Temporal → R2Composite` 已经具备 Lumen 式“屏幕优先、世界空间补洞”的骨架，改造是演进，不是重写。
2. **“只对 raymiss 跑 RTR”应修正为“只对未解决样本跑 RTR”**：当前 R1 除了 raymiss（`reason = 4`），还会把边缘淡出到份额未满的屏幕命中（`reason = 6`，部分份额）和历史被拒的屏幕命中（`reason = 5`，SS 份额 0）送进 HW 队列；严格二值 miss-only 会留下错误屏幕命中，质量风险高。Lumen 实际也是 `miss + uncertain`。
3. **2022.3 无法精确启动 N 条线程**：本地 `UnityEngine.CoreModule.dll` 只暴露 `DispatchRays`，没有 `DispatchRaysIndirect`。D3D12 原生支持 `ExecuteIndirect + D3D12_INDIRECT_ARGUMENT_TYPE_DISPATCH_RAYS`，但 Unity C# API 未暴露。
   - 现实方案：**压缩队列 + 固定上限 K + GPU 早退**；或上一帧异步回读 N 做自适应 K。
   - 压缩能让活跃 warp 满载；但尾部仍会有少量空启动线程。
4. **TMin 续传是高风险项，不能直接把 Hi-Z 的 `t` 当世界安全距离**：现有 [SSR-DXR.md §2.3](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/Done/20-Ray_tracing/research/SSR-DXR.md) 已明确警告；R2b 的现有验收门槛也是“隐藏遮挡物不得漏遮挡”。只能对**可证明为空**的屏幕段续传，并加保守 pullback；否则会从隐藏遮挡物后方开始追，直接漏几何。
5. **当前 300→200 fps（3.33→5.0 ms，+1.67 ms）不是单一 DXR**：帧里同时有 SS trace、R1 分类、DXR、Temporal、重建/上采样、全分辨率 Composite，以及 25+27 个颜色金字塔/历史 dispatch。R2b 只能优化前半段，不能回收全部 1.67 ms。
6. **建议顺序：** Phase 0 先测量归因；Phase 1 做“三态 + 安全 TMin（仅 off-screen exit）”；Phase 2 做压缩 + 自适应 K；Phase 3 做三态合成与计数器闭环；Phase 4 再做天空/probe 策略。遮挡体后方的 full-miss 续传不在本轮 Phase 1–5 内（见 §6.2；若 §13 Q5 决策通过，再单独立项估时）。若 Phase 0 显示 DXR+R1 小于 1.67 ms 的 30%，优先优化固定全屏 pass。

**一句话：** 方案可行，但“完全不浪费”只能做到“被选中的 K 条射线内不浪费”；TMin 的收益取决于能否给出保守的世界安全距离，否则 NO-GO。

---

## 1. 问题定义

### 1.1 用户假设

> 只对 SS raymiss 的光线跑 RTR，并通过续传（TMin）和光线重排提高 warp 利用率，理论上可以完全不浪费 RT 算力，不会重复追踪。

需要回答三个问题：

1. 当前实现是否已经只对 miss 跑 RTR？
2. Lumen 式 compaction + TMin 能否在 Unity 2022.3 / URP 14 落地？
3. 落地后能回收多少 300→200 fps 的损失？

### 1.2 不在本报告范围

- 不改变 SS trace 的屏幕空间算法本身（Hi-Z/DDA）。
- 不引入 Surface Cache、mesh card、多跳、透明/ShaderGraph RT、XR RT。
- 不解决 TLAS 构建策略的全部问题，只记录其对 R2b 的影响。
- 不把 D3D12 原生 `ExecuteIndirect(DISPATCH_RAYS)` 通过 native plugin 接入列为首选；只作为长期可选。

---

## 2. 术语

| 术语 | 含义 |
|---|---|
| SS trace | 屏幕空间 Hi-Z 射线步进，输出命中 UV 或 miss |
| resolved | 屏幕命中被接受且份额已满（`reason = 2`），直接使用 SS 辐亮度 |
| uncertain | 屏幕命中被接受但边缘淡出使份额未满（`reason = 6`），需要 HW 补足剩余份额 |
| raymiss | 屏幕空间没有有效命中（`reason = 4`）；历史命中被拒（`reason = 5`）同样按 miss 处理 |
| unresolved | 进入 HW 队列的样本 = `raymiss + uncertain`（`reason = 4/5/6`） |
| compaction | 把散落在全屏的未解决样本紧凑写入一维队列，并统计 N |
| TMin 续传 | HW 射线从 SS 已验证为空的距离继续，而不是从 0 开始 |
| warp/wave | GPU 线程束（D3D 通常 32，AMD 64）；divergence 会掩掉不活跃 lane |
| K | 每帧固定 DXR 射线预算（当前 32768） |
| N | 本帧实际未解决样本数 |
| R1 | 分类、请求、队列构建阶段 |
| R2 | DXR 求交、时域合并、合成阶段 |
| TLAS | RayTracingAccelerationStructure，场景 BVH |

---

## 3. 当前实现（代码事实）

### 3.1 帧内顺序（以 1080p、Approximation、半分辨率 trace 为例）

```text
Camera base (D3D12, rayTracingReflections=1, SSR fullResolution=0)
│
├─ 203  Depth Pyramid ................................ 12 dispatches
├─ 204  Ground Truth Ambient Occlusion ............... 4
├─ 205  Screen Space Global Illumination ............. 9
├─ 206  Screen Space Reflections ..................... 11
│        ├─ [A] ScreenSpaceReflectionsTracing  960x540  (SS raymarch)
│        ├─ [B] R1Original                     960x540  (读 history，分类)
│        ├─ [C] R1Tiles / R1Offsets / R1Scatter 960x540 (压缩/配额)
│        ├─ [D] R2Clear + DispatchRays         K=32768 rays
│        ├─ [E] R2Temporal                     960x540  (HW 历史合并)
│        ├─ [F] Reprojection + BilateralUpsample       (重建/上采样)
│        └─ [G] R2Composite                    1920x1080 (全分辨率合成)
├─ 207  材质绑定（_ScreenSpaceSsrTexture / _ScreenSpaceSsrParams）
├─ 210  GBuffer（Clustered Deferred 路径）
├─ 250  Forward opaque（如果 renderer 回退）
├─ 400  SSL Current Color Pyramid .................... 25 dispatches
├─ 447  Transparent DepthNormals
├─ 448  Refraction
├─ 449  Transparent SSR
└─ 501  SSL Color History Capture .................... 27 dispatches
```

> 事件数来自 2026-11-26 的 Frame Debugger 截图；它是**事件数**，不是 GPU 时间。

### 3.2 关键分辨率与缓冲

- 屏幕 1920×1080；trace 960×540（半分辨率）。
- `_SsrTraceSize = (960, 540)`；`_SsrScreenSize = (1920, 1080)`。
- `R1Sample` 80 B；`R2Hit` 24 B；`originals` 半分辨率 80 B/px；`hits` 半分辨率 24 B/px。
- `R2Composite` 以 8×8 线程组覆盖 1920×1080（240×135 组）。
- `_SsrHwHistory` 960×540 RGBA16F；`_SsrOutput` 1920×1080 RGBA16F。

### 3.3 R1 分类与预算（核心事实）

R1 不是“只对 miss”：

- SS trace 先写 `_SsrHitPointTextureRW`：命中写 NDC，miss 写 `(99,0)`（[ScreenSpaceReflections.compute:409](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute#L409)、[:455](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute#L455)）。
- `R1Original` 读 hit point、上一帧 color pyramid、当前 P/N，计算 `ssShare` 与 `requestShare`（[SsrRequestQueue.hlsl:37-53](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/SsrRequestQueue.hlsl#L37)）：
  - `reason = 0`：背景/背面/无效深度法线或无效方向，保持默认值，不入队。
  - `reason = 1`：材质无效或粗糙度超出淡出范围（`fade <= 0`），不入队。
  - `reason = 2`：历史命中被接受且 `requestShare = fade * (1 - edge) == 0`，即可靠 SS，不入队。
  - `reason = 3`：仅冷启动 `R1Cold`（无 hit point 纹理）使用，表示缺历史，`requestShare = fade`。
  - `reason = 4`：生产路径（`R1Original`）的 raymiss（无有效 hit point），`requestShare = fade`。
  - `reason = 5`：历史命中被拒（越界/颜色无效），`requestShare` 保持 `fade`。
  - `reason = 6`：历史命中被接受但存在屏幕边缘淡出，`requestShare = fade * (1 - edge) > 0`。
  - 入队条件是 `requestShare > 0`（[SsrRequestQueue.hlsl:86](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/SsrRequestQueue.hlsl#L86)、[:119](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/SsrRequestQueue.hlsl#L119)），与 `reason` 编号无关。
  - `R1Cold`/`R1Original` 的选择在 [SsrPass.cs:448](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs#L448)：无 hit point 纹理时走冷启动。
- 因此队列 = `miss(reason 4) + 历史被拒(reason 5) + 边缘 uncertain(reason 6)`，不是二值 miss-only。这与 Lumen 的“未解决”语义一致，但比用户描述的“raymiss”更宽。

预算：

- 每 tile 是 8×8=64 像素；tile 数 `tiles = ceil(960/8) * ceil(540/8) = 120 * 68 = 8160`。
- 每 tile 配额 `quota = max(1, K / tiles)`；K=32768 → quota=4。
- 64 像素 / quota 4 = 16 帧覆盖一轮；60 fps 0.27 s，300 fps 53 ms。
- 该公式来自 `R1Offsets`（[SsrRequestQueue.hlsl:99-112](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/SsrRequestQueue.hlsl#L99)）和既有记录。

### 3.4 DXR 派发

- `DispatchRays(shader, "TraceSsrRequests", budget, 1, 1)`（[SsrRequestQueue.cs:60-78](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrRequestQueue.cs#L60)）。
- raygen 先做 `index >= min(_R1Counts[2], _R2K)` 早退（[RayTracingSsr.raytrace:12-30](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/RayTracingSsr.raytrace#L12)）。
- 每条射线从 `q.tMin` 到 `q.tMax`；当前 `tMin` 固定 0.002（[SsrRequestQueue.hlsl:26](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/SsrRequestQueue.hlsl#L26)）。
- 命中着色最多选 4 盏灯、最多 2 条阴影射线（[GlxHitLights.hlsl:112-128](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/RayTracing/GlxHitLights.hlsl#L112)）；场景灯表容量 64（[RayTracingSceneLights.cs:12](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/RayTracing/RayTracingSceneLights.cs#L12)）。
- RT 仅在 `Approximation + 半分辨率 + 非 XR + base game camera` 时启用（[SsrRayTracingCameras.cs:32-42](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/SsrRayTracingCameras.cs#L32)）。

### 3.5 时域与合成

- `R2Temporal` 把本帧 hits 与上一帧 resolved history 合并；未服务像素按 motion 重投影，并用深度/法线/粗糙度校验（[SsrPass.cs:386-421](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs#L386)）。
- `R2Composite` 全分辨率逐像素读 `R1Sample`、SS hit point、SS 颜色金字塔、HW history，按 `a = s + (1-s)r` 合成（[ScreenSpaceReflections.compute:277-320](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute#L277)）。
- 输出是 unpremultiplied radiance + weight；材质端只做一次 `lerp(probe, rgb, alpha)`。

### 3.6 同帧固定成本

Frame Debugger 显示：

- Depth Pyramid 12
- GTAO 4
- SSGI 9
- Current Color Pyramid 25
- Color History Capture 27

这些是 SSL 的固定成本，不随 RTR 的压缩/续传消失。颜色金字塔每帧重建两条 Gaussian 链（[ColorPyramidPass.cs:57](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/ColorPyramidPass.cs#L57)、[:143](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/ColorPyramidPass.cs#L143)）。

### 3.7 当前 300→200 fps 的读数

- 300 fps = 3.333 ms/帧；200 fps = 5.000 ms/帧；增量 **1.667 ms**，帧时间 +50%。
- 仅凭 Frame Debugger 不能归因；必须用 GPU Profiler / RenderDoc / PIX 分阶段计时。

---

## 4. 成本模型（用于归因，不是实测）

### 4.1 数据量公式

设 trace 分辨率 `P = 960 × 540 = 518,400`，屏幕 `S = 1920 × 1080 = 2,073,600`。

| 缓冲 | 公式 | 1080p 半分辨率估算 |
|---|---|---|
| `originals` | `P × 80 B` | 41.5 MB |
| `hits` | `P × 24 B` | 12.4 MB |
| `queue` | `K × 80 B` | 2.6 MB |
| HW history | `P × 8 B` (RGBA16F) | 4.1 MB |
| SS 颜色金字塔基级 | `S × 8 B` | 16.6 MB |

R1 链路的请求字节（不是 DRAM 实际流量，缓存会降低；结构化缓冲按成员加载，只有整条赋值才按 80 B 计）：

- `R1Original` 写 41.5 MB，读 depth/normal/history/hit/color ≈ 40 MB。
- `R1Tiles` 只读 `requestShare` 4 B/px ≈ 2.1 MB，写 tile 计数 8160 × 8 B ≈ 65 KB。
- `R1Scatter` 只读 `requestShare` 4 B/px ≈ 2.1 MB；对选中的 ≤K 个样本整条读 80 B ≈ 2.6 MB，写队列 ≈ 2.6 MB。
- `R2Temporal` 按成员读 originals（reason/tMin/tMax/ssShare/requestShare ≈ 20 B/px）≈ 10 MB；整条 80 B 上界 41.5 MB。
- `R2Composite` 全分辨率逐像素读 originals，但 2×2 个像素共享同一 trace slot（[ScreenSpaceReflections.compute:292](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute#L292)），按成员加载 ≈ 40 B/thread ≈ **83 MB**（整条 80 B 上界 166 MB）。
- `R2Clear` 写 hits 12.4 MB；`R2Composite` 写输出 16.6 MB。

中性合计约 **200–250 MB** 请求字节；若所有 originals 访问都按整条 80 B 计，上界约 **400 MB**。300 fps 下分别约 60–75 GB/s（上界 ~120 GB/s）。这是估算，必须用 GPU counter 实测。

### 4.2 DXR 成本

- 固定 32768 主射线；每条 hit 最多 4 盏灯入选，其中只有前 2 盏带阴影射线 → 32768 × (1 + 2) ≈ 98k 条射线（其余选中灯不投射阴影；见 [Freeze.json](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/Done/20-Ray_tracing/validation/B0/Freeze.json) 的 `LmaxHit=4 / Smax=2`）。
- 每条 hit 走 URP Lit 的 `DirectBRDF` + 灯表（Basic 模式为 Lambert），按选中灯逐灯着色，不是简单 probe 采样；阴影只覆盖前 2 盏灯（[GlxHitLights.hlsl:112-145](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/RayTracing/GlxHitLights.hlsl#L112)）。
- TLAS 遍历成本取决于场景几何与实例数；`GlxDXR` fat CHS 还包含材质属性读取。
- 固定 K 的尾部线程早退只省遍历，不省 launch；压缩能降低活跃 warp 的空 lane，但不能让 launch 数变成 N。

### 4.3 为什么是 +1.67 ms

按当前帧结构，增量由五块组成：

1. SS trace（半分辨率 Hi-Z）——RTR **没有**替代它。
2. R1 分类/压缩——originals 80 B/px 的多趟读写。
3. DXR——32k 主射线 + 完整 hit 着色 + 阴影射线。
4. R2Temporal + 重建/上采样——半分辨率历史与全分辨率双边升采样。
5. R2Composite——全分辨率 5 纹理采样 + 约 83 MB 级样本读取（整条 80 B 上界 166 MB）。

因此“把 DXR 变快”只能改善第 3 块；第 1、2、4、5 块仍然存在。**如果 Phase 0 发现第 1/4/5 块占大头，R2b 的收益会被固定成本吃掉。**

---

## 5. Lumen 参考

### 5.1 三阶段管线

```text
[1] Screen Tracing (HZB / depth)
        │
        ├── True Hit ──> sample last-frame scene color
        │                 TraceState = { bHit=1, hitDistance }
        │
        └── Miss / Uncertain
                 TraceState = { bHit=0, lastSafeDistance }
                          │
                          v
[2] CompactTraces (prefix sum / atomic append)
        │  remove bHit=1
        │  write dense queue of unresolved trace IDs
        v
    CompactedQueue[N] + DispatchRaysIndirect(N)
                          │
                          v
[3] Hardware RT
        │  TMin = max(lastSafeDistance - PullbackBias, DefaultTMin)
        │  traverse BVH, shade hit
        ├── Hit  ──> TraceState = { bHit=1, newDistance }, radiance
        └── Miss ──> stays bHit=0
                          │
                          v
[4] Integration
        ├── SS hit  -> SS radiance
        ├── HW hit  -> HW radiance
        └── still miss -> Skylight / probe fallback
```

### 5.2 关键机制

| 机制 | Lumen 做法 | 目的 |
|---|---|---|
| 状态位 | 32-bit `TraceHit`：bit31=bHit，低位=距离 | 让后续 pass 知道哪些已解决 |
| 压缩 | 全屏扫描 + 原子/前缀和，移除已解决样本 | 让 HWRT 的 warp 里不出现死 lane |
| 间接派发 | `DispatchRaysIndirect(N)` | 只启动 N 条，而不是全屏 K |
| TMin 续传 | 从 `lastSafeDistance - bias` 开始 | 不重复走屏幕已探过的近段 |
| 天空兜底 | 仍 miss 的射线在积分阶段采样 skylight | 避免“miss 即黑” |
| 光线顺序 | 按 screen probe/tile 组织 | 提升 BVH 与纹理缓存相干性 |

### 5.3 与当前实现的差异

| 项目 | 当前实现 | Lumen 参考 |
|---|---|---|
| 命中/未命中状态 | 只写 hit UV 或 `(99,0)`；无 bHit/安全距离 | 显式 TraceState |
| 队列 | tile 配额选择，最多 K，旋转覆盖 | 全量压缩 + 间接 N |
| 派发 | 固定 K + 早退 | 间接 N |
| TMin | 固定 0.002 | lastSafeDistance - bias |
| 天空 | HW miss 返回 `GlxMiss`，由材质 probe 兜底 | 积分阶段填 skylight |
| 顺序 | tile-major（R1Scatter） | screen probe/tile 顺序 |
| 分辨率 | per-pixel 半分辨率 | screen probe（更低频） |

### 5.4 可借鉴与不可照搬

**可借鉴：**

- 显式三态（resolved/uncertain/miss），而不是只看最终 alpha。
- 压缩后再派发，减少活跃 warp 的空 lane。
- TMin 只从“可证明为空”的距离续传。
- 对仍 miss 的样本做显式环境兜底，而不是把黑色当有效。

**不可直接照搬：**

- Lumen 有 Surface Cache 和 screen probe；本项目是 per-pixel SSR、无缓存，HW hit 着色成本更高。
- Lumen 的 screen tracing 有明确的 max trace distance 和保守 last-safe distance；当前 `SslRayMarch` 的 `t` 只是屏幕参数，不能直接当世界安全距离（见 §6.2）。
- Lumen 可以使用间接派发；Unity 2022.3 C# 不暴露。

---

## 6. 可行性约束

### 6.1 Unity 2022.3 没有 `DispatchRaysIndirect`（已核实）

本地验证：

```text
D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Data/Managed/UnityEngine/UnityEngine.CoreModule.dll
grep -a -o "DispatchRays[A-Za-z]*" → DispatchRays
grep -a -o "DispatchRaysIndirect"    → 无输出
```

D3D12 原生支持间接光追派发：`D3D12_INDIRECT_ARGUMENT_TYPE_DISPATCH_RAYS`，可通过 `ExecuteIndirect` 在 GPU 上配置工作量。问题是 **Unity 2022.3 的 C# API 没有暴露**。

可选方案：

| 方案 | 能精确 N？ | 成本 | 建议 |
|---|---|---|---|
| 固定 K + 压缩 + 早退 | 否（尾部空线程） | 低 | **首选** |
| 上一帧异步回读 N，动态设 K | 近似（一帧延迟） | 中 | 次选 |
| Native plugin 调 `ExecuteIndirect(DISPATCH_RAYS)` | 是 | 高（平台/维护） | 长期可选，不进 v1 |
| 每帧同步回读 N | 是 | 高（阻塞） | 禁止 |

### 6.2 Hi-Z 的 `t` 不是世界安全距离（关键风险）

现有 `SslRayMarch` 的 `t` 是屏幕空间参数；`last safe` 不能直接写进 `R1Sample.tMin`：

- HZB 只包含屏幕可见深度；被柱子/角色遮住的几何不在深度图里。
- 射线“在屏幕里没命中”不等于“3D 空间里为空”；可能是被遮挡后穿到背面，或出屏。
- `miss = belowMip0 && insideFloor` 表示射线进入遮挡体后方；此时 `t` 可能已经在遮挡体内部。
- 薄物体、厚度 bias、边缘深度都可能导致 `t` 不可靠。

因此 TMin 必须有**保守门控**：

```text
仅当以下条件全部成立才续传：
1. 从原点到安全点始终处于“已确认空”的区间（屏幕内命中前的空段，或出屏前的空段）；
2. 安全点来自该空段的最后一个保守位置，而不是 miss 时的 t；
3. 减去 PullbackBias = max(0.01, 0.001 * distance)；
4. 若无法证明为空，TMin = 默认 0.002，从原点重追。
```

建议的 miss 分类：

| miss 类型 | 可否续传 | 理由 |
|---|---|---|
| 射线在屏幕内走到 TMax，无任何遮挡 | 可以 | 屏幕可见段为空；仍需 pullback |
| 射线出屏（screen edge） | 可以，续到出屏点 | 出屏前为空；出屏后未知，HW 从边界继续 |
| 射线进入遮挡体后方（below floor） | **默认不可以** | 无法证明遮挡体后面为空 |
| 薄物体/厚度不足 | 不可以，除非保守回溯 | HZB 不可靠 |
| killRay（背面/粗糙度超限/无效方向） | 不请求 | 不属于可反射样本 |
| 到达 far plane 且 `reflectsSky=false` | 不请求或 TMin=0 | 截断不等于天空可见 |

**这与现有 [SSR-DXR.md §2.3](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/Done/20-Ray_tracing/research/SSR-DXR.md) 的结论一致：不把 Hi-Z 走过的距离当世界空间安全距离。** R2b 的第一版只对“出屏/无遮挡 TMax”启用续传；遮挡体后方续传需要单独证明。

### 6.3 TLAS 更新成本

`RayTracingSceneInstances.Sync` 对移动物体调用 `UpdateInstanceTransform` 并标记 dirty，随后 `RayTracingSceneResources.TryBuild` 用 `Graphics.ExecuteCommandBuffer` 立即重建 RTAS（[RayTracingSceneInstances.cs:100](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/RayTracing/RayTracingSceneInstances.cs#L100)、[RayTracingSceneResources.cs:113](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/RayTracing/RayTracingSceneResources.cs#L113)）。动态场景下这是固定大头，R2b 不会消除它。Phase 0 必须单独计时。

### 6.4 严格 miss-only vs 三态

- 严格 miss-only：只有 `bHit=0` 才入队；边缘/低置信的屏幕命中不追。
- 风险：边缘像素会保留错误的屏幕颜色/权重，出现轮廓错误和接缝。
- 建议：保留当前 `resolved / uncertain / miss` 三态；`uncertain` 指历史命中被接受但仍有未覆盖份额（`requestShare > 0`，`reason = 6`），raymiss（`reason = 4`）与历史被拒（`reason = 5`）按 miss 处理。这样与 Lumen 的“未解决”一致，也不改变现有合成合同。

### 6.5 预算与收敛

- 当前 quota 策略保证空间公平和 16 帧收敛；如果改成“全量压缩 + 前 K 个”，会按 tile 顺序饿死后半屏。
- 建议保留 quota 作为 **N > K 时的选择器**；N <= K 时全量入队。这样既压缩又不破坏收敛。
- 自适应 K 公式（Phase 3）：

```text
K_next = clamp(ceil(N_prev * safety), K_min, K_max)
safety = 1.25
K_min = 4096
K_max = 32768
```

用 `AsyncGPUReadback` 读上一帧 `N = _R1Counts[1]`（requested），避免同步。若一帧延迟导致溢出，溢出样本沿用现有行为：有 HW 历史则按 motion 重投影复用，无历史才回退到 probe（[ScreenSpaceReflections.compute:354-390](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute#L354)），并在计数器记录。

### 6.6 能量与接缝

- SS 辐亮度来自上一帧 color pyramid；HW 命中来自当前帧完整 PBR。两者混合会产生亮度/颜色接缝（R3 已记录同帧 SS 接缝）。
- 约束：
  - 不要把“未服务/无预算”当天空；保持 probe 兜底。
  - HW miss 的有限 TMax 不等于天空可见。
  - 天空填充必须与材质 probe 的 blend 协调，避免双计。
- 建议先保持现有 `GlxMiss` 语义（不主动写天空），只优化追踪成本；天空策略作为 Phase 4 单独决策。

### 6.7 平台、XR、变体

- RT 仅 Windows D3D12 验证；主机 BLOCKED；XR 不支持 RT。
- 若新增 shader keyword，必须使用 `shader_feature` 并扩展现有 RT-only 剥离（[ShaderScriptableStripper.cs:1048-1070](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Editor/ShaderScriptableStripper.cs#L1048)），保持 D3D11-only 构建 0 个 RT 变体。
- 变体预算不得突破现有 Lit/ForwardLit 6144、compute 1280 的记录；若新增，必须单独记账。

---

## 7. 方案设计（建议实现）

### 7.1 目标与非目标

**目标：**

1. 把“已解决”样本从 HW 队列剔除，减少无效 DXR 工作。
2. 用压缩队列提高活跃 warp 的利用率。
3. 对可证明为空的屏幕段做 TMin 续传，减少近段 BVH 遍历。
4. 用自适应 K 降低固定预算在高帧率下的浪费。
5. 保持现有 unpremultiplied + weight 合成合同和三态语义。

**非目标：**

- 不做间接派发（2022.3 不可用）。
- 不做 Surface Cache/多跳。
- 不改 SS trace 算法和现有像素阈值。
- 不默认开启天空覆盖 probe。

### 7.2 三态（+ invalid）

| 状态 | 判定 | 输出 |
|---|---|---|
| resolved | 历史命中被接受且 `requestShare = 0`（`reason = 2`） | SS 颜色 + 权重，不入 HW |
| uncertain | 边缘淡出（`reason = 6`） | 入 HW，SS 保留 `ssShare` 份额 |
| miss | 无有效屏幕命中（`reason = 4`）或历史被拒（`reason = 5`） | 入 HW，SS 份额 0 |
| invalid | 背景/背面/无效方向（`reason = 0`）、粗糙度/材质超限（`reason = 1`） | 不入 HW，回 probe |

复用当前 `reason`/`requestShare` 语义，不新增二值开关。

### 7.3 数据布局

`R1Sample` 已经有 `tMin` 字段（80 B 布局，偏移 44），raygen 也已经读它：

```text
R1Sample (80 B)
 0  uint  slot
 4  uint  sequenceFrame
 8  uint  reason
12  uint  sourcePixel
16  float3 position
28  float roughness
32  float3 normal
44  float tMin        <-- 现在固定 0.002；v2 写入安全世界距离
48  float3 direction
60  float tMax
64  float ssShare
68  float requestShare
72  uint  materialId
76  uint  instanceId
```

因此 **不需要改 ABI**；需要新增的是一张半分辨率 `TraceState`（或把 hit point 纹理扩为 float4）：

```text
TraceState (half-res, 8-16 B/px)
  bHit        : 1 bit
  missKind    : 2-3 bits (OffscreenExit / FarPlane / BehindOccluder / Thin)
  lastSafeT   : float (world units, 0 if unknown)
  hitT        : float (optional, for debug)
```

### 7.4 Pass 图

```text
SS trace (existing)
   │  writes hitUV + TraceState
   v
Classify (R1Original v2)
   │  reads TraceState, history color
   │  writes R1Sample (tMin = Gate(safeT, missKind))
   v
Compact / Budget
   │  N <= K: all unresolved
   │  N >  K: per-tile quota (rotating)
   │  writes dense queue + counts
   v
DispatchRays (fixed K, early-out; optional adaptive K)
   │  TMin = q.tMin; q.tMin==0 -> default
   v
R2Temporal (existing)
   v
R2Composite (existing) + optional Sky/Probe policy
   v
material lerp(probe, rgb, alpha)
```

### 7.5 压缩与预算

**目标**：让 `queue[0..N-1]` 都是未解决样本，且顺序尽量 tile-major。

实现选项：

1. **保留现有 R1Tiles/Offsets/Scatter，只改选择条件**（低风险）
   - `R1Tiles` 统计每 tile 的 unresolved 数。
   - `R1Offsets` 按 quota 分配；`R1Scatter` 写入队列。
   - 优点：复用现有代码、保持旋转公平。
   - 缺点：N>K 时仍然只写入 K 个；队列已经是紧凑的，但配额不是全量。
2. **全量 prefix-sum 压缩**（中风险）
   - 每个 8×8 组用 wave intrinsics 做组内前缀和，组头 `InterlockedAdd` 得到全局 base。
   - 写入 `queue[base + rank]`；`count` 为 N。
   - 优点：N<=K 时全量；N>K 时可用第二遍 quota 选择。
   - 缺点：多一个原子/计数 pass，队列顺序需要 group 调度保证 tile-major。
3. **两阶段：先计数，再写队列**（推荐）
   - Pass A：只统计每 tile unresolved 数和全局 N。
   - Pass B：按 quota/前缀和写队列。
   - 便于自适应 K 和预算诊断。

建议先做 (1) + TMin，确认 DXR 命中/未服务计数正确；再做 (3)。

### 7.6 TMin 续传：安全规则与伪代码

修改 `SslRayMarch` 返回 `lastSafeT` 与 `missKind`：

```hlsl
struct SslTraceResult
{
    bool  hit;
    float lastSafeT;   // world units, 0 if not provable
    uint  missKind;    // 0=hit, 1=offscreen, 2=farplane, 3=behindOccluder, 4=thin, 5=kill
};

SslTraceResult SslRayMarchEx(...)
{
    float safeT = 0;
    ...
    while (...) {
        // 在确认当前 cell 为空后，推进 safeT
        if (cellEmpty)
            safeT = t; // 或 min(t, distWall) 的保守值
        ...
        if (miss) {
            if (exitedScreen) missKind = Offscreen;
            else if (belowFloor) missKind = BehindOccluder;
            ...
            break;
        }
    }
    // 只有 offscreen/farplane 且路径全程为空时才给 lastSafeT
    if (missKind != Offscreen && missKind != FarPlane) safeT = 0;
    return { hit, safeT, missKind };
}
```

`R1Original`：

```hlsl
float safeT = traceState.lastSafeT;
float tmin = 0.002;
if (traceState.missKind == Offscreen || traceState.missKind == FarPlane)
{
    tmin = max(0.002, safeT - max(0.01, 0.001 * safeT));
}
s.tMin = tmin;
```

raygen：

```hlsl
RayDesc ray;
ray.Origin = q.position;
ray.Direction = normalize(q.direction);
ray.TMin = max(q.tMin, 0.002);
ray.TMax = q.tMax;
if (ray.TMin >= ray.TMax) return; // 已经到最大距离
```

注意：

- `q.position` 必须与 `q.direction` 同一世界单位；方向已归一化，`tMin` 可直接用世界距离。
- `R2Temporal` 用 `q.tMin` 判断 hit 是否有效；TMin 必须保守，否则会把本应计入的 hit 判成无效。
- 若 `lastSafeT` 不可证明，保持 0.002，不做续传。

### 7.7 Warp 利用率与重排

- 压缩后，活跃线程 `0..N-1` 是密集的；固定 K 的尾部线程早退，成本通常小于 0.1 ms。
- DXR 仍有 hit/miss、材质、阴影分支 divergence；压缩不能消除。
- 队列保持 tile-major；可选按方向桶/粗糙度桶排序，但排序本身有成本，先测后做。
- 不要用 CPU 排序；用 GPU 的 group 顺序 + 轻量分桶。
- 评价指标：N/K、活跃线程比例、GPU ms、每 warp 平均射线数（PIX/RenderDoc 可见 DXR 调用）。

### 7.8 天空/probe 兜底策略

| SSR 兜底模式（提议 `reflectionSkyFallback`） | 行为 |
|---|---|
| `None`（默认，等于当前 HEAD） | HW miss 保持 weight 0，由材质 probe/baked GI 填充；不写天空 |
| `Sky` | 可在 composite 填 sky，但必须确认材质端不会再次加 probe |
| `Probe` | 填材质 probe，或保持 weight 0 |
| 未服务/无预算 | 保持 weight 0，不要误标 sky |

**开关来源（关键）：** `rayMiss` 是 SSGI 卷参数 `RayMarchingFallbackHierarchy`（`None / ReflectionProbes / Sky / ReflectionProbesAndSky`，[ScreenSpaceGlobalIllumination.cs:9-23](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.cs#L9)），SSR 卷只有 `reflectSky` 布尔（[ScreenSpaceReflection.cs:106](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceReflection.cs#L106)，只控制 SS trace 的 far-plane 采样）。SSR pass 拿不到 SSGI 的卷参数，**不要直接读 `rayMiss`**；Phase 4 若要 A/B，需在 `ScreenSpaceLightingSettings` 新增 SSR 专用 `reflectionSkyFallback`（默认 `None` = 保持 HEAD 语义），并与 `reflectSky` 解耦。

当前 [RayTracingSsr.raytrace:32-38](file:///E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/RayTracingSsr.raytrace#L32) 的 miss 明确不取天空；**R2b 第一阶段不要改变这个语义**，避免能量双计。天空策略放到 Phase 4，单独做 A/B。

### 7.9 代码落点

| 文件 | 改动 |
|---|---|
| `Runtime/.../Passes/SsrPass.cs` | 分类/派发/合成顺序；新增 continuation 开关；counters |
| `Runtime/.../Passes/SsrRequestQueue.cs` | budget 可变；adaptive K；queue 统计 |
| `Runtime/.../SsrRayTracingCameras.cs` | 资格与 budget 更新；无 RT 时清理 |
| `Runtime/.../ScreenSpaceLightingSettings.cs` | `rayTracingContinuation`、`adaptiveRayBudget`（默认 off）；Phase 4 另加 SSR 专用 `reflectionSkyFallback`（默认 `None`，不复用 SSGI 的 `rayMiss`） |
| `Shaders/.../ScreenSpaceReflections.compute` | TraceState 输出；R1Original 写 tMin；R1Tiles/Offsets/Scatter 统计 |
| `Shaders/.../SsrRequestQueue.hlsl` | 压缩/counters；tMin 传递 |
| `Shaders/.../SsrRayRequest.hlsl` | 保持 80 B；如新增字段需同步 stride 断言 |
| `Shaders/.../RayMarching.hlsl` | `SslRayMarchEx` 返回 safeT/missKind；保持旧入口兼容 |
| `Shaders/.../RayTracingSsr.raytrace` | 使用 q.tMin；越界/早退保护 |
| `Editor/ShaderScriptableStripper.cs` | 若新增 keyword，加入 RT-only 剥离 |
| `Assets/Tests/RayTracing/SSR/SsrRequestTests.cs` | 布局、tMin、计数器测试 |

---

## 8. 实施计划

### Phase 0：测量与冻结（2–3 天）

- 在 R1Original、R1Tiles/Offsets/Scatter、DispatchRays、R2Temporal、Reprojection、Upsample、R2Composite 加 GPU marker。
- 记录：N、K、resolved、uncertain、miss、served、overflow、TLAS build ms。
- 跑 1080p D3D12，RT on/off，静态/动态，SSR 全开/仅 SSR。
- **Gate P0**：得到 1.67 ms 的分项占比；若 DXR+R1 < 30%，先优化固定 pass，R2b 降级为 P2。

### Phase 1：TraceState + 安全 TMin（3–5 天）

- `SslRayMarchEx` 输出 `lastSafeT/missKind`；只对 offscreen/farplane 启用。
- `R1Sample.tMin` 写入保守值；raygen 已支持，只加保护。
- 固定 K，不改压缩。
- **Gate P1**：隐藏遮挡物夹具不漏遮挡；薄墙不穿透；TMin=0 vs 续传的图像差在阈值内；GPU ms 改善 ≥5%。

### Phase 2：压缩 + 自适应 K（3–5 天）

- 先保留 quota，改统计与队列；再加全量 prefix-sum。
- `AsyncGPUReadback` 读上一帧 N；`K_next = clamp(1.25N, 4096, 32768)`。
- **Gate P2**：`eligible = resolved + unresolved`；`unresolved = served + overflow`；N/K 与 GPU ms 记录；1080p SSR 增量降 ≥15%；D3D11 off 图逐字节不变。

### Phase 3：三态合成与计数器闭环（2–3 天）

- 明确 `resolved/uncertain/miss`；不改变现有 unpremultiplied 合同。
- 未服务/溢出不写天空：先复用 HW 历史（`R2Temporal`），无历史才回退到材质 probe。
- **Gate P3**：边缘/轮廓指标不退化；无新接缝；计数器守恒。

### Phase 4：天空/probe 策略（可选，2–3 天）

- 仅在新增的 SSR 参数 `reflectionSkyFallback = Sky` 时填 sky；默认 `None` 与 HEAD 一致，并与 `reflectSky`（SS trace far-plane 采样）解耦。SSR 不复用 SSGI 的 `rayMiss`。
- **Gate P4**：无双计；能量对比参考；用户确认。

### Phase 5：重排/4K/双 API/归档（3–5 天）

- 可选方向分桶；4K 显存与时序；D3D11/D3D12；Player 证据。
- 按 [ACCEPTANCE.md](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/ACCEPTANCE.md) 建 `PLAN/Done/21-R2b/`，证据放 `validation/R2b/`。

### 任务表

| ID | 任务 | 交付 | 预估 |
|---|---|---|---|
| P0-1 | 分阶段 GPU 计时 | markers + CSV | 1 d |
| P0-2 | 计数器与 N/K | counters + summary | 1 d |
| P1-1 | TraceState | 半分辨率状态纹理 | 1 d |
| P1-2 | SslRayMarchEx | safeT/missKind | 1.5 d |
| P1-3 | R1 tMin | 分类写入 | 0.5 d |
| P1-4 | 隐藏遮挡物夹具 | PlayMode + 图 | 1.5 d |
| P2-1 | 压缩队列 | kernel + counts | 2 d |
| P2-2 | 自适应 K | readback + clamp | 1 d |
| P2-3 | 计数守恒测试 | EditMode/PlayMode | 1 d |
| P3-1 | 三态合成 | shader + 测试 | 1.5 d |
| P4-1 | 天空策略 | 决策 + 实现 | 1.5 d |
| P5-1 | 验证/归档 | 报告 + 证据 | 3 d |

总预估 **15–20 人日**（不含主机和 native plugin）。

---

## 9. 验证计划

### 9.1 测试矩阵

| 维度 | 取值 |
|---|---|
| API | D3D12（RT 开）、D3D11（RT 关，逐字节对照） |
| Renderer | Deferred / Forward+ / Forward（RT 只在合格路径） |
| 分辨率 | 1920×1080、3840×2160（可选） |
| 相机 | 静止、平移、旋转、切镜 |
| 场景 | 静态、动态遮挡物、薄墙、隐藏遮挡物、离屏遮挡物 |
| SSR | Approximation 半分辨率、RT off、RT on、TMin on/off、compaction on/off |
| TAA | on/off（历史与抖动） |
| XR | off（RT 不支持 XR） |
| 预算 | K=0、4096、8192、16384、32768；N>K 与 N<K |

### 9.2 功能门槛

1. **计数守恒**（对齐 `_R1Counts`：`[0]` eligible、`[1]` requested、`[2]` served、`[3]` overflow）
   - `eligible = resolved + unresolved`；invalid（`reason = 0/1`）不计入 eligible。
   - `unresolved = requested = served + overflow`
   - `served` 不超过 K；`queue` 写入不越界。
2. **TMin 安全**
   - 隐藏遮挡物：RT 必须命中，不能因为 TMin 提前开始而漏遮挡。
   - 薄墙：不能穿透；不能从墙后开始。
   - 出屏：续传到出屏点；出屏后命中正确。
   - `tMin >= tMax` 时不派发/直接 miss。
3. **图像**
   - 复用现有阈值：mean ≤ 2/255、peak ≤ 24、>8 px 计数；不因 R2b 放宽。
   - SS/HW 接缝、边缘、粗糙度淡出、clear coat 不退化。
   - 关闭 R2b 时与 HEAD 逐像素一致（或记录已知差异）。
4. **性能**
   - 同机、同 Player、同场景、120 预热 + 120×3 采样。
   - 分阶段 GPU ms（R1/DXR/Temporal/Composite）+ 总帧时间。
   - 报告 N/K、served/overflow、TLAS build ms。
5. **变体/剥离**
   - D3D11-only 构建 0 个 RT 变体；Win64 变体不超过现有预算。
6. **资源**
   - TraceState 额外显存、队列、history；重复开关净增长 0。
7. **回归**
   - R1–R5 原有测试全过；GI/refraction/transparent 不受影响。

### 9.3 证据落点

- 计划/决策：`PLAN/Done/21-R2b/`
- 原始证据：`PLAN/Done/21-R2b/validation/`
- 对照：HEAD 与候选各一份 Player 构建、同场景、同 API、同分辨率。
- 报告：HANDOFF + `KNOWN_DIFFERENCES.md` + `ACCEPTANCE.md` 更新。

### 9.4 归因实验（Phase 0 必做）

1. SSR 全关 vs SSR 开 + RT 关 vs SSR 开 + RT 开。
2. `reflectionRayBudget` 扫 0/4096/8192/16384/32768。
3. 静态 vs 动态遮挡物（TLAS 重建）。
4. SSGI/Refraction 关掉，仅 SSR（固定 pass 影响）。
5. 主线程/渲染线程/GPU 分开看；300 fps 下 CPU dispatch 也可能是瓶颈。

---

## 10. 预期收益与 Go/No-Go

### 10.1 预估（必须实测）

| 场景 | 当前 SSR 增量 | R2b 后预估 | 说明 |
|---|---|---|---|
| DXR 占大头 | 1.67 ms | 1.2–1.4 ms | compaction + TMin + adaptive K |
| R1 带宽占大头 | 1.67 ms | 1.3–1.5 ms | 压缩/打包改善有限 |
| 固定 pass 占大头 | 1.67 ms | 1.5–1.6 ms | R2b 基本无效 |

按 200 fps 计：

- 乐观：5.0 → 4.3 ms，约 230 fps。
- 中性：5.0 → 4.6 ms，约 217 fps。
- 悲观：5.0 → 4.9 ms，约 204 fps。

### 10.2 Go/No-Go

- **Go Phase 1**：Phase 0 显示 DXR+R1 ≥ 30% 增量；且隐藏遮挡物夹具可设计。
- **Go Phase 2**：TMin 安全测试通过；压缩队列计数守恒。
- **No-Go TMin**：隐藏遮挡物漏遮挡、薄墙穿透、图像超阈；保留 compaction/adaptive K，不做续传。
- **No-Go R2b**：压缩 + 自适应 K 的总收益 < 0.1 ms，或引入 > 0.1 ms 额外成本；记录为 v2 NO-GO，不影响 R1–R5。
- **优先级调整**：若固定 pass（Composite/Upsample/ColorPyramid）占 > 50%，先做固定 pass 优化，R2b 延后。

---

## 11. 风险与缓解

| 风险 | 影响 | 缓解 |
|---|---|---|
| Hi-Z 安全距离不可靠 | 漏遮挡/穿透 | 只对 offscreen/farplane 续传；pullback；隐藏遮挡物夹具 |
| 无间接派发 | 尾部空线程 | 固定 K + 早退；自适应 K；异步回读 |
| 压缩开销 | 新增 0.05–0.1 ms | 半分辨率、wave 前缀和、只做必要计数 |
| 严格 miss-only | 边缘错误 | 保持三态，不二值化 |
| N>K 饿死 | 局部不收敛 | 保留 per-tile quota/旋转 |
| SS/HW 接缝 | 亮度台阶 | 不改变现有合成；只在 TMin 安全时启用 |
| 天空双计 | 过亮 | 第一阶段不填天空；策略单独 A/B |
| TLAS 重建 | 动态场景贵 | 单独计时；限制动态实例；后续优化 |
| 自适应 K 一帧延迟 | 溢出/欠采样 | 1.25 safety；溢出先复用 HW 历史、无历史回 probe；计数 |
| 变体膨胀 | 构建/包体 | shader_feature + RT-only 剥离 + 预算 |

---

## 12. 回滚与开关

建议新增两个**默认关闭**的 feature 设置：

- `rayTracingContinuation`：控制 TMin 续传。
- `adaptiveRayBudget`：控制自适应 K。

保留现有固定 K / 无续传路径作为 A/B 基线：

- 关闭时行为与 HEAD 一致。
- 任一失败可逐项回退，不影响 R1–R5。
- 不在 Volume 暴露，避免美术误开。

---

## 13. 开放问题（需要 owner 决策）

1. 目标是“严格 miss-only”还是“三态未解决”？建议后者。
2. 是否允许在 SSR 新增的 `reflectionSkyFallback=Sky` 时填天空？涉及 probe 双计。
3. 是否接受自适应 K 的一帧延迟？
4. 是否接受 80 B/px 的 R1Sample，还是先做 32–40 B 打包？
5. 是否把 TMin 只用于 offscreen/farplane，还是投入更多时间做遮挡体后方的保守安全距离？
6. 是否考虑 native plugin 的 `ExecuteIndirect(DISPATCH_RAYS)`？建议不进 v1。
7. 4K/主机是否在本次 R2b 范围？建议仅 Windows 1080p/4K D3D12。

---

## 14. 参考来源

### 14.1 本仓库代码与文档

- 当前 R2b 任务定义与 NO-GO 门槛：[PLAN/Done/20-Ray_tracing/todolist.md](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/Done/20-Ray_tracing/todolist.md)
- SSR × DXR 合同、TMin/Hi-Z 警告、R2b 门槛：[research/SSR-DXR.md](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/Done/20-Ray_tracing/research/SSR-DXR.md)
- 总验收协议：[PLAN/ACCEPTANCE.md](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/ACCEPTANCE.md)
- SS trace：`U/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute`（`ScreenSpaceReflectionsTracing`、`R1Original`、`R2Temporal`、`R2Composite`）
- 请求队列：`U/Shaders/Utils/ScreenSpaceLighting/SsrRequestQueue.hlsl`、`SsrRayRequest.hlsl`
- 屏幕步进：`U/Shaders/Utils/ScreenSpaceLighting/RayMarching.hlsl`
- raygen：`U/Shaders/Utils/ScreenSpaceLighting/RayTracingSsr.raytrace`
- C# 驱动：`U/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs`、`SsrRequestQueue.cs`、`SsrRayTracingCameras.cs`
- TLAS：`U/Runtime/RendererFeatures/RayTracing/RayTracingSceneResources.cs`、`RayTracingSceneInstances.cs`
- 命中着色：`U/Shaders/RayTracing/GlxHitLights.hlsl`、`GlxLitHit.hlsl`、`RayTracingSceneLights.cs`
- 固定成本：`U/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/ColorPyramidPass.cs`
- 变体剥离：`U/Editor/ShaderScriptableStripper.cs`

> `U/` = `URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/`。

### 14.2 Unity / API 事实

- Unity 2022.3.62f1 `UnityEngine.CoreModule.dll` 本地检查：只有 `DispatchRays`，没有 `DispatchRaysIndirect`。
- D3D12 原生间接光追：`D3D12_INDIRECT_ARGUMENT_TYPE_DISPATCH_RAYS`（Microsoft Docs）。
- `ID3D12GraphicsCommandList::ExecuteIndirect`（Microsoft Docs）。
- `D3D12_DISPATCH_RAYS_DESC`（Microsoft Docs）。

### 14.3 Lumen / 外部资料

- Epic Games, *Lumen Technical Details*（Screen Tracing 先于更可靠方法；HWRT/SDF 两种追踪）。
- Epic Games, *Lumen Performance Guide*（Screen Probe Gather、HWRT 推荐场景）。
- UE5 源码：`Engine/Source/Runtime/Renderer/Private/Lumen/LumenScreenProbeTracing.usf`、`LumenReflectionTracing.usf`、`LumenScreenProbeGather.usf`（需 UE 源码许可；本报告不复制源码）。
- UE5.4 Lumen 源码阅读（ScreenProbeTracing / Reflection，含 CompactTracing 说明，作为二手分析）。
- 理论：
  - Aila & Laine, *Understanding the Efficiency of Ray Traversal on GPUs*, HPG 2009.
  - Laine, Karras, Aila, *Megakernels Considered Harmful: Wavefront Path Tracing on GPUs*, HPG 2013.
  - McGuire & Mara, *Efficient GPU Screen-Space Ray Tracing*, JCGT 2014.
  - *Ray Tracing Gems*, Apress 2019（ray compaction / scheduling 章节）。

### 14.4 链接

- https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-technical-details-in-unreal-engine
- https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-performance-guide-for-unreal-engine
- https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ne-d3d12-d3d12_indirect_argument_type
- https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist-executeindirect
- https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_dispatch_rays_desc
- https://zhuanlan.zhihu.com/p/1907166408510530667
- https://zhuanlan.zhihu.com/p/1911818476764525634

---

## 附录 A：预算与收敛公式

```text
P        = traceWidth * traceHeight
tiles    = ceil(traceWidth / 8) * ceil(traceHeight / 8)
quota    = max(1, K / tiles)
frames   = ceil(64 / quota)

1080p 半分辨率:
P     = 960 * 540 = 518,400
tiles = 120 * 68 = 8,160
K=32768 -> quota=4 -> frames=16
K=8192  -> quota=1 -> frames=64
```

## 附录 B：raygen 伪代码

```hlsl
void TraceSsrRequests()
{
    uint index = DispatchRaysIndex().x;
    uint count = min(_R1Counts[2], _R2K);
    if (index >= count) return;

    R1Sample q = _R1Queue[index];
    if (!all(isfinite(q.position)) || !all(isfinite(q.direction))) return;
    if (dot(q.direction, q.direction) <= 0) return;
    if (q.tMax <= q.tMin || q.tMin < 0) return;

    RayDesc ray;
    ray.Origin = q.position;
    ray.Direction = normalize(q.direction);
    ray.TMin = max(q.tMin, 0.002);
    ray.TMax = q.tMax;
    if (ray.TMin >= ray.TMax) return;

    GlxBasicPayload p = GlxEmptyPayload(GlxReflectionMode);
    TraceRay(_GlxScene, RAY_FLAG_NONE, 0xff, 0, 1, 0, ray, p);
    _R2Hits[q.slot] = p;
}
```

## 附录 C：当前代码索引（按阅读顺序）

1. `SsrPass.cs`：`AddRenderPasses` 的启用条件、`Dispatch`、`TryTrace`、`Temporal`、`Composite`。
2. `SsrRequestQueue.cs`：buffer 分配、`Record`、`Trace`。
3. `SsrRequestQueue.hlsl`：R1 分类、tile、offsets、scatter。
4. `SsrRayRequest.hlsl`：`R1Sample` 80 B 布局。
5. `RayMarching.hlsl`：`SslRayMarch` 的 `t`/miss 逻辑。
6. `ScreenSpaceReflections.compute`：SS trace、R1Original、R2Temporal、R2Composite。
7. `RayTracingSsr.raytrace`：raygen/miss。
8. `SsrRayTracingCameras.cs`：资格与队列生命周期。
9. `RayTracingSceneResources.cs` / `RayTracingSceneInstances.cs`：TLAS 构建/更新。
10. `ColorPyramidPass.cs`：固定全屏成本。
