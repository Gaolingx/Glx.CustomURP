# P1-2 GTAO 正交深度与视线修复（F-03）证据（2026-11-17）

> 任务：P1-2 / F-03（Medium）。目标：正交相机的窗口深度必须线性还原为视空间位置，视线方向必须与屏幕位置无关。
> 结论：**修复完成**。新增 `_GtaoOrthoDepthParams` 线性深度参数与共享 `GTAOLinearDepth`，正交分支视线固定为 `(0,0,-1)`，空间双边降噪同步使用同一映射；新增 5 项 EditMode 断言，全量 EditMode **454/454** 通过。GPU 验收见 [P1-GTAO/Report.md](../P1-GTAO/Report.md)：2 m 与 20 m 接触深度下的 AO 覆盖相对差 **0.18%**，左右屏幕位置差 **0.08%**。

## 1. 缺陷

`GroundTruthAmbientOcclusionPass.cs` 无条件用 `near/far` 生成透视 `_ZBufferParams`，`GTAOCommon.hlsl` 用 `LinearEyeDepth` 还原正交窗口深度。正交窗口深度本身是线性的，透视映射会把整个深度范围压缩到相机前方 0.1–1 m：

| 窗口深度（reversed Z） | 透视 `LinearEyeDepth` | 正交正确值 |
|---|---|---|
| 1.0（近） | 0.100 | 0.100 |
| 0.9 | 0.111 | 10.09 |
| 0.5 | 0.200 | 50.05 |
| 0.1 | 0.991 | 90.01 |
| 0.0（远） | 100.0 | 100.0 |

（`near=0.1`、`far=100`，`_ZBufferParams=(999,1,9.99,0.01)`。）

结果是 horizon search 的 `deltaPos` 几乎为零、世界半径与深度脱钩。同一段代码还用 `V = normalize(-positionVS)` 作为视线方向；正交投影下所有像素的视线应相同，透视归一化会随屏幕坐标旋转。`GTAOSpatialDenoise.compute` 的双边深度权重也使用了同一条透视线性化。

## 2. 修复

| 位置 | 变更 |
|---|---|
| `GroundTruthAmbientOcclusionShaderVariables.cs(.hlsl)` | 新增 `_GtaoOrthoDepthParams`（第 15 个 float4），cbuffer 仍为 16 字节对齐的向量序列 |
| `GroundTruthAmbientOcclusionPass.cs` | `ComputeOrthoDepthParams(near, far, reversedZ)`：reversed Z 返回 `(-(f-n), f)`，普通 Z 返回 `(f-n, n)`；随 `_GtaoZBufferParams` 一起上传；对 spatial denoise 上传 `GTAO_ORTHO` keyword |
| `GTAOCommon.hlsl` | 新增 `GTAOLinearDepth(raw)`：正交走 `raw * scale + bias`，透视保持 `LinearEyeDepth`；`GTAOGetPositionVS` 使用它 |
| `GTAO.compute` | `#ifdef GTAO_ORTHO` 分支 `V = float3(0.0, 0.0, -1.0)`；透视分支保持 `normalize(-positionVS)` |
| `GTAOSpatialDenoise.compute` | 增加 `#pragma multi_compile _ GTAO_ORTHO`；双边权重改用 `GTAOLinearDepth`，删除 `LinearEyeDepth` |
| binding probe | 字段数 36 → 37，新增 `_GtaoOrthoDepthParams` 回读行；折射/降噪行索引顺延 |

正交的屏幕半径公式（`radiusWorld * aoHeight / (2 * orthoHalfSize.y)`）在前一轮已经实现，本轮只补齐深度与视线。

## 3. CPU 回归

新增 `GroundTruthAmbientOcclusionOrthographicTests`：

1. `PositionReconstructionUsesTheOrthographicDepthMapping`：`GTAOLinearDepth` 存在、使用 `_GtaoOrthoDepthParams.x`、`GTAOGetPositionVS` 经由共享 helper。
2. `SpatialDenoiseLinearisesDepthThroughTheSharedHelper`：spatial 声明 `GTAO_ORTHO`、使用 `GTAOLinearDepth`、不再出现 `LinearEyeDepth`。
3. `ViewDirectionIsConstantUnderOrthographicProjection`：常量 `(0,0,-1)` 位于 `#ifdef GTAO_ORTHO` 分支，透视分支仍是 `normalize(-positionVS)`。
4. `OrthographicDepthParamsMapNearAndFar`：reversed Z 的 raw 1/0 → near/far；非 reversed 的 raw 0/1 → near/far。
5. `PassUploadsTheOrthographicDepthParamsAndKeyword`：pass 上传参数并对 spatial 设置 keyword。

`GroundTruthAmbientOcclusionCBufferTests` 由 14 个 float4 更新为 **15**。全量 EditMode：**454/454 passed**（原 449 + 新增 5）。

## 4. GPU 证据

用例 `forward_gtao_ortho`（Forward、正交、`orthographicSize=4`、10° 俯角、全分辨率、temporal）：

| 项 | 值 |
|---|---|
| dispatch | **3**：GTAOMain + SpatialDenoise + TemporalDenoise（[P1-GTAO/P1Census.json](../P1-GTAO/P1Census.json)） |
| CPU 快照 | `orthographic=true`、`fullResolution=true`、`temporal=true`、`produced=true`、组 160×90×1 |
| GTAOMain 变体 | 正交 `4c0aae2d-9eb10f42-1480c83c-a5908d89`，透视 `9cafa59e-65c83583-17bd0c70-ddf9d2ef`（[P1-GTAO/Probe.json](../P1-GTAO/Probe.json)） |
| 2 m vs 20 m 覆盖 | 0.6245 vs 0.6256，相对差 **0.0018**（≤5%） |
| 左 vs 右覆盖 | 近端 0.6241/0.6249、远端 0.6260/0.6252，最大差 **0.0008** |
| binding probe | 正交用例 15/15 个 GTAO 字段通过；`_GtaoOrthoDepthParams = (-99.9, 100, 0, 0)`，误差 0（`Player/BindingProbe_forward_gtao_ortho.json`） |

AO 覆盖的定义、采样圆盘、相机参数与判据见 [P1-GTAO/Report.md](../P1-GTAO/Report.md)。

## 5. 边界与后续

- 正交 GTAO 是 URP 新增能力，HDRP 14.0.12 没有正交对照；验收以“世界半径在屏幕上的覆盖不随深度/屏幕位置变化”为准，不承诺像素级 HDRP 一致。
- 极小的世界半径（0.25 m）会把屏幕空间半径钳到 `stepCount` 下限，平坦地面出现大面积轻微压暗；这与 HDRP 使用整数 `dispatchThreadId` 重建位置的约定一致（HDRP `GTAO.compute:154` 同样不加半像素），不是本次改动引入的问题。P1-GTAO 的半径单调性因此采用强遮挡足迹（AO<0.7）度量。
- `_GtaoOrthoHalfSize.zw`、`_GtaoParams0.x` 等字段压实仍留给 P4-4。

## 6. 文件

```text
Packages/com.unity.render-pipelines.universal@14.0.12/
  Runtime/RendererFeatures/GroundTruthAmbientOcclusion/
    GroundTruthAmbientOcclusionPass.cs
    GroundTruthAmbientOcclusionShaderIDs.cs
    GroundTruthAmbientOcclusionShaderVariables.cs
    GroundTruthAmbientOcclusionShaderVariables.cs.hlsl
  Shaders/Utils/GroundTruthAmbientOcclusion/
    GTAO.compute
    GTAOCommon.hlsl
    GTAOSpatialDenoise.compute
  Tests/Editor/RendererFeatures/GroundTruthAmbientOcclusion/
    GroundTruthAmbientOcclusionCBufferTests.cs
    GroundTruthAmbientOcclusionOrthographicTests.cs

Assets/Tests/ScreenSpaceLighting/
  Runtime/ScreenSpaceLightingBindingProbeFeature.cs
  Shaders/ScreenSpaceLightingBindingProbe.compute
  Editor/ScreenSpaceLightingTestRunner.cs

validation/P1-GTAO/（正交 GPU 证据）
```
