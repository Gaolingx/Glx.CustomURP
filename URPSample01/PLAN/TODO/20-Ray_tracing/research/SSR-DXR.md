# 不透明 SSR × DXR：从“屏幕未解决”到“世界空间补追”

**状态：计划，未实现、未测。** 只为高端合格平台的 opaque SSR 增加一个离屏来源；不触碰透明 SSR、折射、默认材质环境反射或 GI 启动策略。v1 不建 Surface Cache，命中着色分级见 §5。平台与 TLAS 见 [TLAS](TLAS.md)；HDRP 对照见 [HDRP14](HDRP14.md)；总验收见 [HANDOFF](../HANDOFF.md)。`U/` 表示本项目内嵌包 `URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/`；行号对该快照有效。

## 0. 现有 SSR 合同（已核实）

| 事实 | 证据 |
|---|---|
| Opaque SSR 在 `AfterRenderingPrePasses + 6`（206），早于材质绑定 207 与 GBuffer 210/Forward 250 | `U/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs:18–22` |
| Approximation 与 PBR 共用同一 trace/reproject；半分辨率 trace 后双边/深度法线上采样 | 同上 `200–265`；`U/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute:21–23` |
| Trace 只写命中 NDC，miss 写 `(99,0)`；命中点的亮度由 reproject 从上一帧 color pyramid 取 | 同上 `54,223–291` |
| Reproject 会做邻域填洞；最终输出是 **unpremultiplied radiance + weight** | 同上 `10–14,303–380` |
| 材质端 `lerp(environmentSpecular, radiance, weight)`，BRDF 只应用一次；clear coat 有独立层级；透明 SSR 是独立纹理/keyword | `U/ShaderLibrary/ScreenSpaceLighting/ScreenSpaceLightingBlend.hlsl:48–55,96–121` |

**关键约束**：不能从最终 alpha 或邻像素补出来的颜色反推原始 miss；否则会把“邻域填出来的有效颜色”再次送入 HW，造成重复与错轮廓。DXR 请求必须在原始 trace/reproject 状态上分类。

## 1. 分类：什么才算“可反射但未解决”

| 接收面状态 | 操作 |
|---|---|
| 背景/无效深度法线/超出支持粗糙度 | 不进入 HW 队列，沿用材质 Reflection Probe / Sky |
| 有可靠屏幕命中且回投成功 | 使用已有 SS 辐亮度；保留边缘/粗糙度规则。若旧边缘淡出故意弃用一部分屏幕信号，该份额可在预算内交 HW；不要把粗糙度不合格也当成洞 |
| 可反射，但出屏/被遮挡/历史无效 | 用**同一射线方向**、接收点和稳定 texel/sample 标识请求 DXR：Approximation 用镜向，PBR 用原 GGX 样本方向/种子 |
| 首帧仅缺颜色历史 | 只要当前深度/法线/材质输入有效，允许独立生成 HW 请求；但现有 pass 在 `readColorPyramid == null` 时整体返回（`U/.../SsrPass.cs:237–238`），实现必须把分类/派发放在该早退之前，只生成 HW 请求（SS 份额为 0）；当前几何输入缺失则回环境 |
| 命中受支持几何 | 返回线性 HDR、hit distance、来源与材质有效位；用命中点完整 PBR（albedo/normal/metallic/smoothness/emission）+ 灯表直接光 + 逐灯阴影算沿返回方向的 radiance；cutout 由 AHS 处理；不再乘接收面 Fresnel；不做 probe 反馈，不依赖 Surface Cache |
| 真正的环境 miss / 未服务 | 只有环境可见性可信的远处 miss 才允许直接用方向 sky；有限 TMax、未服务/无预算、无有效着色或数据过期均保持材质 Reflection Probe / Sky |

未入库的物体没有可恢复的轮廓；不宣称所有洞都能补。被屏幕步进当成有效命中的**假交点**不会自动触发 DXR，必须单测拒绝。

## 2. 请求、派发与合成

1. **请求缓冲**：在 206 的 SS trace/回投分类完成后、邻域填洞/聚合之前，把合格样本写入有界列表：sample slot、receiver P/N、direction、roughness/TMin/TMax、seed、材质/实例 id。记录 `requested/served/overflow`。用确定性空间分布/轮转避免边缘长期饿死。
2. **派发**：Unity 2022.3 没有 GPU 计数间接 `DispatchRays`。使用 CPU 已知宽度 `K` 的固定派发，raygen 读 GPU 有效数 `min(count,K)` 后早退；散写回原槽。`K=0` 时 CPU 不提交。**不是精确按 N 间接派发，也不保证满 wave**；先与同预算固定派发/掩码方案计时对比，不引入阻塞读回。
3. **命中着色**：用材质中与 `SetRayTracingShaderPass` 同名的 `GlxDXR` pass（CHS + AHS；不是 `LightMode="RayTracing"` tag）解完整 SurfaceData（BLAS 顶点属性 + per-material 贴图），cutout 走 AnyHit；CHS 用灯表算命中点直接光 + 逐灯阴影，不做 probe 反馈；输出与 [TLAS](TLAS.md) 的共同 ABI 一致。`LmaxHit/Smax/payload` 受 B0 预算约束。TMin 从原表面做 bias；不把 Hi-Z 走过的距离当世界空间安全距离。有限 TMax 的截断不等于天空可见。方法见 [HitShading](HitShading.md)。
4. **合成**：保持现有 unpremultiplied 合同。令 `s` 为原 SS 权重、`Lss` 为原 SS 辐亮度、`r` 为已解决 HW 份额、`Lhw` 为 HW 辐亮度、`Lenv` 为材质环境反射：
   - `a = s + (1 − s) · r`
   - `rgb = (s · Lss + (1 − s) · r · Lhw) / a`，`a=0` 时 RGB 写零
   - 材质仍执行 `lerp(Lenv, rgb, a)`，得到 `(1−a)Lenv + sLss + (1−s)rLhw`
   - 这样旧非预乘 SSR 合同、点采样、强度与 clear-coat 各 lobe 的 BRDF/环境权重仍只应用一次；HW 不再乘一次 SS 屏幕边缘淡出，否则离屏洞会重新出现。
5. **历史**：保留 SSR 自己的 motion、粗糙度、深度/法线 history；HW 距离及分类只用于历史拒绝。SS→HW 来源变化且距离/亮度不一致时降历史权重；相机切换、尺寸/后端切换要失效；禁止回灌已空间模糊的历史。205 的 GI history 不能借给 206。SS 读历史 scene color，HW 命中着色较简单，观察高亮物体出屏会不会从“空洞”变成“亮度台阶”。

## 3. 帧序与落点

| 顺序 | 内容 | 约束 |
|---|---|---|
| 201 | 共享 RT 场景服务构建/复用 TLAS | `AfterRenderingPrePasses+1`；与 DBuffer、非 Deferred 的 ScreenSpaceShadows 共用 201，按入队顺序执行；TLAS 不读它们的输出，只须先于首个 RT 消费者就绪。202 是内置 early motion，203 是 Hi-Z 与 Editor 相机 motion（见[时序与源码](report.md#21-pass-顺序)）；相机无关；失败则本帧所有 RT 消费者回原路径 |
| 206a | SS trace + reproject + 原始样本分类/请求列表 | 必须保留原始状态，不读最终 alpha |
| 206b | 固定 K 的 `DispatchRays` | raygen 早退；不阻塞读回 |
| 206c | HW 结果合成到 SSR 输出纹理 | 仍为 206 内、207 绑定之前 |
| 207 | `_SCREEN_SPACE_SSR` 绑定 | 原有材质端只消费一次 |
| 210/250 | Deferred GBuffer / Forward opaque 消费 | 三种 renderer 共用权重合同 |

实现落点建议：`SsrPass.cs` 增加可选子阶段；`ScreenSpaceReflections.compute` 增加请求分类 kernel；新增 `RayTracingSsr.raytrace` 与材质命中 pass；合成 shader 保持 unpremultiplied + weight。测试放 `URPSample01/Assets/Tests/RayTracing/SSR/`。基础设施不得引用 SSR Volume；SSR 单独开启时不得启动 probe 更新。

## 4. 预算与已知上限

- B0 先冻结：`K`（按分辨率/质量档）、TMax、最小可反射粗糙度、每像素/每 tile 上限、溢出策略、HW 亮度与 SS 的容许阶跃；命中侧另冻结 `LmaxHit/Smax/payload/variant`。未定预算不报性能通过。
- 固定派发会浪费未服务线程；以 GPU 早退和确定性槽位控制成本，不把“零命中”当成“没派发”。证据必须同时有 DXR marker 与命中/未服务计数。
- 已知上限：屏幕假命中不会触发 DXR；透明/ShaderGraph/蒙皮/未入库材质、灯表不应用 light layer mask/cookie/面光源、预算不足、镜中镜和有限距离仍可能产生错反射；cutout 由 AHS 支持。超过预算时按模块回退，不为补洞默认开启多跳或扩大平台范围。

## 5. 无 Surface Cache 的命中着色分级（v1 recipe）

v1 不建 Surface Cache、不做 mesh cards/atlas、不做多跳。屏幕命中沿用现有 SS 取色/历史规则；世界空间命中用 `GlxDXR` 的 **Fat CHS**：BLAS 顶点属性 + per-material 贴图解完整 SurfaceData，AHS 处理 cutout，CHS 算命中点 PBR + 灯表直接光 + 逐灯阴影；不做 probe 反馈，不乘接收面 Fresnel。GI 的低频由 probe 提供，不是 Surface Cache。

| 层级 | 方案 | 采用条件 | 边界 |
|---|---|---|---|
| H-A（v1） | Fat CHS + per-material pass：完整 SurfaceData + AHS cutout + 灯表/逐灯阴影 | 默认 | 只支持登记不透明 Lit/cutout/自发光；每材质一个命名 DXR pass；ShaderGraph/透明/蒙皮另计；灯光/阴影受 B0 预算 |
| H-B（v1.5 候选） | Thin CHS + 共享材质表（Surface-Cache-lite 数据面） | H-A 的 pass/variant 或 hit shading 成本成为瓶颈，或需要统一 BSDF/多跳/ReSTIR | 仍无 bindless；材质表/复杂材质另证 |
| H-C（v2 候选） | 完整 Surface Cache | 需要广材质覆盖/多跳/稳定降噪，且接受 mesh cards/atlas 系统工程 | 不是 v1 承诺 |

2022.3 没有 GPU 间接 `DispatchRays`：v1 固定 K + GPU 早退，不引入阻塞读回。压缩/TMin 续传不进 v1；作为 R2b 实验，必须用隐藏遮挡物证明不漏几何，且不影响 R1–R5。

## 6. 独立放行条件

| ID / 前置 | 单模块实验（关闭 GI） | 通过条件 / 证据 |
|---|---|---|
| R1 分类与预算 / B0 | 注入可反射/无效、命中/历史拒绝、边缘、零预算与溢出的确定性图案；测试方向重建、半分辨率奇数宽高 | 计数守恒、无重复/越界回写；只调度合格份额；延迟读回对照原坐标/方向；预算为零回原环境；无历史依然可分类。记录 requested/served/overflow |
| R2 离屏求交与取色 / R1+T3 | 光滑镜面和有 ID 的离屏纯色发光板，再加遮挡/移除/超 TMax；摄像机内外两组 | 已服务射线命中正确实例、距离与颜色符合解析解；留出未服务负样本，不能误标为 sky 或黑色有效；DXR 实际派发和 marker 可见，SS-only 无法恢复的轮廓在混合模式恢复 |
| R3 重建与回归 / R2 | 锁曝光/种子，让物体连续出入画；比较 SS-only、Hybrid 和同材质高采样 RT 参考；切镜、移动遮挡、粗糙边界、clear-coat、三种 renderer 和透明控制组 | 同一表面 ROI 统计参考轮廓召回率、错误轮廓率、源切换亮度峰值、历史拖尾帧和每 pass GPU ms，按 B0 预定阈值判定；不能用非黑像素率替代。关闭扩展和透明控制组与原图相同；clear-coat 不重复增亮，GI 更新计数为 0 |
| R4 命中着色分级与材质覆盖 / R2+T3b | H-A 登记 Lit/cutout/自发光；SimpleLit/ComplexLit/ShaderGraph/透明/蒙皮/多 submesh 另计；统计 pass/variant、payload、hit shading GPU ms | 只有登记子集参与；不支持材质显式计数，不静默当黑色/sky；无 Surface Cache/mesh card 资源；不重复接收面 Fresnel/BRDF；H-A 开启/关闭不改变 R1–R3 的正确性门槛；H-B 实现后再加入对照 |
| R5 全材质反射与灯光预算 / R2+T3b+T4 | 命中点 PBR（metallic/smoothness/normal）+ cutout + 方向/点/聚光 + 逐灯阴影；与 SS-only/高采样参考比轮廓与亮度接缝；灯数/阴影/payload/overflow 计数 | 命中点材质与解析参考一致到 B0；cutout 轮廓正确；灯数/阴影在预算内；不做 probe 反馈；超预算回退并计数 |
| R2b 压缩与 TMin 续传（v2 候选，不阻塞 v1） / R2 | 构造大量未解决射线与“屏外隐藏遮挡物”；对比固定 K、GPU 压缩、上一帧 N 估计 K、TMin 续传；记录 N/K、wave 利用率和 GPU ms | 隐藏遮挡物在 TMin 方案下不得漏遮挡；结果确定、可复跑；只有证明收益超过压缩/重追成本才建议进入 v2；任一失败只记录为 v2 NO-GO，不影响 R1–R5 |

证据分 R1 计数/延迟读回、R2 解析交点与颜色、R3 图像与性能、R4 材质覆盖/着色成本、R5 全材质/灯光预算、R2b 压缩/续传 A/B；不能拿调度计数代替画面证据。R2b 未通过不影响 R1–R5；R4/R5 不通过只收窄材质/灯光范围，保留现有 SSR。
