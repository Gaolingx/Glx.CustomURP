# 02-SSRImprove 交接

> 更新：2026-11-16
> 状态：**SSGI 对齐与降噪库迁移完成；SSR T0 契约冻结、T1 `ReflectionDenoiser` 移植、T2 mapping 资源完成（2026-11-16）；T3 进行中。**
> 基线：Unity `2022.3.62f1`，URP `14.0.12`。
> URP 目标包：`E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12`（下文 `URP/`）。
> HDRP 源包：`E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12`（下文 `HDRP/`）。
> Shader include 一律使用 canonical 包名 `Packages/com.unity.render-pipelines.universal/...`，不含物理目录中的 `@14.0.12`。

---

## 1. 本轮已完成（2026-11-16）

### 1.1 提交

| 提交 | 内容 |
|---|---|
| `72e176f` Align SSGI reprojection, miss fallback and denoising with HDRP | 复投影 motion、二次降噪 jitter、intensity/upsample、`rayMiss` 回退链、历史法线、Gather、blue noise |
| `08e8d08` Move the SSGI denoisers to HDRP's denoising layout | 把 TemporalFilter / DiffuseDenoiser 迁入 `URP/Shaders/Utils/Denoising`，kernel 名与参数契约对齐 HDRP |

### 1.2 SSGI 行为对齐（`72e176f`）

- **复投影**：命中点的 depth 与 motion 都从 `hitPixel` 读取（原先误用接收者像素），与 HDRP `ScreenSpaceGlobalIllumination.compute:257` 一致。
- **二次降噪 jitter**：`frameIndex % 6` → `% 4`，与 64 点点分布的 4×16 分组匹配；SSGI 帧索引改为 HDRP 的 `frameCount % 16`。
- **intensity**：改为乘 radiance（>1 真正提亮），0 仍是精确 no-op；`bilateralUpsample` 接线，关闭时走新增的 `BoxUpSampleColor`。
- **未命中回退**：新增 Volume `rayMiss`（`None / Sky / ReflectionProbes / ReflectionProbesAndSky`），默认取 HDRP 的 `ReflectionProbesAndSky`；反射探针回退使用 `ReflectionProbe.defaultTexture`（默认反射/天空），天空回退使用 `RenderSettings.ambientProbe` 的 SH（compute 无法读 `unity_SH*`，由 C# 显式上传）。仍用 weight 表达结果，`None` 时保持原 baked GI。
- **历史法线**：新增上一帧 normal 双缓冲（捕获在 `ScreenSpaceLightingColorHistoryCapturePass`），`ValidateHistory` 增加 `dot < 0.65` 拒绝；降噪 tap 增加 `abs(zNF - hClip.w) > 0.1` 拒绝。
- **Gather**：半分辨率降噪补齐 HDRP 的 `GatherFilter` 重组（LDS 8×8 + 2×2 深度加权）。
- **Blue noise**：HDRP 的三张 LUT 以包内 `Resources/SsgiBlueNoise` 提供，`Sampling.hlsl` 实现了与 HDRP 相同的 BND 序列；资源缺失时回退 IGN + R2。

### 1.3 降噪库迁移（`08e8d08`）

目录（均为 URP-adapted 实现，不是 HDRP 原文件拷贝）：

| 文件 | 角色 | HDRP 来源 |
|---|---|---|
| `URP/Shaders/Utils/Denoising/DenoisingUtils.hlsl` | 重投影半径、Gaussian、2×2 上采样权重、阈值常量 | `DenoisingUtils.hlsl` |
| `URP/Shaders/Utils/Denoising/BilateralFilter.hlsl` | `SsgiBilateralData` / tap / 双边权重 | `BilateralFilter.hlsl` |
| `URP/Shaders/Utils/Denoising/TemporalFilter.compute` | `ValidateHistory` / `TemporalAccumulationColor` / `CopyHistory`（+ URP 的 `CopyHistoryNormals`） | `TemporalFilter.compute` |
| `URP/Shaders/Utils/Denoising/DiffuseDenoiser.compute` | `GeneratePointDistribution` / `BilateralFilterColor` / `GatherColor` | `DiffuseDenoiser.compute` |

- 文件用 `git mv` 连同 `.meta` 移动，**GUID 不变**，已序列化的 Renderer 资源引用未被破坏。
- 参数名对齐 HDRP：`_DenoiseInputTexture`、`_DenoiseOutputTextureRW`、`_ValidationBuffer(RW)`、`_HistoryBuffer`、`_HistorySampleCount(RW)`、`_HistoryDepthTexture`、`_HistoryNormalTexture`、`_CameraMotionVectorsTexture`、`_PointDistribution(RW)`、`_DenoiserFilterRadius`、`_JitterFramePeriod`、`_HalfResolutionFilter`、`_DenoiserResolutionMultiplierVals`、`_PixelSpreadAngleTangent`。
- 新增 HDRP 的 `_ReceiverMotionRejection` / `_OccluderMotionRejection` / `_EnableExposureControl` 代码路径；SSGI 调用方按 HDRP 一样绑定 0/0/0（HDRP 的 SSGI 也传 false）。
- `TemporalAccumulationColor` 后接 `CopyHistory` 写回历史槽，结构同 HDRP。

**URP 保留差异（代码内已注明）**：

1. 信号 alpha 承载 validity weight，历史存 `(radiance, weight)`，sample count 用独立单通道贴图（HDRP 把 sample count 存在 history.w 且默认信号全有效）。
2. Motion 已是 UV 空间，不做 HDRP 的 `DecodeMotionVector`。
3. 无通用 object-motion stencil 位，`ValidateHistory` 不做 stencil 拒绝；unlit/背景用远平面深度 + 零法线判定。
4. URP 颜色链在该阶段是 pre-exposure，曝光控制绑定关闭。

### 1.4 验证证据

- Unity 批处理 C# 编译通过；退出码 0。
- 强制重导入 `TemporalFilter`、`DiffuseDenoiser`、`SSGI`、`SSR`、`BilateralUpsample`、`DepthPyramid`、Lit/ComplexLit/ColorPyramid：`ShaderUtil` 消息数均为 **0**（含临时恒真条件的 blue-noise 分支单独编译）。
- EditMode 全量：**416 passed / 0 failed**。
- 未做：实际渲染对比、XR 播放器、PlayMode、T7/T8 重跑。

### 1.5 行为变化与遗留

- `rayMiss` 默认 `ReflectionProbesAndSky`：SSGI 开启后未命中像素以 weight 1 写入探针/天空，**像 HDRP 一样替换 baked GI（含 lightmap）**；设 `None` 恢复旧行为。
- 显存：上一帧 normal ×2 + 降噪中间 buffer，1280×720 约 +19 MB。
- 旧 T6/T9 验证脚本里“miss 与 baked GI 逐像素一致”的断言已不符合新默认，重跑前需明确用例（`rayMiss=None` 或更新期望）。
- `T14KnownDifferences.md` 尚未同步本轮差异。

---

## 2. SSR 现状与 HDRP 差异

### 2.1 当前实现

- `URP/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs`：事件 `AfterRenderingPrePasses + 6`（206），`usedAlgorithm = Approximation | PBRAccumulation`。
- `URP/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute`：只剩 `ScreenSpaceReflectionsTracing`、`ScreenSpaceReflectionsReprojection`、`DebugSsrParams`；18 个 accumulate kernel、`ClearBuffer2D` 和 `SSR_ACCUMULATE` 代码块已在 T1 删除。
- PBR 链路：trace → reproject（写 `ssrPbrOriginal`）→ `ReflectionDenoiser.compute` 的 `TemporalAccumulationFullRes` → `CopyHistory` → `BilateralFilterH_FR` → `BilateralFilterV_FR`（写 `ssrOutput`，材质消费）。
- 历史：`ScreenSpaceLightingHistory` 的 `ssrPbrOriginal`、`ssrPbrHistory[2]`、`ssrPbrSampleCount[2]`、`ssrDenoiseIntermediate0/1`、`ssrPbrReadSlot/WriteSlot`、`ssrPbrHasValidHistory`、`SyncSsrPbrAlgorithm`。
- 输出契约：radiance（非预乘）+ weight，材质端 `lerp(probe, radiance, weight)`；Approximation 的单点空洞用邻近同表面像素 3×3 cross 填充。
- Volume `ScreenSpaceReflection`：`usedAlgorithm`、`intensity`、`minSmoothness`、`smoothnessFadeStart`、`screenFadeDistance`、`depthBufferThickness`、`rayMaxIterations`、`reflectSky`、`biasFactor`、`denoiserRadius`、`affectSmoothSurfaces`、`singleReflectionBounce`；`accumulationFactor` 与 `speed*` 系列仍存在但 PBR 路径不再读取（T4 标记弃用）。

### 2.2 与 HDRP `ReflectionDenoiser` 的差异

| HDRP 14.0.12 | 当前 URP（T1 后） |
|---|---|
| `TemporalAccumulationFullRes` / `TemporalAccumulationHalfRes` + `CopyHistory` | ✅ `ReflectionDenoiser.compute` 已移植；T1 只调度全分辨率，半分辨率随 T3 接线 |
| 时间累积按 depth/position 校验，历史 RGBA（rgb + sample count） | ✅ 时间累积已移植；历史为 `(radiance, weight)` + 独立 sample count，另加历史法线拒绝 |
| `BilateralFilterH_FR` / `BilateralFilterV_FR`（半分辨率对应 `_HR`），可分离两遍 | ✅ FR 两遍已接；HR 变体已编译，T3 接线 |
| `_ReflectionFilterMapping`（`ReflectionKernelMapping.png`，按 `(NoV, roughness)` 缩放半径） | ✅ T2 已随包提供（HDRP 原 `.meta`），缺失时白纹理回退 |
| `_DenoiserFilterRadius`（`maxKernelSize`） | ✅ Volume `denoiserRadius`（1–32，默认 8）；半分辨率取一半随 T3 |
| `affectSmoothSurfaces`、`singleReflectionBounce`、`roughnessBasedDenoising` | ✅ 前两者是 Volume 参数；`roughnessBasedDenoising` 由 `singleReflectionBounce` 派生（同 HDRP） |
| 历史 `.w` = sample count | 保持 `(radiance, weight)` + 独立 `R16_SFloat`（T0 决策） |
| 曝光控制（`GetCurrent/InversePreviousExposureMultiplier`） | 不移植；URP 该阶段无曝光，radiance 为 pre-exposure |
| Clear coat 单独通道 | base/coat 共用一次 trace，材质端按 hierarchy weight 混合（不变） |

---

## 3. 计划（T0–T6）

> 依赖顺序：T0 → T1 → {T2, T3} → T4 → {T5, T6}。每项完成即提交，不合并大提交。

### T0：冻结 SSR 降噪契约（0.5 天）

- 决定历史布局：沿用本轮 SSGI 的 `(radiance, weight)` + 独立 sample count，还是改用 HDRP 的 `history.w = sampleCount` 并把 weight 留在信号里另存。**建议**：与 SSGI 统一为 `(radiance, weight)` + 独立 sample count，减少两套语义。
- 决定 Approximation 是否也走 TemporalFilter（HDRP 的 SSR 两者都过 temporal）；建议 Approximation 保持现状，只把 PBR 替换为 ReflectionDenoiser，避免同时改两条路径。
- 决定 `singleReflectionBounce` / `affectSmoothSurfaces` 的默认值与映射（没有 RT 路径时语义要重新解释）。
- 输出：契约表写入本文 + `ScreenSpaceLightingHistory` 注释，冻结后再改代码。

#### T0 结论（2026-11-16，已冻结）

| 决策项 | 结论 | 理由 |
|---|---|---|
| 历史布局 | RGBA16F 槽：`rgb` = 累积 radiance（非预乘），`a` = 最近一帧的 blend weight；sample count 独立 `R16_SFloat`，与历史同帧 ping-pong | 与 SSGI 的 `(radiance, weight)` + 独立计数一致；材质契约的 alpha 是 weight，不能与 sample count 共用 |
| 累积输入 | `ssrPbrOriginal`（`rgb` radiance，`a` weight）；temporal 只混合 `rgb`，写出当前帧的 `a` | 保持材质端 `lerp(probe, radiance, weight)`；HDRP 同样写输入 alpha |
| 空间滤波 | 可分离双边 H/V 只过滤 `rgb`，保留中心像素的 `a` | 同 HDRP；weight 是覆盖率，不随邻域 radiance 平均 |
| 历史拒绝 | temporal kernel 内联（HDRP 反射 denoiser 没有独立 Validate）：当前/历史 far depth、重投影出屏、世界半径超限，另加 URP 扩展 `dot(当前N, 历史N) < 0.75` | SSR 有深度法线 prepass，跨轮廓重投影需要法线拒绝；0.75 取 HDRP 文件声明的 `REFLECTION_HISTORY_REJECTION_THRESHOLD` |
| 粗糙度分支 | perceptual roughness `< 0.5` 走 TAA AABB clip（feedback 0.96/0.91）；`>= 0.5` 走 sample-count running average（0.93，上限 8） | 照搬 HDRP，不重新调参 |
| Approximation | 保持单帧 + 3×3 洞填充，不接 temporal/双边 | 一次只改一条路径；Approximation 依赖单帧 color pyramid 语义 |
| `singleReflectionBounce` | 默认 `true`；同时驱动 `_RoughnessBasedDenoising`（同 HDRP）。SSR 只有一次弹射，该开关只表示“允许按 roughness 缩放滤波半径” | HDRP 的默认值为 RT 多弹射设计，URP 需重新解释并写进 Tooltip |
| `affectSmoothSurfaces` | 默认 `false`；`smoothness > 0.99` 且 `singleReflectionBounce` 时跳过 temporal | 同 HDRP 的 early-out 条件 |
| `denoiserRadius` | 新增 `ClampedIntParameter`（1–32，默认 8）；半分辨率时取一半 | HDRP 14.0.12 默认值 |
| `ReflectionKernelMapping` | T1 绑定白色 fallback（等效固定半径），T2 换成 PNG；`_RoughnessBasedDenoising` 的绑定方式不变 | 把资源与 kernel 移植解耦，T1 不依赖新资产 |
| 半分辨率 | T1 只调度全分辨率 kernel；`HALF_RESOLUTION` 变体随 T3 接 dispatch | 与 T3 的资源布局一起验收 |
| XR | `SSL_XR_ARRAY`；历史/计数为 texture array；temporal 用 `SSL_PREV_INV_VIEW_PROJ`，双缓冲区按 slice 读写 | 与 SSGI 的 history 路径一致 |
| 曝光 | 不移植 exposure 代码；radiance 为 pre-exposure，绑定 1/1 | URP 该阶段没有曝光，与 SSGI 历史一致 |
| 旧 Volume 参数 | `biasFactor` 继续用于 trace 的 Rothko bias；`accumulationFactor`、`speed*` 系列在 PBR 路径不再读取，T4 标记弃用 | 不做破坏性删除，已有 Volume 覆盖仍可序列化 |
| 调试 | `SsrAccumulationSpeed` 保留枚举值但退化为 `SsrRaw`；`SsrPbrParams` 保留一个调试 kernel | 调试枚举是公开 API，序列化值不能重排 |

**单帧调度（T1 目标）**

```
trace                → ssrHitPoint
reproject            → ssrPbrOriginal        (radiance, weight)
TemporalAccumulation → ssrDenoiseIntermediate0 + ssrPbrSampleCount[writeSlot]
CopyHistory          → ssrPbrHistory[writeSlot] (累积 radiance, 当前 weight)
BilateralFilterH_FR  → ssrDenoiseIntermediate1
BilateralFilterV_FR  → ssrOutput             (材质消费)
```

历史读取槽在首帧/失效帧仍绑定一个已分配纹理，但 `_HistoryValidity = 0` 时 temporal 在采样前返回，因此不再需要 `ClearAccumulationHistory`。

### T1：移植 `ReflectionDenoiser.compute` 并替换 PBR 累积（2–3 天）

- 新增 `URP/Shaders/Utils/Denoising/ReflectionDenoiser.compute`：
  - kernel：`TemporalAccumulationFullRes`、`TemporalAccumulationHalfRes`（`HALF_RESOLUTION`）、`CopyHistory`、`BilateralFilterH_FR`、`BilateralFilterV_FR`、`BilateralFilterH_HR`、`BilateralFilterV_HR`。
  - 参数沿用本轮 temporal 的命名与绑定方式；`_CurrentEffectResolution`、`_HistoryBufferSize`、`_HistorySizeAndScale` 由 URP 的 `_SsgiScreenSize`/`_SsgiTemporalParams` 映射。
  - 历史拒绝：复用 `ScreenSpaceLightingHistory` 的上一帧 depth/normal；`REFLECTION_HISTORY_REJECTION_THRESHOLD 0.75` 与 HDRP 一致。
  - alpha 通道按 T0 契约处理（weight 或 sample count）。
- `SsrPass`：`PBRAccumulation` 分支改为 trace → reproject（写 `ssrPbrOriginal`）→ temporal (+CopyHistory) → bilateral H/V；删除 18 个 accumulate kernel 与 `ClearAccumulationHistory`（或保留一个调试 kernel，见 T4）。
- 资源：`ssrPbrHistory` 双缓冲保留；新增 `ssrDenoiseIntermediate0/1`（复用 `ssgiDenoiseIntermediate` 模式）。
- 验收：PBR 三渲染器图像与旧实现的稳定帧差异可解释；速度拒绝用例改为历史失效/重投影用例。

#### T1 结论（2026-11-16，已完成）

**新增/修改**

| 文件 | 内容 |
|---|---|
| `URP/Shaders/Utils/Denoising/ReflectionDenoiser.compute` | 7 个 kernel（`TemporalAccumulationFullRes/HalfRes`、`CopyHistory`、`BilateralFilterH/V_FR/HR`）；HDRP 的 AABB clip、sample-count running average、roughness 半径缩放、min smoothness tap 拒绝全部保留 |
| `BilateralFilter.hlsl` | `SsgiBilateralData` 增加 `roughness`，由深度法线 alpha 按 SSR trace 相同约定解码；SSGI 不使用该字段 |
| `ScreenSpaceLightingHistory` | PBR 资源增加 `ssrDenoiseIntermediate0/1` 与每槽 `R16_SFloat` sample count；释放路径同步；T0 契约写进注释 |
| `ScreenSpaceLightingResources` | 新增 `ssrReflectionDenoiserCompute` 与 `ssrReflectionFilterMapping`（`Resources.Load("SsrDenoise/ReflectionKernelMapping")`，缺失时白纹理） |
| `SsrPass` | PBR 分支改为 temporal → CopyHistory → bilateral H/V；删除 18 kernel 调度、`ClearAccumulationHistory`、速度拒绝系数；新增 denoiser 常量上传（复用 SSGI 常量集）与编辑器证据计数 |
| `ScreenSpaceReflections.compute` | 删除 `ClearBuffer2D`、18 个 accumulate kernel、`SSR_ACCUMULATE` 代码块与 `_SsrAccum*` 绑定 |
| `ScreenSpaceReflection` | 新增 `denoiserRadius`、`affectSmoothSurfaces`、`singleReflectionBounce` |
| `ScreenSpaceLightingRenderUtils` | `GetPreviousViewProjection` / `ComputeZBufferParams` / `ComputePixelSpreadTangent` 从 SsgiPass 提升为共享工具 |
| `ScreenSpaceLightingDebugMode` | `SsrAccumulationSpeed` 保留枚举值，文档标注为等价 `SsrRaw` |
| `ScreenSpaceLightingSsrPbrTests` | 删除 kernel 名映射测试，新增 denoiser 参数默认值、中间 buffer、sample count 槽位与 swap 测试 |

**实现细节**

- 时间累积的 reproject 校验：`_HistoryValidity`、当前/历史 far depth、出屏、世界半径（0.1 / 4px 容差），加 URP 扩展 `dot(N, historyN) < 0.75`。
- 历史读槽在失效帧绑定 `writeSlot ^ 1`，但 `_HistoryValidity = 0` 时 kernel 在采样前返回，所以不再需要清屏。
- `roughness < 0.5` 走 TAA AABB clip（0.96 / 0.91 feedback）；`>= 0.5` 走 sample count（0.93，上限 8），与 HDRP 逐行一致。
- 双边滤波只混合 rgb，中心 alpha 原样写出；`_RaytracingReflectionMinSmoothness` 绑定 Volume `minSmoothness`。
- 曝光、stencil、object-motion 拒绝按 T0 决策不移植；XR 走 `SSL_XR_ARRAY`，历史/中间 buffer 按 slice 读写。

**验证证据**（`validation/T1/`）

- `SslShaderCheck.txt`：9 个 shader/compute 强制重导入，7 个新 kernel 全部存在，`ClearBuffer2D` 与 3 个代表性 accumulate kernel 确认已删除，0 条 `ShaderUtil` 消息。
- `UnityShaderCheck.log`：批处理退出码 0，无 `Shader error` / `error CS` / `warning CS`。
- `UnityEditModeTests.xml`：**417 passed / 0 failed / 0 skipped**（基线 416，新增 4 删除 3）。
- `UnityEditMode.log`：同批次测试日志。
- 未做：实际渲染对比、Frame Debugger、XR 播放器、T7/T8/T10 重跑（T5 范围）。

**行为变化与遗留**

- PBR 稳定帧与旧实现不再逐像素一致：时间累积权重、空间双边、weight 不再参与时间混合。
- `accumulationFactor` 与 `speed*` 成为死参数；T4 处理文档/弃用标注。
- `SsrAccumulationSpeed` 调试视图退化为 `SsrRaw`；T8/T10 验证脚本仍读旧静态计数名，T5 需更新。
- 白色 fallback 让所有像素拿到满半径（8px）。真实 LUT 的 roughness 轴是「越光滑半径越小」：roughness 0 → 0，roughness ≥ 0.25 → 1。所以 T1 单独提交时，默认 `minSmoothness = 0.9` 下的光滑反射会被多余地空间模糊；T2 接入 PNG 后恢复 HDRP 行为（见下）。
- 半分辨率尚未接线；`T14KnownDifferences.md` 尚未同步。

### T2：`ReflectionKernelMapping` 资源与 roughness 滤波（0.5–1 天）

- 将 `HDRP/Runtime/RenderPipelineResources/Texture/ReflectionKernelMapping.png`（guid `82eec496...`）连同 `.meta` 复制到 `URP/Runtime/RendererFeatures/ScreenSpaceLighting/Resources/SsrDenoise/`，沿用 blue-noise 的 `Resources.Load` 模式，不新增序列化字段。
- `ReflectionDenoiser.compute` 按 HDRP 读取 `float2 mappingUV = float2(dot(viewWS, normal), roughness)`，`FINAL_PASS` 用 `radiusScale.x`，中间 pass 用 `.y`。
- `_DenoiserFilterRadius` 映射 Volume 的 `denoiserRadius`（若 Volume 没有该字段则新增 `ClampedIntParameter`，语义为最大滤波半径）。
- 验收：粗糙度梯度场景下半径单调；无 mapping 资源时回退到固定半径。

#### T2 结论（2026-11-16，已完成）

**资源与加载**

| 项 | 结果 |
|---|---|
| 资产 | `URP/Runtime/RendererFeatures/ScreenSpaceLighting/Resources/SsrDenoise/ReflectionKernelMapping.png`，连同 HDRP 原 `.meta`（GUID `82eec49626a00b047ba86244607816c8`）复制，沿用 blue-noise 的惯例 |
| 导入设置 | sRGB + mipmap + DXT1/自动压缩，与 HDRP 完全相同；`T14KnownDifferences` 旧稿里“线性、无 mip”的描述有误，HDRP 本身就是 sRGB+mip，必须一致才能得到相同半径 |
| 加载 | `ssrReflectionFilterMapping` 的 `Resources.Load("SsrDenoise/ReflectionKernelMapping")` 命中真实资源；缺失时仍回退白纹理（固定半径） |

**半径验收**（`validation/T2/MappingCheck.txt`，GPU 回读 + sRGB 解码后的线性值）

| roughness | mapping 半径系数 | `denoiserRadius = 8` 时的像素半径 |
|---|---|---|
| 0.000 | 0.000 | 0（不做空间滤波） |
| 0.063 | 0.09–0.10 | ~0.8 |
| 0.126 | 0.32–0.67（随 NoV 变化） | ~2.6–5.4 |
| ≥ 0.252 | 1.000 | 8 |

- 沿 roughness 轴单调不减（DXT1 量化容差 0.05 内）；NoV 只在过渡带（roughness 0.06–0.13）影响系数。
- 结论：LUT 让“光滑反射几乎不滤波、粗糙反射用满半径”，与 T1 的白纹理 interim 相反；T2 修正了 T1 对光滑反射的过度模糊。
- 单测 `ReflectionFilterMappingIsPackagedWithTheHdrpImportSettings` 锁定资源路径、尺寸、sRGB/mipmap 导入设置；EditMode **418 passed / 0 failed**。
- 未做：粗糙度梯度实拍截图（T5 视觉验收）。

### T3：历史校验与半分辨率（1–2 天）

- 复用 `TemporalFilter` 的 `ValidateHistory`（已支持 depth/normal/position/motion），或为反射单独建一次（HDRP 反射没有独立 Validate，直接按 depth/position 自检）。
- 半分辨率：`TemporalAccumulationHalfRes` + `BilateralFilterH/V_HR`；输出经现有 `BilateralUpSampleColor` 或 HDRP 的 half-res 路径回到全分辨率（二选一，需在 T0 定）。
- XR：`SSL_XR_ARRAY` 宏沿用；history 读写按 slice。
- 验收：半分辨率和全分辨率开关下无跳变；XR 单眼/双眼历史不串。

### T4：Volume 参数与清理（1 天）

- 新增/映射：`denoiserRadius`（最大滤波半径）、`roughnessBasedDenoising`、`affectSmoothSurfaces`、`singleReflectionBounce`（无 RT 时的解释写进 Tooltip）。
- 现有 `accumulationFactor`、`biasFactor`、`speed*` 系列：保留还是废弃需要 T0 决策；若保留，明确它们只作用于 history 校验/降噪系数。
- 删除 `ScreenSpaceReflections.compute` 中不再使用的 18 个 kernel、`ClearBuffer2D` 相关代码与 `_SsrAccum*` ID；`DebugSsrParams` 若仍要保留则保留一个 kernel。
- 验收：`ScreenSpaceReflections.compute` 只剩 trace/reproject（+调试图）；C# 无死引用；EditMode 全绿。

### T5：验证与证据（1 天）

- 重跑 T7（Approximation）与 T8（PBR）三渲染器用例；更新断言：
  - miss 像素仍与 probe 基线逐像素一致（Approximation 权 0）。
  - PBR 的稳定帧（32 帧预热）与旧实现对比，差异区域仅限降噪/滤波；
  - 动态相机/物体用例证明历史失效，无拖影。
- Frame Debugger 记录事件顺序（SSGI 205 → SSR 206 → GBuffer 210 / opaque 250）。
- 数值检查：无 NaN/Inf/负值；intensity=0 与首帧恢复基线。
- 证据保存到 `PLAN/TODO/02-SSRImprove/validation/T5/`，完成后随目录归档。

### T6：收尾（0.5 天）

- 更新 `T14KnownDifferences.md`：SSR 降噪替换、mapping 资源、URP 无 stencil/曝光差异。
- 更新本文“已完成”与 `PLAN/HANDOFF`（如果恢复根级 HANDOFF）。
- 清理临时脚本/临时资源；确认 `Resources` 下的 LUT 与 mapping 进入构建且不重复。

---

## 4. 收尾与验收标准

### 4.1 静态/编译

- [x] `TemporalFilter`、`DiffuseDenoiser`、`ReflectionDenoiser`、`SSGI`、`SSR`、`BilateralUpsample`、`DepthPyramid`、ColorPyramid、Debug 强制重导入消息数为 0（T1 证据）。
- [x] `git status` 仅含预期文件；新文件 `.meta` 完整（T1 证据）。
- [x] EditMode 全量通过（T1 后 417 passed）。
- [ ] `git log --follow` 确认无移动文件丢 GUID；T5 补 Lit/ComplexLit 重导入。

### 4.2 功能/回归

- [ ] Approximation 与 PBR 两种算法可切换，切算法重置历史（reset 计数递增）。
- [ ] 相机 cut / 分辨率变化 / 渲染缩放触发历史重置。
- [ ] Forward / Forward+ / Deferred 三路径都可见；Deferred 仍走 DepthNormals prepass（decal 前数据，已知差异）。
- [ ] `intensity=0` 与关闭效果逐字节恢复基线；miss 像素与 probe 基线逐像素一致。
- [ ] 静态相机 32 帧后收敛；动态相机/物体无拖影；无 NaN/Inf/负值。

### 4.3 视觉/数值

- [ ] 光滑金属球反射与 HDRP 参照对齐（同一场景、同一曝光）。
- [x] `ReflectionKernelMapping` 已随包提供，GPU 回读验证半径沿 roughness 单调不减（T2 证据）。
- [ ] 粗糙度梯度实拍无明显台阶；资源缺失时白纹理回退不产生可见跳变。
- [ ] 半分辨率开关无明显跳变；clear coat 的 base/coat 层次不双重计数。

### 4.4 证据清单（完成后必须存在）

```
PLAN/TODO/02-SSRImprove/validation/
├─ T1/SslShaderCheck.txt              # 强制重导入 + kernel 存在/删除检查
├─ T1/UnityShaderCheck.log            # 批处理编译日志
├─ T1/UnityEditModeTests.xml          # EditMode 结果（417 passed）
├─ T1/UnityEditMode.log               # EditMode 日志
├─ T2/MappingCheck.txt                # mapping 半径系数与单调性
├─ T2/UnityMappingCheck.log           # mapping 回读日志
├─ T2/UnityEditModeTests.xml          # EditMode 结果（418 passed）
├─ T2/UnityEditMode.log               # EditMode 日志
├─ T5/T7Verification.json             # Approximation 三渲染器
├─ T5/T8Verification.json             # PBR 三渲染器
├─ T5/FrameDebuggerReport.json        # Pass 顺序
└─ T5/*.png                           # Reference/Approximation/PBR/动态用例
```

### 4.5 复现命令（关闭其他 Unity 编辑器后执行）

```bash
# 编译 + shader 消息（临时 Editor 脚本 SslTempShaderCompileCheck，验证后删除）
"D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod SslTempShaderCompileCheck.Run -quit \
  -logFile "<abs>/validation/T1/UnityShaderCheck.log"

# EditMode
"D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" -runTests -testPlatform EditMode \
  -testResults "<abs>/validation/T1/UnityEditModeTests.xml" \
  -logFile "<abs>/validation/T1/UnityEditMode.log"
```

> Unity 批处理会重写 `ProjectSettings/{Graphics,Quality,Project}Settings.asset`，跑完必须按既有惯例 `git checkout --` 还原。

---

## 5. 风险与已知限制

1. **没有 RT 路径**：HDRP 的 `singleReflectionBounce`、`affectSmoothSurfaces`、`roughnessBasedDenoising` 默认值是为 RT 设计的，URP 只有屏幕空间，需在 T0 重新定义语义并在 Tooltip 说明。
2. **历史布局**：若坚持 weight-in-alpha，反射的 sample count 必须像 SSGI 一样单独存；不要复用 `history.w`，否则会和权重冲突。
3. **半分辨率 + XR**：两套宏组合的 variant 数量增加，构建时间与包体要观察。
4. **旧的 T7/T8 证据**：本轮 SSGI 默认 `rayMiss` 已改变，SSR 改造也会改变 PBR 稳定帧；重跑时以“同版本、同场景、同曝光”的新基线为准，不直接对比旧 PNG。
5. **`ReflectionKernelMapping.png`** 是 HDRP 资产，复制后在 URP 内需保持线性、无压缩、无 mip（沿用 HDRP `.meta` 的导入设置）。
6. **构建保留**：新增 `Resources` 资源会进入所有构建；若需 strip，另立任务，不在本轮顺手做。

---

## 6. 关键文件索引

- SSGI 降噪库：`URP/Shaders/Utils/Denoising/{DenoisingUtils.hlsl,BilateralFilter.hlsl,TemporalFilter.compute,DiffuseDenoiser.compute}`
- SSR 降噪：`URP/Shaders/Utils/Denoising/ReflectionDenoiser.compute`
- SSR 滤波半径 LUT：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/Resources/SsrDenoise/ReflectionKernelMapping.png`
- SSGI 调度：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsgiPass.cs`
- SSR 调度：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs`
- SSR shader：`URP/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute`
- 历史/资源：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingHistory.cs`、`ScreenSpaceLightingResources.cs`、`ScreenSpaceLightingShaderIDs.cs`
- HDRP 参照：`HDRP/Runtime/RenderPipeline/Raytracing/{HDReflectionDenoiser.cs,Shaders/Denoising/ReflectionDenoiser.compute}`、`HDTemporalFilter.cs`、`HDDiffuseDenoiser.cs`
- HDRP mapping 资源：`HDRP/Runtime/RenderPipelineResources/Texture/ReflectionKernelMapping.png`