# ScreenSpaceRefraction 对齐 HDRP 与缺陷修复交接

> 更新：2026-11-17
> 状态：P0 里程碑交付完成。P0-1/P0-2/P0-3 实现并完成 T11 三模式验证；P1-1/P1-2 实现并完成静态/解析验证；P1-3 实现并完成双 probe/相机移动/GC 验收，并新增独立 player + RenderDoc `.rdc` 复核（见第 18 节；GPU p95 与物体边界序列帧仍待补），P2-2..P2-6 未开始，P2-1 复核关闭（与 HDRP 等价）。2026-11-16 复核修复 specular 合成、金字塔 mip 上限与吸收公式，并修正代理语义文档（见第 13、15 节）；2026-11-17 完成 P1-3 per-object 代理表（见第 16 节）与 player `.rdc` 验证（见第 18 节）
> 上游语义基准：HDRP 14.0.12（`E:\workspace\GitHub\HDRPSample01\Library\PackageCache\com.unity.render-pipelines.high-definition@14.0.12`）
> 相关文档：[T14 已知差异](../../Done/01-ScreenSpaceLighting/validation/T14/T14KnownDifferences.md)、[ScreenSpaceLighting 交接](../../Done/01-ScreenSpaceLighting/HANDOFF.md)、[T11 证据](../../Done/01-ScreenSpaceLighting/validation/T11/T11Report.json)

## 0. 本轮做了什么

本轮只做**静态源码对比**，没有修改任何运行时代码、Shader 或测试资产：

- 逐行对比 URP 14.0.12 的 `ScreenSpaceRefraction.hlsl`、`RefractionPass`、颜色金字塔与材质入口，以及 HDRP 14.0.12 的 `Lit.hlsl`、`ScreenSpaceTracing.hlsl`、`LightLoop.hlsl`、`HDRenderPipeline.RenderGraph.cs`、`MipGenerator.cs`。
- 确认已经对齐的部分：Box/Sphere/Thin 几何模型、`EdgeOfScreenFade`、深度拒绝规则、`1 - mip * 0.08` 偏移、roughness^1.3 的 mip 选择、9-tap Gaussian 金字塔、8x8 mip 截断、`screenFadeDistance=0.1` 默认值。
- 确认需要处理的分歧，并按影响面归入 P0/P1/P2：
  - P0：透射 miss 无 probe fallback；opacity 未作为 `transmittanceMask`；代理只认 camera 最近的 box-projected probe。
  - P1：`_Ior` 未进入 F0；Thin 模型的吸收距离未固定为 0.005；probe 选择不是 per-object/per-pixel。
  - P2：屏幕外采样语义、pre-refraction 队列、厚度/透射色贴图、预积分 FGD、动态分辨率/材质覆盖的文档与回归。
- 发现的文档不一致：[ScreenSpaceLighting HANDOFF](../../Done/01-ScreenSpaceLighting/HANDOFF.md) 第 234 行声称“opacity 当 transmittance mask”，但当前代码只把 alpha 按折射权重推向 1，没有 `1 - alpha` 调制。实施 P0-2 时必须同步修文档或补测试证明这是有意行为。
- 本轮唯一交付物：本文件。工作区其余内容未变。

## 1. 目标、边界与不变量

### 目标

让 URP 的屏幕空间折射在**语义**上对齐 HDRP 14 的 Lit 折射，而不是只复用几何公式；优先修正在常见玻璃材质上肉眼可见的差异，其次补齐 HDRP 已有的材质与排序能力。

### 不在本轮范围

- 不改 SSGI/SSR/Transparent SSR 的算法与调度。
- 不重构 URP 的 LightLoop / probe atlas；折射 fallback 只消费现有 probe 数据。
- 不做 HDRP 的 Distortion、Rough Distortion、Path Tracing 折射、Recursive Rendering。
- 不新增 HDRP 式 per-material proxy volume 组件；URP 侧仍以 reflection probe 的 influence bounds 为代理来源。
- 不承诺 Metal/mobile/OpenGLES 与真实 HMD；只做当前工程的 DX11/DX12/Vulkan 与 Mock HMD 回归。
- 动态分辨率是 URP 14.0.12 引擎限制，只记录、不实现。

### 必须保持的不变量

1. **效果关闭逐字节不变**：Volume `enabled=false`、`intensity=0`、材质 `_RefractionModel=None`、无 `_SCREEN_SPACE_REFRACTION` 时，输出与当前基线逐字节一致。
2. **非折射材质不变**：不透明、普通透明、SimpleLit/Particles/Terrain 的输出不变。
3. **SRP Batcher 布局稳定**：新增材质属性必须进 `UnityPerMaterial` CBUFFER；仅 ShaderGraph 使用的全局量不进材质 CBUFFER。
4. **无每帧托管分配**：`RefractionPass` 与 probe 表更新不得产生新的 per-frame GC；probe 场景扫描仍保持 30 帧节流。
5. **XR 可用**：新增纹理、数组和矩阵逻辑必须区分 per-view 与 per-object 数据；单 pass 与 multipass 至少 smoke test。
6. **变体可控**：新增分支优先使用现有 `_REFRACTION_*` / `_SCREEN_SPACE_REFRACTION` 关键字；若新增关键字，记录变体数量前后对比。
7. **证据可复现**：每个任务必须有 `validation/05-ScreenSpaceRefractionImprove/` 下的图像、指标与 JSON，不接受只看最终图的口头结论。

## 2. 任务总览

| ID | 级别 | 任务 | 依赖 | 主要文件 | 状态 |
|---|---|---|---|---|---|
| P0-1 | P0 | 屏幕 miss 时用 probe cubemap 透射 fallback | 无 | `ScreenSpaceRefraction.hlsl`、`LitForwardPass.hlsl`、`PBRForwardPass.hlsl` | 实现完成，T11 通过（miss/边缘另列） |
| P0-2 | P0 | opacity 作为 `transmittanceMask`，拆分 diffuse/specular | P0-1 | `Lighting.hlsl`、`LitForwardPass.hlsl`、`PBRForwardPass.hlsl`、`BRDF.hlsl` | 实现完成，T11 通过 |
| P0-3 | P0 | 代理 fallback：box → 任意 probe → 无限/sky | P0-1 | `RefractionPass.cs`、`ScreenSpaceRefraction.hlsl` | 实现完成，T11 通过 |
| P1-1 | P1 | `_Ior` 进入 F0/Fresnel，透射与反射能量一致 | P0-2 | `BRDF.hlsl`、`Lighting.hlsl`、`LitInput.hlsl`、`PBRForwardPass.hlsl` | 实现完成，静态/解析验证通过，视觉证据待补 |
| P1-2 | P1 | Thin 模型 atDistance 固定 0.005 | 无 | `ScreenSpaceRefraction.hlsl` | 实现完成，静态验证通过，视觉证据待补 |
| P1-3 | P1 | per-object proxy 表；light layer 在 URP 14 不可用 | P0-3 | `RefractionPass.cs`、`ScreenSpaceRefraction.hlsl` | 实现完成；编辑器验收通过；独立 player + RenderDoc `.rdc` 复核通过（第 18 节）；GPU p95 与物体边界序列帧待补 |
| P2-1 | P2 | 屏幕外命中改为 HDRP 式 fade + clamp | P0-1/P0-2 | `ScreenSpaceRefraction.hlsl` | 复核关闭：NDC 外 fade 恒为 0，与硬拒绝等价 |
| P2-2 | P2 | pre-refraction 透明队列 | P0-2 | `UniversalRenderer`、`RefractionPass.cs`、`ScreenSpaceRefraction.hlsl` | 待开始（有决策门） |
| P2-3 | P2 | 厚度贴图 / 透射色贴图 | P0-2 | `Lit.shader`、`ComplexLit.shader`、`LitInput.hlsl`、ShaderGraph | 待开始 |
| P2-4 | P2 | 预积分 FGD LUT（可选） | P1-1 | `BRDF.hlsl`、资源包 | 待开始（可选） |
| P2-5 | P2 | 动态分辨率/MSAA/材质覆盖的测试与文档 | 全部 | 文档、测试 | 待开始 |
| P2-6 | P2 | 验证报告、已知差异、归档与清理 | 全部 | `validation/`、文档 | 待开始 |

推荐实施顺序：`P0-1 → P0-2 → P0-3 → P1-2 → P1-1 → P1-3 → P2-*`。P0-2 依赖 P0-1，否则 opacity=0 且屏幕 miss 时表面会变黑；P0-3 在 P0-1 之后只是扩大可用场景，不改变 fallback 公式。

---

## 3. P0 任务

### P0-1 屏幕 miss 时用 probe cubemap 透射 fallback

**差异**

HDRP 的屏幕空间折射失败或 fade 到 0 时，`refractionHierarchyWeight` 未用满，LightLoop 会继续用 reflection probe 的 cubemap 沿折射方向采样，乘 transmittance 后归入 `specularTransmitted`；天空也会作为 env light 参与。参考 HDRP `Lit.hlsl` 的 `EvaluateBSDF_Env` 折射分支（约 2016–2110 行）和 `LightLoop.hlsl` 的 refraction 调用（约 590–625 行）。

URP 当前在 `ScreenSpaceRefractionEvaluate` 中 miss 直接返回 0（`ScreenSpaceRefraction.hlsl` 约 118–135 行），没有第二数据源；一旦 P0-2 把 opacity 用于 mask，opacity=0 的玻璃在 miss 时会出现黑色/纯镜面区域。

**实现方案**

1. 重构 `ScreenSpaceRefractionEvaluate` 的输出，显式返回：
   - `transmitted`：屏幕空间贡献，仍按 `weight` 缩放；
   - `weight`：edge fade × intensity，范围 0–1；
   - `transmittance`：`exp(-absorption * refraction.dist)`；
   - `refractV` / `refractPosWS`：离开体积后的射线；
   - `valid`：模型、proxy/fallback 是否可用。
2. 在 `LitForwardPass.hlsl` 与 `PBRForwardPass.hlsl` 中，仅在 `_REFRACTION_* && _SCREEN_SPACE_REFRACTION` 分支内计算 fallback：
   ```hlsl
   half3 probeTransmitted = GlossyEnvironmentReflection(
       result.refractV, result.refractPosWS,
       input.perceptualRoughness, 1.0, input.screenUV);
   probeTransmitted *= (1.0 - input.specularFGD) * result.transmittance * (1.0 - result.weight);
   half3 transmittedTotal = result.transmitted + probeTransmitted;
   ```
   `GlossyEnvironmentReflection` 已包含 `unity_SpecCube0` 的 HDR 解码与 box projection，见 `GlobalIllumination.hlsl` 约 281 行。若 `unity_SpecCube0` 在无 probe 场景为黑，再用 `SampleSH(result.refractV)` 作为最后兜底，但必须先验证 URP 默认 reflection probe 是否已回退到 sky。
3. `RefractionPass` 不再仅因“没有 box-projected probe”关闭 keyword。无 proxy 时把屏幕路径的 `weight` 置 0，仍保留材质 keyword，让 fallback 生效。只有 Volume 关闭、feature 资源缺失或 capability 不支持时才关闭 keyword。
4. 当 `_SCREEN_SPACE_REFRACTION` 且 Volume 开启时，折射材质的输出 alpha 固定为 1（与 HDRP 一致）；fallback 与屏幕路径都成立时，用 `weight` 决定两者比例，禁止双计数。
5. Alpha/Premultiply/Additive 三种透明混合模式至少按 Alpha 语义验收；Premultiply/Additive 若与 `alpha=1` 冲突，记录限制并补测试，不在本轮强行统一。

**风险与边界**

- `unity_SpecCube0` 是 per-renderer 的 probe，不一定是 HDRP 在该像素选中的 env light；这是 URP 架构下的近似，必须在已知差异中记录。
- 无 probe、无 sky 的纯色相机可能出现 fallback 为 0；验收要求至少覆盖“有 sky、无 reflection probe”的场景。
- `GlossyEnvironmentReflection` 的 roughness mip 与 HDRP `SampleEnvWithDistanceBaseRoughness` 不完全一致，只要求语义与连续性，不要求像素相同。
- 透射 fallback 不能再乘一次 albedo/diffuse，否则会与 P0-2 的 mask lerp 重复。

**验收标准**

1. 有 sky、无 reflection probe 时，折射可见且玻璃内颜色来自 sky/background，非黑、非仅镜面。
2. 屏幕命中 `weight=1` 时 fallback 项必须为 0：把 probe 染成与背景明显不同的颜色，玻璃内必须显示背景而不是 probe。
3. 屏幕 miss 时玻璃内显示 probe 颜色；将 probe 移走/关闭后回退到 sky 或 SH。
4. 沿屏幕边缘 fade 带取一行像素，屏幕贡献与 fallback 的过渡无 > 1/255 的跳变（允许噪声阈值 2/255）。
5. `opacity=1` 时，无论屏幕命中还是 miss，透射总量（screen + fallback）接近 0；`opacity=0` 时两者按 `weight` 连续混合。
6. 所有 miss 像素无 NaN/Inf/负值；关闭 Volume 后与当前基线逐字节一致。
7. EditMode 新增至少一个 shader/逻辑测试，验证无 proxy 时 keyword 仍开启、fallback 分支被编译进折射变体。

**交付物**

- `ScreenSpaceRefraction.hlsl` 的新输出结构与 fallback helper。
- `LitForwardPass.hlsl`、`PBRForwardPass.hlsl` 的接线。
- `RefractionPass.cs` 的 keyword 生命周期调整。
- `validation/05-ScreenSpaceRefractionImprove/P0-1/` 下的 sky-only、probe-only、混合、边缘带图像与 `Report.json`。

---

### P0-2 opacity 作为 transmittanceMask，拆分 diffuse/specular

**差异**

HDRP 在折射材质上执行：

- `surfaceData.transmittanceMask = 1 - alpha`，并把 `alpha = 1`（`LitDataIndividualLayer.hlsl` 约 345–358 行）。
- `PostEvaluateBSDF` 用 `lightLoopOutput.diffuseLighting = lerp(diffuseLighting, specularTransmitted, transmittanceMask * _EnableSSRefraction)`（`Lit.hlsl` 约 2142–2150 行）；`specularLighting` 保留，不再乘 mask。

URP 当前：

- 把 `transmitted` 直接加到 `color.rgb`（`LitForwardPass.hlsl` 约 267 行）；
- `color.a = lerp(color.a, 1.0, refractionWeight)`（约 271 行）；
- `surfaceData.alpha` 只用于 `InitializeBRDFData`，而该函数只有 `_ALPHAPREMULTIPLY_ON` 时用 alpha 乘 diffuse（`BRDF.hlsl` 约 69–76 行）。

因此 URP 在折射命中时保留全部 diffuse/直接光并叠加折射背景，且 opacity 不控制透射量；这是当前最大的语义差异。

**实现方案**

1. 在 `Lighting.hlsl` 新增一个仅折射变体使用的拆分函数，不要在现有 `UniversalFragmentPBR` 内改写出参，避免影响不透明路径的逐字节基线和编译器重组：
   ```hlsl
   void UniversalFragmentPBRSplit(InputData inputData, SurfaceData surfaceData,
                                  out half3 diffuseColor, out half3 specularColor);
   ```
   内部镜像 `UniversalFragmentPBR` 的累加规则：
   - diffuse：`bakedGI * brdfData.diffuse` + 主光/附加光的 Lambert 部分 + vertex lighting + emission；
   - specular：直接光 `DirectBRDFSpecular * brdfData.specular` + 间接 `GlossyEnvironmentReflection * EnvironmentBRDFSpecular`；
   - AO/clear coat/ComplexLit 的处理与现有函数保持一致；clear coat 归入 specular，base diffuse 可被 mask 替换，作为近似记录。
2. 在 `LitForwardPass.hlsl` / `PBRForwardPass.hlsl` 中：
   ```hlsl
   half transmittanceMask = saturate(1.0 - surfaceData.alpha);
   half3 diffuseColor, specularColor;
   UniversalFragmentPBRSplit(inputData, surfaceData, diffuseColor, specularColor);
   diffuseColor = MixFog(diffuseColor, fogCoord);   // 保持“透射不再吃一次雾”
   specularColor = MixFog(specularColor, fogCoord);
   color.rgb = specularColor + lerp(diffuseColor, transmittedTotal, transmittanceMask);
   color.a = 1.0;
   ```
   `transmittedTotal` 来自 P0-1，已包含屏幕权重与 fallback 补权重；`transmittanceMask` 不再乘 `weight`，因为 `weight` 已在 screen/fallback 分工中消费。
3. 非折射路径继续调用原 `UniversalFragmentPBR`，确保 `_REFRACTION_*` 未定义的变体不变。
4. `DEBUG_DISPLAY` 下不启用拆分/折射合成，继续走原路径，避免调试视图被 mask 改写。
5. 同步修正 `ScreenSpaceLighting HANDOFF.md` 第 234 行的描述，并新增 opacity 语义测试；如果决定保留“加色而非 mask”，必须在已知差异中明确写出，不能继续声称与 HDRP 一致。

**风险与边界**

- 完全复制 `UniversalFragmentPBR` 会让 shader 体积增加；拆分函数只在 `_REFRACTION_*` 变体编译，需记录变体/指令数变化。
- Complex Lit 的 clear coat 在 HDRP 中“不支持 refraction + clear coat”，URP 目前允许编译；本轮只保证不崩溃、mask 作用于 base diffuse，并在已知差异中记录。
- Premultiply/Additive 混合下 `alpha=1` 的语义不同；先以 Alpha Blend 验收，其他模式记录为限制。
- 逐字节基线只要求非折射路径；折射路径允许按设计改变，但必须更新 T11 的预期值并保留旧图作为对照。

**验收标准**

1. 固定背景、固定相机，opacity=0 / 0.3 / 1.0 三张图：玻璃内像素随 opacity 单调向表面颜色收敛；用背景/表面颜色做线性拟合，`m = 1 - opacity` 的斜率误差 ≤ 5%，R² ≥ 0.99。
2. opacity=1 时，玻璃内不存在来自颜色金字塔的透射（把背景换成高亮纯色，像素差 ≤ 1/255，允许抗锯齿边缘排除）。
3. opacity=0 时，玻璃内 diffuse/直接光贡献接近 0；保留探针/SSR 镜面反射和折射背景。
4. 透明材质的 `_RefractionModel=None`、非折射透明、PBR/Unlit/SimpleLit 输出与基线逐字节一致。
5. 折射命中、屏幕 miss、无 proxy 三种情况下都遵守同一 mask；无 NaN/负值。
6. 雾：透射仍只吃颜色金字塔里的雾，不在表面再叠加一次；与当前实现保持同一策略。
7. 变体数量、指令数、SRP Batcher 兼容性记录在报告中；不透明路径的 shader 哈希不因本任务改变。

**交付物**

- `Lighting.hlsl` 的拆分函数；`LitForwardPass.hlsl`、`PBRForwardPass.hlsl` 的新合成路径。
- opacity 测试场景/材质（`Assets/Tests/ScreenSpaceLighting/RefractionImprove/`）。
- `validation/05-ScreenSpaceRefractionImprove/P0-2/` 图像、拟合 CSV、`Report.json`。
- 更新后的 `T14KnownDifferences.md`（移除/修正 opacity 条目）与主 HANDOFF 第 234 行描述。

---

### P0-3 代理 fallback：box → 任意 probe → 无限/sky

**差异**

URP `TryGetProxy` 只考虑 `isBoxProjection` 的 probe，取“包含相机/离相机最近”的一个世界 AABB；没有 box-projected probe 就关闭折射 keyword。HDRP 使用物体所在位置的 env light，probe 用 box 或 sphere influence，完全取不到时用 sky 的默认 proxy；因此 HDRP 在空场景也能折射。

**实现方案**

1. `TryGetProxy` 改为返回明确模式：
   ```csharp
   enum ProxyMode { None = 0, Box = 1, Infinite = 2 }
   ```
   - 优先保留现有 box-projected probe（行为不变）；
   - 没有 box-projected probe 时，接受任意 active/visible reflection probe 的 `bounds` 作为 AABB（`boxProjection` 只影响反射投影，不影响折射代理）；
   - 仍没有 probe 时返回 `Infinite`，用相机 far plane 或固定 1e4 作为投影距离，语义对应 HDRP 的 sky/infinite proxy。
2. 复用 `_SslRefractionProxyBoxMin.w` 作为模式：0=none、1=box、2=infinite；`_SslRefractionProxyBoxMax.w` 保留。Shader 中：
   - mode 1：`IntersectRayAABBSimple`；
   - mode 2：`projectionDistance = min(_ProjectionParams.z * 0.99, 1e4)`，不再与盒子求交；
   - mode 0：屏幕贡献 0，但 P0-1 的 fallback 仍可运行。
3. `RefractionPass` 的 keyword 条件改为“Volume 开启 + capability 支持 + color pyramid/depth 可用”；没有 proxy 不再单独关闭 keyword。资源缺失时保留 fallback（P0-1），只有 Volume 关闭才完全回到 legacy。
4. 代理扫描保持现有 30 帧缓存；新增“任意 probe”分支时复用同一份 `ReflectionProbe[]` 快照，避免额外 `FindObjectsOfType`。
5. 记录行为变化：过去“无 box probe = legacy 透明”，本轮后变成“no proxy = mask + probe/sky 透射 fallback”。如果必须保留旧行为，只能通过关闭 Volume 实现。

**风险与边界**

- URP reflection probe 没有 HDRP 的 sphere influence 数据；本轮把任意 probe 的 AABB 当 box 代理，sphere 近似放到 P1-3 再评估。
- infinite 代理会让屏幕偏移不再被 probe 边界截断，可能放大远处采样误差；必须用 depth rejection 与 mip clamp 兜底，并做边缘测试。
- 无 probe 时 `unity_SpecCube0` 可能是默认天空或黑，P0-1 验收已覆盖。

**验收标准**

1. 空场景（无 reflection probe，有 sky）折射可见，玻璃内显示 sky/背景，不是黑。
2. 非 box-projection 的 reflection probe 也能提供代理；与当前 box-projected 场景相比，相同材质/相机的像素差仅在预期范围（记录差异图）。
3. box-projected probe 场景与当前实现逐字节一致（除 P0-1/P0-2 的设计变化）。
4. 无 proxy 时 keyword 仍开启，fallback 生效；Volume 关闭后与 legacy 基线逐字节一致。
5. 相机移动/分辨率变化不产生 NaN、不越界采样；probe 扫描没有新增 per-frame GC。
6. 在 `T14KnownDifferences.md` 中替换旧的“无 box probe 无折射”描述。

**交付物**

- `RefractionPass.cs` 的 proxy 模式与 keyword 逻辑。
- `ScreenSpaceRefraction.hlsl` 的 infinite 分支。
- `validation/05-ScreenSpaceRefractionImprove/P0-3/` 空场景、非 box probe、box probe、Volume off 对比与 `Report.json`。

---

## 4. P1 任务

### P1-1 `_Ior` 进入 F0/Fresnel

**差异**

HDRP `FillMaterialTransparencyData` 执行 `fresnel0 = lerp(IorToFresnel0(ior), baseColor, metallic)`（`Lit.hlsl` 约 259–269 行），因此 IOR 同时影响反射率与 `(1-F)` 透射。URP 的 `_Ior` 只进入 `refract()` 和体积模型，主光照的 F0 仍由 `_SpecColor`/metallic 决定；高 IOR 材质会“弯得很多但反射不够”。

**实现方案**

1. 在 `BRDF.hlsl` 增加 `IorToFresnel0(float ior)`（`pow((ior - 1) / (ior + 1), 2)`），或复用 core 中同名函数（先检查 core 14.0.12 是否已提供）。
2. 给 `SurfaceData` 增加一个仅折射使用的 F0 覆盖字段（默认 0 表示未设置），在 Lit/ComplexLit 的 SurfaceData 构建处和 ShaderGraph 的 `InitializeInputData`/forward 入口按 `_REFRACTION_*` 关键字写入 `IorToFresnel0(_Ior)`。
3. 在 `InitializeBRDFData` 的折射分支使用该覆盖值：
   ```hlsl
   #if defined(_REFRACTION_PLANE) || defined(_REFRACTION_SPHERE) || defined(_REFRACTION_THIN)
       brdfSpecular = lerp(IorToFresnel0(_Ior).xxx, albedo, metallic);
       reflectivity = Luminance(brdfSpecular); // 单通道近似，报告中注明
   #endif
   ```
   不改变非折射变体的代码路径。
4. `specularFGD` 继续使用 URP 的 `EnvironmentBRDFSpecular`，但 F0 已由 IOR 修正；HDRP 的 LUT 版本放到 P2-4，若不做则在已知差异中记录“rough glass 能量近似”。
5. IOR=1.5 的默认材质输出应与当前基本一致（F0=0.04），用于限制回归范围。

**验收标准**

1. 解析检查：IOR 1.0 → F0=0，1.5 → 0.04，2.5 → 0.1837（误差 1e-4）。
2. 固定环境光/天空，粗糙度 0 的玻璃：IOR 1.0/1.5/2.5 的反射亮度单调上升，透射亮度单调下降；IOR=2.5 的反射亮度与 Schlick 解析值误差 ≤ 10%。
3. IOR=1.5 时与当前基线差异 ≤ 1/255（允许折射合成路径本身的设计差异，但 F0 部分不应跳变）。
4. 非折射材质、非折射透明、opaque 输出逐字节不变。
5. 无 NaN/Inf；SRP Batcher 布局与材质属性面板正常。

**交付物**

- `BRDF.hlsl`、`SurfaceData` 构建点、`LitForwardPass.hlsl`/`PBRForwardPass.hlsl` 的改动。
- `validation/05-ScreenSpaceRefractionImprove/P1-1/` 的 IOR 梯度图、解析对照 CSV、`Report.json`。

---

### P1-2 Thin 模型 atDistance 固定 0.005

**差异**

HDRP 对 `_REFRACTION_THIN` 同时把 `atDistance` 和 `thickness` 设为 0.005（`Lit.hlsl` 约 456–466 行）；URP 只把 thickness 设为 0.005，吸收仍用 `_TransmittanceDistance`，导致有色薄玻璃吸收偏弱。

**实现方案**

1. 在 `ScreenSpaceRefraction.hlsl` 定义 `#define SSL_REFRACTION_THIN_DISTANCE 0.005`。
2. 吸收距离计算改为：
   ```hlsl
   float absorptionDistance = input.transmittanceDistance;
   #if defined(_REFRACTION_THIN)
       absorptionDistance = SSL_REFRACTION_THIN_DISTANCE;
   #endif
   half3 absorption = ScreenSpaceRefractionToAbsorption(input.transmittanceColor, absorptionDistance);
   ```
3. 保持 thickness 分支不变（当前已经是 0.005）；不要修改 Box/Sphere。
4. 如果 P2-3 增加 thickness map，Thin 模型仍强制 0.005，与 HDRP 一致。

**验收标准**

1. 白色 transmittance 的 Thin 图像不变（逐字节或 ≤1/255）。
2. 有色 transmittance 的 Thin：玻璃出射颜色符合 `exp(-(-ln(T)/0.005) * dist)`，与解析值误差 ≤ 1%；与 HDRP 同参数截图的通道均值误差 ≤ 5%。
3. Box/Sphere 模型、非折射材质输出不变。
4. T11 的 Thin 用例若按设计改变，更新预期图/指标并在报告中注明“语义修复”。

**交付物**

- `ScreenSpaceRefraction.hlsl` 的单点改动。
- Thin 白/有色、Box/Sphere 对照图与 `Report.json`。

---

### P1-3 per-object/per-pixel probe 选择与 light layer

**差异**

HDRP 用物体中心所在 tile 的第一个 env light 作为折射代理，并在 `EvaluateBSDF_Env` 中检查 light layer；URP 仍是“全局离相机最近的 probe”，同一物体跨多个 probe 区域时可能与反射/SSR 的 probe 不一致，相机移动时也可能跳变。

**实现方案**

1. `RefractionPass` 构建最多 N 个（先取 8）代理表：
   - `Vector4 _SslRefractionProbeMin[N]`、`_SslRefractionProbeMax[N]`；
   - `Vector4 _SslRefractionProbeParams[N]`（importance、layerMask、模式/序号）；
   - `int _SslRefractionProbeCount`。
   复用现有 visible probes + scene probe 快照，按 importance/包含关系排序。
2. Shader 端在折射入口按**物体中心**选择：
   ```hlsl
   float3 objectCenterWS = GetObjectToWorldMatrix()._m03_m13_m23;
   ```
   优先第一个包含 objectCenter 的代理；否则最近；否则最后一个 infinite fallback。HDRP 用 object center 是为了稳定，URP 保持一致。
3. Light layer：先确认 `ReflectionProbe`/`VisibleReflectionProbe` 在当前版本是否暴露 `renderingLayerMask`。可用则把 mask 写入 `_SslRefractionProbeParams`，用 `GetMeshRenderingLayer()` 过滤；不可用则在已知差异中写明“URP 14 不暴露 probe rendering layer，折射代理不按 layer 过滤”。
4. 旋转：URP `probe.bounds` 是 AABB，不暴露朝向；本轮继续忽略旋转，报告中记录。
5. 性能：循环 8 次只发生在透明折射变体；若指令数/耗材超预算，可降为 4 或先按 nearest 排序后只检查前 2 个包含关系。
6. P0-3 的 far-projection fallback 作为表中的最后一项，保证永不为空。

**验收标准**

1. 两 probe 场景：相机在 A 附近、物体在 B 内；折射必须使用 B 的代理（构造 A/B 不同尺寸/位置，使屏幕命中点可区分），与 HDRP 同场景结构一致。
2. 相机从 A 移到 B、物体不动：代理不因相机移动而跳变；物体跨 probe 边界时只在边界处切换，且切换帧无双影/黑边（记录差异图）。
3. 若实现 light layer：不同 layer 的 probe/物体组合按预期匹配/忽略；未实现则报告明确标注。
4. 空场景/无 probe 仍走 P0-3 infinite；box probe 单场景与 P0-3 结果逐字节一致。
5. 性能：1080p 折射场景 GPU p95 回归 ≤ 10%（参考值，DVFS 无效时标注）；无 per-frame GC。
6. 代码中无每帧 `FindObjectsOfType`；probe 表更新只在已有 30 帧节流点发生。

**交付物**

- probe 表上传与 shader 选择逻辑。
- 双 probe、跨边界、layer（若支持）图像与 `Report.json`。
- 性能/指令数记录。

---

## 5. P2 任务

### P2-1 屏幕外命中改为 HDRP 式 fade + clamp

> **复核结论（2026-11-16）**：HDRP `EdgeOfScreenFade = saturate(fadeRcp * (1 - |2*NDC-1|))`，而 `screenFadeDistance ∈ [0.001,1]` 保证 `fadeRcp ≥ 1`，因此 NDC 出 [0,1] 时权重恒为 0，且发生在深度采样之前；URP 的硬拒绝与之逐像素等价。剩余的下界 clamp 只影响权重为 0 的边缘半 texel。本任务关闭，不修改代码；shader 注释与 T14 已知差异已同步。

**差异**

URP 在 `hitNDC` 任一分量超出 [0,1] 时直接 miss；HDRP 用 `EdgeOfScreenFade` 计算权重，并对采样 UV 做 mip footprint clamp，屏幕外仍可能有部分贡献。URP 还多了一个 UV 下界 clamp。

**实现方案**

1. 去掉“NDC 出屏即 return 0”，保留投影距离/`w>0`/有限值检查。
2. `hitPositionSS` clamp 到 `[0, _ScreenSize-1]` 后读 depth；采样 UV 继续用 HDRP 的 `scale + limit` 公式，只做上界 `min`，下界由 clamp 到 `scaleMinusLimit` 保证不越界。
3. 用 `EdgeOfScreenFade(hitNDC, fadeRcp)` 作为唯一屏幕权重来源；fade 为 0 时自然回落到 P0-1 的 probe fallback。
4. 该任务会改变边缘像素行为，必须在 P0-2 的合成路径稳定后做；若视觉上出现明显拖边/漏色，可以保留当前硬拒绝并在已知差异中记录，不强求。

**验收标准**

1. 边缘带 64px 内无硬切黑边/亮边；屏幕内外过渡的相邻像素差 ≤ 2/255。
2. 屏幕外背景颜色不会被拉进屏幕中心；只允许靠近边缘的 clamp 采样。
3. 无 NaN/Inf；中心区域与 P2-1 前逐字节一致（除边缘带）。
4. fallback 在 fade→0 时接管，不出现双计。

**交付物**

- `ScreenSpaceRefraction.hlsl` 的边界分支。
- 边缘带裁剪图、差异图与 `Report.json`。

---

### P2-2 pre-refraction 透明队列

**差异**

HDRP 在生成颜色金字塔前渲染 `PreRefraction` 队列（`HDRenderQueue` 约 31–33、95 行，队列中心 2750），因此这些透明体能被玻璃折射；URP 的金字塔只含 opaque+sky，所有透明体都不可见。该差异已在 [T14KnownDifferences](../../Done/01-ScreenSpaceLighting/validation/T14/T14KnownDifferences.md) 记录。

**实现方案（有决策门）**

URP 的 AlphaTest 队列覆盖 2450–2999，不能直接复用 HDRP 的 2750；需要自定义透明子区间并改 core 的透明绘制范围。建议方案：

1. 约定 URP pre-refraction 队列为 `3000–3099`（仍在 Transparent 区间内），普通透明为 `3100–5000`。
2. 新增 SSL pass `ScreenSpaceLightingPreRefractionPass`，事件放在 sky 之后、颜色金字塔之前（例如 `AfterRenderingSkybox + 1`，金字塔改为 `+2` 或 402），用 `DrawingSettings` + `FilteringSettings(RenderQueueRange(3000, 3099))` 绘制，`ShaderTagId("UniversalForward")`，颜色写相机色、深度测试用 opaque depth、ZWrite off。
3. 在 `UniversalRenderer` 增加类似 `SetProbeAtlasPublishing` 的 hook，让 SSL feature 把普通透明 pass 的 queue range 改成 `3100–5000`；否则同一物体会被绘制两次。这是 core 改动，必须单独评审。
4. 材质/文档明确“渲染队列 3000–3099 是 pre-refraction，需要 SSL feature；否则会被普通透明 pass 在 3100+ 才绘制”——若 feature 关闭，需要回退为普通透明（core hook 恢复默认 range）。
5. 金字塔生成点移到预折射 pass 之后；`ScreenSpaceRefraction.hlsl` 不需要改采样逻辑。

**决策门**：如果目标场景没有“透明叠透明”的需求，允许本任务延期并保留已知差异；但必须完成“普通透明不进入金字塔”的回归测试，防止未来误改。

**验收标准**

1. pre-refraction 面板放在玻璃后方时出现在玻璃内；普通透明面板放在同一位置时不出现（除非也设为 pre-refraction）。
2. 面板恰好绘制一次：Frame Debugger 中预折射 pass 与普通透明 pass 的 DrawCall 不重复；最终图无变亮/双 alpha。
3. 关闭 SSL feature 时，3000–3099 队列材质回到普通透明绘制，无丢失。
4. 三路径（Forward/Forward+/Deferred）金字塔内容正确；T12 组合矩阵回归通过。
5. 透明排序：预折射内部按距离/优先级排序，普通透明内部排序不变。

**交付物**

- 预折射 pass、core queue hook、材质/文档说明。
- “预折射面板可见/普通面板不可见/关闭 feature 回退”三组图与 `Report.json`。
- 如果延期：更新已知差异，附上决策记录。

---

### P2-3 厚度贴图 / 透射色贴图

**差异**

HDRP 支持 `_ThicknessMap` + `_ThicknessRemap`、`_TransmittanceColorMap`，以及 LayeredLit 的逐层参数；URP 当前只有标量 `_Thickness`/`_TransmittanceColor`。

**实现方案**

1. `Lit.shader` / `ComplexLit.shader` 增加：
   - `_ThicknessMap("Thickness Map", 2D) = "white"`、`_ThicknessRemap("Thickness Remap", Vector) = (0,1,0,0)`；
   - `_TransmittanceColorMap("Transmittance Color Map", 2D) = "white"`。
2. `LitInput.hlsl` 在 `UnityPerMaterial` CBUFFER 增加纹理/缩放/偏移与 remap；声明与现有 `_BaseMap` 相同的 `_ST` 约定。
3. SurfaceData 构建时按 UV 采样：`thickness = remap.x + remap.y * sample`；`transmittanceColor *= map.rgb`；仅在 `_REFRACTION_*` 变体启用纹理采样，避免非折射材质多采样。
4. ShaderGraph Lit target 增加同名属性与 texture slot；`PBRForwardPass.hlsl` 从 SurfaceDescription 读取（如果 target 支持），否则至少暴露材质属性并走默认值。
5. GUI 在 `LitShader.cs` / `ShaderGraphLitGUI.cs` 的 Refraction 区增加贴图槽；未选择时使用 white，保持旧材质视觉不变。
6. 不使用 `_Thickness` 作为 SSS 厚度（URP Lit 无 SSS），避免与 transmission 语义冲突。

**验收标准**

1. 默认 white 贴图 + remap (0,1)：输出与 P1-2 后基线逐字节一致。
2. 渐变 thickness 贴图：玻璃不同区域的吸收随厚度单调变化，与解析 Beer-Lambert 误差 ≤ 2%。
3. 彩色 transmittance map：逐像素色相/强度正确，透明区域仍按 mask 合成。
4. 未使用贴图的材质无额外纹理绑定/变体；SRP Batcher 兼容。
5. 纹理缺失/默认资源被裁剪时回退 white，无洋红/黑块。

**交付物**

- 属性、CBUFFER、采样、GUI、ShaderGraph 属性。
- 渐变厚度/彩色透射贴图对照与 `Report.json`。

---

### P2-4 预积分 FGD LUT（可选）

**差异**

HDRP 用 `GetPreIntegratedFGDGGXAndDisneyDiffuse` 的 LUT 计算 `specularFGD`；URP 用 `EnvironmentBRDFSpecular` + `Pow4(1-NdotV)` 近似。中等粗糙度玻璃的能量会有偏差。

**实现方案**

1. 评估是否值得：优先确认 P1-1 后粗糙玻璃与 HDRP 的差异是否仍超过验收阈值；若不超过，不引入 LUT。
2. 若实施：把 HDRP 的 `_PreIntegratedFGD_GGXDisneyDiffuse` 资源与 `GetPreIntegratedFGD...` 函数移植到 URP，作为全局纹理绑定；仅折射变体采样。
3. 与 P0-2 的拆分函数共用 FGD，确保 direct/indirect 一致。
4. 记录显存、采样次数、变体数；若成本超过收益，保留近似并在已知差异中写明。

**验收标准**

1. 粗糙度 0.2/0.5/0.8 的玻璃反射/透射与 HDRP 同参数截图通道均值误差 ≤ 5%。
2. 非折射材质不使用该 LUT，输出不变。
3. 资源缺失时回退 `EnvironmentBRDFSpecular`，无报错。

**交付物**

- （若实施）LUT 资源、绑定、采样路径与对照报告；否则在已知差异中记录“FGD 近似”。

---

### P2-5 动态分辨率 / MSAA / 材质覆盖的测试与文档

**内容**

1. 动态分辨率：URP 14.0.12 不驱动 `DynamicResolutionHandler`，`ScalableBufferManager` 不缩放 RTHandle 相机目标；维持“不支持”结论，补一条折射相关的 readback/日志证据，避免后续误判。
2. MSAA：补 Forward/Forward+ MSAA 下颜色金字塔生成与折射采样测试；Deferred 兼容路径忽略 MSAA，按现有 T12 结论记录。
3. 材质覆盖：明确 Lit、Complex Lit、ShaderGraph Lit 支持；SimpleLit/Particles/Terrain 继续走 probe，不实现折射。若未来需要，按 P2-3 的属性契约另开任务。
4. 平台：DX11/DX12/Vulkan 至少 smoke；Metal/mobile/HMD 未测试平台继续在支持矩阵标为不承诺。

**验收标准**

- 文档与实测一致；MSAA 开关不产生 NaN/越界；动态分辨率限制有证据；材质覆盖测试与 T11/T12 一致。

**交付物**

- 更新后的支持矩阵/已知差异；MSAA/材质覆盖测试结果。

---

### P2-6 验证报告、已知差异、归档与清理

**内容**

1. 汇总所有 P0/P1/P2 任务的结果到 `validation/05-ScreenSpaceRefractionImprove/Report.json` 与 `Report.md`。
2. 更新 `T14KnownDifferences.md`：删除已修复条目（opacity、无 box probe 无折射、Thin 吸收距离），保留有意差异（per-object probe 近似、FGD 近似、pre-refraction 若延期、动态分辨率、材质覆盖）。
3. 更新主 [ScreenSpaceLighting HANDOFF](../../Done/01-ScreenSpaceLighting/HANDOFF.md) 的当前位置与唯一事实入口。
4. 退出后把本目录移动到 `PLAN/Done/05-ScreenSpaceRefractionImprove/`，保留 `validation/` 全部证据。
5. 执行第 7、8 节的交付与清理清单。

**验收标准**

- 报告 `exitCriteriaMet=true`；所有 P0/P1 用例通过或明确记录阻塞；文档、代码、证据三者一致；工作区干净。

---

## 6. 如何收尾与验收

### 6.1 收尾流程

1. **冻结范围**：P0 全部完成；P1 完成或在报告中明确“引擎/接口阻塞”；P2 按决策门选择，不允许静默跳过。
2. **跑完整回归**：
   - EditMode / PlayMode 全量；
   - T12 组合矩阵 34/34/35（Forward/Forward+/Deferred）；
   - 关闭 Volume、intensity=0、`_RefractionModel=None`、非折射透明、SimpleLit 的逐字节基线；
   - XR single-pass/multipass smoke；
   - Release Player（含 shader prefiltering）构建，确认折射变体未被打掉。
3. **采集证据**：每个用例保存 PNG、指标 CSV、基线哈希；所有用例写入 JSON，不接受“看起来正常”。
4. **更新文档**：已知差异、支持矩阵、主 HANDOFF、本文件状态。
5. **归档**：`PLAN/TODO/05-ScreenSpaceRefractionImprove` → `PLAN/Done/05-ScreenSpaceRefractionImprove`。
6. **清理工作区**：见第 8 节；确认 `git status` 只包含预期文件。

### 6.2 退出标准

| 类别 | 标准 |
|---|---|
| 编译 | 0 error；新增 warning 为 0（或有明确理由） |
| P0 | P0-1/P0-2/P0-3 全部验收用例通过；opacity、fallback、no-proxy 三个主场景无黑块/双计 |
| P1 | P1-1/P1-2 通过；P1-3 通过或记录为“per-object probe 近似”的已接受差异 |
| 回归 | 非折射/关闭效果逐字节一致；T12 34/34/35；EditMode/PlayMode 全绿 |
| 视觉 | 边缘带无硬切；无 NaN/Inf/负值；屏幕命中与 fallback 过渡连续 |
| XR | 单 pass + multipass smoke 通过；无 per-eye 资源串用 |
| 构建 | Release Player 成功；17 个必需 Shader/ComputeShader 在包内；无 missing feature |
| 性能 | 记录 1080p GPU p95；相对本轮基线回归 ≤ 10%（DVFS 无效时标注为参考） |
| 显存 | 新增 probe 表/资源 ≤ 预算；分辨率切换 10 次无泄漏（RTHandle 计数回落） |
| 文档 | 已知差异、支持矩阵、HANDOFF、报告一致；opacity 旧描述已修正 |
| 证据 | `Report.json` 每个用例有 `passed`、图/指标路径、基线哈希；`Report.md` 有结论 |

### 6.3 证据格式

```text
PLAN/TODO/05-ScreenSpaceRefractionImprove/validation/
  BaselineHashes.json
  Report.json
  Report.md
  P0-1/{images,metrics.csv}
  P0-2/{images,fitting.csv}
  P0-3/{images}
  P1-1/{images,analytic.csv}
  P1-2/{images}
  P1-3/{images,perf.json}
  P2-*/...
```

`Report.json` 每个用例至少包含：

```json
{
  "taskId": "P0-2",
  "case": "opacity_mask_0p0_0p3_1p0",
  "passed": true,
  "images": ["P0-2/opacity_0.png", "P0-2/opacity_0p3.png", "P0-2/opacity_1.png"],
  "metrics": { "fitSlopeError": 0.021, "r2": 0.997, "maxChannelDiff": 0 },
  "baselineHash": "sha256:...",
  "notes": ""
}
```

---

## 7. 交付产物

1. **代码**
   - `ScreenSpaceRefraction.hlsl`：输出结构、fallback、infinite proxy、Thin atDistance、边界语义。
   - `LitForwardPass.hlsl` / `PBRForwardPass.hlsl`：拆分散射、mask 合成、fallback 接线。
   - `Lighting.hlsl` / `BRDF.hlsl`：`UniversalFragmentPBRSplit`、`IorToFresnel0`、F0 覆盖。
   - `RefractionPass.cs`：proxy 模式、keyword 生命周期、probe 表、可选 pre-refraction。
   - `UniversalRenderer`（仅 P2-2）：透明队列范围 hook。
   - `Lit.shader` / `ComplexLit.shader` / ShaderGraph target / GUI：P2-3 属性。
2. **测试资产**
   - `Assets/Tests/ScreenSpaceLighting/RefractionImprove/` 下的场景、材质、贴图、渲染器资产；命名与 T11 风格一致。
   - EditMode/PlayMode 测试；shader 变体/关键字断言。
3. **验证证据**
   - `validation/05-ScreenSpaceRefractionImprove/` 下第 6.3 节结构；保留 PNG/CSV/JSON。
4. **文档**
   - 更新 `T14KnownDifferences.md`（或新建本轮 KnownDifferences）与支持矩阵。
   - 更新主 `PLAN/Done/01-ScreenSpaceLighting/HANDOFF.md` 和本文件状态。
   - 若 P2-2 延期，附决策记录；若 P2-4 不做，写明 FGD 近似。
5. **清理记录**
   - `Cleanup.md`：删除的临时目录、恢复的 ProjectSettings SHA-256、最终 `git status --short` 截图/文本。

---

## 8. 工作区清理

完成验收后按以下顺序清理，保留证据、删除临时产物：

1. **先记录再删除**：`git status --short`、`git diff --check` 各保存一份到 `validation/.../Cleanup/git-status.txt`。
2. **删除临时播放器/构建**：本轮创建的 `Builds/RefractionImproveTemp*`、`Temp/`、`Logs/`、`UserSettings/` 变更；不要删除 `Assets/Tests/ScreenSpaceLighting/RefractionImprove/` 与 `validation/`。
3. **恢复 ProjectSettings**：如果为测试改过 `ProjectSettings/*.asset`（Graphics/Quality/Pipeline 等），逐项恢复并用开工前记录的 SHA-256 校验；把校验结果写入 `Cleanup.md`。
4. **清理临时场景/材质**：不留 `Assets/**/Temp*`、`Assets/**/Capture*`；测试所需资产保留并确认 `.meta` 齐全、无孤儿 `.meta`。
5. **清理 GPU/Frame Debugger 临时捕获**：只保留 `validation/` 内的证据；删除 `Captures/`、`Recordings/` 中的临时文件。
6. **确认没有提交生成物**：`Library/`、`Temp/`、`Logs/`、`*.csproj`、`*.sln` 不应进入提交；不要用 `git add -A` 盲目提交。
7. **最终检查**：
   - `git status --short` 只包含预期代码、测试资产、验证证据与文档；
   - `git diff --check` 无空白错误；
   - 所有新资源有 `.meta`；
   - `Report.json` 的路径均存在；
   - 归档目录从 `PLAN/TODO` 移到 `PLAN/Done` 后，旧路径无残留引用。
8. **证据保留原则**：PNG/CSV/JSON 全部保留（项目既往做法）；只删“无法复现的临时中间文件”。如果不确定，先移到 `validation/.../Trash/` 并在 `Cleanup.md` 说明，不直接 `rm`。

---

## 9. 风险与开放问题

1. **`GlossyEnvironmentReflection` 在无 probe 场景的行为**：必须实测 `unity_SpecCube0` 是否为 sky/默认 cube；若为黑，P0-1 需要 SH 兜底，验收指标要相应调整。
2. **per-object probe 与 URP 反射 probe 选择不一致**：URP 的 `unity_SpecCube0` 由 Unity per-renderer 选择，shader 侧 probe 表是近似；可能出现“反射用 A、折射用 B”。P1-3 验收只要求不跳变、可解释。
3. **Light layer 可用性**：先确认 14.0.12 的 `ReflectionProbe` 是否暴露 `renderingLayerMask`；不可用就明确记录，不硬造数据。
4. **拆分函数与 URP 变体**：`UniversalFragmentPBRSplit` 复制光照逻辑，未来 URP 升级要同步；在已知差异中注明维护点。
5. **pre-refraction 的 core 改动**：queue range hook 会影响所有透明材质排序；必须单独评审，不能与 P0 混在一个提交。
6. **Premultiply/Additive 混合**：`alpha=1` 与 mask 语义可能冲突；先 Alpha，其他模式记录限制。
7. **HDRP 像素级对照 vs 语义验收**：两条管线曝光、fog、probe 系统不同，P0/P1 以解析/结构指标为准；像素对照只作为辅助，不承诺全图误差阈值。
8. **性能**：拆分光照 + probe 表循环会增加折射变体成本；必须在报告中给出 1080p p95、指令数和变体数，超过 10% 要有取舍记录。

---

## 10. 下一步

1. 先做 P0-1（fallback），因为它是 P0-2 的前提；P0-1 完成后再改 P0-2，避免 opacity=0 时出现黑洞。
2. P0-3 与 P1-2 可以并行；P1-2 是单点低风险修改，适合先拿一个可验证的语义修复。
3. P1-1 依赖 P0-2 的拆分结果；P1-3 依赖 P0-3 的代理表。
4. P2-2 在动 core 前先做决策：目标场景是否真的需要透明叠透明；不需要就保留已知差异并记录。
5. 每个任务完成即写 `validation/.../任务ID/` 证据，不要攒到最后；最后一轮只做回归、文档和清理。

## 11. 实施记录（2026-11-16）

本轮已落地 P0-1、P0-2、P0-3、P1-1、P1-2，P1-3/P2-* 未开始：

- **P0-1**：`ScreenSpaceRefractionEvaluate` 返回 `ScreenSpaceRefractionResult`（`transmitted`/`weight`/`transmittance`/`refractV`/`refractPosWS`/`valid`）。新增 `ScreenSpaceRefractionTransmission`，用 `GlossyEnvironmentReflection` 沿折射射线补齐 `1 - weight`；`LitForwardPass` 与 ShaderGraph `PBRForwardPass` 都改用它。`intensity=0` 现在在 `RefractionPass` 直接关闭 keyword，保证关闭效果逐字节不变；`valid=false` 时材料保持原输出。
- **P0-2**：`Lighting.hlsl` 新增 `UniversalFragmentPBRSplit` 与 `LightingPhysicallyBasedSplit`，镜像 `UniversalFragmentPBR` 的 GI/主光/附加光/clear coat/顶点光/emission 累加。折射变体按 `transmittanceMask = saturate(1 - surfaceData.alpha)` 只把 diffuse/emission 桶向透射结果 lerp（合成式 `surfaceColor + mask * (transmitted - foggedDiffuse)`），specular 桶完全在 mask 之外，输出 alpha 固定为 1。`DEBUG_DISPLAY` 仍走原 `UniversalFragmentPBR`。HDRP 的 emission 也在 mask lerp 内，与本实现一致。
- **P0-3**：`ScreenSpaceLightingRefractionPass.ProxyMode`（None/Box/Infinite）与 shader `SSL_REFRACTION_PROXY_MODE_*` 对齐。`SelectProxy` 先找 box-projected probe，再接受任意 active probe 的 bounds，最后返回 Infinite（URP 扩展的 65504 远投影；HDRP 无 env light 时的默认折射代理是相机中心、由 renderer bounds 定半径的球，投影通常夹到 0，65504 只是其 sky cubemap 的 `minProjectionDistance`）。keyword 生命周期改为 Volume 开启 + intensity>0 + pyramid/depth 可用，不再因无 box probe 关闭。代理选择仍以相机为中心，per-object/per-pixel 表留给 P1-3。
- **P1-1**：`InitializeBRDFData` 在 `_REFRACTION_* && _SCREEN_SPACE_REFRACTION` 时使用 `lerp(IorToFresnel0(_Ior), albedo, metallic)`，`reflectivity` 用对应亮度近似，F0 同时影响反射和 `(1-F)` 透射。只在全局 keyword 开启时生效，Volume 关闭保持旧 F0；Specular workflow 暂不改写（`_SPECULAR_SETUP` 分支保持原样，记录为近似）。
- **P1-2**：`_REFRACTION_THIN` 下厚度与吸收距离都为 0.005。
- **P1-3**：`RefractionPass` 改为最多 8 项代理表（7 个有限 probe AABB + 1 个 infinite fallback），从 visible probes 与 30 帧节流的 scene probe 快照去重后按 importance/visibility/volume 排序；shader 以物体中心 `GetObjectToWorldMatrix()._m03_m13_m23` 选择“包含中心且 importance 最高”的代理，否则最近 AABB，否则 infinite。light layer 因 URP 14 不暴露 probe rendering layer mask 未实现，已在 T14/代码注释记录。
- **测试/资产**：更新 `ScreenSpaceLightingMaterialTests` 的新契约断言；新增 `ScreenSpaceLightingRefractionImproveTests`（代理模式、keyword 生命周期、fallback、mask、IOR、Thin）。T11 折射测试材质改为 opacity 0（HDRP mask 语义），新增 opacity 0/0.3/1 扫描、Opacity1 参照、非 box probe、无 probe infinite 代理状态；`OutputRoot` 可用 `SSL_VALIDATION_OUTPUT_ROOT` 指向本轮 validation 目录。
- **待办**：P2-2 pre-refraction 队列、P2-3 贴图、P2-4 FGD LUT 仍未做；P2-1 已复核关闭。P1-3 的双 probe/跨边界视觉与性能证据待补。

## 12. 验证结果（2026-11-16）

证据入口：[validation/Report.md](validation/Report.md)、[validation/Report.json](validation/Report.json)、[validation/T11Verification.json](validation/T11Verification.json)、[validation/Cleanup.md](validation/Cleanup.md)。

- **T11**：`completed=True passed=True`，Forward / Forward+ / Deferred 三模式 0 issue。证据图与 CSV 在 `validation/P0-*`、`P1-*`，日志在 `validation/Logs/`。
- **EditMode**：`passed=170 failed=0 skipped=0`，包含新增 `ScreenSpaceLightingRefractionImproveTests`（代理模式、keyword 生命周期、fallback、mask、IOR 解析值、Thin 距离）。
- **P0-2 指标**：opacity 0/0.3/1 单调；归一化 0.3 点误差 2.33%（≤5%），R² 0.9997（≥0.99）；opacity=1 与关闭效果的差异 0 像素，最大通道差 4.6e-5。
- **P0-3 指标**：非 box probe 场景 keyword 绑定 8 次、infinite 0 次；无 probe 场景绑定 8 次、infinite 8 次、折射变化 16281 像素。
- **P0-1 指标**：Box/Complex/SG 折射变化 5915/2996/2476 像素；legacy 透明 0 像素变化；当前帧金字塔跟随场景。
- **未完成证据**：P0-1 的单独 screen-miss probe-only 截图与边缘带连续性（P2-1）；P1-1 的 IOR 梯度图与 P1-2 的有色 Thin 图/HDRP 对照。`Report.json` 中 P1-1/P1-2 为 `passed=null`，总 `exitCriteriaMet=false`。

## 13. 复核修复（2026-11-16）

对照 HDRP 14.0.12 复核当前实现时发现两处此前未记录的偏差，已修复：

- **A1 specular 被 mask 误伤**：此前 `LitForwardPass.hlsl` / `PBRForwardPass.hlsl` 先把 `diffuseColor + specularColor` 相加，再整体 `lerp(surfaceColor, transmitted, mask)`。HDRP `PostEvaluateBSDF` 只把 diffuse/emission 桶 lerp 到 `specularTransmitted`，specular 桶不参与 mask；因此 opacity=0 的 URP 玻璃会丢失全部 Fresnel/SSR/探针反射。现改为 `color.rgb = surfaceColor + mask * (transmitted - foggedDiffuse)`：mask=0 时与原合成逐字节一致，mask=1 时保留 `MixFog(specularColor)` + 透射。
- **A2 颜色金字塔 mip 上限少一级**：`RefractionPass` 传入了 `colorPyramidMipCount - 1`，shader 内又计算 `max(pyramidMipCount - 1, 0)`，最粗糙材质只能采到倒数第二层。现在按 HDRP `_ColorPyramidLodCount` 语义传 mip 总数，由 shader 统一减 1。
- **测试**：更新 `ScreenSpaceLightingRefractionImproveTests.OpacityIsConsumedAsTransmittanceMask` 的合成断言，新增 `ColorPyramidMipCountMatchesHdrpLodCount`。
- **验证**：见第 14 节。

## 14. 修复验证（2026-11-16）

**EditMode**：完整套件 433/433 通过，0 failed（47 秒），包含 `ScreenSpaceLightingRefractionImproveTests`、`ScreenSpaceLightingRefractionTests`、`ScreenSpaceLightingMaterialTests`、`ScreenSpaceLightingHistoryTests`，以及新增的 `ColorPyramidMipCountMatchesHdrpLodCount` 与更新后的 `OpacityIsConsumedAsTransmittanceMask`。

**T11**（Forward / Forward+ / Deferred，RTX 5060 Ti / DX11）：三模式 `completed=True passed=True issues=0`。证据入口：[validation/T11Hotfix/T11Verification.json](validation/T11Hotfix/T11Verification.json)，同目录含 54 张 PNG 与 3 份 opacity CSV。

- opacity 0/0.3/1 扫描：三模式 `opacityMonotonic=True`；归一化 0.3 点误差 2.04%（修复前 2.33%，阈值 5%），R² 0.99975（阈值 0.99）。opacity=0 的玻璃内部亮度由 0.1628 升到 0.1780，对应恢复的 specular 桶；opacity=1 的亮度不变。
- opacity=1 vs 关闭 Volume：0 像素差，最大通道差 4.58e-5，说明 mask=0 时新合成与 legacy 仍逐像素一致。
- legacy 透明基线 0 像素变化；ModelsNone / Intensity0 / OffAgain 与 Reference 逐像素相同。
- 屏幕命中、probe fallback、infinite proxy 状态保持原结论；当前低粗糙度 T11 材质未受 mip 修复影响。

注意：T11 运行会改写 `ProjectSettings/*.asset` 与测试材质的换行/序列化状态，跑完需恢复（本轮已用开工前备份恢复，最终 `git status` 只含预期文件）。

## 15. 复核收尾：代理语义、P2-1 与吸收公式（2026-11-16）

- **代理语义文档修正**：确认 HDRP 默认反射探针使用 influence volume 作为 proxy（`ProbeSettings.NewDefault()` 的 `useInfluenceVolumeAsProxyVolume=true`，`HDProbe.isProjectionInfinite` 仅在显式无限 proxy 或放弃 influence 时为真），因此 URP 用任意 active probe 的 influence AABB 是合理近似，不应按 `boxProjection` 反转。真正无法表达的是显式 infinite proxy volume。HDRP 无 env light 时使用 `InitDefaultRefractionEnvLightData`：相机中心、renderer bounds 半径的球，`minProjectionDistance=0`，投影通常夹到 0；`65504` 只用于 sky env light 的 cubemap 投影。URP 的 65504 远投影保留为有意扩展，shader/Pass/Volume 注释与 T14 已同步。
- **P2-1 关闭**：`EdgeOfScreenFade = saturate(fadeRcp * (1 - |2*NDC-1|))`，`screenFadeDistance ≤ 1 ⇒ fadeRcp ≥ 1`，所以 NDC 出 [0,1] 时权重恒为 0，HDRP 在深度采样前返回；URP 的硬拒绝逐像素等价，无需改代码。仅保留半 texel 下界 clamp 的记录。
- **吸收公式**：`ScreenSpaceRefractionToAbsorption` 改为 core 的 `-log(T + REAL_EPS) / max(atDistance, REAL_EPS)`；新增 `AbsorptionMatchesCoreTransmittanceSemantics` 契约测试。
- **已知差异补齐**：T14 增加 alpha 恒 1（HDRP）vs legacy 透明（URP 有意）、雾二次衰减、fallback box projection、clear coat 抬高 roughness 四条。
- **验证**：EditMode 完整套件 434/434 通过（新增 `AbsorptionMatchesCoreTransmittanceSemantics`）。
- **T11**：Forward / Forward+ / Deferred 均 `completed=True passed=True issues=0`；报告与 opacity CSV 见 [validation/T11Absorption/T11Verification.json](validation/T11Absorption/T11Verification.json)。本轮只入库报告与 CSV，未重复保存 54 张 PNG；tint 指标与 `T11Hotfix` 一致（起始/结束亮度 0.956651/0.750982），opacity=1 与 legacy 透明仍为 0 像素差。

## 16. P1-3 实现（2026-11-17）

- **代理表**：`RefractionPass` 构建最多 8 项（`k_MaxRefractionProxies = 8`，其中 7 个有限 probe AABB + 1 个保留的 infinite fallback）。候选来自 `CullingResults.visibleReflectionProbes` 与 30 帧节流的 `FindObjectsOfType<ReflectionProbe>()` 快照，按 `importance` 降序、可见优先、体积升序去重排序；不要求 `boxProjection`（该标志只影响反射采样）。
- **上传**：`SetGlobalVectorArray` 上传固定 8 项 `_SslRefractionProbeMin/_Max/_Params`，`SetGlobalInt` 上传 `_SslRefractionProbeCount`；数组与 `HashSet<int>` 预分配，`FindObjectsOfType` 仍只在 30 帧节流点发生，无新增每帧托管分配。
- **shader 选择**：以 `GetObjectToWorldMatrix()._m03_m13_m23` 为物体中心；优先“包含中心”的 proxy（其中取 importance 最高），否则最近 AABB，否则 infinite fallback。对象跨 probe 边界时不会因相机移动而切换代理。
- **light layer**：URP 14 的 `ReflectionProbe` / `VisibleReflectionProbe` 不暴露 rendering layer mask，因此未实现按 layer 过滤；`_SslRefractionProbeParams.y` 保留为未来槽位，T14 与 shader 注释已记录。
- **测试**：`ScreenSpaceLightingRefractionImproveTests` 更新 `KeywordLifetimeDoesNotDependOnABoxProjectedProbe`（改为检查 `BuildProbeTable`），新增 `RefractionProxyTableSelectsAtTheObjectCenter`（表上限、infinite 兜底、物体中心选择、旧 uniform 移除）。
- **验证**：EditMode 173/173 通过（0 failed，0 skipped）；T11 Forward/Forward+/Deferred 均 `completed=True passed=True issues=0`，报告见 [validation/T11P13/T11Verification.json](validation/T11P13/T11Verification.json)。单 probe 指标与上一轮一致：三模式 `boxChangedPixels=5915`、`noProxyChangedPixels=infiniteProxyChangedPixels=16281`、`legacyChangedPixels=0`、`opacityOneChangedPixels=0`，说明 P1-3 未改变单 probe 与关闭效果基线。
- **验收**：见第 17 节；双 probe 与相机移动通过，GPU p95 和物体边界序列帧仍待补。

## 17. P1-3 验收（2026-11-17）

- **验收脚本**：`ScreenSpaceLightingT11Verification.RunP13ProxySelectionVerification`。两个 probe 使用同一 capture 位置（cubemap 相同）：B 包围玻璃，A 包围相机；玻璃 IOR=1、opacity=0 使 F0=0，图案面板只进相机颜色金字塔、不进 probe cubemap，因此“屏幕路径”和“cubemap fallback”可区分。
- **双 probe 选择**：Forward / Forward+ / Deferred 全部 `completed=True passed=True`。`Both` 与 `B-only` 在玻璃内圈 0 像素差、平均色差 0；与 `A-only` 分别差 2570 / 5392 像素，平均色差 0.223 / 0.155；无 NaN/Inf。说明物体中心在 B 时确实选 B，而不是相机最近的 A。
- **相机移动**：相机从 A 移到 B 后重新渲染，`Both` 仍与 `B-only` 逐像素一致（0 差），代理不随相机切换。
- **GC**：Forward 的 refraction on/off 各 24 帧均为 0 字节（`gcRecorderValid=true`）；`FindObjectsOfType` 仍只在 30 帧节流点，未新增 per-frame 分配。
- **GPU p95**：`FrameTimingManager` 在编辑器 batchmode 返回 0 个 GPU 样本（启用 `PlayerSettings.enableFrameTimingStats` 后仍为 0），本环境无法给出 p95；需要 player build，按第 4 节性能项作为参考值/环境限制记录。
- **未自动化**：物体跨越 probe 边界的逐帧切换。per-object 选择保证同一物体只用一个代理，不会双重采样；仍需要专门序列帧证据覆盖“只在边界处切换、无黑边”。
- **证据**：`validation/P1-3/P13ProxyVerification.json` 与 18 张 PNG；单 probe 回归见 `validation/T11P13/T11Verification.json`。
- **结论**：P1-3 功能验收（双 probe 选择、相机移动稳定性、GC、无 NaN）通过；GPU p95 与物体边界序列帧保留为环境/后续项。

## 18. P1-3 player + RenderDoc `.rdc` 复核（2026-11-17）

编辑器截图只能证明 CPU 侧读数；本轮新增一条独立验证路径：**D3D11 standalone player 内用 RenderDoc in-app API 精确抓帧**，再用 renderdoc-mcp 读抓帧结果。

- **构建/运行**
  - `ScreenSpaceLightingP13PlayerSetup.CreateScene` 生成 `Scenes/ScreenSpaceLightingP13Player.unity`（Forward pipeline、T3 volume profile、方向光、天空盒、P1-3 Player 材质）。
  - `ScreenSpaceLightingP13PlayerSetup.BuildPlayer` 生成 1280x720 development player，仅启用 D3D11，输出到 `validation/P1-3/Player/`。
  - `ScreenSpaceLightingP13PlayerVerification` 在 player 内重建与编辑器 P1-3 相同的 rig：B probe 8³ 包围玻璃（importance 1），A probe 4³ 围绕相机（importance 0）；玻璃 `_Ior=1`、opacity 0、plane model，图案面板放在 layer 31 并排除出两个 probe 的 cubemap；probe cubemap 在 refraction 关闭时冻结渲染。
  - 运行命令：
    ```bash
    Unity.exe -batchmode -projectPath URPSample01 \
      -executeMethod ScreenSpaceLightingP13PlayerSetup.BuildPlayer
    renderdoccmd capture -w --opt-hook-children \
      -c <out>/manual -d <out> <out>/SslP13Player.exe -ssl-p13-out <out>
    ```
    player 通过 `renderdoc.dll!RENDERDOC_GetAPI(1.6.0)` 的 `TriggerCapture` 对 `CameraAtA`、`CameraMovedToB` 两个位姿 × `Both/ProbeAOnly/ProbeBOnly` 三种配置各抓一帧，共 6 个 `.rdc`；抓帧后同一配置下再截 PNG 并计算玻璃内圈指标。
- **结果**（`validation/P1-3/Player/P13PlayerReport.json`，`passed=true completed=true`）
  - `CameraAtA`：判定圈 1257 px；`Both` vs `ProbeAOnly` 平均色差 **0.468**、`bothToAChangedPixels=1257`（100%）；`Both` vs `ProbeBOnly` 平均色差 **0.0025**、差异 30 px（2.4%，全部位于球体边缘，见下）。
  - `CameraMovedToB`（相机前移 6 m）：判定圈 3745 px；`Both` vs `ProbeAOnly` 平均色差 **0.394**、差异 3745（100%）；`Both` vs `ProbeBOnly` 平均色差 **0**、差异 **0**。
  - 相机进入 A 的包围盒再移出，`Both` 始终跟随 B：说明 shader 按物体中心选择，而不是按相机最近的 A。
- **GPU 帧内证据**
  - 6 个抓帧均为 D3D11、147 events / 65 draws；玻璃球 draw 为 2304 indices（EID 786/783/785/831/835，随配置变化）。
  - `debug_pixel_*.json`：用 renderdoc-mcp `debug_pixel` 对每个抓帧的玻璃中心 (640,359) 调试像素 shader。`CameraAtA_Both` 输出 `(1,1,0,1)` 与 `ProbeBOnly` 完全一致，`ProbeAOnly` 为 `(0.138,0.119,0.104,1)`；`CameraMovedToB` 同样 `Both==ProbeBOnly`。这是 GPU 实际执行的 shader 输出，不是截图比较。
  - `ps_reflection_Both.json`、`ps_shader_Both.txt`：抓帧内 PS 有 5 个常量块（含 16384 B 全局块）。Unity player 剥离了 DXBC 反射变量名，renderdoc-mcp v0.3.0 发布包也尚未包含源码中的 cbuffer 工具，因此无法直接从 `.rdc` dump `_SslRefractionProbe*` 数组数值；代理表内容与“物体中心优先、包含者优先”的规则由 EditMode `RefractionProxyTableSelectsAtTheObjectCenter` 覆盖，`.rdc` 侧以像素输出等价性证明实际选择。
- **判定圈与边缘**：内圈取投影半径的 0.5 倍（A 位姿约 20 px）。球体边缘的 Fresnel/环境反射由 URP per-renderer `unity_SpecCube0` 决定，probe A 是否在场景中会改变该 cubemap 的图集绑定，因此边缘像素不属于代理表选择的判据；该耦合已在 T14 已知差异第 2 条记录（反射与折射可能用不同 probe）。0.5R 内 `Both` 与 `ProbeBOnly` 的余差 ≤ 2.4%（A 位姿）、0（B 位姿）。
- **产物**：`validation/P1-3/Player/` 下 6 个 `.rdc`、6 张 PNG、`P13PlayerReport.json`、`P13Player.log`、`P13PlayerBuildSummary.json`、6 份 `debug_pixel_*.json` 与 PS 反射/反汇编；`PLAN/TODO/05-ScreenSpaceRefractionImprove/tools/rdmcp.py` 为 stdio MCP 客户端（open_capture / debug_pixel / 任意工具调用）。
- **结论**：P1-3 的 per-object 代理选择与相机移动稳定性在独立 player 的 GPU 帧上复核通过。剩余项不变：GPU p95（player 端可后续补测）与物体跨越 probe 边界的序列帧。
