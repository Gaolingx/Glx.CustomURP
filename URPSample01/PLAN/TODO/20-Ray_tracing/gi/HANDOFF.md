# GI 子模块：动态更新 probe，只作 SSGI 的漫反射回退

**状态：G1 CPU 数学与存储夹具 13/13 通过；G2 的 GPU 生产者已在 Win64 D3D12 Player 通过（只验收生产者：双缓冲发布、预算、classification/relocation、动态收敛；材质消费者与 G3–G5 尚未实现）。** [G2 证据](../validation/G2/evidence/Summary.json)。 [实际向量、分类计数与 FP16 读回](../validation/G1/Win64-D3D11/run-04/Tests.xml)覆盖常量与两色场、窗口天空、反馈、固定方向排除、八面接缝及邻居失效。G1 不启用 SSGI 或 DXR。 平台见[总计划](../HANDOFF.md)，数学与参考见[采样研究](report.md)，共享场景见[TLAS](../research/TLAS.md)，命中着色见[HitShading](../research/HitShading.md)。仅在高端 RT 配置、相机启用 SSGI 且选择动态回退时订阅共享场景；关闭扩展沿用原 rayMiss 设置，SSR 不依赖本模块。**SSGI 未启用时不引入独立 DDGI。**

## 两个不同生命周期

1. **世界空间生产者**：单一静态有界网格，位置固定，光照随场景更新。用共享 TLAS 与 `GlxDXR` 命中着色从选中 probes 发球面射线：命中点算方向光/点光/聚光 + 逐灯阴影 + 自发光，再读**上一帧已发布** atlas 做一次 8 邻居反馈（不递归追射线）。固定预算 B probes × R rays（R ≥ 32），轮转更新；前 32 条为不旋转固定射线，供 relocation/classification；inactive probe 只追这 32 条。更新 irradiance、距离矩和 probe state/offset，整批完成后发布一致快照；未更新 tile 保留上一次有效值。**本批 trace 使用上一次已发布的 offset/state；relocate/classify 在本批 trace 之后更新，供下一批使用**（与参考实现一致）。同一场景帧只由首个启用 GI 的合格 base camera 触发更新；双相机测试要求两台非 XR base camera 读取同一 probe 版本、各用自己的屏幕历史；冷启动不能靠一次超预算的全量预热。
   **生产者射线的天空输入**：几何命中走上述表面着色；只有射线穿过已知遮挡范围、能确认环境可见时，才沿射线方向取线性 HDR 天空辐亮度 `Lsky(ω)` 参与余弦积分。仅因有限 `TMax` 截断、场景覆盖不足或请求未服务而没有命中，记为未解决，不写天空样本，也不当作已知黑色。已卷积的 ambient SH 辐照度不能当作逐方向 `Lsky` 再积分。下文 `rayMiss=None` 只约束屏幕消费者，不关闭这项天空输入。
2. **屏幕空间消费者**：保留余弦半球 SSGI。只有真实命中且颜色重投影有效才写屏幕信号；区分 trace miss、历史拒绝和无历史。混合模式不能先把旧 reflection probe/sky 写进 SS 信号。时域、双边和上采样内部可以用预乘颜色 + 覆盖率累积，但**最终写出必须是 unpremultiplied radiance + weight（rgb 未乘 weight）**，让材质端只做一次 `lerp`。probe 查询使用接收点 P、N，不是沿 miss 方向虚构交点。无 SS 输出的首帧/切镜仍可读已就绪的 probe；若 SSGI 本身未启用，则不引入独立 DDGI。**probe 查询必须跳过 inactive probe 并应用 relocation offset**；8 邻居权重归零后重新归一化，不能把 inactive 当黑色。
3. **能量契约（与现有 URP 契约对齐）**：SSGI 当前输出是 **unpremultiplied `(Lss, w)`**，材质执行 `lerp(base, Lss, w)`；`Pgi = Eprobe/π`，`base = lerp(bakedGI, Pgi, c)`。因此单位强度下 `indirectDiffuse = (1−w)·base + w·Lss`。c 是体积覆盖与有效性，不由亮度决定，且只乘一次。w=1 只取 SSGI；w=0,c=1 只取 probe；w=c=0 回 bakedGI（lightmap/APV/SH）。不要把 probe 预乘 w，也不要在 SSGI 纹理里再混一次；现有 SSGI 存储是 unpremultiplied radiance + weight（rgb 未乘 weight），禁止按预乘 `S` 再做 `lerp(base,S,w)`。强度 I>0 等价于 `lerp(base, Lss×I, w)`（颜色乘 I，权重不乘 I），I=0 沿用原路径的精确 no-op。接收面 albedo、AO 在既有材质链只应用一次。
   **反馈项**：probe 射线命中点在射线 radiance 上加 `min(albedo,cap)·Lambert()·Eprev(P,N)`；经余弦积分后，对 `Eprobe` 的贡献是 `min(albedo,cap)·Eprev`（常数场），只在 producer 端加一次。材质端仍只做 `E/π` 与一次 `lerp`，不得再乘反馈系数或 albedo。因为探针分批轮转，反馈是跨帧迭代，不是严格的同帧 1 跳；G5 按阶跃响应与衰减判定，不能只测一帧。
   **与 SSGI fallback 互斥**：启用动态 probe 回退时，SSGI 的 `rayMiss` 必须设为 `None`，让 miss 写 `weight=0` 并由 `base` 填充；不能先把 Reflection Probes/Sky 写进 SS 信号再叠加 probe。关闭 probe 回退时恢复原 `rayMiss`。
4. **绑定与失效**：先在不透明材质合成端按位置、法线和邻居可见性查 probe；三种 renderer 共用权重合同。probe-ready 不得依赖 SSGI producedOutput，否则 207 关 SS 纹理时会丢弃已收敛的 probe；但查询必须用接收点 `P/N`，不是沿 miss 方向虚构交点。若查询超预算，再对比半分辨率查询和深度法线上采样。查询用 `probeWorldPos = gridPos + offset`；inactive/无效邻居权重归零后重新归一化。反馈读取已发布快照，不读正在写的 atlas；切镜只丢相机历史，不重置 probe state/offset。移动光源/发光体须在重访周期内被观察，不随相机旋转清空整个场。场景卸载、网格布局或数据版本不兼容才清相应 probe。

## 最小数据面与非目标

irradiance 八面体 atlas、距离矩 atlas、**probe data（offset + state）**、每 probe 更新时间/版本、**上一帧已发布快照（反馈与消费者读取）**、有界灯表 buffer、预算列表和本批 ray scratch；显存按数量×带边 tile×格式字节数，加实际 history/scratch 统计。必须明确一种发布语义（双缓冲或批次后显式版本/屏障），保证消费者不会读到半批数据；未发布 tile 是“无效/保留上次有效值”，不是黑色。v1 用人工布点的有界体积；relocation/classification 默认开，但多 volume、滚动、独立全屏 DDGI、透明/ShaderGraph 扩展、GI 逐像素 DXR 和递归多跳仍非 v1。不能用更重的时域滤波掩盖错误的可见性或能量。

## 独立测试与交付

| ID / 依赖 | 最小 fixture | 通过标准（预算和误差先在 B0 冻结） |
|---|---|---|
| G1 数学与存储 / B0 | CPU 或 compute 注入恒定 L、两色场、常距平面、非法邻居、relocated/inactive 邻居、注入 `Eprev` 的反馈项；不启动 DXR/SSGI | 编解码后 E/π=L；矩还原 d、d²；反馈项在线性场下对 E 的贡献等于 `albedo·Eprev`（不是 `/π`）；offset/state 索引不串 probe；断言内区、边界、八面接缝、全失效和有效黑色，非有限数 0。交付输入与读回值 |
| G1 天空输入 / B0 | 同一 CPU/compute fixture，关反馈；注入空场常量天空 `L`、带窗口的黑色无反射遮挡，以及只追到 `TMax`／覆盖不足／未服务的未解决样本 | 空场读回 `E/π=L`；窗口场景只积可见方向，`E` 与对同一组方向施加窗口掩码的余弦积分参考一致（遮挡方向仍计入半球归一化）；未解决样本的天空采样数为 0，状态与可见黑色天空不同。交付方向掩码、分类计数和 atlas 读回，误差按 B0 判定 |
| G2 更新 / G1+T3b+T4 | 仅测试生产者；单体积、移动遮挡/发光板、多灯、两台相机 | 每帧调度 ≤B、主射线 ≤B×R；inactive 只追 32；阴影射线 ≤ 主射线×Smax；上传灯数 ≤Lmax（登记数可超过容量）；静态轮转周期 ≤ceil(N/B) 帧；动态变化在 B0 时间窗内回到参考误差，同帧只更新一次；relocation 不抖动、classification 能重激活；未发布 tile 无效而非黑。记录更新时间、列表、灯/阴影计数、GPU ms、显存和动态阶跃曲线 |
| G3 合成 / B0 fixture，真实集成需 G2 | 固定接收面，扫 s,c 的 0/0.5/1 和强度 0/1；锁曝光移出红板、首帧/切镜、薄墙、体积外、双相机；关 SSR；启用 probe 回退时确认 SSGI rayMiss=None；**先 feedback/relocation/classification 全关跑基线**，再分别开 | 中间权重不暗边、不重复 BRDF；首帧已就绪显示 probe，冷启动回 baked；供体出屏后低频色溢保留；反馈开启后阶跃/衰减符合 B0；inactive 不出现黑块，relocated 不改变接收面亮度；测线性 ROI 误差和亮度跳变峰值对旧 fallback；薄墙背面不超过 B0 漏光阈值；probe 与旧 reflection/sky 不叠加；三种 renderer 和 RT-off 对照通过；SSR 的 H-A/H-B/H-C 选择不改变 GI 计数/图像 |
| G4 relocation + classification / G2 | 墙内 probe、空体素、动态几何进出、薄墙、两台相机；开关默认开 | offset 被限制在 B0 上限（参考实现为 0.45×probe 间距）；inactive 只追 32 条且消费者跳过；动态几何进入后在 B0 帧数内重激活；无索引串 probe、无抖动/振荡；记录 active/inactive/relocated 计数 |
| G5 1 跳反馈 / G2+T3b+T4 | 静态房间 + 红墙供体、灯开/关阶跃、移动发光体、薄墙、反馈 on/off、无效/relocated/inactive 邻居 | 与高采样参考的 ROI 在 B0 内；关灯后旧反馈在 B0 时间窗内衰减，不留下永久能量；不得重复乘 albedo/BRDF；不得递归追射线；计数与图像一起交付 |

证据分开提交 G1 数值、G2 场景更新、G3 合成、G4 维护、G5 反馈；不能拿调度计数代替画面证据。验收不通过只关该扩展，保留现有 SSGI。

## G2 生产者签收（Win64 D3D12）

[D3D12 报告](../validation/G2/evidence/D3D12/Report.json)与 [D3D11 拒绝记录](../validation/G2/evidence/D3D11/Report.json)来自同一构建 GUID。D3D11 在 `Backend` 拒绝，0 次 TLAS Build、0 次 probe 派发、0 字节生产者资源。D3D12 在真实 Build/bind/DispatchRays 下跑 581 个批次、149 次主生产者更新：每批 ≤16 个 active probes、每条 ≤128 条射线（32 固定）、inactive 只追 32 条、阴影 ≤主射线×2，全部批次通过冻结预算。首个批次发布 16 个 probe，第 16 个批次后 256/256 全部发布；房间中心 probe 为 active、墙内 probe 被 classification 置为 inactive；relocated probe 的 offset 被限制在 0.45×spacing，拆除遮挡后回零；动态阶跃在第 128 个批次收敛，第 256 个批次残差为红通道 0.9%；两台相机在同一场景帧只触发一次更新且读取同一版本。3 盏灯全部登记/上传。生产者资源 950,376 B（约 0.91 MiB）。CPU 提交平均 0.44 ms，最大 223 ms 出现在首帧（TLAS Build + 首次读回）。**GPU 计时未测到**：ProfilingSampler 在 Player 中采到 0 个样本，`gpuTimingMeasured=false`，不以 CPU 提交时间代替。本夹具只验证生产者；材质查询/合成（G3）、未发布 tile 的消费者行为与反馈图像（G5）尚未签收，T3b/T4 本轮未重跑。

## 实现落点与维护

- **落点**：共享 RT 场景服务在 201（`AfterRenderingPrePasses+1`）构建/复用 TLAS；灯表由共享 `Lights/` 服务在首个消费者之前就绪；probe 生产者在 SSGI dispatch 之前（可作为 SSGI pass 的 205 前置子阶段，避免占用 203 的 Hi-Z/Editor motion 与 204 GTAO），内部顺序为 trace → relocate → classify → irradiance/distance update → publish；材质端查询在 `ScreenSpaceLightingApplyIndirectDiffuse` 的调用点（需要把接收点 P/N 传入）。三种 renderer 共用同一材质合同。
- **开关**：独立 feature/Volume 选项 + 全局 keyword；总开关默认关，关闭时不分配 atlas/灯表订阅、不派发 probe 射线，原 SSGI/SSR 图像逐像素不变。启用后 feedback/relocation/classification 默认开，但各自有显式开关；关掉任一项必须回到 G3 基线，不留残影。
- **依赖**：GI 不引用 SSR Volume，SSR 不启动 probe 更新；GI 订阅共享 TLAS 与灯表，不拥有它们；基础设施失败只关闭 GI 扩展。probe 是辐照度缓存，不是 Surface Cache；SSR 的 H-A/H-B/H-C 选择不改变 G1–G5；GI 不分配 mesh card/atlas。
- **变体**：`GlxDXR` 命中 pass、probe 查询/反馈 shader 只对高端档构建；非目标平台剥离资源与变体，并验证无 shader 错误；记录 pass/variant 数。
- **证据**：`validation/<G ID>/<platform>-<renderer>/` 记录输入、预算、计数、读回/图像和已知差异；不覆盖 P0。
- **文档同步**：数学与参考改 [report.md](report.md)，场景/TLAS 改 [TLAS](../research/TLAS.md)；实现与文档口径不一致时先修文档再验收。
