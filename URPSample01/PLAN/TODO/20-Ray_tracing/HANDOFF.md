# 20 · 高端平台可选光追：实施与签收入口

**状态：W0 基线与 W1 的 T1/G1 夹具已通过（见[任务实施记录](todolist.md)）；T2 已验证几何求交，[T3](validation/T3/Summary.json) 已在 Windows D3D12 Player 验证单色材质的自发光、直射和真实遮挡；[T3b](validation/T3b/Summary.json) 已验证 Windows metallic Lit 的贴图、法线、硬裁剪及主/阴影射线；W5 的 [T4 场景灯表/逐灯阴影](validation/T4/evidence/Summary.json) 与 [R2 离屏反射补追](validation/R2/evidence/Summary.json) 已在同一 Win64 Player 上通过。W6 的 [G2 探针分帧更新](validation/G2/evidence/Summary.json)（仅生产者）、[R3 阴面源切换](validation/R3/evidence/Summary.json) 与 [R4 登记范围内命中着色](validation/R4/evidence/Summary.json) 已在 Win64 D3D12 Player 通过；W7 的 [G4 relocation/classification](validation/G4/evidence/Summary.json)、[G5 1 跳反馈](validation/G5/evidence/Summary.json) 与 [R5 全材质反射/灯光预算](validation/R5/evidence/Summary.json) 已在同一后端分别通过生产者侧夹具（R5 不做相机合成），[R3 图像回归](validation/R3/image-evidence/Summary.json) 已在 Forward/ForwardPlus/Deferred 通过（Deferred 召回 `0.971`、离屏 `0.959`，C7 关闭：根因是构建期剥离与 GBuffer 缺关键词）；W7 的 [G3a 探针合成](validation/G3/evidence/Summary.json) 已由真实 feature 在 Forward/ForwardPlus 通过（常量场 ROI `0.0005` 相对误差、c=0.5 线性、SSGI 同帧不重复叠加、体积外回 baked、冷启动回 baked、D3D11 逐字节回退），共享 TLAS 所有权已上移到 Screen Space Lighting feature；[G3b](validation/G3/evidence/Summary.json) 把同一夹具扩到 Deferred 并关闭 C7（常量场 ROI `0.0005`、三种 renderer 全帧哈希一致、D3D11 回退）：根因是构建期只统计 quality/default pipeline 的 shader features 导致 `Lit/GBuffer` 被剥成 0 变体，加上 GBuffer pass 未声明 `_SCREEN_SPACE_GI_PROBE`，并非 deferred 光照不消费 SSL。[G3c](validation/G3c/evidence/Summary.json) 在三 renderer 上验证三开关（`off` 1.2916 → classification/relocation 1.9189、4 个偏移≤0.45、图像不变）、冻结 inactive tile 不被消费者读取（暗阶跃 3.2%）、SSGI 同帧混合逐像素差 0、切镜首帧即就绪且 probe state 不变、双相机 2 条独立历史、反馈固定点 2.5410（解析 2.56）及 256 帧衰减 3.0%、6 cm 隔板背面/亮面 1.9%。W8 的 [I1 开关矩阵/隔离](validation/I1/evidence/Summary.json) 已在同一 Win64 Player 以两种 API、三 renderer 通过（GUID `804703f3…`，base `f2d88ab`）：`off` 0 场景；`gi`/`ssr` 单开互不派发（GI 40 批且无 SSR 队列，SSR 无生产者/0 批且只订阅 Reflections）；`both` 复用同一 15 实例 TLAS（仅 1 次 build）与灯表（每次灯变化 1 次上传，2 订阅）；GI ROI `1.9209`（解析 1.92）、硬件镜像 `G−R=3.998`；三维护开关 8 组合、T3b 预算 `K→0→K`、T4 阴影/启停均按预期隔离；运行时关闭回到 `off` 逐字节图 `bb37710f…`；切镜首帧与稳定值同、640×360 重分配 1 条历史、Base+Overlay 每帧 ≤1 批、动态供体移出后镜像信号归零并在移回后恢复。D3D11 所有阶段 0 RT，四配置与 `runtimeOff` 全帧逐字节同为 `bb37710f…`，且与 D3D12 的 RT-off 图相同。W9 的 [I2 1080p 画质/性能/剥离](validation/I2/evidence/Summary.json) 已在同一 Win64 Player 通过（GUID `4fb75af9…` 见证据）：120 预热 + 120×3 的三 renderer 定时，RT GPU 增量 `0.41 ms`（B0 4）、CPU TLAS submit `0.029 ms`（0.5）、GPU TLAS `0.042 ms`（1）、probe `0.16 ms`（1.25）、relocate/classify `0.017 ms`（0.25）、SSR trace `0.055 ms`（1.75）、SSR reconstruction `0.15 ms`（0.5）、驱动显存增量 `76.7 MiB`（256，代理）；RT-on 改变 `1.86M` 像素、镜面 `1.40×`、非有限 0；正常构建 16 个 Lit DXR set、D3D11-only 0 个；同机 D3D11 off 0 RT。P0-1 补做后剥离口径扩展到全部 RT-only 变体（`GlxDXR` pass + `_SCREEN_SPACE_GI_PROBE`）：Win64 保留 16 个 GlxDXR set 与 4160 个 probe keyword set，D3D11-only 0/0、0 条 `GlxProbeQuery` 警告（见 [I2 剥离审计](validation/I2/evidence/strip-audit/README.md)）；资产级残留 3 个 RT shader（约 16.8 KB）待例外确认。GPU 计时的根因是 Recorder 默认关闭。主机未签收，不据 PC 成绩默认开启。**HEAD 复跑（`aceb6ca`）**：I1（GUID `1c96f2e7…`）、G3c（`e40d48cf…`）、R3 切换（`e88809aa…`）与 R3 图像（`82d6c25d…`）逐字段与首轮一致（G3c D3D11 仅 `both` 阶段的全帧图像哈希随运行时刻变化，功能门槛 `off=classification=relocation` 全过）；I2 全量（`78f29ca4…`，剥离 `a984e348…`）画质/分项性能复现，RT-only 审计 16/4160 与 0/0；T14 三项回归 EditMode `184/184`、PlayMode `6/6`、T12 `34/34/35`，设置恢复 3/3。** 结论：在 Unity 2022.3 / URP 14 上，Windows D3D12 有可验证的直接 DXR 派发路径；当前屏幕效果的时序允许接入，但不是移植 HDRP Mixed 的四张命中 GBuffer。主机是候选后端，不是已支持平台。研究索引与证据口径见 [research/README.md](research/README.md)。

| 阅读顺序 | 决策归属 |
|---|---|
| [已核实事实与待闭环项](#已核实事实与待闭环项) | 在本地 API/源码/项目设置中核实的事实与未闭环风险 |
| [调研总报告](research/report.md) | 平台/API 边界、URP 合同、HDRP/Lumen 对照、Surface Cache 取舍、无缓存 recipe、探针与总验收；含引用等级、风险与易错点 |
| [HDRP 14 源码研究](research/HDRP14.md) | SSGI/RTGI/RTR 的真实数据流、最佳实践与不可移植部分 |
| [共享 RT 场景与 TLAS](research/TLAS.md) | 平台门控、实例、材质命中、构建/失效、生命周期、直接派发 |
| [SSR × DXR 集成](research/SSR-DXR.md) | 未解决样本、固定预算派发、合成、历史与独立测试 |
| [GI 子计划](gi/HANDOFF.md) / [探针数学与参考](gi/report.md) | 动态更新世界空间探针，只填 SSGI 没解决的漫反射 |
| [不透明反射子计划](reflection/HANDOFF.md) | SSR 未解决射线由 DXR 查几何，并保持独立镜面历史 |
| [命中着色研究](research/HitShading.md) | 无 bindless/inline RayQuery 的全材质 Fat CHS、AnyHit alpha test、灯表与探针反馈 |

## 已核实事实与待闭环项

**结论：可行，但只在锁定范围内可行。** 不是“把 HDRP Mixed 搬过来”，而是“现有 SSGI/SSR 保持原路径，只给未解决样本加世界空间来源”。

已核实（可指向行）：

- Unity 2022.3.62f1 公开 API 有 `RayTracingAccelerationStructure`、`RayTracingShader`、`CommandBuffer.BuildRayTracingAccelerationStructure/DispatchRays`、`SystemInfo.supportsRayTracing`；RTAS 支持 8 位实例 mask、实例 ID、逐实例 transform/mask 更新与 `CullInstances`。
- 现有 URP 时序允许接入：Hi-Z 203 → SSGI 205 → opaque SSR 206 → 绑定 207 → GBuffer 210 / Forward opaque 250；GI/SSR 输出均为 **unpremultiplied radiance + weight（rgb 未乘 weight）**，材质只做一次 `lerp`。
- 项目 Windows 默认 API 为 D3D12（`ProjectSettings.asset:531–533`），质量档有 Performant/Balanced/High Fidelity；本机已有 D3D12 构建/捕获脚本；P0 基线是 `720bedd` 的 D3D11 记录，需另跑。
- 三种 renderer（Forward/Forward+/Deferred）共用现有 SSGI/SSR 材质合同；现有测试 renderer 资产可直接作为 RT 开关矩阵的宿主。

未闭环/必须按计划处理：

1. **材质命中入口与全材质着色**：Unity closest-hit 需要材质 shader 中与 `SetRayTracingShaderPass` 同名的 DXR pass（HDRP 用 `IndirectDXR`/`GBufferDXR` 等；**不是** `LightMode="RayTracing"` tag）；URP Lit/ComplexLit/ShaderGraph 默认没有。v1 给进入 TLAS 的登记不透明子集加 `GlxDXR` pass（CHS + AHS）：`UnityRayTracingFetchVertexAttribute3/4` 取 BLAS 顶点，per-material 绑 `_BaseMap/_BumpMap/_MetallicGlossMap/_EmissionMap`，`_ALPHATEST_ON` 走 AnyHit；不需要 bindless、顶点池或 atlas。代价是每材质 pass 与 variant；ShaderGraph/透明/蒙皮显式计数。见 [HitShading.md](research/HitShading.md)。
2. **没有 GPU 间接派发**：2022.3 只能用 CPU 已知宽度 `K` 的固定 `DispatchRays` + GPU 早退；预算与溢出必须在 B0 冻结。
3. **探针回退与现有 SSGI fallback 互斥**：启用动态 probe 时 SSGI 的 `rayMiss` 必须为 `None`，miss 写 weight 0，由材质端 `base` 填充；否则会先写旧 reflection/sky 再叠加 probe。
4. **主机未验证**：PS5/Xbox 的 Unity RT 支持、SKU/质量档/性能均无本地证据；PC 通过不默认开启主机。
5. **性能未测**：TLAS 构建、probe 更新、SSR 补追、显存与帧时间均无实测数据；B0 先冻结预算，再谈通过。
6. **全材质命中着色的覆盖与变体**：`GlxDXR` 需要每个登记材质一个 pass；keyword 组合（`_NORMALMAP/_METALLICSPECGLOSSMAP/_EMISSION/_ALPHATEST_ON`）会放大 variant。v1 只承诺 Lit 不透明 + cutout + 自发光；specular 流、ComplexLit、SimpleLit、ShaderGraph、SkinnedMesh 要单独统计/验证。
7. **灯光与探针维护成本**：全灯 + 逐灯阴影 + 8 邻居反馈会让命中成本显著高于最小命中着色；relocation/classification 的固定射线与 compute pass 也要计入预算。B0 冻结 `Lmax/LmaxHit/Smax`、feedback 开关、32 条固定射线与 variant 上限。

## 产品与帧契约

- RT 是**可裁剪的扩展**：默认资产不启用；只允许高端质量档 + 显式启用的合格后端 + 运行时能力全部满足。首发验证目标 Windows x64 D3D12 DXR。PS5、Xbox Series 分开验收（各 SKU/质量档），不把主机 API 当作 Windows D3D12；没有 SDK/设备证据就保持关闭。移动、Web、D3D11、其余 PC 后端和低档位不出队、不分配 RT 资源、不变更原 SSGI/SSR fallback。功能不要求更改项目的默认 Graphics API。
- 两个独立消费者：启用 SSGI 时可选动态 probe fallback（含全材质命中着色、逐灯阴影、1 跳反馈、relocation/classification）；启用 opaque SSR 时可选 DXR（同一 `GlxDXR` pass 的完整 PBR 命中着色与同一灯表，不做 probe 反馈）。仅反射不得触发探针更新，仅 GI 不派发反射射线。两者都关即为现有 URP。无独立 DDGI 模式、无 GI 接收面逐像素 DXR、无 Surface Cache、无 Thin CHS/材质表、无递归多跳 PT、无透明/ShaderGraph RT、无滚动/多 volume 或移动端替代后端。
- 帧序（设计，不是实测）：确定合格 base camera 和场景版本 → **共同**准备实例与灯表并构建 TLAS（在第一个 RT 消费者之前）→ 预算内 probe 更新（含 relocation/classification、反馈读取上一帧快照），完成后发布一致的可读快照 → 既有 Hi-Z 203、SSGI 205、opaque SSR 206（含补追/resolve）→ 207 绑定 → GBuffer 210 / Forward opaque 250 只消费一次 BRDF → opaque 后复位。TLAS/探针/灯表属于场景；trace 输出、曝光/历史、关键词属于相机。v1 的“单视图”指每台合格的非 XR game base camera 渲染一个不透明视图，不限制场景只能有一台 base camera。同一场景帧由首个启用 GI 的合格相机触发一次 probe 更新，其余合格相机复用已发布快照；各相机的 SS 输出、历史与绑定独立；overlay、preview、反射相机、未验证的 XR 走原路径并显式清空本相机 RT 状态。
- 共享的是场景、材质命中 ABI、场景级灯表、ray mask 与线性 HDR 辐亮度单位，而不是方向分布、屏幕历史或滤波器。SS/HW 源切换时不得将噪声、缺预算或黑色当作天空；无资源必须回原路径。基础设施不引用 GI 或 SSR Volume；效果之间无启动依赖。调度失败/失去设备/管线销毁释放资源；不留上帧全局绑定。

## 需求与长期约束

**产品需求**：RT 是高端 PC/主机的可选扩展，不是基础 URP 的一部分。默认资产不启用；只有高端质量档 + 显式启用的合格后端 + 运行时能力全部满足才工作。首发验证目标 Windows x64 D3D12 DXR；PS5、Xbox Series 按 SKU/质量档分开验收，不把主机 API 当作 Windows D3D12。

**功能需求**：

1. 两个独立消费者：启用 SSGI 时可选动态 probe 回退（全材质命中着色 + 逐灯阴影 + 1 跳反馈 + relocation/classification）；启用 opaque SSR 时可选 DXR 补追（同一 `GlxDXR` 的完整 PBR 命中着色 + 同一灯表，不做 probe 反馈）。仅反射不得触发 probe 更新，仅 GI 不得派发反射射线。
2. 共享场景服务：一个兼容 TLAS 快照、共同材质命中 ABI、场景级灯表、ray mask 与线性 HDR 单位；不共享方向分布、屏幕历史或滤波器。
3. 屏幕优先：屏幕内继续用现有 Hi-Z SSGI/SSR；只有真实未解决样本才进入世界空间路径。
4. fallback 显式：几何 miss、历史拒绝、未服务/无预算、无效材质分开计数；只有可信的远处 miss 才允许直接采天空。
5. 资源与历史隔离：GI/SSR 各自 history；切镜、尺寸/后端切换、设备丢失、场景卸载、管线重载都有确定失效与释放。
6. 可裁剪：每个模块独立关回原路径，无 shader error、无 RT build/dispatch/常驻图集；非目标构建剥离资源与变体。
7. 命中着色：`GlxDXR` 在 CHS 内解包完整 SurfaceData（albedo/normal/metallic/smoothness/emission），AHS 处理 cutout；命中点直接光来自灯表，逐灯阴影受预算约束；不乘接收面 Fresnel，不重复接收面 BRDF；不支持材质/子网格显式计数。
8. 探针维护：GI 反馈只读上一帧已发布 atlas（一次 8 邻居查询，不递归追射线）；relocation/classification 默认开，消费者跳过 inactive probe 并使用 relocation offset；固定 32 条射线与阈值在 B0 冻结。

**非功能需求**：

- 正确性先于性能：先用已知射线/解析交点/常量场验证，再计时；不能用画面“看起来对”代替断言。
- 预算先冻结：B0 在看候选结果之前签定光线/毫秒/显存/命中灯数/阴影射线/payload/variant 上限；未定预算不报性能通过。
- 确定性：同一输入、相机轨迹、种子、曝光下可复跑；计数与图像一起留存。
- 可维护：研究行号版本化；共享服务单一所有者；效果之间无启动依赖；文档与实现口径不一致时先修文档。
- 不扩大范围：不为通过某项而静默加入移动/Web/D3D11、透明/蒙皮/ShaderGraph 全量支持、递归多跳，或把 `LmaxHit/Smax` 抬到全场景全灯。

**长期任务拆分规则**：每个 ID 是一个可独立重跑的工作单元，先冻结测试输入，再提交实现、测试、数据摘要和已知差异。依赖未就绪可用确定性 fixture 先验接口，但 fixture 不能代替 Player 集成签收。任何一项失败只关该扩展，不回退基础 URP，也不靠扩大平台/材质范围“修好”。

## 文档与代码落点（建议，实施时冻结）

| 模块 | 建议落点 | 边界 |
|---|---|---|
| 共享 RT 场景/TLAS | 内嵌 URP 包 `Runtime/RendererFeatures/RayTracing/`（与 SSL 同仓） | 拥有能力判定、RTAS、实例登记、Build 策略、清理；不被 GI/SSR Volume 反向引用 |
| 命中着色 ABI | 包内 `ShaderLibrary/RayTracing/HitShading.hlsl` + 受支持材质的命名 DXR pass（如 `GlxDXR`；与 `SetRayTracingShaderPass` 一致） | v1 采用 H-A（Fat CHS：完整 SurfaceData + 直接光 + AHS cutout）；只覆盖登记子集；不支持材质/子网格显式计数；H-B/H-C 见 recipe；方法见 [HitShading.md](research/HitShading.md) |
| 场景灯表 | `Runtime/RendererFeatures/RayTracing/Lights/` | 注册/脏标记维护 Directional/Point/Spot；不依赖相机 `visibleLights`；容量与每命中灯数受 B0 约束；不拥有 TLAS |
| GI 探针 | `Runtime/RendererFeatures/RayTracing/GI/` + 包内 HLSL；材质查询在 `ScreenSpaceLightingApplyIndirectDiffuse` 调用点 | 只在 SSGI 启用时工作；不拥有 TLAS；producer 含 relocation/classification 与反馈读取 |
| SSR × DXR | `SsrPass.cs` 子阶段 + `ScreenSpaceReflections.compute` 请求 kernel + `RayTracingSsr.raytrace`/合成 | 只在 opaque SSR 启用时工作；不启动 GI |
| Fixture/Player 测试 | `URPSample01/Assets/Tests/RayTracing/{TLAS,GI,SSR,Integration}/` | 输入、计数、图像、已知差异分开提交 |
| 证据 | `validation/<ID>/<platform>-<renderer>/` | 不覆盖 P0；失败/阻塞同样入库 |

若改用 project-side feature 而非内嵌包，必须先证明不依赖 URP internal API；不要维护两套。内嵌包与 SSL 一致，但会增加 fork 差异；每次改包都在提交说明中列出受影响文件。

## 推荐 recipe（v1，无 Surface Cache）

v1 不建 Surface Cache、不做 mesh cards/atlas、不做递归多跳。用“屏幕优先 + 全材质 Fat CHS + probe 低频/反馈”组成闭环：

```text
[SS]   SSGI/SSR screen trace（现有 Hi-Z）
       -> resolved：沿用现有 SS 取色/历史规则，毕业
       -> needsHW：追加到请求列表（分类时 append）
       -> invalid：回环境/反射探针

[HW]   固定 K / B×R 的 DispatchRays + GPU 早退
       -> GlxDXR CHS：SurfaceData(albedo/normal/metallic/smoothness/emission)
          + 灯表（方向/点/聚光）+ 逐灯阴影
       -> AHS：alpha test（_ALPHATEST_ON + IgnoreHit）；阴影命中即遮挡
       -> probe 模式：+ 上一帧 atlas 反馈（1 次 8 邻居查询）
          reflection 模式：命中点完整 PBR，不做 probe 反馈
       -> 不乘接收面 Fresnel，不重复接收面 BRDF；hitT/valid/unsupported 分开

[合成] unpremultiplied (radiance, weight) 写回原 slot
       材质端 SSR: lerp(env, radiance, weight)；GI: lerp(base, Lss, w)；BRDF 只应用一次

[GI]   动态 probe：B probes × R rays/帧（前 32 条固定）
       relocation/classification 默认开 -> irradiance/distance atlas + probe state/offset
       active 追 R；inactive 只追 32；材质端按 P/N 查询并跳过 inactive

[Miss] 仅可信的远处 miss 采 Skylight；其余保持 Reflection Probe / Sky
```

| 层级 | 方案 | 采用条件 | 边界 |
|---|---|---|---|
| H-A（v1） | Fat CHS + per-material pass：完整 SurfaceData + AHS cutout + 灯表/逐灯阴影；GI 另加 1 跳反馈 | 默认 | 只支持登记不透明 Lit/cutout/自发光；每材质一个命名 DXR pass；不做 Surface Cache；灯光/阴影/反馈受 B0 预算 |
| H-B（v1.5 候选） | Thin CHS + 共享材质表（Surface-Cache-lite 的数据面） | 需要统一 BSDF/多跳/ReSTIR，或 pass/variant 成为瓶颈 | 仍无 bindless；材质表/复杂材质另证 |
| H-C（v2 候选） | 完整 Surface Cache | 需要广材质覆盖、多跳或稳定降噪，且接受 mesh cards/atlas 系统工程 | 不是 v1 承诺；需独立立项与验收 |

2022.3 约束：没有 GPU 间接 `DispatchRays`，v1 用固定 K/B×R + GPU 早退；压缩/TMin 续传不作为 v1 基线，作为 R2b 性能实验（v2 候选）。CHS 内打阴影需要 `#pragma max_recursion_depth`（HDRP PT 同构）；这不是递归多跳。probe 是辐照度缓存，不是 Surface Cache；SSR 不做 Surface Cache 不影响 GI。命中着色方法见 [HitShading.md](research/HitShading.md)。

## 里程碑与独立放行

各 ID 是可独立重跑的工作单元。每项先冻结测试输入，提交实现、测试、数据摘要和已知差异；依赖未就绪可用确定性 fixture 先验接口，不得以 fixture 代替 Player 集成签收。

| ID / 前置 | 交付 | 可单独复现的通过条件 |
|---|---|---|
| B0 / 无 | 冻结目标设备、版本、场景、灯光、相机轨迹、阈值、光线/毫秒/显存上限；新增 `Lmax/LmaxHit/Smax`、payload 字节、variant 上限、32 条固定射线、feedback/relocation/classification 开关与阈值 | 同 HEAD 的 D3D11 和 D3D12 各自跑两遍 RT-off（项目默认 D3D12，需显式切 D3D11；P0 是 D3D11 记录）；关闭扩展与原路径同种子、同历史长度、同帧逐像素一致；记录现有测试筛选器及计数、`supportsRayTracing` 与目标 GPU；在相应 feature 启用时实测 201–203 的 pass 顺序，区分内置 early motion 与 Editor 相机 motion |
| T1–T3 / B0 | 后端门控、RTAS 与基础命中着色 | 见 [TLAS 独立测试](research/TLAS.md)；用已知射线验证 emit/direct/shadow，**不要求打开 SSGI 或 SSR** |
| T3b / T2+T3 | `GlxDXR` 全材质 pass：CHS/AHS、BLAS 顶点属性、贴图/法线/alpha、payload 布局 | 见 [HitShading](research/HitShading.md)；贴图/法线/cutout 与光栅一致到 B0 容差；payload 读回符合布局；supported/unsupported 材质显式计数；记录 payload 字节与 variant 数 |
| T4 / T3b | 场景灯表与逐灯阴影 | CPU 登记全部合格 Directional/Point/Spot；GPU 灯表按 B0 的稳定顺序最多上传 `Lmax` 盏，单独记录未上传数。命中点仅从已上传且影响该点的灯中选取至多 `LmaxHit` 盏、发至多 `Smax` 条阴影射线；分别记录命中筛选与阴影预算的溢出；移动/删除/禁用灯更新；阴影开关影响正确；不依赖相机 `visibleLights`；记录灯数、每命中灯数、阴影射线条数与 overflow |
| G1–G3 / B0；G2 真实更新依赖 G1+T3b+T4，G3 真实集成依赖 G2 | 数据面、分帧更新、SSGI 合成 | 见 [GI 验收](gi/HANDOFF.md)；关 SSR；G3 先用 feedback/relocation/classification 全关跑基线，能分别验证常量场、动态恢复和出屏转场 |
| G4 / G2 | relocation + classification（默认开） | 固定 32 条射线、offset/state 数据面、消费者跳过 inactive 并应用 offset；墙内 probe 被推出、空区域 probe 不更新、动态几何进入后能重新激活；记录 active/inactive/relocated 计数 |
| G5 / G2+T3b+T4 | probe 1 跳反馈 | 灯光开/关阶跃、feedback on/off、无效/relocated/inactive 邻居、薄墙；与高采样参考的 ROI；旧光在 B0 时间窗内衰减；不能把反馈当递归多跳 |
| R1–R3 / B0；真实补追依赖 T3 | 未解决状态、离屏着色、反射重建 | 见 [反射验收](reflection/HANDOFF.md)；关 GI，用自发光物体验证离屏轮廓；命中着色先用 T3 基线 |
| R4 / R2+T3b | 命中着色分级与材质覆盖 | 见 [反射验收](reflection/HANDOFF.md)；记录 supported/unsupported 材质、pass/variant 数、hit shading GPU ms；只有登记子集参与，不重复 BRDF，不分配 Surface Cache/mesh card 资源 |
| R5 / R2+T3b+T4 | 反射全材质与灯光预算 | 命中点 PBR（metallic/smoothness/normal）+ cutout + 灯表/阴影；与 SS-only/高采样参考比轮廓与亮度接缝；灯数/阴影预算与 overflow 计数；不做 probe 反馈 |
| R2b / R2（v2 候选，不阻塞 v1） | 光线压缩与 TMin 续传实验 | 见 [SSR-DXR](research/SSR-DXR.md)；记录 N/K、wave 利用率、隐藏遮挡物 A/B、TMin 漏遮挡与 GPU ms；未通过不影响 R1–R5 |
| I1 / T、G、R | 开关组合、相机与资源隔离 | 同一场景分别跑都关/GI/SSR/两者开，Forward、Forward+、Deferred；再扫 feedback/relocation/classification 与 T3b/T4 开关；双开共用同一 TLAS 与灯表版本；单开无另一模块 dispatch；切镜、分辨率变化、动态物体、相机堆叠和运行时关闭无上一相机残影/泄漏 |
| I2 / I1 | Player 画质/性能及平台签收 | Windows DXR 实派发 + 同机 D3D12 off + D3D11 off + 非目标构建剥离；记录下述指标。主机按目标分别构建、真机测量和批准，不阻塞 Windows 单独交付，不可据 PC 成绩默认开启 |

## 验证协议与回退

- [迁移的旧 P0](validation/P0/Baseline.json) 是 `720bedd` 的 **D3D11** 记录（EditMode 184、PlayMode 6、T12 34/34/35），不证明当前 HEAD、DXR 或主机。保留原始记录/哈希和当时命令，不改写 `round`。新结果放 `validation/<ID>/<platform>-<renderer>/`，不覆盖 P0。实施时从 `URPSample01` 运行原有 [EditMode](../../Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14EditModeTests.ps1)、[PlayMode](../../Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14PlayModeTests.ps1)、[T12](../../Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14GraphicsRegression.ps1)；新 DXR 合同另加 development Player，记录构建设置恢复结果。
- 每个证据包记录基线/实现 SHA、设备/SKU/驱动/API/质量档、分辨率、场景版本、种子、曝光、帧号、预热帧数、计时窗口、输入/输出路径、实际值/门槛、pass/fail/blocked。图像对比在线性 HDR、同轨迹同帧；高采样参考与低预算候选用相同材质和光源。GI 比 ROI 误差和出屏亮度跳变；反射比正确轮廓覆盖率和 SS/HW 切换亮度差，而不是只看非黑像素。命中着色另记 payload 字节、pass/variant 数、每次派发灯数/阴影射线条数、supported/unsupported 材质、cutout A/B、feedback/relocation/classification 计数。
- 性能按 TLAS CPU/GPU、probe trace/update、hit shading（材质解包/灯光/阴影/反馈）、relocation/classification、SSR trace/shade、重建分别计，另记总帧差、峰值显存、合格/请求/派发/命中/真 miss/未服务数。建议基线 1080p、固定轨迹、预热后 120 帧 × 3，给中位数和 P95；冷启动另列。B0 在看候选结果**之前**签定数值预算，未定预算不报性能通过。计数为零、全黑、无 NaN 或 Editor 截图都不能单独证明 DXR 生效。
- 每个模块独立关回原路径，无 shader error、无 RT build/dispatch/常驻图集；失败优先收窄支持材质、实例范围、灯光/阴影预算或光线预算，不静默扩大适用平台，也不静默把 `LmaxHit/Smax` 抬到全场景。每个里程碑更新状态和证据指针；只在真实 Player 通过后把“计划”改为“支持”。W0 的输入与预算见 [B0 冻结契约](validation/B0/Freeze.json)，执行状态见[任务清单](todolist.md)；原 P0 保持不变。
- **停止条件**：若 T3/T3b/T4 无法在目标后端证明真实 Build/bind/DispatchRays、材质 pass/AHS 与灯表，G/R 只保留 fixture 结果，真实 RT 路径保持禁用；若 B0 无法冻结新增预算（灯数/阴影/payload/variant/反馈），不进入 I2；若某项通过需要扩大平台/材质/透明范围，按未通过记录，不修改门槛。
- **文档同步**：研究行号/API 变更先更新 [research](research/README.md)，再改里程碑；验收证据与计划口径冲突时以证据为准并修文档，不保留两套说法。
