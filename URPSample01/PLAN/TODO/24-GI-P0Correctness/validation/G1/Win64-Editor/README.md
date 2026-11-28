# G1 CPU 数学夹具（Editor，非 GPU 签收）

- **版本：** Unity 2022.3.62f1 / URP 14.0.12（本地包）
- **命令：**
  `Unity.exe -batchmode -projectPath URPSample01 -runTests -testPlatform EditMode`
  `-testFilter UnityEditor.Rendering.Universal.Tests.RayTracingProbeMathTests`
  `-testResults .../Tests.xml -logFile <Temp>`
- **结果：** `total="14" passed="14" failed="0"`（`Tests.xml`，SHA-1 `f93d30c463219b73818c1e799879f18452c97708`）。
- **同时刻 shader 导入：** `GlxProbeTrace` / `GlxProbeUpdate` 在本次导入中 0 个 `Shader error`（此前两项已修：D3D11 不认 `RayTMax`；FXC 拒绝屏障前提前返回）。这只证明 D3D11 编辑器导入成功，不是 DXR 运行证据。
- **覆盖：** 常量/两色场、天空样本进入辐照与距离矩、未解决样本不贡献、反馈项、八面体内区与 seam 边界表、邻居失效/relocation。
- **未覆盖：** 任何真实 `BuildRayTracingAccelerationStructure`/`DispatchRays`、D3D12 Player、GPU ms。生产者 A/B 见 [HANDOFF](../../../HANDOFF.md) §三。
- 原始 Unity 日志不含授权与机器标识，仅保留本地 `Temp/t24c.log`，不入库。
