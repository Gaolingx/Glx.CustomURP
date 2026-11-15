# 02-SSRImprove 交接

> 更新：2026-11-16
> 状态：**本轮只完成了 SSGI 对齐与降噪库迁移；SSR 降噪改造尚未开始。**
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
- `URP/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute`：`ScreenSpaceReflectionsTracing`、`ScreenSpaceReflectionsReprojection`、`ClearBuffer2D`、`DebugSsrParams`，以及 **18 个 `ScreenSpaceReflectionsAccumulate*` kernel**（NoWorld/Hard/Smooth × Both/Source/Target × Debug）。
- 历史：`ScreenSpaceLightingHistory` 的 `ssrPbrOriginal`、`ssrPbrHistory[2]`、`ssrPbrReadSlot/WriteSlot`、`ssrPbrHasValidHistory`、`SyncSsrPbrAlgorithm`。
- 输出契约：radiance（非预乘）+ weight，材质端 `lerp(probe, radiance, weight)`；Approximation 的单点空洞用邻近同表面像素 3×3 cross 填充。
- Volume `ScreenSpaceReflection`：`usedAlgorithm`、`intensity`、`minSmoothness`、`smoothnessFadeStart`、`screenFadeDistance`、`depthBufferThickness`、`rayMaxIterations`、`reflectSky`、`accumulationFactor`、`biasFactor`、`enableWorldSpeedRejection`、`speedSmoothReject`、`speedRejectionParam`、`speedRejectionScalerFactor`、`speedSurfaceOnly`、`speedTargetOnly`。

### 2.2 与 HDRP `ReflectionDenoiser` 的差异

| HDRP 14.0.12 | 当前 URP |
|---|---|
| `TemporalAccumulationFullRes` / `TemporalAccumulationHalfRes` + `CopyHistory` | 18 个内联 accumulate kernel，无 CopyHistory |
| 时间累积按 depth/position 校验，历史 RGBA（rgb + sample count） | 自研速度拒绝（18 kernel），历史双缓冲；信号 alpha 为 weight |
| `BilateralFilterH_FR` / `BilateralFilterV_FR`（半分辨率对应 `_HR`），可分离两遍 | 无空间双边滤波，只靠 `accumulationFactor` |
| `_ReflectionFilterMapping`（`ReflectionKernelMapping.png`，按 `(NoV, roughness)` 缩放半径） | 无 roughness 相关滤波半径 |
| `_DenoiserFilterRadius`（`maxKernelSize`） | `accumulationFactor` + `biasFactor` 自研语义 |
| `affectSmoothSurfaces`、`singleReflectionBounce`、`roughnessBasedDenoising` | 无 |
| 历史 `.w` = sample count | `ssrPbrHistory` RGBA 存 radiance+weight（sample count 未单独存） |
| 曝光控制（`GetCurrent/InversePreviousExposureMultiplier`） | URP 无该阶段曝光，绑定 1/1 |
| Clear coat 单独通道 | base/coat 共用一次 trace，材质端按 hierarchy weight 混合 |

---

## 3. 计划（T0–T6）

> 依赖顺序：T0 → T1 → {T2, T3} → T4 → {T5, T6}。每项完成即提交，不合并大提交。

### T0：冻结 SSR 降噪契约（0.5 天）

- 决定历史布局：沿用本轮 SSGI 的 `(radiance, weight)` + 独立 sample count，还是改用 HDRP 的 `history.w = sampleCount` 并把 weight 留在信号里另存。**建议**：与 SSGI 统一为 `(radiance, weight)` + 独立 sample count，减少两套语义。
- 决定 Approximation 是否也走 TemporalFilter（HDRP 的 SSR 两者都过 temporal）；建议 Approximation 保持现状，只把 PBR 替换为 ReflectionDenoiser，避免同时改两条路径。
- 决定 `singleReflectionBounce` / `affectSmoothSurfaces` 的默认值与映射（没有 RT 路径时语义要重新解释）。
- 输出：契约表写入本文 + `ScreenSpaceLightingHistory` 注释，冻结后再改代码。

### T1：移植 `ReflectionDenoiser.compute` 并替换 PBR 累积（2–3 天）

- 新增 `URP/Shaders/Utils/Denoising/ReflectionDenoiser.compute`：
  - kernel：`TemporalAccumulationFullRes`、`TemporalAccumulationHalfRes`（`HALF_RESOLUTION`）、`CopyHistory`、`BilateralFilterH_FR`、`BilateralFilterV_FR`、`BilateralFilterH_HR`、`BilateralFilterV_HR`。
  - 参数沿用本轮 temporal 的命名与绑定方式；`_CurrentEffectResolution`、`_HistoryBufferSize`、`_HistorySizeAndScale` 由 URP 的 `_SsgiScreenSize`/`_SsgiTemporalParams` 映射。
  - 历史拒绝：复用 `ScreenSpaceLightingHistory` 的上一帧 depth/normal；`REFLECTION_HISTORY_REJECTION_THRESHOLD 0.75` 与 HDRP 一致。
  - alpha 通道按 T0 契约处理（weight 或 sample count）。
- `SsrPass`：`PBRAccumulation` 分支改为 trace → reproject（写 `ssrPbrOriginal`）→ temporal (+CopyHistory) → bilateral H/V；删除 18 个 accumulate kernel 与 `ClearAccumulationHistory`（或保留一个调试 kernel，见 T4）。
- 资源：`ssrPbrHistory` 双缓冲保留；新增 `ssrDenoiseIntermediate0/1`（复用 `ssgiDenoiseIntermediate` 模式）。
- 验收：PBR 三渲染器图像与旧实现的稳定帧差异可解释；速度拒绝用例改为历史失效/重投影用例。

### T2：`ReflectionKernelMapping` 资源与 roughness 滤波（0.5–1 天）

- 将 `HDRP/Runtime/RenderPipelineResources/Texture/ReflectionKernelMapping.png`（guid `82eec496...`）连同 `.meta` 复制到 `URP/Runtime/RendererFeatures/ScreenSpaceLighting/Resources/SsrDenoise/`，沿用 blue-noise 的 `Resources.Load` 模式，不新增序列化字段。
- `ReflectionDenoiser.compute` 按 HDRP 读取 `float2 mappingUV = float2(dot(viewWS, normal), roughness)`，`FINAL_PASS` 用 `radiusScale.x`，中间 pass 用 `.y`。
- `_DenoiserFilterRadius` 映射 Volume 的 `denoiserRadius`（若 Volume 没有该字段则新增 `ClampedIntParameter`，语义为最大滤波半径）。
- 验收：粗糙度梯度场景下半径单调；无 mapping 资源时回退到固定半径。

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

- [ ] `TemporalFilter`、`DiffuseDenoiser`、`ReflectionDenoiser`、`SSGI`、`SSR`、`BilateralUpsample`、`DepthPyramid`、Lit/ComplexLit/ColorPyramid 强制重导入消息数为 0。
- [ ] `git status` 仅含预期文件；新文件 `.meta` 完整；移动文件 GUID 不变（`git log --follow` + 资源引用不丢）。
- [ ] EditMode 全量通过（当前基线 416 passed）。

### 4.2 功能/回归

- [ ] Approximation 与 PBR 两种算法可切换，切算法重置历史（reset 计数递增）。
- [ ] 相机 cut / 分辨率变化 / 渲染缩放触发历史重置。
- [ ] Forward / Forward+ / Deferred 三路径都可见；Deferred 仍走 DepthNormals prepass（decal 前数据，已知差异）。
- [ ] `intensity=0` 与关闭效果逐字节恢复基线；miss 像素与 probe 基线逐像素一致。
- [ ] 静态相机 32 帧后收敛；动态相机/物体无拖影；无 NaN/Inf/负值。

### 4.3 视觉/数值

- [ ] 光滑金属球反射与 HDRP 参照对齐（同一场景、同一曝光）。
- [ ] 粗糙度梯度下滤波半径单调，无 `ReflectionKernelMapping` 时回退不产生明显台阶。
- [ ] 半分辨率开关无明显跳变；clear coat 的 base/coat 层次不双重计数。

### 4.4 证据清单（完成后必须存在）

```
PLAN/TODO/02-SSRImprove/validation/
├─ T1/T1ShaderMessages.json           # 强制重导入消息
├─ T1/UnityEditModeTests.xml          # EditMode 结果
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
- SSGI 调度：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsgiPass.cs`
- SSR 调度：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs`
- SSR shader：`URP/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute`
- 历史/资源：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingHistory.cs`、`ScreenSpaceLightingResources.cs`、`ScreenSpaceLightingShaderIDs.cs`
- HDRP 参照：`HDRP/Runtime/RenderPipeline/Raytracing/{HDReflectionDenoiser.cs,Shaders/Denoising/ReflectionDenoiser.compute}`、`HDTemporalFilter.cs`、`HDDiffuseDenoiser.cs`
- HDRP mapping 资源：`HDRP/Runtime/RenderPipelineResources/Texture/ReflectionKernelMapping.png`