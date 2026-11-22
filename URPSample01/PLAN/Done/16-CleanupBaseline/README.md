# 16-CleanupBaseline：Light Culling 善后 C0（基线冻结）

> 建立：2026-11-22。前置：[`PLAN/Done/12-GpuLightList/clean.md`](../../Done/12-GpuLightList/clean.md)（C0–C8）、[`15-ForwardClusterLights`](../../Done/15-ForwardClusterLights/HANDOFF.md)（C2 落地）。
> 已归档（2026-11-22），不代表 T5 通过。[归档复测](validation/archive-retest/RESULTS.txt)：T4 56/56；T5 24/32，双 API 的 8 张 L256 图超阈，D3D12 不适用既有例外。D3D11 三次抓帧分别缺局部灯 183、183、240，仅证明这些帧的灯集合差异与画差共现。单次耗时波动不阻塞归档；跨路径与材质失败保留，零附加灯和 SSL 定向复测见 §6。C4b、C5–C7 未在本轮签收；C8 可选。
>
> 里程碑签收记录（2026-11-22）：**C3 PASS（验收例外）**。T4 CLOSED；T5 OPEN，不阻塞 C3；**C4a PASS（仅 D3D11 上限夹具采用局部差异例外）**。C0 与 C0.5 已通过。
> 冻结决策：[DECISIONS.md](DECISIONS.md)（D-C1–D-C8 及 clean.md 的三处修正）。
> 基线 tag：`pre-cleanup` → `f0c7ec4`（C0 脚手架提交；渲染代码与 `0ffc46c` 相同）。
> 验证记录：[validation/README.md](validation/README.md)（全部数字与证据）。
> C4a：[最终验收、独立参照和双 API 玩家证据](c4/HANDOFF.md#c4a-最终验收pass限定例外)。

## 1. 范围

C0 的交付是“可复现的清理前基线”，不是功能改动：

1. 打 tag，记录冻结提交。
2. 在冻结点重跑 D3D11 / D3D12 的：
   - EditMode / PlayMode 全量；
   - T4 构建与 7 项特性矩阵；
   - T5 构建、0/8/48/256 灯 A/B 与帧时间；
   - CPU 分解（`--cd-cpu`，C4 的对照）；
   - 变体基线；
   - 至少 1 个 RenderDoc 抓帧。
3. 冻结 D-C1–D-C8（见 [DECISIONS.md](DECISIONS.md)）。

**不在本轮**：C0.5 的 Terrain / SpeedTree / ShaderGraph cluster 变体与 forward-only 不透明夹具；C3 起的任何删除。C0.5 在基线冻结后单独提交，避免污染变体基线。

## 2. 闸门

| 闸门 | 判定 | 阈值 | 证据 |
|---|---|---|---|
| C0-1 冻结点 | ✅ PASS | tag + HEAD 记录 | `pre-cleanup` → `f0c7ec4` |
| C0-2 测试 | ✅ PASS | 全绿 | **491/491**、**41/41** |
| C0-3 构建 | ✅ PASS | 4 个构建 0 error | T5/T4 × D3D11/D3D12 全 `Succeeded` |
| C0-4 T5 A/B | ✅ PASS | S/C mean ≤2、max ≤24 | 48 灯 **0.822/9**（D3D11）、**0.814/9**（D3D12） |
| C0-5 T4 矩阵 | ✅ PASS | 7/7 passed | 两 API 各 7/7 |
| C0-6 CPU 分解 | ✅ PASS | 记录 `PreSetup` / jobs / 上传 / 灯常量 | clustered `preSetup` **0.008/0.009 ms**、jobs **0.0001** |
| C0-7 变体基线 | ✅ PASS | Lit ForwardLit 6144、compute 1280 | T5 两 API 均 **6144** |
| C0-8 RenderDoc | ✅ PASS | 默认路径 pass 结构；无 stencil 灯体积 | D3D11/D3D12 各 1 个；透明 PS 读 structured |

## 3. 证据布局

```text
validation/baseline/
├── tests/                 # EditMode.xml / PlayMode.xml
├── CdArenaBuildSummary-{Direct3D11,Direct3D12}.json
├── t4/CdArenaT4BuildSummary-{Direct3D11,Direct3D12}.json
├── t4/player-{d3d11,d3d12}/   # T4FeatureMatrix.json + 28 PNG/组
├── t5-{d3d11,d3d12}/{control,cluster-on}/  # CdPerf.json/csv + 12 PNG/组
├── cpu/{d3d11,d3d12}/CdCpuBreakdown.json
├── rdc/                   # 双 API 抓帧、PS 反汇编、pass 结构、抓帧关键字
├── C0VariantBaseline.json
└── README.md
```

## 4. 复跑命令

```bash
U="/d/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe"
P="E:/GitHub/Glx.CustomURP/URPSample01"
OUT="$P/PLAN/Done/16-CleanupBaseline/validation/baseline"

# 测试
"$U" -batchmode -projectPath "$P" -runTests -testPlatform EditMode \
  -testResults "$OUT/tests/EditMode.xml" -logFile "$OUT/tests/EditMode.log"
"$U" -batchmode -projectPath "$P" -runTests -testPlatform PlayMode \
  -testResults "$OUT/tests/PlayMode.xml" -logFile "$OUT/tests/PlayMode.log"

# 构建（写 summary 到 validation/baseline 与 baseline/t4）
"$U" -batchmode -projectPath "$P" -executeMethod ClusteredDeferredArenaBuild.BuildD3D11AndExit  -logFile "$OUT/build-t5-d3d11.log"
"$U" -batchmode -projectPath "$P" -executeMethod ClusteredDeferredArenaBuild.BuildD3D12AndExit  -logFile "$OUT/build-t5-d3d12.log"
"$U" -batchmode -projectPath "$P" -executeMethod ClusteredDeferredArenaBuild.BuildT4D3D11AndExit -logFile "$OUT/build-t4-d3d11.log"
"$U" -batchmode -projectPath "$P" -executeMethod ClusteredDeferredArenaBuild.BuildT4D3D12AndExit -logFile "$OUT/build-t4-d3d12.log"

# T5 sweep：control（无透明）与 cluster-on（透明，默认路径）
EXE="$P/Builds/ClusteredDeferredArena/Direct3D11/CdArena.exe"
"$EXE" --cd-output "$OUT/t5-d3d11/control"    --cd-total-lights 256 --cd-light-counts 0,8,48,256 --cd-screenshot-lights 0,8,48,256
"$EXE" --cd-output "$OUT/t5-d3d11/cluster-on" --cd-transparent --cd-total-lights 256 --cd-light-counts 0,8,48,256 --cd-screenshot-lights 0,8,48,256
"$EXE" --cd-output "$OUT/cpu/d3d11" --cd-cpu

EXE="$P/Builds/ClusteredDeferredArena/Direct3D12/CdArena.exe"
"$EXE" --cd-output "$OUT/t5-d3d12/control"    --cd-total-lights 256 --cd-light-counts 0,8,48,256 --cd-screenshot-lights 0,8,48,256
"$EXE" --cd-output "$OUT/t5-d3d12/cluster-on" --cd-transparent --cd-total-lights 256 --cd-light-counts 0,8,48,256 --cd-screenshot-lights 0,8,48,256
"$EXE" --cd-output "$OUT/cpu/d3d12" --cd-cpu

# T4（两 API）
"$P/Builds/ClusteredDeferredArenaT4/Direct3D11/CdArenaT4.exe" --cd-output "$OUT/t4/player-d3d11" --cd-t4
"$P/Builds/ClusteredDeferredArenaT4/Direct3D12/CdArenaT4.exe" --cd-output "$OUT/t4/player-d3d12" --cd-t4
```

RenderDoc（D3D11，默认路径、透明、256 灯；D3D12 换成对应 exe 与文件名）：

```bash
RDC="E:/GitHub/renderdoc-mcp/_dist/package/renderdoc-mcp-windows-x64-v0.3.0/bin/renderdoc-cli.exe"
"$RDC" capture "$P/Builds/ClusteredDeferredArena/Direct3D11/CdArena.exe" \
  -w "$P/Builds/ClusteredDeferredArena/Direct3D11" \
  -a "--cd-output $OUT/rdc/capsrc --cd-capture --cd-transparent --cd-total-lights 256 --cd-light-counts 256 --cd-screenshot-lights 256 --cd-hold" \
  -d 240 -o "$OUT/rdc/c0-baseline-d3d11_frame.rdc"
# RenderDoc 实际落盘为 ..._frame<N>.rdc；CLI 的“file not found”提示可忽略。
```

## 6. 下一步（不在本提交）

| 步骤 | 前置 | 退出条件 |
|---|---|---|
| **C0.5 PASS** | C0 冻结完成 | Terrain / SpeedTree / ShaderGraph 前向变体与不透明夹具已验证；双 API 实测见下。 |

**C0.5 验收（2026-11-22）**：[变体明细](validation/c05/C05VariantMeasurement.json) 按 shader / pass / stage 比较剥离后计数；同名无名 pass 保留全部计数，不视作单个 pass。C0 与候选的探针构建 × D3D11/D3D12 均成功，0 error。每个 API 仅 11 个预期前向 pass 的顶点与片元组增长；标准 T5 只包含其中 3 个 Terrain detail/grass pass，Lit ForwardLit 片元仍为 6144。探针片元计数：Terrain Lit/Add/Base 和 SpeedTree 三项各 64→128，Detail/Grass 各 32→64，Grass Billboard 8→16，ShaderGraph Lit/Complex Lit 各 512→1024。

[EditMode 491/491](validation/c05/EditMode.xml)，[D3D11 PlayMode 42/42](validation/c05/PlayMode-D3D11.xml)，[D3D12 PlayMode 42/42](validation/c05/PlayMode-D3D12.xml)。48 灯独立不透明 ShaderGraph `UniversalForwardOnly` 接收体对比 Forward+：双 API 的 cluster-on mean 为 0.003、差值 >8 的像素为 0；per-object 对照为 11.931、23760 px（全图 230400 px）。这是 ShaderGraph 夹具的像素证据，Terrain/SpeedTree 的证据是玩家变体构建，不是逐材质像素 A/B。

**C3 第一次提交：legacy fog 迁移 PASS（2026-11-22）**。clustered resolve 后、forward-only opaques 前对非正交相机执行全屏深度雾；保留 stencil 路径作像素参照。[EditMode 491/491](validation/c3-fog/EditMode.xml)，Editor PlayMode [D3D11 44/44](validation/c3-fog/OneLight-D3D11.xml)、[D3D12 44/44](validation/c3-fog/OneLight-D3D12.xml)。同帧采样 deferred Lit、forward-only Complex Lit、alpha 混合透明 Lit 各自的 12×12 内部区域；透视下四种状态（关 / Linear / Exp / Exp2）逐区域对比 stencil，每种开雾状态另验相对关雾的响应。正交下另验 deferred 不施雾。正式夹具使用一盏方向灯与一盏附加灯。

独立限制：首轮零附加灯夹具在两 API 的正交测试报 `ClusterBuilder` 未绑定 `_CdClusterItems`，同轮另有 6 个 SSL 相机历史计数断言失败（[D3D11](validation/c3-fog/PlayMode-D3D11.xml) / [D3D12](validation/c3-fog/PlayMode-D3D12.xml)）。单附加灯复跑未复现，不宣称零附加灯通过；本提交未改灯表或缓冲布局。尚未删除 stencil 或执行删除后的 player / T4 / T5 / RenderDoc 验收。

归档前定向复测：在 `29ceb1c` 的独立副本应用[零附加灯夹具差异](validation/zero-light-history/zero-light.patch)，保留方向灯与原正交雾断言。[D3D11](validation/zero-light-history/d3d11.xml) 和 [D3D12](validation/zero-light-history/d3d12.xml) 各 7/7：零附加灯正交雾 1 项、SSL 相机历史 2 组 × 3 路径 6 项均通过。两类历史故障在该版本未复现，不据此判定同因。C3/C4 已有 Preview 和 Overlay 实际渲染测试；XR 只有构造 XRPass 的回退路由测试，不代表真机双眼渲染。此复测不替代完整 SSL 图像矩阵或玩家验收。

**C3 第二阶段（`646f75a`）：里程碑 PASS，非全项通过。** 所有者依据[验收例外](DECISIONS.md#c3-验收例外2026-11-22)关闭 T4 删除回归项，接受既有材质跨路径失败；T5 保持 OPEN，不阻塞 C3。以下为 `6575585` 记录的原始结果。删除 stencil 后的四组 T4/T5 玩家均已构建，最后补跑的 [T4 D3D12](validation/c3/t4/CdArenaT4BuildSummary-Direct3D12.json) 为 0 error。保留原始阈值，未将以下失败改判为通过。

| 闸门 | 实测结果 |
|---|---|
| 材质 / PlayMode | EditMode 488/488，两 API PlayMode 各 43/44。以 `5ab7626` 的相同路径和材质夹具复测，[原始帧对照](validation/c3/material-comparison.json)显示两 API 的 1674 个超 `8/255` 像素位置完全相同，新增/消失均为 0；同一路径跨版本最大 RGB 差为 clustered 0.2491/255、Forward+ 0.000122/255 以内。原跨路径断言上限 230，仍失败。 |
| T5 玩家 | [C0 同路径逐图](validation/c3/C3PlayerRegression.json)：D3D12 16/16，D3D11 12/16；失败均在 256 灯，涉及两条路径。[同一候选玩家独立进程复测](validation/c3/T5D3D11L256RootCause.json)：255 盏局部灯时两路 max 均为 2，256 盏时为 72/71。可见灯上限是有证据支持的解释，但未记录被排除的逐灯 ID。 |
| T4 玩家 | 两 API 各 7 项开关均有响应，路由标志正确，无洋红像素；Forward+ 对 clustered 的原矩阵均为 **0/7**。相对 C0 各有 13/14 张 clustered 同路径图通过，唯一例外为 `ReceiveShadowsOff_ClusteredOn`（max 240，5432 px 超 8/255）。C0 的该项材质及替换范围不同。[相同夹具复核](validation/c3/T4MatchedFixtureComparison.json)：`5ab7626` → `646f75a` 两 API 各 28/28 张同路径图通过，最大通道差 1/255；各 14 组 Forward+ 对 clustered 在两版本均未过原阈值，但 >8/255 与 >24/255 的像素位置逐组完全相同。此结果证明本夹具未发现新增回归，不代表跨路径一致性通过。 |
| RenderDoc | [双 API 证据](validation/c3/rdc/)：1280×720、256 灯、默认 clustered 透明路径各一帧。D3D11 compute→resolve EID 630→642，D3D12 1352→1359；六个透明 draw 的 PS 均读取 t0/t1 structured buffer。两帧均未出现 stencil 灯 pass，不外推至其他相机。 |

| 后续步骤 | 前置 | 退出条件 |
|---|---|---|
| **C3** | C0.5 + D-C5 fog 迁移 | 删 stencil pass/shader/material/关键字；preview/overlay/XR 走 Forward；A/B vs C0 ≤ 阈值 |
| **C4a** | C3 | 删 CPU jobs 与 `PreSetup` 构图；保留 `_FPParams*` / 缓冲布局 / per-object 索引 |
| **C4b** | 单独立项 | 新 GPU 光表布局；不属于本轮清理 |

## 7. 边界

- 只测 D3D11/D3D12、1280×720、单卡；GPU 帧时间跨运行噪声约 0.1 ms。
- 256 灯为位置复用的最坏重叠场，不代表空间均匀分布。
- `StencilDeferred` 在本轮仍是参照；C3 之后参照统一为 Forward+。
- 构建会通过 `ClusteredDeferredTestAssetSetup.CreateAll()` 重写测试资产与 ProjectSettings；验证后只保留语义增量，其余还原。
