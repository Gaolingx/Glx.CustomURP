# P2-2 SSGI 历史 stamp（F-07）（2026-11-17）

> 任务：P2-2。目标：切换 `publishProbeAtlas` 等影响历史语义的设置后，下一帧 `historyValidity=0`，旧 radiance 不跨配置残留；不影响历史的参数切换则不重置。
> 结论：**7/7 用例通过**。3 个被纳入 stamp 的字段（probe atlas、ray steps、depth thickness，共 5 个用例，覆盖 Forward/Deferred 两条渲染路径）在切换后下一帧 `historyValid=false`、reset 计数 +1、stamp 改变；`intensity` 与 `denoiserRadius` 作为对照，切换后 history 保持有效且 stamp 不变。

## 1. 修复

`ScreenSpaceLightingSsgiPass.ComputeSettingsStamp`（[SsgiPass.cs](../../../../Packages/com.unity.render-pipelines.universal@14.0.12/Runtime/RendererFeatures/ScreenSpaceLighting/Passes/SsgiPass.cs)）新增：

| 字段 | 纳入原因 |
|---|---|
| `publishProbeAtlas` / 有效 `probeList` | 决定 miss 回退从 cubemap 变为本地探针列表，旧 radiance 语义不同 |
| `maxRaySteps` | 决定光线找到哪个交点，改变“历史样本代表哪个表面” |
| `depthBufferThickness` | 同上，改变交点接受范围 |

保持不纳入并加注释：`intensity` 在材质采样最终纹理时应用；`denoiserRadius`、`bilateralUpsample` 只影响 CopyHistory 之后的输出整形。三者都不改变历史中存储的 radiance。

DEV 统计（`s_LastDispatchStats`）新增 `settingsStamp` 与 `settingsResetCount`（仅 stamp 不匹配导致的重置计数），验证 player 通过反射读取。stamp 在 pass 的 `Execute` 中随 `ResetSsgiHistory()` 一起更新，快照在 `Dispatch` 开头写入。

## 2. 用例与结果

Manifest：[P2Manifest.json](P2Manifest.json)。每个用例预热 16 帧使 history 有效，随后在协程中修改一个字段，等待 1 帧并读取快照。`Player/Report.json` 的 `ssgiSwitch` 字段记录切换前后值；[analyze.py](analyze.py) 复核并生成 [Metrics.json](Metrics.json)。

| 用例 | 渲染器 | 切换 | valid (前→后) | reset 计数 (前→后) | stamp (前→后) | 结果 |
|---|---|---|---|---|---|---|
| `forward_ssgi_probe_atlas_switch` | Forward | atlas off→on | true→false | 1→2 | `-534514056`→`-731027561` | ✅ |
| `forward_ssgi_probe_atlas_switch_back` | Forward | atlas on→off | true→false | 2→3 | `-731027561`→`-534514056` | ✅ |
| `forward_ssgi_raysteps_switch` | Forward | maxRaySteps 32→16 | true→false | 3→4 | `-534514056`→`-1680013720` | ✅ |
| `forward_ssgi_thickness_switch` | Forward | thickness 0.1→0.25 | true→false | 5→6 | `-534514056`→`-841133083` | ✅ |
| `deferred_ssgi_probe_atlas_switch` | Deferred | atlas off→on | true→false | 1→2 | `-534514056`→`-731027561` | ✅ |
| `forward_ssgi_denoiser_radius_switch` | Forward | radius 0.6→0.9（对照） | true→true | 7→7 | 不变 | ✅ |
| `forward_ssgi_intensity_switch` | Forward | intensity 1→2（对照） | true→true | 7→7 | 不变 | ✅ |

- 每次被 stamp 覆盖的切换都恰好重置一次；切换回原值使用同一 stamp，说明 stamp 是纯函数而非“变化计数器”。
- 重置帧仍执行完整 dispatch 链，只是 `historyValidity=0` 且读槽为 `-1`，因此不存在“用旧纹理做 validate”的窗口。
- Forward 与 Deferred 的 SSGI 都是 GBuffer/不透明阶段前的同一 pass，结论对两条路径一致。

## 3. 产物与复现

| 项 | 值 |
|---|---|
| Manifest | [P2Manifest.json](P2Manifest.json) |
| 分析 | [analyze.py](analyze.py) → [Metrics.json](Metrics.json) |
| Player 报告 | [Player/Report.json](Player/Report.json) |
| 截图 | `Player/*.png`（7 张；仅作视觉记录，判定来自 `ssgiSwitch`） |
| Player | `Builds/SslValidationPlayer/SslValidationPlayer.exe`（D3D11、Development、1280×720，本地构建，不入库） |

```bash
"E:/GitHub/Glx.CustomURP/URPSample01/Builds/SslValidationPlayer/SslValidationPlayer.exe" \
  -ssl-validation-manifest ".../validation/P2-2/P2Manifest.json" \
  -ssl-validation-out ".../validation/P2-2/Player" \
  -force-d3d11 -screen-width 1280 -screen-height 720 -screen-fullscreen 0
python analyze.py
```

本轮无需 RenderDoc：切换判定读取的是 pass 自己发布的 CPU 快照（含 stamp/重置计数/history 槽位）。

## 4. 回归

| 项 | 结果 |
|---|---|
| 最终构建重跑（含 P2-3 denoiser 修复） | **7/7**，[Metrics.json](Metrics.json) |
| 全量 EditMode | **454/454**（[P2-3/EditModeFull.xml](../P2-3/EditModeFull.xml)） |

## 5. 验收映射

| 标准 | 结果 |
|---|---|
| S7 stamp（F-07）：切换 `publishProbeAtlas` 后下一帧 validity=0，无跨配置残影 | 通过；`forward_ssgi_probe_atlas_switch` / `..._switch_back` / `deferred_...` 三项均为 true→false 且 reset +1 |
| 其余字段逐项评估 | `maxRaySteps`、`depthBufferThickness` 纳入；`intensity`、`denoiserRadius`、`bilateralUpsample` 不纳入并在代码中注释原因 |
| 对照（不重置） | `intensity` 与 `denoiserRadius` 切换后 history 保持有效、stamp 不变 |
