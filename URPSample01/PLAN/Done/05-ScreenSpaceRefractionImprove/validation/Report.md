# 05 ScreenSpaceRefractionImprove 验证报告

> 生成：2026-11-16
> 运行环境：Unity 2022.3.62f1，DX11，T11 场景（Forward / Forward+ / Deferred）
> 原始证据：[T11Verification.json](T11Verification.json)、[Report.json](Report.json)、[Logs/UnityT11Verification.log](Logs/UnityT11Verification.log)、[Logs/UnityEditModeTests.log](Logs/UnityEditModeTests.log)

## 结论

- `T11 verification finished: completed=True passed=True`，三种渲染路径 0 issue。
- EditMode：`passed=170 failed=0 skipped=0`，包含新增的 `ScreenSpaceLightingRefractionImproveTests`。
- P0-1（屏幕路径 + fallback 接线）、P0-2（opacity mask）、P0-3（任意 probe / infinite 代理）通过本轮可视化验收。
- P1-1（IOR→F0）与 P1-2（Thin 0.005）只有静态/解析证据，尚无 HDRP 对照图；P1-3/P2-* 未开始。
- 因此 `Report.json` 的 `exitCriteriaMet=false`，阻塞项写在 `exitCriteriaBlockers`。

## 指标

| 任务 | 指标 | Forward | Forward+ | Deferred |
|---|---|---|---|---|
| P0-2 | opacity=1 与关闭效果的差异像素（>1/255） | 0 | 0 | 0 |
| P0-2 | opacity=1 最大通道差 | 4.6e-5 | 4.6e-5 | 4.6e-5 |
| P0-2 | 0/0.3/1 亮度线性拟合 R² | 0.999676 | 0.999676 | 0.999678 |
| P0-2 | 归一化 0.3 点误差（要求 ≤5%） | 2.33% | 2.33% | 2.33% |
| P0-2 | opacity 单调（背景→表面） | 是 | 是 | 是 |
| P0-1 | 折射变化像素（Box/Complex/SG） | 5915 / 2996 / 2476 | 同左 | 同左 |
| P0-1 | 非折射透明材质变化像素 | 0 | 0 | 0 |
| P0-1 | 金字塔跟随当前帧背景 | 是 | 是 | 是 |
| P0-3 | 非 box probe 场景 keyword 绑定次数 | 8 | 8 | 8 |
| P0-3 | 非 box probe 场景 infinite 次数 | 0 | 0 | 0 |
| P0-3 | 无 probe 场景 infinite 次数 / 绑定 | 8 / 8 | 8 / 8 | 8 / 8 |
| P0-3 | 无 probe 场景折射变化像素 | 16281 | 16281 | 16281 |

opacity 拟合明细见 [P0-2/fitting.csv](P0-2/fitting.csv)，模式代理指标见 [P0-3/metrics.csv](P0-3/metrics.csv) 与 [P0-1/metrics.csv](P0-1/metrics.csv)。

## 复核修复验证（A1/A2，2026-11-16）

针对 A1（specular 桶被 mask 误伤）与 A2（颜色金字塔 mip 上限少一级）的修复，重新运行了相关 EditMode 套件与 T11 三模式验证：

- EditMode：完整套件 433/433 通过，0 failed；包含 `ScreenSpaceLightingRefractionImproveTests` / `ScreenSpaceLightingRefractionTests` / `ScreenSpaceLightingMaterialTests` / `ScreenSpaceLightingHistoryTests`，以及新增 `ColorPyramidMipCountMatchesHdrpLodCount`。
- T11：Forward / Forward+ / Deferred 均 `completed=True passed=True issues=0`；证据 [T11Hotfix/T11Verification.json](T11Hotfix/T11Verification.json)。
- opacity 0/0.3/1：三模式单调；归一化 0.3 点误差 2.04%（修复前 2.33%），R² 0.99975；opacity=1 与关闭效果 0 像素差（最大通道差 4.58e-5）。
- opacity=0 的玻璃内部亮度由 0.1628 升至 0.1780（specular 桶恢复），opacity=1 亮度不变；legacy 透明基线仍 0 像素变化。
- mip 修复在当前低粗糙度 T11 材质上没有像素变化，需用粗糙玻璃另行回归（P2 阶段）。
- 2026-11-16 收尾：P2-1 复核关闭（NDC 出 [0,1] 时 `EdgeOfScreenFade` 恒为 0，与硬拒绝等价）；吸收公式改为 core 的 `-log(T + REAL_EPS) / max(dist, REAL_EPS)`，新增 `AbsorptionMatchesCoreTransmittanceSemantics` 契约测试。
- 吸收公式回归：EditMode 434/434；T11 三模式 `completed=True passed=True issues=0`，tint 指标与修复前一致，legacy 透明 0 像素变化。报告与 CSV 见 [T11Absorption/T11Verification.json](T11Absorption/T11Verification.json)（PNG 未重复保留）。

## 用例覆盖

### P0-1 屏幕路径与 fallback

- 折射开启时 Box / Sphere / Thin / Complex Lit / ShaderGraph Lit 均改变图像，非折射透明材质逐像素不变。
- 移动 opaque emissive backdrop 后玻璃内颜色跟随当前帧颜色金字塔，说明屏幕命中路径生效且没有被冻结的 probe 覆盖。
- 屏幕命中且 `weight=1` 时 fallback 权重为 0（`ScreenSpaceRefractionTransmission` 在 `remainingWeight=0` 时跳过 cubemap 采样）。
- **未覆盖**：单独构造“屏幕完全 miss、只有 probe/sky”截图。屏幕边缘硬拒绝已证明与 HDRP `EdgeOfScreenFade` 等价（P2-1 关闭），不再需要边缘带证据。

### P0-2 opacity 作为 transmittanceMask

- 0 / 0.3 / 1 三档在同一 box 玻璃内部取均值，单调收敛到表面颜色。
- 归一化 0.3 点误差 2.33%（阈值 5%），最小二乘 R² 0.9997（阈值 0.99）。
- opacity=1 时与“同一材质、关闭 Volume”的参照图差异为 0 像素，最大通道差 4.6e-5，确认没有残留颜色金字塔透射。
- `Intensity0`、`OffAgain`、`ModelsNone` 与 legacy reference 的比较仍通过，说明关闭效果/非折射材质回到 legacy 路径。

### P0-3 代理 fallback

- 关闭 probe 的 box projection：keyword 仍绑定（8 次），infinite 计数为 0，说明任意 active probe 的 bounds 被当作代理。
- 禁用所有 reflection probe：keyword 仍绑定（8 次），infinite/far-projection 计数 8，玻璃内仍出现折射变化，说明 URP 的 far-projection 扩展代理生效。
- 两种场景均无 NaN/Inf/error-shader 像素（T11 全局检查通过）。

### P1-1 / P1-2

- `IorToFresnel0` 的 1.0/1.5/2.5 解析值与测试断言一致（0 / 0.04 / 0.1837）；F0 仅在 `_REFRACTION_* && _SCREEN_SPACE_REFRACTION` 时覆盖，Volume 关闭保持旧 F0。
- Thin 的厚度与吸收距离均为 0.005；静态契约测试通过。
- 尚未采集 IOR 梯度图、有色 Thin Beer-Lambert 图与 HDRP 对照，故 `passed=null`，不声称完成 P1 验收。

## 未覆盖 / 待补

- P0-1 单独构造的“屏幕完全 miss、仅 probe/sky 透射”截图；P2-1 已复核关闭，边缘带不再列为缺口。
- P0-2 的 SimpleLit/Particles/Terrain/PBR/Unlit 逐字节回归，以及折射变体数量/指令数记录；本轮只验证了 legacy 透明材质 0 像素变化。
- P0-2 的雾场景（场景无雾），代码改为“表面光照单次 MixFog 后与未再雾化的透射混合”，需要雾场景补图。
- P0-3 的相机移动/分辨率切换 NaN 与 per-frame GC 测量。
- 所有 HDRP 像素级对照（P1-1/P1-2 的解析/结构指标已通过，但未做同参数 HDRP 截图）。
- P1-3 per-object/per-pixel probe、P2-2、P2-3、P2-4 未开始；P2-1 已复核关闭。

## 复现

关闭所有 Unity 编辑器后，在 `URPSample01` 目录运行：

```bash
SSL_VALIDATION_OUTPUT_ROOT="PLAN/Done/05-ScreenSpaceRefractionImprove/validation" \
"D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" \
  -batchmode -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingT11Verification.RunT11Verification \
  -logFile "PLAN/Done/05-ScreenSpaceRefractionImprove/UnityT11Verification.Run.log" -quit

"D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe" \
  -batchmode -projectPath "E:/GitHub/Glx.CustomURP/URPSample01" \
  -executeMethod ScreenSpaceLightingTestRunner.RunEditModeTests \
  -logFile "PLAN/Done/05-ScreenSpaceRefractionImprove/UnityEditModeTests.Run.log"

python PLAN/Done/05-ScreenSpaceRefractionImprove/validation/Analyze-05Evidence.py
```

注意：直接调用 Unity 不会像 `Invoke-T1Unity.ps1` 那样自动恢复 `ProjectSettings`；跑完后应把 `ProjectSettings/{Graphics,Quality,ProjectSettings}.asset` 恢复到开工前状态，或使用带快照恢复的包装脚本。
