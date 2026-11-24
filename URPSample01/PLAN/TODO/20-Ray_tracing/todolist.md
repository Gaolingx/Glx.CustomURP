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

**W0 执行记录**：`d4592ac` 的 [B0 冻结契约](validation/B0/Freeze.json) 已固定目标机、原路径输入、每帧预算与验收阈值；数值是门槛，不是实测成绩。同一 Development Player（GUID `a00234cc70ff4afba3b476f5186a71b7`）做了四次独立进程回放：[D3D11](validation/B0/Win64-D3D11-Forward/run-02/Pair.json) 和 [D3D12](validation/B0/Win64-D3D12-Forward/run-02/Pair.json) 各自第 33 帧的 8,294,400 个 RGBA16F 通道逐位相同，`supportsRayTracing` 分别为 false/true。这里签收的是 RT-off 原路径可复现，不是候选关闭后的回归或真实 DXR 派发。

**W0 判定：B0 通过（限冻结的 Forward Player、Windows 目标机及原路径）。** W1 的 T1/G1 前置条件已满足。

**W1 判定：T1/G1 夹具通过。** [专用 EditMode 复跑入口](validation/Invoke-W1.ps1)。

- T1：[D3D11](validation/T1/Win64-D3D11/run-04/Tests.xml)、[D3D12](validation/T1/Win64-D3D12/run-04/Tests.xml) 各 15/15。验证相机/质量/后端门控、共享单份资源、重复开关、场景关闭及释放。RTX 5060 Ti 的 D3D11 实测 `Backend`，D3D12 实测 `MissingShaderOrPass`，两者均 0 RTAS；仅在 D3D12 夹具注入 shader-ready 后使用原生构造器，live RTAS 为 0→1→0。未实现实例登记、Build、命中或 Player 派发。早期 D3D11 run-01 是编译中间态；run-02/03 记录了未保存场景不能新增加载、预览场景关闭不触发普通场景回调的差异。最终用已保存的场景测真实关闭事件，不手动伪造通知。
- G1：[CPU 数学与 FP16 读回](validation/G1/Win64-D3D11/run-04/Tests.xml) 13/13。常量/两色场、距离矩、反馈、已知天空/未解决、固定方向排除、接缝与邻居有效性均按 B0 容差验证。没有派发 SSGI、probe 更新或 DXR；数值夹具不是 GPU atlas 画质证据。

| 独立检查 | 结果与边界 |
|---|---|
| 既有功能回归 | [D3D11](validation/B0/Win64-D3D11/regression/run-01/Summary.json)、[D3D12](validation/B0/Win64-D3D12/regression/run-01/Summary.json) 各 EditMode 184/184、PlayMode 6/6；两次 T12 各 Forward 34/34、ForwardPlus 34/34、Deferred 35/35。8 次 Editor 调用的设置恢复报告均通过。 |
| 附加 Editor 跨进程采样哈希 | D3D11 的 85/85 相同；D3D12 仅 29/85 相同，ForwardPlus 与 Deferred 各有 28 项不同，原因未定位。每轮内部三个 renderer 的 `AllOffAgain` 均与各自 `Reference` 采样哈希相同。不得把 D3D12 这两条 Editor 路径当跨进程位精确金图；不以此放宽冻结的 Player 原始帧零差异门槛。 |
| [201–203 实际执行](validation/B0/order/execution.json) | 双 API × Game/SceneView × 两姿态，8/8。201 为 CopyDepth → DBuffer → ScreenSpaceShadows（无 RT）；202 内置 motion 在 Game 记录绘制，在 SceneView 因无 motion 数据返回；203 Hi-Z 后，SceneView 首次建立历史、第二次记录 Editor motion 绘制。两个后端均出现临时渲染纹理警告；此证据只证明 CPU Execute 顺序与命令记录，不证明 GPU 输出。 |

**W2 / T2 通过（显式登记的静态及刚体网格）**：[范围、复跑与限制](validation/T2/Summary.json)。同一 Development Player 在 D3D11 拒绝创建资源；[D3D12 的 25 条解析对照](validation/T2/verified/Player-D3D12.json)验证离屏、移动、mask、绕序和盒内背面。12 个 revision 共 11 次 Build，同 revision 跨相机及仅灯光变化不重复 Build。独立保留测试命中 pass，不等于生产材质变体已验证。

**W2 / R1 通过（半分辨率不透明 SSR 的请求阶段）**：[D3D11](validation/R1/D3D11/Summary.json)、[D3D12](validation/R1/D3D12/Summary.json) 各 6/6。无历史时从当前深度/法线生成候选，有历史时在邻域填洞前分类；混合图样 135 个合格样本、126 个请求、11 个入队、115 个未服务。`K=0` 无分配/派发。`served` 仅表示占有队列槽，不表示射线命中；预通道无材质/实例 ID，显式记未知。默认关闭路径的 [D3D11 Editor T12](validation/R1/D3D11/OffRegression.json) 为 103/103，85 个采样哈希与 B0 相同。未改变默认资产，不包含 R2 射线、生产相机级 DXR 或完整 Player 画面验收。

**W3 / T3 通过（Windows 基础命中着色）**：[同一 Player 的 D3D12 输出](validation/T3/Player-D3D12.json) 18/18，含自发光、白板直射、阴影开关/移除遮挡、有效黑色、miss 与无效区间。最大线性 RGB 相对误差 0.02345%（B0 ≤3%），距离误差 0 m，非有限分量 0。2 次 Build、3 次直接派发、Recorder 实测 3 个 GPU marker；[D3D11](validation/T3/Player-D3D11.json) 为 0 RTAS、0 射线缓冲和 0 派发。无剥离回调，默认相机路径未接入；下一步 W4/T3b 验证 Lit 贴图、法线和 cutout，不将本轮结果外推到完整 Lit 或主机。复跑见 [run.py](validation/T3/run.py)，记录见 [Summary.json](validation/T3/Summary.json)。

**W4 / T3b 通过（Win64、已登记的 metallic Lit 子集）**：[D3D12 Player](validation/T3b/Player-D3D12.json) 63/63。28 条材质诊断和 35 条生产主/阴影向量覆盖变换后的 UV、压缩 emission、metallic/smoothness、镜像非均匀缩放下的切线法线和 cutout。光栅对照经真实 URP 相机绘制；采样处非方向通道与 DXR 相同，法线夹角 <0.00003°。实心处命中 2 m，孔洞穿至 4 m 或 miss；不透明 AHS 实测 0，cutout 实测 1。payload 24 B；[双 API 构建](validation/T3b/Variants.json) 保留 16 个 Lit 命中变体，[仅 D3D11 构建](validation/T3b/D3D11Only.json) 为 0。范围、证据和复跑见 [T3b 摘要](validation/T3b/Summary.json)。下一阶段为 W5/T4 和 R2；本轮未接入 GI/SSR。

**W5 / T4 通过（Win64、场景灯表与逐灯阴影）**：[D3D12 Player](validation/T4/evidence/Player-D3D12.json) 17/17，同一构建 GUID；D3D11 以 `Backend` 拒绝，0 次 Build/派发/缓冲分配。D3D12 在真实 Build/bind/DispatchRays 下覆盖方向/点/聚光、距离与锥角衰减、移动/禁用/销毁、阴影开关与遮挡。66 盏登记/64 上传/2 溢出时稳定选中 4 盏、追踪 2 条阴影射线、其余 2 条计入未追踪；选中/溢出/阴影预算三层计数与解析解一致。复跑见 [run.py](validation/T4/run.py)，记录见 [Summary.json](validation/T4/evidence/Summary.json)。

- **GPU 挂死根因与围堵**：初版 `GlxShadeLights` 在可变次数的选灯循环里嵌套阴影 `TraceRay`，选中 4 盏起命中着色器超出 DXR 栈，D3D12 以 `DXGI_ERROR_DEVICE_HUNG (0x887A0006)` 移除设备；03:46–03:51 触发 34 次 `nvlddmkm` TDR。现改为两个固定阴影调用点（`Smax=2`）、灯表计数按 64 截断，所有可到达表模式的 raygen 保持 `max_recursion_depth 2`。4/8/64 灯预检与完整 66 灯 Player 用例均无新 TDR；事故日志、前后对照和预检 JSON 见 [preflight](validation/T4/preflight/Summary.json)。

**W5 / R2 通过（Win64 D3D12、离屏反射补追与合成；按拆分口径）**：[D3D12](validation/R2/evidence/Summary.json) 与 [D3D11 阶段记录](validation/R2/evidence/D3D11/cold.json) 同一构建 GUID。满屏镜面场景请求 4800 个半分辨率样本，预算服务 640、溢出 4160，未服务 4160 全部保持未解析、0 条误标天空/黑色；服务射线命中 donor 52 条，实例/距离/颜色相对解析解误差为 0，错误实例 0。`removed`/`far` 输出权重为 0（远处有限 TMax 有 171 条超程），`occluded`/`blockerID` 正确命中遮挡体。逐像素服务命中轮廓 recall 1.0、误报 0；SS-only 对同一轮廓恢复 0；D3D11 无 RT 缓冲/归属，warm 与 SS-only 相机图逐像素相同。B0 的每 8×8 tile 最多 8 样本使单帧服务上限为 12.5%，因此 0.90 全轮廓召回改由 R3 的高采样/时间参考签收；本轮的覆盖率与溢出已记录。首轮失败（材质 `_EMISSION` 关键字在 `CreateAsset` 前设置而未序列化）已修复，失败证据保留在 `evidence-pre-fix` 与 `evidence-emission-bug`。复跑见 [run.py](validation/R2/run.py)、[verify.py](validation/R2/verify.py)。

**下一阶段**：W6 的 G2（探针分帧更新）、R3（混合重建/回归与阴面源切换）、R4；随后 G3/G4/G5、R5 与 I1/I2。

**关键路径**：`B0 → T1 → T2 → T3 → T3b → T4 → G2 → G5/G3 → I1 → I2`。这是最长链，WS4 的 G2 是整条链的汇聚点。

## 5. 停止条件（按计划原文）

1. T3/T3b/T4 无法在目标后端证明真实 Build/bind/DispatchRays、材质 pass/AHS 与灯表 → G/R 只保留 fixture，真实 RT 路径保持禁用。
2. B0 无法冻结新增预算（灯数/阴影/payload/variant/反馈）→ 不进入 I2。
3. 某项通过需要扩大平台/材质/透明范围 → 按未通过记录，不改门槛、不回退基础 URP。
