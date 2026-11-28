# P1 验收证据（Editor）

- **版本：** Unity 2022.3.62f1 / URP 14.0.12（本地包）
- **命令：**
  `Unity.exe -batchmode -projectPath URPSample01 -runTests -testPlatform EditMode`
  `-assemblyNames Unity.RayTracing.Tests.Editor -testResults ... -logFile <Temp>`

## 结果

| 文件 | 内容 | 结果 |
|---|---|---|
| [G1/Win64-Editor/Tests-Assembly.xml](G1/Win64-Editor/Tests-Assembly.xml) | 整个 `Unity.RayTracing.Tests.Editor`；SHA-1 `c3ab64c8…` | **26 / 27**；唯一失败 `RayTracingT2Tests.ManualSceneKnownVectors` |
| [Isolation/T2-preexisting.xml](Isolation/T2-preexisting.xml) | 暂存 P1 两个 shader 改动后只跑 T2；SHA-1 `e9c2f036…` | **0 / 1**，同一失败 |

T2 失败点为 `origin=(20,0,0)` 的 `expected=(4,71,254) gpu=(0,0,0)`（GPU 返回纯 miss），属于 TLAS 命中用例；隔离运行证明它与 P1 的两个 shader 无关，且 P0 只向 `GlxBasicPayload` 增加了一个 `static const`，不改变相交结果。**判为既有问题，不在本轮范围。**

同一构建的 GI 数学类（`RayTracingProbeMathTests`）单独运行为 **14/14**；本次 shader 导入 `GlxProbeUpdate` / `GlxProbeQuery` 为 **0 个 `Shader error`**（D3D11 编辑器导入，不是 DXR 运行证据）。

## 未覆盖

- D3D11/D3D12 Player 的 probe 生产者 A/B（P0 与 P1 均未取）、GPU ms。
- P1-4/5/7 没有 CPU 数学 oracle：G1 的 `Query` 是合成径向可见性夹具，不建模 `dot^64` 矩、Chebyshev 三次方/crush 与 gamma-5 时序混合，因此上面的 14/14 不构成这三项的行为证明。行为验证见 [HANDOFF](../HANDOFF.md) §三。
- 原始 Unity 日志（含授权/机器标识）仅留本地 `Temp/t25c.log`、`Temp/t2base.log`，不入库。
