# 07-EditorMVPass 交接：Editor 运动矢量补齐（GTAO 相机拖影）

> 状态：**调研完成 / 待实施**
> 建立：2026-11-18
> 基线：`3cee823`（工作区干净，仅本文件新增）
> 关联：`PLAN/Done/01-ScreenSpaceLighting`（GTAO / SSL 实现）、`PLAN/Done/06-ValidationAllRDC`（差异与证据规范）
> 外部参考：`E:\GitHub\UnitySSGIURP`（SSGI URP，v1.0.2 起自带 Editor MV 修复）
> 本文中的代码行号基于 `3cee823`，改动后以符号名为准。

---

## 0. 一页结论

用户报告：**Edit Mode 下开启 GTAO 后，移动 Scene View 相机会留下 AO 残影；只开 GTAO 仍复现；Player 构建一切正常**。

根因已确认：
1. GTAO 只要拿到 `m_MotionVectorColor` 句柄就开启时间累积（`GroundTruthAmbientOcclusionPass.cs:154`），而该句柄因为 GTAO 请求了 `Motion` 输入一定存在（`:97`）。
2. **Scene View 相机没有 `UniversalAdditionalCameraData`**，URP 的 `MotionVectorRenderPass` 因此提前返回（`MotionVectorRenderPass.cs:81-89`），motion 纹理只保留 `ConfigureClear` 的黑值；上一帧 VP 也不会更新（`UniversalRenderPipeline.cs:657-658`）。
3. 时间降噪 kernel 用 `prevUV = uv - motion` 采样历史（`GTAOTemporalDenoise.compute:89-118`），motion 恒为 0 → 历史原地混合（feedback 0.85–0.95）→ 相机移动时旧 AO 留在屏幕上，即残影。
4. Player 正常是因为 Game 相机带 `UniversalAdditionalCameraData`，URP 原生 MV 有效。

修复方案（参考 `UnitySSGIURP` 已验证的做法）：**Editor 下为“没有运动矢量来源”的相机补一个专用 MV pass，写回 URP 的 motion RT**，让 GTAO 时间累积拿到真实相机速度。不做“Scene View 丢弃 temporal”的降级（会让静置 Scene View 永久变噪）。

附带加固（用户原始怀疑点）：共享深度金字塔按 `Time.frameCount` 去重（`ScreenSpaceDepthPyramid.cs:85-93`），同一帧内重渲染会复用旧姿态深度。它与 MV 缺失是两条独立的残影来源，本轮一并处理（是否阻塞主修复取决于 T0.2 的实测结论）。

---

## 1. 现象、复现与用户侧证据

- 环境：Unity 2022.3.62f1 / URP 14.0.12（fork）/ D3D11。
- 场景：`Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingT3Verification.unity`。
- Renderer：默认管线 renderer（截图为 `Assets/Settings/URP-HighFidelity-Renderer.asset`）上启用 GTAO，`Temporal Accumulation = on`（默认值，`GroundTruthAmbientOcclusionSettings.cs:48`）。
- 操作：Edit Mode 下移动/旋转 Scene View 相机 → 球体接触处、地面出现 AO 拖影。
- A/B：关闭 GTAO temporal 后拖影消失；Player 构建不出现。

**注意（仓库状态）**：截至 `3cee823`，`Assets/Settings/URP-*.asset` 中**没有** GTAO feature（按 feature GUID 检索为空），截图的启用状态不在提交里。复现时必须记录实际使用的 renderer 资产与序列化配置（截图/`git diff`/字段 dump），或改用已内置 GTAO 的 `Assets/Tests/ScreenSpaceLighting/Renderers/SslT5*Renderer.asset` / `SslT3*Renderer.asset`。

---

## 2. 已完成的调研与证据链

### 2.1 GTAO 何时启用时间累积

| 事实 | 位置 |
|---|---|
| `ConfigureInput(Depth｜Normal｜Motion)`，Motion 同时负责把 depth 调度提前 | `GroundTruthAmbientOcclusionPass.cs:97` |
| `temporal = m_Settings.temporalAccumulation && motionTexture != null` | `GroundTruthAmbientOcclusionPass.cs:154` |
| `GetMotionTexture()` 直接返回 `UniversalRenderer.m_MotionVectorColor` | `GroundTruthAmbientOcclusionPass.cs:134-135, 548-554` |
| 只要 `requiresMotionVectors` 为真就分配 `m_MotionVectorColor` 并绑定全局 | `UniversalRenderer.cs:1221-1240` |
| Motion 消费事件为早调度 `AfterRenderingPrePasses + 2`（202） | `UniversalRenderer.cs:101`、`:1531-1535` |

结论：Scene View 下 motion 句柄非空 → temporal 一定开启。

### 2.2 为什么 Scene View 没有运动矢量

| 事实 | 位置 |
|---|---|
| motion 数据只从相机的 `UniversalAdditionalCameraData` 取；取不到直接 `return` | `Runtime/Passes/MotionVectorRenderPass.cs:79-89` |
| 上一帧 VP 也只在相机有该组件时更新 | `Runtime/UniversalRenderPipeline.cs:657-658` |
| URP 明确假设 Scene View 相机**没有**该组件（“镜像 Main Camera 的 volumeLayerMask”只在 `cameraData == null` 分支） | `Runtime/UniversalAdditionalCameraData.cs:250-273` |
| 包内没有任何给 Scene View 相机补组件的钩子（仅 `ModelPostProcessor` 与相机 Inspector 的显式调用） | `Editor/AssetPostProcessors/ModelPostProcessor.cs:11`、`Editor/Camera/UniversalRenderPipelineCameraEditor.cs` |
| |motion RT 在 pass 执行前已按 `ConfigureClear(Color.black)` 清空（`SetRenderPassAttachments` 先于 `Execute`） | `Runtime/Passes/MotionVectorRenderPass.cs:52-63`、`Runtime/ScriptableRenderer.cs:1537-1545` |
| Scene View 相机渲染使用 `asset.scriptableRenderer`（默认 renderer），GTAO feature 就在其中 | `Runtime/UniversalRenderPipeline.cs:1237-1245` |

结论：Scene View 的 `_MotionVectorTexture` = 黑，且物体运动也不会被写入（pass 在画任何东西之前就返回了，`:115` 的 `DepthTextureMode.MotionVectors` 也没设置）。

### 2.3 黑 motion 为什么变成残影

`Shaders/Utils/GroundTruthAmbientOcclusion/GTAOTemporalDenoise.compute:89-118`：

- `motionVector = 0` → `prevFrameNDC = uv`，历史永远取当前像素；
- `motionVecWeighting = 0`、`velWeight = 1`；
- `depthSimilarity = saturate(prevDepth/currDepth + 0.01)`，同一像素深度接近时 ≈ 1；
- `feedback = lerp(0.85, 0.95, weight²)` → 当前 AO 只占 5%–15%。

相机移动后，历史 AO 与当前几何错位，但混合权重几乎不变 → 残影。Player 有真实 motion 时 `prevUV` 正确，历史跟随几何移动，因此无残影。

### 2.4 “深度未及时更新”成立吗

部分成立，但**不是本次残影的主因**：

| 事实 | 位置 |
|---|---|
| 深度金字塔每个相机每个 `Time.frameCount` 只构建一次 | `ScreenSpaceDepthPyramid.cs:85-93`、`ScreenSpaceDepthPyramidSystem.cs:51, 68-83` |
| 上一帧 VP 同样按 `Time.frameCount` 节流 | `Runtime/MotionVectors.cs:81-105` |
| URP 自己的 TAA 专门处理“同帧重渲染/暂停” | `Runtime/TemporalAA.cs:429-449, 471-476` |
| GTAO 没有对应防护 | — |

只要有“同一 `Time.frameCount` 内多次渲染”（暂停、脚本手动 `Camera.Render()`、Scene View 的某些重绘路径），第二次起 AO 的 trace 用的是旧姿态深度，形状像“贴在屏幕上的旧 AO”。它与 2.3 的历史拖影独立叠加。

**尚未实测**：真实 Scene View 每次重绘是否都推进 `Time.frameCount`。见 §5 的 T0.2；该结论决定 T4 是主修复还是防御性加固。

### 2.5 影响面

- 仅 Editor（`UNITY_EDITOR`）；Player 不受影响。
- 不区分 Edit / Play Mode：Editor 内进入 Play Mode 后 Scene View 仍渲染、仍无 ACD，症状相同。
- 只开 GTAO 仍复现（GTAO 自己的历史）；SSL 的 SSGI/SSR 在 Scene View 下理论上也有同类问题（本轮不修，见 §9）。

---

## 3. 参考实现调研（UnitySSGIURP）

### 3.1 做法

| 步骤 | 位置（`E:\GitHub\UnitySSGIURP`） |
|---|---|
| 仅 `UNITY_EDITOR` + `CameraType.SceneView` 入队前置 pass | `Runtime/ScreenSpaceGlobalIlluminationURP.cs:434-443` |
| 自己维护 current/prev 非 jittered VP（按**每次渲染**推进，不看 frameCount） | `:565-571`（当前值）、`:538-540`（写入后存 prev） |
| 反射取 URP motion pass 的 `m_Color`/`m_Depth`，设置 RT 并整屏重画 | `:541-553` |
| 专用 shader pass：`ZTest Always / ZWrite Off`，用 `_CameraDepthTexture` 重建世界坐标，current/prev VP 求 NDC 差，`*0.5`，`UNITY_UV_STARTS_AT_TOP` 翻 y | `Shaders/ScreenSpaceGlobalIllumination.shader:836-897` |
| 上游问题记录 | `CHANGELOG.md:167`：“Fixed an issue from URP where motion vectors in scene view were incorrectly rendered in Unity 2022 & 2023.” |

其 pass 事件：Unity 6 用 `BeforeRenderingPrePasses`，URP 14 用 `BeforeRenderingTransparents - 1`（因为它的消费者 SSGI 在 450）。我们的消费者 GTAO 在 204，不能用同一个事件，见 §6。

### 3.2 为什么有效

- 直接覆盖 URP 已经清黑的 motion RT，绕开 `MotionVectorRenderPass` 对 ACD 的依赖；
- prev VP 按“上一次实际渲染”记录，天然兼容同帧多渲染；
- 只补相机运动，不依赖引擎的上一帧模型矩阵。

### 3.3 不要照抄的点

| 问题 | 说明 | 我们的处理 |
|---|---|---|
| `prevCamVPMatrix == null` | `Matrix4x4` 是 struct，与 null 的抬升比较恒为 false，首帧用零矩阵画出垃圾速度 | 用 `bool hasPreviousVP`，首帧只记录不绘制 |
| pass 状态放 feature 实例 | URP 的 renderer 按 **renderer data** 缓存并被所有相机共享（`UniversalRenderPipelineAsset.cs:878-899`），多个 Scene View / Game+Scene 同帧渲染会互相污染 prev VP | prev VP 存在**按相机 key 的 history entry** 里，不放 pass 字段 |
| 无条件覆盖 motion RT | 会抹掉 URP 已写入的物体运动 | 只在相机**确实没有 ACD**时入队/执行 |
| `Blitter.BlitTexture(source==target)` | 同一资源同时作为 SRV/RT | 用无 source 重载或直接 `DrawProcedural` |

---

## 4. 目标与非目标

### 目标

1. 在 Editor 中，Scene View（以及任何无 ACD 的相机）拖拽时 GTAO 不再产生历史拖影；修复后行为与 Player 的“有 MV”路径一致。
2. 不降低静置 Scene View 的 AO 质量（不允许用“Scene View 永久关闭 temporal”换通过）。
3. 消除同帧重渲染的深度金字塔复用（T4，按 T0.2 结论决定是否阻塞）。
4. 不改变 Player 行为、不改变有 ACD 相机的像素输出。

### 非目标

- 不实现 Scene View 的**物体**运动矢量（参考实现同样只补相机；场景内运动物体会在 Editor 下产生 AO 滞后，记入 KnownDifferences）。
- 不改 URP 的 `MotionVectorsPersistentData` / `MotionVectorRenderPass` 架构（不把 motion 数据从 ACD 搬到 pipeline）。
- 不为“只开 SSL、不开 GTAO”的 Scene View 单独修 MV（见 §9，可作为后续轮次）。
- 不改 XR 路径（Editor MV pass 只处理单视图）。

---

## 5. 调研/验证方案（实施前的 T0，必须先做）

### T0.1 复现与 A/B 基线（30 分钟）

1. 关闭所有 Unity 实例；`git rev-parse HEAD` 与 `git status --porcelain` 记入报告。
2. 打开 `ScreenSpaceLightingT3Verification.unity`，在所用 renderer 上启用 GTAO（若使用默认 renderer 需手动加 feature），记录 feature 序列化字段截图。
3. 复现：拖动 Scene View → 残影；关闭 `Temporal Accumulation` → 残影消失；Player → 无残影；Editor 内 Game View → 无残影。
4. 产物：`validation/baseline/manual.md` + 截图（`before_drag.png`、`before_temporal_off.png`、`player_reference.png`）。

### T0.2 `Time.frameCount` vs 渲染次数（1 小时，决定 T4）

目的：确认“同一 `Time.frameCount` 内多次渲染”在真实 Scene View 拖拽中是否发生。

方法（临时脚本，不入库）：

- 在 `GroundTruthAmbientOcclusionPass.Execute`（或 `ScreenSpaceDepthPyramidPass.Execute`）临时打印 `Time.frameCount` + 相机 `worldToCameraMatrix` 的行哈希；
- 用户/操作者拖动 Scene View 5 秒，记录日志；
- 判据：同一 `Time.frameCount` 是否出现多个不同的相机矩阵。

结论分支：

- **存在同帧多姿态渲染** → T4 是主修复之一，必须与 T1–T3 同轮交付；E2E 测试允许用 tight loop。
- **不存在（每次重绘都推进 frameCount）** → 用户残影由 MV 缺失单独解释；T4 降级为防御性改动（覆盖暂停态/手动 `Camera.Render()`），可拆成独立提交，不阻塞主修复；E2E 测试必须用“每个 editor tick 渲染一步”的异步 harness。

### T0.3 参考实现的可用性映射（已完成，待实施复核）

- 我们不需要反射：`UniversalRenderer.m_MotionVectorColor` 是 `internal`（`UniversalRenderer.cs:140`）。
- 不需要 motion depth RT（我们的 shader 不写 `SV_Depth`）。
- 事件顺序已确认：202（URP 清屏）→ 203（新 pass）→ 204（GTAO）。
- `_CameraDepthTexture` 在 `UniversalRenderer.Setup` 中已全局绑定（`UniversalRenderer.cs:962-966`），Scene View 强制 depth prepass（`:725-726`）。

---

## 6. 任务实施计划

### T1 — 新增 Editor MV pass（核心）

**文件**：`Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionEditorMotionPass.cs`（新建）

- `renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 3)`（203）：晚于 URP 的早调度 motion（202），早于 GTAO（204）。
- `ConfigureInput(ScriptableRenderPassInput.Depth)`（幂等；GTAO 已请求 Depth，显式声明保证单独测试时也有 depth）。
- per-camera 状态放在 `GroundTruthAmbientOcclusionHistory`（已按相机 key）：
  - `bool hasEditorMotionViewProjection`
  - `Matrix4x4 editorMotionViewProjection`
  - `Reset()` / `Dispose()` 时清空；`Dispose` 不泄漏。
- `Execute` 伪代码：

```csharp
if (!(cameraData.renderer is UniversalRenderer renderer)) return;
var motionTarget = GroundTruthAmbientOcclusionPass.GetMotionTexture(renderer);
var material = m_Resources.debugMaterial;           // 见 T2
if (motionTarget == null || material == null) return;

// 防御：有 ACD 的相机由 URP 原生写入，不能覆盖（含物体运动）
if (cameraData.camera.TryGetComponent<UniversalAdditionalCameraData>(out _)) return;

Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(
    cameraData.GetProjectionMatrixNoJitter(0), true) * cameraData.GetViewMatrix(0);

if (!m_History.hasEditorMotionViewProjection)
{
    m_History.RecordEditorMotionViewProjection(viewProjection); // 首帧只记录
    return;
}

cmd.SetGlobalMatrix("_PrevViewProjMatrix", m_History.editorMotionViewProjection);
cmd.SetGlobalMatrix("_NonJitteredViewProjMatrix", viewProjection);
CoreUtils.SetRenderTarget(cmd, motionTarget);
cmd.DrawProcedural(Matrix4x4.identity, material, k_MotionPassIndex, MeshTopology.Triangles, 3, 1);

m_History.RecordEditorMotionViewProjection(viewProjection);
```

- 编辑器测试计数（放在 `#if UNITY_EDITOR`，风格对齐现有 `s_Editor*Count`）：
  - `s_EditorExecuteCount`、`s_EditorDrawCount`、`s_EditorFirstFrameSkipCount`。
- 注意：pass 实例被所有相机共享，**不得**把 prev VP 放在 pass 字段；`Setup(history, resources)` 每次 `AddRenderPasses` 调用。

### T2 — Shader

**推荐（避免资产改动）**：在既有 `Shaders/Utils/GroundTruthAmbientOcclusion/GTAODebug.shader` 追加一个 pass（index 1），复用已序列化的 `debugShader` 与 `resources.debugMaterial`：

- `Name "Editor Camera Motion Vectors"`，SubShader 已声明 `Cull Off ZWrite Off ZTest Always Blend Off`；
- 采样 `_CameraDepthTexture`（`#include ".../ShaderLibrary/DeclareDepthTexture.hlsl"`，`SampleSceneDepth(uv)`）；
- `posWS = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP)`（非 reversed Z 时先做 `lerp(UNITY_NEAR_CLIP_VALUE, 1, depth)`）；
- `posCS = mul(_NonJitteredViewProjMatrix, float4(posWS,1))`、`prevPosCS = mul(_PrevViewProjMatrix, ...)`；
- `velocity = posNDC - prevPosNDC`，`UNITY_UV_STARTS_AT_TOP` 翻 y，最后 `* 0.5`；
- 输出 `float4(velocity, 0, 0)`；
- 顶点用现有 `Vert`（fullscreen triangle，`GetFullScreenTriangleTexCoord`），不依赖 `_BlitScaleBias`。
- `_PrevViewProjMatrix` / `_NonJitteredViewProjMatrix` 由 `ShaderLibrary/UnityInput.hlsl:262-270` 声明，无需重复声明。

**备选（命名更清晰，但不推荐）**：新建 `GTAOSceneViewMotionVectors.shader` + `GroundTruthAmbientOcclusionResources` 序列化字段 + 更新 `isComplete` 与全部 renderer 资产/测试 harness，资产 churn 大。若采用，须同步 `SslT5*`、`SslT12*`、`SslT3*` 等资产与 T4/T5 的资源断言。

### T3 — Feature 接线与 gating

文件：`Runtime/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionRendererFeature.cs`

```csharp
#if UNITY_EDITOR
// URP writes _MotionVectorTexture only for cameras with UniversalAdditionalCameraData.
// Scene View (and any editor-created camera without it) otherwise gets a cleared texture.
if (renderer is UniversalRenderer &&
    cameraData.cameraType != CameraType.Preview &&
    cameraData.cameraType != CameraType.Reflection &&
    !cameraData.camera.TryGetComponent<UniversalAdditionalCameraData>(out _))
{
    m_EditorMotionPass.Setup(history, m_Settings.resources);
    renderer.EnqueuePass(m_EditorMotionPass);
}
#endif
```

- 在 `Create()` 中 new、`Dispose()` 中置空；
- 与参考实现的差异：按“缺少 ACD”这一真实条件 gating，而不是只认 `CameraType.SceneView`，便于用普通相机做回归，也覆盖 Editor 下的其他无 ACD 相机；
- 不检查 `Application.isPlaying`：Editor 内 Play Mode 的 Scene View 同样需要；
- 更新 feature Inspector 帮助文本（`Editor/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionEditor.cs`）与类注释，说明 Editor 下会自行补相机 MV、且不含物体运动。

### T4 — 深度金字塔按姿态重建（按 T0.2 结论执行）

文件：
- `Runtime/RendererFeatures/ScreenSpaceCommon/ScreenSpaceDepthPyramid.cs`
- `Runtime/RendererFeatures/ScreenSpaceCommon/ScreenSpaceDepthPyramidSystem.cs`

改动：

```csharp
internal bool ShouldBuild(int frameIndex, Matrix4x4 viewProjection)
{
    if (m_BuildFrame == frameIndex &&
        m_HasBuildViewProjection &&
        m_BuildViewProjection == viewProjection)
        return false;

    m_BuildFrame = frameIndex;
    m_BuildViewProjection = viewProjection;
    m_HasBuildViewProjection = true;
    return true;
}
```

- `Acquire` 计算 `viewProjection = cameraData.GetProjectionMatrixNoJitter(0) * cameraData.GetViewMatrix(0)`；
- 去重语义保持“同相机 + 同帧 + 同姿态只构建一次”（GTAO/SSL 两个消费者仍共享一次 build）；
- 不覆盖“同姿态下物体移动”的同帧重渲染（无干净 token），在注释与 KnownDifferences 标注；
- 更新 `SameCameraFrameEnqueuesTheSharedPyramidOnce` 的语义注释，新增“姿态变化重建”单测。

### T5 — 测试

**单测（`Packages/.../Tests/Editor/...`，沿用现有 fixture，避免改 T14 过滤器列表）**

- `GroundTruthAmbientOcclusionRendererFeatureTests` 增加：
  - 无 ACD 相机 → Editor MV pass 入队；有 ACD → 不入队；
  - pass 事件 = 203；
  - `Create()` 重建 pass；`Dispose()` 不抛异常；
  - 两个相机交替调用 `AddRenderPasses` → 各自 history 的 prev VP 互不污染。
- `ScreenSpaceDepthPyramidSharingTests` 增加：
  - 同帧 + 同姿态 → 仍只 build 一次；
  - 同帧 + 相机移动 → build 两次；
  - 两个 feature 在同一姿态下仍只 build 一次。
- `GroundTruthAmbientOcclusionHistoryTests` 增加：prev VP 在 `Reset`/`Dispose` 后失效。

**端到端（新脚本 + 异步 harness）**

- 新文件：`Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingEditorMotionVerification.cs`；
- 驱动方式：`-executeMethod` 启动状态机 + `EditorApplication.update` **每个 editor tick 渲染一步**（复用 `SsrGhostAnalysis` 的模式），每步记录 `Time.frameCount`，完成后写报告并 `EditorApplication.Exit(code)`；
- 场景/管线复用 T5：`SslT5{Forward,ForwardPlus,Deferred}Pipeline.asset`（其 renderer index 0 已含 GTAO）；
- 用例：
  1. `SceneViewNoAcd`：无 ACD 相机（Game type 即可，触发条件一致）；
  2. `AcdReference`：同姿态序列 + 有 ACD 相机（URP 原生 MV）；
  3. `NoFixBaseline`：无 ACD + 通过 editor-only 测试钩子抑制新 pass；
  4. `StaticCamera`：不移动相机，验证不产生额外差异。
- 序列：8 帧预热 → 每帧 yaw 3° 共 4 帧 → 停止；每步 dump AO debug 视图 + 最终图；另用“全新相机实例”在终止姿态收敛 12 帧作为参考图；
- 指标：meanAbs、max channel diff、`pixels>0.02`、与 ACD 参考的逐帧 diff；
- 抑制钩子：`GroundTruthAmbientOcclusionRendererFeature.s_EditorSuppressEditorMotionPass`（`internal static bool`，`#if UNITY_EDITOR`，默认 false），只供测试反射设置。

### T6 — 文档与证据

- 完成并通过后：`PLAN/TODO/07-EditorMVPass` → `PLAN/Done/07-EditorMVPass`（保留本文件并更新状态/结论）；
- `PLAN/ACCEPTANCE.md` 增加 07 行（PASS/PASS* + 证据入口）；
- `PLAN/Done/01-ScreenSpaceLighting/HANDOFF.md` 增加“后续缺陷修复（日期）：Editor MV pass”小节；
- 新增 `PLAN/Done/07-EditorMVPass/KnownDifferences.md`：
  - Editor 下只补相机运动，无物体运动；
  - 同姿态物体移动 + 同帧重渲染仍复用金字塔；
  - 只开 SSL 时 Scene View 的 MV 不在本轮范围。

### 变更清单

| 文件 | 类型 |
|---|---|
| `Runtime/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionEditorMotionPass.cs` | 新增 |
| `Runtime/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionRendererFeature.cs` | 修改（创建/释放/入队） |
| `Runtime/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionHistory.cs` | 修改（prev VP 状态，per camera） |
| `Shaders/Utils/GroundTruthAmbientOcclusion/GTAODebug.shader` | 修改（追加 motion pass） |
| `Runtime/RendererFeatures/ScreenSpaceCommon/ScreenSpaceDepthPyramid.cs` | 修改（T4） |
| `Runtime/RendererFeatures/ScreenSpaceCommon/ScreenSpaceDepthPyramidSystem.cs` | 修改（T4） |
| `Editor/RendererFeatures/GroundTruthAmbientOcclusion/GroundTruthAmbientOcclusionEditor.cs` | 修改（帮助文本） |
| `Tests/Editor/.../GroundTruthAmbientOcclusion*Tests.cs`、`ScreenSpaceDepthPyramidSharingTests.cs` | 修改 |
| `Assets/Tests/ScreenSpaceLighting/Editor/ScreenSpaceLightingEditorMotionVerification.cs` | 新增（E2E） |
| `PLAN/.../07-EditorMVPass/validation/**`、`Report.md`、`Metrics.json` | 新增 |
| `PLAN/ACCEPTANCE.md`、`01/HANDOFF.md` | 修改 |

### 提交拆分建议

1. `Fix editor motion vectors for GTAO temporal history`（T1–T3 + 单测 + shader）；
2. `Rebuild the shared depth pyramid when the camera pose changes`（T4 + 单测）；
3. `Validate editor motion vectors and GTAO ghosting`（E2E 脚本 + 证据 + 文档）。

---

## 7. 验收流程

### 7.1 环境

- Unity：`D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe`
- 运行前**关闭所有 Unity 实例**（T1 wrapper 会强制检查）。
- 记录基线：`git rev-parse HEAD`、`git status --porcelain`、`SystemInfo`（GPU/API）。

### 7.2 自动化步骤

1. **EditMode**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14EditModeTests.ps1" `
  -LogPath "PLAN/TODO/07-EditorMVPass/validation/UnityEditModeTests.log" `
  -ResultsPath "PLAN/TODO/07-EditorMVPass/validation/UnityEditModeTests.xml"
```

2. **T4 金字塔单测**（若 T4 实施）：同一 EditMode 运行覆盖；报告中单列相关用例结果。

3. **E2E Editor MV 验证**（异步 harness，不加 `-Quit`，由脚本自行退出）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T1/Invoke-T1Unity.ps1" `
  -ExecuteMethod ScreenSpaceLightingEditorMotionVerification.Run `
  -LogPath "PLAN/TODO/07-EditorMVPass/validation/UnityEditorMotion.log" `
  -ReportPath "PLAN/TODO/07-EditorMVPass/validation/UnityEditorMotionSettingsRestoration.json" `
  -AdditionalArguments @("-ssl-editor-motion-output","<abs>/PLAN/TODO/07-EditorMVPass/validation/EditorMotion")
```

4. **T12 组合矩阵回放**（回归）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14GraphicsRegression.ps1" `
  -LogPath "PLAN/TODO/07-EditorMVPass/validation/T12/UnityT12.log" `
  -OutputRoot "PLAN/TODO/07-EditorMVPass/validation/T12"
```

与 `PLAN/Done/06-ValidationAllRDC`/T14 归档哈希对比；若 T4 导致相机运动类用例图像变化，逐张列出并解释（不得静默接受）。

5. **PlayMode**（回归）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "PLAN/Done/01-ScreenSpaceLighting/validation/T14/Invoke-T14PlayModeTests.ps1" `
  -LogPath "PLAN/TODO/07-EditorMVPass/validation/UnityPlayModeTests.log" `
  -ResultsPath "PLAN/TODO/07-EditorMVPass/validation/UnityPlayModeTests.xml"
```

6. **Player 对照**（可选但推荐）：现有 DX11/DX12 验证 Player，确认 GTAO temporal 输出未变（本改动仅在 `UNITY_EDITOR`）。

### 7.3 人工 Scene View 验收（最终闸门）

1. T3 场景 + 含 GTAO 的 renderer，`Temporal Accumulation = on`；
2. 按住右键旋转、滚轮缩放 5–10 秒：球体接触处与地面不得出现 AO 拖影；
3. 对照：关闭 temporal 后图像变噪但同样不得有拖影；
4. Debug Mode = `AmbientOcclusion`：拖动时 AO 轮廓跟随几何移动，不留在屏幕上；`DepthPyramid` 视图应随相机实时变化；
5. 记录实现前后各一段 GIF/截图（`manual_after.gif`）。

### 7.4 证据目录

```
PLAN/Done/07-EditorMVPass/
  HANDOFF.md
  KnownDifferences.md
  validation/
    baseline/                 # T0 复现（before_*.png、manual.md）
    UnityEditModeTests.xml
    UnityEditModeTests.log    # 大日志可本地保留，不入库（遵循 02 轮约定）
    UnityEditorMotion/        # 每用例 PNG、Metrics.json、Report.json
    UnityEditorMotion.log
    T12/                      # 回放图像 + T12Verification.json
    UnityPlayModeTests.xml
    Report.md                 # 结论、AC 对照、差异说明
    Metrics.json              # 机器可读指标
```

---

## 8. 验收标准（逐条可判定）

> 判定方式：代码在 HEAD、证据在 `validation/`、指标由脚本生成；不以自述为准。

| ID | 标准 | 通过条件 | 证据 |
|---|---|---|---|
| **AC-1 根因确认** | 文档与代码一致：Scene View 无 ACD → motion RT 未写入 | 代码引用齐全；`temporal == true` 且 motion 纹理为黑（可由 E2E 首帧/抑制钩子对照证明） | `Report.md` §根因、`Metrics.json` |
| **AC-2 修复生效** | 无 ACD 相机：首帧仅记录 VP 不绘制；第二帧起写入 motion | `s_EditorFirstFrameSkipCount == 1`、`s_EditorDrawCount > 0`；旋转 3° 帧的平均前向位移与 SSR ghost 分析的 `0.03683` 同量级（±50%），静止帧 ≈ 0 | `Metrics.json`（`motionMeanAbsX/Y`） |
| **AC-3 拖影消除** | 无 ACD 相机 3°/帧 ×4 后，最后一帧 AO 与同姿态收敛参考一致 | `meanAbs ≤ 0.002`、`max ≤ 0.02`、`pixels>0.02 == 0`（1280×720，AO debug 视图） | `EditorMotion/SceneViewNoAcd/*.png` |
| **AC-4 无修复必失败（A/B）** | 同一 harness 抑制新 pass 时复现残影 | `NoFixBaseline` 的指标**显著劣于** AC-3 阈值（记录真实值，例如 `meanAbs ≥ 0.01` 或 `pixels>0.02 > 1%`）；差值图有可辨认的 AO 残影 | `EditorMotion/NoFixBaseline/`、`Report.md` |
| **AC-5 与 Player 路径一致** | 同序列下无 ACD + 修复 vs 有 ACD（原生 MV） | 逐帧 `meanAbs ≤ 0.001`、`pixels>0.02 == 0`、`max ≤ 0.02` | `EditorMotion/AcdReference/`、`Metrics.json` |
| **AC-6 无回归** | EditMode / PlayMode / T12 | EditMode 全绿（含新用例）；PlayMode 6/6；T12 34/34/35，图像对比差异只允许出现在“同一 editor tick 内相机移动”的用例，且逐张解释 | `UnityEditModeTests.xml`、`UnityPlayModeTests.xml`、`T12/` |
| **AC-7 金字塔（若实施 T4）** | 同帧同姿态只 build 一次；同帧姿态变化重建 | 单测通过；T12 的 Depth Pyramid dispatch 计数在静态用例不变 | EditMode XML、`T12/*.json` |
| **AC-8 工程卫生** | 无新增 warning/error；Player 不受影响；设置恢复 | Editor 日志无新 warning（shader 编译、缺失资源）；`ProjectSettings` SHA-256 与运行前一致；`git status` 只含预期文件；`.rdc` 预算不变 | 日志、`*SettingsRestoration.json`、`git status` |
| **AC-9 人工验收** | 真实 SceneView 拖动无残影 | 用户确认；提供前后对照 GIF/截图 | `manual_after.gif` |

**停止/回退条件**：若 AC-5 无法达到（y-flip/矩阵约定无法与原生 MV 对齐），先排查 shader 约定，不得直接放宽阈值；若最终仍无法一致，退回到“Editor 下无 MV 来源时关闭 temporal”的保守方案，并在 `Report.md` 与 `KnownDifferences.md` 明确记录质量降级，由仓库所有者决定是否接受。

---

## 9. 风险、限制与回退

| 风险 | 影响 | 处理 |
|---|---|---|
| T4 改变 T12 图像哈希（同帧相机移动用例） | T14 历史哈希对照失败 | 视为“纠正旧金字塔复用”，逐张列出并重新录制；需所有者确认。若只想最小化，T4 可延后（T0.2 判据） |
| 多相机共享 renderer/feature 实例 | prev VP 互相污染 | prev VP 存 per-camera history；E2E 中交替渲染两个相机验证 |
| Y 翻转/矩阵约定不一致 | motion 方向错误，拖影变成反方向 | 严格复刻 URP `CameraMotionVectors.shader`；AC-5 与原生 MV 路径逐帧对齐 |
| 首帧零 motion | 域重载后 1 帧无累积 | 首帧只记录 VP；GTAO 首帧本来也是 CopyHistory，无可见影响 |
| 相机 cut（F 聚焦） | 历史采样越界被 clamp | 现有 `motionVecWeighting/velWeight` 在大运动时拒绝历史（`GTAOTemporalDenoise.compute:99-118`），需在 E2E 增加一条 cut 用例确认 |
| Editor 下无物体运动 | 动画/移动物体 AO 滞后 | KnownDifferences 明确；本轮非目标 |
| 只开 SSL 的 Scene View | SSGI/SSR 仍有同类问题 | 本轮不修；可在后续把 MV pass 抽到 `ScreenSpaceCommon` 共享，需要每渲染去重与 per-camera 状态 |
| XR | Editor pass 未按眼处理 | 仅在 `viewCount == 1` 时执行；Scene View 不启用 XR |

---

## 10. 完成定义（DoD）

1. T1–T3（+ 条件性 T4）代码合入，提交拆分符合 §6；
2. AC-1…AC-9 全部有证据并通过（AC-9 由仓库所有者确认）；
3. `Report.md` 包含：根因、参考实现差异、A/B 数据、T12 差异说明、限制；
4. `PLAN/ACCEPTANCE.md` 与 `01/HANDOFF.md` 已更新；
5. 目录移动到 `PLAN/Done/07-EditorMVPass`，状态行更新为 PASS/PASS*；
6. 工作区干净，`.rdc` 预算与设置恢复报告无异常。

---

## 11. 开放问题（实施时关闭）

1. 真实 Scene View 每次重绘是否推进 `Time.frameCount`（T0.2）——决定 T4 的定位。
2. GTAO 的 Debug 视图是否足以作为 E2E 指标（AO debug 输出为单通道，比对稳定性最高）。
3. T5 pipeline 的 renderer index 0 是否在所有三路径都含 GTAO（已确认 Forward/Forward+/Deferred 各有独立 pipeline，需在脚本中校验并断言）。
4. 是否需要把“无 ACD 相机”的判定抽成 `ScreenSpaceCommon` 的共享 helper（当前只给 GTAO 用，避免过早抽象）。