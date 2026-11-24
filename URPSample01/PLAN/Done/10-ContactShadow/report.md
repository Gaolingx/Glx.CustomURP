# T8 接触阴影验收报告

**结论：**Windows / RTX 5060 Ti / Unity 2022.3.62f1 上，Forward、Forward+、聚类 Deferred 均有实际渲染与 Player 证据。4 灯 10 步的六项 GPU p95 均低于 T0 的 1.0 ms 参考线。不能据此宣布全平台或历史回归全绿：D3D11 动态分辨率跳过，XR 未测，共享金字塔的取证不完整，旧 compute 变体断言与现实现不兼容。

## AC 判定与复现

路径相对 `validation/`。PASS 指在列明配置已验证；ACCEPTED 指在明确差异下接受，并不满足无限制的原命题；PARTIAL 指仍有未覆盖项或未通过的原门禁。下表命令使用 Windows PowerShell，从本报告所在目录开始：

```powershell
$v=(Resolve-Path ./validation).Path
$o=Join-Path $env:TEMP ('cs-review-'+[guid]::NewGuid())
function EditTest($f,$api='d3d11') { & "$v/Invoke-ContactShadowTests.ps1" -OutputRoot "$o/$api/$f" -Filter $f -GraphicsApi $api }
function LightTest($f,$api='d3d11') { & "$v/Invoke-ContactShadowLightingTests.ps1" -OutputRoot "$o/$api/$f" -Filter $f -GraphicsApi $api }
function Matrix($case,$api='d3d12') { LightTest "ContactShadowTests.PlayMode.ContactShadowT8MatrixTests.$case" $api }
function Audit { python "$v/T8/Audit/audit.py" }
```

| AC | 判定 | 实测证据与边界 | 复现命令 |
|---|---|---|---|
| 1 导入与持久化 | PASS | `T1/EditMode.xml`、`T1/FeatureImport.json` 和 T1 Inspector：菜单添加/移除，重载后资源引用及 Pipeline → Renderer 链保留。 | `& "$v/Invoke-ContactShadowTests.ps1" -OutputRoot "$o/import" -Filter ContactShadowFeatureImportTests -RecordImportEvidence` |
| 2 Volume | PASS | `T1/Volume.xml`、`T2/EditMode.xml` 检查无有效 Volume 的调度；`P5/D3D11/Direct3D11/Forward/volume-disabled.json` 与活动 Baseline 区分中性输出和真实暗化。 | `EditTest ContactShadowVolumeTests; LightTest ContactShadowRuntimeToggleTests` |
| 3 逐灯开关 | PASS | `T4/D3D11/EditMode.xml` 检查标记与槽；`P5/D3D12/Direct3D12/Forward/` 的 Main、Point、Spot 及各自 inactive A/B，覆盖主光及附加点/聚光。 | `EditTest ContactShadowLightTests; LightTest ContactShadowIntegrationTests d3d12` |
| 4 方向、痤疮 | PASS | `T8/Matrix/D3D11/ThreeViews.png` 及 Forward 的 view0、view-12、view12 JSON：+12° 背光 ROI 116 像素变暗、迎光区零差。`TraceBoundaries` 对未遮挡地面断言零命中。 | `Matrix ThreeViews; Matrix TraceBoundaries` |
| 5 shadow map 与 D10 | PASS | `P5/D3D11/Direct3D11/Deferred/D10-*.json`：当前点/聚光投影与当前灯 None、另一灯投影分别对照；Asset 关闭也有独立主/附加光对照。Forward 系不要求 shadow map，Deferred 主光同样独立。按修订 D10 验收。 | `LightTest ContactShadowDeferredLimitationTests; LightTest ContactShadowIntegrationTests` |
| 6 opacity | PASS | `T8/Matrix/D3D12/matrix.json`，无 shadow map 时 0/0.5/1 的 ROI 亮度降幅：Forward、Forward+ 为 0/9.964/19.217，Deferred 为 0/9.635/19.217。 | `Matrix OpacityAndMaterials` |
| 7 距离 fade | PASS | 同一 JSON，min/max=3/7，fadeIn/fade=2/2；距离 2.5、4、5、6、9.5 对应峰值 0、127、255、127、0。远端淡出在 max 处结束，不是从 max 才开始。 | `Matrix DistanceSequence` |
| 8 屏边 fade | PASS | 同一 JSON，y=151/177/182/186/190 的命中样本平均 fade 为 255/247.03/193.81/112.78/19.19，边缘渐弱。 | `Matrix EdgeBands` |
| 9 槽与隔离 | ACCEPTED | `SlotIdentityAndOverflow` 记录 1/4/24/25 灯、有/无主光的实际映射；第 25 个候选无额外槽。`ForwardPlus/one-marker.json` 的独立 receiver24 零差；拥挤场景该 ROI 有 3 像素共享最大 fade 改变（255→246/244/242），不是串 bit，也不能称重叠处逐像素不变。 | `Matrix SlotIdentityAndOverflow; EditTest ContactShadowSlotMappingTests` |
| 10 材质排除 | PASS | 三模式 `OpacityAndMaterials`：透明、Receive Off 等排除区 A/B 相同，替换为接收阴影 Lit 后同坐标变暗；`P5` 三模式 `Receive-False.json`/`Receive-True.json` 对照。BakedLit、自定义光照不保证覆盖。 | `Matrix OpacityAndMaterials; LightTest ContactShadowReceiveShadowsTests` |
| 11 三模式 mask | PASS | 两个 `T8/Matrix/*/matrix.json` 的 `MaskAcrossModes`：相对 Forward，Forward+、Deferred 不同 mask 像素均为 0；比较 compute mask 而非最终颜色。 | `Matrix MaskAcrossModes d3d11; Matrix MaskAcrossModes d3d12` |
| 12 包修改边界 | PASS | 相对 `cfbb3d4`，既有功能源码仅 `ShaderLibrary/Shadows.hlsl`、`Shaders/Utils/ClusteredDeferred.compute` 改动；既有核心 C# 未改。功能目录、独立测试和新增头文件由审计分开检查。 | `Audit` |
| 13 无光追 | PASS | `T8/Audit/audit.py` 检查本轮包内新增源文件及既有文件的新增行；不以仓库其他功能（如 DDGI）的匹配判失败。 | `Audit` |
| 14 共享深度 | PARTIAL | `T8/Audit/SharedPyramid.json`：D3D11 非 XR 96×64，Contact-only 和 Contact+SSL+GTAO 各一次 pass 203、一次 mip0 copy；双 API 粗命中须同一步 mip0 确认。Frame Debugger 不支持、事件数 0；未逐眼测 XR。 | `EditTest ContactShadowPyramidRenderTests; EditTest ContactShadowMipConfirmationTests d3d12; Audit` |
| 15 回归与性能 | PARTIAL | P0 fixture 子集 `T8/Regression/EditMode.xml` 184/184、PlayMode 6/6；`T12.json` 三模式通过。它们不能替代被保留的旧 shader 测试；1280 与 2560 的分歧、配置矩阵、构建与性能见下。 | `python "$v/T8/Regression/replay.py" --stage regression; python "$v/T8/Regression/replay.py" --stage shader; python "$v/T8/Regression/replay.py" --stage performance` |
| 16 证据与收尾 | PASS | 已移至 Done，与[总验收 §24](../../ACCEPTANCE.md)及[已知差异](KnownDifferences.md)同步。29 个唯一 PNG、249272 字节，历史路径按哈希解析；本轮新增 RDC 为零，不是全库为零。 | `Audit; git status --short; git ls-files '*.rdc'; git ls-files -ci --exclude-standard -- '*.rdc'` |

## 矩阵配置与 Player

- [D3D11](validation/T8/Matrix/D3D11/matrix.json)：12 通过、1 跳过。请求 75% DRS 后实际宽仍为 256，不能用原生分辨率结果代替。 [D3D12](validation/T8/Matrix/D3D12/matrix.json)：13 通过；DRS 实宽 192、暗化 188 像素。两端正交相机暗化 286 像素，4× MSAA 实际 targetSamples=4、暗化 279 像素；Scene View 独立通过，两端设置已还原。多相机见 `T6/*/Lighting.xml`，不等同于 XR。4/10/64 步默认 thickness/bias 的命中为 131/279/318；厚度 0.02/1 为 252/279，bias 0/1 为 303/186。
- 上述不是全排列：`ContactShadowT8MatrixTests` 的正交/MSAA/DRS、三视角、距离、边缘和 Scene View 均用 Forward 单主光（`Light.shadows=None`）；三模式对照用于 opacity/材质与 mask，25 灯拥挤隔离对照用 Forward+。性能三模式全矩阵另见下节。
- 整组复跑：`LightTest ContactShadowTests.PlayMode.ContactShadowT8MatrixTests d3d12`，再单独用 `d3d11`。严格 PowerShell 包装器遇 skip 会报未全过，以新 `Lighting.xml` 的具体原因判读，不忽略异常。`T8/Matrix/summarize.py` 读取脚本同目录两端的 `Lighting.xml`、`LightingSettings.json`；若要在临时输出生成摘要，应把脚本及这两组输入按原目录结构复制到临时目录执行。
- [Player 矩阵](validation/T8/Player/results/matrix.json)：Win64 Development/Release × D3D11/12 均 build/player exit=0、buildErrors=0，不等于零警告；分类见 `results/diagnostics.json`。各 `player.json` 检查 requested/actual、深度与接触 kernel、debug 及活动/关闭 ROI，而非仅验证能启动。复跑 `python "$v/T8/Player/tooling/matrix.py" --output "$o/player"`，输出目录须尚不存在，项目在临时目录构建。

## T0 性能参考线与变体门禁

[11 的 GPU 结果](validation/T8/Performance/D3D11.json) / [12 的 GPU 结果](validation/T8/Performance/D3D12.json)：窗口化 Development Player，1920×1080，同一 stress 相机；每格预热 60 帧、采 120 帧，模式 × 1/4/24/25 灯 × 4/10/64 步。下表仅取 **4 个有效槽、10 步**；GPU marker 仅包围 Contact compute，不含 pyramid、cleanup 或整帧。单位 ms（median / p95），各格 120 个有效样本。

| API | Forward | Forward+ | Deferred | T0 ≤1.0 ms |
|---|---:|---:|---:|---|
| D3D11 | 0.488448 / 0.494592 | 0.488960 / 0.492288 | 0.484608 / 0.499968 | 三模式 p95 达标 |
| D3D12 | 0.522752 / 0.526336 | 0.518400 / 0.520704 | 0.502016 / 0.520960 | 三模式 p95 达标 |

这不是满槽预算：例如 D3D12 Deferred、25 灯/24 槽、64 步 p95 为 13.918 ms。`BatchCPU-*.json` 是另一轮 batch Player，未采到 GPU marker；`null` 非零耗时。分开复跑 CPU：`python "$v/T8/Regression/replay.py" --stage performance --batch-cpu`。replay 在仓库外建 scratch，但会写回对应证据文件；需保留原件时使用独立工作副本。

**历史变体门禁未全绿。** `ClusteredDeferredShaderTests.ComputeDeclaresTheDocumentedKeywordMatrix` 保留原样，要求原八轴合计 1280；实际新增 compute-only `_CONTACT_SHADOWS_DEFERRED` 二选一，使 off 路径不声明接触纹理，现为九轴 2560。`ContactShadowShaderBudgetTests` 是新增独立断言，分别钉住原八轴和完整矩阵，不是放宽旧阈值，也不能抵消旧测试。`T8/Regression/ShaderComparison.json` 比较双 API 五种材质 shader 的关键字及 SRP Batcher 结果；`PlayerStripping.json` 的 Lit ForwardLit 实际剥离后 VP/FP 为 16/4096，双 API build 的 clustered compute 5120/5120 是构建汇总，不是单 API 声明数。编译诊断、材质比较和变体预算须分别判读。当前 `replay.py --stage shader` 会核对旧测试源码与 Git blob，并要求双 API 都只出现指定旧断言的失败；重放工具认可这个预期失败，不等于 NUnit 全绿。独立核验可运行 `EditTest ContactShadowShaderBudgetTests; EditTest ClusteredDeferredShaderTests`，后者的失败应保留。

## 证据定位

`P0`、`P3`、`P5` 是 T0/T3/T5 的历史目录名。旧 XML、截图记录和复跑生成文件保留原路径语义；查 [PNG 索引](validation/T8/Audit/PngArchive.json) 中的 `aliases`，按同条 `canonical`、`sha256`、`bytes` 取实际文件。`P5/PreIntegration.json` 的每模式 `archivedOff` 指向保留的 SHA 对照；不要因为 Forward+ 的历史副本已去重而跳过比较。日志、scratch 和可执行文件不作为入库证据。归档位置：`PLAN/Done/10-ContactShadow`。
