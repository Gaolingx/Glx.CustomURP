# 不透明 SSR × DXR：从“屏幕未解决”到“世界空间补追”

2026-11-24｜计划，未实现、未测。只为高端合格平台的 opaque SSR 增加一个离屏来源；不触碰透明 SSR、折射、默认材质环境反射或 GI 启动策略。平台与 TLAS 见 [TLAS](TLAS.md)；HDRP 对照见 [HDRP14](HDRP14.md)；总验收见 [HANDOFF](../HANDOFF.md)。`U/` 表示本项目内嵌包 `URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12/`；行号对该快照有效。

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
| 首帧仅缺颜色历史 | 只要当前深度/法线/材质输入有效，允许独立生成 HW 请求；当前几何输入缺失则回环境 |
| 命中受支持几何 | 返回线性 HDR、hit distance、来源与材质有效位；用命中点材质、自发光及最小直接光算 radiance；不再乘接收面 Fresnel；不默认依赖 GI 探针或 Surface Cache |
| 真正的环境 miss / 未服务 | 只有环境可见性可信的远处 miss 才允许直接用方向 sky；有限 TMax、未服务/无预算、无有效着色或数据过期均保持材质 Reflection Probe / Sky |

未入库的物体没有可恢复的轮廓；不宣称所有洞都能补。被屏幕步进当成有效命中的**假交点**不会自动触发 DXR，必须单测拒绝。

## 2. 请求、派发与合成

1. **请求缓冲**：在 206 的 SS trace/回投分类完成后、邻域填洞/聚合之前，把合格样本写入有界列表：sample slot、receiver P/N、direction、roughness/TMin/TMax、seed、材质/实例 id。记录 `requested/served/overflow`。用确定性空间分布/轮转避免边缘长期饿死。
2. **派发**：Unity 2022.3 没有 GPU 计数间接 `DispatchRays`。使用 CPU 已知宽度 `K` 的固定派发，raygen 读 GPU 有效数 `min(count,K)` 后早退；散写回原槽。`K=0` 时 CPU 不提交。**不是精确按 N 间接派发，也不保证满 wave**；先与同预算固定派发/掩码方案计时对比，不引入阻塞读回。
3. **命中着色**：用材质 `LightMode = "RayTracing"` pass（closest-hit）读取命中点 UV/法线/albedo/emission，加最小直接光与可选遮挡射线；输出与 [TLAS](TLAS.md) 的共同 ABI 一致。TMin 从原表面做 bias；不把 Hi-Z 走过的距离当世界空间安全距离。有限 TMax 的截断不等于天空可见。
4. **合成**：保持现有 unpremultiplied 合同。令 `s` 为原 SS 权重、`Lss` 为原 SS 辐亮度、`r` 为已解决 HW 份额、`Lhw` 为 HW 辐亮度、`Lenv` 为材质环境反射：
   - `a = s + (1 − s) · r`
   - `rgb = (s · Lss + (1 − s) · r · Lhw) / a`，`a=0` 时 RGB 写零
   - 材质仍执行 `lerp(Lenv, rgb, a)`，得到 `(1−a)Lenv + sLss + (1−s)rLhw`
   - 这样旧非预乘 SSR 合同、点采样、强度与 clear-coat 各 lobe 的 BRDF/环境权重仍只应用一次；HW 不再乘一次 SS 屏幕边缘淡出，否则离屏洞会重新出现。
5. **历史**：保留 SSR 自己的 motion、粗糙度、深度/法线 history；HW 距离及分类只用于历史拒绝。SS→HW 来源变化且距离/亮度不一致时降历史权重；相机切换、尺寸/后端切换要失效；禁止回灌已空间模糊的历史。205 的 GI history 不能借给 206。SS 读历史 scene color，HW 命中着色较简单，观察高亮物体出屏会不会从“空洞”变成“亮度台阶”。

## 3. 帧序与落点

| 顺序 | 内容 | 约束 |
|---|---|---|
| 201 | 共享 RT 场景服务构建/复用 TLAS | `AfterRenderingPrePasses+1`；202 已用于 early motion，203 是共享 Hi-Z。在第一个 RT 消费者之前；相机无关；失败则本帧所有 RT 消费者回原路径 |
| 206a | SS trace + reproject + 原始样本分类/请求列表 | 必须保留原始状态，不读最终 alpha |
| 206b | 固定 K 的 `DispatchRays` | raygen 早退；不阻塞读回 |
| 206c | HW 结果合成到 SSR 输出纹理 | 仍为 206 内、207 绑定之前 |
| 207 | `_SCREEN_SPACE_SSR` 绑定 | 原有材质端只消费一次 |
| 210/250 | Deferred GBuffer / Forward opaque 消费 | 三种 renderer 共用权重合同 |

实现落点建议：`SsrPass.cs` 增加可选子阶段；`ScreenSpaceReflections.compute` 增加请求分类 kernel；新增 `RayTracingSsr.raytrace` 与材质命中 pass；合成 shader 保持 unpremultiplied + weight。测试放 `URPSample01/Assets/Tests/RayTracing/SSR/`。基础设施不得引用 SSR Volume；SSR 单独开启时不得启动 probe 更新。

## 4. 预算与已知上限

- B0 先冻结：`K`（按分辨率/质量档）、TMax、最小可反射粗糙度、每像素/每 tile 上限、溢出策略、HW 亮度与 SS 的容许阶跃。未定预算不报性能通过。
- 固定派发会浪费未服务线程；以 GPU 早退和确定性槽位控制成本，不把“零命中”当成“没派发”。证据必须同时有 DXR marker 与命中/未服务计数。
- 已知上限：屏幕假命中不会触发 DXR；蒙皮/透明/alpha test/未入库材质、预算不足、简化灯光、镜中镜和有限距离仍可能产生错反射；超过预算时按模块回退，不为补洞默认开启多跳或扩大平台范围。

## 5. 独立放行条件

| ID / 前置 | 单模块实验（关闭 GI） | 通过条件 / 证据 |
|---|---|---|
| R1 分类与预算 / B0 | 注入可反射/无效、命中/历史拒绝、边缘、零预算与溢出的确定性图案；测试方向重建、半分辨率奇数宽高 | 计数守恒、无重复/越界回写；只调度合格份额；延迟读回对照原坐标/方向；预算为零回原环境；无历史依然可分类。记录 requested/served/overflow |
| R2 离屏求交与取色 / R1+T3 | 光滑镜面和有 ID 的离屏纯色发光板，再加遮挡/移除/超 TMax；摄像机内外两组 | 已服务射线命中正确实例、距离与颜色符合解析解；留出未服务负样本，不能误标为 sky 或黑色有效；DXR 实际派发和 marker 可见，SS-only 无法恢复的轮廓在混合模式恢复 |
| R3 重建与回归 / R2 | 锁曝光/种子，让物体连续出入画；比较 SS-only、Hybrid 和同材质高采样 RT 参考；切镜、移动遮挡、粗糙边界、clear-coat、三种 renderer 和透明控制组 | 同一表面 ROI 统计参考轮廓召回率、错误轮廓率、源切换亮度峰值、历史拖尾帧和每 pass GPU ms，按 B0 预定阈值判定；不能用非黑像素率替代。关闭扩展和透明控制组与原图相同；clear-coat 不重复增亮，GI 更新计数为 0 |

证据分 R1 计数/延迟读回、R2 解析交点与颜色、R3 图像与性能；不能拿调度计数代替画面证据。验收不通过只关该扩展，保留现有 SSR。
