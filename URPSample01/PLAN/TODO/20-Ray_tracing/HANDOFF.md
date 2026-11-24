# 20 · 高端平台可选光追：实施与签收入口

2026-11-24｜研究与计划，**未实现、未做本轮 GPU 测量**。结论：在 Unity 2022.3 / URP 14 上，Windows D3D12 有可验证的直接 DXR 派发路径；当前屏幕效果的时序允许接入，但不是移植 HDRP Mixed 的四张命中 GBuffer。主机是候选后端，不是已支持平台。研究索引与证据口径见 [research/README.md](research/README.md)。

| 阅读顺序 | 决策归属 |
|---|---|
| [可行性核查](#可行性核查2026-11-24) | 本轮在本地 API/源码/项目设置中核实的事实与未闭环风险 |
| [本轮调研总报告](research/report.md) | 平台/API 边界、URP 合同、HDRP/Lumen 对照、Surface Cache 取舍、无缓存 recipe、探针与总验收；含引用等级、风险与勘误 |
| [HDRP 14 源码研究](research/HDRP14.md) | SSGI/RTGI/RTR 的真实数据流、最佳实践与不可移植部分 |
| [共享 RT 场景与 TLAS](research/TLAS.md) | 平台门控、实例、材质命中、构建/失效、生命周期、直接派发 |
| [SSR × DXR 集成](research/SSR-DXR.md) | 未解决样本、固定预算派发、合成、历史与独立测试 |
| [GI 子计划](gi/HANDOFF.md) / [探针数学与参考](gi/report.md) | 动态更新世界空间探针，只填 SSGI 没解决的漫反射 |
| [不透明反射子计划](reflection/HANDOFF.md) | SSR 未解决射线由 DXR 查几何，并保持独立镜面历史 |

## 可行性核查（2026-11-24）

**结论：可行，但只在锁定范围内可行。** 不是“把 HDRP Mixed 搬过来”，而是“现有 SSGI/SSR 保持原路径，只给未解决样本加世界空间来源”。

已核实（可指向行）：

- Unity 2022.3.62f1 公开 API 有 `RayTracingAccelerationStructure`、`RayTracingShader`、`CommandBuffer.BuildRayTracingAccelerationStructure/DispatchRays`、`SystemInfo.supportsRayTracing`；RTAS 支持 8 位实例 mask、实例 ID、逐实例 transform/mask 更新与 `CullInstances`。
- 现有 URP 时序允许接入：Hi-Z 203 → SSGI 205 → opaque SSR 206 → 绑定 207 → GBuffer 210 / Forward opaque 250；GI/SSR 输出均为 **unpremultiplied radiance + weight（rgb 未乘 weight）**，材质只做一次 `lerp`。
- 项目 Windows 默认 API 为 D3D12（`ProjectSettings.asset:531–533`），质量档有 Performant/Balanced/High Fidelity；本机已有 D3D12 构建/捕获脚本；P0 基线是 `720bedd` 的 D3D11 记录，需另跑。
- 三种 renderer（Forward/Forward+/Deferred）共用现有 SSGI/SSR 材质合同；现有测试 renderer 资产可直接作为 RT 开关矩阵的宿主。

未闭环/必须按计划处理：

1. **材质命中入口**：Unity closest-hit 需要材质 shader 中与 `SetRayTracingShaderPass` 同名的 DXR pass（HDRP 用 `IndirectDXR`/`GBufferDXR` 等；**不是** `LightMode="RayTracing"` tag）；URP Lit/ComplexLit/ShaderGraph 默认没有。v1 只给进入 TLAS 的受支持不透明子集加最小命名 pass，并显式统计不支持材质。
2. **没有 GPU 间接派发**：2022.3 只能用 CPU 已知宽度 `K` 的固定 `DispatchRays` + GPU 早退；预算与溢出必须在 B0 冻结。
3. **探针回退与现有 SSGI fallback 互斥**：启用动态 probe 时 SSGI 的 `rayMiss` 必须为 `None`，miss 写 weight 0，由材质端 `base` 填充；否则会先写旧 reflection/sky 再叠加 probe。
4. **主机未验证**：PS5/Xbox 的 Unity RT 支持、SKU/质量档/性能均无本地证据；PC 通过不默认开启主机。
5. **性能未测**：TLAS 构建、probe 更新、SSR 补追、显存与帧时间均无本轮数据；B0 先冻结预算，再谈通过。

## 产品与帧契约

- RT 是**可裁剪的扩展**：默认资产不启用；只允许高端质量档 + 显式启用的合格后端 + 运行时能力全部满足。首发验证目标 Windows x64 D3D12 DXR。PS5、Xbox Series 分开验收（各 SKU/质量档），不把主机 API 当作 Windows D3D12；没有 SDK/设备证据就保持关闭。移动、Web、D3D11、其余 PC 后端和低档位不出队、不分配 RT 资源、不变更原 SSGI/SSR fallback。功能不要求更改项目的默认 Graphics API。
- 两个独立消费者：启用 SSGI 时可选动态 probe fallback；启用 opaque SSR 时可选 DXR。仅反射不得触发探针更新，仅 GI 不派发反射射线。两者都关即为现有 URP。无独立 DDGI 模式、无 GI 接收面逐像素 DXR、无 Surface Cache、无限滚动、多跳递归、透明反射或移动端替代后端。
- 帧序（设计，不是实测）：确定合格 base camera 和场景版本 → **共同**准备实例/灯光并构建 TLAS（在第一个 RT 消费者之前）→ 预算内 probe 更新，完成后发布一致的可读快照 → 既有 Hi-Z 203、SSGI 205、opaque SSR 206（含补追/resolve）→ 207 绑定 → GBuffer 210 / Forward opaque 250 只消费一次 BRDF → opaque 后复位。TLAS/探针属于场景；trace 输出、曝光/历史、关键词属于相机。v1 单视图不透明 game base camera；overlay、preview、反射相机、未验证的 XR 走原路径并显式清空本相机 RT 状态。
- 共享的是场景、材质命中 ABI、ray mask 与线性 HDR 辐亮度单位，而不是方向分布、屏幕历史或滤波器。SS/HW 源切换时不得将噪声、缺预算或黑色当作天空；无资源必须回原路径。基础设施不引用 GI 或 SSR Volume；效果之间无启动依赖。调度失败/失去设备/管线销毁释放资源；不留上帧全局绑定。

## 需求与长期约束

**产品需求**：RT 是高端 PC/主机的可选扩展，不是基础 URP 的一部分。默认资产不启用；只有高端质量档 + 显式启用的合格后端 + 运行时能力全部满足才工作。首发验证目标 Windows x64 D3D12 DXR；PS5、Xbox Series 按 SKU/质量档分开验收，不把主机 API 当作 Windows D3D12。

**功能需求**：

1. 两个独立消费者：启用 SSGI 时可选动态 probe 回退；启用 opaque SSR 时可选 DXR 补追。仅反射不得触发 probe 更新，仅 GI 不得派发反射射线。
2. 共享场景服务：一个兼容 TLAS 快照、共同材质命中 ABI、ray mask 与线性 HDR 单位；不共享方向分布、屏幕历史或滤波器。
3. 屏幕优先：屏幕内继续用现有 Hi-Z SSGI/SSR；只有真实未解决样本才进入世界空间路径。
4. fallback 显式：几何 miss、历史拒绝、未服务/无预算、无效材质分开计数；只有可信的远处 miss 才允许直接采天空。
5. 资源与历史隔离：GI/SSR 各自 history；切镜、尺寸/后端切换、设备丢失、场景卸载、管线重载都有确定失效与释放。
6. 可裁剪：每个模块独立关回原路径，无 shader error、无 RT build/dispatch/常驻图集；非目标构建剥离资源与变体。

**非功能需求**：

- 正确性先于性能：先用已知射线/解析交点/常量场验证，再计时；不能用画面“看起来对”代替断言。
- 预算先冻结：B0 在看候选结果之前签定光线/毫秒/显存上限；未定预算不报性能通过。
- 确定性：同一输入、相机轨迹、种子、曝光下可复跑；计数与图像一起留存。
- 可维护：研究行号版本化；共享服务单一所有者；效果之间无启动依赖；文档与实现口径不一致时先修文档。
- 不扩大范围：不为通过某项而静默加入移动/Web/D3D11、透明/蒙皮/ShaderGraph 全量支持或多跳。

**长期任务拆分规则**：每个 ID 是一个可独立重跑的工作单元，先冻结测试输入，再提交实现、测试、数据摘要和已知差异。依赖未就绪可用确定性 fixture 先验接口，但 fixture 不能代替 Player 集成签收。任何一项失败只关该扩展，不回退基础 URP，也不靠扩大平台/材质范围“修好”。

## 文档与代码落点（建议，实施时冻结）

| 模块 | 建议落点 | 边界 |
|---|---|---|
| 共享 RT 场景/TLAS | 内嵌 URP 包 `Runtime/RendererFeatures/RayTracing/`（与 SSL 同仓） | 拥有能力判定、RTAS、实例登记、Build 策略、清理；不被 GI/SSR Volume 反向引用 |
| 命中着色 ABI | 包内 `ShaderLibrary/RayTracing/` + 受支持材质的命名 DXR pass（如 `GlxDXR`；与 `SetRayTracingShaderPass` 一致） | v1 采用 H-A（per-material 最小 hit lighting）；只覆盖登记子集；不支持材质/子网格显式计数；H-B/H-C 见 recipe |
| GI 探针 | `Runtime/RendererFeatures/RayTracing/GI/` + 包内 HLSL；材质查询在 `ScreenSpaceLightingApplyIndirectDiffuse` 调用点 | 只在 SSGI 启用时工作；不拥有 TLAS |
| SSR × DXR | `SsrPass.cs` 子阶段 + `ScreenSpaceReflections.compute` 请求 kernel + `RayTracingSsr.raytrace`/合成 | 只在 opaque SSR 启用时工作；不启动 GI |
| Fixture/Player 测试 | `URPSample01/Assets/Tests/RayTracing/{TLAS,GI,SSR,Integration}/` | 输入、计数、图像、已知差异分开提交 |
| 证据 | `validation/<ID>/<platform>-<renderer>/` | 不覆盖 P0；失败/阻塞同样入库 |

若改用 project-side feature 而非内嵌包，必须先证明不依赖 URP internal API；不要维护两套。内嵌包与 SSL 一致，但会增加 fork 差异；每次改包都在提交说明中列出受影响文件。

## 推荐 recipe（v1，无 Surface Cache）

v1 明确不建 Surface Cache、不做 mesh cards/atlas、不做多跳。用“屏幕优先 + 少量 hit lighting + probe 低频”组成最小闭环：

```text
[SS]   SSGI/SSR screen trace（现有 Hi-Z）
       -> resolved：沿用现有 SS 取色/历史规则，毕业
       -> needsHW：追加到请求列表（分类时 append）
       -> invalid：回环境/反射探针

[HW]   固定 K 的 DispatchRays + GPU 早退
       -> CHS 返回 geomNormal / UV / instanceId / materialId / hitDistance
       -> Shade：emission + 一盏方向光（可选一条遮挡射线）
       -> 不乘接收面 Fresnel，不重复 BRDF

[合成] unpremultiplied (radiance, weight) 写回原 slot
       材质端 SSR: lerp(env, radiance, weight)；GI: lerp(base, Lss, w)；BRDF 只应用一次

[GI]   动态 probe：B probes × R rays/帧 -> irradiance atlas
       材质端按接收点 P/N 查询；SS 高频 + probe 低频

[Miss] 仅可信的远处 miss 采 Skylight；其余保持 Reflection Probe / Sky
```

| 层级 | 方案 | 采用条件 | 边界 |
|---|---|---|---|
| H-A（v1） | 受支持不透明材质 + 最小 hit lighting | 默认 | 只支持登记材质；每材质一个命名 DXR pass；不做 Surface Cache |
| H-B（v1.5 候选） | 共享 CHS + 材质 buffer（Surface-Cache-lite） | H-A 的 pass/variant 或 hit shading 成本成为瓶颈 | 仍不做 atlas 捕获；Unity 仍需每材质 pass；只覆盖简单材质 |
| H-C（v2 候选） | 完整 Surface Cache | 需要广材质覆盖、多跳或稳定降噪，且接受 mesh cards/atlas 系统工程 | 不是 v1 承诺；需独立立项与验收 |

2022.3 约束：没有 GPU 间接 `DispatchRays`，v1 用固定 K + GPU 早退；压缩/TMin 续传不作为 v1 基线，作为 R2b 性能实验（v2 候选）。probe 是辐照度缓存，不是 Surface Cache；SSR 不做 Surface Cache 不影响 GI。

## 里程碑与独立放行

各 ID 是可独立重跑的工作单元。每项先冻结测试输入，提交实现、测试、数据摘要和已知差异；依赖未就绪可用确定性 fixture 先验接口，不得以 fixture 代替 Player 集成签收。

| ID / 前置 | 交付 | 可单独复现的通过条件 |
|---|---|---|
| B0 / 无 | 冻结目标设备、版本、场景、灯光、相机轨迹、阈值、光线/毫秒/显存上限 | 同 HEAD 的 D3D11 和 D3D12 各自跑两遍 RT-off（项目默认 D3D12，需显式切 D3D11；P0 是 D3D11 记录）；关闭扩展与原路径同种子、同历史长度、同帧逐像素一致；记录现有测试筛选器及计数、`supportsRayTracing` 与目标 GPU；在相应 feature 启用时实测 201–203 的 pass 顺序，区分内置 early motion 与 Editor 相机 motion |
| T1–T3 / B0 | 后端门控、RTAS 与命中着色 | 见 [TLAS 独立测试](research/TLAS.md)；用已知射线验证，**不要求打开 SSGI 或 SSR** |
| G1–G3 / B0；真实更新依赖 T3 | 数据面、分帧更新、SSGI 合成 | 见 [GI 验收](gi/HANDOFF.md)；关 SSR，能分别验证常量场、动态恢复和出屏转场 |
| R1–R3 / B0；真实补追依赖 T3 | 未解决状态、离屏着色、反射重建 | 见 [反射验收](reflection/HANDOFF.md)；关 GI，用自发光物体验证离屏轮廓 |
| R4 / R2 | 无 Surface Cache 的命中着色分级与材质覆盖 | 见 [反射验收](reflection/HANDOFF.md)；记录 supported/unsupported 材质、pass/variant 数、hit shading GPU ms；只有登记子集参与，不重复 BRDF，不分配 Surface Cache/mesh card 资源 |
| R2b / R2（v2 候选，不阻塞 v1） | 光线压缩与 TMin 续传实验 | 见 [SSR-DXR](research/SSR-DXR.md)；记录 N/K、wave 利用率、隐藏遮挡物 A/B、TMin 漏遮挡与 GPU ms；未通过不影响 R1–R4 |
| I1 / T、G、R | 四开关组合、相机与资源隔离 | 同一场景分别跑都关/GI/SSR/两者开，Forward、Forward+、Deferred；双开共用同一 TLAS 版本；单开无另一模块 dispatch；切镜、分辨率变化、动态物体、相机堆叠和运行时关闭无上一相机残影/泄漏 |
| I2 / I1 | Player 画质/性能及平台签收 | Windows DXR 实派发 + 同机 D3D12 off + D3D11 off + 非目标构建剥离；记录下述指标。主机按目标分别构建、真机测量和批准，不阻塞 Windows 单独交付，不可据 PC 成绩默认开启 |

## 验证协议与回退

- [迁移的旧 P0](validation/P0/Baseline.json) 是 `720bedd` 的 **D3D11** 记录（EditMode 184、PlayMode 6、T12 34/34/35），不证明本轮 HEAD、DXR 或主机。保留原始记录/哈希和当时命令，不改写 `round`。新结果放 `validation/<ID>/<platform>-<renderer>/`，不覆盖 P0。实施时从 `URPSample01` 运行原有 [EditMode](../../Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14EditModeTests.ps1)、[PlayMode](../../Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14PlayModeTests.ps1)、[T12](../../Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14GraphicsRegression.ps1)；新 DXR 合同另加 development Player，记录构建设置恢复结果。
- 每个证据包记录基线/实现 SHA、设备/SKU/驱动/API/质量档、分辨率、场景版本、种子、曝光、帧号、预热帧数、计时窗口、输入/输出路径、实际值/门槛、pass/fail/blocked。图像对比在线性 HDR、同轨迹同帧；高采样参考与低预算候选用相同材质和光源。GI 比 ROI 误差和出屏亮度跳变；反射比正确轮廓覆盖率和 SS/HW 切换亮度差，而不是只看非黑像素。
- 性能按 TLAS CPU/GPU、probe trace/update、SSR trace/shade、重建分别计，另记总帧差、峰值显存、合格/请求/派发/命中/真 miss/未服务数。建议基线 1080p、固定轨迹、预热后 120 帧 × 3，给中位数和 P95；冷启动另列。B0 在看候选结果**之前**签定数值预算，未定预算不报性能通过。计数为零、全黑、无 NaN 或 Editor 截图都不能单独证明 DXR 生效。
- 每个模块独立关回原路径，无 shader error、无 RT build/dispatch/常驻图集；失败优先收窄支持材质、实例范围或光线预算，不静默扩大适用平台。每个里程碑更新状态和证据指针；只在真实 Player 通过后把“计划”改为“支持”。本轮仅交付 Markdown 和原证据迁移。
- **停止条件**：若 T3 无法在目标后端证明真实 Build/bind/DispatchRays 与解析命中，G/R 只保留 fixture 结果，真实 RT 路径保持禁用；若 B0 无法冻结预算，不进入 I2；若某项通过需要扩大平台/材质/透明范围，按未通过记录，不修改门槛。
- **文档同步**：研究行号/API 变更先更新 [research](research/README.md)，再改里程碑；验收证据与计划口径冲突时以证据为准并修文档，不保留两套说法。
