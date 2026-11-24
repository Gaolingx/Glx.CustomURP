# 命中点着色：无 bindless / 无 inline RayQuery 的全材质 Fat CHS

**状态：调研与设计，未实现、未测。** 范围：Unity 2022.3 / URP 14 的 `RayTracingShader` + 每材质命名 DXR pass，回答“命中任意登记材质后如何取贴图/法线/粗糙度/金属度/自发光并做 alpha 裁剪”，以及 GI probe 与 SSR 两条消费者如何共享这套 ABI。总计划见 [HANDOFF](../HANDOFF.md)，探针见 [GI HANDOFF](../gi/HANDOFF.md)，反射见 [反射 HANDOFF](../reflection/HANDOFF.md)。

**引用等级**：**A** = 本机源码或安装包；**B** = Unity/NVIDIA 官方文档；**C** = 第三方/演讲。源码行号只对当前快照有效。

## 0. 结论先行

1. **v1 采用 Fat CHS + 每材质命名 pass**：CHS 内完成材质解包、直接光、逐灯阴影与（仅 GI 的）上一帧 probe 反馈；raygen 只负责发射、模式选择与写回。**不需要 bindless、顶点池、材质表或 texture atlas。**
2. **几何来自 BLAS**：`UnityRayTracingFetchVertexAttribute3/4` 直接取 position/normal/tangent/UV（`D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Data/CGIncludes/UnityRayTracingMeshUtils.cginc:48–52,220,274`），不需要把 mesh 烘进大 buffer。
3. **贴图来自材质自身**：per-material pass 绑定 `_BaseMap/_BumpMap/_MetallicGlossMap/_EmissionMap`，分辨率与压缩格式不受 atlas 约束。
4. **alpha test 走 AnyHit**：`_ALPHATEST_ON` + `IgnoreHit()`；透明混合仍不支持。
5. **代价是覆盖与变体**：每材质一个 DXR pass，pass/variant 随材质与 keyword 增长；URP ShaderGraph 没有 DXR target，v1 不承诺；SkinnedMesh 需按版本/设置验证。
6. **不是 Thin CHS**：HDRP 的真实结构是 **Fat CHS + 瘦 raygen 循环**（`Runtime/RenderPipeline/PathTracing/Shaders/PathTracingIntegrator.hlsl:339–385`、`PathTracingMain.raytrace:189–229`），不是“Thin CHS + Fat RayGen”。Thin CHS 需要统一材质表，留给 H-B。

## 1. 为什么不选 Thin CHS + Fat RayGen

“Thin CHS 只回传材质，raygen 统一算光照”听起来更干净，但它要求 raygen 能用**同一种 BSDF** 处理任意材质；URP Lit 的 clear coat、SimpleLit、ShaderGraph 自定义节点都做不到。要补这个缝，就得建“材质表 + 统一 BRDF 分支”，也就是 H-B。

HDRP 的选择相反：CHS 里 `ComputeSurfaceScattering` 建 `MaterialData`、`SampleLights`、`EvaluateMaterial`，并设置下一跳 continuation ray（`PathTracingIntegrator.hlsl:78,162–164,242–254,339–385`）；raygen 的 `TracePath` 只循环 `TraceRay` 和累加（`PathTracingMain.raytrace:189–229`）。**材质与直接光留在 CHS**，raygen 不知道材质细节。

本项目的取舍：

- **v1 = Fat CHS**：材质正确、改动最小、与 URP 现有 `SurfaceData` 同构；代价是每材质 pass。
- **H-B = Thin CHS + 材质表**：只有在需要 ReSTIR 重采样、多跳统一 BSDF 或 pass/variant 成为瓶颈时才做。
- **H-C = Surface Cache**：更远的目标，不是 v1 承诺。

## 2. 2022.3 可用件（已核实）

| 能力 | 证据 | 结论 |
|---|---|---|
| `RayTracingShader`（raygen/closest-hit/any-hit/miss） | `UnityEngine.CoreModule.xml:11969`；HDRP 的 `PathTracingDXR` pass | 2022.3 可用 |
| `CommandBuffer.SetRayTracingShaderPass(shader, passName)` | 同上 `:38147` | 按材质 pass 名选择 CHS/AHS |
| `CommandBuffer.DispatchRays(..., width, height, depth, camera)` | 同上 `:35509` | CPU 固定宽度；无间接派发 |
| `UnityRayTracingFetchVertexAttribute3/4` | `UnityRayTracingMeshUtils.cginc:48–52,220,274` | 从 BLAS 取顶点属性，无顶点池 |
| per-material 贴图与常量 | URP `Shaders/Lit.shader:8,11,13,16,17,26,34,35,51` | `_BaseMap/_BumpMap/_MetallicGlossMap/_EmissionMap/_Smoothness/_Metallic/_EmissionColor/_Cutoff` |
| `_ALPHATEST_ON` | 同上 `:135,218,274,349,395,453,501,539` | Forward/GBuffer/ShadowCaster 已有该 keyword；DXR pass 需自行声明并处理 |
| 嵌套 `TraceRay`（CHS 内打阴影/下一跳） | HDRP `PathTracingIntegrator.hlsl:213`；`.raytrace` 的 `#pragma max_recursion_depth` | 需要显式递归深度；v1 只用于逐灯阴影 |
| 统一 payload + segment/mode | HDRP `PathTracingPayload.hlsl:22–56`；AHS 用 `segmentID` 区分 nearest-hit/random-walk（`PathTracingIntegrator.hlsl:387–440`） | 不需要为阴影单独做一套 pass |

**URP Lit 目前没有任何 DXR pass**：pass 名只有 `ForwardLit / ShadowCaster / GBuffer / DepthOnly / DepthNormals / TransparentDepthNormals / Meta / Universal2D`（`U/Shaders/Lit.shader:107,195,247,327,371,429,478,517`）。因此必须新增一个命名 pass，而不是寻找现成 tag。

## 3. URP Lit 的 `GlxDXR` pass recipe

### 3.1 pass 结构

```hlsl
Pass
{
    Name "GlxDXR"
    Tags { "LightMode" = "GlxDXR" }          // 名称与 C# SetRayTracingShaderPass 一致，不是 "RayTracing"

    HLSLPROGRAM
    #pragma raytracing                       // 或参考实现用的 #pragma raytracing test
    #pragma only_renderers d3d11 xboxseries ps5   // 与 HDRP 的 DXR pass 同口径；不宣称其它后端
    #pragma shader_feature_local _NORMALMAP
    #pragma shader_feature_local _METALLICSPECGLOSSMAP
    #pragma shader_feature_local _SPECGLOSSMAP
    #pragma shader_feature_local _EMISSION
    #pragma shader_feature_local _ALPHATEST_ON
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
    // CHS + AHS
    ENDHLSL
}
```

- pass 名是自由的，但 C# 必须用同名 `SetRayTracingShaderPass`；HDRP 用 `GBufferDXR/IndirectDXR/PathTracingDXR` 等，不是 `LightMode="RayTracing"`。
- 参考实现用 `#pragma raytracing test` 通过编译（`E:/workspace/GitHub/RTXGI-DDGI-URP/Assets/Features/Resources/Shaders/LitRayTracing.shader:127–132`）；HDRP 用 `#pragma raytracing surface_shader` + `#pragma only_renderers d3d11 xboxseries ps5`（`Lit.shader:1255–1261`）。本项目先按 HDRP 口径验证；若 URP 编译/平台门控不成立，退回参考实现的 `#pragma raytracing test` 并记录，不宣称其它后端。

### 3.2 材质解包

在 CHS 内：

1. `GetCurrentIntersectionVertex` 取 positionOS/normalOS/tangentOS/uv（HDRP 的做法见 `RaytracingIntersection.hlsl:97–140`）。
2. 变换到世界空间，建 tangent frame；用 `_BumpMap` 解 normalTS 并变换到世界空间。
3. `InitializeStandardLitSurfaceData(uv, surfaceData)` 复用 URP Lit 的材质解包：albedo、alpha、metallic、smoothness、normalTS、emission。
4. 输出 payload（§3.4）；不乘接收面 Fresnel，不在 CHS 里重复接收面的 BRDF。

**不要**把 `BakedGI/Lightmap/ShadowMask` 带进 DXR pass；命中点的光照只来自灯表、自发光与（GI 的）probe 反馈。

### 3.3 AnyHit 与 alpha test

- `_ALPHATEST_ON` 时，AHS 重解 `SurfaceData`，`if (alpha < _Cutoff) IgnoreHit();`。
- HDRP 的 AHS 就是 `_ALPHATEST_ON` + `IgnoreHit()`（`PathTracingIntegrator.hlsl:387–417`），可作同构参考。
- v1 只支持 **cutout**（Lit + `_ALPHATEST_ON`）；透明混合、alpha-to-coverage、折射不在范围。
- AHS 同时处理阴影遮挡：统一 payload 用 `mode/segment` 区分主射线与阴影射线；阴影射线命中不透明表面后置 occluded 并结束搜索。HDRP 用同一 `PathPayload` + `segmentID`（`PathTracingIntegrator.hlsl:387–440`），不需要第二套材质 pass。
- `_ALPHATEST_ON` 控制 AHS 内的裁剪逻辑，不控制遍历是否调用 AHS。cutout 必须按 [TLAS 的子网格映射](TLAS.md) 登记；不透明几何以 `ClosestHitOnly` 跳过 AHS。阴影命中进入 CHS 时先检查 mode，置 occluded 后立即返回，不再解材质/算光照。

### 3.4 payload 与两种消费者

**CHS 局部数据**：位置、UV、切线框架、`worldNormal` 和 `SurfaceData` 留在命中着色器内。probe 用法线、albedo、emission 求漫反射与反馈；反射还用 metallic/smoothness 求命中点 BRDF。两者都在 CHS 算完 radiance，不把材质参数回传给 raygen。

**跨阶段 payload**：一个材质 pass 共用同一布局；raygen 或发起阴影追踪的 CHS 写入 `mode`，调用方只消费以下返回值：

| 返回字段 | probe 主射线 | 反射主射线 | 阴影射线 |
|---|---|---|---|
| `radiance` | 直接光 + emission + feedback | 命中点出射辐亮度 | 不读 |
| `hitT` | 距离矩、探针维护 | 历史拒绝 | 不读 |
| `flags` | 有效性、背面、无效材质等状态 | 命中/未解决原因 | 遮挡位 |

`instanceId/materialId` 仅供验证变体读回，不属于生产 payload。

- **GI probe 模式**：CHS 算 Lambert 漫反射 + emission + 上一帧 probe 反馈（§5），不需要 roughness/metallic。
- **反射模式**：CHS 用命中点完整 PBR（albedo/metallic/smoothness/normal）算沿 `-WorldRayDirection()` 的出射辐亮度；不做 probe 反馈，不乘接收面 Fresnel。
- payload 越小寄存器压力越低。v1 目标是 **≤64B/命中**（不含 debug 字段）；HDRP 的 `PathPayload` 因 PT continuation/AOV 更大，不能照抄。B0 冻结布局并记录寄存器/占用。

### 3.5 背面与 ray flags

- **probe 射线不裁背面**：relocation/classification 需要背向命中（距离为负/单独 flag）判断 probe 是否卡进几何。
- probe 命中背面应在材质/灯光早退前写 `hitT` 和 backface 标志；反射可裁背面，或按既定策略记为无效。`AddInstance` 的 `enableTriangleCulling` 默认为 true；实例设置与 `RAY_FLAG_CULL_BACK_FACING_TRIANGLES` 的组合以 [T2 正反面 A/B](TLAS.md) 的 Player 结果为准，不能假设改一个 flag 就能满足两个消费者。
- 阴影射线：alpha test 由 AHS 处理；命中即遮挡。TMin 用表面 bias，方向光 TMax 无限，点/聚光 TMax 为到灯距离。

## 4. 命中点光照：灯表、逐灯阴影与预算

### 4.1 灯表

- **场景级灯表**，不是相机 `visibleLights`：射线可能打到屏幕外/相机后方；只依赖当前相机光照会漏灯。
- 收录 **Directional / Point / Spot** 的实时灯；排除 Baked、Area/Rect/Disc；v1 不做 cookie、面光源、SSS、clear coat 的命中点求值。
- 参考实现每帧 `FindObjectsOfType<Light>()`（`RTXGI-DDGI-URP/.../DDGIFeature.cs:752`）并把衰减烘到 CPU buffer；那是 demo 成本。本项目用**注册/脏标记**维护灯表，只在灯集合或参数变化时上传；移动灯按帧更新。
- CPU 登记全部符合上述条件的灯；`Lmax` 仅限制上传的 GPU 表，不限制登记数量。B0 冻结容量、排序和同分规则；超额时按该顺序截断，记录 `tableOverflow = registered − uploaded`。

### 4.2 逐灯阴影

- 候选仅来自 GPU 表中影响命中点的灯。预算充足时全部参与且逐灯追阴影；点/聚光 TMax 到灯距离，方向光无限。
- B0 冻结 `LmaxHit` 和 `Smax`。超额时按影响选至多 `LmaxHit` 盏，未选的灯不参与；选中灯最多发 `Smax` 条阴影射线，未获阴影预算的按既定无阴影降级处理。分别记录未选灯数、未获阴影预算灯数与实际 `shadowRays`，不与上传阶段的 `tableOverflow` 混计。
- 阴影射线与主射线共用材质 pass 的 AHS，所以 cutout 叶子会正确遮挡；透明/未入库几何不会。
- 不使用 Unity 的 shadow map 结果；RT 阴影与光栅阴影是两套数据，切换时可能出现亮度差，必须 A/B。

### 4.3 成本

```
主射线 ≤ B×R + K（每个 active probe 的 R 已含前 32 条固定射线；inactive 只追 32）
阴影射线 = 主射线 × 命中灯数（≤ Smax）
反馈采样 = GI 主射线命中数 × 8 邻居（仅 GI）
```

B0 必须先冻结这四项；`LmaxHit/Smax` 与 `B×R/K` 一起决定帧时间，不能用“画面看起来对”替代预算。

## 5. GI probe 的 1 跳反馈

- 概念：probe 射线命中点取 **上一帧已发布** 的 irradiance atlas，按命中点 P/N 做 8 邻居三线性 × wrap shading × Chebyshev，乘 `min(albedo, cap) * Lambert()` 加到该射线的 radiance。
- 参考实现：`radiance += (min(albedo,0.9) * Lambert()) * SampleDDGIIrradiance(P, N, worldRayDirection)`（`RTXGI-DDGI-URP/.../DDGIRayTracing.raytrace:114–115`）。它读 `_ProbeIrradianceHistory`，不是正在写的 atlas。
- **不要**在 CHS 里递归 `TraceRay` 做多跳；反馈只做一次纹理查询。由于探针分批轮转，这一项跨帧迭代，**不是严格的“同帧 1 跳”**；测试要按阶跃响应与衰减判定，而不是只测一帧。
- 能量：沿用 `Pgi = Eprobe/π` 与材质端 `albedo/π` 只应用一次；反馈项是命中点自身的出射漫反射，不再额外乘接收面 albedo。
- 失效：首帧/无效/tile 未发布/relocated/inactive 邻居按 0 处理并重新归一化；不能把“无效”当黑色证据，也不能让旧反馈永久残留。
- relocation/classification 默认开；反馈查询与消费者一样使用 `grid + offset` 并跳过 inactive，不能用未偏移的网格位置采样。
- 验收：灯光开/关阶跃、feedback on/off、与高采样参考的 ROI、移动发光体、薄墙、relocation/classification 邻居；见 G5。

## 6. 覆盖、成本与限制

| 材质/几何 | v1 | 说明 |
|---|---|---|
| URP Lit 不透明（metallic 流） | 支持 | 主要目标；per-material pass |
| URP Lit cutout | 支持 | AHS + `_ALPHATEST_ON` |
| URP Lit specular 流 | 候选 | 需 `_SPECGLOSSMAP/_SPECULAR_SETUP` 分支，R4 统计 |
| ComplexLit（clear coat） | 候选 | 命中点 clear coat BRDF 另证；不在 v1 承诺 |
| SimpleLit | 候选 | 不同 SurfaceData；先计数 |
| 仅自发光登记材质 | 支持 | emission 作为唯一 radiance |
| 透明/折射/alpha-to-coverage | 不支持 | 无混合路径 |
| URP ShaderGraph | 不支持 | URP 没有 DXR target/pass；需自定义 subgraph/pass，另立项 |
| SkinnedMesh | 条件支持 | RTAS 可含 SMR，但文档将 vertex animation 列为限制；先排除并单测 |
| 多 submesh / LOD | 逐材质 | 缺少 pass 的 submesh 显式计数，不静默当黑 |
| Terrain/Decal/Particles | 不支持 | 不登记 |

**pass/variant 爆炸**是主要维护风险：`_NORMALMAP/_METALLICSPECGLOSSMAP/_SPECGLOSSMAP/_EMISSION/_ALPHATEST_ON` 组合会放大变体；DXR pass 只声明命中着色真正需要的 keyword，B0 冻结 variant 上限并记录实际数量。

## 7. 验收与证据

| ID | 内容 | 通过标准 |
|---|---|---|
| T3b | `GlxDXR` pass、CHS/AHS、payload、顶点属性 | 已知射线/解析交点；贴图/法线/alpha 与光栅一致到 B0 容差；payload 读回符合布局；不支持材质显式计数 |
| T4 | 灯表与逐灯阴影 | 全灯/选择/溢出计数；阴影开关影响正确；移动/删除灯更新；不与 `visibleLights` 绑定 |
| G5 | probe 反馈 | 阶跃/衰减、ROI、feedback on/off、无效邻居 |
| R5 | 反射全材质 + 灯光预算 | 命中点 PBR、粗糙/金属、alpha cutout、灯数/阴影预算与 SS/HW 接缝 |
| R4 | 材质覆盖 | pass/variant、GPU ms、supported/unsupported 清单 |

证据必须同时记录：payload 字节数、pass/variant 数、每次派发灯数/阴影射线条数、supported/unsupported 材质、GPU ms、alpha test A/B、feedback/relocation/classification 开关。计数为零或画面“看起来对”都不能单独证明命中着色生效。

## 8. 易错点与证据

| 易错说法 | 实际 | 证据 |
|---|---|---|
| HDRP 是 Thin CHS + Fat RayGen | 是 **Fat CHS + 瘦 raygen 循环** | `PathTracingIntegrator.hlsl:78,162–164,339–385`；`PathTracingMain.raytrace:189–229` |
| payload 48–64B 是 HDRP 的量级 | HDRP `PathPayload` 字段更多（PT/AOV/continuation），约百字节量级；48–64B 是我们自己的目标 | `PathTracingPayload.hlsl:22–56` |
| ShaderGraph 原生兼容 | 只有 HDRP 有 RT target 且有限制；URP 没有 DXR target/pass | HDRP `Ray-Tracing-Getting-Started.md:288–293`；URP Lit pass 列表 |
| SkinnedMesh 无条件兼容 | 有条件；RTAS 可含 SMR，但 vertex animation 是文档限制 | `HDRaytracingManager.cs:377`；HDRP 文档 `:273` |
| SBT 只慢 10–20% | 无出处；hit-group 分歧、variant、payload 寄存器压力都可能反转 | 需本项目实测 |
| “完全不需要 per-material pass” | 不成立；SBT 的前提就是每材质 CHS/AHS，省的是 bindless/内容改造 | HDRP `Lit.shader:1255`；Unity `SetRayTracingShaderPass` |

## 9. 参考

- Unity 安装包：`D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Data/CGIncludes/UnityRayTracingMeshUtils.cginc:48–52,220,274`。
- Unity API：`UnityEngine.CoreModule.xml:11969`（RayTracingShader）、`:38147`（SetRayTracingShaderPass）、`:35509`（DispatchRays）、`:35216`（BuildRayTracingAccelerationStructure）。
- URP Lit：`URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Lit.shader`（属性 8–51；pass 107–517；`_ALPHATEST_ON` 135/218/274/349/395/453/501/539）。
- HDRP 14 path tracing：`Runtime/RenderPipeline/PathTracing/Shaders/PathTracingMain.raytrace:189–229`；`PathTracingIntegrator.hlsl:78,162–164,339–439`；`PathTracingPayload.hlsl:22–56`；`Runtime/RenderPipeline/Raytracing/Shaders/RaytracingIntersection.hlsl:97–140`；`Runtime/Material/Lit/Lit.shader:1255–1261`。
- 参考实现：`E:/workspace/GitHub/RTXGI-DDGI-URP/Assets/Features/Resources/Shaders/LitRayTracing.shader:105–194`；`DDGIRayTracing.raytrace:63–118`；`DDGIFeature.cs:752,309–340,472–486`。
- 外部：Microsoft DXR 1.1 payload/attribute 配置；Unity `SetShaderPass` 文档（pass 可含 closesthit/anyhit）。
