# P1-4 GTAO 材质覆盖与 Deferred 双路径证据（2026-11-17）

> 任务：P1-4。目标：验收 G6——ComplexLit/SimpleLit/Unlit/Terrain/透明的 AO 覆盖符合 §2.4，并证明 Deferred 的“有方向光”与“无方向光”两条 AO 路径都只把 AO 乘一次，关闭基线逐字节一致。
> 结论：**通过**。16 个用例全部通过（[Player/Report.json](Player/Report.json)）；3 个抓帧确认 AO 纹理的写入者与消费者；[analyze.py](analyze.py) 的材质覆盖、单次乘积、关闭基线指标全部达标（[Metrics.json](Metrics.json)）；[rdc_probe.py](rdc_probe.py) 的两条 Deferred 路径 shader 互斥（[Probe.json](Probe.json)）。
> 附带发现（非 GTAO 缺陷，待后续任务）：Forward+ 下 TerrainLit 只收到环境光，见 §6。

## 1. 场景与方法

| 项 | 值 |
|---|---|
| Manifest | [P1Manifest.json](P1Manifest.json)，16 个用例 |
| 覆盖布局 | `layout=gtaoCoverage`，相机 `(0,2,-9)`、FOV 60° |
| 材质 | 五个 `1.6×2.4×0.4` 竖板（正面 `z=3.8`）：Lit / ComplexLit / SimpleLit / BakedLit / Unlit；TerrainLit 平地 `x 5.5..13.5, z 0..8`（`y=0.01`）+ `1.2×1.6×1.2` 接触盒；透明 Lit 面板 `6×2.5` 悬于 `(0,5.2,12)`，背景为天空 |
| 光照 | 方向光开（默认）或关（`light.noDirectionalLight`）；天空盒环境光不变 |
| 截图指标 | `python analyze.py`（只读 `Player/*.png`，无第三方依赖） |
| 调度 census | `python census.py` → [P1Census.json](P1Census.json) |
| GPU 探针 | `python rdc_probe.py` → [Probe.json](Probe.json) |
| 关闭基线 | Deferred 有光/无光两套 `intensity=0` 与 feature 禁用对比 |

采样点（世界坐标，投影到 1280×720）：竖板正面各取 `(x, 0.4, 3.8)` 的 6 px 圆盘；地板接触 `(-3, 0, 3.3)`（SimpleLit 竖板前 0.5 m）；地形接触 `(9.5, 0.01, 3.0)`（接触盒前 0.4 m）；地形开阔 `(7, 0.01, 1.0)`；透明面板中心 `(0, 5.2, 12)` 的 20 px 圆盘。所有比值均为 sRGB→线性后的亮度比（on/off）。

## 2. AO 调度与资源链

三个抓帧的 GTAO 都是半分辨率 + temporal，4 个 dispatch（main / spatial / temporal / upsample），Depth Pyramid 12 个；`GtaoOcclusion` 由最后一个 upsample kernel 写入（半分辨率需要上采样后才成为材质纹理）。

| 抓帧 | GTAO marker | dispatch | AO 写入 | AO 读取 |
|---|---|---|---|---|
| `forward_coverage_on_frame12623` | 390 | 403/417/434/449 | **449** | `DrawOpaqueObjects` 内 8 个不透明 draw（488, 511, 548, 551, 556, 585, 622, 654） |
| `deferred_coverage_on_frame12691` | 409 | 422/436/453/468 | **468** | `Deferred Pass` 内 **2** 个 draw（670, 685） |
| `deferred_nolight_on_frame12759` | 336 | 349/363/380/395 | **395** | `Deferred Pass` 内 **1** 个 draw（588） |

`Probe.json.pathShaders`：

| 路径 | PS shader | hash | 声明的 2D 纹理数 |
|---|---|---|---|
| Deferred + 方向光（Lit stencil） | `ResourceId`（670） | `1d509d79-cad00756-7d73535e-dcdde8d1` | 6 |
| Deferred + 方向光（SimpleLit stencil） | `ResourceId`（685） | `d6128900-84f23a77-4fca1c9d-522f2fd8` | 6 |
| Deferred 无方向光（AO-only） | `ResourceId`（588） | `2bd9be42-a2084fc6-9fcf130b-db5f591d` | 2 |

两条路径的读取 shader 哈希**互不相同**（`distinctPaths=true`）：有方向光时 AO 由首个方向光的 stencil 照明 pass 消费（`StencilDeferred.shader` 的 `Deferred Directional Light` 两个 pass，`Blend One SrcAlpha` + `_DEFERRED_FIRST_LIGHT`），没有方向光时由 `AmbientOcclusionOnly` 全屏 pass 消费（同文件 `Blend One SrcAlpha`）。Forward 路径则是 8 个材质 draw 直接采样 `_ScreenSpaceOcclusionTexture`。

## 3. G6 材质覆盖

竖板正面（方向光开，AO on/off 亮度比，越小表示 AO 越强）：

| 材质 | Forward | Forward+ | Deferred | 判据 ≤0.97 |
|---|---|---|---|---|
| Lit | **0.9481** | **0.9411** | **0.9312** | ✅ |
| ComplexLit | **0.9401** | **0.9328** | **0.9328** | ✅ |
| SimpleLit | **0.9421** | **0.9372** | **0.9335** | ✅ |
| BakedLit | **0.7858** | **0.7701** | **0.7700** | ✅ |
| Unlit | 1.0000（0 字节差） | 1.0000（0） | 1.0000（0） | ✅ 不受影响 |
| 透明面板 | 1.0000（0） | 1.0000（0） | 1.0000（0） | ✅ 不受影响 |

- **Unlit**：`UnlitForwardPass.hlsl:128-132` 只乘 `aoFactor.directAmbientOcclusion = lerp(1, ssao, _AmbientOcclusionParam.w)`；默认 `directLightingStrength=0` 时恒为 1，整块区域逐字节不变（`maxByteDiff=0`）。注意 §2.4 的“Unlit=否”应表述为“默认直接光强度下不受影响”；把 `directLightingStrength>0` 打开后 Unlit 会随直接光变暗（本轮不覆盖该配置）。
- **透明**：`AmbientOcclusion.hlsl:27` 的 `!defined(_SURFACE_TYPE_TRANSPARENT)` 排除了透明材质；面板背后的天空本身也不受 AO 影响，所以面板区域逐字节不变。
- **BakedLit**：只由环境 SH 着色，AO 全部作用在间接项，故比值最小（0.77），符合“BakedLit 接收 AO”的预期。
- **Terrain**：方向光下 AO 只缩放间接项，而地形反照率较暗、方向光占主导，实际像素变化只有约 2%（Forward 0.9817、Deferred 0.9804、Forward+ 0.9271）。因此地形门控用**无方向光**帧隔离 AO 因子：

| Terrain | 接触（AO） | 开阔（AO） | 差 | 判据 |
|---|---|---|---|---|
| Forward（无方向光） | **0.9126** | **0.9705** | 0.0579 | 接触 ≤0.95、差 ≥0.03 ✅ |
| Deferred（无方向光） | **0.9096** | **0.9663** | 0.0567 | ✅ |
| 有方向光（三渲染器） | 0.9817 / 0.9271 / 0.9804 | 0.9869 / 0.9723 / 0.9886 | 0.0051 / 0.0451 / 0.0082 | 接触 < 开阔 ✅ |

## 4. Deferred 双路径只乘一次

**方法**：线性空间中像素颜色 = `A·ao + D`（A 为环境项，D 为方向项）。同一像素的 AO 移动量应为 `A·(ao-1)`；若某条路径乘了两次，移动量变成 `A·(ao²-1)`。A 用“方向光关”帧测量，ao 用“方向光关 + AO 开/关”帧的比值测量。

先验证“比值 == 材质实际采样的 AO”（`debugMode=GlobalAmbientOcclusion` 读取材质全局绑定）：

| 采样点 | 渲染器 | 无光亮度比 | AO 纹理值 | 误差 |
|---|---|---|---|---|
| 竖板 | Forward | 0.8174 | 0.8075 | 0.0099 |
| 竖板 | Deferred | 0.8096 | 0.8075 | 0.0020 |
| 地板接触 | Forward | 0.9579 | 0.9612 | 0.0033 |
| 地板接触 | Deferred | 0.9501 | 0.9612 | 0.0111 |
| 地形接触 | Forward | 0.9126 | 0.9162 | 0.0036 |
| 地形接触 | Deferred | 0.9096 | 0.9156 | 0.0060 |

全部 ≤0.02（判据），说明环境路径对每个像素正好乘了一次 AO。

**单次 vs 双次预测**（Deferred，方向光开）：

| 采样点 | 实测有光 on | 单次预测 | 单次误差 | 双次预测 | 双次误差 |
|---|---|---|---|---|---|
| 竖板 | 0.26040 | 0.26317 | **0.99%** | 0.24984 | 3.77% |
| 地板接触 | 0.50980 | 0.50843 | **0.26%** | 0.49538 | 2.76% |
| 地形接触 | 0.23949 | 0.23992 | **0.18%** | 0.23595 | 1.45% |

单次假设在三个像素上都优于双次假设（判据：误差 ≤2% 且小于双次误差）。Forward 的同公式结果也列出（竖板 0.50%、地形 0.10%；地板点因 AO 移动量小、8 位量化与帧间抖动相当，单双次差异不可分辨，故不作为门控）。结论：**Deferred 的两条 AO 路径都只把 AO 乘一次**。

## 5. 关闭基线

| 对比 | 路径 | maxAbsDiff | 变化像素 |
|---|---|---|---|
| `deferred_off_zero` vs `deferred_off_disabled` | 有方向光 | **0** | 0 |
| `deferred_nolight_off_zero` vs `deferred_nolight_off_disabled` | 无方向光（AO-only） | **0** | 0 |

`intensity=0` 时 pass 不入队（Report.json 中 `executedThisFrame=false`），与 feature 禁用逐字节一致。

## 6. 附带观察：Forward+ 下 TerrainLit 只收到环境光（非 GTAO）

覆盖截图中 Forward/Deferred 的 TerrainLit 正常受方向光照射（亮度 0.2446），**Forward+ 的 TerrainLit 只呈现环境光**（0.0439 ≈ 无方向光帧的 0.0483），同帧 Lit 地板在三渲染器均为 0.4391。

[ShadowTest](ShadowTest/)（[P1ShadowTest.json](P1ShadowTest.json)）关闭方向光阴影后：

| 场景 | 方向光阴影开 | 阴影关 |
|---|---|---|
| Forward 地形最暗像素 | 0.0483（阴影内） | **0.2331**（受光） |
| Forward+ 地形最暗像素 | 0.0439 | **0.0439**（不变） |
| Forward / Forward+ 地板 | 0.4391 / 0.4391 | 0.4391 / 0.4391 |

即 Forward+ 的地形缺主光与阴影无关（阴影开关对其无影响），而 Forward 的阴影开关确实生效。该现象与 GTAO 无关（AO 比值仍为 0.927，接触比开阔更暗），本轮按“记录待查”处理，建议在 P4-3（渲染器矩阵）或单独的 Terrain/Forward+ 任务中定位。证据 PNG 与 Manifest 已随本轮提交；`.rdc` 未采集。

## 7. 产物与复现

```text
validation/P1-4/
  P1Manifest.json        # 16 用例
  P1ShadowTest.json      # Forward+ Terrain 光照隔离测试（4 用例）
  P1Census.json          # 3 个抓帧的 GTAO / Depth Pyramid / Deferred pass 事件
  Probe.json             # AO 写入者、两条 Deferred 路径的消费 draw 与 shader hash
  Metrics.json           # analyze.py 重算的覆盖/单次乘积/关闭基线
  analyze.py / census.py / rdc_probe.py
  Player/
    Report.json          # 16/16 passed
    *.png                # 16 张截图（含 AO 全局视图）
    *.rdc                # 3 个抓帧，本地保留，不入库（约 12 MB）
  ShadowTest/
    Report.json / *.png  # Forward+ 地形光照观察
```

复现：`python census.py && python rdc_probe.py && python analyze.py`；脚本只读 `Player/` 与已提交的 `.rdc`（本地），不需要重跑 player。

## 8. 验收映射（G6）

| 标准 | 结果 |
|---|---|
| ComplexLit / SimpleLit 接收 AO | ✅ 三渲染器比值 0.93–0.94 |
| Unlit 不受影响 | ✅ 0 字节差（默认 `directLightingStrength=0`） |
| Terrain 接收 AO | ✅ 无光帧接触 0.910/0.913 vs 开阔 0.966/0.971；有光帧接触 < 开阔 |
| 透明不受影响 | ✅ 0 字节差（材质分支排除透明） |
| Deferred 有方向光只暗化一次 | ✅ 单次预测误差 0.18%–0.99%，双次预测 1.45%–3.77% |
| Deferred 无方向光只暗化一次 | ✅ 无光比值与 AO 纹理误差 ≤0.011 |
| Deferred 两条路径互斥 | ✅ 3 个消费 shader 哈希两两不同；有光 2 个 stencil 照明 draw，无光 1 个 AO-only draw |
| 关闭基线 0 像素差 | ✅ 两条路径均为 max 0 / 0 像素 |
| 调度 | ✅ 每个抓帧 4 个 GTAO dispatch、12 个 depth pyramid dispatch |
| 回归 | ✅ 全量 EditMode **454/454**（[EditModeFull.xml](EditModeFull.xml)） |
