# P1 证据

**状态：** B1–B5 完成；B5-fix2 用 RenderDoc 定位并修复了"命中着色不生效"的根因（kernel 现在真实执行，`b1e0a889`）。B6 补齐环境光、量出 SS/RT 边界亮度差：**缝指标未显著下降（NOT MET）**，执行/一致性/D3D11 回归通过；性能门禁仍 OPEN。
**基线：** P0 D3D12 基线继续有效；`_R2Hits`、R1/R2 布局、Pass 顺序与注入点均未改。

## B1：C2.7 hit 表面记录

| 项 | 内容 |
|---|---|
| 契约 | [contracts.md](../contracts.md) C2.7 + C8：`SsrHitSurface` 80 字节 / 5 字段 / `SsrHitSurfaceVersion = 1` |
| 定义 | `Shaders/RayTracing/SsrHitSurface.hlsl`（尚无消费者，B2 起由 closest-hit 写入） |
| 不变项 | `GlxBasicPayload` 仍 24 字节；`R1Sample` 仍 80 字节；tile 8×8；`_R1Counts` 11×uint；注入点 206 |
| tripwire | [tests/ContractTests.xml](tests/ContractTests.xml)：5/5 通过（新增 `SsrHitSurface_IsTheFrozen80ByteReflectionRecord`） |
| 运行时影响 | 无。头文件未被任何 shader include，不改变 P0 输出 |

contracts C2.7 同时冻结了材质能力边界：只承载标准 PBR 字段（baseColor、specularOcclusion、normal、perceptualRoughness、fresnel0、coatMask、emissive+baked、distance、surfaceType）；不提供 SSS/transmission/anisotropy，不设 per-material closest-hit 回退；不支持的材质在注册期报错（每材质一次）并跳过。

## B2：RT 命中表面记录（payload 传输 + raygen 写入）

| 项 | 内容 |
|---|---|
| 引擎约束 | 材质的 closest-hit pass 不能写 UAV（UUM-141971），HDRP 同款走 payload 回传 |
| 传输 | `GlxBasicPayload` 24 → 64B：@0–23 发布结果不变；@24 roughness、@28 metallic、@32 八面体法线（8 对齐）、@40 baseColor、@52 emissive |
| 写入 | raygen 调 `SsrBuildHitSurface` 写 `_R2Surfaces`：请求序号索引、stride 80、version 0 表示未命中/无效，因此不需要逐帧清空 |
| 绑定 | `SsrRequestQueue.surfaces`（k × 80B，≤ 2.6 MB），随 DispatchRays 绑定 `_R2Surfaces` |
| 不改 | `_R2Hits` / `_GlxResults` / `_GlxProbeResults` 仍是 24B；R1/R2 布局、Pass 顺序、注入点不变 |
| 证据 | [ContractTests.xml](tests/ContractTests.xml)：默认 D3D11 Editor，6 通过 + 1 跳过；[ContractTests-d3d12.xml](tests/ContractTests-d3d12.xml)：7/7，编译后 payload 实测 64B |
| 未验证 | 材质 hit pass 的 D3D12 变体要到 Player 构建才编译；逐像素不变性待 B5 复跑确认 |

## B3：命中延迟着色核（默认关）

| 项 | 内容 |
|---|---|
| 核 | `Shaders/Utils/ScreenSpaceLighting/SsrHitDeferred.compute`：`SsrHitDeferred`，每请求一线程 |
| 复用 | 直接 include `ClusteredDeferred.hlsl`，灯/阴影走 `CdMainLight` / `CdGetAdditionalLight` / `CdMainLightShadow` / `CdAdditionalLightShadow` / `CdShade` |
| 表面 | 记录 → 合成 GBuffer（gb0 = baseColor+flags 0、gb1 = fresnel0、gb2 = 法线+smoothness，materialType = 1）；观察方向 = 反向反射射线 |
| 灯 | 主光 + 全部附加灯（`_SsrHitLightCount`，上限 64）；阴影单 tap（SHADOW_LOW），不采 screen-space shadow/cookie/light layer/SSAO/shadowmask |
| 环境光 | `GlxHitLowFrequencyIrradiance`；specular 份额同源近似（P2 换 probe/sky 层级） |
| 回退 | 记录 version ≠ 1、来源不是 RT、未启用、compute 缺失 → 保留 closest-hit radiance |
| 开关 | B3 阶段为静态量；B4 起由 `hitDeferredShading` 控制（见下） |
| 未验证 | compute 变体编译与实际像素要到 B5 的 Player 构建；本阶段只做契约/结构 tripwire |

## B4：feature 开关与回退

| 项 | 内容 |
|---|---|
| 开关 | `ScreenSpaceLightingSettings.hitDeferredShading`（默认 **关**）；`SsrPass` 跟随设置，不再是静态量 |
| 回退 | 记录无效 / 未启用 / compute 缺失 → closest-hit radiance；D3D11 无 DXR，整条链路不进入该核 |
| 材质 | `RayTracingSceneInstances.Classify` 对"有 hit pass 但缺标准 GBuffer 字段"的材质按 C2.7 **报错一次并跳过**（透明面、GBuffer 不兼容的 Lit 变体）；跳过的子网格不进 RTAS，命中落 probe/sky |
| 资源 | 编辑器 `k_ResourceFields` 增加 `ssrHitDeferredCompute`；fixture 构建器显式解析并写入该引用，缺失直接抛错 |
| 证据 | fixture 开关置 true（`Fixture.json.hitDeferredShading = true`），B5 重生成 fixture 后生效 |

## B5：构建 + 门禁（部分完成）

**构建**：fixture 重新生成 + Player 构建成功（D3D11+D3D12，0 shader error，0 build error）。构建期修复：

- `BRDFData` 没有 `fresnel0`（那是 HDRP）；URP 用 `specular` 作 F0。
- `AssetDatabase` 的包路径不带 `@14.0.12`（与 `ScreenSpaceLightingSettings` 一致）；新 compute 需显式 `ImportAsset`。
- `CreateFixture()` 移进 `try`：生成期失败也要还原工程设置。
- 捕获器改读**字段**：Player 构建会裁掉未被运行时使用的 internal 属性（`ssrPass`/`rayTracingScene`）。

**已关闭的门禁（捕获帧内回读，`Player.json`）**：

| 门禁 | 结果 |
|---|---|
| R1 计数 | eligible 187225 / requested 179076 / served 11952 / overflow 167124 |
| TLAS / DXR | 14 实例、0 禁用子网格、121 次构建；`-force-d3d12` 下 `supportsRayTracing=true` |
| D3D11 回退基线 | 首次录制：三变体两进程逐位一致（base `7fb3e01b`、nodenoise `1bfd98e9`、ssroff `8ab8e569`）；tlas 0、R1 全 0、无 NaN |
| 确定性 | D3D12/D3D11 各变体两进程 SHA 一致 |

**OPEN：命中着色未生效。** D3D12 三变体帧 SHA 与 P0 完全相同（base `96b9c197`、nodenoise `693c54de`、ssroff `1295029d`），尽管 kernel 已被派发（Player.log 有 `SsrHitDeferred` 的未绑定探测贴图告警）。当前嫌疑：

1. raygen 写 `_R2Surfaces` 与随后 compute 读取之间的 UAV 时序（Unity 未必在 DispatchRays → DispatchCompute 之间插屏障）；
2. 记录全部未通过 version/source 校验（表面传输没填上）。

下一步用"固定颜色写入"二分：kernel 无条件写 `_R2Hits` 若像素仍不变 → 时序/写路径；若变 → 记录校验。

**未提交 `frame.raw`**：与 P0 逐位相同（P0 本身也只提交 JSON），SHA 见各 `Player.json`。

## B5-fix：固定颜色二分

新增诊断模式（`SsrHitDeferred.compute` 的 `_SsrHitDebug`，捕获参数 `-p3-hit-debug`，写入 `Player.json.hitDebug`）：`0` 正常着色，`1` 无条件写洋红，`2` 回显记录头。

**结果**：debug=1 与 debug=0 帧逐位相同（`96b9c197`），debug=2 也相同。

**当时的排除项**（后来证明都被"kernel 从未运行"掩盖）：记录校验、权重为零、槽位语义、内核缺失。

## B5-fix2：RenderDoc 定位根因（已修复）

诊断模式本身是对的，但它永远走不到——因为 dispatch 在 GPU 上根本没发生。用 RenderDoc 1.47 + renderdoc-mcp 抓 D3D12 Player 后确认：

- `SSR Hit Shading` 标记区间**没有 dispatch**（before 抓帧 1449 push → 1450 pop），本帧 33 个 dispatch，少掉的正是第 34 个 hit kernel。
- 同一帧导出 4 个 `_R2Hits`（24 B × 960×540）候选缓冲：洋红 **0 条**，只有 raygen 的 legacy 值（flags `{0,1,2,6}`）。
- 新增 pass 诊断（`hitDeferredCalls/Dispatches/Skips*/Kernel/Groups/Settings/ComputeBound`，写入 `Player.json`）：120/120 次都调用了 `DispatchCompute`，kernel 0、512 组、settings 与 compute 均正常 → 问题在执行侧。

**根因**：`SsrHitDeferred.compute` 经 `GlxProbeSampling.hlsl` 无条件声明了 GI 探针图集，SSR-only fixture 不跑探针生产者、这些全局资源未绑定。Unity 在执行该 dispatch 时以第一条未绑定资源报 `Property (_GlxProbeIrrSnapshot) ... is not set` 并**不提交派发**；B5 把这条告警当成了"已派发"的证据，方向反了。

**修复**：`DispatchHitDeferred` 在 `rayTracingProbeFallback` 关闭时为探针图集绑占位资源（黑 1×1 + 队列缓冲，不会被采样，因为 `_GlxHitLowFrequencySource == 0`）；回退开启时沿用生产者发布的全局图集。

**修复后证据**：

- D3D12 base 帧 `96b9c197` → `2f713f17`（两次运行一致）；debug=1 帧 `7cbbe4a5`（洋红进入消费者）。
- RenderDoc：`SSR Hit Shading` 1405 → **dispatch 1416**；CS 绑定 4 纹理 / 5 SRV / 1 UAV；`_R2Hits` 洋红 11,950 条，flags 只剩 `{0, 2}`。
- D3D11 base `7fb3e01b` 与 P0 逐位一致；无 NaN；无 "not set" 告警。
- 细节与复现见 [RenderDoc/README.md](RenderDoc/README.md) / [CaptureAnalysis.json](RenderDoc/CaptureAnalysis.json)。两张 `.rdc` 只保留在本地（未提交，`CaptureAnalysis.json` 记录了文件名/尺寸/SHA256）。

## B6：环境光/阴影补齐 + SS/RT 边界度量

**阴影**：不是缺失。CS 反汇编（抓帧 EID 1416）里 `dcl_sampler s0, mode_comparison` 与 4 张纹理（探针 irr/dist + main/additional shadowmap）说明 `_MAIN_LIGHT_SHADOWS_CASCADE` / `_ADDITIONAL_LIGHT_SHADOWS` 由阴影投射 pass 的全局关键字继承进来了，单 tap 图集采样已生效。

**环境光**：确实缺失。`_GlxHitAmbientIrradiance` 在全仓库没有任何写入点，命中与 closest-hit 都以 0 环境光着色。现在 `SsrPass.Execute` 每相机计算 `RenderSettings.ambientProbe` 的六方向球面平均，按 `SH * π` 作为辐照度写入 `_GlxHitAmbientIrradiance`（`GlxHitLowFrequencyIrradiance` 会在 kernel 里再除 π），并只在 hybrid 队列存在时写入，D3D11 帧保持逐位一致。`-p3-hit-ambient` 是标定倍率（默认 1），`Player.json.hitAmbientScale` 记录使用值。

注意：本 fixture 的环境光探针接近 0（倍率 0→2 只让整帧 lumaMean 从 `0.26545300` 动到 `0.26545377`），所以环境光在这份场景里是惰性的；它服务的是有环境光的场景。

**指标**（[seam_metric.py](seam_metric.py)，数据 [seam/seam_ssrt.json](seam/seam_ssrt.json) / [seam/seam_nodenoise.json](seam/seam_nodenoise.json)）：从抓帧导出 `_R1Original` 与 `_R2Hits`——

- `reason == 2`（ssShare≈1）= SS 解析区：8,146 个 trace 槽位；
- `reason == 4/6`（requestShare>0）= RT 域：179,079 个；本帧实际服务 11,950 个；
- trace 半分辨率 → 每槽位对应 2×2 全分辨率块；ROI 内 SS↔RT 相邻块共 560 对，取块均值的线性亮度差；"outer"（RT 块向外再一格）是同场景局部梯度对照。

| 帧（base，denoise 开） | RT 区均值 | SS 区均值 | 边界 step mean | median | p95 | outer 对照 |
|---|---|---|---|---|---|---|
| P0（closest-hit） | 0.138984 | 0.113498 | 0.011384 | 0.002412 | 0.056947 | 0.006931 |
| P1（hit kernel + 环境光） | 0.138989 | 0.113504 | 0.011382 | 0.002412 | 0.056947 | 0.006955 |
| P1（hit kernel，环境光 0） | 0.138987 | 0.113502 | 0.011383 | 0.002412 | 0.056947 | 0.006935 |
| 洋红对照（debug=1） | 0.136857 | 0.111645 | 0.012774 | 0.003168 | 0.059310 | 0.007054 |

`nodenoise` A/B：边界 step mean 0.012394 → 0.013947（+12.5%），median 0.002332 → 0.002346（+0.6%）；两帧的 outer 对照都是约 0.0137。

**判读**：

1. 指标对 RT 着色是敏感的（洋红对照把 mean 拉高 12%），所以 P1 的 -0.02% 是"真的没变"，不是"测不出来"。
2. P0 的 SS/RT 边界 step 已是局部梯度的约 1.65 倍（0.0114 vs 0.0069；outer 对照只有 7 对，只能作方向参考），P1 后仍是 1.63 倍——残余缝主要来自历史/内容与 denoiser，不是着色器差异。
3. 旧 closest-hit 路径本来就用**真实阴影射线 + top-4 灯 + 自有 BRDF**（`GlxHitLights.hlsl`），与新的**单 tap 阴影图 + 全量灯 + CdShade**在这份场景里结果非常接近（整帧 ROI meanAbs 1.4e-6）。`nodenoise` 下的 +12.5% 正好对应 A 决策接受的阴影口径差（低质量单 tap 阴影计入 P2 容差）。

**结论：P1 的"缝显著下降"验收在本 fixture 上未达成（NOT MET）**；命中核执行、与光栅路径的近似一致（1e-6 级）、无 NaN、D3D11 逐位一致均已验证。要在缝上看到显著收益，需要一份能让两条路径真正分叉的测例（金属/粗糙命中面、多盏附加灯、阴影中的命中），这会要求新基线。

## 其他 OPEN

1. 性能增量（≤0.3 ms）尚未测量；需要 Player 内 FrameTimingManager 或带 GPU counter 的抓帧。
2. 探针回退开启时（P2）需绑定真实图集并把 `_GlxHitLowFrequencySource` 置 1，环境光改走 probe/sky。
3. D3D11 逐位基线在"重建后第一次运行"出现过一次 ULP 级离群（max 2.4e-4、mean 2e-9，SHA `484aafb6`）；随后 4/4 次都回到 `7fb3e01b`。证据录制应避开构建后的首次运行，或至少重复两次。

## 复跑

```powershell
$U = "D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe"
$P = "E:\GitHub\Glx.CustomURP\URPSample01"
& $U -batchmode -nographics -projectPath $P -runTests -testPlatform EditMode `
  -testFilter SsrP3ContractTests `
  -testResults "$P\PLAN\Done\23-Ray_Tracing_Inprove_2\validation\P1\tests\ContractTests.xml" `
  -logFile "$env:TEMP\ssr-p3-b1-tests.log"
```

`-runTests` 不能与 `-quit` 同用：加了 `-quit` 会在资源导入后直接退出，不执行测试、不产出 XML。

`SsrRayPayload_CompilesToTheFrozen64ByteSize` 需要能编译光追的 Editor API：默认 D3D11 下会显式 `Ignore`，要用 `-force-d3d12` 才能得到实测 64B 的结果（第二个 XML 即该跑法）。

SS/RT 边界度量（先用 renderdoc-mcp 在 `SSR Hit Shading` dispatch 处 `goto_event`，导出 `_R1Original`（41,472,000 B）与 debug=1 的 `_R2Hits`（12,441,600 B））：

```powershell
python "$P\PLAN\Done\23-Ray_Tracing_Inprove_2\validation\P1\seam_metric.py" `
  --originals <_R1Original.bin> --hits <_R2Hits.bin> `
  --frame p0=<P0 base frame.raw> --frame p1=<P1 frame.raw> `
  --out "$P\PLAN\Done\23-Ray_Tracing_Inprove_2\validation\P1\seam\seam_ssrt.json"
```

`frame.raw` 不入库（`.gitignore`），`seam/evidence/*.json` 记录了每次运行的关键字段与 SHA。
