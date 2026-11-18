# 08-AccurateNormalsFixed 交接：Deferred + Accurate G-buffer normals 噪点与闪烁

> 状态：**修复与自动验收完成**（T0 归因、T1 修复、T2 回归覆盖、EditMode 182/182、归因脚本 passed；T12 回放见 §6）
> 建立：2026-11-19
> 基线：`a8e5a7e`（Share the editor camera motion pass with screen space lighting）
> 结果入口：[Report.md](Report.md)、`validation/`
> 关联：`PLAN/TODO/07-EditorMVPass`（同一批 GTAO/SSL 时域路径）、`PLAN/Done/01-ScreenSpaceLighting`（GTAO / SSL 实现）、`PLAN/Done/06-ValidationAllRDC`（差异与证据规范）
> 本文中的代码行号基于 `a8e5a7e`，改动后以符号名为准。

---

## 0. 一页结论

用户报告：**Deferred 渲染路径下打开 `Accurate G-buffer normals` 后，地面出现细密噪点并闪烁；关闭后完全干净**（截图见 §1.1）。

结论：**不是这个开关的预期行为，是 octahedron 法线路径在屏幕空间光照/GTAO 上的缺陷。**

**根因（T0 已确认）**：代码仓库里材质的 `DepthNormals` pass **没有声明** `_GBUFFER_NORMALS_OCT` 关键字。Deferred + Accurate normals 时：

1. `DeferredLights.SetupLights` 打开全局 `_GBUFFER_NORMALS_OCT`，GBuffer2 变成 `R8G8B8A8_UNorm`（`Runtime/DeferredLights.cs` `GetGBufferFormat`），GBuffer pass（`Lit.shader` "GBuffer"）写入 octahedron 打包法线。
2. SSL / GTAO 在 `AfterRenderingPrePasses+4/+5/+6` 运行，早于 GBuffer，只能读到 **depth-normals prepass** 写入 GBuffer2 的内容；而该 pass 没有关键字 → **永远写 signed 法线**。
3. 消费端 `UseOctEncodedNormals` 在 deferred + accurate 时返回 true（`GroundTruthAmbientOcclusionPass.cs`、`SsgiPass.cs`、`SsrPass.cs`），于是把 signed 数据按 oct 解码。对 +Y 平地，signed 数据按 oct 解码得到约 `(0.998, 0, 0.066)` —— 一个几乎贴着地面的法线：GTAO 地平线搜索自我遮挡（变暗）、SSGI 半球采样跨过地面（光线一半自交、一半未命中）→ 逐像素噪点；时域累积无法收敛 → 闪烁。
4. 该组合**没有任何验证覆盖**：仓库所有 renderer asset 的 `m_AccurateGbufferNormals` 均为 0（含全部 `SslT*DeferredRenderer`），P3-SSR 报告中 `normalsOctKeyword` 在全部用例为 false。`GTAO_NORMALS_OCT` / `SSGI_NORMALS_OCT` / `SSR_NORMALS_OCT` 三个变体从未被端到端跑过。

**修复（T1 已完成）**：让不透明 depth-normals prepass 与 GBuffer2 使用同一编码 —— 给所有 `DepthNormals` / `DepthNormalsOnly` pass 声明 `#pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT`，并为原本没有 oct 分支的 Terrain / SpeedTree depth-normals 输出补上 oct 编码。修复后 oct 与 signed 的整帧亮度/地面高频能量差降到 1e-4 量级（见 §6）。

本轮交付：**根因、修复、归因证据、阈值化回归脚本与 EditMode 关键字不变量测试**（§3–§6）。

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

### 2.1 T0 归因结果（2026-11-19，`validation/Attribution/`）

复现场景 = T3 验证场景，renderer = `SslT5DeferredRenderer`，只切换 `accurateGbufferNormals`；`Attribution.before.json`：

| 用例 | 平均亮度 | 全图高频 | 地面高频 | 帧间差 |
|---|---|---|---|---|
| Gtao_Signed | 0.25762 | 0.00180 | 0.00233 | 2e-6 |
| Gtao_Oct | 0.25312 | 0.00179 | 0.00222 | 5e-6 |
| Ssl_Signed | 0.28306 | 0.00212 | 0.00252 | 0 |
| **Ssl_Oct** | **0.27099** | **0.01245** | **0.00319** | 0 |
| Both_Signed | 0.28128 | 0.00207 | 0.00248 | 8e-6 |
| Both_Oct | 0.25716 | 0.00487 | 0.00253 | 1.6e-5 |

判定：

| ID | 假设 | 结论 |
|---|---|---|
| H1 | 某个 pass 的 oct 关键字未生效 | **部分成立**：三个消费端关键字均启用（`Attribution.json` 的 `keywords`），但**写入端**的 prepass 变体没有关键字 |
| H2 | 历史法线编码/时机不一致 | **排除**：噪点在 `SsgiRaw`（denoise 之前）已存在，帧间差 ≈ 0 |
| H3 | half-res 坐标/插值 | **排除**：GTAO（half-res）在 oct 下同样拿到错误法线；SSGI/SSR 共享同一法线输入 |
| H4 | denoiser 关键字不一致 | **排除**：`DiffuseDenoiser` / `ReflectionDenoiser` 的 oct 关键字均启用 |
| **H5（新）** | **prepass 写入端始终是 signed，消费端按 oct 解码** | **确认**：调试图 `Attribution_Ssl_Oct_SsgiRaw.png` 显示光线逐像素随机命中；`Attribution_Gtao_Oct_GtaoAO.png` 显示整块地面被过遮挡；修复后两张图与 signed 版本一致 |

数值上，把 signed 数据按 oct 解码（模拟 `Unpack888ToFloat2` + `UnpackNormalOctQuadEncode`）对 +Y 法线得到 `(0.998, 0, 0.066)`，与“贴地法线 → 自我遮挡/光线跨界”的现象一致。

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

### T1 修复（已完成 2026-11-19）

原则不变：修 **oct 路径本身**，不关闭 temporal / 降噪、不回退 signed、不新增兼容 shim。

改动（不透明 prepass 与 GBuffer2 编码对齐）：

| 文件 | 改动 |
|---|---|
| `Shaders/Lit.shader` | DepthNormals pass 补 `#pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT` |
| `Shaders/SimpleLit.shader` | 同上 |
| `Shaders/Particles/ParticlesLit.shader`、`ParticlesSimpleLit.shader` | 同上 |
| `Shaders/Terrain/WavingGrassBillboard.shader` | 同上 |
| `Shaders/Terrain/WavingGrassDepthNormalsPass.hlsl` | oct 分支改用与 signed 分支相同的有限差分法线（原先用 `input.normal`，会改变 billboard 法线） |
| `Shaders/Terrain/TerrainLit.shader`、`TerrainLitBase.shader`、`TerrainDetailLit.shader` | DepthNormals pass 补关键字 |
| `Shaders/Terrain/TerrainLitDepthNormalsPass.hlsl` | 补 oct 编码输出（原先没有 oct 分支） |
| `Shaders/Nature/SpeedTree7.shader`、`SpeedTree8.shader` | DepthNormals pass 补关键字 |
| `Shaders/Nature/SpeedTree7CommonPasses.hlsl`、`SpeedTree8Passes.hlsl` | 补 oct 编码输出 |

保持 signed 的路径：`TransparentDepthNormals`（透明 prepass 供 transparent SSR，消费端固定 `octNormals=false`，`TransparentSsrPass.cs`），因此显式不加关键字。

回归保护：

- `Tests/Editor/.../AccurateGbufferNormalsTests.cs`：
  - `OpaqueDepthNormalsPassesDeclareTheOctahedronKeyword`（扫描全部 `.shader`，`DepthNormals`/`DepthNormalsOnly` 必须声明关键字）；
  - `ScreenSpaceEffectDecodersKeepTheOctahedronBranch`（三个消费端必须保留 `Unpack888ToFloat2` + `UnpackNormalOctQuadEncode` 分支）。
- `Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingAccurateNormalsAttribution.cs`：带 AC-1/2/3 阈值判定的可执行归因/验收脚本（`-executeMethod` 可用，退出码即 pass/fail）。

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

## 4. 验收标准与结果

| ID | 标准 | 修复后实测 | 结论 |
|---|---|---|---|
| AC-1 | 静止相机 8+ 帧，平坦无遮挡地面高频能量 oct vs signed 差 ≤ 0.5/255（0.00196） | GTAO 1.2e-4、SSL 1.2e-4、Both 1.2e-4 | ✅ |
| AC-2 | 同区域平均亮度差 ≤ 1/255（0.00392） | GTAO 1e-5、SSL 1e-5、Both 1e-5（修复前 4.5e-3 … 1.2e-2） | ✅ |
| AC-3 | 连续帧帧间平均绝对差 ≤ 0.5/255，oct/signed 均满足 | 最大 8e-6 | ✅ |
| AC-4 | 三个 oct 关键字在 Deferred + accurate 下实际启用，并有自动化断言 | `Attribution.json` 的 `keywords` 全为 true；`AccurateGbufferNormalsTests` 断言写入端/解码端契约 | ✅ |
| AC-5 | signed 路径无回归：T12 图像哈希不变，EditMode / PlayMode 全绿 | EditMode 182/182；T12 Forward 34/34、Forward+ 34/34、Deferred 35/35，`passed=true` | ✅ |
| AC-6 | oct 路径性能回归 < 5% | 本轮未测（只新增 shader 变体，无额外 pass）；列为 follow-up | ○ |

阈值定义在 `ScreenSpaceLightingAccurateNormalsAttribution.cs`（`k_HighFrequencyTolerance`、`k_LuminanceTolerance`、`k_FrameDeltaTolerance`），脚本退出码即判定结果。

---

## 5. 风险与约束

| 风险 | 处理 |
|---|---|
| 截图状态未入库（renderer feature 未序列化） | T0 用仓库内复现资产；结论只引用提交内资产 |
| oct 变体首次运行，可能不止一个缺陷 | T0 归因表必须区分多个缺陷；T1 可拆成多个提交，但不得混入 signed 路径改动 |
| Game View 2× 缩放会污染图像指标 | 捕获统一走 `Camera.Render` 到固定 RT，不用 Game View 截图 |
| `.rdc` 预算 1 GiB | 本轮最多保留 1–2 个 `.rdc`，其余用导出的 PNG/JSON 证据 |

---

## 6. 实施记录

| 日期 | 任务 | 结果 | 证据 |
|---|---|---|---|
| 2026-11-19 | 计划定稿 | 问题描述、方案、验收标准 | 本文件（`0f64764`） |
| 2026-11-19 | T0 复现与归因 | T3 场景 + `SslT5DeferredRenderer`；GTAO-only 的 oct 输出与 signed 接近但 AO 整块变暗；SSL-only 的 oct 全图高频 0.00212 → 0.01245，`SsgiRaw` 逐像素随机命中；H2/H3/H4 排除，H1 细化为 H5（写入端 signed） | `validation/Attribution/Attribution.before.json`、`UnityRun.log` |
| 2026-11-19 | T1 修复 | 14 个 shader/HLSL 文件补齐 oct 关键字/编码（§3 T1 表） | `git show` 本轮提交 |
| 2026-11-19 | T2 回归 | `AccurateGbufferNormalsTests`（2 个测试）加入 T14 过滤器；归因脚本加阈值判定 | `validation/EditMode/` |
| 2026-11-19 | 修复后复测 | `completed=true passed=true settingsRestored=true`；`dLuma ≤ 1e-5`、`dHfBottom = 1.2e-4`、`frameDelta ≤ 8e-6` | `validation/Attribution/Attribution.json`、`UnityRun.final.log` |
| 2026-11-19 | EditMode | 182/182 passed | `validation/EditMode/UnityEditModeTests.xml` |
| 2026-11-19 | PlayMode | 6/6 passed | `validation/PlayMode/UnityPlayModeTests.xml` |
| 2026-11-19 | T12 回放 | 结果见 `validation/T12/` | `validation/T12/T12Verification.json` |