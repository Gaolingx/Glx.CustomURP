# P3-4 SSR 生效链 GPU 验收（两种算法/全半分辨率/透明）证据（2026-11-17）

> 任务：P3-4。目标：证明 SSR 的 Approximation、PBR（全分辨率/半分辨率）与透明 opt-in 三条链路在不同渲染器上都实际执行了预期数量的 dispatch，且写出的反射纹理被材质消费。
> 结论：**通过**。12 个用例（Forward / Forward+ / Deferred × 4 种配置）全部通过 `Report.json` 的整数断言；3 个 RenderDoc 抓帧覆盖 Approx 全分辨率、PBR 全分辨率与 PBR 半分辨率+透明，shader 变体、cbuffer 布局与资源读写链均与代码一致。

## 1. 用例与运行

| 项 | 值 |
|---|---|
| Manifest | [P3Manifest.json](P3Manifest.json) |
| Player | `Builds/SslValidationPlayer/SslValidationPlayer.exe`（D3D11、Development、1280×720，以 Deferred 资产构建） |
| 运行 | `renderdoccmd capture -w --opt-hook-children … -ssl-validation-manifest …P3-SSR/P3Manifest.json -ssl-validation-out …P3-SSR/Player` |
| CPU 断言 | `Report.json` 的 `ssrDispatch` / `transparentSsr`（DEV 静态快照，release 为空） |
| GPU 证据 | [P3Census.json](P3Census.json)、`Player/*.rdc` |

12 个用例 = 3 个渲染器 × {Approx 全分辨率, PBR 全分辨率, PBR 半分辨率, PBR 半分辨率+透明}。每个用例预热 10 帧后读上一帧快照；计数、组维度、keyword、history 槽位与重置次数逐项与 manifest 推导的期望值比较，任何不符写入 `issues`。`Report.json`：`passed=true`、`completed=true`、12/12，`nonFinitePixels=0`。最终 EditMode 套件 449/449 通过。

## 2. CPU：dispatch 矩阵

期望值由分辨率推导：trace 目标为半分辨率时 `ceil(w/2) × ceil(h/2)`，screen 组恒为 `ceil(w/8) × ceil(h/8)`；Approx 为 trace+reproject 两次，PBR 全分辨率 6 次，PBR 半分辨率再加 upsample 共 7 次；透明 SSR 独立 trace+reproject 2 次。

| 用例 | trace 尺寸 | trace/screen 组 | t/r/tmp/cp/h/ups | keyword A/H/BN/XR | history r/w | 结果 |
|---|---|---|---|---|---|---|
| `forward_ssr_approx_full` | 1280×720 | 160×90 | 1/1/0/0/0/0 | T/F/T/F | — | ✅ |
| `forward_ssr_pbr_full` | 1280×720 | 160×90 | 1/1/1/1/1/0 | F/F/T/F | 1/0 | ✅ |
| `forward_ssr_pbr_half` | 640×360 | 80×45 / 160×90 | 1/1/1/1/1/1 | F/T/T/F | 1/0 | ✅ |
| `forward_ssr_pbr_half_transparent` | 640×360 | 80×45 / 160×90 | 1/1/1/1/1/1 | F/T/T/F | 1/0 | ✅ |
| `forwardplus_ssr_approx_full` | 1280×720 | 160×90 | 1/1/0/0/0/0 | T/F/T/F | — | ✅ |
| `forwardplus_ssr_pbr_full` | 1280×720 | 160×90 | 1/1/1/1/1/0 | F/F/T/F | 1/0 | ✅ |
| `forwardplus_ssr_pbr_half` | 640×360 | 80×45 / 160×90 | 1/1/1/1/1/1 | F/T/T/F | 1/0 | ✅ |
| `forwardplus_ssr_pbr_half_transparent` | 640×360 | 80×45 / 160×90 | 1/1/1/1/1/1 | F/T/T/F | 1/0 | ✅ |
| `deferred_ssr_approx_full` | 1280×720 | 160×90 | 1/1/0/0/0/0 | T/F/T/F | — | ✅ |
| `deferred_ssr_pbr_full` | 1280×720 | 160×90 | 1/1/1/1/1/0 | F/F/T/F | 1/0 | ✅ |
| `deferred_ssr_pbr_half` | 640×360 | 80×45 / 160×90 | 1/1/1/1/1/1 | F/T/T/F | 1/0 | ✅ |
| `deferred_ssr_pbr_half_transparent` | 640×360 | 80×45 / 160×90 | 1/1/1/1/1/1 | F/T/T/F | 1/0 | ✅ |

- `t/r/tmp/cp/h/ups`：trace / reproject / temporal / copyHistory / bilateral（H+V 各 1）/ upsample。
- keyword：A=`SSR_APPROX`、H=`SSR_HALF_RES`、BN=`SSR_BLUE_NOISE`、XR=`SSR_XR_ARRAY`；`normalsOctKeyword` 在所有用例均为 false（LDR 法线）。
- 透明用例另记 `transparentSsr`：`traceDispatches=1`、`reprojectDispatches=1`、组 `160×90`（`Screen.width/8 × height/8`），全分辨率透明 trace 与半分辨率不透明 trace 共存。

**history**：PBR 用例 `historyValid=true`，读写槽恒为 `1/0`，8 帧窗口内稳定交替。每次切换算法/分辨率/渲染器后 `historyResetCount` 递增（Forward 10→15→19，Forward+ 13→18→22，Deferred 13→18→22），满足“配置切换必须清历史”的断言；Approx 不记录 history 槽位。

**像素展开角**：PBR 全分辨率 `0.0016037507`、半分辨率 `0.0032075015`，与由 trace 尺寸推导的期望值一致（P3-1 修复延续）。

## 3. GPU：抓帧与变体

3 个代表抓帧（同一最终构建）：

| 用例 | 文件 | SSR marker | dispatch | kernel（事件号 / shader） |
|---|---|---|---|---|
| Forward+ Approx 全分辨率 | `forwardplus_ssr_approx_full_frame14367.rdc` | 330–373 | 2 | trace(348, `9777`), reproject(370, `9780`) |
| Deferred PBR 全分辨率 | `deferred_ssr_pbr_full_frame14437.rdc` | 343–472 | 6 | trace(364, `9829`), reproject(386, `9831`), temporal(413, `9833`), copyHistory(427, `9836`), bilateralH(448, `9838`), bilateralV(469, `9841`) |
| Forward PBR 半分辨率+透明 | `forward_ssr_pbr_half_transparent_frame14349.rdc` | 324–468 | 7 | trace(345, `9886`), reproject(367, `9888`), temporalHalfRes(394, `9889`), copyHistory(408, `9836`), bilateralH_HR(429, `9890`), bilateralV_HR(450, `9891`), upsample(465, `9892`) |
| 同上，透明 pass | 同上 | 1083–1126 | 2 | trace(1101, `9925`), reproject(1123, `9928`) |

变体隔离（cbuffer1 布局）：

| 路径 | trace shader | cbuffer1 | SRV/UAV |
|---|---|---|---|
| Approx 全分辨率 | `9777` | 112 B / 7 vars | 4 / 1 |
| PBR 全分辨率 | `9829` | **144 B / 9 vars** | 7 / 1 |
| PBR 半分辨率 | `9886` | 144 B / 9 vars | 7 / 1 |
| 透明（同帧） | `9925` | **112 B / 7 vars** | 4 / 1 |

即 Approximation 与 PBR、透明与不透明在同一帧各自绑定编译期正确的变体（P3-3 的 F-08 修复在最终构建中保持）。Approx marker 只有 2 个 dispatch，没有 temporal/copyHistory/bilateral/upsample，与 CPU 计数一致。

**资源链（半分辨率+透明抓帧）**：

| 资源 | 写入 | 读取 |
|---|---|---|
| `SslSsrPbrOriginal_640x360` (`9851`) | reproject 367、bilateralV 450 | temporal 394、upsample 465 |
| `SslSsrOutput_1280x720` (`9756`) | upsample 465 | 不透明 draw 509/515/523/587/621/627 |
| `SslSsrTransparentOutput_1280x720` (`9913`) | 透明 reproject 1123 | 透明 draw 1200 |

纹理统计（事件 1123）：`SslSsrOutput` `(0,0,0,0)..(4.94,4.30,3.42,1.0)`；`SslSsrTransparentOutput` `(0,0,0,0)..(0.434,0.465,0.533,1.0)`；透明 hitpoint uv `x∈[0.372,99]`、`y∈[0,0.308]`。透明输出非零，说明透明链路在最终构建中产生了真实反射。

## 4. 透明面板标定修正

首次运行时透明链路 dispatch 正常，但 `SslSsrTransparentOutput` 全为 0、hitpoint 全为 miss：验证场景的透明面板正对相机，镜面反射方向指向相机背后的空域，屏幕上没有可命中的不透明像素。这是**验证场景几何问题**，不是 SSR 缺陷。

修正：`BuildRig` 将透明面板绕 X 轴旋转 `-45°`（`ScreenSpaceLightingValidationPlayerVerification.cs` 的 `BuildRig`），使反射光线垂直落到面板下方可见的地面上。修正后同一抓帧的透明输出/hitpoint 非零；P3-5 的 `transparentWeight` 从 0 提升到面板区域的 44.9%（见 [P3-5/Report.md](../P3-5/Report.md)）。修正只影响验证场景，不涉及运行时代码。

## 5. 验收映射

| 标准 | 结果 |
|---|---|
| Approx 全分辨率链路在三个渲染器生效 | ✅ 2 dispatch；抓帧 marker 330–373 仅 trace+reproject |
| PBR 全分辨率链路 6 个 dispatch | ✅ 三个渲染器 1/1/1/1/1/1；Deferred 抓帧 6 个 kernel |
| PBR 半分辨率链路 7 个 dispatch + upsample | ✅ trace 640×360/80×45；抓帧含 upsample |
| 透明 opt-in 独立 2 个 dispatch | ✅ 全分辨率 trace+reproject 160×90，三渲染器一致 |
| 反射纹理被材质消费 | ✅ upsample 写 `9756`，6 个不透明 draw 读；透明 reproject 写 `9913`，透明 draw 1200 读 |
| 算法变体不串用 | ✅ Approx/透明 trace 112 B/7 vars，PBR trace 144 B/9 vars |
| history 槽位交替与切换重置 | ✅ r/w 恒 1/0，切换后 reset 计数递增 |
| 输出非有限像素 | ✅ 12 用例 `nonFinitePixels=0` |

## 6. 产物与预算

| 文件 | SHA-256 | 字节 |
|---|---|---|
| `Player/forwardplus_ssr_approx_full_frame14367.rdc` | `81eed8e3…4df8a` | 21,385,484 |
| `Player/deferred_ssr_pbr_full_frame14437.rdc` | `8be61fdf…64efc` | 37,309,259 |
| `Player/forward_ssr_pbr_half_transparent_frame14349.rdc` | `52e70ba1…0b5381` | 28,751,930 |

连同 P3-2（2 个）与 P3-3（1 个），当前 `.rdc` 共 6 个、140,263,245 B（约 133.8 MiB），在 150 MB / 12 个预算内。截图 `Player/*.png` 为 12 个用例的最终画面；完整计数见 `Player/Report.json` 与 [P3Census.json](P3Census.json)。
