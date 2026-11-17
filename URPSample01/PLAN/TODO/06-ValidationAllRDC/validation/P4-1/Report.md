# P4-1 折射 keyword / 绑定泄漏（F-05，2026-11-17）

> 任务：P4-1。目标：证明被 `ScreenSpaceCapability` 拒绝的相机（Preview / Reflection / Overlay / 零尺寸 / XR 布局不符）不会继承上一相机的 `_SCREEN_SPACE_REFRACTION` keyword 与材质侧绑定，并修复 `ClearRenderingState` 缺失的折射重置。
> 结论：**修复生效，三渲染器 3/3 用例通过**。注入的泄漏态（keyword、`_SslRefractionParams`、`_SslRefractionProbeCount`、颜色金字塔绑定）在修复前 4 个通道全部以 255 到达 Reflection 相机；修复后同一注入全部为 0，而正常 A→B 流程的对照在修复前后都保持 0。全量 EditMode **455/455**。

## 1. 缺陷与修复

F-05：`ScriptableRenderer.ClearRenderingState` 每相机清理 GI/SSR/TransparentSSR keyword，但没有折射：

- `_SCREEN_SPACE_REFRACTION` 只在折射 pass 的 `OnCameraCleanup` 中关闭；被拒绝的相机根本不入队该 pass，因此只有每相机起点清理能兜住残留。
- `_SslRefractionParams`、`_SslRefractionProbeCount`、`_SslRefractionColorPyramid`、`_SslRefractionDepthTexture` 同样只在 pass 内绑定/清理，残留时全局纹理可能指向上一相机已释放的 RTHandle。

修复（[ScriptableRenderer.cs](../../../../../Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/ScriptableRenderer.cs)）：

```csharp
// ClearRenderingState
cmd.DisableShaderKeyword(ShaderKeywordStrings.RefractionKeyword);
cmd.SetGlobalVector(s_RefractionParamsID, Vector4.zero);
cmd.SetGlobalInt(s_RefractionProbeCountID, 0);
cmd.SetGlobalTexture(s_RefractionColorPyramidID, Texture2D.blackTexture);
cmd.SetGlobalTexture(s_RefractionDepthTextureID, Texture2D.blackTexture);
```

属性 ID 以 core 私有 `Shader.PropertyToID` 声明（与折射 pass 的 `ScreenSpaceLightingShaderIDs` 同名），避免 URP core 引用插件类型；`PluginIsolationTests.CoreRuntimeSourcesDoNotReferencePluginTypes` 保持通过。折射 pass 的 `DisableMaterialBinding` 增加注释指向同一清理列表，两处保持同步。

回归守卫：[ScreenSpaceLightingRefractionTests.PerCameraClearResetsTheRefractionMaterialBinding](../../../../../Packages/com.unity.render-pipelines.universal@14.0.12/Tests/Editor/RendererFeatures/ScreenSpaceLighting/ScreenSpaceLightingRefractionTests.cs) 断言 `ClearRenderingState` 关闭折射 keyword 并重置 4 个属性 ID。

## 2. 验证装置

Player 新增专用泄漏 rig（`BuildLeakRig`），停放在世界下方 `(0, -200, 0)`，不进入主相机视野：

| 元素 | 说明 |
|---|---|
| 泄漏表面 | 内置 Quad（2×2，位于相机前 4 m），材质 `SslValidationRefractionLeakProbe.mat` |
| 泄漏相机 | `CameraType.Reflection`、`enabled=false`、2D 64×64 ARGB32 线性 RT，仅由用例显式 `Render()` |
| 表面 shader | `Hidden/Screen Space Lighting/Refraction Leak Probe`，`#pragma multi_compile_fragment _ _SCREEN_SPACE_REFRACTION` |

表面逐通道输出（`_LeakProbeMode` 选择模式），alpha 恒为 0.5 作为“表面已绘制”标记：

| 通道 | 模式 0 | 模式 1 |
|---|---|---|
| R | `_SCREEN_SPACE_REFRACTION` 变体被选中 | `_SslRefractionColorPyramid` 采样非黑 |
| G | `_SslRefractionParams` 非零 | 0 |
| B | `_SslRefractionProbeCount` 非零 | 0 |
| A | 0.5（表面像素） | 0.5（表面像素） |

用例流程（`refractionLeakCheck`）：

1. 主相机按 manifest 以折射开启预热（`A 相机`），`OnCameraCleanup` 已清理。
2. 对照：渲染 Reflection 相机，期望 `(0,0,0)` + alpha 0.5；证明表面可见且正常流程无残留。
3. 注入：用 `CommandBuffer` + `Graphics.ExecuteCommandBuffer` 写入 keyword/参数/计数/白色哨兵纹理，落入与上一相机命令相同的设备级全局状态；紧接着同步 `Camera.Render()`。
4. 回读表面中心像素并把 4 张 64×64 面图写入 `Player/`、`Before/`；最后用命令缓冲区恢复黑色/清零全局。

注入通道与真实泄漏一致（命令缓冲区的设备级全局态），而不是 `Shader.SetGlobal*` 的 CPU 侧副本——后者在 URP 逐相机命令执行后不会覆盖渲染线程状态，第一版装置因此读到错误的 0，已废弃。

## 3. 结果

`P4Manifest.json`：Forward / Forward+ / Deferred 各一例，折射开启、预热 12 帧。

| 渲染器 | 对照（前） | 注入（前） | 对照（后） | 注入（后） | 结果 |
|---|---|---|---|---|---|
| Forward | 可见，0/0/0/0 | 可见，**255/255/255/255** | 可见，0/0/0/0 | 可见，**0/0/0/0** | ✅ |
| Forward+ | 可见，0/0/0/0 | 可见，**255/255/255/255** | 可见，0/0/0/0 | 可见，**0/0/0/0** | ✅ |
| Deferred | 可见，0/0/0/0 | 可见，**255/255/255/255** | 可见，0/0/0/0 | 可见，**0/0/0/0** | ✅ |

通道顺序：keyword / parameters / probeCount / texture；255 表示 1.0 的 8-bit 值，0 表示清零。

- 修复前注入泄漏 = 4 通道全 255，说明 keyword、参数、探针计数与颜色金字塔绑定都会到达被拒绝的 Reflection 相机。
- 修复后同一注入全部清零，且表面仍被绘制（alpha 0.5），排除“没画到”造成的假通过。
- 正常 A→B 对照在修复前后都为 0：per-pass 的 `OnCameraCleanup` 在正常结束的相机上已经清理；真实风险来自清理未执行的残余状态（中断的相机渲染、后续新增的拒绝路径），注入即模拟该前提。

图像证据：

| 文件 | 内容 |
|---|---|
| [Before/refraction_leak_forward_leak_injected_state.png](Before/refraction_leak_forward_leak_injected_state.png) | 修复前，注入态 4 通道全白 |
| [Before/refraction_leak_forward_leak_control_state.png](Before/refraction_leak_forward_leak_control_state.png) | 修复前对照，全黑但 alpha 0.5 |
| [Player/refraction_leak_forward_leak_injected_state.png](Player/refraction_leak_forward_leak_injected_state.png) | 修复后，注入态清零 |
| [Player/refraction_leak_forward_leak_injected_texture.png](Player/refraction_leak_forward_leak_injected_texture.png) | 修复后，颜色金字塔绑定清零 |

三渲染器同名文件分别在 `Before/`、`Player/` 下；`*_leak_injected_texture.png` 为模式 1（纹理通道）。

## 4. 复现

```bash
# 构建（D3D11、Development、1280×720、Deferred 资产）
"/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode -quit \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingValidationPlayerSetup.BuildPlayer \
  -logFile "<abs>/validation/P4-1/BuildPlayer.log"

# 泄漏用例
"Builds/SslValidationPlayer/SslValidationPlayer.exe" \
  -ssl-validation-manifest "<abs>/validation/P4-1/P4Manifest.json" \
  -ssl-validation-out "<abs>/validation/P4-1/Player" \
  -force-d3d11 -screen-width 1280 -screen-height 720 -screen-fullscreen 0

python analyze.py   # Before/Report.json × Player/Report.json → Metrics.json
```

`Before/` 由未修复代码构建的同版本 player 产生（与 `Player/` 使用同一 manifest、同一场景）。

## 5. 回归与构建

| 项 | 结果 |
|---|---|
| Player 构建 | Succeeded，0 error（[BuildSummary.json](BuildSummary.json)，日志本地 `BuildPlayer.log` 不入库） |
| 全量 EditMode | **455/455**（[EditModeFull.xml](EditModeFull.xml)） |
| 新增守卫 | `PerCameraClearResetsTheRefractionMaterialBinding`；`PluginIsolationTests` 继续通过 |
| 指标 | [Metrics.json](Metrics.json)（`analyze.py` 重算，`allPassed=true`） |

## 6. 覆盖边界

- **Preview / Overlay / 零尺寸 / XR 布局**：player 无法创建 Preview 相机，本用例以 Reflection 相机验证“能力拒绝 + 无 pass”路径；其余相机类型的拒绝规则已有 `ScreenSpaceCapabilityTests` 覆盖（`PreviewCameraIsRejected` / `OverlayCameraIsRejected` / `EmptyTargetSizeIsRejected`）。
- **其余 4 个贡献 keyword**（GI/SSR/TransparentSSR/Occlusion）：`ClearRenderingState` 早已逐相机关闭；本用例只对折射做注入对比。若 P4-2 执行完整 keyword 矩阵，可复用本 rig，把表面 shader 的 `multi_compile` 扩展到 5 个 keyword 并按位编码。
- **纹理绑定通道**：以白色哨兵 RT 模拟残留纹理；修复后表面采样到黑纹理。真实泄漏中该句柄可能已被释放，shader 是否采样取决于 keyword，故不构造释放句柄的崩溃场景。
