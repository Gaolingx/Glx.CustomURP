# 20-Ray_tracing 证据布局

## P0 是迁移记录，不是当前基线

[P0](P0/Baseline.json) 是 `720bedd` 的 **D3D11** 记录（EditMode 184、PlayMode 6、T12 34/34/35），用于保留历史入口、命令与哈希；它不证明当前 HEAD、D3D12、DXR 或主机。不要改写 `round`，不要覆盖 `P0/`。

`Baseline.json` 的 `commands` 保留原始调用。从 `URPSample01/` 复跑时，三个 `PLAN/Done/01-ScreenSpaceLighting/validation/T14/` 脚本入口不变；只替换 `-LogPath`、`-ResultsPath`、`-OutputRoot` 中的旧前缀 `PLAN/TODO/09-DDGI_improve/validation/P0/`。历史证据现归档在 `PLAN/TODO/20-Ray_tracing/validation/P0/`；新结果改写到独立目录，例如 `PLAN/TODO/20-Ray_tracing/validation/B0/Win64-D3D11/run-01/`，保留 `EditMode/`、`PlayMode/`、`T12/` 后缀。每次另选运行目录；目录名不会切换图形 API，复跑 P0 时须确认实际使用 D3D11。

## 新证据路径

```
validation/<ID>/<platform>-<renderer>/
```

- `<ID>`：`B0`、`T1`…`T4`（含 `T3b`）、`G1`…`G5`、`R1`…`R5`、`R2b`（v2 候选）、`I1`、`I2`。
- `<platform>`：`Win64-D3D12`、`Win64-D3D11`、`PS5`、`XSX`、`XSS` 等；主机按实际 SKU。
- `<renderer>`：`Forward`、`ForwardPlus`、`Deferred`；不适用时省略。
- 失败、阻塞、未复跑也入库；不要只留通过记录。

## 每个证据包至少包含

1. 输入与命令：场景/资产版本、相机轨迹、种子、曝光、分辨率、质量档、预热帧、计时窗口。
2. 环境：Unity/URP/HDRP 版本、GPU/SKU、驱动、图形 API、`SystemInfo.supportsRayTracing`。
3. 结果：pass/fail/blocked、实际值 vs B0 门槛、已知差异、原始日志与截图/读回。
4. 计数：TLAS Build/实例数、probe scheduled/updated/published、active/inactive/relocated、SSR requested/served/overflow/hit/miss、dispatch marker、灯数/每命中灯数/阴影射线条数、payload 字节、pass/variant、feedback/relocation/classification 开关与计数；R4/R5 的材质覆盖与 hit shading GPU ms；R2b 的 N/K、wave 利用率与 TMin A/B。
5. 设置恢复：质量档、Graphics API、Volume/Feature 开关在测试后恢复；记录恢复结果。
6. 哈希：基线/实现 SHA、输入资产与输出图像的 SHA（必要时）。

## 判定口径

- 计数为零、全黑、无 NaN、Editor 截图都不能单独证明 DXR 生效；必须有真实 Build/bind/DispatchRays 与命中证据。
- 图像比较在线性 HDR、同轨迹同帧；GI 比 ROI 误差与出屏亮度跳变，反射比轮廓召回/错误率与 SS/HW 切换亮度差，不看非黑像素率。
- 性能按 TLAS CPU/GPU、probe trace/update、hit shading（材质/灯/阴影/反馈）、relocation/classification、SSR trace/shade、合成分别计，另记总帧差、峰值显存与请求/命中/未服务数。
- 任何一项失败只关该扩展；不得通过扩大平台、材质或透明范围修改门槛。
