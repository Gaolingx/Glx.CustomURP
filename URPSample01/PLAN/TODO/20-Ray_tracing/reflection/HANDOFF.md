# 不透明 SSR × DXR：恢复离屏几何的单次反射

**状态：计划，未实现。** 只为高端合格平台的 opaque SSR 加一个离屏来源；不触碰透明 SSR、折射、默认材质环境反射或 GI 启动策略。平台、世界空间光照与材质入库见 [TLAS](../research/TLAS.md)；命中着色见 [HitShading](../research/HitShading.md)；HDRP 对比见[研究](../research/HDRP14.md)；集成细节见 [SSR-DXR](../research/SSR-DXR.md)。

## 输入与分支

| 状态 | 操作 |
|---|---|
| 接收面无效/背景/超出支持粗糙度 | 不进入 HW 队列，沿用既有材质反射 |
| 有可靠屏幕命中且回投成功 | 使用已有 SS 辐亮度；保留已有边缘和粗糙度规则。若旧边缘淡出故意弃用一部分屏幕信号，该份额在预算内可交 HW，不要把粗糙度不合格也当成洞 |
| 可反射，但出屏/被遮挡/历史无效 | 使用**同一射线方向**、接收点和稳定的 texel/sample 标识请求 DXR：光滑 Approximation 用镜向，PBR 用原 GGX 样本方向/种子。首帧仅缺颜色历史时，允许从当前深度/法线独立生成 HW 请求，但现有 pass 在 `readColorPyramid == null` 时整体返回（`SsrPass.cs:237–238`），因此必须把分类/派发放到该早退之前；当前几何输入缺失则回环境 |
| 命中受支持几何 | 返回线性 HDR、hit distance、来源与材质有效位。用命中点完整 PBR（albedo/normal/metallic/smoothness/emission）+ 灯表直接光 + 逐灯阴影算沿返回方向的 radiance；cutout 由 AHS 处理；不乘接收面 Fresnel，不做 probe 反馈，不依赖 Surface Cache |
| 真正的环境 miss / 未解决 | 只有环境可见性可信的远处 miss 才允许直接用方向 sky；有限 TMax、未服务/无预算、无有效着色或数据过期均保持材质 Reflection Probe / Sky。未入库的物体没有可恢复的轮廓，不宣称所有洞都能补 |

## 调度、重建和绑定

- 在 206 内 SS trace/回投分类完成后，邻域填洞/聚合**之前**保留原始样本状态，不从最终 alpha 或邻像素补出来的颜色倒推。请求列表必须记录 sample slot、接收面 P/N、方向、粗糙度、TMin/TMax、seed 与材质/实例 ID，并有界、可计数。v1 先做光滑单射线；PBR 支持须另证重建权重，不冒充完整粗糙镜面支持。连续像素/采样槽记录请求，预算上限 `K`，将合格请求压入有界列表并记录溢出；以确定性空间分布/轮转避免边缘长期饿死。
- Unity 2022.3 使用 CPU 已知宽度 `K` 的直接 DispatchRays，raygen 根据 GPU 有效数 `min(count,K)` 早退，散写回原槽；`K=0` 时 CPU 不提交。**并非精确按 N 间接派发，也不保证满 wave**；先与同预算固定派发/掩码方案计时对比；不引入阻塞读回。TMin 从原表面做 bias；不把 Hi-Z 走过的距离当世界空间安全距离。
- 命中着色使用材质中与 `SetRayTracingShaderPass` 同名的 `GlxDXR` pass（CHS + AHS，不是 `LightMode="RayTracing"` tag）与共同 ABI：BLAS 顶点属性、per-material 贴图、完整 SurfaceData；cutout 走 AnyHit；只对进入 TLAS 的登记不透明子集宣称可恢复。未入库材质/透明/ShaderGraph/蒙皮不能靠“有 DXR”自动补洞。每命中灯数 `LmaxHit`、阴影射线 `Smax`、payload 字节与 variant 数在 B0 冻结。
- 灯表来自共享场景服务，不使用相机 `visibleLights`；灯数/阴影预算与 GI 共用同一 ABI。移动/删除灯更新 buffer，不重建几何；不应用 Unity light layer mask、cookie 或面光源。
- 合成目标：`s·Lss + (1−s)·[r·Lhw + (1−r)·Lenv]`（强度 1）。对材质导出 `a=s+(1−s)r`、`rgb=(s·Lss+(1−s)r·Lhw)/a`；`a=0` 时 RGB 写零。这样旧非预乘 SSR 合同、点采样、强度和 clear-coat 各 lobe 的 BRDF/环境权重仍只应用一次。`r` 仅表示已解决 HW 份额；HW 不再乘一次 SS 屏幕边缘淡出，否则离屏洞会重新出现。
- 保留 SSR 自己的 motion、粗糙度、深度/法线 history；HW 距离及分类用于历史拒绝。SS→HW 来源变化且距离/亮度不一致时降历史权重；相机切换、尺寸/后端切换要失效；禁止回灌已空间模糊的历史。205 的 GI history 不能借给 206。SS 读历史 scene color，HW 命中着色较简单，观察高亮物体出屏会不会从“空洞”变成“亮度台阶”。

## 无 Surface Cache 的命中着色分级（v1 recipe）

v1 不建 Surface Cache。屏幕命中沿用现有 SS 取色/历史规则；世界空间命中用 `GlxDXR` 的 **Fat CHS**：BLAS 顶点属性 + per-material 贴图解出完整 SurfaceData，AHS 处理 cutout，CHS 算命中点 PBR + 灯表直接光 + 逐灯阴影；不做 probe 反馈，不乘接收面 Fresnel。GI 用 probe，不是 Surface Cache。

| 层级 | 方案 | 采用条件 | 边界 |
|---|---|---|---|
| H-A（v1） | Fat CHS + per-material pass：完整 SurfaceData + AHS cutout + 灯表/逐灯阴影 | 默认 | 只支持登记不透明 Lit/cutout/自发光；每材质一个命名 DXR pass；ShaderGraph/透明/蒙皮另计；灯光/阴影受 B0 预算 |
| H-B（v1.5 候选） | Thin CHS + 共享材质表（Surface-Cache-lite 数据面） | H-A 的 pass/variant 或 hit shading 成本成为瓶颈，或需要统一 BSDF/多跳/ReSTIR | 仍无 bindless；材质表/复杂材质另证 |
| H-C（v2 候选） | 完整 Surface Cache | 需要广材质覆盖/多跳/稳定降噪，且接受 mesh cards/atlas 系统工程 | 不是 v1 承诺 |

2022.3 没有 GPU 间接 `DispatchRays`：v1 固定 K + GPU 早退，不引入阻塞读回。压缩/TMin 续传不进 v1；作为 R2b 实验，必须用隐藏遮挡物证明不漏几何，且不影响 R1–R5 的通过。

## 独立放行条件

| ID / 前置 | 单模块实验（关闭 GI） | 通过条件 / 证据 |
|---|---|---|
| R1 分类与预算 / B0 | 注入可反射/无效、命中/历史拒绝、边缘、零预算与溢出的确定性图案；测试方向重建、半分辨率奇数宽高 | 计数守恒、无重复/越界回写；只调度合格份额；延迟读回对照原坐标/方向；预算为零回原环境；无历史依然可分类。记录 requested/served/overflow |
| R2 离屏求交与取色 / R1+T3 | 光滑镜面和有 ID 的离屏纯色发光板，再加遮挡/移除/超 TMax；摄像机内外两组 | 已服务射线命中正确实例、距离与颜色符合解析解；留出未服务负样本，不能误标为 sky 或黑色有效；DXR 实际派发和 marker 可见，SS-only 无法恢复的轮廓在混合模式恢复 |
| R3 重建与回归 / R2 | 锁曝光/种子，让物体连续出入画；比较 SS-only、Hybrid 和同材质高采样 RT 参考；切镜、移动遮挡、粗糙边界、clear-coat、三种 renderer 和透明控制组 | 同一表面 ROI 统计参考轮廓召回率、错误轮廓率、源切换亮度峰值、历史拖尾帧和每 pass GPU ms，按 B0 预定阈值判定；不能用非黑像素率替代。关闭扩展和透明控制组与原图相同；clear-coat 不重复增亮，GI 更新计数为 0 |
| R4 命中着色分级与材质覆盖 / R2+T3b | H-A 登记 Lit/cutout/自发光；SimpleLit/ComplexLit/ShaderGraph/透明/蒙皮/多 submesh 另计；统计 pass/variant、payload、hit shading GPU ms | 只有登记子集参与；不支持材质显式计数，不静默当黑色/sky；无 Surface Cache/mesh card 资源；不重复接收面 Fresnel/BRDF；H-A 开关不改变 R1–R3 的正确性门槛；H-B 实现后再加入对照 |
| R5 全材质反射与灯光预算 / R2+T3b+T4 | 命中点 PBR（metallic/smoothness/normal）+ cutout + 方向/点/聚光 + 逐灯阴影；与 SS-only/高采样参考比轮廓与亮度接缝；灯数/阴影/payload/overflow 计数 | 命中点材质与解析参考一致到 B0；cutout 轮廓正确；灯数/阴影在预算内；不做 probe 反馈；超预算回退并计数 |
| R2b 压缩与 TMin 续传（v2 候选，不阻塞 v1） / R2 | 构造大量未解决射线与“屏外隐藏遮挡物”；对比固定 K、GPU 压缩、上一帧 N 估计 K、TMin 续传；记录 N/K、wave 利用率和 GPU ms | 隐藏遮挡物在 TMin 方案下不得漏遮挡；结果确定、可复跑；只有证明收益超过压缩/重追成本才建议进入 v2；任一失败只记录为 v2 NO-GO，不影响 R1–R5 |

已知上限：被 SSR 当成有效命中的假交点不会自动触发 DXR；透明/ShaderGraph/蒙皮未入库、灯表不应用 light layer mask/cookie/面光源、预算不足、镜中镜和有限距离仍可能产生错反射。cutout 由 AHS 支持，但透明混合不支持。超过预算时按模块回退，不为补洞默认开启多跳或扩大平台范围。

## 落点与维护

- **帧序**：共享 TLAS 在 206 之前就绪；206 内依次为 SS trace/reproject 分类与请求、固定 K 的 DispatchRays、HW 合成；207 仍只做原绑定。206 的额外子阶段必须在 `SsrPass` 内可独立开关，失败时不破坏原 SSR 输出。
- **代码落点**：`SsrPass.cs` 增加可选子阶段；`ScreenSpaceReflections.compute` 增加请求分类 kernel；新增 `RayTracingSsr.raytrace` 与材质命中 pass；合成保持 unpremultiplied radiance + weight。测试放 `URPSample01/Assets/Tests/RayTracing/SSR/`。
- **独立性**：SSR 单独开启时不得启动 probe 更新、不得分配 GI atlas；GI 单独开启时不得派发射反射射线。两者共用 TLAS，但共享的只有场景/材质 ABI/ray mask/线性 HDR 单位，不共享方向分布、屏幕历史或滤波器。
- **开关与剥离**：默认关；关闭时无 RTAS/请求缓冲/派发，原 SSR 图像逐像素不变。非目标平台剥离 RT 资源与变体，并验证无 shader 错误。
- **证据与文档**：`validation/<R ID>/<platform>-<renderer>/` 记录 requested/served/overflow、DXR marker、图像和已知差异；不覆盖 P0。实现与 [SSR-DXR](../research/SSR-DXR.md) 口径不一致时先修文档再验收。
