# 06 P0 冒烟：通用 validation player 与 binding probe（2026-11-17）

> 目的：在进入 P1–P4 修复/验收前，证明 P0-1（三渲染器通用 player + manifest）、P0-2（RenderDoc 分析链路）与 P0-3（binding probe）可以端到端运行，并冻结 `Report.json` / `BindingProbe_*.json` 的字段含义。
> 结论：**9/9 用例通过**。三渲染器（Forward / Forward+ / Deferred）各至少 1 个用例；GTAO / SSGI / SSR / 折射各有独立用例；每个用例产生 1 个 `.rdc`；binding probe 覆盖 35 个字段，0 处数值不匹配。
> 本轮未修改任何运行时代码或 shader；只新增测试资产、player 与证据。

## 1. 产物与运行方式

| 项 | 值 |
|---|---|
| Player | `Builds/SslValidationPlayer/SslValidationPlayer.exe`（D3D11、Development、1280×720、windowed；不入库） |
| 场景 | `Assets/Tests/ScreenSpaceLighting/Scenes/ScreenSpaceLightingValidationPlayer.unity` |
| 渲染器 | `Assets/Tests/ScreenSpaceLighting/Renderers/SslValidation{Forward,ForwardPlus,Deferred}{Renderer,Pipeline}.asset` |
| Profile | `Assets/Tests/ScreenSpaceLighting/Profiles/SslValidationProfile.asset` |
| Manifest | [SmokeManifest.json](SmokeManifest.json) |
| 构建入口 | `ScreenSpaceLightingValidationPlayerSetup.BuildPlayer`（`Tests/Screen Space Lighting/Build Validation Player`） |

```bash
# 构建（batchmode）
"/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" -batchmode -quit \
  -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingValidationPlayerSetup.BuildPlayer \
  -logFile "E:/GitHub/Glx.CustomURP/URPSample01/validation-build.log"

# 抓帧运行
"/c/Program Files/RenderDoc/renderdoccmd.exe" capture -w --opt-hook-children \
  --opt-disallow-fullscreen --opt-disallow-vsync \
  "E:/GitHub/Glx.CustomURP/URPSample01/Builds/SslValidationPlayer/SslValidationPlayer.exe" \
  -ssl-validation-manifest "…/validation/P0/SmokeManifest.json" \
  -ssl-validation-out "…/validation/P0/Smoke" \
  -force-d3d11 -screen-width 1280 -screen-height 720 -screen-fullscreen 0
```

**构建管线注意事项。** URP 会剥离构建时管线未使用的 shader 变体。若以 Forward 资产构建，Deferred 的 GBuffer/StencilDeferred 变体会被剥离，player 中 Deferred 只画天空与透明物。现在 `BuildPlayer` 以 `SslValidationDeferredPipeline.asset` 构建；Forward 与 Forward+ 是基础变体，运行时切换仍然正常（见 §3 的截图差异）。

## 2. 用例矩阵

`Report.json` 中每个用例记录渲染器、Volume/Feature 状态、帧号、抓帧、PNG、非有限像素数、binding probe 统计。

| 用例 | 渲染器 | GTAO | SSGI | SSR | 折射 | 抓帧 | Probe 覆盖 | 不匹配 |
|---|---|---|---|---|---|---|---|---|
| forward_off | Forward | off | off | off | off | ✅ | 0 | 0 |
| forward_gtao_half | Forward | half + temporal | off | off | off | ✅ | 14 | 0 |
| forward_gtao_full | Forward | full + temporal | off | off | off | ✅ | 14 | 0 |
| forward_ssgi_full | Forward | off | on | off | off | ✅ | 8 | 0 |
| forward_ssr_pbr | Forward | off | off | PBR full | off | ✅ | 7 | 0 |
| forward_refraction | Forward | off | off | off | on | ✅ | 6 | 0 |
| forwardplus_ssgi_full | Forward+ | off | on | off | off | ✅ | 8 | 0 |
| deferred_ssgi_full | Deferred | off | on | off | off | ✅ | 8 | 0 |
| deferred_ssr_pbr | Deferred | off | off | PBR full | off | ✅ | 7 | 0 |

所有用例：`captureSucceeded=true`、`nonFinitePixels=0`、`passed=true`。

## 3. GPU 证据：抓帧 dispatch census

由 `renderdoc-cli <rdc> events` 统计各 `ProfilingScope` 内的 dispatch 数量（`Flags=4`）。

| 用例 | 抓帧 | GTAO | SSGI | SSR | 折射绑定 | Probe |
|---|---|---|---|---|---|---|
| forward_off | `forward_off_frame12686.rdc` | 0 | 0 | 0 | 未出现 | 35 |
| forward_gtao_half | `forward_gtao_half_frame12704.rdc` | **4** | 0 | 0 | 未出现 | 35 |
| forward_gtao_full | `forward_gtao_full_frame12722.rdc` | **3** | 0 | 0 | 未出现 | 35 |
| forward_ssgi_full | `forward_ssgi_full_frame12740.rdc` | 0 | **9** | 0 | 未出现 | 35 |
| forward_ssr_pbr | `forward_ssr_pbr_frame12758.rdc` | 0 | 0 | **6** | 未出现 | 35 |
| forward_refraction | `forward_refraction_frame12776.rdc` | 0 | 0 | 0 | **marker 存在，0 dispatch** | 35 |
| forwardplus_ssgi_full | `forwardplus_ssgi_full_frame12797.rdc` | 0 | **9** | 0 | 未出现 | 35 |
| deferred_ssgi_full | `deferred_ssgi_full_frame12818.rdc` | 0 | **9** | 0 | 未出现 | 35 |
| deferred_ssr_pbr | `deferred_ssr_pbr_frame12836.rdc` | 0 | 0 | **6** | 未出现 | 35 |

- GTAO half-res：main + copy-history + spatial + temporal = 4；full-res：main + copy-history + temporal = 3（与 7.2 G1 的预期一致）。
- SSGI：trace/reproject（2）+ validate（1）+ temporal/copy（2）+ denoiser（3）+ upsample（1）= 9。
- SSR PBR：trace + reproject + 4 个降噪 kernel = 6。
- 折射是纯绑定 pass（`SetGlobal*`），没有 dispatch；其证据是 marker 存在 + binding probe 的 6 个全局面量回读。
- `forward_off` 只有 `SSL Binding Probe` marker，没有 Depth Pyramid / SSL / GTAO pass，关闭基线干净。

`.rdc` 总计约 300 MB，按 HANDOFF 7.5 预算不入库；本地保留在 `validation/P0/Smoke/`，由 `.gitignore` 排除。

## 4. Binding probe 结果

35 个字段与 HLSL 声明逐项对齐，覆盖：SSGI 8、SSR 7、GTAO 14、折射 6。`applicable` 由当帧 Volume/Feature 状态决定；`expected` 在发起异步读回的同一帧快照，`actual` 从 4×? 的 `RGBAFloat` UAV 读回。容差 `1e-5`（相对 `max(1,|expected|)`）。

代表值（三者均为 `expected == actual`，误差 0）：

| 用例 | 字段 | 值 |
|---|---|---|
| forward_gtao_half | `_GtaoBufferSize` | `(640, 360, 1/640, 1/360)` |
| forward_gtao_half | `_GtaoParams0` | `(1, 155.8846, 2, 6)`（half-res、FOV 修正、radius、stepCount） |
| forward_ssgi_full | `_SsgiScreenSize` | `(1280, 720, 1/1280, 1/720)` |
| forward_ssgi_full | `_SsgiDenoiserParams` | `(0.00160375, -1, 0, 0)`（60° / 720p 的像素展开角） |
| forward_ssgi_full | `_SsgiRayMarchingParams2` | `(1, 9, 1, 0)`（frameIndex、historyValidity） |
| forward_ssr_pbr | `_SsrPbrParams` | `(0, 0.5, 0, 0)` |
| forward_ssr_pbr | `_SsrPbrFlags` | `(91, 1, 0, 0)`（frameIndex、valid history） |
| forward_refraction | `_SslRefractionParams` | `(9, 10, 1, 0)`（mipCount、1/fade、intensity） |
| forward_refraction | `_SslRefractionProbeCount` | `(5, 0, 0, 0)`（4 个场景探针 + 1 个 fallback） |
| forward_refraction | `_SslRefractionProbeMin[0]` | `(2, 0, 4, 0)` |

**边界说明。** 生产 pass 通过 `SetComputeVectorParam` 把值绑定到各自的 `ComputeShader`，CPU 无法读取；probe 用同名同类型的 cbuffer 重新绑定 pass 当帧的 C# 值，因此它证明的是“C# 结构 → HLSL 名称/类型/打包”契约。生产 dispatch 的实际常量绑定仍由 `.rdc` 的 dispatch/PSO/shader hash 与像素输出作为第二证据（7.1 的三层证据要求）。折射字段是全局 uniform，probe 读到的是实际绑定值，属于端到端证据。

## 5. 图像差异（对照 forward_off）

| 用例 | 平均绝对差 | 变化像素 |
|---|---|---|
| forward_gtao_half | 0.90 | 5.38% |
| forward_gtao_full | 0.26 | 2.07% |
| forward_ssgi_full | 17.02 | 40.19% |
| forward_ssr_pbr | 0.15 | 0.38% |
| forward_refraction | 1.76 | 3.09% |
| forwardplus_ssgi_full | 1.78 | 10.24% |
| deferred_ssgi_full | 16.79 | 40.22% |
| deferred_ssr_pbr | 0.37 | 0.41% |

SSR 的视觉差异小是因为场景的金属球已由反射探针提供相近反射，且 `minSmoothness=0.9`；P3 会使用专门的镜面/粗糙度梯度场景做数值验收。P0 只要求“三个渲染器都能出图、效果能改变图像”。

## 6. 本轮发现并修复的 harness 问题

1. **JsonUtility 不能表示 null 嵌套对象**：manifest 中省略 `volume.ssgi` 时，反序列化仍得到默认实例（`enabled=true`），导致所有效果在每例都开启。修复：三个 Volume 设置类的 `enabled` 默认值改为 `false`，显式 opt-in。
2. **SSR pass 没有 `m_ShaderVariables` 字段**：SSR 的常量在每次 dispatch 时局部构造。修复：probe 通过反射调用 `CreateShaderVariables(ref CameraData, …)` 重建当帧值。
3. **历史字段是属性**：`width` / `height` / `colorPyramidMipCount` 是属性而非字段，读取 0 导致 SSR/折射 probe 覆盖为空。修复：统一的属性/字段读取器。
4. **异步读回期间 expected 被后续帧覆盖**：帧相关字段出现 1–3 帧“假滞后”。修复：在发起读回的同一帧快照 expected/applicable。
5. **同步 `ReadPixels` 在管线切换后阻塞主线程**（渲染线程创建的 RT 被 Unity 当作临时纹理回收，日志出现 `temporary render texture not found`）。修复：RT 在 `Create()` 创建、`HideAndDontSave`，读回改为 `AsyncGPUReadback`；切换后仍会有一条无害 warning，但不再挂起。

## 7. 已知限制与后续

1. **Deferred 构建变体**：player 必须以 Deferred 资产构建，否则 deferred GBuffer 变体被剥离（见 §1）。已固化在 `BuildPlayer`。
2. **Probe 的观测边界**：见 §4 末段；F-02（SSR 降噪器像素展开角）需要 P3-1 的 pass 级证据，不能仅靠 probe。
3. **SSR 视觉差异小**：P3-5 需要专门的反射场景。
4. **GTAO 的 frameIndex**：probe 在 pass 之后重算，按 `effectFrameCount - 1` 还原上传帧；RDC 作为最终判据。
5. **`.rdc` 不入库**：本轮 9 个抓帧共约 300 MB，超出单轮预算；提交的 JSON/PNG 足以复核指标，抓帧按需在本地重放。
6. **ProjectSettings 迁移**：Unity 2022.3.62f1 打开了工程并把 `GraphicsSettings/QualitySettings/URP-Balanced/Performant` 重新序列化（与本轮改动无关，未提交）；P6-2 会统一恢复/记录。

## 8. 证据文件

```text
validation/P0/
  SmokeManifest.json
  SmokeReport.md
  Smoke/
    Report.json
    Player.log
    BindingProbe_<case>.json   ×9
    <case>.png                 ×9
    <case>_frame*.rdc          ×9（本地，不入库）
```