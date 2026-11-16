# P1-1 GTAO 全分辨率深度修复（F-01）证据（2026-11-17）

> 任务：P1-1 / F-01（High）。目标：全分辨率 GTAO 的中心深度与首次采样深度必须读取全分辨率 mip0 / 半分辨率 mip1，而不是按半分辨率坐标落在 mip1 之外。
> 结论：**修复完成**。CPU 回归测试通过；GPU 抓帧显示修复后全分辨率输出与半分辨率参考的结构相关系数从 0.99090 提升到 **0.99983**，平均绝对差从 1.005 降到 **0.144**；半分辨率路径未受影响（相关系数 0.99997）。

## 1. 缺陷与修复

`GTAOCommon.hlsl` 的 `GTAOGetDepthForCentral` / `GTAOGetDepthSample` 使用 `GTAO_FULL_RES` 守卫，但 `GTAO.compute` 与 `GTAOTemporalDenoise.compute` 只声明：

```hlsl
#pragma multi_compile HALF_RES FULL_RES
```

因此全分辨率模式始终编译 `#else` 分支：中心深度读 `mip1 + positionSS`，在全分辨率坐标下越过 mip1 的半尺寸区域；首次采样同样退化。

修复：

- `GTAOCommon.hlsl:88,97`：`#ifdef GTAO_FULL_RES` → `#ifdef FULL_RES`。
- `GroundTruthAmbientOcclusionPass.cs`：删除已无人读取的 `_GtaoParams0.x` 分辨率标志写入（改为常量 0，布局不变）。
- `GroundTruthAmbientOcclusionShaderVariables.cs`：注释改为 `x unused`（字段压实留给 P4-4 的统一清理）。
- 新增 EditMode 回归测试 `GroundTruthAmbientOcclusionFullResolutionTests`：
  - HLSL 不再出现 `GTAO_FULL_RES`，且包含 `#ifdef FULL_RES`；
  - `GTAO.compute` 仍声明 `multi_compile HALF_RES FULL_RES`；
  - pass 不再写入 `fullResolution ? 0f : 1f`。

## 2. 产物与复现

| 项 | 值 |
|---|---|
| Manifest | [P1Manifest.json](P1Manifest.json) |
| Player | `Builds/SslValidationPlayer/SslValidationPlayer.exe`（D3D11、Development、1280×720） |
| 抓帧运行 | `renderdoccmd capture -w --opt-hook-children … -ssl-validation-manifest …P1Manifest.json -ssl-validation-out …P1-1/Player` |
| 图像对比 | `python compare.py --before-dir ../P0/Smoke --after-dir Player --out Metrics.json` |

`Player/Report.json`：2/2 用例通过，`nonFinitePixels=0`，binding probe 14/14 字段 0 不匹配。

## 3. GPU dispatch 证据

| 用例 | GTAO marker | dispatch 数 | 相对 P0 |
|---|---|---|---|
| `forward_gtao_full_after` | 323 | **3**（main + copyHistory + temporal） | 不变 |
| `forward_gtao_half_after` | 323 | **4**（main + copyHistory + spatial + temporal） | 不变 |

修复只改变全分辨率分支的深度坐标，不改变调度结构。

## 4. 图像证据

同一场景、同一相机、同样 10 帧预热与历史重置。`before` 为 P0 冒烟抓帧（旧 shader），`after` 为本轮构建。

| 指标 | before | after |
|---|---|---|
| full ↔ half 亮度相关系数 | 0.99090 | **0.99983** |
| full ↔ half 平均绝对差（0–255） | 1.005 | **0.144** |
| half 相对 before 的相关系数 | — | 0.99997 |
| half 相对 before 的平均绝对差 | — | 0.076 |
| full 相对 before 的平均绝对差 | — | 0.921 |

解读：

- 全分辨率与半分辨率参考的差异缩小到原来的 1/7，且相关系数超过 HANDOFF G4 的 0.95 阈值。
- 半分辨率图像几乎逐像素不变（0.076/255），证明修复没有触碰默认半分辨率路径。
- 全分辨率图像发生变化（0.921/255），正是 F-01 的预期视觉修正；变化集中在 AO 接触区（本场景为球体与地面接触阴影）。

## 5. 边界与后续

- 本轮 G4 的“中心深度与相机同像素深度的 1e-5 相对误差”需要逐像素深度导出；P1-3 会在 GTAO 专项场景中补齐，并与 F-03/F-10 一起验收。
- 本条为 High 缺陷，状态 **Fixed**。`_GtaoParams0.x` 的字段压实与 `_GtaoScreenSize.zw`、`_GtaoOrthoHalfSize.zw` 一起留给 P4-4。

## 6. 文件

```text
validation/P1-1/
  P1Manifest.json
  compare.py
  Metrics.json
  Report.md
  Player/
    Report.json
    BindingProbe_forward_gtao_full_after.json
    BindingProbe_forward_gtao_half_after.json
    forward_gtao_full_after.png
    forward_gtao_half_after.png
    *.rdc（本地，约 40 MB，按 7.5 预算不入库）
```