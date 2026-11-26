# P1 证据

**状态：** B1–B4 完成（B3/B4 默认关，fixture 置开）；B5 构建与门禁部分完成，**命中着色尚未生效（OPEN）**。
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

## 复跑

```powershell
$U = "D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe"
$P = "E:\GitHub\Glx.CustomURP\URPSample01"
& $U -batchmode -nographics -projectPath $P -runTests -testPlatform EditMode `
  -testFilter SsrP3ContractTests `
  -testResults "$P\PLAN\TODO\23-Ray_Tracing_Inprove_2\validation\P1\tests\ContractTests.xml" `
  -logFile "$env:TEMP\ssr-p3-b1-tests.log"
```

`-runTests` 不能与 `-quit` 同用：加了 `-quit` 会在资源导入后直接退出，不执行测试、不产出 XML。

`SsrRayPayload_CompilesToTheFrozen64ByteSize` 需要能编译光追的 Editor API：默认 D3D11 下会显式 `Ignore`，要用 `-force-d3d12` 才能得到实测 64B 的结果（第二个 XML 即该跑法）。
