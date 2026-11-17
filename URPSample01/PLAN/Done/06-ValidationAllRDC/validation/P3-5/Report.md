# P3-5 SSR 正确性场景（反射/梯度/空洞/透明/probe 回退）证据（2026-11-17）

> 任务：P3-5。目标：用可复算的截图指标验证 SSR 的画面正确性：反射位置/朝向、smoothness 权重、空洞覆盖、透明 opt-in、`reflectSky`、关闭基线与探针回退。
> 结论：**通过**。13 个用例全部通过 `Report.json` 的整数断言；[analyze.py](analyze.py) 从截图重算的 9 组指标全部 `passed=true`（`Metrics.json`）。最终 EditMode 套件 449/449 通过。

## 1. 场景与方法

| 项 | 值 |
|---|---|
| Manifest | [P3Manifest.json](P3Manifest.json) |
| Player | `Builds/SslValidationPlayer/SslValidationPlayer.exe`（D3D11、Development、1280×720，Forward） |
| 运行 | `renderdoccmd capture -w --opt-hook-children … -ssl-validation-manifest …P3-5/P3Manifest.json -ssl-validation-out …P3-5/Player` |
| 指标脚本 | `python analyze.py`（只读 `Player/*.png`，自定义 PNG 解码，无第三方依赖） |
| 输出 | [Metrics.json](Metrics.json)、`Player/Report.json`、13 张 PNG |

场景：相机 `(0,2,-7)`、FOV 60°，四个 smoothness 0.95/0.80/0.65/0.40 的金属球、镜面地面、`z=11` 的发光板、`z=13` 的全屏渐变四色板、透明 SSR 面板与牺牲式反射探针。`ssr_mirror_*` 两个用例切换到 `layout=ssrMirror`：相机 `(0,2,-6)`、镜面地面 + 发光球 + 梯度四色板，用于反射中心与朝向标定。

**透明面板标定修正。** 首次运行时透明面板正对相机，反射方向指向相机背后的空域，所有透明光线 miss（`ssr_transparent_weight.png` 全黑），透明输出为 0。这与 P3-4 抓帧中的 hitpoint 全 miss 一致，属于验证场景几何问题。修正为绕 X 轴 `-45°`（`ScreenSpaceLightingValidationPlayerVerification.cs` 的 `BuildRig`），使反射光线垂直落到面板下方可见的地面。修正后透明权重覆盖面板区域 44.9%，透明输出非零（见 P3-4 §4）。

## 2. R3 反射

**Smoothness 权重单调。** `debugMode=12`（`SsrWeight`）重新输出权重 alpha；在每个球心解析投影的 48 px 圆盘内取 p90：

| smoothness | 0.95 | 0.80 | 0.65 | 0.40 |
|---|---|---|---|---|
| p90 weight | 255 | 245 | 207 | 78 |

严格递减，`passed=true`。p90 而非均值，避免反射光线出屏导致的 miss 像素（权重 0）污染统计。

**空洞填充覆盖。** 干净地面带 `x∈[100,1180]`、`y∈[600,700]`（无物体轮廓），权重 >5 的像素占比：

| 算法 | 非零像素/总数 | 占比 | 阈值 | 结果 |
|---|---|---|---|---|
| Approximation（显式邻居填充） | 108000/108000 | 100.0% | ≥95% | ✅ |
| PBR（3×3 gather） | 105887/108000 | 98.0% | ≥95% | ✅ |

**镜面反射中心。** `ssr_mirror_calibration_raw`（`debugMode=11`，只有 SSR 辐射）中发光球反射斑的亮度加权质心与解析镜像点比较：

| 项 | 值 |
|---|---|
| 期望 | (515.29, 609.42) |
| 实测 | (514.59, 608.90) |
| 误差 | **0.87 px**（阈值 ≤2 px） |

**反射朝向。** 同一抓帧第 765 列自上而下应依次为蓝、绿、红、白（渐变板反射）；实测 `["blue","green","red","white"]`，与期望一致。垂直翻转会从白色开始，因此该指标同时覆盖了反射方向的 Y 翻转。

## 3. R4 透明

| 指标 | 数值 | 结果 |
|---|---|---|
| 透明权重覆盖（`debugMode=17`，面板框 `480–800 × 325–478`） | 22000/48960 = **44.9%** | ✅ |
| opt-in on/off 面板差异 | mean 1.495、max 121、>1 像素 19137 | ✅ |
| 对照区（左侧地面）差异 | mean 0.392、max 13、>1 像素 4652 | ✅ |

判据：`max(panel) ≥ 3·max(control)`、`mean(panel) ≥ 1.0`、`>1` 像素数 `panel ≥ 3·control`。面板基色 alpha 0.3 使最终颜色差被稀释（mean 1.5），但权重图证明整个面板都获得了反射权重，面板差异显著高于仅受颜色历史反馈影响的对照区。

**`reflectSky=0`。** `ssr_reflectsky_on/off` 对比：

| 区域 | meanAbsDiff | maxAbsDiff | >1 像素 |
|---|---|---|---|
| 天空（y 40–300） | **0.0** | **0** | 0 |
| 地面反射（y 430–690） | 8.20 | 139 | 262044 |

关闭 `reflectSky` 只改变反射内容，不改变天空本身，符合预期。

**Y 翻转/共享 keyword（F-06/F-08）** 已在 P3-3 的 GPU 证据中验收：透明深度种子源/目标翻转态一致（826,164 px 外最大差 0），透明 trace/reproject 绑定独立变体；本轮最终抓帧再次确认透明 trace `9925`（112 B/7 vars）与不透明 trace `9886`（144 B/9 vars）同帧共存。

## 4. R5 关闭基线与探针回退

| 指标 | 数值 | 结果 |
|---|---|---|
| `intensity=0` vs SSR 关闭（整帧，y<690） | mean 0.0、max 0、>1 像素 0 | ✅ |
| 无 SSR 权重像素上的最终色（探针回退） | 500568 px，max 0、>1 像素 0 | ✅ |

即：`intensity=0` 与关闭完全一致；在所有 SSR 权重为 0（并对 3 px 邻域腐蚀）的像素上，开启 SSR 不改变最终颜色，反射探针回退保持。透明面板区域从该统计中排除。

## 5. 验收映射

| 标准 | 结果 |
|---|---|
| R3 反射中心误差 ≤2 px | ✅ 0.87 px |
| R3 smoothness 0.95/0.80/0.65/0.40 权重单调 | ✅ 255/245/207/78 |
| R3 空洞填充覆盖 ≥95% 且不跨轮廓 | ✅ Approx 100%、PBR 98.0%（仅干净地面带） |
| R4 仅 opt-in 生效 | ✅ 面板 19137 个 >1 像素 vs 对照 4652；透明权重 44.9% |
| R4 `reflectSky=0` | ✅ 天空 0 像素差；地面 mean 8.20 |
| R4 Y 翻转/keyword（F-06/F-08） | ✅ 见 [P3-3/Report.md](../P3-3/Report.md) 与 P3-4 抓帧 |
| R5 `intensity=0` / 关闭 0 像素差 | ✅ 整帧 max 0 |
| R5 探针回退 | ✅ 500568 px max 0 |

## 6. 产物与复现

`Player/` 下 13 张 PNG（权重/原始/最终/透明/reflectSky/标定）与 `Report.json`；`Metrics.json` 由 `python analyze.py` 重新生成。所有指标只依赖提交的 PNG，可在没有 RenderDoc 的机器上复算。本轮 P3-5 不新增 `.rdc`，GPU 证据复用 P3-4 的三个抓帧。
