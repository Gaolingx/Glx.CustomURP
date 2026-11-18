# 08-AccurateNormalsFixed 交接：Deferred + Accurate G-buffer normals 噪点与闪烁

> 状态：**计划阶段**（问题描述、落地方案、验收标准已定稿，T0 归因待执行）
> 建立：2026-11-19
> 基线：`a8e5a7e`（Share the editor camera motion pass with screen space lighting）
> 结果入口（待产出）：`validation/`、`Report.md`、`KnownDifferences.md`
> 关联：`PLAN/TODO/07-EditorMVPass`（同一批 GTAO/SSL 时域路径）、`PLAN/Done/01-ScreenSpaceLighting`（GTAO / SSL 实现）、`PLAN/Done/06-ValidationAllRDC`（差异与证据规范）
> 本文中的代码行号基于 `a8e5a7e`，改动后以符号名为准。

---

## 0. 一页结论

用户报告：**Deferred 渲染路径下打开 `Accurate G-buffer normals` 后，地面出现细密噪点并闪烁；关闭后完全干净**（截图见 §1.1）。

结论：**不是这个开关的预期行为，是 octahedron 法线路径在屏幕空间光照/GTAO 上的缺陷。**

- oct 编码相对 8bit signed 只提高精度；场景地面是无贴图、无 normal map、`smoothness = 0.25` 的平面（`Assets/Tests/ScreenSpaceLighting/Materials/Ground.mat`），oct 解码出的法线是常量，**不可能**由法线精度本身产生逐像素噪点。
- 两帧截图除屏幕空间光照覆盖的表面外**逐像素一致**（天空、几何体高光 diff = 0），说明噪点由 SSL/GTAO 注入，而不是 deferred lighting 或法线编码本身。
- 噪点幅度约 3–4% 亮度，是典型的 1 SPP 随机采样未被时域累积/空间降噪收敛后的残留；静止相机下应收敛，因此必然存在“历史校验/降噪不生效”或“法线被错误解码”的路径级问题。
- 该组合**没有任何验证覆盖**：仓库所有 renderer asset 的 `m_AccurateGbufferNormals` 均为 0（含全部 `SslT*DeferredRenderer`），P3-SSR 报告中 `normalsOctKeyword` 在全部用例为 false。`GTAO_NORMALS_OCT` / `SSGI_NORMALS_OCT` / `SSR_NORMALS_OCT` 三个变体从未被端到端跑过。

本轮的交付目标：**先归因到具体 pass，再修复 oct 路径，并把 oct 变体纳入自动化矩阵**（§3–§5）。

---

## 1. 现象、复现与证据

### 1.1 用户侧证据（2026-11-19）

| 项 | 值 |
|---|---|
| 场景 | `Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingBaseline.unity` |
| Renderer | 截图 Inspector 显示 `URP-High-Fidelity-Renderer` 上启用 Screen Space Lighting feature；`Rendering Path = Deferred` |
| 操作 | 勾选/取消 `Accurate G-buffer normals`，其余不变 |
| 结果 | 勾选：地面密集噪点 + 连续帧闪烁；取消：画面完全干净 |

**仓库状态注意**：截至 `a8e5a7e`，`Assets/Settings/URP-HighFidelity-Renderer.asset` 的 `m_RendererFeatures` 为空、`m_AccurateGbufferNormals = 0`。截图中的启用状态不在提交里（与 `07-EditorMVPass` §1 记录的情况相同）。复现必须记录实际使用的 renderer 资产与序列化字段，或改用本轮新增的仓库内 oct renderer asset（§3.1）。

### 1.2 图像量化（对用户两张截图做离线测量）

测量方法：PIL 读图，取 Game View 内容区；`highfreqAbs` = 与原图 3×3/2px BoxBlur 之差的平均绝对值。

| 区域 | Accurate ON 均值 | Accurate OFF 均值 | ON 高频能量 | OFF 高频能量 |
|---|---|---|---|---|
| 地面左（远离遮挡） | (109.5, 112.7, 120.8) | (118.1, 123.6, 136.7) | (6.4, 7.9, 10.4) | (0.00, 0.05, 0.03) |
| 地面右 | (109.4, 112.8, 122.6) | (116.3, 121.4, 140.4) | (7.0, 8.5, 10.9) | (0.13, 0.08, 0.28) |
| 天空/背景 | (105.7, 99.5, 96.1) | (105.7, 99.5, 96.1) | (0, 0, 0) | (0, 0, 0) |

可复述的结论：

1. **背景逐像素相同** → 两帧是同一相机、同一光照；差异只在屏幕空间光照作用的表面。
2. **地面亮度下降 9–16/255，蓝通道降幅最大** → 被改变的是环境/间接项（AO 乘环境光、SSGI 间接项），不是直射光。
3. **OFF 高频能量 ≈ 0，ON 为 6–11/255** → OFF 路径的时域累积/降噪完全收敛；ON 路径没有。
4. 噪点在 Game View 2× 显示缩放下呈 2×2 重复，换算到渲染分辨率即 1 像素级噪声，与 half-res 效果无关（2×2 由 Game View 缩放造成）。

### 1.3 相关代码事实（HEAD）

| 事实 | 位置 |
|---|---|
| oct 编码写入 depth-normals prepass | `Shaders/LitDepthNormalsPass.hlsl`（`_GBUFFER_NORMALS_OCT` 分支）、`Shaders/DepthNormalsPass.hlsl` |
| deferred GBuffer2 在 oct 下为 `R8G8B8A8_UNorm`，否则为 signed 格式 | `Runtime/DeferredLights.cs`（`GetGBufferFormat`，NormalSmoothnessIndex 分支） |
| 三个消费端的 oct 解码 | `Shaders/Utils/GroundTruthAmbientOcclusion/GTAOCommon.hlsl`（`GTAOGetNormalVS`）、`Shaders/Utils/ScreenSpaceLighting/ScreenSpaceLightingCommon.hlsl`（`SsgiDecodeNormalSS`）、`Shaders/Utils/ScreenSpaceLighting/ScreenSpaceReflections.compute`（`SsrDecodeNormalSS`） |
| 解码实现与 URP 参考 `UnpackNormal` 一致（`PackFloat2To888` ↔ `Unpack888ToFloat2` + `UnpackNormalOctQuadEncode`） | `ShaderLibrary/UnityGBuffer.hlsl`、`ShaderLibrary/DeclareNormalsTexture.hlsl` |
| oct 关键字设置点 | `GroundTruthAmbientOcclusionPass.cs`（`NormalsOctKeyword`）、`SsgiPass.cs`（ssgi/temporal/denoiser 三个 compute）、`SsrPass.cs`（ssr compute + reflection denoiser） |
| 法线来源（deferred）| `UniversalRenderer.cameraNormalsTextureHandle` → GBuffer2；effects 在 `AfterRenderingPrePasses+4/+5/+6` 读到的是 **depth-normals prepass** 内容 |
| 历史法线 | `ColorPyramidPass` 在 `AfterRenderingTransparents+1` 把 GBuffer2 原样拷贝到 `SslPreviousNormal`（`ScreenSpaceLightingRenderUtils.CopyNormalsToPrevious`） |
| 时域校验 | `Shaders/Utils/Denoising/TemporalFilter.compute`（`ValidateHistory`：depth/normal/position/motion），`ReflectionDenoiser.compute` 同构 |

### 1.4 覆盖缺口

| 证据 | 内容 |
|---|---|
| 全部 renderer asset | `m_AccurateGbufferNormals: 0`（`Assets/Settings/*`、`Assets/Tests/ScreenSpaceLighting/Renderers/SslT*DeferredRenderer.asset`） |
| `PLAN/Done/06-ValidationAllRDC/validation/P3-SSR/Report.md:38` | `normalsOctKeyword` 在所有 SSR 用例均为 false |
| `PLAN/TODO/08-...`（本轮） | oct 变体首次被真实运行 |

---

## 2. 根因候选与判定方法（T0 要排除的假设）

按“与现象吻合度 × 代码可疑度”排序。T0 的归因实验必须逐条给出证据，不能以推测关闭。

| ID | 假设 | 与现象吻合点 | 判定方法 |
|---|---|---|---|
| H1 | 某个 pass 的 `*_NORMALS_OCT` 本地关键字实际未生效（变体丢失 / 设在了未被 dispatch 的 compute / 关键字被别的 pass 覆盖） | 平面法线被当成 signed 数据归一化 → 倾斜 ~27° 的假法线 → horizon search 过遮挡（更暗）+ 逐采样旋转敏感（噪）+ 每帧旋转不同（闪） | 在 oct 分支强制输出固定色调试法线；或读取 `ComputeShader.IsKeywordEnabled` + dispatch 前后 dump；GTAO/SSGI/SSR 各自的 Debug View |
| H2 | 时域历史法线与当前法线编码/内容不一致：历史在 GBuffer pass 之后从 GBuffer2 拷贝，当前在 prepass 阶段读取 | normal 校验每帧失败 → 只保留当前帧 1 SPP → 不收敛 | `ValidateHistory` 的 validation buffer（normal 位）；或临时把 normal 拒绝关掉看是否收敛；对比 prepass 与 GBuffer 两处 GBuffer2 内容 |
| H3 | half-res 路径（SSGI/SSR PBR）里 oct 法线的读取坐标/插值与全分辨率不一致（`SsgiGetFullResCoord` / `SslComputeSourceCoordinates`） | 半分辨率 trace 得错法线 → 半分辨率噪声经 upsample 到全屏 | 把 SSGI/SSR 切到 full resolution（volume `fullResolution`）看噪点是否消失/改变 |
| H4 | denoiser（DiffuseDenoiser / ReflectionDenoiser）的 oct 解码关键字与 trace 不一致（关键字只在 trace 的 compute 上生效） | 双边权重失效/错误 → 空间降噪无效 → 残留 1 SPP 噪 | 在 denoiser 的 oct 分支输出权重/法线调试；核对 `SsgiPass`/`SsrPass` 的 `SetKeyword` 目标 |

排除项（已验证、不作为本轮结论）：控制台 `CommandBuffer: temporary render texture not found (SetComputeTextureParam)` 是已知首帧无害 warning（`PLAN/Done/01-ScreenSpaceLighting/validation/T14/T14KnownDifferences.md:45`）。

---

## 3. 落地方案

### T0 复现资产与归因实验（必须先做）

1. **T0.1 oct 复现资产**：以 `Assets/Tests/ScreenSpaceLighting/Renderers/SslT12DeferredRenderer.asset`（GTAO + ScreenSpaceLighting + probe + decal）为母本，生成 `SslOctDeferredRenderer.asset`（`m_AccurateGbufferNormals: 1`）与对应 pipeline asset、`.meta`；保留原 signed asset 作为 A/B 对照。禁止直接改 `URP-HighFidelity-Renderer.asset`（它是其他验证的母本）。
2. **T0.2 归因捕获**：在 `ScreenSpaceLightingBaseline`（或 T12 场景）上，用编辑器/PlayMode 脚本对 signed / oct 各跑固定帧数并保存：
   - 最终画面（`frame0..frameN`，N ≥ 8，静止相机）；
   - GTAO/SSGI/SSR 各自的 Debug View（`ScreenSpaceLightingDebugMode` / GTAO debug）；
   - 需要时用 RenderDoc 抓一帧，导出 `_CameraNormalsTexture`、validation buffer、AO/SSGI intermediate。
3. **T0.3 归因表**：写 `validation/Attribution.md`，逐项回答：
   - 噪点归属哪个效果（GTAO / SSGI / SSR / 组合）；
   - 噪点出现在 trace 前还是 denoise 后；
   - H1–H4 中哪条被证据确认，哪条被排除。

**T0 完成判据**：能用一张表把现象映射到“某个 pass 的某个输入/输出”，并给出可复现的 A/B 图。

### T1 修复（按 T0 归因选择，预期是 H1 或 H2）

修复原则：

- 修 **oct 路径本身**，不允许用“关闭 temporal / 降噪”“强制回退 signed”“禁用该效果”来规避；也不允许给 signed 路径加兼容 shim。
- 如果归因是“法线内容/时机不一致”（H2），统一法线的**唯一来源**：让历史拷贝与效果读取使用同一份 prepass 结果（例如把历史法线拷贝移到 GBuffer 之前，或在 effects 阶段缓存一份法线快照供历史使用），而不是依赖 GBuffer2 在校验时刻的内容。
- 如果归因是“关键字/变体未生效”（H1），修正关键字设置目标并补 `IsKeywordEnabled` 断言；若变体确实未被编译，检查 compute 的 `#pragma multi_compile` 与资源导入。
- 如果归因是 half-res（H3）或 denoiser 不一致（H4），按同样口径修正并保证 full/half 两条路径一致。

产出：代码改动 + `validation/Attribution.md` 的“修复前后”对照。

### T2 回归覆盖（与 T1 同批提交）

1. `SslOctDeferredRenderer` 加入自动化矩阵（至少 EditMode 资产/关键字断言 + 一次 PlayMode 图像捕获）。
2. 新增图像指标断言（数值口径见 §4）：
   - 平坦无遮挡地面区域：oct 与 signed 的 `highfreqAbs` 差、亮度均值差；
   - 静止相机连续帧：帧间平均绝对差（闪烁）。
3. 保留 signed 路径不变性：现有 T12 图像哈希与基准一致（差异逐张解释）。

### T3 证据与收尾

- `validation/Report.md`：声称 → 改动 → 证据 → HEAD 的对照表。
- `KnownDifferences.md`：若有无法在本轮消除的差异（例如 oct 量化带来的微小正常差异），如实记录。
- 更新 `PLAN/ACCEPTANCE.md` 汇总（若本轮验收完成）。

**不做的事**：不重构 SSL/GTAO 的整体架构；不调整现有 signed 路径的算法；不引入新的第三方依赖。

---

## 4. 验收标准

| ID | 标准 | 判定方法 |
|---|---|---|
| AC-1 | 静止相机连续 8 帧，平坦无遮挡地面（远离任何物体投影）的 `highfreqAbs` 在 oct 与 signed 之间的通道均值差 ≤ 0.5/255 | 捕获脚本输出 `Metrics.json` |
| AC-2 | 同一区域 oct 与 signed 的平均亮度差 ≤ 1/255（当前为 9–16/255） | 同上 |
| AC-3 | 静止相机连续 8 帧的帧间平均绝对差 ≤ 0.5/255（无闪烁），oct 与 signed 均满足 | 同上 |
| AC-4 | `GTAO_NORMALS_OCT` / `SSGI_NORMALS_OCT` / `SSR_NORMALS_OCT` 在 Deferred + accurate 下实际启用，并有自动化断言 | EditMode 测试读取 `ComputeShader.IsKeywordEnabled` + 归因调试图 |
| AC-5 | signed 路径无回归：T12 图像哈希与 `a8e5a7e` 基准一致（差异需逐张解释），EditMode / PlayMode 全绿 | 现有验证脚本 |
| AC-6 | oct 路径的性能回归 < 5%（同场景同分辨率，GTAO/SSL 总 GPU 时间） | Frame Timing / 现有性能脚本 |

---

## 5. 风险与约束

| 风险 | 处理 |
|---|---|
| 截图状态未入库（renderer feature 未序列化） | T0 用仓库内复现资产；结论只引用提交内资产 |
| oct 变体首次运行，可能不止一个缺陷 | T0 归因表必须区分多个缺陷；T1 可拆成多个提交，但不得混入 signed 路径改动 |
| Game View 2× 缩放会污染图像指标 | 捕获统一走 `Camera.Render` 到固定 RT，不用 Game View 截图 |
| `.rdc` 预算 1 GiB | 本轮最多保留 1–2 个 `.rdc`，其余用导出的 PNG/JSON 证据 |

---

## 6. 实施记录（随进度更新）

| 日期 | 任务 | 结果 | 证据 |
|---|---|---|---|
| 2026-11-19 | 计划定稿 | 问题描述、方案、验收标准 | 本文件 |
| — | T0.1 | — | — |
| — | T0.2 / T0.3 | — | — |
| — | T1 | — | — |
| — | T2 / T3 | — | — |