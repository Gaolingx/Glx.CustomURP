# 16-CleanupBaseline：C0 验证记录

> 环境：Unity 2022.3.62f1、D3D11 / D3D12 standalone Development player、RTX 5060 Ti、1280×720。
> 冻结提交：`f0c7ec4`（`pre-cleanup` tag；渲染代码与 `0ffc46c` 相同）。
> 范围：只冻结基线与决策，不删代码；C0.5（Terrain/SpeedTree/ShaderGraph 变体）不在本轮。
> 结论：**C0 PASS**。四个构建 0 error，测试全绿，T5/T4 数字与 15 轮一致；`Stencil vs Clustered` 在 256 灯失败是 13 轮已定性的 stencil 累加问题，不是本轮回归。

## 1. 闸门

| 闸门 | 结果 | 关键数字 / 证据 |
|---|---|---|
| C0-1 冻结点 | ✅ PASS | tag `pre-cleanup` → `f0c7ec4`；上一提交 `0ffc46c` |
| C0-2 测试 | ✅ PASS | EditMode **491/491**、PlayMode **41/41**；`tests/*.xml` |
| C0-3 构建 | ✅ PASS | T5 + T4 × D3D11/D3D12 全部 `Succeeded`、0 error；`CdArena*BuildSummary-*.json` |
| C0-4 T5 A/B | ✅ PASS | 48 灯 S/C mean **0.822**（D3D11）/ **0.814**（D3D12）、max 9、>8 **2 / 7 px**；C/F 与 13–15 轮同量级 |
| C0-5 T4 矩阵 | ✅ PASS | 两 API 各 **7/7**；`t4/player-*/T4FeatureMatrix.json` |
| C0-6 CPU 分解 | ✅ PASS | 64 灯 clustered：`preSetup` 0.008/0.009 ms、jobs 0.0001 ms、上传 0；`cpu/*/CdCpuBreakdown.json` |
| C0-7 变体基线 | ✅ PASS | T5 Lit ForwardLit **6144**、compute **1280**；`C0VariantBaseline.json` |
| C0-8 RenderDoc | ✅ PASS | 双 API 抓帧；透明 PS 读 `t0/t1` structured(4 B)；`rdc/` |

## 2. 测试

| 套件 | 结果 | 耗时 | 证据 |
|---|---|---|---|
| EditMode | **491/491** | 47.2 s | `tests/EditMode.xml` |
| PlayMode | **41/41** | 5.2 s | `tests/PlayMode.xml` |

## 3. 构建与变体

| 构建 | API | 结果 | 耗时 | Player 大小 | Lit ForwardLit fp |
|---|---|---|---:|---:|---:|
| T5 | D3D11 | Succeeded / 0 error | 13.4 s | 143,548,612 B | **6144** |
| T5 | D3D12 | Succeeded / 0 error | 11.3 s | 143,548,616 B | **6144** |
| T4 | D3D11 | Succeeded / 0 error | 565.8 s | 153,605,492 B | 9216 |
| T4 | D3D12 | Succeeded / 0 error | 13.1 s | 153,605,487 B | 9216 |

- T5 是清理预算：Lit ForwardLit 片元 6144、顶点 32，与 15 轮逐字相同（3072→6144 的增量来自 `_CLUSTERED_DEFERRED_LIGHTS`）。
- T4 构建把 shadow 管线停在 quality level 上，多出 shadow 关键字轴，因此是 9216；**不要**拿它当删除预算。
- `ClusteredDeferred.compute` 仍是 8 个关键字轴、1280 组合（`C0VariantBaseline.json`）。
- 构建日志（gitignored）中无 `Shader error`；统计方法是对 `After scriptable stripping:` 行的解析。

## 4. T5 A/B

### 4.1 control（无透明，默认路径）

| API | 灯数 | S/C mean / max / >8 | S/C pass | C/F mean / max / >8 |
|---|---:|---|---|---|
| D3D11 | 0 | 0.000 / 0 / 0 | ✅ | 0.175 / 46 / 572 |
| D3D11 | 8 | 0.170 / 5 / 0 | ✅ | 0.179 / 59 / 600 |
| D3D11 | 48 | **0.822 / 9 / 2** | ✅ | 0.209 / 81 / 452 |
| D3D11 | 256 | 4.067 / 102 / 389,428 | ❌ 已知 | 0.290 / 86 / 622 |
| D3D12 | 0 | 0.000 / 0 / 0 | ✅ | 0.175 / 46 / 572 |
| D3D12 | 8 | 0.167 / 5 / 0 | ✅ | 0.179 / 59 / 600 |
| D3D12 | 48 | **0.814 / 9 / 7** | ✅ | 0.209 / 81 / 452 |
| D3D12 | 256 | 4.116 / 70 / 397,058 | ❌ 已知 | 0.290 / 86 / 624 |

### 4.2 cluster-on（透明，默认 cluster 灯路径）

| API | 灯数 | S/C mean / max / >8 | C/F mean / max / >8 |
|---|---:|---|---|
| D3D11 | 0 | 0.000 / 2 / 0 | 0.154 / 49 / 190 |
| D3D11 | 8 | 0.168 / 5 / 0 | 0.155 / 59 / 222 |
| D3D11 | 48 | 1.063 / 247 / 21,475 | **0.187 / 82 / 382** |
| D3D11 | 256 | 7.330 / 239 / 447,618 | **0.262 / 80 / 555** |
| D3D12 | 48 | 1.035 / 247 / 21,431 | **0.187 / 82 / 382** |
| D3D12 | 256 | 7.205 / 239 / 404,553 | **0.263 / 80 / 556** |

- cluster-on 的 C/F（48 灯 382 px、256 灯 555/556 px）与 15 轮 382 / 556 px 一致，证明默认 cluster 灯路径在本基线中生效。
- S/C 在 cluster-on 的 48/256 灯失败是 **C2 的预期语义差**（stencil 透明仍走 per-object），不是回归；C3 起参照统一为 Forward+。
- S/C 256 灯在 control 也失败：stencil 每盏灯一次 additive blend 进 `R11G11B10f`，256 次舍入；13 轮已定性，附录见 13/15 轮 README。
- C/F 的 `passed=false` 来自固定管线差的 max（46–86），>8 像素只有数百个，与 13–15 轮一致；不作为清理闸门。

## 5. 帧时间（同一次运行，ms）

| API | 运行 | 灯数 | Stencil GPU/CPU | Clustered GPU/CPU | Forward+ GPU/CPU |
|---|---|---:|---|---|---|
| D3D11 | control | 48 | 0.592 / 1.468 | **0.400 / 1.077** | 0.445 / 1.303 |
| D3D11 | control | 256 | 1.926 / 4.362 | **1.187 / 1.802** | 1.168 / 2.009 |
| D3D11 | cluster-on | 48 | 0.587 / 1.377 | **0.434 / 1.007** | 0.483 / 1.233 |
| D3D11 | cluster-on | 256 | 1.917 / 3.581 | **1.557 / 1.950** | 1.367 / 1.899 |
| D3D12 | control | 48 | 0.840 / 1.404 | **0.476 / 1.046** | 0.546 / 1.328 |
| D3D12 | control | 256 | 3.930 / 4.919 | **1.190 / 1.752** | 1.244 / 2.138 |
| D3D12 | cluster-on | 48 | 0.903 / 1.730 | **0.570 / 1.154** | 0.637 / 1.436 |
| D3D12 | cluster-on | 256 | 4.140 / 5.689 | **1.491 / 1.931** | 1.679 / 2.434 |

- 8 灯交叉点仍在：D3D11 control stencil 0.239 vs clustered 0.228；D3D12 0.286 vs 0.297。
- 跨运行 GPU 噪声约 0.1 ms；只比较同一次运行内的量级。

## 6. T4 特性矩阵

| API | 结果 | `stencil_vs_clustered_on` mean / max 范围 |
|---|---|---|
| D3D11 | **7/7 passed** | 0.020–0.968 / 3–10 |
| D3D12 | **7/7 passed** | 0.020–0.988 / 3–9 |

7 项：`MainShadowCascade`、`MainShadowScreen`、`AdditionalShadowAtlas`、`ReceiveShadowsOff`、`LightCookie`、`LightLayers`、`ScreenSpaceAO`。阈值 mean ≤2、max ≤24。

## 7. CPU 分解（64 灯、120 帧）

| 切片 | D3D11 Stencil | D3D11 Clustered | D3D12 Stencil | D3D12 Clustered |
|---|---:|---:|---:|---:|
| frame CPU | 1.419 | **0.898** | 1.870 | **1.119** |
| `preSetup` | 0.0002 | 0.0076 | 0.0002 | 0.0093 |
| cluster jobs complete | 0 | 0.0001 | 0 | 0.0001 |
| cluster 上传 | 0 | 0.0000 | 0 | 0.0000 |
| `setupLights` | 0.107 | 0.130 | 0.134 | 0.166 |
| ├ 灯常量 | 0.077 | 0.074 | 0.097 | 0.094 |
| └ `PrecomputeLights` | 0.0028 | 0.0001 | 0.0036 | 0.0001 |
| other | 1.312 | 0.760 | 1.735 | 0.944 |

C4a 的 CPU 收益已经很小（GPU 列表路径的 jobs 已归零、上传为 0）；它的主要价值是删除死代码与 `ForwardLights.PreSetup` 的 CPU 分支，而不是帧时间。

## 8. RenderDoc

| 项 | D3D11 | D3D12 |
|---|---|---|
| 文件 | `rdc/c0-baseline-d3d11_frame320.rdc`（12 MB） | `rdc/c0-baseline-d3d12_frame239.rdc`（15 MB） |
| API / 事件 / draw | D3D11 / 198 / 144 | D3D12 / 216 / 143 |
| pass 结构 | GBuffer → CopyDepth → Clustered Deferred → Forward Only → Transparent → FinalBlit | 同上 |
| 透明 draw | EID 679/682/685（2304 indices） | EID 1412/1422/1432（2304 indices） |
| 透明 PS | `ps_682.txt`：`dcl_resource_structured t0/t1, 4` + 3× `ld_structured` | `ps_d3d12_1412.txt`：同上 |
| 抓帧关键字 | `forwardClusterKeyword=true`、`_FORWARD_PLUS=0`、per-object 上限 4 | 同上 |

`sweep` 的 `CdPerf.json` 里 `forwardClusterKeyword` 只在 capture-only 模式写入（15 轮同样为 false）；以 `rdc/capsrc*/CdPerf.json` 为准。

## 9. 证据清单

```text
validation/baseline/
├── tests/{EditMode,PlayMode}.xml
├── CdArenaBuildSummary-{Direct3D11,Direct3D12}.json
├── t4/CdArenaT4BuildSummary-{Direct3D11,Direct3D12}.json
├── t4/player-{d3d11,d3d12}/T4FeatureMatrix.json + 28 PNG/组
├── t5-{d3d11,d3d12}/{control,cluster-on}/CdPerf.json/csv + 12 PNG/组
├── cpu/{d3d11,d3d12}/CdCpuBreakdown.json
├── rdc/{c0-baseline-*-frame*.rdc, ps_682.txt, ps_d3d12_1412.txt, structure*.txt, capsrc*/CdPerf.json}
└── C0VariantBaseline.json
```

`.log` 被 `URPSample01/.gitignore` 排除；重建日志在本地。

## 10. 复跑

见 [本轮复跑步骤](../README.md) §4。构建会重写测试资产与 ProjectSettings；验证后已还原，提交只含证据。

## 11. 边界与对清理的含义

- 只测 D3D11/D3D12 720p 单卡；256 灯为位置复用的最坏重叠场。
- 透明夹具只覆盖 Lit；SimpleLit/ComplexLit/Particles 的变体未进入这两个 player（C0.5 的缺口）。
- **C3 的前置**：fog 迁移（D-C5）完成；Preview/Overlay/XR 明确走 Forward（D-C7）。
- **C4a**：只删 CPU jobs/`PreSetup` 构图，保留 `_FPParams*` 与缓冲布局；per-object 索引**不删**（D-C7）。
- **变体预算**：T5 Lit ForwardLit 6144；C3 删除 stencil 关键字后不得升高，C5 再重设基线。
