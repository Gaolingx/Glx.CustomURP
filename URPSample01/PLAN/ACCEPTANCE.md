# Screen Space Lighting 逐轮验收（对照 git，2026-11-17）

> 验收对象：`PLAN/Done/01-ScreenSpaceLighting` … `06-ValidationAllRDC`
> 验收基线：`f85952c`；整改提交：`fa67325`、`f6f754e`、`726e57b`（本文件在其后提交）。工作区干净。
> 方法：每轮的 HANDOFF/计划声称 → git commit/diff → `validation/` 证据 → HEAD 代码。
> 结论：**所有轮次的代码交付均可在 HEAD 验证，未发现代码回归。** 初次验收发现 06 的捕获预算/路径漂移与若干文档矛盾；仓库所有者决定把 06 预算提高到 1 GB，并完成路径/文档整改（见 §11）。整改后 06 为 **PASS\***（`P6-2` 不再 FAIL），03 仍为 **PARTIAL**（Frame Debugger/T1 视觉证据缺失），其余轮次为 PASS\*。

## 0. 验收口径

| 标记 | 含义 |
|---|---|
| ✅ PASS | 声称有 commit、证据存在且 pass、代码在 HEAD |
| ✅* PASS | 核心成立，但有明确的文档/证据次级缺口 |
| ◐ PARTIAL | 核心交付存在，但该任务的一个验收项没有证据或未完成 |
| ○ ACCEPTED | 未完成/不可实现项被**如实记录**为 pending/accepted，不是虚假声称 |
| ❌ FAIL | 声称与仓库实际状态矛盾 |
| SUPERSEDED | 当时有效，但后来被新轮次替代；只要求保留历史，不作为本轮失败 |

判定不看 commit message，只看 diff、证据内容和 HEAD 代码。历史证据按“当时有效”判断；被后续轮次替代的行为标 SUPERSEDED。

## 1. 总览

| 轮次 | 结论 | 任务统计 | 首要缺口 |
|---|---|---|---|
| 01-ScreenSpaceLighting | ✅* | 9 PASS、7 PASS*、1 PARTIAL、1 SUPERSEDED | T8 supersession、Hi-Z 共享与 22-resource 已在 `726e57b` 修正；剩余为历史证据格式（T13 无 criteria、T12 FD 无顶层 passed） |
| 02-SSRImprove | ✅* | 5 PASS、3 PASS* | 16 个 `.log` 证据被归档提交删除且 `.gitignore` 排除；已在 §4.5 标注 log 本地-only，入库 XML/JSON/txt 为权威证据 |
| 03-SSRImprove2 | ◐ PARTIAL | 2 PASS、1 PASS*、3 PARTIAL | Frame Debugger `completed=false`；无 shader 编译检查；T1 的 A/B 与 missing-LUT fallback 无证据（已在 §6.1 明确标注） |
| 04-SSGIFallbackImprove | ✅* | 4 PASS、3 PASS*、1 PARTIAL | 运行时开关未真正跨帧验证（06 F-07 才修复）；1/4/32 探针的截图是同一文件；性能 0.153 ms 依赖“配对中位数”估计器（已在 `726e57b` 标注） |
| 05-ScreenSpaceRefractionImprove | ✅* | 1 PASS、1 PASS*、2 PARTIAL、8 ACCEPTED | 汇总 `Report.json`/`Cleanup.md` 的旧快照已在 `726e57b` 修正；433/434 EditMode 仍无日志；P0-1/P0-3 的探针回退辐射未被区分 |
| 06-ValidationAllRDC | ✅* | 21 PASS、2 PASS*、3 PARTIAL、1 ACCEPTED | 初次验收 `P6-2` 为 FAIL（52 个/963.9 MiB vs 150 MB/12）；所有者决定把预算提高到 1 GB，`.gitignore`/4 份报告已同步（`fa67325`），61 个路径已修正（`f6f754e`/`726e57b`）；P0-4/P2-3 仍为 PARTIAL |

## 2. 01-ScreenSpaceLighting

| 任务 | 结论 | Git / 证据 | 说明 |
|---|---|---|---|
| T1 基线协议 | ✅* | `f5a1171`、`5f8de13`；`T1Report.json t1ExitCriteriaMet=true` | HANDOFF 仍写“指向 Run15/16”，实际 HEAD 选中 Run17/18；数值本身可复现 |
| T2 早期 Motion/Depth + smoothness | ✅ | `6e9c540`；`T2Verification.json` 3/3 | `LitDepthNormalsPass.hlsl:122` 仍是 `0.5+0.5*smoothness` |
| T3 空框架/Volume/UI | ✅ | `5f8de13`；`T3Report.json true`、UI 13 PNG | — |
| T4 私有 Hi-Z/颜色金字塔/history | ✅ | `4f5cd44`、`4c7f22c`、`16b4f3a`；`T4Report.json true` | 203→400→501 顺序成立 |
| T5 独立 GTAO / 删除 SSAO | ✅ | `8c68dc0`、`79c4a12`；`T5Report.json true` | 后来 `770777a` 改为共享 Hi-Z；T5 的“重复显存”只对当时成立 |
| T6 SSGI | ✅* | `b9ddfb6`；`T6Report.json` 13/13 | 03 轮重写了 reprojection/denoise；T6 数值是历史快照 |
| T7 SSR Approximation | ✅* | `53cb975`；`T7Report.json` 14/14 | HANDOFF 引用的权重均值是 `2b38091` 修复前的；当前测试以 maxWeight vs fade 为准 |
| T8 SSR PBR 累积 | SUPERSEDED | `2120089`；`T8Report.json` 16/16 | 18 个累积 kernel 已被 02 的 `ReflectionDenoiser` 替换；T8 证据是历史归档，HANDOFF 未标 supersession |
| T9 材质覆盖 + coat | ✅ | `54295f6`、`9e7e4f2`；`T9Report.json` 13/13 | — |
| T10 透明 prepass + 透明 SSR | ✅ | `7a1645f`、`445a7ee`、`185b6cd`；`T10Report.json` 13/13 | — |
| T11 折射契约 | ✅ | `fa2bbbf`；`T11Report.json` 12/12 | — |
| T12 组合矩阵 | ✅* | `1b2f6e0`；`T12Verification.json` 34/34/35 | `FrameDebuggerReport.json` 没有顶层 `passed`，只有 per-mode 事件数 |
| T13 XR | ✅* | `b982ae5` 等；`T13Report.json t13ExitCriteriaMet=true` | 没有 per-criterion 对象；per-mode 结果完整 |
| T14 发布回归 | ✅* | `8bc2783`、`d98d5a7`；`T14Report.json` 9/9 | HANDOFF 说“17 个必需资源”，`T14PlayerBuildSummary.json` 实为 22 个，全部 present |
| GTAO 条带修复 | ✅* | `b9e3a73`、`7b3c490`、`df0d9a2`；`GtaoBandingFix/Report.md` | 34/34/35、153/153；报告“每个不同哈希都是 GTAO 用例”表述不精确 |
| SSR 射线原点修复 | ✅ | `2b38091`、`770777a`、`8a84724`；`SsrRayOriginFix` | T7/T8 after-fix 3/3，EditMode 154 |
| SSR 空洞填充 | ◐ PARTIAL | `fe02bf3`、`da77640`；`SsrHoleFilling` | 代码与 mean weight 可复现；标题数字 96,040→98,960 未出现在任何 committed artifact |
| SSR 拖动残影分析 | ✅ | `da77640`；`SsrGhostAnalysis` | 停止后 `pixels>0.02=0`，motion 0.03683 与报告一致 |

**轮次结论：PASS（带文档债）。** 14 个退出证据均存在且内部一致；四个缺陷修复均可映射到 commit。需要修正的三处：HANDOFF:206 的“Hi-Z 不合并”、T8 的 supersession 标注、T14 的 17→22。

## 3. 02-SSRImprove

| 任务 | 结论 | Git / 证据 | 说明 |
|---|---|---|---|
| T0 契约 | ✅ | `649fefe`（仅文档） | 15 个参数与 history 布局在代码中成立 |
| T1 ReflectionDenoiser | ✅* | `4d7697f`；`T1/SslShaderCheck.txt` 7 kernels、417/417 | “0 ShaderUtil 消息”与“416 baseline”无 committed artifact |
| T2 Kernel mapping LUT | ✅ | `ad146c9`；`MappingCheck.txt`、418/418 | PNG + HDRP `.meta` 在包内，单测锁定 |
| T3 半分辨率 | ✅ | `a6c60e1`；`T3/SslShaderCheck.txt` 12 kernels、419/419 | trace/history/upsample 与 `_SsrTraceSize` 均在 |
| T4 Volume/清理 | ✅ | `e3adb5d`（HANDOFF 未引用）；`T4/SslShaderCheck.txt` | 7 个 `[Obsolete]` 字段后被 T6 删除，属预期 |
| T5 验证 | ✅* | `9e0adee`；`T5Verification.json` PBR 30/30、miss 825,556/0、half 640×360 | Frame Debugger 只跑 PBR；“Approximation 2 事件”来自计数器而非 FD |
| T6 收尾 | ✅* | `4cf9489`、`ed4ed8b`（未引用）；`T6/*`、418/419 | 打包/Volume 检查成立 |
| AuditFix（SSGI 回归） | ✅ | `c02f851`；`AuditFix/SsgiTemporalCheck.json` 3/3 | history UV、jitter、LF、velocity 绑定均在 |

**轮次结论：PASS（证据债）。** 所有数值与 HANDOFF 一致。最大问题是**16 个日志被归档提交删除**且 `.gitignore:13` 排除 `*.log`，§4.4 的证据清单无法从 git 复现；另外多个实现 commit 未写入 HANDOFF。

## 4. 03-SSRImprove2

| 任务 | 结论 | Git / 证据 | 说明 |
|---|---|---|---|
| T0 契约 | ✅* | 无独立 commit，并入 `ca0f9ee` | 决策在代码中可见，但违反“每任务单独提交” |
| T1 SSR 蓝噪声 | ◐ PARTIAL | `ca0f9ee`；无 A/B artifact | 代码/资源/关键字在 HEAD；32 帧 A/B、missing-LUT fallback 未产出证据 |
| T2 `2c` 几何坐标 | ✅ | `ca0f9ee`；`T5` 参考图 md5 与 02 一致 | trace 与 denoiser 共用 `SslComputeSourceCoordinates` |
| T3 Forward+ 探针回退 | ✅ | `ca0f9ee`、`1358abf`；`T4/SsgiFallback/SsgiFallbackCheck.json` | 覆盖 0.306 vs 天空 0.383，探针 G 2.743，green/magenta 6.148 |
| T4 验证/证据 | ◐ PARTIAL | `ca0f9ee`；`FrameDebuggerReport.json completed=false` | FD 超时，HANDOFF 已如实披露；EditMode 418/418 |
| T5 收尾 | ◐ PARTIAL | `ca0f9ee`、`894e49d` | T14 已更新；02 HANDOFF 缺少“03 已接手”交叉引用 |

**轮次结论：PARTIAL。** 代码交付在 HEAD 正确；缺少 03 自己的 shader 编译检查、T1 视觉证据，FD 未完成。证据生成命令的 `OutputRoot` 硬编码到 02，无法按文档重新生成 03 的 T5 证据。

## 5. 04-SSGIFallbackImprove

| 任务 | 结论 | Git / 证据 | 说明 |
|---|---|---|---|
| P0 冻结开关契约 | ✅* | `7d137e2`；`P0/Contract.md` | 默认 false、opt-in、次帧生效；`rayMiss=Sky` 的“before”单元格描述有误 |
| P1 解耦 manager 生命周期 | ✅ | `3302908`；`P1/UnityEditModeTests.xml` 8/8 | 非 FP 按开关更新；FP 忽略开关；Cleanup 幂等 |
| P2 扁平探针列表 | ✅* | `f5f8d3d`；`P2/SslShaderCheck.txt computeMessages=0` | 两条 keyword 均存在；证据是手跑 artifact，无 committed producer |
| P3 Inspector | ◐ PARTIAL | `9c99a15`、`4158f8d`；`P3/UnityEditModeTests.xml` | 字段/HelpBox 已加；P3 XML 与 P1 是同一组 8 个测试，未覆盖 Inspector |
| P4 功能/压力/性能 | ✅* | `fff55c7`；`SsgiProbeFallbackVerification.json` | 绿 tint 2.564/-0.007、0.9997、FP 逐字节一致；`Stress_01/04/32.png` 是同一文件；0.153 ms 是配对中位数（差值中位数 0.389 ms） |
| P5 文档 | ✅ | `14bf1e3`；`T14KnownDifferences.md` | P5 行写“本提交”而非 hash |
| 归档 | ✅ | `f08d856` | 无临时场景/贴图入库 |

**轮次结论：PASS（带 caveat）。** 运行时开关的“次帧生效”没有被跨帧抓帧证明，且实际直到 06 F-07 才把 `publishProbeAtlas` 纳入历史 stamp；多探针混合由 06 P2-4 补证。多探针压力图相同这一点应在报告中显式标注。

## 6. 05-ScreenSpaceRefractionImprove

| 任务 | 结论 | Git / 证据 | 说明 |
|---|---|---|---|
| P0-1 miss probe 透射 | ◐ PARTIAL | `1f88271`；T11 `boxChangedPixels=5915`、legacy 0 | 代码/契约成立；screen-miss-only 与边缘带图未产出（已自述） |
| P0-2 opacity mask + 拆分 | ✅ | `1f88271`、`39d1d83`；`P0-2/fitting.csv` | 2.04%、R² 0.99975、opacity=1 子阈值 0 像素 |
| P0-3 代理 fallback | ◐ PARTIAL | `1f88271`；`P0-3/metrics.csv` | no-proxy/infinite 绑定各 8 次；`RefractionOn`/`Opacity0`/`NoProxyFallback` 的 sha256 同为 `3F4364CC…`，探针与天空的回退辐射不可区分 |
| P1-1 `_Ior`→F0 | ○ ACCEPTED | `1f88271`；`Report.json passed=null` | 解析值 1.0/1.5/2.5 = 0/0.04/0.1837；无 IOR 梯度图 |
| P1-2 Thin 0.005 | ○ ACCEPTED | `1f88271`；EditMode 断言 | 静态成立；无有色 Thin 截图 |
| P1-3 per-object 代理 | ✅* | `9328f09`、`8f80359`、`0fb6864` | CPU 2570/5392/0；player `debug_pixel` `(1,1,0,1)` vs `(0.138,0.119,0.104)`；GPU p95/边界序列 pending |
| P2-1 屏外 fade | ○ ACCEPTED | `33f7a33` | 公式等价性论证成立，无像素测试 |
| P2-2..P2-6 | ○ ACCEPTED | 无 | pre-refraction/贴图/FGD/动态分辨率/归档清理均如实标为 pending |

**轮次结论：PASS（pending 项已接受，文档债）。** 核心 P0-2 与 P1-3 有强证据。问题是汇总 `Report.json` 仍是 11-16 快照（`exitCriteriaMet=false`、blocker 说 P1-3 未开始），`Cleanup.md` 说“未创建 Player/未留 GPU 捕获”，与同目录 6 个 `.rdc` 和 `P13PlayerReport.json` 矛盾；433/434 EditMode 日志不存在。

## 7. 06-ValidationAllRDC

| 任务 | 结论 | Git / 证据 | 说明 |
|---|---|---|---|
| P0-1 通用 player | ✅* | `5cc5083`；`P0/Smoke/Report.json` 9/9 | 9 个 `.rdc`（171.3 MB）提交，已超预算 |
| P0-2 RDC 分析 | ◐ PARTIAL | `bc17c2c`、`84dd666` | CLI 冒烟通过，未脚本化（已自述） |
| P0-3 Binding probe | ✅ | `5cc5083`；9 × `BindingProbe_*.json mismatches=0` | 任务相关子集 14 项/用例 |
| P0-4 证据 schema | ◐ PARTIAL | — | 没有根 `Report.json`/`Report.md`/`BaselineHashes.json`；`exitCriteriaMet` 在 `P6/Results.json` |
| P1-1 F-01 | ✅ | `dfb2b3b`、`732576a`；`P1-1/Metrics.json` | 相关 0.99090→0.99983，MAD 1.005→0.144；`GTAOCommon.hlsl:88,97` 已是 `FULL_RES` |
| P1-2 F-03 | ✅ | `5113ebd`、`fac55ea`；`P1-2/Report.md` | 正交 0.18%/0.08%；参数与视线修复在 HEAD |
| P1-3 F-10 | ✅ | `5113ebd`、`fac55ea`；`P1-GTAO` | 16 用例/9 抓帧；contact 0.4175、open 0.9832、full↔half 0.99978、关闭 0 像素 |
| P1-4 材质覆盖/Deferred | ✅ | `189c713`、`717ccb1`；`P1-4` | Lit/ComplexLit/SimpleLit 0.93–0.95、BakedLit 0.77–0.79、Unlit/透明 0 差；单次乘积误差 0.18–0.99% |
| P2-1 SSGI 生效链 | ✅ | `c08e040`、`732576a`；`P2-SSGI` 10/10 | dispatch/history/LUT/atlas 与 CPU 快照一致 |
| P2-2 F-07 | ✅ | `56d3da7`、`6c4f2d9`；`P2-2` 7 用例 | `publishProbeAtlas` 等进入 stamp，切换后 validity=false |
| P2-3 F-09 | ◐ PARTIAL | `50b9fa0`、`6c4f2d9`；`P2-3/Metrics.json` | HF 0.5054、changed 0.1837 有 artifact；“1/16→16/16 tap”只在叙述里 |
| P2-4 SSGI 覆盖/探针/时域 | ✅ | `2e16fb3`、`665b31a`；`P2-4/Metrics.json` | 覆盖 57.1%、G 2.26×、残影 0.161/255、探针 4.6/4.3×、半全 0.9992 |
| P2-5 清理 | ✅ | `86e5ee6`；`P4-4/Report.md` | 并入 P4-4 |
| P3-1 F-02 | ✅ | `3d2b259`、`5905ced`、`69fd99b`；`P3-1/Player/Report.json` | 0.00160375/0.00320750，误差 0；SSR-only 无 SSGI marker |
| P3-2 F-04 | ✅ | `16ed7ff`、`719c210`；`P3-2/P3Census.json` | 7 dispatch；调试目标隔离，正式输出保留 |
| P3-3 F-06/F-08 | ○ ACCEPTED | `7c04f19`、`719c210`；`P3-3/P3Census.json` | 826,164 px 最大差 0；9844/9865 两变体共存；Y 翻转分支 D3D11 不可达，已记录 |
| P3-4 SSR 生效链 | ✅ | `69990d5`、`00368d5`；`P3-SSR/Player/Report.json` 12/12 | Approx 2、PBR full 6、half 7、透明 +2 |
| P3-5 SSR 正确性 | ✅ | `69990d5`、`00368d5`；`P3-5/Metrics.json` | 反射中心 0.87 px；空洞 100%/98% |
| P4-1 F-05 | ✅ | `9bf9dd5`、`158a17c`；`P4-1/Metrics.json` | 注入态 255→0；455/455 |
| P4-2 X1 | ✅ | `d26eb2f`、`221b5c2`；`P4-2/Metrics.json` | 60/60→0；Overlay/缺资源路径；456/456 |
| P4-3 X2 | ✅ | `8c38466`、`d976495`；`P4-3/Metrics.json` | 20 用例；render scale/MSAA 尺寸正确；Terrain 修好 |
| P4-4 F-11 | ✅ | `86e5ee6`、`ee78e17`；`P4-4/Report.md` | 死代码删除；455/455；0 error |
| P5 差异文档 | ✅ | `59ae531`；`KnownDifferences.md` | 四分类 + F-01..F-12 状态 |
| P6-0 回归 | ✅ | `abec5df`、`84dd666`；`P6/Results.json` | EditMode 455/455、PlayMode 6/6（首跑 3 失败，修后 6/6） |
| P6-1 汇总 | ✅ | `84dd666`；`P6/Results.json exitCriteriaMet=true` | 文件在 `P6/` 而非根 |
| P6-2 清理 | ✅* | `2983f08`、`84dd666`、`fa67325` | 初次验收超预算（52 个/963.9 MiB vs 150 MB/12）；所有者决定把预算提高到 1 GB，`.gitignore` 与 4 份报告已同步 |
| P6-3 归档 | ✅ | `84dd666`、`bcd399c`、`729fc50`、`f85952c` | `PLAN/TODO`→`PLAN/Done`；T12 34/34/35 回放通过 |

**轮次结论：PASS\*（代码 PASS；初次验收证据 FAIL，整改后预算/路径/文档已对齐）。** F-01..F-12 全部在 HEAD 可验证；P1–P5 的数值与 JSON 一致。P6-2 初次验收为硬失败（52 个/963.9 MiB vs 150 MB/12），所有者决定把预算提高到 1 GB 后关闭；61 个路径引用已修正。

## 8. 跨轮问题

### 8.1 捕获预算与证据卫生（初次验收 FAIL，已按所有者决定整改）

- 初次验收：06 提交了 **52 个 `.rdc`、963.9 MiB**；HEAD 全仓库 **58 个、1,048,717,328 B**；HANDOFF §7.5 冻结预算 **≤150 MB / ≤12 个**，P6-2 仍标“完成”；`git ls-files -i -c --exclude-standard '*.rdc'` 显示 48 个“已跟踪但命中 `.gitignore`”的文件。
- 整改（`fa67325`）：所有者决定把预算提高到 **1 GB**，保留全部抓帧；移除 06 的 `.gitignore` 捕获规则；更新 §7.5/§8.4、P6-2 行与 4 份报告。
- 整改后：`git ls-files '*.rdc'` = 58（06 52 个），`git ls-files -i -c --exclude-standard '*.rdc'` = **0**，总量 1,048,717,328 B（06 约 963.9 MiB），在 1 GB 内。
- 初次验收的 FAIL 结论保留为历史记录；**最终状态为 PASS\***（预算策略与仓库事实一致）。

### 8.2 路径漂移

- 初次验收：06 的 **61 个文件**含 `PLAN/TODO/06-ValidationAllRDC/...`；05 的 `P13PlayerReport.json`/`P13PlayerBuildSummary.json` 也嵌 `PLAN/TODO/05-...`。
- 整改（`f6f754e`/`726e57b`）：25 个 JSON + 05/06 的文档引用改为 `PLAN/Done`。剩余 3 个非日志引用是**历史说明/快照**：`05/HANDOFF.md:521,610`、`05/validation/Cleanup.md` 的归档前 git status 快照、`06/HANDOFF.md:706,717`；均在文中注明不代表当前路径。
- 83 个 `.log` 保留抓帧时的绝对路径，未改写；05/06 文档已注明“日志为历史记录，JSON/MD 指向 PLAN/Done”。

### 8.3 过时/矛盾文档

| 位置 | 问题 | 状态 |
|---|---|---|
| `01/HANDOFF.md:206` | 仍称“GTAO Hi-Z 与 SSL Hi-Z 不合并” | 已修正 `726e57b` |
| `01/HANDOFF.md` T8 | T8 证据未标 SUPERSEDED | 已修正 `726e57b` |
| `01/HANDOFF.md` T7 | 仍引用 `2b38091` 前的权重均值 | 已修正 `726e57b` |
| `01/HANDOFF.md:31` | “17 个必需资源”，实测 22 | 已修正 `726e57b` |
| `05/validation/Report.json`、`Report.md:81` | blocker 说 P1-3/P2 未开始 | 已修正 `f6f754e`/`726e57b` |
| `05/validation/Cleanup.md` | 说“未创建 Player/未留 GPU 捕获” | 已修正 `726e57b`，并注明 git status 为归档前快照 |
| `06/HANDOFF.md:65` | 说 T12/性能/10×/XR“仍未完成” | 已修正 `fa67325` |
| `06/HANDOFF.md:106` | 证据路径写成 `validation/06-ValidationAllRDC/` | 已修正 `fa67325` |
| `06/HANDOFF.md` §7.4 | 根 schema 与交付不一致 | 已加说明 `726e57b` |
| `04/P0/Contract.md` | `rayMiss=Sky` 的“before”描述错误 | 已修正 `726e57b` |
| `04/HANDOFF.md` 压力/性能 | 1/4/32 截图相同；0.153 ms 单估计器 | 已加注记 `726e57b` |
| `02/03/06` 证据缺口 | 未标明哪些是叙事/本地证据 | 已加注记 `726e57b` |

### 8.4 未被 git 跟踪的证据

- `02`：归档提交 `894e49d` **删除 16 个 `.log`**，`.gitignore:13` 又排除 `*.log`；已在 §4.5 标注 log 本地-only。
- `03`：`T4/*.log` 全部本地 only；`UnityImport.log` 与“唯一告警是缺尾换行”的叙述不一致；已在 §6.1 标注证据缺口。
- `04`：`P2/SslShaderCheck.txt` 是手跑 artifact，没有 committed producer；P3 XML 与 P1 是同一组测试，已按 PARTIAL 记录。

### 8.5 测试数量漂移

EditMode 153（T14）→ 418/419（02）→ 426（04）→ 455/456（06）是**增长**，不是回归。`05` 的 433/434 没有日志，但 06 的 455/455 与之不矛盾（套件扩大了）。

### 8.6 代码状态

未发现代码回归。所有声称的修复都在 HEAD：

- `GTAOCommon.hlsl:88,97` = `FULL_RES`；`GTAOCommon.hlsl:113-114` + `GTAO.compute:143` 正交；
- `SsrPass.cs:487-495` 像素展开角；`SsrPass.cs:426-428` 调试隔离；
- `TransparentDepthNormalRenderPass.cs:120-128` Y 翻转；`ScreenSpaceLightingResources.cs:139-145` 透明变体；
- `ScriptableRenderer.cs:1461-1483` 每相机重置；`SsgiPass.cs:905-927` stamp；
- `DiffuseDenoiser.compute:108-110` tap 翻转；死代码删除已生效。

## 9. 整改清单与状态

| ID | 优先级 | 动作 | 状态 |
|---|---|---|---|
| R1 | P0 | 06 捕获预算：所有者决定提高到 1 GB，保留全部 52 个抓帧；移除 `.gitignore` 冲突规则；同步 HANDOFF 与 4 份报告 | ✅ 完成 `fa67325` |
| R2 | P1 | 路径漂移：25 个 JSON + 文档引用改为 `PLAN/Done`；日志保留原路径并注明 | ✅ 完成 `f6f754e`/`726e57b` |
| R3 | P1 | 过时文档：01 T7/T8/Hi-Z/22、04 P0/压力/性能、05 汇总/Cleanup、06 §7.4/65/106 | ✅ 完成 `726e57b` |
| R4 | P2 | 补证据或降级声明：03 FD/T1、05 433/434、06 P2-3 tap、04 多探针 | ◐ 部分：已加 “narrative only” 注记；证据本身未补 |
| R5 | P3 | 归档规则：历史证据按当时有效，superseded 项需标注 | ○ 建议保留；06 已加历史路径说明，未建 `PLAN/README` |

> 本轮**没有删除任何 `.rdc`**；R1 改为提高预算，而不是清理捕获。

## 10. 结论

- **代码交付：全部通过。** 01–06 声称的功能、修复、共享绑定与材质消费在 HEAD 均可验证，没有发现回归。
- **证据完整性：06 在 1 GB 预算与路径整改后 PASS\*；03 仍 PARTIAL；01/02/04/05 为 PASS\*。** 剩余问题是历史日志未入库、个别验收项缺失或计数未机器化，不影响当时的代码结论，但影响“从 git 逐项复现”的承诺。
- **建议**：R4 的开放项（03 FD/T1、05 433/434、06 P2-3 tap JSON）要么补证据，要么保持 “narrative only” 标注；除此之外可宣称 public-review clean。

## 11. 整改记录（2026-11-17）

| Commit | 内容 |
|---|---|
| `fa67325` | 06 预算提高到 1 GB；移除 `.gitignore` 捕获规则；同步 HANDOFF 与 4 份报告 |
| `f6f754e` | 25 个证据 JSON 的 `PLAN/TODO` 路径改为 `PLAN/Done`；05 roll-up blockers 更新 |
| `726e57b` | 01/02/03/04/05/06 的过时/矛盾文档与证据缺口注记 |

验证：

- `git ls-files '*.rdc'` = 58（06 = 52），总量 1,048,717,328 B；`git ls-files -i -c --exclude-standard '*.rdc'` = 0。
- 非日志 `PLAN/TODO` 引用 = 3 个历史说明/快照；83 个 `.log` 保留抓帧时路径并已注明。
- `git status --short` 为空；未修改任何运行时代码。
- 未删除任何 `.rdc`。