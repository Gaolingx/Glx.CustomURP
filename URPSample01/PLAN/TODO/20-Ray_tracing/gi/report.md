# 动态更新探针：只作 SSGI 漫反射回退的研究与数学

**状态：调研与设计，未实现、未测。** 范围：高端 RT 配置下，相机启用 SSGI 且选择动态回退时，用世界空间探针补屏幕外低频漫反射。v1 采用**固定网格 + 分帧更新 + 1 跳反馈 + relocation/classification**；**不交付完整 DDGI**：不做独立 DDGI 模式、多 volume、滚动、递归多跳、透明/蒙皮命中或逐像素 GI DXR。平台与共享 TLAS 见 [TLAS](../research/TLAS.md)，命中着色见 [HitShading](../research/HitShading.md)，合成合同见 [GI HANDOFF](HANDOFF.md)，总报告（平台/API、HDRP、Lumen、Surface Cache 取舍与总验收）见 [research/report.md](../research/report.md)。

## 0. 结论先行

1. 参考实现 `E:/workspace/GitHub/RTXGI-DDGI-URP` 是 **DDGI（probe 射线 + 八面体辐照度/距离矩 + 时域 hysteresis + relocation/classification）**，不是 ReSTIR，也不是 SVGF/à-trous。它可作为数据面与采样数学的参考，但不能整包移植。
2. 本项目 v1 取“**固定探针网格 + 分帧更新 + 八面体 atlas + 三线性/Chebyshev 采样 + 1 跳反馈 + relocation/classification**”这一段，作为 SSGI 的 miss 回退；屏幕内仍由 SSGI 提供高频细节。
3. 探针是**接收点估计**，不是精确射线交点；不能用它伪造逐像素可见性，也不能把 probe 更新做成独立效果或让 SSR 依赖它。
4. 不搬：每帧全量更新、递归多跳、Surface Cache、自动布点/多 volume/滚动、透明/蒙皮命中。**反馈只读上一帧 atlas 一次，不递归追射线**；**probe 是辐照度缓存，不是 Surface Cache**；SSR 的 H-A/H-B/H-C 选择不改变 GI 的 G1–G5。

## 1. 通用骨架与参考实现

标准 DDGI 每帧流程（参考实现的顺序）：

```
1. Trace       每 probe 发 N 条球面 Fibonacci 方向；前 32 条固定不旋转；命中点算灯表直接光+阴影+emission，并读上一帧 atlas 做一次反馈
2. Irradiance  8×8 tile（6×6 内容 + 1px border），余弦加权卷积
3. Distance    16×16 tile（14×14 + border），存 (μ, E[d²]) 两阶矩
4. Relocate    把卡进几何的 probe 推出来（v1 默认开）
5. Classify    禁用空区域 probe（v1 默认开；inactive 只追 32 条）
6. Border Fix  八面体接缝处复制 border texel，保证双线性连续
```

参考实现的关键事实（行号对应本地 checkout）：

| 主题 | 事实 | 证据 |
|---|---|---|
| 固定射线 | 前 32 条固定方向用于 relocation/classification，不参与随机旋转与卷积 | `Assets/Features/Resources/Shaders/Lib/DDGIInputs.hlsl:29`；`DDGIFuncs.hlsl:119–133` |
| 方向生成 | 球面 Fibonacci + 逐帧旋转；没有逐 probe 的自定义方向集 | `DDGIFuncs.hlsl:108–133` |
| 命中着色 | 方向光 + 点/聚光全部 + 逐灯阴影 + 自发光；每命中灯数无上限 | `DDGIRayTracing.raytrace:71–114` |
| 反馈 | 命中点读 `_ProbeIrradianceHistory` 的 8 邻居并乘 `min(albedo,0.9)·Lambert()`；跨帧迭代 | `DDGIRayTracing.raytrace:115`；`DDGIFuncs.hlsl:348–430` |
| 灯表 | 每帧 `FindObjectsOfType<Light>()`，CPU 端算衰减并写 buffer | `DDGIFeature.cs:752–809` |
| Relocation | 前 32 条固定射线；背向比例超阈值/离正面太近时偏移；offset 限 0.45×probe 间距 | `DDGIRelocateProbe.compute:20–130`；`DDGI.cs:48–56` |
| Classification | 背向比例高或 32 条射线都超出体素 → inactive；inactive 只追 32 条；消费者跳过 | `DDGIProbeClassification.compute:11–100`；`DDGIRayTracing.raytrace:20–23`；`DDGIFuncs.hlsl:366` |
| 辐照度归一化 | `rgb *= 1 / max(2 * Σw, eps)`，w 为 `saturate(dot(probeDir, rayDir))` | `Shaders/DDGIUpdateIrradiance.compute:108` |
| 时域混合 | hysteresis 约 0.98；历史为黑时直接采用当前值；亮度突变时降 hysteresis 并 clamp | 同上 `110–139` |
| 距离矩 | 深度截断到 `1.5×probeSize`；权重 `pow(saturate(dot),64)`；存 μ 与 E[d²] | `Shaders/DDGIUpdateDistance.compute:87–111` |
| 采样 | 8 邻居三线性 × 法线 wrap shading × Chebyshev 可见性；低权重 crush | `DDGIFuncs.hlsl:348–422` |
| Chebyshev | `variance = abs(μ² − E[d²])`；`chebyshev = variance/(variance + (μ−d)²)`，下限 0.05 | 同上 `389–403` |
| 八面体布局 | probe base texel = 坐标 × (interior+2)，内容从 +1,+1 开始 | `Lib/DDGIProbeIndexing.hlsl:107–168` |
| 调度 | 每帧全量派发（默认 22³ = 10,648 probes × **144** rays，范围 32–256）；无轮转预算；只有可选 variability 回读达标才允许收敛跳过 | `Runtime/DDGIFeature.cs:468–520,983–984`；`Runtime/Overrides/DDGI.cs:91–96` |
| 构建 | `ManagementMode.Automatic` + `RayTracingModeMask.Everything` + layer 255，自动收集全部 Renderer；每帧（未收敛时）Build → bind → DispatchRays，不是按脏帧增量 | `Runtime/DDGIFeature.cs:309–311,472–486` |
| 光线缓冲 | `MaxNumRays = 512` 固定 stride；buffer = probes × 512 × float4，实际每 probe 只发 `NumRays` 条 | `Runtime/DDGIFeature.cs:333–340` |
| 消费者 | 材质采样读 `_ProbeIrradianceHistory` / `_ProbeDistanceHistory`；写完 `CopyTexture` 到 history，因此显示晚一帧 | `Lib/DDGIFuncs.hlsl:394,418`；`DDGIFeature.cs:639–640` |
| 命中材质 | `Custom/LitRayTracing` 的 `DDGIRayTracing` pass（`LightMode="DDGIRayTracing"`）；emission 只读 `_EmissionColor`，`_EmissionMap` 未接 | `Shaders/LitRayTracing.shader:124–194`（`:190`） |
| 死参数 | `_NormalBias` / `_EnergyPreservation` 在 CBUFFER 声明并 push，但 shader 未读 | `Lib/DDGIInputs.hlsl:90,92`；`DDGIFeature.cs:702,704` |

**参考参数不是项目默认值**。v1 的探针数、间距、每 probe 射线数、hysteresis 与 tile 尺寸都在 B0 冻结，并用 G1/G2 的 fixture 先证数学与调度。三个易错点：

- 代码默认是 22³ probes、144 rays（32–256）；asset 里的 256 不是代码默认值。
- `enableProbeVariability` 默认 OFF，走 else 分支每帧强制 `mIsConverged = false`，示例默认**不会**停更；`DDGIFeature.cs:652` 是 `Release()`，不是收敛跳过。
- 收敛判据是 CPU 异步回读平均 variability（阈值 0.025、最少 16 个样本），且收敛门同时包住 RTAS Build 与 irradiance/distance 两个 dispatch。

## 2. v1 数据面

| 资源 | 建议格式/尺寸 | 说明 |
|---|---|---|
| irradiance atlas | `Tex2DArray` RGBA16F；每 probe 8×8（6×6 内容 + 1 texel border + 4 重复角） | 存 irradiance `E`；未更新 tile 保留上一次有效值 |
| distance atlas | `Tex2DArray` RG16F；每 probe 16×16（14×14 内容 + 1 texel border + 4 重复角） | 存 `(μ, E[d²])`；用于 Chebyshev 与薄墙拒绝。border 由 `BORDER_TEXELS = 16×4+4 = 68` 逐 texel 复制，不是 2px 宽 |
| probe data | `Tex2DArray` RGBA16F/32F | `xyz = relocation offset`，`w = state（ACTIVE/INACTIVE）`；手动禁用也走 state，消费者跳过 |
| feedback snapshot | 上一帧已发布 atlas（双缓冲或 copy） | producer 与 consumer 只读；不能读正在写的 atlas |
| 灯表 | 有界 `GraphicsBuffer` | Directional/Point/Spot 的方向、颜色、距离/角度衰减；容量 `Lmax`；注册/脏标记更新，不用逐帧 `FindObjectsOfType` |
| update scratch | 有界 `GraphicsBuffer` | 本批 `B × R` 条主射线 + 阴影射线；不跨帧复用为历史 |
| probe 参数 | CBUFFER | 体积原点、间距、数量、旋转、每批预算、接收点 bias、relocation/classification 阈值 |

显存按 `probeCount × tileBytes × 2` 加 scratch/history 统计；不给未启用平台分配。atlas 可以做双缓冲保证“发布前不可见”，也可以在批次完成后用显式版本/屏障发布；v1 先选一种并在 G1/G2 固定，不能两种口径混用。

## 3. 更新调度

1. **世界空间生产者**：单一静态有界网格，位置固定，光照随场景更新。固定预算 `B probes × R rays`（`R ≥ 32`），按探针索引轮转；前 32 条固定射线不旋转，供 relocation/classification；inactive probe 只追 32 条。多相机同一场景帧不重复。`ceil(N/B)` 帧覆盖全部 probe。
2. **不更新即保留**：未更新的 tile 保留上一次有效值；批次未发布前，消费者不能读到半批数据。冷启动不能靠一次超预算的全量预热；用 fixture 注入常量场验证数学，再让真实场景逐帧收敛。
3. **命中点光照 + 一次反馈**：方向光/点光/聚光 + 逐灯阴影 + 自发光，再加一次 `Eprev(P,N)` 反馈；**不读正在写的 atlas**，只读上一帧已发布快照。反馈跨帧迭代，不是同帧递归多跳。灯光/发光参数变化会通过重访周期体现；不因相机旋转清空整个场。
4. **失效与重发布**：场景卸载、网格布局或数据版本不兼容才清相应 probe；切镜只丢相机历史。probe 数据属于场景，不属于相机。
5. **计数**：每帧记录 `scheduled/updated/published/pending`、主射线/固定射线/阴影射线数、灯数、active/inactive/relocated 数、GPU ms、显存；G2 以计数守恒和收敛曲线判定，不以“画面变亮”代替。

## 4. 采样与能量合同

接收点在材质合成端查询，使用接收点 `P/N`，不是沿 SSGI miss 方向虚构交点。probe 世界位置 = 网格位置 + relocation offset；8 个邻居的三线性权重 × 法线 wrap shading × Chebyshev 可见性；inactive/无效 probe 的权重归零后重新归一化。薄墙、背面与体积外要由 distance moments 和法线拒绝，不能只靠亮度 clamp。

命名与现有 URP 契约对齐：

- `Eprobe`：探针 irradiance（余弦半球积分量，单位与场景线性辐亮度一致）；由命中点直接光 + 自发光 + 一次 `Eprev` 反馈组成。
- 反馈项：射线 radiance 加 `min(albedo, cap) · Lambert() · Eprev(P,N)`；经余弦积分后对 `Eprobe` 的贡献是 `min(albedo,cap)·Eprev`（常数场），只在 producer 端加一次。`Eprev` 是上一帧已发布 atlas，inactive/无效邻居为 0。它不是同帧递归多跳，不能额外乘接收面 albedo。
- `Pgi = Eprobe / π`：作为接收点入射漫反射辐亮度；与 URP 的 `bakedGI` 同量纲。
- `c ∈ [0,1]`：体积覆盖与有效位，不由亮度决定；只乘一次。
- `base = lerp(bakedGI, Pgi, c)`：`c=0` 回 lightmap/APV/SH，`c=1` 用探针。
- 现有 SSGI 输出是 **unpremultiplied `(Lss, w)`**；材质执行 `lerp(base, Lss, w)`，即 `(1−w)·base + w·Lss`。
- 因此 `w=1` 只取 SSGI；`w=0,c=1` 只取 probe；`w=c=0` 回 bakedGI。**不要**把 probe 先乘 `w` 或在 SSGI 纹理里再混一次；也不要用预乘的 `S` 做 `lerp(base,S,w)` 造成二次加权。
- 强度 `I>0` 定义为 `lerp(base, Lss·I, w)` 的等价形式（颜色乘 I，权重不乘 I）；`I=0` 沿用原路径的精确 no-op。接收面 albedo、AO 在既有材质链只应用一次。

**`Pgi=Eprobe/π` 是本项目的设计约定，不是参考实现的能量链**。参考链是 atlas `1/(2Σw)`（存 `E/(2π)`）→ `pow(x,1/5)` 编码 → `pow(x,2.5)` 解码 → `×Σ⁻¹` → 平方 → `×2π` → 材质 `×albedo/π`（`Lib/DDGIFuncs.hlsl:424–430`；`DDGIUpdateIrradiance.compute:102–108`）。常数场 `L` 的往返：atlas 存 `L/2`，采样端平均 `√(L/2)` 后平方回 `L/2`，`×2π` 得 `πL=E`，材质端得 `albedo·L`，**没有额外的 0.5**；非均匀场“对 √E 求均值再平方”才是方差偏差。照抄这条链会把 variance bias 和 `×2π` 一起带进我们的材质端，因此 G1 必须先钉死 atlas 单位与 `E/π` 的换算，再允许 G3 比较 ROI。

**与现有 SSGI fallback 的互斥**：动态 probe 回退启用时，SSGI 的 `rayMiss` 不能同时写 Reflection Probes/Sky 到 SS 信号；否则 miss 像素会先拿到旧 fallback，再被材质 probe 覆盖/叠加。v1 在启用 probe 回退时把 SSGI 的 fallback 设为 `None`，miss 写 `weight=0`，由 `base` 负责填充；关闭 probe 回退则恢复原 `rayMiss` 设置。若 SSGI 本身未启用，不引入独立 DDGI。

## 5. 验证向量（与 [GI HANDOFF](HANDOFF.md) 的 G1–G5 对应）

| ID | 最小 fixture | 通过标准（阈值在 B0 冻结） |
|---|---|---|
| G1 数学与存储 | CPU/compute 注入恒定 `L`、两色场、常距平面、非法邻居、relocated/inactive 邻居、注入 `Eprev` 的反馈项；不启动 DXR/SSGI | 编解码后 `E/π=L`；矩还原 `d`、`d²`；反馈项在线性场下对 E 的贡献等于 `albedo·Eprev`（不是 `/π`）；offset/state 索引不串 probe；断言内区、边界、八面接缝、全失效和有效黑色；非有限数写 0。交付输入与读回值 |
| G2 更新 | 仅生产者；单体积、移动遮挡/发光板、多灯、两台相机 | 每帧调度 ≤B、主射线 ≤B×R；inactive 只追 32；阴影射线 ≤主射线×`Smax`；灯数 ≤`Lmax`；静态轮转周期 ≤`ceil(N/B)` 帧；动态变化在 B0 时间窗内回到参考误差；relocation 不抖动、classification 能重激活；同帧只更新一次；未发布 tile 无效而非黑。记录更新时间、列表、灯/阴影计数、GPU ms、显存和动态阶跃曲线 |
| G3 合成 | 固定接收面，扫 `s,c` 的 0/0.5/1 和强度 0/1；锁曝光移出红板、首帧/切镜、薄墙、体积外、双相机；关 SSR；先 feedback/relocation/classification 全关跑基线 | 中间权重不暗边、不重复 BRDF；首帧已就绪显示 probe，冷启动回 baked；供体出屏后低频色溢保留；测线性 ROI 误差和亮度跳变峰值对旧 fallback；薄墙背面不超过 B0 漏光阈值；三种 renderer 和 RT-off 对照通过 |
| G4 relocation + classification | 墙内 probe、空体素、动态几何进出、薄墙、两台相机；默认开 | offset 限在 B0 上限；inactive 只追 32 且消费者跳过；动态几何进入后在 B0 帧内重激活；无索引串/抖动；记录 active/inactive/relocated 计数 |
| G5 1 跳反馈 | 静态房间 + 红墙供体、灯开/关阶跃、移动发光体、薄墙、feedback on/off、无效/relocated/inactive 邻居 | 与高采样参考的 ROI 在 B0 内；关灯后旧反馈在 B0 时间窗内衰减，不留永久能量；不重复乘 albedo/BRDF；不递归追射线；计数与图像一起交付 |

G1 只证数学，G2 只证更新，G3 只证合成，G4 只证维护，G5 只证反馈；证据分开提交，不能拿调度计数代替画面，也不能拿画面通过跳过非有限值/索引隔离断言。

## 6. 已知上限

- relocation/classification 默认开，但不是自动布点：仍是人工有界网格；多 volume/滚动/自动布局不在 v1。relocation 会改变 probe 位置，消费者必须应用 offset；薄墙泄漏仍要单测。
- 反馈是单次读取、跨帧迭代，不是同帧多跳；低频反弹不等于瞬时收敛，关灯后旧能量要靠衰减/重访清除。
- 命中点光照覆盖方向光/点光/聚光 + 自发光；不命中透明/ShaderGraph/未入库几何。灯表不应用 Unity light layer mask，所有选中灯影响所有命中。
- cutout 通过 AnyHit 支持；透明混合、alpha-to-coverage、蒙皮/动态拓扑仍不支持或待验证；未入库几何没有贡献。
- 快速灯光变化、低预算、探针间距、全灯/阴影预算会造成低频滞后/色块；不能用更重的时域滤波掩盖错误的可见性或能量。

## 7. 参考与版本

- 本地参考：`E:/workspace/GitHub/RTXGI-DDGI-URP`（Unity 2022.3.62f1 + URP 14.0.9，DXR/DX12）。
- 命中着色（Fat CHS、AHS、BLAS 顶点属性、灯表、payload）见 [HitShading](../research/HitShading.md)；本文件只保留探针侧的取舍。
- RTXGI 1.x 是 DDGI；RTXGI 2.x 转向 NRC + SHaRC，不再是 DDGI。RTXDI 是 ReSTIR 采样库，NRD 是降噪器；三者都不能混称。
- Lumen 走 surface cache + world radiance cache + screen probe final gather，不是探针 DDGI，也不是 ReSTIR；其“infinite bounces”是跨帧累积而非同帧多跳。
- HDRP 的 APV 是烘焙 SH，不提供动态更新；HDRP 的 SSGI/RTGI fallback 与探针是两回事。详见 [HDRP14](../research/HDRP14.md)。
- 参考实现逐行事实、默认值、死参数与能量链差异汇总在 [总报告 §6](../research/report.md)；本文件只保留探针决策所需的最小证据。
- Unity 6.7 LTS 的 URP Surface Cache GI（[Unite Seoul 2026](https://unity.com/blog/unite-seoul-keynote-2026-recap)）是官方动态 GI，未来升级优先评估它，而不是自建完整 Surface Cache。
