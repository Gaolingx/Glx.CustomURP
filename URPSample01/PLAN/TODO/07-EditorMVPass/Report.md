# 07-EditorMVPass 验证报告

> 日期：2026-11-18
> 基线：`2291aa0` 之后的工作区（本报告与代码同次提交）
> 环境：Unity 2022.3.62f1 / URP 14.0.12（fork）/ D3D11 / NVIDIA GeForce RTX 5060 Ti
> 状态：**自动验收通过（PASS*）**；AC-9（真实 Scene View 人工拖拽）待仓库所有者确认。

---

## 1. 结论

Edit Mode 下无 `UniversalAdditionalCameraData` 的相机（Scene View 及编辑器脚本创建的相机）现在由 GTAO feature 自行补写**相机运动矢量**：

- 无 ACD 路径与 URP 原生 MV 路径在 3 种渲染模式下**逐帧完全一致**：`meanAbs = 0`、`pixels > 0.02 = 0`。
- 通过 `s_EditorSuppressEditorMotionPass` 抑制后，同一序列与修复结果差异 `meanAbs = 0.01021`，`8.8%` 像素超过 `0.02`，A/B 可复现残影。
- 3°/帧旋转产生的运动均值 `|mx| = 0.03642`、`|my| = 0.00608`，与 SSR ghost 分析的 `0.03683` 同量级（偏差 1.1%）。
- 静止相机两路输出**逐像素相同**（`meanAbs = 0`），说明没有引入额外差异。
- 深度金字塔改为“同相机 + 同帧 + **同姿态**”去重；同帧同姿态仍只构建一次。

回归：EditMode `173/173`、PlayMode `6/6`、T12 `34/34 + 34/34 + 35/35`。T12 有 12 张图哈希变化，全部落在 `RenderScale75` / `TargetResize` / `CameraCut` / `MotionBlur` 四个“同帧内改变姿态或分辨率”的用例，且是 T4 的预期纠正（见 §5.3）。

---

## 2. 根因（代码证据）

| 事实 | 位置 |
|---|---|
| GTAO 只要拿到 motion 句柄就开启时间累积 | `GroundTruthAmbientOcclusionPass.cs:154` |
| Motion 由 `ConfigureInput` 强制请求，Scene View 下句柄一定存在 | `GroundTruthAmbientOcclusionPass.cs:97` |
| URP 只为带 ACD 的相机取 `motionVectorsPersistentData`，否则直接返回 | `Runtime/Passes/MotionVectorRenderPass.cs:81-89` |
| 上一帧 VP 也只在 ACD 存在时更新，且按 `Time.frameCount` 节流 | `Runtime/UniversalRenderPipeline.cs:657-659`、`Runtime/MotionVectors.cs:83` |
| motion RT 在 pass 前已被清黑 | `MotionVectorRenderPass.cs:52-63` |
| 黑 motion 使历史原地混合 | `Shaders/Utils/GroundTruthAmbientOcclusion/GTAOTemporalDenoise.compute:94-118` |
| 深度金字塔按 `Time.frameCount` 去重 | 修改前 `ScreenSpaceDepthPyramid.cs:86-93` |

结论：无 ACD 相机的时间降噪把上一帧 AO 原地混合，相机移动后旧 AO 留在屏幕上，形成拖影；Player 与带 ACD 的相机走原生 MV，不受影响。

---

## 3. 实现

| 文件 | 变更 |
|---|---|
| `Runtime/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionEditorMotionPass.cs` | 新增，Editor-only；事件 `AfterRenderingPrePasses + 3 = 203`（晚于 URP 清屏 202，早于 GTAO 204）；每相机记录上一次渲染的 VP；首帧只记录不绘制 |
| `GroundTruthAmbientOcclusionHistory.cs` | 增加 per-camera `hasEditorMotionViewProjection` / `editorMotionViewProjection`，`Reset`/`Dispose` 清空 |
| `GroundTruthAmbientOcclusionRendererFeature.cs` | `Create` 新建 pass、`Dispose` 置空；仅对**无 ACD** 相机入队；新增测试钩子 `s_EditorSuppressEditorMotionPass` |
| `Shaders/Utils/GroundTruthAmbientOcclusion/GTAODebug.shader` | 追加 pass 1，严格复刻 URP `CameraMotionVectors.shader` 的矩阵、NDC→UV、`UNITY_UV_STARTS_AT_TOP` 约定；复用已序列化的 debug material，避免资产 churn |
| `ScreenSpaceDepthPyramid.cs` | `ShouldBuild(frameIndex, viewProjection)`；`Reset`/`Dispose` 清空构建 token |
| `ScreenSpaceDepthPyramidSystem.cs` | `Acquire` 用 `GetProjectionMatrixNoJitter(0) * GetViewMatrix(0)` 作为姿态 key |
| `Editor/.../GroundTruthAmbientOcclusionEditor.cs` | Inspector 帮助文本说明 Editor 下补相机 MV、不含物体 MV |

关键取舍：

- **不覆盖 URP 已有输出**：只在相机确实没有 ACD 时入队和执行；有 ACD 的相机保留原生相机+物体运动。
- **不把 prev VP 放在 pass 字段**：renderer/feature 实例被多个相机共享，prev VP 放在按相机 key 的 history 里。
- **按每次渲染记录 prev VP**：不用 `Time.frameCount`，同帧多姿态渲染也能正确重投影。
- **首帧跳过绘制**：GTAO 首帧本来就 CopyHistory，没有可见影响。

---

## 4. 验证环境与命令

```powershell
# EditMode（已把 ScreenSpaceDepthPyramidSharingTests 补进过滤器）
powershell -File "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14EditModeTests.ps1" `
  -LogPath  "PLAN/TODO/07-EditorMVPass/validation/UnityEditModeTests.log" `
  -ResultsPath "PLAN/TODO/07-EditorMVPass/validation/UnityEditModeTests.xml"

# E2E（异步 harness，每个 editor tick 渲染一步；脚本自行退出）
powershell -Command '& "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" `
  -ExecuteMethod ScreenSpaceLightingEditorMotionVerification.Run `
  -LogPath "PLAN/TODO/07-EditorMVPass/validation/UnityEditorMotion.log" `
  -ReportPath "PLAN/TODO/07-EditorMVPass/validation/UnityEditorMotionSettingsRestoration.json" `
  -AdditionalArguments @("-ssl-editor-motion-output","<abs>/PLAN/TODO/07-EditorMVPass/validation/EditorMotion")'

# PlayMode
powershell -File "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14PlayModeTests.ps1" `
  -LogPath "PLAN/TODO/07-EditorMVPass/validation/UnityPlayModeTests.log" `
  -ResultsPath "PLAN/TODO/07-EditorMVPass/validation/UnityPlayModeTests.xml"

# T12 组合矩阵
powershell -File "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14GraphicsRegression.ps1" `
  -LogPath "PLAN/TODO/07-EditorMVPass/validation/T12/UnityT12.log" `
  -OutputRoot "PLAN/TODO/07-EditorMVPass/validation/T12"
```

E2E 设计要点：

- 每次 `camera.Render()` 后等待 `Time.frameCount` 前进（`QueuePlayerLoopUpdate`），保证 URP 原生 MV 也按帧更新；相机 `enabled = false`，避免编辑器 player loop 额外渲染。
- 序列：8 帧预热 → 4 帧、每帧 yaw 3°；每帧保存 AO debug 视图与 motion 均值。
- 参考图：同姿态、全新相机实例收敛 12 帧。
- 用例：无 ACD、带 ACD（原生 MV）、无 ACD + 抑制钩子、静止无 ACD。
- 3 种渲染模式各跑一遍。

---

## 5. 结果

### 5.1 E2E（三模式结果一致）

| 用例 | passed | `meanAbs` vs 原生 ACD | `pixels>0.02` vs 原生 | `meanAbs` vs 抑制基线 | motion `|mx|` / `|my|` | editor draws / first-skip |
|---|---|---|---|---|---|---|
| NoAdditionalCameraData | ✅ | **0** | **0** | — | 0.03642 / 0.00608 | 22 / 2 |
| AdditionalCameraData | ✅ | **0** | **0** | — | 0.03642 / 0.00608 | 0 / 0 |
| NoAdditionalCameraDataBaseline | ✅ | — | — | 基线自身；相对修复 `0.01021` / `81,108 px (8.8%)` | 0 / 0 | 0 / 0 |
| StaticNoAdditionalCameraData | ✅ | 0 | 0 | — | 0 / 0 | 22 / 2 |

- `22 / 2`：每用例渲染主相机 + 参考相机各 12 帧，首帧各记录一次（共 2 次 skip），其余 22 次绘制。
- 逐帧 `frameCount` 严格递增（如 Forward 无 ACD 依次为 8、9、10、11），排除了同帧重复渲染对原生参考的干扰。
- 原始数据：[EditorMotion/Metrics.json](validation/EditorMotion/Metrics.json)、[EditorMotion/Report.json](validation/EditorMotion/Report.json)，逐帧 PNG 在 `validation/EditorMotion/`。

**关于“收敛参考”指标**：`meanAbs vs 12 帧收敛参考 ≈ 0.035`，但 **URP 原生 MV 路径同样是 0.035**。原因是时间累积相对瞬时 AO 存在固定反馈延迟（4 帧内无法完全收敛），这不是重投影错误。因此该指标仅作为诊断字段保留，判定改为“与原生 MV 路径逐帧一致 + 抑制后显著劣化”。

### 5.2 测试与回归

| 项 | 结果 | 证据 |
|---|---|---|
| EditMode | 173/173 | `UnityEditModeTests.xml` |
| PlayMode | 6/6 | `UnityPlayModeTests.xml` |
| T12 Forward | 34/34 | `T12/T12Verification.json` |
| T12 Forward+ | 34/34 | 同上 |
| T12 Deferred | 35/35 | 同上 |
| 设置恢复 | 全部 `true`，Unity exit 0 | 四个 `*SettingsRestoration.json` |
| 新增单测 | feature gating / 事件 203 / per-camera 状态 / 首帧清理 / 金字塔姿态去重 | XML 内 `GroundTruthAmbientOcclusion*`、`ScreenSpaceDepthPyramidSharingTests` |

### 5.3 T12 图像哈希差异（12 张）

对比基线：`PLAN/Done/06-ValidationAllRDC/validation/P6/T12`。完整清单见 [T12/HashDiff.json](validation/T12/HashDiff.json)。

| 用例 | 为什么变化 | 结论 |
|---|---|---|
| `RenderScale75` | 同帧内修改 renderScale，金字塔条目 resize 后旧实现不重建，整段 12 帧沿用旧尺寸/旧姿态深度 | T4 现在在 resize 时重建；40%–57% 像素变化，属纠正 |
| `TargetResize` | 同帧内更改目标尺寸，同上 | 同上 |
| `CameraCut` | 同帧内瞬移相机；SSL/GTAO history 重置，但金字塔不重置 | T4 按姿态重建；差异集中在切镜后边缘（1.4%–2.0% 像素） |
| `MotionBlur` | 8 次 `camera.Render()` 紧循环内旋转相机，同帧多姿态 | T4 按姿态重建；26%–31% 像素变化，属纠正 |
| `SameFrameDoubleRender`（对照） | 同帧同姿态重复渲染 | 哈希**不变**，证明“同姿态仍只构建一次” |

逐张数值（meanAbs / maxAbs / >5/255 像素比例）与新旧 SHA-256 均在 `T12/HashDiff.json`。T12 自身判定全部通过，未放宽任何阈值。

### 5.4 T0.2：同帧多姿态是否真实存在

- **存在**：T12 `MotionBlur` 用例就是一个真实路径——同一 `Time.frameCount` 内 8 次手动 `Render()` 并逐次旋转相机；E2E 第一版也观测到 `frameCount` 重复（2 次、3 次同帧渲染）。
- 真实 Scene View 拖拽通常每次重绘推进一个 editor frame，但暂停态、脚本/工具手动渲染、以及同一帧内多次重绘都会命中。
- 因此 T4 保留为本轮交付（防御性但真实可触发），不是可选的清理项。

---

## 6. AC 对照

| ID | 结果 | 证据 |
|---|---|---|
| AC-1 根因确认 | ✅ | §2；E2E 抑制基线 motion = 0 且图像劣化 |
| AC-2 修复生效 | ✅ | `s_EditorFirstFrameSkipCount` 每用例 2（主+参考），`draws = 22`；motion 0.03642 vs 参考 0.03683 |
| AC-3 拖影消除 | ✅（口径调整） | 与原生 MV 路径逐帧 `meanAbs = 0`、`>0.02 = 0`；“收敛参考”受时间反馈延迟影响，见 §5.1 |
| AC-4 无修复必失败 | ✅ | 抑制后相对修复 `0.01021`、81,108 px（8.8%）超阈值 |
| AC-5 与 Player 路径一致 | ✅ | 三种模式逐帧差值均为 0 |
| AC-6 无回归 | ✅ | EditMode 173/173、PlayMode 6/6、T12 34/34/35；12 张差异逐条解释 |
| AC-7 金字塔 | ✅ | 单测覆盖同帧同姿态/姿态变化；T12 静态用例哈希不变 |
| AC-8 工程卫生 | ✅ | 设置恢复 true、exit 0；工作区只含预期文件；无新增编译错误 |
| AC-9 人工验收 | ⏳ 待所有者 | 需在真实 Scene View 拖拽确认；命令与观察点见 HANDOFF §7.3 |

---

## 7. 限制与风险

见 [KnownDifferences.md](KnownDifferences.md)。要点：

- Editor 下只补相机运动，不补物体运动。
- 同姿态下物体移动 + 同帧重渲染仍复用金字塔（无干净 token）。
- 只开 SSL（不开 GTAO）的 Scene View 仍没有 MV；本轮只修 GTAO。
- XR 多视图不补。
- 若 AC-9 在真实 Scene View 中仍出现残影，先检查 renderer 是否实际启用了 GTAO temporal 与本 pass（`s_EditorExecuteCount/DrawCount`），再按 HANDOFF §8 的回退路径处理。