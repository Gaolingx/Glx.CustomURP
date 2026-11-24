# 20-Ray_tracing 任务实施计划

已读完总计划、三个子计划（TLAS / GI / 反射）与命中着色、SSR-DXR 研究。下面按“方案 → 分工 → 依赖 → 顺序”列出，全部来自 [20-Ray_tracing/HANDOFF.md](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/TODO/20-Ray_tracing/HANDOFF.md) 的里程碑表。

## 1. 实施方案（v1 骨架）

不做 Surface Cache、不做递归多跳、不做逐像素 GI DXR。核心是“屏幕优先 + 世界空间补洞”，两条独立消费者共用一套场景服务：

```text
201 共享 TLAS/灯表（场景快照，按 revision 共享）
    ↓
probe 更新（首个启用 GI 的相机触发：trace→relocate→classify→更新→publish）
    ↓
203 Hi-Z → 205 SSGI → 206 SSR(206a 分类/请求 → 206b 固定 K DispatchRays → 206c 合成)
    ↓
207 材质绑定 → 210 GBuffer / 250 Forward opaque（BRDF 只应用一次）
```

四条不变量：

- **共享**：TLAS、`GlxDXR` 命中 ABI、场景灯表、ray mask、线性 HDR 单位。**不共享**：方向分布、屏幕历史、滤波器。
- **命中着色 = H-A**：Fat CHS + 每材质 `GlxDXR` pass，`UnityRayTracingFetchVertexAttribute3/4` 取 BLAS 顶点 + per-material 贴图，AHS 做 cutout；不需要 bindless/顶点池/atlas。
- **调度 = 固定 K / B×R + GPU 早退**（2022.3 无 GPU 间接 DispatchRays）；`K=0` 不提交，不引入阻塞读回。
- **合成 = unpremultiplied radiance + weight**；GI 用 `lerp(base, Lss, w)`，SSR 用 `a=s+(1−s)r`，均只 lerp 一次；启用 probe 回退时 SSGI `rayMiss` 必须为 `None`。

## 2. 任务分解与分工

按“单一所有者”原则分 6 条工作流；“分工”指代码/验收归属，不是人员安排。

| 工作流 | 任务 ID | 交付物（落点） | 签收要点 |
|---|---|---|---|
| **WS1 共享 RT 场景** | T1 门控与资源 | `Runtime/RendererFeatures/RayTracing/`：能力判定、RTAS、实例登记、Build 策略、清理 | 关闭/不支持时分配与派发为 0；双开只持有一个快照；重复开关净增长 0 |
| | T2 实例与调度 | 同上：登记映射、dirty/Build、8 位 mask、背面 flag | 与 CPU 解析交点一致；离屏仍命中；静止不重复 Build；正反面 A/B 过（见 [TLAS.md](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/TODO/20-Ray_tracing/research/TLAS.md)） |
| | T2b 材质覆盖（可选，并入 T2/T3b） | 多 submesh/LOD/禁用统计 | 不支持子网格显式计数，不静默当黑 |
| **WS2 命中着色 ABI** | T3 基础命中 Player | raygen/miss/payload 骨架 + 单色自发光板 | Player 中证明真实 Build/bind/DispatchRays；emit/direct/shadow 与解析参考一致 |
| | T3b 全材质 pass | `ShaderLibrary/RayTracing/HitShading.hlsl` + Lit 的 `GlxDXR` pass（CHS+AHS） | 贴图/法线/cutout 与光栅一致；payload 读回符合布局；记录 payload 字节与 variant 数（见 [HitShading.md](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/TODO/20-Ray_tracing/research/HitShading.md)） |
| **WS3 场景灯表** | T4 灯表与逐灯阴影 | `Runtime/RendererFeatures/RayTracing/Lights/`：注册/脏标记、`Lmax` 上传、`LmaxHit/Smax` | 不依赖 `visibleLights`；上传/选灯/阴影三层溢出分开计数；移动/删除/禁用更新正确 |
| **WS4 GI 探针** | G1 数学与存储 + 天空输入 | CPU/compute fixture，不启 DXR/SSGI | `E/π=L`、矩还原、反馈项 `albedo·Eprev`、八面接缝/全失效断言 |
| | G2 分帧更新 | `RayTracing/GI/` + HLSL：B×R 轮转、前 32 固定射线、双缓冲发布 | 调度 ≤B、主射线 ≤B×R、阴影 ≤主射线×Smax；同帧只更新一次 |
| | G3 合成 | `ScreenSpaceLightingApplyIndirectDiffuse` 调用点传入 P/N | 中间权重不暗边；首帧读已就绪 probe；薄墙漏光 ≤阈值；probe 与旧 fallback 不叠加 |
| | G4 relocation + classification | offset/state 数据面，默认开 | offset ≤0.45×间距；inactive 只追 32 且消费者跳过；无抖动/串索引 |
| | G5 1 跳反馈 | CHS 内一次 8 邻居查询（读上一帧 atlas） | 阶跃/衰减符合 B0；不递归、不重复乘 albedo；见 [gi/HANDOFF.md](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/TODO/20-Ray_tracing/gi/HANDOFF.md) |
| **WS5 SSR × DXR** | R1 分类与预算 | `SsrPass.cs` 子阶段 + `ScreenSpaceReflections.compute` 请求 kernel | 计数守恒；分类放在 `readColorPyramid==null` 早退之前；零预算回原环境 |
| | R2 离屏求交 | `RayTracingSsr.raytrace` + 合成 | 已服务射线命中正确实例/距离/颜色；未服务不能误标 sky |
| | R3 重建与回归 + 阴面源切换 | 混合合成 + 历史规则 | 轮廓召回率/错误率/切换跳变达标；SSR 单开时 GI 更新计数为 0 |
| | R4 命中着色分级 | H-A 登记范围 + 成本统计 | 只有登记子集参与；无 Surface Cache 资源；不重复接收面 BRDF |
| | R5 全材质反射与灯光预算 | 命中点 PBR + cutout + 灯表/阴影 | 与解析参考一致；超预算回退并计数；不做 probe 反馈 |
| | R2b 压缩/TMin 续传（v2 候选） | 实验分支 | **不阻塞 v1**；隐藏遮挡物不得漏遮挡，否则记 v2 NO-GO |
| **WS6 验证与证据** | 公共 harness | `Assets/Tests/RayTracing/{TLAS,GI,SSR,Integration}/` | fixture 可先验接口，**不能代替 Player 签收** |
| | I1 开关矩阵/隔离 | 同场景都关/GI/SSR/双开 × 三种 renderer | 单开无另一模块 dispatch；切镜/尺寸/相机堆叠无残影泄漏 |
| | I2 Player 画质/性能/平台 | Windows DXR + D3D12 off + D3D11 off + 非目标构建剥离 | 按 TLAS/probe/hit shading/relocation/SSR 分项计时；主机分开批准 |

证据统一落 `validation/<ID>/<platform>-<renderer>/`，不覆盖 [P0](file:///E:/GitHub/Glx.CustomURP/URPSample01/PLAN/TODO/20-Ray_tracing/validation/README.md)（P0 是 `720bedd` 的 D3D11 记录）。

## 3. 依赖关系

```text
B0 ──┬─ T1 ── T2 ── T3 ── T3b ── T4 ──┐
     │           │      │            ├─ G2 ── G3(真实集成)
     │           │      │            ├─ G2 ── G4
     │           │      │            └─ G5 (G2+T3b+T4)
     │           ├─ R2 (R1+T3) ── R3 / R3阴面 ── R2b(可选)
     │           ├─ R4 (R2+T3b)
     │           └─ R5 (R2+T3b+T4)
     ├─ G1 ──────────────────────── G2
     └─ R1 ──────────────────────── R2

T,G,R 全绿 ── I1 ── I2
```

- **硬前置**：T3b 必须先有 T3 的真实派发；T4 依赖 T3b 的 CHS 才能验阴影；G2/G5 与 R5 都卡在 T3b+T4；I1 必须等三个模块都能单独开关。
- **可并行**：G1、R1 只需 B0，可与 T1/T2 同期开工；T4 的 CPU 登记可在 T2 后先写，GPU 签收等 T3b；G3 的 fixture 部分可在 B0 后先跑，真实合成等 G2。
- **禁止反向依赖**：基础设施不引用 GI/SSR Volume；GI 不启动 SSR 射线，SSR 不启动 probe 更新；GI 只读 SSR 之外的已发布快照。

## 4. 执行顺序

| 波次 | 内容 | 说明 |
|---|---|---|
| W0 | **B0** | 冻结设备/版本/场景/轨迹/阈值 + `Lmax/LmaxHit/Smax`、payload ≤64B、variant 上限、`B×R/K`、32 条固定射线、feedback/reloc/classify 开关；D3D11 与 D3D12 各跑两遍 RT-off 逐像素一致 |
| W1 | T1；G1（并行） | 门控先用 fixture 定接口；G1 纯数学不碰 GPU 派发 |
| W2 | T2；R1（并行） | TLAS 实例/调度与 SSR 分类可同时开发 |
| W3 | T3 | 第一次真实 Build/bind/DispatchRays 签收 |
| W4 | T3b | `GlxDXR` 全材质 pass，从此 GI/反射才有命中着色 |
| W5 | T4；R2（并行，R1+T3） | 灯表与 SSR 离屏求交并行 |
| W6 | G2；R3/R3阴面；R4 | G2 依赖 G1+T3b+T4；R4 依赖 R2+T3b |
| W7 | G3（真实集成）、G4、G5、R5；R2b（可选） | G3 先 feedback/reloc/classify 全关跑基线；R2b 不阻塞 |
| W8 | **I1** | 都关/GI/SSR/双开 × Forward/Forward+/Deferred，再扫各开关 |
| W9 | **I2** | Windows DXR 实派发 + 同机 D3D12/D3D11 off + 非目标构建剥离；主机单独验收 |

**关键路径**：`B0 → T1 → T2 → T3 → T3b → T4 → G2 → G5/G3 → I1 → I2`。这是最长链，WS4 的 G2 是整条链的汇聚点。

## 5. 停止条件（按计划原文）

1. T3/T3b/T4 无法在目标后端证明真实 Build/bind/DispatchRays、材质 pass/AHS 与灯表 → G/R 只保留 fixture，真实 RT 路径保持禁用。
2. B0 无法冻结新增预算（灯数/阴影/payload/variant/反馈）→ 不进入 I2。
3. 某项通过需要扩大平台/材质/透明范围 → 按未通过记录，不改门槛、不回退基础 URP。
