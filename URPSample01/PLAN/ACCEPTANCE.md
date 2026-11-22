# Screen Space Lighting 逐轮验收（对照 git，2026-11-17）

> 验收对象：`PLAN/Done/01-ScreenSpaceLighting` … `06-ValidationAllRDC`
> 验收基线：`f85952c`；整改提交：`fa67325`、`f6f754e`、`726e57b`（本文件在其后提交）。工作区干净。
> 方法：每轮的 HANDOFF/计划声称 → git commit/diff → `validation/` 证据 → HEAD 代码。
> 结论：**所有轮次的代码交付均可在 HEAD 验证，未发现代码回归。** 初次验收发现 06 的捕获预算/路径漂移与若干文档矛盾；仓库所有者决定把 06 预算提高到 1 GB，并完成路径/文档整改（见 §11）。整改后 06 为 **PASS\***（`P6-2` 不再 FAIL），03 仍为 **PARTIAL**（Frame Debugger/T1 视觉证据缺失），其余轮次为 PASS\*。
>
> **独立复核（2026-11-17，§12）**：6 个只读子代理逐轮重验 + RenderDoc MCP 抽查。代码交付 01–06 仍无回归；但 §1–§11 有若干**评级/统计/证据债**需要修正（06 的 P0-3/P0-2 描述、04 的任务计数、05 的 P0-2/P1-1/P1-2、03 的 T3 指标、01 的 T14 脚本与 T10/T13 数字）。§12 只追加修正，不改写历史结论。

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
| 11-LightCulling | ✅* | 5 PASS、2 PASS*、1 PARTIAL、1 ACCEPTED | mixed lighting-shadowmask 为 PARTIAL（无 baked shadowmask 数据）；T2/T3 的逐元素 parity JSON 未单列，由 T4/T5/PlayMode A/B 覆盖；T6 可选未补 GBuffer |
| 12-GpuLightList | ✅ PASS | 2 PASS、2 ACCEPTED（NO-GO） | 默认同步 GPU 列表 + 位图；G2/G3 实现保留为默认关闭的 A/B；透明/forward-only 接管未覆盖 |
| 13-GpuLightList256 | ✅ PASS | 1 PASS（G2/G3 仍 NO-GO） | 256 灯 parity 成立、zbin 409 档；clustered GPU 1.270 vs stencil 1.763 ms；256 灯 A/B 需改用 Forward+ 参照 |

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

---

## 12. 独立逐轮复核（2026-11-17，subagent）

> 方法：6 个只读子代理分别对 01–06 提取任务/验收项 → `git log/show/ls-files` → committed evidence → HEAD 代码；主线用 `renderdoc_mcp` 抽查 05/06 抓帧，并复核高危结论。未修改代码、未删除证据。
> 判定沿用 §0：PASS / PASS\* / PARTIAL / ACCEPTED / FAIL / SUPERSEDED。历史证据按“当时有效”判断，被后续轮次替代的行为标 SUPERSEDED。
> 结论：**代码交付 01–06 全部 PASS，未发现声称的修复在 HEAD 缺失。** 证据/汇总层有下述修正；§1–§11 的评级与统计不全部成立。

### 12.1 总览修正

| 轮次 | §1–§11 原结论 | 独立复核 | 主要修正 |
|---|---|---|---|
| 01-ScreenSpaceLighting | PASS\* | PASS\*（7 PASS、10 PASS\*、1 SUPERSEDED，共 18） | T14 分析脚本仍断言独立 Hi-Z；11 个 T14 日志被 `6606cfb` 删除；T10/T13 数字与范围 |
| 02-SSRImprove | PASS\* | PASS\*（3 PASS、4 PASS\*、1 PARTIAL，共 8） | T6 降 PARTIAL：06 把 debug enum 14–17 重编号为 13–16；16 个日志可从 `894e49d^` 取回 |
| 03-SSRImprove2 | PARTIAL | PARTIAL（2 PASS\*、4 PARTIAL） | T3 的“probe G 2.743”实为 magenta R 2.7485；T1 零证据；T4 FD 原因错误 |
| 04-SSGIFallbackImprove | PASS\* | PASS\*（1 PASS、5 PASS\*、1 PARTIAL，共 7；原统计写成 8） | 0.389 ms 是中位数之差；`3302908` 违反插件隔离 |
| 05-ScreenSpaceRefractionImprove | PASS\* | 代码 PASS；证据/汇总 PARTIAL（1 PASS\*、3 PARTIAL、8 ACCEPTED、1 FAIL、1 Unverifiable） | P0-2 CSV 过期；P1-1/P1-2 图片重复；汇总生成器硬编码 `exitCriteriaMet=False` |
| 06-ValidationAllRDC | PASS\* | PASS\*（16 PASS、8 PASS\*、3 PARTIAL，共 27） | P0-3 “14 项/用例”不实；P0-2 已脚本化；1 GB 单位；P3-3 应为 PASS\* |

### 12.2 逐轮逐项

#### 01-ScreenSpaceLighting

| 任务 | 复核 | 备注 |
|---|---|---|
| T1 | PASS\* | `Reproducibility.json` 选 Run17/18；HANDOFF:8 已改，但 :551-552 仍写 Run15/16 |
| T2 / T3 / T4 | PASS | — |
| T5 | PASS\* | 现在的 GTAO 使用共享 Hi-Z；T5 的“重复显存”只对当时成立 |
| T6 | PASS\* | 03 重写 reprojection/denoise；T6 数值是历史快照 |
| T7 | PASS\* | 权重均值是 `2b38091` 前的；已披露 |
| T8 | SUPERSEDED | 18 个累积 kernel 已被 02 的 `ReflectionDenoiser` 替换；HANDOFF:15 已标，plan:495 未标 |
| T9 / T11 | PASS | — |
| T10 | PASS\* | 文档 9,560 / 2,314 px；JSON 实为 13,619 / 3,580 |
| T12 | PASS\* | FD 报告无顶层 `passed` |
| T13 | PASS\* | 文档范围 214k–232k / 45k–82k；JSON 193,003–328,849 / 38,903–81,914 |
| T14 | PASS\* | 必需资源 22（plan 仍写 17）；11 个日志被 `6606cfb` 删除；6 个 JSON 含 `PALN/` |
| GTAO 条带 | PASS\* | “每个不同哈希都是 GTAO 用例”不精确：73/103，含 21 个关闭基线 |
| SSR 射线原点 | PASS | — |
| SSR 空洞填充 | PASS\* | 96,040→98,960 / 2,920 只在 `Report.md`，无 artifact |
| SSR 拖动残影 | PASS | — |

**01 首要缺口**：`Analyze-T14Evidence.py:52` 仍写“GTAO 与 SSL 各自拥有 packed Hi-Z”，与 `T14KnownDifferences.md:35` / HANDOFF:206 矛盾；按文档重跑会重新生成过时结论。

#### 02-SSRImprove

| 任务 | 复核 | 备注 |
|---|---|---|
| T0 | PASS\* | 契约在代码中成立；“冻结先于代码”Unverifiable |
| T1 | PASS\* | 7 kernels/417 成立；`ShaderUtil=0` 与 416 baseline 只在被删日志里，可从 `894e49d^` 取回 |
| T2 | PASS | PNG 与 HDRP 逐字节一致；文档漏了 NoV=1 的 0.008 |
| T3 | PASS\* | half-res 成立；`2c` 统一到 03 `ca0f9ee` 才与 HDRP 一致 |
| T4 | PASS | 弃用字段后被 `ed4ed8b` 删除，属预期 |
| T5 | PASS\* | 30/30、825,556/0、640×360 成立；Approximation 的“2 事件”来自计数器，FD 只跑 PBR |
| T6 | **PARTIAL** | `ScreenSpaceLightingDebugMode` 由 14–17 改为 13–16（06 `86e5ee6`）；HANDOFF:421 “14 以上不变”已假，`T6/VolumeOptionsCheck.txt` 与 HEAD 不一致 |
| AuditFix | PASS | 3/3；三渲染器数值相同未解释 |

**02 首要缺口**：T6 的重编号没有在 02 的 HANDOFF/ACCEPTANCE 中交叉引用；旧 Volume/材质若选 14–17 会解析到别的调试视图（06 P4-4 已记录“场景资产全为 0，无迁移”）。

#### 03-SSRImprove2

| 任务 | 复核 | 备注 |
|---|---|---|
| T0 | PARTIAL | 无独立 commit，全部并入 `ca0f9ee` |
| T1 | **PARTIAL** | 代码/资源/关键字在 HEAD；32 帧 A/B、缺 LUT fallback、03 shader check **全部无证据** |
| T2 | PASS\* | `2c` 统一；参考图是 SSR 关闭路径，md5 一致不能证明 PBR 全分辨率不变 |
| T3 | PASS\*（non-FP 半 SUPERSEDED） | `probeGreenTint=2.5917`、`magentaMeanR=2.7485`、`probesMean=(0,0,0)`；文档“probe G 2.743”把 magenta R 写成 green。Forward/Deferred 的默认 cubemap 行为已被 04 `SSL_PROBE_LIST` 替代 |
| T4 | PARTIAL | FD `completed=false`；真实原因是 `locallySupported=False`，不是 GUI 卡住；`UnityImport.log` 有 licensing error + 2 条 YAML GUID 警告 |
| T5 | PARTIAL | `ScreenSpaceLightingSsgiTemporalCheck.Run` 不存在（实际 `RunSsgiTemporalCheck`）；`OutputRoot` 硬编码到 02；fallback 无命令 |

**03 首要缺口**：T1 没有任务专属证据；文档无法按原命令重新生成 03 证据。

#### 04-SSGIFallbackImprove

| 任务 | 复核 | 备注 |
|---|---|---|
| P0 | PASS\* | 默认 false/opt-in 成立；`Sky` 的 before 单元格原描述错误，已修 |
| P1 | PASS\* | 8/8；`3302908` 在 core `UniversalRenderer` 引用插件，违反 `PluginIsolationTests`，`4158f8d` 修复；生产路径走未测的 lazy 分支 |
| P2 | PASS\* | `SSL_PROBE_LIST` 成立；`SslShaderCheck.txt` 无 producer，且一半是源码 grep |
| P3 | PARTIAL | 字段/HelpBox 在 HEAD；无 Inspector 证据，P3 XML 与 P1 是同一组 8 个测试 |
| P4 | PASS\* | 2.56434/−0.00714、0.99966、FP 逐字节、0.15278 均复现；Stress 1/4/32 同一文件；0.389 是中位数之差，配对中位数才是 0.153（n=8，含 −2.65 outlier） |
| P5 | PASS\* | T14 条目在；“runtime switching next frame”在当时代码下为假 |
| 归档 | PASS | 无临时资产 |

**04 首要缺口**：运行时开关未做图像级跨帧证明；默认 temporal 下缺少 stamp，实际直到 06 F-07 才正确。

#### 05-ScreenSpaceRefractionImprove

| 任务 | 复核 | 备注 |
|---|---|---|
| P0-1 | PARTIAL | 代码/fallback 成立；screen-miss-only、tinted probe、edge-band 均无 artifact；criterion 7 仅文本 |
| P0-2 | **PARTIAL**（原 PASS） | 2.04%/0.99975 只在 `T11Hotfix/*_OpacitySweep.csv`；`P0-2/fitting.csv` 是 2.335%/0.999676 的旧快照；criterion 4/6/7 无 artifact |
| P0-3 | PARTIAL | `3F4364CC…` 三个状态都启用探针，只证明 `boxProjection` 无效，不证明 probe/sky 不可区分；`InfiniteProxy` 哈希不同 |
| P1-1 | ACCEPTED | 解析值成立；无梯度图 |
| P1-2 | ACCEPTED | 代码成立；`P1-2/*_Tinted.png` 与 P0-1 逐字节相同，且材质是 `_REFRACTION_PLANE`，不是 Thin |
| P1-3 | PASS\* | CPU/玩家 `debug_pixel` 成立；GPU p95、边界序列帧缺失；GC 采样排除了 30 帧刷新帧；`P13Player.log` 未跟踪 |
| P2-1 | ACCEPTED | 公式等价性复核成立 |
| P2-2…P2-6 | ACCEPTED | 如实 pending |
| 汇总 `Report.json` | **FAIL（作为 roll-up）** | `Analyze-05Evidence.py:239` 硬编码 `exitCriteriaMet=False`；editMode 170；无 P1-3 case；`Report.md:12` 说 P1-3 未开始，`:81` 说已完成；`Cleanup.md:4` 仍 TODO |
| EditMode 433/434 | Unverifiable | 只有 170 入库；未跟踪日志报 173 |

**05 首要缺口**：P1-1/P1-2 的证据目录是 P0-1 图片的副本；P0-2 的引用 CSV 与热修后数值不一致；汇总生成器结构上无法通过。

#### 06-ValidationAllRDC

| 任务 | 复核 | 备注 |
|---|---|---|
| P0-1 | PASS | 9/9 smoke；3 渲染器 asset；manifest 驱动 |
| P0-2 | PARTIAL | RDC 分析**已脚本化**（P1-4/P2-SSGI/P3 等），但按任务定制且硬编码仓库外 `renderdoc-cli.exe` |
| P0-3 | PASS\* | smoke 用 35 字段，适用数 14(GTAO)/8(SSGI)/7(SSR)/6(折射)/0(off)，不是“每用例 14”；HEAD 已增至 37 |
| P0-4 | PARTIAL | 无根 `Report.json/Report.md/BaselineHashes.json`；`exitCriteriaMet` 只在 `P6/Results.json:83`；HANDOFF:381 仍写“完成”并含死路径 |
| P1-1 / P1-3 / P1-4 | PASS | F-01、F-10、材质覆盖均在 HEAD 与 artifact |
| P1-2 | PASS\* | 正交 0.18%/0.08% 成立；`P1-2/` 只有 `Report.md`，数字在 `P1-GTAO/Metrics.json` |
| P2-1 / P2-2 / P2-4 / P2-5 | PASS | — |
| P2-3 | PARTIAL | `0.5054/0.1837` 可复现；“1/16→16/16 tap”只在叙述里，无 `debug_thread` |
| P3-1 | PASS\* | 0.00160375/0.00320750、6/7 dispatch 成立；0.5 px/帧平移的 ≥50% history 接受率未测 |
| P3-2 | PASS | 调试帧 7 dispatch，正式输出隔离 |
| P3-3 | **PASS\***（原 ACCEPTED） | F-06/F-08 已 Fixed 且有 GPU 证明（826,164 px 最大差 0；9844/9865 两变体）；只有 Y 翻转分支 Accepted |
| P3-4 / P3-5 | PASS | 12/12；反射 0.87 px、空洞 100%/98% |
| P4-1 / P4-2 / P4-4 | PASS | 泄漏 255→0、60/60→0、死代码删除 |
| P4-3 | PASS\* | 20 用例，不是 3×4×2 全矩阵；4 个 `P4-3/Terrain`、`P4-3/Diag` 悬空路径（目录不存在） |
| P5 | PASS | 四分类 + F-01..F-12；F-06 的 `0.006142>0.003819` 误引 01/T10 数字 |
| P6-0 / P6-1 | PASS\* | 455/455、6/6、T12 34/34/35；R2 pan 与 R6 sample-count 饱和未测 |
| P6-2 | PASS\* | 58 `.rdc` / 1,048,717,328 B（0.977 GiB，**十进制 1 GB 超 4.9%**）；`.rdc` 的 ignored-but-tracked=0，但全仓库为 **14 个 `.log`**；45 个 06 日志被忽略；3 个 P6 日志被 `84dd666` 删除 |
| P6-3 | PASS | 归档；T12 回放通过 |

**06 首要缺口**：P0-4 的根汇总从未落地；证据卫生仍有 45 个忽略日志、3 个被归档提交删除的 P6 日志、4 个悬空路径；两个 §7.2 数值项（R2 pan、R6 sample count）未测却被 P6 宣称“thresholds Met”。

### 12.3 跨轮问题

| ID | 问题 | 证据 | 影响 | 建议 |
|---|---|---|---|---|
| X-1 | ACCEPTANCE 的若干行未随 `726e57b` 刷新 | 01 的 Run15/16、T8、17 资源；05 的 blocker | 读者会得到过期结论 | 以 §12 为准；下次整改时同步 §1–§11 |
| X-2 | 后续轮次 break change 未回写旧轮文档 | 02 T6 enum 14–17→13–16；01 T14 Hi-Z；03 non-FP superseded；04 runtime toggle | 旧 HANDOFF 与 HEAD 矛盾 | 在旧 HANDOFF 加 SUPERSEDED/重编号注记 |
| X-3 | 日志证据链断裂 | 11 个 T14 日志（`6606cfb`）、16 个 02 日志（`894e49d`）、3 个 P6 日志（`84dd666`） | fresh clone 无法复现部分叙述 | 从历史 commit 取回或标 local-only；不要声称“全部保留” |
| X-4 | 路径漂移与悬空引用 | 6 个 `PALN/` JSON；4 个 `P4-3/Terrain`、`P4-3/Diag`；3 个 P6 日志链接 | 文档命令/链接不可用 | 批量修正或标历史快照 |
| X-5 | 数值只在叙述里 | 03 probe G、05 P0-2、01 T10/T13、06 P2-3 tap | 无法机器复核 | 补 JSON 字段或降级为 narrative-only |
| X-6 | 测试计数已验证 | 455/455、6/6、34/34/35；58 `.rdc`/1,048,717,328 B | — | 保持 |

### 12.4 GPU 独立抽查（RenderDoc MCP）

| 抓帧 | 检查 | 结果 |
|---|---|---|
| `05/.../P13_CameraAtA_Both_frame162.rdc` | EID 786 玻璃球，`debug_pixel(640,359)` | `o0=(1,1,~0,1)`，382 steps，与归档 `(1,1,0,1)` 一致 |
| `06/.../P3-1/forward_ssr_pbr_full_ssgi_off` | `Screen Space Reflections` pass | 6 dispatch，无 SSGI marker |
| `06/.../P1-1/forward_gtao_full_after` | GTAO pass | 3 dispatch（full） |
| `06/.../P1-1/forward_gtao_half_after` | GTAO pass | 4 dispatch（half，+upsample） |
| `06/.../P3-2/forward_ssr_pbr_full_debug_params` | SSR pass | 7 dispatch（正常 6 + 调试 1） |

### 12.5 结论与整改

**代码交付**：01–06 全部 PASS；F-01…F-12 的修复均在 HEAD 可验证；未发现声称的修复缺失。

**证据/汇总**：

- 01 PASS\*（文档/日志债）；02 PASS\*（T6 PARTIAL）；03 PARTIAL（T1/T4/T5 证据）；04 PASS\*（开关未图像级证明）；05 代码 PASS、证据 PARTIAL、roll-up FAIL；06 PASS\*（P0-4/P2-3 PARTIAL，证据卫生债）。
- 尚不能宣称“public-review clean”：ACCEPTANCE 有过期行、若干日志/路径不可复现、若干数值只在叙述里。

**建议整改（按优先级）**

1. **R1**：修 `Analyze-T14Evidence.py:52` 的独立 Hi-Z 文本，避免重跑生成过时结论。
2. **R2**：在 02 T6 行/02 HANDOFF 标注 debug enum 14–17→13–16；这是 06 已记录的 break change，不是回归。
3. **R3**：替换或标注 `P0-2/fitting.csv`；把 2.04%/0.99975 指向 `T11Hotfix/T11_*_OpacitySweep.csv`。
4. **R4**：把 05 的 P1-1/P1-2 图片标为“复用 P0-1，无任务专属测量”，或补 IOR 梯度/有色 Thin。
5. **R5**：修 05 汇总生成器（读最终 T11/EditMode、计算 `exitCriteriaMet`、加入 P1-3 case），同步 `Report.md:12` 与 `Cleanup.md:4`。
6. **R6**：修 06 的 P0-4/HANDOFF:381（根 schema 或明确只在 `P6/`），以及 4 个 `P4-3` 悬空路径和 3 个 P6 日志链接。
7. **R7**：补 06 R2 pan 接受率与 R6 sample-count 饱和的 artifact，或把 P6 的“7.2 thresholds Met”降级为“已测项 Met”。
8. **R8**：把 06 的“1 GB”写成 `1 GiB`，把“ignored-but-tracked=0”限定为 `.rdc`；全仓库仍有 14 个被忽略但跟踪的 `.log`。

**复现命令**

```bash
# 逐轮代码/证据
git log --oneline --all -- URPSample01/PLAN/Done/<round>
git ls-files URPSample01/PLAN/Done/<round>/validation
git show <hash> --stat

# 抓帧预算
git ls-files '*.rdc' | wc -l
git ls-files -z '*.rdc' | xargs -0 du -cb | tail -1
git ls-files -i -c --exclude-standard '*.rdc' | wc -l

# 测试 XML
python - <<'PY'
import xml.etree.ElementTree as ET
for f in [".../P6/EditMode/UnityEditModeFull.xml", ".../P6/PlayMode/UnityPlayModeTests.xml"]:
    print(f, ET.parse(f).getroot().attrib)
PY
```
## 13. 07-EditorMVPass（2026-11-18）

> 范围：Editor 下无 `UniversalAdditionalCameraData` 相机的 GTAO 时间重投影（Scene View 拖影）与共享深度金字塔的同帧姿态复用；第二阶段扩展为 SSL-only Scene View 的共享 Editor MV。
> 计划：[PLAN/TODO/07-EditorMVPass/HANDOFF.md](TODO/07-EditorMVPass/HANDOFF.md)（第二阶段完成后移入 `Done/`）
> 结论：**第一阶段 ✅ PASS**（自动验收 + AC-9 人工确认，2026-11-18）；第二阶段实施中。

| 交付 | Commit | 证据 | 结论 |
|---|---|---|---|
| Editor 相机 MV pass + shader + gating + 单测 | `0bbce70` | `GroundTruthAmbientOcclusionEditorMotionPass.cs`（第二阶段迁移至 `ScreenSpaceCommon`）；E2E `meanAbs vs 原生 = 0` | ✅ |
| 深度金字塔按姿态重建 + 单测 | `3761adc` | `ScreenSpaceDepthPyramid.cs`；同帧同姿态仍只 build 一次 | ✅ |
| E2E / T12 回归 / 报告 | 本轮第三提交 | [Report.md](TODO/07-EditorMVPass/Report.md)、`validation/EditorMotion/`、`validation/T12/` | ✅ |

| 项 | 结果 |
|---|---|
| EditMode | 173/173（`ScreenSpaceDepthPyramidSharingTests` 已加入 T14 过滤器） |
| PlayMode | 6/6 |
| T12 | Forward 34/34、Forward+ 34/34、Deferred 35/35 |
| E2E（3 模式） | 无 ACD vs 原生 MV：逐帧 `meanAbs = 0`、`pixels>0.02 = 0`；抑制基线 vs 修复：`0.01021`、8.8% 像素超阈值；静止相机：完全一致 |
| 设置恢复 | 4 份 `*SettingsRestoration.json` 全部 `true`，Unity exit 0 |
| T12 哈希差异 | 12 张，全部在 `RenderScale75`/`TargetResize`/`CameraCut`/`MotionBlur`；`SameFrameDoubleRender` 对照不变；逐张数值见 `T12/HashDiff.json` |
| AC-9 人工验收 | ✅ 仓库所有者 2026-11-18 确认：真实 Scene View 拖拽/滚轮无 GTAO 残影 |

**第二阶段（SSL-only Scene View 共享 Editor MV）**：代码与自动验收已交付——SSL feature 在 SSGI/SSR 启用且相机无 ACD 时入队共享 `ScreenSpaceEditorCameraMotionPass`；EditMode 180/180、PlayMode 6/6；Editor MV E2E 3 模式与原生 MV 逐帧 `meanAbs = 0`；T12 34/34、34/34、35/35 且 85/85 PNG 哈希不变；3 份设置恢复全部 `true`。真实 Scene View 的 SSR 反射目视确认待仓库所有者（AC-14）。

**口径修订**：计划的 AC-3 使用“与 12 帧收敛参考的差值 ≤ 0.002”。实测 URP 原生 MV 路径同样偏离该参考约 `0.035`（时间累积的固定反馈延迟），该指标无法区分重投影错误。验收改为“与原生 MV 路径逐帧一致（`meanAbs ≤ 0.001`） + 抑制后显著劣化”，并在 `Report.md` §5.1 记录原因与原始数据。

**限制**：Editor 下不补物体运动；同姿态物体移动 + 同帧重渲染仍复用金字塔；SSL-only Scene View 与 XR 不在本轮范围。见 `KnownDifferences.md`。

## 14. 08-AccurateNormalsFixed（2026-11-19）

> 范围：Deferred + `Accurate G-buffer normals` 下屏幕空间光照（GTAO / SSGI / SSR）的噪点与闪烁；octahedron 法线路径首次进入验证矩阵。
> 计划与结果：[PLAN/TODO/08-Accurate_normals_fixed/HANDOFF.md](TODO/08-Accurate_normals_fixed/HANDOFF.md)、[Report.md](TODO/08-Accurate_normals_fixed/Report.md)
> 结论：**根因已确认并修复，自动验收通过**。

| 交付 | 结果 | 证据 |
|---|---|---|
| 根因 | 不透明 `DepthNormals` pass 缺少 `_GBUFFER_NORMALS_OCT`，prepass 写 signed 法线、SSL/GTAO 按 oct 解码 | `Attribution.before.json`；`SsgiRaw`/`GtaoAO` 调试图 |
| 修复 | 14 个 shader/HLSL 补齐 oct 关键字/编码（Lit、SimpleLit、Particles、Terrain、SpeedTree、WavingGrass） | 本轮提交 diff |
| AC-1/2/3 | oct vs signed 亮度差 ≤ 1e-5、地面高频差 1.2e-4、帧间差 ≤ 8e-6；`passed=true` | `validation/Attribution/Attribution.json` |
| AC-4 | 三个 oct 关键字全部启用 + 关键字/解码契约测试 | `validation/Attribution/Attribution.json`、`AccurateGbufferNormalsTests` |
| AC-5 | EditMode 182/182；T12 回放见 `validation/T12/` | `validation/EditMode/UnityEditModeTests.xml` |
| AC-6 | 性能回归未测（只加变体，无额外 pass），列为 follow-up | `Report.md` §6 |

**判定**：现象不属于 accurate normals 的预期表现；修复不触碰 signed 路径的算法，透明 prepass 按原设计保持 signed。

## 15. 11-LightCulling（2026-11-22）

> 范围：Deferred 模式下用 compute 消费 URP 的 tile/zbin 光列表与既有 GBuffer/BRDF/阴影/cookie/layers/GTAO 语义，新增 `UniversalRendererData.clusteredDeferredLighting` 开关；与 stencil deferred 逐像素 A/B、可运行时回退。
> 计划/结果：[PLAN/Done/11-LightCulling/HANDOFF.md](Done/11-LightCulling/HANDOFF.md)、[report.md](Done/11-LightCulling/report.md)、[KnownDifferences.md](Done/11-LightCulling/KnownDifferences.md)
> 结论：**验收通过（mixed lighting-shadowmask PARTIAL）**。

| 任务 | 结论 | Git / 证据 | 说明 |
|---|---|---|---|
| T0 冻结决策 | ✅ | HANDOFF §0（D1–D16） | 本轮范围与 HDRP 能力对齐解耦 |
| T1 构建解耦/灯上限/isForwardPlus | ✅ | `3131b6e`；`T5-runtime/player-d3d11/CdPerf.json`；`T7EditModeClustered.xml::StencilDeferredKeepsLegacyCullingCaps` | 关闭开关时 stencil 行为不变；Deferred 可见灯上限收紧到 256 |
| T2 compute-safe include | ✅* | `0505f10`；`T7EditModeClustered.xml::ComputeKernelAndMaskResolveShadersCompile` | `T2IterParity.json` 未单列；逐像素灯集合由 T4/T5/PlayMode A/B 间接覆盖 |
| T3 kernel v1 | ✅* | `3131b6e`；`T7PlayModeClustered.xml::StencilMatchesClusteredAcrossMaterialTypes` | `T3LightingDiff.json` 未单列；A/B 与材质分流由 PlayMode 覆盖 |
| T4 阴影/cookie/layers/mixed/AO | ◐ PARTIAL | `f98cb53`、`0bc4e02`、`0505f10`、`bca237b`；`T4-shadows/` | 7 项在 D3D11/D3D12 通过；mixed lighting-shadowmask 无 baked 数据，运行时 A/B 不可得 |
| T5 集成/边界/平台 gate | ✅ | `0505f10`；`T5-runtime/`；`T7/T5ModeGateTests.xml`、`T7/T5VariantCount.json` | native RP 构造期关闭并告警；运行时开关、Preview/Overlay/GLES/structured-buffer 回退 |
| T6 可选 GBuffer 覆盖 | ACCEPTED | `T6-preflight/T6Coverage.json` | 8 场景/39 材质（11 Lit、1 ComplexLit 跳过、无 forward-only）；D16 后不作正确性前置，不补 GBuffer |
| T7 测试/证据/性能 | ✅ | `9702d4f`；`T7/T7EditModeAll.xml`、`T7/T7PlayModeAll.xml`、`T7/T7Perf.json`、`T7/T7PlayerSummary.json` | EditMode 484/484、PlayMode 36/36；tile 8/16/32/64 实测；交叉点 8–32 灯 |
| T8 文档/验收/归档 | ✅ | 本提交 | 五处文档一致；TODO→Done |

| 验收项 | 结果 |
|---|---|
| AC-1/2 开关关闭 | stencil 路径与基线一致（EditMode culling 断言 + 既有管线未改） |
| AC-4/5/6 | 无阴影/阴影/cookie/layers/AO A/B `passed=true`；mixed PARTIAL |
| AC-7 | 天空不写、无 NaN；alpha = 间接 AO 取消系数 |
| AC-9/10 | 1280 变体；D3D11/D3D12 0 compute error；MSAA/XR/GLES/Preview/Overlay 回退；tile 8/16/32/64 |
| AC-11 | D3D11 720p 交叉点：32 灯 0.212 vs 0.344 ms；64 灯 0.254 vs 0.709 ms |
| AC-12 | 1 个 D3D12 `.rdc`（14.2 MiB ≤ 20 MiB）；dispatch 80×45；A/B diff 见 CdPerf |
| AC-13 | EditMode 484/484、PlayMode 36/36 |
| AC-15 | 运行时开关、临时 RT 释放、forward-only mask 均在 PlayMode 验证 |

**测试暴露的集成缺陷**：PreSetup 的 cluster jobs 与同帧 `ResolveMixedLightingMode` 争用 culling 灯数组，编辑器 Job safety 失败并级联。修复为在 deferred setup 读取灯数组前完成 jobs（`9702d4f`）；PlayMode 由 0/7 → 7/7；GPU 输入不变。

## 16. 12-GpuLightList（已归档，2026-11-22）

> 范围：C1（clustered 跳过 stencil 灯预计算 + CPU 分解探针）；G1（GPU 两段式构建 `URP_ZBinBuffer`/`urp_TileBuffer`，布局不变）；G2（FPTL 深度细剪）；G3（按 (tile, zbin) 的紧凑列表）。
> 计划/结果：[PLAN/Done/12-GpuLightList/HANDOFF.md](Done/12-GpuLightList/HANDOFF.md)；善后：[clean.md](Done/12-GpuLightList/clean.md)；证据：[validation/](Done/12-GpuLightList/validation)。
> 结论：**PASS。默认路径 = 同步 GPU 列表 + 位图；G2/G3 验收为 NO-GO，实现保留为默认关闭的 A/B。**

| 项 | 结论 | Git / 证据 | 说明 |
|---|---|---|---|
| C1 跳过 `PrecomputeLights` | ✅ PASS | `a313221`；`validation/C1/` | 去掉无人消费的 stencil 灯数组预计算（0.0035 ms/帧）；回退相机照常构建 |
| G1 默认路径 | ✅ PASS | `cc73287`、`2033e04`；`validation/G1/` | **同步 GPU 列表 + 位图**：GPU 两段式写两张位图，`preSetup` 0.051→0.008 ms、job 完成 0.110→0，列表 CPU ≈0.061–0.064 ms；word 布局/header/`_FPParams*`/tile 宽度不变 |
| G1 parity | ✅ PASS | `validation/G1/parity/player-d3d12/G1BufferParity.json` | zbin 逐位相等；tile 保守 AABB 满足 CPU ⊆ GPU（0 漏位）+ 像素逐位一致；async vs sync 逐位相同 |
| G1 A/B 与特性 | ✅ PASS | `T5-runtime/`、`T4-shadows/`、`tests/G1c*.xml` | T5 48 灯 mean 0.812–0.828、max 9；T4 7/7（D3D11/D3D12）；EditMode 490/490、PlayMode 38/38；`.rdc` 14.4 MiB |
| G1 异步 | ○ ACCEPTED（净亏） | `T5-runtime/player-d3d12-async/`、`cpu/d3d12-async/` | 位图逐位一致，但同机全档高 0.11–0.13 ms；`asyncCompute` 默认关闭，保留开关 |
| G2 FPTL 细剪 | ○ ACCEPTED（NO-GO） | `ef86f76`；`validation/G2/` | 清掉 65% 的位、A/B 不变，但 GPU 时间 +0.07–0.10 ms（全屏深度扫描），光照循环无可回收；默认关 |
| G3 紧凑列表 | ○ ACCEPTED（NO-GO） | `0e5ff0e`；`validation/G3/` | 每档 +0.08–0.13 ms；每 cell 8–21 项长于 1–2 word 位图扫描，空 tile 为 0，未做间接派发；默认关 |
| 回归 / 回退矩阵 | ✅ PASS | `tests/G2*.xml`、`tests/G3*.xml` | 最终 EditMode 490/490、PlayMode 40/40；stencil / Preview / Overlay / XR / forward+ / structured-buffer 回退不变 |
| 归档 | ✅ | 本提交 | TODO→Done；HANDOFF/ACCEPTANCE/验证脚本路径同步 |

**判定说明**：G1 的 tile parity 按冻结时修订的闸门执行（CPU ⊆ GPU + 像素逐位一致），因为 HLSL 与 Burst `FloatMode.Fast` 的三角函数差 1 ulp 就会翻位；zbin 仍要求逐位相等。G2/G3 记为 ACCEPTED 而非 FAIL：两者都已完成实现、测量与回归，负结果被如实记录并保留为可复现的 A/B 与扩展性基础设施。

**能力边界**：仍不覆盖透明/forward-only 灯路径（D-G4，clean.md C2）、XR/foveated、GLES、MSAA、`useStructuredBuffer=true`、GPU 灯注册表（C8）。CPU jobs 与 stencil deferred 因此保留为回退与 A/B 参照，C2–C8 的删除必须等透明接管与 D-C2/D-C3 决策。256+ 灯已由 [13-GpuLightList256](Done/13-GpuLightList256/HANDOFF.md) 实测为成立（见 §17）。

## 17. 13-GpuLightList256（已归档，2026-11-22）

> 范围：12 轮唯一未证伪的“256+ 灯扩展性”决策探针。新增 arena CLI 灯数覆盖与 parity 的 bin/word/tile 字段，不改变默认路径。
> 计划/结果：[PLAN/Done/13-GpuLightList256/HANDOFF.md](Done/13-GpuLightList256/HANDOFF.md)；证据：[validation/README.md](Done/13-GpuLightList256/validation/README.md)
> 结论：**PASS。GPU 列表在 256 灯成立，默认同步路径保持；不改 word 预算；G2/G3 仍 NO-GO。**

| 项 | 结论 | 证据 | 说明 |
|---|---|---|---|
| 256 灯 parity | ✅ PASS | `validation/256/parity/player-d3d11-256/G1BufferParity.json` | `binCount=409`、`wordsPerTile=8`、`tileWidth=32`（40×23）；zbin 逐位相等、tile 0 漏位；64 灯对照与 G1c 一致 |
| 像素正确性 | ✅ PASS | `validation/256/sweep/player-d3d11-256/` | clustered vs Forward+ mean 0.290 / 622 px >8；stencil vs clustered mean 4.080 / max 29 失败 |
| A/B 根因 | ✅ | 同上；48 灯基线 | stencil 每盏灯一次 additive blend 进 R11G11B10f（`m_HDRColorBufferPrecision: 0`），256 次舍入、蓝色最差；clustered/Forward+ 单次求和。256 灯应用 Forward+ 或 64-bit HDR 作参照 |
| 性能 | ✅ PASS | 同目录 `CdPerf.json` | 256 灯 GPU 1.763 / 1.270 ms（stencil/clustered）、CPU 3.218 / 1.811 ms；列表 CPU ≈0.155 ms |
| G2/G3 复核 | ○ ACCEPTED（仍 NO-GO） | `compact/`、`prune/` | prune 清 63.1% 位但 1.284 vs 1.206 ms；compact +0.10 ms、每 cell ≈379 项、6,475 cell overflow |
| 决策 | ✅ | HANDOFF §4.4 | 保持 GPU 列表默认；256 扩展性待证项关闭；下一缺口为透明/forward-only（D-G4 / C2） |

**边界**：256 灯场是每 64 盏复用位置的最坏重叠场（96/256 item 整屏），不代表空间均匀分布；只测 D3D11 720p 单卡。跨运行像素噪声 mean ≈0.11 / 5,750 px >8，结论均取同一次运行内的对比。
