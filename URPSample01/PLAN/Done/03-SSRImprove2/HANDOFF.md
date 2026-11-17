# 03-SSRImprove2 交接

> 更新：2026-11-16
> 状态：**T1–T3 已实现并验证；T4 完成（Frame Debugger 因非交互会话无法采集，见 §3.1）；T5 文档已更新。**
> 基线：Unity `2022.3.62f1`，URP `14.0.12`，当前 HEAD `c02f851`（02 收尾 + SSGI temporal 修复）。
> URP 目标包：`E:/GitHub/Glx.CustomURP/URPSample01/Packages/com.unity.render-pipelines.universal@14.0.12`（下文 `URP/`）。
> HDRP 源包：`E:/workspace/GitHub/HDRPSample01/Library/PackageCache/com.unity.render-pipelines.high-definition@14.0.12`（下文 `HDRP/`）。
> 外部参照：`E:/GitHub/UnitySSGIURP/`（下文 `SSGIURP/`），其回退实现见 `Shaders/SSGIFallback.hlsl`。
> 上一轮交接：`PLAN/Done/02-SSRImprove/HANDOFF.md`（T0–T6 + 审核修复）。

---

## 1. 本轮起点（02 已收尾，03 只写了本文档）

### 1.1 02 的最终状态

| 提交 | 内容 |
|---|---|
| `ed4ed8b` | 删除 7 个弃用的 SSR Volume 参数与 `SsrAccumulationSpeed` 调试项 |
| `4cf9489` | 同步 `T14KnownDifferences.md`、删除旧 T8 脚本、Resources LUT 打包检查 |
| `9e0adee` | SSR 三渲染器实拍 + Frame Debugger（419 passed） |
| `c02f851` | **审核修复**：SSGI temporal 历史 UV 尺度、velocity buffer 绑定、jitter 移到第一遍、第二遍写 LF 输出；新增 SSGI temporal 收敛检查与 T5 回归（证据 `validation/AuditFix/`） |

- 02 的 T0–T6 全部完成；SSR PBR 使用 HDRP `ReflectionDenoiser`，Approximation 保持单帧 3×3 洞填充；半分辨率只作用于 PBR。
- `PLAN/Done/02-SSRImprove/validation/AuditFix/T5RegressionSummary.txt`：修复后 T5 三渲染器全部 pass，仅两个运动序列像素计数变化（379184→379180、293236→293223），属预期。

### 1.2 本轮完成的审核结论（决定 03 范围）

对 SSR/SSGI 与 HDRP 逐项对照后，结论按“可改 / 需调研 / 不改”分类：

| 类别 | 结论 |
|---|---|
| SSR PBR 算法族 | URP 用 HDRP **RT 反射 denoiser** 替换了 HDRP 屏幕空间 SSR 的 18-kernel accumulation（HDRP 的 `ReflectionDenoiser` 只服务 RT 反射）。这是 T0 的刻意选择，本轮**不改**；不要按 HDRP 屏幕空间 SSR 的拖影/速度拒绝逐像素对标 |
| SSR 采样 | HDRP SSR 用 BND blue noise（1SPP tile）；URP SSR **未接 LUT**，实际走 IGN + R2 → 本轮 T1 |
| SSR 半分辨率 | HDRP **屏幕空间 SSR 本身是全分辨率**，`2c` 只来自其 RT 反射 denoiser；URP 取 `2c+1` → 本轮 T2 |
| SSGI 探针回退 | HDRP 逐像素 env-light list + atlas + 权重；URP 只有默认 cubemap → 本轮 T3（先调研） |
| SSGI 其他 | 无 APV、无曝光、无 object-motion stencil、历史为 `(radiance, weight)` + 独立 sample count → 维持 T14 已知差异，本轮不改 |
| 文档 | 3 处过时注释（blue noise、mip clamp、SSR 采样描述）在 T5 收尾时一并修 |

### 1.3 本轮已实现（2026-11-16）

- T0：HDRP 调研结论与四项决策写入 §3/§9。
- T1：SSR 接入 BND blue noise，新增 1SPP LUT；帧索引直传 `Time.frameCount`。
- T2：新增 `SslComputeSourceCoordinates`，SSR trace/reproject 与 ReflectionDenoiser 同源取 `2c`。
- T3：新增 compute 安全头 `ReflectionProbeFallback.hlsl`（Forward+ atlas + zbin/tile cluster + box 权重 + LOD 0），SSGI 回退在 Forward+ 走 atlas，其余渲染路径保持默认 cubemap。
- T4：新增 `ScreenSpaceLightingSsgiFallbackCheck`，验证探针颜色、四种 `rayMiss` 与三渲染器；Frame Debugger 在本会话无法采集（非交互环境）。

---

## 2. 本轮目标与范围

只做用户确认的三项：

1. **T1 — SSR 接 blue noise**（已同意）。
2. **T2 — SSR 半分辨率几何坐标对齐 HDRP**（已同意）。
3. **T3 — SSGI 探针回退精度**（先调研 HDRP 与 `SSGIURP/Shaders/SSGIFallback.hlsl`，再实现；调研已完成，见 §5）。

**非目标（明确不做）**：
- 不把 SSR PBR 改回 HDRP 屏幕空间累积；不引入 speed rejection / `accumulationFactor`。
- 不移植曝光控制、object-motion stencil、APV。
- 不改材质端 weight 契约（`lerp(probe, radiance, weight)`）。
- 不改 Transparent SSR / Refraction / GTAO。
- 不新增公开 Volume 参数（T3 如确需开关，先按 §9 讨论）。

**依赖顺序**：T0 冻结 → T1 ∥ T2 → T3 → T4 → T5。每个任务单独提交。

---

## 3. T0：本轮契约（已冻结）

| 决策项 | 建议结论 | 理由 |
|---|---|---|
| SSR blue-noise 关键字 | 复用 `SSGI_BLUE_NOISE`，不新增 `SSL_BLUE_NOISE` | `Sampling.hlsl` 已由该宏控制；SSR/SSGI 是两个独立 compute，关键字互不影响；避免复制宏与变体名 |
| SSR blue-noise 资源 | **新增 1SPP tile**（`ScramblingTile1SPP` / `RankingTile1SPP`），Owen 256 与 SSGI 共用 | HDRP SSR 绑 1SPP、SSGI 绑 8SPP，Owen 共用；1SPP 是 SSR 的精确对齐 |
| SSR 帧索引 | 由 `Time.frameCount % 64` 改为**直传 `Time.frameCount`** | HDRP 传每相机单调计数、C# 不取模，shader 内 `& 255`；直传即等效 256 帧循环 |
| SSR 缺失 LUT | 保持 `Sampling.hlsl` 的 IGN + R2 回退，关键字关闭 | 与 SSGI 同一回退语义 |
| 半分辨率坐标公式 | SSR trace/reproject/denoiser 统一 `floor((float2)traceCoord * (screen/trace))` → 2× 时取 `2c` | 对齐 HDRP RT 反射 denoiser 的 `ComputeSourceCoordinates`（`RayTracingCommon.hlsl:63-67`）；注意 HDRP 屏幕空间 SSR 无半分辨率 |
| SSGI 半分辨率坐标 | **不改**，保持 `round((c+0.5)*inv)` → `2c+1` | HDRP SSGI `GetLowResCoord`（`ScreenSpaceGlobalIllumination.compute:73-76`）就是 `2c+1`；HDRP 自身两套约定并存 |
| 历史 | 半分辨率仍跟随 trace 尺寸；算法/分辨率切换重分配 + reset（已有） | 不改变 T3 契约 |
| T3 探针数据 | Forward+ 用 URP 全局 probe atlas + cluster；非 Forward+ 保留默认 cubemap | URP 14 只有 Forward+ 暴露 atlas 全局；非 FP 的 `unity_SpecCube*` 在 compute 不可读（见 §5.2） |
| T3 APV | 不迁移 | URP 14.0.12 无 APV/ProbeVolume shader 集成；core 的 `ProbeReferenceVolume` 只面向 HDRP |
| T3 反射相机 | **无需处理**（已确认） | `ScreenSpaceCapability.EvaluateCamera` 已对 `CameraType.Reflection` / `Preview` 直接返回不支持（`ScreenSpaceCapability.cs:121-126`），SSGI/SSR 根本不会 dispatch；HDRP 同样是禁用而非 dim，且无 0.3 系数 |
| T3 Volume 参数 | 不新增；沿用 `rayMiss`（None / Sky / ReflectionProbes / ProbesAndSky） | 回退层级已有语义，不扩公开 API |

**已确认决策（2026-11-16，HDRP 调研后冻结）**：
1. T3 范围：**仅 Forward+ atlas**；非 Forward+ 保留默认 cubemap，不做多探针上传。
2. SSR 帧索引：**直传 `Time.frameCount`**；T5 基线截图需重录并标注预期变化。
3. 反射相机：**不改**；现有 capability 检查已跳过 `Reflection`/`Preview`。
4. 坐标对齐：**只改 SSR**（trace + denoiser 同源 2c）；SSGI 保持 HDRP 的 `2c+1`。
5. SSR LUT：**新增 1SPP tile**，Owen 共用 SSGI 的 256 资产。

**HDRP 调研修正**（详见 §5）：
- HDRP 屏幕空间 SSR 是全分辨率（`HDRenderPipeline.LightLoop.cs:1253-1254`），`2c` 仅属于 RT 反射 denoiser。
- HDRP SSR 的 `_FrameCount` 是每相机单调计数，C# 不取模，shader 内 `& 255`。
- HDRP SSR 的 blue noise 无关键字、无缺失回退，绑 1SPP tile；SSGI 绑 8SPP tile。
- HDRP 反射相机通过 FrameSettings 关闭 SSGI（平面反射例外），没有 0.3 调暗。

### 3.1 本轮验证结果（`PLAN/Done/03-SSRImprove2/validation/T4/`）

| 证据 | 结果 |
|---|---|
| `UnityImport.log` | 批量导入与 C# 编译 0 error（唯一告警是仓库既有的 `ScreenSpaceReflections.compute.meta` 缺尾换行） |
| `EditModeTests.xml` | 418/418 passed |
| `UnitySsrT5Verification.log` + `T5/` | Forward / Forward+ / Deferred 全 pass；半分辨率 `fullVsHalfToneMappedPixelFraction` 0.000315（改前 0.00034） |
| `UnitySsgiTemporalCheck.log` + `SsgiTemporal/` | 三渲染器 pass；`coveredFraction` 0.034438 与 AuditFix 基线一致 |
| `UnitySsgiFallbackCheck.log` + `SsgiFallback/` | 三渲染器 pass；Forward+ 探针可见数 1，`ReflectionProbes` 均值 (0.15, 2.74, 0.15)，`ProbesAndSky` 补天空，绿→品红 delta 6.15 |
| `FrameDebugger/FrameDebuggerReport.json` + `FrameDebuggerError.txt` | 环境受限：batch `locallySupported=False`；GUI play mode 在本非交互会话不推进，未采集到事件（需交互式编辑器重跑） |

**实现落点**：
- T1：`ScreenSpaceReflections.compute` pragma；`SsrPass.BindSsrBlueNoise`；`ScreenSpaceLightingResources.ssrScramblingTile/ssrRankingTile/hasSsrBlueNoise`；`Resources/SsgiBlueNoise/{Scrambling,Ranking}Tile1SPP.png`。
- T2：`ScreenSpaceLightingCommon.hlsl:SslComputeSourceCoordinates`；`SsrGetFullResCoord` 与 `ReflectionGetGeometryCoord` 调用它。
- T3：`Shaders/Utils/ScreenSpaceLighting/ReflectionProbeFallback.hlsl`；`SSL_PROBE_ATLAS` 关键字（`SsgiPass` 仅在 Forward+ 启用）；SSGI reproject 回退分支；`_SsgiProbeAtlasParams`（透视标志）。
- T4：`Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingSsgiFallbackCheck.cs`（临时场景副本序列化探针，保证进入 Unity 的探针 culling）。

---

## 4. T1：SSR blue noise（已实现，0.5 天）

### 4.1 现状证据
- URP SSR trace 已经调用 `SsgiGetSequenceSample`（`URP/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute:250-258`），但该 compute 没有 `SSGI_BLUE_NOISE` 编译宏，`SsrPass` 也不绑定 LUT、不设置关键字（`URP/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs:208-213` 只有 XR/NormalsOct/Approx/HalfRes），所以始终走 `Sampling.hlsl:52` 的 IGN + R2 分支。
- SSGI 侧已有完整接线可复用：`SsgiPass.cs:226-228`（关键字）+ `:591-610`（三张 LUT 绑定）。
- HDRP SSR 用 `GetBNDSequenceSample(positionSS, _FrameCount, 0/1)`（`HDRP/Runtime/Lighting/ScreenSpaceLighting/ScreenSpaceReflections.compute:180-181`）。

### 4.2 实现
1. `ScreenSpaceReflections.compute`：在 `#pragma multi_compile` 区域加 `SSGI_BLUE_NOISE`。
2. 资源：从 HDRP 包复制 `ScramblingTile1SPP.png` / `RankingTile1SPP.png` 到 `Resources/SsgiBlueNoise/`（导入设置与 8SPP 一致：sRGB off、point、clamp、无 mip）。`ScreenSpaceLightingResources` 增加 `ssrScramblingTile` / `ssrRankingTile` 与 `hasSsrBlueNoise`，Owen 复用 `ssgiOwenScrambledTexture`。
3. `SsrPass`：
   - trace 的 `SetKeywords` 处加 `SSGI_BLUE_NOISE = m_Resources.hasSsrBlueNoise`；reproject/denoiser 不用序列，无需设置。
   - 新增 `BindSsrBlueNoise(cmd, ssrCompute, traceKernel)`，绑定 Owen + 1SPP tile 到 trace kernel；只在 `hasSsrBlueNoise` 时绑定。
   - `CreateShaderVariables` 的 `_SsrPbrFlags.x` 从 `Time.frameCount % 64` 改为直传 `Time.frameCount`。
4. 仅更新 `Sampling.hlsl` 注释（说明 SSR 绑 1SPP），不改采样函数，缺失资源自动回退。

### 4.3 验收
- 强制重导入 `ScreenSpaceReflections.compute` 消息 0；`SSGI_BLUE_NOISE` 变体成功编译。
- 有 LUT 时：SSR trace 的 `_SsrPbrFlags.x` 与序列实际使用 blue noise（可在 Frame Debugger 的 keyword 列表确认 `SSGI_BLUE_NOISE`；或在 shader 检查脚本中确认 pragma）。
- A/B 截图：同一场景、同帧数（32 帧）下 noise 图案变化，但稳定帧均值/权重统计与改前接近（记录差异，不做逐像素相等断言）。
- 缺失 LUT（临时移走 Resources 或禁用关键字）：回退 IGN + R2，无编译/运行错误。
- T5 三渲染器回归通过；EditMode 基线 418 passed 不下降。

### 4.4 风险
- SSR compute 变体 +1（× XR/Oct/HalfRes 组合），构建时间略增。
- 帧索引从 `%64` 改为直传 `Time.frameCount` 会改变 T5 的噪声/收敛截图，需重录基线并在报告中说明“预期变化”。
- 新增 1SPP tile 资产（两张小 PNG），包体影响可忽略，但需确认 Resources 打包检查仍通过。

---

## 5. T2 与 T3

### 5.1 T2：半分辨率几何坐标（已实现，0.5 天）

**现状**
- `SsrGetFullResCoord`：`floor((c+0.5) * ratio)`，2× 得到 `2c+1`（`URP/.../ScreenSpaceReflections.compute:94-102`）。
- `ReflectionDenoiser` 的 `ReflectionGetGeometryCoord` 同样 `(c+0.5)*ratio`（`URP/Shaders/Utils/Denoising/ReflectionDenoiser.compute:79-87`）。
- HDRP 屏幕空间 SSR 是全分辨率，没有半分辨率路径；`2c` 只出现在 RT 反射链：`ComputeSourceCoordinates(halfResCoord) = halfResCoord * 2`（`HDRP/Runtime/RenderPipeline/Raytracing/Shaders/RayTracingCommon.hlsl:63-67`），被 `RaytracingReflections` / `ReflectionDenoiser` / `RaytracingReflectionFilter` 使用。
- 对照：HDRP SSGI `GetLowResCoord` 是 `round((c+0.5)*2) = 2c+1`；HDRP BilateralUpsample 用 `floor(c/2)` 与 `2c`。

**实现**
- 在 `ScreenSpaceLightingCommon.hlsl` 增加共享 helper（建议名 `SslComputeSourceCoordinates`）：
  `return min((uint2)floor((float2)traceCoord * (screenSize.xy / traceSize.xy)), screenSize.xy - 1u);`
- `SsrGetFullResCoord` 与 `ReflectionGetGeometryCoord` 都改为调用它；两处必须同源，避免 trace 与 denoiser 取到不同 2×2 子像素。
- SSGI 的 `SsgiGetFullResCoord` 保持不动（HDRP SSGI 是 `2c+1`）。

**验收**
- 半分辨率 T5/FD 截图与改前对比：差异集中在 1 像素采样偏移，无新历史/拖影伪影；`fullVsHalfToneMappedPixelFraction` 不劣于改前（当前 0.034%）。
- history 尺寸仍 640×360、generation 递增、reset 计数正常。
- 全分辨率路径逐字节不变（ratio=1 时公式退化为 `c`）。
- EditMode 418 passed。

### 5.2 T3：SSGI 探针回退（方案 A 已实现，1.5–2.5 天）

#### 5.2.1 HDRP 目标
- 复投影未命中时按 `rayMiss` 层级回退：`TraceReflectionProbes` 用逐像素 env-light list（`GetCountAndStart` / `FetchEnvLight`），每个 cubemap probe 做 influence shape 权重、atlas 采样、`UpdateLightingHierarchyWeights` 累积（`HDRP/.../ScreenSpaceGlobalIllumination.compute:141-201`）；剩余权重给 APV 或 ambient probe（`:286-313`）。
- 采样点：`sampleDir`（与 trace 同一余弦样本），atlas LOD 0；最后统一曝光 + HSV clamp（`:284, 320-324`）。

#### 5.2.2 `SSGIURP/Shaders/SSGIFallback.hlsl` 的可借鉴点
- 两条路径：
  - `_FP_REFL_PROBE_ATLAS`（Forward+）：`ClusterInit/ClusterNext` 遍历逐像素探针，`CalculateProbeWeight` 权重、box projection、atlas `MipScaleOffset` 双 mip 混合，`totalWeight` 饱和后余量给天空/APV（`:206-289`）。
  - 非 Forward+：单个 `_SpecCube0`（box projection）+ 天空/APV，probe blending 明确标注为未实现（`:295-357`）。
- 注意：本方案只做 Forward+ 分支；非 FP 保持现有默认 cubemap，不引入 `_SpecCube0` box projection。
- 关键字 `_RAYMARCHING_FALLBACK_REFLECTION_PROBES` / `_RAYMARCHING_FALLBACK_SKY`；probe 相机结果乘 0.3（`:360-372`）——本项目不需要该系数，`ScreenSpaceCapability` 已跳过反射相机。
- 调用点用 mip 1.0（`SSGIURP/Shaders/ScreenSpaceGlobalIllumination.shader:243`），HDRP 用 LOD 0；本项目贴 HDRP 用 LOD 0。
- APV 分支依赖 `PROBE_VOLUMES_L1/L2` 与新 URP，本项目不可用；HDRP 中 APV 是独立 `else if` 分支，不与探针权重混合。

#### 5.2.3 URP 14.0.12 能力（已核实）
| 事实 | 证据 |
|---|---|
| Forward+ atlas/数组/Count 用 `CommandBuffer.SetGlobal*`，compute 声明同名即可读 | `URP/Runtime/ReflectionProbeManager.cs:297-302` |
| `_FPParams0/1/2` 与 `URP_ZBinBuffer`/`urp_TileBuffer` 也是全局，但**只在 Forward+** 设置 | `URP/Runtime/ForwardLights.cs:373-392` |
| atlas 是 octahedral Tex2D，格式 `B10G11R11` 或 `RGBA16F`（线性 HDR，blit 时已解码） | `ReflectionProbeManager.cs:62-83`；`ShaderLibrary/GlobalIllumination.hlsl:206-207` 不再 decode |
| 探针数量上限 `min(maxVisibleAdditionalLights, 64)` | `URP/Runtime/UniversalRenderPipeline.cs:154` |
| 非 Forward+ 的 `unity_SpecCube*` 是引擎 per-draw 状态，compute 不可依赖 | `ShaderLibrary/UnityInput.hlsl:124-129,228-231`；本仓库 SSGI 对 `unity_SH*` 的同类注释 `ScreenSpaceGlobalIllumination.compute:50-52` |
| `CalculateProbeWeight` / box projection 是纯函数，可直接抄；`ClusterInit/Next` 依赖 fragment 宏，需移植约 60–80 行 | `GlobalIllumination.hlsl:142-168`；`Clustering.hlsl:25,95`；`Input.hlsl:121-141` |
| `ReflectionProbeManager`/`atlasRT` 为 internal，无公开访问器，全局是唯一实用入口 | `ReflectionProbeManager.cs:8,53`；`ForwardLights.cs:153` |
| URP 14 无 APV | 包内只有失效的 `ProbeVolumeVariants.hlsl` 引用；core 的 `ProbeReferenceVolume` 无 URP shader 集成 |

#### 5.2.4 建议实现（方案 A，推荐）
1. 新增 `URP/Shaders/Utils/ScreenSpaceLighting/ReflectionProbeFallback.hlsl`：
   - 声明 `urp_ReflProbes_Atlas`、`urp_ReflProbes_BoxMin/BoxMax/ProbePosition/MipScaleOffset`、`urp_ReflProbes_Count`、`_FPParams0/1/2`，按 `MAX_REFLECTION_PROBES`（16/32/64）与 GLES3 cbuffer guard 复制 URP 布局。
   - 移植 `ClusterInit/ClusterNext`，显式传入 camera position / view-forward / perspective flag，避免 include fragment 的 `Input.hlsl`。
   - 实现 `SslSampleReflectionProbesAtlas(dir, posWS, mip, screenUV, out weight)`：cluster 循环 + `CalculateProbeWeight` + box projection + octahedral atlas UV + mip scale/offset 混合。
2. 关键字：新增 compute 关键字 `SSL_PROBE_ATLAS`，由 `SsgiPass` 根据 `UniversalRenderer.renderingModeActual == RenderingMode.ForwardPlus` 设置（该属性已在 `SsgiPass.cs:752` 使用）。
3. `ScreenSpaceGlobalIllumination.compute` 的 fallback 分支：
   - 若 `SSL_PROBE_ATLAS`：atlas 采样结果 + `totalWeight`；`weight < 1` 且 `rayMiss` 含 Sky 时，余量乘 ambient SH。
   - 否则：现有 `_SsgiReflectionProbeCubemap`（默认 cubemap）+ SH，保持非 FP 行为。
   - `rayMiss` 的 None / Sky / Probes / ProbesAndSky 语义不变。
4. 绑定：Forward+ 下无需 CPU 绑定 atlas（全局）；非 FP 继续绑定默认 cubemap。可选：给 `_SsgiFallbackParams.y` 记录 atlas 是否可用，便于调试。

**方案 B（可选，非 FP 多探针）**：C# 从 `cullResults.visibleReflectionProbes` 取前 N 个，上传 box/position/blend 与各自的 cubemap（需要 `TextureCubeArray` 或逐探针绑定 + 循环），收益有限、成本高；建议 T3 先不做，除非验收要求 Forward/Deferred 也贴 HDRP。

#### 5.2.5 验收
- 新增验证脚本（仿 `ScreenSpaceLightingSsgiTemporalCheck`）：场景放 2 个不同颜色的 box-projected probe，分别在 Forward+/Forward/Deferred 渲染：
  - Forward+：SSGI 在 `rayMiss=ReflectionProbes` 下的回退颜色随探针位置/颜色变化；与手动采样探针（或材质 probe 反射）在容差内一致；`ProbesAndSky` 余量补天空；`Sky` 只用 SH；`None` 保持 baked GI。
  - 非 Forward+：回退仍为默认 cubemap（记录为已知差异），无 NaN/Inf，权重/基线契约不变。
- `rayMiss` 四种模式的输出差异可测（非零像素 + 颜色方向正确）。
- 反射相机（如启用 dim/禁用）验证无递归/爆亮。
- T5 三渲染器回归 + SSGI temporal 收敛检查通过；Frame Debugger 顺序不变。
- EditMode 418 passed；无新增公开 Volume 参数。

#### 5.2.6 验证实现说明（本轮实测）

- 运行时 `AddComponent<ReflectionProbe>()` 在编辑器下**不会进入** `CullingResults.visibleReflectionProbes`（实测 `urp_ReflProbes_Count = 0`），因此 `SsgiFallbackCheck` 先创建探针、保存临时场景副本并重新打开；探针成为场景对象后 atlas 才可见（`visible=1`）。
- Forward+ 实测：探针盒覆盖约 80% 的 miss 像素（`ReflectionProbes` coverage 0.306 vs `Sky` 0.383），其余像素在 `ProbesAndSky` 中由 ambient SH 补齐。
- 探针颜色验证：绿探针下 SSGI 均值为 (0.15, 2.74, 0.15)；切换品红后 `greenVsMagentaDelta = 6.15`，证明 atlas 跟随探针内容而非常量。
- Frame Debugger：batchmode 报告 `locallySupported=False`；GUI 在本非交互会话中 play mode 不推进，需在交互式编辑器重跑同一 `ScreenSpaceLightingFrameDebuggerCapture.Capture` 命令。

#### 5.2.7 风险
- 变体：`SSL_PROBE_ATLAS` × XR × NormalsOct × HalfRes 组合增加，需统计构建时间/包体。
- `MAX_REFLECTION_PROBES` 与 GLES3 布局必须与 URP 完全一致，否则 cbuffer 错位；建议只支持桌面（D3D11/Vulkan/Metal）并在移动端保留默认 cubemap。
- atlas 满时探针会被丢弃（URP 自身行为），回退颜色可能突变；需在文档标注。
- XR single-pass 的 cluster 数据含 eye offset（URP 代码已处理），compute 移植时要用 per-eye tile offset。
- 性能：逐像素 cluster 循环在 SSGI 全分辨率上半分辨率 trace 下可接受（默认可见探针 ≤4），但需记录 GPU 时间。
- 行为变化（Forward+ 限定）：若场景没有可见探针，`rayMiss=ReflectionProbes` 的 miss 权重为 0（保留 baked GI），不再像旧代码那样给默认 cubemap 权重 1；`ProbesAndSky` 仍由 ambient SH 补齐。需在 T4 专项验证中确认这是期望语义。

---

## 6. T4：验证与证据（1 天）

### 6.1 静态
- `ScreenSpaceReflections.compute`、`ReflectionDenoiser.compute`、`ScreenSpaceGlobalIllumination.compute`、`BilateralUpsample.compute` 强制重导入消息 0。
- 新增 `SSL_PROBE_ATLAS` / `SSGI_BLUE_NOISE` 变体全部编译；kernel 清单不变（SSR trace/reproject/DebugSsrParams + denoiser 7 + SSGI 2）。
- EditMode 全量（基线 418）不下降；新增单测（如资源/关键字配置）按需添加。
- **证据缺口（2026-11-17 验收）**：本轮未单独产出 03 的 `SslShaderCheck.txt`；T1 的 blue-noise A/B 与 missing-LUT fallback 未单独留图，仅由 T5 回归间接覆盖；Frame Debugger 仍未采集（§3.1 `completed=false`）。以上按“未完成”理解，不按通过计。

### 6.2 功能/视觉
- T1：blue noise 开/关 A/B（32 帧）截图 + 稳定帧权重统计；缺失 LUT 回退。
- T2：半分辨率改前/改后截图 + `fullVsHalfToneMappedPixelFraction`、历史尺寸、reset 计数。
- T3：`SsgiFallbackCheck` 三渲染器 × 四种 `rayMiss`；Forward+ atlas 颜色随探针变化；非 FP 默认 cubemap；反射相机检查。
- 回归：T5 三渲染器全部 pass；SSGI temporal check pass；Frame Debugger 事件顺序与 SSR dispatch 数（PBR 6 / Approx 2）不变。

### 6.3 证据目录
```
PLAN/Done/03-SSRImprove2/validation/
└─ T4/EditModeTests.xml、EditMode.log、UnityImport.log
   T4/T5/T5Verification.json 与 24 张 PNG
   T4/SsgiTemporal/SsgiTemporalCheck.json 与三渲染器 On/Off PNG
   T4/SsgiFallback/SsgiFallbackCheck.json 与三渲染器 PNG
   T4/FrameDebugger/FrameDebuggerReport.json、FrameDebuggerError.txt
   T4/UnitySsrT5Verification.log、UnitySsgiTemporalCheck.log、UnitySsgiFallbackCheck.log
```

---

## 7. T5：收尾（0.5 天）

- 更新 `PLAN/Done/01-ScreenSpaceLighting/validation/T14/T14KnownDifferences.md`：
  - SSR 已接 blue noise（与 HDRP 一致；缺失 LUT 回退 IGN+R2）。
  - SSR 半分辨率几何坐标已对齐 HDRP RT denoiser（2c）。
  - SSGI 回退：Forward+ 使用 URP atlas + cluster（与 HDRP env-light list 近似）；非 Forward+ 仍为默认 cubemap；APV 不支持。
  - 反射相机策略（若实现）。
- 在 `PLAN/Done/02-SSRImprove/HANDOFF.md` 顶部加一句“03 已接手三项改进，见 03 交接”。
- 清理临时脚本/截图；确认 `Resources` 无新增重复资产。
- 目录已归档到 `PLAN/Done/03-SSRImprove2/`（本文与证据链接路径已随之更新）。

---

## 8. 任务表

| 任务 | 内容 | 预估 | 依赖 |
|---|---|---|---|
| T0 | 冻结 §3 契约，确认 4 个待定问题 | 0.5h | — |
| T1 | SSR blue noise 接线 + 帧索引 + A/B | 0.5d | T0 |
| T2 | 共享 `SslComputeSourceCoordinates`，SSR trace/denoiser 对齐 2c | 0.5d | T0 |
| T3a | Forward+ atlas 回退 + 默认 cubemap 保留 | 1.5–2d | T1/T2（可并行） |
| T3b | 非 Forward+ 多探针（可选） | +1.5d | T3a |
| T4 | 三渲染器 / Frame Debugger / fallback 验证 | 1d | T1–T3 |
| T5 | T14 + 02 交叉引用 + 归档 | 0.5d | T4 |

---

## 9. 决策记录（原待确认问题，已关闭）

1. **T3 范围**：仅 Forward+ atlas；非 FP 默认 cubemap（T3b 不做）。
2. **SSR 帧索引**：直传 `Time.frameCount`（shader `&255`），T5 基线需重录。
3. **反射相机**：无需改动；`ScreenSpaceCapability` 已跳过 `Reflection`/`Preview`。
4. **坐标对齐范围**：只改 SSR 为 2c；SSGI 保持 2c+1。
5. **SSR LUT**：新增 1SPP tile。

---

## 10. 复现命令

```bash
# EditMode
"D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" -runTests -testPlatform EditMode \
  -testResults "<abs>/PLAN/Done/03-SSRImprove2/validation/T4/EditModeTests.xml" \
  -logFile "<abs>/PLAN/Done/03-SSRImprove2/validation/T4/EditMode.log"

# 现有 SSR T5 回归（三渲染器）
"D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingSsrT5Verification.RunSsrT5Verification -quit \
  -logFile "<abs>/PLAN/Done/03-SSRImprove2/validation/T4/UnitySsrT5Verification.log"

# 现有 SSGI temporal 检查
"D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingSsgiTemporalCheck.Run -quit \
  -logFile "<abs>/PLAN/Done/03-SSRImprove2/validation/T4/UnitySsgiTemporalCheck.log"
```

> Frame Debugger 需 GUI 编辑器并进入 Play Mode；采集前确认无残留 Unity 进程（否则占项目锁，新实例 exit 0 且无产物）。Unity 批处理会重写 `ProjectSettings/*.asset`，跑完 `git checkout --` 还原。

---

## 11. 关键文件索引

- SSR trace/reproject：`URP/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute`
- SSR 调度：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsrPass.cs`
- SSR denoiser：`URP/Shaders/Utils/Denoising/ReflectionDenoiser.compute`
- SSGI trace/reproject：`URP/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute`
- SSGI 采样：`URP/Shaders/Utils/ScreenSpaceLighting/Sampling.hlsl`
- SSGI 调度/回退绑定：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsgiPass.cs`
- Forward+ 探针回退头：`URP/Shaders/Utils/ScreenSpaceLighting/ReflectionProbeFallback.hlsl`
- SSR/SSGI blue noise 资产：`URP/Runtime/RendererFeatures/ScreenSpaceLighting/Resources/SsgiBlueNoise/`
- 共享常量：`URP/Shaders/Utils/ScreenSpaceLighting/ScreenSpaceLightingCommon.hlsl`
- URP 探针 atlas 全局：`URP/Runtime/ReflectionProbeManager.cs`、`URP/Runtime/ForwardLights.cs`、`URP/ShaderLibrary/Input.hlsl`
- HDRP SSGI 回退：`HDRP/Runtime/Lighting/ScreenSpaceLighting/ScreenSpaceGlobalIllumination.compute`
- HDRP SSR 采样/坐标：`HDRP/Runtime/Lighting/ScreenSpaceLighting/ScreenSpaceReflections.compute`、`HDRP/Runtime/RenderPipeline/Raytracing/Shaders/RayTracingCommon.hlsl`
- 外部参照：`SSGIURP/Shaders/SSGIFallback.hlsl`