# P1-3 GTAO 生效与正确性 GPU 验收（含 F-10）证据（2026-11-17）

> 任务：P1-3。目标：在三种渲染器、全/半分辨率、temporal/非 temporal、正交与空 history 条件下，证明 GTAO 的调度、keyword、材质消费和输出正确性，并验收 F-10。
> 结论：**通过**。16 个用例全部通过（`Player/Report.json`），9 个抓帧；[analyze.py](analyze.py) 的 8 组截图指标全部通过（[Metrics.json](Metrics.json)）；[rdc_probe.py](rdc_probe.py) 的深度/变体/history 证据全部通过（[Probe.json](Probe.json)）。全量 EditMode **454/454**。

## 1. 场景与方法

| 项 | 值 |
|---|---|
| Manifest | [P1Manifest.json](P1Manifest.json)，16 个用例 |
| Player | `Builds/SslValidationPlayer/SslValidationPlayer.exe`（D3D11、Development、1280×720，Deferred 资产构建、运行时切换三渲染器） |
| 抓帧 | `renderdoccmd capture -w --opt-hook-children --opt-disallow-fullscreen --opt-disallow-vsync … -ssl-validation-manifest …P1Manifest.json -ssl-validation-out …P1-GTAO/Player` |
| 截图指标 | `python analyze.py`（只读 `Player/*.png`，无第三方依赖） |
| 调度 census | `python census.py` → [P1Census.json](P1Census.json) |
| GPU 探针 | `python rdc_probe.py` → [Probe.json](Probe.json)、[DepthProbe/](DepthProbe/) |
| 主场景 | 相机 `(0,2,-7)`、FOV 60°；四个金属球、镜面地面、发光板、渐变板、透明面板、探针；SSL 三个 Volume 效果全部关闭 |
| 正交场景 | `layout=gtaoOrtho`：相机 `(0,3.5,-2)`、俯角 10°、`orthographicSize=4`；四个同尺寸球，两对接触点分别位于视深度 **2.0 m** 与 **20.0 m**，左右对称 |

AO 强度/覆盖的截图来自 GTAO 调试视图（`debugMode=AmbientOcclusion`），最终色是 8 位 sRGB 输出；`analyze.py` 先做 sRGB→线性反解再与阈值比较。GTAO 的遮挡纹理是 `R8_UNorm`，线性值量化步长约 1/255（例如地板读数 0.9832 对应纹理值 250/255 = 0.9804）。

## 2. G1 调度

`Report.json` 的 DEV 快照（`gtaoDispatch`）与 RDC census 一致：

| 用例 | 渲染器 | AO / temporal | CPU main/spatial/copy/temporal/blur/up | RDC 抓帧 dispatch |
|---|---|---|---|---|
| `forward_gtao_full_temporal` | Forward | 1280×720 / 是 | 1/1/0/1/0/0 | 3（336,350,367） |
| `forward_gtao_half_temporal` | Forward | 640×360 / 是 | 1/1/0/1/0/1 | 4（336,350,367,382） |
| `forward_gtao_full_spatial` | Forward | 1280×720 / 否 | 1/1/0/0/0/0 | 2（336,350） |
| `forward_gtao_half_spatial` | Forward | 640×360 / 否 | 1/0/0/0/1/0 | 2（336,351） |
| `forward_gtao_first_frame` | Forward | 640×360 / 是 | 1/1/**1**/1/0/1 | 5（336,350,364,381,396） |
| `forwardplus_gtao_half_temporal` | Forward+ | 640×360 / 是 | 1/1/0/1/0/1 | 4（342,356,373,388） |
| `deferred_gtao_half_temporal` | Deferred | 640×360 / 是 | 1/1/0/1/0/1 | 4（355,369,386,401） |
| `forward_gtao_ortho` | Forward（正交） | 1280×720 / 是 | 1/1/0/1/0/0 | 3（291,305,322） |
| `forward_gtao_off_zero` | Forward | 关闭 | 未执行（`executedThisFrame=false`） | **无 GTAO marker，0 dispatch** |

所有活动用例的组维度均等于 `ceil(ao/8)`（160×90 或 80×45），z=1；每帧 Depth Pyramid 有 12 个 dispatch（mip0 copy 加逐级下采样）。非 temporal 全分辨率由 `GTAOMain + SpatialDenoise` 写正式输出；非 temporal 半分辨率由 `GTAOMain + BlurUpsample` 直接写出。

## 3. G2 keyword、材质消费与关闭基线

- **关闭**：`intensity=0` 抓帧无 GTAO marker、0 dispatch，CPU 快照 `executedThisFrame=false`。
- **材质变体**：同一地面像素（呈现坐标 `(640,510)`）的 Lit PS 在 AO 开/关时是两个变体：

| | shader | hash | texture2D | sampler |
|---|---|---|---|---|
| AO 开 | `ResourceId::9632` | `b1ea629c-c6cf6b85-05e26194-6a514fa9` | 3 | 4 |
| AO 关 | `ResourceId::10172` | `ffd71032-f0b3aa06-12bdf0ad-bdb8e3fe` | 2 | 3 |

开变体多出一张 2D 纹理和一个 sampler，正是 `_SCREEN_SPACE_OCCLUSION` 分支新增的屏幕空间遮挡采样；反汇编见 [DepthProbe/Lit_AO_on.ps.asm.txt](DepthProbe/Lit_AO_on.ps.asm.txt) 与 `Lit_AO_off.ps.asm.txt`。

- **资源消费**：`GtaoOcclusion`（`ResourceId::9659`，R8_UNorm）在事件 **367**（TemporalDenoise）被写，在 **408/414/422/447/489/523/529** 七个不透明 draw 的 PS 被读，证明 AO 先写后读、进入材质。
- **binding probe**：`forward_gtao_full_temporal` 与 `forward_gtao_ortho` 各 15/15 个 GTAO 字段回读 0 不匹配，含新增的 `_GtaoOrthoDepthParams`。
- **关闭基线**：`intensity=0` 与 renderer feature 禁用两帧逐像素比较 **max 0 / 0 像素变化**（`Metrics.json.offBaseline`）。

## 4. G3 输出正确性

| 指标 | 值 | 判据 |
|---|---|---|
| 接触角 visibility（球-地接触点 12 px 圆盘均值） | **0.4175**（p50 0.3613） | ≤0.70 ✅ |
| 开阔地面（前方地板 20 px 圆盘均值） | **0.9832** | ≥0.98 ✅ |
| 开阔面板（渐变板中心 20 px 圆盘均值） | **1.0000** | ≥0.99 ✅ |
| intensity 0.5 / 1 / 4 的接触均值 | **0.6257 / 0.4175 / 0.0938** | 严格递减 ✅ |
| radius 0.25 / 1 / 3 的强遮挡足迹（AO<0.7，接触点 120 px 圆盘） | **688 / 1108 / 3376** | 严格递增 ✅ |
| 非有限像素（16 张截图） | **0** | =0 ✅ |

半径说明：0.25 m 会把屏幕半径钳到 `stepCount` 下限（6 px），平坦地面出现大面积轻微压暗（<0.9 的计数反而更高），因此“影响范围”用强遮挡足迹度量；这与 HDRP 使用整数 `dispatchThreadId` 重建位置的约定一致（HDRP `GTAO.compute:154` 同样不加半像素），不是本轮回归。

## 5. G4 全分辨率深度

**深度源逐像素一致。** 全分辨率路径的中心深度读 packed depth pyramid mip0 的 `positionSS`（P1-1 的 F-01 修复，`GTAOCommon.hlsl:88`）。抓帧中把 pyramid 的 mip0 与 `_CameraDepthTexture` 分别导出比较：

| 项 | 值 |
|---|---|
| 纹理尺寸 / 格式 | pyramid atlas 1280×1080 R32F（mip0 区域 1280×720）；camera depth 1280×720 D32S8 |
| min / max（float，含 32 位 bit 模式） | 两者 `0.0` / `0.027855239808559418`（uint 1021587628）完全相同 |
| 导出图像逐像素（921,600 px） | **different=0，max=0**（[DepthProbe/DepthPyramidMip0.png](DepthProbe/DepthPyramidMip0.png) vs [DepthProbe/CameraDepthTexture.png](DepthProbe/CameraDepthTexture.png)） |
| 探针像素 raw depth（AO 开 / 关） | `0.01107927318662405` / `0.01107927318662405`（`pixel_history`） |

因此“中心深度 == 摄像机同像素深度”在整帧范围内成立（导出为 8 位显示值；min/max 的 float 位模式提供了额外的全精度极值校验）。mip0 位于 atlas 顶部 1280×720，全分辨率 `positionSS` 不会越界。

**full ↔ half 结构一致性**：两份最终图亮度相关系数 **0.99978**、平均绝对差 **0.159/255**（判据 ≥0.95）。

## 6. G5 正交

| 指标 | 值 | 判据 |
|---|---|---|
| 接触深度（解析） | 近 **2.0 m** / 远 **20.02 m** | — |
| AO 覆盖（40 px 圆盘内 AO<0.9 的比例） | 近 **0.6245** / 远 **0.6256** | 相对差 **0.0018** ≤5% ✅ |
| 屏幕位置（左 vs 右，同一深度） | 近 0.6241/0.6249；远 0.6260/0.6252 | 最大差 **0.0008** ≤5% ✅ |
| 圆盘均值 | 近 0.6900 / 0.6897；远 0.6803/0.6797 | 近/远差 1.4% |
| `V` 方向 | 正交分支常量 `(0,0,-1)`（`GTAO.compute`，EditMode 断言） | ✅ |
| 正交 GTAOMain 变体 | hash `4c0aae2d-…`，透视 `9cafa59e-…`（同一 GTAOMain kernel） | 独立变体 ✅ |
| `_GtaoOrthoDepthParams` 回读 | `(-99.9, 100, 0, 0)`，误差 0（正交用例 probe 15/15） | ✅ |

即：相同世界半径在 2 m 与 20 m 深度、在屏幕左右两侧产生相同的 AO 覆盖；透视实现下远端的屏幕半径会缩小约一个数量级。

## 7. G7 提前返回与空 history（F-10）

**修复。** `GroundTruthAmbientOcclusionPass.Dispatch` 现在返回“本帧是否写出正式 AO”：只有返回 true 时才绑定 `_ScreenSpaceOcclusionTexture`、开启 `_SCREEN_SPACE_OCCLUSION` 并设置 `_AmbientOcclusionParam`；返回 false 时显式关闭 keyword 并把 AO 参数归零，避免材质消费上一帧/未初始化的纹理。`intensity=0` 与 feature 禁用时 pass 不入队，快照 `executedThisFrame=false`。

**空 history 首帧 GPU 证据**（`forward_gtao_first_frame`，reset 后第一帧即抓帧）：

| 事件 | 动作 | 资源 |
|---|---|---|
| 336 / 350 | GTAOMain / SpatialDenoise | packed data → packed blurred |
| **364** | CopyHistory | 写 `GtaoHistory0` |
| **381** | TemporalDenoise | 读 `GtaoHistory0`，写 `GtaoHistory1` |
| **396** | TemporalDenoise | 写 `GtaoOcclusion` |
| 437 / 443 / 451 / 476 / 518 / 552 / 558 | 不透明 draw PS | 读 `GtaoOcclusion` |

CPU 快照同一帧：`historyReady=false`、`copyHistoryDispatches=1`、`temporalDispatches=1`、`produced=true`。即首帧先把当前空间结果复制进历史槽，再由 temporal 读取并写出正式 AO，材质读到的是本帧结果。

## 8. 验收映射

| 标准 | 结果 |
|---|---|
| G1 调度 | ✅ CPU + RDC 双份（§2） |
| G2 keyword / 关闭 | ✅ 变体哈希 + 0 dispatch + 0 像素差（§3） |
| G3 输出 | ✅ 接触/开阔/intensity/radius 全部达标（§4） |
| G4 full-res | ✅ 深度源逐像素一致 + full/half 0.99978（§5） |
| G5 正交 | ✅ 深度/屏幕位置覆盖差 0.18%/0.08%（§6） |
| G7 F-10 | ✅ 首帧 copy→temporal→材质链 + 发布门控（§7） |
| G6 覆盖 / Deferred 双路径 | 留给 **P1-4**（材质覆盖矩阵与 Deferred 去双暗化） |

## 9. 产物与复现

```text
validation/P1-GTAO/
  P1Manifest.json        # 16 用例
  P1Census.json          # 9 个抓帧的 GTAO / Depth Pyramid dispatch
  Probe.json             # 深度源、材质/计算变体、history 播种
  Metrics.json           # analyze.py 重算的截图指标
  census.py / rdc_probe.py / analyze.py
  DepthProbe/
    DepthPyramidMip0.png / CameraDepthTexture.png
    Lit_AO_on.ps.asm.txt / Lit_AO_off.ps.asm.txt
  Player/
    Report.json          # 16/16 passed
    BindingProbe_forward_gtao_full_temporal.json   # 15/15 GTAO 字段
    BindingProbe_forward_gtao_ortho.json           # 15/15 GTAO 字段
    *.png                # 16 张截图
    *.rdc                # 9 个抓帧，已入库
```

复现：`python census.py && python rdc_probe.py && python analyze.py`；三个脚本只读 `Player/` 与已提交的抓帧，不需要重跑 player。`.rdc` 已入库（约 65 MB）。

## 10. 边界

- Deferred 的 dispatch 与 Forward 相同，但抓帧中没有 `DrawOpaqueObjects` marker（AO 在 GBuffer 阶段消费）；Deferred 的“有/无方向光只暗化一次”和材质覆盖矩阵属于 P1-4。
- sRGB/8 位量化：AO 纹理是 R8_UNorm，截图阈值在 sRGB 反解后比较；开阔面 0.9832 对应纹理值 250/255。
- 正交 GTAO 没有 HDRP 对照，验收以解析覆盖不变性为准（P1-2）。
- 本轮没有新增 XR / 多相机用例；`GTAO_XR_ARRAY` 的 slice 绑定沿用 T13 的结论，P4-3 再覆盖分辨率/XR 矩阵。
