# 11-LightCulling 交接：HDRP 光列表调研与 URP Clustered Deferred（URP BRDF）实施计划

> 状态：**调研完成，范围与决策冻结，未实施**
> 建立：2026-11-21
> 基线：`c37b9dd`（Record the DDGI plan revision commit）
> 结果入口：本文件；实施后追加 `report.md` 与 `validation/`
> 关联：`PLAN/Done/01-ScreenSpaceLighting`（共享深度/探针 atlas/证据格式）、`PLAN/Done/06-ValidationAllRDC`（`.rdc` 预算与证据卫生）、`PLAN/TODO/10-ContactShadow`（计划格式）
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
| D7 | kernel 每 tile 一个线程组（16×16 像素，1 像素/线程），一次写 lighting attachment；不使用 light-shape stencil、不做每灯全屏 pass | tile 内 wave 全同 tile，无 forward PS 的 tile 分歧；stencil 灯体积整套不移植 |
| D8 | kernel 只算直接光，`Blend One One, Zero One` additive 写 GBuffer3；间接光保持在 GBuffer pass | GBuffer3 已含 GI/emission；与 stencil deferred 的混合语义一致（`StencilDeferred.shader:158`） |
| D9 | v1 要求所有参与 deferred 的 opaque 材质都有 `UniversalGBuffer` pass；本模式下不启用 forward-only opaque | 避免“deferred 与 forward-only 像素分类”需要读 stencil；透明仍在 deferred 之后走现有 forward（不受影响） |
| D10 | 平台：D3D11/D3D12/Vulkan/Metal，要求 Compute + 可读 GBuffer/depth；GLES/WebGL 不支持；v1 不支持 native render pass（`_RENDER_PASS_ENABLED`）与 MSAA（deferred 本身已禁 MSAA，`UniversalRenderer.cs:367`）；XR/foveated 不在 v1 | 与 HDRP deferred 的 API 前提一致；减少分支与变体 |
| D11 | 构图仍在 CPU（URP Burst jobs）；GPU FPTL 为 follow-up，仅在 profiler 证明 CPU 构图成为瓶颈时做，且缓冲布局保持不变 | 先拿到最大收益/最小风险的一步；“lightlistbuild 移植”不是本轮前置 |
| D12 | 性能与 A/B 必须使用多灯重叠场景；`HDRPSample01/Assets/OutdoorsScene`（1 盏平行光）不作为结论依据 | 单灯场景下 tile 循环必然不如现有路径，会得出误导结论 |
| D13 | 代码边界：新增 kernel/include/pass 放 URP 包内独立目录；核心修改限于接入点（`ForwardLights`、`UniversalRenderer`、`UniversalRendererData` 及 Editor），逐项列入 §4.6 并审计 | 可整目录回滚；不让 HDRP 代码扩散进 URP 本体 |
| D14 | 不做 HDRP 的 7 张 GBuffer、material feature、面积光、体积雾、物理曝光、SSS、StackLit/Hair/Fabric、HD 阴影 atlas、HD 曝光/Volume | 本轮是“光照架构 + 光列表”，不是“渲染能力对齐” |

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
- **v1 必须在 clustered 模式跳过 `m_RenderOpaqueForwardOnlyPass`**（`EnqueueDeferred` 末尾），并强制 `!m_DeferredLights.UseRenderPass`；否则 D9（opaque 全覆盖 GBuffer）与 native RP 不支持这两条无法成立。
- **验证/测试/清理补全**：原 §7 只有场景与预算；已扩为既有测试、新增测试、player 验证、证据 schema、清理归档、文档一致性六节。
- **估算统一**：核心 6–10 人周；按 30–50% 验收上浮为 **9–15 人周**（原 8–14 与上浮比例不符）。

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
- v1 要求 opaque 全覆盖 GBuffer（D9），因此不需要“deferred/forward-only 像素分类”，也不需要读 stencil。

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
  - **clustered 时跳过 `EnqueuePass(m_RenderOpaqueForwardOnlyPass)`**（D9）；
  - 关闭开关时完全走原 `DeferredPass`，逐像素一致（AC-1）。
- 关键字：**不设置 `_FORWARD_PLUS`**（`ForwardLights.cs:429` 仍只由 `m_UseForwardPlus` 控制）；透明/forward-only 继续使用逐物体灯索引（`SetupPerObjectLightIndices` 仍执行，`ForwardLights.cs:630+`）。compute 只复用 cluster 缓冲与数据布局。
- native render pass：`m_DeferredLights.UseRenderPass` 必须为 `false`；若为 `true`，本特性在该相机上禁用并输出一次告警（D10）。D3D11 默认不满足 native RP 条件，风险主要在 Vulkan/Metal。

### 4.2 帧序（新增部分）

```
GBuffer Pass（写 GBuffer0-2、GBuffer3=GI+emission、depth、材质 stencil 位）
  ↓
CopyDepth / DeferredLights 绑定 _GBuffer0..2、_CameraDepthTexture
  ↓
Clustered Deferred Compute（新）：
  每 tile 一个 16×16 线程组 → 读 depth/GBuffer → 主光 → cluster 列表 → 直接光累加
  → Blend One One, Zero One 写 GBuffer3
  ↓
（原有下游：copy lighting 到 camera color / fog / 透明 forward / 后处理）
```

补充：

- 这是本版本 `UniversalRenderer` 的实际执行路径（legacy `ScriptableRenderPass`/`EnqueuePass`；无 RenderGraph 版本）。`m_DeferredLights.Setup(...)` 仍必须调用，否则 `_GBuffer0..2` 与附件绑定不成立。
- 原 `m_RenderOpaqueForwardOnlyPass` 的 event 是 `BeforeRenderingOpaques`，实际排在 deferred lighting 之后；本模式直接**不 enqueue**，不是靠 stencil 跳过。
- `_CameraDepthTexture` 的创建条件包含 `renderingMode == Deferred && !useRenderPassEnabled`（`UniversalRenderer.cs:790-791`）；v1 强制 `!UseRenderPass`，因此该纹理可用。
- 透明：在 deferred 之后照常走 forward，灯数据仍是逐物体（因为 `_FORWARD_PLUS` 未开）。

### 4.3 附件、混合与 alpha 语义

- 目标：与 `DeferredPass.Configure` 相同——lighting attachment（`GBuffer3`）+ depth（`Runtime/Passes/DeferredPass.cs:33-42`）。
- 混合：`Blend One One, Zero One`，与 stencil deferred 的 punctual pass 一致（`Shaders/Utils/StencilDeferred.shader:158`）；alpha 不被 kernel 改写。
- 不写 stencil；不改变材质位。
- 天空/背景：depth 达到 far 的像素直接 `return`，不写 lighting attachment（与 `DeferredShading` 的 early-out 语义一致，见 `StencilDeferred.hlsl:256-260` 的 subtractive 早退）。

### 4.4 kernel 伪代码

```
[numthreads(16,16,1)] // 一个线程组 = 一个 16×16 tile
void CSMain(uint3 groupID, uint3 threadID) {
    uint2 pixel = groupID.xy * 16 + threadID.xy;
    if (any(pixel >= _ScreenSize.xy)) return;

    float rawDepth = LOAD_TEXTURE2D_X(_CameraDepthTexture, pixel).x;
    if (rawDepth >= farValue) return; // sky

    float2 uv = (pixel + 0.5) * _ScreenSize.zw;
    float3 posWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);

    half4 g0 = LOAD_TEXTURE2D_X(_GBuffer0, pixel);
    half4 g1 = LOAD_TEXTURE2D_X(_GBuffer1, pixel);
    half4 g2 = LOAD_TEXTURE2D_X(_GBuffer2, pixel);
    half4 shadowMask = LOAD_SHADOWMASK(pixel);          // 仅 mixed lighting
    uint meshLayers  = LOAD_RENDERING_LAYERS(pixel);    // 仅 _LIGHT_LAYERS

    InputData inputData = InputDataFromGbufferAndWorldPosition(g2, posWS); // compute-safe 副本
    inputData.normalizedScreenSpaceUV = uv;
    BRDFData brdf = BRDFDataFromGbuffer(g0, g1, g2);
    uint materialFlags = UnpackMaterialFlags(g0.a);
    bool specularOff = materialFlags & kMaterialFlagSpecularHighlightsOff;

    half3 color = 0;
    // 1) 主光（方向光 + cascade/screen-space shadow）
    Light mainLight = GetMainLightCompute(shadowMask, ao);
    if (IsMatchingLightLayer(mainLight.layerMask, meshLayers))
        color += LightingPhysicallyBased(brdf, mainLight, inputData.normalWS, inputData.viewDirectionWS, specularOff);

    // 2) 附加光：cluster 位图（header 0），方向光已由 URP_FP_DIRECTIONAL_LIGHTS_COUNT 先行
    CdClusterIterator it = CdClusterInit(uv, posWS);   // header 0
    uint idx;
    while (CdClusterNext(it, idx)) {
        idx += URP_FP_DIRECTIONAL_LIGHTS_COUNT;
        Light l = CdGetAdditionalLight(idx, inputData.positionWS, shadowMask, ao); // 全局索引
        if (IsMatchingLightLayer(l.layerMask, meshLayers))
            color += LightingPhysicallyBased(brdf, l, inputData.normalWS, inputData.viewDirectionWS, specularOff);
    }

    // 3) 只输出直接光；由 Blend One One 与 GBuffer3 中原有 GI 相加
    return half4(color, 0);
}
```

说明：

- 线程组 16×16 与 tile 绑定，wave 内 tile 索引一致；不需要 HDRP forward 的 scalarization。
- 屏幕尺寸、深度参数与相机矩阵由 C# 显式上传；不要假设 compute 里存在 per-draw 视图常量（`ReflectionProbeFallback.hlsl:9-20`）。v1 非 XR，用 `LOAD_TEXTURE2D` 而非 `LOAD_TEXTURE2D_X`。
- 输出由 render target 的 `Blend One One, Zero One` 完成累加；kernel 不读 lighting attachment，避免与“GI 已在 GBuffer3”冲突。
- tile 尺寸与 tile 数量从 `_FPParams1.xy`（pixelRect.size / tileWidth）与 `_FPParams2.y` 读取；16 是 gate 后的实际值，不是硬编码常量。
- 深度转换复用 URP 的 `_ZBufferParams`/`LinearEyeDepth`/`ComputeWorldSpacePosition`，保留 reversed-Z 与正交分支；不要自己假设 `[0,1]` NDC。
- `URP_FP_DIRECTIONAL_LIGHTS_COUNT` 来自 `_FPParams0.w`（`Input.hlsl:131`）；header 0 的最大实体索引是 `URP_FP_PROBES_BEGIN - 1`，而 `URP_FP_PROBES_BEGIN == _FPParams0.z`（`Input.hlsl:128`、`Clustering.hlsl:75`）。偏移与 off-by-one 由 T2 的 parity 测试锁定。
- 材质 flag 与 stencil deferred 对齐：`kMaterialFlagSpecularHighlightsOff`（`:305`）、`kMaterialFlagReceiveShadowsOff`（`:137`）、`kMaterialFlagSubtractiveMixedLighting`（`:256-260` 的早退）。
- 关键字集合收敛为：`_MAIN_LIGHT_SHADOWS*`、`_ADDITIONAL_LIGHT_SHADOWS`、`_SHADOWS_SOFT*`、`_LIGHT_LAYERS`、`_LIGHT_COOKIES`、`_SCREEN_SPACE_OCCLUSION`、`_DEFERRED_MIXED_LIGHTING`、`SHADOWS_SHADOWMASK`、`_GBUFFER_NORMALS_OCT`；不编 `_ADDITIONAL_LIGHTS_VERTEX`、`_SURFACE_TYPE_TRANSPARENT`、foveated 组合。
- AO 只乘到直接光；不实现 `_DEFERRED_FIRST_LIGHT` 的 alpha 技巧（该技巧只服务 stencil deferred 的 baked occlusion 抵消）。
- 每 tile 循环上限以 `MAX_LIGHTS_PER_TILE` 与位图容量为准；不要用固定 `for(i<32)` 假设。
- `InputDataFromGbufferAndWorldPosition` 不填 `normalizedScreenSpaceUV`（`UnityGBuffer.hlsl:263-279`），必须手工补。
- 若 tile 尺寸被 CPU 选为 8/32，本路径按 D10 的 gate 关闭并告警，不尝试适配。

### 4.5 compute-safe include 边界

新增 `ShaderLibrary/ClusteredDeferred.hlsl`，只包含：

- `URP_ZBins`/`urp_Tiles`/`_FPParams*` 的 cbuffer 声明与 `CdClusterInit/Next`；容量常量与 `Input.hlsl` 一致（`MAX_ZBIN_VEC4S=1024`；`MAX_TILE_VEC4S` 按 `MAX_VISIBLE_LIGHTS<=32` 取 1024/4096）；
- `_AdditionalLights*` 常量数组（长度 `MAX_VISIBLE_LIGHTS`）、`DistanceAttenuation`/`AngleAttenuation`、`CdGetAdditionalLight`；
- `URP_FP_ZBIN_SCALE/OFFSET`、`URP_FP_TILE_SCALE/COUNT`、`URP_FP_WORDS_PER_TILE`、`URP_FP_DIRECTIONAL_LIGHTS_COUNT` 的宏（值来自 `_FPParams0/1/2`，与 `Input.hlsl:121-136` 一致）；
- 主光/附加光阴影采样所需的最小全局与 PCF 函数（从 `Shadows.hlsl` 复制必要片段）；
- GBuffer 解码的两个纯函数（`BRDFDataFromGbuffer`、`InputDataFromGbufferAndWorldPosition` 的 compute-safe 副本，补 `normalizedScreenSpaceUV`）；
- GTAO 直接光 AO 的唯一函数（`_SCREEN_SPACE_OCCLUSION`）。

不得 include：URP 的 `ShaderLibrary/Input.hlsl`、`ShaderLibrary/Lighting.hlsl`、`ShaderLibrary/Core.hlsl` 与 `Shadows.hlsl` 整体（均拉 per-draw state）；允许 include core 包的 `Common.hlsl`/`ShadowSamplingTent.hlsl` 等无 per-draw 依赖的头。

### 4.6 新增 / 修改文件

新增（URP 包内）：

| 文件 | 作用 |
|---|---|
| `Runtime/ClusteredDeferred/ClusteredDeferredPass.cs` | `ScriptableRenderPass`；`ConfigureTarget` 与 `DeferredPass` 对齐；`Configure`/`Execute`（本版本无 RenderGraph 路径） |
| `Runtime/ClusteredDeferred/ClusteredDeferredResources.cs` | kernel/材质/参数 id、每相机状态（若需要） |
| `Shaders/Utils/ClusteredDeferred.compute` | kernel + 关键字集合 |
| `ShaderLibrary/ClusteredDeferred.hlsl` | compute-safe 子集（§4.5） |
| `Tests/.../ClusteredDeferred*` | 编译检查、矩阵测试（§7） |

修改（限定接入点，逐项审计）：

| 文件 | 改动 |
|---|---|
| `Runtime/UniversalRendererData.cs` | 新增 `[SerializeField] bool m_ClusteredDeferredLighting`（默认 false，旧资产无需迁移）；`OnValidate` 断言仅 Deferred 有效 |
| `Editor/UniversalRendererDataEditor.cs` | 开关 UI；Rendering Mode != Deferred 时禁用并提示 |
| `Runtime/UniversalRenderer.cs` | (1) `forwardInitParams.clusteredDeferred = ...`；(2) `m_Clustering = ForwardPlus || clustered`（只影响 debug/中间纹理）；(3) `EnqueueDeferred` 二选一 pass；(4) clustered 时跳过 `m_RenderOpaqueForwardOnlyPass`；(5) `SetupCullingParameters` 收紧 `maximumVisibleLights`（当前 Deferred 为 `0xFFFF`，`:1432`）；(6) 强制 `!m_DeferredLights.UseRenderPass`；(7) 新 pass 带 `ProfilingSampler`，便于 Frame Debugger/Profiler 取证 |
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
| native render pass | 不支持 | `UseRenderPass == true` 时该相机回退 stencil deferred 并告警；Vulkan/Metal 若默认开启 native RP，本特性在这些相机上不可用 |
| XR / foveated | 不支持 | follow-up |
| 正交相机 | 支持（zbin 有线性分支） | 纳入 AC |
| GLES/WebGL | 不支持 | 平台 gate |
| Preview / Overlay 相机 | 回退 stencil deferred | 预览/Overlay 的灯数据与附件组合未验证；不猜测，直接走原路径 |
| 多相机 | 支持，顺序执行 | 聚类缓冲是全局单份；依赖每相机 `PreSetup` 重新构建；若 Overlay 相机不构建则回退并告警（T5 验证） |
| tile 宽度 != 16 | 该帧禁用本特性并告警 | kernel 线程组固定 16×16；不尝试适配 8/32 |
| `useStructuredBuffer == true` | 不支持 | 本 fork 恒为 false（`RenderingUtils.cs:74-87`）；启动时断言 |
| Forward-only opaque | 不支持 | D9；该 pass 不 enqueue |
| 深度纹理 | 必需 | v1 依赖 `!UseRenderPass` 下的 `_CameraDepthTexture` |

### 4.8 与 stencil deferred 的已知差异

1. 灯体积光栅化与 early-z 拒绝被移除；改为每像素列表循环。灯少/大体积时可能更慢。
2. 每像素读取 tile 列表（常量缓冲位图）与计算 tile/zbin 索引，存在固定开销。
3. forward-only opaque 不支持（D9）；材质缺 GBuffer pass 时不渲染（见 §8 风险）。
4. **stencil 只写不读**：GBuffer pass 仍写材质位（`GBufferPass.cs:58-61`），但本路径不写 `StencilLight`、也不读 stencil 做像素分类；依赖这些位的自定义效果会看到 0，需审计并记入 KnownDifferences。
5. 混合光照的逐灯早退（`_LightFlags`/subtractive）以 per-pixel 方式处理；`_RECEIVE_SHADOWS_OFF` 需在 kernel 内按材质 flag 跳过阴影；语义对齐写入 KnownDifferences。
6. 浮点累加顺序不同：stencil deferred 是每灯单独 pass 的加法，kernel 是 tile 内循环后一次加法；阈值要允许极小差异，不要求 bit-exact（AC-4/AC-5）。
7. alpha 不改写（`Blend ... Zero One`），与现有 AO/depth 语义一致；若未来有 pass 依赖 lighting alpha 需另立任务。

---

## 5. 任务清单（T0–T8）

### 5.1 依赖图与文件锁

```
T0(冻结)
 └→ T1(构建解耦 + 灯上限 + isForwardPlus 审计)
     └→ T2(compute-safe include)
         └→ T3(kernel v1 无阴影)
             ├→ T4(阴影/cookie/layers/mixed/AO)  [kernel/include]
             └→ T5(集成/边界/平台 gate)          [C#/Editor]
                 └──────────┬──────────────────────┘
                            └→ T6(GBuffer 覆盖清单与自定义 shader)
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
| 各材质 shader 的 GBuffer pass | T6 | 仅 T6；不得与 T7 的测试资产同批修改 |
| `Assets/Tests/ClusteredDeferred/**` | T7 | 仅 T7 提交测试资产 |
| `report.md`、`KnownDifferences.md`、`PLAN/ACCEPTANCE.md` | T8 | 仅 T8 |

### 5.2 任务定义

| ID | 任务 | 主要文件 | 退出条件 | 证据 |
|---|---|---|---|---|
| T0 | 冻结决策与基线 | 本文件 | D1–D14 入库；基线 `c37b9dd` 记录 | — |
| T1 | 构建解耦 + 灯上限 + `isForwardPlus` 审计 | `ForwardLights.cs`、`UniversalRenderer.cs`、`UniversalRenderPipeline.cs` | (a) `InitParams.clusteredDeferred` 生效；(b) 6 处 `m_UseForwardPlus` gating 拆为 `m_UseClusters`，关键字与逐物体索引仍只受 `m_UseForwardPlus` 控制；(c) `isClusteredLighting` 只用于阴影启用；(d) Deferred 可见灯上限收紧到 `maxVisibleAdditionalLights`；(e) 关闭开关时 forward/透明逐像素一致 | `T1ClusterParity.json`：开关关闭图像差分=0；开启后 `URP_ZBins/urp_Tiles/_FPParams*` 与 Forward+ 同场景逐元素一致；`_AdditionalLightsIndices` 仍存在 |
| T2 | compute-safe include：灯数组、衰减、cluster 迭代、GBuffer 解码、阴影子集 | `ClusteredDeferred.hlsl` | 编译 0 错误；header 0/1 语义、方向光偏移、`URP_FP_PROBES_BEGIN` 边界（off-by-one）与 `Clustering.hlsl:25-130` 一致 | `T2ShaderCheck.txt`（0 消息）、`T2IterParity.json`（逐像素灯集合与 PS 一致） |
| T3 | kernel v1：无阴影、点/聚光/方向光、direct-only additive | `ClusteredDeferred.compute`、`ClusteredDeferredPass.cs` | 无阴影场景与 stencil deferred 差异在阈值内；天空不写；无 NaN；tile 参数来自 `_FPParams1/2`；深度/正交/翻转 Z 正确 | `T3LightingDiff.json`（mean/max/NaN）、A/B PNG |
| T4 | 阴影/cookie/layers/mixed/AO | kernel + include | 每项单开关 A/B；`_RECEIVE_SHADOWS_OFF` 跳阴影；subtractive 早退；AO 只作用于直接光；cookie/layer 与 forward 一致 | `T4FeatureMatrix.json`、A/B PNG |
| T5 | 集成/边界：renderer data 开关、pass 替换、跳过 forward-only、`UseRenderPass` gate、预览/overlay、tile 宽度、平台 gate、变体 | `Runtime/UniversalRendererData.cs`、`Editor/UniversalRendererDataEditor.cs`、`UniversalRenderer.cs` | 开关可运行时切换；不满足条件时明确回退/禁用并告警；变体数可报告 | `T5ModeGateTests.xml`、`T5VariantCount.json` |
| T6 | opaque 材质 GBuffer 覆盖清单；缺 pass 的自定义 shader 补齐 | shader 清单 + 各 shader | 清单落盘；编辑器扫描能列出缺 pass 的材质；本模式下无“漏渲染”材质；每个新增 pass 有编译与视觉验证 | `T6Coverage.json`、编译检查、A/B PNG |
| T7 | 测试、证据与性能矩阵 | `validation/` | 既有测试全绿；新测试全绿；A/B/交叉点/RenderDoc/Player 完成 | `T7Perf.json`、`T7PlayerSummary.json`、`*.xml`、PNG |
| T8 | 文档/验收/归档 | `report.md`、`KnownDifferences.md`、`PLAN/ACCEPTANCE.md` | 五处文档一致；产物清理；TODO→Done | `git status`/`git diff --stat`、`report.md` |

### 5.3 任务冲突/遗漏审计

**已识别的遗漏（本版补入）**：

- `isForwardPlus` 的四处用途（阴影启用 vs 逐物体灯数据）未区分；若只改 `m_UseForwardPlus`，会造成“阴影被静默关闭”或“透明灯数据缺失”。
- `m_Clustering` 被误当作构建开关（它只影响 debug/中间纹理）。
- 未指定 `m_RenderOpaqueForwardOnlyPass` 在 clustered 模式下不 enqueue。
- 未指定 `UseRenderPass` 的强制关闭与回退。
- 未指定 `maximumVisibleLights` 必须在 culling 前收紧（Deferred 当前为 `0xFFFF`）。
- 未覆盖 Preview/Overlay 相机、多相机共享缓冲、tile 宽度 gate、变体 stripping、`.meta` 与测试资产归属。

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
| AC-8 | GBuffer 覆盖清单落盘；本模式下不存在无 GBuffer 的 opaque 材质；发现问题时有明确告警 |
| AC-9 | compute 在 D3D11/D3D12/Vulkan/Metal 编译 0 错误；无 `USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA` 分支；报告变体数量；GLES/WebGL 被明确排除 |
| AC-10 | 支持矩阵如实落盘：MSAA/native RP/XR/foveated/GLES 不支持；Preview/Overlay 回退；多相机顺序构建；tile 宽度 !=16 时禁用；`useStructuredBuffer=true` 禁用；正交支持并有测试 |
| AC-11 | 性能：多灯重叠场景给出 stencil deferred / clustered deferred / forward+ 的交叉点；记录 CPU 构图与 GPU 时间 |
| AC-12 | RenderDoc：dispatch 尺寸、tile 布局、每像素灯数分布、A/B diff；`.rdc` 遵守预算 |
| AC-13 | 既有 SSL/GTAO/SSR/折射与 EditMode/PlayMode 全绿；§7.3 新测试全绿；shader 编译检查 0 消息；变体计数变化有记录 |
| AC-14 | 代码边界审计：核心文件改动仅 §4.6 清单；`DeferredPass.cs`/`DeferredLights` 每灯 pass/GBuffer 布局未改；无 HDRP 面光源/7 GBuffer/material feature 引入 |
| AC-15 | 回退：关闭开关无需重编 shader/重建 renderer；clustered 模式下 forward-only 不重复受光；player build 不丢 shader；Preview/Overlay 正确回退 |
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
| `ClusteredDeferredIterationParityTests` | EditMode（包内） | header 0/1 语义；方向光偏移；灯数 0/1/31/32/33；探针列表不串扰；sky 像素 |
| `ClusteredDeferredBuildParityTests` | EditMode（包内） | Deferred 模式构建 cluster；开关关闭不构建；`_FORWARD_PLUS` 仍为 false；`_AdditionalLightsIndices` 仍存在；缓冲与 Forward+ 同场景一致 |
| `ClusteredDeferredCullingCapTests` | EditMode（包内） | `maximumVisibleLights` 在 culling 前收紧；超 `maxVisibleAdditionalLights` 时告警；数组不越界 |
| `ClusteredDeferredModeGateTests` | EditMode/PlayMode（包内+项目） | `UseRenderPass`、MSAA、XR、foveated、GLES、Preview/Overlay、tile 宽度 !=16、`useStructuredBuffer=true` 的禁用/回退 |
| `ClusteredDeferredLightingEqualityTests` | PlayMode（项目）+ 视觉 | 无阴影/有阴影/cookie/layers/mixed/AO/正交；阈值见 AC-4/AC-5 |
| `ClusteredDeferredForwardParityTests` | PlayMode（项目） | 开关关闭逐像素一致；开关开启时 forward/透明与基线一致 |
| `ClusteredDeferredCoverageTests` | EditMode（包内） | 材质扫描：缺 GBuffer pass 的材质清单；告警 |
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
| `UseRenderPass`/native RP 未关闭 | GBuffer/深度绑定不同，kernel 行为错误 | T5 强制 `!UseRenderPass`；不满足则回退并告警（AC-10） |
| forward-only pass 未跳过 | 像素被重复受光/分类错误 | T5 在 `EnqueueDeferred` 不 enqueue；AC-15 覆盖 |
| Preview/Overlay/多相机 | 缓冲缺失或串帧 | T5 回退 stencil deferred；AC-10 |
| tile 宽度被 CPU 选为 8/32 | kernel 线程组映射错误 | v1 强制 16；不满足时关闭特性并告警（AC-10） |
| GBuffer 覆盖不完整 | 材质漏渲染/错误光照 | T6 先出清单；编辑器扫描 + 运行时告警；必要时禁用该材质 |
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
| T3 kernel v1（无阴影） | 1.5–2 |
| T4 阴影/cookie/layers/mixed/AO | 2–3 |
| T5 集成/边界/平台 gate | 1–2 |
| T6 自定义 shader GBuffer 覆盖 | 每个 0.5–2 天（线性） |
| T7 证据/性能矩阵 | 1–2 |
| T8 文档/验收/归档 | 0.5–1 |
| **核心合计（不含 T6）** | **6–10 人周** |
| 按本仓库验收强度上浮 30–50% | **9–15 人周** |
| GPU FPTL（可选 follow-up） | +2–4 人周 |

---

## 10. 立即下一步

1. 按 T1 先做最小接线：`InitParams.clusteredDeferred`、`m_UseClusters` 拆分、`isClusteredLighting` 区分阴影启用、culling 前收紧可见灯上限；**保持 `_FORWARD_PLUS` 关闭**，验证 forward/透明逐像素不变。
2. 按 T5 的最小子集先确认 `EnqueueDeferred` 二选一、跳过 forward-only、`!UseRenderPass` gate 都成立，再写 kernel；避免在错误帧序上调试光照。
3. 按 T2 建立 compute-safe include，并在测试 kernel 中对齐 PS 的灯集合（header 0/1、方向光偏移、off-by-one）。
4. 按 T3 出第一版无阴影 A/B，确认“翻译函数”这条路成立，再进入阴影与覆盖清单。
5. 不要先搬 `lightlistbuild.compute`；只有当 T7 的 profiler 证明 CPU 构图是瓶颈时，才启动 GPU FPTL，并保持缓冲布局不变。