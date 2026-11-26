# P1 证据

**状态：** B1（契约先行）完成。本阶段只新增未接入的头文件与 tripwire，运行时行为未变。
**基线：** P0 D3D12 基线继续有效；24B payload、R1/R2 布局、Pass 顺序与注入点均未改，不需要重录。

## B1：C2.7 hit 表面记录

| 项 | 内容 |
|---|---|
| 契约 | [contracts.md](../contracts.md) C2.7 + C8：`SsrHitSurface` 80 字节 / 5 字段 / `SsrHitSurfaceVersion = 1` |
| 定义 | `Shaders/RayTracing/SsrHitSurface.hlsl`（尚无消费者，B2 起由 closest-hit 写入） |
| 不变项 | `GlxBasicPayload` 仍 24 字节；`R1Sample` 仍 80 字节；tile 8×8；`_R1Counts` 11×uint；注入点 206 |
| tripwire | [tests/ContractTests.xml](tests/ContractTests.xml)：5/5 通过（新增 `SsrHitSurface_IsTheFrozen80ByteReflectionRecord`） |
| 运行时影响 | 无。头文件未被任何 shader include，不改变 P0 输出 |

contracts C2.7 同时冻结了材质能力边界：只承载标准 PBR 字段（baseColor、specularOcclusion、normal、perceptualRoughness、fresnel0、coatMask、emissive+baked、distance、surfaceType）；不提供 SSS/transmission/anisotropy，不设 per-material closest-hit 回退；不支持的材质在注册期报错（每材质一次）并跳过。

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
