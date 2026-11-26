# 23 HANDOFF：SS/RT 统一着色（P3 前置）

**更新时间：** 2026-11-27
**基线上限：** `350d18c`（P0 的 fixture 与证据均为其后新增文件）
**当前阶段：** B5 部分完成（构建、R1/TLAS/D3D11 门禁已关闭；**命中着色未生效，P1 验收 OPEN**）

## 已完成

| 交付 | 位置 | 状态 |
|---|---|---|
| 调研 | [research.md](research.md) | 完成 |
| 计划（P0–P3） | [plan.md](plan.md) | 完成 |
| 契约冻结（C1–C8） | [contracts.md](contracts.md) | 完成 |
| 布局 tripwire | [SsrP3ContractTests.cs](../../../Assets/Tests/RayTracing/SSR/SsrP3ContractTests.cs) | 完成（4/4 通过，[ContractTests.xml](validation/P0/tests/ContractTests.xml)） |
| Fixture 构建器 | [SsrP3BaselineSetup.cs](../../../Assets/Tests/ScreenSpaceLighting/Editor/SsrP3BaselineSetup.cs) | 完成 |
| Player 捕获器 | [SsrP3BaselineCapture.cs](../../../Assets/Tests/ScreenSpaceLighting/Runtime/SsrP3BaselineCapture.cs) | 完成 |
| Fixture 资产 | `Assets/Tests/RayTracing/SSR/Fixture/` | 已生成 |
| D3D12 基线 | [validation/P0](validation/P0/README.md) | 已捕获，三变体两进程逐位一致 |
| A 决策锁定 | [plan.md](plan.md) §2 | 完成（2026-11-27） |
| P1 契约（C2.7 记录） | [contracts.md](contracts.md) / `Shaders/RayTracing/SsrHitSurface.hlsl` | 完成（tripwire 5/5，[ContractTests.xml](validation/P1/tests/ContractTests.xml)） |
| P1 命中表面传输 | `GlxBasicPayload` 64B / `RayTracingSsr.raytrace` 写 `_R2Surfaces` | 完成（7/7 @D3D12，payload 实测 64B） |
| P1 命中延迟着色 | `SsrHitDeferred.compute` + `SsrPass.DispatchHitDeferred` | 完成（默认关，待 B4 开关 / B5 构建验证） |
| P1 开关与回退 | `hitDeferredShading` / `RayTracingSceneInstances.ReportUnsupportedSurface` / 构建器资源解析 | 完成（默认关；不支持材质注册期报错并跳过） |
| B5 构建 + 证据 | `validation/P1/Win64-D3D12` 与 `Win64-D3D11` | 完成（构建 0 错误；双 API 各变体两进程 SHA 一致） |

## 已锁定决策（2026-11-27）

1. **阴影**：接受 HDRP 同款 `SHADOW_LOW`。hit 着色单 tap 阴影图比较，不做 PCF/Poisson/dither；与光栅的质量差计入 P2 容差，不作为缺陷追。
2. **材质**：只支持标准 PBR 字段；SSS/transmission/anisotropy 不提供、不设 per-material 回退，注册期报错并跳过，命中按 miss（probe/sky）。禁止第二条着色路径（含 closest-hit 近似）。写入 contracts C2.7（B1 落地）。
3. 未定项时间点：GBuffer 8bit/16F → P2 A/B；P3 一帧跳变、历史捕获时机 → P3 开工前。

## 关键结论（P0 实测）

- 工程的 Windows API 是 Automatic（实际只编 D3D11）。Player 若用 `-force-d3d12` 启动而构建时没带 D3D12，计算着色器 kernel 会缺失：`Clustered deferred fell back to Forward`、`Kernel 'KDepthPyramidCopy' not found`。构建器现在显式设置 D3D11+D3D12，并在 `finally` 中先还原显式列表、再还原 Automatic 标志（`SettingsRestore.json` 的 `apisRestored = true`），工程文件逐字节回到提交值。
- 工程默认管线是 Forward。Player 构建若不在构建期把 fixture 的 Deferred 管线设为默认/所有质量档，URP 会剔除 Deferred GBuffer 变体，画面变成"天空盒 + 全黑几何"。构建器已在 `try/finally` 中处理并恢复。
- 原始基线场景没有任何 `SsrRayTracingGeometry`，DXR 混合会静默不启用（`hwMergeDispatches = 0`）。fixture 已给 14 个网格注册稳定实例 ID。
- 共享 `Ground.mat` 的 smoothness 是 0.25，本来不产生 SSR；fixture 使用独立的 0.95 smoothness 地面材质，不修改共享资产。
- 构建器修正 API 后已重建 Player 并重录六份证据（三变体 × 两进程），`frame.raw` SHA256 与冻结值逐位一致，说明修正只影响 kernel 可用性、不改变画面。
- 基线（121 次渲染，`firstUnityFrame = 1`）：
  - `base` `96b9c197…`，`nodenoise` `693c54de…`，`ssroff` `1295029d…`；同变体两进程 SHA 完全一致。
  - `hwMergeDispatches = 1`（base/nodenoise），`traceDispatches = 1`；`ssroff` 全 0。
  - `base−ssroff` 地面 ROI：meanAbs 8.4e-4、p99 0.030、2.5% 像素 > 0.01。
  - `base−nodenoise` 地面 ROI：meanAbs 4.0e-4、p99 0.003、max 5.93（少量高光/萤火虫）。
  - 无 NaN/Inf。

## B2 关键结论（实现期发现）

- **Unity 光追管线不允许材质 `RayTracing` pass 的 closest-hit 写 UAV**（6000.3 修复说明 UUM-141971：not supported，编译器报错；2022.3 上属"可能崩"）。HDRP 14 同样不这么做：`RayIntersectionGBuffer` 经 payload 回传，由 raygen 写 `_GBufferTexture*RW`。
- 因此反射表面只能经 ray payload 回传：`GlxBasicPayload` 24 → **64 字节**（发布结果仍在 @0–23，`_R2Hits`/`_GlxResults`/`_GlxProbeResults` 全部保持 24B），raygen 调 `SsrBuildHitSurface` 写 80B 记录。shadow/probe 模式不填 @24–63。
- 传输布局避开了对齐歧义：`float2` 八面体法线放在 32（4B/8B 对齐都一致），两个 `float3` 跟随发布头证明的 4B 打包。
- `SsrHitSurface` 按请求序号索引（`DispatchRaysIndex().x`），消费者用 `_R1Queue[i].slot` 映射像素槽；未命中/无效槽写 version 0，无需逐帧清空 80B 缓冲。

## 未完成 / 门禁

| Gate | 状态 | 说明 |
|---|---|---|
| P0 fixture 生成 | CLOSED | `SsrP3BaselineSetup.CreateFixtureMenu` |
| P0 Player 构建 | CLOSED | `BuildPlayerMenu`；显式 D3D11+D3D12，D3D12 运行，构建后工程设置精确还原 |
| P0 两进程复现 | CLOSED（D3D12） | 三个变体均逐位一致 |
| P0 契约测试 | CLOSED | `ContractTests.xml`：4/4 通过（payload 24B、R1Sample 80B、队列 stride、Pass 206） |
| R1 计数器 | CLOSED | 捕获帧内异步回读：187225 / 179076 / 11952 / 167124 |
| DXR dispatch 直接证据 | CLOSED | TLAS 14 实例、0 禁用子网格、121 次构建（`Player.json`） |
| D3D11 回退基线 | CLOSED | 首次录制，三变体两进程逐位一致；tlas 0、R1 全 0 |
| P1 验收（SS/RT 缝下降） | **OPEN** | 命中着色未生效：D3D12 帧与 P0 逐位相同 |
| P1 契约（C2.7 + `SsrHitSurface`） | CLOSED | 80B / 5 字段 / version 1 已冻结，tripwire 5/5 |
| P1 hit GBuffer + 延迟着色核 | CLOSED | B3 核 + B4 开关/回退完成；B5 构建验证 + 证据 |

## 如何跑

```powershell
$U = "D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe"
$P = "E:\GitHub\Glx.CustomURP\URPSample01"

# 生成 fixture（快）
& $U -batchmode -quit -projectPath $P -executeMethod SsrP3BaselineSetup.CreateFixtureMenu -logFile "$env:TEMP\ssr-p3-fixture.log"

# 构建 Player（会临时把 fixture 管线设为默认 + 所有质量档，然后恢复）
& $U -batchmode -quit -projectPath $P -executeMethod SsrP3BaselineSetup.BuildPlayerMenu -logFile "$env:TEMP\ssr-p3-build.log"

# 捕获（每个变体两个全新进程）
$exe = "$P\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation\P0\Player\SsrP3Baseline.exe"
foreach ($v in "base","nodenoise","ssroff") {
  foreach ($r in "run-01","run-02") {
    $out = "$P\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation\P0\Win64-D3D12\$v\$r"
    & $exe -force-d3d12 -p3-output $out -p3-variant $v -p3-run $r
  }
}

# 对比
python "$P\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation\P0\compare.py"

# 契约测试（-runTests 不能带 -quit，否则导入后直接退出、不产出 XML）
& $U -batchmode -nographics -projectPath $P -runTests -testPlatform EditMode `
  -testFilter SsrP3ContractTests `
  -testResults "$P\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation\P1\tests\ContractTests.xml" `
  -logFile "$env:TEMP\ssr-p3-b1-tests.log"
```

## 下一阶段（P1）入口

1. 先读 [contracts.md](contracts.md) C2/C3：新增 hit 表面记录必须带版本位，24B payload 不动。
2. 先做"去 top-4 截断 + shadow atlas 采样"的 hit 光照核（不依赖世界 cluster），用 `nodenoise` 帧做 A/B。
3. 发现任何布局/Pass 顺序变化，先改契约与本文件的 gate 表，再改代码。
