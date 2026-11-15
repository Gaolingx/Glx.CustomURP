# 02-SSRImprove 交接

> 更新：2026-11-16
> 状态：**SSGI 对齐与降噪库迁移完成；SSR T0 契约、T1 `ReflectionDenoiser` 移植、T2 mapping 资源、T3 半分辨率、T4 参数弃用与清理、T5 三渲染器实拍与 Frame Debugger 完成（2026-11-16）；T6 未开始。**
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
- PBR 链路：trace → reproject（写 `ssrPbrOriginal`）→ `ReflectionDenoiser.compute` 的 `TemporalAccumulationFullRes/HalfRes` → `CopyHistory` → `BilateralFilterH/V_FR/HR`（写回 `ssrPbrOriginal`）。全分辨率直接把它交给材质；半分辨率再用 `BilateralUpSampleColor` 升到全分辨率 `ssrOutput`。
- 历史：`ScreenSpaceLightingHistory` 的 `ssrPbrHitPoint`、`ssrPbrOriginal`、`ssrPbrHistory[2]`、`ssrPbrSampleCount[2]`、`ssrDenoiseIntermediate0/1`、`ssrPbrReadSlot/WriteSlot`、`ssrPbrHasValidHistory`、`SyncSsrPbrAlgorithm`；尺寸跟随 trace（半分辨率时为半屏）。
- 输出契约：radiance（非预乘）+ weight，材质端 `lerp(probe, radiance, weight)`；Approximation 的单点空洞用邻近同表面像素 3×3 cross 填充。
- Volume `ScreenSpaceReflection`：`usedAlgorithm`、`fullResolution`、`intensity`、`minSmoothness`、`smoothnessFadeStart`、`screenFadeDistance`、`depthBufferThickness`、`rayMaxIterations`、`reflectSky`、`biasFactor`、`denoiserRadius`、`affectSmoothSurfaces`、`singleReflectionBounce`；`accumulationFactor` 与 `speed*` 系列在 T4 标记 `[Obsolete]`，仍可反序列化但 PBR 路径不读取。

### 2.2 与 HDRP `ReflectionDenoiser` 的差异

| HDRP 14.0.12 | 当前 URP（T1 后） |
|---|---|
| `TemporalAccumulationFullRes` / `TemporalAccumulationHalfRes` + `CopyHistory` | ✅ 已移植；T3 起两种分辨率都调度 |
| 时间累积按 depth/position 校验，历史 RGBA（rgb + sample count） | ✅ 时间累积已移植；历史为 `(radiance, weight)` + 独立 sample count，另加历史法线拒绝 |
| `BilateralFilterH_FR` / `BilateralFilterV_FR`（半分辨率对应 `_HR`），可分离两遍 | ✅ FR/HR 都已在 T3 接线 |
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
| 历史布局 | RGBA16F 槽：`rgb` = 累积 radiance（非预乘），`a` = 最近一帧的 blend weight；sample count 独立 `R16_SFloat`，与历史同帧 ping-pong。T3 起历史跟随效果分辨率（全分辨率或半屏），上一帧 depth/normal 仍为全分辨率 | 与 SSGI 的 `(radiance, weight)` + 独立计数一致；材质契约的 alpha 是 weight，不能与 sample count 共用 |
| 累积输入 | `ssrPbrOriginal`（`rgb` radiance，`a` weight）；temporal 只混合 `rgb`，写出当前帧的 `a` | 保持材质端 `lerp(probe, radiance, weight)`；HDRP 同样写输入 alpha |
| 空间滤波 | 可分离双边 H/V 只过滤 `rgb`，保留中心像素的 `a` | 同 HDRP；weight 是覆盖率，不随邻域 radiance 平均 |
| 历史拒绝 | temporal kernel 内联（HDRP 反射 denoiser 没有独立 Validate）：当前/历史 far depth、重投影出屏、世界半径超限，另加 URP 扩展 `dot(当前N, 历史N) < 0.75` | SSR 有深度法线 prepass，跨轮廓重投影需要法线拒绝；0.75 取 HDRP 文件声明的 `REFLECTION_HISTORY_REJECTION_THRESHOLD` |
| 粗糙度分支 | perceptual roughness `< 0.5` 走 TAA AABB clip（feedback 0.96/0.91）；`>= 0.5` 走 sample-count running average（0.93，上限 8） | 照搬 HDRP，不重新调参 |
| Approximation | 保持单帧 + 3×3 洞填充，不接 temporal/双边 | 一次只改一条路径；Approximation 依赖单帧 color pyramid 语义 |
| `singleReflectionBounce` | 默认 `true`；同时驱动 `_RoughnessBasedDenoising`（同 HDRP）。SSR 只有一次弹射，该开关只表示“允许按 roughness 缩放滤波半径” | HDRP 的默认值为 RT 多弹射设计，URP 需重新解释并写进 Tooltip |
| `affectSmoothSurfaces` | 默认 `false`；`smoothness > 0.99` 且 `singleReflectionBounce` 时跳过 temporal | 同 HDRP 的 early-out 条件 |
| `denoiserRadius` | 新增 `ClampedIntParameter`（1–32，默认 8）；半分辨率时取一半 | HDRP 14.0.12 默认值 |
| `ReflectionKernelMapping` | T1 绑定白色 fallback（等效固定半径），T2 换成 PNG；`_RoughnessBasedDenoising` 的绑定方式不变 | 把资源与 kernel 移植解耦，T1 不依赖新资产 |
| 半分辨率 | ✅ T3 已接线：PBR trace/reproject/denoise 半分辨率 + `BilateralUpSampleColor` 升采样；Approximation 保持全分辨率 | 洞填充是 3×3 全分辨率语义，两条路径同时改风险大 |
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
- `T14KnownDifferences.md` 尚未同步；T4 清理死参数。

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

#### T3 结论（2026-11-16，已完成）

**决策**

| 项 | 结论 |
|---|---|
| 生效范围 | 半分辨率只作用于 PBR；Approximation 保持全分辨率（洞填充是 3×3 全分辨率语义） |
| 升采样 | 复用现有 `BilateralUpsample.compute` 的 `BilateralUpSampleColor`（深度加权 2×2），不新增 kernel |
| 历史分辨率 | 历史、sample count、中间 buffer 跟随效果分辨率；上一帧 depth/normal 仍是全分辨率（颜色历史捕获的产物） |
| 拒绝 | 沿用 T1 的内联校验，几何坐标映射回全分辨率后再做 depth/position/normal 检查；没有独立 `ValidateHistory` pass |
| XR | trace/denoise/history 在 trace 尺寸上按 slice；upsample 写全分辨率 array |

**实现**

- Volume 新增 `fullResolution`（默认 `true`，Tooltip 说明仅 PBR 生效）。
- `ScreenSpaceReflections.compute` 新增 `SSR_HALF_RES` 与 `SsrGetFullResCoord`：trace/reproject 的深度、法线、motion、几何重建和 Hi-Z marching 全部走全分辨率坐标，hit 写 NDC（与分辨率无关），输出/hitpoint 走 trace 坐标。
- `ScreenSpaceLightingSsrShaderVariables` 新增 `_SsrTraceSize`（布局 8→9 个 Vector4，生成 include 与布局测试同步）。
- `ScreenSpaceLightingHistory.EnsureSsrPbrResources` 增加屏幕 + trace 尺寸参数；新增独立 `ssrPbrHitPoint`，PBR 历史/计数/中间 buffer 全部按 trace 分配；尺寸变化触发重分配 + 历史重置。
- `SsrPass`：trace/reproject/denoise 用 trace groups，upsample 用 screen groups；temporal kernel 在 `FullRes/HalfRes` 间选择，双边在 `FR/HR` 间选择；`_DenoiserFilterRadius` 半分辨率取 `denoiserRadius / 2`；pixel spread 用 trace 尺寸。
- V 双边统一写回 `ssrPbrOriginal`（trace 尺寸）：全分辨率直接作为材质输入，半分辨率经 `BilateralUpSampleColor` 写入 `ssrOutput`。
- `ReflectionDenoiser` 的重投影改为归一化 UV + effect texel 边界检查，删除 HDRP 的 `_HistorySizeAndScale`/`_HistoryBufferSize` 换算（URP 历史不做动态缩放）；上一帧 depth/normal 用全分辨率 UV 采样。

**验证证据**（`validation/T3/`）

- `SslShaderCheck.txt`：SSR / ReflectionDenoiser / BilateralUpsample 三个 compute 强制重导入，12 个 kernel 全部存在。
- `UnityShaderCheck.log`：批处理退出码 0，无 `Shader error` / `error CS`。
- `UnityEditModeTests.xml`：**419 passed / 0 failed / 0 skipped**（T2 基线 418 + 半分辨率资源测试）。
- 未做：半/全分辨率切换实拍、XR 播放器、动态相机历史失效（T5）。

**已知限制**

- 切换 `fullResolution` 会重分配 PBR 资源并重置历史，首帧相当于算法切换，属预期。
- 半分辨率下 `SsrSamplePreviousColor` 仍按全分辨率 motion/color pyramid 取样，这是刻意的：历史源本来就是全分辨率。
- 半分辨率 trace 的 GPU 开销下降，但 upsample 与全分辨率 depth 采样是新增开销；T5 记录 Frame Debugger 事件。

### T4：Volume 参数与清理（1 天）

- 新增/映射：`denoiserRadius`（最大滤波半径）、`roughnessBasedDenoising`、`affectSmoothSurfaces`、`singleReflectionBounce`（无 RT 时的解释写进 Tooltip）。
- 现有 `accumulationFactor`、`biasFactor`、`speed*` 系列：保留还是废弃需要 T0 决策；若保留，明确它们只作用于 history 校验/降噪系数。
- 删除 `ScreenSpaceReflections.compute` 中不再使用的 18 个 kernel、`ClearBuffer2D` 相关代码与 `_SsrAccum*` ID；`DebugSsrParams` 若仍要保留则保留一个 kernel。
- 验收：`ScreenSpaceReflections.compute` 只剩 trace/reproject（+调试图）；C# 无死引用；EditMode 全绿。

#### T4 结论（2026-11-16，已完成）

**参数弃用**

| 参数 | 处理 | 说明 |
|---|---|---|
| `accumulationFactor` | `[Obsolete]`，字段保留 | 时间混合改由 denoiser 的 sample count 决定；旧 Volume 覆盖仍可反序列化 |
| `speedRejectionParam` / `speedRejectionScalerFactor` | `[Obsolete]`，字段保留 | 速度拒绝已由逐像素 history 校验取代 |
| `speedSmoothReject` / `speedSurfaceOnly` / `speedTargetOnly` / `enableWorldSpeedRejection` | `[Obsolete]`，字段保留 | 同属旧速度拒绝，均无效果 |
| `biasFactor` | 保持启用 | 仍用于 trace 的 GGX 可见法线 bias |
| `SsrAccumulationSpeed` 调试枚举 | 保留值 13 | `ScreenSpaceLightingDebugPass` 合并到 `SsrRaw` 分支，不再要求 PBR |

- 弃用字段带 `[Obsolete]`、`<summary>` 与 Tooltip 说明“保留仅为序列化兼容、无效果”；不做破坏性删除，避免旧 Volume Profile 丢覆盖。
- `denoiserRadius` / `affectSmoothSurfaces` / `singleReflectionBounce` 已在 T1 接入，`fullResolution` 在 T3 接入；`roughnessBasedDenoising` 不单独暴露，由 `singleReflectionBounce` 派生（同 HDRP）。

**清理**

- `ScreenSpaceReflections.compute` 在 T1 后已只剩 `ScreenSpaceReflectionsTracing`、`ScreenSpaceReflectionsReprojection`、`DebugSsrParams` 三个 kernel；T4 复核确认无 `_SsrAccum*`、`ClearBuffer2D`、accumulate kernel 残留。
- 删除 `ScreenSpaceLightingShaderIDs.DstSize`（与 `ScreenSpaceDepthPyramidPass` 内部 ID 重复且无人使用）。
- 删除 `SsrPass` 的死属性 `usingPbrAccumulation` 与字段 `m_UsingPbrAccumulation`（T4 前仅调试图读过）。
- 测试：原 `VolumeExposesPbrAccumulationControls` 改为 `VolumeExposesPbrControlsAndDeprecatesTheUnusedAccumulationAndSpeedOnes`，用反射锁定 7 个弃用字段仍存在、仍是 `VolumeParameter`、都带 `[Obsolete]`，并确认 `biasFactor` 未弃用、默认算法仍为 Approximation。

**验证证据**（`validation/T4/`）

- `SslShaderCheck.txt`：SSR / ReflectionDenoiser / BilateralUpsample 强制重导入消息数为 0；SSR compute 只有 3 个 kernel；`_SsrAccum` / `ClearBuffer2D` / accumulate kernel 在 shader 与 Runtime 源码中均为 False；7 个弃用字段 `present=True obsolete=True`，`biasFactor obsolete=False`。
- `UnityShaderCheck.log`：批处理退出码 0。
- `UnityEditModeTests.xml`：**419 passed / 0 failed / 0 skipped**（T3 基线 419，替换 1 个测试）。
- 未做：T5 的实拍、Frame Debugger、XR 播放器验证。

**行为变化与遗留**

- Volume Inspector 仍会显示这些字段（带 Deprecated Tooltip）；若需要隐藏，只能等破坏性删除或自定义 Volume 编辑器，本轮不做。
- 旧 T8/T10 验证脚本若直接读取 `accumulationFactor` / `speed*`，会在编译时收到 CS0618 警告；T5 更新脚本时改用 denoiser 参数或 `#pragma warning disable 618`。

### T5：验证与证据（1 天）

- 重跑 T7（Approximation）与 T8（PBR）三渲染器用例；更新断言：
  - miss 像素仍与 probe 基线逐像素一致（Approximation 权 0）。
  - PBR 的稳定帧（32 帧预热）与旧实现对比，差异区域仅限降噪/滤波；
  - 动态相机/物体用例证明历史失效，无拖影。
- Frame Debugger 记录事件顺序（SSGI 205 → SSR 206 → GBuffer 210 / opaque 250）。
- 数值检查：无 NaN/Inf/负值；intensity=0 与首帧恢复基线。
- 证据保存到 `PLAN/TODO/02-SSRImprove/validation/T5/`，完成后随目录归档。

#### T5 结论（2026-11-16，已完成）

**工具**

| 项 | 内容 |
|---|---|
| 新增脚本 | `Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingSsrT5Verification.cs`：三渲染器 Reference / Approximation / PBR 全分辨率 / PBR 半分辨率实拍 + 数值断言；复用 T8 的 `SslT8{Mode}Renderer/Pipeline` 与 T3 验证场景，并在运行时用 `ScreenSpaceLightingResourceLocator.AssignDefaults` 补齐旧序列化资源里缺失的 `ssrReflectionDenoiserCompute`（不改写资产） |
| 反射机位 | 镜面地板 + 自发光背板 + 4 个 smoothness 0.95/0.80/0.65/0.40 的金属球 + 非金属光滑球，覆盖 LUT 半径梯度 |
| Frame Debugger | `ScreenSpaceLightingFrameDebuggerCapture` 新增 `--ssl-fd-ssr-pbr`（运行时克隆 Volume Profile 并切到 PBR，不写资产）与 SSL 资源补全 |

**三渲染器数值结果**（1280×720，32 帧预热；`validation/T5/T5Verification.json`）

| 检查 | Forward | ForwardPlus | Deferred |
|---|---|---|---|
| Reference / Approximation / PBR 全 / PBR 半 是否产出 | ✅ | ✅ | ✅ |
| miss 像素与 probe 基线（PBR 全分辨率） | 825,556 像素，0 差异，max 0 | 同 | 同 |
| miss 像素与 probe 基线（Approximation） | 0 差异 | 0 差异 | 0 差异 |
| `intensity=0` 恢复基线 | 0 像素差异 | 0 | 0 |
| 历史 reset 后首帧恢复基线 | 0 像素差异 | 0 | 0 |
| NaN / Inf / 负 radiance | 0 | 0 | 0 |
| PBR 全分辨率 dispatch（trace/reproject/temporal/copy/H/V） | 30/30/30/30/30/30 | 同 | 同 |
| 全分辨率 kernel / 半径 | `BilateralFilterV_FR` / 8 | 同 | 同 |
| 半分辨率 kernel / 半径 / upsample | `BilateralFilterV_HR` / 4 / 30 | 同 | 同 |
| 半分辨率历史尺寸 | 640×360（generation 2） | 同 | 同 |
| 算法切换 / 相机 cut reset | 2 / 2 | 2 / 2 | 2 / 2 |
| 全 vs 半 tone-mapped 可见差异（>0.2） | 312 px（0.034%） | 312 px | 317 px（0.034%） |
| 运动停止 48 帧后 vs reset 基线 | 7 px >0.02，max tone 0.040 | 同 | 同 |

- 全/半分辨率原始最大差 43.7（HDR 高光），但 tone-mapped 超过 0.2 的仅 0.034% 像素；两张 PNG 目视一致。
- “PBR 稳定帧与旧实现对比”无法执行：旧累积路径已在 T1 删除。改为 Approximation vs PBR、全 vs 半分辨率与 reset 基线对比，差异集中在反射区域。
- 动态用例：运动 12 帧无 NaN/Inf；停止后不再 reset、收敛 48 帧的结果与 reset 基线仅 7 个像素超过 0.02（0.0008%），无拖影。
- Lit / ComplexLit 强制重导入消息数 0（`LitReimportCheck.txt`）。

**Frame Debugger**（`validation/T5/FrameDebugger/FrameDebuggerSummary.txt`）

| 渲染器 | 事件数 | 顺序（索引） |
|---|---|---|
| Forward / Forward+ | 88 | MotionVectors(5) → SSL Depth Pyramid(7–18) → SSGI(19–25) → SSR(26–31) → DrawOpaque(32) |
| Deferred | 99 | MotionVectors(9) → Depth Pyramid(8–19) → SSGI(20–26) → SSR(27–32) → Render GBuffer(33) → Deferred Shading(36–43) |

- PBR 模式下 SSR 事件为 **6**（trace、reproject、TemporalAccumulation、CopyHistory、BilateralFilterH、BilateralFilterV），Approximation 时只有 2。三个渲染器一致。
- T5 计划文字里的 `205/206/210/250` 是 HDRP 事件编号；URP 的 Frame Debugger 不暴露数字 ID，这里记录名称与索引，顺序关系 `Depth Pyramid → SSGI → SSR → GBuffer/opaque` 与计划一致。

**证据**（`validation/T5/`）

- `T5Verification.json`：三渲染器全部 `passed=true`、`completed=true`。
- 24 张 PNG：`T5_{Forward,ForwardPlus,Deferred}_{Reference,Approximation,PbrFullRes,PbrHalfRes,PbrIntensityZero,PbrMoving,PbrSettled,CameraCut}.png`。
- `FrameDebugger/FrameDebuggerReport.json` + 每渲染器 JSON + `FrameDebuggerSummary.txt`（RESULT: PASS）。
- `LitReimportCheck.txt`、`UnitySsrT5Verification.log`、`UnityLitReimportCheck.log`、`UnityEditModeTests.xml`（419 passed）。
- 未做：XR 播放器（T13 已历史覆盖，但新的 PBR 降噪半分辨率路径没有在 XR 下重跑）、HDRP 同场景参照图。

**运行注意**

- Frame Debugger 采集需要 GUI 编辑器（不带 `-batchmode`），且会进入 Play Mode；若上一次进程残留会占项目锁，新实例会以 0 直接退出且不写产物，重跑前确认 `tasklist | grep Unity.exe` 为空。
- 旧 `ScreenSpaceLightingT8Verification` 仍引用已删除的 speed rejection kernel 与 `accumulationFactor`，其断言不再成立；T5 脚本取代它做 PBR 验证，T6 可考虑删除或降级为历史脚本。

### T6：收尾（0.5 天）

- 更新 `T14KnownDifferences.md`：SSR 降噪替换、mapping 资源、URP 无 stencil/曝光差异。
- 更新本文“已完成”与 `PLAN/HANDOFF`（如果恢复根级 HANDOFF）。
- 清理临时脚本/临时资源；确认 `Resources` 下的 LUT 与 mapping 进入构建且不重复。

---

## 4. 收尾与验收标准

### 4.1 静态/编译

- [x] `TemporalFilter`、`DiffuseDenoiser`、`ReflectionDenoiser`、`SSGI`、`SSR`、`BilateralUpsample`、`DepthPyramid`、ColorPyramid、Debug 强制重导入消息数为 0（T1/T4 证据）。
- [x] `git status` 仅含预期文件；新文件 `.meta` 完整（T1 证据）。
- [x] EditMode 全量通过（T4 后 419 passed）。
- [x] `ScreenSpaceReflections.compute` 仅剩 trace/reproject/`DebugSsrParams`，无 accumulate kernel、`_SsrAccum*`、`ClearBuffer2D` 残留（T4 证据）。
- [x] 旧 Volume 参数 `accumulationFactor` 与 `speed*` 已 `[Obsolete]` 且不再被 PBR 路径读取；`biasFactor` 仍驱动 trace（T4 证据）。
- [x] `git log --follow` 确认移动的降噪文件保留历史；Lit / ComplexLit 强制重导入消息数 0（T5 证据）。

### 4.2 功能/回归

- [x] Approximation 与 PBR 两种算法可切换，切算法重置历史（T5：两次切换 reset=2，三渲染器一致）。
- [x] 相机 cut / 分辨率变化 / 渲染缩放触发历史重置（T5：camera cut reset=2；分辨率/缩放由 T3 资源键与重置单测覆盖）。
- [x] Forward / Forward+ / Deferred 三路径都可见；Deferred 仍走 DepthNormals prepass（decal 前数据，已知差异；T5 实拍与 Frame Debugger）。
- [x] `intensity=0` 与关闭效果逐字节恢复基线；miss 像素与 probe 基线逐像素一致（T5：0 差异，miss 825,556 像素 max 0）。
- [x] 静态相机 32 帧后收敛；动态相机/物体无拖影；无 NaN/Inf/负值（T5：运动后 48 帧收敛到 reset 基线，仅 7 像素 >0.02）。
- [x] 半分辨率开关无跳变；切换后 PBR 资源重分配且历史重置（T5：tone-mapped 可见差异 0.034%，history 640×360、generation 2、reset 计数递增）。

### 4.3 视觉/数值

- [ ] 光滑金属球反射与 HDRP 参照对齐（同一场景、同一曝光）。本轮无 HDRP 同场景截图，保留为 T6 可选项。
- [x] `ReflectionKernelMapping` 已随包提供，GPU 回读验证半径沿 roughness 单调不减（T2 证据）。
- [x] 粗糙度梯度实拍无明显台阶；资源缺失时白纹理回退不产生可见跳变（T5：0.95/0.80/0.65/0.40 四档金属球实拍；回退路径由 T2 单测锁定）。
- [x] 半分辨率开关无明显跳变（T5：目视一致，0.034% 像素 tone-mapped 差 >0.2）；clear coat 的 base/coat 层次不双重计数仍由 T9 历史证据覆盖，本轮未重跑。

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
├─ T3/SslShaderCheck.txt              # SSR/denoiser/upsample kernel 检查
├─ T3/UnityShaderCheck.log            # 批处理编译日志
├─ T3/UnityEditModeTests.xml          # EditMode 结果（419 passed）
├─ T3/UnityEditMode.log               # EditMode 日志
├─ T4/SslShaderCheck.txt              # kernel 清单、死引用、弃用字段检查
├─ T4/UnityShaderCheck.log            # 批处理编译日志
├─ T4/UnityEditModeTests.xml          # EditMode 结果（419 passed）
├─ T4/UnityEditMode.log               # EditMode 日志
├─ T5/T5Verification.json             # 三渲染器 reference/Approximation/PBR 全/半 + 数值断言
├─ T5/T5_<Mode>_<State>.png           # 24 张实拍（Reference/Approximation/PbrFullRes/PbrHalfRes/PbrIntensityZero/PbrMoving/PbrSettled/CameraCut）
├─ T5/FrameDebugger/FrameDebuggerReport.json      # 三渲染器事件列表
├─ T5/FrameDebugger/FrameDebuggerSummary.txt      # 顺序与 SSR=6 dispatch 断言
├─ T5/FrameDebugger/{Forward,ForwardPlus,Deferred}.json
├─ T5/LitReimportCheck.txt            # Lit/ComplexLit 强制重导入
├─ T5/UnitySsrT5Verification.log      # 实拍日志
├─ T5/UnityLitReimportCheck.log       # 重导入日志
├─ T5/UnityEditModeTests.xml          # EditMode 结果（419 passed）
└─ T5/UnityEditMode.log               # EditMode 日志
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

# T5 三渲染器实拍（GUI 或 batchmode 均可；退出由脚本 EditorApplication.Exit 控制）
"D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingSsrT5Verification.RunSsrT5Verification -quit \
  -logFile "<abs>/validation/T5/UnitySsrT5Verification.log"

# T5 Frame Debugger（必须 GUI，不带 -batchmode；确保没有残留 Unity 进程）
"D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingFrameDebuggerCapture.Capture \
  -logFile "<abs>/validation/T5/FrameDebugger/UnitySsrT5FrameDebugger.log" \
  --ssl-fd-output "PLAN/TODO/02-SSRImprove/validation/T5/FrameDebugger" \
  --ssl-fd-names "Forward,ForwardPlus,Deferred" \
  --ssl-fd-pipelines "Assets/Tests/ScreenSpaceLighting/Renderers/SslT8ForwardPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT8ForwardPlusPipeline.asset,Assets/Tests/ScreenSpaceLighting/Renderers/SslT8DeferredPipeline.asset" \
  --ssl-fd-scene "Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity" \
  --ssl-fd-ssr-pbr 1
```

> Unity 批处理会重写 `ProjectSettings/{Graphics,Quality,Project}Settings.asset`，跑完必须按既有惯例 `git checkout --` 还原。

---

## 5. 风险与已知限制

1. **没有 RT 路径**：HDRP 的 `singleReflectionBounce`、`affectSmoothSurfaces`、`roughnessBasedDenoising` 默认值是为 RT 设计的，URP 只有屏幕空间，需在 T0 重新定义语义并在 Tooltip 说明。
2. **历史布局**：若坚持 weight-in-alpha，反射的 sample count 必须像 SSGI 一样单独存；不要复用 `history.w`，否则会和权重冲突。
3. **半分辨率 + XR**：两套宏组合的 variant 数量增加，构建时间与包体要观察。
4. **旧的 T7/T8 证据与脚本**：本轮 SSGI 默认 `rayMiss` 已改变，SSR 改造也改变了 PBR 稳定帧；`ScreenSpaceLightingT8Verification`（旧 PBR 累积 + speed rejection）已失效，T5 由 `ScreenSpaceLightingSsrT5Verification` 取代。旧 PNG 只作历史参考，不直接对比。
5. **`ReflectionKernelMapping.png`** 是 HDRP 资产，复制后必须沿用 HDRP `.meta` 的导入设置（sRGB + mipmap + DXT1），否则 roughness 半径曲线会偏移（T2 已锁定）。
6. **构建保留**：新增 `Resources` 资源会进入所有构建；若需 strip，另立任务，不在本轮顺手做。
7. **验证资产陈旧**：T2–T12 的 `SslT*Renderer.asset` 序列化时还没有 `ssrReflectionDenoiserCompute`；T5 与 Frame Debugger 均在内存里调用 `AssignDefaults` 补齐，未改写资产。若要在编辑器里手动检查 PBR，需先选中 Feature 让 Inspector 重新赋值或运行一次 T5 脚本。
8. **Frame Debugger 运行约束**：需要 GUI 编辑器并进入 Play Mode；残留 Unity 进程会占项目锁使新实例直接退出（exit 0、无产物）。采集前确认无 Unity 进程。
9. **半分辨率 HDR 高光**：全/半分辨率在极亮、细小的反射上可差 40+（线性），属于分辨率损失而非上采样错误；验收用 tone-mapped 可见面积（<0.5% 像素差 >0.2）判断。

---

## 6. 关键文件索引

- SSGI 降噪库：`URP/Shaders/Utils/Denoising/{DenoisingUtils.hlsl,BilateralFilter.hlsl,TemporalFilter.compute,DiffuseDenoiser.compute}`
- SSR 降噪：`URP/Shaders/Utils/Denoising/ReflectionDenoiser.compute`
- SSR 滤波半径 LUT：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/Resources/SsrDenoise/ReflectionKernelMapping.png`
- SSGI 调度：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsgiPass.cs`
- SSR 调度：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs`
- SSR shader：`URP/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute`
- 历史/资源：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingHistory.cs`、`ScreenSpaceLightingResources.cs`、`ScreenSpaceLightingShaderIDs.cs`
- SSR T5 验证：`Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingSsrT5Verification.cs`
- Frame Debugger 采集：`Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingFrameDebuggerCapture.cs`（`--ssl-fd-ssr-pbr`）
- T5 证据：`PLAN/TODO/02-SSRImprove/validation/T5/`（JSON、24 张 PNG、FrameDebugger 目录）
- HDRP 参照：`HDRP/Runtime/RenderPipeline/Raytracing/{HDReflectionDenoiser.cs,Shaders/Denoising/ReflectionDenoiser.compute}`、`HDTemporalFilter.cs`、`HDDiffuseDenoiser.cs`
- HDRP mapping 资源：`HDRP/Runtime/RenderPipelineResources/Texture/ReflectionKernelMapping.png`