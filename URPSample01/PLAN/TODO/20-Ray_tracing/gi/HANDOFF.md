# GI 子模块：动态更新 probe，只作 SSGI 的漫反射回退

2026-11-24｜设计，未实现。平台见[总计划](../HANDOFF.md)，数学与参考见[采样研究](report.md)，共享场景见[TLAS](../research/TLAS.md)。原“完整 DDGI 取代 SSGI”的互斥测试不再是交付要求。仅在高端 RT 配置、相机启用 SSGI 且选择动态回退时订阅共享场景；关闭扩展沿用原 rayMiss 设置，SSR 不依赖本模块。**SSGI 未启用时不引入独立 DDGI。**

## 两个不同生命周期

1. **世界空间生产者**：单一静态有界网格，位置固定，光照随场景更新。用[共享 TLAS 和命中着色](../research/TLAS.md) 从选中 probes 发球面射线，只取直接反射光、自发光和有效环境；v1 不做 probe 自反馈。固定预算 B probes × R rays，轮转更新；多相机同一场景帧不重复。更新 irradiance、距离矩和有效位，整批完成后发布一致快照；未更新 tile 保留上一次有效值。冷启动不能靠一次超预算的全量预热。
2. **屏幕空间消费者**：保留余弦半球 SSGI。只有真实命中且颜色重投影有效才写屏幕信号；区分 trace miss、历史拒绝和无历史。混合模式不能先把旧 reflection probe/sky 写进 SS 信号。时域、双边和上采样内部可以用预乘颜色 + 覆盖率累积，但**最终写出必须是 unpremultiplied radiance + weight（rgb 未乘 weight）**，让材质端只做一次 `lerp`。probe 查询使用接收点 P、N，不是沿 miss 方向虚构交点。无 SS 输出的首帧/切镜仍可读已就绪的 probe；若 SSGI 本身未启用，则不引入独立 DDGI。
3. **能量契约（与现有 URP 契约对齐）**：SSGI 当前输出是 **unpremultiplied `(Lss, w)`**，材质执行 `lerp(base, Lss, w)`；`Pgi = Eprobe/π`，`base = lerp(bakedGI, Pgi, c)`。因此单位强度下 `indirectDiffuse = (1−w)·base + w·Lss`。c 是体积覆盖与有效性，不由亮度决定，且只乘一次。w=1 只取 SSGI；w=0,c=1 只取 probe；w=c=0 回 bakedGI（lightmap/APV/SH）。不要把 probe 预乘 w，也不要在 SSGI 纹理里再混一次；现有 SSGI 存储是 unpremultiplied radiance + weight（rgb 未乘 weight），禁止按预乘 `S` 再做 `lerp(base,S,w)`。强度 I>0 等价于 `lerp(base, Lss×I, w)`（颜色乘 I，权重不乘 I），I=0 沿用原路径的精确 no-op。接收面 albedo、AO 在既有材质链只应用一次。
   **与 SSGI fallback 互斥**：启用动态 probe 回退时，SSGI 的 `rayMiss` 必须设为 `None`，让 miss 写 `weight=0` 并由 `base` 填充；不能先把 Reflection Probes/Sky 写进 SS 信号再叠加 probe。关闭 probe 回退时恢复原 `rayMiss`。
4. **绑定与失效**：先在不透明材质合成端按位置、法线和邻居可见性查 probe；三种 renderer 共用权重合同。probe-ready 不得依赖 SSGI producedOutput，否则 207 关 SS 纹理时会丢弃已收敛的 probe；但查询必须用接收点 `P/N`，不是沿 miss 方向虚构交点。若查询超预算，再对比半分辨率查询和深度法线上采样。切镜只丢相机历史；移动光源/发光体须在重访周期内被观察，不随相机旋转清空整个场。场景卸载、网格布局或数据版本不兼容才清相应 probe。

## 最小数据面与非目标

irradiance 八面体 atlas、距离矩 atlas、每 probe 有效位/更新时间、预算列表和本批 ray scratch；显存按数量×带边 tile×格式字节数，加实际 history/scratch 统计。必须明确一种发布语义（双缓冲或批次后显式版本/屏障），保证消费者不会读到半批数据；未发布 tile 是“无效/保留上次有效值”，不是黑色。v1 用人工布点的体积，可显式屏蔽几何内部的 probe；薄墙泄漏需单测。自动 relocation/classification、多 volume、滚动、独立全屏 DDGI、透明/ShaderGraph 扩展、GI 逐像素 DXR 和递归多跳均非 v1。不能用更重的时域滤波掩盖错误的可见性或能量。

## 独立测试与交付

| ID / 依赖 | 最小 fixture | 通过标准（预算和误差先在 B0 冻结） |
|---|---|---|
| G1 数学与存储 / B0 | CPU 或 compute 注入恒定 L、两色场、常距平面、非法邻居；不启动 DXR/SSGI | 编解码后 E/π=L；矩还原 d、d²；断言内区、边界、八面接缝、全失效和有效黑色，非有限数 0；索引不串 probe。交付输入与读回值 |
| G2 更新 / G1+T3 | 仅测试生产者；单体积、移动遮挡/发光板、两台相机 | 每帧调度 ≤B、主光线 ≤B×R；静态轮转周期 ≤ceil(N/B) 帧；动态变化在 B0 时间窗内回到参考误差，同帧只更新一次；未发布 tile 无效而非黑。记录更新时间、列表、GPU ms、显存和动态阶跃曲线 |
| G3 合成 / B0 fixture，真实集成需 G2 | 固定接收面，扫 s,c 的 0/0.5/1 和强度 0/1；锁曝光移出红板、首帧/切镜、薄墙、体积外、双相机；关 SSR；启用 probe 回退时确认 SSGI rayMiss=None | 中间权重不暗边、不重复 BRDF；首帧已就绪显示 probe，冷启动回 baked；供体出屏后低频色溢保留，测线性 ROI 误差和亮度跳变峰值对旧 fallback；薄墙背面不超过 B0 漏光阈值；probe 与旧 reflection/sky 不叠加；三种 renderer 和 RT-off 对照通过 |

证据分开提交 G1 数值、G2 场景更新、G3 图像；不能拿调度计数代替画面证据。验收不通过只关该扩展，保留现有 SSGI。

## 实现落点与维护

- **落点**：共享 RT 场景服务在 201（`AfterRenderingPrePasses+1`）构建/复用 TLAS；probe 生产者在 SSGI dispatch 之前（可作为 SSGI pass 的 205 前置子阶段，避免占用 202 early motion 与 204 GTAO）；材质端查询在 `ScreenSpaceLightingApplyIndirectDiffuse` 的调用点（需要把接收点 P/N 传入）。三种 renderer 共用同一材质合同。
- **开关**：独立 feature/Volume 选项 + 全局 keyword；默认关，关闭时不分配 atlas、不订阅 TLAS、不派发 probe 射线，原 SSGI/SSR 图像逐像素不变。
- **依赖**：GI 不引用 SSR Volume，SSR 不启动 probe 更新；基础设施失败只关闭 GI 扩展。
- **变体**：probe 查询/命中 shader 只对高端档构建；非目标平台剥离资源与变体，并验证无 shader 错误。
- **证据**：`validation/<G ID>/<platform>-<renderer>/` 记录输入、预算、计数、读回/图像和已知差异；不覆盖 P0。
- **文档同步**：数学与参考改 [report.md](report.md)，场景/TLAS 改 [TLAS](../research/TLAS.md)；实现与文档口径不一致时先修文档再验收。
