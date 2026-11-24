# 10-ContactShadow 交接：HDRP Contact Shadows 调研与 URP 包内 Renderer Feature 迁移计划

> 状态：**T0、T1、T3、T4、T7 已通过；T2 资源门禁通过，Frame Debugger 取证 PARTIAL；下一步 T5 光照接入**（[T2 证据](#t2-evidence)、[T3 单灯 GPU 验收](#t3-evidence)、[T4 逐灯映射](#t4-evidence)）。已核验 [Feature Inspector](validation/T1/T1_FeatureInspector.png)、[Volume Inspector](validation/T1/T1_VolumeInspector.png) 和 [Light Inspector](validation/T1/T1_LightInspector.png)。T1：[菜单添加、移除与域重载 3/3](validation/T1/EditMode.xml)、[序列化引用](validation/T1/FeatureImport.json)、[既有 EditMode 184/184](validation/T1/Regression.xml)、[PlayMode 6/6](validation/T1/PlayMode.xml)。临时资产已删除；尚未接入 Compute。实施基线 `cfbb3d4`；见 [契约](validation/P0/Contract.md) 与 [基线记录](validation/P0/Baseline.json)。D3D11：EditMode 184/184、PlayMode 6/6、T12 Forward 34/34、Forward+ 34/34、Deferred 35/35。
> **2026-11-24 所有者修订（优先于下方旧 stencil 描述）**：本 fork 的 Deferred 是聚类 compute，不存在 stencil 路径。T7 只接受真实 `renderingModeActual`，不接受 Forward fallback。T5 允许在 `Shadows.hlsl` 外最少量修改聚类 Deferred shader；仍不修改已有核心 C#、不加材质关键字。D10 对本 fork 按“Asset 允许附加阴影且该灯有有效阴影切片”执行，主光不限；不引入 T10。
> 建立：2026-11-19
> 基线：`cedd1bb`（Run SSR approximation at half resolution when enabled）
> 结果入口：本文件；实施后追加 `report.md` 与 `validation/`
> 关联：`PLAN/Done/01-ScreenSpaceLighting`（共享深度金字塔/XR/证据格式）、`PLAN/Done/06-ValidationAllRDC`（`.rdc` 预算）、`PLAN/TODO/09-DDGI_improve`（计划格式）
> 外部参考：`E:\workspace\GitHub\HDRPSample01\Library\PackageCache\com.unity.render-pipelines.high-definition@14.0.12`（HDRP 14.0.12 源码）
> 本文行号基于上述 HDRP 包与基线 `cedd1bb`；改动后以符号名为准。

---

## 0. 一页结论

**目标**：把 HDRP 14.0.12 的屏幕空间接触阴影迁移到本仓库的 URP 14.0.12 fork。Contact Shadows 以 **URP 包内 Renderer Feature** 的方式放在：

```
Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ContactShadows
```

通过 **Universal Renderer Data → Add Renderer Feature** 接入，通过 **Volume Profile → Shadowing/Contact Shadows** 开启，通过灯上的 **Contact Shadow Light** 组件逐灯启用。

**调研结论（一句话）**：HDRP 从相机深度重建世界坐标，把“表面 → 灯光”的一小段射线投影到屏幕 UV + NDC z，沿屏幕空间做固定步数 raymarch；命中深度即视为遮挡，结果按“每灯 1 bit”打包进一张全分辨率 `R32_UInt`，在光照循环里与 shadow map 取 `min`。

**与用户描述的对应关系**：

| 用户描述 | HDRP 的实际做法 | 迁移结论 |
|---|---|---|
| raymarch 在屏幕 UV 空间，因为要采样深度图 | 起点/终点先在世界空间构造，再投影到裁剪空间，除以 w 后转到 UV；之后每步只在 UV + NDC z 上走，用 `Load` 读深度 | 一致。世界空间只用于构造射线两端 |
| 方向是灯光方向的反方向 | 射线从表面**指向灯光**：方向光为 `-light.forward`，点/聚光为 `normalize(lightPos - positionWS)` | 一致。注意这是“灯光传播方向的反方向”，即灯光的入射方向 |
| 只走一小段距离 | `rayLength = length * max(0.5, linearDepth * distanceScaleFactor)`，默认 `length = 0.15 m` | 一致。远距离由 shadow map 接管 |

**冻结决策（D1–D12）**：

| ID | 决策 | 理由 |
|---|---|---|
| D1 | Feature 代码放在 URP 包内 `Runtime/RendererFeatures/ContactShadows`，Editor 代码放在 `Editor/RendererFeatures/ContactShadows` | 与现有 `ScreenSpaceLighting` / `GroundTruthAmbientOcclusion` 的结构一致；作为 URP 内置 Renderer Feature 交付 |
| D2 | 复用包内 internal 基础设施：`ScreenSpaceDepthPyramidSystem`、`ScreenSpaceDepthPyramid`、`PackedMipChainInfo`、`CameraHistoryStore`、`ScreenSpaceCapability`、`ScreenSpaceLightingXr`、`ScreenSpaceRenderUtils` | 同属 URP Runtime 程序集，可直接使用；不新增 `InternalsVisibleTo`，不改共享代码 |
| D3 | 深度源使用共享深度金字塔的 **mip 0（全分辨率）+ mip 1（半分辨率）**：像素输入用 mip 0，射线首采 mip 1、命中后同一个 t 读 mip 0 确认；不新建私有深度 pyramid | 与 HDRP 的 depth pyramid 读取方式一致；与 SSL/GTAO 复用同一份金字塔；避免重复 downsample |
| D4 | 逐灯启用用 Feature 目录内的 `ContactShadowLight` 组件，不修改 `UniversalAdditionalLightData` / 灯 Inspector | 把逐灯配置留在 ContactShadows 功能目录内；已有 URP C# 文件零修改 |
| D5 | 已有 URP C# 文件 **0 修改**；新增**功能 C#** 全部位于 `Runtime/RendererFeatures/ContactShadows`（及对应 Editor 目录）；测试脚本按 §5.3 放在测试目录，不计入功能代码 | 边界清晰，可整目录删除回滚 |
| D6 | URP 本体 shader 只保留一个接入点：新增 `ShaderLibrary/ContactShadows.hlsl`，并在 `Shadows.hlsl` 的 `MainLightShadow` / `AdditionalLightShadow` 末尾各加一个 `min` 块 | 内置 Lit/SimpleLit/ComplexLit/Particles/Terrain 都经过这两个函数；这是覆盖内置材质的唯一最小钩子 |
| D7 | 输出全分辨率 `R32_UInt`；低 24 bit 每灯 1 bit，高 8 bit 全局 fade；槽位按参与顺序分配：主光（若参与）优先 `bit 0`，附加光随后；总计最多 24 盏 | 与 HDRP 打包格式一致；URP 主光/附加光分开，但顺序分配可避免无主光时浪费槽位 |
| D8 | 材质侧不新增 `_CONTACT_SHADOWS` shader 变体；始终声明/采样，禁用时掩码为 0 + 绑定 1×1 中性 `R32_UInt`，`mask==0` 时在 `Load` 前返回 | 与 HDRP `_ContactShadowTexture` 设计一致；关闭时零纹理读取，不给所有 Lit 变体增加关键字 |
| D9 | 只作用于不透明表面；材质侧用 `_SURFACE_TYPE_TRANSPARENT` 编译期排除；`AfterRenderingOpaques` 后绑定中性纹理并清零掩码 | HDRP 明确“Transparents have no contact shadow information” |
| D10 | Deferred 附加光的接触阴影只在该帧 `_ADDITIONAL_LIGHT_SHADOWS` 变体实际启用时生效：Asset 开启 Additional Light Shadows，且 Off 变体被 strip，或**当前这盏附加光**投射阴影（`shadows != None && shadowLightIndex >= 0`）。`shadows=None` 的灯即使同帧其他灯在投影也不会获得接触阴影；主光不受此限制。若要支持 Asset 关闭附加光阴影或 Off 变体未 strip 时的 Deferred 附加光，另开 T10，**不纳入本轮** | 保持已有 URP C# 零修改；显式记录唯一的 deferred 边界 |
| D11 | 仅支持裁剪空间 z ∈ [0,1] 的 API：D3D11 / D3D12 / Vulkan / Metal；要求 Compute + `R32_UInt` LoadStore。材质侧 `ContactShadows.hlsl` 用 `SHADER_API_*` 宏包裹，其他 API 直接返回 1，避免 GLES/WebGL 变体编译失败 | HDRP 的 NDC 深度比较公式依赖该约定；运行时 gate 与 shader 编译 guard 必须一致 |
| D12 | **不涉及** raytracing / raygeneration / DXR / RT shadow；不生成 `.raytrace`，不创建 TLAS；默认不新增 `.rdc` | 本轮明确排除 raytracing；证据以 JSON/XML/PNG/Frame Debugger 为主 |

**验收摘要**：AC-1…AC-16（§7）。核心是：Feature 能从 Renderer Data 导入、Volume 能开启、灯组件能逐灯启用；关闭时零图像差；三模式/三灯型正确（Deferred 附加光按 D10 限制）；24 灯槽位正确；已有 URP C# 文件零修改、已有 URP shader 只改 `Shadows.hlsl`；共享深度金字塔只构建一次；无 raytracing；测试与产物善后完成。

**本版复核修正（相对上一稿）**：

- Feature 位置从项目侧 `Assets/RenderFeatures/ContactShadows` 改为 **URP 包内** `Runtime/RendererFeatures/ContactShadows`，与 SSL/GTAO 一致。
- 重新允许并采用包内 internal 共享基础设施：共享深度金字塔（mip 1 首采 + mip 0 确认）、`CameraHistoryStore`、`ScreenSpaceCapability`、`ScreenSpaceLightingXr`。
- 删除私有深度 downsample pass / `cameraDepthTargetHandle` 方案；深度直接来自共享金字塔 mip 1/mip 0。
- 已有 URP C# 文件仍然零修改；新增**功能 C#** 位于 ContactShadows 目录；测试脚本按 §5.3 放置，不计入功能代码。
- 保留上一版已补齐的：Renderer/Volume/灯组件导入验收、无关键字、24 槽位、Deferred 限制、测试/产物/文档善后、任务冲突审计。
- 明确 raytracing / raygeneration / DXR 不在本轮，也不作为 follow-up 任务。

**2026-11-19 复核修正（测试/边界/善后）**：

- 槽位改为“主光优先、附加光随后”的顺序分配；无主光时第一盏附加光可用 `bit 0`，不再固定浪费。
- `Shadows.hlsl` 增加 `_RECEIVE_SHADOWS_OFF` 与 `lightIndex` 边界保护；Forward/Deferred 的 Receive Shadows 语义统一，并写入 AC-10/KnownDifferences。
- 明确 Active 帧 ComputePass（208）与 CleanupPass（300）必须成对；ComputePass 负责真实全局绑定，CleanupPass 负责中性/零归零；Inactive/资源缺失状态写入 §4.1 状态表。
- `_ContactShadowLightBuffer` 固定 32 B 布局：`float3 position; uint type; float3 direction; uint mask;`；该 buffer 的 `mask` 直接写 `uint`。全局 `_AdditionalLightsContactShadowMasks` 是 float 数组，元素写 `math.asfloat(1u << slot)`，shader 端 `asuint`。
- `ContactShadowLight` 只查 Light 所在 GameObject；补 `[AddComponentMenu]`、子父/非 Light/Rectangle/disabled 行为。
- 任务 DAG 改为 T1→T2→T3、T1→T4、T3+T4→T5；新增共享文件串行化（Feature T1→T2→T5→T6、Pass T3→T5、测试资产仅 T7、ACCEPTANCE 仅 T9）。
- T1 不提交 Pipeline/Renderer/Volume/Scene 测试资产（临时 fixture `TearDown` 删除，只提交 import test 脚本）；T7 唯一提交测试资产；T6 只做序列化检查；T8 负责 player/证据；T9 负责 ACCEPTANCE/KnownDifferences/归档。
- 依赖重排：T7 验证资产在 T4 后先行，T5 依赖 T3/T4/T7；T7 只提交资产/脚本，矩阵执行归 T8。
- 测试改动补：PlayMode 新 asmdef、非 Development build、SRP Batcher/变体数量、ReceiveOff/Deferred 限制测试、Frame Debugger 超时兜底。
- 证据/善后补：PNG ≤80 张/≤45 MB、`validation/` 总预算 ≤50 MB、日志 local-only、`.rdc` 默认 0（批准时最多 1 个 ≤20 MiB）、BuildSummary、ACCEPTANCE 执行时新增下一节（当前 §14→§15）、KnownDifferences/report 入库。
- AC-10/11/13/14/15/16 同步收紧；AC-13 的 raytracing grep 限定本轮文件。
- 复核修补：步骤 5 深度采样按 `tracingHalfRes` 选择 mip 1/mip 0；API guard 片段改为 `Texture2D`/`ContactShadowLoad`/函数在 guard 内、CBUFFER/解包函数在 guard 外；T9 补 `.rdc` 的 `git ls-files` 检查；KnownDifferences 补 `ContactShadowLight` 组件的逐灯启用 UX 差异。

---

## 1. 目标与非目标

### 1.1 目标

1. 复刻 HDRP 14.0.12 的屏幕空间接触阴影算法与视觉行为（参数、fade、bias、thickness、dither、屏外遮罩、每灯掩码）。
2. 以 **URP 包内 Renderer Feature** 交付：可从 Universal Renderer Data 的 “Add Renderer Feature” 菜单导入；可从 Volume Profile 的 `Shadowing/Contact Shadows` 开启；可用灯上的 `Contact Shadow Light` 逐灯启用。
3. 在 Forward / Forward+ / Deferred 三条路径中生效，覆盖方向光、点光、聚光。
4. 复用现有共享深度金字塔与每相机资源框架；不重复构建深度金字塔。
5. 已有 URP C# 文件零修改；URP shader 只新增一个头文件、修改一个 `Shadows.hlsl`。
6. 提供可复现证据：Feature 导入、Volume 开关、逐灯开关、三模式 A/B、参数矩阵、性能、回归、测试与产物善后。

### 1.2 非目标（本轮明确不做）

| 项 | 原因 | 后续 |
|---|---|---|
| HDRP 的 Ray Traced Contact Shadows / 任何 raytracing / raygeneration | 用户明确排除；本项目 DDGI 已占用 DXR 预算 | 不在本轮，也不作为 follow-up 任务 |
| 透明物接触阴影 | HDRP 不提供；深度缓冲是 opaque 深度 | follow-up |
| HDRP Quality 分级 | URP 无等价 Volume 框架；直接暴露 `sampleCount` | follow-up |
| 逐灯 length / distanceScaleFactor | HDRP 也只有逐灯 enable；其余是 Volume 全局 | 不计划 |
| Area Light（Rectangle） | URP 不渲染实时 area light | 不计划 |
| Forward+ tile / 逐像素灯光列表加速 | v1 每像素循环最多 24 盏接触阴影灯；先保证正确性 | follow-up（性能） |
| 接触阴影时域累积/降噪 | HDRP 只做 dither，不做时域累积 | 不计划 |
| 半分辨率输出 + 上采样 | HDRP 输出全分辨率；半分辨率会改变视觉 | follow-up（可选） |
| Asset 关闭 Additional Light Shadows，或当前附加光 `shadows=None` 且 Off 变体未 strip 时的 Deferred 附加光接触阴影 | 需要改 URP 核心 C#（`DeferredLights`/`StencilDeferred`） | 可选 T10，不纳入本轮 |
| 移动端 / GLES / OpenGL / WebGL | NDC z 约定不同；Compute/格式不保证 | 不在本轮 |
| ShaderGraph 自定义节点 / 手写 shader 的接触阴影 | 只覆盖经过 URP 阴影函数的内置 Lit 路径 | follow-up |

### 1.3 成功标准

- Feature：能在 Universal Renderer Data 中导入/移除；Volume 组件能添加/开关；灯组件能逐灯开关；player build 不丢资源。
- 功能：接触暗部方向正确；方向光/点光/聚光均生效；逐灯关闭 shadow map 时仍生效；Deferred 附加光按 D10 限制；Receive Shadows=Off 不生效。
- 复用：Contact Shadows 与 SSL/GTAO 同时开启时，共享深度金字塔每个相机每帧只构建一次；Contact Shadows 首采 mip 1、确认读 mip 0。
- 边界：已有 URP C# 文件零修改；新增**功能 C#** 全在 `Runtime/RendererFeatures/ContactShadows` 与 `Editor/RendererFeatures/ContactShadows`；测试脚本按 §5.3 放在测试目录；URP shader 只改 `Shadows.hlsl` + 新增 `ContactShadows.hlsl`。
- 正确性：关闭时逐像素一致；无 NaN/闪烁/自阴影痤疮；三模式一致。
- 工程：D3D11/D3D12 构建零 shader error；既有 EditMode/PlayMode/T12 全绿。
- 收尾：测试改动、证据留存、产物清理、文档一致性全部完成。

---

## 2. HDRP 实现调研

### 2.1 文件与入口

| 作用 | 文件 |
|---|---|
| Volume 参数 | `Runtime/Lighting/Shadow/ContactShadows.cs` |
| Compute kernel | `Runtime/Lighting/Shadow/ContactShadows.compute` |
| 参数别名 | `Runtime/Lighting/Shadow/ContactShadows.hlsl` |
| Pass 调度/资源 | `Runtime/RenderPipeline/HDRenderPipeline.LightLoop.cs::RenderContactShadows`（约 1541 行） |
| 帧序调用 | `Runtime/RenderPipeline/HDRenderPipeline.RenderGraph.cs:170` |
| 全局绑定 | `Runtime/RenderPipeline/HDRenderPipeline.LightLoop.cs:39/48` |
| 每灯槽位 | `Runtime/Lighting/Light/HDGpuLightsBuilder.LightLoop.cs::GetContactShadowMask`（约 215 行） |
| 打包/解包/取用 | `Runtime/Lighting/LightLoop/LightLoopDef.hlsl:398-433` |
| 光照循环应用 | `Runtime/Lighting/LightEvaluation.hlsl:311-317, 494-500` |
| Opacity 上传 | `Runtime/RenderPipeline/HDRenderPipeline.cs:1108` |
| 文档 | `Documentation~/Override-Contact-Shadows.md`、`Documentation~/Ray-Traced-Contact-Shadows.md`（仅作对照，raytraced 不在本轮） |

关键源码位置（HDRP 14.0.12）：

| 内容 | 行号 |
|---|---|
| Volume 参数与默认值 | `ContactShadows.cs:16-77` |
| 半分辨率深度采样 / 阈值 / raymarch | `ContactShadows.compute:27-143` |
| 每灯 `ComputeContactShadow` | `ContactShadows.compute:145-164` |
| Kernel / 早退 / 打包 | `ContactShadows.compute:166-267` |
| compute 参数别名 | `ContactShadows.hlsl:3-20` |
| Pass 调度、RT 创建、dispatch | `HDRenderPipeline.LightLoop.cs:1541-1650` |
| ContactShadow 纹理全局绑定 | `HDRenderPipeline.LightLoop.cs:17-48` |
| 帧序调用 | `HDRenderPipeline.RenderGraph.cs:170` |
| 每灯槽位分配（24 上限） | `HDGpuLightsBuilder.LightLoop.cs:215-235` |
| 打包 / 解包 / `GetContactShadow` | `LightLoopDef.hlsl:398-433` |
| `min(shadow, contact)` 与 NdotL 门控 | `LightEvaluation.hlsl:311-317, 494-500` |
| Opacity 上传 | `HDRenderPipeline.cs:1108` |

### 2.2 HDRP 帧序

```
Depth Prepass / Depth Pyramid (mip 0..N)
GPULightList (方向光 + 分块点/聚光)
RenderContactShadows            ← 接触阴影
  ├─ 读 depth pyramid：射线 mip 1 首采 + mip 0 确认
  ├─ 遍历方向光 + tile 内点/聚光
  └─ 写 ContactShadowsBuffer（R32_UInt，全分辨率）
LightLoop（deferred/forward）
  └─ InitContactShadow → GetContactShadow → min(shadow, contact)
```

`RenderContactShadows` 在光照之前执行；纹理创建为全分辨率、XR-ready 的 `R32_UInt`、`enableRandomWrite`。深度传入的是 prepass 的 depth pyramid，同时把 `mipLevelOffsets[1].y` 作为 `firstMipOffsetY` 传入 compute。

### 2.3 参数与默认值

来自 `ContactShadows.cs`：

| 参数 | 默认 | 范围 | 说明 |
|---|---|---|---|
| `enable` | false | bool | Volume 总开关 |
| `length` | 0.15 | 0–1 m | 射线长度基数 |
| `opacity` | 1.0 | 0–1 | 全局不透明度 |
| `distanceScaleFactor` | 0.5 | 0–1 | 射线长度随线性深度放大 |
| `maxDistance` | 50 | ≥0 | 开始淡出距离 |
| `minDistance` | 0 | ≥0 | 开始淡入距离 |
| `fadeDistance` | 5 | ≥0 | 超过 maxDistance 的淡出区间 |
| `fadeInDistance` | 0 | ≥0 | 超过 minDistance 的淡入区间 |
| `rayBias` | 0.2 | 0–1 | 射线起点向相机偏移 |
| `thicknessScale` | 0.15 | 0.02–1 | 深度比较厚度 |
| `sampleCount` | 10 | 4–64 | 每像素步数（HDRP 可由 Quality 分级覆盖） |

HDRP 的 `sampleCount` 通过 `VolumeComponentWithQuality` 从 Lighting Quality Settings 取值；包内 Feature 直接暴露为 Volume 参数。

### 2.4 算法逐步拆解

#### 步骤 1：像素输入

- 读深度 `depth = LoadCameraDepth(pixelCoord)`。
- `PositionInputs` 提供 `positionWS`、`linearDepth`、`positionSS`。
- 早退：`depth == UNITY_RAW_FAR_CLIP_VALUE`、`linearDepth - fadeEnd > 1`、`linearDepth < minDistance` → 写 `0`（中性）。

#### 步骤 2：射线起点/终点

```
bias        = rayBias * 0.01
rayStartWS  = positionWS - positionWS * bias          // HDRP 是 camera-relative 世界空间
rayEndWS    = rayStartWS + rayDirWS * rayLength

rayLength   = length * max(0.5, linearDepth * distanceScaleFactor)

方向光:  rayDirWS = -light.forward
点/聚光: rayDirWS = normalize(light.positionRWS.xyz - positionWS)
```

HDRP 使用 camera-relative 世界空间，所以 `positionWS * (1 - bias)` 等价于“把起点向相机挪一小段”。**URP 是世界绝对坐标**，迁移必须写成：

```
rayStartWS = positionWS - (positionWS - cameraPosWS) * bias
```

否则在远离原点的场景中 bias 会指向世界原点，产生错误自阴影。

#### 步骤 3：投影到裁剪空间与 UV

```
rayStartCS = TransformWorldToHClip(rayStartWS)
rayEndCS   = TransformWorldToHClip(rayEndWS)
rayStartCS.xyz /= rayStartCS.w
rayEndCS.xyz   /= rayEndCS.w

rayDirCS = rayEndCS.xyz - rayStartCS.xyz
startUV  = rayStartCS.xy * 0.5 + 0.5
rayStart = float3(startUV.x, 1 - startUV.y, rayStartCS.z)   // D3D 风格 y 翻转
rayDir   = float3(rayDirCS.x * 0.5, -rayDirCS.y * 0.5, rayDirCS.z)
```

之后所有步进都在 `rayStart + t * rayDir` 上完成，只做 `Load`，不做世界空间求交。

#### 步骤 4：与射线斜率无关的深度阈值

HDRP 额外计算一条“纯视图 Z 方向”的射线：

```
rayOrthoCS = rayStartCS + float4(P[0][2], P[1][2], P[2][2], P[3][2]) * rayLength
rayOrthoCS /= rayOrthoCS.w
```

其中 `P` 是 View→Clip（投影）矩阵。它表示“从起点沿视图 Z 走 `rayLength`”的裁剪空间位置。阈值：

```
step             = 1 / sampleCount
compareThreshold = abs(rayOrthoCS.z - rayStartCS.z) * thickness * max(0.07, step)
thickness        = thicknessScale * 10
```

`CompareDepth(diff, threshold) = abs(threshold - diff) < threshold`，即 `0 < diff < 2 * threshold`。阈值不随射线在屏幕上的斜率剧烈变化。

#### 步骤 5：步进与半分辨率优先

```
t = step * dither + step
tracingHalfRes = true
i = 0

while i < sampleCount:
    sample = rayStart + t * rayDir
    if sample.xy outside [0,1]: break

    sampleDepth = tracingHalfRes
        ? 半分辨率深度（mip 1）
        : 全分辨率深度（mip 0）
    diff = sampleDepth - sample.z

    if diff > 0 && CompareDepth(diff, threshold) && sample.z > 0:
        if tracingHalfRes:
            tracingHalfRes = false
            continue          // 同一个 t 改读 mip 0（全分辨率）再采一次；不增加 i、不前进 t
        occluded = 1
        break

    t += step
    i++
```

- 半分辨率优先：先读 mip 1，命中后切到全分辨率；只有连续两次命中才判定遮挡，避免半分辨率深度造成的误遮挡。
- 只接受“背景在射线后面且距离在厚度内”的样本，即 `sampleDepth > sample.z` 且深度差在阈值内。

#### 步骤 6：dither / TAA

```
dither = InterleavedGradientNoise(positionSS, (frameCount % 8) * taaEnabled) - 0.5
```

- `taaEnabled` 来自 `_TaaFrameInfo.w`；TAA 开启时按帧变化，关闭时是静态 IGN 图案。
- dither 只影响起点在步长内的偏移，不改变步数。

#### 步骤 7：屏外遮罩

```
vignette = max(6 * abs(rayStartCS.xy + rayDirCS.xy * t) - 5, 0)
fade    *= saturate(1 - dot(vignette, vignette))
```

`t` 是最终步进位置（命中或退出时的 t）。效果是：射线越接近屏幕边缘，遮挡越弱，避免屏幕外信息缺失造成的硬边。

#### 步骤 8：距离 fade

```
fade *= saturate((linearDepth - minDistance) * rcp(fadeInEnd))
fade *= saturate((fadeEnd - linearDepth) * oneOverFadeRange)

fadeEnd            = maxDistance
fadeInEnd          = clamp(fadeInDistance, 1e-6, fadeEnd)
minDistance        = min(minDistance, fadeEnd)
oneOverFadeRange   = 1 / max(1e-6, min(fadeDistance, fadeEnd))
```

多盏灯时 `globalFade = max(globalFade, fade)`，即所有灯共用一个 fade 字节。

#### 步骤 9：打包

```
mask          = 24 bit，每盏参与接触阴影的灯 1 bit
fadeByte      = uint(saturate(globalFade) * 255) << 24
packed        = mask | fadeByte
```

- `ContactShadowsBuffer` 是 `R32_UInt`，每像素一个 `uint`。
- 值为 `0` 表示“无遮挡、无 fade”，即中性。

#### 步骤 10：光照循环应用

`LightLoopDef.hlsl`：

```
fade = (packed >> 24) / 255
mask = packed & 0x00FFFFFF

GetContactShadow(lightMask, rayTracedShadow):
    occluded = (mask & lightMask) != 0
    return 1 - occluded * lerp(fade, 1, rayTracedShadow) * _ContactShadowOpacity
```

`LightEvaluation.hlsl`：

```
shadow = min(shadow, allowContactShadow ? GetContactShadow(...) : 1)
```

- `allowContactShadow = NdotL > 0`（除 hair 等强制开启的特例）。
- 透明表面被 `#if !defined(_SURFACE_TYPE_TRANSPARENT)` 排除。
- 先应用 shadow map / shadow mask / shadow dimmer，再与接触阴影取 `min`。
- `opacity` 是全局 uniform，不在纹理里。

### 2.5 每灯启用与槽位

`HDGpuLightsBuilder.GetContactShadowMask`：

- 只检查 `useContactShadow` 和 `m_ContactShadowIndex < 24`。
- 第 1 盏参与的灯得到 `1 << 0`，第 2 盏 `1 << 1`，依此类推。
- 超过 24 盏的灯 `contactShadowMask = 0`，不参与接触阴影。
- 与“灯光是否投射 shadow map”无关；与 Mesh Renderer 的 `Cast Shadows` 也无关（HDRP 文档明确说明）。

### 2.6 分辨率与性能

- 计算输出：全分辨率（`numTilesX = ceil(actualWidth / 8)`）。
- 射线步进的深度读取：首采 mip 1（半分辨率），命中后读 mip 0（全分辨率）确认；mip1 坐标 `fullPixel * 0.5 + mip1Offset`，mip0 坐标 `fullPixel + mip0Offset`。像素输入（初始深度）始终读 mip 0。
- HDRP 文档给出基础版 PS4 1080p 约 0.5–1.3 ms；桌面 GPU 通常远低于此。
- 主要成本：每像素 × 参与灯数 × sampleCount 次深度 Load。HDRP 用 tile 灯光列表限制每像素灯数；本 Feature v1 没有等价列表，每像素最多 24 盏（见风险 R5）。

### 2.7 与 URP 的关键差异

| 差异 | HDRP | 本 Feature |
|---|---|---|
| 世界坐标 | camera-relative | 绝对世界坐标；bias 必须朝相机，不能朝原点 |
| 灯光遍历 | 方向光数组 + FPTL tile 列表 | 每像素循环最多 24 盏接触阴影灯（无 tile 裁剪） |
| TAA 帧信息 | `_TaaFrameInfo.w` | Feature 显式上传 `frameIndex` 与 `taaEnabled` |
| Volume 质量 | `VolumeComponentWithQuality` | 直接 `sampleCount` |
| 深度纹理 | `_CameraDepthTexture` 指向打包金字塔 | 复用 URP 共享 `ScreenSpaceDepthPyramid`：mip 1 首采、mip 0 确认 |
| 槽位 | 方向光/附加光统一在 light loop 中分配 | 按参与顺序：主光优先 `bit 0`，附加光随后；总计 24 |
| 透明物 | 编译期排除 | `_SURFACE_TYPE_TRANSPARENT` 编译期排除；opaques 后绑中性纹理/清零掩码 |
| API | `#pragma only_renderers d3d11 ...` | 运行时 gate 到 D3D11/D3D12/Vulkan/Metal |
| 每灯开关 | HDAdditionalLightData | Feature 目录内的 `ContactShadowLight` 组件 |

---

## 3. 包内边界与现有 URP 基线

### 3.1 版本与平台

| 项 | 值 |
|---|---|
| Unity | 2022.3.62f1 |
| URP | `com.unity.render-pipelines.universal@14.0.12`（本仓库内嵌 fork） |
| 目标平台 | Windows Desktop；D3D11 / D3D12 / Vulkan / Metal |
| 非目标 | GLES / OpenGL / 移动 / WebGL |
| XR | 复用 `ScreenSpaceLightingXr`；支持 1–2 view，作为次级验收 |

### 3.2 包内 vs 现有代码（边界表）

| 区域 | 归属 | 内容 |
|---|---|---|
| Feature Runtime（新增） | `Packages/.../Runtime/RendererFeatures/ContactShadows` | Feature、Volume、灯组件、资源、History、Pass、Capability、ShaderIDs |
| Feature Editor（新增） | `Packages/.../Editor/RendererFeatures/ContactShadows` | Feature Inspector、资源默认值、校验 |
| Feature Shaders（新增） | `Packages/.../Shaders/Utils/ContactShadows` | `ContactShadows.compute`、`ContactShadowDebug.shader` |
| 材质 shader 钩子（新增） | `Packages/.../ShaderLibrary/ContactShadows.hlsl` | 全局声明、解包、采样、`mask==0` 早退 |
| 材质 shader 钩子（修改） | `Packages/.../ShaderLibrary/Shadows.hlsl` | include + 两个 `min` 块 |
| 现有 URP C# 文件（修改） | **无**（新增 `ContactShadows` 目录除外） | 不改 `UniversalRenderer`、`ForwardLights`、`DeferredLights`、`UniversalAdditionalLightData`、Volume 框架 |
| 现有 URP shader（修改） | 仅 `Shadows.hlsl` | 其他 Lit/SimpleLit/Particles/Terrain shader 不改 |
| 共享基础设施（复用） | `Runtime/RendererFeatures/ScreenSpaceCommon` | 不改；新 Feature 调用 `ScreenSpaceDepthPyramidSystem.Acquire` |
| 测试资产（新增，包外） | `Assets/Tests/ContactShadow` | 场景、Pipeline/Renderer、Volume Profile、Player 验证 |
| 包内测试（新增） | `Packages/.../Tests/Editor/RendererFeatures/ContactShadows` | 单元测试、shader 编译、槽位、清理 |
| 文档（修改） | `PLAN/TODO/10-ContactShadow`、`PLAN/ACCEPTANCE.md` | HANDOFF、report、验收表 |

**改动预算**：

```
已有 URP C# 文件修改:     0
已有 URP shader 文件修改: 1（Shadows.hlsl，约 6–10 行）
新增 URP 包内文件:        ContactShadows 目录（Runtime/Editor/Shaders/ShaderLibrary/Tests）
项目侧测试资产:           Assets/Tests/ContactShadow（不属于 URP 包）
```

如果要求 **连 `Shadows.hlsl` 也不改**，唯一替代是要求所有材质换成自定义 shader 才能采样接触阴影；这会丢失内置 Lit 覆盖，不采用。这个取舍在 T0 由所有者确认。

### 3.3 复用的包内 internal 基础设施

| 需求 | 复用 |
|---|---|
| 共享深度金字塔 | `ScreenSpaceDepthPyramidSystem.RegisterOwner/UnregisterOwner/Acquire`；`ScreenSpaceDepthPyramidPass`（+3）；`PackedMipChainInfo` |
| 半分辨率深度 | `ScreenSpaceDepthPyramid.texture`：mip 1 首采、mip 0 确认（`mipLevelOffsets[0/1]` / `mipLevelSizes[0/1]`） |
| 深度来源 | 共享 pass 内部使用 `ScreenSpaceRenderUtils.GetCameraDepthTexture`（forward 用 `m_DepthTexture`，deferred 用 `cameraDepthTargetHandle`），本 Feature 不重复实现 |
| 每相机资源 | `CameraHistoryStore<T>` + `IScreenSpaceHistoryEntry` |
| 平台/格式检查 | `ScreenSpaceCapability.EvaluatePlatform/EvaluateCamera`（可加 ContactShadows 自己的 `R32_UInt` 检查） |
| XR | `ScreenSpaceLightingXr.GetViewCount/GetResourceDimension/GetViewProjection` |
| 深度 pyramid XR 关键字 | `ScreenSpaceShaderIDs.XrArrayKeyword`（`SSL_XR_ARRAY`） |
| 资源默认值 | 仿 `ScreenSpaceLightingResourceLocator` 的 `FindPackageAsset` 模式 |

**不使用/不修改**：`ScreenSpaceDepthPyramidPass` 的现有实现、`ScreenSpaceCapability` 的现有判断、`CameraHistoryStore`、SSL/GTAO 的任何代码。

### 3.4 URP 本体 shader 钩子（唯一核心 shader 改动）

新增 `ShaderLibrary/ContactShadows.hlsl`：

```
// CBUFFER 与解包函数必须在 API guard 外，保证未支持 API 的调用点仍能编译。
CBUFFER_START(ContactShadows)
float _ContactShadowOpacity;
uint  _MainLightContactShadowMask;
float _AdditionalLightsContactShadowMasks[MAX_VISIBLE_LIGHTS];
CBUFFER_END

uint  ContactShadowUnpackMask(uint packed) { return packed & 0x00FFFFFFu; }
float ContactShadowUnpackFade(uint packed) { return (packed >> 24) / 255.0; }

#if defined(SHADER_API_D3D11) || defined(SHADER_API_D3D12) || \
    defined(SHADER_API_VULKAN) || defined(SHADER_API_METAL)
    Texture2D<uint> _ContactShadowTexture;      // XR 时用 Texture2DArray<uint>
    #define ContactShadowLoad(pixel) ...        // 按 XR/非 XR 选择 Load 坐标

    float GetContactShadowAttenuation(float3 positionWS, uint mask)
    {
        if (mask == 0) return 1.0;   // 禁用/未参与的灯：在 Load 之前返回
        float2 uv = ComputeNormalizedDeviceCoordinates(positionWS, GetWorldToHClipMatrix()).xy;
        uint2 pixel = (uint2)(saturate(uv) * _ScaledScreenParams.xy);
        uint packed = ContactShadowLoad(pixel);
        bool occluded = (ContactShadowUnpackMask(packed) & mask) != 0;
        return 1.0 - occluded * ContactShadowUnpackFade(packed) * _ContactShadowOpacity;
    }
#else
    float GetContactShadowAttenuation(float3 positionWS, uint mask) { return 1.0; }
#endif
```

修改 `Shadows.hlsl`：

- 在 `MainLightShadow` 末尾：
  ```
  #if !defined(_SURFACE_TYPE_TRANSPARENT) && !defined(_RECEIVE_SHADOWS_OFF)
      shadow = min(shadow, GetContactShadowAttenuation(positionWS, _MainLightContactShadowMask));
  #endif
  ```
- 在 `AdditionalLightShadow` 末尾：
  ```
  #if !defined(_SURFACE_TYPE_TRANSPARENT) && !defined(_RECEIVE_SHADOWS_OFF)
      uint contactMask = (lightIndex >= 0 && lightIndex < MAX_VISIBLE_LIGHTS)
          ? asuint(_AdditionalLightsContactShadowMasks[lightIndex]) : 0u;
      shadow = min(shadow, GetContactShadowAttenuation(positionWS, contactMask));
  #endif
  ```

- `_RECEIVE_SHADOWS_OFF` 与 Deferred 的 `materialReceiveShadowsOff` 语义保持一致：材质关闭 Receive Shadows 时，Forward 与 Deferred 都不应用接触阴影。这是 v1 的明确选择（HDRP 的行为可能不同，见 KnownDifferences）。
- `lightIndex` 必须做 `[0, MAX_VISIBLE_LIGHTS)` 边界保护；Deferred 主光不走 `AdditionalLightShadow`，`_ShadowLightIndex = -1` 不会进入上述分支。
- 不新增 `_CONTACT_SHADOWS` 关键字、不改 `Input.hlsl`、不改任何 Lit/SimpleLit/Particles/Terrain shader。
- 新数组/常量放**独立 CBUFFER**（如 `ContactShadows`），不要插入现有 `AdditionalLights` CBUFFER，避免改变 URP 现有 UBO 布局/SRP Batcher 兼容性。
- **API guard**：`Texture2D<uint>` / `ContactShadowLoad` / 函数实现用 `#if defined(SHADER_API_D3D11) || defined(SHADER_API_D3D12) || defined(SHADER_API_VULKAN) || defined(SHADER_API_METAL)` 包裹；其他 API 提供 `GetContactShadowAttenuation(...) { return 1.0; }`，保证 GLES/WebGL 变体编译。
- **API guard 的声明边界**：`_ContactShadowOpacity`、`_MainLightContactShadowMask`、`_AdditionalLightsContactShadowMasks` 的声明必须放在 `SHADER_API_*` guard **外**；guard 只包 `Texture2D<uint>`、`ContactShadowLoad` 和函数实现。否则未支持 API 的 `MainLightShadow`/`AdditionalLightShadow` 调用点仍会引用这些全局变量而编译失败。
- 关闭时所有掩码为 0，函数在 `Load` 前返回；Feature 绑定 1×1 中性 `R32_UInt` 纹理，避免未定义绑定。这与 HDRP `LightLoopDef.hlsl:414-416` 的“始终取一次、禁用时绑 1×1 纹理”一致。
- 头文件内部用 `#if` 定义 XR/非 XR 的 `ContactShadowLoad` 宏（`Texture2D<uint>` 或 `Texture2DArray<uint>`）；`mask==0` 时不执行该宏。

### 3.5 灯光索引与掩码

| 事实 | 位置 |
|---|---|
| 附加光 GPU 数组顺序 | `ForwardLights.SetupAdditionalLightConstants`（`ForwardLights.cs:553-627`）：`visibleLights` 顺序、跳过主光、最多 `maxVisibleAdditionalLights` |
| 附加光索引 | 该顺序就是 `_AdditionalLightsPosition` / `AdditionalLightShadow(lightIndex)` 的索引 |
| Forward+ 索引 | cluster index 加上 `URP_FP_DIRECTIONAL_LIGHTS_COUNT` 后仍落在同一 additional 数组索引 |
| Deferred 附加光索引 | `_ShadowLightIndex = m_VisibleLightIndexToAdditionalLightIndex[visibleLightIndex]`，即 additional light index；**即使灯不投射阴影也会赋值**（`AdditionalLightsShadowCasterPass.cs:660-679, 899-905`） |
| 主光 | 独立 `_MainLightPosition` / `_MainLightColor` |

Feature 在 `ScreenSpaceContactShadowLights.Build(renderingData.lightData)` 中按**完全相同的顺序**扫描 `visibleLights`、跳过主光，填充：

- `_MainLightContactShadowMask`（主光参与时拿当前 slot；主光未参与则为 0）；
- `_AdditionalLightsContactShadowMasks`（float 数组，长度 `MAX_VISIBLE_LIGHTS` = 256 desktop；元素为 `math.asfloat(1u << slot)`，shader 端 `asuint`）；
- `_ContactShadowLightBuffer`（最多 24 条记录：position/type/direction/mask）；
- `_ContactShadowLightCount`。

每个非主光 `visibleLight` 都消耗一个 additional light index，**无论是否标记**；否则掩码数组与 `_AdditionalLightsPosition`/`_ShadowLightIndex` 错位。每帧先清零再填充。**不修改 `ForwardLights.cs`。**

### 3.6 Deferred 的边界

- **主光**：`StencilDeferred.hlsl` 的 `_DEFERRED_MAIN_LIGHT` 分支始终调用 `MainLightShadow`（在 `!materialReceiveShadowsOff` 时），因此主光接触阴影在 Deferred 下**不依赖** shadow map 是否存在。
- **附加光**：`AdditionalLightShadow` 在 Deferred 的附加光分支被 `_ADDITIONAL_LIGHT_SHADOWS` 包裹。`DeferredLights.SetAdditionalLightsShadowsKeyword` 在每盏附加光 draw 前设置该关键字：Asset 开启 Additional Light Shadows，且 Off 变体被 strip，或**当前这盏附加光**投射阴影。满足时接触阴影叠加；`shadows=None` 的灯不会因其他灯投影而获得接触阴影。
- 若 URP Asset **整体关闭 Additional Light Shadows**，或 **Off 变体未 strip 且该附加光 `shadows=None`**，Deferred 附加光不会调用阴影函数，接触阴影也不应用；这是 v1 的已知限制（D10）。
- 解除该限制需要改 `DeferredLights.cs` + `StencilDeferred.hlsl`，作为可选 T10，**不纳入本轮**；本轮不把它列入 AC。

### 3.7 现有缺口

- 无接触阴影 Volume / 逐灯组件 / 打包输出。
- 无 `ContactShadows` Renderer Feature / compute / shader 头文件。
- 共享深度金字塔已存在且可复用；无需新建 pyramid。
- 无“从 Renderer Data 导入 Feature + Volume 开启”的验收资产。

---

## 4. 迁移架构

### 4.1 帧序（新增部分）

```
Depth Prepass / Depth Pyramid       203   （共享，ScreenSpaceDepthPyramidPass）
GTAO                                204   （可选）
SSGI                                205   （可选）
SSR                                 206   （可选）
SSL Setup                           207   （可选）
Contact Shadows (compute)           208   ← 新
Deferred GBuffer                    210
Deferred Lighting                   230   （采样接触阴影）
Forward / Forward+ Opaque           250   （采样接触阴影）
AfterRenderingOpaques               300   （绑定 1×1 中性纹理 + 清零掩码）
BeforeRenderingPostProcessing       ...   （可选 debug view）
```

- 共享深度金字塔 pass 由第一个 consumer 入队；Contact Shadows 与 SSL/GTAO 同时存在时每个相机每帧只构建一次。
- 如果 Volume 未启用、平台不支持、或没有任何 `ContactShadowLight`，Feature 在 `AddRenderPasses` 直接返回：不 Acquire 金字塔、不入队 Compute、不分配 RT、不写全局掩码。
- **状态生命周期**：Active 帧 ComputePass（208）与 CleanupPass（300）成对入队（资源缺失时不 dispatch，但 Cleanup 仍入队归零）；ComputePass 在 dispatch 后绑定真实 `_ContactShadowTexture` / 掩码 / opacity，CleanupPass 在 opaques 后绑定 1×1 中性纹理 / 零掩码 / opacity 0。Inactive 帧不入队，依赖上一 Active 帧 Cleanup 的归零状态；首次运行全局默认 0。

| 状态 | ComputePass | 全局绑定 | packed RT |
|---|---|---|---|
| Active | 入队 | 208 dispatch 后绑真实纹理/掩码/opacity；300 Cleanup 归零 | 分配 |
| Inactive | 不入队 | 上一 Active 帧 Cleanup 已归零；首次默认 0 | 不分配 |
| Active 但资源缺失 | 不入队 | Cleanup 绑中性/零 | 不分配 |
| Unsupported API/格式 | 不入队 | 同 Inactive | 不分配 |

### 4.2 资源

| 资源 | 格式 | 尺寸 | 说明 |
|---|---|---|---|
| `ScreenSpaceDepthPyramid` | `R32_SFloat` | 共享打包图集 | 复用；mip 1 首采（半分辨率），mip 0 确认（全分辨率） |
| `_ContactShadowTexture` | `R32_UInt` | 全分辨率 × viewCount | packed：低 24 bit 每灯遮挡，高 8 bit 全局 fade |
| `_ContactShadowNeutralTexture` | `R32_UInt` | 1×1 | 值为 0；禁用时绑定 |
| `_ContactShadowLightBuffer` | `ComputeBuffer`（24 条） | 24 × 32 B | 每条：`float3 position; uint type; float3 direction; uint mask;`（避免 float 位模式在 denormal/FTZ 平台丢 bit） |
| `_ContactShadowParams*` | CBUFFER | — | length / distanceScaleFactor / fade / bias / thickness / sampleCount / frameIndex / taaEnabled |
| `_ContactShadowMatrices` | CBUFFER | 每 view | ViewProj / InvViewProj / Projection / CameraPosWS（XR 2 view 数组） |

“全分辨率”指 `cameraData.cameraTargetDescriptor` 的宽高（动态分辨率 / Render Scale 之后），与共享金字塔和 `_ScaledScreenParams` 一致。

每相机（和 XR eye）的 packed RT 由 `CameraHistoryStore<ScreenSpaceContactShadowsHistory>` 管理；`ScreenSpaceContactShadowsHistory` 持有 `RTHandle`、尺寸、viewCount、dimension，`Reset/Dispose` 释放。

### 4.3 新增文件（包内 + 测试）

```
Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ContactShadows/
  ScreenSpaceContactShadowsRendererFeature.cs
  ContactShadowsVolume.cs                 # VolumeComponent
  ContactShadowLight.cs                   # 灯组件（逐灯启用）
  ScreenSpaceContactShadowsSettings.cs
  ScreenSpaceContactShadowsResources.cs   # compute/debug shader 引用 + 中性纹理
  ScreenSpaceContactShadowsHistory.cs
  ScreenSpaceContactShadowsCapability.cs
  ScreenSpaceContactShadowLights.cs       # 槽位分配 + GPU buffer
  ScreenSpaceContactShadowsShaderIDs.cs
  Passes/ScreenSpaceContactShadowsPass.cs
  Passes/ScreenSpaceContactShadowsCleanupPass.cs
  Passes/ScreenSpaceContactShadowsDebugPass.cs

Packages/com.unity.render-pipelines.universal@14.0.12/Editor/RendererFeatures/ContactShadows/
  ScreenSpaceContactShadowsEditor.cs
  ContactShadowsVolumeEditor.cs           # 可选
  ContactShadowLightEditor.cs             # 可选

Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/ContactShadows/
  ContactShadows.compute
  ContactShadowDebug.shader

Packages/com.unity.render-pipelines.universal@14.0.12/ShaderLibrary/
  ContactShadows.hlsl                      # 新增（URP 本体 shader 钩子）

Packages/com.unity.render-pipelines.universal@14.0.12/Tests/Editor/RendererFeatures/ContactShadows/
  ContactShadowVolumeTests.cs
  ContactShadowCapabilityTests.cs
  ContactShadowHistoryTests.cs
  ContactShadowLightTests.cs
  ContactShadowSlotMappingTests.cs
  ContactShadowShaderCompileTests.cs
  ContactShadowCleanupTests.cs
  ContactShadowGlobalBindingTests.cs

Assets/Tests/ContactShadow/
  Scenes/ContactShadowBaseline.unity
  Pipelines/ContactShadowForward.asset
  Pipelines/ContactShadowForwardPlus.asset
  Pipelines/ContactShadowDeferred.asset
  Renderers/ContactShadowForwardRenderer.asset
  Renderers/ContactShadowForwardPlusRenderer.asset
  Renderers/ContactShadowDeferredRenderer.asset
  Profiles/ContactShadowVolumeProfile.asset
  Editor/ContactShadowFeatureImportTests.cs
  Editor/ContactShadowValidationPlayerSetup.cs
  Runtime/ContactShadowValidationPlayerVerification.cs
  Tests/PlayMode/Unity.ContactShadow.Tests.PlayMode.asmdef
  Tests/PlayMode/ContactShadowRuntimeToggleTests.cs
  Tests/PlayMode/ContactShadowReceiveShadowsTests.cs
  Tests/PlayMode/ContactShadowDeferredLimitationTests.cs

PLAN/TODO/10-ContactShadow/
  HANDOFF.md                              # 本文件
  report.md                               # T8 产出
  KnownDifferences.md                     # T9 产出/更新
```

### 4.4 修改文件（URP 本体）

| 文件 | 改动 | 责任 |
|---|---|---|
| `Packages/.../ShaderLibrary/ContactShadows.hlsl` | 新增：全局声明、解包、采样、`mask==0` 早退 | W2 |
| `Packages/.../ShaderLibrary/Shadows.hlsl` | include 新头文件；`MainLightShadow` / `AdditionalLightShadow` 末尾各加一个 `min` 块 | W2 |

**已有 URP C# 文件不修改**：

- `Runtime/UniversalRenderer.cs`、`ForwardLights.cs`、`DeferredLights.cs`、`UniversalAdditionalLightData.cs`、`UniversalRenderPipeline.cs`。
- `ShaderLibrary/Input.hlsl`、任何 Lit/SimpleLit/ComplexLit/Particles/Terrain shader。
- `Shaders/Utils/StencilDeferred.hlsl`（v1 不改；仅可选 T10 才需要）。
- Volume 框架、Renderer Feature 列表、Pass 顺序。
- 现有 `ScreenSpaceLighting` / GTAO / DDGI 代码。

### 4.5 Volume 设计

`ContactShadowsVolume : VolumeComponent`：

```
[VolumeComponentMenuForRenderPipeline("Shadowing/Contact Shadows", typeof(UniversalRenderPipeline))]
public sealed class ContactShadowsVolume : VolumeComponent
{
    public BoolParameter enabled = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);
    public ClampedFloatParameter length = new ClampedFloatParameter(0.15f, 0f, 1f);
    public ClampedFloatParameter opacity = new ClampedFloatParameter(1f, 0f, 1f);
    public ClampedFloatParameter distanceScaleFactor = new ClampedFloatParameter(0.5f, 0f, 1f);
    public MinFloatParameter maxDistance = new MinFloatParameter(50f, 0f);
    public MinFloatParameter minDistance = new MinFloatParameter(0f, 0f);
    public MinFloatParameter fadeDistance = new MinFloatParameter(5f, 0f);
    public MinFloatParameter fadeInDistance = new MinFloatParameter(0f, 0f);
    public ClampedFloatParameter rayBias = new ClampedFloatParameter(0.2f, 0f, 1f);
    public ClampedFloatParameter thicknessScale = new ClampedFloatParameter(0.15f, 0.02f, 1f);
    public ClampedIntParameter sampleCount = new ClampedIntParameter(10, 4, 64);
}
```

- Feature 必须在 Active Renderer Data 上；Volume 是效果的总开关。`AddRenderPasses` 读取 Volume 并把设置缓存进 pass，`Execute` 不再重新读 Volume，避免入队决策与执行设置不一致。
- Volume/灯组件的运行时变化在**下一帧**生效；T7 的 runtime toggle 测试必须等待一帧再断言。
- 无 Volume / `enabled=false`：不入队 Compute、不分配 RT、不写全局；状态按 §4.1 的 Inactive 行处理。
- 不做 HDRP Quality 分级；Inspector 加一句说明。

### 4.6 逐灯启用与槽位

`ContactShadowLight`（Feature 目录内组件）：

```
[DisallowMultipleComponent]
[AddComponentMenu("Rendering/Contact Shadow Light")]
public sealed class ContactShadowLight : MonoBehaviour
{
    public bool castContactShadows = true;
}
```

- 只对 `LightType.Directional / Point / Spot` 生效；Rectangle 忽略。
- 组件不存在或 `castContactShadows=false` 或 `Behaviour.enabled=false`：该灯不参与。
- 只检查 **Light 所在 GameObject** 上的组件；不向父/子层级查找。组件挂在非 Light 对象上时忽略并 warning 一次。
- 灯组件状态在 `AddRenderPasses` 读取并缓存。
- 这是 Feature 目录内的 opt-in，不修改 URP 的 `UniversalAdditionalLightData` 或灯 Inspector。
- 用户流程：在灯 GameObject 上 Add Component → Contact Shadow Light。

发现逻辑必须固定为：

```
Light unityLight = visibleLight.light;                       // 可能为 null（baked-only）
ContactShadowLight marker = unityLight != null
    ? unityLight.GetComponent<ContactShadowLight>() : null;
bool marked = marker != null
    && marker.isActiveAndEnabled
    && marker.castContactShadows
    && unityLight.enabled
    && (unityLight.type == LightType.Directional
        || unityLight.type == LightType.Point
        || unityLight.type == LightType.Spot);
```

槽位分配（`ScreenSpaceContactShadowLights`）：

compute 的解码约定为 `0=Directional, 1=Point, 2=Spot`，与 Unity `LightType`（`Spot=0, Directional=1, Point=2`）不同，必须显式映射（`EncodeLightType`），不能直接写 `(uint)light.type`。

```
slot = 0
additionalIndex = 0
mainMask = 0
additionalMasks[0..MAX_VISIBLE_LIGHTS-1] = 0
records.Clear()

if mainLightIndex >= 0 && IsMarked(mainLight):
    mainMask = 1u << slot++
    records.Add(main,
        type = EncodeLightType(mainLight.type),     // 0=Directional, 1=Point, 2=Spot
        direction = (Directional ? -forward : 0),
        position  = (Directional ? 0 : mainLightPos))

for i in visibleLights:
    if i == mainLightIndex: continue
    if additionalIndex >= maxVisibleAdditionalLights: break

    // 关键：每个非主光 visibleLight 都消耗一个 additional light index，
    // 无论它是否标记；必须与 ForwardLights.SetupAdditionalLightConstants 的 lightIter 一致。
    if IsMarked(light) && slot < 24:
        additionalMasks[additionalIndex] = 1u << slot++
        records.Add(light,
            type = EncodeLightType(light.type),
            direction = (Directional ? -forward : 0),
            position  = (Directional ? 0 : lightPos))

    additionalIndex++
```

- 主光参与时拿 `slot 0`；主光未参与/未标记时，第一盏附加光拿 `slot 0`。因此总计最多 24 盏，不固定浪费槽位。
- `record` 布局为 32 B：`float3 position; uint type; float3 direction; uint mask;`；`type` 取 `EncodeLightType` 的 0/1/2，`mask` 直接放 `uint`。
- **全局掩码的 float 位模式**：上传用的 `_AdditionalLightsContactShadowMasks` 是 `float[]`，元素必须写 `math.asfloat(1u << slot)`，shader 端 `asuint` 取回（与 URP `_AdditionalLightsLayerMasks` 相同）；直接写 `1.0f` 会使掩码永不命中。`record.mask` 不需要 `asfloat`。
- **索引一致性**：`additionalIndex` 对每个非主光递增，未标记的灯也会占一个 additional light index；否则 `_AdditionalLightsContactShadowMasks[lightIndex]` 与 shader 的 `lightIndex` 错位。测试必须包含“未标记灯在标记灯之前”的用例。

`additionalLightIndex` 必须与 `ForwardLights.SetupAdditionalLightConstants` 的 `lightIter` 完全一致：按 `visibleLights` 顺序、跳过主光、最多 `UniversalRenderPipeline.maxVisibleAdditionalLights`。这保证 Forward、Forward+、Deferred 三条路径取到同一槽位。

每帧先把 `_AdditionalLightsContactShadowMasks` 的整个数组清零，再按同一份 `visibleLights` 快照填充；否则上一帧/上一相机残留的掩码会把接触阴影错误地应用到已经关闭的灯。

### 4.7 Compute 适配

`ContactShadows.compute` 从 HDRP 移植，做以下替换：

| HDRP | 本 Feature |
|---|---|
| `PositionInputs` | 用 `uv + deviceDepth + InvViewProj` 重建 `positionWS`/`linearDepth` |
| `_CameraDepthTexture`（HDRP 金字塔） | 共享 `ScreenSpaceDepthPyramid.texture`：首采 mip 1；命中后同一 t 读 mip 0 确认；绑定 mip0/mip1 的 offsets/sizes |
| `_ScreenSize` | `_ContactShadowScreenSize` |
| `_TaaFrameInfo.w` | `_ContactShadowTaaEnabled` |
| `_FrameCount` | `_ContactShadowFrameIndex` |
| `_DirectionalLightDatas` / `_LightDatas` / `g_vLightListTile` | `_ContactShadowLightBuffer` + `_ContactShadowLightCount` |
| `GetViewToHClipMatrix()` | 上传的每 view 投影矩阵 |
| `positionWS * (1 - bias)` | `positionWS - (positionWS - cameraPosWS) * bias` |

核心 kernel：

```
[numthreads(8,8,1)]
KContactShadow(pixel):
    depth = depthPyramid.Load(mip0Offset + pixel)   // 初始深度必须全分辨率；mip 1 只用于 RayCast 首采
    if background or outside fade range: store 0; return

    posWS       = ReconstructWorldPos(pixel, depth)
    linearDepth = -viewZ(posWS)

    contactMask = 0
    globalFade  = 0
    for each record in [0, lightCount):
        type = record.type                      // 0=Directional, 1=Point, 2=Spot
        mask = record.mask
        if type == 0:
            rayDir = record.direction.xyz
        else:
            rayDir = normalize(record.position.xyz - posWS)

        rayLength = length * max(0.5, linearDepth * distanceScaleFactor)
        occluded, fade = RayCast(posWS, rayDir, rayLength, pixel)
        contactMask |= mask * occluded
        globalFade = max(globalFade, fade)

    if lightCount == 0: store 0; return       // 与“无参与灯”一致
    store PackContactShadowData(globalFade, contactMask)
```

`RayCast` 内部保留 HDRP 的全部细节：bias、投影、斜率无关阈值、half-res（mip 1）→ full-res（mip 0）两段确认、dither、屏外 vignette、距离 fade。mip 0 的坐标是 `mip0Offset + fullPixel`，mip 1 的坐标是 `mip1Offset + fullPixel/2`；两者都来自同一张共享图集，不需要额外绑定 `_CameraDepthTexture`。

- XR：复用 `SSL_XR_ARRAY` 关键字与 `ScreenSpaceLightingXr` 的 view 矩阵/资源 dimension；compute 按 view 调度。
- Compute 只读共享金字塔；不写它。
- Compute 加 `#pragma only_renderers d3d11 vulkan metal`（D3D12 走 d3d11 目标），避免 GLES/WebGL 编译；运行时 gate 仍必须存在。
- **UV y 方向**：必须与共享金字塔/相机深度的采样约定一致；用 `UNITY_UV_STARTS_AT_TOP` 或直接以像素坐标重建，不能照搬 HDRP 的无条件 `1-y`，否则上下颠倒。T3 用非对称场景验证。

### 4.8 着色器采样与应用

材质侧只通过 `ShaderLibrary/ContactShadows.hlsl` 的函数，代码见 §3.4。要点：

- `mask == 0` 时在 `Load` 前返回 1；关闭时所有掩码为 0，因此没有真实纹理读取。
- 禁用时 Feature 绑定 1×1 中性 `R32_UInt`（值为 0），避免未定义绑定；`Load` 越界在 D3D 返回 0，也是中性。
- 不新增 shader 关键字；不给 Lit/SimpleLit/Particles/Terrain 增加变体。
- 透明物由 `_SURFACE_TYPE_TRANSPARENT` 排除；材质 `Receive Shadows = Off` 由 `_RECEIVE_SHADOWS_OFF` 排除；Deferred 用 GBuffer 的 `materialReceiveShadowsOff` 标志，二者结果一致。
- **API gate**：运行时只启用 D3D11/D3D12/Vulkan/Metal；shader 侧必须用 `SHADER_API_*` 包裹，非支持 API 返回 1。T5 的 shader 编译检查必须覆盖 GLES/WebGL 变体（至少编译不报错）。
- 附加光索引做 `[0, MAX_VISIBLE_LIGHTS)` 保护；越界返回 mask 0。
- Deferred 主光通过 `MainLightShadow` 自动覆盖；Deferred 附加光依赖 `_ADDITIONAL_LIGHT_SHADOWS`（见 §3.6）。
- 内置 Lit/SimpleLit/ComplexLit/Particles/Terrain 通过 `UniversalFragmentPBR` / `UniversalFragmentBlinnPhong` 覆盖；直接调用 `GetMainLight(shadowCoord)` 的自定义 shader 不覆盖；BakedLit 无实时直射光，不覆盖。

**全局绑定顺序（ComputePass 内）**：

1. 设置 compute 参数（长度/fade/bias/thickness/sampleCount/frameIndex/taaEnabled/矩阵/灯光 buffer）；
2. `DispatchCompute`；
3. `SetGlobalTexture("_ContactShadowTexture", rt)`；
4. `SetGlobalFloat("_ContactShadowOpacity", volume.opacity)`；
5. `SetGlobalInt("_MainLightContactShadowMask", mainMask)`；
6. `SetGlobalFloatArray("_AdditionalLightsContactShadowMasks", floatMasks)`（元素为 `math.asfloat(mask)`）；
7. `SetGlobalInt("_ContactShadowLightCount", records.Count)`。

`taaEnabled` 定义为 `cameraData.postProcessEnabled && cameraData.antialiasing == AntialiasingMode.TemporalAntiAliasing`；`frameIndex = Time.frameCount`。XR 使用 `ScreenSpaceLightingXr` 的每 view 矩阵与相机位置。

### 4.9 清理与共存

- `ScreenSpaceContactShadowsCleanupPass` 在 `AfterRenderingOpaques`：
  - 绑定 1×1 中性纹理；
  - `_MainLightContactShadowMask = 0`；
  - `_AdditionalLightsContactShadowMasks` 全部清零；
  - `_ContactShadowOpacity = 0`；
  - `_ContactShadowLightCount = 0`。
- **绑定责任**：ComputePass 在 dispatch 后设置真实纹理/掩码/opacity；CleanupPass 是状态归零的唯一责任点。
- **多相机**：每个 Active 相机都必须完成 Compute→Cleanup 配对；Inactive 相机依赖上一 Active 相机的 Cleanup 状态。中性 1×1 纹理在 Feature 资源中创建一次，`Dispose` 释放。
- 与 URP `ScreenSpaceShadows` feature 共存：先算屏幕空间 shadow map，再与接触阴影取 `min`。
- 与 GTAO/SSGI/SSR：互不影响；接触阴影只改直射光遮挡。
- 透明物：编译期排除 + opaques 后中性绑定。
- `ScreenSpaceContactShadowsDebugPass` 在 `Execute` 时按当前相机取 per-camera packed RT 句柄（不能缓存上一相机句柄），不依赖 cleanup 之后的全局绑定；可以在 `BeforeRenderingPostProcessing` 继续显示 mask/fade。
- 关闭 Volume / 移除灯组件 / 移除 Feature：图像逐像素不变；Feature `Dispose` 释放 RT/ComputeBuffer/Material，并 `UnregisterOwner`。

### 4.10 与 HDRP 的已知差异

| 项 | HDRP | 本 Feature v1 | 影响 |
|---|---|---|---|
| 每像素灯数 | FPTL tile 列表 | 最多 24 盏接触阴影灯全遍历 | 性能；正确性不变 |
| 深度金字塔 | 共享 depth pyramid mip 1 | 复用 URP 共享金字塔（mip 1 首采 + mip 0 确认） | 基本一致 |
| 共享金字塔输入 | 深度专用 | 现有共享 pass 同时请求 Motion（SSL 需要） | 仅 Contact Shadows 时多一次 motion 准备；follow-up 可拆分输入 |
| TAA | 内置 `_TaaFrameInfo` | 显式上传 frameIndex/taaEnabled | 视觉一致 |
| Quality 分级 | Lighting Quality Settings | 直接 sampleCount | UI 不同 |
| 世界坐标 | camera-relative | 绝对坐标 + 朝相机 bias | 视觉一致；大世界精度略低 |
| 槽位 | 方向光/附加光统一分配 | 主光优先 `bit 0`，附加光随后；总计 24 | 仅内部索引不同 |
| 逐灯启用 | Light Inspector 的 Contact Shadows 开关（`HDAdditionalLightData`） | 独立 `ContactShadowLight` 组件 | UX 不同；不修改 `UniversalAdditionalLightData`，按 §4.6 流程添加组件 |
| 灯不投射 shadow map | 支持 | Forward 支持；Deferred 主光支持；Deferred 附加光仅在 `_ADDITIONAL_LIGHT_SHADOWS` 变体启用时支持（见 D10） | 已知限制（D10） |
| 透明物 | 不支持 | 不支持 | 一致 |
| Receive Shadows = Off | 行为可能不同 | v1 跳过（与 URP 语义一致） | 明确差异 |
| Vulkan / Metal | 支持 | 代码 gate 支持，但本轮只验证 D3D11/D3D12 | 未在本轮机器验证，不声称通过 |
| Area Light | 部分支持 | 不支持 | URP 本身不支持 |
| 自定义 shader / BakedLit | 所有 lit pass 经过 LightEvaluation | 仅内置 URP Lit 路径 | 已知限制 |
| Raytracing | 有 Ray Traced Contact Shadows | **完全没有** | 本轮排除 |

---

## 5. 模块责任与文件清单

### 5.1 工作流（验收责任，不是人员分工）

| 工作流 | 负责范围 | 主要任务 |
|---|---|---|
| W1 Feature Runtime | Feature/Volume/灯组件/History/Pass/Capability/全局绑定 | T1、T2、T4、T5(Feature/绑定)、T6 |
| W2 GPU/Shader | compute、URP shader 钩子/材质采样 | T3、T5(shader/compute) |
| W3 测试/证据 | 测试资产、player、矩阵、性能 | T7、T8 |
| W4 文档/收尾 | 契约、报告、ACCEPTANCE、清理/归档 | T0、T9 |

> T3/T5 是 W1+W2 联合任务：T3 的 Pass 脚手架与 T5 的全局绑定/Feature 编排归 W1，compute 与 `Shadows.hlsl`/`ContactShadows.hlsl` 归 W2。验收时分别要求代码 diff 与 GPU 证据。

### 5.2 文件级边界

| 类型 | 文件/目录 | 状态 |
|---|---|---|
| Feature Runtime | `Packages/.../Runtime/RendererFeatures/ContactShadows/**` | 新增 |
| Feature Editor | `Packages/.../Editor/RendererFeatures/ContactShadows/**` | 新增 |
| Feature Shaders | `Packages/.../Shaders/Utils/ContactShadows/**` | 新增 |
| 材质 shader 钩子 | `Packages/.../ShaderLibrary/ContactShadows.hlsl` | 新增 |
| 材质 shader 钩子 | `Packages/.../ShaderLibrary/Shadows.hlsl` | 修改（最小） |
| 包内测试 | `Packages/.../Tests/Editor/RendererFeatures/ContactShadows/**` | 新增 |
| 项目测试资产 | `Assets/Tests/ContactShadow/**` | 新增 |
| 现有 URP C# 文件 | `Packages/.../Runtime/**`（新增 `ContactShadows` 目录除外） | **不修改** |
| 现有 SSL/GTAO/DDGI | `Runtime/RendererFeatures/ScreenSpaceLighting`、`ScreenSpaceCommon`、`GroundTruthAmbientOcclusion`、`DynamicDiffuseGI`（若存在） | **不修改** |
| 文档 | `PLAN/TODO/10-ContactShadow/**`（HANDOFF/report/KnownDifferences）、`PLAN/ACCEPTANCE.md` | 修改/新增 |
| 默认渲染资产 | `Assets/Settings/URP-*.asset` | **不修改** |
| 既有测试资产/Golden | `Assets/Tests/ScreenSpaceLighting/**`、既有场景/Renderer/Profile/GoldenImages | **不修改** |
| 包元数据 | `package.json`、`CHANGELOG.md`、URP asmdef | **不修改** |

### 5.3 程序集与引用

- Runtime C# 直接编译进现有 `Unity.RenderPipelines.Universal.Runtime`；**不新增 runtime asmdef**。
- Editor C# 直接编译进现有 `Unity.RenderPipelines.Universal.Editor`；**不新增 editor asmdef**。
- 包内测试先确认现有 `Tests/Editor` asmdef 的覆盖范围：默认**不新增** asmdef，测试文件放在现有 asmdef 覆盖的目录下；仅当现有 asmdef 不覆盖 `Tests/Editor/RendererFeatures/ContactShadows` 时，才在该目录新增局部 asmdef，并在 T0 Contract 记录原因。
- 项目侧 `Assets/Tests/ContactShadow/Editor` 与 `Runtime` 脚本沿用 SSL 的 Assembly-CSharp-Editor / Assembly-CSharp 约定（无 asmdef）；`Tests/PlayMode` 新增 `Unity.ContactShadow.Tests.PlayMode.asmdef`，引用 URP Runtime/Core、TestRunner、`nunit.framework.dll`、`UNITY_INCLUDE_TESTS`。
- 不改既有 `Unity.ScreenSpaceLighting.Tests.*.asmdef`；不新增 runtime/editor asmdef；不使用 `InternalsVisibleTo`。

### 5.4 序列化与导入要求

- Feature 必须是 `public sealed`，Settings 标 `[Serializable]` 并以 `[SerializeField]` 持有；否则不会出现在 Add Renderer Feature 菜单或无法序列化。
- `ContactShadowsVolume` 必须 `public sealed` + `[VolumeComponentMenuForRenderPipeline]`；`ContactShadowLight` 必须 `public sealed` + `[AddComponentMenu]` + `[DisallowMultipleComponent]`。
- Feature 是 `ScriptableObject`，被序列化进 Universal Renderer Data；其 `ComputeShader` / `Shader` 引用必须序列化，player build 才能包含。
- Feature Inspector 提供 “Assign Defaults”，在包内 `Shaders/Utils/ContactShadows` 按文件名查找；找不到时显示警告。
- 共享金字塔 compute 引用：Feature 资源中同样序列化 `depthPyramidCompute`（与 SSL 相同的包内 `DepthPyramid.compute`），供 `ScreenSpaceDepthPyramidSystem.Acquire` 使用。
- 中性 1×1 `R32_UInt` 在 Feature 资源中创建一次并持有；不使用 `Texture2D.blackTexture`（类型不匹配）；`Dispose` 释放。
- 非 Development player 构建必须验证上述引用未被 strip（T8）。

### 5.5 用户接入流程（验收路径）

1. 创建/打开 Universal Renderer Data（例如 `Assets/Tests/ContactShadow/Renderers/ContactShadowForwardRenderer.asset`）。
2. 点击 **Add Renderer Feature** → **Screen Space Contact Shadows**。
3. 在 URP Asset（`ContactShadowForward.asset`）的 Renderer List 中引用该 Renderer。
4. 在 Volume Profile 中添加 **Shadowing/Contact Shadows** 并勾选 **Enabled**。
5. 在需要投射接触阴影的 Light 上 Add Component → **Contact Shadow Light**。
6. 进入 Play/Scene View，验证效果；关闭 Volume 或移除组件应恢复基线。

> URP 14 的 Renderer Feature 挂在 **Universal Renderer Data** 上；Pipeline Asset 通过 Renderer List 引用它。验收必须覆盖这条链路，而不是只在代码里 new 一个 feature。

---

## 6. 任务清单（T0–T9）

> 提交协议：每个任务单独提交；提交前通过该任务的门禁；任务完成即更新本文件的证据路径与状态；不得把多个任务混在一个提交里。

### 6.1 依赖图

```
T0 契约/基线
 └─> T1 Feature 骨架 + 导入/Volume 发现
      ├─> T2 共享深度金字塔 + 每相机 packed RT ─> T3 Compute raymarch
      └─> T4 灯组件发现 + 槽位/掩码
T4 ─> T7 验证资产（三模式场景/Renderer/Profile/测试脚本）
T3 + T4 + T7 ─> T5 URP shader 钩子 + 三模式接入
T5 ─> T6 Cleanup/UI/Debug/序列化检查
T6 + T7 ─> T8 矩阵执行/Player/证据/性能/回归
T8 ─> T9 文档/清理/归档
```

- T2 与 T4 可并行：T2 只改 Feature/History/资源，T4 只完善 T1 的灯组件发现逻辑/新增灯光构建类，不改 Feature。
- T3 依赖 T2（共享金字塔/History 就绪）；T3 先用临时主光记录打通，不依赖 T4。
- T4 依赖 T1；T7 依赖 T4（场景需要 `ContactShadowLight` 组件）；T7 可与 T3 并行。
- T5 依赖 T3/T4/T7：需要 compute、真实灯数据、三模式测试资产才能产出 A/B。
- T6 依赖 T5；T8 依赖 T6/T7（在 T7 资产上执行矩阵 + player + 证据）；T9 依赖 T8。
- **共享文件串行化**：`ScreenSpaceContactShadowsRendererFeature.cs` 只按 T1→T2→T5→T6 顺序改；`ScreenSpaceContactShadowsPass.cs` 只按 T3→T5 顺序改；Settings/Resources/ShaderIDs 由 T1 一次定义全部字段，T3/T4 不互相加字段；`Shadows.hlsl` 只由 T5 改；测试**资产**只由 T7 提交（T1 只提交 import test 脚本）；`PLAN/ACCEPTANCE.md` 只由 T9 改。

### 6.2 任务定义

#### T0 契约与基线冻结

- **目标**：冻结 D1–D12、参数默认值、AC 阈值、改动预算。
- **产出**：`validation/P0/Contract.md`、`validation/P0/Baseline.json`。
- **内容**：
  - HEAD `cedd1bb`、Unity/URP 版本、测试机 GPU、`graphicsDeviceType`。
  - 既有 EditMode/PlayMode/T12 基线（以实际运行结果为准）。
  - `.rdc` 数量/字节/预算。
  - 明确“已有 URP C# 文件零修改、已有 URP shader 只改 `Shadows.hlsl`、新增 `ContactShadows.hlsl`”的验收口径。
- **验收**：文档入库；无代码改动；所有者确认 D1–D12。
- **门禁**：若所有者不接受 shader 钩子，先改契约，不进入 T5。

#### T1 Feature 骨架 + Renderer/Volume 导入

- **目标**：建立包内 Feature/Volume/灯组件/空 Pass；证明“能从 Renderer Data 导入、能在 Volume Profile 添加、能 Add Component”。
- **改动**：
  - `Runtime/RendererFeatures/ContactShadows`：Feature（`public sealed` + `[DisallowMultipleRendererFeature("Screen Space Contact Shadows")]`）、`ContactShadowsVolume`（`public sealed` + `[VolumeComponentMenuForRenderPipeline]`）、`ContactShadowLight`（`[AddComponentMenu]` + `[DisallowMultipleComponent]`）、空 `ScreenSpaceContactShadowsPass`（只实现 Setup/Execute 空壳；T3 填充 compute，T5 填充全局绑定）、Settings/Resources/ShaderIDs/Capability 骨架；
  - 包内 `ContactShadowVolumeTests` 与项目侧 `ContactShadowFeatureImportTests`（临时 fixture）；
  - **Settings/Resources/ShaderIDs 在 T1 一次性定义全部已知字段/ID（含 debug 字段）**，后续 T2/T3/T4/T6 不互相加字段，也不因新字段重写 T7 资产；
  - `Editor/RendererFeatures/ContactShadows`：Feature Inspector + Assign Defaults；
  - 导入测试使用 EditMode 临时创建 Renderer/Pipeline/Volume Profile，`TearDown` 删除；**不提交测试资产**（Pipeline/Renderer/Volume/Scene）；只提交 `Assets/Tests/ContactShadow/Editor/ContactShadowFeatureImportTests.cs` 测试脚本。
- **验收**：
  - Feature 出现在 **Add Renderer Feature** 菜单；添加/移除/domain reload 后仍正常；
  - Volume Profile 出现 **Shadowing/Contact Shadows**；灯上能 Add Component → Contact Shadow Light；
  - `AddRenderPasses` 能读到 Volume；关闭时 **Compute 不入队、不分配 RT、不写全局**；状态按 §4.1 的 Inactive 行处理；
  - 无 compute 时不抛异常，只 warning 一次；
  - 默认 `Assets/Settings/URP-*.asset` 未被修改。
- **证据**：临时 Renderer/Pipeline/Volume 的序列化内容；Feature/Volume/Light Inspector 截图；`ContactShadowFeatureImportTests`、`ContactShadowVolumeTests`。
- **回滚**：删除新增 Feature 目录 + import test 脚本；没有测试资产需要清理。

#### T2 共享深度金字塔 + 每相机 packed RT

- **目标**：Feature 作为 `ScreenSpaceDepthPyramidSystem` 的 owner；复用 mip 1/mip 0；每相机/eye 的 packed RT 生命周期正确。
- **改动**：
  - `ScreenSpaceContactShadowsHistory`；
  - `ScreenSpaceContactShadowsResources.depthPyramidCompute`；
  - `RegisterOwner/UnregisterOwner`；
  - `Acquire` 与 `CameraHistoryStore` 接入；
  - `ScreenSpaceContactShadowsCapability`（复用 `ScreenSpaceCapability` + `R32_UInt` 检查）。
- **验收**：
  - Contact + SSL/GTAO 同时存在时，Depth Pyramid pass 每个相机每帧只入队一次；
  - Contact 使用 mip 1 首采、mip 0 确认（offset/size 正确）；
  - 多相机/尺寸变化/XR layout 变化不泄漏；
  - 不支持 API/格式时 no-op + 一次 warning；
  - 深度 pyramid 不可用时：Compute 不入队、packed RT 不分配；归零状态由 T6 的 CleanupPass 验证（T2 只验证 History/RT 不泄漏）。
- **证据**：EditMode `ContactShadowHistoryTests`、`ContactShadowCapabilityTests`；Frame Debugger 203 pass；金字塔 debug 图。
- **依赖**：T1。

<a id="t2-evidence"></a>

**T2 实测（D3D11，2026-11-24）**：[专项 24/24](validation/T2/EditMode.xml)、[既有 EditMode 184/184](validation/T2/Regression.xml)、[PlayMode 6/6](validation/T2/PlayMode.xml)。

- 真实 `Camera.Render()`（96×64）：[Contact 单独开启](validation/T2/ContactOnlyDepthAtlas.png)和[叠加 SSL + GTAO](validation/T2/ContactSslGtaoDepthAtlas.png)均只执行一次 pass 203、一次 mip 0 拷贝；组合配置的三个 history 引用同一图集。GPU 读回 mip 0/1 中心均为 0.18750003、背景为 0，逐像素核对 mip 1 是对应 2×2 最近深度。
- pass 208 仅在共享深度就绪后分配 packed RT；未 dispatch 接触阴影、未写材质全局量。未标记灯不会为 Contact 分配 history 或触发独立构建。奇数尺寸、双眼独立条目、数组布局切换与释放已测；不是头显实机测试。射线先读 mip 1、同一 t 读 mip 0 的执行验证属于 T3。
- 当前交付尚无 T3 contact compute；测试用未 dispatch 的 compute 引用满足 T1 资源门禁，深度生产使用真正的共享 kernel。保留缺少引用时只警告、不分配的行为。
- 按 §8.1.6，Frame Debugger 事件列表为 **PARTIAL**：batch editor 回报 `locallySupported=False, eventCount=0`，不声称有交互式捕获。使用上述执行/dispatch 计数和 GPU 图像作替代证据。组合配置有一条临时纹理绑定警告及 GTAO 编译警告，保留在 XML；未把组合效果自身的画质列入本轮结论。
- 从仓库根目录复现：`./URPSample01/PLAN/TODO/10-ContactShadow/validation/Invoke-ContactShadowTests.ps1 -OutputRoot URPSample01/PLAN/TODO/10-ContactShadow/validation/T2 -Filter 'ContactShadowPyramidRenderTests;ContactShadowCapabilityTests;ContactShadowHistoryTests;ContactShadowVolumeTests;ContactShadowFeatureImportTests' -RecordDepthEvidence`。

#### T3 Compute raymarch

- **目标**：移植 HDRP 算法；先用方向光单灯打通。
- **改动**：
  - `ContactShadows.compute::KContactShadow`（32 B 记录布局见 §4.2/§4.6）；
  - `ScreenSpaceContactShadowsPass`（+8）：只负责 params/UAV/灯光 buffer 绑定与 dispatch；
  - 灯光记录先用**临时主光单灯**打通，不依赖 `ContactShadowLight`（T4）；
  - 方向光 + 点/聚光分支；
  - **不在 T3 绑定全局纹理/掩码**；该绑定由 T5 在 Feature 中完成，避免 T3/T5 同时改全局状态。
- **验收**：
  - packed RT 在物体背光侧有遮挡 bit、朝光侧无（通过 `AsyncGPUReadback`/临时 debug blit 读取，不依赖最终光照）；
  - bias 默认无 acne（mask 图）；
  - sampleCount 4/10/64 有可测差异（mask/fade）；
  - 半分辨率命中→全分辨率确认（mip 1 首采、mip 0 同 t 复采；薄物体 mask 边缘）；
  - 屏外 vignette 与距离 fade 生效（fade 图）；
  - 上下/左右方向正确（非对称场景，不出现 UV 翻转）；
  - 单灯 dispatch 可被 Frame Debugger/计数器观察到。
- **证据**：`validation/P3/` packed RT 的 mask/fade PNG + 指标 JSON（readback 或临时 debug blit）；**不产出三模式最终 A/B**（那属于 T5）。
- **依赖**：T2。

<a id="t3-evidence"></a>

**T3 实测（2026-11-24）：通过。** pass 208 写每相机 packed RT，不写全局掩码、不改材质。临时输入为 URP 选出的方向性主光（bit 0，不要求 marker，显式关闭的 marker 仍跳过）；不是最终的逐灯分配或三路径光照接入。

| 实测 | 结果 |
|---|---|
| 真实相机，256×192，离世界原点，主光 `shadows=None` | [D3D11](validation/P3/D3D11/Raymarch.json) / [D3D12](validation/P3/D3D12/Raymarch.json) 均为 7 次渲染、每次 1 个 contact dispatch。水平背光/朝光 ROI 为 392/0，垂直为 200/0；默认 bias 的纯平面 0 个遮挡，细边粗级误命中 0。见[水平 mask](validation/P3/D3D11/Horizontal10Mask.png)、[垂直 mask](validation/P3/D3D11/Vertical10Mask.png)。|
| 步数与 fade | 4→10、10→64 步分别改变 1260、64 个 mask 像素；[屏幕中心→边缘](validation/P3/D3D11/Horizontal10Fade.png)命中像素的平均 fade 字节为 255→233.3→98.3，近端和远端 50% 距离坡均为 127。两 API 的 14 对 mask/fade PNG 逐字节相同。 |
| mip 1 首采，mip 0 同一 t 确认 | 单独粗命中/单独细命中均读回 0；一条恰好一个步长宽的遮挡条，两级均命中时读回 `FF000007`（手工方向/点/聚光记录）。另测数组切片隔离；不是 XR 头显实测。专项 [D3D11 30/30](validation/P3/D3D11/EditMode.xml)、[D3D12 30/30](validation/P3/D3D12/EditMode.xml)，无跳过；[既有 EditMode 184/184](validation/P3/Regression/Regression.xml)、[PlayMode 6/6](validation/P3/Regression/PlayMode.xml)。 |
| 非 Development Win64 Player | [一次构建](validation/P3/Player/BuildSummary.json)，同一 EXE 各以 [D3D11](validation/P3/Player/Direct3D11/Probe.json)、[D3D12](validation/P3/Player/Direct3D12/Probe.json) 运行。序列化引用实际 kernel，合成图集直接 dispatch：指定 bit 5 命中 `FF000020`，朝光/镜像/零灯对照为 0。Player 验证打包与执行，不替代上面的真实相机测试。 |

复现：在仓库根目录的 PowerShell 中，令 `$v='./URPSample01/PLAN/TODO/10-ContactShadow/validation'`，运行 `foreach ($api in 'd3d11','d3d12') { & "$v/Invoke-ContactShadowTests.ps1" -OutputRoot "$v/P3/$($api.ToUpperInvariant())" -Filter 'ContactShadowRaymarchRenderTests;ContactShadowMipConfirmationTests;ContactShadowPyramidRenderTests;ContactShadowFeatureImportTests;ContactShadowVolumeTests;ContactShadowHistoryTests;ContactShadowCapabilityTests' -GraphicsApi $api -RecordRaymarchEvidence }`；Player 用 `& "$v/P3/tooling/Invoke-T3KernelPlayer.ps1"`，默认输出至本地新目录并清理临时工程/EXE。Vulkan、Metal 未实测。

#### T4 灯组件发现 + 槽位/掩码

- **目标**：在 T1 已落地的 `ContactShadowLight` 上实现发现逻辑；按参与顺序分配 bit（主光优先 `bit 0`，附加光随后）；总计 24 上限；与 `ForwardLights` 顺序一致。
- **改动**：
  - 复用/完善 T1 的 `ContactShadowLight`（**不在 T4 重复创建**；只补发现/校验逻辑）；
  - `ScreenSpaceContactShadowLights`：按 §4.6 的发现逻辑构建掩码/32 B 记录；
  - **只产出数据，不写全局、不改 Feature/Pass**；T5 调用 `Build` 并上传；`Build` 每次先清零输出数组。
- **验收**：
  - 0/1/23/24/25 边界（主光存在/不存在两种；主光未标记时第一盏附加光拿 bit 0）；
  - **未标记灯在标记灯之前**时索引不错位（additionalIndex 对每个非主光递增）；
  - 主光 + 附加光、点/聚光/方向光；记录的 `type` 为 0/1/2（不是 Unity `LightType`）；
  - 关闭单灯只影响该灯；`castContactShadows=false` / `Behaviour.enabled=false` / Rectangle 不参与；
  - 组件只在 Light 所在 GameObject 上生效；子/父/非 Light 对象不生效；
  - 上传后的全局掩码满足 `asuint(floatMasks[i]) == 1u << slot`；
  - `Build` 每次先清零 CPU 输出数组；全局掩码的上一帧/上一相机残留由 T5 上传前清零、T6 Cleanup 验证；
  - 组件在 prefab/实例上生效。
- **证据**：`ContactShadowSlotMappingTests`、`ContactShadowLightTests`；slot debug 图。
- **依赖**：T1；可与 T2 并行。

<a id="t4-evidence"></a>

**T4 实测（2026-11-24）：通过。** `Build` 只生成 CPU 数据；不改 Feature/Pass、材质、shader 或生产全局状态。T3 的临时主光仍是当前 pass 的输入。下一步先做 T7 三模式验证资产，再由 T5 接入真实逐灯记录。

| 验证 | 结果 |
|---|---|
| 灯发现与槽位 | [D3D11 59/59](validation/T4/D3D11/EditMode.xml)、[D3D12 59/59](validation/T4/D3D12/EditMode.xml)，均无跳过（本轮新增 29 例，既有专项 30 例）。覆盖主光有/无、未标记或不在索引 0；0/1/23/24/25 盏、URP 平台附加灯上限、跨快照清零、Prefab 覆盖及非 Light 组件只警告一次。 |
| 掩码与记录 | 三灯型编码 0/1/2；32 B 记录字段偏移 0/12/16/28。临时测试 kernel 用独立全局名上传完整 float 数组，GPU `asuint` 读回 bit 0…23、前导空位及溢出后的零，与预期逐项一致；不是 T5 的生产绑定验收。 |
| [槽位图](validation/T4/D3D11/SlotMapping.png) | 来自 CPU 构建结果，不是材质渲染图。行按 visible 顺序（首行未标记、次行主光、末行溢出），白格从左至右表示 bit 0…23；首末行都无白格。[D3D12 图](validation/T4/D3D12/SlotMapping.png)与 D3D11 逐字节一致。 |
| 回归 | D3D11 [EditMode 184/184](validation/T4/Regression/Regression.xml)、[PlayMode 6/6](validation/T4/Regression/PlayMode.xml)。[非 Development Win64 构建](validation/T4/Player/BuildSummary.json) 0 error / 0 warning；同一 Player 的 T3 kernel 在 [D3D11](validation/T4/Player/Direct3D11/Probe.json) / [D3D12](validation/T4/Player/Direct3D12/Probe.json) 均读回 `FF000020`，零灯对照无非零像素。 |

复现：沿用 T3 专项命令，将输出目录改为 `T4/<API>`，在 `-Filter` 追加 `;ContactShadowLightTests;ContactShadowSlotMappingTests`，并以 `-RecordSlotEvidence` 替换 `-RecordRaymarchEvidence`。

#### T5 URP shader 钩子 + 三模式接入

- **目标**：唯一核心 shader 改动；Forward/Forward+/Deferred 主光/附加光应用；透明物排除。
- **改动**：
  - `ShaderLibrary/ContactShadows.hlsl`（新增）；
  - `ShaderLibrary/Shadows.hlsl`（include + 两个 `min`，带 `_SURFACE_TYPE_TRANSPARENT` / `_RECEIVE_SHADOWS_OFF` / `lightIndex` 边界保护）；
  - Feature：`AddRenderPasses` 缓存 Volume、调用 `ScreenSpaceContactShadowLights.Build`、按 Active 状态入队 ComputePass（208）+ CleanupPass（300）；资源缺失时只入队 Cleanup 归零；
  - ComputePass 在 dispatch 后按 §4.8 顺序绑定 packed 纹理/掩码/opacity/lightCount。
- **验收**：
  - 三模式 A/B 均可见接触暗部（Deferred 附加光按 D10 配置）；
  - 透明物区域 A/B 像素差为 0；`Receive Shadows=Off` 材质 A/B 像素差为 0；
  - 关闭时逐像素一致；Compute 不入队；cleanup/中性状态成立或上一 Active 帧已归零；
  - 不新增 shader 关键字/不给 Lit 增加变体；
  - `git diff` 中已有 URP C# 文件修改为 0，已有 shader 修改仅 `Shadows.hlsl`。
- **证据**：`validation/P5/` 三模式图 + `git diff --stat` + `ContactShadowGlobalBindingTests`、`ContactShadowShaderCompileTests`。
- **提交拆分建议**：若单个提交过大，拆成 T5a（`ContactShadows.hlsl` + `Shadows.hlsl` 钩子）与 T5b（Feature 全局绑定/Compute+Cleanup 成对入队）；仍按 §6.1 的共享文件串行化，T5a 先合入。
- **依赖**：T3/T4/T7（compute、灯数据、三模式资产）。

#### T6 Cleanup、UI、Debug、序列化检查

- **目标**：清理全局状态；完整 Inspector；debug；序列化引用完整（player 构建留给 T8）。
- **改动**：
  - `ScreenSpaceContactShadowsCleanupPass`（AfterRenderingOpaques）；
  - Feature Inspector（使用 T1 已定义的 debug 字段，不新增序列化字段）；
  - `ScreenSpaceContactShadowsDebugPass`；
  - 资源默认值/校验；中性 1×1 纹理创建/释放。
- **验收**：
  - opaques 后中性纹理/零掩码/opacity=0/lightCount=0；
  - Active 帧即使 ComputePass 因资源缺失早退，CleanupPass 仍入队并归零；
  - debug 模式 Mask/Fade/Slot/Raw 可用；DebugPass 在 `Execute` 时按当前相机取 per-camera RT 句柄，多相机 debug 图不串帧；
  - 多相机连续渲染不串状态；Inactive 相机依赖上一 Active 相机的 Cleanup；
  - Feature 资产中 compute/shader/depthPyramidCompute 引用均已序列化，Inspector 无 missing。
- **证据**：`ContactShadowCleanupTests`；debug PNG；Feature 资产序列化检查。
- **依赖**：T5。

#### T7 验证资产（矩阵执行归 T8）

- **目标**：提交三模式测试场景/Pipeline/Renderer/Volume/PlayMode 测试脚本；供 T5 的 A/B 与 T8 的矩阵使用。
- **改动**：`Assets/Tests/ContactShadow/**`（**唯一提交测试资产与验证脚本的任务**；T1 的 import test 脚本除外；T1 的临时 fixture 已删除）。
  - 新增 `Assets/Tests/ContactShadow/Tests/PlayMode/Unity.ContactShadow.Tests.PlayMode.asmdef`，引用 URP Runtime/Core、TestRunner、`nunit.framework.dll`、`UNITY_INCLUDE_TESTS`；不改既有 asmdef。
- **资产内容**：
  - `ContactShadowBaseline.unity`：地面 + 箱体 + 方向光/点光/聚光 + `ContactShadowLight` 组件 + Volume Profile。
  - Forward / Forward+ / Deferred 三套 Pipeline + Renderer。
  - 透明物、`Receive Shadows=Off`、BakedLit、自定义 shader 对象。
  - 24/25 灯压力场景；MSAA/动态分辨率变体；透视/正交相机；XR mock 场景（可选）。
- **验收**：资产可加载；三模式 Renderer 引用正确；Volume/灯组件存在；默认资产未改；`.meta`/asmdef 完整；**不在 T7 产出最终矩阵证据**（T8 执行）；依赖 T5 行为的 PlayMode 测试只保证编译，T8 执行。
- **依赖**：T1（Feature/Volume）、T4（Light 组件）；可与 T3 并行。

**T7 实测（2026-11-24）：通过。** [D3D11 资产/导入检查 10/10](validation/T7/D3D11/EditMode.xml)，无跳过。持久化基线与 25 灯场景可重新加载，三种管线引用各自 Renderer（Forward+ / Deferred 显式引用聚类计算资源）；另有 4× MSAA Pipeline，正交、MSAA、动态分辨率备用相机。后者只验序列化，不代表硬件动态分辨率已实测。PlayMode 图像脚本已编译；最终颜色 A/B 留给 T5，完整矩阵留给 T8。未修改项目默认资产与 Build Settings。

#### T8 Player、验收与证据

- **目标**：按 AC-1…AC-16 生成证据，映射到 git diff 与 HEAD；完成 player 构建。
- **产出**：`report.md`、`validation/`；`PLAN/ACCEPTANCE.md` 由 T9 更新，T8 不直接改。
- **内容**：
  - 在 T7 资产上执行矩阵：
    - 模式：Forward / Forward+ / Deferred。
    - 灯光：Directional / Point / Spot；逐灯 shadow on/off。
    - 参数：opacity 0/0.5/1；min/max/fade；sampleCount 4/10/64；thickness/rayBias 边界。
    - 灯数：1 / 4 / 24 / 25；主光存在/不存在；未标记灯在标记灯之前。
    - MSAA：off/on；动态分辨率（若支持）。
    - 多相机/Scene View；透视/正交相机；XR mock（次级；失败记入 KnownDifferences）。
    - 透明物、`Receive Shadows=Off`、BakedLit、自定义 shader（已知限制验证）。
    - Deferred 附加光：Asset 开+当前灯投影、Asset 开+当前灯 `shadows=None` 但另一灯投影（Off 变体未 strip）、Asset 开+全 `shadows=None`（Off 变体未 strip）、Asset 关。
  - D3D11 与 D3D12 各构建一个 Development player + 一个非 Development player；检查 compute/shader 资源未被 strip。
  - A/B 图、差值图、区域亮度指标。
  - Feature 导入/Volume/灯组件证据。
  - 槽位单测、shader 编译、Frame Debugger pass 顺序；Frame Debugger 超时则用 pass 计数器/ProfilingSampler 日志替代，并在 report 标注。
  - 共享金字塔只构建一次的计数证据（Contact-only / Contact+SSL+GTAO 两种配置）。
  - 性能表：模式 × 灯数 × sampleCount；记录 GPU/CPU 与测试机。
  - 既有 shader 编译/SRP Batcher/变体数量基线；变化时记录新基线与原因。
  - raytracing 排除的 **scoped** grep/code review（只查本轮新增/修改文件；既有 DDGI 不算）。
  - `.rdc` 预算与证据体积说明。
- **验收**：每个 AC 都有 “结论 + 证据路径 + 复现命令”；player 无 missing 资源/异常；证据体积在 §8.4 预算内。
- **依赖**：T6（cleanup/UI/debug）与 T7（测试资产）。

#### T9 文档、清理、归档

- **目标**：文档一致、产物清理、移动到 Done；`KnownDifferences.md`/`report.md`/ACCEPTANCE 同步。
- **检查**：
  - HANDOFF/report/KnownDifferences/ACCEPTANCE/T0 Contract 五处无矛盾；HANDOFF 状态改为完成。
  - `PLAN/ACCEPTANCE.md` 执行时读取当前最后一节编号，新增下一节（当前最后一节为 §14，因此本次为 §15）；不得预写未来编号。
  - `KnownDifferences.md` 至少包含：Deferred 附加光限制（D10，含 Off 变体/当前灯 None 条件）、Receive Shadows=Off、BakedLit/自定义 shader、逐灯启用方式（`ContactShadowLight` 组件）、Vulkan/Metal 未验证、XR/MSAA 结果、性能。
  - 测试资产保留（`Assets/Tests/ContactShadow`）；T1 临时 fixture、T3 临时 readback/debug blit/临时主光记录、临时场景/材质/GameObject 删除；默认资产未改。
  - 日志本地-only；JSON/XML/PNG/txt 入库；无 `Library/`、`Temp/`、`Builds/`、player 二进制；`.rdc` 为 0 或不超预算。
  - 运行 `git ls-files '*.rdc'` 与 `git ls-files -i -c --exclude-standard '*.rdc'`：前者记录已跟踪捕获数量/字节，后者必须为空（无 ignored-but-tracked），并与预算对照。
  - 证据体积符合 §8.4（PNG 数量/总大小）；无 orphan PNG/metadata。
  - 归档到 `PLAN/Done/10-ContactShadow`；修正相对链接；删除空目录；无重复副本。
  - `git status` 干净；`git diff --stat` 只包含本轮预期文件；AC-12 的边界成立。
- **验收**：归档提交；AC-16 全项有证据。
- **停止条件**：AC-1…AC-16 均有 PASS/PARTIAL/ACCEPTED 证据；T10 未启动；归档完成。否则不归档。

### 6.3 任务冲突/遗漏审计

| 检查项 | 结论 |
|---|---|
| 两个任务改同一个文件？ | **共享文件串行化**：`ScreenSpaceContactShadowsRendererFeature.cs` 只按 T1→T2→T5→T6；`ScreenSpaceContactShadowsPass.cs` 只按 T3→T5；`Shadows.hlsl` 只由 T5 改。T2/T4 不并行改 Feature。 |
| T2 与 T4 可并行吗？ | 可以：T2 只改 Feature/History/资源，T4 只新增灯组件/灯光构建类；T4 不写 Feature/Pass。T3 依赖 T2。 |
| T3 与 T4 可并行吗？ | 可以：T3 只做 compute/pass，T4 只做灯组件/槽位；`ShaderIDs`/Settings/Resources 由 T1 预定义全部字段，二者不再互相加字段。 |
| 现有 URP 文件与新增文件混在同一任务？ | 只有 T5 修改现有 `Shadows.hlsl`；现有 URP C# 不在任何任务中修改；其余为 ContactShadows 目录新增文件。 |
| T1/T7 都改测试资产？ | T1 只用 EditMode 临时 fixture（`TearDown` 删除）并提交 import test 脚本；Pipeline/Renderer/Scene/Profile 资产只由 T7 提交。 |
| T7/T8 都做矩阵？ | T7 只提交资产/测试脚本；T8 执行矩阵、player 和证据。T5 依赖 T7 资产。 |
| T6/T8 都做 player？ | T6 只做序列化引用检查；player 构建/资源 strip/A-B/性能由 T8 做。 |
| T8/T9 都改 ACCEPTANCE？ | T8 只产出 `report.md`/`validation/`；`PLAN/ACCEPTANCE.md`、`KnownDifferences.md` 归档由 T9 做。 |
| T1/T6 都改 Feature/Inspector？ | T1 放骨架与全部字段；T6 在 T5 之后扩展 cleanup/debug/Inspector，串行。 |
| 是否遗漏 Volume 开启路径？ | T1 验证 Volume 菜单/读取；T7 验证开启/关闭画面。 |
| 是否遗漏 Renderer Feature 导入路径？ | T1 验证 Add Renderer Feature + 序列化；T7 验证 Pipeline Asset→Renderer List。 |
| 是否遗漏逐灯开关？ | T1 引入 `ContactShadowLight`；T4 实现发现/槽位；T7 验证单灯/多灯/25 灯/非 Light/子物体。 |
| 是否遗漏共享金字塔只构建一次？ | T2 验证 Contact + SSL/GTAO 的 pass 计数；T8 证据。 |
| 是否遗漏清理/中性状态？ | T5 成对入队 Compute+Cleanup；T6 CleanupPass；T8 AC；T9 检查。 |
| 是否遗漏 `Receive Shadows=Off`？ | §3.4 加 `_RECEIVE_SHADOWS_OFF`；T8 矩阵（T7 资产）；AC-10。 |
| 是否遗漏既有 shader 编译/SRP Batcher 回归？ | T8 显式运行；计数/兼容性变化时记录新基线，不静默改阈值。 |
| 是否遗漏 raytracing 排除？ | D12 + AC-13 + T8 的 **scoped** grep/code review；不扫既有 DDGI。 |
| 是否遗漏 deferred 附加光限制？ | D10 + §3.6 + KnownDifferences + AC-5/T7 配置（当前灯投影/当前灯 None 而其他灯投影/全 None+Off 未 strip/Asset 关）。 |
| 是否遗漏 XR/MSAA/动态分辨率？ | T8 矩阵（T7 资产）；XR 为次级，失败在 KnownDifferences 标注，不声称通过。 |
| 是否遗漏文档矛盾？ | T9 五处一致性检查；AC-16。 |
| 是否遗漏证据预算/复现命令？ | §8.4 预算；T8 每个 AC 带复现命令；T9 检查 orphan/链接。 |
| 是否误启动 T10？ | T10 明确不启动；若启动必须先修订 D5（已有 URP C# 0 修改）并重新走 T0 所有者确认。 |

---

## 7. 验收标准（AC）

| ID | 标准 | 证据 |
|---|---|---|
| AC-1 | Feature 能从 Universal Renderer Data 的 **Add Renderer Feature** 导入/移除；序列化后 domain reload 不丢；Pipeline Asset 的 Renderer List 指向该 Renderer | Renderer/Pipeline 资产序列化内容 + Inspector 截图 |
| AC-2 | Volume Profile 出现 **Shadowing/Contact Shadows**；`enabled=false` 无效果且 Compute 不入队；`enabled=true` 且有灯组件时生效；无 Volume 时无效果 | Volume 资产 + A/B PNG + Frame Debugger |
| AC-3 | 灯上的 `Contact Shadow Light` 能逐灯开关；主光 + 附加光；点/聚光/方向光 | 单灯/多灯 A/B + EditMode 测试 |
| AC-4 | 方向/遮挡正确：接触暗部在背光侧；默认 bias 无自阴影痤疮 | 三视角 PNG + 区域亮度 |
| AC-5 | 逐灯 `Light.shadows=None` 时：Forward 主/附加、Deferred 主光仍生效；Deferred 附加光仅在该灯 draw 的 `_ADDITIONAL_LIGHT_SHADOWS` 关键字启用时生效（Asset 开启 + Off 变体 strip，或**当前灯**投影）；当前灯 None 且 Off 变体未 strip 时记录为已知限制 | 三模式 A/B + KnownDifferences |
| AC-6 | 在 shadow map 未遮挡的接触区域：`opacity=0` 无效果；0.5 约一半；1 最大 | 区域亮度表 |
| AC-7 | 距离 fade：`<minDistance` 为 0；`>maxDistance+fadeDistance` 为 0；中间单调 | 相机距离序列图 |
| AC-8 | 屏外 fade：边缘无硬切；vignette 单调衰减 | 边缘带差值图 |
| AC-9 | 24 灯槽位正确（主光存在/不存在两种顺序）；第 25 盏无接触阴影；关闭单灯只影响该灯；掩码无残留 | slot debug + 单灯 A/B + EditMode 测试 |
| AC-10 | 透明物区域 A/B 差值为 0；`Receive Shadows=Off` 材质 A/B 差值为 0；BakedLit/自定义 shader 不覆盖（已知限制） | 透明/ReceiveOff 场景差值 + KnownDifferences |
| AC-11 | 固定相机/灯光/深度配置时，同一 Contact compute 产出的 debug mask 在三模式间逐像素一致（只比较 compute mask，不含光照应用差异；D10 不影响 mask）；如深度 prepass 有 1px 边缘差，非零差异 <1% 且记录原因 | mask PNG 对比 + 差异统计 |
| AC-12 | 已有 URP C# 文件修改 = 0；新增**功能 C#** 全在 `Runtime/RendererFeatures/ContactShadows` 与 `Editor/RendererFeatures/ContactShadows`（测试脚本按 §5.3 放置）；已有 shader 修改仅 `Shadows.hlsl`，新增 `ContactShadows.hlsl` | `git diff --stat` + `git status` |
| AC-13 | 本轮新增/修改文件中无 raytracing/raygeneration/DXR/TLAS；既有 DDGI 不算 | **scoped** `grep`/code review（仅 ContactShadows 目录 + `ContactShadows.hlsl` + `Shadows.hlsl` diff） |
| AC-14 | Contact-only 与 Contact+SSL+GTAO 两种配置下，共享 Depth Pyramid pass 每个相机（含 XR eye）每帧只出现一次；Contact 首采 mip 1、确认读 mip 0 | Frame Debugger + 计数器断言 |
| AC-15 | 既有 EditMode/PlayMode/T12 全绿；新增测试全绿；shader 编译 0 error；MSAA/ReceiveOff/D10/正交相机配置有记录；SRP Batcher/变体数量无未解释回退；性能记录 | XML/日志/性能 JSON |
| AC-16 | 测试改动、证据留存、产物清理、文档一致性完成；PNG ≤80 张/≤45 MB，`validation/` 总预算 ≤50 MB；日志本地-only；`.rdc` 默认 0（经所有者批准最多 1 个 ≤20 MiB，并记录）；Build 不入库 | `git status` + `git ls-files '*.rdc'`（ignored-but-tracked 为 0）+ validation 清单 + ACCEPTANCE |

> 性能绝对阈值由 T0 在测试机上冻结（建议 1920×1080、4 灯、10 samples 每模式 ≤1.0 ms；超出则记录为 follow-up，不阻塞正确性验收）。

---

## 8. 测试改动、证据与善后

### 8.1 既有测试怎么改

1. **不改既有断言**：现有 EditMode/PlayMode/T12 的期望值保持；只追加新 suite。既有 SSL/GTAO 场景、Renderer、Profile、GoldenImages 一律不改。
2. **新增 round-local wrapper**：参考 06/09 的 wrapper，新增 `validation/Invoke-ContactShadowTests.ps1`，必须接受 `-OutputRoot`，不硬编码旧轮次目录。
3. **shader 编译回归**：新增的 `ContactShadows.hlsl` + `Shadows.hlsl` include 可能影响既有 shader 编译检查的计数。T8 必须：
   - 以 HEAD 的 06 轮 T12 矩阵与 shader 编译检查为基线（Forward 34/34、Forward+ 34/34、Deferred 35/35，具体以实际运行结果为准）；
   - 运行既有 shader 检查，并记录 SRP Batcher 兼容性与变体数量；
   - 若计数/消息变化，记录新基线、原因和 diff，不静默放宽阈值；
   - 在 `report.md` 说明。
4. **测试资产**：新增 `Assets/Tests/ContactShadow` 的 Pipeline/Renderer/Volume/Scene；默认 `Assets/Settings/URP-*.asset` 不改。PlayMode 测试新增 `Assets/Tests/ContactShadow/Tests/PlayMode/Unity.ContactShadow.Tests.PlayMode.asmdef`，不改既有 asmdef。
5. **测试清理**：T1 的临时资产在 `TearDown` 删除；T7 提交的专用资产保留；不留下 `Assets/Scenes` 临时对象。
6. **Frame Debugger 兜底**：若 Frame Debugger 超时，使用 pass 计数器/ProfilingSampler 日志作为替代证据，并在 report 标为 PARTIAL，不以推测代替。

### 8.2 新增测试清单

| 测试 | 类型 | 覆盖 |
|---|---|---|
| `ContactShadowVolumeTests` | EditMode（包内） | 默认值/范围/菜单路径/`enabled` |
| `ContactShadowCapabilityTests` | EditMode（包内） | API/格式 gate；不支持时 no-op |
| `ContactShadowHistoryTests` | EditMode（包内） | 多相机/尺寸/XR/Dispose；packed RT 生命周期 |
| `ContactShadowSlotMappingTests` | EditMode（包内） | 0/1/23/24/25；与 ForwardLights 顺序一致；未标记灯在标记灯之前不导致索引错位；`type` 编码 0/1/2；掩码 `asuint` 位模式 |
| `ContactShadowShaderCompileTests` | EditMode（包内） | 核心头文件 + Feature shader/compute 0 error |
| `ContactShadowCleanupTests` | EditMode（包内） | opaques 后中性/零掩码/opacity=0 |
| `ContactShadowFeatureImportTests` | EditMode（项目侧） | Renderer 资产含 Feature；Pipeline 引用 Renderer；Volume Profile 含组件；reload |
| `ContactShadowLightTests` | EditMode（包内） | 组件发现/主光/附加光/prefab/关闭/子父物体/非 Light/Rectangle |
| `ContactShadowReceiveShadowsTests` | PlayMode（项目侧）+ 视觉 | `_RECEIVE_SHADOWS_OFF` / `materialReceiveShadowsOff` 时不应用 |
| `ContactShadowGlobalBindingTests` | EditMode（包内） | Active 时 208 后绑真实纹理/掩码；300 后绑中性/零；Inactive 时不写 |
| `ContactShadowDeferredLimitationTests` | PlayMode（项目侧）+ 三模式 A/B | 当前灯 None 且 Off 变体未 strip：该灯 Deferred 附加光按 D10 不生效，其他投影灯的接触阴影不受影响；Asset 关时全部不生效；Forward/Deferred 主光仍生效 |
| `ContactShadowPlayerSerializationTests` | Player（T8，由 setup/verification 执行） | 非 Development build 中 compute/shader/depthPyramidCompute 未 strip |
| `ContactShadowRuntimeToggleTests` | PlayMode（项目侧） | Volume/灯组件/Feature 运行时开关；次帧生效；多相机 |

**测试归属**：T1 `FeatureImport`/`Volume`；T2 `Capability`/`History`；T4 `Light`/`SlotMapping`；T5 `GlobalBinding`/`ShaderCompile`；T6 `Cleanup`；T7 `ReceiveShadows`/`DeferredLimitation`/`RuntimeToggle`（资产+脚本）；T8 `PlayerSerialization` 与矩阵执行。

### 8.3 Player 验证

- D3D11 与 D3D12 各构建一个 Development player + 一个非 Development player；release 构建用于验证资源未被 strip。
- 使用专用的 Forward/Forward+/Deferred 三套 Pipeline + Renderer 资产（T7）与 `ContactShadowVolumeProfile.asset`。
- 验证：
  - Feature/compute/shader/depthPyramidCompute 在 build 中存在；
  - Volume/灯组件生效；
  - A/B PNG；
  - 无 missing resource/异常；
  - 性能 ms（记录测试机 GPU/分辨率）。
- Vulkan/Metal 只做 API gate 检查；本轮机器未验证的组合必须在 `KnownDifferences.md` 标注“未验证”，不声称通过。
- Player 构建目录只用于证据，记录路径/大小/结果后删除，不入库。

### 8.4 证据留存

| 类型 | 留存 | 说明 |
|---|---|---|
| Contract/Baseline | `validation/P0/*.json/*.md` | 入库 |
| 测试结果 | `*.xml` | 入库 |
| 汇总/报告 | `*.json/*.txt`、`report.md` | 入库 |
| KnownDifferences | `KnownDifferences.md` | 入库，T9 更新 |
| 图像 | 小尺寸 PNG（A/B/差值/debug） | 入库；**≤80 张、≤45 MB** |
| 日志 | `*.log` | **本地-only**，`.gitignore` 已排除；不入库 |
| `.rdc` | 默认 0 | 预算 1000.1/1024 MiB；若必须，1 个 ≤20 MiB 1280×720，记录并获批 |
| Player/Builds | 不入库 | `Builds/`、二进制、`Library/`、`Temp/` 清理；保留 `BuildSummary.json`（路径/大小/结果） |
| 证据总预算 | `validation/`（不含既有 `.rdc`） | 目标 ≤50 MB；超出需在 report 说明 |

`P0/P3/P5` 分别是 `T0/T3/T5` 的证据目录；其他任务使用 `validation/T{n}`。

### 8.5 归档与清理

1. 删除 T1 临时 fixture、**T3 临时 readback/debug blit/临时主光记录**、临时场景/材质/GameObject；保留 T7 的 `Assets/Tests/ContactShadow` 专用验证资产。测试计数器若保留，必须限定 `UNITY_EDITOR`/`DEVELOPMENT_BUILD` 并在 report 说明用途。
2. 确认默认 Pipeline/Renderer/场景/GoldenImages 未被修改；如手动测试改过，恢复。
3. 删除 `Builds/`、player 日志、临时 `.rdc`、`Library/`/`Temp/` 残留；保留 `BuildSummary.json`。
4. 把 `PLAN/TODO/10-ContactShadow` 移到 `PLAN/Done/10-ContactShadow`；不保留重复副本。
5. 更新 `PLAN/ACCEPTANCE.md`：按执行时的最后一节编号新增下一节（当前 §14 → §15）。
6. 更新 `KnownDifferences.md`（D10、Receive Shadows=Off、BakedLit/自定义 shader、逐灯启用方式（`ContactShadowLight` 组件）、Vulkan/Metal 未验证、XR/MSAA、性能）。
7. 修正相对链接；删除空目录；检查 `git status` 干净、`git diff --stat` 只含预期文件。
8. 确保 `.meta` 随新资产提交；删除资产时对应 `.meta` 一并删除，不留 orphan。
9. 最终提交只包含计划/代码/测试/证据；不包含日志/构建/大 `.rdc`；确认 T10 未启动。

### 8.6 文档一致性检查

- **五处**：HANDOFF（本文件）、`report.md`、`PLAN/ACCEPTANCE.md`、`KnownDifferences.md`、T0 `Contract.md`/`Baseline.json` 的：
  - 参数默认值；
  - 槽位规则（主光优先、总计 24）；
  - 三模式覆盖与 Deferred 附加光限制；
  - Receive Shadows=Off 行为；
  - 改动预算（已有 C# 0 修改、已有 shader 只改 `Shadows.hlsl`）；
  - `.rdc`/证据预算；
  - 测试/产物留存。
- 任何一处改动必须同步其余四处；T9 逐项对照。
- AC 证据路径必须存在、无死链；未验证的平台/组合写“未验证”，缺失项标 PARTIAL/ACCEPTED，不声称 PASS、不用推测补齐。

---

## 9. 风险与缓解

| ID | 风险 | 缓解 |
|---|---|---|
| R1 | URP 绝对世界坐标导致 bias 方向错误、自阴影 | 公式改为 `positionWS - (positionWS - cameraPosWS) * bias`；远离原点场景专项测试 |
| R2 | 共享深度金字塔 pass 被多个 Feature 重复入队 | 复用 `ScreenSpaceDepthPyramidSystem` 的 `ShouldBuild`；T2/AC-14 用 Frame Debugger/计数器验证只出现一次 |
| R3 | Feature 从 Renderer Data 导入失败/player 丢资源 | T1 先做导入门禁；序列化 compute/shader；T6 序列化检查；T8 player 构建验证 |
| R4 | Volume 未启用/无灯组件时仍有开销 | `AddRenderPasses` 三重门控（Volume/平台/灯）；T1/T2 验证 Compute 不入队、不分配 RT；状态按 §4.1 处理 |
| R5 | 24 盏灯全遍历导致 GPU 成本高 | v1 记录性能；默认测试 4 盏；follow-up 用 Forward+ tile |
| R6 | 共享金字塔当前同时请求 Motion（SSL 需要） | 已知差异；仅 Contact Shadows 时接受一次 motion 准备；follow-up 可拆分输入 |
| R7 | Deferred 附加光依赖 `_ADDITIONAL_LIGHT_SHADOWS` 变体（Asset 开启 + Off 变体 strip，或当前灯投影） | D10 明确记录；AC-5 限定范围；可选 T10 另行评估 |
| R8 | `R32_UInt` LoadStore 不支持 | `ScreenSpaceCapability` + Contact 自己的格式检查；不支持时 no-op + 一次 warning |
| R9 | GLES/OpenGL NDC z 约定不同 | 运行时 API gate；不支持平台不启用 |
| R10 | 掩码/纹理泄漏到透明 pass 或下一相机 | Active 帧 Compute+Cleanup 成对；CleanupPass 中性绑定 + 清零；EditMode 清理测试 |
| R11 | 新增 shader 头文件影响既有 shader 编译计数 | T8 运行既有检查；变化时记录新基线并说明 |
| R12 | 证据/`.rdc`/日志超预算 | 默认 0 `.rdc`；日志本地-only；PNG 控制尺寸/数量 |
| R13 | 测试改动污染默认资产 | 专用测试 Pipeline/Renderer；T9 检查 `Assets/Settings` 未改 |
| R14 | 文档五处不一致 | T9 对照表；AC-16 |
| R15 | 共享金字塔在编辑器同一帧复用旧深度 | 现有 `ShouldBuild` 按 frame + VP 判断；物体移动/相机变化专项测试 |
| R16 | 内部 API 耦合（Feature 与 URP 包同版本） | Feature 在包内，跟随包版本；升级 URP 时合并检查；不承诺跨版本可移植 |
| R17 | `Receive Shadows=Off` 行为与 HDRP 不同 | v1 明确采用 URP 语义；§3.4/AC-10/KnownDifferences 三处一致 |
| R18 | `ContactShadowLight` 挂在子物体/非 Light 上导致“不生效” | 只查 Light 所在 GameObject；非 Light/子父对象忽略并 warning 一次；T4 专项测试 |
| R19 | 多任务改同一文件导致冲突/覆盖 | §6.1 共享文件串行化：Feature T1→T2→T5→T6、Pass T3→T5、`Shadows.hlsl` 仅 T5、测试资产仅 T7、ACCEPTANCE 仅 T9 |
| R20 | 证据路径硬编码/不可复现 | wrapper 必须支持 `-OutputRoot`；每个 AC 在 report 写复现命令；T9 检查死链 |
| R21 | Release build 资源被 strip | T8 增加非 Development build；检查 compute/shader/depthPyramidCompute；失败则阻塞 AC-15 |
| R22 | Frame Debugger 超时导致证据缺失 | 用 pass 计数器/ProfilingSampler 日志替代，并在 report 标 PARTIAL；不以推测代替 |
| R23 | `#pragma only_renderers d3d11` 是否覆盖 D3D12，与材质侧 `SHADER_API_*` 分支不一致 | T3/T5 在 D3D11 与 D3D12 两个 player 上编译/运行验证；若 D3D12 不覆盖，调整 pragma 或使用 D3D11/D3D12 共同宏；T0 记录实际宏 |

---

## 10. 可选 T10（不纳入本轮）

**仅在所有者要求“Asset 关闭 Additional Light Shadows，或当前附加光 `shadows=None` 且 Off 变体未 strip 时，Deferred 附加光也支持接触阴影”时启动。**

- 需要修改：
  - `Runtime/DeferredLights.cs`：每次附加光 draw 设置 `_ContactShadowLightIndex`（additional light index），不依赖 shadow pass；
  - `Shaders/Utils/StencilDeferred.hlsl`：在 `unityLight.shadowAttenuation` 之后增加不依赖 `_ADDITIONAL_LIGHT_SHADOWS` 的接触阴影取用。
- 代价：已有 URP C# 与 shader 修改增加，需要额外的 deferred 回归/证据。
- **启动前提**：T10 会破坏 D5（已有 URP C# 0 修改）；必须先修订 D5/D6、重新走 T0 所有者确认，并新增 AC/证据；不能在本轮直接启动。
- 本轮不启动、不列入 AC、不影响 D1–D12。

---

## 11. 立即下一步

1. **T0**：写 `validation/P0/Contract.md`，冻结基线、参数、AC 阈值与“已有 URP C# 零修改 / 已有 shader 只改 `Shadows.hlsl`”口径；确认 D5/D6/D10。
2. **T1**：搭包内 Feature/Volume/Light 组件骨架；用 EditMode 临时资产验证 Add Renderer Feature、Volume Profile、Add Component 三条导入链路；不接 compute，不提交 `Assets/Tests/ContactShadow`。
3. **T2**：接 `ScreenSpaceDepthPyramidSystem` + 每相机 packed RT；Frame Debugger 确认 203 只出现一次。
4. **T3**：移植 compute，用临时主光单灯打通；不绑全局。
5. **T4**：灯组件/槽位/32 B 记录；不写全局。
6. **T7（资产先行）**：提交三模式场景/Pipeline/Renderer/Profile/PlayMode 测试脚本；T5 需要这些资产做 A/B。
7. **T5**：唯一集成点：Feature 成对入队 Compute+Cleanup、绑定全局、`Shadows.hlsl` 钩子；用 T7 资产做三模式。
8. **T6**：cleanup/UI/debug/序列化检查。
9. **T8**：在 T7 资产上执行矩阵、player、证据、性能、回归。
10. **T9**：文档/KnownDifferences/清理/归档。

> 完成 T3 后即可得到第一张 packed RT 的 mask/fade 调试图；最终三模式 A/B 要等 T5 接上 `Shadows.hlsl`。在 T5 之前不要改 `Shadows.hlsl`，避免把算法问题与接入问题混在一起定位。