# P4-2 keyword 泄漏与多相机矩阵（X1，2026-11-17）

> 任务：P4-2。目标：X1——A 相机开启全部效果后，B 相机为 Preview / Reflection / Overlay / 缺资源时，5 个贡献 keyword 全关，且没有上一相机的结果纹理（RTHandle）残留。P4-1 已覆盖折射 keyword 与绑定；本轮补齐 GI / SSR / TransparentSSR / Occlusion 四个 keyword 的注入矩阵，并新增 Overlay 与缺资源两条拒收路径。
> 结论：**修复生效，三渲染器 3/3 用例通过**。5 个 keyword 在拒收相机上由既有逐相机清理保持全 0（本轮的回归矩阵）；新发现的材料绑定泄漏（GI/SSR/TransparentSSR 两张结果纹理与参数、GTAO 遮挡纹理）在修复前为 **44/60**（普通/注入态），修复后全部为 0。正常相机（正对照）仍观测到 22/25 keyword、44/63 纹理、12/19 参数，证明掩码没有卡在 0。全量 EditMode **456/456**。

## 1. 缺陷与修复

X1 的 keyword 部分在 `ScriptableRenderer.ClearRenderingState` 中早已覆盖：`_SCREEN_SPACE_OCCLUSION`（GTAO）、`_SCREEN_SPACE_GI`、`_SCREEN_SPACE_SSR`、`_SCREEN_SPACE_TRANSPARENT_SSR` 在每相机起点关闭，`_SCREEN_SPACE_REFRACTION` 由 P4-1 补齐。本轮注入矩阵确认 5 个 keyword 在三种拒收路径下都为 0。

但 **材料绑定**只剩折射被 P4-1 重置。上一相机结束后仍留在全局状态的 RTHandle：

| 全局属性 | 所有者 | 修复前行为 |
|---|---|---|
| `_ScreenSpaceGiTexture` / `_ScreenSpaceGiParams` | SSGI | Setup 每帧绑定结果纹理，Cleanup 只清参数；纹理指向该相机 history 的 RTHandle |
| `_ScreenSpaceSsrTexture` / `_ScreenSpaceSsrParams` | SSR | 同上 |
| `_ScreenSpaceTransparentSsrTexture` / `_ScreenSpaceTransparentSsrParams` | 透明 SSR | pass 清理只覆盖正常结束的相机 |
| `_ScreenSpaceOcclusionTexture` | GTAO | 只由 pass 绑定；没有逐相机重置 |

被 `ScreenSpaceCapability` 拒绝的相机不入队任何 SSL/GTAO pass，因此这些句柄会跨相机残留；若上一相机的 history 已被释放，绑定还可能指向失效句柄。

修复（[ScriptableRenderer.cs](../../../../../Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/ScriptableRenderer.cs)）在 `ClearRenderingState` 中补上 7 条命令：

```csharp
cmd.SetGlobalTexture(s_SsgiTextureID, Texture2D.blackTexture);
cmd.SetGlobalVector(s_SsgiParamsID, Vector4.zero);
cmd.SetGlobalTexture(s_SsrTextureID, Texture2D.blackTexture);
cmd.SetGlobalVector(s_SsrParamsID, Vector4.zero);
cmd.SetGlobalTexture(s_TransparentSsrTextureID, Texture2D.blackTexture);
cmd.SetGlobalVector(s_TransparentSsrParamsID, Vector4.zero);
cmd.SetGlobalTexture(s_ScreenSpaceOcclusionTextureID, Texture2D.blackTexture);
```

属性 ID 以 core 私有 `Shader.PropertyToID` 声明（与插件 `ScreenSpaceLightingShaderIDs`、GTAO 的 ID 同名），`PluginIsolationTests` 保持通过。回退值刻意选用 pass 停止时使用的同一 `Texture2D.blackTexture` / `Vector4.zero`，保证“拒收相机”和“效果关闭的相机”读到同一状态。

回归守卫：[ScreenSpaceLightingRendererFeatureTests.PerCameraClearResetsTheMaterialBindings](../../../../../Packages/com.unity.render-pipelines.universal@14.0.12/Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingRendererFeatureTests.cs) 断言 `ClearRenderingState` 关闭 4 个 keyword 并重置上述 6 个 ID 与遮挡纹理。

## 2. 验证装置

在 P4-1 的泄漏 rig 上泛化。rig 停放在世界下方 `(0, -200, 0)`，与主场景隔离：

| 元素 | 说明 |
|---|---|
| 反射表面 | 两层表面，每层含一个不透明 Quad（屏幕中央）与一个透明队列 Quad（右侧 `x=1.6`）；同一相机看到的两块区域互不重叠 |
| 基础表面层 | layer 30，供 Reflection 相机与 Base 相机观察，alpha marker = 0.5 |
| Overlay 表面层 | layer 31，供 Overlay 相机观察，alpha marker = 0.75 |
| 反射相机 | `CameraType.Reflection`、禁用、手动 `Render()`，64×64 ARGB32 线性 RT |
| Base 相机 | `CameraType.Game`、禁用、culling mask = layer 30；Overlay 相机挂在它的 camera stack 上 |
| Overlay 相机 | `CameraRenderType.Overlay`、culling mask = layer 31；Base 的 `Camera.Render()` 会渲染整个栈 |
| 表面 shader | 5 个 keyword 的 `multi_compile_fragment`，逐模式输出 R 通道掩码，A = marker |

掩码（R 通道 = 掩码 / 255，8-bit 线性目标可逐位读回）：

| 模式 | 通道 | 位定义 |
|---|---|---|
| 0 | keyword | 1 折射、2 GI、4 SSR、8 透明 SSR、16 遮挡 |
| 1 | 纹理绑定 | 1 折射颜色金字塔、2 折射深度、4 GI、8 SSR、16 透明 SSR、32 遮挡 |
| 2 | uniform | 1 折射参数、2 折射探针数、4 GI、8 SSR、16 透明 SSR |

“是否绑定”按纹理尺寸判断而非内容：probe 场景是一块平面，结果纹理内容可能全黑；`Texture2D.blackTexture` 实测为 **4×4**，因此装置用 `blackTextureSize + 1 = 5` 作为阈值，注入用的白色哨兵取 `max(8, 2×阈值)`。未绑定/黑回退读 0，相机结果纹理（≥32×32）读 1。阈值随报告写出（`blackTextureSize` / `bindThreshold`）。

用例流程（`keywordLeakCheck`）：

1. **control**：Roadmap 正常结束后直接渲染 Reflection 相机（无注入）。正常流程应为 0；P4-1 修复后折射也不例外。
2. **live（正对照）**：渲染 Base 相机 4 帧预热 history，读三个掩码。不透明表面必须看到 GI/SSR/遮挡 keyword 与 GI/SSR/遮挡纹理；透明表面必须看到折射/透明 SSR keyword 与对应纹理/参数。
3. **missing（缺资源）**：把 SSL feature 的 `depthPyramidCompute` 置空（`hasRuntimeResources` 的提前返回分支），同时临时关闭 GTAO 排除遮挡 keyword 干扰，再渲染 Base 相机；最后恢复资源与 GTAO 设置。
4. **overlay**：启用 Overlay 相机后渲染 Base 相机；overlay 的逐相机清理应抹掉 Base 刚写入的状态，alpha 191 证明读回像素来自 overlay draw。Deferred 的 `UniversalRenderer.SupportedCameraStackingTypes()` 只含 Base，Overlay 相机不会被渲染，因此该分支在 Deferred 不存在（报告中 `overlaySupported=false`）。
5. **stale**：用命令缓冲区注入 5 个 keyword、6 条纹理绑定与 5 组参数（设备级全局态），紧接着同步渲染 Reflection 相机；结束后恢复黑色/清零。

## 3. 结果

`P4Manifest.json`：Forward / Forward+ / Deferred 各一例，SSGI + SSR（含透明）+ 折射 + GTAO 全开，预热 12 帧。

### 3.1 修复前（Before/Report.json）

| 状态 | keyword（不透明/透明） | 纹理绑定 | uniform |
|---|---|---|---|
| control | 0 / 0 | **44 / 44** | 0 / 0 |
| live | 22 / 25 | 44 / 63 | 12 / 19 |
| missing | 0 / 0 | **44 / 44** | 0 / 0 |
| overlay（Forward/Forward+） | 0 / 0 | **44 / 44** | 0 / 0 |
| stale | 0 / 0 | **60 / 60** | **28 / 28** |

三渲染器数值一致（Deferred 的 overlay 行不适用，见第 6 节）。逐位解读：

- 44 = 4 | 8 | 32：GI、SSR、遮挡三张结果纹理跨相机残留。遮挡纹理由 GTAO 在不透明阶段绑定，之后没有逐相机清理。
- 60 = 4 | 8 | 16 | 32：注入态再加透明 SSR 纹理；折射的两张纹理与参数已由 P4-1 的逐相机清理抹掉，因此不是 63。
- 28 = 4 | 8 | 16：注入的 GI/SSR/透明 SSR 参数；折射参数同样已被 P4-1 清零。
- keyword 全 0：既有清理（含 P4-1）已覆盖，本表是回归证据。

### 3.2 修复后（Player/Report.json）

| 状态 | keyword | 纹理绑定 | uniform | 结果 |
|---|---|---|---|---|
| control | 0 / 0 | 0 / 0 | 0 / 0 | ✅ |
| live（正对照） | 22 / 25 | 44 / 63 | 12 / 19 | ✅ |
| missing | 0 / 0 | 0 / 0 | 0 / 0 | ✅ |
| overlay（Forward/Forward+） | 0 / 0 | 0 / 0 | 0 / 0 | ✅ |
| overlay（Deferred） | 不支持相机栈，不渲染 | — | — | 记录为不适用 |
| stale | 0 / 0 | 0 / 0 | 0 / 0 | ✅ |

正对照与修复前完全一致：不透明阶段 keyword 22 = GI|SSR|遮挡，透明阶段 25 = 折射|透明 SSR|遮挡（GTAO 的 keyword 在透明阶段仍然开启，清理发生在相机结束）；纹理与参数同理。判断任一掩码为 0 之前，装置先验证 alpha marker（Base 128 / Overlay 191）与正对照，排除“没画到”或“探针失效”造成的假通过。

图像证据：`Before/` 与 `Player/` 各有 45 张 64×64 面图（3 相机 × 5 状态 × 3 模式，文件名 `<case>_leak_<state>_<keywords|bindings|uniforms>.png`）。例如：

| 文件 | 内容 |
|---|---|
| [Before/keyword_leak_forward_leak_stale_bindings.png](Before/keyword_leak_forward_leak_stale_bindings.png) | 修复前注入态绑定掩码 60 |
| [Before/keyword_leak_forward_leak_control_bindings.png](Before/keyword_leak_forward_leak_control_bindings.png) | 修复前对照残留 44 |
| [Player/keyword_leak_forward_leak_stale_bindings.png](Player/keyword_leak_forward_leak_stale_bindings.png) | 修复后清零 |
| [Player/keyword_leak_forward_leak_overlay_keywords.png](Player/keyword_leak_forward_leak_overlay_keywords.png) | Overlay draw 后 keyword 全 0（alpha 191） |

## 4. 复现

```bash
# 构建（D3D11、Development、1280×720；构建期把三套 pipeline 停放到 quality levels）
"/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode -quit \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingValidationPlayerSetup.BuildPlayer \
  -logFile "<abs>/validation/P4-2/BuildPlayer.log"

# 矩阵
"Builds/SslValidationPlayer/SslValidationPlayer.exe" \
  -ssl-validation-manifest "<abs>/validation/P4-2/P4Manifest.json" \
  -ssl-validation-out "<abs>/validation/P4-2/Player" \
  -force-d3d11 -screen-width 1280 -screen-height 720 -screen-fullscreen 0

python analyze.py   # Before/Report.json × Player/Report.json → Metrics.json
```

`Before/` 由未修复代码构建的同版本 player 产生（在 P4-1 修复之上，仅回退本轮 `ClearRenderingState` 的新增重置），与 `Player/` 使用同一 manifest、同一场景。`Before/` 运行会按预期以退出码 1 结束（用例断言失败），`Report.json` 照常写出。

## 5. 回归与构建

| 项 | 结果 |
|---|---|
| Player 构建 | Succeeded，0 error（[BuildSummary.json](BuildSummary.json)；日志本地 `BuildPlayer*.log` 不入库） |
| 全量 EditMode | **456/456**（[EditModeFull.xml](EditModeFull.xml)，新增 `PerCameraClearResetsTheMaterialBindings`） |
| 指标 | [Metrics.json](Metrics.json)（`analyze.py` 重算，`allPassed=true`） |
| Plugin 隔离 | `PluginIsolationTests.CoreRuntimeSourcesDoNotReferencePluginTypes` 继续通过 |

## 6. 覆盖边界

- **Preview / 零尺寸 / XR 布局**：player 无法创建 Preview 相机；Reflection 相机覆盖 `CameraUnsupported` 的拒收路径，其余规则由 `ScreenSpaceCapabilityTests` 在 CPU 层覆盖。零尺寸与 XR 布局不在本轮 GPU 举证范围。
- **Deferred + Overlay**：`UniversalRenderer.SupportedCameraStackingTypes()` 在 Deferred 只返回 Base，Overlay 相机被 URP 跳过（player 日志有对应 warning）。因此“Overlay 相机被 SSL 拒绝”的分支在 Deferred 不存在，报告写 `overlaySupported=false`。
- **缺资源分支**：只置空 `depthPyramidCompute`；`hasRuntimeResources` 同时要求 `colorPyramidShader`，两者共用同一提前返回，未重复跑第二种组合。
- **keyword 掩码的语义**：R 通道记录 draw 时选中的变体（全局 keyword），与 P4-1 相同；不能区分 keyword 由谁开启，只能证明拒收相机上确实关闭。
- **遮挡参数**：`_AmbientOcclusionParam` 在 P4 之前已有逐相机清理；本轮只补遮挡纹理绑定。URP 自带 SSAO 的同一全局不在本轮范围。
