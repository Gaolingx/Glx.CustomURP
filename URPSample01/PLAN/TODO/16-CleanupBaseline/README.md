# 16-CleanupBaseline：Light Culling 善后 C0（基线冻结）

> 建立：2026-11-22。前置：[`PLAN/Done/12-GpuLightList/clean.md`](../../Done/12-GpuLightList/clean.md)（C0–C8）、[`15-ForwardClusterLights`](../../Done/15-ForwardClusterLights/HANDOFF.md)（C2 落地）。
> 状态：**C0 PASS（2026-11-22）**。基线可复现，决策已冻结，未删代码。
> 冻结决策：[DECISIONS.md](DECISIONS.md)（D-C1–D-C8 及 clean.md 的三处修正）。
> 基线 tag：`pre-cleanup` → `f0c7ec4`（C0 脚手架提交；渲染代码与 `0ffc46c` 相同）。
> 验证记录：[validation/README.md](validation/README.md)（全部数字与证据）。

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
OUT="$P/PLAN/TODO/16-CleanupBaseline/validation/baseline"

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
| **C0.5** | C0 冻结完成 | Terrain/SpeedTree/ShaderGraph 的 ForwardLit 加 `_CLUSTERED_DEFERRED_LIGHTS`；新增 forward-only 不透明夹具并拿到像素 A/B；重记变体数（预期只增这些 shader） |
| **C3** | C0.5 + D-C5 fog 迁移 | 删 stencil pass/shader/material/关键字；preview/overlay/XR 走 Forward；A/B vs C0 ≤ 阈值 |
| **C4a** | C3 | 删 CPU jobs 与 `PreSetup` 构图；保留 `_FPParams*` / 缓冲布局 / per-object 索引 |
| **C4b** | 单独立项 | 新 GPU 光表布局；不属于本轮清理 |

## 7. 边界

- 只测 D3D11/D3D12、1280×720、单卡；GPU 帧时间跨运行噪声约 0.1 ms。
- 256 灯为位置复用的最坏重叠场，不代表空间均匀分布。
- `StencilDeferred` 在本轮仍是参照；C3 之后参照统一为 Forward+。
- 构建会通过 `ClusteredDeferredTestAssetSetup.CreateAll()` 重写测试资产与 ProjectSettings；验证后只保留语义增量，其余还原。
