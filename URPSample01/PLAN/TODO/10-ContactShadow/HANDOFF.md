# 10-ContactShadow 交接：HDRP Contact Shadows 调研与 URP 包内 Renderer Feature 迁移计划

> 状态：**调研完成，计划按“包内 Renderer Feature”复核后定稿，未实施**
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
| D3 | 深度源使用共享深度金字塔的 **mip 1（半分辨率）**；不新建私有深度 pyramid | 与 HDRP 的 depth pyramid mip 1 一致；与 SSL/GTAO 复用同一份金字塔；避免重复 downsample |
| D4 | 逐灯启用用 Feature 目录内的 `ContactShadowLight` 组件，不修改 `UniversalAdditionalLightData` / 灯 Inspector | 把逐灯配置留在 ContactShadows 功能目录内；已有 URP C# 文件零修改 |
| D5 | 已有 URP C# 文件 **0 修改**；新增 C# 全部位于 `Runtime/RendererFeatures/ContactShadows`（及对应 Editor 目录） | 边界清晰，可整目录删除回滚 |
| D6 | URP 本体 shader 只保留一个接入点：新增 `ShaderLibrary/ContactShadows.hlsl`，并在 `Shadows.hlsl` 的 `MainLightShadow` / `AdditionalLightShadow` 末尾各加一个 `min` 块 | 内置 Lit/SimpleLit/ComplexLit/Particles/Terrain 都经过这两个函数；这是覆盖内置材质的唯一最小钩子 |
| D7 | 输出全分辨率 `R32_UInt`；低 24 bit 每灯 1 bit，高 8 bit 全局 fade；主光 `bit 0`，附加光 `bit 1..23`，最多 24 盏 | 与 HDRP 打包格式一致；URP 主光/附加光天然分开 |
| D8 | 材质侧不新增 `_CONTACT_SHADOWS` shader 变体；始终声明/采样，禁用时掩码为 0 + 绑定 1×1 中性 `R32_UInt`，`mask==0` 时在 `Load` 前返回 | 与 HDRP `_ContactShadowTexture` 设计一致；关闭时零纹理读取，不给所有 Lit 变体增加关键字 |
| D9 | 只作用于不透明表面；材质侧用 `_SURFACE_TYPE_TRANSPARENT` 编译期排除；`AfterRenderingOpaques` 后绑定中性纹理并清零掩码 | HDRP 明确“Transparents have no contact shadow information” |
| D10 | Deferred 附加光的接触阴影要求 URP Asset 开启 **Additional Light Shadows**；主光不受此限制；逐灯 `Light.shadows = None` 仍支持。若必须支持“Asset 级关闭附加光阴影”，另开 T10（需要改 `DeferredLights.cs` / `StencilDeferred.hlsl`，**不纳入本轮**） | 保持已有 URP C# 零修改；把唯一的 deferred 边界显式记录 |
| D11 | 仅支持裁剪空间 z ∈ [0,1] 的 API：D3D11 / D3D12 / Vulkan / Metal；要求 Compute + `R32_UInt` LoadStore | HDRP 的 NDC 深度比较公式依赖该约定；GLES/OpenGL 不适用 |
| D12 | **不涉及** raytracing / raygeneration / DXR / RT shadow；不生成 `.raytrace`，不创建 TLAS；默认不新增 `.rdc` | 本轮明确排除 raytracing；证据以 JSON/XML/PNG/Frame Debugger 为主 |

**验收摘要**：AC-1…AC-16（§7）。核心是：Feature 能从 Renderer Data 导入、Volume 能开启、灯组件能逐灯启用；关闭时零图像差；三模式/三灯型正确；24 灯槽位正确；已有 URP C# 文件零修改、shader 只改一个接入点；共享深度金字塔只构建一次；无 raytracing；测试与产物善后完成。

**本版复核修正（相对上一稿）**：

- Feature 位置从项目侧 `Assets/RenderFeatures/ContactShadows` 改为 **URP 包内** `Runtime/RendererFeatures/ContactShadows`，与 SSL/GTAO 一致。
- 重新允许并采用包内 internal 共享基础设施：共享深度金字塔 mip 1、`CameraHistoryStore`、`ScreenSpaceCapability`、`ScreenSpaceLightingXr`。
- 删除私有深度 downsample pass / `cameraDepthTargetHandle` 方案；深度直接来自共享金字塔 mip 1。
- 已有 URP C# 文件仍然零修改；新增 C# 全部位于 ContactShadows 目录。
- 保留上一版已补齐的：Renderer/Volume/灯组件导入验收、无关键字、24 槽位、Deferred 限制、测试/产物/文档善后、任务冲突审计。
- 明确 raytracing / raygeneration / DXR 不在本轮，也不作为 follow-up 任务。

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
| Asset 级关闭 Additional Light Shadows 时的 Deferred 附加光接触阴影 | 需要改 URP 核心 C#（`DeferredLights`/`StencilDeferred`） | 可选 T10，不纳入本轮 |
| 移动端 / GLES / OpenGL / WebGL | NDC z 约定不同；Compute/格式不保证 | 不在本轮 |
| ShaderGraph 自定义节点 / 手写 shader 的接触阴影 | 只覆盖经过 URP 阴影函数的内置 Lit 路径 | follow-up |

### 1.3 成功标准

- Feature：能在 Universal Renderer Data 中导入/移除；Volume 组件能添加/开关；灯组件能逐灯开关；player build 不丢资源。
- 功能：接触暗部方向正确；方向光/点光/聚光均生效；逐灯关闭 shadow map 时仍生效。
- 复用：Contact Shadows 与 SSL/GTAO 同时开启时，共享深度金字塔每帧只构建一次；Contact Shadows 使用 mip 1。
- 边界：已有 URP C# 文件零修改；新增 C# 全在 `Runtime/RendererFeatures/ContactShadows`；URP shader 只改 `Shadows.hlsl` + 新增 `ContactShadows.hlsl`。
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
  ├─ 读 depth pyramid mip 1（半分辨率）
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

    sampleDepth = 半分辨率深度（mip 1）
    diff = sampleDepth - sample.z

    if diff > 0 && CompareDepth(diff, threshold) && sample.z > 0:
        if tracingHalfRes:
            tracingHalfRes = false
            continue          // 同一个 t 用全分辨率再采一次；不增加 i、不前进 t
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
- 深度读取：mip 1（半分辨率），坐标 `fullPixel * 0.5 + mip1Offset`。
- HDRP 文档给出基础版 PS4 1080p 约 0.5–1.3 ms；桌面 GPU 通常远低于此。
- 主要成本：每像素 × 参与灯数 × sampleCount 次深度 Load。HDRP 用 tile 灯光列表限制每像素灯数；本 Feature v1 没有等价列表，每像素最多 24 盏（见风险 R5）。

### 2.7 与 URP 的关键差异

| 差异 | HDRP | 本 Feature |
|---|---|---|
| 世界坐标 | camera-relative | 绝对世界坐标；bias 必须朝相机，不能朝原点 |
| 灯光遍历 | 方向光数组 + FPTL tile 列表 | 每像素循环最多 24 盏接触阴影灯（无 tile 裁剪） |
| TAA 帧信息 | `_TaaFrameInfo.w` | Feature 显式上传 `frameIndex` 与 `taaEnabled` |
| Volume 质量 | `VolumeComponentWithQuality` | 直接 `sampleCount` |
| 深度纹理 | `_CameraDepthTexture` 指向打包金字塔 | 复用 URP 共享 `ScreenSpaceDepthPyramid` 的 mip 1 |
| 槽位 | 方向光/附加光统一在 light loop 中分配 | 主光固定 `bit 0`，附加光 `bit 1..23` |
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
| 现有 URP C#（修改） | **无** | 不改 `UniversalRenderer`、`ForwardLights`、`DeferredLights`、`UniversalAdditionalLightData`、Volume 框架 |
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
| 半分辨率深度 | `ScreenSpaceDepthPyramid.texture` 的 mip 1（`info.mipLevelOffsets[1]` / `mipLevelSizes[1]`） |
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
Texture2D<uint> _ContactShadowTexture;              // XR 时用 Texture2DArray<uint>
float           _ContactShadowOpacity;
uint            _MainLightContactShadowMask;
float           _AdditionalLightsContactShadowMasks[MAX_VISIBLE_LIGHTS];

uint  ContactShadowUnpackMask(uint packed) { return packed & 0x00FFFFFFu; }
float ContactShadowUnpackFade(uint packed) { return (packed >> 24) / 255.0; }

float GetContactShadowAttenuation(float3 positionWS, uint mask)
{
    if (mask == 0) return 1.0;   // 禁用/未参与的灯：在 Load 之前返回
    float2 uv = ComputeNormalizedDeviceCoordinates(positionWS, GetWorldToHClipMatrix()).xy;
    uint2 pixel = (uint2)(saturate(uv) * _ScaledScreenParams.xy);
    uint packed = ContactShadowLoad(pixel);
    bool occluded = (ContactShadowUnpackMask(packed) & mask) != 0;
    return 1.0 - occluded * ContactShadowUnpackFade(packed) * _ContactShadowOpacity;
}
```

修改 `Shadows.hlsl`：

- 在 `MainLightShadow` 末尾：
  ```
  #if !defined(_SURFACE_TYPE_TRANSPARENT)
      shadow = min(shadow, GetContactShadowAttenuation(positionWS, _MainLightContactShadowMask));
  #endif
  ```
- 在 `AdditionalLightShadow` 末尾：
  ```
  #if !defined(_SURFACE_TYPE_TRANSPARENT)
      shadow = min(shadow, GetContactShadowAttenuation(
          positionWS, asuint(_AdditionalLightsContactShadowMasks[lightIndex])));
  #endif
  ```

- 不新增 `_CONTACT_SHADOWS` 关键字、不改 `Input.hlsl`、不改任何 Lit/SimpleLit/Particles/Terrain shader。
- 关闭时所有掩码为 0，函数在 `Load` 前返回；Feature 绑定 1×1 中性 `R32_UInt` 纹理，避免未定义绑定。这与 HDRP `LightLoopDef.hlsl:414-416` 的“始终取一次、禁用时绑 1×1 纹理”一致。
- 头文件内部用 `#if` 定义 XR/非 XR 的 `ContactShadowLoad` 宏（`Texture2D<uint>` 或 `Texture2DArray<uint>`）；`mask==0` 时不执行该宏。

### 3.5 灯光索引与掩码

| 事实 | 位置 |
|---|---|
| 附加光 GPU 数组顺序 | `ForwardLights.SetupAdditionalLightConstants`（`ForwardLights.cs:553-627`）：`visibleLights` 顺序、跳过主光、最多 `maxVisibleAdditionalLights` |
| 附加光索引 | 该顺序就是 `_AdditionalLightsPosition` / `AdditionalLightShadow(lightIndex)` 的索引 |
| Deferred 附加光索引 | `_ShadowLightIndex = m_VisibleLightIndexToAdditionalLightIndex[visibleLightIndex]`，即 additional light index；**即使灯不投射阴影也会赋值**（`AdditionalLightsShadowCasterPass.cs:660-679, 899-905`） |
| 主光 | 独立 `_MainLightPosition` / `_MainLightColor` |

Feature 在 `ScreenSpaceContactShadowLights.Build(renderingData.lightData)` 中按**完全相同的顺序**扫描 `visibleLights`、跳过主光，填充：

- `_MainLightContactShadowMask`（主光 bit 0）；
- `_AdditionalLightsContactShadowMasks`（float 位掩码数组，长度 `MAX_VISIBLE_LIGHTS` = 256 desktop）；
- `_ContactShadowLightBuffer`（最多 24 条记录：position/type/direction/mask）；
- `_ContactShadowLightCount`。

每帧先把整个掩码数组清零再填充，避免上一帧/上一相机残留。**不修改 `ForwardLights.cs`。**

### 3.6 Deferred 的边界

- **主光**：`StencilDeferred.hlsl` 的 `_DEFERRED_MAIN_LIGHT` 分支始终调用 `MainLightShadow`（在 `!materialReceiveShadowsOff` 时），因此主光接触阴影在 Deferred 下**不依赖** shadow map 是否存在。
- **附加光**：`AdditionalLightShadow` 在 Deferred 的附加光分支被 `_ADDITIONAL_LIGHT_SHADOWS` 包裹。若 URP Asset **开启 Additional Light Shadows**，Feature 的接触阴影会正常叠加；逐灯 `Light.shadows = None` 也支持（索引仍被赋值）。
- 若 URP Asset **整体关闭 Additional Light Shadows**，Deferred 的附加光不会调用阴影函数，接触阴影也不会应用。这是 v1 的已知限制（D10）。
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

- 共享深度金字塔 pass 由第一个 consumer 入队；Contact Shadows 与 SSL/GTAO 同时存在时只构建一次。
- 如果 Volume 未启用、平台不支持、或没有任何 `ContactShadowLight`，Feature 在 `AddRenderPasses` 直接返回：不 Acquire 金字塔、不入队、不写全局掩码。

### 4.2 资源

| 资源 | 格式 | 尺寸 | 说明 |
|---|---|---|---|
| `ScreenSpaceDepthPyramid` | `R32_SFloat` | 共享打包图集 | 复用；取 mip 1 的半分辨率深度 |
| `_ContactShadowTexture` | `R32_UInt` | 全分辨率 × viewCount | packed：低 24 bit 每灯遮挡，高 8 bit 全局 fade |
| `_ContactShadowNeutralTexture` | `R32_UInt` | 1×1 | 值为 0；禁用时绑定 |
| `_ContactShadowLightBuffer` | `ComputeBuffer`（24 条） | 24 × 32 B | 每灯 position/type/direction/mask |
| `_ContactShadowParams*` | CBUFFER | — | length / distanceScaleFactor / fade / bias / thickness / sampleCount / frameIndex / taaEnabled |

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
  Runtime/ContactShadowRuntimeToggleTests.cs
```

### 4.4 修改文件（URP 本体）

| 文件 | 改动 | 责任 |
|---|---|---|
| `Packages/.../ShaderLibrary/ContactShadows.hlsl` | 新增：全局声明、解包、采样、`mask==0` 早退 | W2 |
| `Packages/.../ShaderLibrary/Shadows.hlsl` | include 新头文件；`MainLightShadow` / `AdditionalLightShadow` 末尾各加一个 `min` 块 | W2 |

**已有 URP C# 文件不修改**：

- `Runtime/UniversalRenderer.cs`、`ForwardLights.cs`、`DeferredLights.cs`、`UniversalAdditionalLightData.cs`、`UniversalRenderPipeline.cs`。
- `ShaderLibrary/Input.hlsl`、任何 Lit/SimpleLit/ComplexLit/Particles/Terrain shader。
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

- Feature 在 `AddRenderPasses` 和 `Execute` 都从 `VolumeManager.instance.stack.GetComponent<ContactShadowsVolume>()` 读取；Volume 是效果的唯一总开关。
- 无 Volume / `enabled=false`：不入队、不分配、不写全局。
- 不做 HDRP Quality 分级；Inspector 加一句说明。

### 4.6 逐灯启用与槽位

`ContactShadowLight`（Feature 目录内组件）：

```
[DisallowMultipleComponent]
public sealed class ContactShadowLight : MonoBehaviour
{
    public bool castContactShadows = true;
}
```

- 只对 `LightType.Directional / Point / Spot` 生效；Rectangle 忽略。
- 组件不存在或 `castContactShadows=false` 或 `Behaviour.enabled=false`：该灯不参与。
- 这是 Feature 目录内的 opt-in，不修改 URP 的 `UniversalAdditionalLightData` 或灯 Inspector。
- 用户流程：在灯 GameObject 上 Add Component → Contact Shadow Light。

槽位分配（`ScreenSpaceContactShadowLights`）：

```
slot = 0
mainMask = 0
additionalMasks[0..255] = 0
records.Clear()

if mainLightIndex >= 0 && IsMarked(mainLight):
    mainMask = 1u << slot++
    records.Add(main, Directional, direction=-forward)

for i in visibleLights:
    if i == mainLightIndex: continue
    if !IsMarked(light): continue
    if slot >= 24: continue
    additionalMasks[additionalLightIndex] = 1u << slot++
    records.Add(light,
        type = Directional / Point / Spot,
        direction = (Directional ? -forward : 0),
        position  = (Directional ? 0 : lightPos))
```

`additionalLightIndex` 必须与 `ForwardLights.SetupAdditionalLightConstants` 的 `lightIter` 完全一致：按 `visibleLights` 顺序、跳过主光、最多 `UniversalRenderPipeline.maxVisibleAdditionalLights`。这保证 Forward、Forward+、Deferred 三条路径取到同一槽位。

每帧先把 `_AdditionalLightsContactShadowMasks` 的整个数组清零，再按同一份 `visibleLights` 快照填充；否则上一帧/上一相机残留的掩码会把接触阴影错误地应用到已经关闭的灯。

### 4.7 Compute 适配

`ContactShadows.compute` 从 HDRP 移植，做以下替换：

| HDRP | 本 Feature |
|---|---|
| `PositionInputs` | 用 `uv + deviceDepth + InvViewProj` 重建 `positionWS`/`linearDepth` |
| `_CameraDepthTexture`（HDRP 金字塔） | 共享 `ScreenSpaceDepthPyramid.texture` 的 mip 1；绑定 `mipLevelOffsets[1]` / `mipLevelSizes[1]` |
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
    depth = depthPyramid.Load(mip1Offset + pixel/2)
    if background or outside fade range: store 0; return

    posWS       = ReconstructWorldPos(pixel, depth)
    linearDepth = -viewZ(posWS)

    contactMask = 0
    globalFade  = 0
    for each record in [0, lightCount):
        if record.type == Directional:
            rayDir = record.directionWS
        else:
            rayDir = normalize(record.positionWS - posWS)

        rayLength = length * max(0.5, linearDepth * distanceScaleFactor)
        occluded, fade = RayCast(posWS, rayDir, rayLength, pixel)
        contactMask |= record.mask * occluded
        globalFade = max(globalFade, fade)

    store PackContactShadowData(globalFade, contactMask)
```

`RayCast` 内部保留 HDRP 的全部细节：bias、投影、斜率无关阈值、half-res→full-res 两段确认、dither、屏外 vignette、距离 fade。

- XR：复用 `SSL_XR_ARRAY` 关键字与 `ScreenSpaceLightingXr` 的 view 矩阵/资源 dimension；compute 按 view 调度。
- Compute 只读共享金字塔；不写它。

### 4.8 着色器采样与应用

材质侧只通过 `ShaderLibrary/ContactShadows.hlsl` 的函数，代码见 §3.4。要点：

- `mask == 0` 时在 `Load` 前返回 1；关闭时所有掩码为 0，因此没有真实纹理读取。
- 禁用时 Feature 绑定 1×1 中性 `R32_UInt`（值为 0），避免未定义绑定。
- 不新增 shader 关键字；不给 Lit/SimpleLit/Particles/Terrain 增加变体。
- 透明物由 `_SURFACE_TYPE_TRANSPARENT` 编译期排除。
- Deferred 主光通过 `MainLightShadow` 自动覆盖；Deferred 附加光依赖 `_ADDITIONAL_LIGHT_SHADOWS`（见 §3.6）。
- 内置 Lit/SimpleLit/ComplexLit/Particles/Terrain 通过 `UniversalFragmentPBR` / `UniversalFragmentBlinnPhong` 覆盖；直接调用 `GetMainLight(shadowCoord)` 的自定义 shader 不覆盖；BakedLit 无实时直射光，不覆盖。

### 4.9 清理与共存

- `ScreenSpaceContactShadowsCleanupPass` 在 `AfterRenderingOpaques`：
  - 绑定 1×1 中性纹理；
  - `_MainLightContactShadowMask = 0`；
  - `_AdditionalLightsContactShadowMasks` 全部清零；
  - `_ContactShadowOpacity = 0`。
- 与 URP `ScreenSpaceShadows` feature 共存：先算屏幕空间 shadow map，再与接触阴影取 `min`。
- 与 GTAO/SSGI/SSR：互不影响；接触阴影只改直射光遮挡。
- 透明物：编译期排除 + opaques 后中性绑定。
- `ScreenSpaceContactShadowsDebugPass` 持有 packed RT 的句柄，不依赖 cleanup 之后的全局绑定；可以在 `BeforeRenderingPostProcessing` 继续显示 mask/fade。
- 关闭 Volume / 移除灯组件 / 移除 Feature：图像逐像素不变；Feature `Dispose` 释放 RT/ComputeBuffer/Material，并 `UnregisterOwner`。

### 4.10 与 HDRP 的已知差异

| 项 | HDRP | 本 Feature v1 | 影响 |
|---|---|---|---|
| 每像素灯数 | FPTL tile 列表 | 最多 24 盏接触阴影灯全遍历 | 性能；正确性不变 |
| 深度金字塔 | 共享 depth pyramid mip 1 | 复用 URP 共享金字塔 mip 1 | 基本一致 |
| 共享金字塔输入 | 深度专用 | 现有共享 pass 同时请求 Motion（SSL 需要） | 仅 Contact Shadows 时多一次 motion 准备；follow-up 可拆分输入 |
| TAA | 内置 `_TaaFrameInfo` | 显式上传 frameIndex/taaEnabled | 视觉一致 |
| Quality 分级 | Lighting Quality Settings | 直接 sampleCount | UI 不同 |
| 世界坐标 | camera-relative | 绝对坐标 + 朝相机 bias | 视觉一致；大世界精度略低 |
| 槽位 | 方向光/附加光统一分配 | 主光固定 bit 0 | 仅内部索引不同 |
| 灯不投射 shadow map | 支持 | Forward 支持；Deferred 主光支持；Deferred 附加光需 Asset 级 Additional Light Shadows | 已知限制（D10） |
| 透明物 | 不支持 | 不支持 | 一致 |
| Area Light | 部分支持 | 不支持 | URP 本身不支持 |
| 自定义 shader / BakedLit | 所有 lit pass 经过 LightEvaluation | 仅内置 URP Lit 路径 | 已知限制 |
| Raytracing | 有 Ray Traced Contact Shadows | **完全没有** | 本轮排除 |

---

## 5. 模块责任与文件清单

### 5.1 工作流（验收责任，不是人员分工）

| 工作流 | 负责范围 | 主要任务 |
|---|---|---|
| W1 Feature Runtime | Feature/Volume/灯组件/History/Pass/Capability | T1、T2、T4、T6 |
| W2 GPU/Shader | compute、URP shader 钩子 | T2、T3、T5 |
| W3 测试/证据 | 测试资产、player、矩阵、性能 | T7、T8 |
| W4 文档/收尾 | 契约、报告、ACCEPTANCE、清理/归档 | T0、T9 |

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
| 文档 | `PLAN/TODO/10-ContactShadow/**`、`PLAN/ACCEPTANCE.md` | 修改 |
| 默认渲染资产 | `Assets/Settings/URP-*.asset` | **不修改** |
| 现有 URP C# | `Packages/.../Runtime/**` | **不修改** |
| 现有 SSL/GTAO/DDGI | `Packages/.../Runtime/RendererFeatures/**` | **不修改** |

### 5.3 程序集与引用

- Runtime C# 直接编译进现有 `Unity.RenderPipelines.Universal.Runtime`；**不新增 runtime asmdef**。
- Editor C# 直接编译进现有 `Unity.RenderPipelines.Universal.Editor`；**不新增 editor asmdef**。
- 包内测试沿用 `Tests/Editor` 的现有测试 asmdef 约定；如果该目录需要独立 asmdef，只在 `Tests/Editor/RendererFeatures/ContactShadows` 下新增测试 asmdef。
- 项目侧 `Assets/Tests/ContactShadow` 沿用现有 SSL 测试的 asmdef/Assembly-CSharp 约定；导入/序列化测试需要引用 URP Runtime。
- 不使用 `InternalsVisibleTo`；不修改 URP asmdef。

### 5.4 序列化与导入要求

- Feature 是 `ScriptableObject`，被序列化进 Universal Renderer Data；其 `ComputeShader` / `Shader` 引用必须序列化，player build 才能包含。
- Feature Inspector 提供 “Assign Defaults”，在包内 `Shaders/Utils/ContactShadows` 按文件名查找；找不到时显示警告。
- 共享金字塔 compute 引用：Feature 资源中同样序列化 `depthPyramidCompute`（与 SSL 相同的包内 `DepthPyramid.compute`），供 `ScreenSpaceDepthPyramidSystem.Acquire` 使用。
- 中性 1×1 `R32_UInt` 在运行时创建并持有；不使用 `Texture2D.blackTexture`（类型不匹配）。

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

### 6.1 依赖图

```
T0 契约/基线
 ├─> T1 Feature 骨架 + 导入/Volume 发现
 │    ├─> T2 共享深度金字塔 + 每相机 packed RT
 │    │    └─> T3 Compute raymarch
 │    └─> T4 灯组件 + 槽位/掩码
 │         └─> T5 URP shader 钩子 + 三模式接入
 │              └─> T6 Cleanup/UI/Debug/Player 序列化
 │                   └─> T7 验证资产 + 矩阵 + Player
 │                        └─> T8 验收/证据/性能/回归
 │                             └─> T9 文档/清理/归档
```

- T2 与 T4 可并行（不同文件）。
- T3 依赖 T2（共享金字塔/History 就绪）。
- T5 依赖 T3/T4（需要 packed 纹理与掩码）。
- T6 依赖 T5（cleanup/UI 需要最终状态）。
- T8 必须等 T7 全部矩阵完成。

### 6.2 任务定义

#### T0 契约与基线冻结

- **目标**：冻结 D1–D12、参数默认值、AC 阈值、改动预算。
- **产出**：`validation/P0/Contract.md`、`validation/P0/Baseline.json`。
- **内容**：
  - HEAD `cedd1bb`、Unity/URP 版本、测试机 GPU、`graphicsDeviceType`。
  - 既有 EditMode/PlayMode/T12 基线（以实际运行结果为准）。
  - `.rdc` 数量/字节/预算。
  - 明确“已有 URP C# 文件零修改、shader 只改一个接入点”的验收口径。
- **验收**：文档入库；无代码改动；所有者确认 D1–D12。
- **门禁**：若所有者不接受 shader 钩子，先改契约，不进入 T5。

#### T1 Feature 骨架 + Renderer/Volume 导入

- **目标**：建立包内 Feature/Volume/空 Pass；证明“能从 Renderer Data 导入、能在 Volume Profile 添加”。
- **改动**：
  - `Runtime/RendererFeatures/ContactShadows`：Feature/Volume/Settings/Resources/ShaderIDs/Capability 骨架；
  - `Editor/RendererFeatures/ContactShadows`：Feature Inspector + Assign Defaults；
  - `Assets/Tests/ContactShadow`：测试 Renderer/Pipeline/Volume Profile。
- **验收**：
  - Feature 出现在 **Add Renderer Feature** 菜单；
  - 添加/移除/domain reload 后仍正常；
  - Volume Profile 出现 **Shadowing/Contact Shadows**；
  - `AddRenderPasses` 能读到 Volume；关闭时 pass 不入队、不分配 RT；
  - 无 compute 时不抛异常，只 warning；
  - 默认 `Assets/Settings/URP-*.asset` 未被修改。
- **证据**：Renderer/Pipeline/Volume 资产的序列化内容；Feature Inspector 截图；`ContactShadowFeatureImportTests`、`ContactShadowVolumeTests`。
- **回滚**：删除新增 Feature 目录/测试资产。

#### T2 共享深度金字塔 + 每相机 packed RT

- **目标**：Feature 作为 `ScreenSpaceDepthPyramidSystem` 的 owner；复用 mip 1；每相机/eye 的 packed RT 生命周期正确。
- **改动**：
  - `ScreenSpaceContactShadowsHistory`；
  - `ScreenSpaceContactShadowsResources.depthPyramidCompute`；
  - `RegisterOwner/UnregisterOwner`；
  - `Acquire` 与 `CameraHistoryStore` 接入；
  - `ScreenSpaceContactShadowsCapability`（复用 `ScreenSpaceCapability` + `R32_UInt` 检查）。
- **验收**：
  - Contact + SSL 同时存在时，Depth Pyramid pass 每帧只入队一次；
  - Contact 使用 mip 1（offset/size 正确）；
  - 多相机/尺寸变化/XR layout 变化不泄漏；
  - 不支持 API/格式时 no-op + 一次 warning；
  - 深度 pyramid 不可用时 Feature 不入队/不写全局。
- **证据**：EditMode `ContactShadowHistoryTests`；Frame Debugger 203 pass；金字塔 debug 图。
- **依赖**：T1。

#### T3 Compute raymarch

- **目标**：移植 HDRP 算法；先用方向光单灯打通。
- **改动**：
  - `ContactShadows.compute::KContactShadow`；
  - `ScreenSpaceContactShadowsPass`（+8）；
  - 灯光 buffer/params 绑定；
  - 方向光 + 点/聚光分支。
- **验收**：
  - 接触暗部方向正确（背光侧）；
  - bias 默认无 acne；
  - sampleCount 4/10/64 有可测差异；
  - 半分辨率命中→全分辨率确认；
  - 屏外 vignette 与距离 fade 生效。
- **证据**：`validation/P3/` 三模式 PNG + 指标 JSON。
- **依赖**：T2。

#### T4 灯组件 + 槽位/掩码

- **目标**：逐灯启用；主光 bit 0、附加光 bit 1..23；24 上限；与 ForwardLights 顺序一致。
- **改动**：
  - `ContactShadowLight`；
  - `ScreenSpaceContactShadowLights`；
  - 全局掩码/主光掩码上传。
- **验收**：
  - 0/1/23/24/25 边界；
  - 主光 + 附加光、点/聚光/方向光；
  - 关闭单灯只影响该灯；
  - 上一帧/上一相机掩码不残留；
  - 组件在 prefab/实例上生效。
- **证据**：`ContactShadowSlotMappingTests`；slot debug 图。
- **依赖**：T1；可与 T2 并行。

#### T5 URP shader 钩子 + 三模式接入

- **目标**：唯一核心 shader 改动；Forward/Forward+/Deferred 主光/附加光应用；透明物排除。
- **改动**：
  - `ShaderLibrary/ContactShadows.hlsl`（新增）；
  - `ShaderLibrary/Shadows.hlsl`（include + 两个 `min`）；
  - Feature 绑定 packed 纹理/掩码/opacity。
- **验收**：
  - 三模式 A/B 均可见接触暗部；
  - 透明物区域 A/B 像素差为 0；
  - 关闭时逐像素一致；
  - 不新增 shader 关键字/不给 Lit 增加变体；
  - `git diff` 中已有 URP C# 文件修改为 0，shader 修改仅两个文件。
- **证据**：`validation/P5/` 三模式图 + `git diff --stat` + shader 编译检查。
- **依赖**：T3/T4。

#### T6 Cleanup、UI、Debug、Player 序列化

- **目标**：清理全局状态；完整 Inspector；debug；player 不丢资源。
- **改动**：
  - `ScreenSpaceContactShadowsCleanupPass`（AfterRenderingOpaques）；
  - Feature Inspector / debug settings；
  - `ScreenSpaceContactShadowsDebugPass`；
  - 资源默认值/校验。
- **验收**：
  - opaques 后中性纹理/零掩码/opacity=0；
  - debug 模式 Mask/Fade/Slot/Raw 可用；
  - 多相机连续渲染不串状态；
  - player build 中 compute/shader 存在，无 missing 资源。
- **证据**：`ContactShadowCleanupTests`；debug PNG；player 构建日志。
- **依赖**：T5。

#### T7 验证资产与矩阵

- **目标**：建立可复现的测试场景、Pipeline/Renderer/Volume、player 验证。
- **改动**：`Assets/Tests/ContactShadow/**`。
- **矩阵**：
  - 模式：Forward / Forward+ / Deferred。
  - 灯光：Directional / Point / Spot；逐灯 shadow on/off。
  - 参数：opacity 0/0.5/1；min/max/fade；sampleCount 4/10/64；thickness/rayBias 边界。
  - 灯数：1 / 4 / 24 / 25。
  - MSAA：off/on；动态分辨率（若支持）。
  - 多相机/Scene View；XR mock（次级）。
  - 透明物、BakedLit、自定义 shader（已知限制验证）。
- **验收**：矩阵全部有 JSON/PNG；无 NaN/崩溃；默认资产未改。
- **依赖**：T5/T6。

#### T8 验收与证据

- **目标**：按 AC-1…AC-16 生成证据，映射到 git diff 与 HEAD。
- **产出**：`report.md`、`validation/`、`PLAN/ACCEPTANCE.md` 新章节。
- **内容**：
  - A/B 图、差值图、区域亮度指标。
  - Feature 导入/Volume/灯组件证据。
  - 槽位单测、shader 编译、Frame Debugger pass 顺序。
  - 共享金字塔只构建一次的计数证据。
  - 性能表：模式 × 灯数 × sampleCount。
  - 既有测试回归；shader 编译计数基线（如有变化）。
  - `.rdc` 预算说明。
- **验收**：每个 AC 都有 “结论 + 证据路径 + 复现命令”。

#### T9 文档、清理、归档

- **目标**：文档一致、产物清理、移动到 Done。
- **检查**：
  - HANDOFF/report/ACCEPTANCE 无矛盾；更新状态为完成。
  - 测试资产保留，临时资产删除；默认资产未改。
  - 日志本地-only；JSON/XML/PNG/txt 入库；无 `Library/`、`Temp/`、`Builds/`、player 二进制。
  - `.rdc` 不超预算。
  - 归档到 `PLAN/Done/10-ContactShadow`；修正相对链接；`git status` 干净。
- **验收**：归档提交。

### 6.3 任务冲突/遗漏审计

| 检查项 | 结论 |
|---|---|
| 两个任务改同一个文件？ | T2→T3 顺序：T2 只做 History/资源，T3 只做 compute/pass dispatch；T1/T6 都改 Feature/Inspector → T6 在 T5 之后，T1 只放骨架 |
| 现有 URP 文件与新增文件混在同一任务？ | 现有 shader 只在 T5；现有 C# 不在任何任务中修改；其余为 ContactShadows 目录新增文件 |
| 是否遗漏 Volume 开启路径？ | T1 的空框架就验证 Volume 菜单/读取；T7 验证开启/关闭画面 |
| 是否遗漏 Renderer Feature 导入路径？ | T1 验证 Add Renderer Feature + 序列化；T7 验证 Pipeline Asset→Renderer List |
| 是否遗漏逐灯开关？ | T4 引入 `ContactShadowLight`；T7 验证单灯/多灯/25 灯 |
| 是否遗漏共享金字塔只构建一次？ | T2 验证 Contact + SSL 同时存在时的 pass 计数；T8 证据 |
| 是否遗漏 player 资源包含？ | T6 验证 compute/shader 序列化；T8 player 构建检查 |
| 是否遗漏清理/中性状态？ | T6 CleanupPass + T8 AC-9；T9 检查 |
| 是否遗漏既有 shader 编译回归？ | T8 显式运行既有 shader 检查；若计数变化，记录新基线，不静默改阈值 |
| 是否遗漏 raytracing 排除？ | D12 + AC-13 + T8 的 grep/code review 证据 |
| 是否遗漏 deferred 附加光限制？ | D10 + §3.6 + KnownDifferences + AC-5 的范围说明 |
| 是否遗漏 XR/MSAA/动态分辨率？ | T7 矩阵；XR 为次级，失败则在 KnownDifferences 标注 |
| 是否遗漏文档矛盾？ | T9 一致性检查；AC-16 |

---

## 7. 验收标准（AC）

| ID | 标准 | 证据 |
|---|---|---|
| AC-1 | Feature 能从 Universal Renderer Data 的 **Add Renderer Feature** 导入/移除；序列化后 domain reload 不丢；Pipeline Asset 的 Renderer List 指向该 Renderer | Renderer/Pipeline 资产序列化内容 + Inspector 截图 |
| AC-2 | Volume Profile 出现 **Shadowing/Contact Shadows**；`enabled=false` 无效果；`enabled=true` 且有灯组件时生效；无 Volume 时无效果 | Volume 资产 + A/B PNG |
| AC-3 | 灯上的 `Contact Shadow Light` 能逐灯开关；主光 + 附加光；点/聚光/方向光 | 单灯/多灯 A/B + EditMode 测试 |
| AC-4 | 方向/遮挡正确：接触暗部在背光侧；默认 bias 无自阴影痤疮 | 三视角 PNG + 区域亮度 |
| AC-5 | 逐灯 `Light.shadows=None` 时仍生效：Forward 主/附加、Deferred 主光；Deferred 附加光在 Asset 开启 Additional Light Shadows 时生效；Asset 级关闭的记录为已知限制 | 三模式 A/B + KnownDifferences |
| AC-6 | 在 shadow map 未遮挡的接触区域：`opacity=0` 无效果；0.5 约一半；1 最大 | 区域亮度表 |
| AC-7 | 距离 fade：`<minDistance` 为 0；`>maxDistance+fadeDistance` 为 0；中间单调 | 相机距离序列图 |
| AC-8 | 屏外 fade：边缘无硬切；vignette 单调衰减 | 边缘带差值图 |
| AC-9 | 24 灯槽位正确；第 25 盏无接触阴影；关闭单灯只影响该灯；掩码无残留 | slot debug + 单灯 A/B + EditMode 测试 |
| AC-10 | 透明物区域 A/B 差值为 0；BakedLit/自定义 shader 不覆盖（已知限制） | 透明场景差值 + KnownDifferences |
| AC-11 | 在 Asset 开启 Additional Light Shadows 的配置下，三模式 debug mask 一致；Forward/Forward+/Deferred 的接触区域一致（Asset 级关闭的 Deferred 附加光按 AC-5 已知限制处理） | mask PNG 对比 |
| AC-12 | 已有 URP C# 文件修改 = 0；新增 C# 全在 `Runtime/RendererFeatures/ContactShadows`；shader 修改仅 `Shadows.hlsl` + 新增 `ContactShadows.hlsl` | `git diff --stat` + `git status` |
| AC-13 | 无 raytracing/raygeneration/DXR/TLAS 代码或资产 | `grep`/code review 证据 |
| AC-14 | Contact + SSL/GTAO 同时开启时，共享 Depth Pyramid pass 每帧只出现一次；Contact 使用 mip 1 | Frame Debugger + 计数器断言 |
| AC-15 | 既有 EditMode/PlayMode/T12 全绿；新增测试全绿；shader 编译 0 error；性能记录 | XML/日志/性能 JSON |
| AC-16 | 测试改动、证据留存、产物清理、文档一致性完成；无日志/`.rdc`/Build 超预算 | `git status` + validation 清单 + ACCEPTANCE |

> 性能绝对阈值由 T0 在测试机上冻结（建议 1920×1080、4 灯、10 samples 每模式 ≤1.0 ms；超出则记录为 follow-up，不阻塞正确性验收）。

---

## 8. 测试改动、证据与善后

### 8.1 既有测试怎么改

1. **不改既有断言**：现有 EditMode/PlayMode/T12 的期望值保持；只追加新 suite。
2. **新增 round-local wrapper**：参考 06/09 的 wrapper，新增 `validation/Invoke-ContactShadowTests.ps1`，不硬编码旧轮次目录。
3. **shader 编译回归**：新增的 `ContactShadows.hlsl` + `Shadows.hlsl` include 可能影响既有 shader 编译检查的计数。T8 必须：
   - 以 HEAD 的 06 轮 T12 矩阵与 shader 编译检查为基线（Forward 34/34、Forward+ 34/34、Deferred 35/35，具体以实际运行结果为准）；
   - 运行既有 shader 检查；
   - 若计数/消息变化，记录新基线、原因和 diff，不静默放宽阈值；
   - 在 `report.md` 说明。
4. **测试资产**：新增 `Assets/Tests/ContactShadow` 的 Pipeline/Renderer/Volume/Scene；默认 `Assets/Settings/URP-*.asset` 不改。
5. **测试清理**：测试中临时创建的资产（如临时 Renderer）在 `TearDown` 删除；提交的测试资产保留。

### 8.2 新增测试清单

| 测试 | 类型 | 覆盖 |
|---|---|---|
| `ContactShadowVolumeTests` | EditMode（包内） | 默认值/范围/菜单路径/`enabled` |
| `ContactShadowCapabilityTests` | EditMode（包内） | API/格式 gate；不支持时 no-op |
| `ContactShadowHistoryTests` | EditMode（包内） | 多相机/尺寸/XR/Dispose；packed RT 生命周期 |
| `ContactShadowSlotMappingTests` | EditMode（包内） | 0/1/23/24/25；与 ForwardLights 顺序一致 |
| `ContactShadowShaderCompileTests` | EditMode（包内） | 核心头文件 + Feature shader/compute 0 error |
| `ContactShadowCleanupTests` | EditMode（包内） | opaques 后中性/零掩码/opacity=0 |
| `ContactShadowFeatureImportTests` | EditMode（项目侧） | Renderer 资产含 Feature；Pipeline 引用 Renderer；Volume Profile 含组件；reload |
| `ContactShadowLightTests` | EditMode（包内） | 组件发现/主光/附加光/prefab/关闭 |
| `ContactShadowRuntimeToggleTests` | PlayMode（项目侧） | Volume/灯组件/Feature 运行时开关；多相机 |

### 8.3 Player 验证

- D3D11 与 D3D12 各构建一个 player。
- 使用专用的 Forward/Forward+/Deferred 三套 Pipeline + Renderer 资产（§4.3）与 `ContactShadowVolumeProfile.asset`。
- 验证：
  - Feature/compute/shader 在 build 中存在；
  - Volume/灯组件生效；
  - A/B PNG；
  - 无 missing resource/异常；
  - 性能 ms。

### 8.4 证据留存

| 类型 | 留存 | 说明 |
|---|---|---|
| Contract/Baseline | `validation/P0/*.json/*.md` | 入库 |
| 测试结果 | `*.xml` | 入库 |
| 汇总 | `*.json/*.txt` | 入库 |
| 图像 | 小尺寸 PNG（A/B/差值/debug） | 入库；控制数量与体积 |
| 日志 | `*.log` | **本地-only**，`.gitignore` 已排除；不入库 |
| `.rdc` | 默认 0 | 预算 1000.1/1024 MiB；若必须，1 个 ≤20 MiB 1280×720，记录并获批 |
| Player/Builds | 不入库 | `Builds/`、二进制、`Library/`、`Temp/` 清理 |

### 8.5 归档与清理

1. 删除临时场景/脚本/材质；保留 `Assets/Tests/ContactShadow` 专用验证资产。
2. 确认默认 Pipeline/Renderer 资产未被修改；如手动测试改过，恢复。
3. 把 `PLAN/TODO/10-ContactShadow` 移到 `PLAN/Done/10-ContactShadow`。
4. 更新 `PLAN/ACCEPTANCE.md` 新增 §16，写总结/证据/已知限制。
5. 修正相对链接；删除空目录；检查 `git status` 干净。
6. 确保 `.meta` 随新资产提交，删除资产时对应 `.meta` 一并删除。
7. 最终提交只包含计划/代码/测试/证据，不包含日志/构建/大 `.rdc`。

### 8.6 文档一致性检查

- HANDOFF（本文件）、report、ACCEPTANCE、KnownDifferences 四处的：
  - 参数默认值；
  - 24 灯上限；
  - 三模式覆盖；
  - Deferred 附加光限制；
  - 改动预算（已有 C# 0 修改、shader 两个文件）；
  - `.rdc` 预算；
  - 测试/产物留存。
- 任何一处改动必须同步其余三处；T9 逐项对照。

---

## 9. 风险与缓解

| ID | 风险 | 缓解 |
|---|---|---|
| R1 | URP 绝对世界坐标导致 bias 方向错误、自阴影 | 公式改为 `positionWS - (positionWS - cameraPosWS) * bias`；远离原点场景专项测试 |
| R2 | 共享深度金字塔 pass 被多个 Feature 重复入队 | 复用 `ScreenSpaceDepthPyramidSystem` 的 `ShouldBuild`；T2/AC-14 用 Frame Debugger/计数器验证只出现一次 |
| R3 | Feature 从 Renderer Data 导入失败/player 丢资源 | T1 先做导入门禁；序列化 compute/shader；T6/T8 player 构建验证 |
| R4 | Volume 未启用/无灯组件时仍有开销 | `AddRenderPasses` 三重门控（Volume/平台/灯）；T1/T2 验证 pass 不入队 |
| R5 | 24 盏灯全遍历导致 GPU 成本高 | v1 记录性能；默认测试 4 盏；follow-up 用 Forward+ tile |
| R6 | 共享金字塔当前同时请求 Motion（SSL 需要） | 已知差异；仅 Contact Shadows 时接受一次 motion 准备；follow-up 可拆分输入 |
| R7 | Deferred 附加光依赖 Asset 级 Additional Light Shadows | D10 明确记录；AC-5 限定范围；可选 T10 另行评估 |
| R8 | `R32_UInt` LoadStore 不支持 | `ScreenSpaceCapability` + Contact 自己的格式检查；不支持时 no-op + 一次 warning |
| R9 | GLES/OpenGL NDC z 约定不同 | 运行时 API gate；不支持平台不启用 |
| R10 | 掩码/纹理泄漏到透明 pass 或下一相机 | CleanupPass 中性绑定 + 清零；EditMode 清理测试 |
| R11 | 新增 shader 头文件影响既有 shader 编译计数 | T8 运行既有检查；变化时记录新基线并说明 |
| R12 | 证据/`.rdc`/日志超预算 | 默认 0 `.rdc`；日志本地-only；PNG 控制尺寸/数量 |
| R13 | 测试改动污染默认资产 | 专用测试 Pipeline/Renderer；T9 检查 `Assets/Settings` 未改 |
| R14 | 文档四处不一致 | T9 对照表；AC-16 |
| R15 | 共享金字塔在编辑器同一帧复用旧深度 | 现有 `ShouldBuild` 按 frame + VP 判断；物体移动/相机变化专项测试 |
| R16 | 内部 API 耦合（Feature 与 URP 包同版本） | Feature 在包内，跟随包版本；升级 URP 时合并检查；不承诺跨版本可移植 |

---

## 10. 可选 T10（不纳入本轮）

**仅在所有者要求“Asset 级关闭 Additional Light Shadows 时 Deferred 附加光也支持接触阴影”时启动。**

- 需要修改：
  - `Runtime/DeferredLights.cs`：每次附加光 draw 设置 `_ContactShadowLightIndex`（additional light index），不依赖 shadow pass；
  - `Shaders/Utils/StencilDeferred.hlsl`：在 `unityLight.shadowAttenuation` 之后增加不依赖 `_ADDITIONAL_LIGHT_SHADOWS` 的接触阴影取用。
- 代价：已有 URP C# 与 shader 修改增加，需要额外的 deferred 回归/证据。
- 本轮不启动、不列入 AC、不影响 D1–D12。

---

## 11. 立即下一步

1. **T0**：写 `validation/P0/Contract.md`，冻结基线、参数、AC 阈值与“已有 URP C# 零修改”口径。
2. **T1**：搭包内 Feature 骨架，先在 Inspector 中验证 Add Renderer Feature 与 Volume Profile 两条导入链路；不接 compute。
3. **T2**：接 `ScreenSpaceDepthPyramidSystem` + 每相机 packed RT；Frame Debugger 确认 203 只出现一次。
4. **T3**：移植 compute，先用方向光单灯打通。
5. **T4/T5**：灯组件/槽位 + 核心 shader 钩子，再做点/聚光与三模式。
6. **T6-T9**：清理/UI/debug、验证矩阵、证据、文档与归档。

> 完成 T3 后即可得到第一张可对比的接触阴影图；在 T5 之前不要改 `Shadows.hlsl`，避免把算法问题与接入问题混在一起定位。