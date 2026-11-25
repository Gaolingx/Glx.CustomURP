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

**W6 / G2 通过（Win64 D3D12，只验收生产者）**：[D3D12](validation/G2/evidence/D3D12/Report.json) 与 [D3D11 拒绝记录](validation/G2/evidence/D3D11/Report.json)同一构建 GUID。581 个批次、149 次主更新全部满足每批 ≤16 probes × ≤128 射线、inactive 只追 32 条、阴影 ≤2/射线；首批发 16 个 probe、第 16 批后 256/256 发布；classification 把墙内 probe 置 inactive，relocation 限幅 0.45×spacing 并在拆除遮挡后回零，动态阶跃 128 批收敛（256 批残差 0.9%），两相机同帧只更新一次。3/3 灯登记与上传，生产者常驻 950,376 B，CPU 提交均值 0.44 ms（首帧 223 ms 含 Build/读回）。D3D11 为 `Backend`、0 Build/0 派发。**GPU 计时未测到**（0 样本，`gpuTimingMeasured=false`）；本夹具不含材质查询、未发布 tile 消费与反馈图像。复跑见 [run.py](validation/G2/run.py)、[verify.py](validation/G2/verify.py)；生产者边界见 [GI 签收](gi/HANDOFF.md#g2-生产者签收win64-d3d12)。

**W6 / R3 阴面源切换通过（Win64 D3D12 夹具；只验收源切换）**：[D3D12](validation/R3/evidence/D3D12/Report.json) 与 [D3D11 拒绝记录](validation/R3/evidence/D3D11/Report.json)同一构建。D3D11 为 `Backend`、0 Build/0 派发。D3D12 登记 1 个不透明面、6 次派发：ambient 辐照度 2 读回 0.611155（=0.96×2/π），有效快照 6 读回 1.833465（=0.96×6/π）；快照版本失效、未发布邻居、邻居 inactive 三组均回 ambient；恢复后只加 probe 一次（与 ambient+probe 之和相差 >1e-3）。payload 24 B、请求 48 B；GI producer 未分配，SSR pass 未实例化。夹具中的 Lit 材质资产在首次使用前只有 1 个占位 pass，需先渲染一帧再 `FindPass("GlxDXR")`；fixture raytrace 用包路径 include 并显式绑定 ray count。R3 的图像回归见下方 W7 行；复跑见 [run.py](validation/R3/run.py)、[verify.py](validation/R3/verify.py)。

**W6 / R4 通过（Win64 D3D12，仅登记子集）**：[D3D12](validation/R4/evidence/D3D12/Report.json) 与 [D3D11 拒绝记录](validation/R4/evidence/D3D11/Report.json)同一构建。D3D11 为 `Backend`，0 TLAS/0 实例/0 派发。D3D12 从 6 个登记组件得到 3 个成员/3 个 TLAS 实例：emission 读回 (2, .5, .125)、cutout 实心读回 (0, 1, 0)@2 m、孔洞 miss（flags=1）；SimpleLit、ComplexLit、透明 Lit、未登记 Lit 与多 submesh 的 SimpleLit 子网格均不入 TLAS 且逐项显式计数（3 不支持实例、1 禁用 submesh、1 蒙皮跳过）。payload 24 B/请求 48 B；无 GI producer、无 Surface Cache/mesh card 资源；Lit `GlxDXR` 保留 16 个生产变体；9 条材质向量加 32 次 2048 射线计时。CPU 提交 0.039 ms/派发；**GPU ms 未测得**（0 样本，`gpuTimingMeasured=false`），不以 CPU 数字代替。复跑见 [run.py](validation/R4/run.py)、[verify.py](validation/R4/verify.py)。

**W7 / G4 relocation + classification 通过（Win64 D3D12，默认开）**：[D3D12](validation/G4/evidence/D3D12/Report.json) 与 [D3D11](validation/G4/evidence/D3D11/Report.json) 同一构建。D3D11 为 `Backend`、0 Build/0 派发。夹具用 `RayTracingProbeConfig.Default`：0.8³ 盒内 probe 的 offset 为 `0.25`（≤0.45×spacing）、4 批后漂移 0、6 个轴向邻居 offset 全 0；空中心 `state=0` 且 12 批内 tile 保持 `E=0.064238` 不变，墙边 `state=1`；每批 512 固定 + `96×active` 旋转、inactive 只追 32（96 批中 60 批含 inactive），阴影 ≤2/主射线。拆盒后 offset 回 0；加入发光盒后 17 批内 `state=1`、下一次重访 E 升至 `0.323043`，移除后 17 批回 `state=0`。消费者跳过 inactive/应用 offset 由 G1 与 R3/G5 覆盖，材质合成留 G3；GPU ms 未测得。复跑见 [run.py](validation/G4/run.py)、[verify.py](validation/G4/verify.py)；细节见 [GI 签收](gi/HANDOFF.md#g4-relocation--classification-签收win64-d3d12默认开)。

**W7 / G5 生产者侧反馈通过（Win64 D3D12 夹具；ROI/图像未覆盖）**：[D3D12](validation/G5/evidence/D3D12/Report.json) 与 [D3D11](validation/G5/evidence/D3D11/Report.json) 同一构建。D3D11 为 `Backend`、0 Build/0 派发。D3D12 的 `GlxLitClosestHit` 在 probe 模式加一次 `min(albedo, .9)·Eprev/π`，读上一代已发布 atlas、每命中一次查询（≤8 邻居）、不递归：常量 `Eprev=6` 下白 albedo 读回 `1.718873`、sRGB 0.5（线性 `0.214041`）读回 `0.408788`；反馈关/快照失效/邻居全 inactive/邻居未发布均为 0；反射模式在反馈开/关都保持 ambient `0.611155`；单条 probe ray 只追 `1` 条、无旋转射线。关闭房间点光阶跃：反馈把稳态红从 `0.076629` 提到 `0.088081`（+14.9%），关灯 256 批残差 `0.99%`/`2.03%`（≤5%）；每批 16 active、512 固定 + 1536 旋转、阴影 `1180≤4096`。payload 24 B/请求 48 B；**GPU ms 未测得**（0 样本），**ROI/图像、薄墙、红墙供体/移动发光体尚未覆盖**，留待 G3 合成后。复跑见 [run.py](validation/G5/run.py)、[verify.py](validation/G5/verify.py)；细节见 [GI 签收](gi/HANDOFF.md#g5-1-跳反馈签收win64-d3d12生产者侧)。

**W7 / R5 生产者侧通过（Win64 D3D12，全材质反射与灯光预算；图像接缝未覆盖）**：[D3D12](validation/R5/evidence/D3D12/Report.json) 与 [D3D11](validation/R5/evidence/D3D11/Report.json) 同一构建 GUID。D3D11 为 `Backend`、0 Build/0 派发/0 缓冲。D3D12 登记 9 个成员/9 个 TLAS 实例（4 面 + 5 遮板），1 次 Build、55 次派发（22 向量 + 1 次 20 射线轮廓 + 32 帧计时），payload 24 B/请求 48 B，无 GI producer/Surface Cache，资源全部释放。Lit `GlxDXR` 命中覆盖电介质、`_METALLICSPECGLOSSMAP`（metallic=1、smoothness=0.64）、`_NORMALMAP` 与 `_ALPHATEST_ON _EMISSION` cutout，最大相对误差 `1.33e-6`（B0 3%）；相邻电介质|金属接缝自身误差 `2.87e-7`、跨材质差 `0.706`；cutout 20 条射线 10 命中/10 未命中、0 错配。方向/点/聚光衰减解析一致（点 `0.071738`；聚光中心 `0.017322`、半影 `0.009419`、锥外 0），遮挡时 0 且 `flags=6`，移除遮挡恢复。预算：66 登记/64 上传/2 溢出，选 4/拒 60，4 盏可投影只追 2 条、2 条未追踪；rank 0 遮挡 + rank 2 物理遮挡但未追踪时读回 `0.843049/0.402857/0.227812`（绿灯 + 未追踪红灯 + 蓝灯），放开后 `1.896859/0.767061/0.377831`；被上传上限丢弃的强度 1000/500 两灯无贡献。有效 `E=6` 快照下反射 feedback 开/关逐位一致（delta 0），probe 源只加一次 `diffuse·E/π`（`3.340552/1.249764/0.562061`）。CPU 提交均值 `0.0606 ms`；**GPU ms 未测得**（0 样本），**图像级 SS↔HW 接缝/高采样参考、透明/ShaderGraph/蒙皮与多 submesh 仍是 R3/后续范围**。复跑见 [run.py](validation/R5/run.py)、[verify.py](validation/R5/verify.py)。

**W7 / R3 图像回归通过（Win64 D3D12，Forward/ForwardPlus；Deferred 记 C7）**：[D3D12](validation/R3/image-evidence/Summary.json) 与 [D3D11](validation/R3/image-evidence/D3D11/Report.json) 同一构建；按 B0 `sourceSwitchControl` 布线（1920×1080、FOV60、相机 `(0,1.6,-2.5)`、2×2 金属镜面 `z=3.98`、0.3 m 金属黑发光供体 `(1,1.6,0)→(1,1.6,-2.4)` 再返回、重复 4 轮、黑色环境 + 一盏竖直向下灯、K=32768）。Forward 时间并集召回 `0.968`、ForwardPlus `0.971`（其中供体完全出屏的帧 `0.941/0.959`），误报 `0`，核心帧 `208`，SS↔HW 台阶 `0.087/0.080`（B0 `0.10`），RT-off 阶段离屏召回 `0`、D3D11 为 `Backend`（0 Build/0 实例/0 订阅）且 `switch`/`switchOff` 的 ROI 关键帧逐字节相同，而 D3D12 的 Forward/ForwardPlus 同帧不同（混合确实写入），clear-coat/普通核心亮度比 `1.00`（ComplexLit 不重复增亮），透明控制逐像素差 `0`、蓝色 `0.50`，供体离开镜面后拖尾 `0` 帧，RT-off 两轮全帧 SHA256 逐 renderer 相同；GI producer 未分配，RT 场景只订阅 `Reflections`，payload 24 B/请求 48 B。**已知差异（C7）**：Deferred 的 Lit GBuffer 面在 RT 关/开都未被着色（off 帧非黑 `0`），clustered deferred 光照不消费 `_ScreenSpaceSsrTexture`；同一 renderer 中前向绘制的 ComplexLit 清漆仍解析出 `0.740`，说明 RT 路径本身在 Deferred 可用，缺口在 deferred 光照的 SSL 消费。**未覆盖**：本夹具中 Approximation SSR 对屏内/离屏供体解析均为 `0`，签收的是 HW 时间并集对解析参考的召回/误报/台阶/拖尾；SS 份额与 HW 份额共存时的同帧接缝仍需在 SS 能解析屏内供体的场景另测；GPU ms 未测。复跑见 [run_image.py](validation/R3/run_image.py)、[verify_image.py](validation/R3/verify_image.py)。

**W7 / G3a 合成通过（真实集成，Forward/ForwardPlus；Deferred 记 C7）**：[D3D12](validation/G3/evidence/D3D12/Report.json) 与 [D3D11](validation/G3/evidence/D3D11/Report.json) 同一构建 GUID `1a828d06…`（base `464c769`），同一 [Summary](validation/G3/evidence/Summary.json)。夹具按 B0 常量场口径：6 面 inward 立方体室 emission 2、接收面白介质（`0.96×E/π`）、无灯/黑环境，probe 网格 `(-3.5,0.5,-3.5)/1 m/8×4×8`，feedback/relocation/classification 全关。D3D12 由真实 feature 驱动：Forward 与 ForwardPlus 的 `probe` ROI `1.9189`（解析 `1.9200`，相对误差 `0.0005`）、`probeHalf`（c=0.5）`0.9595` 且逐像素恰为 c=1 的 0.5 倍（最大差 `0`）、`probeBoth`（SSGI 强度 1）仍 `1.9189`（不重复叠加）、体积外 `0`、冷启动首帧为 `baked`；40 帧内生产 41 批（每场景帧 ≤1 批，含同帧第二台 base camera），常驻 `950,376 B`，6 实例/1 订阅 `GI`、3 次 TLAS 构建；`off`/`offAgain` 全帧 SHA256 逐 renderer 相同而 `probe` 不同（`b0ed9154…` vs `a49947e1…`）。D3D11 为 `Backend`、0 批/0 字节/0 分配，`probe` 与 `off` 全帧逐字节相同（同一 `b0ed9154…`）。实现：共享 `RayTracingSceneResources` 上移到 feature、GI/反射各持请求位；`ScreenSpaceLightingApplyIndirectDiffuse` 接收 P/N，先 `base=lerp(bakedGI,E/π,c)` 再 `lerp(base,Lss,w)`；probe 全局在 207 绑定、`_SCREEN_SPACE_GI_PROBE` 由 Setup/Cleanup 开关；probe 就绪时 SSGI `rayMiss` 强制 `None`。**已知差异 C7（Deferred）**：GBuffer 光照仍不消费 SSL 漫反射，Deferred 下 `probe` 与 RT-off 全帧相同（非黑 0），留 G3b；根因与关闭见下条。**未覆盖**：三开关打开后的动态/重定位图像、薄墙漏光、切镜、双相机屏幕历史、SS 能解析供体时的同帧接缝、GPU ms、非 Windows 构建的变体剥离（留 I2）；R3 的 SSR-only 图像夹具未在本轮重跑（本轮只把场景容器所有权上移，SSR 路径的请求位/释放语义未变）。复跑见 [run.py](validation/G3/run.py)、[verify.py](validation/G3/verify.py)。

**W7 / G3b Deferred 合成通过（C7 关闭）**：[D3D12](validation/G3/evidence/D3D12/Report.json) 与 [D3D11](validation/G3/evidence/D3D11/Report.json) 同一构建 GUID `9fe964c7…`（base `464c769`），同一 [Summary](validation/G3/evidence/Summary.json)。根因不是「deferred 光照不消费 SSL」：URP 构建期只按 quality/default pipeline 统计 shader features，而 G3/R3 夹具在运行期才赋 pipeline，`Lit/GBuffer` 被剥成 0 个变体，Lit 面从未进入 GBuffer（`off` 帧非黑 `0`）。修正：夹具构建时临时把夹具 pipeline 赋给全部 quality 级并在构建后恢复；GBuffer pass 补 `_SCREEN_SPACE_GI_PROBE`（Lit/ComplexLit 的 GBuffer，及 ShaderGraph Lit 的 Forward/GBuffer 关键词集合），probe 在写 GBuffer 时与 SSGI/SSR 一起合成；`ClusteredDeferredPass` 改为绑定具体 RT（nameID 要等色交接后才注册，首帧会丢写）。修后 `Lit/GBuffer` (fp) 剥离后剩 128 个变体，Deferred `off` 非黑 `446,576`；三种 renderer 的 `probe` ROI 均 `1.9189`（解析 `1.9200`，相对误差 `0.0005`）、`probeHalf` `0.9595` 且逐像素恰为 0.5 倍、`probeBoth` 仍 `1.9189`、体积外 `0`、冷启动 `baked`；各 renderer 的 `off`/`offAgain` 全帧哈希同为 `b0ed9154…`、`probe` 同为 `a49947e1…`；40 帧 41 批、`950,376 B`、6 实例/1 订阅 `GI`。D3D11 仍为 `Backend`、0 批/0 字节、`probe`=`off`。**未覆盖**：三开关打开后的动态/重定位图像、薄墙漏光、切镜、双相机屏幕历史、SS 能解析供体时的同帧接缝、GPU ms、非 Windows 构建的变体剥离（I2）；R3 的 SSR-only 图像夹具需用同一构建修正复跑。

**下一阶段**：R3 图像夹具用同一构建修正复跑（Deferred SSR），随后 G3c（三开关与薄墙/切镜/接缝扫描）；之后 I1/I2。

**关键路径**：`B0 → T1 → T2 → T3 → T3b → T4 → G2 → G5/G3 → I1 → I2`。这是最长链，WS4 的 G2 是整条链的汇聚点。

## 5. 停止条件（按计划原文）

1. T3/T3b/T4 无法在目标后端证明真实 Build/bind/DispatchRays、材质 pass/AHS 与灯表 → G/R 只保留 fixture，真实 RT 路径保持禁用。
2. B0 无法冻结新增预算（灯数/阴影/payload/variant/反馈）→ 不进入 I2。
3. 某项通过需要扩大平台/材质/透明范围 → 按未通过记录，不改门槛、不回退基础 URP。
