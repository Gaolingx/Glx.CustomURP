# 11-LightCulling 交接：HDRP 光列表调研与 URP Clustered Deferred（URP BRDF）实施计划

> **SUPERSEDED（旧 stencil 基线）**：旧 stencil/clustered A/B 操作仅用于冻结版本，不能在当前玩家重跑。当前参照为 Forward+，普通 Forward 是不支持场景的回退。 见[当前验收口径](../../ACCEPTANCE.md#c6-current)。

> 状态：**计划已修订（D15/D16）；T1/T5 运行时接入完成并在 D3D11/D3D12 player 验证；多灯 A/B（mean 0.78/255）与性能交叉点已取得；T3 kernel 与 T6 preflight 已产出；T4 阴影子集与 cookie / light layers / GTAO 直接光 AO 已在 D3D11/D3D12 player A/B 通过，mixed lighting-shadowmask 实现完成但无 baked 数据、运行时 A/B 标 PARTIAL；已与 HDRP 14.0.12 对照定稿**
> 修订：2026-11-22（T4 cookie / light layers / mixed / GTAO 直接光 AO；间接 AO 取消分支；feature matrix 两个 API 重跑）
> 修订：2026-11-21（T5 运行时接入、cluster 缓冲释放、设备/XR gate、arena A/B、D3D12 光源抓帧；T4 阴影子集与 feature matrix）
> 建立：2026-11-21
> 基线：`c37b9dd`（Record the DDGI plan revision commit）
> 结果入口：本文件；实施后追加 `report.md` 与 `validation/`
> 关联：`PLAN/Done/01-ScreenSpaceLighting`（共享深度/探针 atlas/证据格式）、`PLAN/Done/06-ValidationAllRDC`（`.rdc` 预算与证据卫生）、`PLAN/Done/10-ContactShadow`（计划格式）
> 外部参考：`E:\workspace\GitHub\HDRPSample01\Library\PackageCache\com.unity.render-pipelines.high-definition@14.0.12`
> 本仓库 URP：`URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12`（fork）
> 行号基于上述 HDRP 14.0.12 与基线 `c37b9dd`；改动后以符号名为准。

---

## 0. 一页结论

**问题**：light culling 是否必须走 CS-based deferred 才有收益？

**答案**：不是。HDRP 自己的 **opaque forward** 就是“GPU 构建 tile 光列表 + PS 逐像素读表着色”；deferred 只是复用同一张表。两者是正交的两件事：

- forward pass 直接定义 FPTL：`Runtime/Material/Lit/Lit.shader:893` `#define USE_FPTL_LIGHTLIST`；透明才退回 clustered（`Lit.shader:806`）；deferred 强制 FPTL（`Runtime/Lighting/LightLoop/DeferredTile.shader:46`）。
- PS 读表在 `Runtime/Lighting/LightLoop/LightLoop.hlsl:259-322`：`GetCountAndStart` 取 tile 内 `lightStart/lightCount`，循环 `FetchIndex` 取全局灯索引。
- forward/deferred 的唯一结构差异是 wave 标量化：deferred 一个 wave 落在一个 tile 上，forward 才需要 scalarize（`LightLoop.hlsl:4-8`）。

**本轮范围（用户冻结）**：只做 URP 自身特性；不搬面光源、体积雾、物理曝光、SSS、HDRP 材质体系；BRDF 用 URP 现有函数；新增 GBuffer 复用 URP 布局；“deferred lighting”翻译成 compute；光列表与剔除采用“借鉴 HDRP 思路、复用 URP 数据”。

**一句话结论**：这不是“HDRP deferred 迁移”，而是 **Clustered Deferred (URP BRDF)**——用 URP 现有 GBuffer/BRDF/阴影/光数据，把每灯全屏 pass 换成 tile 循环 compute。核心成本 6–10 人周（不含自定义 shader 的 GBuffer 覆盖），按本仓库验收强度上浮 30–50%；不是上一轮口头评估的 20–30 人周。那个数字对应“HDRP 能力对齐”（面光源 LTC/软阴影、7 张 GBuffer、material feature、阴影 atlas、体积雾、曝光、StackLit/Hair/Fabric），与本轮不是同一件事。

**三项关键调研发现（决定成本下降的原因）**：

1. **tile 列表与 deferred 无关**，且 URP 14 已自带 Forward+ 的 tile/zbin 列表；你们 fork 里还有一份 compute-safe 的迭代器（`Shaders/Utils/ScreenSpaceLighting/ReflectionProbeFallback.hlsl:100-155`），已经在 compute 里读 `URP_ZBins/urp_Tiles`。灯列表只是 header 0（探针是 header 1，`:126`）。
2. **URP 的每灯 deferred 着色函数已经存在**：`Shaders/Utils/StencilDeferred.hlsl:213-321` 已包含“深度→世界坐标（`:274`）→ GBuffer 解码（`:298`）→ `BRDFDataFromGbuffer` + `LightingPhysicallyBased`（`:307-308`）”。翻译成 compute 是搬函数，不是重写 BRDF。
3. **间接光不在 deferred 灯光 pass 里**：GBuffer pass 已把 emission + `GlobalIllumination(...)`（含烘焙 GI、反射探针、SSGI/SSR 钩子）写进 GBuffer3（`Shaders/LitGBufferPass.hlsl:213-226`；`ShaderLibrary/UnityGBuffer.hlsl:277` 的注释明确 `bakedGI` 在 lighting pass 不可用，因为已经在 GBuffer3 里）。因此 compute kernel **只做直接光**，additive 写回 GBuffer3 即可；不需要在 kernel 里重做探针/SSGI/GI，工作量与风险进一步下降。

**冻结决策（D1–D14）**：

| ID | 决策 | 理由 |
|---|---|---|
| D1 | 新路径命名为 **Clustered Deferred (URP BRDF)**；接入方式为 `RenderingMode.Deferred` + `UniversalRendererData` 上新增布尔开关（默认关）；关闭时与现有 stencil deferred 逐像素一致 | 所有 `renderingMode == Deferred` 的既有分支保持成立；可 A/B、可回退；避免新增 enum 值带来的序列化与全量审计 |
| D2 | 不新增 GBuffer 布局：复用 GBuffer0–2 + GBuffer3（GI/lighting）+ 可选 shadowmask/light layers；不扩到 7 张、不引入 material feature | 保住现有 SSGI/SSR/GTAO/折射对 GBuffer 的依赖；避免 HDRP 材质体系 |
| D3 | v1 灯型：方向光 + 点光 + 聚光；支持 light cookie、light layers；面光源/矩形光/面光源软阴影**不做** | URP 无面光源 BRDF/LTC；单独硬骨头，另立计划 |
| D4 | 光列表复用 URP Forward+ 的 cluster 构建与缓冲布局（`URP_ZBins`/`urp_Tiles`/`_FPParams*`）；不移植 HDRP FPTL；**只扩展“何时构建”**，不打开 `_FORWARD_PLUS` 全局关键字 | 关闭时透明/forward-only 的逐物体灯数据完全不变，回归面最小；compute 只消费列表 |
| D5 | 光照求值复用 `BRDFDataFromGbuffer` / `LightingPhysicallyBased` / `InputDataFromGbufferAndWorldPosition`；不重写 BRDF | `StencilDeferred.hlsl` 已验证同一套函数；与 forward 结果一致 |
| D6 | 阴影复用 URP 现有主光 cascade / screen-space shadow 与 `AdditionalLightShadow(lightIndex)`；v1 只支持常量数组灯数据（本 fork `RenderingUtils.useStructuredBuffer == false`） | `RenderingUtils.cs:74-87` 硬编码返回 false；Forward+ 已证明“全局灯索引 → 阴影采样”这条链可用 |
| D7 | kernel 采用**视口像素并行** dispatch：`ceil(viewport/16)` 个 16×16 线程组、1 像素/线程；tile id 由每像素 UV × `_FPParams1.xy` 推导，不依赖 CPU 选出的 tile 宽度；一次写 lighting attachment；不使用 light-shape stencil、不做每灯全屏 pass | compute 没有 per-draw scalarization；zbin header 本就逐像素不同，“组=tile”只换来 tile mask 同址；像素并行同时支持 CPU 会选的 8/16/32/64 |
| D8 | kernel 只算直接光，写 `RGBA16F` lighting UAV；再由一个全屏 resolve pass 以 `Blend One One, Zero One` 加进 GBuffer3；间接光保持在 GBuffer pass | **compute dispatch 没有固定功能混合**，不能直接“Blend 写 GBuffer3”；resolve 与 stencil deferred 的混合语义一致（`StencilDeferred.shader:158`） |
| D9 | **已废止，由 D16 取代**：不再要求所有 opaque 都有 `UniversalGBuffer`；保留 `m_RenderOpaqueForwardOnlyPass`（其在 deferred lighting 之后执行） | 计算 pass 通过材质类型 mask 跳过 forward-only 像素，forward-only 仍由原 forward 路径着色；避免“缺 pass 的物体静默消失”和 T6 的线性成本 |
| D10 | 平台：D3D11/D3D12/Vulkan/Metal，要求 Compute + 可读 GBuffer/depth；GLES/WebGL 不支持；v1 **不使用** native render pass（`_RENDER_PASS_ENABLED`）与 MSAA（deferred 本身已禁 MSAA，`UniversalRenderer.cs:367`）；若 renderer 资产开了 native RP，则本帧强制 `useRenderPassEnabled = false` + `m_DeferredLights.UseRenderPass = false` 并输出一次告警；XR/foveated 不在 v1 | 仓库内所有 renderer 资产 `m_UseNativeRenderPass: 0`，且 D3D12 被硬排除（`ScriptableRenderer.cs:647`）；强制关闭与 URP 自身 validation 失败降级路径一致 |
| D11 | 构图仍在 CPU（URP Burst jobs）；GPU FPTL 为 follow-up，仅在 profiler 证明 CPU 构图成为瓶颈时做，且缓冲布局保持不变 | 先拿到最大收益/最小风险的一步；“lightlistbuild 移植”不是本轮前置 |
| D12 | 性能与 A/B 必须使用多灯重叠场景；`HDRPSample01/Assets/OutdoorsScene`（1 盏平行光）不作为结论依据 | 单灯场景下 tile 循环必然不如现有路径，会得出误导结论 |
| D13 | 代码边界：新增 kernel/include/pass 放 URP 包内独立目录；核心修改限于接入点（`ForwardLights`、`UniversalRenderer`、`UniversalRendererData` 及 Editor），逐项列入 §4.6 并审计 | 可整目录回滚；不让 HDRP 代码扩散进 URP 本体 |
| D14 | 不做 HDRP 的 7 张 GBuffer、material feature、面积光、体积雾、物理曝光、SSS、StackLit/Hair/Fabric、HD 阴影 atlas、HD 曝光/Volume | 本轮是“光照架构 + 光列表”，不是“渲染能力对齐” |
| D15 | tile 宽度**不设 gate**：支持 CPU 实际会选择的 8/16/32/64；删除“tile != 16 禁用特性”的支持矩阵行、测试项与风险项 | `maxTileWords=16384`；4K 下 tile16 = 32400 tiles，CPU 必选 32；原 gate 会让特性在 4K 永久关闭 |
| D16 | 新增 **Material Type Mask** 全屏 pass：用 stencil（`MaterialMask`，`Equal` `MaterialLit`/`MaterialSimpleLit`）向 `R8_UInt` 写 0/1/2；compute 读 mask 分流 Lit / SimpleLit / 跳过，不直接读 stencil，不改 GBuffer 布局 | 材质类型在 URP GBuffer 中不存在（HDRP 用 GBuffer `materialFeatures` 解码）；HDRP 的 `Deferred.compute` 虽用 `RenderTextureSubElement.Stencil` 直读 stencil，但其 `only_renderers` **不含 d3d12**；v1 要求 D3D12，故用可移植的 stencil-test mask |

**与上一轮口头估算的差异**：

| 口径 | 估算 | 说明 |
|---|---|---|
| HDRP deferred 能力对齐（上一轮） | 20–30 人周起 | 含面光源、7 GBuffer、material feature、阴影 atlas 等 |
| Clustered Deferred（本轮） | **6–10 人周** + 自定义 shader GBuffer 覆盖；含验收 9–15 | 复用 URP 列表 + 既有每灯 PBR + 间接光已在 GBuffer3 |
| GPU FPTL（可选 follow-up） | +2–4 人周 | 仅在 CPU 构图被证明是瓶颈时 |

**本版复核修正（2026-11-21，对照代码）**：

- **T1 的构建开关不是 `m_Clustering`**。`m_Clustering` 只用于 debug 视图与 Editor 中间纹理（`UniversalRenderer.cs:440`、`:623`）；真正控制聚类构建的是 `ForwardLights.InitParams.forwardPlus`（`UniversalRenderer.cs:245-250` → `ForwardLights.cs:108`）。计划改为在 `InitParams` 新增 `clusteredDeferred`：6 处 gating 中，缓冲创建/`PreSetup`/上传与 `_FPParams*` 改由 `m_UseClusters` 控制（`:140`、`:194`、`:391-410`），而 `_FORWARD_PLUS` 关键字（`:429`）、逐物体索引（`:630+`）与探针 manager（`:143`）保持只由 `m_UseForwardPlus`/`m_PublishProbeAtlas` 控制（详见 §4.6）。
- **补 `isForwardPlus` 审计**。`UniversalRenderPipeline.InitializeRenderingData` 用 `isForwardPlus` 决定附加光阴影是否启用（`:1356`、`:1380`、`:1465`）以及是否生成逐物体灯数据（`:1384`、`:1598-1609`）。Deferred + clustered 下 `isForwardPlus` 为 false：阴影启用需要改用 `ForwardPlus || clusteredDeferred`，而逐物体灯数据必须保持开启（透明仍走 forward）。原计划漏了这一项。
- **本 URP 14.0.12 没有 RenderGraph 版 `UniversalRenderer` 路径**：deferred 只在 `Execute`/`EnqueueDeferred` 里 `EnqueuePass`（`UniversalRenderer.cs:1122`、`:1463-1494`）。删除“RenderGraph 版本”的表述；新 pass 只需 `Configure`/`Execute`。
- **`DeferredPass.cs` 不修改**；pass 二选一发生在 `EnqueueDeferred`。原 §4.6 把它列在“修改”表里但写“不改”，已修正。
- ~~**v1 必须在 clustered 模式跳过 `m_RenderOpaqueForwardOnlyPass`**~~（已被 D16 取代）：保留该 pass，由材质类型 mask 保证 compute 不重复处理；仍强制 `!m_DeferredLights.UseRenderPass`（D10）。
- **验证/测试/清理补全**：原 §7 只有场景与预算；已扩为既有测试、新增测试、player 验证、证据 schema、清理归档、文档一致性六节。
- **估算统一**：核心 6–10 人周；按 30–50% 验收上浮为 **9–15 人周**（原 8–14 与上浮比例不符）。

**本版修订（2026-11-21，第二遍：像素并行 + 材质分类）**：

- **tile 宽度边界（D15）**：按 fork 实际参数，`MAX_VISIBLE_LIGHTS = 256`（桌面）→ `maxTileWords = 4096*4 = 16384`；tile 宽度从 8 起倍增，直到 `tileX*tileY*wordsPerTile <= 16384`（`ForwardLights.cs:247-253`）。实测边界：1080p 且 items（灯+探针）≤64（≤2 words）→ 16；1080p 3+ words → 32；**4K 即使只有 1 盏附加光也必然选 32**（32400 tiles > 16384）；0 盏附加光 → 8。因此“tile 必须 16”是分辨率相关的误关闭，删除该 gate，kernel 改为像素并行 dispatch。
- **材质类型分类（D16）**：stencil deferred 用 stencil bits[5,6] 选择 `_LIT` / `_SIMPLELIT`，并把 Unlit 与 ComplexLit 统一写为 `MaterialUnlit` 后跳过（`GBufferPass.cs:58-61`）。URP 的 GBuffer 不携带材质模型（HDRP 的 `materialFeatures`），而 HDRP compute 的 stencil 直读（`RenderTextureSubElement.Stencil`）不覆盖 d3d12；因此新增一个可移植的全屏 stencil-tested mask pass：mask=1（Lit）、2（SimpleLit）、0（Unlit/ComplexLit/forward-only/天空）。
- **D9 废止**：有了 mask，forward-only 像素在 compute 中 mask=0 被跳过，仍由原 `m_RenderOpaqueForwardOnlyPass`（event `BeforeRenderingOpaques`，实际在 deferred lighting 之后）着色；不会重复受光，也没有“缺 pass 就消失”的内容风险。T6 从“必须补齐 GBuffer”降为“可选优化”。
- **附加方向光遗漏**：Forward+ 的 `LIGHT_LOOP_BEGIN` 在 cluster 索引上加 `URP_FP_DIRECTIONAL_LIGHTS_COUNT`，并在进入 cluster 前**单独循环** 0..`URP_FP_DIRECTIONAL_LIGHTS_COUNT-1` 的附加方向光（`Lighting.hlsl:463-483`）。原 kernel 伪代码只处理主光 + cluster，漏了附加方向光；T3 必须补。
- **SimpleLit 分支**：mask=2 的像素走 `SurfaceDataFromGbuffer` + `LightingLambert`/`LightingSpecular`（与 `StencilDeferred.hlsl:309-318` 一致）；Unlit/ComplexLit 在 stencil deferred 中本就不参与直接光，mask=0 跳过是行为对齐，不是缺失。
- **preflight 产物（2026-11-21）**：`validation/T6-preflight/T6Coverage.json`（8 场景 / 39 个不透明材质；引用中 11 Lit、1 ComplexLit 跳过、无 forward-only）、`T3ShaderCheck.txt`（D3D11）与 `T3ShaderCheck_Direct3D12.txt`（D3D12，compute + mask + resolve 均 0 消息）、`renderdoc-stencil-deferred-baseline.json`（D3D11）与 `renderdoc-stencil-deferred-baseline-d3d12.json` + `clustered-deferred-d3d12-stencil-baseline.rdc`（D3D12：`SslDeferredRenderer`，GBuffer → CopyDepth → Deferred Shading (Stencil) 2 全屏 + 6 灯体积 → Forward Only → Skybox；RT `R11G11B10` + `D32S8`，与 D3D11 结构一致；捕获 7.4 MiB，遵守 1 个 ≤20 MiB 预算）。

**验收摘要**：AC-1…AC-16（§6）。核心：开关关闭逐像素一致；点/聚光/方向光与 stencil deferred 一致；阴影/cookie/layers/mixed/AO 各单项一致；天空不写、无 NaN；forward/透明路径零回归；GBuffer 覆盖清单落盘；compute 在四个桌面 API 编译 0 错误；性能交叉点有数据；`.rdc` 遵守预算。

---

## 1. 目标与非目标

### 1.1 目标

1. 新增 Clustered Deferred 路径：GBuffer 之后一个全屏 compute pass，按 tile 列表循环灯，使用 URP BRDF 写 lighting attachment。
2. 光列表复用 URP Forward+ 的 cluster 构建与缓冲布局；只按需扩展其构建条件，不改变 forward/透明行为。
3. 与现有 stencil deferred 可逐像素 A/B、可运行时回退。
4. 覆盖方向光、点光、聚光；支持阴影、cookie、light layers、mixed lighting/shadowmask、GTAO 直接光 AO。
5. 不改 URP 的 GBuffer 布局、BRDF、光数据表示与阴影 atlas。
6. 提供可复现证据：多灯场景 A/B、每像素灯数分布、性能交叉点、RenderDoc 捕获、回归矩阵。

### 1.2 非目标（本轮明确不做）

| 项 | 原因 | 后续 |
|---|---|---|
| HDRP 面光源（Rectangle/Line/Disc）与 LTC | URP 无对应 BRDF/阴影 | 另立计划 |
| 面光源软阴影、HD 阴影 atlas 重写 | 依赖 HDRP 阴影体系 | 另立计划 |
| HDRP 7 张 GBuffer + material feature（StackLit/Hair/Fabric/Eye） | 会破坏现有 SSL/GTAO/SSR 布局 | 不计划 |
| 体积雾、物理曝光、Volume 框架、SSS | 与光照架构正交 | 另立计划 |
| GPU FPTL（big-tile 64 + 细剔除 + 分类列表） | 先复用 URP 列表；CPU 构图证明瓶颈后再做 | follow-up |
| MSAA | deferred 本身不支持（`UniversalRenderer.cs:367`） | 不计划 |
| native render pass（`_RENDER_PASS_ENABLED`） | v1 降低分支与附件绑定复杂度 | follow-up |
| XR / foveated rendering | cluster 缓冲已有 eye index，但验证面大 | follow-up |
| GLES/WebGL | 无 Compute 或格式不保证 | 不计划 |
| 透明物体的 deferred 光照 | URP/HDRP 一致：透明走 forward；本轮保持 forward | 不计划 |
| 动态分辨率 / 半分辨率 deferred | 会改变 A/B 与 tile 语义 | follow-up |

### 1.3 成功标准

- 开启后：多灯场景的 GPU 时间低于 stencil deferred，且与 forward 的光照结果在可接受阈值内一致。
- 关闭后：与基线逐像素一致，不需要重编 shader、不需重建 renderer。
- 边界：透明/forward-only 的既有行为不变；GBuffer/SSGI/SSR/GTAO 绑定不变；无 NaN/闪烁/漏光。
- 工程：四个桌面 API 编译通过；既有 EditMode/PlayMode 全绿；变体数量可报告。

---

## 2. 调研：light culling 与 deferred 的关系

### 2.1 HDRP 的证据链

| 结论 | 证据 |
|---|---|
| forward opaque 用 FPTL 列表 | `Runtime/Material/Lit/Lit.shader:893`；透明在 `:806` 用 clustered |
| deferred 也用同一张 FPTL 表 | `Runtime/Lighting/LightLoop/DeferredTile.shader:46`、`:319` |
| PS 每像素读 tile 列表 | `Runtime/Lighting/LightLoop/LightLoop.hlsl:259-322`；环境光列表 `:336-349` |
| wave 标量化只对 forward 开 | `LightLoop.hlsl:4-8` |
| tile header 低 16 位是灯数，索引 16bit 打包 | `LightLoopDef.hlsl:227-239`、`:241-258` |
| 列表由 GPU 构建：big-tile 64 → tile 16 | `Runtime/Lighting/LightLoop/lightlistbuild.compute:211-237`；`LightLoop.cs:2063-2075` 的 dispatch 备注 |
| 分类、feature flags、每 tile 上限 | `lightlistbuild.compute:276-306`、`:325-342`；`LightLoop.cs.hlsl` 的 `TILE_SIZE_FPTL=16`、`TILE_SIZE_BIG_TILE=64`、`LIGHTCATEGORY_*` |

### 2.2 HDRP FPTL 算法拆解（仅供借鉴，不在本轮移植）

1. 每 64×64 big tile 先做粗列表（`USE_TWO_PASS_TILED_LIGHTING`，`lightlistbuild.compute:211-237`）。
2. 每 16×16 tile 读取深度并求 min/max（`:180-202`）。
3. 用屏幕空间 AABB 与 tile 边界做粗筛（`:226-237`）。
4. 球体与 tile 相交测试（`:348-388`）。
5. 用 tile 内实际像素深度做 fine prune，位图记录“任一像素命中”（`:457-592`）。
6. 按 `LIGHTCATEGORY_PUNCTUAL/AREA/ENV/DECAL` 分桶（`:276-285`、`:317-342`）。
7. 生成每 tile feature flags，供材质/着色快速拒绝（`:293-306`）。
8. 输出 header + 16bit 灯索引（`:325-342`）；PS 端 `FetchIndex` 读取。

### 2.3 URP 14 Forward+ 现状（本仓库 fork）

| 项 | 实现 |
|---|---|
| 列表构建 | CPU Burst jobs：`Runtime/ForwardLights.cs` + `Runtime/Tiling/*`（`LightMinMaxZJob`、`ZBinningJob`、`TileRangeExpansionJob`） |
| tile 尺寸 | 从 8 开始倍增，直到 `maxTileWords` 装得下（`ForwardLights.cs:247-253`） |
| zbin | 透视用 `log2(z)`，正交用线性 z（`ForwardLights.cs:255-275`） |
| 上传 | `URP_ZBinBuffer` / `urp_TileBuffer` 常量缓冲 + `_FPParams0/1/2`（`:391-410`） |
| 关键字 | `_FORWARD_PLUS` 在 `ForwardLights.cs:429` 设置 |
| 灯数据 | `_AdditionalLightsPosition/Color/Attenuation/SpotDir/LayerMasks` 常量数组（`:560-620`；`Input.hlsl:151-157`） |
| 每 tile 上限 | `MAX_LIGHTS_PER_TILE`：`MAX_VISIBLE_LIGHTS<=32` 时为 32，否则等于 `MAX_VISIBLE_LIGHTS`（`Input.hlsl:26-38`） |
| 灯循环 | `LIGHT_LOOP_BEGIN` = `ClusterInit(uv, posWS, 0)` + `ClusterNext`（`RealtimeLights.hlsl:36-42`） |
| 全局索引 | Forward+ 下 `GetAdditionalLight(i)` 直接用全局索引；方向光先循环（`RealtimeLights.hlsl:249-280`；`Lighting.hlsl:464`） |

### 2.4 两种列表的结构差异

| 维度 | HDRP FPTL | URP 14 Forward+ cluster |
|---|---|---|
| tile | 固定 16×16 + 64×64 big tile | 自适应 8/16/32…（本仓库常见 16） |
| 深度 | 构建时读 depth 求 tile min/max；逐像素细剔除 | 构建时按灯的 z 范围分 bin；像素按自身深度选 bin |
| 数据 | header + 16bit 索引数组（每类一份） | tile 位图 ∧ zbin 位图；位掩码迭代 |
| 剔除质量 | 更紧（能砍掉“只穿过 tile 空白”的灯） | zbin 粒度较粗，但无需构建时读深度 |
| 构建 | GPU compute | CPU Burst + 常量化上传 |
| 每 tile 上限 | 编译期 `SHADEROPTIONS_FPTLMAX_LIGHT_COUNT` | 32 或 `MAX_VISIBLE_LIGHTS` |
| 额外能力 | 分类列表、feature flags、big tile 粗筛 | 探针列表共用同一结构 |

**结论**：FPTL 的“更强剔除”是真实的，但在 forward 里 PS 循环次数仍是 tile 级；相对 URP Forward+，其边际收益主要是“GPU 构图 + 粗筛 + 更紧的 tile 深度”，不是“能不能用列表”。因此本轮先复用 URP 列表是正确的成本/收益选择。

### 2.5 何时有收益、何时是负收益

有收益：

- 可见灯多，且大面积表面（地面、墙）与小范围灯重叠；
- 大量灯在屏幕上占用小面积，per-object 列表/全屏每灯 pass 浪费明显；
- 材质重、overdraw 高，deferred 的单次着色同时摊薄两者。

负收益：

- 灯少（少量点光/聚光、无重叠）；
- 灯普遍覆盖大半屏（大半径、方向光主导）；
- 分辨率/灯数导致 tile 位宽被迫增大、带宽/构图成本超过着色节省；
- 透明为主（透明不走 deferred，tile 列表帮不上）。

因此验收必须给出“交叉点”，而不是只报一个更快/更慢的数字。

---

## 3. 本仓库可复用资产

### 3.1 compute-safe cluster 迭代器

`Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ScreenSpaceLighting/ReflectionProbeFallback.hlsl`：

- `:9-20` 明确说明为什么不能 include `Input.hlsl`，以及 compute 没有 per-draw 常量；
- `:32-60` 复制 cbuffer 布局（`URP_ZBinBuffer`、`urp_TileBuffer`、`_FPParams*`）；
- `:100-155` `SslProbeClusterInit/Next` 与 `Clustering.hlsl` 同构；
- `:126-128` 注释：header 0 是 punctual 灯列表，header 1 是探针。

本轮做法：新建 **独立的** `ClusteredDeferred.hlsl`，实现 header 0 的灯迭代器与灯数据读取；不修改/不依赖该 fallback 文件，避免 SSGI 功能被牵连。

### 3.2 每灯 PBR 与 GBuffer 解码

`Shaders/Utils/StencilDeferred.hlsl`：

- `:133-211` `GetStencilLight`：由逐灯 uniform 构造 `Light`；
- `:213-321` `DeferredShading`：深度→`posWS`（`:274`）、GBuffer 采样（`:231-247`）、`InputDataFromGbufferAndWorldPosition`（`:298`）、`BRDFDataFromGbuffer` + `LightingPhysicallyBased`（`:307-308`）。

本轮只需把 `GetStencilLight`（uniform）替换为 `GetAdditionalLight(globalIndex)`，外面套 tile 循环。

### 3.3 间接光的位置（重要）

- `Shaders/LitGBufferPass.hlsl:213-226`：GBuffer pass 计算 `GlobalIllumination(...)` 与 emission，写入 GBuffer3。
- `ShaderLibrary/UnityGBuffer.hlsl:277`：注释明确 `bakedGI` 在 lighting pass 不可用，因为已在 GBuffer3。
- 因此 kernel **不采样探针、不算 GI/SSGI**；只做直接光并 additive 写回。SSGI/SSR/GTAO 保持现有 pass 与绑定不变。

结论：`UniversalFragmentPBRSplit`（`ShaderLibrary/Lighting.hlsl:388`）本轮**不需要**在 kernel 中使用；它是给透射透明用的，且依赖 per-draw state。

### 3.4 灯数据表示

- `Runtime/RenderingUtils.cs:74-87`：`useStructuredBuffer` 硬编码为 `false`，灯数据走常量数组。
- `ShaderLibrary/Input.hlsl:151-157`：`_AdditionalLightsPosition/Color/Attenuation/SpotDir/OcclusionProbes/LayerMasks`。
- `RealtimeLights.hlsl:153-187`：`GetAdditionalPerObjectLight` 是纯函数（距离/角度衰减），可直接复制到 compute-safe include。
- `RealtimeLights.hlsl:249-280`：Forward+ 下 `lightIndex` 即全局索引；`AdditionalLightShadow(lightIndex, ...)` 与 cookie 也按该索引。

本 fork 的 `useStructuredBuffer=false` 意味着：**v1 只支持常量数组路径**；`USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA` 分支不实现，平台 gate 写明。

### 3.5 阴影

- 主光：`_MainLightShadowmapTexture`、`_MainLightWorldToShadow`、cascade split、`_MainLightShadowParams`，以及 screen-space shadow（`SCREENSPACE_TEXTURE(_ScreenSpaceShadowmapTexture)`）。
- 附加光：`_AdditionalLightsShadowmapTexture` 是单张 atlas 切槽（`Runtime/Passes/AdditionalLightsShadowCasterPass.cs:833`）；槽位/分辨率是真正的灯数上限。
- 采样函数：`MainLightShadow`、`AdditionalLightShadow`，与 forward PS 共用同一套全局。
- 计算里需要复制 `ShadowSamplingTent.hlsl` 依赖与 PCF 关键字（`_SHADOWS_SOFT*`）。

### 3.6 stencil 的现状与最小化

- `Runtime/StencilUsage.cs:4-16`：bit[0,3] 用户、bit4 `StencilLight`、bit[5,6] material type。
- GBuffer 写材质位（`Runtime/Passes/GBufferPass.cs:58-61`）；forward-only 用 ref（`Runtime/UniversalRenderer.cs:297-304`）。
- 现有 stencil deferred 的每灯 ref/read/write mask（`Runtime/DeferredLights.cs:1018-1035`）用于把全屏 pass 裁到灯体积。

本设计下：

- **不需要 light-shape stencil**；
- GBuffer 的材质位继续写（成本不变，兼容其他特性）；
- v1 **需要读材质类型**（Lit / SimpleLit / 跳过）。HDRP 的做法是在 compute 里用 `RenderTextureSubElement.Stencil` 直读 stencil（`Deferred.compute:100,159`），但其 `only_renderers` 不含 d3d12；URP v1 要求 D3D12，因此改为在 GBuffer 之后跑一个可移植的全屏 stencil-tested mask pass：`ReadMask=MaterialMask`、`Comp=Equal`、`Ref=MaterialLit`（写 1）/`Ref=MaterialSimpleLit`（写 2），目标 `R8_UInt` 先清 0；`WriteMask=0` 不改 stencil。Unlit/ComplexLit 写的是 `MaterialUnlit`（`GBufferPass.cs:61`），forward-only 的 ref 也含 `MaterialUnlit`，两者都保持 0，与 stencil deferred 的跳过语义一致。stencil 直读保留为 D3D11/Vulkan/Metal 的后续优化。
- mask pass 机制已有先例：`DeferredLights.ClearStencilPartial` 就是用全屏 mesh + stencil 状态操作 depth-stencil（`DeferredLights.cs:555-563`）。

### 3.7 deferred 的既有前提与约束

- MSAA：deferred 不支持（`UniversalRenderer.cs:367`）。
- 深度：deferred 有 GBuffer depth + copy；compute 读 `_CameraDepthTexture`。
- 可见灯上限：deferred 当前 `maximumVisibleLights = 0xFFFF`（`UniversalRenderer.cs:1432`），但灯数组长度为 `MAX_VISIBLE_LIGHTS`；启用本路径时必须收紧到 Forward+ 的 `maxVisibleAdditionalLights`，否则数组越界/静默截断。
- native render pass：v1 关闭（D10），走纹理采样路径。
- 现有透明/forward-only 路径：`SetupPerObjectLightIndices` 在非 Forward+ 下继续工作（`ForwardLights.cs:630+`），本轮保持不变。

---

## 4. 迁移架构

### 4.1 接入方式

- 新字段：`UniversalRendererData.m_ClusteredDeferredLighting`（默认 `false`），Editor 中仅在 Rendering Mode = Deferred 时显示。
- 构造期：
  - `ForwardLights.InitParams` 新增 `clusteredDeferred`；`UniversalRenderer` 在 `renderingMode == Deferred && m_ClusteredDeferredLighting` 时置真。
  - `m_Clustering` 改为 `ForwardPlus || clusteredDeferred`，但**只**用于 debug 视图与 Editor 中间纹理（`UniversalRenderer.cs:440`、`:623`），不参与聚类构建。
  - 创建 `ClusteredDeferredPass`；仍复用 `DeferredLights` 的 GBuffer 附件、depth、`_GBuffer0..3` 全局绑定与下游合成。
- 执行期：`EnqueueDeferred`（`UniversalRenderer.cs:1463-1494`，本版本无 RenderGraph 路径）里：
  - `m_DeferredLights.Setup(...)` 照常执行；
  - `m_GBufferPass` → `m_GBufferCopyDepthPass` 照常；
  - 在 `EnqueuePass(m_DeferredPass)` 处二选一：clustered 时 `EnqueuePass(m_ClusteredDeferredPass)`；
  - 在 GBuffer 之后、compute 之前 enqueue **Material Type Mask** pass（新）；
  - **保留 `EnqueuePass(m_RenderOpaqueForwardOnlyPass)`**（D9 已废止）；其像素 mask=0，compute 不处理；
  - 关闭开关时完全走原 `DeferredPass`，逐像素一致（AC-1）。
- 关键字：**不设置 `_FORWARD_PLUS`**（`ForwardLights.cs:429` 仍只由 `m_UseForwardPlus` 控制）；透明/forward-only 继续使用逐物体灯索引（`SetupPerObjectLightIndices` 仍执行，`ForwardLights.cs:630+`）。compute 只复用 cluster 缓冲与数据布局。
- native render pass：本帧强制 `useRenderPassEnabled = false` 且 `m_DeferredLights.UseRenderPass = false`，并输出一次告警（D10）。仓库内 renderer 资产当前全部为 `m_UseNativeRenderPass: 0`，D3D12 也被硬排除；Vulkan/Metal 只有在资产显式开启时才命中该路径，强制关闭后功能仍可用。

### 4.2 帧序（新增部分）

```
GBuffer Pass（写 GBuffer0-2、GBuffer3=GI+emission、depth、材质 stencil 位）
  ↓
CopyDepth / DeferredLights 绑定 _GBuffer0..2、_CameraDepthTexture
  ↓
Material Type Mask（新）：2 个全屏 stencil-tested draw → R8_UInt（0=跳过，1=Lit，2=SimpleLit）
  ↓
Clustered Deferred Compute（新）：
  视口像素并行 dispatch（ceil(viewport/16)）→ 读 depth/GBuffer/mask → 主光 + 附加方向光 + cluster 列表 → 直接光写 RGBA16F UAV
  ↓
Clustered Deferred Resolve（新）：全屏 pass，Blend One One, Zero One 把 lighting UAV 加进 GBuffer3
  ↓
（原有下游：copy lighting 到 camera color / fog / 透明 forward / 后处理）
  ↓
Forward-only opaque（原有 m_RenderOpaqueForwardOnlyPass，保留；event BeforeRenderingOpaques，实际排在 deferred lighting 之后）
```

补充：

- 这是本版本 `UniversalRenderer` 的实际执行路径（legacy `ScriptableRenderPass`/`EnqueuePass`；无 RenderGraph 版本）。`m_DeferredLights.Setup(...)` 仍必须调用，否则 `_GBuffer0..2` 与附件绑定不成立。
- 原 `m_RenderOpaqueForwardOnlyPass` 的 event 是 `BeforeRenderingOpaques`，实际排在 deferred lighting 之后；本模式**保留 enqueue**，其像素在材质类型 mask 中为 0，compute 不处理，避免重复受光与“缺 GBuffer 就消失”。
- `_CameraDepthTexture` 的创建条件包含 `renderingMode == Deferred && !useRenderPassEnabled`（`UniversalRenderer.cs:790-791`）；v1 强制 `!UseRenderPass`，因此该纹理可用。
- 透明：在 deferred 之后照常走 forward，灯数据仍是逐物体（因为 `_FORWARD_PLUS` 未开）。

### 4.3 附件、混合与 alpha 语义

- 目标：与 `DeferredPass.Configure` 相同——lighting attachment（`GBuffer3`）+ depth（`Runtime/Passes/DeferredPass.cs:33-42`）。
- **compute 不能混合**：kernel 写 `_CdLightingTextureRW`（`RGBA16F` UAV，每像素都写，mask/sky 写 0，无需单独 clear）；随后 `ClusteredDeferredResolve` 全屏 pass 采样该纹理，以 `Blend One One, Zero One` 加到 GBuffer3，与 stencil deferred 的 punctual pass 一致（`Shaders/Utils/StencilDeferred.shader:158`）；alpha 不被 resolve 改写。
- 不写 stencil；不改变材质位。
- 天空/背景与 mask=0：depth 达到 far 或材质类型为 0 的像素写 0，不产生光照（与 `DeferredShading` 的 early-out 语义一致，见 `StencilDeferred.hlsl:256-260` 的 subtractive 早退）。

### 4.4 kernel 伪代码

```
// T3 形状：无阴影/cookie/layer/AO；T4 在此基础上给 CdMainLight/CdGetAdditionalLight/CdShade
// 加 shadowMask、ao、meshLayers 参数。
[numthreads(16,16,1)] // 视口像素并行：一个线程组 = 16×16 像素，不绑定 tile
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 pixel = id.xy;                         // dispatch = ceil(viewport/16)
    if (any(pixel >= (uint2)_ScreenSize.xy)) return;

    uint materialType = LOAD_TEXTURE2D(_MaterialTypeMask, pixel).r; // 0=skip 1=Lit 2=SimpleLit
    if (materialType == 0) { _CdLightingTextureRW[pixel] = 0; return; }

    float rawDepth = LOAD_TEXTURE2D(_CameraDepthTexture, pixel).x;
    if (rawDepth >= farValue) { _CdLightingTextureRW[pixel] = 0; return; } // sky

    float2 uv = (pixel + 0.5) * _ScreenSize.zw;
    float3 posWS = ComputeWorldSpacePosition(uv, rawDepth, unity_MatrixInvVP);

    half4 g0 = LOAD_TEXTURE2D(_GBuffer0, pixel);
    half4 g1 = LOAD_TEXTURE2D(_GBuffer1, pixel);
    half4 g2 = LOAD_TEXTURE2D(_GBuffer2, pixel);
    half4 shadowMask = LOAD_SHADOWMASK(pixel);          // 仅 mixed lighting
    uint meshLayers  = LOAD_RENDERING_LAYERS(pixel);    // 仅 _LIGHT_LAYERS

    CdInputData inputData = CdInputDataFromGbufferAndWorldPosition(g2, posWS);
    inputData.normalizedScreenSpaceUV = uv;

    half3 color = 0;
    // 1) 主光
    CdLight mainLight = CdMainLight();
    color += CdShade(materialType, g0, g1, g2, mainLight, inputData);

    // 2) 附加方向光：Forward+ 在 cluster 之前单独循环（Lighting.hlsl:463-483）
    for (uint i = 0; i < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, CD_MAX_VISIBLE_LIGHTS); i++) {
        CdLight l = CdGetAdditionalLight(i, posWS);
        color += CdShade(materialType, g0, g1, g2, l, inputData);
    }

    // 3) cluster 列表（header 0）；cluster 索引从 punctual 段开始，需加方向光数量
    CdClusterIterator it = CdClusterInit(uv, posWS);
    uint idx;
    while (CdClusterNext(it, idx)) {
        idx += URP_FP_DIRECTIONAL_LIGHTS_COUNT;
        CdLight l = CdGetAdditionalLight(idx, posWS);
        color += CdShade(materialType, g0, g1, g2, l, inputData);
    }

    // 4) 只写直接光到 UAV；resolve pass 再与 GBuffer3 中原有 GI 相加
    _CdLightingTextureRW[pixel] = float4(color, 1.0);
}
```

说明：

- **像素并行**：`pixel = SV_DispatchThreadID`，dispatch 组数 = `ceil(viewport/16)`；tile id 由 `ClusterInit` 用每像素 UV × `_FPParams1.xy`（tile 计数）自行推导。tile 宽度 8/16/32/64 都不需要 kernel 感知，因此没有 tile 宽度 gate。
- 16×16 block 在 tile ≥16 时必然整体落在一个 tile 内（tile mask 读取同址）；tile=8 只在 0 盏附加光时出现，mask 全 0、kernel 不产生光照。
- **材质类型**：`_MaterialTypeMask` 为 `R8_UInt`；`CdShade` 按 1/2 分支：1 走 `BRDFDataFromGbuffer` + `LightingPhysicallyBased`，2 走 `SurfaceDataFromGbuffer` + `LightingLambert`/`LightingSpecular`（对齐 `StencilDeferred.hlsl:300-318`）。0 直接返回，不改写 lighting。
- **附加方向光**：必须单独循环 0..`URP_FP_DIRECTIONAL_LIGHTS_COUNT-1`；cluster 迭代返回的是 punctual 段的相对索引，取值前加 `URP_FP_DIRECTIONAL_LIGHTS_COUNT`。漏掉任一步都会与 Forward+ 不一致。
- 屏幕尺寸用全局 `_ScreenSize`（`ScriptableRenderer.cs:267` 上传）；`unity_MatrixInvVP` 在 compute 中可用（SSL 已这么做，`ScreenSpaceLightingCommon.hlsl:89`）；v1 非 XR，用 `LOAD_TEXTURE2D` 而非 `_X`。
- 输出写 `_CdLightingTextureRW`（`RGBA16F` UAV）；resolve 全屏 pass 完成加法；kernel 不读 lighting attachment。
- 深度转换复用 `ComputeWorldSpacePosition` + `_ZBufferParams`/`LinearEyeDepth`，保留 reversed-Z 与正交分支。
- `URP_FP_DIRECTIONAL_LIGHTS_COUNT` 来自 `_FPParams0.w`；header 0 的最大实体索引是 `URP_FP_PROBES_BEGIN - 1`。偏移与 off-by-one 由 T2 的 parity 测试锁定。
- 材质 flag 与 stencil deferred 对齐：`kMaterialFlagSpecularHighlightsOff`（`:305`）、`kMaterialFlagReceiveShadowsOff`（`:137`）、`kMaterialFlagSubtractiveMixedLighting`（`:256-260` 的早退）。
- 关键字集合收敛为：`_MAIN_LIGHT_SHADOWS*`、`_ADDITIONAL_LIGHT_SHADOWS`、`_SHADOWS_SOFT*`、`_LIGHT_LAYERS`、`_LIGHT_COOKIES`、`_SCREEN_SPACE_OCCLUSION`、`_DEFERRED_MIXED_LIGHTING`、`SHADOWS_SHADOWMASK`、`_GBUFFER_NORMALS_OCT`；不编 `_ADDITIONAL_LIGHTS_VERTEX`、`_SURFACE_TYPE_TRANSPARENT`、foveated 组合。
- AO 只乘到直接光；不实现 `_DEFERRED_FIRST_LIGHT` 的 alpha 技巧（该技巧只服务 stencil deferred 的 baked occlusion 抵消）。
- 每 tile 循环上限以 `MAX_LIGHTS_PER_TILE` 与位图容量为准；不要用固定 `for(i<32)` 假设。
- `CdInputDataFromGbufferAndWorldPosition` 与 `InputDataFromGbufferAndWorldPosition` 同构，但不依赖 `Input.hlsl` 的 `InputData`；`normalizedScreenSpaceUV` 必须手工补。
- **不做** tile 宽度 gate；若未来 CPU 选到 8（0 灯）或 64（极多灯/高分辨率），kernel 行为不变。

### 4.5 compute-safe include 边界

新增 `ShaderLibrary/ClusteredDeferred.hlsl`，只包含：

- `URP_ZBins`/`urp_Tiles`/`_FPParams*` 的 cbuffer 声明与 `CdClusterInit/Next`；容量常量与 `Input.hlsl` 一致（`MAX_ZBIN_VEC4S=1024`；`MAX_TILE_VEC4S` 按 `MAX_VISIBLE_LIGHTS<=32` 取 1024/4096）；
- `_AdditionalLights*` 常量数组（长度 `MAX_VISIBLE_LIGHTS`）、`DistanceAttenuation`/`AngleAttenuation`、`CdGetAdditionalLight`；
- `URP_FP_ZBIN_SCALE/OFFSET`、`URP_FP_TILE_SCALE/COUNT`、`URP_FP_WORDS_PER_TILE`、`URP_FP_DIRECTIONAL_LIGHTS_COUNT` 的宏（值来自 `_FPParams0/1/2`，与 `Input.hlsl:121-136` 一致）；
- 主光/附加光阴影采样所需的最小全局与 PCF 函数（从 `Shadows.hlsl` 复制必要片段，T4）；
- GBuffer 解码的纯函数：`CdBRDFDataFromGbuffer`、`CdSurfaceDataFromGbuffer`（SimpleLit）、`CdUnpackNormal`、`CdInputDataFromGbufferAndWorldPosition`（补 `normalizedScreenSpaceUV`）；
- 光照求值：复制 `LightingPhysicallyBased`（Lit）与 `LightingLambert`/`LightingSpecular`（SimpleLit）；可 include `BRDF.hlsl`（只拉 core `BSDF.hlsl`/`CommonMaterial.hlsl` 与 `SurfaceData.hlsl`，无 per-draw 依赖）；
- 材质类型 mask 纹理 `_MaterialTypeMask`（`R8_UInt`，0/1/2）；
- GTAO 直接光 AO 的唯一函数（`_SCREEN_SPACE_OCCLUSION`，T4）。

不得 include：URP 的 `ShaderLibrary/Input.hlsl`、`ShaderLibrary/Lighting.hlsl`、`ShaderLibrary/Core.hlsl` 与 `Shadows.hlsl` 整体（均拉 per-draw state）；允许 include core 包的 `Common.hlsl`/`ShadowSamplingTent.hlsl` 等无 per-draw 依赖的头。`Light`/`InputData` 不要从 `RealtimeLights.hlsl`/`Input.hlsl` 取，在 include 内定义 `CdLight`/`CdInputData`。

### 4.6 新增 / 修改文件

新增（URP 包内）：

| 文件 | 作用 |
|---|---|
| `Runtime/ClusteredDeferred/ClusteredDeferredPass.cs` | `ScriptableRenderPass`；分配/管理 `R8_UInt` mask 与 `RGBA16F` lighting RT；mask pass、dispatch、resolve 三步；`Configure`/`Execute`（本版本无 RenderGraph 路径） |
| `Runtime/ClusteredDeferred/ClusteredDeferredResources.cs` | kernel/材质/参数 id、每相机状态（若需要） |
| `Shaders/Utils/ClusteredDeferred.compute` | kernel + 关键字集合（像素并行 dispatch，写 RGBA16F UAV） |
| `Shaders/Utils/ClusteredDeferredMask.shader` | 2 个全屏 stencil-tested pass，向 `R8_UInt` 写 1（Lit）/2（SimpleLit）；`WriteMask=0` |
| `Shaders/Utils/ClusteredDeferredResolve.shader` | 全屏采样 `_CdLightingTexture`，`Blend One One, Zero One` 加进 GBuffer3 |
| `ShaderLibrary/ClusteredDeferred.hlsl` | compute-safe 子集（§4.5） |
| `Tests/.../ClusteredDeferred*` | 编译检查、矩阵测试（§7） |

修改（限定接入点，逐项审计）：

| 文件 | 改动 |
|---|---|
| `Runtime/UniversalRendererData.cs` | 新增 `[SerializeField] bool m_ClusteredDeferredLighting`（默认 false，旧资产无需迁移）；`OnValidate` 断言仅 Deferred 有效 |
| `Editor/UniversalRendererDataEditor.cs` | 开关 UI；Rendering Mode != Deferred 时禁用并提示 |
| `Runtime/UniversalRenderer.cs` | (1) `forwardInitParams.clusteredDeferred = ...`；(2) `m_Clustering = ForwardPlus || clustered`（只影响 debug/中间纹理）；(3) `EnqueueDeferred` 二选一 pass，并在 GBuffer 之后 enqueue mask pass；(4) **保留** `m_RenderOpaqueForwardOnlyPass`；(5) `SetupCullingParameters` 收紧 `maximumVisibleLights`（当前 Deferred 为 `0xFFFF`，`:1432`）；(6) 强制 `!m_DeferredLights.UseRenderPass`；(7) 新 pass 带 `ProfilingSampler`，便于 Frame Debugger/Profiler 取证 |
| `Runtime/ForwardLights.cs` | `InitParams` 新增 `clusteredDeferred`；`m_UseClusters = m_UseForwardPlus || m_ClusteredDeferred`，用于缓冲创建（`:140`）、`PreSetup` 构图（`:194`）、jobs complete/上传/`_FPParams*`（`:391-410`）；**`_FORWARD_PLUS`（`:429`）与 `SetupPerObjectLightIndices`（`:630+`）仍只由 `m_UseForwardPlus` 控制** |
| `Runtime/UniversalRenderPipeline.cs` | 新增 `isClusteredLighting`：附加光阴影启用（`:1356`、`:1380`、`:1465`）使用 `ForwardPlus || clusteredDeferred`；逐物体灯 flags（`:1384`、`:1598-1609`）仍只用 `isForwardPlus` |

不在本轮修改：`Runtime/Passes/DeferredPass.cs`（原路径保留）、`Runtime/DeferredLights.cs` 的 stencil 材质/每灯 pass、`ShaderLibrary/UnityGBuffer.hlsl`、`ShaderLibrary/Lighting.hlsl`、`ShaderLibrary/RealtimeLights.hlsl`、`ShaderLibrary/Shadows.hlsl`、GBuffer 布局与格式。

### 4.7 v1 支持矩阵

| 项 | v1 | 说明 |
|---|---|---|
| Rendering Mode | Deferred + 开关 | 开关默认关 |
| 灯型 | Directional / Point / Spot | Area 不支持 |
| 阴影 | 主光 cascade、screen-space shadow、附加光 atlas | 受 atlas 槽位限制 |
| Light cookies | 支持 | 走 `_LIGHT_COOKIES` |
| Light layers | 支持 | 读 `GBUFFER_LIGHT_LAYERS` |
| Mixed lighting / shadowmask | 支持 | 读 `GBUFFER_SHADOWMASK`，沿用 `_DEFERRED_MIXED_LIGHTING` 语义 |
| GTAO 直接光 AO | 支持 | 采样 `_ScreenSpaceOcclusionTexture` |
| SSGI/SSR/折射 | 不参与 kernel | 保持 GBuffer pass / 现有 pass |
| MSAA | 不支持 | deferred 已禁用 |
| native render pass | 命中时强制关闭并告警 | 本帧 `useRenderPassEnabled=false`；仓库资产默认关闭，D3D12 硬排除；Vulkan/Metal 显式开启时仍可用（回退到普通 RT 绑定） |
| XR / foveated | 不支持 | follow-up |
| 正交相机 | 支持（zbin 有线性分支） | 纳入 AC |
| GLES/WebGL | 不支持 | 平台 gate |
| Preview / Overlay 相机 | 回退 stencil deferred | 预览/Overlay 的灯数据与附件组合未验证；不猜测，直接走原路径 |
| 多相机 | 支持，顺序执行 | 聚类缓冲是全局单份；依赖每相机 `PreSetup` 重新构建；若 Overlay 相机不构建则回退并告警（T5 验证） |
| tile 宽度 8/16/32/64 | 支持（像素并行） | 无 gate；tile id 由每像素 UV × `_FPParams1.xy` 推导 |
| 材质类型 | Lit / SimpleLit 参与直接光；Unlit / ComplexLit 跳过 | mask 0/1/2；与 stencil deferred 的 stencil 分流一致 |
| `useStructuredBuffer == true` | 不支持 | 本 fork 恒为 false（`RenderingUtils.cs:74-87`）；启动时断言 |
| Forward-only opaque | **支持**（由原 forward-only pass 着色） | D9 已废止；mask=0，compute 跳过，不重复受光 |
| 材质类型 mask | 必需 | `R8_UInt`；GBuffer 之后 2 个全屏 stencil-tested draw；v1 增加约 0.1ms/mp 级固定开销 |
| 深度纹理 | 必需 | v1 依赖 `!UseRenderPass` 下的 `_CameraDepthTexture` |

### 4.8 与 stencil deferred 的已知差异

1. 灯体积光栅化与 early-z 拒绝被移除；改为每像素列表循环。灯少/大体积时可能更慢。
2. 每像素读取 tile 列表（常量缓冲位图）与计算 tile/zbin 索引，存在固定开销。
3. forward-only opaque **支持**：仍由原 forward-only pass（在 deferred lighting 之后）着色；compute 通过 mask=0 跳过，不重复受光。缺 GBuffer 只意味着该物体不享受 clustered deferred，不是渲染缺失。
4. **stencil 只写不新增**：GBuffer pass 仍写材质位（`GBufferPass.cs:58-61`）；本路径额外用 2 个全屏 stencil-tested draw 把材质类型转成 mask，不改 stencil、不写 `StencilLight`。依赖 `StencilLight` 位的自定义效果会看到 0，需审计并记入 KnownDifferences。
5. 混合光照的逐灯早退（`_LightFlags`/subtractive）以 per-pixel 方式处理；`_RECEIVE_SHADOWS_OFF` 需在 kernel 内按材质 flag 跳过阴影；语义对齐写入 KnownDifferences。
6. 浮点累加顺序不同：stencil deferred 是每灯单独 pass 的加法，kernel 是循环后一次加法；阈值要允许极小差异，不要求 bit-exact（AC-4/AC-5）。
7. alpha 不改写（`Blend ... Zero One`），与现有 AO/depth 语义一致；若未来有 pass 依赖 lighting alpha 需另立任务。
8. 新增 1 张 `R8_UInt` mask、1 张 `RGBA16F` lighting、2 个全屏 draw（mask）+ 1 个全屏 resolve；mask/lighting 与 compute 之间各有一次读写依赖，暂不并入同一 native RP。
9. Unlit / ComplexLit 不参与直接光（与 stencil deferred 一致：ComplexLit 在 `GBufferPass.cs:61` 被写成 `MaterialUnlit`）。
10. compute 输出经 `RGBA16F` 中转再 resolve，比 stencil deferred 多一次全屏采样/写；这是 compute 无固定功能混合的代价。若后续实测 resolve 成为瓶颈，可改为单次全屏 PS（同一套灯循环，`SV_Target` 直接 Blend），但仍保持 cluster/GBuffer 数据布局不变。

### 4.9 与 HDRP 14.0.12 的对照与定稿

参照 `high-definition@14.0.12/Runtime/Lighting/LightLoop/{DeferredTile.shader, Deferred.compute, LightLoop.hlsl}` 与 `RenderPipeline/HDStencilUsage.cs`。

| 维度 | HDRP 14.0.12 | 本方案 | 定稿 |
|---|---|---|---|
| deferred 着色 | 默认全屏 PS（`DeferredTile.shader:402`，`Blend Off`，输出**总光照**）；可选 compute（`Deferred.compute`） | compute 只算直接光；UAV + resolve 加法写 GBuffer3 | 保持。URP 的 GI 在 GBuffer3，HDRP 在 deferred pass 内算总光照，所以 HDRP 能 `Blend Off`/UAV 直写，URP 必须加法合成 |
| 像素分类 | stencil 只查 `RequiresDeferredLighting`（`HDStencilUsage.cs:15`）；材质模型来自 GBuffer `materialFeatures` | mask 0/1/2 分流 Lit/SimpleLit/跳过 | 保持 mask。HDRP 的 compute 直读 stencil（`Deferred.compute:100,159`），但 `only_renderers` 不含 d3d12（`:94`）；v1 要求 D3D12，直读降为后续优化 |
| 列表构建 | GPU compute，固定 16×16 + 64 big tile，tile feature flags，`builddispatchindirect` 只 dispatch 非空 tile | CPU Burst zbin/tile 位图，全屏 dispatch | 保持 v1；GPU FPTL/indirect 为 D11 follow-up |
| compute 线程映射 | 固定 16 tile，4 个 8×8 组/tile（`Deferred.compute:104`），组内共享 tile | 视口像素并行，逐像素推导 tile id | 保持。URP tile 宽度自适应，绑定组=tile 会重新引入 gate；kernel 无 group-shared 数据 |
| 材质模型 | GBuffer `materialFeatures` + Lit/StackLit/Hair/...（`Lit.hlsl:309+`） | Lit/SimpleLit 两路；Unlit/ComplexLit 跳过 | 保持 v1；与 URP stencil deferred 的跳过语义一致 |
| 间接光 | deferred pass 内算 GI/反射/SSS | GBuffer pass 已写 GBuffer3 | 保持；kernel 不采样探针、不算 GI |
| 透明 | clustered/FPTL | 逐物体（`_FORWARD_PLUS` 关闭） | 保持；零回归优先 |
| 阴影 | 阴影 atlas + per-light 索引 | 附加光 atlas + 全局索引 | 保持；T4 复用 Forward+ 已验证链路 |
| 剔除精度 | tile 深度 min/max + fine prune | zbin 粒度 + 屏幕空间 tile 位图 | **已知差异**：URP 会多算“z 重叠但空间不相关”的灯 |
| 空 tile | indirect dispatch 跳过 | 全屏 dispatch + 逐像素 mask/depth 早退 | **已知差异**：空 tile 仍有 dispatch/线程开销 |
| 输出 | 总光照直接写 lighting buffer | `RGBA16F` UAV + resolve | **已知差异**：多一次全屏读写；HDRP compute 同样证明 compute 无法混合 |

**定稿的技术细节**：

- **D8（UAV+resolve）确认**：HDRP 的 compute 同样用 UAV 写 lighting（`Deferred.compute:101-102,185-190`），其 PS 路径 `Blend Off` 能直写是因为它产出总光照；URP 的加法 resolve 是 GBuffer3 已含 GI 的必然结果。
- **D16（mask pass）确认**：直读 stencil 在 HDRP 的 D3D11/Vulkan/Metal compute 路径可行，但 d3d12 被 `only_renderers` 排除；v1 以 D3D12 为目标，mask 是唯一跨 API 的可移植方案。后续若放弃 D3D12 或完成逐 API 验证，可去掉 2 个全屏 draw 与 `R8_UInt`。
- **D7（像素并行）确认**：HDRP 能绑定 8×8 组到固定 16 tile，是因为 tile 宽度固定；URP 的 tile 宽度随分辨率/灯数变化，像素并行避免了重新引入 gate。代价是逐像素 tile id 运算（几个 ALU）。
- **D4（不开 `_FORWARD_PLUS`）确认**：HDRP 透明也走 cluster，但本仓库目标是透明零回归；transparent 的 cluster 化另立计划。
- **明确不做**：HDRP 的 tile feature flags/material variants、非空 tile indirect dispatch、GPU FPTL、big-tile 粗筛、面光源/体积雾/SSS/HD 阴影 atlas。这些列入 KnownDifferences 与 follow-up，不进入 v1 核心成本。

---

## 5. 任务清单（T0–T8）

### 5.1 依赖图与文件锁

```
T0(冻结)
 └→ T1(构建解耦 + 灯上限 + isForwardPlus 审计)
     └→ T2(compute-safe include)
         └→ T3(kernel v1：像素并行 + mask + 无阴影)
             ├→ T4(阴影/cookie/layers/mixed/AO)  [kernel/include]
             └→ T5(集成/边界/平台 gate)          [C#/Editor]
                 └──────────┬──────────────────────┘
                            └→ T6(可选：GBuffer 覆盖清单与自定义 shader)
                                └→ T7(证据/性能矩阵)
                                    └→ T8(文档/验收/归档)
```

T4 与 T5 可以并行，但必须满足下列文件锁；任何同文件改动串行：

| 文件/目录 | 允许的任务 | 规则 |
|---|---|---|
| `Runtime/ForwardLights.cs` | T1 | 仅 T1；T5 不得改 |
| `Runtime/UniversalRenderer.cs`、`UniversalRenderPipeline.cs` | T1 → T5 | 串行；T5 在 T1 合并后开始 |
| `Runtime/UniversalRendererData.cs`、`Editor/UniversalRendererDataEditor.cs` | T5 | 仅 T5 |
| `ShaderLibrary/ClusteredDeferred.hlsl` | T2 → T4 | 串行；T4 只追加阴影子集 |
| `Shaders/Utils/ClusteredDeferred.compute` | T3 → T4 | 串行；T5 不得改 kernel |
| `Shaders/Utils/ClusteredDeferredMask.shader` | T3 | 仅 T3 |
| `Assets/Tests/ClusteredDeferred/Editor/ClusteredDeferred*` | T6 preflight | 覆盖扫描、shader 编译检查、D3D12 抓帧构建；与 T7 测试资产分开提交 |
| 各材质 shader 的 GBuffer pass | T6 | 仅 T6；不得与 T7 的测试资产同批修改 |
| `Assets/Tests/ClusteredDeferred/**`（测试资产） | T7 | 仅 T7 提交测试资产 |
| `report.md`、`KnownDifferences.md`、`PLAN/ACCEPTANCE.md` | T8 | 仅 T8 |

### 5.2 任务定义

| ID | 任务 | 主要文件 | 退出条件 | 证据 |
|---|---|---|---|---|
| T0 | 冻结决策与基线 | 本文件 | D1–D16 入库；基线 `c37b9dd` 记录 | — |
| T1 | 构建解耦 + 灯上限 + `isForwardPlus` 审计 | `ForwardLights.cs`、`UniversalRenderer.cs`、`UniversalRenderPipeline.cs` | (a) `InitParams.clusteredDeferred` 生效；(b) 6 处 `m_UseForwardPlus` gating 拆为 `m_UseClusters`，关键字与逐物体索引仍只受 `m_UseForwardPlus` 控制；(c) `isClusteredLighting` 只用于阴影启用；(d) Deferred 可见灯上限收紧到 `maxVisibleAdditionalLights`；(e) 关闭开关时 forward/透明逐像素一致 | `T5-runtime/CdPerf.json`：关闭时 ForwardLights 仍走 `m_UseClusters=false` 分支，stencil 模式渲染正常；`T1ClusterParity.json` 的逐元素缓冲对比与关闭差分仍待 T7 |
| T2 | compute-safe include：灯数组、衰减、cluster 迭代、GBuffer 解码、阴影子集 | `ClusteredDeferred.hlsl` | 编译 0 错误；header 0/1 语义、方向光偏移、`URP_FP_PROBES_BEGIN` 边界（off-by-one）与 `Clustering.hlsl:25-130` 一致 | `T2ShaderCheck.txt`（0 消息）、`T2IterParity.json`（逐像素灯集合与 PS 一致） |
| T3 | kernel v1：像素并行 dispatch、材质类型 mask、Lit+SimpleLit、主光+附加方向光+cluster、无阴影、direct-only additive | `ClusteredDeferred.compute`、`ClusteredDeferred.hlsl`、`ClusteredDeferredMask.shader`、`ClusteredDeferredResolve.shader` | 无阴影场景与 stencil deferred 差异在阈值内；天空/mask=0 不写；无 NaN；tile 宽度 8/16/32/64 行为一致；深度/正交/翻转 Z 正确 | `T3LightingDiff.json`（mean/max/NaN）、`T3ShaderCheck.txt`、A/B PNG |
| T4 | 阴影/cookie/layers/mixed/AO | kernel + include | 每项单开关 A/B；`_RECEIVE_SHADOWS_OFF` 跳阴影；subtractive 早退；AO 只作用于直接光；cookie/layer 与 forward 一致 | `T4FeatureMatrix.json`、A/B PNG |
| T5 | 集成/边界：renderer data 开关、pass 替换、mask pass、保留 forward-only、`UseRenderPass` gate、预览/overlay、平台 gate、变体 | `Runtime/UniversalRendererData.cs`、`Editor/UniversalRendererDataEditor.cs`、`UniversalRenderer.cs`、`Runtime/ClusteredDeferred/*` | 开关可运行时切换；mask 绑定/清除正确；不满足条件时明确回退/禁用并告警；变体数可报告 | `T5-runtime/`：双 API player、A/B、D3D12 抓帧（已产出）；`T5ModeGateTests.xml`、`T5VariantCount.json` 待 T7 |
| T6 | **可选优化**：opaque 材质 GBuffer 覆盖清单，把希望享受 clustered deferred 的 forward-only 材质补 GBuffer | 扫描工具 + 各 shader | 清单落盘（含材质类型）；可选补齐项有编译与视觉验证；**不再是正确性前置** | `T6Coverage.json`、编译检查、A/B PNG |
| T7 | 测试、证据与性能矩阵 | `validation/` | 既有测试全绿；新测试全绿；A/B/交叉点/RenderDoc/Player 完成 | `T7Perf.json`、`T7PlayerSummary.json`、`*.xml`、PNG、`renderdoc-stencil-deferred-baseline*.json` + 批准范围内的 `.rdc` |
| T8 | 文档/验收/归档 | `report.md`、`KnownDifferences.md`、`PLAN/ACCEPTANCE.md` | 五处文档一致；产物清理；TODO→Done | `git status`/`git diff --stat`、`report.md` |

### 5.3 任务冲突/遗漏审计

**已识别的遗漏（本版补入）**：

- `isForwardPlus` 的四处用途（阴影启用 vs 逐物体灯数据）未区分；若只改 `m_UseForwardPlus`，会造成“阴影被静默关闭”或“透明灯数据缺失”。
- `m_Clustering` 被误当作构建开关（它只影响 debug/中间纹理）。
- 原“clustered 时跳过 forward-only pass”会静默丢失缺 GBuffer 的物体；本版改为保留 pass + mask 跳过。
- 未指定 `UseRenderPass` 的强制关闭与回退。
- 未指定 `maximumVisibleLights` 必须在 culling 前收紧（Deferred 当前为 `0xFFFF`）。
- 未覆盖 Preview/Overlay 相机、多相机共享缓冲、变体 stripping、`.meta` 与测试资产归属。
- **材质类型分层遗漏**：stencil deferred 用 stencil 分 Lit/SimpleLit/跳过；compute 不能读 stencil，必须有 mask pass（本版补入）。
- **附加方向光遗漏**：Forward+ 在 cluster 前单独循环 `URP_FP_DIRECTIONAL_LIGHTS_COUNT` 盏附加方向光（本版补入）。
- **tile 宽度 gate 误判**：4K 必选 32；本版删除 gate（D15）。

**冲突与顺序规则**：

- 同文件串行：`ForwardLights.cs` 仅 T1；`UniversalRenderer.cs`/`UniversalRenderPipeline.cs` 按 T1→T5；kernel/include 按 T3→T4；材质 shader 仅 T6。
- T4 与 T5 可并行，但 T4 只改 kernel/include，T5 只改 C#/Editor；出现跨界需求时回流拆分，不在并行期互改。
- T7 只产出测试/证据，不改功能代码；发现问题回流到对应任务。
- T8 不改功能代码，只改文档、索引与归档。

---

## 6. 验收标准（AC）

| ID | 标准 |
|---|---|
| AC-1 | 开关关闭：与基线 stencil deferred 逐像素一致（无容差，忽略非确定性浮点排序） |
| AC-2 | 开关关闭：forward/透明路径的灯数据、关键字、图像与基线一致 |
| AC-3 | 开关开启：Deferred 模式下每帧构建并上传 `URP_ZBins/urp_Tiles/_FPParams*`；culling 前收紧可见灯上限且数组不越界；`_FORWARD_PLUS` 仍为 false；逐物体灯索引仍存在（透明可用） |
| AC-4 | 无阴影场景：方向光 + 点光 + 聚光结果与 stencil deferred 的差异在阈值内（报告 mean/max、NaN 数） |
| AC-5 | 阴影：主光 cascade、screen-space shadow、附加光 atlas 各单项 A/B 通过 |
| AC-6 | Cookie、light layers、mixed lighting/shadowmask、GTAO 直接光 AO 各单项 A/B 通过 |
| AC-7 | 天空/背景像素不写 lighting；无 NaN/Inf；alpha 语义与基线一致 |
| AC-8 | GBuffer/材质类型清单落盘（含 Lit/SimpleLit/Unlit/ComplexLit/forward-only 分类）；forward-only 物体在 clustered 模式下仍由原 pass 渲染且与基线一致；缺 GBuffer 不再视为渲染缺失 |
| AC-9 | compute 在 D3D11/D3D12/Vulkan/Metal 编译 0 错误；无 `USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA` 分支；报告变体数量；GLES/WebGL 被明确排除 |
| AC-10 | 支持矩阵如实落盘：MSAA/XR/foveated/GLES 不支持；native RP 命中时本帧强制关闭并告警；Preview/Overlay 回退；多相机顺序构建；tile 宽度 8/16/32/64 均支持（无 gate）；`useStructuredBuffer=true` 禁用；正交支持并有测试；mask 对 Lit/SimpleLit/Unlit/ComplexLit/forward-only 的分流与 stencil deferred 一致 |
| AC-11 | 性能：多灯重叠场景给出 stencil deferred / clustered deferred / forward+ 的交叉点；记录 CPU 构图与 GPU 时间 |
| AC-12 | RenderDoc：dispatch 尺寸、tile 布局、每像素灯数分布、A/B diff；`.rdc` 遵守预算 |
| AC-13 | 既有 SSL/GTAO/SSR/折射与 EditMode/PlayMode 全绿；§7.3 新测试全绿；shader 编译检查 0 消息；变体计数变化有记录 |
| AC-14 | 代码边界审计：核心文件改动仅 §4.6 清单；`DeferredPass.cs`/`DeferredLights` 每灯 pass/GBuffer 布局未改；无 HDRP 面光源/7 GBuffer/material feature 引入 |
| AC-15 | 回退：关闭开关无需重编 shader/重建 renderer；clustered 模式下 forward-only 不重复受光（mask=0）；mask/GBuffer 同帧正确清除与绑定；player build 不丢 shader；Preview/Overlay 正确回退 |
| AC-16 | 文档一致：HANDOFF/report/KnownDifferences/ACCEPTANCE/T1 契约五处一致，AC 证据路径存在 |

---

## 7. 测试、证据与善后

### 7.1 验证场景

- 主场景：多灯重叠（≥32 盏点/聚光，含大半径与小型灯混合，地面/墙接收）。
- 对照场景：`HDRPSample01/Assets/OutdoorsScene` 仅用于“单灯负收益”说明，不作为性能结论。
- 变体：无阴影 / 有阴影 / cookie / light layers / mixed lighting / GTAO / 正交相机 / 1080p / 4K。
- 固定相机与固定灯参数；A/B 只切换 `m_ClusteredDeferredLighting`，其他不变。

### 7.2 既有测试怎么改

1. **不改既有断言**：现有 EditMode/PlayMode 的期望值保持；只追加新 suite 与新资产。默认 Pipeline/Renderer/场景/GoldenImages 不动。
2. **可能受影响的既有测试**（T7 必须逐项运行并记录）：
   - `Tests/Editor/ShaderScriptableStripperTests.cs`：新增 compute/关键字变体后，变体计数可能变化；
   - `Tests/Editor/ShaderBuildPreprocessorTests.cs`、`Tests/Editor/ShaderPrefilteringTests.cs`：若新增关键字进入 build stripping；
   - `Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingProbeAtlasTests.cs`：确认 `ForwardLights` 改动未影响探针 atlas；
   - 既有 EditMode/PlayMode 渲染与截图测试：确认默认路径（开关关闭）无回归。
3. **round-local wrapper**：新增 `validation/Invoke-ClusteredDeferredTests.ps1`，必须接受 `-OutputRoot`，不硬编码轮次目录；输出 `*.xml` 与汇总 JSON。
4. **shader 编译回归**：新增 compute/include 后运行既有 shader 检查；记录 SRP Batcher 兼容性、变体数量与消息；计数变化必须记录新基线与原因，不静默放宽。
5. **测试资产归属**：包内 EditMode 测试放 `Tests/Editor/ClusteredDeferred`（若新目录未纳入现有 test asmdef，复用包内既有 test asmdef，不新建游离程序集）；项目侧 PlayMode 测试与专用 Pipeline/Renderer/场景放 `Assets/Tests/ClusteredDeferred`，只由 T7 提交；T1–T6 的临时 fixture 在 `TearDown` 删除。

### 7.3 新增测试清单

| 测试 | 类型 | 覆盖 |
|---|---|---|
| `ClusteredDeferredShaderCompileTests` | EditMode（包内） | include + compute 0 error；关键字集合；无 `USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA` 分支 |
| `ClusteredDeferredIterationParityTests` | EditMode（包内） | header 0/1 语义；方向光偏移；附加方向光单独循环；灯数 0/1/31/32/33；探针列表不串扰；sky 像素 |
| `ClusteredDeferredBuildParityTests` | EditMode（包内） | Deferred 模式构建 cluster；开关关闭不构建；`_FORWARD_PLUS` 仍为 false；`_AdditionalLightsIndices` 仍存在；缓冲与 Forward+ 同场景一致 |
| `ClusteredDeferredCullingCapTests` | EditMode（包内） | `maximumVisibleLights` 在 culling 前收紧；超 `maxVisibleAdditionalLights` 时告警；数组不越界 |
| `ClusteredDeferredModeGateTests` | EditMode/PlayMode（包内+项目） | `UseRenderPass`（强制关闭并告警）、MSAA、XR、foveated、GLES、Preview/Overlay、`useStructuredBuffer=true` 的禁用/回退；tile 宽度 8/16/32/64 均不触发禁用 |
| `ClusteredDeferredMaterialTypeTests` | EditMode（包内）+ PlayMode | mask pass 对 Lit/SimpleLit/Unlit/ComplexLit/forward-only 的 0/1/2 分流；SimpleLit 走 Lambert+Specular；forward-only 仍由原 pass 渲染且不重复受光 |
| `ClusteredDeferredLightingEqualityTests` | PlayMode（项目）+ 视觉 | 无阴影/有阴影/cookie/layers/mixed/AO/正交；阈值见 AC-4/AC-5 |
| `ClusteredDeferredForwardParityTests` | PlayMode（项目） | 开关关闭逐像素一致；开关开启时 forward/透明与基线一致 |
| `ClusteredDeferredCoverageTests` | EditMode（包内） | 材质扫描：GBuffer/材质类型/forward-only 清单（信息性，不再是正确性 gate） |
| `ClusteredDeferredResourceLifecycleTests` | EditMode（包内） | 开关反复切换、Renderer/相机销毁：无泄漏 RT/GraphicsBuffer；cluster 缓冲 Dispose |
| `ClusteredDeferredPlayerTests` | Player（T7 执行） | 非 Development build 中 compute/shader 未 strip；运行时开关生效 |

### 7.4 Player 验证

- D3D11 与 D3D12 各构建 Development 与非 Development player；release 用于验证资源未 strip。
- 使用 T7 的专用 `Assets/Tests/ClusteredDeferred` Pipeline/Renderer/场景；默认资产不改。
- 验证：compute/shader 存在；开关生效；A/B PNG；无 missing resource/异常；记录 GPU/分辨率/ms。
- Vulkan/Metal 只做 API gate 检查；未在本机验证的组合必须在 `KnownDifferences.md` 标注“未验证”，不声称通过。
- Player 构建目录、日志、临时 `.rdc` 只用于取证，记录 `BuildSummary.json`（路径/大小/结果）后删除，不入库。

### 7.5 证据与预算

| 类型 | 留存 | 说明 |
|---|---|---|
| 契约/基线 | `validation/T0/*.json/*.md` | 入库；含基线提交与场景/相机参数 |
| 测试结果 | `*.xml` | 入库；按任务放 `validation/T{n}` |
| 汇总/报告 | `*.json/*.txt`、`report.md` | 入库 |
| KnownDifferences | `KnownDifferences.md` | 入库，T8 更新 |
| 图像 | 小尺寸 PNG（A/B/差值/tile 灯数热图） | 入库；≤80 张、≤45 MB |
| 日志 | `*.log` | local-only，不入库 |
| `.rdc` | 默认 0；批准时最多 1 个 ≤20 MiB | D3D11 player capture；记录相机/场景/分辨率 |
| Player/Builds | 不入库 | `Builds/`、二进制、`Library/`、`Temp/` 清理；保留 `BuildSummary.json` |
| 证据总预算 | `validation/`（不含既有 `.rdc`） | ≤50 MB；超出需在 report 说明 |

补充：

- 本轮沿用 **per-round** 的 `.rdc` 规则（默认 0）；`06-ValidationAllRDC` 的 1 GB 是历史总预算，不作为本轮许可。
- Frame Debugger 用于 pass 顺序、附件绑定与 dispatch 尺寸证据；若超时，用 `ProfilingSampler` 日志/计数器替代并标 PARTIAL，不以推测补齐。

任务级证据命名（必须可复现）：

| 任务 | 产物 |
|---|---|
| T1 | `T1ClusterParity.json`（开关关闭差分、缓冲逐元素对比、逐物体索引存在性） |
| T2 | `T2ShaderCheck.txt`（0 消息）、`T2IterParity.json` |
| T3 | `T3LightingDiff.json`（mean/max/NaN）、A/B PNG |
| T4 | `T4FeatureMatrix.json`、各特性 A/B PNG |
| T5 | `T5ModeGateTests.xml`、`T5VariantCount.json` |
| T6 | `T6Coverage.json`、缺 pass 清单、补齐后的编译与 A/B |
| T7 | `T7Perf.json`（交叉点）、`T7PlayerSummary.json`、测试 XML、RenderDoc 摘要 |
| T8 | `report.md`、`KnownDifferences.md`、ACCEPTANCE 新节 |

### 7.5.1 本轮 T1/T5 运行时证据（2026-11-21）

**测试资产**（仅 `Assets/Tests/ClusteredDeferred`，默认 Pipeline/Renderer/场景未改）：

- `Scenes/ClusteredDeferredArena.unity`：固定相机、平行主光（无阴影）、地面/后墙；runner 运行时生成 64 盏点/聚光（无阴影、无 cookie、无 light layer）与 60 个 Lit 接收体。
- `Renderers/CdStencilDeferredRenderer` 与 `CdClusteredDeferredRenderer`：除 `m_ClusteredDeferredLighting` 外完全相同；均拷贝自 `SslValidationDeferredRenderer` 并移除 renderer features，使 A/B 只比较光照路径。
- `Runtime/ClusteredDeferredArenaRunner.cs`：只切换 pipeline（stencil / clustered / Forward+）× 灯数 `[0,8,16,32,48,64]`；FrameTimingManager 采样，48 灯截图并计算 A/B 差分。

**A/B（D3D11 player，1280x720，48 灯）**：平均通道差 0.78/255（0.31%），最大 9/255，超过 8/255 的像素 5/921600；记录阈值 mean ≤ 2/255、max ≤ 24/255 → 通过。PNG：`T5-runtime/player-d3d11/StencilDeferred_L48.png`、`ClusteredDeferred_L48.png`。

**性能交叉点（GPU mean ms，RTX 5060 Ti，720p）**：

| 灯数 | Stencil deferred | Clustered deferred | Forward+ |
|---:|---:|---:|---:|
| 0 | 0.191 | 0.186 | 0.255 |
| 8 | 0.212 | 0.197 | 0.267 |
| 16 | 0.230 | 0.192 | 0.280 |
| 32 | 0.344 | 0.212 | 0.312 |
| 48 | 0.484 | 0.241 | 0.363 |
| 64 | 0.709 | 0.254 | 0.420 |

clustered 从约 24 盏灯起低于 stencil deferred（16 灯已略低，32 灯起差距 >1.5×），64 灯时 0.254 vs 0.709 ms；本场景下 clustered 在所有灯数均低于 Forward+。CPU 侧 stencil deferred 随灯数升到 1.59 ms（64 灯，逐灯 draw/dispatch），clustered 约 1.13 ms。完整数据：`T5-runtime/player-d3d11/CdPerf.json`、`CdPerf.csv`。

**D3D12 抓帧**：`T5-runtime/clustered-deferred-d3d12-lighting_frame172.rdc`（1 个，14.2 MiB，预算内）。结构：GBuffer（54 draws）→ CopyDepth → **Clustered Deferred**（mask clear、2 个全屏 mask draw、1 次 dispatch、1 个 resolve）→ Forward Only（0）→ FinalBlit，**没有 stencil 灯体积**。mask 为 R8_UINT（场景全 Lit，max=1）；compute 绑定 5 张 SRV + 1 个 RGBA16F UAV + `URP_ZBinBuffer`(16 KB)/`urp_TileBuffer`(64 KB)；UAV 在 670 写入、677 被 resolve 读取；`debug_thread` 证明 dispatch 覆盖 (1279,719)，Lit 像素线程执行 728 步；`assert_clean` 无 ≥high 消息。详见 `T5-runtime/renderdoc-clustered-deferred-d3d12.json`。

**本轮未覆盖**：v1 无阴影/cookie/light layers/mixed/AO（T4）；legacy fog 仍由 stencil deferred 的 fog pass 绘制，clustered 路径未绘制（列入 KnownDifferences）；AC-15 的运行时开关切换、变体计数、Preview/Overlay 门控测试与 `T5ModeGateTests.xml` 留待 T7。设备/XR gate 已实现：不支持 compute、`useStructuredBuffer=true` 或 GLES 时回退 stencil deferred 并告警；XR 相机不启用。

### 7.5.2 T4 阴影子集证据（2026-11-21）

**范围**：T4 的阴影子集——主光 cascade、screen-space shadow、附加光 atlas，以及按 GBuffer 材质 flag 的 `_RECEIVE_SHADOWS_OFF` 跳采样。cookie / light layers / GTAO 直接光 AO 见 §7.5.3；mixed lighting-shadowmask 在 §7.5.3 标 PARTIAL。

**实现**（只改 kernel/include，符合文件锁）：

- `ShaderLibrary/ClusteredDeferred.hlsl`：追加 `Shadows.hlsl` 的 compute-safe 子集——`LightShadows` cbuffer（字段与顺序和 `Shadows.hlsl:64-89` 一致）、主光/附加光 PCF（low/medium/high 与 `_SHADOWS_SOFT` 逐灯质量）、cascade 索引、screen-space shadow 采样、阴影 fade。fade 使用 `_CdCameraPositionWS`（compute 无 `_WorldSpaceCameraPos`）。
- `Shaders/Utils/ClusteredDeferred.compute`：新增 `_MAIN_LIGHT_SHADOWS/_CASCADE/_SCREEN`、`_ADDITIONAL_LIGHT_SHADOWS`、`_SHADOWS_SOFT{,_LOW,_MEDIUM,_HIGH}` 变量（连同 `_GBUFFER_NORMALS_OCT` 共 80 个变体）；主光与附加光在 `receiveShadowsOff` 为假时填 `shadowAttenuation`。
- 两个 compute 特有修正：
  1. 主光 shadow coord 的 `w == 0`，必须按 `Shadows.hlsl` 的 `isPerspectiveProjection=false` 不除 w（附加光才除）；
  2. screen-space shadow 纹理在 compute 里必须用 `SAMPLE_TEXTURE2D_LOD(..., 0)`；`SAMPLE_TEXTURE2D` 在 D3D11 映射为隐式 LOD 的 `Sample()`，所有 20 个 `_MAIN_LIGHT_SHADOWS_SCREEN` 变体在 player 构建时报 `cannot map expression to cs_5_0`。
- 混合光照暂用 `shadowMask = 1`、无 baked 项（`_DEFERRED_MIXED_LIGHTING` 留到 mixed 子集）。

**测试资产**（仍只写在 `Assets/Tests/ClusteredDeferred`）：

- 新增 shadow 管线对 `CdShadow{Stencil,Clustered}DeferredPipeline`（4 cascade、软阴影 Medium、附加光 shadows、2048 atlas）与 screen-space 管线对 `CdScreenShadow{Stencil,Clustered}DeferredPipeline`（1 cascade；renderer 通过反射加入 URP 内部 `ScreenSpaceShadows` feature，并序列化其 shader 引用）。除 `clusteredDeferredLighting` 外 stencil/clustered 资产一致。
- `ClusteredDeferredArenaRunner --cd-t4`：逐特性“单开关”——Off/On × stencil/clustered 各截一张 720p 图，计算 `stencil_vs_clustered_{on,off}` 与该特性在两种路径下的 `feature_effect`。用例：主光 cascade、`_RECEIVE_SHADOWS_OFF`（材质 + 半数量接收体）、主光 screen-space、附加光 atlas。附加光用例把 6 盏 spot 移到接地球体上方并关闭其余灯，避免 64 灯互相冲淡；新增材质资产 `Materials/CdReceiveShadowsOff.mat` 保证该 Lit GBuffer 变体进入 player。
- 构建入口 `ClusteredDeferredArenaBuild.BuildT4D3D11AndExit / BuildT4D3D12AndExit`；T4 构建把 3 个 shadow 管线停在 quality level 上，并将默认 pipeline 设为 screen-shadow 资产（URP prefiltering 以默认 pipeline 的 renderer features 为准）。

**A/B 结果**（阈值沿用 mean ≤ 2/255、max ≤ 24/255；`feature_effect` 只要求可测变化）：

| 特性 | API | stencil vs clustered（on） | feature effect（clustered on-off） |
|---|---|---|---|
| 主光 cascade（4 级、软阴影） | D3D11 | mean 0.940 / max 8、>8 差 0 px | mean 0.445，>8 差 22904 px |
| 主光 cascade | D3D12 | mean 0.972 / max 8、>8 差 0 px | mean 0.445，>8 差 22904 px |
| `_RECEIVE_SHADOWS_OFF` 材质（半数量接收体） | D3D11 | mean 0.943 / max 8、>8 差 0 px | mean 0.383 / max 240，>8 差 5541 px |
| `_RECEIVE_SHADOWS_OFF` 材质 | D3D12 | mean 0.975 / max 8、>8 差 0 px | mean 0.383 / max 240，>8 差 5541 px |
| 主光 screen-space | D3D11 | mean 0.942 / max 8、>8 差 0 px | mean 0.390，>8 差 19899 px |
| 主光 screen-space | D3D12 | mean 0.974 / max 8、>8 差 0 px | mean 0.390，>8 差 19899 px |
| 附加光 atlas（6 spot） | D3D11 | mean 0.020 / max 3、>8 差 0 px | mean 0.962，>8 差 25261 px |
| 附加光 atlas | D3D12 | mean 0.020 / max 3、>8 差 0 px | mean 0.962，>8 差 25261 px |

四项 `passed=true`；两种 API 的逐像素差分一致（>8 阈值像素均为 0，mean 相差 ≤ 0.03，来自浮点/驱动排序）。`_RECEIVE_SHADOWS_OFF` 用例只能用材质资产（编辑期复制 .mat 后开启关键字）验证：运行时改 `Renderer.receiveShadows` 在 SRP 下没有产生该关键字，stencil 与 clustered 都看不到变化；且不能用 `new Material(source)`（会补默认属性、改变外观，污染 A/B）。证据：`validation/T4-shadows/player-d3d11/`、`player-d3d12/`（`T4FeatureMatrix.json` + 每个状态 1 张 PNG，共 32 张）。

**编译/回归**：

- 两个 D3D11/D3D12 T4 player 构建日志中 `Shader error in 'ClusteredDeferred'` 均为 0；构建摘要 `CdArenaT4BuildSummary-{Direct3D11,Direct3D12}.json`。
- **工具缺陷记录**：`ClusteredDeferredShaderCheck`（`ShaderUtil.GetComputeShaderMessageCount`）在 `SAMPLE_TEXTURE2D` 那版仍报 0 消息，而 player 构建拒绝了全部 20 个 screen 变体。compute 变体的权威编译检查是 player 构建日志；T7 的 shader 检查需改为“player 构建 + 日志计数”，不能只看编辑器 `ShaderUtil`。
- EditMode 全量 **474/474 通过**：`validation/T4-shadows/T4EditMode.xml`。

### 7.5.3 T4 特性子集：cookie / light layers / GTAO 直接光 AO（2026-11-22）

**范围**：T4 剩余四项。附加光 cookie、light layers、GTAO 直接光 AO 在 D3D11/D3D12 player A/B 通过；mixed lighting-shadowmask 的 kernel/绑定已实现，但场景没有 baked shadowmask/probe 数据，运行时 A/B 不可得，标 **PARTIAL**（原因见下）。

**实现**（kernel/include + 消费侧接线）：

- `ShaderLibrary/ClusteredDeferred.hlsl`：
  - `LightCookie.hlsl` 的 compute-safe 子集：`LightCookies` cbuffer（数组长度与 `Input.hlsl` 的 `MAX_VISIBLE_LIGHTS` 一致）、directional / spot / point（octahedral）UV、enable-bit 判定、`SAMPLE_TEXTURE2D_LOD(...,0)` 采样；`_LIGHT_COOKIES` 下把 cookie 颜色乘进 `CdMainLight` / `CdGetAdditionalLight`。
  - 混合光照：`CdBakedShadow`（`1 + dot(mask-1, occlusionProbeChannels)`）、`CdMixRealtimeAndBakedShadows`（`LIGHTMAP_SHADOW_MIXING` → min-lerp，否则 fade-lerp；模式由 `_CdBakedShadowMixMode` 传入，因为 compute 没有逐材质关键字）；`CdLight.subtractiveMixed` 对应 stencil 的 `_LightFlags & kMaterialFlagSubtractiveMixedLighting` 早退。
  - light layers：`CdDecodeMeshRenderingLayer` + `IsMatchingLightLayer`，主光/附加方向光/cluster 光三处统一 culling。
  - GTAO：`CdDirectAmbientOcclusion`（直接光系数）与 `CdIndirectAmbientOcclusionCancel`（间接光取消系数，见“根因”）。
- `Shaders/Utils/ClusteredDeferred.compute`：新增 `_LIGHT_COOKIES`、`_LIGHT_LAYERS`、`_SCREEN_SPACE_OCCLUSION`、`_DEFERRED_MIXED_LIGHTING` 四个关键字，变体 80 → 1280；shadowmask / rendering layer 从绑定槽按像素 `LOAD_TEXTURE2D`；末尾 `color *= CdDirectAmbientOcclusion(uv)`，UAV 的 alpha 写间接 AO 取消系数（非 Lit / sky 像素写 1）。
- `Runtime/ClusteredDeferred/ClusteredDeferredPass.cs`：当 `GBufferShadowMask` / `GBufferRenderingLayers` 存在时绑定 `_CdShadowMaskTexture` / `_CdLightLayersTexture`；由 `MixedLightingSetup` + `QualitySettings.shadowmaskMode` 计算 `_CdBakedShadowMixMode`（与 `DeferredLights.Setup` 的 `LIGHTMAP_SHADOW_MIXING` 推导一致）；dispatch 前后设置/清除 `_DEFERRED_MIXED_LIGHTING`（stencil 在 `DeferredLights.cs:581` 做同样的事）。
- `Shaders/Utils/ClusteredDeferredResolve.shader`：`Blend One One` → `Blend One SrcAlpha, Zero One`，alpha 从 UAV 透传。

**根因（首版两项失败）**：首版 A/B 中 `LightCookie` / `ScreenSpaceAO` 的 max 均为 32/255（`>8` 约 300 / 492 px），均值却只有 0.07 / 1.03。逐像素定位后**全部落在最后一列 x=1279**：GTAO 在该列写出 AO=0；stencil 路径把 SSAO 用于间接光的方式不是“乘 AO”，而是用 `_DEFERRED_FIRST_LIGHT` 的 `Blend One SrcAlpha` **取消 GBuffer 已烘焙的 occlusion，再代入 min(baked, ssao)**（`StencilDeferred.hlsl:286-295`）。clustered 只实现了直接光 AO，间接光原样保留，于是该列出现 0 vs (1,7,32) 的差。补上 `CdIndirectAmbientOcclusionCancel` 后两项通过；cookie 用例中原本判定为“spot cone 边缘”的 1–4 级环状差也一并消失——同一根因。**结论**：T4 的 AO 必须是“直接光系数 + 间接光取消系数”两段，缺一不可。

**测试资产**（仍只在 `Assets/Tests/ClusteredDeferred`）：

- `CdFeature{Stencil,Clustered}Deferred{Renderer,Pipeline}.asset`：renderer 打开 light layers 并挂 GTAO feature（indirect AO 常开，direct strength 由 runner 反射设置；`temporalAccumulation=false`，否则两次截图的帧相位噪声会污染 A/B）。
- `CreateShadowPipeline` 给所有 shadow 管线打开 `m_SupportsLightLayers`：URP 的 shader prefiltering 只收集停在 quality level 上的管线，否则 `_LIGHT_LAYERS` / `_WRITE_RENDERING_LAYERS` 变体被剥离——首版 feature 管线输出全黑即此原因（mask 读取的 layer 恒 0，所有灯被 cull）。
- `Textures/CdSpotCookie.png`（64×64、径向 × 8 px 棋盘、无压缩、clamp、无 mip）。
- runner 新增三个用例：`LightCookie`（6 盏 spot 移到接收体上方并挂 cookie）、`LightLayers`（8 盏灯、一半放到 layer 2，接收体都在 layer 1）、`ScreenSpaceAO`（两条管线同时切 `directLightingStrength`）。matrix 的异常现在会写 `CdError.txt` 并以非零码退出（首版输出文件被外部 mmap 时会挂起）。
- 用例间状态隔离：`ResetLightState` 每次截图前恢复灯的位置、朝向、intensity、range、spotAngle、innerSpotAngle、cookie、rendering layer 与接收体材质/层；spot 的 55° 下压写入记录基线（否则会被重置回 +Z）。首版 cookie 用例改过的 range/angle 会泄漏到后续用例，导致 layers/AO 用例不是默认灯场——证据已按修复后的运行重取。

**A/B 结果**（阈值 mean ≤ 2/255、max ≤ 24/255）：

| 特性 | API | stencil vs clustered（on） | stencil vs clustered（off） | feature effect（clustered on-off） |
|---|---|---|---|---|
| 附加光 spot cookie（6 spot） | D3D11 | mean 0.044 / max 3、>8 差 0 px | mean 0.058 / max 3、>8 差 0 px | mean 3.024 / max 186、>8 差 187682 px（stencil 3.011） |
| 附加光 spot cookie | D3D12 | mean 0.044 / max 3、>8 差 0 px | mean 0.058 / max 3、>8 差 0 px | mean 3.024 / max 186、>8 差 187682 px（stencil 3.011） |
| light layers（一半灯在 layer 2） | D3D11 | mean 0.083 / max 4、>8 差 0 px | mean 0.160 / max 5、>8 差 0 px | mean 2.871 / max 245、>8 差 136281 px（stencil 2.795） |
| light layers | D3D12 | mean 0.084 / max 4、>8 差 0 px | mean 0.163 / max 5、>8 差 0 px | mean 2.871 / max 245、>8 差 136281 px（stencil 2.792） |
| GTAO 直接光 AO | D3D11 | mean 0.949 / max 8、>8 差 0 px | mean 0.953 / max 8、>8 差 0 px | mean 0.477 / max 203、>8 差 18635 px（stencil 0.473） |
| GTAO 直接光 AO | D3D12 | mean 0.981 / max 9、>8 差 1 px | mean 0.986 / max 8、>8 差 0 px | mean 0.477 / max 203、>8 差 18635 px（stencil 0.472） |

七项（含 §7.5.2 四项）在两种 API 均 `passed=true`。AO 用例残留的逐像素差来自 GTAO 空间去噪噪声（两条路径采样同一张 AO 纹理，但 stencil 用插值 `screenUV`、clustered 用 dispatch 像素中心；`temporalAccumulation=false` 保证逐帧确定），max ≤ 9/255、>8 至多 1 px；cookie / layers 用例的两种路径差 ≤ 5/255。

**mixed lighting-shadowmask：PARTIAL**

- 已实现并静态对照：kernel 的 baked shadow 混合与 min-lerp/lerp 选择、subtractive 早退、`GBufferShadowMask` 槽绑定、mix-mode uniform；`_DEFERRED_MIXED_LIGHTING` 变体已编译进 player（构建日志 0 error）。
- 未做运行时 A/B，原因可复现：deferred 的 mixed 模式检测读 `Light.bakingOutput.lightmapBakeType`；编辑器探针显示运行时设 `Light.lightmapBakeType = Mixed` 后 `bakingOutput` 仍为 `Realtime`，而 `m_BakingOutput.isBaked = false` 无法在没有 GI bake 时伪造。场景没有 baked lightmap/probe 数据，shadowmask 恒为白，min-lerp 与 fade-lerp 数值上不可区分。要覆盖需新增烘焙场景 + lightmap 资产，并重做全部 T4/T5 基线（列入 KnownDifferences，T8 决定是否补）。
- 附注：本项目 `QualitySettings.shadowmaskMode` 默认 `DistanceShadowmask`，即 `_CdBakedShadowMixMode = 0` 的 fade-lerp 分支。

**覆盖边界**：cookie 只端到端验证了 spot（透视）路径；point（octahedral）与 directional（主光/附加方向光）UV 是 `LightCookie.hlsl` 的逐行移植并随全部 `_LIGHT_COOKIES` 变体编译，但运行时未触发。mixed 的 sub-1 shadowmask 数值与 subtractive 早退同样只有编译级覆盖。

**编译/回归与预算**：

- 两个 API 的 T4 player 构建 0 `Shader error in 'ClusteredDeferred'`；`T4ShaderBuild.json` 记录 8 个关键字组、声明组合 1280。
- EditMode 全量 **474/474**（`T4EditMode.xml`）。
- 证据 `validation/T4-shadows/` 共 56 张 720p PNG + 2 份 `T4FeatureMatrix.json` + 构建摘要，约 24 MB；`.rdc` 0 个（本轮无抓帧）。产物：`player-d3d11/`、`player-d3d12/`。

### 7.5.4 T5/T7 集成门控、测试与性能矩阵（2026-11-22）

**范围**：收尾 T5（运行时开关、native RP/预览门控、变体计数）与 T7（回归测试、A/B、tile 宽度、性能与 RenderDoc 汇总）。T6 保持“可选优化”：`T6Coverage.json` 已落盘（8 场景 / 39 个不透明材质；11 Lit、1 ComplexLit 跳过、无 forward-only），D16 之后不再作为正确性前置，本轮不补 GBuffer。

**新增/修改代码**（均在 T5 允许的文件内）：

- `Runtime/UniversalRenderer.cs:262-268`：`Deferred + clusteredDeferredLighting + data.useNativeRenderPass` 时在构造期把 `useRenderPassEnabled` 置 false 并输出一次告警（AC-10），删除原先每帧修改 `DeferredLights.UseRenderPass` 的块——该状态是渲染器级的，逐帧只会留下“首帧后再也回不到 native RP”的隐患。
- `Runtime/ForwardLights.cs:196` 新增 `CompleteClusterJobs()`；`Runtime/UniversalRenderer.cs:706-710` 在 `DeferredLights.ResolveMixedLightingMode` 之前完成 PreSetup 调度的 cluster jobs。
- **根因（PlayMode 首版 7/7 失败）**：PreSetup 在 `Setup` 早期调度的 Tiling/ZBinning jobs 持有 `visibleLights`/`visibleReflectionProbes`，而同一 `Setup` 后半段 `ResolveMixedLightingMode` 用 `UnsafeElementAtMutable` 取得可变访问；编辑器 Job safety 因此抛 `InvalidOperationException`，该帧中断后下一帧 `PreSetup` 的 MemClear 又撞上未完成的 ZBinningJob，形成级联。Player 关闭 safety 检查，且该方法只读，故既有 player 证据不受影响；GPU 输入不变（上传仍在完成之后）。修复后 PlayMode 7/7。
- 新增测试：包内 `Tests/Editor/ClusteredDeferred/ClusteredDeferredEditorTests.cs`（10 个测试：compute/mask/resolve 编译、1280 变体矩阵、无 structured-buffer 分支、设备/相机门控、渲染器开关只对 Deferred 生效、culling 上限与 probe 排序、重复 Dispose）；项目侧 `Assets/Tests/ClusteredDeferred/Tests/PlayMode/ClusteredDeferredPlayModeTests.cs` + `Unity.ClusteredDeferred.Tests.PlayMode.asmdef`（7 个测试）。

**测试结果**：

| 套件 | 结果 | 证据 |
|---|---|---|
| EditMode（仅 ClusteredDeferred） | 10/10 | `T7EditModeClustered.xml` |
| EditMode（全量） | **484/484** | `T7EditModeAll.xml` |
| PlayMode（仅 ClusteredDeferred） | 7/7 | `T7PlayModeClustered.xml` |
| PlayMode（全量） | **36/36** | `T7PlayModeAll.xml` |
| `ClusteredDeferredModeGateTests` 单跑 | 3/3 | `T5ModeGateTests.xml` |

PlayMode 覆盖：Lit + SimpleLit + Unlit + ComplexLit 混合场景、24 盏附加灯的 stencil/clustered A/B（阈值 mean ≤ 2/255、max ≤ 24/255）、运行时 `clusteredDeferredLighting` off→stencil 等价 / on→clustered 复现、临时 mask/lighting RT 在切换渲染器后释放、tile 宽度 8/16/32/64（由 `_FPParams1` 实测：640×360/8 灯→8，1280×720/48 灯→16，1920×1080/65 灯→32，3840×2160/65 灯→64）。

**变体与性能**：`T5VariantCount.json` 记录 8 个关键字组（2·4·2·5·2·2·2·2 = 1280）；`T7Perf.json` 从 `T5-runtime/player-d3d11/CdPerf.json` 汇总交叉点（首个更低的附加灯数 8；32 灯 GPU 比值 0.616；64 灯 0.254 vs 0.709 ms）；`T7PlayerSummary.json` 汇总 4 次 player 构建、48 灯 A/B（mean 0.785/255、max 9、>8 仅 5 px）与 RenderDoc 抓帧（80×45 dispatch）。

**边界**：player 证据产生于 `0505f10`/`bca237b`，早于 §7.5.4 的 job 完成顺序修复；该修复不改变 GPU 输入。开关的运行时验证在编辑器 PlayMode 完成，未在 player 内切换。mixed lighting-shadowmask 仍为 PARTIAL（见 §7.5.3）。Vulkan/Metal 未验证。详见 `report.md` 与 `KnownDifferences.md`。

### 7.6 归档与清理

1. 删除 T1–T6 的临时 fixture、临时 readback/调试 blit、临时场景/材质/GameObject、临时 Renderer/Pipeline 资产；保留 T7 的 `Assets/Tests/ClusteredDeferred` 专用资产。
2. 恢复默认 Pipeline/Renderer/场景/GoldenImages；确认未被手动测试改过。
3. 删除 `Builds/`、player 日志、临时 `.rdc`、`Library/`/`Temp/` 残留；保留 `BuildSummary.json`。
4. 确认运行时资源释放：cluster GraphicsBuffer/NativeArray、compute kernel、临时 RT 在 Renderer Dispose/相机销毁时释放（`ClusteredDeferredResourceLifecycleTests`）。
5. 把 `PLAN/TODO/11-LightCulling` 移到 `PLAN/Done/11-LightCulling`；不保留重复副本。
6. 更新 `PLAN/ACCEPTANCE.md`：按执行时最后一节编号新增下一节。
7. 修正相对链接；删除空目录；检查 `git status` 干净、`git diff --stat` 只含预期文件；新增资产 `.meta` 一并提交，删除资产时 `.meta` 同步删除。

### 7.7 文档一致性

- **五处**：本 HANDOFF、`report.md`、`PLAN/ACCEPTANCE.md`、`KnownDifferences.md`、`T1` 的契约/基线 JSON 的：
  - 决策 D1–D14；
  - 支持矩阵（MSAA/native RP/XR/foveated/GLES/Preview/Overlay/tile 宽度）；
  - AC 阈值与证据路径；
  - 证据/`.rdc` 预算；
  - 测试与产物归属。
- 任何一处改动同步其余四处；T8 逐项对照。
- AC 证据必须存在、无死链；未验证的平台/组合写“未验证”；缺失项标 PARTIAL/ACCEPTED，不声称 PASS。

### 7.8 AC → 证据映射

| AC | 主证据 |
|---|---|
| AC-1、AC-2 | `T1ClusterParity.json` + `ClusteredDeferredForwardParityTests` |
| AC-3 | `T1ClusterParity.json`、`T5ModeGateTests.xml` |
| AC-4 | `T3LightingDiff.json` |
| AC-5、AC-6 | `T4FeatureMatrix.json` + A/B PNG |
| AC-7 | `T3LightingDiff.json`（NaN/sky 计数） |
| AC-8 | `T6Coverage.json` |
| AC-9 | `T2ShaderCheck.txt`、`T5VariantCount.json` |
| AC-10 | `T5ModeGateTests.xml` |
| AC-11 | `T7Perf.json` |
| AC-12 | RenderDoc 摘要 + `.rdc` 记录 |
| AC-13 | `Invoke-ClusteredDeferredTests.ps1` 的 `*.xml` |
| AC-14 | T8 边界审计（`git diff --stat`） |
| AC-15 | `T7PlayerSummary.json` |
| AC-16 | `report.md`、`KnownDifferences.md`、ACCEPTANCE |

---

## 8. 风险与缓解

| 风险 | 影响 | 缓解 |
|---|---|---|
| `ForwardLights` 构建解耦回归 | forward/透明光照错误 | AC-1/AC-2 逐像素；T1 独立提交；保留 `m_UseForwardPlus` 原路径与逐物体索引 |
| `isForwardPlus` 用法混淆 | 附加光阴影被静默关闭，或透明缺失逐物体灯数据 | T1 区分“阴影启用”与“per-object flags”；AC-3/AC-13 覆盖 |
| 灯数组/可见灯上限不匹配 | 越界或静默截断 | T1 在 culling 前收紧 `maximumVisibleLights`；启动断言；超限告警 |
| `UseRenderPass`/native RP 未关闭 | GBuffer/深度绑定不同，kernel 行为错误 | T5 本帧强制 `useRenderPassEnabled=false` + `m_DeferredLights.UseRenderPass=false` 并告警（AC-10） |
| forward-only pass 与 mask 不一致 | 重复受光或物体消失 | 保留 forward-only pass；mask 以 stencil `MaterialUnlit` 保持 0；AC-15 覆盖 |
| Preview/Overlay/多相机 | 缓冲缺失或串帧 | T5 回退 stencil deferred；AC-10 |
| 材质类型 mask 未清/未绑定 | Lit 像素被跳过（黑）或分流错误 | mask 每帧 Clear 0；T5 绑定与生命周期测试；AC-10/AC-15 |
| mask pass 与 native RP/附件依赖冲突 | stencil test/RT 绑定错误 | v1 强制非 native RP；mask 与 compute 分两个 pass；T5 gate |
| GBuffer 覆盖不完整 | 该材质不走 clustered deferred | 不再导致漏渲染（mask 0 + 原 forward pass）；T6 仅作可选优化清单 |
| compute-safe 阴影/AO 子集编译失败 | kernel 无法编译/行为不一致 | T2 先做编译 gate；AC-9；失败时按 keyword 关单项并记录 |
| 全局索引与阴影槽位不一致 | 阴影错位 | 复用 Forward+ 已验证链路；T4 逐项 A/B |
| 现有 stencil 依赖未清理 | SSR/自定义效果异常 | T5 grep + 关闭开关 A/B；KnownDifferences |
| 浮点累加顺序不同 | A/B 被误判为错误 | AC-4/AC-5 用 mean/max 阈值并记录分布，不要求 bit-exact |
| `useStructuredBuffer` 未来变化 | 灯数据布局不匹配 | 启动断言 false；不满足时禁用并记录 |
| 资源释放遗漏 | 长时间运行泄漏 | `ClusteredDeferredResourceLifecycleTests`；T8 清理检查 |
| 灯少时更慢 | 误判“deferred 无用” | D12：交叉点报告；保留 stencil deferred 默认 |
| 相机栈/后处理/深度绑定差异 | player 与编辑器不一致 | T7 player 验证；AC-15 |
| 变体膨胀 | 编译时间/包体 | kernel 只用必要 keyword；报告变体数（AC-9） |

---

## 9. 工作量估算

1 名熟悉两条管线的资深图形工程师、桌面 D3D11、不做像素级对齐：

| 工作 | 人周 |
|---|---|
| T1 构建解耦 + 灯上限 + `isForwardPlus` 审计 | 1–2 |
| T2 compute-safe include（灯/阴影/AO/GBuffer 解码） | 1–2 |
| T3 kernel v1（像素并行 + 材质 mask + Lit/SimpleLit + 附加方向光，无阴影） | 1.5–2 |
| T4 阴影/cookie/layers/mixed/AO | 2–3 |
| T5 集成/边界/平台 gate + mask pass | 1–2 |
| T6 可选：自定义 shader GBuffer 覆盖 | 从核心成本移出；每个 0.5–2 天（线性） |
| T7 证据/性能矩阵 | 1–2 |
| T8 文档/验收/归档 | 0.5–1 |
| **核心合计（不含 T6）** | **6–10 人周** |
| 按本仓库验收强度上浮 30–50% | **9–15 人周** |
| GPU FPTL（可选 follow-up） | +2–4 人周 |

> 说明：mask pass 约 +0.5 人周，但取消 D9 的“全 opaque 补 GBuffer”约束，把 T6 的线性成本移出核心；两者相抵后核心区间不变。

---

## 10. 立即下一步

1. 按 T1 先做最小接线：`InitParams.clusteredDeferred`、`m_UseClusters` 拆分、`isClusteredLighting` 区分阴影启用、culling 前收紧可见灯上限；**保持 `_FORWARD_PLUS` 关闭**，验证 forward/透明逐像素不变。
2. 按 T3 先出 **材质类型 mask + 像素并行 kernel**：mask 的 0/1/2 分流先于光照调试验证；kernel 先跑无阴影 Lit/SimpleLit，再确认附加方向光与 cluster 偏移。
3. 按 T5 的最小子集确认 `EnqueueDeferred` 二选一、mask pass 插入点、**保留 forward-only**、`!UseRenderPass` gate 都成立；避免在错误帧序上调试光照。
4. 按 T4 加阴影/cookie/layers/mixed/AO，逐项单开关 A/B。
5. 不要先搬 `lightlistbuild.compute`；只有当 T7 的 profiler 证明 CPU 构图是瓶颈时，才启动 GPU FPTL，并保持缓冲布局不变。