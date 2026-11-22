# 09-DDGI_improve 交接：桌面 URP DDGI 实施计划

> 状态：**计划定稿已修订，未实施代码**（基线已按 HEAD `720bedd` 重测，见 §2.2）
> 建立：2026-11-19；修订：2026-11-20（复核 1–5：基线、互斥、TLAS、schema、任务、流程）
> 基线：计划于 `0a73b2b` 起草；T0 已按实施 HEAD `720bedd` 重测（见 §2.2）
> 结果入口：本文件、[report.md](report.md)、`validation/`
> 关联：`PLAN/Done/01-ScreenSpaceLighting`（SSL/GTAO 实现）、`PLAN/Done/06-ValidationAllRDC`（证据与 `.rdc` 规范）、`PLAN/Done/07-EditorMVPass`、`PLAN/Done/08-Accurate_normals_fixed`（测试/收尾格式）
> 外部参考：`E:\workspace\GitHub\RTXGI-DDGI-URP`（DDGI 数据面/采样/光追）、`E:\workspace\GitHub\UnitySSGIURP`（材质/降噪接入风格）
> 本文中的参考仓库行号基于其当前工作树；本项目代码行号基于 `0a73b2b`，改动后以符号名为准。

---

## 0. 一页结论

**目标**：在桌面 URP 中落地一个**完整、可读、可验收**的 DDGI：单一静态 probe volume、D3D12 + DXR 光追、内置不透明材质参与、与现有 Screen Space Lighting 共存（互斥）并保留 APV/lightmap 兜底。v1 不追求多 volume、滚动、透明物、ShaderGraph、ReSTIR/NRD/Lumen。

**已冻结的决策（D1–D10）**：

| ID | 决策 | 理由 |
|---|---|---|
| D1 | 后端只用 **D3D12 + DXR**（`SystemInfo.supportsRayTracing`） | 与参考实现一致；D3D11/Vulkan 没有可信的 DDGI 追踪回退 |
| D2 | **单一静态 volume**，不做滚动/多 volume | 控制范围与可读性；滚动是参考实现未完成的 TODO |
| D3 | 只覆盖**内置不透明材质**（Lit / ComplexLit / SimpleLit / Terrain / SpeedTree / Particles Lit 的 Opaque 变体） | 允许改内置材质系统；ShaderGraph 与透明变体放 follow-up |
| D4 | DDGI 与 **SSGI 互斥**（DDGI 优先，SSGI 跳过） | 两者都是间接漫反射，同时开会双重计能量；依据见 report §5.7 |
| D5 | 采样接在 `GlobalIllumination.hlsl` / `Lighting.hlsl`，不做全屏合成 | forward / forward+ / deferred（GBuffer 写入）统一覆盖 |
| D6 | 默认 **22³ probes、256 rays、每帧更新 1/8**，warmup 8 帧全量 | 参考实现全量 273 万 rays/frame 不可接受 |
| D7 | 多跳保留“命中点采样上一帧 probe”反馈，**删除参考实现采样末尾的额外平方** | 一帧一跳 + 时序累积；修正能量 |
| D8 | 灯光用 URP `visibleLights`，不用 `FindObjectsOfType` | 生产可用；避免每帧 CPU 反射扫描 |
| D9 | 收敛后**降频不停更**；TLAS 只在场景/实例变化时重建，与 probe 调度解耦 | 避免参考实现的 GI 冻结 bug，同时避免每帧重建 TLAS |
| D10 | 默认**不新增 `.rdc`**；证据以 JSON/XML/PNG 为主 | 仓库 `.rdc` 已用 1000.1 MiB / 1024 MiB |

**验收摘要**：AC-1…AC-13（§6.6）。核心是：关闭/不支持时零图像差；D3D12 下 probe 收敛且无 NaN；分帧预算与动态恢复成立；所有内置不透明材质贡献 GI；SSGI 互斥；D3D11 构建与既有 T12 无回归；性能与产物预算达标。

---

## 1. 目标与非目标

### 1.1 目标

1. 新增一个可复用的 `DynamicDiffuseGI` Renderer Feature + Volume，桌面 D3D12 下工作。
2. 探针数据面：八面体 irradiance（6×6 内容 + border）+ 距离矩（14×14 + border）+ probe 状态 + 历史。
3. 每帧流程：DXR 探针射线 → irradiance/distance 卷积 → hysteresis → relocation/classification → history 交换。
4. 分帧预算：round-robin 更新、固定 32 rays 用于 relocation/classification、warmup 全量。
5. 多跳：命中点直接光 + 上一帧 probe 辐照度反馈；能量归一化正确。
6. 材质接入：内置不透明材质接收 DDGI；DDGIRayTracing pass 让这些材质进入 probe 命中着色。
7. 与现有 SSL：DDGI 关闭时行为完全不变；开启时 SSGI 跳过，SSR/折射保留。
8. 调试与验证：probe/atlas/classification/relocation/未覆盖材质可视化；D3D12 player 自动验收。

### 1.2 非目标（v1 明确不做）

| 项 | 原因 | 后续 |
|---|---|---|
| 多 volume / 无限滚动 | 范围与可读性；参考实现未完成 | follow-up |
| 透明物/粒子参与 probe 命中 | 需要额外 pass 与排序语义；v1 只收 Particles Lit 的 Opaque 变体 | follow-up |
| ShaderGraph 材质 | 生成 `DDGIRayTracing` pass 需改 UniversalTarget | follow-up |
| 移动端 / GLES / Vulkan 回退 | DXR 是唯一后端 | 不在本计划 |
| ReSTIR / NRD / SVGF / Lumen surface cache | 解决不同问题，范围爆炸 | 不在本计划 |
| 软件 SDF/voxel 追踪 | 第二后端，成本高 | 不在本计划 |
| APV/lightmap 烘焙管线改造 | 只做运行时兜底混合 | 不在本计划 |
| 性能自动调参/动态分辨率 | 先固定默认值并测量 | follow-up |

### 1.3 成功标准

- 功能：D3D12 下开关 DDGI，阴影区间接光有可测差异；探针收敛；动态光/自发光变化可恢复。
- 正确性：背景/未覆盖材质/体积外不发黑；无 NaN；能量不爆；DDGI 关闭时与基线逐像素一致。
- 工程：D3D11 构建零 shader error；既有 EditMode/PlayMode/T12 全绿；新增测试进入 round-local filter。
- 产物：证据可复现；`.rdc` 预算不超；文档与任务无自相矛盾。

---

## 2. 事实、约束与基线

### 2.1 版本与工具

| 项 | 值 |
|---|---|
| Unity | `2022.3.62f1`（`D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe`） |
| URP 包 | `com.unity.render-pipelines.universal@14.0.12`（本仓库 fork） |
| 参考仓库 | `E:\workspace\GitHub\RTXGI-DDGI-URP`（Unity 2022.3.62f1 + URP 14.0.9，DXR/DX12） |
| 目标平台 | Windows Desktop，D3D12 + DXR |
| 非目标平台 | D3D11（功能禁用）、Vulkan、GLES、XR、移动 |

### 2.2 测试与验收基线（实施 HEAD `720bedd`）

| 项 | 基线 |
|---|---|
| HEAD | `720bedd`（2026-11-20 实测；计划起草于 `0a73b2b`） |
| EditMode | **184/184**（既有 21 类 filter；`validation/P0/EditMode/UnityEditModeTests.xml`） |
| PlayMode | **6/6**（`validation/P0/PlayMode/UnityPlayModeTests.xml`） |
| T12 矩阵 | Forward 34/34、Forward+ 34/34、Deferred 35/35（D3D11；`validation/P0/T12/T12Verification.json`） |
| 测试机 GPU | NVIDIA GeForce RTX 5060 Ti（D3D11） |
| 既有 `.rdc` | 58 个，1,048,717,328 B（1000.1 MiB）/ 预算 1024 MiB |
| 工作树 | 09 目录已随 `145d9a4` 提交；P0 证据在 `validation/P0/` |

> **`0a73b2b` 的数字已失效**：`c69d9f2` 新增 2 个 SSR 测试（EditMode 182→184），`cedd1bb` 改变 SSR 像素；T12 的对照以本次 P0 重录为准。实施若从其他 HEAD 开始，T0 必须重跑上述三项并更新本表与 `Baseline.json`。
> 机器可读基线：`validation/P0/Baseline.json`（含命令、恢复状态、103 个 T12 用例结果哈希；PNG 只留 3 renderer × Reference/AllOn/AllOffAgain 共 9 张）。

> `.rdc` 预算只剩约 24 MiB 余量。**默认不新增抓帧**；若必须抓，最多 1 个 ≤20 MiB 的 1280×720 帧，并在 `Cleanup.md` 记录；超预算需仓库所有者书面同意。

### 2.3 能力门控

```text
supported =
    SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12
 && SystemInfo.supportsRayTracing
 && SystemInfo.supportsComputeShaders
 && SystemInfo.IsFormatSupported(RGBA16F/RG16F/R32F, FormatUsage.LoadStore)
```

不满足时：feature 在 `AddRenderPasses` 直接返回，`Debug.LogWarning` 一次，保留 baked GI/APV；不得抛异常、不得改全局关键字。

### 2.4 现有接入点（HEAD）

| 事实 | 位置 |
|---|---|
| Forward / Forward+ 的 GI 入口 | `ShaderLibrary/Lighting.hlsl`（`UniversalFragmentPBR` → `ScreenSpaceLightingApplyIndirectDiffuse`） |
| Deferred GBuffer 的 GI 入口 | `ShaderLibrary/GlobalIllumination.hlsl`（`GlobalIllumination`）；`Shaders/LitGBufferPass.hlsl` 调用它写入 GBuffer3 |
| 现有 SSGI 绑定/清理 | `Runtime/RendererFeatures/ScreenSpaceLighting/Passes/ScreenSpaceLightingSetupPass.cs`、`ScreenSpaceLightingCleanupPass` |
| 共享深度金字塔/能力检查 | `Runtime/RendererFeatures/ScreenSpaceCommon/ScreenSpaceDepthPyramidPass.cs`、`ScreenSpaceCapability.cs` |
| 验证 player | `Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingValidationPlayerSetup.cs`（D3D11）、`ScreenSpaceLightingT13PlayerSetup.cs`（D3D11/D3D12/Vulkan） |
| RenderDoc in-app | `Assets/Tests/ScreenSpaceLighting/Runtime/ScreenSpaceLightingValidationPlayerVerification.cs`（`RenderDocApi`） |

> DDGI 必须跑在 **GBuffer pass（210）之前**，因为 deferred 的 GI 在 `LitGBufferPass` 里写入 GBuffer3。选择 `AfterRenderingPrePasses + 2`（202），早于 SSGI（205）、SSR（206）、Setup（207）。

---

## 3. 架构

### 3.1 帧序（新增部分）

```
DepthPrepass / DepthNormals (155–200)
DDGI Trace + Update            [新] 202   ← 本计划
Depth Pyramid                  203
GTAO                           204
SSGI                           205   （DDGI 开启时跳过）
SSR                            206
SSL Setup                      207
Deferred GBuffer / Forward Opaque 210 / 250   ← 采样 DDGI
...
DDGI Debug（可选）             AfterRenderingOpaques
```

### 3.2 数据流

```
Lights (visibleLights) ─┐
Sky (cubemap/gradient) ─┼─> DDGITracePass (DXR)
TLAS (scene) ───────────┘        │
                                 ▼
                         RayBuffer (StructuredBuffer)
                                 │
                 ┌───────────────┴───────────────┐
                 ▼                               ▼
        DDGIUpdateIrradiance             DDGIUpdateDistance
        (6×6 + border, RGBA16F)          (14×14 + border, RG16F)
                 │                               │
                 ▼                               ▼
        hysteresis + border              hysteresis + border
                 │                               │
                 └───────────────┬───────────────┘
                                 ▼
                     ProbeData / History 交换
                                 │
                                 ▼
        GlobalIllumination.hlsl / Lighting.hlsl 采样（8 probe + Chebyshev）
                                 │
                                 ▼
                    材质间接漫反射 / GBuffer3
```

### 3.3 资源布局（默认）

| 资源 | 格式 | 尺寸/说明 |
|---|---|---|
| `_DDGIProbeIrradiance` | `RGBA16F` Tex2DArray | `(px*8) × (pz*8) × py`，6×6 内容 + 每边 1px border（8×8） |
| `_DDGIProbeDistance` | `RG16F` Tex2DArray | `(px*16) × (pz*16) × py`，14×14 内容 + 每边 1px border（16×16），存（mean, mean²） |
| `_DDGIProbeData` | `RGBA16F` Tex2DArray | `px × pz × py`，1 texel/probe（offset.xyz、state.w） |
| `_DDGIProbeVariability` | `R32F` | 1 texel/probe，可选 |
| `_DDGIRayBuffer` | `RWStructuredBuffer<float4>`（raygen 写入/UAV；更新 kernel 以 `StructuredBuffer` 读取） | `scheduledProbes × raysPerProbe`，xyz=radiance，w=distance |
| `_DDGIProbeUpdateList` | `StructuredBuffer<uint>` | 本帧调度的 probe 线性索引 |
| `_DDGIProbeIrradianceHistory` / `DistanceHistory` | 同上 | 双缓冲；只复制本帧调度的 tile |
| Light buffers | `StructuredBuffer` | 方向光、点/聚光（最多 8 + 32，可配） |
| TLAS | `RayTracingAccelerationStructure` | 场景/实例变化时重建；probe 调度不触发重建（见 D9/T6） |

### 3.4 调度

| 参数 | 默认 | 范围/说明 |
|---|---|---|
| `enabled` | **false** | opt-in；默认不影响任何既有画面 |
| `probeCountX/Y/Z` | 22/22/22 | 1–32；单一 volume |
| `volumeOrigin` | (0,0,0) | 世界空间最小角；T0 冻结 |
| `volumeSize` | 50 m（每轴） | 世界空间边长；probe 间距 = `volumeSize/(count-1)`（count=1 时为单 probe）；T0 冻结 |
| `raysPerProbe` | 256 | 64–256；前 32 固定 ray（固定 ray 不参与卷积，低于 64 会退化） |
| `updateDivisor` | 8 | 1–32；每帧更新 `ceil(total/divisor)` 个 probe |
| `warmupFrames` | 8 | 前 N 帧全量更新 |
| `hysteresis` | 0.97 | 0–0.99 |
| `irradianceIntensity` | 1.0 | 0–4 |
| `variabilityThreshold` | 0（关闭） | 开启后只降频，最低 divisor 16 |

更新列表由 CPU round-robin 生成，按 `updateDivisor` 轮转；`warmupFrames` 内 divisor=1。inactive probe 在本帧被调度时只追 32 条固定 ray；未被调度的 probe 不追。

### 3.5 材质接入与共存

- 在 `GlobalIllumination.hlsl`/`Lighting.hlsl` 引入统一的 `ApplyIndirectDiffuse(...)`：
  ```
  if (_DDGI) return lerp(bakedGI, ddgiRadiance * intensity, volumeWeight * validity);
  return ScreenSpaceLightingApplyIndirectDiffuse(bakedGI, uv);
  ```
- `volumeWeight` 来自 volume 边缘淡出；`validity` 来自探针可用/非背景。
- 体积外/无探针：回退 `bakedGI`（APV/lightmap/SH），不全黑。
- DDGI 开启时：SSL Feature 不再入队 SSGI，并**同时复位 SSGI 输出状态**。只在 Feature 跳过入队不够：`ScreenSpaceLightingSsgiPass.producedOutput` 只在 `Execute` 里复位，SSGI 不入队时会保留上一帧的 `true`，207 的 `ScreenSpaceLightingSetupPass` 会继续绑定旧纹理并重新开启 `_SCREEN_SPACE_GI`。互斥契约因此是“跳过入队 + 复位输出”两处，缺一不可（见 §4.4）。
- DDGI 开启期间，`_SCREEN_SPACE_GI` 必须在**不透明绘制阶段**保持关闭；SSR/折射不受影响。Cleanup（AfterRenderingOpaques）之后关键字必然关闭，不能作为互斥证据。
- DDGI 关闭/不支持：`_DDGI` 关闭，现有 SSL 路径输出逐像素一致（AC-1）。

### 3.6 多跳与能量

- 命中点辐射 = 直接光（方向光/点光/聚光 + RT shadow）+ 自发光 + **上一帧 probe 辐照度** × albedo × Lambert。
- 采样端：`radiance = decay * Σ(irradiance * weight) / Σweight`；删除参考实现的 `sum *= sum`；`radiance *= 2π` 只保留一次。
- 体积边缘、背面、背景、INACTIVE probe 的权重为 0；miss 不写入 NaN。
- 一帧一跳；收敛由 hysteresis + warmup 保证。

### 3.7 灯光与天空

- 方向光：方向、颜色、强度；点/聚光：位置、方向、颜色、range、spot 参数。
- 方向光/点光各一条 RT shadow ray；聚光按角度衰减。
- 天空：`Cubemap`（可选纹理/HDR decode）/`Gradient`（sky/equator/ground）/`Color`；默认 Gradient 取 RenderSettings ambient。
- 最大灯数可配；超出丢弃并打一次 warning。

### 3.8 调试

- `Off / ProbeGrid / IrradianceAtlas / DistanceAtlas / Classification / Relocation / UncoveredMaterials / Stats`。
- `UncoveredMaterials`：对没有 `DDGIRayTracing` pass 的材质输出洋红/标记，便于发现 ShaderGraph/自定义 shader。
- In-player `Stats`：probe 数、本帧调度数、ray 数、dispatch 数、GPU ms（ProfilingSampler）。

---

## 4. 模块边界与责任

### 4.1 工作流（责任划分）

| 工作流 | 负责范围 | 主要任务 |
|---|---|---|
| W1 Runtime/调度 | Feature、Volume、Capability、History、Pass 接线、灯光上传 | T1、T6、T9 |
| W2 GPU/着色器 | 探针数学、DXR、更新 kernel、采样库、内置材质 pass | T2、T3、T4、T5、T8 |
| W3 集成/共存 | GlobalIllumination/Lighting 接入、SSGI 互斥、APV 兜底 | T7 |
| W4 验证/证据 | 测试资产、player、指标、RenderDoc、性能 | T10、T11、T12 |
| W5 文档/收尾 | 契约、报告、ACCEPTANCE、清理、归档 | T0、T13 |

> 单人实施时以上是“验收责任”而不是人员分工：每个任务必须由对应工作流给出证据，不能由实现者口头代替。

### 4.2 新增文件（包内）

```
Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/DynamicDiffuseGI/
  DynamicDiffuseGIRendererFeature.cs
  DynamicDiffuseGI.cs                    # VolumeComponent
  DynamicDiffuseGICapability.cs
  DynamicDiffuseGIResources.cs
  DynamicDiffuseGIHistory.cs
  DynamicDiffuseGIShaderIDs.cs
  DynamicDiffuseGILights.cs
  DynamicDiffuseGIDebugMode.cs
  Passes/DynamicDiffuseGITracePass.cs
  Passes/DynamicDiffuseGIUpdatePass.cs
  Passes/DynamicDiffuseGIDebugPass.cs

Packages/com.unity.render-pipelines.universal@14.0.12/Shaders/Utils/DynamicDiffuseGI/
  DDGIProbeIndexing.hlsl
  DDGIPacking.hlsl
  DDGIRayTracing.raytrace
  DDGIRayTracingCommon.hlsl
  DDGIRayTracingPass.hlsl
  DDGIUpdateIrradiance.compute
  DDGIUpdateDistance.compute
  DDGIRelocateProbe.compute
  DDGIProbeClassification.compute
  DDGICopyHistory.compute
  DDGIVariability.compute
  DDGIDebug.shader

Packages/com.unity.render-pipelines.universal@14.0.12/ShaderLibrary/DynamicDiffuseGI/
  DynamicDiffuseGIInput.hlsl
  DDGISampling.hlsl
  DynamicDiffuseGIBlend.hlsl

Packages/com.unity.render-pipelines.universal@14.0.12/Tests/Editor/RendererFeatures/DynamicDiffuseGI/
  # 命名空间统一为 UnityEditor.Rendering.Universal.Tests
  DynamicDiffuseGIProbeIndexingTests.cs
  DynamicDiffuseGIOctahedralTests.cs
  DynamicDiffuseGIVolumeTests.cs
  DynamicDiffuseGIHistoryTests.cs
  DynamicDiffuseGICapabilityTests.cs
  DynamicDiffuseGIMaterialContractTests.cs
  DynamicDiffuseGIExclusivityTests.cs
  DynamicDiffuseGISchedulingTests.cs
```

**文件→任务归属**（每个文件必须有且只有一个创建任务；串行修改在同一行注明）：

| 文件 | 创建 | 后续修改 |
|---|---|---|
| `DynamicDiffuseGIRendererFeature.cs`、`DynamicDiffuseGI.cs`、`DynamicDiffuseGICapability.cs`、`DynamicDiffuseGIShaderIDs.cs`、`DynamicDiffuseGIDebugMode.cs` | T1 | T6/T7/T10 按需扩展字段（ShaderIDs/DebugMode 由 T1 一次定义全部枚举） |
| `DynamicDiffuseGIResources.cs`、`DynamicDiffuseGIHistory.cs`、`DDGIProbeIndexing.hlsl`、`DDGIPacking.hlsl` | T2 | T5（History 复制）、T6（update list） |
| `DDGIRayTracing.raytrace`、`DDGIRayTracingCommon.hlsl`、`DDGIRayTracingPass.hlsl`、`DynamicDiffuseGITracePass.cs` | T3 | T4（材质共享 include）、T8（raygen 反馈）、T9（灯光循环） |
| `DDGIUpdateIrradiance.compute`、`DDGIUpdateDistance.compute`、`DDGICopyHistory.compute`、`DynamicDiffuseGIUpdatePass.cs` | T5 | T6（调度/relocation/classification 扩展同一个 Pass） |
| `DDGIRelocateProbe.compute`、`DDGIProbeClassification.compute`、`DDGIVariability.compute` | T6 | — |
| `DynamicDiffuseGILights.cs` | T9 | — |
| `DDGIDebug.shader`、`DynamicDiffuseGIDebugPass.cs` | T10 | — |
| `ShaderLibrary/DynamicDiffuseGI/*` | T7 | T8（`DDGISampling.hlsl` 能量归一化） |
| `DynamicDiffuseGIProbeIndexingTests.cs`、`DynamicDiffuseGIOctahedralTests.cs`、`DynamicDiffuseGIHistoryTests.cs`（含资源尺寸断言） | T2 | — |
| `DynamicDiffuseGIVolumeTests.cs`、`DynamicDiffuseGICapabilityTests.cs` | T1 | — |
| `DynamicDiffuseGIMaterialContractTests.cs` | T3（仅 Lit） | T4 扩到全量材质 |
| `DynamicDiffuseGIExclusivityTests.cs` | T7 | — |
| `DynamicDiffuseGISchedulingTests.cs` | T6 | — |

### 4.3 新增文件（测试/资产，包外）

```
Assets/Tests/DynamicDiffuseGI/
  Editor/DynamicDiffuseGIValidationPlayerSetup.cs
  Runtime/DynamicDiffuseGIValidationPlayerVerification.cs
  Scenes/DynamicDiffuseGIBaseline.unity
  Renderers/DdgiForwardRenderer.asset, DdgiForwardPlusRenderer.asset, DdgiDeferredRenderer.asset
  Profiles/DdgiValidationProfile.asset
  Tests/PlayMode/Unity.DynamicDiffuseGI.Tests.PlayMode.asmdef + tests
```

PlayMode asmdef 参照 `Unity.ScreenSpaceLighting.Tests.PlayMode.asmdef`：引用 `Unity.RenderPipelines.Universal.Runtime`、`Unity.RenderPipelines.Core.Runtime`、`UnityEngine.TestRunner`、`UnityEditor.TestRunner`，`precompiledReferences: nunit.framework.dll`，`defineConstraints: UNITY_INCLUDE_TESTS`。测试命名空间与 asmdef 同名（`Unity.DynamicDiffuseGI.Tests.PlayMode`），`-testFilter` 按该命名空间过滤。

### 4.4 修改文件（明确清单）

| 文件 | 改动 | 责任 |
|---|---|---|
| `ShaderLibrary/GlobalIllumination.hlsl` | 引入 `ApplyIndirectDiffuse`，DDGI 优先 | W3 |
| `ShaderLibrary/Lighting.hlsl` | `UniversalFragmentPBRSplit` 走同一 `ApplyIndirectDiffuse`（透明 Lit/ShaderGraph 路径）；不透明 `UniversalFragmentPBR` 经 `GlobalIllumination` 覆盖，无需重复改 | W3 |
| `Shaders/Lit.shader` | 增加 `DDGIRayTracing` pass（共享 include） | W2 |
| `Shaders/ComplexLit.shader` | 同上 | W2 |
| `Shaders/SimpleLit.shader` | 同上 | W2 |
| `Shaders/Terrain/TerrainLit.shader`、`Shaders/Terrain/TerrainLitBase.shader`（若含独立渲染 pass） | 同上 | W2 |
| `Shaders/Nature/SpeedTree7.shader`、`SpeedTree8.shader` | 同上 | W2 |
| `Shaders/Particles/ParticlesLit.shader` | 同上 | W2 |
| `Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingRendererFeature.cs` | `AddRenderPasses` 读取 DDGI Volume + Capability；DDGI 开启时跳过 SSGI 入队并调用 `m_SsgiPass.ResetOutput()`（SSR/折射照常） | W3 |
| `Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsgiPass.cs` | 只新增内部 `ResetOutput()`：复位 `m_ProducedOutput`/`m_FinalTexture`/`m_HistoryWritten`；**不改 SSGI 算法** | W3 |
| `PLAN/ACCEPTANCE.md` | 执行时读取最后一节编号，新增下一节（当前最后一节 §14，故为 §15）；不预写未来编号 | W5 |

> `GlobalIllumination.hlsl` 与 `Lighting.hlsl` 的改动必须保持 DDGI 关闭时**输出逐像素一致**（源码不可能逐字节不变）；这是 AC-1 的一部分。

### 4.5 不修改/禁止

- 不改 `UniversalRenderer` 的 pass 顺序、`DeferredLights`、GTAO、SSL 的 SSGI/SSR 算法。
- 不改 ShaderGraph `UniversalTarget`（v1 非目标）。
- 不新增第三方包（不引入 LWGUI；用标准 Volume/Inspector）。
- 不修改 D3D11/移动端默认图形 API；验证脚本临时设置并恢复。
- 不提交 Player 构建、`Library/`、`Temp/`、大日志、默认 `.rdc`。

---

## 5. 任务清单（T0–T13）

> 每个任务单独提交；提交前必须通过该任务的验证门禁。任务编号按依赖排序：允许 T1/T2 并行；T6 依赖 T5 与 T9（动态恢复需要灯光 buffer）。

### T0 契约与基线冻结（W5）

- **目标**：冻结范围、决策 D1–D10、默认参数、AC 阈值；记录可复现基线。
- **产出**：`validation/P0/Contract.md`、`validation/P0/Baseline.json`。
- **内容**：
  - HEAD、Unity/包版本、测试机 `graphicsDeviceType` 与 `supportsRayTracing`。
  - EditMode 184/184、PlayMode 6/6、T12 34/34/35（§2.2 实测）。
  - `.rdc` 58 个 / 1,048,717,328 B。
  - 默认值（§3.3–3.4）与 AC 阈值（§6.6）。
  - 剥离检查清单：关键字 `_DDGI`、pass tag `DDGIRayTracing`、RayTracingShader 资产名 `DDGIRayTracing`、compute 资产名（`DDGIUpdateIrradiance`/`DDGIUpdateDistance`/`DDGICopyHistory`/`DDGIRelocateProbe`/`DDGIProbeClassification`/`DDGIVariability`）、调试 shader 名 `Hidden/Universal Render Pipeline/DDGI/Debug`。T12 的 `BuildVariants.json` 按此清单逐项断言。
- **验收**：Contract 与 Baseline 入库；无代码改动；所有者确认 D1–D10。
- **阻塞**：测试机无 DXR → 不进入 T3；先换机器，不得用 D3D11 假装验收。

### T1 能力门控与空框架（W1）

- **目标**：Feature/Volume/Capability 落地；D3D11 与无 RT 环境完全 no-op。
- **改动**：新增 `DynamicDiffuseGIRendererFeature`、`DynamicDiffuseGI`、`DynamicDiffuseGICapability`；创建 `validation/Invoke-DDGIEditModeTests.ps1`（复用 T14 wrapper，追加 DDGI filter）；不接材质。
- **验收**：D3D11 下 DDGI 开/关图像逐像素一致（diff=0）；D3D12 无 RT 只 warning 一次；Volume 参数可序列化；`enabled` 默认 false；Preview/Reflection 相机不入队、Scene View 无 UniversalAdditionalCameraData 时只读跳过（EditMode gating 断言相机过滤 + 全局单份 history）；T12 不变。
- **证据**：`validation/P1/`（JSON + 0 差值证明）、EditMode XML、round-local 脚本。
- **回滚**：只删新增文件。

### T2 探针数学与资源布局（W2）

- **目标**：把参考实现的索引/八面体/border/体积/权重/状态数学变成可单测的共享库；定义资源描述。
- **改动**：`DDGIProbeIndexing.hlsl`、`DDGIPacking.hlsl`、`DynamicDiffuseGIResources.cs`、`DynamicDiffuseGIHistory.cs`。
- **验收**：八面体编码/解码往返；probe→texel 边界（x/y/z 最小/最大）；border 复制索引；8 probe 三线性权重和为 1；体积外权重 0；内存尺寸与 §3.3 一致。
- **证据**：EditMode XML（CPU 参考实现）。
- **依赖**：T0；可与 T1 并行。

### T3 光追后端与命中契约（W2）

- **目标**：TLAS + `.raytrace` + payload + Lit `DDGIRayTracing` pass；miss/backface/fixed-ray 语义。
- **改动**：`DDGIRayTracing.raytrace`、`DDGIRayTracingCommon.hlsl`、`Lit.shader` 新 pass、`DynamicDiffuseGITracePass.cs`。
- **验收**：D3D12 简单解析场景（平面 + 球）ray buffer 读回：miss=`1e27`、背面负距离、命中距离与解析值误差 <1e-3；D3D11 构建 0 shader error；Lit pass 契约断言（在 `DynamicDiffuseGIMaterialContractTests.cs` 中先只覆盖 Lit，T4 扩到全量）。
- **证据**：`validation/P3/RayBuffer.json`、D3D11/D3D12 build summary。
- **依赖**：T2。

### T4 内置材质覆盖（W2）

- **目标**：ComplexLit/SimpleLit/Terrain/SpeedTree/Particles Lit 都能贡献 probe 命中。
- **改动**：§4.4 的内置 shader 列表；共享 `DDGIRayTracingPass.hlsl`。
- **验收**：契约测试扫描**公共可见** shader（Lit、ComplexLit、SimpleLit、TerrainLit、SpeedTree7/8、ParticlesLit 的 Opaque 变体）必须含 `LightMode = DDGIRayTracing`；`Hidden/.../Terrain/Lit (Base Pass)` 由 `TerrainLit` 继承覆盖，不单独断言，但需验证该 pass 可达；D3D12 全部编译；D3D11 不受影响；测试场景每种材质至少一个，probe 读回非零（Particles 只用 Opaque 材质）。
- **证据**：`validation/P4/MaterialContract.txt`、`Contribution.json`。
- **依赖**：T3。

### T5 更新管线（W2）

- **目标**：irradiance/distance 卷积 + hysteresis + border + history 复制正确。
- **改动**：`DDGIUpdateIrradiance.compute`、`DDGIUpdateDistance.compute`、`DDGICopyHistory.compute`、`DynamicDiffuseGIUpdatePass.cs`（本任务建立 dispatch 宿主，T6 再扩展调度）。
- **验收**：合成 ray buffer → 期望 irradiance/distance（CPU 参考，误差 ≤1e-3）；hysteresis 收敛；border 相等；0 NaN；dispatch 尺寸与 probe 数一致。
- **证据**：`validation/P5/Update.json`。
- **依赖**：T3。

### T6 调度与稳定性（W1）

- **目标**：分帧预算、warmup、relocation/classification 顺序、variability 降频、**不停更**。
- **改动**：扩展 `DynamicDiffuseGIUpdatePass.cs`（T5 创建）、`DDGIRelocateProbe.compute`、`DDGIProbeClassification.compute`、`DDGIVariability.compute`、`DDGIProbeUpdateList`。
- **验收**：每帧调度数 = `ceil(total/divisor)`（warmup 除外）；`divisor` 帧内覆盖全部 probe；动态光/自发光变化后 2×divisor 帧内恢复；`mIsConverged` 不存在完全停更路径；TLAS 重建次数只与场景/实例变化次数一致，**probe 调度不触发重建**（静态场景持续更新时应为 0 次）。
- **证据**：`validation/P6/Scheduling.json`、`Recovery.json`。
- **依赖**：T5、T9（动态恢复验收需要灯光 buffer）。

### T7 采样接入与共存（W3）

- **目标**：材质采样 DDGI；SSGI 互斥；APV/lightmap 兜底；三种 renderer 覆盖。
- **改动**：`ShaderLibrary/DynamicDiffuseGI/*`、`GlobalIllumination.hlsl`（不透明主路径）、`Lighting.hlsl` 的 `UniversalFragmentPBRSplit`（透明 Lit/ShaderGraph 路径）、`ScreenSpaceLightingRendererFeature.AddRenderPasses`（跳过 SSGI 入队 + 复位输出）、`ScreenSpaceLightingSsgiPass.ResetOutput()`。
- **验收**：Forward/Forward+/Deferred 阴影区 DDGI on/off 有可测差异；体积外回退 baked GI；DDGI 开启时 SSGI dispatch=0，且不透明绘制阶段 `_SCREEN_SPACE_GI` 关闭（以 `s_EditorSsgiBoundCount`/dispatch 计数器 + Frame Debugger 抽查为准，Cleanup 后的关键字状态不算证据）；DDGI 关闭时与基线逐像素一致；EditMode 契约测试（关键 include/关键字/互斥/ResetOutput）。
- **证据**：`validation/P7/`（图像 + 指标 + dispatch census）。
- **依赖**：T1、T4、T5。

### T8 多跳与能量（W2）

- **目标**：上一帧 probe 反馈、删除额外平方、能量归一化、收敛 A/B。
- **改动**：raygen 反馈、`DDGISampling.hlsl`。
- **验收**：1 跳 vs 2 跳（反馈开关）能量单调且不爆；静态场景收敛后帧间差 ≤0.00196；动态恢复不发黑；与参考实现对比记录能量差异。
- **证据**：`validation/P8/Energy.json`、A/B PNG。
- **依赖**：T7、T5。

### T9 灯光与天空（W1）

- **目标**：visibleLights 上传、方向/点/聚光、RT shadow、天空三种模式。
- **改动**：`DynamicDiffuseGILights.cs`、raygen 灯光循环。
- **验收**：方向光/点光/聚光的解析辐照度误差 ≤5%；遮挡时 shadow 生效；sky-only 场景非零且无 NaN；灯数超限只 warning。
- **证据**：`validation/P9/Lights.json`。
- **依赖**：T3（为 T6 提供灯光 buffer）。

### T10 调试与可观测性（W4）

- **目标**：probe/atlas/classification/relocation/未覆盖材质/stats。
- **改动**：`DDGIDebug.shader`、`DynamicDiffuseGIDebugPass.cs`、stats 字段。
- **验收**：每个 debug 模式有 PNG；stats JSON 包含 probe/调度/ray/dispatch/GPU ms；debug 不影响正式输出（与 Off 逐像素一致）。
- **证据**：`validation/P10/`。
- **依赖**：T6、T7。

### T11 验证资产与自动化（W4）

- **目标**：D3D12 player 自动验收；场景/三 renderer/Volume；开关、光变、体积外、APV 兜底。
- **改动**：`Assets/Tests/DynamicDiffuseGI/*`；创建 `validation/Invoke-DDGIPlayModeTests.ps1` 与 `validation/Invoke-DDGIPlayer.ps1`（D3D12 + 设置恢复）。
- **验收**：§6.5 的 **9 个用例**（`forward_off/on`、`forwardplus_off/on`、`deferred_off/on`、`light_moved`、`emissive_changed`、`outside_volume`）全部 `passed`；D3D11 另跑 `forward_off` 与 `deferred_off`，结果与基线一致；Scene View 冒烟（无 ACD 相机不入队，不计入 9 用例）；`settingsRestored=true`；0 非有限像素；D3D12 `supportsRayTracing=true`；PNG/Report.json 入库；可选 1 个 `.rdc`（≤20 MiB）。
- **证据**：`validation/P11/Report.json`、`Images/`。
- **依赖**：T7、T8、T9、T10。

### T12 性能与回归（W4/W5）

- **目标**：测量并冻结默认值；跑全量回归；检查构建变体。
- **验收**：
  - 1080p DDGI sampling ≤0.5 ms、update ≤4 ms（默认参数，参考 GPU RTX 5060 Ti；三跑中位数）。
  - D3D11 构建零 shader error；既有 T12 34/34/35；EditMode/PlayMode 全绿。
  - player 构建包含 DDGI shader/pass/keyword（不被剥离），按 T0 冻结的名称清单逐项断言。
  - `.rdc` 预算不超。
- **证据**：`validation/P12/Perf.json`、`T12/`、EditMode/PlayMode XML、`BuildVariants.json`。
- **依赖**：T11。

### T13 收尾与归档（W5）

- **目标**：最终报告、ACCEPTANCE 新增节（执行时编号）、清理、归档、文档一致性审计。
- **产出**：`validation/Report.md`（验收报告，区别于根目录的调研 `report.md`）、`validation/Cleanup.md`、`validation/KnownDifferences.md`、`validation/DocAudit.md`、`PLAN/ACCEPTANCE.md` 新增节（执行时读取最后一节编号，当前为 §15）；`PLAN/TODO/09…` → `PLAN/Done/09-DDGI_improve`。
- **验收**：§8.4 签收清单全部勾选、§7 的清理/文档项全部完成；所有 AC 有证据或明确 ACCEPTED；路径无 `PLAN/TODO/09` 漂移；任务冲突/遗漏审计通过。
- **依赖**：T12。

### 5.1 依赖图

```
T0  → T1, T2
T2  → T3
T3  → T4, T5, T9
T5, T9       → T6
T1, T4, T5   → T7
T7           → T8
T6, T7       → T10
T7, T8, T9, T10 → T11
T11 → T12 → T13
```

### 5.2 任务冲突/遗漏审计（摘要）

| ID | 冲突/遗漏 | 处理 |
|---|---|---|
| C1 | T6 “降频不停更” vs T8 多跳反馈 | T6 先完成；T8 的恢复测试包含动态光/自发光；T6 不得删除 TLAS 的“场景/实例变化”失效路径 |
| C2 | T7 DDGI/SSGI 互斥 vs 既有 SSL 测试 | DDGI 默认关闭；开启时跳过 SSGI 入队**并复位 `producedOutput`**（只跳过入队会让 Setup 在 207 重绑旧纹理并重开关键字）；AC-8 同时断言 dispatch=0 与不透明阶段关键字关闭；既有 T12 全部在 DDGI 关闭下跑 |
| C3 | T4 内置 shader 加 RT pass vs D3D11 编译 | 采用 HDRP 的 `#pragma raytracing` pass 模式；T4 门禁包含 D3D11 构建 0 error；失败则对非 RT 平台 exclude pass |
| C4 | T11 D3D12 player vs T12 D3D11 回归 | 一套 D3D12 player；D3D11 只跑 PlayMode off 用例 + T12 矩阵；脚本临时 `SetGraphicsAPIs` 并恢复，不修改默认 |
| C5 | T11 抓帧 vs `.rdc` 预算 | 默认 0 个；最多 1 个 ≤20 MiB；提交前 `git ls-files` + 字节统计 |
| C6 | T2 资源默认值 vs T3/T5 实现 | 默认值只在 T0 Contract 冻结；T2 只读契约，不自行改默认 |
| C7 | T7 采样接入 vs ShaderGraph 非目标 | 明示非目标；T10 的 UncoveredMaterials 标记；follow-up |
| C8 | T9 灯光 buffer vs 多相机 | 灯光每帧从 `visibleLights` 重建；DDGI history 全局单份，不按相机复制 |
| C9 | T6 预算 vs relocation 依赖固定 rays | 调度到的 probe 总是先跑 32 固定 ray；未调度 probe 不参与 relocation |
| C10 | T12 性能目标 vs 硬件差异 | T0 记录参考 GPU；若达不到，先降默认 divisor/rays，再重测；阈值写入 Contract |
| C11 | 测试计数增长 vs T14 基线 | 新增测试加入 round-local filter；计数增长不是回归（ACCEPTANCE §8.5） |
| C12 | 文档路径 vs 归档 | TODO 阶段只写 TODO 路径；T13 归档时统一改 Done 并检查 JSON/命令 |

> 完整审计见 §8；这里只列会阻塞实施或验收的冲突。

---

## 6. 验证与收尾流程

### 6.1 每个任务的提交门禁

1. 代码/资产改动完成，`git diff` 只包含该任务文件。
2. EditMode 通过：既有 21 类 filter（T0 记录的基线）＋ 本任务新增类，全部绿。
3. 如涉及 GPU：D3D11 构建/PlayMode 无回归；D3D12 相关用例通过。
4. 证据写入 `validation/<Pn>/`，JSON 含 `passed`、`head`、`timestamp`、`graphicsDeviceType`。
5. 更新本文件 §10 实施记录（日期/任务/结果/证据/commit）。
6. 一个任务一个 commit；标题沿用仓库的英文描述句风格，正文包含任务号（如 `T5`）与证据相对路径。

### 6.2 测试套件与命令

**EditMode（包内 + 新增）**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "PLAN/TODO/09-DDGI_improve/validation/Invoke-DDGIEditModeTests.ps1" `
  -LogPath "PLAN/TODO/09-DDGI_improve/validation/EditMode/UnityEditModeTests.log" `
  -ResultsPath "PLAN/TODO/09-DDGI_improve/validation/EditMode/UnityEditModeTests.xml"
```

round-local 脚本**复制** `PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14EditModeTests.ps1` 的 filter 列表（21 类，即本文件 §2.2 的基线口径），追加 DDGI filter，并调用 `PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1`。**不改 Done 脚本**。新增测试类统一放在 `UnityEditor.Rendering.Universal.Tests` 命名空间。追加：

```
UnityEditor.Rendering.Universal.Tests.DynamicDiffuseGIProbeIndexingTests
UnityEditor.Rendering.Universal.Tests.DynamicDiffuseGIOctahedralTests
UnityEditor.Rendering.Universal.Tests.DynamicDiffuseGIVolumeTests
UnityEditor.Rendering.Universal.Tests.DynamicDiffuseGIHistoryTests
UnityEditor.Rendering.Universal.Tests.DynamicDiffuseGICapabilityTests
UnityEditor.Rendering.Universal.Tests.DynamicDiffuseGIMaterialContractTests
UnityEditor.Rendering.Universal.Tests.DynamicDiffuseGIExclusivityTests
UnityEditor.Rendering.Universal.Tests.DynamicDiffuseGISchedulingTests
```

> 该 21 类 filter 是历史口径：`ScreenSpaceLightingProbeAtlasTests`、`ScreenSpaceLightingRefractionImproveTests` 目前不在其中。T0 决定是否纳入；若纳入，基线总数以新 XML 为准并在 `Contract.md` 记录。

**PlayMode（既有 SSL，D3D11 回归）**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14PlayModeTests.ps1" `
  -LogPath "PLAN/TODO/09-DDGI_improve/validation/PlayMode/UnityPlayModeTests.log" `
  -ResultsPath "PLAN/TODO/09-DDGI_improve/validation/PlayMode/UnityPlayModeTests.xml"
```

**PlayMode（DDGI，D3D12）**

round-local 脚本 `Invoke-DDGIPlayModeTests.ps1`，参数 `-GraphicsApi D3D12|D3D11`，内部**直接调用** `PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1`，在 `-AdditionalArguments` 中传 `-runTests -testPlatform PlayMode -testResults <xml> -testFilter Unity.DynamicDiffuseGI.Tests.PlayMode -force-d3d12`（`-force-d3d11` 同理）。不要经由 `Invoke-T14PlayModeTests.ps1`：它只接受 `-LogPath/-ResultsPath`，不转发附加参数。测试内先断言 `SystemInfo.supportsRayTracing`，不满足则 `Assert.Ignore` 并写 JSON。

**D3D12 Player**

```powershell
Unity.exe -batchmode -quit -projectPath <repo>/URPSample01 `
  -executeMethod DynamicDiffuseGIValidationPlayerSetup.BuildPlayer `
  -logFile PLAN/TODO/09-DDGI_improve/validation/P11/Build.log
```

- 构建前 `PlayerSettings.SetGraphicsAPIs(StandaloneWindows64, {Direct3D12})`，构建后在 `finally` 恢复原 API；写 `SettingsRestoration.json`。
- 构建产物固定到 `Builds/DdgiValidationPlayer/`（根级 `Builds/` 已被 `.gitignore` 忽略）；`validation/P11/` 只留 `BuildSummary.json` 与 `SettingsRestoration.json`。
- Player 以 development + `-ddgiValidationOutput <dir>` 运行；完成后读 `Report.json`。

**T12 回放（既有）**

复用 `PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14GraphicsRegression.ps1`（即 07 HANDOFF §7.2 记录的回放脚本），`-OutputRoot` 指向 `validation/P12/T12`；要求 34/34/35 与 DDGI 关闭状态一致。

**RenderDoc（可选，预算内）**

- 只在 T11 需要证明 dispatch 链时抓 1 帧；使用 in-app `RenderDocApi`（与 06 相同的模式）。
- 1280×720、单帧、D3D12；抓前确认 `.rdc` 总量 + 预计大小 ≤1024 MiB。

### 6.3 图像/性能指标

| 指标 | 口径 |
|---|---|
| 开关差异 | 阴影区平均亮度差（8-bit 归一化）、HF 能量差；DDGI off vs baseline 的 `differingPixels` 必须为 0 |
| 收敛 | 静止相机/灯光，warmup 后连续 8 帧 `frameDeltas[*]` ≤0.00196（0.5/255） |
| 动态恢复 | 移动光源/改自发光后 ≤2×divisor 帧回到阈值 |
| 非有限像素 | 0 |
| 体积外 | 与 baked GI 路径差 ≤1/255 |
| 性能 | 统一采样口径为 warmup 之后、divisor=8 的稳态；GPU ms（ProfilingSampler + player stats）+ TLAS/灯光上传 CPU ms，3 次中位数；1080p |
| 构建变体 | player 内 `Shader.Find`/关键字/dispatch 存在性检查；名称以 T0 Contract 冻结的清单为准 |

### 6.4 证据 schema（`Report.json`）

```json
{
  "schemaVersion": 2,
  "round": "09-DDGI_improve",
  "head": "<git hash>",
  "timestamp": "2026-11-20T00:00:00Z",
  "unityVersion": "2022.3.62f1",
  "graphicsDeviceType": "Direct3D12",
  "graphicsDeviceName": "NVIDIA GeForce ...",
  "supportsRayTracing": true,
  "completed": true,
  "passed": true,
  "settingsRestored": true,
  "warmupFrames": 8,
  "cases": [
    {
      "id": "deferred_on",
      "baseline": "deferred_off",
      "renderer": "Deferred",
      "ddgi": true,
      "resolution": [1280, 720],
      "frames": 32,
      "metrics": {
        "meanLuma": 0.0,
        "shadowRegionMeanLuma": 0.0,
        "shadowRegionMeanLumaDelta8bit": 0.0,
        "highFrequency": 0.0,
        "frameDeltas": [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0],
        "frameDeltaMax": 0.0,
        "nonFinitePixels": 0,
        "volumeOutsideDelta": 0.0
      },
      "imageDiff": {
        "baselineSha256": "<sha>",
        "imageSha256": "<sha>",
        "differingPixels": 0,
        "maxChannelDelta": 0.0
      },
      "dispatch": {
        "trace": 1, "irradiance": 1, "distance": 1, "copyHistory": 1,
        "relocate": 1, "classify": 1, "variability": 0,
        "ssgi": 0, "ssr": 1
      },
      "keywords": {
        "screenSpaceGI": false,
        "screenSpaceReflection": true,
        "ddgi": true
      },
      "tlasRebuilds": 0,
      "images": ["Images/deferred_on.png"],
      "passed": true
    }
  ],
  "perf": [
    {
      "case": "deferred_on",
      "resolution": [1920, 1080],
      "phase": "steadyState",
      "runs": 3,
      "gpuMs": {"trace": 0.0, "update": 0.0, "sampling": 0.0, "total": 0.0},
      "cpuMs": {"tlasBuild": 0.0, "lightsUpload": 0.0}
    }
  ],
  "artifacts": {"rdc": [], "png": [], "xml": []}
}
```

字段与验收项的绑定：

| AC | 依赖字段 | 口径 |
|---|---|---|
| AC-1 | `imageDiff`（`baselineSha256`/`imageSha256`/`differingPixels`） | DDGI off 用例的 `differingPixels=0` |
| AC-2 | `shadowRegionMeanLumaDelta8bit` | 阴影区遮罩由验证场景预置的 stencil/UV 区域定义，差值按 8-bit 归一化（0–1） |
| AC-3 | `frameDeltas`（长度 8）、`frameDeltaMax` | warmup 后连续 8 帧，全部 ≤0.00196 |
| AC-8 | `dispatch.ssgi=0`、`keywords.screenSpaceGI=false` | 关键字在**不透明绘制阶段**采样；Cleanup 后状态不算 |
| AC-10 | `dispatch.*`、`tlasRebuilds` | 与预期调度/失效次数逐项一致 |
| AC-11 | `perf`（`phase="steadyState"`、`runs=3`、`gpuMs`） | warmup 之后、divisor=8 的稳态；取中位数；`graphicsDeviceName` 绑定参考 GPU |

> `timestamp` 必须存在（§6.1 门禁第 4 条）；`warmupFrames` 内的性能数据不得进入 `perf`。
> AC-4/5/6/7/9/12/13 由 EditMode XML、构建日志、`T12Verification.json`、`Cleanup.md`、`DocAudit.md` 承载，不塞进 `Report.json`。

### 6.5 验收矩阵（最小）

| 用例 | Renderer | DDGI | 目的 |
|---|---|---|---|
| `forward_off` | Forward | off | 基线 |
| `forward_on` | Forward | on | 功能/收敛 |
| `forwardplus_off` | Forward+ | off | 基线（AC-1 覆盖 Forward+） |
| `forwardplus_on` | Forward+ | on | 功能 |
| `deferred_off` | Deferred | off | 基线 |
| `deferred_on` | Deferred | on | GBuffer 接入 |
| `light_moved` | Deferred | on | 动态恢复 |
| `emissive_changed` | Deferred | on | 多跳/自发光恢复 |
| `outside_volume` | Deferred | on | APV/baked 兜底 |

D3D11 另跑 `deferred_off` 与 `forward_off`，要求与基线逐像素一致；无 RT 时 `on` 用例必须记录“跳过 + warning”，不得算 pass。

### 6.6 验收标准

| ID | 标准 | 任务 | 证据 |
|---|---|---|---|
| AC-1 | DDGI 关闭或平台不支持时，图像与基线逐像素一致（diff=0） | T1/T7 | `P1`、`P7` |
| AC-2 | D3D12 开启后在阴影区产生可测间接光（与 DDGI off 的 8-bit 归一化均值差 ≥0.02，阈值在 T0 Contract 冻结），0 非有限像素 | T7 | `P7` |
| AC-3 | 静止场景 warmup 后连续 8 帧 `frameDeltas[*] ≤0.00196` | T5/T7 | `P7` |
| AC-4 | 八面体/索引/border/权重/状态单测全部通过 | T2 | EditMode XML |
| AC-5 | 每帧调度数符合 divisor；divisor 帧内覆盖全部 probe | T6 | `P6` |
| AC-6 | 动态光/自发光变化后 ≤2×divisor 帧恢复；无冻结 | T6/T8 | `P6`/`P8` |
| AC-7 | 公共可见内置不透明 shader（Lit/ComplexLit/SimpleLit/TerrainLit/SpeedTree7/8/ParticlesLit Opaque）含 `DDGIRayTracing` pass；每种材质贡献非零；Hidden Terrain 基座由继承覆盖 | T4 | `P4` |
| AC-8 | DDGI 开启时 SSGI dispatch=0，且**不透明绘制阶段** `_SCREEN_SPACE_GI` 关闭（Setup 未重绑 SSGI）；SSR/折射不变 | T7 | `P7`（dispatch 计数器 + Frame Debugger/`.rdc` 抽查） |
| AC-9 | D3D11 构建 0 shader error；T12 34/34/35；EditMode/PlayMode 全绿 | T4/T12 | 构建/T12/XML |
| AC-10 | D3D12 player `supportsRayTracing=true`；dispatch 链与统计一致 | T11 | `P11` |
| AC-11 | 1080p 稳态（warmup 后、divisor=8）sampling ≤0.5 ms、update ≤4 ms（参考 GPU：RTX 5060 Ti，3 次中位数） | T12 | `P12` |
| AC-12 | 产物：默认无新 `.rdc`；若有则 ≤1 个、单帧 ≤20 MiB、总量 ≤1024 MiB；无 Player/Library 入库 | T12/T13 | `Cleanup.md` |
| AC-13 | 文档一致：report/HANDOFF/ACCEPTANCE 无矛盾；任务依赖无环；路径可解析 | T13 | `DocAudit.md` |

---

## 7. 收尾与善后

### 7.1 测试改动

| 套件 | 改动 | 预期 |
|---|---|---|
| 包内 EditMode | 新增 8 个 DDGI 测试类 | 全绿；加入 round-local filter |
| PlayMode D3D11 | 既有 SSL 套件不变；DDGI assembly 另跑 off 用例（`-force-d3d11`） | SSL 6/6；DDGI off 用例通过/跳过 |
| PlayMode D3D12 | 新增 DDGI 测试 assembly | 有 RT 时全绿；无 RT 时 Ignore + JSON 记录 |
| T12 | 不改矩阵；在 DDGI 关闭状态重放 | 34/34/35 |
| Player | 新增 D3D12 DDGI player setup/verification | 9/9 |
| 设置恢复 | 所有 Unity 运行写 `*SettingsRestoration.json` | 全部 `true` |

测试计数会增长（184 → 约 200+）。**计数增长不是回归**（沿用 ACCEPTANCE §8.5）；最终以实际 XML 为准，并在 `Contract.md` 记录新总数。

### 7.2 产物留存与清理

**入库（权威证据）**

- `validation/**/Report.json`、`*.xml`、`*.png`（仅关键图）、`*.md`；构建类证据只留 `BuildSummary.json`/`SettingsRestoration.json`。
- `validation/Cleanup.md`：文件清单、大小、`.rdc` 数量/总字节、Player 是否删除、`git status` 快照。
- `validation/KnownDifferences.md`：ShaderGraph、透明物、体积外、能量差异、性能偏差等。

**仅在本地（不入库）**

- `*.log`（`.gitignore` 已排除；命令写入文档）。
- `Builds/DdgiValidationPlayer/` 构建目录、`Library/`、`Temp/`、`*.pdb`、临时截图。
- 除 T11 允许的 1 个小型 `.rdc` 外的所有抓帧。

**预算（与 10-ContactShadow 一致）**

- `validation/` 总大小 ≤50 MB；PNG ≤80 张且总量 ≤45 MB；超过时只保留每个用例 1 张代表图 + 指标 JSON。
- T12 回放（P12）只保留 `T12Verification.json`；全绿时不存 PNG，与基线哈希不一致时只存差异图；P0 基线只留 3 renderer × `Reference/AllOn/AllOffAgain` 共 9 张。
- 入库前用 `du -sh`/文件清单核对；超预算必须删图，不得删 `Report.json`。

**清理步骤（T13 必做）**

```bash
# 1) 删除 player 构建与临时目录（构建在根级 Builds/，已被忽略）
rm -rf Builds/DdgiValidationPlayer
# 2) 检查 .rdc 预算
git ls-files '*.rdc' | wc -l
git ls-tree -r -l HEAD | grep "\.rdc$" | awk '{s+=$4; n++} END {printf "files=%d bytes=%d MiB=%.1f\n", n, s, s/1048576}'
# 3) 检查被忽略但跟踪的文件
git ls-files -i -c --exclude-standard
# 4) 新资产 .meta 完整性（无孤儿 meta）
git ls-files --others --exclude-standard -- 'Assets/Tests/DynamicDiffuseGI/**' '*.meta'
find Assets/Tests/DynamicDiffuseGI -name '*.meta' -exec sh -c 'test -e "${1%.meta}" || echo "orphan: $1"' _ {} \;
# 5) 归档并提交后，再检查路径漂移（排除归档后的 Done 目录）
git grep -n "PLAN/TODO/09-DDGI_improve" -- ':!PLAN/Done/09-DDGI_improve/**'
# 6) 工作树
git status --short
```

> 当前 `.rdc` 为 58 个 / 1,048,717,328 B；`1024 MiB = 1,073,741,824 B`，余量约 24 MiB。**默认不新增**。

### 7.3 文档一致性

- `report.md`：调研结论、勘误表；正文不再把“144 rays / 153 万”当事实；§9 勘误表的“旧说法”列必须保留旧值，T13 的 grep 审计将 §9 列入白名单。
- `HANDOFF.md`：本文件；决策 D1–D10 与 report §7 一致；任务 T0–T13 与 AC 表一致。
- `PLAN/ACCEPTANCE.md`：T13 执行时读取当前最后一节编号，新增下一节（当前为 §15），引用 `validation/Report.md` 与 AC 结果；不修改历史章节。若 10-ContactShadow 先归档，则以届时最后一节为准。
- `KnownDifferences.md`：至少记录 ShaderGraph 未覆盖、透明物不贡献、体积外回退、`hysteresis` 0.97 vs 参考 0.98、能量/性能偏差。
- 归档：`PLAN/TODO/09-DDGI_improve` → `PLAN/Done/09-DDGI_improve`；T13 统一更新文档/JSON/命令中的路径；保留历史日志中的绝对路径并注明。

### 7.4 未完成项（follow-up，不阻塞 v1）

1. ShaderGraph `DDGIRayTracing` pass。
2. 多 volume / 无限滚动 / 体积随相机移动。
3. 透明物/粒子参与 probe 命中。
4. 软件 SDF/voxel 后端（非 DXR 平台）。
5. probe 动态分辨率、relocation 质量、分类状态机细化。
6. 与 APV 的更深集成（DDGI→SH 写入 APV 池）。

---

## 8. 文档一致性与任务冲突审计

### 8.1 已发现并修正的矛盾

| 位置 | 矛盾 | 修正 |
|---|---|---|
| report 旧 §3.1/§5.3 | 144 rays / 153 万 rays/frame | 256 rays / 273 万；Sponza 22×22×23 |
| report 旧 §3 | 参考仓库 Unity 2022.3.17f1c1 | 2022.3.62f1（README 过时） |
| report 旧 §2 | 暗示 HDRP/RTXGI 有 DDGI 或 SVGF | 明确 HDRP 无 DDGI；RTXGI/RTXDI 无 SVGF |
| report 旧 §5 | “DDGI 给 RTGI 多跳”当作通用事实 | 仅旧 RTXDI 1.2 示例；现代 RTXDI GI 不消费 DDGI |
| report 旧 §5 | “Lumen 用 ReSTIR” | 截至 UE 5.7 Lumen GI 无 ReSTIR；MegaLights 是直接光 |
| report 旧 §5 | 建议“SDF/voxel 或 cubemap 回退” | v1 决策为 D3D12+DXR；SDF 列为非目标 |
| HANDOFF vs report | 默认 22³/256/1/8 与 report 建议 | 以 HANDOFF D6/§3.4 为准；report §7 已同步 |
| HANDOFF §3.5/§4.4 | “SSGI 互斥只改 Feature 一处”与 `producedOutput` 只在 `Execute` 复位的实现冲突 | 改为“跳过入队 + `ResetOutput()`”；AC-8 限定不透明绘制阶段 |
| HANDOFF D9/T6 | “有 probe 更新就重建 TLAS”在不停更下退化为每帧重建 | 改为“场景/实例变化时重建” |
| HANDOFF §2.2 | 以 `0a73b2b` 的 182/182 作为现状 | 按实施 HEAD `720bedd` 实测为 184/6/34-34-35 |
| 任务 | T6 不停更与参考实现 `mIsConverged` | T6 明确禁止完全停更；T8 验证恢复 |

### 8.2 任务依赖/冲突检查

- 依赖图无环：T0 → {T1,T2}；T2 → T3；T3 → {T4,T5,T9}；{T5,T9} → T6；{T1,T4,T5} → T7；T7 → T8；{T6,T7} → T10；{T7,T8,T9,T10} → T11；T11 → T12 → T13。
- T6 依赖 T5 与 T9：动态恢复测试必须有灯光 buffer；T9 只依赖 T3。
- T8 依赖 T7（采样）+ T5（更新）；不得在 T6 之前做多跳，否则无法区分“恢复”和“预算延迟”。
- T11 依赖 T7/T8/T9/T10（`emissive_changed` 依赖 T8 的反馈）；T12 依赖 T11；T13 依赖 T12。
- 每个任务只改自己清单内的文件；跨任务改动必须在 HANDOFF §4.4 登记。

### 8.3 遗漏项检查（已补齐）

| 遗漏风险 | 补齐动作 |
|---|---|
| Player 构建剥离 DDGI shader/pass/keyword | T12 `BuildVariants.json`；player 内断言 |
| `SetGraphicsAPIs` 未恢复 | T11 `SettingsRestoration.json` |
| 多相机重复 history | T1 验收断言 Preview/Reflection 不入队、history 全局单份 |
| Editor Scene View | T1 gating 断言（无 ACD 相机不入队）；T11 做 Scene View 冒烟 |
| 体积外发黑 | T7 `outside_volume` 用例 + AC-2 / `P7` |
| APV/lightmap 双重计能量 | T7 混合契约 + 体积外回退测试 |
| 透明/ShaderGraph 静默缺失 | T10 `UncoveredMaterials` + KnownDifferences |
| 测试计数漂移 | §7.1；ACCEPTANCE §8.5 口径 |
| `.rdc` 预算 | §2.2/§7.2；默认 0 |
| 路径漂移 | T13 归档检查命令 |
| 文档与代码不一致 | §8.1 + T13 `DocAudit.md` |

### 8.4 签收清单

- [ ] T0 Contract 经仓库所有者确认（D1–D10、AC 阈值、参考 GPU）。
- [ ] 所有任务文件落在 §4.2/§4.3/§4.4 清单内，无越界修改。
- [ ] 每个任务一个 commit，§10 有记录。
- [ ] AC-1…AC-13 全部有证据或明确 ACCEPTED。
- [ ] `Cleanup.md`、`KnownDifferences.md`、`Report.md`、ACCEPTANCE 新增节（执行时编号）齐备。
- [ ] `.rdc` 预算检查通过；无 Player/Library/日志入库。
- [ ] 归档路径与交叉引用无 `PLAN/TODO/09` 残留。

### 8.5 仍需所有者确认的点（不算阻塞，但必须在 T0 落字）

| 项 | 现状 | 需要确认 |
|---|---|---|
| 参考 GPU | 测试机为 RTX 5060 Ti（§2.2 实测） | T0 Contract 记录该型号；AC-11 的 0.5/4 ms 以它为准；换机需重测 |
| 默认 22³/256/1/8 | 有意取参考实现上限再分帧 | 若目标机偏弱，T0 可把默认降到 16³/128/1/8；一旦冻结，T12 不再改 |
| ShaderGraph 优先级 | v1 非目标 | 是否接受“ShaderGraph 只接收 GI、不贡献 GI”的 v1 限制 |
| `.rdc` 余量 | 约 24 MiB | 是否允许 T11 抓 1 个 ≤20 MiB；若不允许，全部用 PNG/JSON |
| 归档目录名 | `PLAN/Done/09-DDGI_improve` | 沿用现有命名即可 |

---

## 9. 风险与回滚

| 风险 | 触发条件 | 处理 | 回滚 |
|---|---|---|---|
| DXR 不可用 | 测试机/目标机无 RT | T0 记录；不进入 T3；D3D11 路径保持原样 | 删除 T1 新增文件 |
| D3D11 shader 编译失败 | 内置材质加 RT pass | 用 HDRP 的 `#pragma raytracing` 模式；必要时对非 RT 平台 exclude pass | 回退该 shader pass |
| 变体被剥离 | player 无 DDGI 输出 | T12 构建检查；确保材质/场景引用；必要时加 shader preprocessor 规则 | 保持 DDGI 默认关闭 |
| 性能超标 | update >4 ms 或 sampling >0.5 ms | 降 rays/divisor/分辨率；记录基准；必要时把默认 grid 降到 16³ | 恢复上一组默认值 |
| 能量异常 | 反馈后过亮/发黑 | 关闭反馈 A/B；核对 `2π`/Lambert/额外平方；限制最大值 | 关闭多跳（T8 开关） |
| probe 漏光/穿墙 | 薄墙/大体积 | 调 thickness/relocation/Chebyshev；记录 KnownDifferences | 缩小默认 volume |
| 与 SSGI 双重计能量 | 两者同时开 | 互斥 early-out；测试 AC-8 | 强制 DDGI 关 |
| SSGI 输出状态残留 | 只跳过 SSGI 入队 | `producedOutput` 不复位，Setup 重绑旧纹理并开关键字；T7 复位 + AC-8 断言 opaque 阶段关键字关闭 | 关闭 DDGI 或回退 T7 |
| 证据超预算 | 新 `.rdc` 超 24 MiB | 删除本地抓帧，保留 PNG/JSON | 不提交 `.rdc` |
| 任务范围膨胀 | 想加 ShaderGraph/滚动/ReSTIR | 一律进 follow-up；v1 不做 | 拒绝范围变更 |

---

## 10. 实施记录

| 日期 | 任务 | 结果 | 证据 | Commit |
|---|---|---|---|---|
| 2026-11-19 | 计划定稿 | report/HANDOFF 更新，未实施 | 本文件、`report.md` | `145d9a4` |
| 2026-11-20 | 计划修订（复核 1–5） | 基线按 HEAD `720bedd` 重测；互斥/TLAS/schema/任务/流程修订 | `validation/P0/`、本文件 | `4b2956c` |

> 后续每个任务完成后在此追加一行；没有证据/commit 的任务不得标记完成。

---

## 11. 交接清单

**当前状态**：仅文档与 T0 基线证据；代码未实施。`PLAN/TODO/09-DDGI_improve/` 已在 `145d9a4` 提交，T0 证据在 `validation/P0/`（待提交）。

**恢复条件**

1. 关闭所有 Unity Editor 进程（T14 wrapper 会检查）。
2. Unity `2022.3.62f1`；测试机 D3D12 + DXR。
3. 从 T0 开始；T0 未完成不得进入 T3。
4. 每个任务完成即提交并更新 §10；不要累积多个任务再提交。

**下一位接手者先读**

1. 本文件 §0（决策）、§4（边界）、§5（任务）、§6（验收）。
2. [report.md](report.md) §5（参考实现解剖）、§5.7（本仓库 SSGI/SSR 对照）、§7（选型）、§9（勘误）。
3. `PLAN/Done/06-ValidationAllRDC/HANDOFF.md` §7.1（三层证据）与 §8（收尾/清理）、`PLAN/Done/08-Accurate_normals_fixed/HANDOFF.md`（测试/收尾格式）。

**完成判据**：§8.4 全部勾选，T13 归档到 `PLAN/Done/09-DDGI_improve`，ACCEPTANCE 新增节（执行时编号）记录 AC-1…AC-13 的结论与证据链接。