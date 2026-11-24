# 动态更新探针：只作 SSGI 漫反射回退的研究与数学

2026-11-24｜调研与设计，未实现、未测。范围：高端 RT 配置下，相机启用 SSGI 且选择动态回退时，用世界空间探针补屏幕外低频漫反射。**不交付完整 DDGI**：不做独立 DDGI 模式、relocation/classification、多 volume、滚动、多跳递归、透明/蒙皮命中或逐像素 GI DXR。平台与共享 TLAS 见 [TLAS](../research/TLAS.md)，合成合同见 [GI HANDOFF](HANDOFF.md)。

## 0. 结论先行

1. 参考实现 `E:/workspace/GitHub/RTXGI-DDGI-URP` 是 **DDGI（probe 射线 + 八面体辐照度/距离矩 + 时域 hysteresis + relocation/classification）**，不是 ReSTIR，也不是 SVGF/à-trous。它可作为数据面与采样数学的参考，但不能整包移植。
2. 本项目 v1 只取“**固定探针网格 + 分帧更新 + 八面体 atlas + 三线性/Chebyshev 采样**”这一段，作为 SSGI 的 miss 回退；屏幕内仍由 SSGI 提供高频细节。
3. 探针是**接收点估计**，不是精确射线交点；不能用它伪造逐像素可见性，也不能把 probe 更新做成独立效果或让 SSR 依赖它。
4. 不搬：relocation/classification、每帧全量更新、probe 自反馈多跳、Surface Cache、自动布点、透明/蒙皮命中。

## 1. 通用骨架与参考实现

标准 DDGI 每帧流程（参考实现的顺序）：

```
1. Trace       每 probe 发 N 条球面 Fibonacci 方向光线（可加随机旋转）
2. Irradiance  8×8 tile（6×6 内容 + 1px border），余弦加权卷积
3. Distance    16×16 tile（14×14 + border），存 (μ, E[d²]) 两阶矩
4. Relocate    把卡进几何的 probe 推出来（v1 不做）
5. Classify    禁用空区域 probe（v1 不做）
6. Border Fix  八面体接缝处复制 border texel，保证双线性连续
```

参考实现的关键事实（行号对应本地 checkout）：

| 主题 | 事实 | 证据 |
|---|---|---|
| 固定射线 | 前 32 条固定方向用于 relocation/classification，不参与随机旋转与卷积 | `Assets/Features/Resources/Shaders/Lib/DDGIInputs.hlsl:29`；`DDGIFuncs.hlsl:119–133` |
| 方向生成 | 球面 Fibonacci + 逐帧旋转；没有逐 probe 的自定义方向集 | `DDGIFuncs.hlsl:108–133` |
| 辐照度归一化 | `rgb *= 1 / max(2 * Σw, eps)`，w 为 `saturate(dot(probeDir, rayDir))` | `Shaders/DDGIUpdateIrradiance.compute:108` |
| 时域混合 | hysteresis 约 0.98；历史为黑时直接采用当前值；亮度突变时降 hysteresis 并 clamp | 同上 `110–139` |
| 距离矩 | 深度截断到 `1.5×probeSize`；权重 `pow(saturate(dot),64)`；存 μ 与 E[d²] | `Shaders/DDGIUpdateDistance.compute:87–111` |
| 采样 | 8 邻居三线性 × 法线 wrap shading × Chebyshev 可见性；低权重 crush | `DDGIFuncs.hlsl:348–422` |
| Chebyshev | `variance = |μ² − E[d²]|`；`chebyshev = variance/(variance + (μ−d)²)`，下限 0.05 | 同上 `389–403` |
| 八面体布局 | probe base texel = 坐标 × (interior+2)，内容从 +1,+1 开始 | `Lib/DDGIProbeIndexing.hlsl:107–168` |
| 调度 | 每帧全量派发（示例 10,648 probes × 256 rays），收敛后跳过；没有轮转预算 | `DDGIFeature.cs:451–486,652` |
| 构建 | 自动管理实例，每帧 Build → bind → DispatchRays；不是按脏帧增量 | `DDGIFeature.cs:309–311,474–486` |

**参考参数不是项目默认值**。v1 的探针数、间距、每 probe 射线数、hysteresis 与 tile 尺寸都在 B0 冻结，并用 G1/G2 的 fixture 先证数学与调度。

## 2. v1 数据面

| 资源 | 建议格式/尺寸 | 说明 |
|---|---|---|
| irradiance atlas | `Tex2DArray` RGBA16F；每 probe 8×8（6×6 内容 + 1px border） | 存 irradiance `E`；未更新 tile 保留上一次有效值 |
| distance atlas | `Tex2DArray` RG16F；每 probe 16×16（14×14 内容 + 2px border） | 存 `(μ, E[d²])`；用于 Chebyshev 与薄墙拒绝 |
| probe state | `GraphicsBuffer` 或 `Tex2DArray` R8/RGBA8 | 有效位、更新时间/版本、可选手动禁用；不做自动 relocation |
| update scratch | 有界 `GraphicsBuffer` | 本批 `B × R` 条主射线的 radiance/distance；不跨帧复用为历史 |
| probe 参数 | CBUFFER | 体积原点、间距、数量、旋转、每批预算、接收点 bias |

显存按 `probeCount × tileBytes × 2` 加 scratch/history 统计；不给未启用平台分配。atlas 可以做双缓冲保证“发布前不可见”，也可以在批次完成后用显式版本/屏障发布；v1 先选一种并在 G1/G2 固定，不能两种口径混用。

## 3. 更新调度

1. **世界空间生产者**：单一静态有界网格，位置固定，光照随场景更新。固定预算 `B probes × R rays`，按探针索引轮转；多相机同一场景帧不重复。`ceil(N/B)` 帧覆盖全部 probe。
2. **不更新即保留**：未更新的 tile 保留上一次有效值；批次未发布前，消费者不能读到半批数据。冷启动不能靠一次超预算的全量预热；用 fixture 注入常量场验证数学，再让真实场景逐帧收敛。
3. **只取直接光 + 自发光 + 有效环境**：v1 不做 probe 自反馈（不读自己正在写的 atlas），也不做屏幕空间历史复用。灯光/发光参数变化会通过重访周期体现；不因相机旋转清空整个场。
4. **失效与重发布**：场景卸载、网格布局或数据版本不兼容才清相应 probe；切镜只丢相机历史。probe 数据属于场景，不属于相机。
5. **计数**：每帧记录 `scheduled/updated/published/pending`、主射线数、GPU ms、显存；G2 以计数守恒和收敛曲线判定，不以“画面变亮”代替。

## 4. 采样与能量合同

接收点在材质合成端查询，使用接收点 `P/N`，不是沿 SSGI miss 方向虚构交点。8 个邻居的三线性权重 × 法线 wrap shading × Chebyshev 可见性；无效/被禁用 probe 的权重归零后重新归一化。薄墙、背面与体积外要由 distance moments 和法线拒绝，不能只靠亮度 clamp。

命名与现有 URP 契约对齐：

- `Eprobe`：探针 irradiance（余弦半球积分量，单位与场景线性辐亮度一致）。
- `Pgi = Eprobe / π`：作为接收点入射漫反射辐亮度；与 URP 的 `bakedGI` 同量纲。
- `c ∈ [0,1]`：体积覆盖与有效位，不由亮度决定；只乘一次。
- `base = lerp(bakedGI, Pgi, c)`：`c=0` 回 lightmap/APV/SH，`c=1` 用探针。
- 现有 SSGI 输出是 **unpremultiplied `(Lss, w)`**；材质执行 `lerp(base, Lss, w)`，即 `(1−w)·base + w·Lss`。
- 因此 `w=1` 只取 SSGI；`w=0,c=1` 只取 probe；`w=c=0` 回 bakedGI。**不要**把 probe 先乘 `w` 或在 SSGI 纹理里再混一次；也不要用预乘的 `S` 做 `lerp(base,S,w)` 造成二次加权。
- 强度 `I>0` 定义为 `lerp(base, Lss·I, w)` 的等价形式（颜色乘 I，权重不乘 I）；`I=0` 沿用原路径的精确 no-op。接收面 albedo、AO 在既有材质链只应用一次。

**与现有 SSGI fallback 的互斥**：动态 probe 回退启用时，SSGI 的 `rayMiss` 不能同时写 Reflection Probes/Sky 到 SS 信号；否则 miss 像素会先拿到旧 fallback，再被材质 probe 覆盖/叠加。v1 在启用 probe 回退时把 SSGI 的 fallback 设为 `None`，miss 写 `weight=0`，由 `base` 负责填充；关闭 probe 回退则恢复原 `rayMiss` 设置。若 SSGI 本身未启用，不引入独立 DDGI。

## 5. 验证向量（与 [GI HANDOFF](HANDOFF.md) 的 G1–G3 对应）

| ID | 最小 fixture | 通过标准（阈值在 B0 冻结） |
|---|---|---|
| G1 数学与存储 | CPU/compute 注入恒定 `L`、两色场、常距平面、非法邻居；不启动 DXR/SSGI | 编解码后 `E/π=L`；矩还原 `d`、`d²`；断言内区、边界、八面接缝、全失效和有效黑色；非有限数写 0；索引不串 probe。交付输入与读回值 |
| G2 更新 | 仅生产者；单体积、移动遮挡/发光板、两台相机 | 每帧调度 ≤B、主射线 ≤B×R；静态轮转周期 ≤`ceil(N/B)` 帧；动态变化在 B0 时间窗内回到参考误差；同帧只更新一次；未发布 tile 无效而非黑。记录更新时间、列表、GPU ms、显存和动态阶跃曲线 |
| G3 合成 | 固定接收面，扫 `s,c` 的 0/0.5/1 和强度 0/1；锁曝光移出红板、首帧/切镜、薄墙、体积外、双相机；关 SSR | 中间权重不暗边、不重复 BRDF；首帧已就绪显示 probe，冷启动回 baked；供体出屏后低频色溢保留；测线性 ROI 误差和亮度跳变峰值对旧 fallback；薄墙背面不超过 B0 漏光阈值；三种 renderer 和 RT-off 对照通过 |

G1 只证数学，G2 只证更新，G3 只证合成；证据分开提交，不能拿调度计数代替画面，也不能拿画面通过跳过非有限值/索引隔离断言。

## 6. 已知上限

- 无 relocation/classification：探针卡进几何或落在空区域需人工布点/显式禁用；薄墙泄漏要单测并可能收窄体积。
- 无多跳：命中点只算直接光、自发光和有效环境；低频反弹靠探针跨帧更新，不保证瞬时收敛。
- 无自动布点/多 volume/滚动：v1 是人工有界体积；场景扩展需重新布点。
- 无透明/蒙皮/alpha test 命中；未入库几何没有贡献。
- 快速灯光变化、低预算和探针间距会造成低频滞后/色块；不能用更重的时域滤波掩盖错误的可见性或能量。

## 7. 参考与版本

- 本地参考：`E:/workspace/GitHub/RTXGI-DDGI-URP`（Unity 2022.3.62f1 + URP 14.0.9，DXR/DX12）。
- RTXGI 1.x 是 DDGI；RTXGI 2.x 转向 NRC + SHaRC，不再是 DDGI。RTXDI 是 ReSTIR 采样库，NRD 是降噪器；三者都不能混称。
- Lumen 走 surface cache + world radiance cache + screen probe final gather，不是探针 DDGI，也不是 ReSTIR；其“infinite bounces”是跨帧累积而非同帧多跳。
- HDRP 的 APV 是烘焙 SH，不提供动态更新；HDRP 的 SSGI/RTGI fallback 与探针是两回事。详见 [HDRP14](../research/HDRP14.md)。
